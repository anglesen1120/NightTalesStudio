#!/usr/bin/env bash
# worker-node/bin/smoke-proxy.sh — story-level checks against the running VieNeu proxy
# (04.1.5-T1). Row ids are the acceptance contract. The key is only ever sent through a
# 0600 header file in $TMP (`curl -H @file`), never argv, and is deleted with $TMP.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/smoke-proxy.sh

Checks the proxy on http://127.0.0.1:8001 (start it with worker-node/bin/start-proxy.sh).
Lines: PASS <id> · FAIL <id>: <detail> · INFO <id>: <value>.
Exit codes: 0 all pass · 1 a check failed · 2 environment error (missing tool/key/config).
EOF
}
case "${1:-}" in
  "") ;;
  --help|-h) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac

BASE=http://127.0.0.1:8001
NTS_HOME="$(nts_home)"
for t in curl lsof ffprobe ffmpeg openssl uv xxd awk sed; do
  command -v "$t" >/dev/null 2>&1 || die 2 "tool missing: $t"
done
PROXY_API_KEY="$(secret nts.proxy.api_key)"
[ "${#PROXY_API_KEY}" -ge 32 ] || { unset PROXY_API_KEY; die 2 "nts.proxy.api_key too short"; }
# Second key for the log-leak row only; kept unexported so it never reaches child processes.
VIENEU_API_KEY="$(secret nts.vieneu.api_key)"
[ -n "$VIENEU_API_KEY" ] || { unset PROXY_API_KEY VIENEU_API_KEY; die 2 "nts.vieneu.api_key empty"; }
# Config/env preflight (venv + Settings + catalog); does not bind the port.
"$NTS_REPO_ROOT/worker-node/bin/start-proxy.sh" --check 2>/dev/null \
  || die 2 "start-proxy.sh --check failed (venv/config/catalog)"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"; unset PROXY_API_KEY VIENEU_API_KEY' EXIT
# Fresh auth header file, 0600, deleted by the trap; the key never enters argv (01 D3/SEC-08).
AUTH="$TMP/auth.hdr"
( umask 077; printf 'Authorization: Bearer %s\n' "$PROXY_API_KEY" >"$AUTH" )
acurl() { curl -s -H "@$AUTH" "$@"; }
wrong_hdr() { printf 'Authorization: Bearer %s\n' "wrong-$(openssl rand -hex 16)"; }
# JSON checks without jq (01 D9): stdlib Python via uv; the expression gets the parsed object as `d`.
pyjson() { uv run --quiet --no-project --python 3.12 python -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if ($1) else 1)"; }
pyget()  { uv run --quiet --no-project --python 3.12 python -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

rc=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1: $2"; rc=1; }
info() { echo "INFO $1: $2"; }

# health_200_schema — GET /health needs no auth (D8), 200 with schema proxy-health.v1 and reasons [].
check_health() {
  local code
  code="$(curl -s --max-time 10 -o "$TMP/health.json" -w '%{http_code}' "$BASE/health" || true)"
  if [ "$code" = 200 ] \
    && pyjson 'd.get("schema")=="proxy-health.v1" and d.get("reasons")==[]' <"$TMP/health.json"; then
    pass health_200_schema
  else
    fail health_200_schema "code=$code"
  fi
  info health "$(cat "$TMP/health.json" 2>/dev/null)"
}

# speech_no_key_401 / speech_wrong_key_401 (D2 auth on the speech route).
check_auth_required() {
  local code
  code="$(curl -s --max-time 10 -o /dev/null -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{}' "$BASE/v1/audio/speech" || true)"
  if [ "$code" = 401 ]; then pass speech_no_key_401; else fail speech_no_key_401 "code=$code"; fi
  code="$(curl -s --max-time 10 -o /dev/null -w '%{http_code}' -H @<(wrong_hdr) \
    -H 'Content-Type: application/json' -d '{}' "$BASE/v1/audio/speech" || true)"
  if [ "$code" = 401 ]; then pass speech_wrong_key_401; else fail speech_wrong_key_401 "code=$code"; fi
}

# no_route_/docs · no_route_/openapi.json — docs surface is off; unknown_path_404 on an arbitrary path.
check_no_routes() {
  local p code id
  for p in /docs /openapi.json /definitely-not-a-route; do
    code="$(curl -s --max-time 10 -o /dev/null -w '%{http_code}' "$BASE$p" || true)"
    case "$p" in
      /definitely-not-a-route) id=unknown_path_404 ;;
      *) id="no_route_$p" ;;
    esac
    if [ "$code" = 404 ]; then pass "$id"; else fail "$id" "code=$code"; fi
  done
}

# voices_post_405 — the voices route is GET-only (D2).
check_voices_405() {
  local code
  code="$(acurl --max-time 10 -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/audio/voices" || true)"
  if [ "$code" = 405 ]; then pass voices_post_405; else fail voices_post_405 "code=$code"; fi
}

# voices_nonempty — D9: GET /v1/audio/voices returns the active allow-list (revision 0: 2 entries).
check_voices_nonempty() {
  acurl --max-time 10 "$BASE/v1/audio/voices" -o "$TMP/voices.json" || true
  if [ -s "$TMP/voices.json" ] && pyjson 'len(d.get("voices") or [])>0' <"$TMP/voices.json"; then
    pass voices_nonempty
  else
    fail voices_nonempty "empty or invalid list"
  fi
}

# unknown_voice_400_code — any id outside the allow-list → 400 voice_not_in_catalog (D6).
check_unknown_voice_400() {
  local code
  code="$(acurl --max-time 10 -o "$TMP/unknown.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' \
    -d '{"input":"xin chào","voice":"__no_such_voice__"}' "$BASE/v1/audio/speech" || true)"
  if [ "$code" = 400 ] \
    && pyjson 'd.get("error",{}).get("code")=="voice_not_in_catalog"' <"$TMP/unknown.json"; then
    pass unknown_voice_400_code
  else
    fail unknown_voice_400_code "code=$code"
  fi
}

# mp3_status_200 · mp3_frame_sync · mp3_duration · log_line_has_d7_fields ·
# log_line_no_input_text · log_no_key_leak — one real short speech request.
check_mp3_and_log() {
  local marker voice body head dur code ctype logf last
  marker="smokemarker$(openssl rand -hex 4)"
  voice=""
  if [ -s "$TMP/voices.json" ]; then
    voice="$(pyget 'd["voices"][0]["id"]' <"$TMP/voices.json" 2>/dev/null || true)"
  fi
  if [ -z "$voice" ]; then
    for id in mp3_status_200 mp3_frame_sync mp3_duration log_line_has_d7_fields \
              log_line_no_input_text log_no_key_leak; do
      fail "$id" "no voice id from /v1/audio/voices"
    done
    return
  fi
  body="$(VOICE="$voice" MARKER="$marker" uv run --quiet --no-project --python 3.12 python -c \
    'import json,os; print(json.dumps({"input": "Đêm nay trời trong, gió nhẹ. " + os.environ["MARKER"], "voice": os.environ["VOICE"]}, ensure_ascii=False))')"
  code="$(acurl --max-time 120 -H 'Content-Type: application/json' --data-binary "$body" \
    -D "$TMP/smoke.hdrs" -o "$TMP/smoke.mp3" -w '%{http_code}' "$BASE/v1/audio/speech" || true)"
  ctype="$(tr -d '\r' <"$TMP/smoke.hdrs" | awk 'tolower($0) ~ /^content-type:/ {print $2; exit}')"
  if [ "$code" = 200 ] && [ "$ctype" = "audio/mpeg" ]; then
    pass mp3_status_200
  else
    fail mp3_status_200 "code=$code ctype=$ctype"
  fi
  head="$(xxd -p -l2 "$TMP/smoke.mp3" 2>/dev/null || true)"
  # MPEG-1 Layer III fffb expected (-ar 48000); fff3 (MPEG-2) accepted defensively.
  if [ "$head" = "fffb" ] || [ "$head" = "fff3" ]; then
    pass mp3_frame_sync
  else
    fail mp3_frame_sync "head=$head"
  fi
  dur="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$TMP/smoke.mp3" 2>/dev/null || true)"
  if awk -v d="$dur" 'BEGIN{exit !(d>0.5)}'; then
    pass mp3_duration
  else
    fail mp3_duration "dur=$dur"
  fi
  sleep 1   # the D7 line is written after the response ends
  logf="$(find "$NTS_HOME/logs" -maxdepth 1 -name 'proxy-2*.log' -type f 2>/dev/null | sort | tail -n 1 || true)"
  if [ -z "$logf" ]; then
    for id in log_line_has_d7_fields log_line_no_input_text log_no_key_leak; do
      fail "$id" "no $NTS_HOME/logs/proxy-*.log"
    done
    return
  fi
  last="$(grep -F '"route":"/v1/audio/speech"' "$logf" 2>/dev/null | tail -1 || true)"
  if printf '%s' "$last" | pyjson \
    'all(k in d for k in ("ts","rid","route","voice","chars","audio_seconds","vieneu_ms","ffmpeg_ms","ttfb_ms","max_gap_ms","status","http_status")) and d["status"]==200'; then
    pass log_line_has_d7_fields
  else
    fail log_line_has_d7_fields "fields/status (logf=$logf)"
  fi
  if grep -qF "$marker" "$logf" 2>/dev/null; then
    fail log_line_no_input_text "marker found in log"
  else
    pass log_line_no_input_text
  fi
  # SEC-08: both key patterns come from the fd, never argv.
  local hits
  hits="$(grep -cFf <(printf '%s\n%s\n' "$PROXY_API_KEY" "$VIENEU_API_KEY") "$logf" 2>/dev/null || true)"
  if [ "$hits" = 0 ]; then pass log_no_key_leak; else fail log_no_key_leak "hits=$hits"; fi
}

# listener_entry_point (04 CR-6: cmdline contains nts_vieneu_proxy) · listener_loopback_only (D1).
check_listener() {
  local rows pid cmd bad
  rows="$(lsof -a -nP -iTCP:8001 -sTCP:LISTEN -Fpn 2>/dev/null || true)"
  pid="$(printf '%s\n' "$rows" | sed -n 's/^p//p' | head -1)"
  cmd="$(ps -o command= -p "$pid" 2>/dev/null || true)"
  if [[ "$cmd" == *"-m nts_vieneu_proxy"* ]]; then
    pass listener_entry_point
  else
    fail listener_entry_point "cmd=${cmd:0:80}"
  fi
  bad="$(printf '%s\n' "$rows" | sed -n 's/^n//p' \
    | grep -vE '^(127\.0\.0\.1|\[::1\]):8001$' || true)"
  if [ -n "$rows" ] && [ -z "$bad" ]; then
    pass listener_loopback_only
  elif [ -n "$bad" ]; then
    fail listener_loopback_only "non-loopback listener: $(printf '%s' "$bad" | tr '\n' ' ')"
  else
    fail listener_loopback_only "no listener on 8001"
  fi
}

check_health
check_auth_required
check_no_routes
check_voices_405
check_voices_nonempty
check_unknown_voice_400
check_mp3_and_log
check_listener
unset PROXY_API_KEY VIENEU_API_KEY
exit "$rc"
