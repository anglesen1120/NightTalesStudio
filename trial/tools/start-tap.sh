#!/usr/bin/env bash
# trial/tools/start-tap.sh — start the usage tap (trial/tools/usage_tap.py) in the
# foreground on 127.0.0.1:8002 with a teed log (01.3.1-T2, D9).
# The tap forwards /v1/* to the pinned upstream (origin of OPENAI_BASE_URL,
# 01 CR-15 / DEC-9) and appends one D6 JSONL line per call. It must run before
# any MPT render. Ctrl-C stops it. It writes $NTS_HOME/tap.json itself once bound.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
# shellcheck source=worker-node/bin/lib.sh
. "$REPO/worker-node/bin/lib.sh"

usage() {
  cat <<'EOF'
Usage: trial/tools/start-tap.sh [--help]
  Starts usage_tap.py in the foreground on 127.0.0.1:8002 (D4).
  Env overrides (see usage_tap.py --help): TAP_PORT, TAP_UPSTREAM (stub tests
  only), TAP_UPSTREAM_TIMEOUT_S, TAP_CONNECT_TIMEOUT_S, TAP_MAX_BODY_BYTES,
  TAP_MAX_RESPONSE_BYTES, TAP_IMAGE_QUALITY, TAP_RUNS_DIR, TAP_MAX_CONCURRENCY.
Exit codes: 0 ok · 2 usage/environment error (D9).
EOF
}

for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
    *) die 2 "unknown argument: $arg (try --help)" ;;
  esac
done

NTS_HOME="$(nts_home)"; export NTS_HOME   # the tap writes tap.json and reads current_run here
load_pins
PORT="${TAP_PORT:-8002}"
require_port_free "$PORT"                      # exit 2 if listening (D4)
mkdir -p "$NTS_HOME/logs"
ts="$(date -u +%Y%m%d-%H%M%S)"
log "usage tap on 127.0.0.1:$PORT -> ${TAP_UPSTREAM:-${OPENAI_BASE_URL%/v1}}"   # (01 CR-15)
uv run --no-project --python 3.12 "$HERE/usage_tap.py" 2>&1 \
  | tee -a "$NTS_HOME/logs/tap-$ts.log"
