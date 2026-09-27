// クロック駆動 — meta の clocking に従って 1 周期の「ピン書込 → 収束」の順序を組み立てる
// (DESIGN-VERIFY.md §5.1 / §5.2 / §5.2.1)。
//
// 周期のどこでクロックをどう動かすかはここだけが知っている。メモリ操作 (memory_program.rs) は
//   settle_idle (クロックを休止状態にして収束) → バス観測・メモリ操作・data_in 書込 → settle_idle → latch
// の順に呼ぶだけで、単相 / 2 相の違いを意識しない。
//
//   単相 (singleEdge):  idle = clk=0 で収束       latch = clk=1 で収束 ("high")
//   2 相 (twoPhase):    idle = clkA=0,clkB=0 で収束 latch = clkA=1 で収束 ("phaseA")
//                                                       → clkA=0,clkB=1 を同時に書いて収束 ("phaseB")
// 2 相の latch は必ず 2 回の収束に分ける (1 回にまとめると clkA と clkB が重なり hold の保護が消える)。
//
// GPU を直接呼ばず CaDriver trait を通すので、書込と収束の順序を GPU なしで単体テストできる。
use anyhow::{Context, Result};

use crate::gpu::GpuSim;
use crate::routed_meta::{Clocking, RoutedMeta, Xy};

/// Pin セルのエンコーディング: kind=Pin (1) << 5 | level。dir は常に 0
const PIN_CELL_BASE: u8 = 0x20;

/// Pin セルに書くバイト。
pub fn pin_cell(level: bool) -> u8 {
    PIN_CELL_BASE | level as u8
}

/// 1 回の収束待ちの結果。
pub struct Settled {
    /// 収束後 (または打ち切り時) のセル配列
    pub cells: Vec<u8>,
    /// 実行した世代数
    pub gens: u32,
    /// maxStepsPerPhase 以内に固定点に達したか
    pub settled: bool,
}

/// CA を駆動する最小の操作。GpuSim の他、テストでは書込と収束の記録器が実装する。
pub trait CaDriver {
    /// Pin セルに level を書く。次の settle から反映される (同じ settle の前に書いたものは同時に効く)
    fn write_pin(&mut self, at: Xy, level: bool);
    /// 固定点まで進める
    fn settle(&mut self) -> Result<Settled>;
}

/// バス (LSB first) に整数値を書く。
pub fn write_bus<D: CaDriver>(driver: &mut D, coords: &[Xy], value: u64) {
    for (i, c) in coords.iter().enumerate() {
        driver.write_pin(*c, (value >> i) & 1 == 1);
    }
}

/// GpuSim を収束パラメータ付きで CaDriver にする。
pub struct GpuDriver<'a> {
    pub sim: &'a mut GpuSim,
    pub max_steps_per_phase: u32,
    pub check_interval: u32,
}

impl CaDriver for GpuDriver<'_> {
    fn write_pin(&mut self, at: Xy, level: bool) {
        self.sim.write_cell(at.x, at.y, pin_cell(level));
    }

    fn settle(&mut self) -> Result<Settled> {
        let (cells, gens, settled) = self.sim.run_until_settled(self.max_steps_per_phase, self.check_interval)?;
        Ok(Settled { cells, gens, settled })
    }
}

/// 周期内の 1 段の収束。label は settle 表示と UNSETTLED の報告に使う。
pub struct Phase {
    pub label: &'static str,
    pub settled: Settled,
    pub elapsed_secs: f32,
}

impl Phase {
    /// "high=123g" の形 (settle の世代数表示)
    pub fn gens_str(&self) -> String {
        format!("{}={}g", self.label, self.settled.gens)
    }

    /// "high=123g(settled=true)" の形 (UNSETTLED の報告)
    pub fn status_str(&self) -> String {
        format!("{}={}g(settled={})", self.label, self.settled.gens, self.settled.settled)
    }
}

/// 周期の段の表示ラベル。golden 不一致・UNSETTLED の報告と trace に出る。
pub const LABEL_SETUP: &str = "setup";
pub const LABEL_DATA_IN: &str = "data_in";
pub const LABEL_HIGH: &str = "high";
pub const LABEL_PHASE_A: &str = "phaseA";
pub const LABEL_PHASE_B: &str = "phaseB";

/// meta の clocking から決まる、実際に書くクロックピン。
#[derive(Clone, Debug, PartialEq)]
pub enum ClockPins {
    /// 単相: inputs[clockPort] (1 ビット)
    SingleEdge { clk: Xy },
    /// 2 相: マスター (clkA) / スレーブ (clkB)
    TwoPhase { clk_a: Xy, clk_b: Xy },
}

/// 単相の meta でクロックとして駆動する入力ポート名 (RTL の慣習。2 相では meta の clockPort が持つ)
pub const SINGLE_EDGE_CLOCK_PORT: &str = "clk";

impl ClockPins {
    pub fn from_meta(meta: &RoutedMeta) -> Result<Self> {
        match &meta.clocking {
            Clocking::SingleEdge => {
                let clk = meta.inputs.get(SINGLE_EDGE_CLOCK_PORT).with_context(||
                    format!("singleEdge meta must have input '{SINGLE_EDGE_CLOCK_PORT}'"))?;
                anyhow::ensure!(clk.len() == 1,
                    "clock input '{SINGLE_EDGE_CLOCK_PORT}' must be 1 bit (got {})", clk.len());
                Ok(ClockPins::SingleEdge { clk: clk[0] })
            }
            Clocking::TwoPhase { clk_a, clk_b, .. } => Ok(ClockPins::TwoPhase { clk_a: *clk_a, clk_b: *clk_b }),
        }
    }

    pub fn scheme_name(&self) -> &'static str {
        match self {
            ClockPins::SingleEdge { .. } => "singleEdge",
            ClockPins::TwoPhase { .. } => "twoPhase",
        }
    }

    /// クロックを休止状態 (単相 clk=0 / 2 相 clkA=0,clkB=0) にして収束させる。
    /// 周期の手順 1 と、data_in を書いた後の手順 4 の両方で使う。
    pub fn settle_idle<D: CaDriver>(&self, driver: &mut D, label: &'static str) -> Result<Phase> {
        match self {
            ClockPins::SingleEdge { clk } => driver.write_pin(*clk, false),
            ClockPins::TwoPhase { clk_a, clk_b } => {
                driver.write_pin(*clk_a, false);
                driver.write_pin(*clk_b, false);
            }
        }
        timed(driver, label)
    }

    /// DFF にラッチさせる (単相の手順 5 / 2 相の手順 5〜6)。最後の段の cells が周期の出力。
    pub fn latch<D: CaDriver>(&self, driver: &mut D) -> Result<Vec<Phase>> {
        match self {
            ClockPins::SingleEdge { clk } => {
                driver.write_pin(*clk, true);
                Ok(vec![timed(driver, LABEL_HIGH)?])
            }
            ClockPins::TwoPhase { clk_a, clk_b } => {
                // 手順 5: マスターが D を取り込む。スレーブは clkB=0 のまま
                driver.write_pin(*clk_a, true);
                let phase_a = timed(driver, LABEL_PHASE_A)?;
                // 手順 6: clkA を下ろすのと clkB を上げるのを同じ収束の前に書く
                // (マスターは立ち下がりを見ないので動かず、スレーブがマスター Q を取り込む)
                driver.write_pin(*clk_a, false);
                driver.write_pin(*clk_b, true);
                let phase_b = timed(driver, LABEL_PHASE_B)?;
                Ok(vec![phase_a, phase_b])
            }
        }
    }
}

fn timed<D: CaDriver>(driver: &mut D, label: &'static str) -> Result<Phase> {
    let start = std::time::Instant::now();
    let settled = driver.settle()?;
    Ok(Phase { label, settled, elapsed_secs: start.elapsed().as_secs_f32() })
}

#[cfg(test)]
pub mod testing {
    use super::*;

    /// 書込と収束の順序を記録する CaDriver。settle は現在のセル配列 (書いたピンが反映済み) を返す。
    #[derive(Debug, PartialEq, Clone)]
    pub enum Event {
        Write(Xy, bool),
        Settle,
    }

    pub struct RecordingDriver {
        pub width: u32,
        pub cells: Vec<u8>,
        pub events: Vec<Event>,
    }

    impl RecordingDriver {
        pub fn new(width: u32, cells: Vec<u8>) -> Self {
            Self { width, cells, events: Vec::new() }
        }
    }

    impl CaDriver for RecordingDriver {
        fn write_pin(&mut self, at: Xy, level: bool) {
            self.cells[(at.y * self.width + at.x) as usize] = pin_cell(level);
            self.events.push(Event::Write(at, level));
        }

        fn settle(&mut self) -> Result<Settled> {
            self.events.push(Event::Settle);
            Ok(Settled { cells: self.cells.clone(), gens: 7, settled: true })
        }
    }
}

#[cfg(test)]
mod tests {
    use super::testing::{Event::*, RecordingDriver};
    use super::*;

    const CLK: Xy = Xy { x: 0, y: 0 };
    const CLK_A: Xy = Xy { x: 1, y: 0 };
    const CLK_B: Xy = Xy { x: 2, y: 0 };

    fn driver() -> RecordingDriver {
        RecordingDriver::new(3, vec![pin_cell(false); 3])
    }

    #[test]
    fn single_edge_idle_and_latch() {
        let clock = ClockPins::SingleEdge { clk: CLK };
        let mut d = driver();
        let idle = clock.settle_idle(&mut d, LABEL_SETUP).unwrap();
        let latch = clock.latch(&mut d).unwrap();
        assert_eq!(d.events, vec![Write(CLK, false), Settle, Write(CLK, true), Settle]);
        assert_eq!(idle.gens_str(), "setup=7g");
        let labels: Vec<&str> = latch.iter().map(|p| p.label).collect();
        assert_eq!(labels, [LABEL_HIGH]);
    }

    #[test]
    fn two_phase_latch_settles_between_phases_and_swaps_clocks_together() {
        let clock = ClockPins::TwoPhase { clk_a: CLK_A, clk_b: CLK_B };
        let mut d = driver();
        clock.settle_idle(&mut d, LABEL_SETUP).unwrap();
        let latch = clock.latch(&mut d).unwrap();
        assert_eq!(d.events, vec![
            Write(CLK_A, false), Write(CLK_B, false), Settle,   // 手順 1
            Write(CLK_A, true), Settle,                          // 手順 5
            Write(CLK_A, false), Write(CLK_B, true), Settle,     // 手順 6 (同時に書いてから 1 回収束)
        ]);
        let labels: Vec<&str> = latch.iter().map(|p| p.label).collect();
        assert_eq!(labels, [LABEL_PHASE_A, LABEL_PHASE_B]);
        // 最後の段の出力は clkA=0, clkB=1
        assert_eq!(latch[1].settled.cells, vec![pin_cell(false), pin_cell(false), pin_cell(true)]);
    }

    #[test]
    fn write_bus_is_lsb_first() {
        let mut d = driver();
        write_bus(&mut d, &[CLK, CLK_A, CLK_B], 0b101);
        assert_eq!(d.events, vec![Write(CLK, true), Write(CLK_A, false), Write(CLK_B, true)]);
    }
}
