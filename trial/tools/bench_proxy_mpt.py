#!/usr/bin/env python3
"""trial/tools/bench_proxy_mpt.py — one TTS call through MPT's own code path.

04.1.2-T3 helper: it exercises exactly what the render pipeline exercises —
MPT's pinned `requests` client and MoviePy decode (M1-M3, M9) — against the
running VieNeu proxy, and reports MPT's own view of the result.

Run from the MPT root with MPT's interpreter (its config.toml is read from the
current directory, and `app` is imported from it):

  cd "$NTS_HOME/src/mpt" && .venv/bin/python \\
      "$REPO/trial/tools/bench_proxy_mpt.py" <fixture.txt> <voice> <out.mp3>

Prints exactly one JSON line on stdout:

  {"fixture": ..., "voice": ..., "ok": ..., "client_wall_ms": ..., "out_bytes": ...}

`ok` is true when MPT's `voice.tts()` returned a SubMaker (not None) and the
output file exists. The input text is never printed or passed on any argv; the
fixture path and the output path are the only arguments. Exit 0 when ok, 1 when
MPT reported a failure, 2 on usage/environment error.
"""

from __future__ import annotations

import json
import os
import sys
import time


def fail_usage(msg: str) -> int:
    print(f"bench_proxy_mpt: {msg}", file=sys.stderr)
    print(
        "usage: (cd $NTS_HOME/src/mpt && .venv/bin/python "
        "<repo>/trial/tools/bench_proxy_mpt.py <fixture.txt> <voice> <out.mp3>)",
        file=sys.stderr,
    )
    return 2


def main(argv: list[str]) -> int:
    if len(argv) != 4 or argv[1] in ("-h", "--help"):
        return fail_usage("expected exactly three arguments")

    fixture_path, voice, out_path = argv[1], argv[2], argv[3]
    if not os.path.isfile(fixture_path):
        return fail_usage(f"fixture not found: {fixture_path}")
    if not os.path.isfile("app/services/voice.py") or not os.path.isfile("config.toml"):
        return fail_usage(
            "current directory is not the MPT root (needs app/services/voice.py "
            "and config.toml); cd $NTS_HOME/src/mpt first"
        )

    # MPT's app.config configures loguru with sys.stdout as its sink at import
    # time. Keep stdout clean for the single JSON line: bind MPT's logging to
    # our stderr before importing anything from `app`.
    real_stdout = sys.stdout
    sys.stdout = sys.stderr
    try:
        sys.path.insert(0, os.getcwd())
        from app.services import voice as mpt_voice  # noqa: PLC0415

        with open(fixture_path, encoding="utf-8") as fh:
            text = fh.read()
        if os.path.exists(out_path):
            os.remove(out_path)

        started = time.monotonic()
        sub_maker = mpt_voice.tts(
            text=text,
            voice_name=f"kokoro:{voice}",
            voice_rate=1.0,
            voice_file=out_path,
        )
        client_wall_ms = int(round((time.monotonic() - started) * 1000))
    finally:
        sys.stdout = real_stdout

    exists = os.path.exists(out_path)
    record = {
        "fixture": os.path.basename(fixture_path),
        "voice": voice,
        "ok": sub_maker is not None and exists,
        "client_wall_ms": client_wall_ms,
        "out_bytes": os.path.getsize(out_path) if exists else 0,
    }
    real_stdout.write(json.dumps(record, ensure_ascii=False) + "\n")
    real_stdout.flush()
    return 0 if record["ok"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
