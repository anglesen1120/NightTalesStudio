"""D7 request log line: one compact JSON object per request on stdout.

`status` is the logical outcome (200 only when the complete MP3 was
delivered); `http_status` is the status line actually sent (None when none
was sent — e.g. a post-commit abort). Failure-only keys
`ffmpeg_stderr_tail` and `warnings` are appended only when non-empty (T2).

Never logged (D7): input text, request/response bodies, headers, keys.
"""

from __future__ import annotations

import json
import sys
from dataclasses import asdict, dataclass, field


@dataclass
class RequestLog:
    ts: str
    rid: str
    route: str
    method: str
    voice: str | None = None
    chars: int | None = None
    audio_seconds: float | None = None
    vieneu_ms: int | None = None
    ffmpeg_ms: int | None = None
    ttfb_ms: int | None = None
    max_gap_ms: int | None = None
    admission_wait_ms: int | None = None
    bytes_out: int = 0
    status: int | None = None
    http_status: int | None = None
    error_code: str | None = None
    upstream_rid: str | None = None
    ignored_fields: list[str] = field(default_factory=list)
    wall_ms: int | None = None
    # Failure-only keys (omitted from the emitted line when empty):
    ffmpeg_stderr_tail: str | None = None
    warnings: list[str] | None = None


def emit(entry: RequestLog) -> None:
    d = asdict(entry)
    # D7 bound (P1): ≤ 16 names, each ≤ 64 chars, then "+<n> more".
    names = [str(n)[:64] for n in d["ignored_fields"][:16]]
    extra = len(d["ignored_fields"]) - 16
    if extra > 0:
        names.append(f"+{extra} more")
    d["ignored_fields"] = names
    if not d["ffmpeg_stderr_tail"]:
        del d["ffmpeg_stderr_tail"]
    if not d["warnings"]:
        del d["warnings"]
    sys.stdout.write(json.dumps(d, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()
