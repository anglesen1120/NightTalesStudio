#!/usr/bin/env bash
# worker-node/bin/start-proxy.sh — start the VieNeu speech proxy in the foreground on
# 127.0.0.1:8001 (04 D1/D13, 04.1.5-T1). Keys come from the Keychain and reach the proxy
# only through its environment — never argv, never a log line (01 D3/D9).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/start-proxy.sh [--check]

Starts nts_vieneu_proxy in the foreground (child of this script), output tee'd to
$NTS_HOME/logs/proxy-<YYYYmmdd-HHMMSS>.log. Exit code is the proxy's own.
Requires: bootstrap.sh proxy, VieNeu listening on 127.0.0.1:8000, and both keys
in the Keychain (nts.proxy.api_key, nts.vieneu.api_key via secrets.sh init-local).

  --check   Validate config and the voice catalog in-process (exit 0 "config OK"),
            without binding port 8001 — safe while the proxy runs.
EOF
}

CHECK_ONLY=0
case "${1:-}" in
  "") ;;
  --check) CHECK_ONLY=1 ;;
  --help|-h) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac
[ $# -le 1 ] || { usage >&2; exit 2; }

# Port guard (01 D4). Skipped in --check so the preflight also works while the proxy runs.
[ "$CHECK_ONLY" -eq 1 ] || require_port_free 8001

# Non-secret config goes into the environment. The two keys never do (P3 security review):
# they are read from the Keychain into shell variables and reach the proxy child only through
# a bash prefix assignment (the child's environment — never argv, never exported, never logged).
export PROXY_HOST=127.0.0.1 PROXY_PORT=8001
export VIENEU_BASE_URL=http://127.0.0.1:8000
export VOICE_CATALOG_FILE="$NTS_REPO_ROOT/seed/voices.v1.json"
export PROXY_MAX_CONCURRENT=2   # 04 D13: Mac runs 2 concurrent pipelines

PY="$(nts_home)/src/proxy-venv/bin/python"
[ -x "$PY" ] || die 2 "proxy venv missing; run worker-node/bin/bootstrap.sh proxy"

# Clear the keys from this shell on every exit path, not just the happy path.
trap 'unset PROXY_API_KEY VIENEU_API_KEY' EXIT

# --check: Settings.from_env + load_catalog in-process — catches config/catalog errors
# (D13/D9, ConfigError/CatalogError) in under a second, without binding the port. The keys
# reach only the interpreter's environment via a per-command prefix assignment.
if [ "$CHECK_ONLY" -eq 1 ]; then
  PROXY_API_KEY="$(secret nts.proxy.api_key)" \
    VIENEU_API_KEY="$(secret nts.vieneu.api_key)" \
    "$PY" - <<'PYEOF' || exit 2
import os, sys
from nts_vieneu_proxy.config import Settings, ConfigError
from nts_vieneu_proxy.catalog import load_catalog, CatalogError
try:
    s = Settings.from_env(os.environ)
    load_catalog(s.voice_catalog_file)
except (ConfigError, CatalogError) as e:
    print(f"invalid: {e}", file=sys.stderr)
    sys.exit(2)
print("config OK")
PYEOF
  exit 0
fi

# Fast, clear failure instead of a slow first-request timeout when VieNeu is down. The probe
# runs before the Keychain reads, so a down VieNeu never even decrypts the secrets.
if ! curl -fsS --max-time 5 -o /dev/null "http://127.0.0.1:8000/health"; then
  die 2 "VieNeu is not listening on 127.0.0.1:8000; start it first (worker-node/bin/start-vieneu.sh)"
fi

# Keys: Keychain -> shell variables -> the proxy child's environment only (per-command prefix).
PROXY_API_KEY="$(secret nts.proxy.api_key)"
VIENEU_API_KEY="$(secret nts.vieneu.api_key)"

NTS_HOME="$(nts_home)"
mkdir -p "$NTS_HOME/logs"
LOG="$NTS_HOME/logs/proxy-$(date +%Y%m%d-%H%M%S).log"
log "starting proxy, log: $LOG"
# Foreground child (no exec — an exec inside a pipeline does not replace the script);
# the listener's command line is ".../proxy-venv/bin/python -m nts_vieneu_proxy" (04 CR-6,
# matched by 01.2.2-T4 step 3a and smoke-proxy.sh). Exit code is the proxy's (PIPESTATUS).
# The keys are prefix-assigned for this command only — the child's env, never argv.
PROXY_API_KEY="$PROXY_API_KEY" VIENEU_API_KEY="$VIENEU_API_KEY" \
  "$PY" -m nts_vieneu_proxy 2>&1 | tee "$LOG"
rc="${PIPESTATUS[0]}"
unset PROXY_API_KEY VIENEU_API_KEY
exit "$rc"
