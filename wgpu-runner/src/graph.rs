// 遅延グラフエンジン (`--engine graph`)。配線済みグリッドを「ドライバ (Pin / NAND / DFF) と、その間の
// 遅延」のグラフに畳み、ゲート出力の遷移だけをタイムホイールで処理する。
//
// 根拠: WireLevel の Wire と Cross の各チャネルは、決まった 1 個の近傍を 1 世代遅れで写すだけ
// (v(t+1) = src(t))。だからドライバ r から配線を L セルたどった先のセルは v(t) = r(t − L) で、
// グリッチも含めて厳密 (transport delay)。cpu エンジンのセル変化の 99% はこの伝搬で、ゲート出力の
// 遷移は 1% しかない (sm83_full の 1 周期: セル変化 約 315,000 回 / ゲート遷移 約 3,000 回)。
//
// ネットリストではなくグリッドから作るので、配線の接続・遅延・クロックの到達差はそのまま入る
// (NetlistSim との違い)。固定点で cpu / GPU と一致するもの:
//   - 全セル: 配線セル = 根のドライバの値、ゲートセル = ゲートの状態
//   - 収束世代数: max(ドライバの遷移時刻 + その配線木の最大深さ) + 1
// 一致しないもの: 収束途中のセル (配線上を流れている値は持たない)。未収束で打ち切った settle の後の
// cell / snapshot は配線セルを根の現在値で埋めた近似になる (その実行はどのみち UNSETTLED で失敗)。
//
// 起動直後は cpu エンジンで動かし、最初に固定点に達したところでグラフに切り替える (初期グリッドは
// 固定点とは限らず、配線上の初期値を再現するには遷移履歴が要るため)。ドライバを持たない配線の輪
// (値を保持できる) があるグリッドはグラフにできないので、cpu エンジンのまま動かす。
use anyhow::Result;

use crate::clocking::{pin_cell, CaDriver, Settled};
use crate::cpu::{cross_hd, cross_vd, neighbors, CpuSim, E, K_CROSS, K_DFF, K_NAND, K_PIN, K_WIRE, W};
use crate::routed_meta::Xy;

/// ノードの根: 値を出すものが無い (常に 0)
const ROOT_CONST0: u32 = u32::MAX;
/// ノードの根: ドライバの無い配線の輪 (グラフにできない)
const ROOT_LOOP: u32 = u32::MAX - 1;
const UNRESOLVED: u32 = u32::MAX - 2;
const ON_STACK: u32 = u32::MAX - 3;
const NO_DRIVER: u32 = u32::MAX;

/// セルが `toward` 側の隣に提示するもの。
enum Src {
    /// 何も提示しない
    Absent,
    /// ドライバ (Pin / NAND / DFF) のセル添字
    Driver(usize),
    /// 配線ノード (セル添字 * 2 + チャネル)
    Node(usize),
}

fn presented_src(cells: &[u8], j: usize, toward: u8) -> Src {
    let c = cells[j];
    match c >> 5 {
        K_PIN | K_NAND | K_DFF => Src::Driver(j),
        K_WIRE => Src::Node(j * 2),
        K_CROSS => {
            if toward == cross_hd(c) { Src::Node(j * 2) }
            else if toward == cross_vd(c) { Src::Node(j * 2 + 1) }
            else { Src::Absent }
        }
        _ => Src::Absent,
    }
}

/// 配線ノードが値を引く相手 (Wire は dir の逆側、Cross は各チャネルの向きの逆側)。
fn node_source(cells: &[u8], pw: usize, node: usize) -> Src {
    let i = node / 2;
    let c = cells[i];
    let d = match (c >> 5, node & 1) {
        (K_WIRE, _) => (c >> 3) & 3,
        (_, 0) => cross_hd(c),
        _ => cross_vd(c),
    };
    presented_src(cells, neighbors(i, pw)[(d ^ 1) as usize], d)
}

/// グリッドから作った静的な構造。
struct Graph {
    pw: usize,
    /// セルの kind / dir (下位 3 ビットは使わない)
    base: Vec<u8>,
    /// ノード (セル * 2 + チャネル) → 根のドライバ番号 / ROOT_CONST0
    node_root: Vec<u32>,
    /// セル → ドライバ番号 (Pin / NAND / DFF 以外は NO_DRIVER)
    cell_driver: Vec<u32>,
    driver_cell: Vec<u32>,
    /// ドライバの配線木の最大深さ (配線が無ければ 0)
    depth_max: Vec<u32>,
    /// NAND: 動的な入力スロットのマスク (全部 1 なら出力 0)。入力が無い / 常に 0 の入力があるものは mode で扱う
    full: Vec<u8>,
    mode: Vec<Mode>,
    /// ファンアウト (CSR)。fan_tgt = 相手のドライバ番号 << 2 | スロット、fan_delay = 遅延 (世代)
    fan_start: Vec<u32>,
    fan_tgt: Vec<u32>,
    fan_delay: Vec<u32>,
    max_delay: u32,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Mode {
    Pin,
    /// 出力 = (入力ビット != full)
    Nand,
    /// 入力が 1 つも無い NAND (出力は常に 0) / 常に 0 の入力を持つ NAND (出力は常に 1)
    NandConst(u8),
    /// スロット 0 = D、1・2 = クロック (両隣の OR)
    Dff,
}

impl Graph {
    /// グラフにできない (配線の輪がある) ときは None。
    fn build(cells: &[u8], pw: usize) -> Option<Graph> {
        let n = cells.len();
        let mut cell_driver = vec![NO_DRIVER; n];
        let mut driver_cell = Vec::new();
        for (i, &c) in cells.iter().enumerate() {
            if matches!(c >> 5, K_PIN | K_NAND | K_DFF) {
                cell_driver[i] = driver_cell.len() as u32;
                driver_cell.push(i as u32);
            }
        }
        let nd = driver_cell.len();

        // 配線ノードを根までたどる。depth = 根のドライバからのセル数 (v(t) = r(t − depth))
        let mut node_root = vec![ROOT_CONST0; n * 2];
        let mut node_depth = vec![0u32; n * 2];
        for (i, &c) in cells.iter().enumerate() {
            match c >> 5 {
                K_WIRE => node_root[i * 2] = UNRESOLVED,
                K_CROSS => {
                    node_root[i * 2] = UNRESOLVED;
                    node_root[i * 2 + 1] = UNRESOLVED;
                }
                _ => {}
            }
        }
        let mut depth_max = vec![0u32; nd];
        let mut stack: Vec<usize> = Vec::new();
        for start in 0..n * 2 {
            if node_root[start] != UNRESOLVED { continue; }
            let mut cur = start;
            // (根, 根から stack の最後のノードの 1 つ手前までの深さ)
            let (root, mut depth) = loop {
                node_root[cur] = ON_STACK;
                stack.push(cur);
                match node_source(cells, pw, cur) {
                    Src::Absent => break (ROOT_CONST0, 0),
                    Src::Driver(j) => break (cell_driver[j], 0),
                    Src::Node(next) => match node_root[next] {
                        ON_STACK => break (ROOT_LOOP, 0),
                        UNRESOLVED => cur = next,
                        r => break (r, node_depth[next]),
                    },
                }
            };
            while let Some(node) = stack.pop() {
                depth += 1;
                node_root[node] = root;
                node_depth[node] = depth;
                if root < ROOT_LOOP {
                    let dm = &mut depth_max[root as usize];
                    *dm = (*dm).max(depth);
                }
            }
        }
        if node_root.iter().any(|&r| r == ROOT_LOOP) {
            return None;
        }

        // ゲートの入力 → ファンアウト
        let mut mode = vec![Mode::Pin; nd];
        let mut full = vec![0u8; nd];
        // (根のドライバ, 相手 << 2 | スロット, 遅延)
        let mut edges: Vec<(u32, u32, u32)> = Vec::new();
        // 提示されているものを (根, 遅延) に直す。None = 常に 0
        let resolve = |src: Src| -> Option<Option<(u32, u32)>> {
            match src {
                Src::Absent => None,
                Src::Driver(j) => Some(Some((cell_driver[j], 0))),
                Src::Node(nidx) => Some(match node_root[nidx] {
                    ROOT_CONST0 => None,
                    r => Some((r, node_depth[nidx])),
                }),
            }
        };
        for (g, &i) in driver_cell.iter().enumerate() {
            let i = i as usize;
            let c = cells[i];
            let d = (c >> 3) & 3;
            let nb = neighbors(i, pw);
            match c >> 5 {
                K_NAND => {
                    let (mut slots, mut const0, mut any) = (0u32, false, false);
                    for s in 0..4u8 {
                        if s == d { continue; }
                        match resolve(presented_src(cells, nb[s as usize], s ^ 1)) {
                            None => {}
                            Some(None) => { any = true; const0 = true; }
                            Some(Some((r, delay))) => {
                                any = true;
                                edges.push((r, (g as u32) << 2 | slots, delay));
                                slots += 1;
                            }
                        }
                    }
                    full[g] = (1u8 << slots) - 1;
                    mode[g] = if !any { Mode::NandConst(0) } else if const0 { Mode::NandConst(1) } else { Mode::Nand };
                }
                K_DFF => {
                    mode[g] = Mode::Dff;
                    let perp: [u8; 2] = if d == E || d == W { [2, 3] } else { [0, 1] };
                    let sides = [(d ^ 1, 0u32), (perp[0], 1), (perp[1], 2)];
                    for (s, slot) in sides {
                        if let Some(Some((r, delay))) = resolve(presented_src(cells, nb[s as usize], s ^ 1)) {
                            edges.push((r, (g as u32) << 2 | slot, delay));
                        }
                    }
                }
                _ => {}
            }
        }
        // 出力が定数の NAND への入力は、状態を変えないので捨てる
        edges.retain(|&(_, tgt, _)| !matches!(mode[(tgt >> 2) as usize], Mode::NandConst(_)));

        let mut fan_start = vec![0u32; nd + 1];
        for &(r, _, _) in &edges { fan_start[r as usize + 1] += 1; }
        for g in 0..nd { fan_start[g + 1] += fan_start[g]; }
        let mut fill = fan_start.clone();
        let mut fan_tgt = vec![0u32; edges.len()];
        let mut fan_delay = vec![0u32; edges.len()];
        let mut max_delay = 0;
        for &(r, tgt, delay) in &edges {
            let k = fill[r as usize] as usize;
            fill[r as usize] += 1;
            fan_tgt[k] = tgt;
            fan_delay[k] = delay;
            max_delay = max_delay.max(delay);
        }
        let base = cells.iter().map(|&c| c & 0xF8).collect();
        Some(Graph { pw, base, node_root, cell_driver, driver_cell, depth_max, full, mode,
                     fan_start, fan_tgt, fan_delay, max_delay })
    }
}

/// グラフ上の動的な状態。
struct State {
    /// ドライバの状態。bit0 = 出力 (Pin の値 / NAND の出力 / DFF の Q)、bit1 = DFF の前世代クロック
    val: Vec<u8>,
    /// ゲートの入力ビット (スロットごと)
    inbits: Vec<u8>,
    dirty: Vec<bool>,
    dirty_list: Vec<u32>,
    /// タイムホイール: wheel[t & mask] = 時刻 t に入力が反転する (相手 << 2 | スロット)
    wheel: Vec<Vec<u32>>,
    mask: u64,
    scratch: Vec<u32>,
    /// ホイールに残っているイベント数
    pending: u64,
    /// 現在の時刻 (世代、通算)
    now: u64,
    /// 最後にセルが変化する時刻 (通算)。ドライバの遷移時刻 + 配線木の最大深さの最大
    last_change: u64,
    /// 前の settle の後に書かれた Pin (ドライバ番号, 書く前の値)。settle の先頭でまとめて反映する
    /// (同じ Pin を 1 → 0 と書き戻したら変化なし、という cpu / GPU の挙動に合わせる)
    written: Vec<(u32, u8)>,
}

/// ドライバの次の状態 (入力ビットと今の状態から)。
#[inline(always)]
fn eval(mode: Mode, full: u8, inbits: u8, val: u8) -> u8 {
    match mode {
        Mode::Nand => (inbits != full) as u8,
        Mode::Dff => {
            let clk = ((inbits >> 1) | (inbits >> 2)) & 1;
            let prev = (val >> 1) & 1;
            let q = if clk == 1 && prev == 0 { inbits & 1 } else { val & 1 };
            (clk << 1) | q
        }
        Mode::NandConst(v) => v,
        Mode::Pin => val,
    }
}

impl State {
    /// 固定点のグリッドから状態を作る。グリッドがグラフと矛盾していたら None。
    fn from_fixed_point(g: &Graph, cells: &[u8]) -> Option<State> {
        let nd = g.driver_cell.len();
        let val: Vec<u8> = g.driver_cell.iter().zip(&g.mode)
            .map(|(&i, &m)| cells[i as usize] & if m == Mode::Dff { 3 } else { 1 })
            .collect();
        let mut inbits = vec![0u8; nd];
        for r in 0..nd {
            if val[r] & 1 == 0 { continue; }
            for k in g.fan_start[r]..g.fan_start[r + 1] {
                let tgt = g.fan_tgt[k as usize];
                inbits[(tgt >> 2) as usize] |= 1 << (tgt & 3);
            }
        }
        // 固定点なら、配線セルは根の値に等しく、ゲートは入力から計算した値に等しい
        for (node, &root) in g.node_root.iter().enumerate() {
            let c = cells[node / 2];
            let bit = match (c >> 5, node & 1) {
                (K_WIRE, 0) | (K_CROSS, 0) => c & 1,
                (K_CROSS, _) => (c >> 1) & 1,
                _ => continue,
            };
            let want = if root == ROOT_CONST0 { 0 } else { val[root as usize] & 1 };
            if bit != want { return None; }
        }
        for r in 0..nd {
            if eval(g.mode[r], g.full[r], inbits[r], val[r]) != val[r] { return None; }
        }
        let size = (g.max_delay as usize + 2).next_power_of_two();
        Some(State {
            val, inbits, dirty: vec![false; nd], dirty_list: Vec::new(),
            wheel: (0..size).map(|_| Vec::new()).collect(), mask: size as u64 - 1, scratch: Vec::new(),
            pending: 0, now: 0, last_change: 0, written: Vec::new(),
        })
    }

    /// ドライバ r の出力が時刻 t に反転した: ファンアウトに入力の反転を予約する。
    #[inline(always)]
    fn schedule(&mut self, g: &Graph, r: usize, t: u64) {
        let (a, b) = (g.fan_start[r] as usize, g.fan_start[r + 1] as usize);
        for k in a..b {
            let at = t + g.fan_delay[k] as u64;
            self.wheel[(at & self.mask) as usize].push(g.fan_tgt[k]);
        }
        self.pending += (b - a) as u64;
        self.last_change = self.last_change.max(t + g.depth_max[r] as u64);
    }

    /// 時刻 t の入力反転を適用し、t+1 のゲート出力を決める。
    #[inline(always)]
    fn tick(&mut self, g: &Graph, t: u64, stats: &mut Stats) {
        let slot = (t & self.mask) as usize;
        if self.wheel[slot].is_empty() { return; }
        std::mem::swap(&mut self.scratch, &mut self.wheel[slot]);
        self.pending -= self.scratch.len() as u64;
        stats.events += self.scratch.len() as u64;
        for &ev in &self.scratch {
            let gate = (ev >> 2) as usize;
            self.inbits[gate] ^= 1 << (ev & 3);
            if !self.dirty[gate] {
                self.dirty[gate] = true;
                self.dirty_list.push(gate as u32);
            }
        }
        self.scratch.clear();
        let mut list = std::mem::take(&mut self.dirty_list);
        for &gate in &list {
            let gate = gate as usize;
            self.dirty[gate] = false;
            let old = self.val[gate];
            let new = eval(g.mode[gate], g.full[gate], self.inbits[gate], old);
            if new == old { continue; }
            self.val[gate] = new;
            stats.transitions += 1;
            if (old ^ new) & 1 == 1 {
                self.schedule(g, gate, t + 1);
            } else {
                // DFF の前世代クロックだけが変わった (セルは変化するが、誰も読まない)
                self.last_change = self.last_change.max(t + 1);
            }
        }
        list.clear();
        self.dirty_list = list;
    }
}

#[derive(Default)]
struct Stats {
    settles: u64,
    gens: u64,
    events: u64,
    transitions: u64,
    ns: u64,
    bootstrap_settles: u64,
}

enum Phase {
    /// 最初の固定点に達するまで (グラフにできないグリッドではずっと) cpu エンジンで動かす
    Cpu(CpuSim),
    Graph(State),
}

pub struct GraphSim {
    w: usize,
    h: usize,
    graph: Option<Graph>,
    phase: Phase,
    max_steps: u32,
    stats: Stats,
}

impl GraphSim {
    pub fn new(w: u32, h: u32, init: &[u8], max_steps: u32) -> Result<Self> {
        let cpu = CpuSim::new(w, h, init, max_steps)?;
        let graph = Graph::build(cpu.padded_cells(), cpu.padded_width());
        if graph.is_none() {
            eprintln!("graph: ドライバの無い配線の輪があるためグラフにできません。cpu エンジンで実行します");
        }
        Ok(GraphSim { w: w as usize, h: h as usize, graph, phase: Phase::Cpu(cpu), max_steps, stats: Stats::default() })
    }

    /// グラフに切り替わっているか (テスト・表示用)。
    pub fn is_graph(&self) -> bool {
        matches!(self.phase, Phase::Graph(_))
    }

    #[inline(always)]
    fn idx(g: &Graph, at: Xy) -> usize {
        (at.y as usize + 1) * g.pw + at.x as usize + 1
    }

    fn cell_at(g: &Graph, st: &State, i: usize) -> u8 {
        let base = g.base[i];
        let node_val = |node: usize| match g.node_root[node] {
            ROOT_CONST0 => 0,
            r => st.val[r as usize] & 1,
        };
        match base >> 5 {
            K_PIN | K_NAND | K_DFF => base | st.val[g.cell_driver[i] as usize],
            K_WIRE => base | node_val(i * 2),
            K_CROSS => base | (node_val(i * 2 + 1) << 1) | node_val(i * 2),
            _ => base,
        }
    }
}

impl CaDriver for GraphSim {
    fn write_pin(&mut self, at: Xy, level: bool) {
        match &mut self.phase {
            Phase::Cpu(cpu) => cpu.write_pin(at, level),
            Phase::Graph(st) => {
                let g = self.graph.as_ref().expect("graph phase has a graph");
                let i = Self::idx(g, at);
                assert!(g.base[i] >> 5 == K_PIN, "write_pin: ({},{}) is not a Pin cell", at.x, at.y);
                debug_assert_eq!(g.base[i] | level as u8, pin_cell(level));
                let r = g.cell_driver[i] as usize;
                if st.val[r] == level as u8 { return; }
                if !st.written.iter().any(|&(w, _)| w as usize == r) {
                    st.written.push((r as u32, st.val[r]));
                }
                st.val[r] = level as u8;
            }
        }
    }

    fn settle(&mut self) -> Result<Settled> {
        let st = match &mut self.phase {
            Phase::Cpu(cpu) => {
                let settled = cpu.settle()?;
                self.stats.bootstrap_settles += 1;
                if settled.settled {
                    if let Some(g) = &self.graph {
                        match State::from_fixed_point(g, cpu.padded_cells()) {
                            Some(st) => self.phase = Phase::Graph(st),
                            None => {
                                eprintln!("graph: 固定点のグリッドがグラフと一致しません。cpu エンジンで実行します");
                                self.graph = None;
                            }
                        }
                    }
                }
                return Ok(settled);
            }
            Phase::Graph(st) => st,
        };
        let g = self.graph.as_ref().expect("graph phase has a graph");
        let t0 = std::time::Instant::now();
        let start = st.now;
        let mut written = std::mem::take(&mut st.written);
        for (r, old) in written.drain(..) {
            if st.val[r as usize] != old {
                st.schedule(g, r as usize, start);
            }
        }
        st.written = written;
        // 前の settle が未収束なら、配線上に残っている変化の分を持ち越す
        st.last_change = st.last_change.max(start);
        let limit = self.max_steps.saturating_add(1) as u64;
        let end = start + limit;
        let mut t = start;
        while st.pending > 0 && t < end {
            st.tick(g, t, &mut self.stats);
            t += 1;
        }
        // 状態が最後に変わる世代 (start からの相対)。その次の世代が「変化 0」を確かめる 1 世代
        let rel = st.last_change - start;
        let settled = rel < limit;
        let gens = if settled { rel + 1 } else { limit };
        debug_assert!(!settled || st.pending == 0);
        st.now = if settled { t.max(st.last_change + 1) } else { end };
        self.stats.settles += 1;
        self.stats.gens += gens;
        self.stats.ns += t0.elapsed().as_nanos() as u64;
        Ok(Settled { gens: gens as u32, settled })
    }

    fn cell(&self, at: Xy) -> u8 {
        match &self.phase {
            Phase::Cpu(cpu) => cpu.cell(at),
            Phase::Graph(st) => {
                let g = self.graph.as_ref().expect("graph phase has a graph");
                Self::cell_at(g, st, Self::idx(g, at))
            }
        }
    }

    fn snapshot(&self) -> Vec<u8> {
        match &self.phase {
            Phase::Cpu(cpu) => cpu.snapshot(),
            Phase::Graph(st) => {
                let g = self.graph.as_ref().expect("graph phase has a graph");
                let mut out = Vec::with_capacity(self.w * self.h);
                for y in 0..self.h {
                    let row = (y + 1) * g.pw + 1;
                    out.extend((row..row + self.w).map(|i| Self::cell_at(g, st, i)));
                }
                out
            }
        }
    }
}

impl Drop for GraphSim {
    fn drop(&mut self) {
        if std::env::var("WWC_STATS").is_err() { return; }
        let s = &self.stats;
        if let Some(g) = &self.graph {
            eprintln!("[graph-stats] ドライバ {} / 辺 {} / 最大遅延 {} 世代 / cpu での立ち上げ settle {} 回",
                g.driver_cell.len(), g.fan_tgt.len(), g.max_delay, s.bootstrap_settles);
        }
        if s.settles > 0 {
            eprintln!("[graph-stats] settle {} 回 / {} 世代 / イベント {} / ゲート遷移 {} / {:.3}s ({:.1} ns/イベント)",
                s.settles, s.gens, s.events, s.transitions, s.ns as f64 / 1e9, s.ns as f64 / s.events.max(1) as f64);
        }
    }
}

/// cpu と graph を並走させ、settle のたびに世代数・収束・全セルを突き合わせる (`--cross-check`)。
/// graph の検証用: cpu は GPU と byte-exact なので、cpu と一致すれば GPU とも一致する。
pub struct CrossCheck {
    cpu: CpuSim,
    graph: GraphSim,
    width: usize,
    settles: u64,
}

impl CrossCheck {
    pub fn new(w: u32, h: u32, init: &[u8], max_steps: u32) -> Result<Self> {
        Ok(CrossCheck {
            cpu: CpuSim::new(w, h, init, max_steps)?,
            graph: GraphSim::new(w, h, init, max_steps)?,
            width: w as usize,
            settles: 0,
        })
    }
}

impl CaDriver for CrossCheck {
    fn write_pin(&mut self, at: Xy, level: bool) {
        self.cpu.write_pin(at, level);
        self.graph.write_pin(at, level);
    }

    fn settle(&mut self) -> Result<Settled> {
        let a = self.cpu.settle()?;
        let b = self.graph.settle()?;
        let n = self.settles;
        self.settles += 1;
        anyhow::ensure!(a.gens == b.gens && a.settled == b.settled,
            "cross-check: settle #{n}: cpu = {}g (settled={}) / graph = {}g (settled={})",
            a.gens, a.settled, b.gens, b.settled);
        // 未収束で打ち切った状態は graph では近似なので比べない
        if a.settled {
            let (ca, cb) = (self.cpu.snapshot(), self.graph.snapshot());
            if let Some(i) = ca.iter().zip(&cb).position(|(x, y)| x != y) {
                let diff = ca.iter().zip(&cb).filter(|(x, y)| x != y).count();
                anyhow::bail!("cross-check: settle #{n}: {diff} cells differ. first at ({},{}): cpu = {:#04x} / graph = {:#04x}",
                    i % self.width, i / self.width, ca[i], cb[i]);
            }
        }
        Ok(b)
    }

    fn cell(&self, at: Xy) -> u8 {
        self.graph.cell(at)
    }

    fn snapshot(&self) -> Vec<u8> {
        self.graph.snapshot()
    }
}

impl Drop for CrossCheck {
    fn drop(&mut self) {
        eprintln!("cross-check: settle {} 回を cpu と graph で突き合わせました (graph に切替済み: {})",
            self.settles, self.graph.is_graph());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cpu::testing::*;

    /// cpu と graph に同じ書込列を与え、settle ごとに世代数と全セルが一致することを確かめる。
    fn check(rows: &[&str], max_steps: u32, writes: &[&[(u32, u32, bool)]]) -> CrossCheck {
        let (w, h, cells) = grid(rows);
        let mut d = CrossCheck::new(w, h, &cells, max_steps).unwrap();
        d.settle().unwrap();
        for batch in writes {
            for &(x, y, level) in *batch {
                d.write_pin(Xy { x, y }, level);
            }
            d.settle().unwrap();
        }
        d
    }

    #[test]
    fn wire_chain_reports_the_same_generation_count() {
        let (w, h, cells) = grid(&["P>>>>"]);
        let mut g = GraphSim::new(w, h, &cells, 100).unwrap();
        assert_eq!(g.settle().unwrap().gens, 1);
        assert!(g.is_graph());
        g.write_pin(Xy { x: 0, y: 0 }, true);
        let s = g.settle().unwrap();
        assert_eq!((s.gens, s.settled), (5, true));
        assert_eq!(g.cell(Xy { x: 4, y: 0 }), wire(E) | 1);
        // 何も書かない settle は 1 世代
        assert_eq!(g.settle().unwrap().gens, 1);
    }

    #[test]
    fn nand_dff_and_cross_match_the_cell_engine() {
        // 行 0: D 側 (Pin → NAND → DFF)。行 2 の Pin がクロック (Cross で行 1 の配線をまたいで DFF の南へ)
        let rows = [
            "P>N>>D>>N>",
            "P>>>>x>>^.",
            "P>>>>^....",
        ];
        let d = check(&rows, 100, &[
            &[(0, 2, true)],
            &[(0, 0, true), (0, 1, true)],
            &[(0, 2, false)],
            &[(0, 2, true)],
            &[(0, 0, false), (0, 2, false)],
            &[(0, 2, true), (0, 1, false)],
        ]);
        assert!(d.graph.is_graph());
    }

    #[test]
    fn glitches_travel_down_the_wires_intact() {
        // NAND の 2 入力に同じ Pin が長さの違う配線で届く → 入力が食い違う間だけ出力が動く
        let rows = [
            "P>>>>>>>>...",
            "v.......N>>>",
            "v>>>>>>>>...",
        ];
        check(&rows, 100, &[&[(0, 0, true)], &[(0, 0, false)], &[(0, 0, true)]]);
    }

    #[test]
    fn writing_a_pin_back_before_settling_is_not_a_change() {
        let d = check(&["P>>>>N>>"], 100, &[&[(0, 0, true), (0, 0, false)], &[(0, 0, true), (0, 0, true)]]);
        assert!(d.graph.is_graph());
    }

    #[test]
    fn unsettled_settles_carry_over() {
        let rows = ["P>>>>>>>>N>>>>>>"];
        let (w, h, cells) = grid(&rows);
        let mut d = CrossCheck::new(w, h, &cells, 4).unwrap();
        // 初期グリッドは固定点でない (NAND の出力 1 が配線に出ていく)。graph はまだ cpu で動いている
        while !d.settle().unwrap().settled {}
        assert!(d.graph.is_graph());
        d.write_pin(Xy { x: 0, y: 0 }, true);
        let mut rounds = 0;
        loop {
            rounds += 1;
            let s = d.settle().unwrap();
            if s.settled { break; }
            assert_eq!(s.gens, 5);
        }
        assert!(rounds > 2);
        assert_eq!(d.graph.cell(Xy { x: 15, y: 0 }) & 1, 0);
    }

    #[test]
    fn wire_loops_fall_back_to_the_cell_engine() {
        let rows = [
            "P>>.",
            ".^>.",
            ".<v.",
        ];
        let d = check(&rows, 100, &[&[(0, 0, true)]]);
        assert!(!d.graph.is_graph());
    }

    #[test]
    fn dangling_and_constant_inputs() {
        // 配線の先が空 (常に 0) の入力を持つ NAND、入力の無い NAND、D の無い DFF
        let rows = [
            ".>>N>>D>",
            "....P>>.",
            "N>......",
        ];
        check(&rows, 100, &[&[(4, 1, true)], &[(4, 1, false)], &[(4, 1, true)]]);
    }
}
