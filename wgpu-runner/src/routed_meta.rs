// routed meta — src/RoutedArtifact.fs が書く <circuit>.meta.json の読み込み (DESIGN-VERIFY.md §6.3)。
//
// formatVersion:
//   1 — clocking なし。単相 (singleEdge) として読む (既存の routed/sm83_subset.meta.json 等)
//   2 — clocking を必ず持つ:
//         {"scheme": "singleEdge"}
//         {"scheme": "twoPhase", "clockPort": "clk", "clkA": {x,y}, "clkB": {x,y}}
//   それ以外はエラー。
//
// JSON の形 (RawRoutedMeta) と、検査済みの形 (RoutedMeta) を分ける。バージョンと clocking の
// 組み合わせの不整合 (v1 に clocking がある、v2 に無い、twoPhase なのに inputs に clockPort が残っている等) は
// RoutedMeta::from_json で弾くので、以降のコードは Clocking を見るだけで駆動手順を選べる。
use std::collections::BTreeMap;
use anyhow::{Context, Result};
use serde::Deserialize;

#[derive(Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
pub struct Xy { pub x: u32, pub y: u32 }

/// RoutedArtifact.fs の OutputProbe に対応する (meta JSON の表現で区別する)。
#[derive(Deserialize, Clone, Debug, PartialEq)]
#[serde(untagged)]
pub enum OutputProbe {
    /// セルのレベル bit0 を読む。meta: {"x":..,"y":..}
    Cell { x: u32, y: u32 },
    /// yosys が定数に畳んだビット。meta: {"const":0|1}
    Const {
        #[serde(rename = "const")]
        value: u8,
    },
    /// 駆動元が grid 上に無い。meta: {"unobservable":<netId>}
    Unobservable { unobservable: i64 },
}

/// clocking を持たない旧形式 (RoutedArtifact.fs の LegacyFormatVersion)。単相として読む。
pub const META_FORMAT_VERSION_LEGACY: u32 = 1;
/// clocking を持つ現行形式 (RoutedArtifact.fs の CurrentFormatVersion)。
pub const META_FORMAT_VERSION_CURRENT: u32 = 2;

/// クロック方式 (RoutedArtifact.fs の ClockingMeta)。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Clocking {
    /// 従来の単相。inputs の clockPort (慣習的に "clk") を §5.2 の手順で駆動する
    SingleEdge,
    /// 2 相ノンオーバーラップ。元のクロックポート (clock_port) は grid 上に無く、
    /// clk_a (マスター) / clk_b (スレーブ) のピンを §5.2.1 の手順で駆動する
    TwoPhase { clock_port: String, clk_a: Xy, clk_b: Xy },
}

/// meta JSON の clocking の表現そのまま。未知の scheme は serde がエラーにする。
#[derive(Deserialize, Debug)]
#[serde(tag = "scheme", deny_unknown_fields)]
enum RawClocking {
    /// 空の struct variant にするのは deny_unknown_fields を効かせるため (unit variant では余分なキーが通る)
    #[serde(rename = "singleEdge")]
    SingleEdge {},
    #[serde(rename = "twoPhase", rename_all = "camelCase")]
    TwoPhase { clock_port: String, clk_a: Xy, clk_b: Xy },
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawRoutedMeta {
    format_version: u32,
    circuit: String,
    width: u32,
    height: u32,
    #[serde(default)]
    source_sha256: Option<String>,
    #[serde(default)]
    gate_count: u32,
    #[serde(default)]
    dff_count: u32,
    inputs: BTreeMap<String, Vec<Xy>>,
    outputs: BTreeMap<String, Vec<OutputProbe>>,
    #[serde(default)]
    clocking: Option<RawClocking>,
}

/// 検査済みの routed meta。clocking はバージョン差を吸収した後の値。
#[derive(Debug)]
pub struct RoutedMeta {
    pub format_version: u32,
    pub circuit: String,
    pub width: u32,
    pub height: u32,
    /// 元 verilog JSON の SHA-256。golden の sourceSha256 と突き合わせる
    pub source_sha256: Option<String>,
    pub gate_count: u32,
    pub dff_count: u32,
    /// 入力ポート名 → ピンセル座標列 (LSB first)。2 相では clocking.clock_port を含まない
    pub inputs: BTreeMap<String, Vec<Xy>>,
    pub outputs: BTreeMap<String, Vec<OutputProbe>>,
    pub clocking: Clocking,
}

impl RoutedMeta {
    pub fn from_json(json: &str) -> Result<Self> {
        let raw: RawRoutedMeta = serde_json::from_str(json).context("parsing routed meta JSON")?;
        let clocking = resolve_clocking(raw.format_version, raw.clocking)?;
        if let Clocking::TwoPhase { clock_port, clk_a, clk_b } = &clocking {
            validate_two_phase_ports(&raw.inputs, clock_port, *clk_a, *clk_b)?;
        }
        Ok(RoutedMeta {
            format_version: raw.format_version,
            circuit: raw.circuit,
            width: raw.width,
            height: raw.height,
            source_sha256: raw.source_sha256,
            gate_count: raw.gate_count,
            dff_count: raw.dff_count,
            inputs: raw.inputs,
            outputs: raw.outputs,
            clocking,
        })
    }

    /// 駆動しなければならないピンすべて (inputs + 2 相のクロックピン)。grid との照合に使う。
    pub fn driven_pins(&self) -> Vec<(String, Xy)> {
        let mut pins: Vec<(String, Xy)> = self.inputs.iter()
            .flat_map(|(name, coords)| coords.iter().enumerate()
                .map(move |(i, c)| (format!("input {name}[{i}]"), *c)))
            .collect();
        if let Clocking::TwoPhase { clk_a, clk_b, .. } = &self.clocking {
            pins.push(("clocking.clkA".to_string(), *clk_a));
            pins.push(("clocking.clkB".to_string(), *clk_b));
        }
        pins
    }
}

/// formatVersion と clocking の組み合わせを Clocking に解決する。
fn resolve_clocking(version: u32, raw: Option<RawClocking>) -> Result<Clocking> {
    match (version, raw) {
        (META_FORMAT_VERSION_LEGACY, None) => Ok(Clocking::SingleEdge),
        (META_FORMAT_VERSION_LEGACY, Some(c)) => anyhow::bail!(
            "meta formatVersion {META_FORMAT_VERSION_LEGACY} must not have clocking (got {c:?}) — \
             clocking was added in formatVersion {META_FORMAT_VERSION_CURRENT}"),
        (META_FORMAT_VERSION_CURRENT, None) => anyhow::bail!(
            "meta formatVersion {META_FORMAT_VERSION_CURRENT} requires clocking"),
        (META_FORMAT_VERSION_CURRENT, Some(RawClocking::SingleEdge {})) => Ok(Clocking::SingleEdge),
        (META_FORMAT_VERSION_CURRENT, Some(RawClocking::TwoPhase { clock_port, clk_a, clk_b })) =>
            Ok(Clocking::TwoPhase { clock_port, clk_a, clk_b }),
        (other, _) => anyhow::bail!(
            "meta formatVersion {other} is not supported (expected {META_FORMAT_VERSION_LEGACY} or {META_FORMAT_VERSION_CURRENT})"),
    }
}

/// 2 相の前提: 元のクロックポートは inputs に無く、clkA / clkB は互いに、また他の入力ピンとも別のセル。
fn validate_two_phase_ports(inputs: &BTreeMap<String, Vec<Xy>>, clock_port: &str, clk_a: Xy, clk_b: Xy) -> Result<()> {
    anyhow::ensure!(!clock_port.is_empty(), "twoPhase clocking has an empty clockPort");
    anyhow::ensure!(!inputs.contains_key(clock_port),
        "twoPhase meta still lists clock port '{clock_port}' in inputs — the original clock is not on the grid in two-phase");
    anyhow::ensure!(clk_a != clk_b,
        "twoPhase clkA and clkB are the same cell ({},{})", clk_a.x, clk_a.y);
    for (name, coords) in inputs {
        for (i, c) in coords.iter().enumerate() {
            anyhow::ensure!(*c != clk_a && *c != clk_b,
                "input {name}[{i}] at ({},{}) overlaps a two-phase clock pin", c.x, c.y);
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const PORTS: &str = r#""circuit":"c","sourceSha256":"ab","width":4,"height":1,
        "gateCount":5,"dffCount":2,
        "outputs":{"q":[{"x":3,"y":0},{"const":0},{"unobservable":3}]}"#;

    fn meta_json(version: u32, inputs: &str, clocking: Option<&str>) -> String {
        let clocking = clocking.map(|c| format!(r#","clocking":{c}"#)).unwrap_or_default();
        format!(r#"{{"formatVersion":{version},{PORTS},"inputs":{{{inputs}}}{clocking}}}"#)
    }

    const TWO_PHASE: &str = r#"{"scheme":"twoPhase","clockPort":"clk","clkA":{"x":1,"y":0},"clkB":{"x":2,"y":0}}"#;

    #[test]
    fn v1_without_clocking_is_single_edge() {
        let meta = RoutedMeta::from_json(&meta_json(1, r#""clk":[{"x":0,"y":0}]"#, None)).unwrap();
        assert_eq!(meta.clocking, Clocking::SingleEdge);
        assert_eq!((meta.gate_count, meta.dff_count), (5, 2));
        assert_eq!(meta.outputs["q"][1], OutputProbe::Const { value: 0 });
    }

    #[test]
    fn v2_single_edge_is_single_edge() {
        let json = meta_json(2, r#""clk":[{"x":0,"y":0}]"#, Some(r#"{"scheme":"singleEdge"}"#));
        assert_eq!(RoutedMeta::from_json(&json).unwrap().clocking, Clocking::SingleEdge);
    }

    #[test]
    fn v2_two_phase_carries_clock_pins() {
        let meta = RoutedMeta::from_json(&meta_json(2, r#""rst":[{"x":0,"y":0}]"#, Some(TWO_PHASE))).unwrap();
        assert_eq!(meta.clocking, Clocking::TwoPhase {
            clock_port: "clk".into(), clk_a: Xy { x: 1, y: 0 }, clk_b: Xy { x: 2, y: 0 },
        });
        let pins: Vec<String> = meta.driven_pins().into_iter().map(|(n, _)| n).collect();
        assert_eq!(pins, ["input rst[0]", "clocking.clkA", "clocking.clkB"]);
    }

    fn rejects(json: &str, needle: &str) {
        let err = format!("{:#}", RoutedMeta::from_json(json).unwrap_err());
        assert!(err.contains(needle), "expected '{needle}' in: {err}");
    }

    #[test]
    fn rejects_unsupported_versions() {
        rejects(&meta_json(3, r#""clk":[{"x":0,"y":0}]"#, Some(r#"{"scheme":"singleEdge"}"#)), "formatVersion 3");
        rejects(&meta_json(0, r#""clk":[{"x":0,"y":0}]"#, None), "formatVersion 0");
    }

    #[test]
    fn rejects_version_clocking_mismatch() {
        rejects(&meta_json(2, r#""clk":[{"x":0,"y":0}]"#, None), "requires clocking");
        rejects(&meta_json(1, r#""rst":[{"x":0,"y":0}]"#, Some(TWO_PHASE)), "must not have clocking");
    }

    #[test]
    fn rejects_malformed_clocking() {
        rejects(&meta_json(2, "", Some(r#"{"scheme":"fourPhase"}"#)), "fourPhase");
        rejects(&meta_json(2, "", Some(r#"{"scheme":"twoPhase","clockPort":"clk","clkB":{"x":2,"y":0}}"#)), "clkA");
        rejects(&meta_json(2, "", Some(r#"{"scheme":"twoPhase","clkA":{"x":1,"y":0},"clkB":{"x":2,"y":0}}"#)), "clockPort");
        rejects(&meta_json(2, "", Some(r#"{"scheme":"singleEdge","clkA":{"x":1,"y":0}}"#)), "clkA");
    }

    #[test]
    fn rejects_inconsistent_two_phase_ports() {
        rejects(&meta_json(2, r#""clk":[{"x":0,"y":0}]"#, Some(TWO_PHASE)), "still lists clock port 'clk'");
        rejects(&meta_json(2, r#""rst":[{"x":1,"y":0}]"#, Some(TWO_PHASE)), "overlaps");
        let same = r#"{"scheme":"twoPhase","clockPort":"clk","clkA":{"x":1,"y":0},"clkB":{"x":1,"y":0}}"#;
        rejects(&meta_json(2, "", Some(same)), "same cell");
    }
}
