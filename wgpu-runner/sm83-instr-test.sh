#!/usr/bin/env bash
# sm83-instr-test.sh
# SM83 命令レベルのシミュレーション検証 (GPU)。
#
# Phase1b で実装予定:
#   F# でコンパイル → ピン設定 → stepN → .bin 出力 (reference)
#   wgpu-runner に同じ .bin を入力 → stepN → .bin 出力
#   2 つの .bin を byte-exact 比較して一致確認
#   レジスタ値 (A/B/PC/Flags) を .bin から読み出し期待値と比較
#
# F# リファレンスの stepN は低速 (69k grid, 62ms/step) なため、
# コンパイル検証は F# (WlSm83Test)、高速シミュレーション検証は
# wgpu-runner (GPU, ~0.4s/2000steps) で行う。

echo "SM83 instruction GPU tests — planned for Phase 1b"
echo "Use: wgpu-runner/run-tests.sh (existing golden tests)"
