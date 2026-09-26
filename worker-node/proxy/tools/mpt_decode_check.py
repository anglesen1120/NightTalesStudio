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
Exit 0 when the duration is finite and > 0, 1 otherwise, 2 on usage error.
"""

from __future__ import annotations

import json
import math
import sys


def main(argv: list[str]) -> int:
    if len(argv) != 2 or argv[1] in ("-h", "--help"):
        print(
            "usage: mpt_decode_check.py <mp3>  (run with MPT's interpreter: "
            "$NTS_HOME/src/mpt/.venv/bin/python)",
            file=sys.stderr,
        )
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
    try:
        clip = AudioFileClip(path)
        duration = clip.duration
    except Exception as exc:  # empty/truncated MP3, unreadable file, ...
        decode_error = f"{type(exc).__name__}"
    finally:
        if clip is not None:
            clip.close()

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
