#!/usr/bin/env bash
# WireLevel GPU 統合ランナー
#
# 使い方:
#   web/run-wl.sh --all          # 全 golden テスト (bin 自動エクスポート)
#   web/run-wl.sh sm83           # SM83 の export → GPU test
#   web/run-wl.sh --no-export mincpu  # GPU test のみ (export 済み前提)
#   web/run-wl.sh --list         # 利用可能なテストケース一覧
#
# Playwright の引数も透過:
#   web/run-wl.sh sm83 --headed  # ブラウザ表示あり
#   web/run-wl.sh sm83 --debug   # デバッグモード
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Chromium 依存ライブラリ (run-test.sh から移植)
if [[ -n "${WWC_CHROMIUM_LIBS:-}" ]] \
   && { [[ "${WWC_USE_NIX_LIBS:-}" == "1" ]] \
        || ! ldconfig -p 2>/dev/null | grep -q libnss3.so; }; then
  export LD_LIBRARY_PATH="${WWC_CHROMIUM_LIBS}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  echo "Using WWC_CHROMIUM_LIBS for LD_LIBRARY_PATH" >&2
fi

# 依存自動セットアップ
if [[ ! -d web/node_modules ]]; then
  echo "node_modules not found — running npm install..." >&2
  (cd web && npm install)
fi
if ! npx playwright install --dry-run chromium 2>/dev/null | grep -q "is already installed"; then
  echo "Installing Playwright Chromium..." >&2
  npx playwright install chromium
fi

# --- ヘルパー ---
list_cases() {
  echo "Available test cases:"
  echo "  $(python3 -c "
import json
with open('web/golden-cases.json') as f:
    cases = json.load(f)
for c in cases:
    export = f' (export: dotnet fsi {c[\"exportScript\"]})' if c.get('exportScript') else ''
    print(f'  {c[\"name\"]}: {c[\"steps\"]} steps, init={c[\"initFile\"]}, expected={c[\"expectedFile\"]}{export}')
" 2>/dev/null || echo "  (python3 not available — read web/golden-cases.json directly)")"
}

run_export() {
  local name="$1"
  local script
  script=$(python3 -c "
import json
with open('web/golden-cases.json') as f:
    cases = json.load(f)
for c in cases:
    if c['name'] == '$name' and c.get('exportScript'):
        print(c['exportScript'])
        break
" 2>/dev/null || echo "")
  if [[ -z "$script" ]]; then
    echo "No export script found for '$name' — skipping export"
    return 0
  fi
  if [[ ! -f "$script" ]]; then
    echo "Export script not found: $script"
    return 1
  fi
  echo "=== Exporting .bin files ($script) ==="
  dotnet fsi "$script"
}

export_all() {
  echo "=== Exporting all .bin files ==="
  python3 -c "
import json, subprocess, sys
with open('web/golden-cases.json') as f:
    cases = json.load(f)
scripts = set(c['exportScript'] for c in cases if c.get('exportScript'))
for s in sorted(scripts):
    print(f'  dotnet fsi {s}')
    ret = subprocess.run(['dotnet', 'fsi', s], capture_output=False)
    if ret.returncode != 0:
        print(f'ERROR: {s} failed')
        sys.exit(1)
" || exit $?
  echo "=== Export complete ==="
}

# --- メイン ---
CMD="${1:---help}"
shift 2>/dev/null || true

case "$CMD" in
  --all)
    export_all
    echo ""
    echo "=== Running all golden tests ==="
    exec npx playwright test -c web/playwright.config.ts "$@"
    ;;
  --export-all)
    export_all
    ;;
  --list|-l)
    list_cases
    ;;
  --no-export)
    NAME="${1:-}"; shift 2>/dev/null || true
    [[ -z "$NAME" ]] && { echo "Usage: run-wl.sh --no-export <name>"; exit 1; }
    echo "=== Running GPU test: $NAME ==="
    exec npx playwright test -c web/playwright.config.ts --grep "$NAME" "$@"
    ;;
  --help|-h)
    head -22 "$0" | grep '^#' | sed 's/^#//'
    ;;
  "")
    echo "Usage: run-wl.sh <command>"
    echo "  run-wl.sh <name>     — export + GPU test"
    echo "  run-wl.sh --all      — export + all golden tests"
    echo "  run-wl.sh --list     — list available tests"
    echo "  run-wl.sh --help     — this help"
    exit 1
    ;;
  *)
    NAME="$CMD"
    run_export "$NAME"
    echo ""
    echo "=== GPU test ==="
    exec npx playwright test -c web/playwright.config.ts --grep "$NAME" "$@"
    ;;
esac
