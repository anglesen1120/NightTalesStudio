#!/bin/bash
# check-fonts.sh — fail-closed subtitle font check for MPT (05.2.2-T1).
#
# For each row of worker-node/fonts/FONTS.csv: verify the sha256 of the font
# binary, then run MPT's own subtitle_font_supports_text over the vi fixture
# via worker-node/mpt/tools/font_glyph_check.py. One "hash ok" and one
# "glyphs ok" line per registered font. Called by 11.3.1-T1 provisioning and
# by the adapter's assets.font cache (start / on change / every 600 s — never
# per health request, 05 D15).
#
# Exit codes (01 D9): 0 ok / 1 a check failed / 2 usage or environment error.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REGISTER="$REPO_ROOT/worker-node/fonts/FONTS.csv"
FIXTURE="$REPO_ROOT/trial/fixtures/vi-glyphs.txt"
TOOL="$REPO_ROOT/worker-node/mpt/tools/font_glyph_check.py"
MPT_ROOT="${MPT_ROOT:-}"

usage() {
    cat <<'EOF'
usage: check-fonts.sh [--mpt-root DIR] [--register FILE]

Fail-closed subtitle-font gate (05.2.2-T1). For every row of the font register:
verify the font binary's sha256, then call MPT's own
subtitle_font_supports_text once per distinct letter/digit of the vi fixture
(never reimplemented here). One line per check — "hash ok <file>" and
"glyphs ok <n>/<n>" — or "hash FAIL", "missing <n> glyph(s): <letters>" or
"font FAIL <file> not loadable" with exit 1.

  --mpt-root DIR   MoneyPrinterTurbo checkout holding resource/fonts and .venv
                   (default $MPT_ROOT, else $NTS_HOME/src/mpt; see 01.2.2-T1)
  --register FILE  font register CSV (default worker-node/fonts/FONTS.csv)
  -h, --help       print this usage on stdout and exit 0

Exit codes (01 D9): 0 every registered font ok / 1 a check failed (sha256
mismatch, missing glyphs, or a font the client library cannot open — a font
that cannot be checked is never reported "glyphs ok") / 2 usage or environment
error (missing MPT checkout, venv, font checker, register or fixture). Called
by 11.3.1-T1 provisioning and by the adapter's assets.font cache (start / on
change / every 600 s — never per health request, 05 D15).
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --mpt-root)
            MPT_ROOT="${2:-}"
            [ -n "$MPT_ROOT" ] || { usage >&2; exit 2; }
            shift 2
            ;;
        --register)
            REGISTER="${2:-}"
            [ -n "$REGISTER" ] || { usage >&2; exit 2; }
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "check-fonts: unknown flag: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [ -z "$MPT_ROOT" ]; then
    NTS_HOME="${NTS_HOME:-$REPO_ROOT/.nts}"
    MPT_ROOT="$NTS_HOME/src/mpt"
fi

# Environment guards — every one exits 2 with a hint naming 01.2.2-T1.
[ -d "$MPT_ROOT/resource/fonts" ] || {
    echo "check-fonts: $MPT_ROOT/resource/fonts missing — run 01.2.2-T1 first" >&2
    exit 2
}
PYBIN="$MPT_ROOT/.venv/bin/python"
[ -x "$PYBIN" ] || {
    echo "check-fonts: MPT venv python missing at $PYBIN — run 01.2.2-T1 first" >&2
    exit 2
}
[ -f "$TOOL" ] || {
    echo "check-fonts: glyph checker missing: $TOOL" >&2
    exit 2
}
[ -f "$REGISTER" ] || {
    echo "check-fonts: register missing: $REGISTER" >&2
    exit 2
}
[ -f "$FIXTURE" ] || {
    echo "check-fonts: fixture missing: $FIXTURE" >&2
    exit 2
}

# Register rows: the `file` column. Bare names resolve under MPT's
# resource/fonts; values containing '/' are used as given.
rows="$("$PYBIN" - "$REGISTER" <<'PY'
import csv, sys
with open(sys.argv[1], newline="", encoding="utf-8") as fh:
    for row in csv.DictReader(fh):
        name = (row.get("file") or "").strip()
        if name:
            print(name)
PY
)"

rc=0
while IFS= read -r font_name; do
    [ -n "$font_name" ] || continue
    case "$font_name" in
        */*) font_path="$font_name" ;;
        *)   font_path="$MPT_ROOT/resource/fonts/$font_name" ;;
    esac
    ec=0
    "$PYBIN" "$TOOL" --font "$font_path" --text "$FIXTURE" \
        --mpt-root "$MPT_ROOT" --register "$REGISTER" || ec=$?
    # exit 2 = environment/usage problem: abort the whole check.
    if [ "$ec" -eq 2 ]; then
        exit 2
    fi
    if [ "$ec" -ne 0 ]; then
        rc=1
    fi
done <<EOF
$rows
EOF

exit "$rc"
