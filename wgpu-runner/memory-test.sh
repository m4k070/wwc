#!/usr/bin/env bash
# memory-test.sh — wgpu-runner --memory の回帰テスト (TODO Step B-3b)
#
# 1. golden 照合: 各プログラムを実行し、exit 0 (golden 全周期一致 + expect 合格) を確認する
#    既定は単相の sm83_subset_smoke と 2 相 (clocking=twoPhase) の membus_tiny_2p_xor
# 2. 検証器の検証: 出力ビットを駆動する DFF の D 入力セルを空にした .bin を作り、
#    golden との食い違いを「そのビット」として報告して止まること (exit 1、ダンプあり) を確認する。
#    照合が壊れて何でも PASS する状態を検出するためのテスト
#    - 単相: smoke の a_out[6] (cycle 1 で 0x01 → 0x42、bit 6 が 0→1)
#    - 2 相: membus_tiny_2p の a_out[4] のスレーブ DFF (cycle 0 で 0x00 → 0x11、bit 4 が 0→1)。
#      出力 probe がスレーブを指し、その D がマスター DFF から来ることも確かめる
# 3. 古い golden: romSha256 を書き換えた golden が GPU 初期化前にエラーになることを確認する
#
# Usage: ./wgpu-runner/memory-test.sh [program.json ...]   (1 の対象。既定: 下の PROGRAMS)
set -euo pipefail

cd "$(dirname "$0")/.."

# nix develop の外から呼ばれたら中で実行し直す (Vulkan ライブラリと python3 のため)
if [ -z "${IN_NIX_SHELL:-}" ]; then
  exec nix develop -c "$0" "$@"
fi

RUNNER=./wgpu-runner/target/release/wgpu-runner
SMOKE=routed/sm83_subset_smoke.json
TWO_PHASE=routed/membus_tiny_2p_xor.json
# D 入力を切ったビットが、その周期でそのビットだけ食い違うはず
EXPECTED_DIVERGENCE_SINGLE="a_out: expected 0x42 got 0x2 (bits [6])"
EXPECTED_DIVERGENCE_TWO_PHASE="a_out: expected 0x11 got 0x1 (bits [4])"

if [ $# -gt 0 ]; then
  PROGRAMS=("$@")
else
  PROGRAMS=("$SMOKE" "$TWO_PHASE")
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
  summary=$(echo "$out" | grep -E '^golden:|^expect checks:' | tr '\n' ' ' || true)
  if [ $code -eq 0 ]; then
    pass "$prog: $summary"
  else
    fail "$prog (exit $code): $summary"
    echo "$out" | grep -E 'DIVERGED|UNSETTLED|MISMATCH|^  |Error' | sed 's/^/      /' || true
  fi
done

# --- 2/3 用の変種を作る ------------------------------------------------------
# 引数: <program.json> <出力先> <変種名> <port> <bit> <scheme>
#   <変種名>.json / .bin:   <port>[<bit>] の DFF (東向き) の D 入力 = 西隣のセルを空にしたもの
#   <変種名>_stale.json:    romSha256 を壊した golden を指すもの
make_variants() {
python3 - "$@" <<'PY'
import json, pathlib, sys
prog_path, work, name, port, bit, scheme = sys.argv[1:7]
prog_path = pathlib.Path(prog_path).resolve()
work = pathlib.Path(work)
bit = int(bit)
base = prog_path.parent
prog = json.loads(prog_path.read_text())

def absolute(p):
    return str((base / p).resolve())

for key in ("meta", "init", "golden"):
    prog[key] = absolute(prog[key])
prog["memory"]["rom"] = absolute(prog["memory"]["rom"])
prog["trace"] = False

meta = json.loads(pathlib.Path(prog["meta"]).read_text())
actual_scheme = meta.get("clocking", {}).get("scheme", "singleEdge")
assert actual_scheme == scheme, f"{prog_path}: clocking {actual_scheme} != {scheme}"

KIND_WIRE, KIND_CROSS, KIND_DFF = 2, 4, 5
EAST, WEST, NORTH, SOUTH = 0, 1, 2, 3
STEP = {EAST: (1, 0), WEST: (-1, 0), NORTH: (0, -1), SOUTH: (0, 1)}
OPPOSITE = {EAST: WEST, WEST: EAST, NORTH: SOUTH, SOUTH: NORTH}

grid = bytearray(pathlib.Path(prog["init"]).read_bytes())
width = int.from_bytes(grid[0:4], "little")
def index(x, y):
    return 8 + y * width + x
def kind(x, y):
    return grid[index(x, y)] >> 5
def direction(x, y):
    return (grid[index(x, y)] >> 3) & 3

probe = meta["outputs"][port][bit]
px, py = probe["x"], probe["y"]
assert kind(px, py) == KIND_DFF, f"{port}[{bit}] probe is not a DFF cell"
assert direction(px, py) == EAST, f"{port}[{bit}] DFF does not face east"

if scheme == "twoPhase":
    # 出力 probe はスレーブ: D (西隣) から配線を遡るとマスター DFF に着く
    x, y, travel = px - 1, py, WEST
    for _ in range(len(grid)):
        k = kind(x, y)
        if k == KIND_WIRE:
            travel = OPPOSITE[direction(x, y)]   # Wire は向きの反対側から値を引く
        elif k != KIND_CROSS:                    # Cross は同じ向きのまま通り抜ける
            break
        dx, dy = STEP[travel]
        x, y = x + dx, y + dy
    assert kind(x, y) == KIND_DFF, \
        f"{port}[{bit}] D input does not trace back to a master DFF (kind={kind(x, y)} at {x},{y})"

d_input = index(px - 1, py)
assert grid[d_input] != 0, "D input cell is already empty"
grid[d_input] = 0
(work / f"{name}.bin").write_bytes(grid)
(work / f"{name}.json").write_text(json.dumps(dict(prog, init=str(work / f"{name}.bin"))))

golden = json.loads(pathlib.Path(prog["golden"]).read_text())
golden["romSha256"] = "0" * 64
(work / f"{name}_stale.golden.json").write_text(json.dumps(golden))
(work / f"{name}_stale.json").write_text(json.dumps(dict(prog, golden=str(work / f"{name}_stale.golden.json"))))
PY
}

make_variants "$SMOKE" "$WORK" mutated a_out 6 singleEdge
make_variants "$TWO_PHASE" "$WORK" mutated_2p a_out 4 twoPhase

# --- 2. 検証器の検証 --------------------------------------------------------
# 引数: <変種名> <期待する食い違い> <食い違う周期 (3 桁)> <説明>
check_mutation() {
  local name=$1 expected=$2 cycle=$3 label=$4
  set +e
  out=$("$RUNNER" --memory "$WORK/$name.json" --dump-dir "$WORK/dump_$name" 2>&1)
  code=$?
  set -e
  if [ $code -eq 1 ] && echo "$out" | grep -qF "$expected" && [ -f "$WORK/dump_$name/diverge_cycle${cycle}_high.bin" ]; then
    pass "$label is reported as: $expected"
  else
    fail "$label was not reported as expected (exit $code)"
    echo "$out" | grep -E 'golden:|DIVERGED|^  |MISMATCH|Error' | sed 's/^/      /'
  fi
}

check_mutation mutated "$EXPECTED_DIVERGENCE_SINGLE" 001 "mutated grid (a_out[6] D input removed)"
check_mutation mutated_2p "$EXPECTED_DIVERGENCE_TWO_PHASE" 000 "two-phase mutated grid (a_out[4] slave DFF D input removed)"

# --- 3. 古い golden ---------------------------------------------------------
set +e
out=$("$RUNNER" --memory "$WORK/mutated_stale.json" 2>&1)
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
