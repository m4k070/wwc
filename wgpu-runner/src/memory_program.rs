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
//     "trace": false
//   }
//
// クロック駆動 (DESIGN-VERIFY.md §5.2 の契約): 1 サイクル = 「clk=0 settle → バス観測 →
// mem_write なら書込 → mem_read なら data_in=mem[addr]、そうでなければ 0 → clk=0 settle →
// clk=1 settle」。
//
// 設定ミス (expect の未知ポート名・値の幅超え・meta と grid の座標ずれ) は GPU 実行前に
// エラーにする。収束しなかった周期は結果が信用できないので失敗として扱う (終了コード 1)。
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use anyhow::{Context, Result};
use serde::Deserialize;

use crate::gpu::{load_bin, save_bin, GpuSim};
use crate::memory::{Memory, MemoryConfig, RomSource};

#[derive(Deserialize, Clone, Copy)]
pub struct Xy { pub x: u32, pub y: u32 }

/// RoutedArtifact.fs の OutputProbe に対応する (meta JSON の表現で区別する)。
#[derive(Deserialize, Clone, Debug, PartialEq)]
#[serde(untagged)]
pub enum OutputProbe {
    /// セルのレベル bit0 を読む。meta: {"x":..,"y":..}
    Cell { x: u32, y: u32 },
    /// yosys が定数に畳んだビット。meta: {"const":0|1}
    Const {
        #[serde(rename = "const")]
        value: u8,
    },
    /// 駆動元が grid 上に無い。meta: {"unobservable":<netId>}
    Unobservable { unobservable: i64 },
}

/// RoutedArtifact.fs (CurrentFormatVersion) が書く meta JSON の形式バージョン。
const META_FORMAT_VERSION: u32 = 1;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RoutedMeta {
    pub format_version: u32,
    pub circuit: String,
    pub width: u32,
    pub height: u32,
    /// 元 verilog JSON の SHA-256。B-3b (golden 照合) で golden 側と突き合わせる
    #[serde(default)]
    #[allow(dead_code)]
    pub source_sha256: Option<String>,
    #[serde(default)]
    pub gate_count: u32,
    #[serde(default)]
    pub dff_count: u32,
    pub inputs: BTreeMap<String, Vec<Xy>>,
    pub outputs: BTreeMap<String, Vec<OutputProbe>>,
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
    pub dump_dir: Option<PathBuf>,
}

const K_PIN: u8 = 1;
const K_NAND: u8 = 3;
const K_DFF: u8 = 5;

fn cell_kind(cells: &[u8], w: u32, c: &Xy) -> u8 {
    (cells[(c.y * w + c.x) as usize] >> 5) & 7
}

fn set_bus(sim: &mut GpuSim, coords: &[Xy], value: u64) {
    for (i, c) in coords.iter().enumerate() {
        let bit = ((value >> i) & 1) as u8;
        sim.write_cell(c.x, c.y, 0x20 | bit);
    }
}

fn read_bit(cells: &[u8], w: u32, probe: &OutputProbe) -> u64 {
    match probe {
        OutputProbe::Cell { x, y } => (cells[(*y * w + *x) as usize] & 1) as u64,
        OutputProbe::Const { value } => (*value & 1) as u64,
        // validate_expect で比較対象から外している。トレース表示では 0 として扱う
        OutputProbe::Unobservable { .. } => 0,
    }
}

fn read_bus(cells: &[u8], w: u32, probes: &[OutputProbe]) -> u64 {
    probes.iter().enumerate()
        .map(|(i, p)| read_bit(cells, w, p) << i)
        .sum()
}

fn fmt_val(name: &str, v: u64) -> String {
    if name.contains("flag") { format!("0x{v:X}") } else { format!("{v:#x} ({v})") }
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

/// meta の入力座標が Pin セル、出力 probe が Pin/Nand/Dff セルを指しているか。
/// meta と .bin の取り違えを GPU 実行前に検出する。
fn validate_meta_against_grid(meta: &RoutedMeta, cells: &[u8]) -> Result<()> {
    let (w, h) = (meta.width, meta.height);
    for (name, coords) in &meta.inputs {
        for (i, c) in coords.iter().enumerate() {
            anyhow::ensure!(c.x < w && c.y < h, "input {name}[{i}] at ({},{}) out of range", c.x, c.y);
            let k = cell_kind(cells, w, c);
            anyhow::ensure!(k == K_PIN,
                "input {name}[{i}] at ({},{}) is not a Pin cell (kind={k}) — meta/init mismatch?", c.x, c.y);
        }
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

pub fn run_memory_program(prog_path: &Path, opts: &MemProgOpts) -> Result<i32> {
    let prog: MemoryProgram = serde_json::from_str(
        &fs::read_to_string(prog_path).with_context(|| format!("reading {prog_path:?}"))?
    ).with_context(|| format!("parsing {prog_path:?}"))?;
    let dir = prog_path.parent().unwrap_or_else(|| Path::new(".")).to_path_buf();

    let meta_path = dir.join(&prog.meta);
    let meta: RoutedMeta = serde_json::from_str(
        &fs::read_to_string(&meta_path).with_context(|| format!("reading {meta_path:?}"))?
    ).with_context(|| format!("parsing {meta_path:?}"))?;
    anyhow::ensure!(meta.format_version == META_FORMAT_VERSION,
        "meta formatVersion {} is not supported (expected {META_FORMAT_VERSION})", meta.format_version);

    if let Some(circuit) = &prog.circuit {
        anyhow::ensure!(circuit == &meta.circuit,
            "circuit mismatch: program='{}' meta='{}'", circuit, meta.circuit);
    }

    let init_path = dir.join(&prog.init);
    let (w, h, init_cells) = load_bin(&init_path)?;
    anyhow::ensure!(w == meta.width && h == meta.height,
        "grid size mismatch: init.bin {}×{} vs meta {}×{}", w, h, meta.width, meta.height);
    validate_meta_against_grid(&meta, &init_cells)?;

    let clk = required_input(&meta, "clk")?.clone();
    let rst = required_input(&meta, "rst")?.clone();
    let data_in = required_input(&meta, "data_in")?.clone();
    let addr_probes = required_output(&meta, "addr")?.clone();
    let data_out_probes = required_output(&meta, "data_out")?.clone();
    let mem_read_probes = required_output(&meta, "mem_read")?.clone();
    let mem_write_probes = required_output(&meta, "mem_write")?.clone();
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
    let cfg = MemoryConfig {
        ram_base: prog.memory.ram_base.unwrap_or(0xC000) as u16,
        ram_size: prog.memory.ram_size.unwrap_or(8192),
    };
    let mut mem = Memory::new(rom, cfg);
    println!("Program: {} cycles, circuit={}, grid {w}×{h} (gates={}, DFF={}), rom={}B, ram={}B@{:#X}",
        prog.cycles, meta.circuit, meta.gate_count, meta.dff_count,
        mem.rom.len(), mem.ram.len(), mem.config.ram_base);

    let mut sim = GpuSim::new(w, h, &init_cells, opts.batch)?;
    if let Some(d) = &opts.dump_dir { fs::create_dir_all(d)?; }

    // 収束しなかったフェーズ。1 つでもあれば結果は信用できないので失敗にする
    let mut unsettled: Vec<String> = Vec::new();

    // リセット
    for pulse in 0..prog.rst_pulses {
        set_bus(&mut sim, &rst, 1);
        set_bus(&mut sim, &clk, 0);
        let (_, g0, ok0) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        set_bus(&mut sim, &clk, 1);
        let (_, g1, ok1) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        if !(ok0 && ok1) {
            unsettled.push(format!("rst pulse {pulse}: setup={g0}g(settled={ok0}) high={g1}g(settled={ok1})"));
        }
    }
    set_bus(&mut sim, &rst, 0);

    let mut last_cells: Vec<u8> = init_cells.clone();
    let mut trace_lines: Vec<String> = Vec::new();

    for cycle in 0..prog.cycles {
        // 1) clk=0 settle
        set_bus(&mut sim, &clk, 0);
        let spent_lo = std::time::Instant::now();
        let (cells_lo, g_lo, ok_lo) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;

        // 2) バス観測 + メモリ操作 (書込 → 読出)
        let addr = read_bus(&cells_lo, w, &addr_probes);
        let mem_read = read_bus(&cells_lo, w, &mem_read_probes) > 0;
        let mem_write = read_bus(&cells_lo, w, &mem_write_probes) > 0;
        let data_out = read_bus(&cells_lo, w, &data_out_probes);

        if mem_write {
            mem.write(addr as u16, data_out as u8);
        }
        let data_in_value = if mem_read { mem.read(addr as u16) as u64 } else { 0 };
        set_bus(&mut sim, &data_in, data_in_value);

        // data_in 変化を posedge 前に伝播させる (setup settle)。クロックとデータの
        // 競合を避ける — clk パルスは DFF に到達するまで数十世代かかる。
        set_bus(&mut sim, &clk, 0);
        let (_, g_wait, ok_wait) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;

        // 3) clk=1 settle (posedge)
        set_bus(&mut sim, &clk, 1);
        let spent_hi = std::time::Instant::now();
        let (cells_hi, g_hi, ok_hi) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        last_cells = cells_hi;

        if !(ok_lo && ok_wait && ok_hi) {
            unsettled.push(format!(
                "cycle {cycle}: setup={g_lo}g(settled={ok_lo}) data_in={g_wait}g(settled={ok_wait}) high={g_hi}g(settled={ok_hi})"));
        }

        if prog.trace {
            let fmt_opt = |probes: &Option<Vec<OutputProbe>>, width: usize| {
                probes.as_ref()
                    .map(|p| format!("{:#0width$X}", read_bus(&last_cells, w, p), width = width))
                    .unwrap_or_else(|| "-".into())
            };
            trace_lines.push(format!(
                "cycle {cycle:3}: addr={:#06X} mem_read={} mem_write={} dout={:#04X} din={:#04X} pc={} a={} setup={}g({:.1}s) data_in={}g high={}g({:.1}s)",
                addr, mem_read as u8, mem_write as u8, data_out, data_in_value,
                fmt_opt(&pc_probes, 6), fmt_opt(&a_probes, 4),
                g_lo, spent_lo.elapsed().as_secs_f32(), g_wait, g_hi, spent_hi.elapsed().as_secs_f32(),
            ));
        }
        if let Some(d) = &opts.dump_dir {
            let f = d.join(format!("cycle{:03}_hi.bin", cycle));
            save_bin(&f, w, h, &last_cells)?;
        }
    }

    if prog.trace {
        for l in &trace_lines { println!("{l}"); }
    }

    // 4) 期待値比較 (ポート名・幅は validate_expect で検査済み)
    let mut passed_checks = 0u32;
    let mut mismatches: Vec<String> = Vec::new();
    if let Some(expect) = &prog.expect {
        for (name, &exp) in expect {
            let got = read_bus(&last_cells, w, &meta.outputs[name]);
            if got == exp {
                passed_checks += 1;
            } else {
                mismatches.push(format!("{name}: expected {} got {}", fmt_val(name, exp), fmt_val(name, got)));
            }
        }
    }
    for (spec, addr, exp) in &expect_mem {
        let got = mem.read(*addr);
        if got == *exp {
            passed_checks += 1;
        } else {
            mismatches.push(format!("mem[{spec}]: expected {exp:#04x} got {got:#04x}"));
        }
    }

    println!();
    let total_checks = passed_checks + mismatches.len() as u32;
    if total_checks > 0 {
        println!("expect checks: {passed_checks}/{total_checks} passed");
    }
    for u in &unsettled {
        eprintln!("UNSETTLED: {u} (maxStepsPerPhase={})", prog.max_steps_per_phase);
    }
    for m in &mismatches {
        eprintln!("MISMATCH: {m}");
    }
    if !mismatches.is_empty() || !unsettled.is_empty() {
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

    #[test]
    fn parses_meta_field_names_written_by_routed_artifact() {
        let json = r#"{"formatVersion":1,"circuit":"c","sourceSha256":"ab","gitCommit":"g",
            "createdAtUtc":"2026-09-16T00:00:00+00:00","width":2,"height":1,"origin":{"x":0,"y":0},
            "gateCount":5,"dffCount":2,"inputs":{"clk":[{"x":0,"y":0}]},
            "outputs":{"b_out":[{"x":1,"y":0},{"const":0}]}}"#;
        let meta: RoutedMeta = serde_json::from_str(json).unwrap();
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
}
