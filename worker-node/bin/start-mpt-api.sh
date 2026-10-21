#!/usr/bin/env bash
# worker-node/bin/start-mpt-api.sh — render and guard the MPT config, verify the upstream tap/proxy
# listeners MPT will send keys to (SEC-04), then run MPT on 127.0.0.1:8080 in the foreground
# with a teed log (01.2.2-T4, FR-01-1). No WebUI in M0 (D14).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/start-mpt-api.sh [--template PATH] [--no-tap] [--profile normal|spike] [--help]
  --template PATH   test hook: passed through to render-mpt-config.py --template
  --no-tap          test hook, never in measured runs: skip the 8002 tap check and require
                    the rendered base URLs to be OPENAI_BASE_URL (pins.env)
  --profile spike   03.4.1-T1 spike: render config.spike.toml (image upstream = the local
                    stub on 127.0.0.1:18003, empty image key list, raw-term prompt
                    template), run MPT on 127.0.0.1:18080 beside the live 8080 instance,
                    and restore the previous config.toml on every exit path. Requires a
                    running image_stub.py on 18003 and no leftover config.toml.pre-spike.
Exit codes: 0 ok · 1 check failed · 2 usage/environment error (D9).
Secrets: the four Keychain values exist only in the renderer's environment and in
config.toml; MPT gets no secret env var. Never echoes a value.
EOF
}

TEMPLATE="" NO_TAP=0 PROFILE=normal SPIKE_SWAPPED=0
while [ $# -gt 0 ]; do
  case "$1" in
    --template) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift; TEMPLATE="$1" ;;
    --no-tap) NO_TAP=1 ;;
    --profile) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift; PROFILE="$1" ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done
case "$PROFILE" in
  normal|spike) ;;
  *) echo "start-mpt-api.sh: --profile must be normal or spike (got '$PROFILE')" >&2; exit 2 ;;
esac
if [ "$PROFILE" = spike ] && [ "$NO_TAP" -eq 1 ]; then
  echo "start-mpt-api.sh: --profile spike and --no-tap are mutually exclusive" >&2
  exit 2
fi

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

# 3. Port (D4). The spike profile uses its own port so it can run beside the live
#    8080 instance without touching it (03.4.1-T1).
MPT_PORT=8080
LOG_TAG="mpt-api"
if [ "$PROFILE" = spike ]; then
  MPT_PORT=18080
  LOG_TAG="mpt-spike-api"
  # The swap is never started without a running stub, and never over a swap that
  # an earlier run failed to restore.
  [ ! -e "$ROOT/config.toml.pre-spike" ] \
    || die 2 "spike: $ROOT/config.toml.pre-spike exists (an earlier spike did not restore config.toml); restore it by hand first"
else
  [ ! -e "$ROOT/config.toml.pre-spike" ] \
    || die 2 "refusing to start the normal profile: $ROOT/config.toml.pre-spike exists (a spike swap is in place); restore it first"
fi
require_port_free "$MPT_PORT"

# 3a. SEC-04 upstream listener ownership — BEFORE any Keychain read. MPT sends the OpenAI keys
# to 127.0.0.1:8002 and the proxy key to 127.0.0.1:8001, so those listeners must be the expected
# processes owned by this user.
listener_rows() { lsof -a -nP -iTCP:"$1" -sTCP:LISTEN -Fpcu 2>/dev/null || true; }

assert_owned_listener() {
  # assert_owned_listener <port> <command-regex> <label>
  local port="$1" pattern="$2" label="$3" rows n pid
  rows="$(listener_rows "$port")"
  n="$(printf '%s\n' "$rows" | grep -c '^p' || true)"
  [ "$n" = 1 ] || die 2 "$label: port $port expected exactly 1 listener, found $n"
  printf '%s\n' "$rows" | grep -qx "u$(id -u)" \
    || die 2 "$label: port $port listener not owned by $(id -un)"
  pid="$(printf '%s\n' "$rows" | sed -n 's/^p//p' | head -1)"
  ps -o command= -p "$pid" | grep -q "$pattern" \
    || die 2 "$label: port $port listener is not $pattern"
}

# The spike profile talks to the local image stub instead of the tap: the tap is
# neither required nor used, so it is checked only in the normal profile.
if [ "$PROFILE" = spike ]; then
  assert_owned_listener 18003 'image_stub\.py' "spike image stub"
  log "spike profile: image upstream replaced by the stub on 127.0.0.1:18003 (no real OpenAI call)"
fi

if [ "$NO_TAP" -eq 0 ] && [ "$PROFILE" = normal ]; then
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
  uv run --no-project --python 3.12 python - "$TAP_JSON" "$tap_pid" "${OPENAI_BASE_URL%/v1}" <<'PY' \
    || die 2 "tap not verified: tap.json upstream or pid mismatch"
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
assert d.get("upstream") == sys.argv[3], "upstream"   # origin of OPENAI_BASE_URL (01 DEC-9)
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
RENDER_ARGS=(--profile "$PROFILE")
if [ "$PROFILE" = spike ]; then
  # Render beside the live config, then swap it in with a backup so that every
  # exit path can restore the file MPT's 8080 instance was started with.
  RENDER_ARGS+=(--out "$ROOT/config.spike.toml" --state "$NTS_HOME/mpt-config.spike.last.redacted.toml")
fi
[ -n "$TEMPLATE" ] && RENDER_ARGS+=(--template "$TEMPLATE")
[ "$NO_TAP" -eq 1 ] && RENDER_ARGS+=(--allow-direct-openai)
rc=0
NTS_MPT_API_KEY="$K_MPT" NTS_OPENAI_STORY_KEY="$K_STORY" \
NTS_OPENAI_IMAGE_KEY="$K_IMAGE" NTS_PROXY_API_KEY="$K_PROXY" \
  uv run --no-project --python 3.12 "$RENDERER" "${RENDER_ARGS[@]+"${RENDER_ARGS[@]}"}" || rc=$?
unset K_MPT K_STORY K_IMAGE K_PROXY
[ "$rc" -eq 0 ] || exit 1

restore_config() {
  # Restore the config.toml the live 8080 instance was started with, then drop
  # the spike file so the next start cannot silently reuse a stale one. When the
  # root had no config.toml at all, the spike render itself is removed: leaving
  # it as config.toml would look like a normal render and the next spike run
  # would adopt it as its "previous" config (03.4.1-T1).
  if [ -e "$ROOT/config.toml.pre-spike" ]; then
    mv -f "$ROOT/config.toml.pre-spike" "$ROOT/config.toml" || log "FAIL could not restore config.toml from config.toml.pre-spike"
    log "restored config.toml (spike swap undone)"
  elif [ "$SPIKE_SWAPPED" -eq 1 ]; then
    rm -f "$ROOT/config.toml" || log "FAIL could not remove the spike config.toml (no previous file existed)"
    log "removed the spike config.toml (the root had none before the spike)"
  fi
  rm -f "$ROOT/config.spike.toml"
  SPIKE_SWAPPED=0
}

if [ "$PROFILE" = spike ]; then
  [ -f "$ROOT/config.spike.toml" ] || die 2 "spike: renderer wrote no $ROOT/config.spike.toml"
  # Register the restore before the first destructive move: an abort between the
  # two mv calls must still undo the swap (03.4.1-T1).
  trap 'restore_config' EXIT
  [ -e "$ROOT/config.toml" ] && mv -f "$ROOT/config.toml" "$ROOT/config.toml.pre-spike"
  if ! mv -f "$ROOT/config.spike.toml" "$ROOT/config.toml"; then
    restore_config
    die 2 "spike: could not swap config.spike.toml into place"
  fi
  # The unswapped copy is regenerated on demand, so remove it now.
  SPIKE_SWAPPED=1
  if [ -e "$ROOT/config.toml.pre-spike" ]; then
    log "spike profile: config.toml is now the stub config (previous file kept as config.toml.pre-spike)"
  else
    log "spike profile: config.toml is now the stub config (the root had no config.toml to keep)"
  fi
fi

# --no-tap additionally requires the rendered file to bypass the tap entirely.
if [ "$NO_TAP" -eq 1 ]; then
  [ "$OPENAI_IMAGE_API" = responses ] && log "WARN --no-tap: MPT image calls go straight to the relay's /images/generations, which the tap would translate (OPENAI_IMAGE_API=responses, 01 DEC-11); expect image failures"
  uv run --no-project --python 3.12 python - "$ROOT/config.toml" "$OPENAI_BASE_URL" <<'PY' \
    || die 2 "--no-tap: rendered config does not point at $OPENAI_BASE_URL"
import sys, tomllib
cfg = tomllib.loads(open(sys.argv[1], encoding="utf-8").read())
app = cfg["app"]
assert app["openai_base_url"] == sys.argv[2], "openai_base_url"
assert app["openai_image_base_url"] == sys.argv[2], "openai_image_base_url"
PY
fi

# 6. Refuse unless the rendered file is present and valid (MPT would copy the example → auth
#    off + 0.0.0.0, F07). In the spike profile the swapped-in config.toml is checked against
#    the spike guards, so a half-applied swap can never start MPT.
CHECK_ARGS=(--check --out "$ROOT/config.toml" --profile "$PROFILE")
[ "$NO_TAP" -eq 1 ] && CHECK_ARGS+=(--allow-direct-openai)
if [ ! -f "$ROOT/config.toml" ] || [ -L "$ROOT/config.toml" ] \
  || ! uv run --no-project --python 3.12 "$RENDERER" "${CHECK_ARGS[@]}"; then
  log "refusing to start: config.toml missing/invalid (MPT would copy config.example.toml → auth off, 0.0.0.0; F07)"
  exit 1
fi

# 7. Launch in the foreground with a teed log. Secrets live only in config.toml; the child
#    environment is rebuilt with builtins (never `env -i` with a value: it would sit in env(1)'s
#    argv until exec).
LOG="$NTS_HOME/logs/$LOG_TAG-$(date +%Y%m%d-%H%M%S).log"
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

# Stop cleanly on Ctrl-C / TERM: kill the wrapper and whatever holds the port.
cleanup() {
  kill -TERM "$SRV" 2>/dev/null || true
  lsof -t -iTCP:"$MPT_PORT" -sTCP:LISTEN 2>/dev/null | xargs kill 2>/dev/null || true
  wait "$SRV" 2>/dev/null || true
}
trap 'cleanup; exit 0' INT TERM

# 8. Post-start assertions: listener within 60 s, loopback binds, auth enabled.
T0=$SECONDS
while :; do
  if lsof -nP -iTCP:"$MPT_PORT" -sTCP:LISTEN -t >/dev/null 2>&1; then break; fi
  if ! kill -0 "$SRV" 2>/dev/null; then
    log "server exited during start; last log lines:"
    tail -30 "$LOG" >&2 || true
    exit 1
  fi
  if [ $((SECONDS - T0)) -ge 60 ]; then
    log "FAIL no listener on $MPT_PORT after 60 s"
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
lsof -nP -iTCP:"$MPT_PORT" -sTCP:LISTEN -Fn | grep -qx "n127.0.0.1:$MPT_PORT" \
  || fail_start "$MPT_PORT is not bound to 127.0.0.1"
grep -q 'API key authentication is disabled' "$LOG" \
  && fail_start "API key authentication is disabled (empty api_key, M11)"

echo "mpt-api ready on 127.0.0.1:$MPT_PORT profile=$PROFILE log=$LOG"

# Foreground until the server exits.
rc=0
wait "$SRV" || rc=$?
[ "$rc" -eq 0 ] || exit 1
