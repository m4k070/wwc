#!/usr/bin/env bash
# memory-test.sh — wgpu-runner --memory の回帰テスト (TODO Step B-3b)
#
# 1. golden 照合: 各プログラムを実行し、exit 0 (golden 全周期一致 + expect 合格) を確認する
# 2. 検証器の検証: smoke の出力ビット a_out[6] を駆動する DFF の D 入力セルを空にした .bin を作り、
#    golden との食い違いを「そのビット」として報告して止まること (exit 1、ダンプあり) を確認する。
#    照合が壊れて何でも PASS する状態を検出するためのテスト
# 3. 古い golden: romSha256 を書き換えた golden が GPU 初期化前にエラーになることを確認する
#
# Usage: ./wgpu-runner/memory-test.sh [program.json ...]   (既定: routed/sm83_subset_smoke.json)
set -euo pipefail

cd "$(dirname "$0")/.."

# nix develop の外から呼ばれたら中で実行し直す (Vulkan ライブラリと python3 のため)
if [ -z "${IN_NIX_SHELL:-}" ]; then
  exec nix develop -c "$0" "$@"
fi

RUNNER=./wgpu-runner/target/release/wgpu-runner
SMOKE=routed/sm83_subset_smoke.json
# smoke では a_out が cycle 1 で 0x01 → 0x42 になる (bit 6 が 0→1)。
# bit 6 の DFF の D 入力を切ると、その周期で bit 6 だけが食い違うはず
EXPECTED_DIVERGENCE="a_out: expected 0x42 got 0x2 (bits [6])"

if [ $# -gt 0 ]; then
  PROGRAMS=("$@")
else
  PROGRAMS=("$SMOKE")
fi

echo "Building wgpu-runner..."
(cd wgpu-runner && cargo build --release 2>&1 | tail -1)

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

PASSED=0
FAILED=0
pass() { echo "PASS  $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL  $1"; FAILED=$((FAILED + 1)); }

# --- 1. golden 照合 --------------------------------------------------------
for prog in "${PROGRAMS[@]}"; do
  set +e
  out=$("$RUNNER" --memory "$prog" 2>&1)
  code=$?
  set -e
  summary=$(echo "$out" | grep -E '^golden:|^expect checks:' | tr '\n' ' ')
  if [ $code -eq 0 ]; then
    pass "$prog: $summary"
  else
    fail "$prog (exit $code): $summary"
    echo "$out" | grep -E 'DIVERGED|UNSETTLED|MISMATCH|^  |Error' | sed 's/^/      /'
  fi
done

# --- 2/3 用の変種を作る (smoke を基にする) -----------------------------------
python3 - "$SMOKE" "$WORK" <<'EOF'
import json, pathlib, sys
prog_path = pathlib.Path(sys.argv[1]).resolve()
work = pathlib.Path(sys.argv[2])
base = prog_path.parent
prog = json.loads(prog_path.read_text())

def absolute(p):
    return str((base / p).resolve())

for key in ("meta", "init", "golden"):
    prog[key] = absolute(prog[key])
prog["memory"]["rom"] = absolute(prog["memory"]["rom"])
prog["trace"] = False

# 2. a_out[6] の DFF (東向き) の D 入力 = 西隣のセルを空にする
meta = json.loads(pathlib.Path(prog["meta"]).read_text())
probe = meta["outputs"]["a_out"][6]
grid = bytearray(pathlib.Path(prog["init"]).read_bytes())
width = int.from_bytes(grid[0:4], "little")
def index(x, y):
    return 8 + y * width + x
assert grid[index(probe["x"], probe["y"])] >> 5 == 5, "a_out[6] probe is not a DFF cell"
d_input = index(probe["x"] - 1, probe["y"])
assert grid[d_input] != 0, "D input cell is already empty"
grid[d_input] = 0
(work / "mutated.bin").write_bytes(grid)
(work / "mutated.json").write_text(json.dumps(dict(prog, init=str(work / "mutated.bin"))))

# 3. romSha256 を壊した golden
golden = json.loads(pathlib.Path(prog["golden"]).read_text())
golden["romSha256"] = "0" * 64
(work / "stale.golden.json").write_text(json.dumps(golden))
(work / "stale.json").write_text(json.dumps(dict(prog, golden=str(work / "stale.golden.json"))))
EOF

# --- 2. 検証器の検証 --------------------------------------------------------
set +e
out=$("$RUNNER" --memory "$WORK/mutated.json" --dump-dir "$WORK/dump" 2>&1)
code=$?
set -e
if [ $code -eq 1 ] && echo "$out" | grep -qF "$EXPECTED_DIVERGENCE" && [ -f "$WORK/dump/diverge_cycle001_high.bin" ]; then
  pass "mutated grid (a_out[6] D input removed) is reported as: $EXPECTED_DIVERGENCE"
else
  fail "mutated grid was not reported as expected (exit $code)"
  echo "$out" | grep -E 'golden:|DIVERGED|^  |MISMATCH|Error' | sed 's/^/      /'
fi

# --- 3. 古い golden ---------------------------------------------------------
set +e
out=$("$RUNNER" --memory "$WORK/stale.json" 2>&1)
code=$?
set -e
if [ $code -ne 0 ] && echo "$out" | grep -q "romSha256" && ! echo "$out" | grep -q "^Adapter:"; then
  pass "stale golden (romSha256) is rejected before GPU initialization"
else
  fail "stale golden was not rejected before GPU initialization (exit $code)"
  echo "$out" | sed 's/^/      /'
fi

echo
echo "$PASSED/$((PASSED + FAILED)) passed"
[ $FAILED -eq 0 ]
