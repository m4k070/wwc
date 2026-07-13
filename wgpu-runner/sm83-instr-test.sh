#!/usr/bin/env bash
# sm83-instr-test.sh — SM83 命令レベル GPU 検証 (Phase 1b)
#
# F# でコンパイル済みの初期グリッド (init.bin) + 座標メタ (meta.json) +
# 命令列と期待レジスタ値 (program.json) を wgpu-runner のプログラムモードに渡し、
# GPU 単独で「inst 書込 → clk=0 settle → clk=1 settle → レジスタ読出 → 期待値比較」
# のサイクルを回す。F# リファレンスシミュレーションは不要。
#
# Usage: ./wgpu-runner/sm83-instr-test.sh [--export] [--dump-regs] [--dump-dir DIR]
#   --export     F# エクスポート (ExportSm83MinInstr.fsx) を強制再実行 (~4min)
#   --dump-regs  期待値比較せず全レジスタ値を JSON lines で出力
#   --dump-dir   FAIL した命令のグリッドを .bin 保存するディレクトリ
set -euo pipefail

cd "$(dirname "$0")/.."
RUNNER=./wgpu-runner/target/release/wgpu-runner
PROGRAM=web/sm83_min_program.json

FORCE_EXPORT=false
PASSTHRU=()
for arg in "$@"; do
  case "$arg" in
    --export) FORCE_EXPORT=true ;;
    *) PASSTHRU+=("$arg") ;;
  esac
done

if [ ! -x "$RUNNER" ]; then
  echo "Building wgpu-runner..."
  nix develop -c bash -c "cd wgpu-runner && cargo build --release" 2>&1 | tail -1
fi

if $FORCE_EXPORT || [ ! -f "$PROGRAM" ]; then
  echo "Exporting sm83_min instruction test artifacts (F# compile + reset settle)..."
  nix develop -c bash -c "dotnet build src/WwHdl.fsproj -v q && dotnet fsi src/ExportSm83MinInstr.fsx"
fi

nix develop -c bash -c "\"$RUNNER\" --program \"$PROGRAM\" ${PASSTHRU[*]+"${PASSTHRU[*]}"}"
