use std::env;
use std::fs;
use std::path::PathBuf;
use anyhow::{Context, Result};

const WGSL_SHADER: &str = r#"
// WireLevel WGSL — level-driven, pull-type directional wiring.
//   naga restriction: ptr<storage,...> cannot be a function parameter.
//   All storage access goes through globals b1 (read) / b2 (write).
const E: u32 = 0u; const W: u32 = 1u; const N: u32 = 2u; const S: u32 = 3u;
const K_EMPTY: u32 = 0u; const K_PIN: u32 = 1u; const K_WIRE: u32 = 2u;
const K_NAND: u32 = 3u; const K_CROSS: u32 = 4u; const K_DFF: u32 = 5u;

struct Params { width: u32, height: u32, flags: u32, }
@group(0) @binding(0) var<storage, read>       b1   : array<u32>;
@group(0) @binding(1) var<storage, read_write>  b2   : array<u32>;
@group(0) @binding(2) var<uniform>              dims : Params;

fn readBuf(i: u32) -> u32 { return b1[i]; }
fn writeBuf(i: u32, v: u32) { b2[i] = v; }
fn kind(c: u32) -> u32 { return (c >> 5u) & 7u; }
fn dir(c: u32) -> u32 { return (c >> 3u) & 3u; }
fn level(c: u32) -> u32 { return c & 1u; }

fn opposite(d: u32) -> u32 {
  switch (d) { case E { return W; } case W { return E; } case N { return S; } default { return N; } }
}
fn delta(d: u32) -> vec2<i32> {
  switch (d) { case E { return vec2( 1,  0); } case W { return vec2(-1,  0); }
               case N { return vec2( 0, -1); } default { return vec2( 0,  1); } }
}
fn presentedTo(c: u32, toward: u32) -> u32 {
  let k = kind(c);
  if (k == K_EMPTY) { return 0xFFFFFFFFu; }
  if (k == K_PIN || k == K_WIRE || k == K_NAND) { return level(c); }
  if (k == K_DFF) { return c & 1u; }
  if (k == K_CROSS) {
    let hDir = select(E, W, ((c >> 4u) & 1u) == 1u);
    let vDir = select(N, S, ((c >> 3u) & 1u) == 1u);
    if (toward == hDir) { return c & 1u; }
    if (toward == vDir) { return (c >> 1u) & 1u; }
    return 0xFFFFFFFFu;
  }
  return 0xFFFFFFFFu;
}
fn pullFrom(x: i32, y: i32, side: u32) -> u32 {
  let d = delta(side);
  let nx = x + d.x; let ny = y + d.y;
  if (nx < 0 || ny < 0 || nx >= i32(dims.width) || ny >= i32(dims.height)) { return 0xFFFFFFFFu; }
  return presentedTo(readBuf(u32(ny) * dims.width + u32(nx)), opposite(side));
}
fn stepCell(x: i32, y: i32) -> u32 {
  let i = u32(y) * dims.width + u32(x);
  let cell = readBuf(i);
  let k = kind(cell);
  if (k == K_EMPTY || k == K_PIN) { return cell; }
  if (k == K_WIRE) {
    return (cell & 0xF8u) | select(0u, 1u, pullFrom(x, y, opposite(dir(cell))) == 1u);
  }
  if (k == K_NAND) {
    let d = dir(cell);
    var allTrue: bool = true; var anyInput: bool = false;
    for (var s: u32 = 0u; s < 4u; s = s + 1u) {
      if (s != d) {
        let v = pullFrom(x, y, s);
        if (v != 0xFFFFFFFFu) { anyInput = true; if (v == 0u) { allTrue = false; } }
      }
    }
    return (cell & 0xF8u) | select(0u, 1u, anyInput && !allTrue);
  }
  if (k == K_CROSS) {
    let hd = select(E, W, ((cell >> 4u) & 1u) == 1u);
    let vd = select(N, S, ((cell >> 3u) & 1u) == 1u);
    let hv = pullFrom(x, y, opposite(hd));
    let vv = pullFrom(x, y, opposite(vd));
    return (cell & 0xF8u) | (select(0u, 1u, vv == 1u) << 1u) | select(0u, 1u, hv == 1u);
  }
  if (k == K_DFF) {
    let d = dir(cell);
    let dVal = select(0u, 1u, pullFrom(x, y, opposite(d)) == 1u);
    var clk: bool = false;
    for (var s: u32 = 0u; s < 4u; s = s + 1u) {
      let isPerp = ((d == E || d == W) && (s == N || s == S)) || ((d == N || d == S) && (s == E || s == W));
      if (isPerp) { if (pullFrom(x, y, s) == 1u) { clk = true; } }
    }
    let prevClk = (cell >> 1u) & 1u;
    let q = select(cell & 1u, dVal, clk && prevClk == 0u);
    return (cell & 0xF8u) | (select(0u, 1u, clk) << 1u) | q;
  }
  return cell;
}

@compute @workgroup_size(16, 16)
fn step(@builtin(global_invocation_id) gid: vec3u) {
  let x = i32(gid.x); let y = i32(gid.y);
  if (x >= i32(dims.width) || y >= i32(dims.height)) { return; }
  let i = u32(y) * dims.width + u32(x);
  writeBuf(i, stepCell(x, y));
}
"#;

fn load_bin(path: &PathBuf) -> Result<(u32, u32, Vec<u8>)> {
    let data = fs::read(path).context("reading .bin file")?;
    if data.len() < 8 {
        anyhow::bail!("file too small: {} bytes", data.len());
    }
    let w = u32::from_le_bytes(data[0..4].try_into().unwrap());
    let h = u32::from_le_bytes(data[4..8].try_into().unwrap());
    let expected = 8 + (w as usize) * (h as usize);
    if data.len() != expected {
        anyhow::bail!("size mismatch: expected {} got {}", expected, data.len());
    }
    Ok((w, h, data[8..].to_vec()))
}

fn save_bin(path: &PathBuf, w: u32, h: u32, cells: &[u8]) -> Result<()> {
    let mut buf = Vec::with_capacity(8 + cells.len());
    buf.extend_from_slice(&w.to_le_bytes());
    buf.extend_from_slice(&h.to_le_bytes());
    buf.extend_from_slice(cells);
    fs::write(path, buf).context("writing .bin file")?;
    Ok(())
}

fn print_usage() {
    eprintln!("Usage: wgpu-runner <input.bin> [--steps N] [--output out.bin] [--batch B]");
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

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--steps" => { i += 1; steps = args[i].parse().context("--steps must be a number")?; }
            "--output" => { i += 1; output = Some(PathBuf::from(&args[i])); }
            "--batch" => { i += 1; batch = args[i].parse().context("--batch must be a number")?; }
            s if s.starts_with('-') => { anyhow::bail!("unknown flag {s}"); }
            _ => { input = PathBuf::from(&args[i]); }
        }
        i += 1;
    }

    let (w, h, mut cells) = load_bin(&input)?;
    let cell_count = (w * h) as usize;
    let nc = cells.len();
    println!("Loaded {w}×{h} grid ({nc} cells)",);

    let u32_count = cell_count;
    let mut src_data = Vec::with_capacity(u32_count);
    for i in 0..cell_count {
        src_data.push(cells[i] as u32);
    }

    // ---- wgpu init ----
    let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor {
        backends: wgpu::Backends::VULKAN,
        ..Default::default()
    });
    let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
        power_preference: wgpu::PowerPreference::HighPerformance,
        ..Default::default()
    })).context("no WebGPU adapter (is Vulkan driver available?)")?;

    let adapter_info = adapter.get_info();
    println!("Adapter: {} ({:?})", adapter_info.name, adapter_info.backend);

    let (device, queue) = pollster::block_on(adapter.request_device(
        &wgpu::DeviceDescriptor {
            label: Some("wgpu-runner"),
            required_features: wgpu::Features::empty(),
            required_limits: wgpu::Limits::default(),
            memory_hints: wgpu::MemoryHints::default(),
        },
        None,
    )).context("requesting device")?;

    // ---- Buffers ----
    let buf_size = (u32_count * 4) as u64;
    let src_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("src"),
        size: buf_size,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let dst_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("dst"),
        size: buf_size,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("params"),
        size: 12,
        usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });

    // Upload initial data
    queue.write_buffer(&src_buf, 0, bytemuck::cast_slice(&src_data));
    queue.write_buffer(&dst_buf, 0, bytemuck::cast_slice(&src_data));
    queue.write_buffer(&params_buf, 0, bytemuck::cast_slice(&[w, h, 0u32]));

    // ---- Shader ----
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("wirelevel"),
        source: wgpu::ShaderSource::Wgsl(std::borrow::Cow::Borrowed(WGSL_SHADER)),
    });

    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some("step"),
        layout: None,
        module: &shader,
        entry_point: Some("step"),
        cache: None,
        compilation_options: Default::default(),
    });

    let bind_group_layout = pipeline.get_bind_group_layout(0);
    let make_bind_group = |src: &wgpu::Buffer, dst: &wgpu::Buffer| -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: None,
            layout: &bind_group_layout,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: src.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: dst.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: params_buf.as_entire_binding() },
            ],
        })
    };
    let bind_groups = [
        make_bind_group(&src_buf, &dst_buf),
        make_bind_group(&dst_buf, &src_buf),
    ];

    // ---- Read-back buffer ----
    let read_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("read"),
        size: buf_size,
        usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });

    let mut ping = true;
    let mut remaining = steps;
    let gx = (w + 15) / 16;
    let gy = (h + 15) / 16;

    while remaining > 0 {
        let b = remaining.min(batch);
        let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("sim"),
        });
        for _ in 0..b {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("step"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&pipeline);
            pass.set_bind_group(0, if ping { &bind_groups[0] } else { &bind_groups[1] }, &[]);
            pass.dispatch_workgroups(gx, gy, 1);
            ping = !ping;
        }
        queue.submit([encoder.finish()]);
        remaining -= b;
    }

    // Read back result
    let final_buf = if ping { &src_buf } else { &dst_buf };
    let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
        label: Some("readback"),
    });
    encoder.copy_buffer_to_buffer(final_buf, 0, &read_buf, 0, buf_size);
    queue.submit([encoder.finish()]);

    let slice = read_buf.slice(..);
    slice.map_async(wgpu::MapMode::Read, |_| {});
    device.poll(wgpu::Maintain::Wait);
    let mapped = slice.get_mapped_range();
    let result: &[u32] = bytemuck::cast_slice(&mapped);
    cells.clear();
    for &v in result.iter().take(cell_count) {
        cells.push((v & 0xFF) as u8);
    }
    drop(mapped);
    read_buf.destroy();

    if let Some(out) = &output {
        save_bin(out, w, h, &cells)?;
        println!("Saved {} generations to {:?}", steps, out);
    } else {
        std::io::Write::write_all(&mut std::io::stdout(), &cells)?;
    }

    Ok(())
}
