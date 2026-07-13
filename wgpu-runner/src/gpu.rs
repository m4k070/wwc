// GpuSim — WireLevel CA の GPU シミュレータ本体。
// ping-pong 2 バッファでバッチ実行し、任意時点でホストへ読み戻せる。
use std::fs;
use std::path::Path;
use anyhow::{Context, Result};

pub const WGSL_SHADER: &str = r#"
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

pub fn load_bin(path: &Path) -> Result<(u32, u32, Vec<u8>)> {
    let data = fs::read(path).with_context(|| format!("reading .bin file {path:?}"))?;
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

pub fn save_bin(path: &Path, w: u32, h: u32, cells: &[u8]) -> Result<()> {
    let mut buf = Vec::with_capacity(8 + cells.len());
    buf.extend_from_slice(&w.to_le_bytes());
    buf.extend_from_slice(&h.to_le_bytes());
    buf.extend_from_slice(cells);
    fs::write(path, buf).context("writing .bin file")?;
    Ok(())
}

pub struct GpuSim {
    device: wgpu::Device,
    queue: wgpu::Queue,
    pipeline: wgpu::ComputePipeline,
    bufs: [wgpu::Buffer; 2],
    bind_groups: [wgpu::BindGroup; 2],
    read_buf: wgpu::Buffer,
    pub w: u32,
    pub h: u32,
    batch: u32,
    ping: bool,
}

impl GpuSim {
    pub fn new(w: u32, h: u32, cells: &[u8], batch: u32) -> Result<Self> {
        let cell_count = (w * h) as usize;
        anyhow::ensure!(cells.len() == cell_count, "cell count mismatch");

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

        let buf_size = (cell_count * 4) as u64;
        let make_storage = |label: &str| device.create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size: buf_size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let src_buf = make_storage("src");
        let dst_buf = make_storage("dst");
        let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("params"),
            size: 12,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let src_data: Vec<u32> = cells.iter().map(|&c| c as u32).collect();
        queue.write_buffer(&src_buf, 0, bytemuck::cast_slice(&src_data));
        queue.write_buffer(&dst_buf, 0, bytemuck::cast_slice(&src_data));
        queue.write_buffer(&params_buf, 0, bytemuck::cast_slice(&[w, h, 0u32]));

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

        let read_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("read"),
            size: buf_size,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Ok(GpuSim {
            device, queue, pipeline,
            bufs: [src_buf, dst_buf],
            bind_groups, read_buf,
            w, h, batch,
            ping: true,
        })
    }

    /// 次の step が読む側 (front) のバッファ。ピン書き換えはここに行う。
    fn front_buf(&self) -> &wgpu::Buffer {
        &self.bufs[if self.ping { 0 } else { 1 }]
    }

    pub fn run(&mut self, steps: u32) {
        let gx = (self.w + 15) / 16;
        let gy = (self.h + 15) / 16;
        let mut remaining = steps;
        while remaining > 0 {
            let b = remaining.min(self.batch);
            let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("sim"),
            });
            for _ in 0..b {
                let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label: Some("step"),
                    timestamp_writes: None,
                });
                pass.set_pipeline(&self.pipeline);
                pass.set_bind_group(0, if self.ping { &self.bind_groups[0] } else { &self.bind_groups[1] }, &[]);
                pass.dispatch_workgroups(gx, gy, 1);
                self.ping = !self.ping;
            }
            self.queue.submit([encoder.finish()]);
            remaining -= b;
        }
    }

    pub fn read_cells(&mut self) -> Result<Vec<u8>> {
        let cell_count = (self.w * self.h) as usize;
        let buf_size = (cell_count * 4) as u64;
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("readback"),
        });
        encoder.copy_buffer_to_buffer(self.front_buf(), 0, &self.read_buf, 0, buf_size);
        self.queue.submit([encoder.finish()]);

        let slice = self.read_buf.slice(..);
        slice.map_async(wgpu::MapMode::Read, |_| {});
        self.device.poll(wgpu::Maintain::Wait);
        let cells = {
            let mapped = slice.get_mapped_range();
            let result: &[u32] = bytemuck::cast_slice(&mapped);
            result.iter().take(cell_count).map(|&v| (v & 0xFF) as u8).collect()
        };
        self.read_buf.unmap();
        Ok(cells)
    }

    /// front バッファの 1 セルを書き換える。Pin セルは step で不変・毎世代コピーされる
    /// ため、front 側 1 バッファへの書き込みだけで以降の全世代に反映される。
    pub fn write_cell(&mut self, x: u32, y: u32, byte: u8) {
        let offset = ((y * self.w + x) as u64) * 4;
        self.queue.write_buffer(self.front_buf(), offset, bytemuck::cast_slice(&[byte as u32]));
    }

    /// 固定点 (step(g) == g) まで実行する。F# WireLevel.settle と同値の判定。
    /// interval 世代ごとに「+1 世代して不変か」を検査する。
    /// 戻り値: (最終セル配列, 実行世代数, 収束したか)
    pub fn run_until_settled(&mut self, max_steps: u32, interval: u32) -> Result<(Vec<u8>, u32, bool)> {
        let interval = interval.max(1);
        let mut gens = 0u32;
        while gens < max_steps {
            let chunk = interval.min(max_steps - gens);
            self.run(chunk);
            gens += chunk;
            let snap = self.read_cells()?;
            self.run(1);
            gens += 1;
            let next = self.read_cells()?;
            if snap == next {
                return Ok((next, gens, true));
            }
        }
        Ok((self.read_cells()?, gens, false))
    }
}
