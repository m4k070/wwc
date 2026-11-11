// セル単位のイベント駆動 CPU エンジン (`--engine cpu`)。同期更新で、GPU (wirelevel.wgsl) と byte-exact。
//
// 「前世代に変化したセルを実際に読んでいるセル」だけを再評価する。GPU tiled エンジンのタイル粒度を
// 1 セルにしたもので、固定点までの世代数も全セルも GPU と一致する。sm83_full では 1 世代に変化するのは
// 約 73 セル (GPU tiled が計算するのは約 31,787 セル) なので、単スレッドでも GPU と同等以上に速い。
// GPU を使わないので、Vulkan の無い環境でも CA 照合が回る。
//
// 誰が誰を読むか (reader) はセルの kind / dir だけで決まり実行中に変わらないので、起動時に 1 セル
// 1 バイトの表にしておく。Pin の書込は kind を変えない (write_pin は Pin セル以外を拒否する)。
use anyhow::Result;

use crate::clocking::{pin_cell, CaDriver, Settled};
use crate::routed_meta::Xy;

/// 近傍が何も提示していない (Empty / 向きの合わない Cross)
pub const NONE: u8 = 2;
pub const E: u8 = 0;
pub const W: u8 = 1;
pub const N: u8 = 2;
pub const S: u8 = 3;

pub const K_PIN: u8 = 1;
pub const K_WIRE: u8 = 2;
pub const K_NAND: u8 = 3;
pub const K_CROSS: u8 = 4;
pub const K_DFF: u8 = 5;

/// Cross の水平チャネルの向き (E / W)
#[inline(always)]
pub fn cross_hd(c: u8) -> u8 {
    if (c >> 4) & 1 == 1 { W } else { E }
}

/// Cross の垂直チャネルの向き (N / S)
#[inline(always)]
pub fn cross_vd(c: u8) -> u8 {
    if (c >> 3) & 1 == 1 { S } else { N }
}

/// セル c が `toward` 側の隣に提示する値 (0 / 1 / NONE)。
#[inline(always)]
fn presented(c: u8, toward: u8) -> u8 {
    match c >> 5 {
        K_PIN | K_WIRE | K_NAND | K_DFF => c & 1,
        K_CROSS => {
            if toward == cross_hd(c) { c & 1 } else if toward == cross_vd(c) { (c >> 1) & 1 } else { NONE }
        }
        _ => NONE,
    }
}

/// 周囲 1 セルを Empty で囲んだ配列の、4 近傍の添字 (E, W, N, S の順)。
#[inline(always)]
pub fn neighbors(i: usize, pw: usize) -> [usize; 4] {
    [i + 1, i - 1, i - pw, i + pw]
}

/// WireLevel の遷移規則 (WireLevel.fs の step / wirelevel.wgsl と同じ)。nb は E, W, N, S の近傍セル。
#[inline(always)]
pub fn rule(c: u8, nb: [u8; 4]) -> u8 {
    let pull = |s: u8| presented(nb[s as usize], s ^ 1);
    let d = (c >> 3) & 3;
    match c >> 5 {
        K_WIRE => (c & 0xF8) | (pull(d ^ 1) == 1) as u8,
        K_NAND => {
            let (mut all, mut any) = (true, false);
            for s in 0..4u8 {
                if s != d {
                    let v = pull(s);
                    if v != NONE {
                        any = true;
                        if v == 0 { all = false; }
                    }
                }
            }
            (c & 0xF8) | (any && !all) as u8
        }
        K_CROSS => {
            let hv = pull(cross_hd(c) ^ 1);
            let vv = pull(cross_vd(c) ^ 1);
            (c & 0xF8) | (((vv == 1) as u8) << 1) | (hv == 1) as u8
        }
        K_DFF => {
            let dval = (pull(d ^ 1) == 1) as u8;
            let perp: [u8; 2] = if d == E || d == W { [N, S] } else { [E, W] };
            let clk = pull(perp[0]) == 1 || pull(perp[1]) == 1;
            let prev = (c >> 1) & 1;
            let q = if clk && prev == 0 { dval } else { c & 1 };
            (c & 0xF8) | ((clk as u8) << 1) | q
        }
        _ => c,
    }
}

/// 表 readers のビット: 0..3 = その側 (E, W, N, S) の隣が自分を読む、4 = 自分自身 (DFF)
const READ_SELF: u8 = 1 << 4;

pub struct CpuSim {
    w: usize,
    h: usize,
    /// 周囲 1 セルを Empty で囲んだ幅 (= w + 2)。境界検査を省くため
    pw: usize,
    cells: Vec<u8>,
    readers: Vec<u8>,
    /// mark[i] == stamp なら i は次世代の候補に積み済み
    mark: Vec<u32>,
    stamp: u32,
    cand: Vec<u32>,
    next: Vec<u32>,
    changes: Vec<(u32, u8)>,
    max_steps: u32,
    stats: Stats,
}

#[derive(Default)]
struct Stats {
    settles: u64,
    gens: u64,
    evals: u64,
    changes: u64,
    ns: u64,
    by_kind: [u64; 8],
}

impl CpuSim {
    pub fn new(w: u32, h: u32, init: &[u8], max_steps: u32) -> Result<Self> {
        let (w, h) = (w as usize, h as usize);
        anyhow::ensure!(init.len() == w * h, "cell count mismatch: {} cells for {w}x{h}", init.len());
        let pw = w + 2;
        let mut cells = vec![0u8; pw * (h + 2)];
        for y in 0..h {
            cells[(y + 1) * pw + 1..(y + 1) * pw + 1 + w].copy_from_slice(&init[y * w..(y + 1) * w]);
        }
        let mut readers = vec![0u8; cells.len()];
        let mut cand = Vec::new();
        let mut mark = vec![0u32; cells.len()];
        for y in 1..=h {
            for x in 1..=w {
                let i = y * pw + x;
                let c = cells[i];
                if c >> 5 == K_DFF { readers[i] |= READ_SELF; }
                for (s, &j) in neighbors(i, pw).iter().enumerate() {
                    // 隣 j (自分の s 側) から見て自分は s^1 側にいる
                    if reads_side(cells[j], s as u8 ^ 1) { readers[i] |= 1 << s; }
                }
                // 最初の世代は全セルを評価する (初期グリッドが固定点とは限らない)
                if c >> 5 >= K_WIRE {
                    cand.push(i as u32);
                    mark[i] = 1;
                }
            }
        }
        Ok(CpuSim { w, h, pw, cells, readers, mark, stamp: 1, cand, next: Vec::new(), changes: Vec::new(),
                    max_steps, stats: Stats::default() })
    }

    #[inline(always)]
    fn idx(&self, at: Xy) -> usize {
        (at.y as usize + 1) * self.pw + at.x as usize + 1
    }

    /// セル i が変化したとき、i を読んでいるセルを list に積む。
    #[inline(always)]
    fn push_readers(list: &mut Vec<u32>, mark: &mut [u32], readers: &[u8], stamp: u32, i: usize, pw: usize) {
        let r = readers[i];
        let nbs = [i + 1, i - 1, i - pw, i + pw, i];
        for (s, &j) in nbs.iter().enumerate() {
            if (r >> s) & 1 == 1 && mark[j] != stamp {
                mark[j] = stamp;
                list.push(j as u32);
            }
        }
    }

    /// 1 世代進める。変化したセルがあれば true。
    fn step(&mut self) -> bool {
        let pw = self.pw;
        self.changes.clear();
        self.stats.evals += self.cand.len() as u64;
        for &i in &self.cand {
            let i = i as usize;
            let c = self.cells[i];
            let n = rule(c, [self.cells[i + 1], self.cells[i - 1], self.cells[i - pw], self.cells[i + pw]]);
            if n != c {
                self.changes.push((i as u32, n));
                self.stats.by_kind[(c >> 5) as usize] += 1;
            }
        }
        self.cand.clear();
        // 候補を使い切ったので印を無効にする (変化が無くても進める: 次に write_pin が積む候補が、
        // いま評価し終えたセルの印に妨げられないように)
        self.stamp = self.stamp.wrapping_add(1);
        if self.stamp == 0 {
            // 約 43 億世代で 1 周する。古い印と衝突しないよう消す
            self.mark.fill(0);
            self.stamp = 1;
        }
        if self.changes.is_empty() {
            return false;
        }
        self.stats.changes += self.changes.len() as u64;
        for &(i, n) in &self.changes {
            self.cells[i as usize] = n;
        }
        for &(i, _) in &self.changes {
            Self::push_readers(&mut self.next, &mut self.mark, &self.readers, self.stamp, i as usize, pw);
        }
        std::mem::swap(&mut self.cand, &mut self.next);
        true
    }

    /// steps 世代ちょうど進める (単発モード用)。
    pub fn run(&mut self, steps: u32) {
        for _ in 0..steps {
            if !self.step() { break; } // 固定点。以降は何世代進めても変わらない
        }
    }

    /// 周囲を囲んだままの内部配列 (GraphSim が固定点から状態を取り出すのに使う)。
    pub fn padded_cells(&self) -> &[u8] {
        &self.cells
    }

    pub fn padded_width(&self) -> usize {
        self.pw
    }

    pub fn cells(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(self.w * self.h);
        for y in 0..self.h {
            out.extend_from_slice(&self.cells[(y + 1) * self.pw + 1..(y + 1) * self.pw + 1 + self.w]);
        }
        out
    }
}

/// セル c は `side` 側の隣を読むか。
#[inline(always)]
pub fn reads_side(c: u8, side: u8) -> bool {
    let d = (c >> 3) & 3;
    match c >> 5 {
        K_WIRE => side == d ^ 1,
        K_NAND | K_DFF => side != d,
        K_CROSS => side == cross_hd(c) ^ 1 || side == cross_vd(c) ^ 1,
        _ => false,
    }
}

impl CaDriver for CpuSim {
    fn write_pin(&mut self, at: Xy, level: bool) {
        let i = self.idx(at);
        assert!(self.cells[i] >> 5 == K_PIN, "write_pin: ({},{}) is not a Pin cell", at.x, at.y);
        let v = pin_cell(level);
        if self.cells[i] == v { return; }
        self.cells[i] = v;
        Self::push_readers(&mut self.cand, &mut self.mark, &self.readers, self.stamp, i, self.pw);
    }

    /// GpuSim::run_until_settled と同じ数え方: 変化 0 の世代 (検証の 1 世代) を含めた実行世代数を返す。
    fn settle(&mut self) -> Result<Settled> {
        let t0 = std::time::Instant::now();
        let limit = self.max_steps.saturating_add(1);
        let mut gens = 0u32;
        let mut settled = false;
        while gens < limit {
            gens += 1;
            if !self.step() {
                settled = true;
                break;
            }
        }
        self.stats.settles += 1;
        self.stats.gens += gens as u64;
        self.stats.ns += t0.elapsed().as_nanos() as u64;
        Ok(Settled { gens, settled })
    }

    fn cell(&self, at: Xy) -> u8 {
        self.cells[self.idx(at)]
    }

    fn snapshot(&self) -> Vec<u8> {
        self.cells()
    }
}

impl Drop for CpuSim {
    fn drop(&mut self) {
        if std::env::var("WWC_STATS").is_err() || self.stats.settles == 0 { return; }
        let s = &self.stats;
        let g = s.gens.max(1) as f64;
        eprintln!("[cpu-stats] settle {} 回 / {} 世代 / 評価 {} ({:.1}/世代) / 変化 {} ({:.1}/世代) / {:.2}s ({:.0} ns/世代, {:.1} ns/評価)",
            s.settles, s.gens, s.evals, s.evals as f64 / g, s.changes, s.changes as f64 / g,
            s.ns as f64 / 1e9, s.ns as f64 / g, s.ns as f64 / s.evals.max(1) as f64);
        eprintln!("[cpu-stats] 変化の内訳 wire={} nand={} cross={} dff={}",
            s.by_kind[K_WIRE as usize], s.by_kind[K_NAND as usize], s.by_kind[K_CROSS as usize], s.by_kind[K_DFF as usize]);
    }
}

#[cfg(test)]
pub mod testing {
    use super::*;

    pub const PIN0: u8 = K_PIN << 5;
    pub fn wire(d: u8) -> u8 { (K_WIRE << 5) | (d << 3) }
    pub fn nand(d: u8) -> u8 { (K_NAND << 5) | (d << 3) }
    pub fn dff(d: u8) -> u8 { (K_DFF << 5) | (d << 3) }
    /// h: E / W、v: N / S
    pub fn cross(h: u8, v: u8) -> u8 { (K_CROSS << 5) | (((h == W) as u8) << 4) | (((v == S) as u8) << 3) }

    /// 文字で描いたグリッドを作る。'.'=Empty 'P'=Pin '>' '<' '^' 'v'=Wire、'N'=NAND(E)、'D'=DFF(E)、
    /// '+'=Cross(E,S)、'x'=Cross(E,N)
    pub fn grid(rows: &[&str]) -> (u32, u32, Vec<u8>) {
        let w = rows[0].len();
        let mut cells = Vec::new();
        for r in rows {
            assert_eq!(r.len(), w);
            for ch in r.chars() {
                cells.push(match ch {
                    '.' => 0,
                    'P' => PIN0,
                    '>' => wire(E),
                    '<' => wire(W),
                    '^' => wire(N),
                    'v' => wire(S),
                    'N' => nand(E),
                    'D' => dff(E),
                    '+' => cross(E, S),
                    'x' => cross(E, N),
                    _ => panic!("unknown cell {ch}"),
                });
            }
        }
        (w as u32, rows.len() as u32, cells)
    }
}

#[cfg(test)]
mod tests {
    use super::testing::*;
    use super::*;

    #[test]
    fn wire_chain_delays_one_generation_per_cell() {
        let (w, h, cells) = grid(&["P>>>>"]);
        let mut sim = CpuSim::new(w, h, &cells, 100).unwrap();
        assert_eq!(sim.settle().unwrap().gens, 1); // 初期状態が固定点
        sim.write_pin(Xy { x: 0, y: 0 }, true);
        let s = sim.settle().unwrap();
        // 4 セル伝搬 + 変化 0 を確かめる 1 世代
        assert_eq!((s.gens, s.settled), (5, true));
        assert_eq!(sim.cell(Xy { x: 4, y: 0 }) & 1, 1);
    }

    #[test]
    fn unsettled_when_the_limit_is_too_small() {
        let (w, h, cells) = grid(&["P>>>>"]);
        let mut sim = CpuSim::new(w, h, &cells, 3).unwrap();
        sim.settle().unwrap();
        sim.write_pin(Xy { x: 0, y: 0 }, true);
        let s = sim.settle().unwrap();
        assert_eq!((s.gens, s.settled), (4, false));
        // 状態 g_4 が固定点なのは 5 世代目で分かる。max_steps=4 ならちょうど収束
        let mut sim = CpuSim::new(w, h, &cells, 4).unwrap();
        sim.settle().unwrap();
        sim.write_pin(Xy { x: 0, y: 0 }, true);
        let s = sim.settle().unwrap();
        assert_eq!((s.gens, s.settled), (5, true));
    }

    #[test]
    fn nand_and_dff_follow_the_rule() {
        // 上の Pin = D (NAND で反転)、下の Pin = clk (DFF の南から入る)
        let (w, h, cells) = grid(&[
            "P>N>D>",
            "...P>.",
        ]);
        let mut sim = CpuSim::new(w, h, &cells, 100).unwrap();
        sim.settle().unwrap();
        let q = Xy { x: 4, y: 0 };
        assert_eq!(sim.cell(q) & 1, 0); // NAND(0)=1 だがクロックがまだ
        sim.write_pin(Xy { x: 3, y: 1 }, true);
        sim.settle().unwrap();
        assert_eq!(sim.cell(q) & 3, 0b11); // 立ち上がりで 1 を取り込む
        sim.write_pin(Xy { x: 0, y: 0 }, true);
        sim.settle().unwrap();
        assert_eq!(sim.cell(q) & 1, 1); // クロックが高のままでは取り込まない
        sim.write_pin(Xy { x: 3, y: 1 }, false);
        sim.settle().unwrap();
        sim.write_pin(Xy { x: 3, y: 1 }, true);
        sim.settle().unwrap();
        assert_eq!(sim.cell(q) & 1, 0);
    }

    #[test]
    fn run_matches_settle_at_the_fixed_point() {
        let (w, h, cells) = grid(&["P>N>D>", "...P>."]);
        let mut a = CpuSim::new(w, h, &cells, 100).unwrap();
        let mut b = CpuSim::new(w, h, &cells, 100).unwrap();
        a.write_pin(Xy { x: 3, y: 1 }, true);
        b.write_pin(Xy { x: 3, y: 1 }, true);
        a.settle().unwrap();
        b.run(50);
        assert_eq!(a.snapshot(), b.snapshot());
    }
}
