mod clocking;
mod gpu;
mod memory;
mod memory_program;
mod program;
mod routed_meta;

use std::env;
use std::path::PathBuf;
use anyhow::{Context, Result};
use gpu::{load_bin, save_bin, Engine, GpuSim};

fn print_usage() {
    eprintln!("Usage: wgpu-runner <input.bin> [--steps N] [--output out.bin] [--batch B]");
    eprintln!("       (全モード共通) [--engine tiled|dense]   # tiled: 動いたタイルだけ計算 (既定)、dense: 毎世代全セル");
    eprintln!("       wgpu-runner --program prog.json [--dump-regs] [--dump-dir DIR] [--batch B]");
    eprintln!("       wgpu-runner --memory prog.json [--batch B] [--dump-dir DIR]   # メモリバスモード (Step B)");
}

/// WWC_STATS=1 のとき、GPU 実行の内訳を stderr に出す。
/// encode = dispatch 列の記録 + submit (CPU 側)、wait = 読み戻しの map 待ち (未実行の GPU 仕事を含む)
fn print_stats() {
    if env::var("WWC_STATS").is_err() {
        return;
    }
    let enc = gpu::STAT_ENCODE_NS.load(std::sync::atomic::Ordering::Relaxed);
    let wait = gpu::STAT_WAIT_NS.load(std::sync::atomic::Ordering::Relaxed);
    let total = enc + wait;
    let pct = |v: u64| if total == 0 { 0.0 } else { v as f64 * 100.0 / total as f64 };
    eprintln!(
        "[stats] encode(記録+submit) = {:.2}s ({:.0}%) / wait(読み戻し) = {:.2}s ({:.0}%) / 計 {:.2}s",
        enc as f64 / 1e9, pct(enc), wait as f64 / 1e9, pct(wait), total as f64 / 1e9
    );
    // 走査セル数の見積り: 1 タイルを dispatch すると (TILE+2K)² セルを走査して K 世代進む
    // (TILE=16, K=3 → 484 セル / 3 世代 = 161 セル/世代/タイル)
    let gens = gpu::STAT_GENS.load(std::sync::atomic::Ordering::Relaxed);
    let tiles = gpu::STAT_CHANGED_TILES.load(std::sync::atomic::Ordering::Relaxed);
    if gens > 0 {
        let scanned = tiles as f64 / gens as f64 * 161.0;
        eprintln!(
            "[stats] 世代 {gens} / 変化タイル計 {tiles} = {:.1} タイル/世代 ≒ 走査 {:.0} セル/世代 / GPU 1 セルあたり {:.2} ps",
            tiles as f64 / gens as f64, scanned,
            if scanned > 0.0 { wait as f64 * 1000.0 / (scanned * gens as f64) } else { 0.0 }
        );
    }
}

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();
    if args.len() < 2 {
        print_usage();
        std::process::exit(1);
    }

    let mut input = PathBuf::new();
    let mut steps = 1000u32;
    let mut output = None;
    let mut batch = 128u32;
    let mut program_path: Option<PathBuf> = None;
    let mut memory_path: Option<PathBuf> = None;
    let mut dump_regs = false;
    let mut dump_dir: Option<PathBuf> = None;
    let mut engine = Engine::Tiled;

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--steps" => { i += 1; steps = args[i].parse().context("--steps must be a number")?; }
            "--output" => { i += 1; output = Some(PathBuf::from(&args[i])); }
            "--batch" => { i += 1; batch = args[i].parse().context("--batch must be a number")?; }
            "--program" => { i += 1; program_path = Some(PathBuf::from(&args[i])); }
            "--memory" => { i += 1; memory_path = Some(PathBuf::from(&args[i])); }
            "--dump-regs" => { dump_regs = true; }
            "--dump-dir" => { i += 1; dump_dir = Some(PathBuf::from(&args[i])); }
            "--engine" => { i += 1; engine = args[i].parse()?; }
            s if s.starts_with('-') => { anyhow::bail!("unknown flag {s}"); }
            _ => { input = PathBuf::from(&args[i]); }
        }
        i += 1;
    }

    // ---- プログラムモード (命令レベル検証) ----
    if let Some(prog) = program_path {
        let opts = program::ProgOpts { batch, engine, dump_regs, dump_dir };
        let code = program::run_program(&prog, &opts)?;
        std::process::exit(code);
    }

    // ---- メモリバスモード (Step B: ROM/RAM 駆動シミュレーション) ----
    if let Some(prog) = memory_path {
        let opts = memory_program::MemProgOpts { batch, engine, dump_dir };
        let code = memory_program::run_memory_program(&prog, &opts)?;
        print_stats();
        std::process::exit(code);
    }

    // ---- 単発モード (golden test 用) ----
    if input.as_os_str().is_empty() {
        print_usage();
        std::process::exit(1);
    }
    let (w, h, cells) = load_bin(&input)?;
    println!("Loaded {w}×{h} grid ({} cells)", cells.len());

    let mut sim = GpuSim::new(w, h, &cells, batch, engine)?;
    sim.run(steps);
    let result = sim.read_cells()?;

    if let Some(out) = &output {
        save_bin(out, w, h, &result)?;
        println!("Saved {} generations to {:?}", steps, out);
    } else {
        std::io::Write::write_all(&mut std::io::stdout(), &result)?;
    }

    Ok(())
}
