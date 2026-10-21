#!/usr/bin/env bash
# worker-node/bin/check-binds.sh — loopback allowlist for every D4 port and every LISTEN socket of the
# service process trees; nothing may listen on 8501–8599 (D14). 01.2.1-T3, SEC-05.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

if [ "${1:-}" = --help ] || [ "${1:-}" = -h ]; then
  cat <<'EOF'
Usage: worker-node/bin/check-binds.sh
Lines: PASS <id> · FAIL <id>: <detail> · INFO <id>: <value>
Exit codes: 0 all loopback · 1 a non-loopback or forbidden listener · 2 lsof missing.
EOF
  exit 0
fi
[ $# -eq 0 ] || { echo "check-binds.sh: no arguments (see --help)" >&2; exit 2; }
command -v lsof >/dev/null 2>&1 || die 2 "lsof missing"

rc=0
fail() { echo "FAIL $1: $2"; rc=1; }
is_loopback() { [ "$1" = "127.0.0.1:$2" ] || [ "$1" = "[::1]:$2" ]; }

pids=()
for port in 8000 8001 8002 8080; do
  names="$(lsof -nP -iTCP:"$port" -sTCP:LISTEN -Fn 2>/dev/null | sed -n 's/^n//p' || true)"
  if [ -z "$names" ]; then echo "INFO port-$port: not running"; continue; fi
  bad=""
  while IFS= read -r n; do is_loopback "$n" "$port" || bad="$bad $n"; done <<<"$names"
  if [ -n "$bad" ]; then fail "port-$port" "non-loopback listener:$bad"; else echo "PASS port-$port"; fi
  while IFS= read -r p; do [ -n "$p" ] && pids+=("$p"); done < <(lsof -nP -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null || true)
done

# Descendants and the listeners' own trees (uv run → python). Scoped so macOS daemons do not count.
add_children() {
  local c
  for c in $(pgrep -P "$1" 2>/dev/null || true); do pids+=("$c"); add_children "$c"; done
}
if [ "${#pids[@]}" -gt 0 ]; then
  for p in "${pids[@]}"; do add_children "$p"; done
  list="$(printf '%s\n' "${pids[@]}" | sort -u | paste -sd, -)"
  bad=""
  while IFS= read -r n; do
    [ -n "$n" ] || continue
    port="${n##*:}"
    is_loopback "$n" "$port" || bad="$bad $n"
  done < <(lsof -a -nP -p "$list" -iTCP -sTCP:LISTEN -Fn 2>/dev/null | sed -n 's/^n//p' | sort -u || true)
  if [ -n "$bad" ]; then fail service-trees "non-loopback socket:$bad"; else echo "PASS service-trees"; fi
else
  echo "INFO service-trees: no D4 listener"
fi

webui="$(lsof -nP -iTCP:8501-8599 -sTCP:LISTEN -Fn 2>/dev/null | sed -n 's/^n//p' | sort -u | tr '\n' ' ' || true)"
if [ -n "$webui" ]; then fail webui-range "listener on 8501-8599 (D14): $webui"; else echo "PASS webui-range"; fi

exit "$rc"
