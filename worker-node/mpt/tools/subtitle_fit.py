#!/usr/bin/env python3
"""subtitle_fit.py — predict MPT's burned-in subtitle lines for a script (05.2.3-T1).

Run with MPT's interpreter so MPT's own helpers are used, never re-implemented
(05 README C15/C16):

    $MPT_ROOT/.venv/bin/python worker-node/mpt/tools/subtitle_fit.py \
        --script FILE --preset landscape|portrait --aspect landscape|portrait [--presets FILE]

Cues = ``utils.split_string_by_punctuations(utils.normalize_script_for_subtitle_matching(script))``
(what ``subtitle.correct`` writes into ``subtitle.srt``); each cue is wrapped by
``app.services.video.wrap_text`` at the preset's ``fontSize`` and 90 % of the frame
width (1728 px landscape, 972 px portrait), minus the background padding MPT
subtracts when a background colour is set (video.py ``create_text_clip``).

Output (stdout, deterministic):
    cue n=<n> chars=<c> lines=<l> longest_line_chars=<m> over_max_lines=<true|false>
    summary cues=<n> max_chars_per_line=<m> max_lines_seen=<l> long_cues=<k>
``over_max_lines`` = lines > maxLines; ``long_cues`` = cues with chars > maxCharsPerLine x maxLines (D6).

Exit codes (01 D9): 0 report written (the tool reports, it never judges) ·
2 usage/environment (bad arguments, unreadable/oversized script or presets, missing
font or MPT config.toml, an MPT helper that cannot be imported — named, fit budget
exceeded). Never 1.

Fit budget (README T82): ``FIT_BUDGET_S`` is armed as ``SIGALRM``/``ITIMER_REAL`` around
the cue loop, so ``fit()`` must run on the main thread. The handler runs only between
bytecodes, so the real bound is the budget plus the longest single C call (one PIL
``getbbox`` inside ``wrap_text``: about 14 s at the 256 KiB script cap, measured).

MPT_ROOT is trusted code: importing ``app.services.video`` runs MPT's config loader,
which reads ``$MPT_ROOT/config.toml``. The tool refuses to run unless that file and
``app/config/config.py`` are regular files under the root (MPT would otherwise create one
elsewhere: the loader resolves config.toml from ``realpath(app/config/__file__)``,
SEC-0523F-2), and routes MPT's log handler to stderr.
"""
import argparse
import json
import math
import os
import signal
import sys
import threading
import time
import unicodedata

REPO_ROOT = os.path.realpath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
DEFAULT_PRESETS = os.path.join(REPO_ROOT, "seed", "subtitle-presets.v1.json")
FRAME_WIDTH = {"landscape": 1920, "portrait": 1080}  # schema.py VideoAspect.to_resolution
MAX_SCRIPT_BYTES = 256 << 10
MAX_PRESETS_BYTES = 64 << 10
FIT_BUDGET_S = 120
PRESET_BOUNDS = {"fontSize": 512, "maxCharsPerLine": 200, "maxLines": 10}
PRESET_NAMES = ("landscape", "portrait")
EXIT_OK, EXIT_ENV = 0, 2


class EnvError(Exception):
    pass


def eprint(msg):
    print(msg, file=sys.stderr)


def find_mpt_root(requested):
    """Prefer --mpt-root, else MPT_ROOT env, else $NTS_HOME/src/mpt."""
    for cand in (requested, os.environ.get("MPT_ROOT")):
        if cand:
            return os.path.realpath(cand)
    nts = os.environ.get("NTS_HOME") or os.path.join(REPO_ROOT, ".nts")
    return os.path.join(nts, "src", "mpt")


def load_mpt_helpers(mpt_root):
    """Import MPT's split/normalize/wrap helpers; EnvError names the missing symbol."""
    if not os.path.isdir(mpt_root):
        raise EnvError("mpt_root not found: %s (see 01.2.2-T1)" % mpt_root)
    config_file = os.path.join(mpt_root, "config.toml")
    if os.path.islink(config_file) or not os.path.isfile(config_file):
        raise EnvError("MPT config missing: %s is not a regular file (render it via 01.2.2-T1)" % config_file)
    # SEC-0523F-2: MPT's loader resolves config.toml from realpath(app/config/__file__), so an `app`
    # imported from elsewhere would read/create a config.toml outside the guard above.
    config_py = os.path.join(mpt_root, "app", "config", "config.py")
    if not os.path.isfile(config_py):
        raise EnvError("MPT app config missing: %s is not a regular file" % config_py)
    root_real = os.path.realpath(mpt_root) + os.sep
    sys.path.insert(0, mpt_root)
    cwd, stdout = os.getcwd(), sys.stdout
    try:
        os.chdir(mpt_root)
        # app.config binds its terminal log handler to sys.stdout at import; keep the report clean.
        sys.stdout = sys.stderr
        helpers = {}
        for module, name in (("app.utils.utils", "split_string_by_punctuations"),
                             ("app.utils.utils", "normalize_script_for_subtitle_matching"),
                             ("app.services.video", "wrap_text")):
            try:
                mod = __import__(module, fromlist=[name])
            except Exception as exc:  # noqa: BLE001 — named, never guessed
                raise EnvError("cannot import %s (needed for %s): %r" % (module, name, exc))
            origin = getattr(mod, "__file__", None)  # SEC-0523F-2: prove the module came from mpt_root
            if not origin or not os.path.realpath(origin).startswith(root_real):
                raise EnvError("MPT module %s imported from %s, not under %s" % (module, origin, mpt_root))
            fn = getattr(mod, name, None)
            if not callable(fn):
                raise EnvError("%s.%s is missing at this MPT checkout" % (module, name))
            helpers[name] = fn
        return helpers
    finally:
        sys.stdout = stdout
        os.chdir(cwd)


def positive_int(value, key):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) \
            or int(value) != value or not 0 < value <= PRESET_BOUNDS[key]:
        raise EnvError("preset key %s must be an integer in 1..%d, got %r" % (key, PRESET_BOUNDS[key], value))
    return int(value)


def reject_constant(name):
    raise ValueError("non-finite number %s" % name)


def load_preset(path, name):
    try:
        if not os.path.isfile(path):
            raise EnvError("presets file missing or not a regular file: %s" % path)
        with open(path, "rb") as handle:
            data = handle.read(MAX_PRESETS_BYTES + 1)
        if len(data) > MAX_PRESETS_BYTES:
            raise EnvError("presets file %s is over the %d-byte cap" % (path, MAX_PRESETS_BYTES))
        doc = json.loads(data.decode("utf-8"), parse_constant=reject_constant)
    except (OSError, ValueError, RecursionError) as exc:
        raise EnvError("cannot read presets file %s: %s" % (path, exc))
    if not isinstance(doc, dict) or doc.get("schema") != "subtitle-presets.v1":
        raise EnvError("%s is not a subtitle-presets.v1 document" % path)
    presets = doc.get("presets")
    preset = presets.get(name) if isinstance(presets, dict) else None
    if not isinstance(preset, dict):
        raise EnvError("%s has no presets.%s block" % (path, name))
    font_name = doc.get("fontName")
    if not isinstance(font_name, str) or not font_name or os.path.basename(font_name) != font_name \
            or font_name in (".", ".."):
        raise EnvError("fontName must be a bare file name, got %r" % (font_name,))
    return {
        "fontName": font_name,
        "fontSize": positive_int(preset.get("fontSize"), "fontSize"),
        "maxCharsPerLine": positive_int(preset.get("maxCharsPerLine"), "maxCharsPerLine"),
        "maxLines": positive_int(preset.get("maxLines"), "maxLines"),
        "textBackgroundColor": preset.get("textBackgroundColor", False),
        "roundedSubtitleBackground": bool(preset.get("roundedSubtitleBackground", False)),
    }


def read_script(path):
    try:
        with open(path, "rb") as handle:
            data = handle.read(MAX_SCRIPT_BYTES + 1)
        if len(data) > MAX_SCRIPT_BYTES:
            raise EnvError("script %s is over the %d-byte cap" % (path, MAX_SCRIPT_BYTES))
        text = data.decode("utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise EnvError("cannot read script %s: %s" % (path, exc))
    # --script is the narration MPT receives as video_script (mpt_client.py build_body
    # strips the story file's title line into video_subject, which is never subtitled).
    # build_body sends NFC text, so the cues MPT sees are NFC.
    return unicodedata.normalize("NFC", text)


def text_max_width(aspect, preset):
    """video.py create_text_clip: 90 % width, minus 2 x pad_x when a background is drawn."""
    max_width = FRAME_WIDTH[aspect] * 0.9
    has_background = bool(preset["textBackgroundColor"])
    rounded = preset["roundedSubtitleBackground"] and has_background
    pad_x = int(preset["fontSize"] * (0.4 if rounded else 0.6)) if has_background else 0
    return max(1, int(max_width) - 2 * pad_x)


def fit(script, preset, aspect, helpers, font_path, budget_s=FIT_BUDGET_S):
    def on_alarm(_signum, _frame):
        if cues is None:
            raise EnvError("fit budget of %g s exceeded while splitting the script into cues" % budget_s)
        raise EnvError("fit budget of %g s exceeded inside cue %d of %d" % (budget_s, len(rows) + 1, len(cues)))

    if threading.current_thread() is not threading.main_thread():
        raise EnvError("fit() must run on the main thread (SIGALRM fit budget, README T82)")
    rows, cues = [], None
    previous = signal.signal(signal.SIGALRM, on_alarm)
    try:
        signal.setitimer(signal.ITIMER_REAL, max(budget_s, 0.001))
        cues = helpers["split_string_by_punctuations"](helpers["normalize_script_for_subtitle_matching"](script))
        width = text_max_width(aspect, preset)
        deadline = time.monotonic() + budget_s
        for n, cue in enumerate(cues, 1):
            if time.monotonic() > deadline:
                raise EnvError("fit budget of %g s exceeded at cue %d of %d" % (budget_s, n, len(cues)))
            wrapped, _height = helpers["wrap_text"](cue, max_width=width, font=font_path,
                                                    fontsize=preset["fontSize"])
            lines = wrapped.split("\n")
            rows.append({
                "n": n,
                "chars": len(cue),
                "lines": len(lines),
                "longest_line_chars": max(len(line) for line in lines),
            })
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser(description="Predict MPT subtitle line wraps for a script (05.2.3-T1).")
    ap.add_argument("--script", required=True,
                    help="narration text file (UTF-8) as sent in video_script; not a story file with its title line")
    ap.add_argument("--preset", required=True, choices=PRESET_NAMES, help="preset block in the presets file")
    ap.add_argument("--aspect", required=True, choices=PRESET_NAMES, help="frame: landscape 1920 / portrait 1080 wide")
    ap.add_argument("--presets", default=DEFAULT_PRESETS, help="subtitle-presets.v1 file (default seed/)")
    ap.add_argument("--mpt-root", help="MPT checkout (default $MPT_ROOT or $NTS_HOME/src/mpt)")
    args = ap.parse_args(argv)

    try:
        if not os.path.isfile(args.script):
            raise EnvError("script missing: %s" % args.script)
        script = read_script(args.script)
        preset = load_preset(args.presets, args.preset)
        mpt_root = find_mpt_root(args.mpt_root)
        font_path = os.path.join(mpt_root, "resource", "fonts", preset["fontName"])
        if not os.path.isfile(font_path):
            raise EnvError("font missing: %s (05.2.2-T1)" % font_path)
        helpers = load_mpt_helpers(mpt_root)
        rows = fit(script, preset, args.aspect, helpers, font_path)
    except EnvError as exc:
        eprint("subtitle_fit.py: %s" % exc)
        return EXIT_ENV
    except Exception as exc:  # noqa: BLE001 — an MPT helper raised; environment, not a verdict
        eprint("subtitle_fit.py: MPT helper failed: %s: %s" % (type(exc).__name__, exc))
        return EXIT_ENV

    cap_line, cap_lines = preset["maxCharsPerLine"], preset["maxLines"]
    out = []
    for row in rows:
        out.append("cue n=%d chars=%d lines=%d longest_line_chars=%d over_max_lines=%s" % (
            row["n"], row["chars"], row["lines"], row["longest_line_chars"],
            "true" if row["lines"] > cap_lines else "false"))
    out.append("summary cues=%d max_chars_per_line=%d max_lines_seen=%d long_cues=%d" % (
        len(rows),
        max((r["longest_line_chars"] for r in rows), default=0),
        max((r["lines"] for r in rows), default=0),
        sum(1 for r in rows if r["chars"] > cap_line * cap_lines)))
    print("\n".join(out))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
