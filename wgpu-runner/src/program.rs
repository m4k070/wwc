// プログラムモード — 命令レベル GPU 検証 (Phase 1b)。
// meta JSON (ピン/レジスタの正規化座標) と program JSON (命令列 + 期待レジスタ値)
// を受け取り、GPU 単独で「pins 書込 → clk=0 settle → clk=1 settle → レジスタ読出 →
// 期待値比較」のサイクルを回す。F# リファレンスは不要。
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use anyhow::{Context, Result};
use serde::Deserialize;

use crate::gpu::{load_bin, save_bin, GpuSim};

#[derive(Deserialize, Clone, Copy)]
pub struct Xy { pub x: u32, pub y: u32 }

#[derive(Deserialize)]
pub struct Meta {
    pub circuit: String,
    pub width: u32,
    pub height: u32,
    /// バス名 → ピンセル座標列 (LSB first)。clk/rst も 1 要素バス。
    pub pins: BTreeMap<String, Vec<Xy>>,
    /// レジスタ名 → ゲート出力セル座標列 (LSB first)。
    pub regs: BTreeMap<String, Vec<Xy>>,
}

fn default_max_steps() -> u32 { 6000 }
fn default_check_interval() -> u32 { 256 }

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Program {
    pub circuit: Option<String>,
    pub meta: String,
    pub init: String,
    #[serde(default = "default_max_steps")]
    pub max_steps_per_phase: u32,
    #[serde(default = "default_check_interval")]
    pub check_interval: u32,
    pub steps: Vec<Step>,
}

#[derive(Deserialize)]
pub struct Step {
    pub desc: Option<String>,
    /// バス名 → 書き込む整数値 (clk は runner が管理するので含めない)
    #[serde(default)]
    pub pins: BTreeMap<String, u64>,
    /// レジスタ名 → 期待値。省略時は読み出し表示のみ。
    pub expect: Option<BTreeMap<String, u64>>,
}

pub struct ProgOpts {
    pub batch: u32,
    pub dump_regs: bool,
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
        // Pin セルのエンコーディングは 0x20 | level (dir は常に 0)
        sim.write_cell(c.x, c.y, 0x20 | bit);
    }
}

fn read_reg(cells: &[u8], w: u32, coords: &[Xy]) -> u64 {
    coords.iter().enumerate()
        .map(|(i, c)| ((cells[(c.y * w + c.x) as usize] & 1) as u64) << i)
        .sum()
}

/// flags 系はビットマスクなので 16 進表示、それ以外は 10 進。
fn fmt_val(name: &str, v: u64) -> String {
    if name == "flags" { format!("0x{v:X}") } else { format!("{v}") }
}

pub fn run_program(prog_path: &Path, opts: &ProgOpts) -> Result<i32> {
    let prog: Program = serde_json::from_str(
        &fs::read_to_string(prog_path).with_context(|| format!("reading {prog_path:?}"))?
    ).with_context(|| format!("parsing {prog_path:?}"))?;
    let dir = prog_path.parent().unwrap_or_else(|| Path::new("."));

    let meta_path = dir.join(&prog.meta);
    let meta: Meta = serde_json::from_str(
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

    // 座標検査 — meta と .bin の座標系ズレを起動時に検出する
    for (name, coords) in &meta.pins {
        for (i, c) in coords.iter().enumerate() {
            anyhow::ensure!(c.x < w && c.y < h, "pin {name}[{i}] out of range ({},{})", c.x, c.y);
            let k = cell_kind(&init_cells, w, c);
            anyhow::ensure!(k == K_PIN,
                "pin {name}[{i}] at ({},{}) is not a Pin cell (kind={k}) — meta/init mismatch?", c.x, c.y);
        }
    }
    for (name, coords) in &meta.regs {
        for (i, c) in coords.iter().enumerate() {
            anyhow::ensure!(c.x < w && c.y < h, "reg {name}[{i}] out of range ({},{})", c.x, c.y);
            let k = cell_kind(&init_cells, w, c);
            anyhow::ensure!(k == K_NAND || k == K_DFF,
                "reg {name}[{i}] at ({},{}) is not a gate cell (kind={k}) — meta/init mismatch?", c.x, c.y);
        }
    }

    let clk = meta.pins.get("clk").context("meta.pins must contain 'clk'")?.clone();
    println!("Program: {} steps, circuit={}, grid {w}×{h}, maxStepsPerPhase={}, checkInterval={}",
        prog.steps.len(), meta.circuit, prog.max_steps_per_phase, prog.check_interval);

    let mut sim = GpuSim::new(w, h, &init_cells, opts.batch)?;
    if let Some(d) = &opts.dump_dir { fs::create_dir_all(d)?; }

    let mut passed = 0u32;
    let mut failed = 0u32;

    for (idx, step) in prog.steps.iter().enumerate() {
        let n = idx + 1;
        let desc = step.desc.as_deref().unwrap_or("");

        // 1) pins 書込 + clk=0 で組合せ収束 (前命令の low フェーズ兼用。
        //    inst 変更は必ず clk=0 のまま収束させてから posedge を与える)
        for (bus, &value) in &step.pins {
            let coords = meta.pins.get(bus)
                .with_context(|| format!("step {n}: unknown pin bus '{bus}'"))?;
            anyhow::ensure!(coords.len() >= 64 || value >> coords.len() == 0,
                "step {n}: value {value} exceeds {}-bit bus '{bus}'", coords.len());
            set_bus(&mut sim, coords, value);
        }
        set_bus(&mut sim, &clk, 0);
        let (cells_lo, gens_lo, ok_lo) =
            sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;

        // 2) clk=1 → settle (posedge で DFF ラッチ)
        set_bus(&mut sim, &clk, 1);
        let (cells_hi, gens_hi, ok_hi) =
            sim.run_until_settled(prog.max_steps_per_phase, prog.check_interval)?;

        // 3) レジスタ読出 (high settle 後)
        let regs: Vec<(&str, u64)> = meta.regs.iter()
            .map(|(name, coords)| (name.as_str(), read_reg(&cells_hi, w, coords)))
            .collect();
        let regs_str = regs.iter()
            .map(|(name, v)| format!("{name}={}", fmt_val(name, *v)))
            .collect::<Vec<_>>().join(" ");

        if opts.dump_regs {
            let regs_json = regs.iter()
                .map(|(name, v)| format!("\"{name}\":{v}"))
                .collect::<Vec<_>>().join(",");
            println!("{{\"step\":{n},\"desc\":\"{desc}\",\"regs\":{{{regs_json}}},\"setupGens\":{gens_lo},\"highGens\":{gens_hi},\"settled\":{}}}",
                ok_lo && ok_hi);
            if !(ok_lo && ok_hi) { failed += 1; } else { passed += 1; }
            continue;
        }

        let mut mismatches: Vec<String> = Vec::new();
        if !ok_lo { mismatches.push(format!("NOTSETTLED setup after {gens_lo}g")); }
        if !ok_hi { mismatches.push(format!("NOTSETTLED high after {gens_hi}g")); }
        if let Some(expect) = &step.expect {
            for (name, &exp) in expect {
                let coords = meta.regs.get(name)
                    .with_context(|| format!("step {n}: unknown reg '{name}' in expect"))?;
                let got = read_reg(&cells_hi, w, coords);
                if got != exp {
                    mismatches.push(format!("{name}: expected {} got {}",
                        fmt_val(name, exp), fmt_val(name, got)));
                }
            }
        }

        if mismatches.is_empty() {
            println!("PASS [{n:2}] {desc:<12} {regs_str}  (setup {gens_lo}g, high {gens_hi}g)");
            passed += 1;
        } else {
            println!("FAIL [{n:2}] {desc:<12} {}", mismatches.join("; "));
            println!("     regs: {regs_str}");
            failed += 1;
            if let Some(d) = &opts.dump_dir {
                let lo = d.join(format!("step{n:02}_setup.bin"));
                let hi = d.join(format!("step{n:02}_high.bin"));
                save_bin(&lo, w, h, &cells_lo)?;
                save_bin(&hi, w, h, &cells_hi)?;
                println!("     dumped {lo:?}, {hi:?}");
            }
        }
    }

    println!();
    println!("{passed}/{} passed", passed + failed);
    Ok(if failed == 0 { 0 } else { 1 })
}
