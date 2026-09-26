#!/usr/bin/env python3
"""worker-node/proxy/tools/mpt_decode_check.py — 04 README D14 decode check.

Decodes one MP3 with MoviePy exactly as MPT does (MPT M3: MoviePy 2.2.1,
`AudioFileClip`), so the 04.1.2-T3 baseline can compare the written file with
the proxy's own `audio_seconds`. Stdlib + MoviePy only; no other dependency.

Run with MPT's own interpreter (the venv MPT runs its tasks in):

  "$NTS_HOME/src/mpt/.venv/bin/python" \\
      worker-node/proxy/tools/mpt_decode_check.py <mp3>

Prints exactly one JSON line on stdout: {"path": ..., "duration_s": ...} —
`duration_s` is null when MoviePy cannot report a finite positive duration.
Exit 0 when the duration is finite and > 0, 1 otherwise, 2 on usage error
(``-h``/``--help`` prints usage to stdout and exits 0).
"""

from __future__ import annotations

import gc
import json
import math
import sys

USAGE = (
    "usage: mpt_decode_check.py <mp3>  (run with MPT's interpreter: "
    "$NTS_HOME/src/mpt/.venv/bin/python)"
)


def main(argv: list[str]) -> int:
    if len(argv) >= 2 and argv[1] in ("-h", "--help"):
        print(USAGE)  # L-3: --help to stdout, exit 0 (01 D9)
        return 0
    if len(argv) != 2:
        print(USAGE, file=sys.stderr)
        return 2

    path = argv[1]
    try:
        from moviepy.audio.io.AudioFileClip import AudioFileClip
    except Exception as exc:  # pragma: no cover - environment error
        print(f"mpt_decode_check: cannot import MoviePy: {exc}", file=sys.stderr)
        return 2

    clip = None
    duration = None
    decode_error = None
    # QA-PR7-6: a reader that failed to initialise prints "Exception ignored in:
    # <function FFMPEG_AudioReader.__del__>" (MoviePy readers.py:294-304) at
    # collection time; CPython hands the hook the *function* as `object`, so
    # match its qualname/module and forward everything else to the old hook.
    previous_hook = sys.unraisablehook

    def quiet_moviepy_reader_del(unraisable) -> None:
        obj = unraisable.object
        if (getattr(obj, "__qualname__", "") == "FFMPEG_AudioReader.__del__"
                and (getattr(obj, "__module__", "") or "").startswith("moviepy")):
            return
        previous_hook(unraisable)

    sys.unraisablehook = quiet_moviepy_reader_del
    try:
        try:
            clip = AudioFileClip(path)
            duration = clip.duration
        except Exception as exc:  # empty/truncated MP3, unreadable file, ...
            decode_error = f"{type(exc).__name__}"
        finally:
            if clip is not None:
                clip.close()
        gc.collect()  # finalise the failed reader while the hook is installed
    finally:
        sys.unraisablehook = previous_hook

    # QA-PR7-5: a truncated MP3 with >= 1 valid frame passes here by design (D14: rc 1 only for a non-finite or <= 0 duration); truncation is caught by the bench's duration check (bench-proxy.sh), not by this tool.
    duration_s: float | None
    try:
        value = float(duration)  # type: ignore[arg-type]
        duration_s = round(value, 3) if math.isfinite(value) else None
    except (TypeError, ValueError):
        value = float("nan")
        duration_s = None

    record: dict[str, object] = {"path": path, "duration_s": duration_s}
    if decode_error is not None:
        record["decode_error"] = decode_error
    print(json.dumps(record, ensure_ascii=False))
    if decode_error is not None or duration_s is None or value <= 0:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
