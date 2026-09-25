#!/usr/bin/env bash
# worker-node/bin/strip-bundled-music.sh — remove every audio file from the MPT tree that is not
# hash-allow-listed in worker-node/music/LICENSES.csv (01.2.2-T3; DECISIONS §5.5).
# resource/songs keeps only allow-listed files; storage/bgm ends up empty of audio.
# Never touches models/, storage/tasks/ or config.toml; never runs git clean.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/strip-bundled-music.sh [--check] [--mpt-root DIR]
  --check      list offenders, delete nothing, exit 1 if any unlicensed audio exists
  --mpt-root   MPT checkout (default: $NTS_HOME/src/mpt)
Exit codes: 0 no unlicensed audio remains · 1 offenders found/remove failed · 2 environment error.
EOF
}

CHECK=0
ROOT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --check) CHECK=1 ;;
    --mpt-root) [ $# -ge 2 ] || { usage >&2; exit 2; }; shift; ROOT="$1" ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
  shift
done
[ -z "$ROOT" ] && ROOT="$(nts_home)/src/mpt"
CSV="$NTS_REPO_ROOT/worker-node/music/LICENSES.csv"

# Env checks (exit 2).
[ -f "$ROOT/main.py" ] || die 2 "not an MPT root (no main.py): $ROOT"
[ -d "$ROOT/resource" ] || die 2 "no resource/ dir in $ROOT"
mkdir -p "$ROOT/resource/songs" "$ROOT/storage/bgm"

# Allowed set = sha256 column of LICENSES.csv (csv module handles quoted commas).
ALLOWED="$(uv run --no-project --python 3.12 python - "$CSV" <<'PY'
import csv, sys
try:
    rows = list(csv.DictReader(open(sys.argv[1], encoding="utf-8")))
except FileNotFoundError:
    sys.exit(0)
for r in rows:
    s = (r.get("sha256") or "").strip().lower()
    if s:
        print(s)
PY
)"

# Scan scope: only resource/songs and storage/bgm, only the media extensions
# (M6 audio + defensive video/AIFF). A symlink is always an offender.
is_media() {
  case "$(printf '%s' "${1##*.}" | tr '[:upper:]' '[:lower:]')" in
    mp3|m4a|aac|wav|flac|ogg|opus|wma|mp4|webm|aiff|aif) return 0 ;;
    *) return 1 ;;
  esac
}
in_allowed() {
  local h
  h="$(shasum -a 256 "$1" | cut -d' ' -f1)"
  printf '%s\n' "$ALLOWED" | grep -qx "$h"
}

removed=0 kept=0 offenders=0
for dir in resource/songs storage/bgm; do
  [ -d "$ROOT/$dir" ] || continue
  while IFS= read -r -d '' f; do
    rel="${f#"$ROOT"/}"
    if [ -L "$f" ]; then
      :   # a symlink is always an offender
    elif ! is_media "$f"; then
      continue
    elif [ "$dir" = "resource/songs" ] && in_allowed "$f"; then
      kept=$((kept + 1))
      continue
    fi
    offenders=$((offenders + 1))
    if [ "$CHECK" -eq 1 ]; then
      log "unlicensed $rel"
    elif rm -f -- "$f"; then
      removed=$((removed + 1))
      log "removed $rel"
    else
      die 1 "could not remove $rel"
    fi
  done < <(find "$ROOT/$dir" \( -type f -o -type l \) -print0)
done

if [ "$CHECK" -eq 1 ]; then
  if [ "$offenders" -gt 0 ]; then
    log "check: $offenders unlicensed item(s), $kept licensed kept"
    exit 1
  fi
  log "check: clean ($kept licensed kept)"
  exit 0
fi
log "strip: removed $removed, kept $kept (licensed)"
exit 0
