#!/usr/bin/env bash
# worker-node/bin/tests/font-glyph-check.test.sh — 05.2.2-T1 named cases.
#
# Hermetic: runs worker-node/mpt/tools/font_glyph_check.py under
# `uv run --no-project --python 3.12` against a stub MPT root whose
# app.services.video.subtitle_font_supports_text marks a fixed set of letters
# as missing. Scratch lives under $NTS_HOME/runs/font-glyph-check-$$ and is
# removed on exit. No network, no real Pillow (the fail-closed loadability
# probe gets a stub PIL on PYTHONPATH), no mutation outside the temp tree.
#
# Cases:
#   1. NFC fixture vs stub font that misses "ơưả" -> exit 1, "missing 3".
#   2. The same fixture in NFD -> identical stdout (the defect this test pins:
#      decomposed text used to lose its combining marks and report a false
#      "glyphs ok"), plus a stderr note that the input was not NFC.
#   3. ASCII-only fixture -> exit 0, "glyphs ok" (unexercised happy path here).
#   4. A font the client library cannot open -> exit 1 naming it, and never
#      "glyphs ok" (MPT's own function answers True for anything it cannot
#      inspect — video.py L1189-1192 — so the probe must fail closed).
#   5. check-fonts.sh --help -> exit 0 with a usage block on stdout, not stderr.
set -euo pipefail

SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
BIN_DIR="$(dirname "$(dirname "$SELF")")"
REPO_ROOT="$(cd "$BIN_DIR/../.." && pwd)"
TOOL="$REPO_ROOT/worker-node/mpt/tools/font_glyph_check.py"
NTS_HOME_DIR="${NTS_HOME:-$REPO_ROOT/.nts}"
RUN_DIR="$NTS_HOME_DIR/runs/font-glyph-check-$$"
STUB="$RUN_DIR/stub-mpt"
STUB_PIL="$RUN_DIR/stub-pil"
FAILURES=0

command -v uv >/dev/null 2>&1 || { echo "test: uv missing" >&2; exit 2; }
[ -f "$TOOL" ] || { echo "test: $TOOL missing" >&2; exit 2; }

mkdir -p "$STUB/app/services" "$RUN_DIR"
trap 'rm -rf "$RUN_DIR"' EXIT

run_tool() { # (font, text) -> stdout on $1, captures exit code
    PYTHONPATH="$STUB_PIL${PYTHONPATH:+:$PYTHONPATH}" \
        uv run --no-project --python 3.12 python "$TOOL" \
        --font "$1" --text "$2" \
        --mpt-root "$STUB" --register "$RUN_DIR/FONTS.csv"
}

fail() { echo "FAIL: $1" >&2; FAILURES=$((FAILURES + 1)); }

# ── Sandbox ─────────────────────────────────────────────────────────────────
: > "$STUB/app/__init__.py"
: > "$STUB/app/services/__init__.py"
cat > "$STUB/app/services/video.py" <<'PY'
MISSING = set("ơưả")


def subtitle_font_supports_text(font_path, text):
    return not any(ch in MISSING for ch in text)
PY

# Stub PIL: the fail-closed probe must be provable without a real Pillow in the
# uv sandbox. It opens every font except one whose first bytes are b"BROKEN".
mkdir -p "$STUB_PIL/PIL"
: > "$STUB_PIL/PIL/__init__.py"
cat > "$STUB_PIL/PIL/ImageFont.py" <<'PY'
class _Font:
    def __init__(self, path, size):
        self.path, self.size = path, size


def truetype(path, size=10, **kwargs):
    with open(path, "rb") as fh:
        head = fh.read(8)
    if head.startswith(b"BROKEN"):
        raise OSError("cannot load font %r" % path)
    return _Font(path, size)
PY

# Register with header only -> every font prints "hash skip".
printf 'file,family,style,license,license_url,source_url,sha256,checked_at\n' \
  > "$RUN_DIR/FONTS.csv"
printf 'fake' > "$RUN_DIR/fake.ttf"
printf 'BROKEN-not-a-font' > "$RUN_DIR/broken.ttf"

NFC="$RUN_DIR/fixture-nfc.txt" NFD="$RUN_DIR/fixture-nfd.txt" \
  ASCII="$RUN_DIR/fixture-ascii.txt" \
  uv run --no-project --python 3.12 python - <<'PY'
import os
import unicodedata

nfc = "\n".join(["a", "A", "1", "ơ", "ư", "ả", "á"]) + "\nNgười\n"
with open(os.environ["NFC"], "w", encoding="utf-8") as fh:
    fh.write(nfc)
with open(os.environ["NFD"], "w", encoding="utf-8") as fh:
    fh.write(unicodedata.normalize("NFD", nfc))
with open(os.environ["ASCII"], "w", encoding="utf-8") as fh:
    fh.write("a\nA\n1\nZ\n")
PY

# ── Case 1: NFC fixture reports the stubbed missing letters ──────────────────
rc=0
run_tool "$RUN_DIR/fake.ttf" "$RUN_DIR/fixture-nfc.txt" \
  > "$RUN_DIR/out-nfc.txt" 2> "$RUN_DIR/err-nfc.txt" || rc=$?
[ "$rc" -eq 1 ] || fail "NFC run exit code $rc, want 1"
grep -qF "missing 3 glyph(s): ơưả" "$RUN_DIR/out-nfc.txt" \
  || fail "NFC run stdout lacks 'missing 3 glyph(s): ơưả'"

# ── Case 2: NFD fixture must give the identical verdict (was: false ok) ──────
rc=0
run_tool "$RUN_DIR/fake.ttf" "$RUN_DIR/fixture-nfd.txt" \
  > "$RUN_DIR/out-nfd.txt" 2> "$RUN_DIR/err-nfd.txt" || rc=$?
[ "$rc" -eq 1 ] || fail "NFD run exit code $rc, want 1"
cmp -s "$RUN_DIR/out-nfc.txt" "$RUN_DIR/out-nfd.txt" \
  || fail "NFD stdout differs from NFC stdout (normalisation defect)"
grep -qF "is not NFC; normalised" "$RUN_DIR/err-nfd.txt" \
  || fail "NFD run stderr lacks the not-NFC note"

# ── Case 3: ASCII-only fixture, nothing missing ──────────────────────────────
rc=0
run_tool "$RUN_DIR/fake.ttf" "$RUN_DIR/fixture-ascii.txt" \
  > "$RUN_DIR/out-ok.txt" 2> "$RUN_DIR/err-ok.txt" || rc=$?
[ "$rc" -eq 0 ] || fail "ASCII run exit code $rc, want 0"
grep -qF "glyphs ok 4/4" "$RUN_DIR/out-ok.txt" \
  || fail "ASCII run stdout lacks 'glyphs ok 4/4'"

# ── Case 4: unloadable font must fail closed (was: MPT's True = "glyphs ok") ──
rc=0
run_tool "$RUN_DIR/broken.ttf" "$RUN_DIR/fixture-ascii.txt" \
  > "$RUN_DIR/out-broken.txt" 2> "$RUN_DIR/err-broken.txt" || rc=$?
[ "$rc" -eq 1 ] || fail "unloadable font exit code $rc, want 1"
grep -qF "font FAIL broken.ttf not loadable" "$RUN_DIR/out-broken.txt" \
  || fail "unloadable font stdout lacks 'font FAIL broken.ttf not loadable'"
if grep -q "glyphs ok" "$RUN_DIR/out-broken.txt"; then
    fail "unloadable font printed 'glyphs ok' — the gate must fail closed"
fi

# ── Case 5: check-fonts.sh --help prints usage on stdout and exits 0 ─────────
rc=0
bash "$REPO_ROOT/worker-node/bin/check-fonts.sh" --help \
  > "$RUN_DIR/out-help.txt" 2> "$RUN_DIR/err-help.txt" || rc=$?
[ "$rc" -eq 0 ] || fail "check-fonts.sh --help exit code $rc, want 0"
grep -qF "usage: check-fonts.sh" "$RUN_DIR/out-help.txt" \
  || fail "check-fonts.sh --help stdout lacks the 'usage: check-fonts.sh' line"
if [ -s "$RUN_DIR/err-help.txt" ]; then
    fail "check-fonts.sh --help wrote to stderr: $(cat "$RUN_DIR/err-help.txt")"
fi

if [ "$FAILURES" -gt 0 ]; then
    echo "font-glyph-check.test.sh: $FAILURES failure(s)" >&2
    exit 1
fi
echo "font-glyph-check.test.sh: ok (5 cases)"
