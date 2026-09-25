#!/usr/bin/env bash
# worker-node/bin/start-vieneu.sh — start VieNeu in the foreground on 127.0.0.1:8000 (01.2.1-T2, FR-01-1).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/start-vieneu.sh [--check] [--verify-weights] [--ready-timeout <s>]
                                       [--env-file <path>] [--key-service <name>]   (last two: guard tests only)

Starts VieNeu (apps.openai_speech) in the foreground: 127.0.0.1:8000, ONNX fp32, 1 stream, watermark on,
weights offline (HF_HUB_OFFLINE=1), key from Keychain nts.vieneu.api_key (child env only, never argv).
Prints "READY vieneu http://127.0.0.1:8000 after <n>s" when /health returns 200. Stop with Ctrl-C.

  --check           preflight only (env file, guards, pin, weights refs, key); works while the server runs
  --verify-weights  also run fetch-vieneu-weights.sh --verify (sha256 of every weight file)
  --ready-timeout   seconds to wait for /health (default 120)

Env file: worker-node/vieneu/env.example · Logs: $NTS_HOME/logs/vieneu-<YYYYmmdd-HHMMSS>.log
Exit codes: 0 ok / stopped by Ctrl-C · 1 not ready, server exited or failed · 2 usage/environment/guard.
EOF
}

CHECK=0 VERIFY=0 TIMEOUT="${VIENEU_READY_TIMEOUT:-120}"
ENV_FILE="$NTS_REPO_ROOT/worker-node/vieneu/env.example"
KEY_SERVICE=nts.vieneu.api_key
while [ $# -gt 0 ]; do
  case "$1" in
    --help|-h) usage; exit 0 ;;
    --check) CHECK=1 ;;
    --verify-weights) VERIFY=1 ;;
    --ready-timeout) [ $# -ge 2 ] || die 2 "--ready-timeout needs a value"; TIMEOUT="$2"; shift ;;
    --env-file) [ $# -ge 2 ] || die 2 "--env-file needs a value"; ENV_FILE="$2"; shift ;;
    --key-service) [ $# -ge 2 ] || die 2 "--key-service needs a value"; KEY_SERVICE="$2"; shift ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done
[[ "$TIMEOUT" =~ ^[0-9]+$ ]] || die 2 "--ready-timeout must be an integer"

# a. Paths and pins.
NTS_HOME="$(nts_home)"
load_pins

# b. Parse the env file (never sourced).
ALLOWED=(HOST PORT VIENEU_BACKEND VIENEU_DEVICE VIENEU_PRECISION VIENEU_MAX_STREAMS VIENEU_WATERMARK
  HF_HOME HF_HUB_OFFLINE HF_HUB_DISABLE_TELEMETRY)
[ -f "$ENV_FILE" ] || die 2 "env file missing: $ENV_FILE"
CHILD_ENV=()
while IFS= read -r line || [ -n "$line" ]; do
  case "$line" in ''|'#'*) continue ;; esac
  key="${line%%=*}"
  [ "$key" = VIENEU_API_KEY ] && die 2 "secret in env.example"
  [[ "$line" =~ ^[A-Z][A-Z0-9_]*=[^\'\"\`\ ]*$ ]] || die 2 "env file: malformed line for $key"
  _nts_in_list "$key" "${ALLOWED[@]}" || die 2 "env file: key not allowed: $key"
  val="${line#*=}"
  val="${val//\$\{NTS_HOME\}/$NTS_HOME}"
  printf -v "E_$key" '%s' "$val"
  CHILD_ENV+=("$key=$val")
done <"$ENV_FILE"

# c. Guards (D4, D11, F09). Caller environment is never passed through (env -i below).
guard() { local k="$1" want="$2" v; v="$(eval "printf '%s' \"\${E_$k:-}\"")"; [ "$v" = "$want" ] || die 2 "guard: $k=$v, expected $want"; }
guard HOST 127.0.0.1
guard PORT 8000
guard VIENEU_BACKEND onnx
guard VIENEU_PRECISION fp32
guard VIENEU_MAX_STREAMS 1
guard VIENEU_WATERMARK 1
guard HF_HUB_OFFLINE 1
guard HF_HOME "$NTS_HOME/hf"

# d. Clone at pin.
SRC="$NTS_HOME/src/vieneu"
[ -e "$SRC/.git" ] && [ "$(git -C "$SRC" rev-parse HEAD 2>/dev/null)" = "$VIENEU_COMMIT" ] \
  || die 2 "VieNeu clone is not at VIENEU_COMMIT: run bootstrap.sh vieneu"

# e. Weights at pin.
for pair in "$VIENEU_MODEL_REPO=$VIENEU_MODEL_REVISION" "$VIENEU_CODEC_REPO=$VIENEU_CODEC_REVISION"; do
  repo="${pair%%=*}"; rev="${pair#*=}"
  ref="$NTS_HOME/hf/hub/models--${repo//\//--}/refs/main"
  [ -s "$ref" ] && [ "$(cat "$ref")" = "$rev" ] || die 2 "weights for $repo not at the pin: run fetch-vieneu-weights.sh"
done
if [ "$VERIFY" -eq 1 ]; then
  "$NTS_REPO_ROOT/worker-node/bin/fetch-vieneu-weights.sh" --verify >&2 || die 2 "weights verify failed"
fi

# f. Port (not in --check) and key.
[ "$CHECK" -eq 1 ] || require_port_free 8000
if [ "$KEY_SERVICE" != nts.vieneu.api_key ]; then NTS_TEST_SERVICE="$KEY_SERVICE"; fi
KEY="$(secret "$KEY_SERVICE")" || die 2 "no usable key in Keychain $KEY_SERVICE: run secrets.sh init-local (and check the D13 trusted location)"
[ "${#KEY}" -ge 32 ] || { unset KEY; die 2 "key in $KEY_SERVICE too short: run secrets.sh rotate-local"; }
if [ "$CHECK" -eq 1 ]; then
  unset KEY
  log "check ok"
  exit 0
fi

# g. Start (key only in the child env).
mkdir -p "$NTS_HOME/logs"
LOG="$NTS_HOME/logs/vieneu-$(date +%Y%m%d-%H%M%S).log"
log "log: $LOG"
cd "$SRC"
KEEP_PATH="$PATH" KEEP_HOME="$HOME" KEEP_USER="$USER"   # plain (non-exported) copies survive the unset loop
# Build the child env with builtins only (review finding: `env -i … KEY=… cmd` puts the key in the
# argv of /usr/bin/env until it execs). The subshell drops every exported variable, exports the
# allow-listed ones plus the key, then execs uv, so the key never appears in any argv.
(
  while IFS= read -r n; do unset "$n" 2>/dev/null || true; done < <(compgen -e)
  export PATH="$KEEP_PATH" HOME="$KEEP_HOME" USER="$KEEP_USER" LANG=en_US.UTF-8
  for kv in "${CHILD_ENV[@]}"; do export "${kv?}"; done
  export VIENEU_API_KEY="$KEY"
  exec uv run --no-sync python -m apps.openai_speech
) > >(tee -a "$LOG") 2>&1 &
SRV=$!
unset KEY

# h. Stop cleanly on Ctrl-C.
trap 'kill -TERM "$SRV" 2>/dev/null || true; wait "$SRV" 2>/dev/null || true; exit 0' INT TERM

# i. Readiness.
T0=$SECONDS
while :; do
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 http://127.0.0.1:8000/health || true)"
  if [ "$code" = 200 ]; then
    echo "READY vieneu http://127.0.0.1:8000 after $((SECONDS - T0))s" | tee -a "$LOG"
    break
  fi
  if ! kill -0 "$SRV" 2>/dev/null; then log "server exited"; exit 1; fi
  if [ $((SECONDS - T0)) -ge "$TIMEOUT" ]; then
    echo "NOT READY after $((SECONDS - T0))s" | tee -a "$LOG"
    kill -TERM "$SRV" 2>/dev/null || true
    exit 1
  fi
  sleep 1
done

# j. Foreground until the server exits.
rc=0
wait "$SRV" || rc=$?
[ "$rc" -eq 0 ] || exit 1
