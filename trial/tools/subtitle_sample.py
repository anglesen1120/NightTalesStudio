#!/usr/bin/env python3
"""subtitle_sample.py — draw the human sync-review sample from an MPT `subtitle.srt`
(05.2.1-T1 step 6, D9; stdlib only, `uv run --no-project --python 3.12`).

    uv run --no-project --python 3.12 trial/tools/subtitle_sample.py \\
        --srt "$RUNS/<id>/subtitle.srt" --n 20 --seed <run_id> [--csv "$RUNS/<id>/sample.csv"]

Draws `min(n, cues)` cue indices with `random.Random(seed).sample(range(cues), k)`,
sorted, and writes them to stdout as CSV with the header
`cue_id,start,end,duration,chars,text`:
    cue_id   1-based index of the cue in the SRT (the order MPT wrote it)
    start    cue start, seconds with 3 decimals (seek the video here)
    end      cue end, seconds with 3 decimals
    duration end - start, seconds with 3 decimals
    chars    length of the cue text with whitespace collapsed (the measure
             `render_check.py` reports as `max_cue_chars`/`long_cues`)
    text     the cue text on one line (inner newlines collapse to spaces)
The sample is deterministic for the same `--srt` + `--n` + `--seed`.

`--csv FILE` writes the same rows plus a trailing `label` column, left empty: the
reviewer plays the video from each `start` and writes one of `on_time` (start
within +- 0.5 s of the spoken onset), `early`, `late`, `wrong_window`. Labels come
only from a human reviewer; this tool never fills them from a heuristic (05.2.1-T1
step 6 / measure b). A cue text that starts with `=` `+` `-` `@` TAB or CR is
written into the CSV with a leading single quote (spreadsheet-formula guard,
SEC-0521-1); stdout and the `chars` count keep the raw text.

The SRT is read through a single `O_NONBLOCK|O_CLOEXEC` fd, must be a regular file
(a FIFO, device, directory or symlink reads as unreadable) and is refused, never
truncated, when it is larger than 16 MiB (SEC-0521-2). `--csv` may not name the same
file as `--srt` (SEC-0521-3).

Exit codes (01 D9): 0 sample written · 1 the SRT is unreadable or holds no valid
cue · 2 usage/environment (bad arguments, `--n` < 1, `--csv` not writable or the
same file as `--srt`).
"""

import argparse
import csv
import os
import random
import re
import stat
import sys

MAX_SRT_BYTES = 16 << 20  # the cap render_check.py (SRT_MAX_BYTES) reads an SRT under
EXIT_OK, EXIT_CHECK, EXIT_ENV = 0, 1, 2

# SEC-0521-1: a cue text that starts with one of these would be read as a formula by a
# spreadsheet that opens sample.csv, so the --csv writer prefixes it with a single quote.
# The stdout listing and the `chars` count always keep the raw text.
FORMULA_PREFIXES = ("=", "+", "-", "@", "\t", "\r")

# Anchored whole-line cue time line, identical to render_check.py's TIME_LINE_RE
# (QA-0551-3): a negative sign, a 3-digit hour or an out-of-range minute/second is
# invalid, never sign-flipped and never silently dropped.
TIME_LINE_RE = re.compile(
    r"^\s*(\d{1,2}):([0-5]\d):([0-5]\d)[,.](\d{1,3})\s*-->\s*"
    r"(\d{1,2}):([0-5]\d):([0-5]\d)[,.](\d{1,3})\s*$")
TIME_LINE_HINT_RE = re.compile(r"-->")


class CheckError(Exception):
    """The SRT cannot be read or holds no cue -> exit 1."""


class EnvError(Exception):
    """Usage/environment failure -> exit 2."""


def collapse_ws(text):
    return " ".join(text.split())


def _seconds(hours, minutes, seconds, millis):
    return (int(hours) * 3600 + int(minutes) * 60 + int(seconds)
            + int(millis.ljust(3, "0")) / 1000.0)


def parse_srt(raw):
    """Parse SRT text into `(cues, invalid)`; a cue is `(start_s, end_s, text)`.

    Same block splitting and time-line anchoring as `render_check.py`. The number
    line is not trusted: `cue_id` is the parsed order. A block that carries an
    arrow but no valid time line, or `end < start`, counts as invalid (reported on
    stderr, never sampled) instead of failing the whole file.
    """
    cues = []
    invalid = 0
    text = raw.lstrip("\ufeff")
    for block in re.split(r"\n[ \t]*\n", text):
        lines = block.split("\n") if block.strip() else []
        if not lines:
            continue
        match = None
        index = 0
        for position, line in enumerate(lines):
            if TIME_LINE_HINT_RE.search(line):
                match = TIME_LINE_RE.match(line)
                index = position
                break
        if match is None:
            if any(TIME_LINE_HINT_RE.search(line) for line in lines):
                invalid += 1
            continue
        start = _seconds(*match.group(1, 2, 3, 4))
        end = _seconds(*match.group(5, 6, 7, 8))
        if end < start:
            invalid += 1
            continue
        cue_lines = list(lines[index + 1:])
        while cue_lines and not cue_lines[-1].strip():
            cue_lines.pop()
        cues.append((start, end, collapse_ws("\n".join(cue_lines))))
    return cues, invalid


def read_srt(path):
    """Read the SRT through one fd (SEC-0521-2).

    `os.open` with `O_NONBLOCK` means a FIFO is refused instead of blocking on a writer;
    `fstat` on the fd (not `stat` on the path) decides regular-or-not, so a symlink, a
    device, a directory or a FIFO each read as "not a regular file" -> exit 1. The read is
    bounded at `MAX_SRT_BYTES` + 1 bytes and refuses, never truncates.
    """
    flags = (os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NONBLOCK", 0)
             | getattr(os, "O_NOFOLLOW", 0))  # FR-1: a symlink fails with ELOOP -> exit 1
    try:
        fd = os.open(path, flags)
    except OSError as exc:
        raise CheckError(f"{path}: {exc.strerror or exc}") from exc
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode):
            raise CheckError(f"{path}: not a regular file")
        chunks = []
        total = 0
        while total <= MAX_SRT_BYTES:
            chunk = os.read(fd, min(1 << 20, MAX_SRT_BYTES + 1 - total))
            if not chunk:
                break
            chunks.append(chunk)
            total += len(chunk)
        if total > MAX_SRT_BYTES:
            raise CheckError(f"{path}: larger than the {MAX_SRT_BYTES}-byte cap")
        raw_bytes = b"".join(chunks)
    except CheckError:
        raise
    except OSError as exc:
        raise CheckError(f"{path}: {exc.strerror or exc}") from exc
    finally:
        os.close(fd)
    try:
        raw = raw_bytes.decode("utf-8-sig")
    except UnicodeDecodeError as exc:
        raise CheckError(f"{path}: not UTF-8 ({exc})") from exc
    if not raw.strip():
        raise CheckError(f"{path}: empty")
    cues, invalid = parse_srt(raw)
    if not cues:
        raise CheckError(f"{path}: no cue with a valid `HH:MM:SS,mmm --> HH:MM:SS,mmm` line")
    if invalid:
        print(f"subtitle_sample.py: WARN {invalid} block(s) carry an unusable time line",
              file=sys.stderr)
    return cues


def same_file(left, right):
    """True when the two paths are the same file (SEC-0521-3); `samefile` when both exist,
    `realpath` otherwise, so a not-yet-created `--csv` target is caught too."""
    try:
        if os.path.exists(left) and os.path.exists(right) and os.path.samefile(left, right):
            return True
    except OSError:
        return False
    return os.path.realpath(left) == os.path.realpath(right)


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="subtitle_sample.py",
        description="Draw the deterministic human sync-review sample from an MPT subtitle.srt (05.2.1-T1).",
    )
    parser.add_argument("--srt", required=True, metavar="FILE", help="subtitle.srt to sample")
    parser.add_argument("--n", required=True, type=int, metavar="N", help="cues to draw (>= 1)")
    parser.add_argument("--seed", required=True, metavar="SEED",
                        help="random seed; the M0 convention is the run id, so a re-run reproduces the sample")
    parser.add_argument("--csv", metavar="FILE",
                        help="also write the sample with an empty `label` column for the reviewer")
    return parser.parse_args(argv)


def csv_safe(text):
    """SEC-0521-1: a spreadsheet eats `= + - @ TAB CR` as a formula; prefix one quote."""
    return "'" + text if text.startswith(FORMULA_PREFIXES) else text


def write_csv(path, rows):
    try:
        with open(path, "w", encoding="utf-8", newline="") as handle:
            writer = csv.writer(handle, lineterminator="\n")
            writer.writerow(["cue_id", "start", "end", "duration", "chars", "text", "label"])
            for row in rows:
                guarded = list(row)
                guarded[5] = csv_safe(str(row[5]))
                writer.writerow(guarded + [""])
    except OSError as exc:
        raise EnvError(f"{path}: cannot write ({exc.strerror or exc})") from exc


def main(argv):
    args = parse_args(argv)
    if args.n < 1:
        print(f"subtitle_sample.py: --n must be >= 1 (got {args.n})", file=sys.stderr)
        return EXIT_ENV
    if args.csv and same_file(args.csv, args.srt):
        print(f"subtitle_sample.py: --csv {args.csv} is the --srt file; refusing to overwrite "
              "the SRT with the sample (SEC-0521-3)", file=sys.stderr)
        return EXIT_ENV
    try:
        cues = read_srt(args.srt)
    except CheckError as exc:
        print(f"subtitle_sample.py: {exc}", file=sys.stderr)
        return EXIT_CHECK
    count = min(args.n, len(cues))
    picked = sorted(random.Random(args.seed).sample(range(len(cues)), count))
    rows = []
    for index in picked:
        start, end, text = cues[index]
        rows.append([index + 1, f"{start:.3f}", f"{end:.3f}", f"{end - start:.3f}",
                     len(text), text])
    out = csv.writer(sys.stdout, lineterminator="\n")
    out.writerow(["cue_id", "start", "end", "duration", "chars", "text"])
    out.writerows(rows)
    if args.csv:
        try:
            write_csv(args.csv, rows)
        except EnvError as exc:
            print(f"subtitle_sample.py: {exc}", file=sys.stderr)
            return EXIT_ENV
    print(f"subtitle_sample.py: {len(cues)} cues, sampled {count} "
          f"(seed {args.seed!r}, n {args.n})", file=sys.stderr)
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
