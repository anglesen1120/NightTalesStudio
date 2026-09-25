#!/usr/bin/env bash
# worker-node/bin/smoke.sh — run every present smoke-*.sh in a fixed order, then check-binds.sh, then the
# git.hooks row (01.2.1-T3 step 4, SEC-12). Exit = highest child code (2 > 1 > 0).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

if [ "${1:-}" = --help ] || [ "${1:-}" = -h ]; then
  cat <<'EOF'
Usage: worker-node/bin/smoke.sh
Order: smoke-vieneu.sh, smoke-mpt.sh, then any other smoke-*.sh (sorted), check-binds.sh, git.hooks.
Lines: PASS/FAIL/WARN/INFO. Exit codes: 0 all pass · 1 a check failed · 2 an environment error.
EOF
  exit 0
fi

BIN="$NTS_REPO_ROOT/worker-node/bin"
worst=0
run() {
  local s="$1" rc=0
  echo "== $(basename "$s")"
  "$s" || rc=$?
  if [ "$rc" -eq 2 ]; then worst=2; elif [ "$rc" -eq 1 ] && [ "$worst" -lt 1 ]; then worst=1; fi
}

ordered=()
for s in smoke-vieneu.sh smoke-mpt.sh; do [ -x "$BIN/$s" ] && ordered+=("$BIN/$s"); done
for s in "$BIN"/smoke-*.sh; do
  [ -x "$s" ] || continue
  case "$(basename "$s")" in smoke-vieneu.sh|smoke-mpt.sh) continue ;; esac
  ordered+=("$s")
done
for s in "${ordered[@]+"${ordered[@]}"}"; do run "$s"; done
if [ -x "$BIN/check-binds.sh" ]; then run "$BIN/check-binds.sh"; else echo "FAIL binds-all: check-binds.sh missing"; worst=$(( worst > 1 ? worst : 1 )); fi

echo "== git.hooks"
if [ "$(git -C "$NTS_REPO_ROOT" config --get core.hooksPath 2>/dev/null || true)" = .githooks ]; then
  echo "PASS git.hooks"
else
  echo "FAIL git.hooks: core.hooksPath is not .githooks (run: git config core.hooksPath .githooks)"
  [ "$worst" -ge 1 ] || worst=1
fi
exit "$worst"
