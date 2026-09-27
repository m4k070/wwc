// メモリバス対応プログラムモード — RoutedArtifact meta (inputs/outputs) + ROM/RAM
// イメージでサイクル駆動シミュレーションを行う (TODO Step B、設計: DESIGN-VERIFY.md)。
//
// プログラム JSON 形式:
//   {
//     "circuit": "sm83_subset",            // optional, meta と一致確認
//     "meta": "routed/sm83_subset.meta.json",
//     "init": "routed/sm83_subset.bin",
//     "memory": {
//       "rom": "rom/rom.bin",              // 相対パス or "base64:XXXX"
//       "ramBase": 49152,                  // 0xC000 (省略可)
//       "ramSize": 8192                    // 省略可
//     },
//     "rstPulses": 2,                      // 起動時に rst=1 を N サイクル当てる (省略 2)
//     "cycles": 24,                        // 実行するクロック数
//     "maxStepsPerPhase": 12000,
//     "checkInterval": 256,
//     "expect": { "a_out": 66, "pc_out": 264 },  // meta の outputs にあるポート名
//     "expectMem": { "49152": 42 },        // RAM 絶対アドレス (10進 or "0xC000" 16進) → 値
//     "golden": "prog.golden.json",        // optional: ExportGolden.fsx の出力と全周期を照合
//     "trace": false
//   }
//
// クロック駆動 (DESIGN-VERIFY.md §5.2 の契約): 1 サイクル = 「idle settle → バス観測 →
// mem_write なら書込 → 割込み受付 → mem_read なら data_in=mem[addr]、そうでなければ 0 → idle settle →
// latch」。idle / latch の中身は meta の clocking で決まる (clocking.rs):
//   singleEdge: idle = clk=0、latch = clk=1 settle
//   twoPhase:   idle = clkA=0,clkB=0、latch = clkA=1 settle → clkA=0,clkB=1 settle (§5.2.1)
//
// golden を指定すると、周期ごとに data_in と全出力 (latch の最後の settle 後) を NetlistSim の結果と
// 比べ、最初に食い違った周期で止める (DESIGN-VERIFY.md §6.2)。
//
// 設定ミス (expect の未知ポート名・値の幅超え・meta と grid の座標ずれ・古い golden) は GPU 実行前に
// エラーにする。収束しなかった周期は結果が信用できないので失敗として扱う (終了コード 1)。
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use anyhow::{Context, Result};
use serde::Deserialize;
use sha2::{Digest, Sha256};

use crate::clocking::{write_bus, CaDriver, ClockPins, GpuDriver, Phase, LABEL_DATA_IN, LABEL_SETUP};
use crate::gpu::{load_bin, save_bin, Engine, GpuSim};
use crate::memory::{Memory, MemoryConfig, RomSource};
use crate::routed_meta::{OutputProbe, RoutedMeta, Xy};

/// Testbench.fs (GoldenFormat) が書く golden JSON の形式。
const GOLDEN_FORMAT: &str = "wwc-golden/1";

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct GoldenCycle {
    /// この周期で書くべき data_in (§5.2 手順 3)
    pub data_in: u64,
    /// この周期で書くべき irq (IE & IF)。割込みポートのない回路・古い golden では 0
    #[serde(default)]
    pub irq: u64,
    /// clk=1 settle 後の全出力 (§5.2 手順 6)。2 相では手順 7 (clkB=1 settle 後) と比べる
    pub outputs: BTreeMap<String, u64>,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Golden {
    pub format: String,
    pub circuit: String,
    pub program: String,
    pub source_sha256: String,
    pub rom_sha256: String,
    pub rst_pulses: u32,
    pub cycles: Vec<GoldenCycle>,
}

fn default_rst_pulses() -> u32 { 2 }
fn default_max_steps() -> u32 { 12000 }
fn default_check_interval() -> u32 { 256 }

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MemoryProgram {
    pub circuit: Option<String>,
    pub meta: String,
    pub init: String,
    pub memory: MemorySection,
    #[serde(default = "default_rst_pulses")]
    pub rst_pulses: u32,
    pub cycles: u32,
    #[serde(default = "default_max_steps")]
    pub max_steps_per_phase: u32,
    #[serde(default = "default_check_interval")]
    pub check_interval: u32,
    #[serde(default)]
    pub expect: Option<BTreeMap<String, u64>>,
    #[serde(default)]
    pub expect_mem: Option<BTreeMap<String, u8>>,
    #[serde(default)]
    pub golden: Option<String>,
    #[serde(default)]
    pub trace: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MemorySection {
    pub rom: String,
    #[serde(default)]
    pub ram_base: Option<u32>,
    #[serde(default)]
    pub ram_size: Option<usize>,
}

pub struct MemProgOpts {
    pub batch: u32,
    pub engine: Engine,
    pub dump_dir: Option<PathBuf>,
}

const K_PIN: u8 = 1;
const K_NAND: u8 = 3;
const K_DFF: u8 = 5;

fn cell_kind(cells: &[u8], w: u32, c: &Xy) -> u8 {
    (cells[(c.y * w + c.x) as usize] >> 5) & 7
}

fn read_bit(cells: &[u8], w: u32, probe: &OutputProbe) -> u64 {
    match probe {
        OutputProbe::Cell { x, y } => (cells[(*y * w + *x) as usize] & 1) as u64,
        OutputProbe::Const { value } => (*value & 1) as u64,
        // expect / golden の比較では observable_mask で除外する。トレース表示では 0 として扱う
        OutputProbe::Unobservable { .. } => 0,
    }
}

fn read_bus(cells: &[u8], w: u32, probes: &[OutputProbe]) -> u64 {
    probes.iter().enumerate()
        .map(|(i, p)| read_bit(cells, w, p) << i)
        .sum()
}

/// 比較に使えるビット (Unobservable 以外) のマスク。
fn observable_mask(probes: &[OutputProbe]) -> u64 {
    probes.iter().enumerate()
        .filter(|(_, p)| !matches!(p, OutputProbe::Unobservable { .. }))
        .fold(0u64, |mask, (i, _)| mask | (1u64 << i))
}

fn fmt_val(name: &str, v: u64) -> String {
    if name.contains("flag") { format!("0x{v:X}") } else { format!("{v:#x} ({v})") }
}

fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

fn short_hash(s: &str) -> &str {
    &s[..s.len().min(12)]
}

fn parse_addr_spec(spec: &str) -> Result<u16> {
    let s = spec.trim();
    // "49152" (10進) or "0xC000" (16進)
    if let Some(hex) = s.strip_prefix("0x").or_else(|| s.strip_prefix("0X")) {
        u16::from_str_radix(hex, 16).with_context(|| format!("bad addr '{spec}'"))
    } else {
        s.parse::<u16>().with_context(|| format!("bad addr '{spec}'"))
    }
}

fn parse_rom_source(spec: &str, base_dir: &Path) -> Result<RomSource> {
    if let Some(b64) = spec.strip_prefix("base64:") {
        Ok(RomSource::Base64(b64.to_string()))
    } else {
        let p = PathBuf::from(spec);
        Ok(RomSource::Path(if p.is_absolute() { p } else { base_dir.join(p) }))
    }
}

fn required_input<'a>(meta: &'a RoutedMeta, name: &str) -> Result<&'a Vec<Xy>> {
    meta.inputs.get(name).with_context(|| format!("meta.inputs must contain '{name}'"))
}

fn required_output<'a>(meta: &'a RoutedMeta, name: &str) -> Result<&'a Vec<OutputProbe>> {
    meta.outputs.get(name).with_context(|| format!("meta.outputs must contain '{name}'"))
}

/// 割込みポート: irq (入力 5bit、= IE & IF) と int_ack (出力 5bit、受け付けた割込みの one-hot)。
/// どちらもない回路 (sm83_subset) では None。片方だけならエラー。
fn interrupt_ports(meta: &RoutedMeta) -> Result<Option<(Vec<Xy>, Vec<OutputProbe>)>> {
    match (meta.inputs.get("irq"), meta.outputs.get("int_ack")) {
        (None, None) => Ok(None),
        (Some(irq), Some(ack)) => {
            anyhow::ensure!(irq.len() == 5, "irq must be 5 bits (got {})", irq.len());
            anyhow::ensure!(ack.len() == 5, "int_ack must be 5 bits (got {})", ack.len());
            Ok(Some((irq.clone(), ack.clone())))
        }
        (Some(_), None) => anyhow::bail!("meta has irq but no int_ack — interrupt ports must come in pairs"),
        (None, Some(_)) => anyhow::bail!("meta has int_ack but no irq — interrupt ports must come in pairs"),
    }
}

/// meta の入力座標 (と 2 相のクロックピン) が Pin セル、出力 probe が Pin/Nand/Dff セルを指しているか。
/// meta と .bin の取り違えを GPU 実行前に検出する。
fn validate_meta_against_grid(meta: &RoutedMeta, cells: &[u8]) -> Result<()> {
    let (w, h) = (meta.width, meta.height);
    for (name, c) in meta.driven_pins() {
        anyhow::ensure!(c.x < w && c.y < h, "{name} at ({},{}) out of range", c.x, c.y);
        let k = cell_kind(cells, w, &c);
        anyhow::ensure!(k == K_PIN,
            "{name} at ({},{}) is not a Pin cell (kind={k}) — meta/init mismatch?", c.x, c.y);
    }
    for (name, probes) in &meta.outputs {
        for (i, p) in probes.iter().enumerate() {
            if let OutputProbe::Cell { x, y } = p {
                anyhow::ensure!(*x < w && *y < h, "output {name}[{i}] at ({x},{y}) out of range");
                let k = cell_kind(cells, w, &Xy { x: *x, y: *y });
                anyhow::ensure!(k == K_PIN || k == K_NAND || k == K_DFF,
                    "output {name}[{i}] at ({x},{y}) is not a Pin/Nand/Dff cell (kind={k}) — meta/init mismatch?");
            }
        }
    }
    Ok(())
}

/// expect のポート名・値の幅・観測可能性を実行前に検査する
/// (長い GPU 実行の後で設定ミスに気づく、または誤って PASS するのを防ぐ)。
fn validate_expect(outputs: &BTreeMap<String, Vec<OutputProbe>>, expect: &BTreeMap<String, u64>) -> Result<()> {
    for (name, &value) in expect {
        let probes = outputs.get(name).with_context(|| {
            let available: Vec<&str> = outputs.keys().map(String::as_str).collect();
            format!("expect: unknown output '{name}' (available: {})", available.join(", "))
        })?;
        anyhow::ensure!(probes.len() >= 64 || value >> probes.len() == 0,
            "expect: value {value} exceeds {}-bit output '{name}'", probes.len());
        let unobservable: Vec<usize> = probes.iter().enumerate()
            .filter(|(_, p)| matches!(p, OutputProbe::Unobservable { .. }))
            .map(|(i, _)| i)
            .collect();
        anyhow::ensure!(unobservable.is_empty(),
            "expect: output '{name}' has unobservable bits {unobservable:?}");
    }
    Ok(())
}

/// golden がこの配線結果・ROM・プログラムに対して作られたものかを実行前に検査する。
fn validate_golden(golden: &Golden, meta: &RoutedMeta, rst_pulses: u32, cycles: u32, rom_sha256: &str) -> Result<()> {
    anyhow::ensure!(golden.format == GOLDEN_FORMAT,
        "golden format '{}' is not supported (expected {GOLDEN_FORMAT})", golden.format);
    anyhow::ensure!(golden.circuit == meta.circuit,
        "golden circuit '{}' != meta circuit '{}'", golden.circuit, meta.circuit);
    let meta_sha = meta.source_sha256.as_deref()
        .context("meta has no sourceSha256 — cannot check that golden matches the routed grid")?;
    anyhow::ensure!(golden.source_sha256 == meta_sha,
        "golden sourceSha256 {}… != meta {}… — the netlist changed; re-route or regenerate golden",
        short_hash(&golden.source_sha256), short_hash(meta_sha));
    anyhow::ensure!(golden.rom_sha256 == rom_sha256,
        "golden romSha256 {}… != ROM {}… — regenerate golden (src/ExportGolden.fsx)",
        short_hash(&golden.rom_sha256), short_hash(rom_sha256));
    anyhow::ensure!(golden.rst_pulses == rst_pulses,
        "golden rstPulses {} != program rstPulses {rst_pulses}", golden.rst_pulses);
    anyhow::ensure!(golden.cycles.len() == cycles as usize,
        "golden has {} cycles but program runs {cycles}", golden.cycles.len());
    let meta_ports: Vec<&str> = meta.outputs.keys().map(String::as_str).collect();
    for (k, cycle) in golden.cycles.iter().enumerate() {
        let golden_ports: Vec<&str> = cycle.outputs.keys().map(String::as_str).collect();
        anyhow::ensure!(golden_ports == meta_ports,
            "golden cycle {k} outputs {golden_ports:?} != meta outputs {meta_ports:?}");
    }
    Ok(())
}

#[derive(Debug, PartialEq)]
struct PortMismatch {
    port: String,
    expected: u64,
    got: u64,
    /// 食い違ったビット位置 (LSB = 0)
    bits: Vec<usize>,
}

/// 全出力ポートを期待値と比べる (観測不能ビットは除外)。ポートの存在は validate_golden で検査済み。
fn diff_outputs(
    outputs: &BTreeMap<String, Vec<OutputProbe>>,
    cells: &[u8],
    w: u32,
    expected: &BTreeMap<String, u64>,
) -> Vec<PortMismatch> {
    outputs.iter()
        .filter_map(|(name, probes)| {
            let mask = observable_mask(probes);
            let exp = expected.get(name).copied().unwrap_or(0) & mask;
            let got = read_bus(cells, w, probes) & mask;
            if exp == got {
                return None;
            }
            let diff = exp ^ got;
            let bits = (0..probes.len()).filter(|i| (diff >> i) & 1 == 1).collect();
            Some(PortMismatch { port: name.clone(), expected: exp, got, bits })
        })
        .collect()
}

/// --memory が駆動・観測するポート (クロック以外)。
struct BusPorts {
    rst: Vec<Xy>,
    data_in: Vec<Xy>,
    addr: Vec<OutputProbe>,
    data_out: Vec<OutputProbe>,
    mem_read: Vec<OutputProbe>,
    mem_write: Vec<OutputProbe>,
    /// (irq 入力, int_ack 出力)。割込みポートのない回路では None
    interrupts: Option<(Vec<Xy>, Vec<OutputProbe>)>,
}

impl BusPorts {
    fn from_meta(meta: &RoutedMeta) -> Result<Self> {
        Ok(BusPorts {
            rst: required_input(meta, "rst")?.clone(),
            data_in: required_input(meta, "data_in")?.clone(),
            addr: required_output(meta, "addr")?.clone(),
            data_out: required_output(meta, "data_out")?.clone(),
            mem_read: required_output(meta, "mem_read")?.clone(),
            mem_write: required_output(meta, "mem_write")?.clone(),
            interrupts: interrupt_ports(meta)?,
        })
    }
}

/// リセットの 1 周期 (§5.1): rst=1 を書き、idle settle → latch。rst は呼び出し側が最後に 0 へ戻す。
fn run_reset_cycle<D: CaDriver>(driver: &mut D, clock: &ClockPins, bus: &BusPorts) -> Result<Vec<Phase>> {
    write_bus(driver, &bus.rst, 1);
    let mut phases = vec![clock.settle_idle(driver, LABEL_SETUP)?];
    phases.extend(clock.latch(driver)?);
    Ok(phases)
}

/// バス周期 1 回分の観測結果。
struct BusCycle {
    /// 手順 2 (idle settle 後) のバス
    addr: u64,
    mem_read: bool,
    mem_write: bool,
    data_out: u64,
    /// 手順 3 で決めて書いた値
    data_in: u64,
    irq: u64,
    /// setup, data_in, latch の各段 (単相: high / 2 相: phaseA, phaseB)
    phases: Vec<Phase>,
}

impl BusCycle {
    /// 周期の出力 (latch の最後の settle 後) のセル
    fn output_cells(&self) -> &[u8] {
        &self.phases.last().expect("a bus cycle always has phases").settled.cells
    }

    /// 手順 1 (idle settle 後) のセル
    fn setup_cells(&self) -> &[u8] {
        &self.phases[0].settled.cells
    }

    fn all_settled(&self) -> bool {
        self.phases.iter().all(|p| p.settled.settled)
    }

    /// "setup=..g data_in=..g high=..g" (2 相では phaseA / phaseB)
    fn gens_str(&self) -> String {
        self.phases.iter().map(Phase::gens_str).collect::<Vec<_>>().join(" ")
    }
}

/// バス周期 1 回 (§5.2 / §5.2.1): idle settle → バス観測 → 書込 → 割込み受付 → 読出 →
/// data_in / irq を書いて idle settle → latch。
fn run_bus_cycle<D: CaDriver>(
    driver: &mut D, clock: &ClockPins, bus: &BusPorts, mem: &mut Memory, w: u32,
) -> Result<BusCycle> {
    // 1) クロック休止で収束
    let setup = clock.settle_idle(driver, LABEL_SETUP)?;

    // 2) バス観測 (前周期にラッチされた値)
    let cells = &setup.settled.cells;
    let addr = read_bus(cells, w, &bus.addr);
    let mem_read = read_bus(cells, w, &bus.mem_read) > 0;
    let mem_write = read_bus(cells, w, &bus.mem_write) > 0;
    let data_out = read_bus(cells, w, &bus.data_out);
    let ack = bus.interrupts.as_ref().map(|(_, ack_probes)| read_bus(cells, w, ack_probes) as u8);

    // 3) メモリ操作: 書込 → 割込み受付 (int_ack を IF に反映) → 読出と irq (F# Testbench と同じ順序)
    if mem_write {
        mem.write(addr as u16, data_out as u8);
    }
    if let Some(ack) = ack {
        mem.acknowledge_interrupts(ack);
    }
    let data_in = if mem_read { mem.read(addr as u16) as u64 } else { 0 };
    write_bus(driver, &bus.data_in, data_in);
    let irq = match &bus.interrupts {
        Some((irq_pins, _)) => {
            let pending = mem.pending_interrupts() as u64;
            write_bus(driver, irq_pins, pending);
            pending
        }
        None => 0,
    };

    // 4) data_in 変化をラッチ前に伝播させる (クロック休止のまま収束)。クロックとデータの
    //    競合を避ける — クロックは DFF に到達するまで数十世代かかる
    let wait = clock.settle_idle(driver, LABEL_DATA_IN)?;

    // 5) ラッチ (単相: clk=1 / 2 相: clkA=1 → clkA=0,clkB=1)
    let mut phases = vec![setup, wait];
    phases.extend(clock.latch(driver)?);
    Ok(BusCycle { addr, mem_read, mem_write, data_out, data_in, irq, phases })
}

pub fn run_memory_program(prog_path: &Path, opts: &MemProgOpts) -> Result<i32> {
    let prog: MemoryProgram = serde_json::from_str(
        &fs::read_to_string(prog_path).with_context(|| format!("reading {prog_path:?}"))?
    ).with_context(|| format!("parsing {prog_path:?}"))?;
    let dir = prog_path.parent().unwrap_or_else(|| Path::new(".")).to_path_buf();

    let meta_path = dir.join(&prog.meta);
    let meta = RoutedMeta::from_json(
        &fs::read_to_string(&meta_path).with_context(|| format!("reading {meta_path:?}"))?
    ).with_context(|| format!("loading {meta_path:?}"))?;

    if let Some(circuit) = &prog.circuit {
        anyhow::ensure!(circuit == &meta.circuit,
            "circuit mismatch: program='{}' meta='{}'", circuit, meta.circuit);
    }

    let init_path = dir.join(&prog.init);
    let (w, h, init_cells) = load_bin(&init_path)?;
    anyhow::ensure!(w == meta.width && h == meta.height,
        "grid size mismatch: init.bin {}×{} vs meta {}×{}", w, h, meta.width, meta.height);
    validate_meta_against_grid(&meta, &init_cells)?;

    let clock = ClockPins::from_meta(&meta)?;
    let bus = BusPorts::from_meta(&meta)?;
    // トレース表示用。無ければ "-" と表示する
    let pc_probes = meta.outputs.get("pc_out").cloned();
    let a_probes = meta.outputs.get("a_out").cloned();

    if let Some(expect) = &prog.expect {
        validate_expect(&meta.outputs, expect)?;
    }
    let expect_mem: Vec<(String, u16, u8)> = match &prog.expect_mem {
        Some(m) => m.iter()
            .map(|(spec, &v)| parse_addr_spec(spec).map(|a| (spec.clone(), a, v)))
            .collect::<Result<_>>()?,
        None => Vec::new(),
    };

    let rom_src = parse_rom_source(&prog.memory.rom, &dir)?;
    let rom = rom_src.load().context("loading ROM")?;
    let rom_sha256 = sha256_hex(&rom);

    let golden: Option<Golden> = match &prog.golden {
        Some(spec) => {
            let path = dir.join(spec);
            let g: Golden = serde_json::from_str(
                &fs::read_to_string(&path).with_context(|| format!("reading golden {path:?}"))?
            ).with_context(|| format!("parsing golden {path:?}"))?;
            validate_golden(&g, &meta, prog.rst_pulses, prog.cycles, &rom_sha256)?;
            println!("Golden: {} ({} cycles, program={})", path.display(), g.cycles.len(), g.program);
            Some(g)
        }
        None => None,
    };

    let cfg = MemoryConfig {
        ram_base: prog.memory.ram_base.unwrap_or(0xC000) as u16,
        ram_size: prog.memory.ram_size.unwrap_or(8192),
    };
    let mut mem = Memory::new(rom, cfg);
    println!("Program: {} cycles, circuit={}, grid {w}×{h} (gates={}, DFF={}), rom={}B, ram={}B@{:#X}, meta v{} clocking={}",
        prog.cycles, meta.circuit, meta.gate_count, meta.dff_count,
        mem.rom.len(), mem.ram.len(), mem.config.ram_base, meta.format_version, clock.scheme_name());

    let mut sim = GpuSim::new(w, h, &init_cells, opts.batch, opts.engine)?;
    let mut driver = GpuDriver {
        sim: &mut sim,
        max_steps_per_phase: prog.max_steps_per_phase,
        check_interval: prog.check_interval,
    };
    if let Some(d) = &opts.dump_dir { fs::create_dir_all(d)?; }

    // 収束しなかったフェーズ。1 つでもあれば結果は信用できないので失敗にする
    let mut unsettled: Vec<String> = Vec::new();
    let status_line = |phases: &[Phase]| phases.iter().map(Phase::status_str).collect::<Vec<_>>().join(" ");

    // リセット
    for pulse in 0..prog.rst_pulses {
        let phases = run_reset_cycle(&mut driver, &clock, &bus)?;
        if !phases.iter().all(|p| p.settled.settled) {
            unsettled.push(format!("rst pulse {pulse}: {}", status_line(&phases)));
        }
    }
    write_bus(&mut driver, &bus.rst, 0);

    let mut last_cells: Vec<u8> = init_cells.clone();
    let mut trace_lines: Vec<String> = Vec::new();
    // golden と一致した周期数と、最初に食い違った周期の報告
    let mut golden_matched = 0u32;
    let mut divergence: Option<Vec<String>> = None;

    for cycle in 0..prog.cycles {
        let result = run_bus_cycle(&mut driver, &clock, &bus, &mut mem, w)?;
        last_cells = result.output_cells().to_vec();

        if !result.all_settled() {
            unsettled.push(format!("cycle {cycle}: {}", status_line(&result.phases)));
        }

        if prog.trace {
            let fmt_opt = |probes: &Option<Vec<OutputProbe>>, width: usize| {
                probes.as_ref()
                    .map(|p| format!("{:#0width$X}", read_bus(&last_cells, w, p), width = width))
                    .unwrap_or_else(|| "-".into())
            };
            // setup と latch の段は所要時間も出す (data_in の段は短いので世代数のみ)
            let settle_str = result.phases.iter()
                .map(|p| if p.label == LABEL_DATA_IN { p.gens_str() } else { format!("{}({:.1}s)", p.gens_str(), p.elapsed_secs) })
                .collect::<Vec<_>>().join(" ");
            trace_lines.push(format!(
                "cycle {cycle:3}: addr={:#06X} mem_read={} mem_write={} dout={:#04X} din={:#04X} irq={:#04X} pc={} a={} {settle_str}",
                result.addr, result.mem_read as u8, result.mem_write as u8, result.data_out, result.data_in, result.irq,
                fmt_opt(&pc_probes, 6), fmt_opt(&a_probes, 4),
            ));
        }
        if let Some(d) = &opts.dump_dir {
            let f = d.join(format!("cycle{:03}_hi.bin", cycle));
            save_bin(&f, w, h, &last_cells)?;
        }

        // golden 照合 (data_in と latch 後の全出力)
        if let Some(g) = &golden {
            let expected = &g.cycles[cycle as usize];
            let mut report: Vec<String> = Vec::new();
            if expected.data_in != result.data_in {
                report.push(format!("data_in: expected {:#04X} got {:#04X} (bus addr={:#06X} mem_read={} — memory model or bus diverged)",
                    expected.data_in, result.data_in, result.addr, result.mem_read as u8));
            }
            if expected.irq != result.irq {
                report.push(format!("irq: expected {:#04X} got {:#04X} (IE={:#04X} IF={:#04X} — memory model or int_ack diverged)",
                    expected.irq, result.irq, mem.interrupt_enable, mem.interrupt_flag));
            }
            for m in diff_outputs(&meta.outputs, &last_cells, w, &expected.outputs) {
                report.push(format!("{}: expected {:#X} got {:#X} (bits {:?})", m.port, m.expected, m.got, m.bits));
            }
            if report.is_empty() {
                golden_matched += 1;
            } else {
                report.push(format!("settle: {}", result.gens_str()));
                if let Some(d) = &opts.dump_dir {
                    let lo = d.join(format!("diverge_cycle{cycle:03}_setup.bin"));
                    let hi = d.join(format!("diverge_cycle{cycle:03}_high.bin"));
                    save_bin(&lo, w, h, result.setup_cells())?;
                    save_bin(&hi, w, h, &last_cells)?;
                    report.push(format!("dumped {} / {}", lo.display(), hi.display()));
                }
                report.insert(0, format!("cycle {cycle}"));
                divergence = Some(report);
                // 以降は data_in の前提が崩れているので比較を続ける意味がない
                break;
            }
        }
    }

    if prog.trace {
        for l in &trace_lines { println!("{l}"); }
    }

    // 5) 期待値比較 (ポート名・幅は validate_expect で検査済み)。
    //    golden と食い違って途中で止めた場合、最終状態に届いていないので比較しない
    //    (止めた周期の値との不一致は二次的で、原因調査の邪魔になる)
    let mut passed_checks = 0u32;
    let mut mismatches: Vec<String> = Vec::new();
    let stopped_early = divergence.is_some();
    if let Some(expect) = prog.expect.as_ref().filter(|_| !stopped_early) {
        for (name, &exp) in expect {
            let got = read_bus(&last_cells, w, &meta.outputs[name]);
            if got == exp {
                passed_checks += 1;
            } else {
                mismatches.push(format!("{name}: expected {} got {}", fmt_val(name, exp), fmt_val(name, got)));
            }
        }
    }
    for (spec, addr, exp) in expect_mem.iter().filter(|_| !stopped_early) {
        let got = mem.read(*addr);
        if got == *exp {
            passed_checks += 1;
        } else {
            mismatches.push(format!("mem[{spec}]: expected {exp:#04x} got {got:#04x}"));
        }
    }

    println!();
    if golden.is_some() {
        println!("golden: {golden_matched}/{} cycles match", prog.cycles);
    }
    let total_checks = passed_checks + mismatches.len() as u32;
    if stopped_early && (prog.expect.is_some() || !expect_mem.is_empty()) {
        println!("expect checks: skipped (stopped at golden divergence)");
    } else if total_checks > 0 {
        println!("expect checks: {passed_checks}/{total_checks} passed");
    }
    for u in &unsettled {
        eprintln!("UNSETTLED: {u} (maxStepsPerPhase={})", prog.max_steps_per_phase);
    }
    if let Some(report) = &divergence {
        eprintln!("DIVERGED from golden at {}", report[0]);
        for line in &report[1..] {
            eprintln!("  {line}");
        }
    }
    for m in &mismatches {
        eprintln!("MISMATCH: {m}");
    }
    if !mismatches.is_empty() || !unsettled.is_empty() || divergence.is_some() {
        return Ok(1);
    }
    Ok(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_all_probe_kinds_written_by_routed_artifact() {
        let json = r#"[{"x":3,"y":4},{"const":0},{"const":1},{"unobservable":17}]"#;
        let probes: Vec<OutputProbe> = serde_json::from_str(json).unwrap();
        assert_eq!(probes, vec![
            OutputProbe::Cell { x: 3, y: 4 },
            OutputProbe::Const { value: 0 },
            OutputProbe::Const { value: 1 },
            OutputProbe::Unobservable { unobservable: 17 },
        ]);
    }

    fn sample_meta_json() -> &'static str {
        r#"{"formatVersion":1,"circuit":"c","sourceSha256":"ab","gitCommit":"g",
            "createdAtUtc":"2026-09-16T00:00:00+00:00","width":2,"height":1,"origin":{"x":0,"y":0},
            "gateCount":5,"dffCount":2,"inputs":{"clk":[{"x":0,"y":0}]},
            "outputs":{"b_out":[{"x":1,"y":0},{"const":0}],"flag":[{"unobservable":3}]}}"#
    }

    #[test]
    fn parses_meta_field_names_written_by_routed_artifact() {
        let meta = RoutedMeta::from_json(sample_meta_json()).unwrap();
        assert_eq!(meta.format_version, 1);
        assert_eq!((meta.gate_count, meta.dff_count), (5, 2));
        assert_eq!(meta.source_sha256.as_deref(), Some("ab"));
        assert_eq!(meta.outputs["b_out"][1], OutputProbe::Const { value: 0 });
    }

    #[test]
    fn read_bus_combines_cells_and_constants() {
        // (0,0) = Nand level 1, (1,0) = Nand level 0
        let cells = [0b011_00_001u8, 0b011_00_000u8];
        let probes = vec![
            OutputProbe::Cell { x: 0, y: 0 },
            OutputProbe::Cell { x: 1, y: 0 },
            OutputProbe::Const { value: 1 },
        ];
        assert_eq!(read_bus(&cells, 2, &probes), 0b101);
    }

    fn sample_outputs() -> BTreeMap<String, Vec<OutputProbe>> {
        BTreeMap::from([
            ("a_out".to_string(), vec![OutputProbe::Cell { x: 0, y: 0 }, OutputProbe::Const { value: 0 }]),
            ("flags".to_string(), vec![OutputProbe::Unobservable { unobservable: 9 }]),
        ])
    }

    #[test]
    fn validate_expect_accepts_known_port() {
        let expect = BTreeMap::from([("a_out".to_string(), 1u64)]);
        assert!(validate_expect(&sample_outputs(), &expect).is_ok());
    }

    #[test]
    fn validate_expect_rejects_unknown_port() {
        let expect = BTreeMap::from([("pc".to_string(), 1u64)]);
        let err = validate_expect(&sample_outputs(), &expect).unwrap_err().to_string();
        assert!(err.contains("unknown output 'pc'"), "{err}");
    }

    #[test]
    fn validate_expect_rejects_too_wide_value() {
        let expect = BTreeMap::from([("a_out".to_string(), 4u64)]);
        let err = validate_expect(&sample_outputs(), &expect).unwrap_err().to_string();
        assert!(err.contains("exceeds 2-bit"), "{err}");
    }

    #[test]
    fn validate_expect_rejects_unobservable_bits() {
        let expect = BTreeMap::from([("flags".to_string(), 0u64)]);
        let err = validate_expect(&sample_outputs(), &expect).unwrap_err().to_string();
        assert!(err.contains("unobservable"), "{err}");
    }

    #[test]
    fn parse_addr_spec_accepts_decimal_and_hex() {
        assert_eq!(parse_addr_spec("49152").unwrap(), 0xC000);
        assert_eq!(parse_addr_spec("0xC000").unwrap(), 0xC000);
        assert!(parse_addr_spec("C000").is_err());
    }

    #[test]
    fn sha256_hex_matches_known_vector() {
        assert_eq!(sha256_hex(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    fn sample_golden(rom_sha: &str) -> Golden {
        let json = format!(r#"{{"format":"wwc-golden/1","circuit":"c","program":"p","sourceSha256":"ab",
            "romSha256":"{rom_sha}","rstPulses":2,
            "cycles":[{{"dataIn":62,"outputs":{{"b_out":1,"flag":0}}}}]}}"#);
        serde_json::from_str(&json).unwrap()
    }

    #[test]
    fn validate_golden_accepts_matching_golden() {
        let meta = RoutedMeta::from_json(sample_meta_json()).unwrap();
        assert!(validate_golden(&sample_golden("r"), &meta, 2, 1, "r").is_ok());
    }

    #[test]
    fn validate_golden_rejects_stale_inputs() {
        let meta = RoutedMeta::from_json(sample_meta_json()).unwrap();
        let cases: [(&str, u32, u32, &str); 3] = [
            ("romSha256", 2, 1, "other-rom"),
            ("rstPulses", 1, 1, "r"),
            ("cycles", 2, 5, "r"),
        ];
        for (needle, rst, cycles, rom) in cases {
            let err = validate_golden(&sample_golden("r"), &meta, rst, cycles, rom).unwrap_err().to_string();
            assert!(err.contains(needle), "expected '{needle}' in: {err}");
        }
        let mut stale = sample_golden("r");
        stale.source_sha256 = "zz".into();
        let err = validate_golden(&stale, &meta, 2, 1, "r").unwrap_err().to_string();
        assert!(err.contains("sourceSha256"), "{err}");
    }

    #[test]
    fn validate_golden_rejects_port_set_mismatch() {
        let meta = RoutedMeta::from_json(sample_meta_json()).unwrap();
        let mut g = sample_golden("r");
        g.cycles[0].outputs.remove("flag");
        let err = validate_golden(&g, &meta, 2, 1, "r").unwrap_err().to_string();
        assert!(err.contains("outputs"), "{err}");
    }

    fn meta_with_ports(inputs: &str, outputs: &str) -> RoutedMeta {
        RoutedMeta::from_json(&format!(
            r#"{{"formatVersion":1,"circuit":"c","width":1,"height":1,"inputs":{{{inputs}}},"outputs":{{{outputs}}}}}"#
        )).unwrap()
    }

    #[test]
    fn interrupt_ports_are_optional_but_must_come_in_pairs() {
        let five_in = (0..5).map(|i| format!(r#"{{"x":{i},"y":0}}"#)).collect::<Vec<_>>().join(",");
        let five_out = five_in.clone();
        let none = meta_with_ports(r#""clk":[{"x":0,"y":0}]"#, r#""addr":[{"x":0,"y":0}]"#);
        assert!(interrupt_ports(&none).unwrap().is_none());
        let both = meta_with_ports(&format!(r#""irq":[{five_in}]"#), &format!(r#""int_ack":[{five_out}]"#));
        assert!(interrupt_ports(&both).unwrap().is_some());
        let only_irq = meta_with_ports(&format!(r#""irq":[{five_in}]"#), r#""addr":[{"x":0,"y":0}]"#);
        assert!(interrupt_ports(&only_irq).unwrap_err().to_string().contains("pairs"));
        let narrow = meta_with_ports(r#""irq":[{"x":0,"y":0}]"#, &format!(r#""int_ack":[{five_out}]"#));
        assert!(interrupt_ports(&narrow).unwrap_err().to_string().contains("5 bits"));
    }

    #[test]
    fn golden_irq_defaults_to_zero_for_old_goldens() {
        let old: GoldenCycle = serde_json::from_str(r#"{"dataIn":1,"outputs":{}}"#).unwrap();
        let new: GoldenCycle = serde_json::from_str(r#"{"dataIn":1,"irq":6,"outputs":{}}"#).unwrap();
        assert_eq!((old.irq, new.irq), (0, 6));
    }

    #[test]
    fn diff_outputs_reports_bits_and_ignores_unobservable() {
        // (0,0) = level 0, (1,0) = level 1
        let cells = [0b011_00_000u8, 0b011_00_001u8];
        let outputs = BTreeMap::from([
            ("p".to_string(), vec![OutputProbe::Cell { x: 0, y: 0 }, OutputProbe::Cell { x: 1, y: 0 }]),
            ("u".to_string(), vec![OutputProbe::Unobservable { unobservable: 1 }]),
        ]);
        // p: expected 0b01, got 0b10 → bits 0 and 1 differ. u: expected 1 but unobservable → ignored
        let expected = BTreeMap::from([("p".to_string(), 0b01u64), ("u".to_string(), 1u64)]);
        assert_eq!(diff_outputs(&outputs, &cells, 2, &expected), vec![
            PortMismatch { port: "p".into(), expected: 0b01, got: 0b10, bits: vec![0, 1] },
        ]);
        let matching = BTreeMap::from([("p".to_string(), 0b10u64), ("u".to_string(), 0u64)]);
        assert!(diff_outputs(&outputs, &cells, 2, &matching).is_empty());
    }

    // ---- 周期の駆動順序 (GPU なし、RecordingDriver で書込と収束の列を記録する) ----
    mod cycle_order {
        use super::super::*;
        use crate::clocking::testing::{Event::{self, Settle, Write}, RecordingDriver};
        use crate::clocking::pin_cell;

        const W: u32 = 10;
        const RST: Xy = Xy { x: 0, y: 0 };
        const DIN0: Xy = Xy { x: 1, y: 0 };
        const DIN1: Xy = Xy { x: 2, y: 0 };
        const CLK: Xy = Xy { x: 3, y: 0 };
        const CLK_A: Xy = Xy { x: 4, y: 0 };
        const CLK_B: Xy = Xy { x: 5, y: 0 };
        const IRQ: [Xy; 5] = [Xy { x: 6, y: 0 }, Xy { x: 7, y: 0 }, Xy { x: 8, y: 0 }, Xy { x: 9, y: 0 }, Xy { x: 9, y: 1 }];

        fn consts(value: u64, bits: usize) -> Vec<OutputProbe> {
            (0..bits).map(|i| OutputProbe::Const { value: ((value >> i) & 1) as u8 }).collect()
        }

        /// バスは定数 probe: addr=0xC000, mem_write=1, mem_read=1, data_out=0x5A。
        /// data_in は 2 ビットだけ配線されている (0x5A の下位 2 ビット = 0b10 が書かれる)
        fn bus(int_ack: Option<u64>) -> BusPorts {
            BusPorts {
                rst: vec![RST],
                data_in: vec![DIN0, DIN1],
                addr: consts(0xC000, 16),
                data_out: consts(0x5A, 8),
                mem_read: consts(1, 1),
                mem_write: consts(1, 1),
                interrupts: int_ack.map(|ack| (IRQ.to_vec(), consts(ack, 5))),
            }
        }

        fn driver() -> RecordingDriver {
            RecordingDriver::new(W, vec![pin_cell(false); (W * 2) as usize])
        }

        const SINGLE: ClockPins = ClockPins::SingleEdge { clk: CLK };
        const TWO_PHASE: ClockPins = ClockPins::TwoPhase { clk_a: CLK_A, clk_b: CLK_B };

        /// data_in = 0b10 の書込
        fn data_in_writes() -> Vec<Event> {
            vec![Write(DIN0, false), Write(DIN1, true)]
        }

        #[test]
        fn single_edge_cycle_keeps_the_existing_order() {
            let mut d = driver();
            let mut mem = Memory::new(vec![], MemoryConfig::default());
            let r = run_bus_cycle(&mut d, &SINGLE, &bus(None), &mut mem, W).unwrap();
            let mut expected = vec![Write(CLK, false), Settle];
            expected.extend(data_in_writes());
            expected.extend([Write(CLK, false), Settle, Write(CLK, true), Settle]);
            assert_eq!(d.events, expected);
            // 書込 (0x5A → 0xC000) の後に読出
            assert_eq!((r.addr, r.data_in, mem.read(0xC000)), (0xC000, 0x5A, 0x5A));
            assert_eq!(r.gens_str(), "setup=7g data_in=7g high=7g");
        }

        #[test]
        fn two_phase_cycle_follows_design_verify_5_2_1() {
            let mut d = driver();
            let mut mem = Memory::new(vec![], MemoryConfig::default());
            let r = run_bus_cycle(&mut d, &TWO_PHASE, &bus(None), &mut mem, W).unwrap();
            let mut expected = vec![Write(CLK_A, false), Write(CLK_B, false), Settle];   // 1
            expected.extend(data_in_writes());                                           // 3
            expected.extend([
                Write(CLK_A, false), Write(CLK_B, false), Settle,                        // 4
                Write(CLK_A, true), Settle,                                              // 5
                Write(CLK_A, false), Write(CLK_B, true), Settle,                         // 6
            ]);
            assert_eq!(d.events, expected);
            assert_eq!(r.data_in, 0x5A);
            assert_eq!(r.gens_str(), "setup=7g data_in=7g phaseA=7g phaseB=7g");
            // 周期の出力は手順 6 の後 (clkB=1)
            assert_eq!(r.output_cells()[CLK_B.x as usize], pin_cell(true));
            assert_eq!(r.setup_cells()[CLK_B.x as usize], pin_cell(false));
        }

        #[test]
        fn interrupt_ack_is_applied_before_irq_is_written() {
            let mut d = driver();
            let mut mem = Memory::new(vec![], MemoryConfig::default());
            mem.write(crate::memory::INTERRUPT_ENABLE_ADDR, 0x1F);
            mem.write(crate::memory::INTERRUPT_FLAG_ADDR, 0x03);
            let r = run_bus_cycle(&mut d, &TWO_PHASE, &bus(Some(0x01)), &mut mem, W).unwrap();
            assert_eq!(r.irq, 0x02);
            // data_in → irq → クロック休止 → settle の順
            let irq_writes: Vec<Event> = IRQ.iter().enumerate().map(|(i, c)| Write(*c, (0x02 >> i) & 1 == 1)).collect();
            let mut expected_tail = data_in_writes();
            expected_tail.extend(irq_writes);
            expected_tail.extend([Write(CLK_A, false), Write(CLK_B, false), Settle]);
            assert_eq!(&d.events[3..3 + expected_tail.len()], expected_tail.as_slice());
        }

        #[test]
        fn reset_cycle_is_idle_then_latch() {
            let mut d = driver();
            let phases = run_reset_cycle(&mut d, &TWO_PHASE, &bus(None)).unwrap();
            assert_eq!(d.events, vec![
                Write(RST, true),
                Write(CLK_A, false), Write(CLK_B, false), Settle,
                Write(CLK_A, true), Settle,
                Write(CLK_A, false), Write(CLK_B, true), Settle,
            ]);
            assert_eq!(phases.len(), 3);
            let mut d = driver();
            run_reset_cycle(&mut d, &SINGLE, &bus(None)).unwrap();
            assert_eq!(d.events, vec![Write(RST, true), Write(CLK, false), Settle, Write(CLK, true), Settle]);
        }

        #[test]
        fn clock_pins_follow_meta_clocking() {
            let single = RoutedMeta::from_json(r#"{"formatVersion":1,"circuit":"c","width":4,"height":1,
                "inputs":{"clk":[{"x":3,"y":0}]},"outputs":{}}"#).unwrap();
            assert_eq!(ClockPins::from_meta(&single).unwrap(), SINGLE);
            let two = RoutedMeta::from_json(r#"{"formatVersion":2,"circuit":"c","width":6,"height":1,
                "inputs":{},"outputs":{},
                "clocking":{"scheme":"twoPhase","clockPort":"clk","clkA":{"x":4,"y":0},"clkB":{"x":5,"y":0}}}"#).unwrap();
            assert_eq!(ClockPins::from_meta(&two).unwrap(), TWO_PHASE);
            let no_clk = RoutedMeta::from_json(r#"{"formatVersion":2,"circuit":"c","width":4,"height":1,
                "inputs":{},"outputs":{},"clocking":{"scheme":"singleEdge"}}"#).unwrap();
            assert!(ClockPins::from_meta(&no_clk).unwrap_err().to_string().contains("'clk'"));
        }
    }
}
