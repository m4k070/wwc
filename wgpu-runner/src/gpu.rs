// GpuSim — WireLevel CA の GPU シミュレータ本体。
// ping-pong 2 バッファで世代を進め、収束判定は GPU 上の変化ログ (世代ごとの変化タイル数) で行う。
use std::fs;
use std::path::Path;
use anyhow::{Context, Result};

/// WireLevel の 1 世代を計算するシェーダー (step_dense)
const WGSL_SHADER: &str = include_str!("wirelevel.wgsl");

/// タイル (= workgroup) の一辺。wirelevel.wgsl の TILE / @workgroup_size と一致させる
const TILE_SIZE: u32 = 16;
/// 1 回の読み戻しで扱える最大世代数 (changeLog と GenParams テーブルの長さ)
const MAX_GENS_PER_BATCH: u32 = 1024;
/// GenParams テーブルの 1 エントリの間隔 (dynamic offset の既定アラインメント)
const GEN_PARAMS_STRIDE: u64 = 256;

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


/// WireLevel CA の GPU シミュレータ。
/// セルは u32 に 1 個 (下位 8 ビット)。ping-pong 2 バッファで、front が現在の世代を持つ。
pub struct GpuSim {
    device: wgpu::Device,
    queue: wgpu::Queue,
    step_dense: wgpu::ComputePipeline,
    cell_bufs: [wgpu::Buffer; 2],
    /// cell_bind_groups[i]: cell_bufs[i] を読み cell_bufs[1-i] に書く
    cell_bind_groups: [wgpu::BindGroup; 2],
    /// 世代ごとの「変化したタイル数」。changeLog[slot] == 0 ⇔ その世代で step(g) == g
    change_log: wgpu::Buffer,
    change_log_read: wgpu::Buffer,
    cells_read: wgpu::Buffer,
    pub w: u32,
    pub h: u32,
    tiles_x: u32,
    tiles_y: u32,
    /// run() で 1 回の submit に積む世代数
    batch: u32,
    /// 現在の世代を持つバッファの添字 (0 or 1)
    front: usize,
}

/// run_logged が返す、世代ごとの変化タイル数 (添字 = バッチ内の世代番号)
type ChangeLog = Vec<u32>;

impl GpuSim {
    pub fn new(w: u32, h: u32, cells: &[u8], batch: u32) -> Result<Self> {
        let cell_count = (w as usize) * (h as usize);
        anyhow::ensure!(cells.len() == cell_count, "cell count mismatch: {} cells for {w}x{h}", cells.len());
        anyhow::ensure!(batch >= 1, "batch must be >= 1");

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

        let tiles_x = w.div_ceil(TILE_SIZE);
        let tiles_y = h.div_ceil(TILE_SIZE);

        let cell_buf_size = (cell_count * 4) as u64;
        let make_storage = |label: &str| device.create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size: cell_buf_size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let cell_bufs = [make_storage("cells0"), make_storage("cells1")];
        let src_data: Vec<u32> = cells.iter().map(|&c| c as u32).collect();
        for buf in &cell_bufs {
            queue.write_buffer(buf, 0, bytemuck::cast_slice(&src_data));
        }

        let dims_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("dims"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&dims_buf, 0, bytemuck::cast_slice(&[w, h, tiles_x, tiles_y]));

        // GenParams テーブル: エントリ slot は { slot }。dynamic offset slot*STRIDE で世代ごとに選ぶ
        let gen_params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("gen_params"),
            size: GEN_PARAMS_STRIDE * MAX_GENS_PER_BATCH as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let mut gen_params = vec![0u32; (GEN_PARAMS_STRIDE / 4) as usize * MAX_GENS_PER_BATCH as usize];
        for slot in 0..MAX_GENS_PER_BATCH {
            gen_params[(slot as u64 * GEN_PARAMS_STRIDE / 4) as usize] = slot;
        }
        queue.write_buffer(&gen_params_buf, 0, bytemuck::cast_slice(&gen_params));

        let change_log_size = 4 * MAX_GENS_PER_BATCH as u64;
        let change_log = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("change_log"),
            size: change_log_size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let change_log_read = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("change_log_read"),
            size: change_log_size,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let cells_read = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("cells_read"),
            size: cell_buf_size,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let storage_entry = |binding: u32, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage { read_only },
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        };
        let uniform_entry = |binding: u32, has_dynamic_offset: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset,
                min_binding_size: None,
            },
            count: None,
        };
        let cell_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("cells"),
            entries: &[
                storage_entry(0, true),
                storage_entry(1, false),
                uniform_entry(2, false),
                uniform_entry(3, true),
                storage_entry(4, false),
            ],
        });
        let gen_params_binding = wgpu::BufferBinding {
            buffer: &gen_params_buf,
            offset: 0,
            size: Some(std::num::NonZeroU64::new(4).expect("4 != 0")),
        };
        let make_cell_bind_group = |src: &wgpu::Buffer, dst: &wgpu::Buffer| device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: None,
            layout: &cell_layout,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: src.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: dst.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: dims_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::Buffer(gen_params_binding.clone()) },
                wgpu::BindGroupEntry { binding: 4, resource: change_log.as_entire_binding() },
            ],
        });
        let cell_bind_groups = [
            make_cell_bind_group(&cell_bufs[0], &cell_bufs[1]),
            make_cell_bind_group(&cell_bufs[1], &cell_bufs[0]),
        ];

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("wirelevel"),
            source: wgpu::ShaderSource::Wgsl(std::borrow::Cow::Borrowed(WGSL_SHADER)),
        });
        let dense_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("dense"),
            bind_group_layouts: &[&cell_layout],
            push_constant_ranges: &[],
        });
        let step_dense = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("step_dense"),
            layout: Some(&dense_layout),
            module: &shader,
            entry_point: Some("step_dense"),
            cache: None,
            compilation_options: Default::default(),
        });

        Ok(GpuSim {
            device, queue, step_dense,
            cell_bufs, cell_bind_groups,
            change_log, change_log_read, cells_read,
            w, h, tiles_x, tiles_y, batch,
            front: 0,
        })
    }

    /// 次の step が読む側 (front) のバッファ。ピン書き換えはここに行う。
    fn front_buf(&self) -> &wgpu::Buffer {
        &self.cell_bufs[self.front]
    }

    /// n 世代ぶんのコマンドを 1 つの compute pass に積む。世代 i は changeLog[i] に変化タイル数を足す。
    fn encode_gens(&mut self, encoder: &mut wgpu::CommandEncoder, n: u32) {
        debug_assert!(n <= MAX_GENS_PER_BATCH);
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("step"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&self.step_dense);
        for slot in 0..n {
            let offset = (slot as u64 * GEN_PARAMS_STRIDE) as u32;
            pass.set_bind_group(0, &self.cell_bind_groups[self.front], &[offset]);
            pass.dispatch_workgroups(self.tiles_x, self.tiles_y, 1);
            self.front = 1 - self.front;
        }
    }

    /// steps 世代進める (変化ログは読まない)。
    pub fn run(&mut self, steps: u32) {
        let per_submit = self.batch.min(MAX_GENS_PER_BATCH);
        let mut remaining = steps;
        while remaining > 0 {
            let n = remaining.min(per_submit);
            let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("sim") });
            self.encode_gens(&mut encoder, n);
            self.queue.submit([encoder.finish()]);
            remaining -= n;
        }
    }

    /// n 世代進め、世代ごとの変化タイル数を読み戻す。
    fn run_logged(&mut self, n: u32) -> Result<ChangeLog> {
        anyhow::ensure!((1..=MAX_GENS_PER_BATCH).contains(&n), "run_logged: n={n} out of 1..={MAX_GENS_PER_BATCH}");
        let log_bytes = 4 * n as u64;
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("sim_logged") });
        encoder.clear_buffer(&self.change_log, 0, Some(log_bytes));
        self.encode_gens(&mut encoder, n);
        encoder.copy_buffer_to_buffer(&self.change_log, 0, &self.change_log_read, 0, log_bytes);
        self.queue.submit([encoder.finish()]);
        map_read(&self.device, &self.change_log_read, log_bytes, |bytes| bytemuck::cast_slice::<u8, u32>(bytes).to_vec())
    }

    pub fn read_cells(&mut self) -> Result<Vec<u8>> {
        let cell_count = (self.w as usize) * (self.h as usize);
        let buf_size = (cell_count * 4) as u64;
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("readback") });
        encoder.copy_buffer_to_buffer(self.front_buf(), 0, &self.cells_read, 0, buf_size);
        self.queue.submit([encoder.finish()]);
        map_read(&self.device, &self.cells_read, buf_size, |bytes| {
            bytemuck::cast_slice::<u8, u32>(bytes).iter().map(|&v| (v & 0xFF) as u8).collect()
        })
    }

    /// front バッファの 1 セルを書き換える。Pin セルは step で不変・毎世代コピーされる
    /// ため、front 側 1 バッファへの書き込みだけで以降の全世代に反映される。
    pub fn write_cell(&mut self, x: u32, y: u32, byte: u8) {
        let offset = ((y * self.w + x) as u64) * 4;
        self.queue.write_buffer(self.front_buf(), offset, bytemuck::cast_slice(&[byte as u32]));
    }

    /// 固定点 (step(g) == g) まで実行する。F# WireLevel.settle (`next = cur` で停止) と同値の判定。
    ///
    /// GPU が世代ごとに変化タイル数を changeLog に書き、ホストは batch_gens 世代ごとにそれだけを読む。
    /// 「世代 t の計算で変化 0」⇔ 状態 g_t が固定点。最初のそのような t について t+1 (検証の 1 世代を含む
    /// 実行世代数) を返す。固定点に達した後の余分な世代は状態を変えないので、読み戻すセルは g_t と同じ。
    /// max_steps 以内の状態 (t <= max_steps) が固定点でなければ未収束。
    /// 戻り値: (最終セル配列, 実行世代数, 収束したか)
    pub fn run_until_settled(&mut self, max_steps: u32, batch_gens: u32) -> Result<(Vec<u8>, u32, bool)> {
        let batch_gens = batch_gens.clamp(1, MAX_GENS_PER_BATCH);
        // 状態 g_{max_steps} の検証に要る世代数
        let gens_limit = max_steps.saturating_add(1);
        let mut gens = 0u32;
        while gens < gens_limit {
            let n = batch_gens.min(gens_limit - gens);
            let log = self.run_logged(n)?;
            if let Some(i) = log.iter().position(|&changed_tiles| changed_tiles == 0) {
                gens += i as u32 + 1;
                return Ok((self.read_cells()?, gens, true));
            }
            gens += n;
        }
        Ok((self.read_cells()?, gens, false))
    }
}

/// buf の先頭 size バイトを map して f で変換する。map の失敗はエラーとして返す。
fn map_read<T>(device: &wgpu::Device, buf: &wgpu::Buffer, size: u64, f: impl FnOnce(&[u8]) -> T) -> Result<T> {
    let slice = buf.slice(..size);
    let (tx, rx) = std::sync::mpsc::channel();
    slice.map_async(wgpu::MapMode::Read, move |r| {
        // 受信側は下の recv で待っているので send は失敗しない
        let _ = tx.send(r);
    });
    device.poll(wgpu::Maintain::Wait);
    rx.recv().context("map_async callback was dropped")?.context("mapping readback buffer")?;
    let value = {
        let mapped = slice.get_mapped_range();
        f(&mapped)
    };
    buf.unmap();
    Ok(value)
}
