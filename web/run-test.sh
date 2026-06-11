#!/usr/bin/env bash
# WebGPU ゴールデンテスト (Playwright) を実行する。
#   web/run-test.sh             # 全テスト
#   web/run-test.sh --headed    # ブラウザ表示あり
# 前提: nix develop シェル内 (WWC_CHROMIUM_LIBS が設定される)、
#       またはシステムに Chromium 依存ライブラリがあること。
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Chromium 依存ライブラリ:
#   システムに揃っていれば (Ubuntu/Pop!_OS 等) そのまま使う。
#   Playwright の Chromium は Ubuntu glibc でビルドされているため、
#   nix の glibc 系を混ぜるとシンボルエラーになる。
#   システムに無い場合 (NixOS 等) のみ WWC_CHROMIUM_LIBS (flake.nix が export) を注入。
#   WWC_USE_NIX_LIBS=1 で強制注入できる。
if [[ -n "${WWC_CHROMIUM_LIBS:-}" ]] \
   && { [[ "${WWC_USE_NIX_LIBS:-}" == "1" ]] \
        || ! ldconfig -p 2>/dev/null | grep -q libnss3.so; }; then
  export LD_LIBRARY_PATH="${WWC_CHROMIUM_LIBS}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  echo "Using WWC_CHROMIUM_LIBS for LD_LIBRARY_PATH"
fi

if ! command -v npx >/dev/null; then
  echo "error: npx not found. Run inside 'nix develop' or install Node.js." >&2
  exit 1
fi

# 依存パッケージとブラウザの自動セットアップ
if [[ ! -d node_modules ]]; then
  echo "node_modules not found — running npm install..."
  npm install
fi
if ! npx playwright install --dry-run chromium 2>/dev/null | grep -q "is already installed"; then
  echo "Installing Playwright Chromium..."
  npx playwright install chromium
fi

exec npx playwright test -c web/playwright.config.ts "$@"
