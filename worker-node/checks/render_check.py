#!/usr/bin/env python3
"""render_check.py — measure a finished MPT task dir and emit `render-check.v1`
(D9, 05.5.1-T1; contract README §3.1, D6, D14, D15 P2/P3).

Usage
    uv run --no-project --python 3.12 worker-node/checks/render_check.py run \\
        --task-dir DIR --aspect landscape|portrait --subtitles on|off \\
        [--normalize] [--presets FILE] [--out FILE]
    uv run --no-project --python 3.12 worker-node/checks/render_check.py --self-test [--keep]

`run` writes the report to `--out` (default `<task-dir>/render-check.json`) and
also prints it, one JSON object, on **stdout** (the M0 client `mpt_client.py`
`run` does `json.loads(proc.stdout)`); every diagnostic goes to stderr.

Exit codes (01 D9): 0 report written (pass/fail is **not** decided here)
                    1 input unreadable — report still written, `errors` non-empty
                    2 usage/environment error (bad arguments, ffmpeg/ffprobe
                      absent, an explicit `--presets` file that does not exist,
                      an `--out` that is a symlink or cannot be written)

Steps 1–7 (D9): ffprobe container → narration duration → loudnorm pass 1 →
(optional) two-pass normalisation to −15 LUFS / −1.5 dBTP with the video stream
copied, re-measured with `ebur128=peak=true` → `silencedetect` → SRT parse →
script text match through a faithful port of MPT's
`normalize_script_for_subtitle_matching` + `split_string_by_punctuations`
(`app/utils/utils.py` @ MPT_COMMIT 3d5f4e421927d61f3eac729cf4b711ac0b69d688).

Every subprocess is `subprocess.run([...])` with list arguments, a timeout and a
minimal allow-listed environment (PATH, HOME, LANG, LC_ALL, TMPDIR) — no shell,
and no `*KEY*`/`*TOKEN*` variable is ever inherited; ffmpeg stderr is streamed to a
file in a private temp dir, never buffered. Each task-dir input is opened with
`O_NOFOLLOW`, must be a regular file, and must resolve inside the task dir. The
report carries counts, durations, hashes and booleans only: never script text,
never a host path.
"""

import argparse
import contextlib
import hashlib
import io
import json
import math
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import traceback
import unicodedata

SCHEMA = "render-check.v1"
TOOL_VERSION = "1.0.0"

# D9 module constants (T3): a change bumps TOOL_VERSION after 05.2.1-T1.
MAX_CPS = 25
MAX_CUE_S = 12.0
ZERO_CUE_DIVISOR = 0.05

VIDEO_FILE = "final-1.mp4"
NORMALIZED_FILE = "final-1.norm.mp4"
AUDIO_FILE = "audio.mp3"
SRT_FILE = "subtitle.srt"
SCRIPT_FILE = "script.json"
REPORT_FILE = "render-check.json"

PROBE_TIMEOUT = 60
MIN_FFMPEG_TIMEOUT = 120
# PRF-0551-1/SEC-0551-1: the run-wide ffmpeg budget, under the adapter's
# Adapter:CheckTimeoutSeconds = 900 s (11.3.5-T3:36). Every ffmpeg step's
# timeout is min(its own formula, the budget left); the budget covers all four
# measurements of one run.
RUN_BUDGET_S = 840
# A duration outside this is a bogus container, not a render (SEC-0551-1).
MAX_DURATION_S = 14400.0  # 4 h
# Loudnorm/ebur128 JSON sits at the end of stderr; only this tail is read
# (PRF-0551-3/SEC-0551-6).
STDERR_TAIL_BYTES = 256 * 1024
# SEC-0551-8: in-process reads are capped by fstat before the read.
SRT_MAX_BYTES = 16 << 20
SCRIPT_MAX_BYTES = 4 << 20
O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)

LOUDNESS_TARGET = "I=-15:TP=-1.5:LRA=11"
LOUDNESS_FILTER = "loudnorm=" + LOUDNESS_TARGET
SILENCE_FILTER = "silencedetect=noise=-50dB:d=2"
HASH_CHUNK = 1 << 20

# SEC-0551-2: the parsers are anchored on ffmpeg's own log prefix and every
# measured value must be finite and inside a sane range. `run -af` makes ffmpeg
# print `[Parsed_<filter>_<n> @ 0x…]`, so the prefix is matched with the
# `Parsed_`/`_<n>` part optional (the bare form is accepted too).
LOUDNORM_MARKER = "[Parsed_loudnorm_"
EBUR128_MARKER = "[Parsed_ebur128_"
# ffmpeg prints `[Parsed_silencedetect_0 @ 0x…] silence_start: 0` and
# `… ] silence_end: 6 | silence_duration: 6`; the line must carry the filter's
# own anchored log prefix before any `silence_duration` in it is believed.
SILENCE_PREFIX_RE = re.compile(
    r"^\[(?:Parsed_)?silencedetect(?:_\d+)? @ 0x[0-9a-f]+\] ")
SILENCE_DURATION_RE = re.compile(r"silence_duration:\s*(-?\d+(?:\.\d+)?)")
# measured_* / summary value ranges (SEC-0551-2): a value outside its range is
# a poisoned or unparsable measurement, never a number to report.
LOUDNESS_RANGES = {
    "input_i": (-99.0, 0.0),
    "input_tp": (-99.0, 99.0),
    "input_lra": (0.0, 99.0),
    "input_thresh": (-99.0, 0.0),
    "target_offset": (-99.0, 99.0),
}
AFTER_RANGES = {"integrated_lufs": (-99.0, 0.0), "true_peak_dbtp": (-99.0, 99.0),
                "lra": (0.0, 99.0)}

# D6 inline fallback (05.2.3-T1 owns seed/subtitle-presets.v1.json; the same
# "d6-inline" fallback `mpt_client.py load_preset_file` uses).
D6_PRESETS = {
    "landscape": {"maxCharsPerLine": 42, "maxLines": 2},
    "portrait": {"maxCharsPerLine": 28, "maxLines": 2},
}
PRESETS_FALLBACK_WARNING = "presets_missing_d6_inline"
DEFAULT_PRESETS_REL = os.path.join("seed", "subtitle-presets.v1.json")

# --------------------------------------------------------------------------- #
# Port of MPT's chunking functions (verbatim behaviour, @3d5f4e42).
# `app/models/const.py` PUNCTUATIONS, `app/utils/utils.py`
# PAUSE_TAG_KEYWORDS/PAUSE_TAG_PATTERN/remove_pause_tags/
# normalize_script_for_subtitle_matching/split_string_by_punctuations.
# --------------------------------------------------------------------------- #
PUNCTUATIONS = [
    "?", ",", ".", "、", ";", ":", "!", "…",
    "？", "，", "。", "、", "；", "：", "！", "...",
    "،", "؛", "؟",
]

PAUSE_TAG_KEYWORDS = (
    r"pause|pausa|silence|silencio|silêncio|silenzio|stille|"
    r"пауза|тишина|停顿|暂停|静音|ポーズ|一時停止|無音|일시중지|정지"
)
PAUSE_TAG_PATTERN = re.compile(
    r"[\[\(]\s*(?:%s)\b(?:\s*[:：]?\s*([^\]\)]*?))?\s*[\]\)]" % PAUSE_TAG_KEYWORDS,
    re.IGNORECASE,
)
MARKDOWN_SEPARATOR_RE = re.compile(r"[-*_]{3,}")


def ported_remove_pause_tags(text):
    """MPT `remove_pause_tags` (utils.py L349): drop pause tags, collapse blanks."""
    if not text:
        return ""
    cleaned = PAUSE_TAG_PATTERN.sub(" ", text)
    cleaned = re.sub(r"[ \t]+", " ", cleaned)
    return cleaned.strip()


def ported_normalize_script_for_subtitle_matching(video_script):
    """MPT `normalize_script_for_subtitle_matching` (utils.py L451)."""
    video_script = ported_remove_pause_tags(video_script or "")
    video_script = video_script.replace("_", "")
    cleaned_lines = []
    for line in video_script.splitlines():
        line = line.strip()
        if MARKDOWN_SEPARATOR_RE.fullmatch(line):
            continue
        cleaned_lines.append(line)
    return "\n".join(cleaned_lines).strip()


def ported_split_string_by_punctuations(s):
    """MPT `split_string_by_punctuations` (utils.py L281): split on newlines and
    on `const.PUNCTUATIONS`, keeping the digit `.`/`,` of "2.5"/"1,000"."""
    result = []
    txt = ""
    previous_char = ""
    next_char = ""
    for i in range(len(s)):
        char = s[i]
        if char == "\n":
            result.append(txt.strip())
            txt = ""
            continue
        if i > 0:
            previous_char = s[i - 1]
        if i < len(s) - 1:
            next_char = s[i + 1]
        if char == "." and previous_char.isdigit() and next_char.isdigit():
            txt += char
            continue
        if char == "," and previous_char.isdigit() and next_char.isdigit():
            txt += char
            continue
        if char not in PUNCTUATIONS:
            txt += char
        else:
            result.append(txt.strip())
            txt = ""
    result.append(txt.strip())
    return [chunk for chunk in result if chunk]


PUNCTUATION_TRIM = "".join(sorted(set(PUNCTUATIONS)))


class EnvError(Exception):
    """Usage/environment failure → exit 2, no partial report."""


# --------------------------------------------------------------------------- #
# Subprocess helpers (list argv, explicit timeout, allow-listed environment).
# --------------------------------------------------------------------------- #
def minimal_env():
    env = {}
    for key in ("PATH", "HOME", "LANG", "LC_ALL", "TMPDIR"):
        if key in os.environ:
            env[key] = os.environ[key]
    env.setdefault("LC_ALL", "C")
    env.setdefault("PATH", os.defpath)
    return env


def resolve_tool(name):
    found = shutil.which(name, path=os.environ.get("PATH"))
    if not found:
        raise EnvError("%s not found on PATH (ffmpeg and ffprobe are required)" % name)
    return found


def run_cmd(argv, timeout):
    """Run argv (a list; never a shell). Returns (rc, stdout, stderr, timed_out)."""
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=timeout, env=minimal_env())
    except subprocess.TimeoutExpired:
        return None, "", "", True
    except OSError as exc:
        raise EnvError("cannot run %s: %s" % (argv[0], exc))
    return proc.returncode, proc.stdout, proc.stderr, False


def run_cmd_bytes(argv, timeout):
    """Binary-output variant of `run_cmd` (the H.264 bitstream hash of X3)."""
    try:
        proc = subprocess.run(argv, capture_output=True, timeout=timeout, env=minimal_env())
    except subprocess.TimeoutExpired:
        return None, b"", b"", True
    except OSError as exc:
        raise EnvError("cannot run %s: %s" % (argv[0], exc))
    return proc.returncode, proc.stdout, proc.stderr, False


def run_cmd_log(argv, timeout, log_path):
    """Run argv with stdout discarded and **stderr streamed to `log_path`**.

    PRF-0551-3/SEC-0551-6: a decode of a long (or hostile) file can emit
    unbounded stderr, so it is never buffered in memory. Returns
    `(rc, timed_out)`.
    """
    try:
        with open(log_path, "wb") as handle:
            proc = subprocess.run(argv, stdout=subprocess.DEVNULL, stderr=handle,
                                  stdin=subprocess.DEVNULL, timeout=timeout,
                                  env=minimal_env())
    except subprocess.TimeoutExpired:
        return None, True
    except OSError as exc:
        raise EnvError("cannot run %s: %s" % (argv[0], exc))
    return proc.returncode, False


def read_tail(path, limit=STDERR_TAIL_BYTES):
    """The last `limit` bytes of `path` (the loudnorm/ebur128 JSON tail)."""
    try:
        size = os.path.getsize(path)
        with open(path, "rb") as handle:
            if size > limit:
                handle.seek(size - limit)
            return handle.read().decode("utf-8", "replace")
    except OSError:
        return ""


def stream_lines(path):
    """Yield the lines of `path` without materialising the file."""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                yield line
    except OSError:
        return


def following_json(text, marker):
    """The `{…}` object that directly follows the last `marker` line.

    SEC-0551-2: loudnorm/ebur128 print their JSON/summary right after their own
    `[Parsed_…]` line, so a value planted in the container's Metadata block
    (which ffmpeg echoes earlier on stderr) can never be picked up.
    """
    index = text.rfind(marker)
    if index < 0:
        return None
    chunk = text[index:]
    newline = chunk.find("\n")
    if newline < 0:
        return None
    return last_json_object(chunk[newline:])


def following_section(text, marker):
    """The text after the last `marker`, or "" when the marker is absent."""
    index = text.rfind(marker)
    return text[index:] if index >= 0 else ""


def ffmpeg_timeout(duration_s):
    if not duration_s or duration_s <= 0:
        return MIN_FFMPEG_TIMEOUT
    return max(MIN_FFMPEG_TIMEOUT, int(2 * duration_s) + 1)


def budgeted_timeout(wanted, remaining):
    """`min(wanted, remaining)` seconds, or None when the budget is spent.

    PRF-0551-1: the run-wide budget is the adapter's slack, so a step can never
    outlive `RUN_BUDGET_S`; an exhausted budget records `ffmpeg_timeout` and
    skips the remaining ffmpeg steps.
    """
    if remaining <= 1.0:
        return None
    return max(1, int(min(wanted, remaining)))


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="render_check.py",
        description="measure an MPT task dir and emit render-check.v1 (D9)")
    parser.add_argument("--self-test", action="store_true",
                        help="run the built-in cases over lavfi media (exit 1 on any failure)")
    parser.add_argument("--keep", action="store_true",
                        help="--self-test: keep the temp dir and print its path")
    sub = parser.add_subparsers(dest="command")
    run = sub.add_parser("run", help="measure one task dir")
    run.add_argument("--task-dir", required=True)
    run.add_argument("--aspect", required=True, choices=("landscape", "portrait"))
    run.add_argument("--subtitles", required=True, choices=("on", "off"))
    run.add_argument("--normalize", action="store_true",
                     help="two-pass loudnorm to -15 LUFS / -1.5 dBTP, video stream copied")
    run.add_argument("--presets", default=None,
                     help="subtitle-presets.v1 file (default <repo>/%s)" % DEFAULT_PRESETS_REL)
    run.add_argument("--out", default=None, help="report path (default <task-dir>/%s)" % REPORT_FILE)
    return parser.parse_args(argv)


# --------------------------------------------------------------------------- #
# Presets (D6).
# --------------------------------------------------------------------------- #
def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def preset_char_cap(path, aspect, explicit):
    """Read `<maxCharsPerLine> x <maxLines>` for `aspect`; D6 inline when the file is absent."""
    if not path:
        path = os.path.join(repo_root(), DEFAULT_PRESETS_REL)
    if not os.path.exists(path):
        if explicit:
            raise EnvError("--presets file does not exist: %s" % path)
        preset = D6_PRESETS[aspect]
        return preset["maxCharsPerLine"] * preset["maxLines"], PRESETS_FALLBACK_WARNING
    try:
        with open(path, encoding="utf-8") as handle:
            doc = json.load(handle)
    except (OSError, ValueError) as exc:
        raise EnvError("cannot read --presets file %s: %s" % (path, exc))
    presets = doc.get("presets")
    preset = presets.get(aspect) if isinstance(presets, dict) else None
    if not isinstance(preset, dict):
        raise EnvError("--presets file %s has no presets.%s block" % (path, aspect))
    try:
        cap = int(preset["maxCharsPerLine"]) * int(preset["maxLines"])
    except (KeyError, TypeError, ValueError):
        raise EnvError("--presets presets.%s needs integer maxCharsPerLine/maxLines" % aspect)
    if cap <= 0:
        raise EnvError("--presets presets.%s gives a non-positive cue cap" % aspect)
    return cap, None


# --------------------------------------------------------------------------- #
# Small helpers.
# --------------------------------------------------------------------------- #
def sha256_file(path):
    """P3: hash in 1 MiB chunks, never by reading the whole file."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        while True:
            block = handle.read(HASH_CHUNK)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def file_size(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return None


def dedupe(items):
    """First-occurrence order, no repeats (QA-0551-5: `errors` holds codes)."""
    seen = set()
    unique = []
    for item in items:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def sha256_fd(fd):
    """P3/SEC-0551-5: hash a file through its already-open fd, in 1 MiB chunks."""
    digest = hashlib.sha256()
    os.lseek(fd, 0, os.SEEK_SET)
    while True:
        block = os.read(fd, HASH_CHUNK)
        if not block:
            break
        digest.update(block)
    return digest.hexdigest()


def bounded_float(value, low, high):
    """`float(value)` when finite and inside [low, high], else None (SEC-0551-2)."""
    number = to_float(value)
    if number is None or not math.isfinite(number) or number < low or number > high:
        return None
    return number


def open_task_input(task_dir, name):
    """Open an exact task-dir input without following links (QA-0551-2/SEC-0551-4).

    Returns `(fd, path, status)` with status one of
      `ok`         — `fd` is open on a regular file whose real path is inside
                     `task_dir`; the caller closes it
      `missing`    — the name does not exist
      `unreadable` — it exists but cannot be opened (EACCES, ...), or it is not
                     a regular file / escapes the task dir through its parents
      `unsafe`     — `O_NOFOLLOW` or the containment check refused a symlink
                     (or any other non-regular file)
    """
    path = os.path.join(task_dir, name)
    try:
        fd = os.open(path, os.O_RDONLY | O_NOFOLLOW)
    except FileNotFoundError:
        return None, path, "missing"
    except OSError:
        # O_NOFOLLOW refuses a symlink (ELOOP); anything else that exists but
        # cannot be opened (EACCES) is "unreadable".
        try:
            info = os.lstat(path)
        except OSError:
            return None, path, "missing"
        return None, path, "unsafe" if not stat.S_ISREG(info.st_mode) else "unreadable"
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode):
            os.close(fd)
            return None, path, "unsafe"
        root = os.path.realpath(task_dir) + os.sep
        if not os.path.realpath(path).startswith(root):
            os.close(fd)
            return None, path, "unsafe"
    except OSError:
        os.close(fd)
        return None, path, "unreadable"
    return fd, path, "ok"


def input_error_codes(status, missing_code, hard=False):
    """The error codes for a refused task-dir input (QA-0551-1/2).

    The input's own code is always used (so a refusal reads like an absent
    input); `input_unreadable` is added when the file exists but cannot be read,
    and — for the required container (`hard=True`) — also when it is a symlink
    or another non-regular file.
    """
    codes = [missing_code]
    if status == "unreadable" or (hard and status == "unsafe"):
        codes.append("input_unreadable")
    return codes


def input_still_ok(task_dir, name):
    """True when `name` is still a readable regular file inside the task dir."""
    fd, _path, status = open_task_input(task_dir, name)
    if status != "ok":
        return False
    os.close(fd)
    return True


def read_task_text(task_dir, name, max_bytes, encoding="utf-8"):
    """Read a small fixed-name task-dir file through its own fd (SEC-0551-4/8).

    Returns `(text, status)`; status is `ok`, or `missing` / `unreadable` /
    `unsafe` from `open_task_input`, or `too_large` (fstat says the file is over
    `max_bytes`, so it is never read into memory) or `unparsable` (bad text).
    """
    fd, _path, status = open_task_input(task_dir, name)
    if status != "ok":
        return None, status
    raw = None
    try:
        if os.fstat(fd).st_size > max_bytes:
            return None, "too_large"
        with os.fdopen(fd, "rb") as handle:
            fd = None
            raw = handle.read()
    except OSError:
        return None, "unreadable"
    finally:
        if fd is not None:
            os.close(fd)
    try:
        return raw.decode(encoding), "ok"
    except UnicodeDecodeError:
        return None, "unparsable"


def loudness_measure(block):
    """The five pass-1 measured values, finite and in range, else None.

    SEC-0551-2: pass 2's filter graph is built only from values that survived
    this, so a poisoned or unparsable block can never reach ffmpeg.
    """
    if not isinstance(block, dict):
        return None
    values = {}
    for key, (low, high) in LOUDNESS_RANGES.items():
        value = bounded_float(block.get(key), low, high)
        if value is None:
            return None
        values[key] = value
    return values


def silence_durations(log_path):
    """Non-negative `silence_duration` values ffmpeg's silencedetect logged."""
    for line in stream_lines(log_path):
        if not SILENCE_PREFIX_RE.match(line):
            continue
        match = SILENCE_DURATION_RE.search(line)
        if not match:
            continue
        value = to_float(match.group(1))
        if value is not None and math.isfinite(value) and value >= 0:
            yield value


def last_json_object(text):
    """The last balanced `{…}` block of `text` that parses (loudnorm's JSON)."""
    decoder = json.JSONDecoder()
    for start in range(len(text) - 1, -1, -1):
        if text[start] != "{":
            continue
        try:
            obj, _end = decoder.raw_decode(text[start:])
        except ValueError:
            continue
        if isinstance(obj, dict):
            return obj
    return None


def to_float(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def round_or_none(value, digits):
    return None if value is None else round(value, digits)


def collapse_ws(text):
    return " ".join(text.split())


def strip_trailing_punctuation(text):
    return text.rstrip().rstrip(PUNCTUATION_TRIM + " ").strip()


def faststart(path):
    """True when the top-level `moov` box precedes `mdat` (fragmented layout → False)."""
    try:
        with open(path, "rb") as handle:
            order = []
            offset = 0
            size = file_size(path) or 0
            while offset + 8 <= size and len(order) < 64:
                handle.seek(offset)
                header = handle.read(16)
                if len(header) < 8:
                    break
                box_size = int.from_bytes(header[0:4], "big")
                box_type = header[4:8].decode("latin-1")
                order.append(box_type)
                if box_size == 1:
                    if len(header) < 16:
                        break
                    box_size = int.from_bytes(header[8:16], "big")
                if box_size <= 0:
                    break
                offset += box_size
    except OSError:
        return None
    if "moov" not in order:
        return False
    if "mdat" not in order:
        return True
    return order.index("moov") < order.index("mdat")


# --------------------------------------------------------------------------- #
# SRT parsing (step 6).
# --------------------------------------------------------------------------- #
# QA-0551-3: a cue time line is matched whole (anchored), so a negative sign, a
# 3-digit hour field or an out-of-range minute/second is *invalid* — counted and
# excluded, never sign-flipped and never silently dropped.
TIME_LINE_RE = re.compile(
    r"^\s*(\d{1,2}):([0-5]\d):([0-5]\d)[,.](\d{1,3})\s*-->\s*"
    r"(\d{1,2}):([0-5]\d):([0-5]\d)[,.](\d{1,3})\s*$")
TIME_LINE_HINT_RE = re.compile(r"-->")


def _seconds(hours, minutes, seconds, millis):
    return (int(hours) * 3600 + int(minutes) * 60 + int(seconds)
            + int(millis.ljust(3, "0")) / 1000.0)


def parse_srt(raw):
    """Parse SRT text into `(cues, invalid)`.

    `cues` is `[(start_s, end_s, text)]`; `invalid` counts the cue blocks whose
    time line is malformed (bad shape, > 99 h, negative, or end < start). UTF-8
    BOM and CRLF are tolerated.
    """
    text = raw.lstrip("\ufeff").replace("\r\n", "\n").replace("\r", "\n")
    cues = []
    invalid = 0
    for block in re.split(r"\n[ \t]*\n", text):
        lines = [line for line in block.split("\n")] if block.strip() else []
        match = None
        index = None
        for position, line in enumerate(lines):
            if TIME_LINE_HINT_RE.search(line):
                match = TIME_LINE_RE.match(line)
                index = position
                break
        if match is None:
            # a block that carries an arrow but no valid time line is a cue the
            # tool cannot measure: count it, do not drop it silently
            if any(TIME_LINE_HINT_RE.search(line) for line in lines):
                invalid += 1
            continue
        start = _seconds(*match.group(1, 2, 3, 4))
        end = _seconds(*match.group(5, 6, 7, 8))
        if end < start:
            invalid += 1
            continue
        cue_lines = [line for line in lines[index + 1:]]
        while cue_lines and not cue_lines[-1].strip():
            cue_lines.pop()
        cues.append((start, end, "\n".join(cue_lines)))
    return cues, invalid


def empty_subtitle_block():
    return {
        "present": False, "cues": 0, "zero_duration": 0, "overlapping": 0,
        "non_monotonic": 0, "out_of_range": 0, "coverage_share": 0.0,
        "max_cue_chars": 0, "long_cues": 0, "cps_p95": 0.0, "sync_suspect": 0,
        "text_matches_script": None, "text_mismatch_cues": 0,
    }


def union_seconds(intervals):
    """Total length covered by the union of `[(start, end)]` (CRV-0551-2/D9).

    Overlapping cues must not count twice: D9 pins `coverage_share` as the
    *union* of cue time ÷ narration duration.
    """
    total = 0.0
    current_end = None
    for start, end in sorted(intervals):
        if end <= start:
            continue
        if current_end is None or start > current_end:
            total += end - start
            current_end = end
        elif end > current_end:
            total += end - current_end
            current_end = end
    return total


def subtitle_metrics(cues, ref_duration, narration_duration, cue_cap):
    """Computed fields of the `subtitles` block (D9/§3.1)."""
    block = empty_subtitle_block()
    block["present"] = True
    block["cues"] = len(cues)
    durations = [end - start for start, end, _text in cues]
    block["zero_duration"] = sum(1 for value in durations if value == 0.0)
    block["overlapping"] = sum(
        1 for i in range(1, len(cues)) if cues[i][0] < cues[i - 1][1])
    block["non_monotonic"] = sum(
        1 for i in range(1, len(cues)) if cues[i][0] < cues[i - 1][0])
    base = ref_duration if ref_duration is not None else narration_duration
    if base is not None:
        block["out_of_range"] = sum(1 for _s, end, _t in cues if end > base + 0.5)
    covered = union_seconds([(start, end) for start, end, _t in cues])
    coverage_base = narration_duration if narration_duration else ref_duration
    if coverage_base:
        block["coverage_share"] = round(min(1.0, covered / coverage_base), 4)
    chars = [len(collapse_ws(text)) for _s, _e, text in cues]
    block["max_cue_chars"] = max(chars) if chars else 0
    block["long_cues"] = sum(1 for value in chars if value > cue_cap)
    cps = [chars[i] / max(durations[i], ZERO_CUE_DIVISOR) for i in range(len(cues))]
    if cps:
        ordered = sorted(cps)
        rank = max(0, -(-len(ordered) * 95 // 100) - 1)
        block["cps_p95"] = round(ordered[rank], 2)
    block["sync_suspect"] = sum(
        1 for i in range(len(cues))
        if cps[i] > MAX_CPS or durations[i] > MAX_CUE_S)
    return block


def match_script(cues, script_body):
    """Compare cue i against script chunk i (C15: MPT's sentence mode assigns them by index)."""
    chunks = ported_split_string_by_punctuations(
        ported_normalize_script_for_subtitle_matching(script_body))
    total = max(len(cues), len(chunks))

    def cue_form(index):
        if index >= len(cues):
            return ""
        text = unicodedata.normalize("NFC", collapse_ws(cues[index][2]))
        return strip_trailing_punctuation(text)

    def chunk_form(index):
        if index >= len(chunks):
            return ""
        return unicodedata.normalize("NFC", collapse_ws(chunks[index]))

    mismatches = sum(1 for i in range(total) if cue_form(i) != chunk_form(i))
    return mismatches, len(chunks)


# --------------------------------------------------------------------------- #
# The check itself.
# --------------------------------------------------------------------------- #
class Context(object):
    def __init__(self, task_dir, aspect, subtitles, normalize, cue_cap,
                 presets_warning, ffmpeg, ffprobe):
        self.task_dir = os.path.abspath(task_dir)
        self.aspect = aspect
        self.subtitles = subtitles
        self.normalize = normalize
        self.cue_cap = cue_cap
        self.presets_warning = presets_warning
        self.ffmpeg = ffmpeg
        self.ffprobe = ffprobe


def build_context(task_dir, aspect, subtitles, normalize, presets, out=None):
    if not os.path.isdir(task_dir):
        raise EnvError("--task-dir is not a directory: %s" % task_dir)
    explicit = presets is not None
    cap, warning = preset_char_cap(presets, aspect, explicit)
    return Context(task_dir, aspect, subtitles, normalize, cap, warning,
                   resolve_tool("ffmpeg"), resolve_tool("ffprobe"))


def probe_json(ffprobe, path, show_streams, errors, timeout=PROBE_TIMEOUT):
    """ffprobe -of json; returns (ok, parsed). Appends probe_timeout on timeout."""
    argv = [ffprobe, "-v", "error"]
    if show_streams:
        argv += ["-show_format", "-show_streams"]
    else:
        argv += ["-show_entries", "format=duration"]
    argv += ["-of", "json", path]
    rc, out, _err, timed_out = run_cmd(argv, timeout)
    if timed_out:
        errors.append("probe_timeout")
        return False, None
    if rc != 0:
        return False, None
    try:
        parsed = json.loads(out)
    except ValueError:
        return False, None
    # SEC-0551-7: a non-object payload (a list, a bare number) has no `.get`.
    return (True, parsed) if isinstance(parsed, dict) else (False, None)


def video_block(stream):
    fps = None
    rate = stream.get("r_frame_rate") or ""
    if "/" in rate:
        num, _, den = rate.partition("/")
        try:
            value = float(num) / float(den) if float(den) else None
            # a non-finite fps is not JSON (and not a real frame rate)
            fps = round(value, 3) if value is not None and math.isfinite(value) else None
        except ValueError:
            fps = None
    return {
        "codec": stream.get("codec_name"),
        "pix_fmt": stream.get("pix_fmt"),
        "width": stream.get("width"),
        "height": stream.get("height"),
        "fps": fps,
    }


def audio_block(stream):
    try:
        sample_rate = int(stream["sample_rate"]) if stream.get("sample_rate") else None
    except (TypeError, ValueError):
        sample_rate = None
    return {
        "codec": stream.get("codec_name"),
        "sample_rate": sample_rate,
        "channels": stream.get("channels"),
    }


def minimal_report():
    """The report written when `run_check` dies unexpectedly (CRV-0551-1).

    Every block is `null`, so a consumer (D10) fails closed on `errors` alone.
    """
    return {
        "schema": SCHEMA,
        "tool_version": TOOL_VERSION,
        "ffmpeg_version": None,
        "input": {"file": VIDEO_FILE, "bytes": None, "sha256": None},
        "output": None,
        "container": None,
        "video": None,
        "audio": None,
        "narration": None,
        "loudness": None,
        "silence": None,
        "subtitles": None,
        "warnings": [],
        "errors": ["internal_error"],
    }


def run_check(ctx):
    """Returns (report, exit_code). The report is always complete (T65 nulls).

    The shell, `ffmpeg -version` and the private stderr log dir live here; the
    D9 steps are `run_steps`. `errors`/`warnings` are de-duplicated at the end
    (QA-0551-5), so the exit code is "any error".
    """
    report = {
        "schema": SCHEMA,
        "tool_version": TOOL_VERSION,
        "ffmpeg_version": None,
        "input": {"file": VIDEO_FILE, "bytes": None, "sha256": None},
        "output": None,
        "container": None,
        "video": None,
        "audio": None,
        "narration": None,
        "loudness": None,
        "silence": None,
        "subtitles": None,
        "warnings": [],
        "errors": [],
    }
    if ctx.presets_warning:
        report["warnings"].append(ctx.presets_warning)
    rc, out, _err, timed_out = run_cmd([ctx.ffmpeg, "-version"], 30)
    first_line = (out or "").splitlines()[0] if out else ""
    if timed_out or rc != 0 or not first_line:
        raise EnvError("cannot run %s -version" % ctx.ffmpeg)
    match = re.search(r"ffmpeg version (\S+)", first_line)
    # SEC-0551-9: never fall back to the whole banner line (a build prefix can
    # carry a host path, and the report promises to be path-free).
    report["ffmpeg_version"] = match.group(1) if match else "unknown"

    # PRF-0551-3/SEC-0551-6: ffmpeg stderr goes to a private 0700 dir, never to
    # memory; the dir is removed whatever the steps do.
    log_dir = tempfile.mkdtemp(prefix="render-check-log-")
    try:
        run_steps(ctx, report, log_dir)
    finally:
        shutil.rmtree(log_dir, ignore_errors=True)
    report["errors"] = dedupe(report["errors"])
    report["warnings"] = dedupe(report["warnings"])
    return report, 1 if report["errors"] else 0


def run_steps(ctx, report, log_dir):
    """D9 steps 1–7. Every early `return` fails closed through `report["errors"]`."""
    errors = report["errors"]
    warnings = report["warnings"]

    # SEC-0551-5: the container is opened once — O_NOFOLLOW, regular file,
    # realpath inside the task dir — and hashed through that same fd. Probing
    # `src` by path again is a documented residual gap (README Decisions).
    fd, src, status = open_task_input(ctx.task_dir, VIDEO_FILE)
    if status != "ok":
        errors.extend(input_error_codes(status, "container_invalid", hard=True))
        return
    try:
        info = os.fstat(fd)
        start_hash = sha256_fd(fd)
    finally:
        os.close(fd)
    report["input"]["bytes"] = info.st_size
    report["input"]["sha256"] = start_hash
    start_stat = (info.st_ino, info.st_size, info.st_mtime_ns)

    # PRF-0551-1/SEC-0551-1: one run-wide ffmpeg budget under the adapter's
    # 900 s check timeout; a step never gets more than what is left.
    deadline = time.monotonic() + RUN_BUDGET_S

    def step_timeout(wanted):
        """min(the step's own timeout, the budget left); None = exhausted."""
        return budgeted_timeout(wanted, deadline - time.monotonic())

    # --- step 1: container --------------------------------------------------- #
    ok, probed = probe_json(ctx.ffprobe, src, True, errors)
    if not ok:
        if "probe_timeout" not in errors:
            errors.append("container_invalid")
            # QA-0551-1: it may have vanished or become unreadable between the
            # hash and the probe — say so, instead of only "invalid container"
            if not input_still_ok(ctx.task_dir, VIDEO_FILE):
                errors.append("input_unreadable")
        return
    fmt = probed.get("format")
    fmt = fmt if isinstance(fmt, dict) else {}
    # SEC-0551-7: a non-list `streams` (or a non-object element) has no `.get`
    raw_streams = probed.get("streams")
    streams = [s for s in raw_streams if isinstance(s, dict)] if isinstance(raw_streams, list) else []
    duration = to_float(fmt.get("duration"))
    finite_duration = duration is not None and math.isfinite(duration)
    report["container"] = {
        "format": fmt.get("format_name"),
        # `Infinity`/`NaN` are not JSON: a non-finite duration is null here and
        # `container_invalid` below (SEC-0551-1).
        "duration_s": round_or_none(duration, 3) if finite_duration else None,
        "faststart": faststart(src),
    }
    if duration is not None and (not finite_duration or duration > MAX_DURATION_S):
        # no ffmpeg step runs on a non-finite or absurd duration (SEC-0551-1)
        errors.append("container_invalid")
        return
    video_stream = next((s for s in streams if s.get("codec_type") == "video"), None)
    audio_stream = next((s for s in streams if s.get("codec_type") == "audio"), None)
    if video_stream is None:
        errors.append("no_video_stream")
    else:
        report["video"] = video_block(video_stream)
    if audio_stream is None:
        errors.append("no_audio_stream")
    else:
        report["audio"] = audio_block(audio_stream)

    # --- step 2: narration -------------------------------------------------- #
    report["narration"] = {"duration_s": None}
    audio_fd, audio_path, audio_status = open_task_input(ctx.task_dir, AUDIO_FILE)
    if audio_status != "ok":
        errors.extend(input_error_codes(audio_status, "audio_missing"))
    else:
        os.close(audio_fd)
        ok, narrated = probe_json(ctx.ffprobe, audio_path, False, errors)
        if ok and isinstance(narrated.get("format"), dict):
            report["narration"]["duration_s"] = round_or_none(
                to_float(narrated["format"].get("duration")), 3)
        elif ok:
            errors.append("audio_missing")
        elif "probe_timeout" not in errors:
            errors.append("audio_missing")
    narration_s = report["narration"]["duration_s"]

    report["loudness"] = {"before": None, "after": None, "normalization_type": None}
    report["silence"] = {"longest_s": 0.0, "total_s": 0.0}
    timeout = ffmpeg_timeout(duration)
    measured = None
    measured_type = None
    served = src
    silent_audio = False
    budget_forced = False
    norm_tmp = None

    # --- step 3: loudness pass 1 -------------------------------------------- #
    if audio_stream is not None:
        limit = step_timeout(timeout)
        if limit is None:
            errors.append("ffmpeg_timeout")
            budget_forced = True
        else:
            argv = [ctx.ffmpeg, "-hide_banner", "-nostdin", "-nostats", "-i", src, "-vn",
                    "-af", LOUDNESS_FILTER + ":print_format=json", "-f", "null", "-"]
            log_path = os.path.join(log_dir, "loudnorm-pass1.log")
            rc, timed_out = run_cmd_log(argv, limit, log_path)
            block = None if timed_out else following_json(read_tail(log_path), LOUDNORM_MARKER)
            if timed_out:
                errors.append("ffmpeg_timeout")
            elif rc != 0 or block is None or "input_i" not in block:
                errors.append("loudness_failed")
            elif str(block.get("input_i")).strip() in ("-inf", "inf", "nan"):
                # X4: digital silence — nothing to normalise, the whole file is silence.
                errors.append("audio_silent")
                silent_audio = True
                whole = duration if duration is not None else narration_s
                report["silence"] = {"longest_s": round_or_none(whole, 3),
                                     "total_s": round_or_none(whole, 3)}
            else:
                values = loudness_measure(block)
                if values is None:
                    # SEC-0551-2: a non-finite / out-of-range measurement is a
                    # failed pass, never a number to feed into pass 2.
                    errors.append("loudness_failed")
                else:
                    measured = values
                    report["loudness"]["before"] = {
                        "integrated_lufs": round(values["input_i"], 2),
                        "true_peak_dbtp": round(values["input_tp"], 2),
                        "lra": round(values["input_lra"], 2),
                    }
                    kind = block.get("normalization_type")
                    measured_type = kind if isinstance(kind, str) else None
                    report["loudness"]["normalization_type"] = measured_type

    # --- step 4: normalise (only with --normalize) --------------------------- #
    if ctx.normalize and measured is not None and not budget_forced:
        if measured_type != "linear":
            warnings.append("loudnorm_dynamic")
        norm_path = os.path.join(ctx.task_dir, NORMALIZED_FILE)
        # SEC-0551-2: fixed two-decimal formatting, never the raw pass-1 strings.
        filt = ("%s:measured_I=%.2f:measured_TP=%.2f:measured_LRA=%.2f:measured_thresh=%.2f:"
                "offset=%.2f:linear=true:print_format=json" % (
                    LOUDNESS_FILTER, measured["input_i"], measured["input_tp"],
                    measured["input_lra"], measured["input_thresh"],
                    measured["target_offset"]))
        argv = [ctx.ffmpeg, "-hide_banner", "-nostdin", "-nostats", "-y", "-i", src,
                "-c:v", "copy", "-af", filt, "-c:a", "aac", "-b:a", "192k"]
        if report["audio"] and report["audio"].get("sample_rate"):
            argv += ["-ar", str(report["audio"]["sample_rate"])]
        # QA-0551-2: ffmpeg writes a fresh mkstemp file (O_CREAT|O_EXCL, 0600)
        # and `os.replace` moves it onto `final-1.norm.mp4`; a planted symlink
        # there is replaced, never written through.
        try:
            # the `.mp4` suffix matters: ffmpeg picks the muxer by extension
            norm_fd, norm_tmp = tempfile.mkstemp(dir=ctx.task_dir,
                                                 prefix=".final-1.norm.", suffix=".mp4")
            os.close(norm_fd)
        except OSError:
            errors.append("normalize_failed")
            norm_tmp = None
        if norm_tmp is not None:
            argv += ["-movflags", "+faststart", norm_tmp]
            limit = step_timeout(timeout)
            replaced = False
            if limit is None:
                errors.append("ffmpeg_timeout")
                budget_forced = True
            else:
                rc, timed_out = run_cmd_log(argv, limit,
                                            os.path.join(log_dir, "normalize.log"))
                if timed_out:
                    errors.append("ffmpeg_timeout")
                elif rc != 0:
                    errors.append("normalize_failed")
                else:
                    try:
                        os.replace(norm_tmp, norm_path)
                        replaced = True
                    except OSError:
                        errors.append("normalize_failed")
            if replaced:
                norm_tmp = None
                served = norm_path
                report["output"] = {
                    "file": NORMALIZED_FILE,
                    "bytes": file_size(norm_path),
                    "sha256": sha256_file(norm_path),
                    "normalized": True,
                }
                # D15 P2: container/video/audio/`faststart` describe the served file.
                served_ok, served_probe = probe_json(ctx.ffprobe, norm_path, True, errors)
                if served_ok:
                    served_format = served_probe.get("format")
                    served_format = served_format if isinstance(served_format, dict) else {}
                    raw_served = served_probe.get("streams")
                    served_streams = ([s for s in raw_served if isinstance(s, dict)]
                                      if isinstance(raw_served, list) else [])
                    duration = to_float(served_format.get("duration")) or duration
                    timeout = ffmpeg_timeout(duration)
                    report["container"] = {
                        "format": served_format.get("format_name"),
                        "duration_s": round_or_none(duration, 3),
                        "faststart": faststart(norm_path),
                    }
                    served_video = next((s for s in served_streams
                                         if s.get("codec_type") == "video"), None)
                    served_audio = next((s for s in served_streams
                                         if s.get("codec_type") == "audio"), None)
                    if served_video is not None:
                        report["video"] = video_block(served_video)
                    if served_audio is not None:
                        report["audio"] = audio_block(served_audio)
                else:
                    errors.append("container_invalid")
                report["loudness"]["after"] = re_measure(
                    ctx, served, step_timeout(timeout),
                    os.path.join(log_dir, "ebur128.log"), errors)

    # --- step 5: silence ----------------------------------------------------- #
    if audio_stream is not None and not silent_audio and not budget_forced:
        limit = step_timeout(timeout)
        if limit is None:
            errors.append("ffmpeg_timeout")
            budget_forced = True
        else:
            argv = [ctx.ffmpeg, "-hide_banner", "-nostdin", "-nostats", "-i", served, "-vn",
                    "-af", SILENCE_FILTER, "-f", "null", "-"]
            log_path = os.path.join(log_dir, "silencedetect.log")
            rc, timed_out = run_cmd_log(argv, limit, log_path)
            if timed_out:
                errors.append("ffmpeg_timeout")
            elif rc != 0:
                errors.append("silence_failed")
            else:
                values = list(silence_durations(log_path))
                report["silence"] = {
                    "longest_s": round(max(values), 3) if values else 0.0,
                    "total_s": round(float(sum(values)), 3),
                }

    # --- step 6: subtitles --------------------------------------------------- #
    report["subtitles"] = empty_subtitle_block()
    parsed_cues = None
    if ctx.subtitles == "off":
        pass  # SRT is not read at all (D9 step 6)
    else:
        raw, srt_status = read_task_text(ctx.task_dir, SRT_FILE, SRT_MAX_BYTES,
                                         encoding="utf-8-sig")
        if srt_status in ("too_large", "unparsable"):
            # SEC-0551-8: over 16 MiB is not read at all; a bad decode is unparsable.
            errors.append("srt_unparsable")
        elif srt_status != "ok":
            # QA-0551-2: a symlink or non-regular SRT is treated as missing.
            errors.append("srt_missing")
            if srt_status == "unreadable":
                errors.append("input_unreadable")
        elif not raw.strip():
            errors.append("srt_empty")
        else:
            parsed_cues, invalid = parse_srt(raw)
            if invalid:
                # QA-0551-3: counted per cue, not fatal (D9 has no field for it).
                warnings.append("srt_time_invalid:%d" % invalid)
            if not parsed_cues:
                errors.append("srt_unparsable")
            else:
                report["subtitles"] = subtitle_metrics(parsed_cues, duration,
                                                       narration_s, ctx.cue_cap)

    # --- step 7: text match, then the report --------------------------------- #
    if report["subtitles"]["present"]:
        script_text, script_status = read_task_text(ctx.task_dir, SCRIPT_FILE,
                                                    SCRIPT_MAX_BYTES)
        body = None
        if script_status == "ok":
            try:
                doc = json.loads(script_text)
            except (ValueError, RecursionError):
                doc = None
            if (isinstance(doc, dict) and isinstance(doc.get("script"), str)
                    and doc["script"].strip()):
                body = doc["script"]
        if body is None:
            # optional input: absent, unreadable, over 4 MiB or shape-invalid all
            # read as `script_missing` (SEC-0551-7/8).
            warnings.append("script_missing")
        else:
            mismatches, _chunks = match_script(parsed_cues, body)
            report["subtitles"]["text_mismatch_cues"] = mismatches
            report["subtitles"]["text_matches_script"] = mismatches == 0

    # SEC-0551-5: the path must still be the same inode (and the same bytes) —
    # a swap-back between the first hash and now is what this catches.
    end_fd, _end_path, end_status = open_task_input(ctx.task_dir, VIDEO_FILE)
    if end_status != "ok":
        errors.append("input_changed")
    else:
        end_info = os.fstat(end_fd)
        end_stat = (end_info.st_ino, end_info.st_size, end_info.st_mtime_ns)
        end_hash = sha256_fd(end_fd)
        os.close(end_fd)
        if end_stat != start_stat or end_hash != start_hash:
            errors.append("input_changed")

    if norm_tmp is not None:
        try:
            os.unlink(norm_tmp)
        except OSError:
            pass


def re_measure(ctx, served, limit, log_path, errors):
    """Step 4 re-measure: the `ebur128=peak=true` Summary → `loudness.after`."""
    if limit is None:
        errors.append("ffmpeg_timeout")
        return None
    argv = [ctx.ffmpeg, "-hide_banner", "-nostdin", "-nostats", "-i", served, "-vn",
            "-af", "ebur128=peak=true", "-f", "null", "-"]
    rc, timed_out = run_cmd_log(argv, limit, log_path)
    if timed_out:
        errors.append("ffmpeg_timeout")
        return None
    if rc != 0:
        errors.append("loudness_failed")
        return None
    # SEC-0551-2: the Summary must be inside the output of the ebur128 filter —
    # a `Summary:`/`I:` line planted in the input's metadata cannot match.
    section = following_section(read_tail(log_path), EBUR128_MARKER)
    summary_start = section.rfind("Summary:")
    if summary_start < 0:
        errors.append("loudness_failed")
        return None
    text = section[summary_start:]
    integrated = re.search(r"^\s*I:\s+(-?[0-9.]+)\s+LUFS", text, re.MULTILINE)
    lra = re.search(r"^\s*LRA:\s+(-?[0-9.]+)\s+LU", text, re.MULTILINE)
    peak = re.search(r"^\s*Peak:\s+(-?[0-9.]+)\s+dBFS", text, re.MULTILINE)
    loudness = (bounded_float(integrated.group(1), *AFTER_RANGES["integrated_lufs"])
                if integrated else None)
    if loudness is None:
        errors.append("loudness_failed")
        return None
    true_peak = (bounded_float(peak.group(1), *AFTER_RANGES["true_peak_dbtp"])
                 if peak else None)
    range_lu = (bounded_float(lra.group(1), *AFTER_RANGES["lra"]) if lra else None)
    if (peak and true_peak is None) or (lra and range_lu is None):
        errors.append("loudness_failed")
        return None
    return {
        "integrated_lufs": round(loudness, 2),
        "true_peak_dbtp": round(true_peak, 2) if true_peak is not None else None,
        "lra": round(range_lu, 2) if range_lu is not None else None,
    }


def write_report(report, out_path):
    """Atomic write: `<out>.tmp` → os.replace (`render-check.json.tmp`, D9).

    SEC-0551-3: the temporary is created with `O_CREAT|O_EXCL|O_NOFOLLOW` and
    mode 0600, so a pre-planted symlink `render-check.json.tmp -> /etc/…` is
    refused instead of written through. `os.replace` moves the file onto
    `out_path` (replacing a symlink there, never following it).
    """
    payload = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    tmp_path = out_path + ".tmp"
    directory = os.path.dirname(os.path.abspath(out_path))
    if directory and not os.path.isdir(directory):
        try:
            os.makedirs(directory, exist_ok=True)
        except OSError as exc:
            raise EnvError("cannot create the report directory %s: %s" % (directory, exc))
    flags = os.O_CREAT | os.O_WRONLY | os.O_EXCL | O_NOFOLLOW
    try:
        fd = os.open(tmp_path, flags, 0o600)
    except FileExistsError:
        # a stale regular temporary from a crashed run may be replaced; anything
        # else (a symlink, a directory) is refused
        info = os.lstat(tmp_path)
        if not stat.S_ISREG(info.st_mode):
            raise EnvError("refusing to write the report through %s" % tmp_path)
        os.unlink(tmp_path)
        fd = os.open(tmp_path, flags, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        handle.write(payload)
    os.replace(tmp_path, out_path)
    return payload


# --------------------------------------------------------------------------- #
# --self-test (D14: lavfi media in a temp dir; no binary media in the repo).
# --------------------------------------------------------------------------- #
def srt_text(texts, spans):
    """Build SRT text for (text, (start, end)) pairs — the layout of the committed fixtures."""
    blocks = []
    for index, (text, (start, end)) in enumerate(zip(texts, spans), start=1):
        blocks.append("%d\n%s --> %s\n%s\n" % (index, start, end, text))
    return "\n".join(blocks)


class SelfTest(object):
    def __init__(self, keep):
        self.keep = keep
        self.failures = []
        self.cases = 0
        self.tmp = tempfile.mkdtemp(prefix="render-check-selftest-")
        self.ffmpeg = resolve_tool("ffmpeg")
        self.ffprobe = resolve_tool("ffprobe")

    def case(self, name, ok, detail=""):
        self.cases += 1
        if ok:
            print("ok   %-22s %s" % (name, detail))
        else:
            self.failures.append(name)
            print("FAIL %-22s %s" % (name, detail))

    def path(self, *parts):
        return os.path.join(self.tmp, *parts)

    def gen(self, name, args):
        out = self.path(name)
        argv = [self.ffmpeg, "-hide_banner", "-nostdin", "-y", "-loglevel", "error"] + args + [out]
        rc, _o, err, timed_out = run_cmd(argv, 600)
        if timed_out or rc != 0:
            raise EnvError("self-test: cannot generate %s: %s" % (name, err or "timeout"))
        return out

    def media(self, name, size, rate, audio, extra=()):
        """testsrc2 video (+ optional audio) in one mp4."""
        args = ["-f", "lavfi", "-i", "testsrc2=size=%s:rate=%s" % (size, rate)]
        if audio:
            args += ["-f", "lavfi", "-i", audio, "-shortest"]
        args += ["-t", "12", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2",
                 "-pix_fmt", "yuv420p"]
        if audio:
            args += ["-c:a", "aac", "-b:a", "192k"]
        args += list(extra)
        return self.gen(name, args)

    def task_dir(self, name, video, srt_text=None, script_body=None, audio="audio.mp3"):
        directory = self.path(name)
        os.makedirs(directory, exist_ok=True)
        shutil.copyfile(video, os.path.join(directory, VIDEO_FILE))
        if audio:
            target = os.path.join(directory, os.path.basename(audio))
            if os.path.isfile(audio):
                # an existing narration file (the video may carry no audio stream)
                shutil.copyfile(audio, target)
            else:
                rc, _o, err, _t = run_cmd(
                    [self.ffmpeg, "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                     "-i", video, "-vn", "-c:a", "libmp3lame", "-b:a", "192k", target], 120)
                if rc != 0:
                    raise EnvError("self-test: cannot write %s: %s" % (target, err))
        if srt_text is not None:
            with open(os.path.join(directory, SRT_FILE), "w", encoding="utf-8") as handle:
                handle.write(srt_text)
        if script_body is not None:
            with open(os.path.join(directory, SCRIPT_FILE), "w", encoding="utf-8") as handle:
                json.dump({"script": script_body}, handle)
        return directory

    def ctx(self, task_dir, aspect="landscape", subtitles="on", normalize=False, cap=None):
        if cap is None:
            cap = D6_PRESETS[aspect]["maxCharsPerLine"] * D6_PRESETS[aspect]["maxLines"]
        return Context(task_dir, aspect, subtitles, normalize, cap, None,
                       self.ffmpeg, self.ffprobe)

    def run(self):
        start = time.time()
        print("# render_check.py --self-test (tmp=%s)" % self.tmp)
        good = self.media("good.mp4", "1920x1080", "30", "sine=frequency=440:sample_rate=48000")
        portrait = self.media("portrait.mp4", "1080x1920", "30", "sine=frequency=440:sample_rate=48000")
        small = self.media("small.mp4", "1280x720", "30", "sine=frequency=440:sample_rate=48000")
        videoonly = self.media("videoonly.mp4", "1920x1080", "30", None)
        silent = self.media("silent.mp4", "1920x1080", "30", "anullsrc=r=48000:cl=mono")
        loud = self.media("loud.mp4", "1920x1080", "30", "sine=frequency=440:sample_rate=48000",
                          extra=["-af", "loudnorm=I=-8:TP=-1.5:LRA=11"])
        truncated = self.path("truncated.mp4")
        with open(good, "rb") as src_handle, open(truncated, "wb") as dst_handle:
            remaining = 400 * 1024
            while remaining > 0:
                block = src_handle.read(min(HASH_CHUNK, remaining))
                if not block:
                    break
                dst_handle.write(block)
                remaining -= len(block)

        # a small valid render with an audio stream, for the guard/CLI cases
        tiny = self.gen("tiny.mp4", ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10",
                                     "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                                     "-shortest", "-t", "4", "-c:v", "libx264", "-preset",
                                     "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p",
                                     "-c:a", "aac"])

        lines = [
            "The lighthouse keeper counted seven lamps",
            "Each one belonged to a family that had left",
            "yet the fires still answered the sea",
            "and the village slept through the storm",
        ]
        good_srt = self.srt_lines(lines, [("00:00:00,000", "00:00:02,000"),
                                          ("00:00:02,200", "00:00:04,500"),
                                          ("00:00:04,700", "00:00:07,000"),
                                          ("00:00:07,200", "00:00:09,400")])
        script_body = "\n".join(lines)
        good_dir = self.task_dir("srt-good", good, good_srt, script_body)

        report, code = run_check(self.ctx(good_dir))
        self.case("good-landscape",
                  code == 0 and report["container"]["duration_s"] == 12.0
                  and report["video"]["width"] == 1920 and report["video"]["height"] == 1080
                  and report["audio"]["codec"] == "aac" and report["subtitles"]["present"]
                  and report["subtitles"]["zero_duration"] == 0
                  and report["subtitles"]["text_matches_script"] is True
                  and report["container"]["faststart"] is False,
                  "12.0s 1920x1080 aac present=%s matches=%s faststart=%s rc=%d"
                  % (report["subtitles"]["present"], report["subtitles"]["text_matches_script"],
                     report["container"]["faststart"], code))

        portrait_dir = self.task_dir("srt-portrait", portrait, good_srt, script_body)
        report, code = run_check(self.ctx(portrait_dir, aspect="portrait"))
        self.case("portrait",
                  code == 0 and report["video"]["width"] == 1080 and report["video"]["height"] == 1920,
                  "%sx%s rc=%d" % (report["video"]["width"], report["video"]["height"], code))

        silent_dir = self.task_dir("silent", silent, good_srt, script_body)
        report, code = run_check(self.ctx(silent_dir))
        longest = report["silence"]["longest_s"]
        self.case("silent",
                  code == 1 and "audio_silent" in report["errors"]
                  and longest is not None and abs(longest - report["container"]["duration_s"]) < 0.2
                  and report["loudness"]["before"] is None,
                  "errors=%s longest_s=%s rc=%d" % (report["errors"], longest, code))

        small_dir = self.task_dir("wrong-resolution", small, good_srt, script_body)
        report, code = run_check(self.ctx(small_dir))
        self.case("wrong-resolution", code == 0 and report["video"]["width"] == 1280,
                  "width=%s rc=%d (the policy decides, not the tool)"
                  % (report["video"]["width"], code))

        truncated_dir = self.path("truncated")
        os.makedirs(truncated_dir, exist_ok=True)
        shutil.copyfile(truncated, os.path.join(truncated_dir, VIDEO_FILE))
        report, code = run_check(self.ctx(truncated_dir))
        self.case("truncated", code == 1 and report["errors"] == ["container_invalid"]
                  and report["container"] is None and report["subtitles"] is None,
                  "errors=%s rc=%d" % (report["errors"], code))

        narration_mp3 = self.gen("audio.mp3", ["-f", "lavfi", "-i",
                                               "sine=frequency=440:sample_rate=48000",
                                               "-t", "12", "-c:a", "libmp3lame", "-b:a", "192k"])
        videoonly_dir = self.task_dir("videoonly", videoonly, good_srt, script_body,
                                      audio=narration_mp3)
        report, code = run_check(self.ctx(videoonly_dir))
        narration_voice = report["narration"]["duration_s"]
        self.case("no-audio-stream",
                  code == 1 and report["errors"] == ["no_audio_stream"]
                  and report["loudness"]["before"] is None
                  and narration_voice is not None and abs(narration_voice - 12.0) < 0.3,
                  "errors=%s narration=%s rc=%d"
                  % (report["errors"], narration_voice, code))

        loud_dir = self.task_dir("loud", loud, good_srt, script_body)
        before_sha1 = self.video_sha1(os.path.join(loud_dir, VIDEO_FILE))
        src = os.path.join(loud_dir, VIDEO_FILE)
        src_sha256 = sha256_file(src)
        report, code = run_check(self.ctx(loud_dir, normalize=True))
        after = report["loudness"]["after"] or {}
        integrated = report["loudness"]["before"]["integrated_lufs"] if report["loudness"]["before"] else None
        normalized_ok = bool(report["output"] and report["output"]["normalized"])
        band_ok = after.get("integrated_lufs") is not None and -16.0 <= after["integrated_lufs"] <= -14.0
        after_sha1 = self.video_sha1(src)
        self.case("loud-normalize",
                  code == 0 and integrated is not None and abs(integrated + 8) < 1.5 and band_ok
                  and normalized_ok and before_sha1 is not None and after_sha1 == before_sha1
                  and sha256_file(src) == src_sha256
                  and report["container"]["faststart"] is True,
                  "before=%s after=%s normalized=%s video_sha1=%s input_sha256_kept=%s"
                  % (integrated, after.get("integrated_lufs"), normalized_ok,
                     (after_sha1 or "?")[:8], sha256_file(src) == src_sha256))

        # SRT variants (committed fixtures) on the good task dir.
        self.case("fixtures/good.srt",
                  self.fixture("good.srt") == good_srt,
                  "committed fixture matches the self-test script/SRT pair")
        report, code = self.run_with_srt(good_dir, good_srt, script_body)
        sub = report["subtitles"]
        self.case("srt-good", code == 0 and sub["cues"] == 4 and sub["zero_duration"] == 0
                  and sub["overlapping"] == 0 and sub["non_monotonic"] == 0
                  and sub["out_of_range"] == 0 and sub["long_cues"] == 0
                  and sub["text_matches_script"] is True and sub["coverage_share"] > 0.5,
                  "cues=%s zero=%s overlap=%s oob=%s coverage=%s long=%s matches=%s"
                  % (sub["cues"], sub["zero_duration"], sub["overlapping"], sub["out_of_range"],
                     sub["coverage_share"], sub["long_cues"], sub["text_matches_script"]))

        report, code = self.run_with_srt(good_dir, self.fixture("zero-cues.srt"), script_body)
        sub = report["subtitles"]
        self.case("srt-zero-cues",
                  code == 0 and sub["cues"] > 0 and sub["zero_duration"] == sub["cues"]
                  and sub["non_monotonic"] == 0,
                  "cues=%s zero_duration=%s non_monotonic=%s rc=%d"
                  % (sub["cues"], sub["zero_duration"], sub["non_monotonic"], code))

        report, code = self.run_with_srt(good_dir, self.fixture("overlapping.srt"), script_body)
        self.case("srt-overlapping", code == 0 and report["subtitles"]["overlapping"] == 1,
                  "overlapping=%s rc=%d" % (report["subtitles"]["overlapping"], code))

        report, code = self.run_with_srt(good_dir, self.fixture("out-of-range.srt"), script_body)
        self.case("srt-out-of-range", code == 0 and report["subtitles"]["out_of_range"] == 1,
                  "out_of_range=%s rc=%d" % (report["subtitles"]["out_of_range"], code))

        report, code = self.run_with_srt(good_dir, self.fixture("empty.srt", raw=True), script_body)
        self.case("srt-empty",
                  code == 1 and "srt_empty" in report["errors"]
                  and report["subtitles"]["present"] is False,
                  "errors=%s present=%s rc=%d"
                  % (report["errors"], report["subtitles"]["present"], code))

        single = self.srt_lines(["A single short line"], [("00:00:00,000", "00:00:00,000")])
        report, code = self.run_with_srt(good_dir, single, "A single short line")
        sub = report["subtitles"]
        self.case("srt-single-zero-cue", code == 0 and sub["cues"] == 1
                  and sub["zero_duration"] == 1 and sub["coverage_share"] == 0.0,
                  "F02: cues=%s zero=%s coverage=%s rc=%d"
                  % (sub["cues"], sub["zero_duration"], sub["coverage_share"], code))

        non_monotonic = self.srt_lines(["first line here", "second line here"],
                                       [("00:00:05,000", "00:00:07,000"),
                                        ("00:00:01,000", "00:00:03,000")])
        report, code = self.run_with_srt(good_dir, non_monotonic, "first line here\nsecond line here")
        self.case("srt-non-monotonic", code == 0 and report["subtitles"]["non_monotonic"] == 1,
                  "non_monotonic=%s" % report["subtitles"]["non_monotonic"])

        long_text = "x" * 90
        long_srt = self.srt_lines([long_text], [("00:00:00,000", "00:00:05,000")])
        long_dir = self.run_with_srt_dir(good_dir, long_srt, long_text)
        landscape_report, _code = run_check(self.ctx(long_dir))
        portrait_report, _code = run_check(self.ctx(long_dir, aspect="portrait"))
        self.case("long-cues",
                  landscape_report["subtitles"]["long_cues"] == 1
                  and portrait_report["subtitles"]["long_cues"] == 1,
                  "90 chars: landscape cap=%d long=%s portrait cap=%d long=%s"
                  % (D6_PRESETS["landscape"]["maxCharsPerLine"] * D6_PRESETS["landscape"]["maxLines"],
                     landscape_report["subtitles"]["long_cues"],
                     D6_PRESETS["portrait"]["maxCharsPerLine"] * D6_PRESETS["portrait"]["maxLines"],
                     portrait_report["subtitles"]["long_cues"]))

        report, code = run_check(self.ctx(good_dir, subtitles="off"))
        self.case("subtitles-off",
                  code == 0 and report["subtitles"]["present"] is False
                  and not [e for e in report["errors"] if e.startswith("srt_")]
                  and report["subtitles"]["text_matches_script"] is None
                  and report["narration"]["duration_s"] is not None,
                  "present=%s errors=%s rc=%d"
                  % (report["subtitles"]["present"], report["errors"], code))

        report, code = self.run_with_srt(good_dir, None, script_body)
        self.case("srt-missing",
                  code == 1 and report["errors"] == ["srt_missing"]
                  and report["subtitles"]["present"] is False,
                  "S8: errors=%s present=%s rc=%d"
                  % (report["errors"], report["subtitles"]["present"], code))

        report, code = self.run_with_srt(good_dir, "not an srt at all\n", script_body)
        self.case("srt-unparsable",
                  code == 1 and "srt_unparsable" in report["errors"]
                  and report["subtitles"]["present"] is False,
                  "errors=%s rc=%d" % (report["errors"], code))

        # --- CRV-0551-3: the fields the metrics exist for -------------------- #
        mismatch_srt = self.srt_lines(["A completely different line"],
                                      [("00:00:00,000", "00:00:02,000")])
        report, code = self.run_with_srt(good_dir, mismatch_srt, script_body)
        sub = report["subtitles"]
        self.case("srt-text-mismatch",
                  sub["text_matches_script"] is False and sub["text_mismatch_cues"] > 0,
                  "cue text != chunk: matches=%s mismatches=%s rc=%d"
                  % (sub["text_matches_script"], sub["text_mismatch_cues"], code))

        punct_srt = self.srt_lines(["The lighthouse keeper counted seven lamps,"],
                                   [("00:00:00,000", "00:00:02,000")])
        report, code = self.run_with_srt(good_dir, punct_srt,
                                        "The lighthouse keeper counted seven lamps")
        sub = report["subtitles"]
        self.case("srt-trailing-punctuation",
                  code == 0 and sub["text_matches_script"] is True
                  and sub["text_mismatch_cues"] == 0,
                  "trailing ',' stripped: matches=%s mismatches=%s rc=%d"
                  % (sub["text_matches_script"], sub["text_mismatch_cues"], code))

        mixed = self.srt_lines(["first line here", "second line here"],
                               [("00:00:00,000", "00:00:02,000"),
                                ("100:00:00,000", "100:00:05,000")])
        report, code = self.run_with_srt(good_dir, mixed, "first line here\nsecond line here")
        self.case("srt-time-invalid",
                  code == 0 and report["subtitles"]["cues"] == 1
                  and "srt_time_invalid:1" in report["warnings"],
                  "the >99 h cue is counted, not dropped: cues=%s warnings=%s rc=%d"
                  % (report["subtitles"]["cues"], report["warnings"], code))

        report, code = run_check(self.ctx(good_dir))
        silent_report, _silent_code = run_check(self.ctx(silent_dir))
        self.case("normalize-off",
                  code == 0 and report["output"] is None
                  and report["loudness"]["after"] is None
                  and silent_report["output"] is None
                  and silent_report["loudness"]["after"] is None
                  and silent_report["loudness"]["normalization_type"] is None,
                  "output=%s after=%s silent normalization_type=%s rc=%d"
                  % (report["output"], report["loudness"]["after"],
                     silent_report["loudness"]["normalization_type"], code))

        # QA-0551-2: ffmpeg writes a fresh file and os.replace moves it on; a
        # planted symlink there is replaced, never written through.
        link_dir = self.path("norm-symlink")
        os.makedirs(link_dir, exist_ok=True)
        for name in (VIDEO_FILE, AUDIO_FILE, SRT_FILE, SCRIPT_FILE):
            shutil.copyfile(os.path.join(good_dir, name), os.path.join(link_dir, name))
        victim = self.path("norm-victim.txt")
        with open(victim, "w", encoding="utf-8") as handle:
            handle.write("ORIGINAL-OUTSIDE")
        link = os.path.join(link_dir, NORMALIZED_FILE)
        if os.path.exists(link) or os.path.islink(link):
            os.unlink(link)
        os.symlink(victim, link)
        report, code = run_check(self.ctx(link_dir, normalize=True))
        with open(victim, encoding="utf-8") as handle:
            kept = handle.read()
        self.case("norm-symlink",
                  code == 0 and kept == "ORIGINAL-OUTSIDE" and not os.path.islink(link)
                  and report["output"] is not None
                  and report["output"]["bytes"] == os.path.getsize(link),
                  "outside file kept=%s still a symlink=%s output=%d bytes rc=%d"
                  % (kept == "ORIGINAL-OUTSIDE", os.path.islink(link),
                     report["output"]["bytes"] if report["output"] else -1, code))

        self.check_input_guards(tiny)
        self.check_metrics()
        self.check_budget_and_dedupe(good_dir)
        self.check_report_writes(tiny)
        self.check_chunk_fixtures()
        self.check_presets()

        elapsed = time.time() - start
        print("# %d cases, %d failures, %.1fs" % (self.cases, len(self.failures), elapsed))
        if self.failures:
            print("# failed: %s" % ", ".join(self.failures))
        if self.keep:
            print("# kept: %s" % self.tmp)
        else:
            shutil.rmtree(self.tmp, ignore_errors=True)
        return 1 if self.failures else 0

    def srt_lines(self, texts, spans):
        return srt_text(texts, spans)

    def fixture(self, name, raw=False):
        path = os.path.join(repo_root(), "worker-node", "checks", "fixtures", name)
        if raw:
            with open(path, "rb") as handle:
                return handle.read()
        with open(path, encoding="utf-8") as handle:
            return handle.read()

    def run_with_srt_dir(self, base_dir, srt_text, script_body):
        directory = self.path("srt-variant")
        os.makedirs(directory, exist_ok=True)
        src = os.path.join(base_dir, VIDEO_FILE)
        dst = os.path.join(directory, VIDEO_FILE)
        if not os.path.exists(dst):
            shutil.copyfile(src, dst)
            for name in (AUDIO_FILE,):
                shutil.copyfile(os.path.join(base_dir, name), os.path.join(directory, name))
        srt_path = os.path.join(directory, SRT_FILE)
        if srt_text is None:
            if os.path.exists(srt_path):
                os.remove(srt_path)
        elif isinstance(srt_text, bytes):
            with open(srt_path, "wb") as handle:
                handle.write(srt_text)
        else:
            with open(srt_path, "w", encoding="utf-8") as handle:
                handle.write(srt_text)
        with open(os.path.join(directory, SCRIPT_FILE), "w", encoding="utf-8") as handle:
            json.dump({"script": script_body}, handle)
        return directory

    def run_with_srt(self, base_dir, srt_text, script_body):
        return run_check(self.ctx(self.run_with_srt_dir(base_dir, srt_text, script_body)))

    def video_sha1(self, path):
        """X3: sha1 of the copied H.264 bitstream (the video stream must not change)."""
        rc, out, _err, _t = run_cmd_bytes(
            [self.ffmpeg, "-v", "error", "-i", path, "-map", "0:v", "-c", "copy",
             "-f", "h264", "-"], 300)
        return hashlib.sha1(out).hexdigest() if rc == 0 and out else None

    def check_chunk_fixtures(self):
        """Drift guard: the port must reproduce every committed chunking fixture."""
        directory = os.path.join(repo_root(), "worker-node", "checks", "fixtures", "chunking")
        names = sorted(n for n in os.listdir(directory)) if os.path.isdir(directory) else []
        if not names:
            self.case("chunking-fixtures", False, "no committed fixtures in %s" % directory)
            return
        bad = []
        for name in names:
            with open(os.path.join(directory, name), encoding="utf-8") as handle:
                doc = json.load(handle)
            text = doc.get("input_text")
            if not isinstance(text, str):
                bad.append("%s: no input_text" % name)
                continue
            if hashlib.sha256(text.encode("utf-8")).hexdigest() != doc.get("input_sha256"):
                bad.append("%s: input_sha256 mismatch" % name)
            normalized = ported_normalize_script_for_subtitle_matching(text)
            if normalized != doc.get("normalized"):
                bad.append("%s: normalized drift" % name)
            if doc.get("punctuations") != PUNCTUATIONS:
                bad.append("%s: punctuations drift" % name)
            if ported_split_string_by_punctuations(normalized) != doc.get("chunks"):
                bad.append("%s: chunks drift" % name)
        self.case("chunking-fixtures", not bad,
                  "%d fixtures reproduced%s" % (len(names), "" if not bad else "; " + "; ".join(bad)))

    def invoke(self, args):
        """Call `main` in-process, capturing stdout/stderr (no report escapes)."""
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = main(args)
        return code, out.getvalue(), err.getvalue()

    def fake_tool(self, name, lines):
        path = self.path(name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("#!%s\n" % sys.executable)
            handle.write("".join(lines))
        os.chmod(path, 0o755)
        return path

    def check_input_guards(self, good):
        """QA-0551-2/SEC-0551-1: refusals, and no ffmpeg step on a bogus duration."""
        outside = self.path("guards-outside.txt")
        with open(outside, "w", encoding="utf-8") as handle:
            handle.write("OUTSIDE")
        directory = self.path("guards")
        os.makedirs(directory, exist_ok=True)
        for name in (VIDEO_FILE, AUDIO_FILE):
            shutil.copyfile(good, os.path.join(directory, name))

        link = os.path.join(directory, VIDEO_FILE)
        os.unlink(link)
        os.symlink(outside, link)
        report, code = run_check(self.ctx(directory, subtitles="off"))
        self.case("container-symlink",
                  code == 1 and report["errors"] == ["container_invalid", "input_unreadable"],
                  "errors=%s input=%s rc=%d"
                  % (report["errors"], report["input"]["bytes"], code))

        os.unlink(link)
        shutil.copyfile(good, link)
        srt_link = os.path.join(directory, SRT_FILE)
        os.symlink(outside, srt_link)
        report, code = run_check(self.ctx(directory))
        self.case("srt-symlink",
                  code == 1 and report["errors"] == ["srt_missing"]
                  and report["subtitles"]["present"] is False,
                  "errors=%s present=%s rc=%d"
                  % (report["errors"], report["subtitles"]["present"], code))
        os.unlink(srt_link)

        # an input that exists but cannot be read: the same refusal + input_unreadable
        perm = self.path("unreadable")
        os.makedirs(perm, exist_ok=True)
        target = os.path.join(perm, VIDEO_FILE)
        shutil.copyfile(good, target)
        os.chmod(target, 0o000)
        report, code = run_check(self.ctx(perm, subtitles="off"))
        os.chmod(target, 0o600)
        # root ignores the mode bits, so only a non-root run can be denied
        self.case("container-unreadable",
                  os.geteuid() == 0
                  or (code == 1 and report["errors"] == ["container_invalid", "input_unreadable"]),
                  "errors=%s bytes=%s rc=%d" % (report["errors"], report["input"]["bytes"], code))

        # a container that vanishes between the hash and the probe (QA-0551-1)
        vanish_dir = self.path("vanish")
        os.makedirs(vanish_dir, exist_ok=True)
        for name in (VIDEO_FILE, AUDIO_FILE):
            shutil.copyfile(good, os.path.join(vanish_dir, name))
        fake_vanish = self.fake_tool("fake-ffprobe-vanish", [
            "import os, sys\n",
            "os.remove(sys.argv[-1])\n",
            "sys.exit(1)\n",
        ])
        report, code = run_check(Context(vanish_dir, "landscape", "off", False, 84, None,
                                        self.ffmpeg, fake_vanish))
        self.case("container-vanished",
                  code == 1 and report["errors"] == ["container_invalid", "input_unreadable"]
                  and report["input"]["sha256"] is not None,
                  "errors=%s (hashed then removed) rc=%d" % (report["errors"], code))

        # a bogus (non-finite or > 4 h) duration: rejected with zero ffmpeg steps
        duration_file = self.path("fake-duration.txt")
        marker = self.path("fake-ffmpeg-called.txt")
        fake_ffmpeg = self.fake_tool("fake-ffmpeg", [
            "import sys\n",
            "if sys.argv[1:2] == ['-version']:\n",
            "    print('ffmpeg version 9.0.2-fake')\n",
            "    sys.exit(0)\n",
            "open(%r, 'a').write(' '.join(sys.argv[1:]) + '\\n')\n" % marker,
        ])
        fake_ffprobe = self.fake_tool("fake-ffprobe", [
            "import json, sys\n",
            "duration = open(%r, encoding='utf-8').read().strip()\n" % duration_file,
            "sys.stdout.write(json.dumps({'format': {'format_name': 'mov,mp4',\n",
            "    'duration': duration}, 'streams': [\n",
            "    {'codec_type': 'video', 'codec_name': 'h264', 'width': 1920, 'height': 1080},\n",
            "    {'codec_type': 'audio', 'codec_name': 'aac', 'sample_rate': '48000',\n",
            "     'channels': 1}]}))\n",
        ])
        guards = []
        for value, reported_duration in (("Infinity", None), ("20000.0", 20000.0)):
            with open(duration_file, "w", encoding="utf-8") as handle:
                handle.write(value)
            if os.path.exists(marker):
                os.unlink(marker)
            ctx = Context(directory, "landscape", "off", False, 84, None,
                          fake_ffmpeg, fake_ffprobe)
            report, code = run_check(ctx)
            guards.append(code == 1 and report["errors"] == ["container_invalid"]
                          and report["narration"] is None and report["loudness"] is None
                          and report["container"]["duration_s"] == reported_duration
                          and not os.path.exists(marker))
        self.case("bogus-duration", all(guards),
                  "Infinity and 20000 s -> container_invalid, no ffmpeg step (%s)"
                  % ("ok" if all(guards) else "see FAIL"))

        # SEC-0551-8: the caps are enforced by fstat, before any read
        big_srt = os.path.join(directory, SRT_FILE)
        with open(big_srt, "w", encoding="utf-8") as handle:
            handle.write("1\n00:00:00,000 --> 00:00:02,000\n")
            handle.write("x" * (SRT_MAX_BYTES + 4096))
            handle.write("\n")
        report, code = run_check(self.ctx(directory))
        os.unlink(big_srt)
        srt_capped = (code == 1 and report["errors"] == ["srt_unparsable"]
                      and report["subtitles"]["present"] is False)
        with open(big_srt, "w", encoding="utf-8") as handle:
            handle.write("1\n00:00:00,000 --> 00:00:02,000\na short cue\n")
        with open(os.path.join(directory, SCRIPT_FILE), "w", encoding="utf-8") as handle:
            json.dump({"script": "y" * (SCRIPT_MAX_BYTES + 4096)}, handle)
        report, code = run_check(self.ctx(directory))
        script_capped = (code == 0 and "script_missing" in report["warnings"]
                         and report["subtitles"]["text_matches_script"] is None)
        self.case("big-inputs", srt_capped and script_capped,
                  "SRT over %d MiB -> srt_unparsable; script.json over %d MiB -> script_missing"
                  % (SRT_MAX_BYTES >> 20, SCRIPT_MAX_BYTES >> 20))

        # SEC-0551-7: a non-object / wrong-shaped ffprobe payload must not crash
        payload_file = self.path("fake-probe-payload.json")
        fake_shape = self.fake_tool("fake-ffprobe-shape", [
            "import sys\n",
            "sys.stdout.write(open(%r, encoding='utf-8').read())\n" % payload_file,
        ])
        shapes = []
        expected = {"[]": ["container_invalid"],
                    '"x"': ["container_invalid"],
                    '{"format": [], "streams": 1}': ["no_video_stream", "no_audio_stream",
                                                     "audio_missing"],
                    '{"format": 1}': ["no_video_stream", "no_audio_stream", "audio_missing"]}
        for payload, want in expected.items():
            with open(payload_file, "w", encoding="utf-8") as handle:
                handle.write(payload)
            try:
                report, code = run_check(Context(directory, "landscape", "off", False,
                                                84, None, self.ffmpeg, fake_shape))
            except Exception as exc:  # noqa: BLE001 - any raise is the failure
                shapes.append("%s raised %s" % (payload, type(exc).__name__))
                continue
            if code != 1 or report["errors"] != want or "internal_error" in report["errors"]:
                shapes.append("%s -> %s/%d" % (payload, report["errors"], code))
        self.case("probe-shape", not shapes,
                  "non-object/wrong-shaped ffprobe JSON stays a measurement, never a crash (%s)"
                  % ("ok" if not shapes else "; ".join(shapes)))

    def check_metrics(self):
        """CRV-0551-2/3 + QA-0551-3/5 + SEC-0551-2: the pure functions behind the report."""
        overlap = [(0.0, 3.0, "a"), (2.5, 5.0, "b"), (4.0, 7.0, "c"), (6.0, 9.0, "d")]
        block = subtitle_metrics(overlap, 20.0, 20.0, 84)
        cps_cues = [(0.0, 1.0, "x" * 10), (1.0, 1.5, "x" * 10), (1.5, 2.5, "x" * 40),
                    (2.5, 3.5, "x" * 40), (3.5, 4.5, "x" * 40), (10.0, 23.0, "x" * 5)]
        cps_block = subtitle_metrics(cps_cues, 20.0, 20.0, 84)
        zero_block = subtitle_metrics([(0.0, 0.0, "a"), (0.0, 0.0, "b"), (0.0, 0.0, "c")],
                                     12.0, 12.0, 84)
        regress_block = subtitle_metrics([(5.0, 7.0, "a"), (1.0, 3.0, "b")], 12.0, 12.0, 84)
        self.case("metrics-union-cps-sync",
                  block["coverage_share"] == 0.45 and block["overlapping"] == 3
                  and cps_block["cps_p95"] == 40.0 and cps_block["sync_suspect"] == 4
                  and cps_block["max_cue_chars"] == 40
                  and zero_block["non_monotonic"] == 0 and zero_block["zero_duration"] == 3
                  and regress_block["non_monotonic"] == 1,
                  "union=0.45 cps_p95=%s sync=%s zero-shape non_monotonic=%s regression=%s"
                  % (cps_block["cps_p95"], cps_block["sync_suspect"],
                     zero_block["non_monotonic"], regress_block["non_monotonic"]))

        bad_srt = ("1\n-00:00:01,000 --> 00:00:05,000\nnegative\n\n"
                   "2\n00:00:10,000 --> 00:00:02,000\nreversed\n\n"
                   "3\n100:00:00,000 --> 100:00:05,000\nthree-digit hours\n\n"
                   "4\n00:00:05,000 --> 00:00:09,000\nfine\n")
        cues, invalid = parse_srt(bad_srt)
        self.case("srt-time-accounting",
                  len(cues) == 1 and invalid == 3 and cues[0][0] == 5.0
                  and dedupe(["b", "a", "b", "c", "a"]) == ["b", "a", "c"],
                  "cues=%d invalid=%d (negative, reversed, >99 h) dedupe=ok"
                  % (len(cues), invalid))

        block_json = ('{"input_i" : "-16.50", "input_tp" : "-4.66", "input_lra" : "1.20",\n'
                      ' "input_thresh" : "-26.55", "target_offset" : "0.03",\n'
                      ' "normalization_type" : "linear"}\n')
        poison = self.path("poison.log")
        with open(poison, "w", encoding="utf-8") as handle:
            handle.write("Metadata:\n    comment: %s" % block_json)
            handle.write("    silence_duration: 99999\n")
        with open(poison, encoding="utf-8") as handle:
            poisoned = handle.read()
        clean = self.path("clean.log")
        with open(clean, "w", encoding="utf-8") as handle:
            handle.write("[Parsed_loudnorm_0 @ 0x55aa] \n" + block_json)
            handle.write("[Parsed_ebur128_0 @ 0x55aa] Summary:\n  Integrated loudness:\n"
                         "    I:          -15.0 LUFS\n  Loudness range:\n    LRA:         1.4 LU\n"
                         "  True peak:\n    Peak:       -3.2 dBFS\n")
            handle.write("[Parsed_silencedetect_0 @ 0x55aa] silence_start: 0\n"
                         "[Parsed_silencedetect_0 @ 0x55aa] silence_end: 3 | "
                         "silence_duration: 3\n")
        with open(clean, encoding="utf-8") as handle:
            measured = following_json(handle.read(), LOUDNORM_MARKER)
        self.case("anchored-parsers",
                  following_json(poisoned, LOUDNORM_MARKER) is None
                  and following_section(poisoned, EBUR128_MARKER) == ""
                  and list(silence_durations(poison)) == []
                  and measured is not None
                  and loudness_measure(measured) is not None
                  and loudness_measure(dict(measured, input_i="1e300")) is None
                  and loudness_measure(dict(measured, input_tp="nan")) is None
                  and bounded_float("inf", -99.0, 99.0) is None
                  and list(silence_durations(clean)) == [3.0]
                  and "Summary:" in following_section(
                      open(clean, encoding="utf-8").read(), EBUR128_MARKER),
                  "metadata poison ignored, own-prefix lines accepted "
                  "(silence_duration=%s)" % list(silence_durations(clean)))

    def check_budget_and_dedupe(self, good_dir):
        """PRF-0551-1/QA-0551-5: the budget clamp, its wiring, and error de-duplication."""
        self.case("budget",
                  RUN_BUDGET_S == 840 and budgeted_timeout(600, 400.0) == 400
                  and budgeted_timeout(120, 900.0) == 120
                  and budgeted_timeout(120, 0.5) is None,
                  "RUN_BUDGET_S=%d clamp(600,400)=%s clamp(120,900)=%s exhausted=%s"
                  % (RUN_BUDGET_S, budgeted_timeout(600, 400.0), budgeted_timeout(120, 900.0),
                     budgeted_timeout(120, 0.5)))

        original_budget = globals()["RUN_BUDGET_S"]
        globals()["RUN_BUDGET_S"] = 0.0
        try:
            report, code = run_check(self.ctx(good_dir))
        finally:
            globals()["RUN_BUDGET_S"] = original_budget
        self.case("budget-exhausted",
                  code == 1 and report["errors"] == ["ffmpeg_timeout"]
                  and report["loudness"]["before"] is None
                  and report["silence"] == {"longest_s": 0.0, "total_s": 0.0}
                  and report["subtitles"]["present"] is True,
                  "spent budget: errors=%s loudness.before=%s silence=%s subtitles=%s rc=%d"
                  % (report["errors"], report["loudness"]["before"], report["silence"],
                     report["subtitles"]["present"], code))

        original_log = globals()["run_cmd_log"]
        globals()["run_cmd_log"] = lambda argv, timeout, log_path: (None, True)
        try:
            report, code = run_check(self.ctx(good_dir))
        finally:
            globals()["run_cmd_log"] = original_log
        self.case("dedupe-errors",
                  code == 1 and report["errors"] == ["ffmpeg_timeout"]
                  and report["warnings"] == [],
                  "step 3 and 5 both timed out: errors=%s (one code, first order)"
                  % report["errors"])

    def check_report_writes(self, good):
        """QA-0551-1/SEC-0551-3: `--out` guards and the fail-closed internal report."""
        directory = self.path("report-dir")
        os.makedirs(directory, exist_ok=True)
        for name in (VIDEO_FILE, AUDIO_FILE):
            shutil.copyfile(good, os.path.join(directory, name))
        args = ["run", "--task-dir", directory, "--aspect", "landscape", "--subtitles", "off"]

        victim = self.path("out-victim.json")
        with open(victim, "w", encoding="utf-8") as handle:
            handle.write("OUTSIDE")
        link = self.path("out-link.json")
        os.symlink(victim, link)
        code, _out, err = self.invoke(args + ["--out", link])
        with open(victim, encoding="utf-8") as handle:
            kept = handle.read()
        tmp_link = self.path("report-tmp.json.tmp")
        tmp_victim = self.path("tmp-victim.json")
        with open(tmp_victim, "w", encoding="utf-8") as handle:
            handle.write("OUTSIDE")
        os.symlink(tmp_victim, tmp_link)
        raised = None
        try:
            write_report(minimal_report(), self.path("report-tmp.json"))
        except EnvError as exc:
            raised = str(exc)
        with open(tmp_victim, encoding="utf-8") as handle:
            tmp_kept = handle.read()
        self.case("out-symlink",
                  code == 2 and kept == "OUTSIDE" and "Traceback" not in err
                  and raised is not None and tmp_kept == "OUTSIDE",
                  "exit=%d victim kept=%s report .tmp symlink refused=%s: %s"
                  % (code, kept == "OUTSIDE", raised is not None, err.strip()[:44]))

        read_only = self.path("read-only")
        os.makedirs(read_only, exist_ok=True)
        os.chmod(read_only, 0o500)
        code, _out, err = self.invoke(args + ["--out", os.path.join(read_only, "x.json")])
        os.chmod(read_only, 0o700)
        # root can write anywhere: only a non-root run can be denied
        self.case("out-unwritable",
                  code == 2 and "Traceback" not in err and err.strip(),
                  "exit=%d one-line message (no traceback): %s" % (code, err.strip()[:48]))

        out_path = self.path("internal.json")
        original = globals()["run_check"]

        def boom(_ctx):
            raise RuntimeError("self-test: injected failure")

        globals()["run_check"] = boom
        try:
            code, _out, err = self.invoke(args + ["--out", out_path])
        finally:
            globals()["run_check"] = original
        with open(out_path, encoding="utf-8") as handle:
            report = json.load(handle)
        blocks = [report[key] for key in ("output", "container", "video", "audio",
                                         "narration", "loudness", "silence", "subtitles")]
        self.case("internal-error",
                  code == 1 and report["errors"] == ["internal_error"]
                  and all(value is None for value in blocks) and "Traceback" in err,
                  "exit=%d errors=%s all-blocks-null=%s traceback=%s"
                  % (code, report["errors"], all(value is None for value in blocks),
                     "Traceback" in err))

    def check_presets(self):
        """D6 fallback + explicit-missing exit 2 + a real preset file's cue cap."""
        absent = self.path("no-such-presets.json")
        cap, warning = preset_char_cap(absent, "landscape", False)
        raised = None
        try:
            preset_char_cap(absent, "landscape", True)
        except EnvError as exc:
            raised = str(exc)
        real = self.path("subtitle-presets.v1.json")
        with open(real, "w", encoding="utf-8") as handle:
            json.dump({"schema": "subtitle-presets.v1", "revision": 1,
                       "presets": {"landscape": {"maxCharsPerLine": 10, "maxLines": 1},
                                   "portrait": {"maxCharsPerLine": 8, "maxLines": 2}}}, handle)
        real_cap, real_warning = preset_char_cap(real, "landscape", True)
        self.case("presets",
                  cap == 84 and warning == PRESETS_FALLBACK_WARNING
                  and raised is not None and real_cap == 10 and real_warning is None,
                  "absent -> %d chars + %s; explicit missing -> %r; preset file -> %d"
                  % (cap, warning, raised, real_cap))


def self_test(keep):
    test = SelfTest(keep)
    return test.run()


def main(argv):
    args = parse_args(argv)
    if args.self_test:
        try:
            return self_test(args.keep)
        except EnvError as exc:
            print("render_check.py: %s" % exc, file=sys.stderr)
            return 2
    if args.command != "run":
        print("render_check.py: choose `run` or `--self-test` (see --help)", file=sys.stderr)
        return 2
    try:
        ctx = build_context(args.task_dir, args.aspect, args.subtitles, args.normalize,
                            args.presets)
    except EnvError as exc:
        print("render_check.py: %s" % exc, file=sys.stderr)
        return 2
    out_path = args.out or os.path.join(ctx.task_dir, REPORT_FILE)
    # QA-0551-2: never write the report through a planted symlink.
    if os.path.islink(out_path):
        print("render_check.py: --out is a symlink, refusing to write through it: %s"
              % out_path, file=sys.stderr)
        return 2
    try:
        report, code = run_check(ctx)
    except EnvError as exc:
        print("render_check.py: %s" % exc, file=sys.stderr)
        return 2
    except Exception:
        # CRV-0551-1/SEC-0551-7: an unexpected failure still writes a
        # fail-closed report (every block null) and exits 1.
        traceback.print_exc()
        report, code = minimal_report(), 1
    try:
        payload = write_report(report, out_path)
    except EnvError as exc:
        print("render_check.py: %s" % exc, file=sys.stderr)
        return 2
    except OSError as exc:
        # an unwritable --out is an environment error: one line, no traceback
        print("render_check.py: cannot write the report to %s: %s" % (out_path, exc),
              file=sys.stderr)
        return 2
    for warning in report["warnings"]:
        print("render_check.py: warning: %s" % warning, file=sys.stderr)
    for error in report["errors"]:
        print("render_check.py: error: %s" % error, file=sys.stderr)
    print("render_check.py: wrote %s (exit %d)" % (out_path, code), file=sys.stderr)
    sys.stdout.write(payload)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
