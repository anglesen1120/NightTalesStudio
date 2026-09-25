#!/usr/bin/env bash
# worker-node/bin/install-music.sh — install every track listed in worker-node/music/LICENSES.csv
# into <mpt-root>/resource/songs, hash-verified (01.2.2-T3). Idempotent.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/install-music.sh [--mpt-root DIR] [--source DIR]
  --mpt-root   MPT checkout (default: $NTS_HOME/src/mpt)
  --source     directory holding the licensed audio (default: $NTS_HOME/music)
Exit codes: 0 installed/verified · 1 missing file or hash mismatch · 2 environment error.
EOF
}

ROOT="" SRC=""
while [ $# -gt 0 ]; do
  case "$1" in
    --mpt-root) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift; ROOT="$1" ;;
    --source) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift; SRC="$1" ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done
H="$(nts_home)"
[ -z "$ROOT" ] && ROOT="$H/src/mpt"
[ -z "$SRC" ] && SRC="$H/music"
CSV="$NTS_REPO_ROOT/worker-node/music/LICENSES.csv"

# Env checks (exit 2).
[ -f "$ROOT/main.py" ] || die 2 "not an MPT root (no main.py): $ROOT"
[ -d "$ROOT/resource" ] || die 2 "no resource/ dir in $ROOT"
[ -f "$CSV" ] || die 2 "missing $CSV"
head -1 "$CSV" | grep -q '^file,' || die 2 "bad or missing CSV header in $CSV"

# Iterate CSV rows via the csv module (quoted commas); emits file<TAB>sha256 per row.
ROWS="$(uv run --no-project --python 3.12 python - "$CSV" <<'PY'
import csv, sys
for r in csv.DictReader(open(sys.argv[1], encoding="utf-8")):
    f = (r.get("file") or "").strip()
    s = (r.get("sha256") or "").strip().lower()
    if f:
        print(f"{f}\t{s}")
PY
)"

mkdir -p "$ROOT/resource/songs"
installed=0 skipped=0
while IFS=$'\t' read -r fname want; do
  [ -n "$fname" ] || continue
  case "$fname" in *[!a-z0-9._-]*|..*) die 2 "bad file name in CSV: $fname" ;; esac
  src_file="$SRC/$fname"
  [ -f "$src_file" ] || die 1 "missing $src_file"
  have="$(shasum -a 256 "$src_file" | cut -d' ' -f1)"
  [ "$have" = "$want" ] || die 1 "hash mismatch $fname"
  dest="$ROOT/resource/songs/$fname"
  if [ -f "$dest" ] && [ "$(shasum -a 256 "$dest" | cut -d' ' -f1)" = "$want" ]; then
    skipped=$((skipped + 1))
    continue
  fi
  install -m 0644 "$src_file" "$dest"
  installed=$((installed + 1))
  log "installed $fname"
done <<<"$ROWS"
log "install-music: installed $installed, already-present $skipped"

# Final gate: nothing unlicensed may remain (propagate its exit code).
"$NTS_REPO_ROOT/worker-node/bin/strip-bundled-music.sh" --check --mpt-root "$ROOT"
