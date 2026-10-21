#!/usr/bin/env bash
# trial/tools/bench-vieneu.sh — VieNeu Mac baseline: cold start, idle/peak RSS, per-request RTF,
# 120 s gate (informational, 04 CR-2) and WAV handoff for 04.1.1 (01.2.1-T4).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/../../worker-node/bin/lib.sh"

usage() {
  cat <<'EOF'
Usage: trial/tools/bench-vieneu.sh --voice-vi <id> --voice-en <id>
                                    [--repeats N] [--cold-starts N] [--tag <t>]
                                    [--watermark-effective on|off|unknown]
                                    [--watermark-evidence <text>]

Runs the VieNeu Mac baseline: preflight, N cold starts, idle RSS, per-request
RTF/RSS on the tts-{vi,en}-{1000,5min} fixtures (trial/fixtures/), decodes
audio seconds (04 F3: header is 0xFFFFFFFF, never trust it), computes the
120 s gate (informational capacity input, 04 CR-2) and copies one WAV per
language/size to $NTS_HOME/runs/samples/ for 04.1.1. Writes
trial/baselines/vieneu-mac-<YYYYMMDD>.json (schema_version 1; suffix -2, -3
if the file already exists for today). Starts and stops its own VieNeu
server via worker-node/bin/start-vieneu.sh; port 8000 must be free on entry.

  --voice-vi <id>            required; voice id for the vi fixtures
  --voice-en <id>            required; voice id for the en fixtures
  --repeats N                counted repeats per fixture (default 2)
  --cold-starts N            cold-start measurements (default 2)
  --tag <t>                  baseline tag, e.g. "watermark-extra" (default "plain")
  --watermark-effective <v>  on|off|unknown, from T4 step 2 (default "unknown")
  --watermark-evidence <v>   log line or file:line from T4 step 2 (default "")
  -h, --help                 this text

Exit codes: 0 ok · 1 a run/gate step failed · 2 usage/preflight/environment error (D9).
EOF
}

VOICE_VI="" VOICE_EN="" REPEATS=2 COLD_STARTS=2 TAG=plain
WATERMARK_EFFECTIVE=unknown WATERMARK_EVIDENCE=""
WATERMARK_DECISION="" GATE_DECISION="" DECIDED_BY="" DECIDED_ON=""

while [ $# -gt 0 ]; do
  case "$1" in
    --voice-vi) VOICE_VI="${2:?--voice-vi needs a value}"; shift 2 ;;
    --voice-en) VOICE_EN="${2:?--voice-en needs a value}"; shift 2 ;;
    --repeats) REPEATS="${2:?--repeats needs a value}"; shift 2 ;;
    --cold-starts) COLD_STARTS="${2:?--cold-starts needs a value}"; shift 2 ;;
    --tag) TAG="${2:?--tag needs a value}"; shift 2 ;;
    --watermark-effective) WATERMARK_EFFECTIVE="${2:?--watermark-effective needs a value}"; shift 2 ;;
    --watermark-evidence) WATERMARK_EVIDENCE="${2:?--watermark-evidence needs a value}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) log "unknown argument: $1"; usage >&2; exit 2 ;;
  esac
done

[ -n "$VOICE_VI" ] || die 2 "--voice-vi is required"
[ -n "$VOICE_EN" ] || die 2 "--voice-en is required"
[[ "$REPEATS" =~ ^[0-9]+$ ]] && [ "$REPEATS" -ge 1 ] || die 2 "--repeats must be a positive integer"
[[ "$COLD_STARTS" =~ ^[0-9]+$ ]] && [ "$COLD_STARTS" -ge 1 ] || die 2 "--cold-starts must be a positive integer"
case "$WATERMARK_EFFECTIVE" in on|off|unknown) ;; *) die 2 "--watermark-effective must be on|off|unknown" ;; esac

PY() { uv run --quiet --no-project --python 3.12 python "$@"; }

FIXDIR="$NTS_REPO_ROOT/trial/fixtures"
BASEDIR="$NTS_REPO_ROOT/trial/baselines"
START_SCRIPT="$NTS_REPO_ROOT/worker-node/bin/start-vieneu.sh"
FIXTURES="tts-vi-1000 tts-en-1000 tts-vi-5min tts-en-5min"
NOTES=""

add_note() { NOTES="${NOTES:+$NOTES; }$1"; }

fixture_lang() { case "$1" in tts-vi-*) printf 'vi\n' ;; tts-en-*) printf 'en\n' ;; *) die 2 "unknown fixture: $1" ;; esac; }
fixture_voice() { case "$(fixture_lang "$1")" in vi) printf '%s\n' "$VOICE_VI" ;; en) printf '%s\n' "$VOICE_EN" ;; esac; }
fixture_unit_kind() { case "$(fixture_lang "$1")" in vi) printf 'syllables\n' ;; en) printf 'words\n' ;; esac; }
fixture_size() { case "$1" in *-1000) printf '1000\n' ;; *-5min) printf '5min\n' ;; esac; }

# counts_for_file <path> — prints "<chars> <units>" per the T4 step-1 script; dies if not NFC.
counts_for_file() {
  PY -c '
import sys, unicodedata
t = open(sys.argv[1], encoding="utf-8").read()
if t != unicodedata.normalize("NFC", t):
    sys.stderr.write(sys.argv[1] + " not NFC\n")
    sys.exit(1)
print(len(t.strip()), len(t.split()))
' "$1"
}

# stat_file <file> <median|max> [<divisor>] — one number per line; 0 on an empty file.
stat_file() {
  PY -c '
import sys
vals = []
with open(sys.argv[1]) as fh:
    for line in fh:
        line = line.strip()
        if line:
            vals.append(float(line))
op = sys.argv[2]
div = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0
if not vals:
    print(0.0)
else:
    if op == "median":
        vals.sort(); n = len(vals)
        v = vals[n // 2] if n % 2 else (vals[n // 2 - 1] + vals[n // 2]) / 2
    else:
        v = max(vals)
    print(v / div)
' "$1" "$2" "${3:-1}"
}

# ratio <num> <den> — num/den, or 0.0 if den <= 0 (never divide by zero on a failed request).
ratio() {
  PY -c '
import sys
n = float(sys.argv[1]); d = float(sys.argv[2])
print(n / d if d > 0 else 0.0)
' "$1" "$2"
}

# build_body <fixture-file> <voice> <out-json> — never puts the fixture text on argv.
build_body() {
  PY -c '
import json, sys
text = open(sys.argv[1], encoding="utf-8").read().strip()
body = {"model": "vieneu-v3-turbo", "input": text, "voice": sys.argv[2], "response_format": "wav"}
with open(sys.argv[3], "w", encoding="utf-8") as fh:
    json.dump(body, fh)
' "$1" "$2" "$3"
}

# append_run <18 fields…> <runs.jsonl> — one JSON line per request (D9: stdlib json, never hand-built).
append_run() {
  PY -c '
import json, sys
(fixture, lang, chars, units, unit_kind, voice, repeat, http_status, size_bytes,
 wall_s, ttfb_s, audio_s, audio_s_source, ffprobe_duration_s, rtf, units_per_min,
 peak_rss_mb, out_path) = sys.argv[1:19]
rec = {
    "fixture": fixture + ".txt", "lang": lang, "chars": int(chars), "units": int(units),
    "unit_kind": unit_kind, "voice": voice, "repeat": int(repeat),
    "http_status": int(http_status), "bytes": int(size_bytes),
    "wall_s": float(wall_s), "ttfb_s": float(ttfb_s), "audio_s": float(audio_s),
    "audio_s_source": audio_s_source, "ffprobe_duration_s": float(ffprobe_duration_s),
    "rtf": float(rtf), "units_per_min": float(units_per_min), "peak_rss_mb": float(peak_rss_mb),
}
with open(out_path, "a", encoding="utf-8") as fh:
    fh.write(json.dumps(rec) + "\n")
' "$@"
}

start_rss_sampler() { # <pid> <csv> <interval> — prints the sampler's own PID.
  local pid="$1" csv="$2" interval="$3"
  : >"$csv"
  ( while kill -0 "$pid" 2>/dev/null; do
      ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ' >>"$csv" || true
      sleep "$interval"
    done ) >/dev/null 2>&1 &
  echo $!
}

stop_rss_sampler() { kill "$1" 2>/dev/null || true; wait "$1" 2>/dev/null || true; }

start_server() { # sets SRV_PID, SRV_LOG (globals)
  SRV_LOG="$(mktemp "$OUT/server-log.XXXXXX")"
  "$START_SCRIPT" >"$SRV_LOG" 2>&1 &
  SRV_PID=$!
}

# wait_ready <timeout_s> — prints the elapsed seconds from the READY line; returns 1 on timeout/exit.
wait_ready() {
  local timeout="$1" waited=0 n
  while [ "$waited" -lt "$timeout" ]; do
    n="$(sed -n 's/^READY vieneu http:\/\/127\.0\.0\.1:8000 after \([0-9]*\)s$/\1/p' "$SRV_LOG" | tail -n 1)"
    if [ -n "$n" ]; then printf '%s\n' "$n"; return 0; fi
    kill -0 "$SRV_PID" 2>/dev/null || return 1
    sleep 1
    waited=$((waited + 1))
  done
  return 1
}

stop_server() {
  # TERM, not INT: bash sets SIGINT to ignored for asynchronous ("&") list items in a
  # non-interactive shell, and that ignore is inherited by an exec'd bash script (start-vieneu.sh)
  # regardless of its own `trap … INT` — only SIGTERM (also trapped there) reaches it reliably.
  kill -TERM "$SRV_PID" 2>/dev/null || true
  local waited=0
  while lsof -nP -iTCP:8000 -sTCP:LISTEN -t >/dev/null 2>&1; do
    sleep 1
    waited=$((waited + 1))
    [ "$waited" -lt 30 ] || break
  done
  # Fallback: KILL the wrapper and the real listener (SIGKILL is not propagated to children).
  if lsof -nP -iTCP:8000 -sTCP:LISTEN -t >/dev/null 2>&1; then
    kill -KILL "$SRV_PID" 2>/dev/null || true
    for p in $(lsof -nP -iTCP:8000 -sTCP:LISTEN -t 2>/dev/null); do kill -KILL "$p" 2>/dev/null || true; done
    sleep 1
    ! lsof -nP -iTCP:8000 -sTCP:LISTEN -t >/dev/null 2>&1 || die 1 "port 8000 still bound after stop"
  fi
  wait "$SRV_PID" 2>/dev/null || true
}

### 1. Preflight (D9: 2 = usage/environment error). Order chosen so busy-port and a missing
### PATH tool each fail independently of VieNeu's own install state (bootstrap/weights/key).
for f in $FIXTURES; do
  [ -f "$FIXDIR/$f.txt" ] || die 2 "fixture missing: $FIXDIR/$f.txt"
done
for tool in ffmpeg ffprobe curl lsof; do
  command -v "$tool" >/dev/null 2>&1 || die 2 "required tool not on PATH: $tool"
done
require_port_free 8000
"$START_SCRIPT" --check >&2 || die 2 "start-vieneu.sh --check failed"

NTS_HOME="$(nts_home)"
load_pins
mkdir -p "$NTS_HOME/runs/samples"
OUT="$NTS_HOME/runs/samples/bench-$(date +%Y%m%d-%H%M)"
mkdir -p "$OUT"
RUNS_JSONL="$OUT/runs.jsonl"
: >"$RUNS_JSONL"

EXTRA_INSTALLED=false
if [ -d "$NTS_HOME/src/vieneu" ]; then
  ( cd "$NTS_HOME/src/vieneu" && uv run --no-sync python -c 'import perth' ) >/dev/null 2>&1 \
    && EXTRA_INSTALLED=true
fi

### 2. Cold starts (default 2; the last one is kept running for the phases below).
COLD_START_ARR=()
i=1
while [ "$i" -le "$COLD_STARTS" ]; do
  start_server
  n="$(wait_ready 300)" || die 1 "cold start $i: no READY line within 300s (see $SRV_LOG)"
  COLD_START_ARR+=("$n")
  [ "$i" -eq 1 ] && add_note "cold_start[1] file cache state not asserted (spec: not required after reboot/purge)"
  [ "$i" -lt "$COLD_STARTS" ] && stop_server
  i=$((i + 1))
done

### 3. Idle RSS: median of 5 samples, 1 s apart, 10 s after the last READY.
SERVER_PID="$(lsof -tiTCP:8000 -sTCP:LISTEN | head -n 1)"
[ -n "$SERVER_PID" ] || die 1 "no process listening on 8000 after the last cold start"
sleep 10
IDLE_CSV="$OUT/idle-rss.csv"
: >"$IDLE_CSV"
i=1
while [ "$i" -le 5 ]; do
  ps -o rss= -p "$SERVER_PID" 2>/dev/null | tr -d ' ' >>"$IDLE_CSV" || true
  sleep 1
  i=$((i + 1))
done
IDLE_RSS_MB="$(stat_file "$IDLE_CSV" median 1024)"

### 4. Per-request phase: vi-1000 gets one discarded-from-summary warm-up (repeat 0, still logged).
KEY="$(secret nts.vieneu.api_key)"
HEALTH_BEFORE="$(curl -s --max-time 5 http://127.0.0.1:8000/health || true)"

do_request() { # <fixture> <lang> <chars> <units> <unit_kind> <voice> <repeat>
  local fixture="$1" lang="$2" chars="$3" units="$4" unit_kind="$5" voice="$6" repeat="$7"
  local file="$FIXDIR/$fixture.txt" wav="$OUT/$fixture-$repeat.wav"
  local rsscsv="$OUT/rss-$fixture-$repeat.csv" tsv="$OUT/curl-$fixture-$repeat.tsv"
  local body sampler_pid curl_rc http_status ttfb_s wall_s size_bytes peak_rss_mb
  local audio_s=0.0 audio_s_source=decoded ffprobe_duration_s=0.0 rtf=0.0 units_per_min=0.0

  body="$(mktemp "$OUT/body.XXXXXX.json")"
  build_body "$file" "$voice" "$body"
  sampler_pid="$(start_rss_sampler "$SERVER_PID" "$rsscsv" 0.5)"
  curl_rc=0
  curl -sS -H 'Content-Type: application/json' -H @<(printf 'Authorization: Bearer %s\n' "$KEY") \
    --max-time 900 --data-binary @"$body" -o "$wav" \
    -w '%{http_code}\t%{time_starttransfer}\t%{time_total}\t%{size_download}\n' \
    http://127.0.0.1:8000/v1/audio/speech >"$tsv" || curl_rc=$?
  stop_rss_sampler "$sampler_pid"
  rm -f "$body"
  [ "$curl_rc" -eq 0 ] || log "curl exit $curl_rc for $fixture repeat $repeat (recorded as http_status below)"
  IFS=$'\t' read -r http_status ttfb_s wall_s size_bytes <"$tsv"
  peak_rss_mb="$(stat_file "$rsscsv" max 1024)"
  if [ "$http_status" = 200 ] && [ -s "$wav" ]; then
    local pcm_bytes
    pcm_bytes="$(ffmpeg -v error -i "$wav" -f s16le -ac 1 -ar 48000 - | wc -c | tr -d ' ')"
    audio_s="$(ratio "$pcm_bytes" 96000)"
    ffprobe_duration_s="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$wav" 2>/dev/null || printf '0\n')"
    rtf="$(ratio "$wall_s" "$audio_s")"
    units_per_min="$(ratio "$units" "$(ratio "$audio_s" 60)")"
  else
    log "$fixture repeat $repeat: http_status=$http_status (audio_s/rtf left at 0)"
  fi
  append_run "$fixture" "$lang" "$chars" "$units" "$unit_kind" "$voice" "$repeat" \
    "$http_status" "$size_bytes" "$wall_s" "$ttfb_s" "$audio_s" "$audio_s_source" \
    "$ffprobe_duration_s" "$rtf" "$units_per_min" "$peak_rss_mb" "$RUNS_JSONL"
  printf '%s\n' "$wav"
}

for fixture in $FIXTURES; do
  lang="$(fixture_lang "$fixture")"
  voice="$(fixture_voice "$fixture")"
  unit_kind="$(fixture_unit_kind "$fixture")"
  read -r chars units <<<"$(counts_for_file "$FIXDIR/$fixture.txt")"

  if [ "$fixture" = tts-vi-1000 ]; then
    do_request "$fixture" "$lang" "$chars" "$units" "$unit_kind" "$voice" 0 >/dev/null
  fi
  r=1
  last_wav=""
  while [ "$r" -le "$REPEATS" ]; do
    last_wav="$(do_request "$fixture" "$lang" "$chars" "$units" "$unit_kind" "$voice" "$r")"
    r=$((r + 1))
  done
  case "$fixture" in
    tts-vi-1000) LAST_WAV_VI_1000="$last_wav" ;;
    tts-en-1000) LAST_WAV_EN_1000="$last_wav" ;;
    tts-vi-5min) LAST_WAV_VI_5MIN="$last_wav" ;;
    tts-en-5min) LAST_WAV_EN_5MIN="$last_wav" ;;
  esac
done

HEALTH_AFTER="$(curl -s --max-time 5 http://127.0.0.1:8000/health || true)"
unset KEY

### 5. Stop the server before writing the baseline.
stop_server

### 6. WAV handoff for 04.1.1: copy, sha256, RIFF/data hex (04 F3 expects 0xffffffff), ffmpeg -f null.
SAMPLES_JSONL="$OUT/samples.jsonl"
: >"$SAMPLES_JSONL"
copy_sample() { # <src-wav> <lang> <size>
  local src="$1" lang="$2" size="$3" dest="$NTS_HOME/runs/samples/vieneu-$2-$3.wav"
  [ -n "$src" ] && [ -f "$src" ] || { log "no WAV recorded for vieneu-$lang-$size.wav"; return 0; }
  cp "$src" "$dest"
  local ffmpeg_rc=0
  ffmpeg -v error -i "$dest" -f null - >/dev/null 2>&1 || ffmpeg_rc=$?
  PY -c '
import hashlib, json, sys
path, ffmpeg_rc, out_path = sys.argv[1], int(sys.argv[2]), sys.argv[3]
data = open(path, "rb").read()
idx = data.find(b"data", 12)
data_size_hex = data[idx + 4:idx + 8].hex() if idx != -1 else "notfound"
rec = {
    "path": path, "sha256": hashlib.sha256(data).hexdigest(),
    "riff_size_hex": data[4:8].hex(), "data_size_hex": data_size_hex,
    "ffmpeg_f_null_exit": ffmpeg_rc,
}
with open(out_path, "a", encoding="utf-8") as fh:
    fh.write(json.dumps(rec) + "\n")
' "$dest" "$ffmpeg_rc" "$SAMPLES_JSONL"
}
copy_sample "${LAST_WAV_VI_1000:-}" vi 1000
copy_sample "${LAST_WAV_EN_1000:-}" en 1000
copy_sample "${LAST_WAV_VI_5MIN:-}" vi 5min
copy_sample "${LAST_WAV_EN_5MIN:-}" en 5min

### 7. Assemble and write the baseline JSON (schema: plan.md §3).
mkdir -p "$BASEDIR"
DATESTR="$(date +%Y%m%d)"
OUTFILE="$BASEDIR/vieneu-mac-$DATESTR.json"
if [ -e "$OUTFILE" ]; then
  n=2
  while [ -e "$BASEDIR/vieneu-mac-$DATESTR-$n.json" ]; do n=$((n + 1)); done
  OUTFILE="$BASEDIR/vieneu-mac-$DATESTR-$n.json"
fi

BENCH_TAG="$TAG"
BENCH_DATE_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
BENCH_MACOS="$(sw_vers -productVersion)"
BENCH_NCPU="$(sysctl -n hw.ncpu)"
BENCH_MEMSIZE="$(sysctl -n hw.memsize)"
BENCH_VIENEU_COMMIT="${VIENEU_COMMIT:-}"
BENCH_MODEL_REPO="${VIENEU_MODEL_REPO:-}"
BENCH_MODEL_REVISION="${VIENEU_MODEL_REVISION:-}"
BENCH_CODEC_REPO="${VIENEU_CODEC_REPO:-}"
BENCH_CODEC_REVISION="${VIENEU_CODEC_REVISION:-}"
BENCH_WATERMARK_EXTRA_INSTALLED="$EXTRA_INSTALLED"
BENCH_WATERMARK_EFFECTIVE="$WATERMARK_EFFECTIVE"
BENCH_WATERMARK_EVIDENCE="$WATERMARK_EVIDENCE"
BENCH_WATERMARK_DECISION="$WATERMARK_DECISION"
BENCH_COLD_START_S="$(IFS=,; echo "${COLD_START_ARR[*]}")"
BENCH_IDLE_RSS_MB="$IDLE_RSS_MB"
BENCH_HEALTH_BEFORE="$HEALTH_BEFORE"
BENCH_HEALTH_AFTER="$HEALTH_AFTER"
BENCH_RUNS_JSONL="$RUNS_JSONL"
BENCH_SAMPLES_JSONL="$SAMPLES_JSONL"
BENCH_OUT_PATH="$OUTFILE"
BENCH_GATE_DECISION="$GATE_DECISION"
BENCH_DECIDED_BY="$DECIDED_BY"
BENCH_DECIDED_ON="$DECIDED_ON"
BENCH_NOTES="$NOTES"
export BENCH_TAG BENCH_DATE_UTC BENCH_MACOS BENCH_NCPU BENCH_MEMSIZE \
  BENCH_VIENEU_COMMIT BENCH_MODEL_REPO BENCH_MODEL_REVISION BENCH_CODEC_REPO BENCH_CODEC_REVISION \
  BENCH_WATERMARK_EXTRA_INSTALLED BENCH_WATERMARK_EFFECTIVE BENCH_WATERMARK_EVIDENCE BENCH_WATERMARK_DECISION \
  BENCH_COLD_START_S BENCH_IDLE_RSS_MB BENCH_HEALTH_BEFORE BENCH_HEALTH_AFTER \
  BENCH_RUNS_JSONL BENCH_SAMPLES_JSONL BENCH_OUT_PATH \
  BENCH_GATE_DECISION BENCH_DECIDED_BY BENCH_DECIDED_ON BENCH_NOTES

PY - <<'PYEOF'
import json, os, statistics

def env(k, default=""):
    return os.environ.get(k, default)

runs = []
with open(env("BENCH_RUNS_JSONL")) as fh:
    for line in fh:
        line = line.strip()
        if line:
            runs.append(json.loads(line))

samples = []
with open(env("BENCH_SAMPLES_JSONL")) as fh:
    for line in fh:
        line = line.strip()
        if line:
            samples.append(json.loads(line))

summary = {}
for fixture in ("tts-vi-1000.txt", "tts-en-1000.txt", "tts-vi-5min.txt", "tts-en-5min.txt"):
    counted = [r for r in runs if r["fixture"] == fixture and r["repeat"] >= 1]
    if not counted:
        summary[fixture] = {"wall_s_median": 0.0, "audio_s": 0.0, "rtf_median": 0.0, "peak_rss_mb_max": 0.0}
        continue
    summary[fixture] = {
        "wall_s_median": statistics.median(r["wall_s"] for r in counted),
        "audio_s": statistics.median(r["audio_s"] for r in counted),
        "rtf_median": statistics.median(r["rtf"] for r in counted),
        "peak_rss_mb_max": max(r["peak_rss_mb"] for r in counted),
    }

rtf_5min_max = max(summary["tts-vi-5min.txt"]["rtf_median"], summary["tts-en-5min.txt"]["rtf_median"])
projected_wall_s = rtf_5min_max * 300
gate_result = "pass" if projected_wall_s < 100 else "fail"

try:
    health_before = json.loads(env("BENCH_HEALTH_BEFORE")) if env("BENCH_HEALTH_BEFORE") else None
except ValueError:
    health_before = env("BENCH_HEALTH_BEFORE")
try:
    health_after = json.loads(env("BENCH_HEALTH_AFTER")) if env("BENCH_HEALTH_AFTER") else None
except ValueError:
    health_after = env("BENCH_HEALTH_AFTER")

cold = [float(x) for x in env("BENCH_COLD_START_S").split(",") if x]

doc = {
    "schema_version": 1,
    "component": "vieneu",
    "tag": env("BENCH_TAG", "plain"),
    "date": env("BENCH_DATE_UTC"),
    "host": {
        "model": "Mac", "macos": env("BENCH_MACOS"),
        "cpu_count": int(env("BENCH_NCPU", "0")),
        "ram_gb": round(int(env("BENCH_MEMSIZE", "0")) / (1024 ** 3)),
    },
    "pins": {
        "vieneu_commit": env("BENCH_VIENEU_COMMIT"),
        "model_repo": env("BENCH_MODEL_REPO"), "model_revision": env("BENCH_MODEL_REVISION"),
        "codec_repo": env("BENCH_CODEC_REPO"), "codec_revision": env("BENCH_CODEC_REVISION"),
    },
    "server": {
        "backend": "onnx", "precision": "fp32", "max_streams": 1,
        "health": {"before": health_before, "after": health_after},
    },
    "watermark": {
        "env": "1",
        "extra_installed": env("BENCH_WATERMARK_EXTRA_INSTALLED") == "true",
        "effective": env("BENCH_WATERMARK_EFFECTIVE", "unknown"),
        "evidence": env("BENCH_WATERMARK_EVIDENCE"),
        "decision": env("BENCH_WATERMARK_DECISION"),
    },
    "cold_start_s": cold,
    "idle_rss_mb": float(env("BENCH_IDLE_RSS_MB", "0")),
    "runs": runs,
    "summary": summary,
    "gate_120s": {
        "rule": "rtf_5min_max * 300 < 100",
        "rtf_5min_max": rtf_5min_max,
        "projected_wall_s": projected_wall_s,
        "threshold_s": 100,
        "result": gate_result,
        "decision": env("BENCH_GATE_DECISION"),
        "decided_by": env("BENCH_DECIDED_BY"),
        "decided_on": env("BENCH_DECIDED_ON"),
    },
    "samples": samples,
    "notes": env("BENCH_NOTES"),
}

with open(env("BENCH_OUT_PATH"), "w", encoding="utf-8") as fh:
    json.dump(doc, fh, indent=2, sort_keys=False)
    fh.write("\n")
print(env("BENCH_OUT_PATH"))
PYEOF

log "baseline written: $OUTFILE"
log "raw data: $OUT"
