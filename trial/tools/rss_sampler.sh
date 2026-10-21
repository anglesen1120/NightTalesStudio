#!/usr/bin/env bash
# trial/tools/rss_sampler.sh — 1 s process-tree RSS sampler (01.3.2-T1, plan 01).
# Records RSS (KiB) of each service's whole process tree, swap use and memory
# pressure while a run is active. Roots found by listener port (D4):
#   VieNeu 8000 · proxy 8001 · MPT API 8080 · MPT WebUI 8501 (0 in M0, D14).
# Raw samples:  $NTS_HOME/runs/<run_id>/rss_raw.csv   (untracked)
# Summary row:  trial/runs/<run_id>/rss.csv           (tracked, `all` scope)
# No sudo, no secrets, numbers only. Bash 3.2 compatible (no mapfile/assoc arrays).
#
# Tick design (measured on M4, 2026-09-25): one `ps` snapshot piped into ONE awk
# pass; awk does the recursive tree sum and, only on probe ticks, runs lsof /
# sysctl / memory_pressure through `cmd | getline`. All four lsof port probes
# are a single lsof process (~55 ms once per 10 s instead of ~50 ms x4 on the
# same tick — that stacked past the 100 ms p95 budget; spec's failure-handling
# allows raising the roots/pressure cadence, never the RSS cadence). Root and
# pressure probes land on different ticks so no tick does both.
set -euo pipefail

# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/../../worker-node/bin/lib.sh"

usage() {
  cat <<'EOF'
Usage:
  rss_sampler.sh <run_id> [--interval <s>] [--duration <s>]
  rss_sampler.sh --selftest <seconds>
  rss_sampler.sh --help

Samples RSS of the VieNeu/proxy/MPT/WebUI process trees (roots = listeners on
ports 8000/8001/8080/8501) every <interval> seconds until SIGTERM/SIGINT,
--duration elapses, or $NTS_HOME/current_run no longer equals <run_id>.
run_id must match D10: YYYYMMDD-HHMM-(vi|en)-(landscape|portrait)-NN and
$NTS_HOME/runs/<run_id>/ must already exist. Exit codes: 0 ok, 2 usage/env.
EOF
}

# ---- args -------------------------------------------------------------------
RUN_ID="" INTERVAL=1 DURATION="" SELFTEST_S=""
case "${1:-}" in
  --help|-h) usage; exit 0 ;;
  --selftest)
    SELFTEST_S="${2:-}"
    [[ $SELFTEST_S =~ ^[0-9]+$ && $SELFTEST_S -ge 1 ]] || die 2 "--selftest needs <seconds> >= 1"
    DURATION=$SELFTEST_S
    shift 2
    ;;
  "") usage >&2; exit 2 ;;
  *) RUN_ID=$1; shift ;;
esac
while (( $# )); do
  case "$1" in
    --interval) INTERVAL=${2:-}; shift 2 ;;
    --duration) DURATION=${2:-}; shift 2 ;;
    *) usage >&2; exit 2 ;;
  esac
done
[[ $INTERVAL =~ ^[0-9]+$ && $INTERVAL -ge 1 ]] || die 2 "--interval must be a positive integer"
[[ -z $DURATION ]] || { [[ $DURATION =~ ^[0-9]+$ && $DURATION -ge 1 ]] || die 2 "--duration must be a positive integer"; }

if [[ -z $SELFTEST_S ]]; then
  RE='^[0-9]{8}-[0-9]{4}-(vi|en)-(landscape|portrait)-[0-9]{2}$'
  [[ $RUN_ID =~ $RE ]] || die 2 "bad run_id '$RUN_ID' (want D10 YYYYMMDD-HHMM-<lang>-<aspect>-<nn>)"
fi

# ---- setup ------------------------------------------------------------------
nts_home >/dev/null   # exports NTS_HOME (no side effects)
PORTS="8000 8001 8080 8501"                          # vieneu proxy mpt webui
if [[ -n $SELFTEST_S ]]; then
  RUN_ID="sampler-selftest-$(date -u +%Y%m%d-%H%M%S)"
  mkdir -p "$NTS_HOME/runs/$RUN_ID"
  PORTS="18091 18092 18099 18093"
fi
OUT="$NTS_HOME/runs/$RUN_ID"
[[ -d $OUT ]] || die 2 "run dir missing: $OUT (created by start_run.sh)"
RAW="$OUT/rss_raw.csv"
LOGF="$OUT/rss_sampler.log"
PIDF="$OUT/rss_sampler.pid"
if [[ -n $SELFTEST_S ]]; then
  SUMMARY="$OUT/rss.csv"                       # self-test summary stays in $NTS_HOME
else
  SUMMARY="$NTS_REPO_ROOT/trial/runs/$RUN_ID/rss.csv"   # tracked run file
  mkdir -p "$(dirname "$SUMMARY")"
fi

echo $$ > "$PIDF"
[[ -n $SELFTEST_S ]] || exec >>"$LOGF" 2>&1   # run mode: all output to sampler log

# ---- state ------------------------------------------------------------------
R_V="-" R_P="-" R_M="-" R_W="-"        # last known root pids ('-' = none)
STOP=0 TICK=0 SAMPLES=0
START_TS="" START_EPOCH=0 T0=0
MS_ARR=()
V_MAX=0 P_MAX=0 M_MAX=0 W_MAX=0 T_MAX=0 M_NP_MAX=0
SWAP_START="" SWAP_MAX=0 P_LVL_MAX="" FREE_MIN=""
LVL=NA FP=NA
WROTE_SUMMARY=0

mb() { printf '%.1f' "$(awk -v k="$1" 'BEGIN{printf "%.1f", k / 1024}')"; }

write_summary() {
  (( WROTE_SUMMARY )) && return 0
  WROTE_SUMMARY=1
  [[ -f $SUMMARY ]] || printf '%s\n' \
    'scope,start_utc,end_utc,samples,vieneu_peak_mb,proxy_peak_mb,mpt_peak_mb,webui_peak_mb,total_peak_mb,swap_start_mb,swap_peak_mb,pressure_max,free_pct_min,mpt_nproc_max,sample_ms_p95' \
    > "$SUMMARY"
  local p95=0 idx
  if (( SAMPLES > 0 )); then
    idx=$(( (95 * SAMPLES + 99) / 100 ))
    p95=$(printf '%s\n' "${MS_ARR[@]}" | sort -n | sed -n "${idx}p")
  fi
  local ptxt=NA
  case $P_LVL_MAX in 1) ptxt=normal ;; 2) ptxt=warn ;; 4) ptxt=critical ;; esac
  printf 'all,%s,%s,%d,%s,%s,%s,%s,%s,%s,%s,%s,%s,%d,%s\n' \
    "$START_TS" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$SAMPLES" \
    "$(mb "$V_MAX")" "$(mb "$P_MAX")" "$(mb "$M_MAX")" "$(mb "$W_MAX")" "$(mb "$T_MAX")" \
    "${SWAP_START:-NA}" "${SWAP_MAX:-0}" "$ptxt" "${FREE_MIN:-NA}" "$M_NP_MAX" "$p95" \
    >> "$SUMMARY"
  log "summary 'all' row appended to $SUMMARY (samples=$SAMPLES)"
}

SELFTEST_TREE_PID=""

kill_selftest_tree() {
  local lp
  lp=$(lsof -nP -t -iTCP:18099 -sTCP:LISTEN 2>/dev/null | head -1 || true)
  if [[ -n $lp ]]; then
    pkill -P "$lp" 2>/dev/null || true
    kill "$lp" 2>/dev/null || true
  fi
  if [[ -n $SELFTEST_TREE_PID ]]; then
    kill "$SELFTEST_TREE_PID" 2>/dev/null || true
    wait "$SELFTEST_TREE_PID" 2>/dev/null || true
  fi
  return 0
}

on_exit() {
  trap - EXIT
  if [[ -n $SELFTEST_S ]]; then kill_selftest_tree 2>/dev/null || true; fi
  write_summary || true
  rm -f "$PIDF"
  log "stopped (tick=$TICK samples=$SAMPLES)"
}
trap 'on_exit' EXIT
trap 'STOP=1' TERM INT

# ---- selftest load ----------------------------------------------------------
if [[ -n $SELFTEST_S ]]; then
  # A leftover listener would satisfy the wait below and the sampler would walk a foreign tree (QA D1).
  require_port_free 18099
  uv run --no-project --python 3.12 python - >>"$LOGF" 2>&1 <<'PY' &
import subprocess, sys
from http.server import HTTPServer, BaseHTTPRequestHandler
# macOS compresses cold pages, so a zero or repeating buffer shrinks (dirty-once fell 50→11 MiB).
# Random bytes can't be compressed; re-dirtying every 30 s also keeps the pages hot.
subprocess.Popen([sys.executable, "-c",
                  "import os, time\n"
                  "b=bytearray(os.urandom(50*2**20))\n"
                  "for i in range(600):\n"
                  "    if i % 30 == 0: b[::4096]=os.urandom(len(b[::4096]))\n"
                  "    time.sleep(1)"])
class H(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.send_header("Content-Length", "0"); self.end_headers()
    def log_message(self, *a):
        pass
HTTPServer(("127.0.0.1", 18099), H).serve_forever()
PY
  SELFTEST_TREE_PID=$!
  ok=0
  for _ in $(seq 1 50); do
    lp=$(lsof -nP -t -iTCP:18099 -sTCP:LISTEN 2>/dev/null | head -1 || true)
    # The listener must be our own load process, not a process that grabbed the port meanwhile.
    if [[ -n $lp ]] && [[ $lp == "$SELFTEST_TREE_PID" || $(ps -o ppid= -p "$lp" | tr -d ' ') == "$SELFTEST_TREE_PID" ]]; then ok=1; break; fi
    sleep 0.2
  done
  (( ok )) || die 2 "selftest listener on 127.0.0.1:18099 did not come up"
fi

# ---- per-tick worker --------------------------------------------------------
# One ps snapshot in; one awk pass emits 15 fields:
#   v_pid v_rss v_n  p_pid p_rss p_n  m_pid m_rss m_n  w_pid w_rss w_n  swap lvl fp
# '-' = no root / not probed this tick; 'NA' = probe failed.
tick() {   # $1 = refresh-roots flag, $2 = probe-pressure flag
  ps -axo pid=,ppid=,rss= | awk \
    -v ROOTS="$R_V $R_P $R_M $R_W" -v PORTS="$PORTS" \
    -v DOLSOF="$1" -v DOPRESS="$2" '
    { kids[$2] = kids[$2] " " $1; rss[$1] = $3 }
    function walk(r,   a,i){ S += rss[r]; N++; split(kids[r], a, " "); for (i in a) if (a[i] != "") walk(a[i]) }
    END {
      np = split(PORTS, pt, " ")
      if (DOLSOF == 1) {           # one lsof covers all four ports
        delete rt
        LC = "lsof -nP -iTCP:" pt[1] " -iTCP:" pt[2] " -iTCP:" pt[3] \
             " -iTCP:" pt[4] " -sTCP:LISTEN -Fpn 2>/dev/null"
        lp = ""
        while ((LC | getline l) > 0) {
          if (l ~ /^p/) lp = substr(l, 2)
          else if (l ~ /^n/) {
            for (i = 1; i <= np; i++)
              if (l ~ (":" pt[i] "$")) rt[pt[i]] = lp
          }
        }
        close(LC)
        for (i = 1; i <= np; i++) rr[i] = (pt[i] in rt ? rt[pt[i]] : "-")
      } else {
        split(ROOTS, rr, " ")
      }
      swap = "NA"
      SC = "sysctl -n vm.swapusage"
      if ((SC | getline sl) > 0 && match(sl, /used = [0-9.]+[MG]/)) {
        u = substr(sl, RSTART + 7, RLENGTH - 7)
        v = substr(u, 1, length(u) - 1)
        swap = sprintf("%.1f", (substr(u, length(u)) == "G" ? v * 1024 : v))
      }
      close(SC)
      lvl = "-"; fp = "-"
      if (DOPRESS == 1) {
        lvl = "NA"; fp = "NA"
        PC = "sysctl -n kern.memorystatus_vm_pressure_level"
        if ((PC | getline pl) > 0 && pl ~ /^[0-9]+$/) lvl = pl
        close(PC)
        MC = "memory_pressure 2>/dev/null"
        while ((MC | getline ml) > 0)
          if (match(ml, /free percentage: [0-9]+%/))
            fp = substr(ml, RSTART + 17, RLENGTH - 18)
        close(MC)
      }
      out = ""
      for (i = 1; i <= 4; i++) {
        r = rr[i]; S = 0; N = 0
        if (r != "" && r != "-" && (r in rss)) walk(r)
        out = out " " (r == "" ? "-" : r) " " S " " N
      }
      sub(/^ /, "", out)
      print out " " swap " " lvl " " fp
    }'
}

# ---- main loop ---------------------------------------------------------------
START_TS=$(date -u +%Y-%m-%dT%H:%M:%SZ)
START_EPOCH=$(date +%s)
[[ -s $RAW ]] || printf '%s\n' \
  'ts_utc,vieneu_pid,vieneu_rss_kb,vieneu_nproc,proxy_pid,proxy_rss_kb,proxy_nproc,mpt_pid,mpt_rss_kb,mpt_nproc,webui_pid,webui_rss_kb,webui_nproc,swap_used_mb,pressure_level,free_pct,sample_ms' \
  > "$RAW"
log "started: run=$RUN_ID interval=${INTERVAL}s duration=${DURATION:-none} roots=$PORTS"
T0=$(date +%s%N)

while (( STOP == 0 )); do
  TICK=$(( TICK + 1 ))
  T0=$(date +%s%N)
  # roots refresh on ticks 1,10,20,...; pressure probe on ticks 1,6,11,16,...
  DOLSOF=0; DOPRESS=0
  (( TICK == 1 || TICK % 10 == 0 )) && DOLSOF=1
  (( TICK == 1 || TICK % 5 == 1 )) && DOPRESS=1

  # shellcheck disable=SC2046  # awk emits 15 plain numeric/dash fields; split intended
  set -- $(tick "$DOLSOF" "$DOPRESS")
  (( $# == 15 )) || die 1 "tick emitted $# fields (want 15)"
  V_P=$1; V_R=$2; V_N=$3; P_P=$4; P_R=$5; P_N=$6
  M_P=$7; M_R=$8; M_N=$9; W_P=${10}; W_R=${11}; W_N=${12}
  SWAP_MB=${13}; LVLC=${14}; FPC=${15}
  R_V=$V_P; R_P=$P_P; R_M=$M_P; R_W=$W_P           # remember roots for next tick
  [[ $LVLC == - ]] || LVL=$LVLC                  # repeat last between probes
  [[ $FPC == - ]] || FP=$FPC

  # shellcheck disable=SC2046  # two plain fields; split intended
  set -- $(date -u +'%Y-%m-%dT%H:%M:%SZ %s%N')
  TS=$1; T1=$2
  MS=$(( (T1 - T0) / 1000000 ))
  printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
    "$TS" "${V_P/-/}" "$V_R" "$V_N" "${P_P/-/}" "$P_R" "$P_N" \
    "${M_P/-/}" "$M_R" "$M_N" "${W_P/-/}" "$W_R" "$W_N" \
    "$SWAP_MB" "$LVL" "$FP" "$MS" >> "$RAW"

  # maxima
  SAMPLES=$(( SAMPLES + 1 ))
  MS_ARR+=("$MS")
  (( V_R > V_MAX )) && V_MAX=$V_R; (( P_R > P_MAX )) && P_MAX=$P_R
  (( M_R > M_MAX )) && M_MAX=$M_R; (( W_R > W_MAX )) && W_MAX=$W_R
  TOT=$(( V_R + P_R + M_R + W_R )); (( TOT > T_MAX )) && T_MAX=$TOT
  (( M_N > M_NP_MAX )) && M_NP_MAX=$M_N
  if [[ $SWAP_MB != NA ]]; then
    [[ -z $SWAP_START ]] && SWAP_START=$SWAP_MB
    SWAP_MAX=$(awk -v a="$SWAP_MB" -v b="$SWAP_MAX" 'BEGIN{print (a>b ? a : b)}')
  fi
  if [[ $LVL =~ ^[0-9]+$ ]] && { [[ -z $P_LVL_MAX ]] || (( LVL > P_LVL_MAX )); }; then P_LVL_MAX=$LVL; fi
  if [[ $FP =~ ^[0-9]+$ ]] && { [[ -z $FREE_MIN ]] || (( FP < FREE_MIN )); }; then FREE_MIN=$FP; fi

  # stop conditions
  if [[ -z $SELFTEST_S ]]; then
    CUR=$(cat "$NTS_HOME/current_run" 2>/dev/null || true)
    [[ $CUR == "$RUN_ID" ]] || break
  fi
  if [[ -n $DURATION ]] && (( $(date +%s) - START_EPOCH >= DURATION )); then break; fi
  (( STOP )) && break

  sleep "$INTERVAL" & wait $! 2>/dev/null || true
done

# ---- shutdown ---------------------------------------------------------------
if [[ -n $SELFTEST_S ]]; then
  write_summary
  rows=$(( $(wc -l < "$RAW" | tr -d ' ') - 1 ))
  # mean + p95 of sample_ms (col 17); p95 by sorted index, BSD awk (no asort)
  stats=$(awk -F, 'NR>1{s+=$17;n++} END{if(!n){print "0";exit} printf "%.1f", s/n}' "$RAW")
  p95=$(awk -F, 'NR>1{print $17}' "$RAW" | sort -n | awk -v n="$rows" \
    'NR==int(0.95*n+0.999){print; exit}')
  cpu_now=$(ps -o %cpu= -p $$ | tr -d ' ')
  echo "selftest report (run_id=$RUN_ID)"
  echo "  raw rows:            $rows (>= $((SELFTEST_S - 3)) expected)"
  echo "  mpt tree:            pid=$M_P rss_kb=$M_R nproc=$M_N (50 MiB child inside tree)"
  echo "  mpt_rss_kb >= 51200: $(( M_R >= 51200 ? 1 : 0 ))"
  echo "  mpt_nproc  >= 2:     $(( M_N >= 2 ? 1 : 0 ))"
  echo "  swap_used_mb:        $SWAP_MB"
  echo "  pressure_level:      $LVL"
  echo "  sample_ms mean/p95:  ${stats:-0} / ${p95:-0}"
  echo "  sampler CPU (ps %cpu): ${cpu_now:-0}%"
  echo "  raw:     $RAW"
  echo "  summary: $SUMMARY"
  kill_selftest_tree
  # Exit 1 unless every criterion holds (QA D2): the printed report alone gave no signal.
  cpu_int=${cpu_now%%.*}
  if (( rows >= SELFTEST_S - 3 && M_R >= 51200 && M_N >= 2 && ${p95:-999} < 100 && ${cpu_int:-99} < 2 )); then
    echo "selftest: PASS"
  else
    echo "selftest: FAIL"; exit 1
  fi
fi
