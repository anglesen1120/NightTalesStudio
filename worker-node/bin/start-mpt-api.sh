#!/usr/bin/env bash
# worker-node/bin/start-mpt-api.sh — render and guard the MPT config, verify the upstream tap/proxy
# listeners MPT will send keys to (SEC-04), then run MPT on 127.0.0.1:8080 in the foreground
# with a teed log (01.2.2-T4, FR-01-1). No WebUI in M0 (D14).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/start-mpt-api.sh [--template PATH] [--no-tap] [--help]
  --template PATH   test hook: passed through to render-mpt-config.py --template
  --no-tap          test hook, never in measured runs: skip the 8002 tap check and require
                    the rendered base URLs to be https://api.openai.com/v1
Exit codes: 0 ok · 1 check failed · 2 usage/environment error (D9).
Secrets: the four Keychain values exist only in the renderer's environment and in
config.toml; MPT gets no secret env var. Never echoes a value.
EOF
}

TEMPLATE="" NO_TAP=0
while [ $# -gt 0 ]; do
  case "$1" in
    --template) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift; TEMPLATE="$1" ;;
    --no-tap) NO_TAP=1 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done

# 1-2. Paths, pins, environment checks (exit 2).
NTS_HOME="$(nts_home)"
load_pins
ROOT="$NTS_HOME/src/mpt"
REPO="$NTS_REPO_ROOT"
RENDERER="$REPO/worker-node/bin/render-mpt-config.py"
[ "$(git -C "$ROOT" rev-parse HEAD 2>/dev/null)" = "$MPT_COMMIT" ] \
  || die 2 "MPT clone is not at MPT_COMMIT: run bootstrap.sh mpt"
[ -x /opt/homebrew/bin/ffmpeg ] || die 2 "missing /opt/homebrew/bin/ffmpeg (E2)"
[ -d "$ROOT/.venv" ] || die 2 "no venv in $ROOT: run bootstrap.sh mpt"
mkdir -p "$NTS_HOME/logs"

# 3. Port (D4).
require_port_free 8080

# 3a. SEC-04 upstream listener ownership — BEFORE any Keychain read. MPT sends the OpenAI keys
# to 127.0.0.1:8002 and the proxy key to 127.0.0.1:8001, so those listeners must be the expected
# processes owned by this user.
listener_rows() { lsof -a -nP -iTCP:"$1" -sTCP:LISTEN -Fpcu 2>/dev/null || true; }

if [ "$NO_TAP" -eq 0 ]; then
  rows="$(listener_rows 8002)"
  n="$(printf '%s\n' "$rows" | grep -c '^p' || true)"
  [ "$n" = 1 ] || die 2 "tap not verified: port 8002 expected exactly 1 listener, found $n"
  printf '%s\n' "$rows" | grep -qx "u$(id -u)" \
    || die 2 "tap not verified: port 8002 listener not owned by $(id -un)"
  tap_pid="$(printf '%s\n' "$rows" | sed -n 's/^p//p' | head -1)"
  ps -o command= -p "$tap_pid" | grep -q 'usage_tap\.py' \
    || die 2 "tap not verified: port 8002 listener is not usage_tap.py"
  TAP_JSON="$NTS_HOME/tap.json"
  [ -f "$TAP_JSON" ] || die 2 "tap not verified: $TAP_JSON missing (usage_tap.py writes it)"
  uv run --no-project --python 3.12 python - "$TAP_JSON" "$tap_pid" <<'PY' \
    || die 2 "tap not verified: tap.json upstream or pid mismatch"
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
assert d.get("upstream") == "https://api.openai.com", "upstream"
assert str(d.get("pid")) == sys.argv[2], "pid"
PY
fi

# --no-tap skips only the 8002 check (01.2.2-T4 step 3a): the proxy key still goes to 8001.
rows="$(listener_rows 8001)"
n="$(printf '%s\n' "$rows" | grep -c '^p' || true)"
if [ "$n" -eq 0 ]; then
  log "WARN port 8001: no proxy listener (04.1.2 not landed); runs needing kokoro: voices will fail"
else
  [ "$n" = 1 ] || die 2 "port 8001: expected exactly 1 listener, found $n"
  printf '%s\n' "$rows" | grep -qx "u$(id -u)" \
    || die 2 "port 8001: listener not owned by $(id -un)"
  proxy_pid="$(printf '%s\n' "$rows" | sed -n 's/^p//p' | head -1)"
  ps -o command= -p "$proxy_pid" | grep -q 'nts_vieneu_proxy' \
    || die 2 "port 8001: listener is not the nts_vieneu_proxy"
fi

# 4. Licensed-music gate (DECISIONS §5.5).
"$REPO/worker-node/bin/strip-bundled-music.sh" --check --mpt-root "$ROOT" || exit 1

# 5. Render. Secret reads are standalone assignments: a missing Keychain item makes `secret`
#    die 2 and set -e propagates it (a failure inside an env-prefix substitution would be
#    swallowed into an empty value). The values live only in these variables and the
#    renderer's environment; they are never printed or passed as argv.
K_MPT="$(secret nts.mpt.api_key)"
K_STORY="$(secret nts.openai.story)"
K_IMAGE="$(secret nts.openai.image)"
K_PROXY="$(secret nts.proxy.api_key)"
RENDER_ARGS=()
[ -n "$TEMPLATE" ] && RENDER_ARGS+=(--template "$TEMPLATE")
[ "$NO_TAP" -eq 1 ] && RENDER_ARGS+=(--allow-direct-openai)
rc=0
NTS_MPT_API_KEY="$K_MPT" NTS_OPENAI_STORY_KEY="$K_STORY" \
NTS_OPENAI_IMAGE_KEY="$K_IMAGE" NTS_PROXY_API_KEY="$K_PROXY" \
  uv run --no-project --python 3.12 "$RENDERER" "${RENDER_ARGS[@]+"${RENDER_ARGS[@]}"}" || rc=$?
unset K_MPT K_STORY K_IMAGE K_PROXY
[ "$rc" -eq 0 ] || exit 1

# --no-tap additionally requires the rendered file to bypass the tap entirely.
if [ "$NO_TAP" -eq 1 ]; then
  uv run --no-project --python 3.12 python - "$ROOT/config.toml" <<'PY' \
    || die 2 "--no-tap: rendered config does not point at https://api.openai.com/v1"
import sys, tomllib
cfg = tomllib.loads(open(sys.argv[1], encoding="utf-8").read())
app = cfg["app"]
assert app["openai_base_url"] == "https://api.openai.com/v1", "openai_base_url"
assert app["openai_image_base_url"] == "https://api.openai.com/v1", "openai_image_base_url"
PY
fi

# 6. Refuse unless the rendered file is present and valid (MPT would copy the example → auth
#    off + 0.0.0.0, F07).
CHECK_ARGS=(--check)
[ "$NO_TAP" -eq 1 ] && CHECK_ARGS=(--check --allow-direct-openai)
if [ ! -f "$ROOT/config.toml" ] || [ -L "$ROOT/config.toml" ] \
  || ! uv run --no-project --python 3.12 "$RENDERER" "${CHECK_ARGS[@]}"; then
  log "refusing to start: config.toml missing/invalid (MPT would copy config.example.toml → auth off, 0.0.0.0; F07)"
  exit 1
fi

# 7. Launch in the foreground with a teed log. Secrets live only in config.toml; the child
#    environment is rebuilt with builtins (never `env -i` with a value: it would sit in env(1)'s
#    argv until exec).
LOG="$NTS_HOME/logs/mpt-api-$(date +%Y%m%d-%H%M%S).log"
log "log: $LOG"
cd "$ROOT"
HFO="${HF_HUB_OFFLINE:-1}"
[ "$HFO" = 1 ] || log "WARN HF_HUB_OFFLINE=$HFO (expected 1; honouring caller value, 01.2.3-T1)"
KEEP_PATH="$PATH" KEEP_HOME="$HOME" KEEP_USER="$USER"
(
  while IFS= read -r ename; do unset "$ename" 2>/dev/null || true; done < <(compgen -e)
  export PATH="$KEEP_PATH" HOME="$KEEP_HOME" USER="$KEEP_USER" LANG=en_US.UTF-8
  export IMAGEIO_FFMPEG_EXE=/opt/homebrew/bin/ffmpeg HF_HUB_OFFLINE="$HFO" PYTHONUNBUFFERED=1
  exec uv run python main.py
) > >(tee -a "$LOG") 2>&1 &
SRV=$!

# Stop cleanly on Ctrl-C / TERM: kill the wrapper and whatever holds :8080.
cleanup() {
  kill -TERM "$SRV" 2>/dev/null || true
  lsof -t -iTCP:8080 -sTCP:LISTEN 2>/dev/null | xargs kill 2>/dev/null || true
  wait "$SRV" 2>/dev/null || true
}
trap 'cleanup; exit 0' INT TERM

# 8. Post-start assertions: listener within 60 s, loopback binds, auth enabled.
T0=$SECONDS
while :; do
  if lsof -nP -iTCP:8080 -sTCP:LISTEN -t >/dev/null 2>&1; then break; fi
  if ! kill -0 "$SRV" 2>/dev/null; then
    log "server exited during start; last log lines:"
    tail -30 "$LOG" >&2 || true
    exit 1
  fi
  if [ $((SECONDS - T0)) -ge 60 ]; then
    log "FAIL no listener on 8080 after 60 s"
    cleanup
    exit 1
  fi
  sleep 1
done

fail_start() {
  log "FAIL $1"
  cleanup
  exit 1
}

"$REPO/worker-node/bin/check-binds.sh" || fail_start "non-loopback listener (check-binds.sh)"
lsof -nP -iTCP:8080 -sTCP:LISTEN -Fn | grep -qx 'n127.0.0.1:8080' \
  || fail_start "8080 is not bound to 127.0.0.1"
grep -q 'API key authentication is disabled' "$LOG" \
  && fail_start "API key authentication is disabled (empty api_key, M11)"

echo "mpt-api ready on 127.0.0.1:8080 log=$LOG"

# Foreground until the server exits.
rc=0
wait "$SRV" || rc=$?
[ "$rc" -eq 0 ] || exit 1
