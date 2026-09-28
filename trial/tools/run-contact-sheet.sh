#!/usr/bin/env bash
# 03.3.2-T1 wrapper: reads nts.openai.image from the Keychain into the child's environment only
# (01 D3), loads the image pins, and marks the tap run per aspect batch (01 D6/D10):
# $NTS_HOME/current_run = <YYYYMMDD-HHMM>-en-landscape-90, then ...-en-portrait-91 (non-video runs,
# finish_run.py is not called). --max-usd is shared: the tool counts rows already in the results CSV.
#   trial/tools/run-contact-sheet.sh --mode sheet --out "$NTS_HOME/runs/contact-sheet-$(date +%Y%m%d)"
#   trial/tools/run-contact-sheet.sh --mode previews --qualities medium --out DIR --max-usd 2
# Exit codes: the tool's (0 ok / 1 cells failed / 2 usage / 3 budget reached).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; REPO="$(cd "$HERE/../.." && pwd)"
# shellcheck source=worker-node/bin/lib.sh
. "$REPO/worker-node/bin/lib.sh"
NTS_HOME="$(nts_home)"; export NTS_HOME
load_pins
[ -f "$NTS_HOME/current_run" ] && die 2 "run $(cat "$NTS_HOME/current_run") active; finish or --abort it"

mode=""; prev=""
for arg in "$@"; do
  [ "$prev" = "--mode" ] && mode="$arg"
  case "$arg" in --mode=*) mode="${arg#--mode=}" ;; --aspects|--aspects=*) die 2 "--aspects is set by the wrapper" ;; esac
  prev="$arg"
done

stamp="$(date +%Y%m%d-%H%M)"
cleanup() { rm -f "$NTS_HOME/current_run"; }
trap cleanup EXIT

batch() { # batch <aspect> <nn> <tool args...>
  local aspect="$1" nn="$2"; shift 2
  printf '%s\n' "$stamp-en-$aspect-$nn" > "$NTS_HOME/current_run.tmp"
  mv "$NTS_HOME/current_run.tmp" "$NTS_HOME/current_run"
  log "tap run $stamp-en-$aspect-$nn"
  OPENAI_API_KEY="$(secret nts.openai.image)" \
    uv run --no-project --python 3.12 "$HERE/image_contact_sheet.py" "$@" --aspects "$aspect"
}

if [ "$mode" = "previews" ]; then
  batch landscape 92 "$@"
else
  rc=0
  batch landscape 90 "$@" || rc=$?
  [ "$rc" -eq 3 ] || [ "$rc" -eq 2 ] && exit "$rc"
  rc2=0
  batch portrait 91 "$@" || rc2=$?
  [ "$rc2" -ne 0 ] && exit "$rc2"
  exit "$rc"
fi
