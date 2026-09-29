// GpuSim — WireLevel CA の GPU シミュレータ本体。
// ping-pong 2 バッファで世代を進め、収束判定は GPU 上の変化ログ (世代ごとの変化タイル数) で行う。
use std::fs;
use std::path::Path;
use anyhow::{Context, Result};

/// WireLevel の世代を進めるシェーダー。先頭に BLOCK_GENS の定義を付け足して使う (shader_source)
const WGSL_SHADER: &str = include_str!("wirelevel.wgsl");

/// 診断用の累積時間 (ns)。`WWC_STATS=1` のとき memory_program が表示する。
/// 1 プロセス 1 GPU なのでグローバルで足りる。
/// encode: dispatch 列の記録 + submit (CPU 側)。wait: 読み戻しの map 待ち (未実行の GPU 仕事を含む)
pub static STAT_ENCODE_NS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
pub static STAT_WAIT_NS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

/// タイル (= workgroup) の一辺。wirelevel.wgsl の TILE / @workgroup_size と一致させる
const TILE_SIZE: u32 = 16;
/// 1 回の読み戻しで扱える最大世代数 (changeLog と GenParams テーブルの長さ)
const MAX_GENS_PER_BATCH: u32 = 1024;
/// Tiled で 1 dispatch に進める世代数 (= halo の幅)。shared memory は 2×(16+2K)² × 4 バイト。
/// 実測 (2026-09-29、34 本の GPU 全周期照合): K=3 で 77.6 秒 / K=4 79.2 秒 / K=8 112.6 秒 /
/// K=12 以上は halo の再計算で悪化 / K=1 は dispatch 律速で悪化 (daa_edge 単体で 16.8 秒)。
/// block_* は 1 世代ごとに計算範囲 (16+2K)² を走査するので、K を上げても世代あたりコストは下がらない
const BLOCK_GENS: u32 = 3;
// block_* の shared memory (計算範囲 (16+2K)² の 2 面) が既定の上限 16 KiB に収まること
const _: () = assert!(2 * (TILE_SIZE + 2 * BLOCK_GENS) * (TILE_SIZE + 2 * BLOCK_GENS) * 4 <= 16384);
/// pack_bytes の workgroup の大きさ (wirelevel.wgsl の PACK_WORKGROUP)
const PACK_WORKGROUP: u32 = 256;
/// 1 回の submit に積む dispatch 数。小さいほど GPU が早く走り出し、CPU の記録と重なる
const SUBMIT_CHUNK_DISPATCHES: usize = 16;
/// GenParams テーブルの 1 エントリの間隔 (dynamic offset の既定アラインメント)
const GEN_PARAMS_STRIDE: u64 = 256;
/// GenParams テーブルのエントリ: 0..MAX_GENS_PER_BATCH は { slot: i, gens: BLOCK_GENS } (固定)、
/// 全タイル計算用は { slot: 0, gens: 1 } (固定)、端数ブロック用はバッチごとに書く
const GEN_PARAMS_FULL_ENTRY: u32 = MAX_GENS_PER_BATCH;
const GEN_PARAMS_PARTIAL_ENTRY: u32 = MAX_GENS_PER_BATCH + 1;
const GEN_PARAMS_ENTRIES: u32 = MAX_GENS_PER_BATCH + 2;
/// Dims uniform のバイト数 (5 × u32 を 16 バイト境界に切り上げ)
const DIMS_SIZE: u64 = 32;
/// DispatchArgs.x の初期値 = 制御用 workgroup の数 (wirelevel.wgsl の ARGS_CONTROL_WORKGROUPS)
const ARGS_CONTROL_WORKGROUPS: u32 = 1;
/// stamp_base がこれを超えたら stamps を 0 に戻す (u32 の桁あふれ防止)
const STAMP_RESET_THRESHOLD: u32 = u32::MAX - 2 * MAX_GENS_PER_BATCH;
/// アクティブリストの本数 (cur / next / free を世代ごとに回す)
const LIST_ROTATION: usize = 3;

/// 世代の進め方。どちらも同じ CA 規則で、結果のグリッドは byte 単位で一致する。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Engine {
    /// 毎世代全タイルを計算する (参照実装)
    Dense,
    /// 前世代に変化したタイルとその隣接タイルだけを計算する
    Tiled,
}

impl std::str::FromStr for Engine {
    type Err = anyhow::Error;
    fn from_str(s: &str) -> Result<Self> {
        match s {
            "dense" => Ok(Engine::Dense),
            "tiled" => Ok(Engine::Tiled),
            _ => anyhow::bail!("unknown engine {s:?} (expected dense or tiled)"),
        }
    }
}

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
/// Tiled エンジンの GPU 資源と、世代をまたいで持ち越す状態。
struct TiledStepper {
    block_full: wgpu::ComputePipeline,
    block_list: wgpu::ComputePipeline,
    /// args[i]: list[i] の dispatch_workgroups_indirect 引数 (x = 1 + タイル数)
    args: [wgpu::Buffer; LIST_ROTATION],
    /// list_bind_groups[r]: cur = list[r]、next = list[r+1]、free = args[r+2] (mod 3)。dispatch ごとに回す
    list_bind_groups: [wgpu::BindGroup; LIST_ROTATION],
    stamps: wgpu::Buffer,
    /// 次の dispatch が cur として使うリストの添字
    rot: usize,
    /// 次のバッチの Dims.stamp_base
    stamp_base: u32,
    /// 次の世代を block_full で計算するか (初回と、ホストがセルを書いた直後)
    needs_full_step: bool,
}

enum Stepper {
    Dense { step_dense: wgpu::ComputePipeline },
    Tiled(TiledStepper),
}

pub struct GpuSim {
    device: wgpu::Device,
    queue: wgpu::Queue,
    stepper: Stepper,
    dims_buf: wgpu::Buffer,
    /// GenParams テーブル (端数ブロックのエントリだけバッチごとに書く)
    gen_params_buf: wgpu::Buffer,
    cell_bufs: [wgpu::Buffer; 2],
    /// cell_bind_groups[i]: cell_bufs[i] を読み cell_bufs[1-i] に書く
    cell_bind_groups: [wgpu::BindGroup; 2],
    /// 世代ごとの「変化したタイル数」。changeLog[slot] == 0 ⇔ その世代で step(g) == g
    change_log: wgpu::Buffer,
    change_log_read: wgpu::Buffer,
    /// pack_bytes: 現在の世代を 1 セル 1 バイトに詰めて packed_cells に書く
    pack_bytes: wgpu::ComputePipeline,
    pack_bind_group: wgpu::BindGroup,
    packed_cells: wgpu::Buffer,
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

/// バッチ内の 1 dispatch
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum DispatchPlan {
    /// step_dense で世代 slot を計算する
    Dense { slot: u32 },
    /// block_full で世代 0 を全タイル計算する
    Full,
    /// block_list で世代 slot..slot+gens をアクティブタイルだけ計算する
    Block { slot: u32, gens: u32 },
}

impl DispatchPlan {
    /// この dispatch が使う GenParams テーブルのエントリ
    fn gen_params_entry(&self) -> u32 {
        match *self {
            DispatchPlan::Dense { slot } => slot,
            DispatchPlan::Full => GEN_PARAMS_FULL_ENTRY,
            DispatchPlan::Block { slot, gens } if gens == BLOCK_GENS => slot,
            DispatchPlan::Block { .. } => GEN_PARAMS_PARTIAL_ENTRY,
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum PipelineKind { Dense, BlockFull, BlockList }

/// Tiled のバッチ n 世代を dispatch 列にする: (全タイル 1 世代) + BLOCK_GENS 世代のブロック + 端数ブロック 1 個以下
fn plan_tiled_batch(n: u32, starts_with_full: bool) -> Vec<DispatchPlan> {
    let mut plan = Vec::new();
    let mut slot = 0;
    if starts_with_full {
        plan.push(DispatchPlan::Full);
        slot = 1;
    }
    while slot < n {
        let gens = BLOCK_GENS.min(n - slot);
        plan.push(DispatchPlan::Block { slot, gens });
        slot += gens;
    }
    plan
}

/// pack_bytes の dispatch (x, y): words 個の u32 を PACK_WORKGROUP ずつ、x が上限を超えないように 2 次元に並べる
fn pack_dispatch_size(words: u64, max_per_dimension: u32) -> (u32, u32) {
    let workgroups = words.div_ceil(PACK_WORKGROUP as u64).max(1);
    let gx = workgroups.min(max_per_dimension as u64);
    let gy = workgroups.div_ceil(gx);
    (gx as u32, gy as u32)
}

/// wirelevel.wgsl の先頭にホスト側の定数を付け足したもの
fn shader_source() -> String {
    format!("const BLOCK_GENS: u32 = {BLOCK_GENS}u;\n{WGSL_SHADER}")
}

/// run_logged が返す、世代ごとの変化タイル数 (添字 = バッチ内の世代番号)
type ChangeLog = Vec<u32>;

impl GpuSim {
    pub fn new(w: u32, h: u32, cells: &[u8], batch: u32, engine: Engine) -> Result<Self> {
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
            size: DIMS_SIZE,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&dims_buf, 0, bytemuck::cast_slice(&[w, h, tiles_x, tiles_y, 0u32, 0, 0, 0]));

        // GenParams テーブル: エントリ slot は { slot }。dynamic offset slot*STRIDE で世代ごとに選ぶ
        let gen_params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("gen_params"),
            size: GEN_PARAMS_STRIDE * GEN_PARAMS_ENTRIES as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let mut gen_params = vec![0u32; (GEN_PARAMS_STRIDE / 4) as usize * GEN_PARAMS_ENTRIES as usize];
        let entry_word = |entry: u32| (entry as u64 * GEN_PARAMS_STRIDE / 4) as usize;
        for slot in 0..MAX_GENS_PER_BATCH {
            gen_params[entry_word(slot)] = slot;
            gen_params[entry_word(slot) + 1] = BLOCK_GENS;
        }
        gen_params[entry_word(GEN_PARAMS_FULL_ENTRY)] = 0;
        gen_params[entry_word(GEN_PARAMS_FULL_ENTRY) + 1] = 1;
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
        // 1 セル 1 バイトに詰めた読み戻し用 (u32 境界に切り上げ)
        let packed_size = (cell_count as u64).div_ceil(4) * 4;
        let packed_cells = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("packed_cells"),
            size: packed_size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let cells_read = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("cells_read"),
            size: packed_size,
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
            size: Some(std::num::NonZeroU64::new(8).expect("8 != 0")),
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
            source: wgpu::ShaderSource::Wgsl(std::borrow::Cow::Owned(shader_source())),
        });
        let make_pipeline = |label: &str, layout: &wgpu::PipelineLayout| device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some(label),
            layout: Some(layout),
            module: &shader,
            entry_point: Some(label),
            cache: None,
            compilation_options: Default::default(),
        });

        let pack_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("pack"),
            entries: &[storage_entry(0, false)],
        });
        let pack_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("pack"),
            layout: &pack_layout,
            entries: &[wgpu::BindGroupEntry { binding: 0, resource: packed_cells.as_entire_binding() }],
        });
        let pack_pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("pack"),
            bind_group_layouts: &[&cell_layout, &pack_layout],
            push_constant_ranges: &[],
        });
        let pack_bytes = make_pipeline("pack_bytes", &pack_pipeline_layout);

        let stepper = match engine {
            Engine::Dense => {
                let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some("dense"),
                    bind_group_layouts: &[&cell_layout],
                    push_constant_ranges: &[],
                });
                Stepper::Dense { step_dense: make_pipeline("step_dense", &layout) }
            }
            Engine::Tiled => {
                let tile_count = tiles_x * tiles_y;
                let max_workgroups = device.limits().max_compute_workgroups_per_dimension;
                anyhow::ensure!(tile_count + ARGS_CONTROL_WORKGROUPS <= max_workgroups,
                    "grid {w}x{h} has {tile_count} tiles; tiled engine dispatches up to {} workgroups (limit {max_workgroups}). Use --engine dense",
                    tile_count + ARGS_CONTROL_WORKGROUPS);
                let list_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("active_lists"),
                    entries: &[
                        storage_entry(0, true),
                        storage_entry(1, false),
                        storage_entry(2, false),
                        storage_entry(3, false),
                        storage_entry(4, false),
                    ],
                });
                let make_list = |i: usize| device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some(&format!("list{i}")),
                    size: 4 * tile_count as u64,
                    usage: wgpu::BufferUsages::STORAGE,
                    mapped_at_creation: false,
                });
                let lists: [wgpu::Buffer; LIST_ROTATION] = std::array::from_fn(make_list);
                let make_args = |i: usize| device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some(&format!("args{i}")),
                    size: 12,
                    usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::INDIRECT | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                let args: [wgpu::Buffer; LIST_ROTATION] = std::array::from_fn(make_args);
                let stamps = device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("stamps"),
                    size: 4 * tile_count as u64,
                    usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                let list_bind_groups: [wgpu::BindGroup; LIST_ROTATION] = std::array::from_fn(|r| {
                    let next = (r + 1) % LIST_ROTATION;
                    let free = (r + 2) % LIST_ROTATION;
                    device.create_bind_group(&wgpu::BindGroupDescriptor {
                        label: None,
                        layout: &list_layout,
                        entries: &[
                            wgpu::BindGroupEntry { binding: 0, resource: lists[r].as_entire_binding() },
                            wgpu::BindGroupEntry { binding: 1, resource: lists[next].as_entire_binding() },
                            wgpu::BindGroupEntry { binding: 2, resource: args[next].as_entire_binding() },
                            wgpu::BindGroupEntry { binding: 3, resource: args[free].as_entire_binding() },
                            wgpu::BindGroupEntry { binding: 4, resource: stamps.as_entire_binding() },
                        ],
                    })
                });
                let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some("tiled"),
                    bind_group_layouts: &[&cell_layout, &list_layout],
                    push_constant_ranges: &[],
                });
                Stepper::Tiled(TiledStepper {
                    block_full: make_pipeline("block_full", &layout),
                    block_list: make_pipeline("block_list", &layout),
                    args, list_bind_groups, stamps,
                    rot: 0,
                    stamp_base: 0,
                    // 初期グリッドが固定点とは限らないので、初回は全タイルを計算する
                    needs_full_step: true,
                })
            }
        };

        Ok(GpuSim {
            device, queue, stepper, dims_buf, gen_params_buf,
            cell_bufs, cell_bind_groups,
            change_log, change_log_read,
            pack_bytes, pack_bind_group, packed_cells, cells_read,
            w, h, tiles_x, tiles_y, batch,
            front: 0,
        })
    }

    /// 次の step が読む側 (front) のバッファ。ピン書き換えはここに行う。
    fn front_buf(&self) -> &wgpu::Buffer {
        &self.cell_bufs[self.front]
    }

    /// バッチ (changeLog の slot 0..n を使う連続した n 世代) の dispatch 列を決め、準備をする。
    /// Tiled では stamp_base、端数ブロックの GenParams、全タイル計算から始めるなら args の初期化を
    /// queue.write_buffer で行う。write_buffer は次の submit の前に実行されるので、呼び出し側は
    /// バッチの最初の encoder を次に submit すること。
    fn begin_batch(&mut self, encoder: &mut wgpu::CommandEncoder, n: u32) -> Vec<DispatchPlan> {
        debug_assert!((1..=MAX_GENS_PER_BATCH).contains(&n));
        let t = match &mut self.stepper {
            Stepper::Dense { .. } => return (0..n).map(|slot| DispatchPlan::Dense { slot }).collect(),
            Stepper::Tiled(t) => t,
        };
        if t.stamp_base > STAMP_RESET_THRESHOLD {
            encoder.clear_buffer(&t.stamps, 0, None);
            t.stamp_base = 0;
        }
        self.queue.write_buffer(&self.dims_buf, 16, bytemuck::cast_slice(&[t.stamp_base]));
        let plan = plan_tiled_batch(n, t.needs_full_step);
        if t.needs_full_step {
            // block_full は argsNext に積み、argsFree は触らない。全リストを空にしてから始める
            for a in &t.args {
                self.queue.write_buffer(a, 0, bytemuck::cast_slice(&[ARGS_CONTROL_WORKGROUPS, 1, 1]));
            }
            t.needs_full_step = false;
        }
        for d in &plan {
            if let DispatchPlan::Block { slot, gens } = *d {
                if gens != BLOCK_GENS {
                    let offset = GEN_PARAMS_PARTIAL_ENTRY as u64 * GEN_PARAMS_STRIDE;
                    self.queue.write_buffer(&self.gen_params_buf, offset, bytemuck::cast_slice(&[slot, gens]));
                }
            }
        }
        plan
    }

    /// バッチを閉じる (n = バッチの世代数)。次のバッチのスタンプが今回のものより大きくなるようにする
    fn end_batch(&mut self, n: u32) {
        if let Stepper::Tiled(t) = &mut self.stepper {
            t.stamp_base += n;
        }
    }

    /// dispatch 列を 1 つの compute pass に積む。
    fn encode_dispatches(&mut self, encoder: &mut wgpu::CommandEncoder, plan: &[DispatchPlan]) {
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("step"),
            timestamp_writes: None,
        });
        // 今 pass に設定しているパイプライン (同じなら設定し直さない)
        let mut current: Option<PipelineKind> = None;
        for d in plan {
            let offset = (d.gen_params_entry() as u64 * GEN_PARAMS_STRIDE) as u32;
            pass.set_bind_group(0, &self.cell_bind_groups[self.front], &[offset]);
            match (&mut self.stepper, *d) {
                (Stepper::Dense { step_dense }, DispatchPlan::Dense { .. }) => {
                    if current != Some(PipelineKind::Dense) {
                        pass.set_pipeline(step_dense);
                        current = Some(PipelineKind::Dense);
                    }
                    pass.dispatch_workgroups(self.tiles_x, self.tiles_y, 1);
                }
                (Stepper::Tiled(t), DispatchPlan::Full) => {
                    pass.set_bind_group(1, &t.list_bind_groups[t.rot], &[]);
                    if current != Some(PipelineKind::BlockFull) {
                        pass.set_pipeline(&t.block_full);
                        current = Some(PipelineKind::BlockFull);
                    }
                    pass.dispatch_workgroups(self.tiles_x, self.tiles_y, 1);
                    t.rot = (t.rot + 1) % LIST_ROTATION;
                }
                (Stepper::Tiled(t), DispatchPlan::Block { .. }) => {
                    pass.set_bind_group(1, &t.list_bind_groups[t.rot], &[]);
                    if current != Some(PipelineKind::BlockList) {
                        pass.set_pipeline(&t.block_list);
                        current = Some(PipelineKind::BlockList);
                    }
                    pass.dispatch_workgroups_indirect(&t.args[t.rot], 0);
                    t.rot = (t.rot + 1) % LIST_ROTATION;
                }
                (_, plan) => unreachable!("dispatch {plan:?} does not match the engine"),
            }
            self.front = 1 - self.front;
        }
    }

    /// n 世代 (1 バッチ) を SUBMIT_CHUNK_DISPATCHES 個ずつ submit する。GPU が前の塊を実行している間に
    /// CPU が次の塊を記録するので、記録と実行が重なる。
    /// first は最初の encoder への追加 (changeLog のクリア等)、last は最後の encoder への追加 (読み戻しのコピー等)
    fn submit_batch(&mut self, n: u32,
                    first: impl FnOnce(&mut wgpu::CommandEncoder),
                    last: impl FnOnce(&mut wgpu::CommandEncoder)) {
        let t0 = std::time::Instant::now();
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("sim") });
        first(&mut encoder);
        let plan = self.begin_batch(&mut encoder, n);
        let mut chunks = plan.chunks(SUBMIT_CHUNK_DISPATCHES).peekable();
        let mut last = Some(last);
        while let Some(chunk) = chunks.next() {
            self.encode_dispatches(&mut encoder, chunk);
            if chunks.peek().is_none() {
                if let Some(f) = last.take() { f(&mut encoder); }
            }
            self.queue.submit([encoder.finish()]);
            encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("sim") });
        }
        self.end_batch(n);
        STAT_ENCODE_NS.fetch_add(t0.elapsed().as_nanos() as u64, std::sync::atomic::Ordering::Relaxed);
    }

    /// steps 世代進める (変化ログは読まない)。
    pub fn run(&mut self, steps: u32) {
        let per_batch = self.batch.min(MAX_GENS_PER_BATCH);
        let mut remaining = steps;
        while remaining > 0 {
            let n = remaining.min(per_batch);
            self.submit_batch(n, |_| {}, |_| {});
            remaining -= n;
        }
    }

    /// n 世代進め、世代ごとの変化タイル数を読み戻す。
    fn run_logged(&mut self, n: u32) -> Result<ChangeLog> {
        anyhow::ensure!((1..=MAX_GENS_PER_BATCH).contains(&n), "run_logged: n={n} out of 1..={MAX_GENS_PER_BATCH}");
        let log_bytes = 4 * n as u64;
        let change_log = self.change_log.clone();
        let change_log_read = self.change_log_read.clone();
        self.submit_batch(n,
            |e| e.clear_buffer(&change_log, 0, Some(log_bytes)),
            |e| e.copy_buffer_to_buffer(&change_log, 0, &change_log_read, 0, log_bytes));
        map_read(&self.device, &self.change_log_read, log_bytes, |bytes| bytemuck::cast_slice::<u8, u32>(bytes).to_vec())
    }

    /// 現在の世代のセル配列を読み戻す。GPU 上で 1 セル 1 バイトに詰めてから転送する。
    pub fn read_cells(&mut self) -> Result<Vec<u8>> {
        let cell_count = (self.w as usize) * (self.h as usize);
        let packed_size = (cell_count as u64).div_ceil(4) * 4;
        let (gx, gy) = pack_dispatch_size(packed_size / 4, self.device.limits().max_compute_workgroups_per_dimension);
        let mut encoder = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("readback") });
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("pack"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&self.pack_bytes);
            // pack_bytes は b1 (= front) だけを読む。GenParams は見ないのでエントリ 0 でよい
            pass.set_bind_group(0, &self.cell_bind_groups[self.front], &[0]);
            pass.set_bind_group(1, &self.pack_bind_group, &[]);
            pass.dispatch_workgroups(gx, gy, 1);
        }
        encoder.copy_buffer_to_buffer(&self.packed_cells, 0, &self.cells_read, 0, packed_size);
        self.queue.submit([encoder.finish()]);
        map_read(&self.device, &self.cells_read, packed_size, |bytes| bytes[..cell_count].to_vec())
    }

    /// front バッファの 1 セルを書き換える。Pin セルは step で不変・毎世代コピーされる
    /// ため、front 側 1 バッファへの書き込みだけで以降の全世代に反映される。
    /// Tiled では次の 1 世代を全タイル計算にする (書いたセルのタイルと隣接タイルを確実に動かし、
    /// 書いたタイルで back 側に残る古い値を上書きするため。F# settleIncremental の「初回は全セル評価」と同じ)
    pub fn write_cell(&mut self, x: u32, y: u32, byte: u8) {
        let offset = ((y * self.w + x) as u64) * 4;
        self.queue.write_buffer(self.front_buf(), offset, bytemuck::cast_slice(&[byte as u32]));
        if let Stepper::Tiled(t) = &mut self.stepper {
            t.needs_full_step = true;
        }
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
    let t0 = std::time::Instant::now();
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
    STAT_WAIT_NS.fetch_add(t0.elapsed().as_nanos() as u64, std::sync::atomic::Ordering::Relaxed);
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tiled_batch_starts_with_full_step_then_covers_every_generation_once() {
        let plan = plan_tiled_batch(20, true);
        assert_eq!(plan[0], DispatchPlan::Full);
        let mut next_slot = 1;
        for d in &plan[1..] {
            let DispatchPlan::Block { slot, gens } = *d else { panic!("unexpected {d:?}") };
            assert_eq!(slot, next_slot);
            assert!((1..=BLOCK_GENS).contains(&gens));
            next_slot += gens;
        }
        assert_eq!(next_slot, 20);
    }

    #[test]
    fn tiled_batch_has_at_most_one_partial_block_at_the_end() {
        for n in 1..=3 * BLOCK_GENS + 1 {
            for full in [false, true] {
                let plan = plan_tiled_batch(n, full);
                let partial: Vec<usize> = plan.iter().enumerate()
                    .filter(|(_, d)| matches!(d, DispatchPlan::Block { gens, .. } if *gens != BLOCK_GENS))
                    .map(|(i, _)| i)
                    .collect();
                assert!(partial.len() <= 1, "n={n} full={full}: {plan:?}");
                if let Some(&i) = partial.first() {
                    assert_eq!(i, plan.len() - 1, "partial block must be last (n={n} full={full})");
                }
            }
        }
    }

    #[test]
    fn gen_params_entry_of_partial_block_is_the_per_batch_entry() {
        assert_eq!(DispatchPlan::Block { slot: 3, gens: BLOCK_GENS }.gen_params_entry(), 3);
        assert_eq!(DispatchPlan::Block { slot: 3, gens: BLOCK_GENS - 1 }.gen_params_entry(), GEN_PARAMS_PARTIAL_ENTRY);
        assert_eq!(DispatchPlan::Full.gen_params_entry(), GEN_PARAMS_FULL_ENTRY);
    }

    #[test]
    fn pack_dispatch_covers_all_words_within_dimension_limit() {
        for (words, max) in [(1u64, 65535u32), (771_962, 65535), (40_000_000, 65535), (1000, 3)] {
            let (gx, gy) = pack_dispatch_size(words, max);
            assert!(gx <= max && gy <= max);
            assert!(gx as u64 * gy as u64 * PACK_WORKGROUP as u64 >= words);
        }
    }
}
