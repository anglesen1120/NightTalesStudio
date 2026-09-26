#!/usr/bin/env bash
# trial/tools/bench-proxy.sh — 04.1.2-T3: MPT-side acceptance and performance baseline.
#
# Proves, with MPT's own code path (voice.tts() -> kokoro_tts ->
# _openai_compatible_tts and MPT's pinned requests + MoviePy, README M1-M3, M9),
# that the running VieNeu proxy delivers complete, decodable MP3 for the short and
# 5-minute vi/en fixtures, measures every request against the direct-VieNeu
# baseline of 01.2.1-T4, and publishes the CR-2 numbers (ttfb_ms, max_gap_ms) plus
# the NFR-04-3 proxy overhead in trial/baselines/proxy-mac-<YYYYMMDD>.json.
#
# --inject-failure additionally proves the failure path (D4/F02, M2, M8): VieNeu is
# killed mid-request, MPT makes three attempts, and the task fails at stage "audio".
# It also runs the two live T15 checks requested by the TL (client disconnect mid-
# stream; the proxy's ffmpeg killed mid-stream -> drain keeps VieNeu's slot until the
# upstream ends). Those are recorded under "t15_live" and are not part of the pass rule.
#
# The script does not start the services (start-vieneu.sh / start-proxy.sh /
# start-mpt-api.sh must already run); the only exception is the VieNeu restart it
# has to perform after the injected kill. Keys come from the Keychain through
# lib.sh `secret` and reach curl only through 0600 header files in a temp dir —
# never argv, never the baseline file.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/../../worker-node/bin/lib.sh"

usage() {
  cat <<'EOF'
Usage: trial/tools/bench-proxy.sh [--repeats N] [--fixtures <list>]
                                   [--inject-failure] [--direct-baseline <file>]

MPT-side acceptance + performance baseline for the VieNeu speech proxy (04.1.2-T3).
Each run calls MPT's `voice.tts()` in MPT's venv against the running proxy; the
proxy's D7 log line of that request supplies ttfb_ms/max_gap_ms/audio_seconds.
Writes trial/baselines/proxy-mac-<YYYYMMDD>.json (metrics only; no text, no keys).

Requires, already running: VieNeu on 127.0.0.1:8000 (start-vieneu.sh) and the proxy
on 127.0.0.1:8001 (start-proxy.sh); with --inject-failure also the MPT API on
127.0.0.1:8080 (start-mpt-api.sh, itself requiring the usage tap).

  --repeats N            counted repeats per fixture (default 2)
  --fixtures <list>      comma-separated subset of vi-1000,en-1000,vi-5min,en-5min
                         (default: all four in that order)
  --inject-failure       prove the failure path and run the live T15 checks; SIGKILLs
                         the VieNeu listener mid-request (SIGTERM would let uvicorn
                         finish the stream) and restarts it at the end
  --direct-baseline <f>  direct-VieNeu baseline JSON used for overhead_pct
                         (default trial/baselines/vieneu-mac-20260925.json, 01.2.1-T4)
  -h, --help             this text

MP3s, curl logs and the raw per-run evidence stay in
$NTS_HOME/runs/bench-proxy/<YYYYmmdd-HHMMSS>/.
Exit codes: 0 pass · 1 a check failed · 2 usage/environment error (D9).
EOF
}

REPEATS=2
FIXLIST=""
INJECT=0
DIRECT_BASELINE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --repeats) [ $# -ge 2 ] || die 2 "--repeats needs a value"; REPEATS="$2"; shift 2 ;;
    --fixtures) [ $# -ge 2 ] || die 2 "--fixtures needs a value"; FIXLIST="$2"; shift 2 ;;
    --inject-failure) INJECT=1; shift ;;
    --direct-baseline) [ $# -ge 2 ] || die 2 "--direct-baseline needs a value"; DIRECT_BASELINE="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die 2 "unknown argument: $1 (try --help)" ;;
  esac
done
[[ "$REPEATS" =~ ^[0-9]+$ ]] && [ "$REPEATS" -ge 1 ] || die 2 "--repeats must be a positive integer"

### 0. Paths, pins, interpreter.
REPO="$NTS_REPO_ROOT"
NTS_HOME="$(nts_home)"
load_pins
FIXDIR="$REPO/trial/fixtures"
BASEDIR="$REPO/trial/baselines"
HELPER="$REPO/trial/tools/bench_proxy_mpt.py"
DECODE="$REPO/worker-node/proxy/tools/mpt_decode_check.py"
START_VIENEU="$REPO/worker-node/bin/start-vieneu.sh"
VIENEU_BASE="http://127.0.0.1:8000"
PROXY_BASE="http://127.0.0.1:8001"
MPT_ROOT="$NTS_HOME/src/mpt"
MPT_PY="$MPT_ROOT/.venv/bin/python"
MPT_BASE="http://127.0.0.1:8080/api/v1"
DEFAULT_FIXTURES="vi-1000,en-1000,vi-5min,en-5min"
[ -n "$DIRECT_BASELINE" ] || DIRECT_BASELINE="$BASEDIR/vieneu-mac-20260925.json"

# PY <args…> — repo convention (01 D9): stdlib-only helpers run on 3.12 via uv.
PY() { uv run --quiet --no-project --python 3.12 python "$@"; }

# jget <json-file> <python-expr> — print one value out of a JSON object; the
# expression sees it as `d`. Used only on proxy/MPT JSON we produced ourselves.
jget() {
  PY - "$1" "$2" <<'PYEOF'
import json, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    d = json.load(fh)
v = eval(sys.argv[2], {"__builtins__": {}}, {"d": d})  # noqa: S307 - fixed expressions
print("" if v is None else ("true" if v is True else ("false" if v is False else v)))
PYEOF
}

# newest_log <glob> — newest matching file by mtime ("" when none). Avoids `ls -t`
# so filenames stay safe, and stays inside bash 3.2 (no mapfile, no sort -z).
newest_log() {
  local f newest="" newest_m=0 m
  for f in "$@"; do
    [ -f "$f" ] || continue
    m="$(stat -f %m "$f" 2>/dev/null)"; m="${m:-0}"
    if [ "$m" -gt "$newest_m" ]; then newest="$f"; newest_m="$m"; fi
  done
  printf '%s\n' "$newest"
}

### 1. Preflight (exit 2 = usage/environment).
for t in curl ffmpeg ffprobe lsof uv ps pgrep wc sed; do
  command -v "$t" >/dev/null 2>&1 || die 2 "tool missing: $t"
done
[ -x "$MPT_PY" ] || die 2 "MPT venv missing: $MPT_PY (run worker-node/bin/bootstrap.sh mpt)"
for f in "$HELPER" "$DECODE" "$START_VIENEU" "$REPO/seed/voices.v1.json" "$DIRECT_BASELINE"; do
  [ -f "$f" ] || die 2 "missing file: $f"
done

TMP="$(mktemp -d)"
OUT="$NTS_HOME/runs/bench-proxy/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"
KEY_PROXY=""
KEY_MPT=""
VN_PID=""
# shellcheck disable=SC2329  # invoked indirectly by the EXIT/INT/TERM trap below
cleanup() {
  unset KEY_PROXY KEY_MPT 2>/dev/null || true
  case "${TMP:-}" in /var/folders/*|/tmp/*) rm -rf -- "$TMP" ;; esac
}
trap 'cleanup' EXIT INT TERM

curl -fsS --max-time 5 "$VIENEU_BASE/health" -o "$OUT/vieneu-health-before.json" \
  || die 2 "VieNeu is not healthy on $VIENEU_BASE (worker-node/bin/start-vieneu.sh)"
curl -fsS --max-time 5 "$PROXY_BASE/health" -o "$OUT/proxy-health.json" \
  || die 2 "proxy is not healthy on $PROXY_BASE (worker-node/bin/start-proxy.sh)"
PROXY_PID="$(lsof -nP -iTCP:8001 -sTCP:LISTEN -t 2>/dev/null | head -n 1)"
[ -n "$PROXY_PID" ] || die 2 "no listener on 8001"

# The log we correlate must be the one the *running* proxy writes: start-proxy.sh
# pipes its output through `tee <log>`, so a matching tee process proves it.
PLOG="$(newest_log "$NTS_HOME"/logs/proxy-*.log)"
[ -n "$PLOG" ] || die 2 "no proxy log under $NTS_HOME/logs"
pgrep -f -- "tee $PLOG" >/dev/null 2>&1 \
  || die 2 "newest proxy log $PLOG is not the running proxy's log (start it with start-proxy.sh)"

# MPT config must point the kokoro slot at the proxy (D10, CR-5).
MPT_CFG_TSV="$("$MPT_PY" - "$MPT_ROOT/config.toml" <<'PYEOF'
import sys, tomllib
with open(sys.argv[1], "rb") as fh:
    kokoro = tomllib.load(fh).get("kokoro", {})
print(f'{kokoro.get("base_url", "")}\t{kokoro.get("model_id", "")}')
PYEOF
)"
IFS=$'\t' read -r MPT_CFG_URL MPT_CFG_MODEL <<<"$MPT_CFG_TSV"
[ "$MPT_CFG_URL" = "http://127.0.0.1:8001/v1" ] \
  || die 2 "MPT [kokoro] base_url=$MPT_CFG_URL, expected http://127.0.0.1:8001/v1 (re-render: start-mpt-api.sh)"
[ "$MPT_CFG_MODEL" = "vieneu-v3-turbo" ] \
  || die 2 "MPT [kokoro] model_id=$MPT_CFG_MODEL, expected vieneu-v3-turbo (04 F18/CR-5)"

if [ "$INJECT" -eq 1 ]; then
  lsof -nP -iTCP:8080 -sTCP:LISTEN -t >/dev/null 2>&1 \
    || die 2 "MPT API is not listening on 8080 (worker-node/bin/start-mpt-api.sh)"
  MPT_LOG="$(newest_log "$NTS_HOME"/logs/mpt-api-*.log)"
  [ -n "$MPT_LOG" ] || die 2 "no MPT log under $NTS_HOME/logs"
  pgrep -f -- "tee -a $MPT_LOG" >/dev/null 2>&1 \
    || die 2 "newest MPT log $MPT_LOG is not the running MPT API's log"
  KEY_MPT="$(secret nts.mpt.api_key)"
  MPT_AUTH="$TMP/mpt.hdr"
  ( umask 077; printf 'x-api-key: %s\n' "$KEY_MPT" >"$MPT_AUTH" )
else
  MPT_LOG=""
  MPT_AUTH=""
fi

# Fixtures, voices (from the catalog the proxy serves) and the direct baseline.
[ -n "$FIXLIST" ] || FIXLIST="$DEFAULT_FIXTURES"
IFS=',' read -r -a FIXARR <<<"$FIXLIST"
[ "${#FIXARR[@]}" -ge 1 ] || die 2 "--fixtures is empty"
for fx in "${FIXARR[@]}"; do
  case "$fx" in
    vi-1000|en-1000|vi-5min|en-5min) ;;
    *) die 2 "unknown fixture: $fx (vi-1000,en-1000,vi-5min,en-5min)" ;;
  esac
  [ -f "$FIXDIR/tts-$fx.txt" ] || die 2 "fixture missing: $FIXDIR/tts-$fx.txt"
done

VOICES_TSV="$(PY - "$REPO/seed/voices.v1.json" <<'PYEOF'
import json, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    doc = json.load(fh)
seen = {}
for v in doc["voices"]:
    if v.get("active") and v["language"] not in seen:
        seen[v["language"]] = v["id"]
for lang, vid in sorted(seen.items()):
    print(f"{lang}\t{vid}")
PYEOF
)"
VOICE_VI=""
VOICE_EN=""
while IFS=$'\t' read -r lang vid; do
  case "$lang" in vi) VOICE_VI="$vid" ;; en) VOICE_EN="$vid" ;; esac
done <<<"$VOICES_TSV"
for need in vi en; do
  case "$need" in
    vi) [ -n "$VOICE_VI" ] || die 2 "catalog has no active vi voice" ;;
    en) [ -n "$VOICE_EN" ] || die 2 "catalog has no active en voice" ;;
  esac
done

fixture_lang() { case "$1" in vi-*) printf 'vi\n' ;; en-*) printf 'en\n' ;; esac; }
fixture_voice() { case "$(fixture_lang "$1")" in vi) printf '%s\n' "$VOICE_VI" ;; en) printf '%s\n' "$VOICE_EN" ;; esac; }

DIRECT_TSV="$(PY - "$DIRECT_BASELINE" <<'PYEOF'
import json, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    doc = json.load(fh)
for name, summary in doc["summary"].items():
    print(f"{name}\t{int(round(summary['wall_s_median'] * 1000))}\t{summary['audio_s']}")
PYEOF
)"
direct_ms_for() { # <fixture-file> — exit 2 when the baseline lacks it (never substitute)
  local want="$1" name ms audio
  # shellcheck disable=SC2034  # only the median wall ms is needed; audio_s stays in the TSV
  while IFS=$'\t' read -r name ms audio; do
    [ "$name" = "$want" ] && { printf '%s\n' "$ms"; return 0; }
  done <<<"$DIRECT_TSV"
  return 1
}
direct_audio_for() { # <fixture-file> — direct-VieNeu audio seconds (drain evidence)
  local want="$1" name ms audio
  while IFS=$'\t' read -r name ms audio; do
    [ "$name" = "$want" ] && { printf '%s\n' "$audio"; return 0; }
  done <<<"$DIRECT_TSV"
  return 1
}
for fx in "${FIXARR[@]}"; do
  direct_ms_for "tts-$fx.txt" >/dev/null || die 2 "direct VieNeu baseline $DIRECT_BASELINE has no per-fixture wall time for tts-$fx.txt (exit 2 by design, no substitute)"
done

# Story-level precondition: smoke-proxy.sh must pass (04 README §7 gate 04-M0).
if ! "$REPO/worker-node/bin/smoke-proxy.sh" >"$OUT/smoke-proxy.log" 2>&1; then
  die 2 "smoke-proxy.sh failed (see $OUT/smoke-proxy.log)"
fi

### 2. Helpers for the run loop and the failure/T15 phases.
# speech_count — the number of D7 speech lines in the running proxy's log.
speech_count() {
  local n
  n="$(grep -c '"route":"/v1/audio/speech"' "$PLOG" 2>/dev/null)" || n=0
  printf '%s\n' "${n:-0}"
}

# new_speech_line <want-count> <timeout-s> — print the single new speech log line.
# Returns 1 on timeout, 2 when more than one line appeared (two requests overlapped).
new_speech_line() {
  local want="$1" timeout="$2" waited=0 n
  while [ "$waited" -lt "$timeout" ]; do
    n="$(speech_count)"
    if [ "$n" -gt "$want" ]; then return 2; fi
    if [ "$n" -eq "$want" ]; then
      grep '"route":"/v1/audio/speech"' "$PLOG" | sed -n "${want}p"
      return 0
    fi
    sleep 1
    waited=$((waited + 1))
  done
  return 1
}

# log_fields <line-file> — one space separated line of the fields the bench prints
# (display only; the baseline JSON is built from the raw file).
log_fields() {
  PY - "$1" <<'PYEOF'
import json, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    d = json.load(fh)
keys = ("status", "http_status", "error_code", "chars", "audio_seconds", "ttfb_ms",
        "max_gap_ms", "vieneu_ms", "ffmpeg_ms", "wall_ms", "admission_wait_ms",
        "bytes_out", "rid")
out = []
for k in keys:
    v = d.get(k)
    out.append("-" if v is None else str(v).replace(" ", "_"))
print(" ".join(out))
PYEOF
}

build_body() { # <fixture-file> <voice> <out-json> — MPT-shaped speech body
  PY - "$1" "$2" "$3" <<'PYEOF'
import json, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    text = fh.read()
body = {"model": "vieneu-v3-turbo", "input": text, "voice": sys.argv[2],
        "response_format": "mp3", "speed": 1.0}
with open(sys.argv[3], "w", encoding="utf-8") as fh:
    json.dump(body, fh)
PYEOF
}

proxy_speech_curl() { # <body-file> <out-mp3> <max-time-s> — prints curl's -w line
  curl -sS -H "@$AUTH" -H 'Content-Type: application/json' \
    --max-time "$3" --data-binary "@$1" -o "$2" \
    -w '%{http_code}\t%{time_starttransfer}\t%{time_total}\t%{size_download}\n' \
    "$PROXY_BASE/v1/audio/speech"
}

vieneu_active() {
  curl -s --max-time 5 "$VIENEU_BASE/health" 2>/dev/null \
    | sed -n 's/.*"active"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p'
}

# wait_active <value> <timeout-s> — 0 when /health reported <value>; prints elapsed s.
wait_active() {
  local want="$1" timeout="$2" t=0 a
  while [ "$t" -lt "$timeout" ]; do
    a="$(vieneu_active)"
    [ -n "$a" ] && [ "$a" = "$want" ] && { printf '%s\n' "$t"; return 0; }
    sleep 1
    t=$((t + 1))
  done
  return 1
}

# wait_ffmpeg_child <timeout-s> — print the proxy's ffmpeg child pid.
wait_ffmpeg_child() {
  local timeout="$1" t=0 ff=""
  while [ "$t" -lt "$timeout" ]; do
    ff="$(pgrep -P "$PROXY_PID" -x ffmpeg 2>/dev/null | head -n 1 || true)"
    [ -n "$ff" ] && { printf '%s\n' "$ff"; return 0; }
    sleep 1
    t=$((t + 1))
  done
  return 1
}

kill_vieneu() { # SIGKILL the 8000 listener; 0 when the port is free again
  # SIGKILL, not SIGTERM: uvicorn (no timeout_graceful_shutdown) finishes in-flight
  # responses on SIGTERM, so the 5-min stream would complete as a 200 and the proxy
  # would never report upstream_stream_broken (verified: uvicorn server.py
  # shutdown() -> h11_impl.shutdown() keeps the cycle alive, timeout=None).
  local pids p t=0
  pids="$(lsof -nP -iTCP:8000 -sTCP:LISTEN -t 2>/dev/null || true)"
  [ -n "$pids" ] || return 1
  while IFS= read -r p; do
    [ -n "$p" ] && kill -KILL "$p" 2>/dev/null || true
  done <<<"$pids"
  while [ "$t" -lt 30 ]; do
    lsof -nP -iTCP:8000 -sTCP:LISTEN -t >/dev/null 2>&1 || return 0
    sleep 1
    t=$((t + 1))
  done
  return 1
}

restart_vieneu() { # <tag> — start-vieneu.sh in the background; prints seconds to READY
  local log="$OUT/vieneu-restart-$1.log" t=0
  printf '%s\n' 'start-vieneu.sh (04.1.2-T3 restart after the injected kill)' >"$log"
  "$START_VIENEU" >>"$log" 2>&1 &
  VN_PID=$!
  while [ "$t" -lt 300 ]; do
    if grep -q '^READY vieneu http://127\.0\.0\.1:8000 after ' "$log" 2>/dev/null; then
      printf '%s\n' "$t"
      return 0
    fi
    kill -0 "$VN_PID" 2>/dev/null || return 1
    sleep 1
    t=$((t + 1))
  done
  return 1
}

KEY_PROXY="$(secret nts.proxy.api_key)"
[ "${#KEY_PROXY}" -ge 32 ] || { unset KEY_PROXY; die 2 "nts.proxy.api_key too short"; }
AUTH="$TMP/proxy.hdr"
( umask 077; printf 'Authorization: Bearer %s\n' "$KEY_PROXY" >"$AUTH" )

### 3. Runs: fixture × repeat through MPT's own code path.
: >"$OUT/runs.tsv"
SPEECH_N="$(speech_count)"
for fx in "${FIXARR[@]}"; do
  voice="$(fixture_voice "$fx")"
  r=1
  while [ "$r" -le "$REPEATS" ]; do
    mp3="$OUT/$fx-$r.mp3"
    call_json="$OUT/call-$fx-$r.json"
    decode_json="$OUT/decode-$fx-$r.json"
    line_file="$OUT/logline-$fx-$r.json"
    rc=0
    ( cd "$MPT_ROOT" && exec "$MPT_PY" "$HELPER" "$FIXDIR/tts-$fx.txt" "$voice" "$mp3" ) \
      >"$call_json" 2>"$OUT/call-$fx-$r.err" || rc=$?
    call_rc="$rc"

    want=$((SPEECH_N + 1))
    output_lrc=0
    line="$(new_speech_line "$want" 60)" || output_lrc=$?
    if [ "$output_lrc" -eq 2 ]; then
      die 1 "more than one proxy speech log line appeared for $fx repeat $r (calls overlapped)"
    fi
    if [ "$output_lrc" -eq 0 ]; then
      SPEECH_N=$want
      printf '%s\n' "$line" >"$line_file"
    else
      log "WARN $fx repeat $r: no proxy speech log line within 60s; recorded as a failure"
      printf '%s\n' '{"status":null,"http_status":null,"error_code":"log_line_missing","chars":null,"audio_seconds":null,"ttfb_ms":null,"max_gap_ms":null,"vieneu_ms":null,"ffmpeg_ms":null,"wall_ms":null,"admission_wait_ms":null,"bytes_out":0,"rid":null}' >"$line_file"
    fi

    drc=0
    "$MPT_PY" "$DECODE" "$mp3" >"$decode_json" 2>"$OUT/decode-$fx-$r.err" || drc=$?
    mp3_bytes=0
    [ -f "$mp3" ] && mp3_bytes="$(wc -c <"$mp3" | tr -d ' ')"

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
      "$fx" "$voice" "$r" "$mp3" "$call_json" "$decode_json" "$drc" "$call_rc" "$mp3_bytes" "$line_file" \
      >>"$OUT/runs.tsv"

    read -r st hs ec ch au tt mg vm fm wl aw bo rd < <(log_fields "$line_file")
    log "INFO bench.$fx.$r: ttfb_ms=$tt max_gap_ms=$mg status=$st http_status=$hs error_code=${ec:--} wall_ms=$wl chars=$ch audio_s=$au vieneu_ms=$vm ffmpeg_ms=$fm admission_wait_ms=$aw bytes_out=$bo rid=${rd:--}"
    r=$((r + 1))
  done
done

### 4. Live T15 checks (TL-requested; recorded under t15_live, not part of the pass rule).
T15_FILE="$OUT/t15_live.json"
if [ "$INJECT" -eq 1 ]; then
  # (a) client disconnect mid-stream: the proxy must finish VieNeu's stream anyway.
  build_body "$FIXDIR/tts-vi-1000.txt" "$VOICE_VI" "$OUT/t15-dc-body.json"
  dc_before="$(speech_count)"
  dc_rc=0
  proxy_speech_curl "$OUT/t15-dc-body.json" "$OUT/t15-dc.mp3" 2 >"$OUT/t15-dc.tsv" 2>"$OUT/t15-dc.curl.err" || dc_rc=$?
  dc_active_one="$(wait_active 1 20)" && : || dc_active_one=""
  dc_zero="$(wait_active 0 180)" || dc_zero=""
  dc_line="$OUT/t15-dc-logline.json"
  dc_line_ok=1
  new_speech_line "$((dc_before + 1))" 60 >"$dc_line" || dc_line_ok=0
  [ "$dc_line_ok" -eq 1 ] && SPEECH_N=$((dc_before + 1))
  build_body "$FIXDIR/tts-vi-1000.txt" "$VOICE_VI" "$OUT/t15-dc-next-body.json"
  next_before="$(speech_count)"
  next_rc=0
  proxy_speech_curl "$OUT/t15-dc-next-body.json" "$OUT/t15-dc-next.mp3" 300 >"$OUT/t15-dc-next.tsv" 2>"$OUT/t15-dc-next.curl.err" || next_rc=$?
  next_code="$(cut -f1 "$OUT/t15-dc-next.tsv" 2>/dev/null || true)"
  next_line="$OUT/t15-dc-next-logline.json"
  next_line_ok=1
  new_speech_line "$((next_before + 1))" 60 >"$next_line" || next_line_ok=0
  [ "$next_line_ok" -eq 1 ] && SPEECH_N=$((next_before + 1))
  log "INFO t15.disconnect: curl_exit=$dc_rc active_zero_after_s=${dc_zero:--} next_status=${next_code:--}"

  # (b) ffmpeg killed mid-stream: the upstream drain must let VieNeu finish.
  build_body "$FIXDIR/tts-vi-5min.txt" "$VOICE_VI" "$OUT/t15-fk-body.json"
  fk_before="$(speech_count)"
  proxy_speech_curl "$OUT/t15-fk-body.json" "$OUT/t15-fk.mp3" 600 \
    >"$OUT/t15-fk.tsv" 2>"$OUT/t15-fk.curl.err" &
  FK_CURL_PID=$!
  fk_pid="$(wait_ffmpeg_child 120)" || fk_pid=""
  fk_bytes=0
  if [ -n "$fk_pid" ]; then
    t=0
    while [ "$t" -lt 60 ]; do
      if [ -f "$OUT/t15-fk.mp3" ] && [ -s "$OUT/t15-fk.mp3" ]; then break; fi
      sleep 1
      t=$((t + 1))
    done
    [ -f "$OUT/t15-fk.mp3" ] && fk_bytes="$(wc -c <"$OUT/t15-fk.mp3" | tr -d ' ')"
    [ "$fk_bytes" -gt 0 ] && sleep 1
    if [ "$fk_bytes" -gt 0 ]; then kill -KILL "$fk_pid" 2>/dev/null || true; fi
  fi
  fk_rc=0
  wait "$FK_CURL_PID" || fk_rc=$?
  fk_zero="$(wait_active 0 300)" || fk_zero=""
  fk_line="$OUT/t15-fk-logline.json"
  fk_line_ok=1
  new_speech_line "$((fk_before + 1))" 120 >"$fk_line" || fk_line_ok=0
  [ "$fk_line_ok" -eq 1 ] && SPEECH_N=$((fk_before + 1))
  build_body "$FIXDIR/tts-vi-1000.txt" "$VOICE_VI" "$OUT/t15-fk-next-body.json"
  fkn_before="$(speech_count)"
  fkn_rc=0
  proxy_speech_curl "$OUT/t15-fk-next-body.json" "$OUT/t15-fk-next.mp3" 300 >"$OUT/t15-fk-next.tsv" 2>"$OUT/t15-fk-next.curl.err" || fkn_rc=$?
  fkn_code="$(cut -f1 "$OUT/t15-fk-next.tsv" 2>/dev/null || true)"
  fkn_line="$OUT/t15-fk-next-logline.json"
  fkn_line_ok=1
  new_speech_line "$((fkn_before + 1))" 60 >"$fkn_line" || fkn_line_ok=0
  [ "$fkn_line_ok" -eq 1 ] && SPEECH_N=$((fkn_before + 1))
  log "INFO t15.ffmpeg_kill: ffmpeg_pid=${fk_pid:--} curl_exit=$fk_rc active_zero_after_s=${fk_zero:--} next_status=${fkn_code:--}"

  t15_direct_audio_1000="$(direct_audio_for "tts-vi-1000.txt" || echo 0)"
  t15_direct_audio_5min="$(direct_audio_for "tts-vi-5min.txt" || echo 0)"
  export T15_FILE="$T15_FILE" T15_OUT="$OUT" T15_DC_LINE="$dc_line" T15_DC_TSV="$OUT/t15-dc.tsv" \
    T15_DIRECT_AUDIO_1000="$t15_direct_audio_1000" \
    T15_DIRECT_AUDIO_5MIN="$t15_direct_audio_5min" \
    T15_DC_EXIT="$dc_rc" T15_DC_ZERO="${dc_zero:--}" T15_DC_ACTIVE_ONE="${dc_active_one:--}" \
    T15_DC_NEXT_LINE="$next_line" T15_DC_NEXT_TSV="$OUT/t15-dc-next.tsv" T15_DC_NEXT_EXIT="$next_rc" \
    T15_DC_LINE_OK="$dc_line_ok" T15_DC_NEXT_LINE_OK="$next_line_ok" T15_FK_LINE="$fk_line" \
    T15_FK_TSV="$OUT/t15-fk.tsv" T15_FK_EXIT="$fk_rc" T15_FK_ZERO="${fk_zero:--}" \
    T15_FK_LINE_OK="$fk_line_ok" T15_FK_NEXT_LINE="$fkn_line" T15_FK_NEXT_TSV="$OUT/t15-fk-next.tsv" \
    T15_FK_NEXT_EXIT="$fkn_rc" T15_FK_NEXT_LINE_OK="$fkn_line_ok" T15_FK_BYTES="$fk_bytes" \
    T15_FK_PID="${fk_pid:--}"
  PY - <<'PYEOF' >"$T15_FILE"
import json, os

def num(v, cast):
    try:
        return cast(v)
    except (TypeError, ValueError):
        return None

def speech_line(path, ok):
    if not ok or not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)

def tsv_http_code(tsv):
    try:
        with open(tsv, encoding="utf-8") as fh:
            row = fh.readline().rstrip("\n").split("\t")
        return int(row[0]) if row and row[0] else None
    except Exception:
        return None

dc_line = speech_line(os.environ["T15_DC_LINE"], os.environ["T15_DC_LINE_OK"] == "1")
dc_next = speech_line(os.environ["T15_DC_NEXT_LINE"], os.environ["T15_DC_NEXT_LINE_OK"] == "1")
fk_line = speech_line(os.environ["T15_FK_LINE"], os.environ["T15_FK_LINE_OK"] == "1")
fk_next = speech_line(os.environ["T15_FK_NEXT_LINE"], os.environ["T15_FK_NEXT_LINE_OK"] == "1")

doc = {
    "schema": "proxy-t15-live.v1",
    "note": "TL-requested live checks (2026-09-26); informational, not part of the T3 pass rule",
    "client_disconnect": {
        "curl_max_time_s": 2,
        "curl_exit": num(os.environ["T15_DC_EXIT"], int),
        "active_one_seen_after_s": num(os.environ["T15_DC_ACTIVE_ONE"], int),
        "vieneu_active_zero_after_s": num(os.environ["T15_DC_ZERO"], int),
        "proxy_log_status": dc_line.get("status"),
        "proxy_log_error_code": dc_line.get("error_code"),
        "drained_audio_seconds": dc_line.get("audio_seconds"),
        "drain_wall_ms": dc_line.get("wall_ms"),
        "next_request_http": tsv_http_code(os.environ["T15_DC_NEXT_TSV"]),
        "next_request_curl_exit": num(os.environ["T15_DC_NEXT_EXIT"], int),
        "next_request_log_status": dc_next.get("status"),
        "next_request_delivered_audio_seconds": dc_next.get("audio_seconds"),
    },
    "ffmpeg_kill": {
        "ffmpeg_pid": num(os.environ["T15_FK_PID"], int),
        "client_bytes_before_kill": num(os.environ["T15_FK_BYTES"], int),
        "curl_exit": num(os.environ["T15_FK_EXIT"], int),
        "vieneu_active_zero_after_s": num(os.environ["T15_FK_ZERO"], int),
        "proxy_log_status": fk_line.get("status"),
        "proxy_log_error_code": fk_line.get("error_code"),
        "drained_audio_seconds": fk_line.get("audio_seconds"),
        "drain_wall_ms": fk_line.get("wall_ms"),
        "next_request_http": tsv_http_code(os.environ["T15_FK_NEXT_TSV"]),
        "next_request_curl_exit": num(os.environ["T15_FK_NEXT_EXIT"], int),
        "next_request_log_status": fk_next.get("status"),
    },
}
direct_audio_1000 = num(os.environ["T15_DIRECT_AUDIO_1000"], float)
direct_audio_5min = num(os.environ["T15_DIRECT_AUDIO_5MIN"], float)

def drain_fraction(drained, direct):
    if not isinstance(drained, (int, float)) or not direct:
        return None
    return round(drained / direct, 3)

dc = doc["client_disconnect"]
dc["direct_vieneu_audio_s"] = direct_audio_1000
dc["drain_fraction"] = drain_fraction(dc["drained_audio_seconds"], direct_audio_1000)
dc["ok"] = bool(
    dc["curl_exit"] == 28
    and dc["proxy_log_status"] == 499
    and dc["proxy_log_error_code"] == "client_disconnected"
    and dc["vieneu_active_zero_after_s"] is not None
    # T15 drain evidence: the proxy kept reading, so VieNeu finished the whole
    # synthesis (>= 90% of the direct-VieNeu audio) instead of being cut off.
    and dc["drain_fraction"] is not None and dc["drain_fraction"] >= 0.9
    and dc["next_request_http"] == 200
    and dc["next_request_log_status"] == 200
)
fk = doc["ffmpeg_kill"]
fk["direct_vieneu_audio_s"] = direct_audio_5min
fk["drain_fraction"] = drain_fraction(fk["drained_audio_seconds"], direct_audio_5min)
fk["ok"] = bool(
    # The client must have seen the stream aborted, not a clean end of file.
    fk["curl_exit"] not in (None, 0)
    and fk["proxy_log_status"] == 502
    and fk["proxy_log_error_code"] == "transcode_failed"
    and fk["vieneu_active_zero_after_s"] is not None
    and fk["drain_fraction"] is not None and fk["drain_fraction"] >= 0.9
    and fk["next_request_http"] == 200
    and fk["next_request_log_status"] == 200
)
json.dump(doc, open(os.environ["T15_FILE"], "w", encoding="utf-8"), indent=2)
open(os.environ["T15_FILE"], "a", encoding="utf-8").write("\n")
PYEOF
else
  T15_FILE=""
fi

### 5. Failure injection: VieNeu dies mid-request -> 3 attempts -> task fails at audio.
FI_FILE="$OUT/failure_injection.json"
if [ "$INJECT" -eq 1 ]; then
  build_mpt_body() { # <fixture-file> <voice> <out-json>
    PY - "$1" "$2" "$3" <<'PYEOF'
import json, sys
with open(sys.argv[1], encoding="utf-8") as fh:
    text = fh.read()
body = {"video_script": text, "video_source": "local",
        "voice_name": "kokoro:" + sys.argv[2], "voice_rate": 1.0}
with open(sys.argv[3], "w", encoding="utf-8") as fh:
    json.dump(body, fh)
PYEOF
  }

  # run_injection <tag> <pre-kill-wait-s> — 0 when the attempt produced a usable
  # outcome file; the injected task is expected to fail.
  run_injection() {
    local tag="$1" prewait="$2"
    local body="$OUT/inject-$tag-body.json"
    local submit="$OUT/inject-$tag-submit.json"
    local task_json="$OUT/inject-$tag-task.json"
    local line_file="$OUT/inject-$tag-logline.json"
    local http task_id before_speech before_mpt t="" ff="" az=""
    build_mpt_body "$FIXDIR/tts-vi-5min.txt" "$VOICE_VI" "$body"
    before_speech="$(speech_count)"
    before_mpt="$(wc -l <"$MPT_LOG" | tr -d ' ')"
    http="$(curl -sS -H "@$MPT_AUTH" -H 'Content-Type: application/json' \
      --max-time 30 --data-binary "@$body" -o "$submit" -w '%{http_code}' "$MPT_BASE/audio")"
    [ "$http" = 200 ] || { log "WARN injection submit: HTTP $http"; return 1; }
    task_id="$(jget "$submit" 'd["data"]["task_id"]')"
    [ -n "$task_id" ] || return 1

    az="$(wait_active 1 60)" || az=""
    ff="$(wait_ffmpeg_child 60)" || ff=""
    [ -n "$ff" ] && sleep "$prewait"
    kill_vieneu || log "WARN injection: VieNeu listener did not go away within 30s"

    # Wait for the MPT task to reach a terminal state (state -1 failed, 1 complete).
    local waited=0 state=""
    while [ "$waited" -lt 300 ]; do
      curl -sS -H "@$MPT_AUTH" --max-time 10 -o "$task_json" "$MPT_BASE/tasks/$task_id" >/dev/null 2>&1 || true
      state="$(jget "$task_json" 'd["data"]["state"]' 2>/dev/null || true)"
      [ -n "$state" ] && [ "$state" != 4 ] && break
      sleep 2
      waited=$((waited + 2))
    done

    new_speech_line "$((before_speech + 1))" 30 >"$line_file" || true
    sleep 2  # let the remaining attempts' log lines land before slicing the range
    grep '"route":"/v1/audio/speech"' "$PLOG" | sed -n "$((before_speech + 1)),\$p" >"$OUT/inject-$tag-lines.jsonl"
    sed -n "$((before_mpt + 1)),\$p" "$MPT_LOG" >"$OUT/inject-$tag-mpt.log" 2>/dev/null || true
    restart_vieneu "$tag" >"$OUT/inject-$tag-restart.txt" 2>/dev/null || true

    inj_restart_s=""
    if [ -f "$OUT/inject-$tag-restart.txt" ]; then
      inj_restart_s="$(cat "$OUT/inject-$tag-restart.txt")"
    fi
    export FI_FILE="$FI_FILE" INJ_OUT="$OUT" INJ_TAG="$tag" INJ_HTTP="$http" INJ_ACTIVE_ONE="${az:--}" \
      INJ_FFMPEG="${ff:--}" INJ_PREWAIT="$prewait" INJ_TASK="$task_json" INJ_KILL_SIGNAL="KILL" \
      INJ_RESTART_S="$inj_restart_s"
    PY - <<'PYEOF' >"$FI_FILE"
import json, os

tag = os.environ["INJ_TAG"]
base = os.path.join(os.environ["INJ_OUT"], f"inject-{tag}")
lines = []
with open(f"{base}-lines.jsonl", encoding="utf-8") as fh:
    for raw in fh:
        raw = raw.strip()
        if raw:
            lines.append(json.loads(raw))

task = {}
try:
    with open(os.environ["INJ_TASK"], encoding="utf-8") as fh:
        task = json.load(fh).get("data", {})
except Exception:
    pass

MPT_MARKERS = ("kokoro tts", "tts succeeded", "task failed", "generating audio")
mpt_lines = []
try:
    with open(f"{base}-mpt.log", encoding="utf-8") as fh:
        for raw in fh:
            line = raw.rstrip("\n")
            if any(marker in line.lower() for marker in MPT_MARKERS):
                mpt_lines.append(line[-200:])
except FileNotFoundError:
    pass

def num(v, cast=int):
    try:
        return cast(v)
    except (TypeError, ValueError):
        return None

expected_error = ("failed to synthesize audio; verify the selected voice and "
                  "TTS connectivity")
doc = {
    "mpt_attempts": len(lines),
    "proxy_statuses": [ln.get("status") for ln in lines],
    "proxy_error_codes": [ln.get("error_code") for ln in lines],
    "first_status_is_stream_broken": bool(
        lines and lines[0].get("status") == 502
        and lines[0].get("error_code") == "upstream_stream_broken"
    ),
    "later_attempts_upstream_unavailable": bool(
        len(lines) >= 3 and all(
            ln.get("status") == 502 and ln.get("error_code") == "upstream_unavailable"
            for ln in lines[1:3]
        )
    ),
    "task_state": task.get("state"),
    "task_failed_stage": task.get("failed_stage"),
    "task_error": task.get("error"),
    "task_error_matches_m8": task.get("error") == expected_error,
    "mpt_tts_attempt_lines": mpt_lines[:12],
    "vieneu_active_one_before_kill_s": num(os.environ["INJ_ACTIVE_ONE"]),
    "ffmpeg_child_before_kill": os.environ["INJ_FFMPEG"] != "-",
    "pre_kill_wait_s": num(os.environ["INJ_PREWAIT"], float),
    "vieneu_kill_signal": os.environ.get("INJ_KILL_SIGNAL", "KILL"),
    "vieneu_restart_ready_s": num((os.environ.get("INJ_RESTART_S") or "").strip() or None),
    "mpt_submit_http": num(os.environ["INJ_HTTP"]),
}
doc["ok"] = bool(
    doc["mpt_attempts"] == 3
    and doc["first_status_is_stream_broken"]
    and doc["later_attempts_upstream_unavailable"]
    and doc["task_state"] == -1
    and doc["task_failed_stage"] == "audio"
    and doc["task_error_matches_m8"]
    and doc["vieneu_restart_ready_s"] is not None
)
json.dump(doc, open(os.environ["FI_FILE"], "w", encoding="utf-8"), indent=2)
open(os.environ["FI_FILE"], "a", encoding="utf-8").write("\n")
PYEOF
    return 0
  }

  INJ_ATTEMPTS=1
  if ! run_injection 1 3; then
    die 1 "failure injection: could not submit/drive the MPT task (see $OUT)"
  fi
  if [ "$(jget "$FI_FILE" 'd.get("first_status_is_stream_broken")')" != "true" ]; then
    log "WARN injection attempt 1 did not abort the upstream mid-stream (see $FI_FILE); retrying with a longer pre-kill wait"
    INJ_ATTEMPTS=2
    run_injection 2 8 || die 1 "failure injection retry could not be driven (see $OUT)"
  fi
fi

### 6. Assemble the baseline and print the verdict.
# Machine and pin facts for the baseline header.
BENCH_DATE="$(date +%Y-%m-%d)"
BENCH_STARTED_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
BENCH_MODEL="$(sysctl -n hw.model 2>/dev/null || echo unknown)"
BENCH_CPU="$(sysctl -n machdep.cpu.brand_string 2>/dev/null || true)"
[ -n "$BENCH_CPU" ] || BENCH_CPU="$BENCH_MODEL"
BENCH_RAM_GB="$(PY -c 'import os; print(round(os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES") / 1024**3))')"
BENCH_OS="macOS $(sw_vers -productVersion 2>/dev/null) ($(sw_vers -buildVersion 2>/dev/null))"
BENCH_FFMPEG="$(ffmpeg -version 2>/dev/null | awk 'NR==1{print $3}')"
BENCH_VIENEU_COMMIT="${VIENEU_COMMIT:-}"
MPT_VER_TSV="$(cd "$MPT_ROOT" && "$MPT_PY" -c 'import app, importlib.metadata as m; print(app.__version__, m.version("requests"), m.version("moviepy"))')"
read -r BENCH_MPT_VERSION BENCH_REQUESTS BENCH_MOVIEPY <<<"$MPT_VER_TSV"
BENCH_MP3_BITRATE="${MP3_BITRATE:-192k}"
BENCH_PROXY_MAX_CONCURRENT="$(sed -n 's/^export PROXY_MAX_CONCURRENT=\([0-9][0-9]*\).*$/\1/p' "$REPO/worker-node/bin/start-proxy.sh" | head -n 1)"
[ -n "$BENCH_PROXY_MAX_CONCURRENT" ] || BENCH_PROXY_MAX_CONCURRENT=4
DATESTR="$(date +%Y%m%d)"
OUTFILE="$BASEDIR/proxy-mac-$DATESTR.json"
if [ -e "$OUTFILE" ]; then
  n=2
  while [ -e "$BASEDIR/proxy-mac-$DATESTR-$n.json" ]; do n=$((n + 1)); done
  OUTFILE="$BASEDIR/proxy-mac-$DATESTR-$n.json"
fi
BENCH_NOTES="MPT-side runs via trial/tools/bench_proxy_mpt.py (MPT venv, moviepy decode) + worker-node/proxy/tools/mpt_decode_check.py; direct wall from $(basename "$DIRECT_BASELINE") (01.2.1-T4 median wall_s x 1000). mp3_bitrate from the bench env or the D13 default 192k; proxy_max_concurrent read from start-proxy.sh."

export BENCH_OUT="$OUT" BENCH_OUTFILE="$OUTFILE" BENCH_RUNS_TSV="$OUT/runs.tsv" \
  BENCH_DIRECT_BASELINE="$DIRECT_BASELINE" BENCH_PROXY_HEALTH="$OUT/proxy-health.json" \
  BENCH_DATE BENCH_STARTED_UTC BENCH_MODEL BENCH_CPU BENCH_RAM_GB BENCH_OS BENCH_FFMPEG \
  BENCH_VIENEU_COMMIT BENCH_MPT_VERSION BENCH_REQUESTS BENCH_MOVIEPY BENCH_MP3_BITRATE \
  BENCH_PROXY_MAX_CONCURRENT BENCH_NOTES BENCH_FI="$FI_FILE" BENCH_T15="$T15_FILE" \
  BENCH_INJ_ATTEMPTS="${INJ_ATTEMPTS:-0}" BENCH_MPT_CFG_URL="$MPT_CFG_URL" BENCH_FIXLIST="$FIXLIST" \
  BENCH_REPEATS="$REPEATS" BENCH_PROXY_PID="$PROXY_PID"
VERDICT="$(PY - <<'PYEOF'
import json, os, statistics

out = os.environ["BENCH_OUT"]
outfile = os.environ["BENCH_OUTFILE"]


def load_json(path):
    if not path or not os.path.exists(path) or os.path.getsize(path) == 0:
        return None
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return None


direct = load_json(os.environ["BENCH_DIRECT_BASELINE"]) or {}
direct_ms = {name: int(round(s["wall_s_median"] * 1000)) for name, s in direct.get("summary", {}).items()}
direct_audio = {name: s.get("audio_s") for name, s in direct.get("summary", {}).items()}

runs = []
with open(os.environ["BENCH_RUNS_TSV"], encoding="utf-8") as fh:
    for raw in fh:
        raw = raw.rstrip("\n")
        if not raw:
            continue
        fx, voice, rep, mp3, call_json, decode_json, drc, call_rc, mp3_bytes, line_file = raw.split("\t")
        call = load_json(call_json) or {}
        line = load_json(line_file) or {}
        decode = load_json(decode_json) or {}
        duration = decode.get("duration_s")
        audio_seconds = line.get("audio_seconds")
        dev_ok = (
            isinstance(duration, (int, float)) and isinstance(audio_seconds, (int, float))
            and abs(duration - audio_seconds) <= 0.1
        )
        runs.append({
            "fixture": fx,
            "voice": voice,
            "repeat": int(rep),
            "ok": bool(call.get("ok") and line.get("status") == 200 and line.get("http_status") == 200
                       and (line.get("bytes_out") or 0) > 0 and int(mp3_bytes) > 0),
            "status": line.get("status"),
            "http_status": line.get("http_status"),
            "error_code": line.get("error_code"),
            "chars": line.get("chars"),
            "audio_seconds": audio_seconds,
            "moviepy_duration_s": duration,
            "duration_within_0_1s": bool(dev_ok),
            "ttfb_ms": line.get("ttfb_ms"),
            "max_gap_ms": line.get("max_gap_ms"),
            "vieneu_ms": line.get("vieneu_ms"),
            "ffmpeg_ms": line.get("ffmpeg_ms"),
            "wall_ms": line.get("wall_ms"),
            "admission_wait_ms": line.get("admission_wait_ms"),
            "bytes_out": line.get("bytes_out"),
            "client_wall_ms": call.get("client_wall_ms"),
            "direct_vieneu_wall_ms": direct_ms.get("tts-%s.txt" % fx),
            "overhead_pct": None,
            "rid": line.get("rid"),
            "mp3_path": mp3,
            "mp3_bytes": int(mp3_bytes),
            "decode_exit": int(drc),
            "bench_helper_exit": int(call_rc),
        })

# Per-fixture medians and the NFR-04-3 overhead.
summary = {}
for fx in sorted({r["fixture"] for r in runs}):
    group = [r for r in runs if r["fixture"] == fx]
    walls = [r["wall_ms"] for r in group if isinstance(r["wall_ms"], (int, float))]
    clients = [r["client_wall_ms"] for r in group if isinstance(r["client_wall_ms"], (int, float))]
    direct_wall = direct_ms.get("tts-%s.txt" % fx)
    median_wall = statistics.median(walls) if walls else None
    overhead = None
    if median_wall is not None and direct_wall:
        overhead = round((median_wall - direct_wall) / direct_wall * 100, 2)
    for r in group:
        r["overhead_pct"] = overhead
    summary[fx] = {
        "wall_ms_median": median_wall,
        "client_wall_ms_median": statistics.median(clients) if clients else None,
        "audio_seconds_median": statistics.median(
            [r["audio_seconds"] for r in group if isinstance(r["audio_seconds"], (int, float))]
        ) if any(isinstance(r["audio_seconds"], (int, float)) for r in group) else None,
        "direct_vieneu_wall_ms": direct_wall,
        "direct_vieneu_audio_s": direct_audio.get("tts-%s.txt" % fx),
        "overhead_pct": overhead,
        "runs": len(group),
    }

fi = load_json(os.environ["BENCH_FI"])
t15 = load_json(os.environ["BENCH_T15"])

checks = {
    "status_200_all": bool(runs) and all(r["status"] == 200 and r["http_status"] == 200 for r in runs),
    "ok_all": bool(runs) and all(r["ok"] for r in runs),
    "ttfb_lt_30s": bool(runs) and all(isinstance(r["ttfb_ms"], (int, float)) and r["ttfb_ms"] < 30000 for r in runs),
    "max_gap_lt_60s": bool(runs) and all(isinstance(r["max_gap_ms"], (int, float)) and r["max_gap_ms"] < 60000 for r in runs),
    "duration_within_0_1s": bool(runs) and all(r["duration_within_0_1s"] for r in runs),
}
five_min = [fx for fx in summary if fx.endswith("-5min")]
checks["overhead_le_5pct"] = all(
    summary[fx]["overhead_pct"] is not None and summary[fx]["overhead_pct"] <= 5 for fx in five_min
)
if fi is not None:
    checks["failure_injection_ok"] = bool(fi.get("ok"))
result = "pass" if all(checks.values()) else "fail"

doc = {
    "schema": "proxy-baseline.v1",
    "date": os.environ["BENCH_DATE"],
    "started_at_utc": os.environ["BENCH_STARTED_UTC"],
    "machine": {
        "model": os.environ["BENCH_MODEL"],
        "cpu": os.environ["BENCH_CPU"],
        "ram_gb": int(os.environ["BENCH_RAM_GB"]),
        "os": os.environ["BENCH_OS"],
    },
    "pins": {
        "vieneu_commit": os.environ["BENCH_VIENEU_COMMIT"],
        "mpt_version": os.environ["BENCH_MPT_VERSION"],
        "requests": os.environ["BENCH_REQUESTS"],
        "moviepy": os.environ["BENCH_MOVIEPY"],
        "ffmpeg": os.environ["BENCH_FFMPEG"],
    },
    "proxy": {
        "mp3_bitrate": os.environ["BENCH_MP3_BITRATE"],
        "proxy_max_concurrent": int(os.environ["BENCH_PROXY_MAX_CONCURRENT"]),
        "catalog_revision": ((load_json(os.environ["BENCH_PROXY_HEALTH"]) or {}).get("catalog") or {}).get("revision"),
        "version": ((load_json(os.environ["BENCH_PROXY_HEALTH"]) or {}).get("proxy") or {}).get("version"),
        "pid": int(os.environ["BENCH_PROXY_PID"]),
    },
    "direct_baseline": {
        "file": os.path.relpath(os.environ["BENCH_DIRECT_BASELINE"], os.getcwd()),
        "tag": direct.get("tag"),
        "watermark_extra_installed": (direct.get("watermark") or {}).get("extra_installed"),
        "overhead_rule": "(median(wall_ms) - direct_vieneu_wall_ms) / direct_vieneu_wall_ms * 100",
    },
    "run_options": {
        "repeats": int(os.environ["BENCH_REPEATS"]),
        "fixtures": os.environ["BENCH_FIXLIST"],
        "inject_failure": os.path.exists(os.environ["BENCH_FI"]),
        "injection_attempts": int(os.environ["BENCH_INJ_ATTEMPTS"] or 0),
        "mpt_kokoro_base_url": os.environ["BENCH_MPT_CFG_URL"],
    },
    "runs": runs,
    "summary": summary,
    "checks": checks,
    "failure_injection": fi,
    "t15_live": t15,
    "result": result,
    "notes": os.environ["BENCH_NOTES"],
}
with open(outfile, "w", encoding="utf-8") as fh:
    json.dump(doc, fh, indent=2, ensure_ascii=False)
    fh.write("\n")

failed = [k for k, v in checks.items() if not v]
print("pass" if result == "pass" else "fail:" + ",".join(failed))
PYEOF
)" || die 1 "baseline assembly failed (see $OUT)"

log "baseline written: $OUTFILE"
log "raw evidence: $OUT"
case "$VERDICT" in
  pass) echo "PASS 04.1.2-T3"; exit 0 ;;
  fail:*) echo "FAIL 04.1.2-T3: ${VERDICT#fail:}"; exit 1 ;;
  *) die 1 "unexpected assembler verdict: $VERDICT" ;;
esac
