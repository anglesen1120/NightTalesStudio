#!/usr/bin/env bash
# worker-node/bin/smoke-vieneu.sh — story-level checks of 01.2.1 against the running VieNeu (01.2.1-T3).
# The key is only ever sent through `-H @<(printf …)` (never argv) and never printed.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/smoke-vieneu.sh [--update-snapshot]
Checks http://127.0.0.1:8000: health (no auth), 401s, models, voices (+ snapshot diff), mp3/unknown voice → 400,
wav vi/en → 200 at 48 kHz, POST /v1/voices unauthenticated → 401, loopback binds, LAN refusal, key not in log.
Lines: PASS <id> · FAIL <id>: <detail> · WARN <id>: <detail> · INFO <id>: <value>
Env NTS_VOICES_SNAPSHOT overrides worker-node/vieneu/voices.snapshot.json (drift self-test only).
Exit codes: 0 all pass (WARN/INFO allowed) · 1 any FAIL · 2 environment (server not running, tool or key missing).
EOF
}

UPDATE=0
case "${1:-}" in
  '') ;;
  --update-snapshot) UPDATE=1 ;;
  --help|-h) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac

BASE=http://127.0.0.1:8000
SNAP="${NTS_VOICES_SNAPSHOT:-$NTS_REPO_ROOT/worker-node/vieneu/voices.snapshot.json}"
NTS_HOME="$(nts_home 2>/dev/null)"
for t in curl lsof ffprobe ffmpeg openssl uv; do command -v "$t" >/dev/null 2>&1 || die 2 "tool missing: $t"; done
"$NTS_REPO_ROOT/worker-node/bin/start-vieneu.sh" --check 2>/dev/null || die 2 "start-vieneu.sh --check failed"
lsof -nP -iTCP:8000 -sTCP:LISTEN -t >/dev/null 2>&1 || die 2 "VieNeu is not listening on 8000: start-vieneu.sh first"
KEY="$(secret nts.vieneu.api_key)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

rc=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1: $2"; rc=1; }
warn() { echo "WARN $1: $2"; }
info() { echo "INFO $1: $2"; }
py() { uv run --quiet --no-project --python 3.12 python -c "$@"; }

auth_hdr() { printf 'Authorization: Bearer %s\n' "$KEY"; }
wrong_hdr() { printf 'Authorization: Bearer %s\n' "wrong-$(openssl rand -hex 16)"; }

# body <text> <voice> <format>
body() {
  B_TEXT="$1" B_VOICE="$2" B_FMT="$3" py 'import json,os
print(json.dumps({"model":"vieneu-v3-turbo","input":os.environ["B_TEXT"],"voice":os.environ["B_VOICE"],"response_format":os.environ["B_FMT"]}, ensure_ascii=False))'
}

# speech <out> <auth: key|none|wrong> <text> <voice> <format> → prints HTTP code
speech() {
  local out="$1" mode="$2"
  case "$mode" in
    key) body "$3" "$4" "$5" | curl -s --max-time 60 -o "$out" -w '%{http_code}' -H @<(auth_hdr) \
           -H 'Content-Type: application/json' --data-binary @- "$BASE/v1/audio/speech" || true ;;
    wrong) body "$3" "$4" "$5" | curl -s --max-time 60 -o "$out" -w '%{http_code}' -H @<(wrong_hdr) \
           -H 'Content-Type: application/json' --data-binary @- "$BASE/v1/audio/speech" || true ;;
    none) body "$3" "$4" "$5" | curl -s --max-time 60 -o "$out" -w '%{http_code}' \
           -H 'Content-Type: application/json' --data-binary @- "$BASE/v1/audio/speech" || true ;;
  esac
}

TEXT_VI='Xin chào, đây là bài kiểm tra.'
TEXT_EN='This is a short test.'

# health-noauth
code="$(curl -s --max-time 5 -o "$TMP/health.json" -w '%{http_code}' "$BASE/health" || true)"
if [ "$code" = 200 ] && py 'import json,sys
d=json.load(open(sys.argv[1]))
need={"status","backend","max_streams","active","waiting","sample_rate"}
sys.exit(0 if need<=set(d) and int(d["max_streams"])==1 and int(d["sample_rate"])==48000 else 1)' "$TMP/health.json"; then
  pass health-noauth
else
  fail health-noauth "status $code body $(head -c 200 "$TMP/health.json" 2>/dev/null)"
fi
info health "$(cat "$TMP/health.json" 2>/dev/null)"

# speech-noauth / speech-wrongkey
code="$(speech "$TMP/noauth.json" none "$TEXT_EN" x wav)"
if [ "$code" = 401 ] && grep -q 'invalid api key' "$TMP/noauth.json"; then pass speech-noauth; else fail speech-noauth "status $code"; fi
code="$(speech "$TMP/wrong.json" wrong "$TEXT_EN" x wav)"
if [ "$code" = 401 ]; then pass speech-wrongkey; else fail speech-wrongkey "status $code"; fi

# models
code="$(curl -s --max-time 5 -o "$TMP/models.json" -w '%{http_code}' -H @<(auth_hdr) "$BASE/v1/models" || true)"
if [ "$code" = 200 ]; then
  pass models
  info models "$(py 'import json,sys; print(" ".join(m.get("id","?") for m in json.load(open(sys.argv[1])).get("data",[])))' "$TMP/models.json" || echo unparsed)"
else
  fail models "status $code"
fi

# voices-list, voices-count, voices-snapshot
code="$(curl -s --max-time 5 -o "$TMP/voices.json" -w '%{http_code}' -H @<(auth_hdr) "$BASE/v1/voices" || true)"
VOICE=""
if [ "$code" = 200 ] && py 'import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if d.get("object")=="list" and isinstance(d.get("data"),list) else 1)' "$TMP/voices.json"; then
  pass voices-list
  n="$(py 'import json,sys; print(len(json.load(open(sys.argv[1]))["data"]))' "$TMP/voices.json")"
  info voices_count "$n"
  if [ "$n" = 25 ]; then pass voices-count; else warn voices-count "$n (expected 25 for SDK 3.8.3)"; fi
  VOICE="$(py 'import json,sys
ids=[v.get("id") for v in json.load(open(sys.argv[1]))["data"]]
print("Hải Đăng" if "Hải Đăng" in ids else ids[0])' "$TMP/voices.json")"
  py 'import json,sys; print(json.dumps(json.load(open(sys.argv[1])), ensure_ascii=False, indent=2, sort_keys=True))' \
    "$TMP/voices.json" >"$TMP/voices.norm.json"
  if [ "$UPDATE" -eq 1 ]; then
    cp "$TMP/voices.norm.json" "$SNAP"
    pass voices-snapshot
    info voices_snapshot "written $SNAP"
  elif [ ! -f "$SNAP" ]; then
    fail voices-snapshot "missing $SNAP (run with --update-snapshot after review)"
  elif cmp -s "$TMP/voices.norm.json" "$SNAP"; then
    pass voices-snapshot
  else
    fail voices-snapshot "drift (run with --update-snapshot after review)"
  fi
else
  fail voices-list "status $code"
fi
code="$(curl -s --max-time 5 -o /dev/null -w '%{http_code}' "$BASE/v1/voices" || true)"
info voices_noauth_status "$code"
[ -n "$VOICE" ] || VOICE="Hải Đăng"

# speech-mp3 / speech-unknown-voice
code="$(speech "$TMP/mp3.out" key "$TEXT_EN" "$VOICE" mp3)"
if [ "$code" = 400 ]; then pass speech-mp3; else fail speech-mp3 "status $code (expected 400)"; fi
code="$(speech "$TMP/unk.out" key "$TEXT_EN" nts-no-such-voice wav)"
if [ "$code" = 400 ]; then pass speech-unknown-voice; else fail speech-unknown-voice "status $code (expected 400)"; fi

# speech-wav-vi / speech-wav-en
wav_check() {
  local id="$1" text="$2" f="$TMP/$1.wav" code probe secs riff
  code="$(speech "$f" key "$text" "$VOICE" wav)"
  if [ "$code" != 200 ]; then fail "$id" "status $code"; return; fi
  if [ "$(head -c 4 "$f")" != RIFF ] || [ "$(dd if="$f" bs=1 skip=8 count=4 2>/dev/null)" != WAVE ]; then fail "$id" "not a RIFF/WAVE file"; return; fi
  riff="$(dd if="$f" bs=1 skip=4 count=4 2>/dev/null | od -An -tx1 | tr -d ' \n')"
  info "${id}_riff_size" "$riff"
  probe="$(ffprobe -v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels -of csv=p=0 "$f" 2>&1 || true)"
  info "${id}_ffprobe" "$probe"
  secs="$(ffmpeg -v error -i "$f" -f s16le -ac 1 -ar 48000 - 2>/dev/null | wc -c | awk '{printf "%.2f", $1/2/48000}')"
  info "${id}_decoded_seconds" "$secs"
  if ! awk -v s="$secs" 'BEGIN{exit !(s>0.5)}'; then fail "$id" "decoded ${secs}s (expected > 0.5 s)"; return; fi
  case "$probe" in
    *48000*) pass "$id" ;;
    '') warn "$id" "ffprobe could not parse the header (04 F3); decode ok" ;;
    *) fail "$id" "sample rate is not 48000: $probe" ;;
  esac
}
wav_check speech-wav-vi "$TEXT_VI"
wav_check speech-wav-en "$TEXT_EN"

# voices-post-auth-probe (SEC-03)
ffmpeg -v error -f lavfi -i anullsrc=r=48000:cl=mono -t 0.5 -c:a pcm_s16le "$TMP/probe.wav"
c1="$(curl -s --max-time 10 -o /dev/null -w '%{http_code}' -F name=nts-smoke-probe -F file=@"$TMP/probe.wav" "$BASE/v1/voices" || true)"
c2="$(curl -s --max-time 10 -o /dev/null -w '%{http_code}' -H @<(wrong_hdr) -F name=nts-smoke-probe -F file=@"$TMP/probe.wav" "$BASE/v1/voices" || true)"
info voices_post_unauth_status "$c1 $c2"
if [ "$c1" = 401 ] && [ "$c2" = 401 ]; then
  pass voices-post-auth-probe
elif [[ "$c1$c2" =~ 2[0-9][0-9] ]]; then
  fail voices-post "clone endpoint open: stop the server, delete voice nts-smoke-probe, escalate (SEC-03)"
else
  fail voices-post "not decisive (validation ran before auth): $c1 $c2"
fi

# bind-loopback
names="$(lsof -nP -iTCP:8000 -sTCP:LISTEN -Fn 2>/dev/null | sed -n 's/^n//p' | sort -u || true)"
bad="$(printf '%s\n' "$names" | grep -vxE '127\.0\.0\.1:8000|\[::1\]:8000' || true)"
if [ -z "$bad" ]; then pass bind-loopback; else fail bind-loopback "listener $(printf '%s' "$bad" | tr '\n' ' ')"; fi

# lan-refused / lan6-refused
IP="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)"
if [ -z "$IP" ]; then
  warn lan-refused "no LAN address"
else
  e=0; curl -s --max-time 3 -o /dev/null "http://$IP:8000/health" || e=$?
  if [ "$e" -eq 7 ]; then pass lan-refused; elif [ "$e" -eq 0 ]; then fail lan-refused "reachable on $IP"; else warn lan-refused "curl exit $e on $IP"; fi
fi
IP6="$(ifconfig en0 2>/dev/null | awk '/inet6 / && $2 !~ /^fe80/ {print $2; exit}' || true)"
if [ -z "$IP6" ]; then
  info lan6-refused "no IPv6"
else
  e=0; curl -g -s --max-time 3 -o /dev/null "http://[$IP6]:8000/health" || e=$?
  if [ "$e" -eq 7 ]; then pass lan6-refused; elif [ "$e" -eq 0 ]; then fail lan6-refused "reachable on [$IP6]"; else warn lan6-refused "curl exit $e"; fi
fi

# log-no-key (SEC-08): the pattern comes from a file descriptor, never argv.
LOG="$(find "$NTS_HOME/logs" -maxdepth 1 -name 'vieneu-2*.log' -type f 2>/dev/null | sort | tail -n 1 || true)"
if [ -z "$LOG" ]; then
  warn log-no-key "no vieneu log found"
else
  hits="$(grep -cFf <(printf '%s\n' "$KEY") "$LOG" || true)"
  if [ "$hits" = 0 ]; then pass log-no-key; else fail log-no-key "count $hits: rotate nts.vieneu.api_key and delete the log"; fi
  if grep -qF 'đây là bài kiểm tra' "$LOG"; then info log_has_input_text yes; else info log_has_input_text no; fi
fi
unset KEY

# binds-all
if [ -x "$NTS_REPO_ROOT/worker-node/bin/check-binds.sh" ]; then
  if "$NTS_REPO_ROOT/worker-node/bin/check-binds.sh" >"$TMP/binds.txt"; then pass binds-all; else fail binds-all "$(grep FAIL "$TMP/binds.txt" | tr '\n' ' ')"; fi
else
  fail binds-all "check-binds.sh missing"
fi

exit "$rc"
