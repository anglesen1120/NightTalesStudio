#!/usr/bin/env bash
# trial/tools/start_run.sh — start a measured run (01.3.1-T3, plan 01).
# Validates args and services, writes run.json, records $NTS_HOME/current_run
# and launches the RSS sampler. Prints ONLY the run id on stdout:
#   RUN_ID="$(trial/tools/start_run.sh vi landscape "topic" 5)"
# Human messages go to stderr via log. Bash 3.2 compatible. Exit codes:
# 0 ok · 1 check failed · 2 usage/environment (D9).
set -euo pipefail

# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/../../worker-node/bin/lib.sh"

usage() {
  cat <<'EOF'
Usage:
  start_run.sh <lang> <aspect> "<topic>" <target_minutes> [--status calibration|trial] [--voice <name>] [--story-source mpt|plan02] [--no-tap]
  start_run.sh --help

  lang            vi | en
  aspect          landscape | portrait
  topic           1-200 chars, no newline
  target_minutes  decimal 0.5-30
  --status        calibration | trial   (default trial)
  --voice         kokoro voice name; stored as "kokoro:<name>" (default n/a:not-given)
  --story-source  mpt | plan02          (default mpt)
  --no-tap        skip usage-tap checks; assert MPT config does NOT point at 8002

Checks (in order): one active run (I3) · listeners on 8000/8001/8080 (+8002
unless --no-tap; the 8002 listener pid must equal the pid in $NTS_HOME/tap.json,
NTS01-SEC-04) · pins.env loads and $NTS_HOME/src/{mpt,vieneu} HEADs equal
MPT_COMMIT / VIENEU_COMMIT · MPT config.toml points at the tap
(run_manifest.py --check-tap).
Then: run id (D10, local clock), trial/runs/<id> + $NTS_HOME/runs/<id>, run.json,
atomic current_run, rss_sampler.sh (rolled back if no pid file within 3 s).
stdout: the run id only. Exit codes: 0 ok, 1 check failed, 2 usage/env error.
EOF
}

# ---- A1: args ----------------------------------------------------------------
case "${1:-}" in --help|-h) usage; exit 0 ;; esac
STATUS=trial; VOICE=""; VOICE_GIVEN=0; STORY_SOURCE=mpt; NO_TAP=0
POS=()
while (( $# )); do
  case "$1" in
    --status)       STATUS=${2:-}; shift 2 || die 2 "--status needs a value" ;;
    --voice)        VOICE=${2:-}; VOICE_GIVEN=1; shift 2 || die 2 "--voice needs a value" ;;
    --story-source) STORY_SOURCE=${2:-}; shift 2 || die 2 "--story-source needs a value" ;;
    --no-tap)       NO_TAP=1; shift ;;
    --*)            die 2 "unknown option $1 (see --help)" ;;
    *)              POS+=("$1"); shift ;;
  esac
done
[ "${#POS[@]}" -eq 4 ] || { usage >&2; exit 2; }
RLANG=${POS[0]}; ASPECT=${POS[1]}; TOPIC=${POS[2]}; TMIN=${POS[3]}
[ "$RLANG" = vi ] || [ "$RLANG" = en ] || die 2 "lang must be vi or en, got '$RLANG'"
[ "$ASPECT" = landscape ] || [ "$ASPECT" = portrait ] || die 2 "aspect must be landscape or portrait, got '$ASPECT'"

case "$TOPIC" in
  *$'\n'*|*$'\r'*) die 2 "topic must not contain a newline" ;;
esac
[ "${#TOPIC}" -ge 1 ] && [ "${#TOPIC}" -le 200 ] || die 2 "topic must be 1-200 chars, got ${#TOPIC}"
[[ $TMIN =~ ^[0-9]+(\.[0-9]+)?$ ]] || die 2 "target_minutes must be a decimal number, got '$TMIN'"
awk -v v="$TMIN" 'BEGIN{exit !(v>=0.5 && v<=30)}' || die 2 "target_minutes must be 0.5-30, got '$TMIN'"
[ "$STATUS" = calibration ] || [ "$STATUS" = trial ] || die 2 "--status must be calibration or trial, got '$STATUS'"
[ "$STORY_SOURCE" = mpt ] || [ "$STORY_SOURCE" = plan02 ] || die 2 "--story-source must be mpt or plan02, got '$STORY_SOURCE'"
if [ "$VOICE_GIVEN" -eq 1 ]; then
  case "$VOICE" in
    *$'\n'*|*$'\r'*) die 2 "--voice must not contain a newline" ;;
  esac
  # Same rule as run_manifest.normalize_voice: strip repeated kokoro: prefixes
  # (any case); an empty name is a usage error, never n/a:not-given.
  voice_name="$VOICE"
  while :; do
    voice_name="${voice_name#"${voice_name%%[![:space:]]*}"}"
    lower="$(printf '%s' "${voice_name:0:7}" | tr '[:upper:]' '[:lower:]')"
    [ "$lower" = "kokoro:" ] || break
    voice_name="${voice_name:7}"
  done
  [ -n "${voice_name//[[:space:]]/}" ] || die 2 "--voice needs a non-empty voice name"
fi

NTS_HOME="$(nts_home 2>/dev/null)"
TRIAL_RUNS="$NTS_REPO_ROOT/trial/runs"
MANIFEST="$NTS_REPO_ROOT/trial/tools/run_manifest.py"
SAMPLER="$NTS_REPO_ROOT/trial/tools/rss_sampler.sh"

# ---- A2: one active run (I3) -------------------------------------------------
if [ -f "$NTS_HOME/current_run" ]; then
  die 2 "run $(cat "$NTS_HOME/current_run") active; finish or --abort it"
fi

# ---- A3: services up (D4) ----------------------------------------------------
port_pid() { lsof -nP -t -iTCP:"$1" -sTCP:LISTEN 2>/dev/null | head -1 || true; }
for port in 8000 8001 8080; do
  [ -n "$(port_pid "$port")" ] || die 2 "no listener on port $port (D4; start the services)"
done
if [ "$NO_TAP" -eq 0 ]; then
  tap_lsof="$(lsof -nP -t -iTCP:8002 -sTCP:LISTEN 2>/dev/null || true)"
  [ -n "$tap_lsof" ] || die 2 "no listener on port 8002 (D4; start the usage tap or pass --no-tap)"
  [ -f "$NTS_HOME/tap.json" ] || die 2 "port 8002 is not the usage tap: $NTS_HOME/tap.json missing"
  tap_pid="$(sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$NTS_HOME/tap.json" | head -1)"
  [[ $tap_pid =~ ^[0-9]+$ ]] || die 2 "port 8002 is not the usage tap: no numeric pid in $NTS_HOME/tap.json"
  found=0
  while IFS= read -r lp; do
    if [ "$lp" = "$tap_pid" ]; then found=1; fi
  done <<<"$tap_lsof"
  [ "$found" -eq 1 ] || die 2 "port 8002 is not the usage tap (pid $tap_pid in tap.json is not listening; NTS01-SEC-04)"
fi

# ---- A4: pins + heads ---------------------------------------------------------
load_pins
mpt_head="$(git -C "$NTS_HOME/src/mpt" rev-parse HEAD 2>/dev/null)" || die 1 "git rev-parse failed in $NTS_HOME/src/mpt"
[ "$mpt_head" = "$MPT_COMMIT" ] || die 1 "mpt HEAD $mpt_head != pinned MPT_COMMIT $MPT_COMMIT"
vieneu_head="$(git -C "$NTS_HOME/src/vieneu" rev-parse HEAD 2>/dev/null)" || die 1 "git rev-parse failed in $NTS_HOME/src/vieneu"
[ "$vieneu_head" = "$VIENEU_COMMIT" ] || die 1 "vieneu HEAD $vieneu_head != pinned VIENEU_COMMIT $VIENEU_COMMIT"

# ---- A5: config points at the tap ---------------------------------------------
if [ "$NO_TAP" -eq 1 ]; then
  uv run --no-project --python 3.12 "$MANIFEST" --check-tap --no-tap || die 1 "config check failed (--no-tap): config.toml still points at the tap"
else
  uv run --no-project --python 3.12 "$MANIFEST" --check-tap || die 1 "config.toml does not point at the usage tap"
fi

# ---- A6: run id (D10, local clock) ---------------------------------------------
base="$(date +%Y%m%d-%H%M)-$RLANG-$ASPECT"
# nn = 1 + max numeric -NN suffix over BOTH run dirs (tracked and
# $NTS_HOME): counting dirs collides after a rolled-back run (D10).
nn=1
for d in "$TRIAL_RUNS/$base"-*/ "$NTS_HOME/runs/$base"-*/; do
  [ -d "$d" ] || continue
  s="${d%/}"; s="${s##*-}"
  case "$s" in ''|*[!0-9]*) continue ;; esac
  if (( 10#$s >= nn )); then nn=$(( 10#$s + 1 )); fi
done
run_id="$(printf '%s-%02d' "$base" "$nn")"

# ---- A7: run dirs --------------------------------------------------------------
mkdir "$TRIAL_RUNS/$run_id" 2>/dev/null || die 1 "run dir exists or cannot be created: $TRIAL_RUNS/$run_id"
mkdir -p "$NTS_HOME/runs"
if ! mkdir "$NTS_HOME/runs/$run_id" 2>/dev/null; then
  rmdir "$TRIAL_RUNS/$run_id"
  die 1 "run dir exists or cannot be created: $NTS_HOME/runs/$run_id"
fi

# ---- A8: run.json ---------------------------------------------------------------
manifest_args=(start --run-id "$run_id" --lang "$RLANG" --aspect "$ASPECT"
               --topic "$TOPIC" --target-minutes "$TMIN" --status "$STATUS"
               --story-source "$STORY_SOURCE")
if [ -n "$VOICE" ]; then manifest_args+=(--voice "$VOICE"); fi
if [ "$NO_TAP" -eq 1 ]; then manifest_args+=(--no-tap); fi
if ! uv run --no-project --python 3.12 "$MANIFEST" "${manifest_args[@]}"; then
  rm -rf "${TRIAL_RUNS:?}/${run_id:?}" "${NTS_HOME:?}/runs/${run_id:?}"
  die 1 "run_manifest.py start failed; rolled back run dirs"
fi

# ---- A9: atomic current_run ------------------------------------------------------
printf '%s\n' "$run_id" > "$NTS_HOME/current_run.tmp"
mv "$NTS_HOME/current_run.tmp" "$NTS_HOME/current_run"

# ---- A10: sampler (rollback on failure) --------------------------------------------
nohup "$SAMPLER" "$run_id" >"$NTS_HOME/runs/$run_id/rss_sampler.log" 2>&1 &
sampler_nohup_pid=$!
ok=0
for _ in $(seq 1 30); do
  if [ -s "$NTS_HOME/runs/$run_id/rss_sampler.pid" ]; then ok=1; break; fi
  if ! kill -0 "$sampler_nohup_pid" 2>/dev/null; then break; fi   # exited before writing pid
  sleep 0.1
done
if [ "$ok" -ne 1 ]; then
  kill "$sampler_nohup_pid" 2>/dev/null || true
  rm -f "$NTS_HOME/current_run"
  rm -rf "${TRIAL_RUNS:?}/${run_id:?}" "${NTS_HOME:?}/runs/${run_id:?}"
  die 1 "rss_sampler.sh did not create a pid file within 3 s; rolled back run dirs and current_run"
fi

# ---- A11: run id only on stdout -----------------------------------------------------
printf '%s\n' "$run_id"
