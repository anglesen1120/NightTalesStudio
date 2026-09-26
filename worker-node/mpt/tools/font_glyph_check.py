#!/usr/bin/env python3
"""font_glyph_check.py — fail-closed subtitle-font check for MPT (05.2.2-T1).

Loads FONTS.csv rows, verifies the font binary sha256, then exercises MPT's own
``app.services.video.subtitle_font_supports_text`` once per distinct letter/digit
of a fixture file (the function truncates its sample to 64 L/N chars, so the
check calls it per character). Optionally renders a proof PNG with PIL.

Exit codes:
  0  hash ok and all glyphs supported
  1  sha256 mismatch against the register, missing glyphs, or a font the client
     library (PIL) cannot open — never a "glyphs ok" for a font not checked
  2  usage error, missing venv/module/font/fixture, PIL unavailable, or the MPT
     function is not callable as (font_path, text) — the observed problem is
     printed, nothing is guessed
"""
import argparse
import csv
import hashlib
import inspect
import os
import sys
import unicodedata

REPO_ROOT = os.path.realpath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
DEFAULT_REGISTER = os.path.join(REPO_ROOT, "worker-node", "fonts", "FONTS.csv")


def eprint(msg):
    print(msg, file=sys.stderr)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_register(register_path):
    """Return {basename: row} for register rows; empty dict if unreadable."""
    rows = {}
    with open(register_path, newline="", encoding="utf-8") as fh:
        for row in csv.DictReader(fh):
            name = os.path.basename(row.get("file", "").strip())
            if name:
                rows[name] = row
    return rows


def find_mpt_root(requested):
    """Prefer --mpt-root, else MPT_ROOT env, else $NTS_HOME/src/mpt."""
    for cand in (requested, os.environ.get("MPT_ROOT")):
        if cand:
            return os.path.realpath(cand)
    nts = os.environ.get("NTS_HOME") or os.path.join(REPO_ROOT, ".nts")
    return os.path.join(nts, "src", "mpt")


def load_glyph_function(mpt_root):
    """Import MPT's subtitle_font_supports_text. Returns (fn, err)."""
    if not os.path.isdir(mpt_root):
        return None, "mpt_root not found: %s (see 01.2.2-T1)" % mpt_root
    sys.path.insert(0, mpt_root)
    cwd = os.getcwd()
    try:
        os.chdir(mpt_root)
        try:
            import app.services.video as v  # noqa: F401
        except Exception as exc:
            return None, "import app.services.video failed: %r" % exc
        fn = getattr(__import__("app.services.video", fromlist=["subtitle_font_supports_text"]),
                     "subtitle_font_supports_text", None)
        if fn is None:
            return None, "app.services.video has no subtitle_font_supports_text"
        return fn, None
    finally:
        os.chdir(cwd)


def check_signature(fn):
    """Verify fn binds two positional args. Returns observed signature string or None."""
    try:
        sig = inspect.signature(fn)
    except (TypeError, ValueError) as exc:
        return "uninspectable signature: %r" % exc
    try:
        sig.bind("x.ttf", "ch")
    except TypeError:
        return str(sig)
    return None


def read_text_nfc(fixture_path):
    """Read a fixture and normalise it to NFC.

    NFD input would silently pass: combining marks are category Mn and are
    filtered out below, so a decomposed Vietnamese letter loses its tone and a
    font that cannot render it still reports "glyphs ok" (measured: STHeitiMedium
    gave 64/64 on an NFD fixture vs missing 92 on the NFC one).
    """
    with open(fixture_path, encoding="utf-8") as fh:
        raw = fh.read()
    text = unicodedata.normalize("NFC", raw)
    if text != raw:
        eprint("note: %s is not NFC; normalised for the glyph check" % fixture_path)
    return text


def collect_chars(text):
    """Distinct L/N characters of the (NFC) fixture text, in order."""
    seen = []
    for ch in text:
        if unicodedata.category(ch)[0] in {"L", "N"} and ch not in seen:
            seen.append(ch)
    return seen


def render_proof(font_path, text, out_path):
    try:
        from PIL import Image, ImageDraw, ImageFont
    except Exception as exc:
        eprint("proof skip: PIL unavailable (%r)" % exc)
        return False
    font = ImageFont.truetype(font_path, 84)
    pad = 24
    tmp = Image.new("RGB", (8, 8), "white")
    d = ImageDraw.Draw(tmp)
    bbox = d.textbbox((0, 0), text, font=font)
    w = max(1, bbox[2] - bbox[0]) + 2 * pad
    h = max(1, bbox[3] - bbox[1]) + 2 * pad
    img = Image.new("RGB", (w, h), "white")
    d = ImageDraw.Draw(img)
    d.text((pad - bbox[0], pad - bbox[1]), text, font=font, fill="black")
    img.save(out_path)
    return True


def probe_font_loadable(font_path):
    """One-shot client-library load of the font, deliberately outside MPT.

    MPT's own ``subtitle_font_supports_sample`` wraps every inspection exception
    and returns ``True`` (app/services/video.py L1189-1192), so a font PIL cannot
    even open would still be reported as "glyphs ok". Fail closed instead:
    ``(True, None)`` loadable, ``(False, exc)`` not loadable,
    ``(None, exc)`` PIL itself unavailable (an environment problem).
    """
    try:
        from PIL import ImageFont
    except Exception as exc:  # noqa: BLE001 — named, never guessed
        return None, repr(exc)
    try:
        ImageFont.truetype(font_path, 84)
    except Exception as exc:  # noqa: BLE001
        return False, repr(exc)
    return True, None


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Fail-closed font hash + glyph check against MPT's own function (05.2.2-T1).")
    ap.add_argument("--font", required=True, help="path to the font file under test")
    ap.add_argument("--text", required=True, help="fixture file (trial/fixtures/vi-glyphs.txt)")
    ap.add_argument("--mpt-root", help="MPT checkout (default $MPT_ROOT or $NTS_HOME/src/mpt)")
    ap.add_argument("--register", default=DEFAULT_REGISTER,
                    help="FONTS.csv path (default worker-node/fonts/FONTS.csv)")
    ap.add_argument("--proof-out", help="optional PNG render of the fixture's last line")
    args = ap.parse_args(argv)

    if not os.path.isfile(args.font):
        eprint("font missing: %s" % args.font)
        return 2
    if not os.path.isfile(args.text):
        eprint("fixture missing: %s" % args.text)
        return 2

    # 1. Register hash guard (fail closed before glyph probing).
    register_path = os.path.realpath(args.register)
    try:
        rows = load_register(register_path)
    except OSError as exc:
        eprint("register unreadable: %s (%s)" % (register_path, exc))
        return 2
    base = os.path.basename(args.font)
    if base in rows:
        expected = rows[base].get("sha256", "").strip()
        actual = sha256_file(args.font)
        if expected != actual:
            print("hash FAIL %s expected=%s actual=%s" % (base, expected, actual))
            return 1
        print("hash ok %s" % base)
    else:
        print("hash skip %s (not in %s)" % (base, register_path))

    # 2. Fail-closed loadability probe, outside MPT: MPT's own function swallows
    #    inspection errors and answers True, so a font it cannot open would
    #    otherwise be reported as "glyphs ok".
    loadable, detail = probe_font_loadable(args.font)
    if loadable is None:
        eprint("PIL unavailable, font not checked: %s" % detail)
        return 2
    if not loadable:
        print("font FAIL %s not loadable: %s" % (base, detail))
        return 1

    # 3. Load MPT's own glyph function; never reimplement it.
    mpt_root = find_mpt_root(args.mpt_root)
    fn, err = load_glyph_function(mpt_root)
    if fn is None:
        eprint(err)
        return 2
    bad_sig = check_signature(fn)
    if bad_sig is not None:
        eprint("signature not (font_path, text): %s" % bad_sig)
        return 2

    # 4. Per-character check (the function itself truncates to 64 L/N chars).
    try:
        text = read_text_nfc(args.text)
    except OSError as exc:
        eprint("fixture unreadable: %s" % exc)
        return 2
    chars = collect_chars(text)
    missing = []
    for ch in chars:
        try:
            ok = fn(args.font, ch)
        except Exception as exc:  # noqa: BLE001 — observed, never guessed
            eprint("glyph check call raised on %r: %r" % (ch, exc))
            return 2
        if not ok:
            missing.append(ch)
    if missing:
        print("missing %d glyph(s): %s" % (len(missing), "".join(missing)))
        return 1
    print("glyphs ok %d/%d" % (len(chars) - len(missing), len(chars)))

    # 5. Optional proof render (never affects the verdict).
    if args.proof_out:
        try:
            sentence = [l for l in text.splitlines() if l][-1]
        except IndexError:
            sentence = ""
        if sentence:
            try:
                os.makedirs(os.path.dirname(os.path.realpath(args.proof_out)) or ".",
                            exist_ok=True)
                if render_proof(args.font, sentence, args.proof_out):
                    print("proof written %s" % args.proof_out)
            except Exception as exc:  # noqa: BLE001
                eprint("proof skip: %r" % exc)
    return 0


if __name__ == "__main__":
    sys.exit(main())
