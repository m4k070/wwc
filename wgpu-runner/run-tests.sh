#!/usr/bin/env bash
# WireLevel GPU golden tests — uses native wgpu-runner (RTX 3060)
# Usage: ./wgpu-runner/run-tests.sh [--list] [test-name-prefix]
set -euo pipefail

cd "$(dirname "$0")/.."
RUNNER=./wgpu-runner/target/release/wgpu-runner

if [ ! -x "$RUNNER" ]; then
  echo "Building wgpu-runner..."
  nix develop -c bash -c "cd wgpu-runner && cargo build --release" 2>&1 | tail -1
fi

# Parse golden-cases.json to temp file (avoid stdin conflict with nix)
TMPCASES=$(mktemp /tmp/wgpu_cases.XXXXXX.json)
python3 -c "
import json
cases = json.load(open('web/golden-cases.json'))
for c in cases:
    print(json.dumps(c))
" > "$TMPCASES"

PREFIX="${1:-}"
LIST_ONLY=false
[ "$PREFIX" = "--list" ] && LIST_ONLY=true && PREFIX="${2:-}"

passed=0
failed=0
while IFS= read -r line; do
  name=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin)['name'])")
  init=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin)['initFile'])")
  expected=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin)['expectedFile'])")
  steps=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin)['steps'])")

  [[ "$name" != $PREFIX* ]] && continue
  $LIST_ONLY && echo "$name" && continue

  tmp=$(mktemp /tmp/wgpu_test.XXXXXX.bin)
  if nix develop -c bash -c "\"$RUNNER\" \"web/$init\" --steps \"$steps\" --output \"$tmp\"" 2>/dev/null; then
    if cmp -s "$tmp" "web/$expected"; then
      echo "PASS $name"
      passed=$((passed+1))
    else
      echo "FAIL $name (output mismatch)"
      failed=$((failed+1))
    fi
  else
    echo "FAIL $name (runner error)"
    failed=$((failed+1))
  fi
  rm -f "$tmp"
done < "$TMPCASES"
rm -f "$TMPCASES"

if ! $LIST_ONLY; then
  total=$((passed + failed))
  echo ""
  echo "$passed/$total passed"
fi
[ "$failed" -eq 0 ] || exit 1
