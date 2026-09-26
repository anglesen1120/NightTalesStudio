"""ffmpeg PCM→MP3 encoder subprocess (README D4).

Exact argv (pinned): ``ffmpeg -hide_banner -loglevel error -f s16le -ar 48000
-ac 1 -i pipe:0 -vn -c:a libmp3lame -b:a <bitrate> -write_xing 0
-id3v2_version 0 -flush_packets 1 -f mp3 pipe:1`` — no shell, PIPE ×3, stderr
drained into a bounded last-8-KiB buffer.
"""

from __future__ import annotations

import asyncio
import os
from contextlib import suppress

_STDERR_LIMIT = 8192
_READ_CHUNK = 16384

# D13.1 (04 README §3): the proxy process holds PROXY_API_KEY and VIENEU_API_KEY
# in its environment; ffmpeg must never inherit them (or any other credential).
# The child env is built from this allow-list only — allow-list, never a
# deny-list — so a secret cannot leak by accident, not even under a name that no
# key/token/secret/password regex would have matched. `health.py` imports
# `child_env` from here; `tools/audition.py` mirrors this list and must stay
# equal to it (equality test, code review L-1).
_CHILD_ENV_KEYS = ("PATH", "HOME", "LANG", "LC_ALL", "TMPDIR")


def child_env() -> dict[str, str]:
    """Minimal allow-list env for every ffmpeg child (D13).

    Returns a fresh dict of only ``PATH``/``HOME``/``LANG``/``LC_ALL``/
    ``TMPDIR`` taken from :data:`os.environ`; absent keys are simply omitted.
    """
    return {key: os.environ[key] for key in _CHILD_ENV_KEYS if key in os.environ}


def ffmpeg_args(ffmpeg_path: str, bitrate: str) -> list[str]:
    """The exact D4 command line."""
    return [
        ffmpeg_path,
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "s16le",
        "-ar",
        "48000",
        "-ac",
        "1",
        "-i",
        "pipe:0",
        "-vn",
        "-c:a",
        "libmp3lame",
        "-b:a",
        bitrate,
        "-write_xing",
        "0",
        "-id3v2_version",
        "0",
        "-flush_packets",
        "1",
        "-f",
        "mp3",
        "pipe:1",
    ]


class Mp3Encoder:
    """One streaming ffmpeg process: s16le 48 kHz mono in, MP3 out."""

    def __init__(self, ffmpeg_path: str, bitrate: str) -> None:
        self._args = ffmpeg_args(ffmpeg_path, bitrate)
        self._proc: asyncio.subprocess.Process | None = None
        self._stderr = bytearray()
        self._stderr_task: asyncio.Task[None] | None = None

    async def start(self) -> None:
        """Spawn ffmpeg. ``OSError`` (missing binary, EACCES) propagates."""
        self._proc = await asyncio.create_subprocess_exec(
            *self._args,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=child_env(),
        )
        self._stderr_task = asyncio.create_task(self._drain_stderr())

    async def _drain_stderr(self) -> None:
        assert self._proc is not None and self._proc.stderr is not None
        while True:
            chunk = await self._proc.stderr.read(4096)
            if not chunk:
                return
            self._stderr.extend(chunk)
            if len(self._stderr) > _STDERR_LIMIT:
                del self._stderr[: len(self._stderr) - _STDERR_LIMIT]

    async def feed(self, data: bytes) -> None:
        """Write one PCM chunk; ``OSError`` means ffmpeg's stdin is dead."""
        assert self._proc is not None and self._proc.stdin is not None
        self._proc.stdin.write(data)
        await self._proc.stdin.drain()

    async def close(self) -> None:
        """Close ffmpeg's stdin (EOF → it flushes the MP3 tail and exits)."""
        proc = self._proc
        if proc is None or proc.stdin is None or proc.stdin.is_closing():
            return
        proc.stdin.close()
        with suppress(OSError):
            await proc.stdin.wait_closed()

    async def read(self, n: int = _READ_CHUNK) -> bytes:
        """Next MP3 chunk; ``b""`` on EOF."""
        assert self._proc is not None and self._proc.stdout is not None
        return await self._proc.stdout.read(n)

    async def wait(self) -> int:
        """Wait for ffmpeg to exit; returns the exit code."""
        assert self._proc is not None
        rc = await self._proc.wait()
        await self._join_stderr_task()
        return rc

    async def kill(self) -> None:
        """Idempotent SIGKILL + reap; never leaves a zombie."""
        proc = self._proc
        if proc is None:
            return
        if proc.returncode is None:
            with suppress(ProcessLookupError):
                proc.kill()
        with suppress(Exception):
            await proc.wait()
        await self._join_stderr_task()

    async def _join_stderr_task(self) -> None:
        task = self._stderr_task
        if task is not None and not task.done():
            task.cancel()
            with suppress(asyncio.CancelledError):
                await task

    @property
    def stderr_tail(self) -> str:
        """Last ≤ 8 KiB of ffmpeg stderr (D4 `ffmpeg_stderr_tail`)."""
        return bytes(self._stderr[-_STDERR_LIMIT:]).decode("utf-8", "replace")

    @property
    def pid(self) -> int | None:
        return self._proc.pid if self._proc is not None else None
