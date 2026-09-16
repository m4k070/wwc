// メモリバス対応プログラムモード — RoutedArtifact meta (inputs/outputs) + ROM/RAM
// イメージでサイクル駆動シミュレーションを行う (TODO Step B)。
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
//     "expect": { "a_out": 66, "pc_out": 264 },
//     "expectMem": { "49152": 42 },        // RAM 絶対アドレス (10進 or "0xC000" 16進) → 値
//     "trace": false
//   }
//
// クロック駆動: 1 サイクル = 「clk=0 settle → バス観測 → data_in/mem_write 処理 →
// clk=1 settle」。mem_read は clk=0 settle 後に観測し、ROM/RAM から data_in 供給。
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use anyhow::{Context, Result};
use serde::Deserialize;

use crate::gpu::{load_bin, save_bin, GpuSim};
use crate::memory::{Memory, MemoryConfig, RomSource};

#[derive(Deserialize, Clone, Copy)]
pub struct Xy { pub x: u32, pub y: u32 }

#[derive(Deserialize, Clone)]
#[serde(untagged)]
pub enum OutputProbe {
    /// セルのレベル bit0 を読む
    Cell { x: i64, y: i64 },
    /// yosys が定数に畳んだビット
    Const(bool),
    /// 駆動元が grid 上に無い
    Unobs { net: i64 },
}

#[derive(Deserialize)]
pub struct RoutedMeta {
    pub circuit: String,
    pub width: u32,
    pub height: u32,
    #[serde(default)]
    pub origin: serde_json::Value,
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
        OutputProbe::Cell { x, y } => {
            let (ux, uy) = (*x as u32, *y as u32);
            ((cells[(uy * w + ux) as usize] & 1) as u64)
        }
        OutputProbe::Const(b) => *b as u64,
        OutputProbe::Unobs { .. } => 0,
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

fn parse_addr_spec(spec: &str, ram_base: u16) -> Result<u16> {
    let s = spec.trim();
    // "49152" (10進) or "0xC000" / "C000" (16進)。単純に "0x" があれば 16進
    let v = if let Some(hex) = s.strip_prefix("0x").or_else(|| s.strip_prefix("0X")) {
        u16::from_str_radix(hex, 16).with_context(|| format!("bad addr '{spec}'"))?
    } else {
        s.parse::<u16>().with_context(|| format!("bad addr '{spec}'"))?
    };
    let _ = ram_base;
    Ok(v)
}

fn parse_rom_source(spec: &str, base_dir: &Path) -> Result<RomSource> {
    if let Some(b64) = spec.strip_prefix("base64:") {
        Ok(RomSource::Base64(b64.to_string()))
    } else {
        let p = PathBuf::from(spec);
        Ok(RomSource::Path(if p.is_absolute() { p } else { base_dir.join(p) }))
    }
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

    if let Some(circuit) = &prog.circuit {
        anyhow::ensure!(circuit == &meta.circuit,
            "circuit mismatch: program='{}' meta='{}'", circuit, meta.circuit);
    }

    let init_path = dir.join(&prog.init);
    let (w, h, init_cells) = load_bin(&init_path)?;
    anyhow::ensure!(w == meta.width && h == meta.height,
        "grid size mismatch: init.bin {}×{} vs meta {}×{}", w, h, meta.width, meta.height);

    for (name, coords) in &meta.inputs {
        for (i, c) in coords.iter().enumerate() {
            anyhow::ensure!(c.x < w && c.y < h, "input {name}[{i}] out of range");
            let k = cell_kind(&init_cells, w, c);
            anyhow::ensure!(k == K_PIN,
                "input {name}[{i}] at ({},{}) is not a Pin cell (kind={k})", c.x, c.y);
        }
    }

    let rom_src = parse_rom_source(&prog.memory.rom, &dir)?;
    let rom = rom_src.load().context("loading ROM")?;
    let ram_base = prog.memory.ram_base.unwrap_or(0xC000) as u16;
    let cfg = MemoryConfig {
        ram_base,
        ram_size: prog.memory.ram_size.unwrap_or(8192),
    };
    let mut mem = Memory::new(rom, cfg);
    println!("Program: {} cycles, circuit={}, grid {w}×{h}, rom={}B, ram={}B@{:#X}",
        prog.cycles, meta.circuit, mem.rom.len(), mem.ram.len(), mem.config.ram_base);

    let clk = meta.inputs.get("clk").context("meta.inputs must contain 'clk'")?.clone();
    let rst = meta.inputs.get("rst").context("meta.inputs must contain 'rst'")?.clone();
    let data_in = meta.inputs.get("data_in").context("meta.inputs must contain 'data_in'")?.clone();

    // 出力ポートを事前に解決 (sorry no unwrap_or_else with ?. — separate)
    let addr_probes = meta.outputs.get("addr").context("meta.outputs must contain 'addr'")?.clone();
    let data_out_probes = meta.outputs.get("data_out").context("meta.outputs must contain 'data_out'")?.clone();
    let mem_read_probes = meta.outputs.get("mem_read").context("meta.outputs must contain 'mem_read'")?.clone();
    let mem_write_probes = meta.outputs.get("mem_write").context("meta.outputs must contain 'mem_write'")?.clone();
    let pc_probes = meta.outputs.get("pc_out")
        .or_else(|| meta.outputs.get("pc"))
        .context("meta.outputs must contain 'pc_out' or 'pc'")?.clone();
    let a_probes = meta.outputs.get("a_out")
        .or_else(|| meta.outputs.get("a"))
        .cloned();   // a_out がなくても expect が addr/pc だけなら動く

    let mut sim = GpuSim::new(w, h, &init_cells, opts.batch)?;
    if let Some(d) = &opts.dump_dir { fs::create_dir_all(d)?; }

    let mut passed_checks = 0u32;
    let mut failed_checks = 0u32;
    let mut mismatches: Vec<String> = Vec::new();

    // リセット
    for _ in 0..prog.rst_pulses {
        set_bus(&mut sim, &rst, 1);
        set_bus(&mut sim, &clk, 0);
        let (_, g0, ok0) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        set_bus(&mut sim, &clk, 1);
        let (_, g1, ok1) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        if !ok0 || !ok1 { eprintln!("WARN: rst pulse unsettled ({g0} / {g1} gens)"); }
    }
    set_bus(&mut sim, &rst, 0);

    let mut last_cells: Vec<u8> = init_cells.clone();
    let mut trace_lines: Vec<String> = Vec::new();

    for cycle in 0..prog.cycles {
        // 1) clk=0 settle
        set_bus(&mut sim, &clk, 0);
        let spent_lo = std::time::Instant::now();
        let (cells_lo, g_lo, ok_lo) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;

        // 2) バス観測 + メモリ操作
        let addr = read_bus(&cells_lo, w, &addr_probes);
        let mem_read = read_bus(&cells_lo, w, &mem_read_probes) > 0;
        let mem_write = read_bus(&cells_lo, w, &mem_write_probes) > 0;
        let data_out = read_bus(&cells_lo, w, &data_out_probes);

        if mem_write {
            mem.write(addr as u16, data_out as u8);
        }
        if mem_read {
            let v = mem.read(addr as u16);
            set_bus(&mut sim, &data_in, v as u64);
        } else {
            set_bus(&mut sim, &data_in, 0);
        }
        // data_in 変化を posedge 前に伝播させる (setup settle)。クロックとデータの
        // 競合を避ける — clk パルスは DFF に到達するまで数十世代かかる。
        set_bus(&mut sim, &clk, 0);
        let (_, g_wait, ok_wait) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        let _ = (g_wait, ok_wait);

        // 3) clk=1 settle (posedge)
        set_bus(&mut sim, &clk, 1);
        let spent_hi = std::time::Instant::now();
        let (cells_hi, g_hi, ok_hi) = sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;
        last_cells = cells_hi;

        if prog.trace {
            let pc = read_bus(&last_cells, w, &pc_probes);
            let a = a_probes.as_ref().map(|p| read_bus(&last_cells, w, p));
            trace_lines.push(format!(
                "cycle {cycle:3}: addr={:#06X} mem_read={} mem_write={} dout={:#04X} pc={:#06X} a={} setup={}g({:.1}s) high={}g({:.1}s)",
                addr, mem_read as u8, mem_write as u8, data_out,
                pc,
                a.map(|v| format!("{:#04X}", v as u8)).unwrap_or_else(|| "-".into()),
                g_lo, spent_lo.elapsed().as_secs_f32(), g_hi, spent_hi.elapsed().as_secs_f32(),
            ));
        }
        if !ok_lo || !ok_hi {
            eprintln!("WARN: cycle {} unsettled (setup {g_lo}, high {g_hi})", cycle);
        }
        if let Some(d) = &opts.dump_dir {
            let f = d.join(format!("cycle{:03}_hi.bin", cycle));
            save_bin(&f, w, h, &last_cells)?;
        }
    }

    if prog.trace {
        for l in &trace_lines { println!("{l}"); }
    }

    // 4) 期待値比較
    if let Some(expect) = &prog.expect {
        for (name, &exp) in expect {
            let unknown = |n: &str| {
                eprintln!("MISMATCH: unknown output '{n}' in expect (available: pc_out, a_out, addr, data_out, mem_read, mem_write)");
            };
            let probes: Option<&Vec<OutputProbe>> = match name.as_str() {
                "pc_out" | "pc" => Some(&pc_probes),
                "a_out" | "a" => a_probes.as_ref(),
                other => { unknown(other); None }
            };
            if let Some(probes) = probes {
                let got = read_bus(&last_cells, w, probes);
                if got == exp { passed_checks += 1; }
                else {
                    failed_checks += 1;
                    mismatches.push(format!("{name}: expected {} got {}", fmt_val(name, exp), fmt_val(name, got)));
                }
            }
        }
    }

    // RAM 内容の期待値
    if let Some(expect_mem) = &prog.expect_mem {
        for (spec, &exp) in expect_mem {
            let addr = parse_addr_spec(spec, mem.config.ram_base)?;
            let got = mem.read(addr);
            if got == exp { passed_checks += 1; }
            else {
                failed_checks += 1;
                mismatches.push(format!("mem[{spec}]: expected {exp:#04x} got {got:#04x}"));
            }
        }
    }

    println!();
    if let Some(mm) = &prog.expect {
        if !mm.is_empty() { println!("expect checks: {}/{} passed", passed_checks, passed_checks + failed_checks); }
    }
    if !mismatches.is_empty() {
        for m in &mismatches { eprintln!("MISMATCH: {m}"); }
        return Ok(1);
    }
    Ok(0)
}
