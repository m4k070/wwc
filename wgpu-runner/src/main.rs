mod gpu;
mod program;

use std::env;
use std::path::PathBuf;
use anyhow::{Context, Result};
use gpu::{load_bin, save_bin, GpuSim};

fn print_usage() {
    eprintln!("Usage: wgpu-runner <input.bin> [--steps N] [--output out.bin] [--batch B]");
    eprintln!("       wgpu-runner --program prog.json [--dump-regs] [--dump-dir DIR] [--batch B]");
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
    let mut dump_regs = false;
    let mut dump_dir: Option<PathBuf> = None;

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--steps" => { i += 1; steps = args[i].parse().context("--steps must be a number")?; }
            "--output" => { i += 1; output = Some(PathBuf::from(&args[i])); }
            "--batch" => { i += 1; batch = args[i].parse().context("--batch must be a number")?; }
            "--program" => { i += 1; program_path = Some(PathBuf::from(&args[i])); }
            "--dump-regs" => { dump_regs = true; }
            "--dump-dir" => { i += 1; dump_dir = Some(PathBuf::from(&args[i])); }
            s if s.starts_with('-') => { anyhow::bail!("unknown flag {s}"); }
            _ => { input = PathBuf::from(&args[i]); }
        }
        i += 1;
    }

    // ---- プログラムモード (命令レベル検証) ----
    if let Some(prog) = program_path {
        let opts = program::ProgOpts { batch, dump_regs, dump_dir };
        let code = program::run_program(&prog, &opts)?;
        std::process::exit(code);
    }

    // ---- 単発モード (golden test 用) ----
    if input.as_os_str().is_empty() {
        print_usage();
        std::process::exit(1);
    }
    let (w, h, cells) = load_bin(&input)?;
    println!("Loaded {w}×{h} grid ({} cells)", cells.len());

    let mut sim = GpuSim::new(w, h, &cells, batch)?;
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
