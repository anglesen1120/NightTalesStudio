"""transcode.py — exact D4 argv, real ffmpeg on generated sine PCM."""

from __future__ import annotations

import asyncio
import json
import shutil
import subprocess

import pytest

from nts_vieneu_proxy.transcode import Mp3Encoder, ffmpeg_args

from .conftest import sine_pcm

FFMPEG = shutil.which("ffmpeg")
FFPROBE = shutil.which("ffprobe")

pytestmark = pytest.mark.skipif(FFMPEG is None, reason="ffmpeg not on PATH")


def test_transcode_ffmpeg_args_exact_d4_command():
    assert ffmpeg_args("ffmpeg", "192k") == [
        "ffmpeg",
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
        "192k",
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


@pytest.mark.asyncio
async def test_transcode_sine_pcm_duration_within_40ms(tmp_path):
    seconds = 0.5
    pcm = sine_pcm(seconds)
    enc = Mp3Encoder(FFMPEG, "192k")
    await enc.start()
    feeder = asyncio.create_task(_feed_all(enc, pcm))
    mp3 = bytearray()
    while True:
        chunk = await enc.read()
        if chunk == b"":
            break
        mp3.extend(chunk)
    await feeder
    rc = await enc.wait()
    assert rc == 0
    assert len(mp3) > 0
    # First MP3 frame must start with a frame sync word (no ID3/Xing, X1).
    assert mp3[0] == 0xFF and (mp3[1] & 0xE0) == 0xE0

    out = tmp_path / "sine.mp3"
    out.write_bytes(bytes(mp3))
    probe = subprocess.run(
        [
            FFPROBE,
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "json",
            str(out),
        ],
        capture_output=True,
        text=True,
        check=True,
    )
    duration = float(json.loads(probe.stdout)["format"]["duration"])
    # X1: streamed MP3 runs ≈ 25–30 ms longer than the PCM.
    assert abs(duration - seconds) <= 0.040


@pytest.mark.asyncio
async def test_transcode_kill_reaps_process():
    enc = Mp3Encoder(FFMPEG, "192k")
    await enc.start()
    await enc.feed(sine_pcm(0.05))
    pid = enc.pid
    assert pid is not None
    await enc.kill()
    await enc.kill()  # idempotent
    assert enc._proc.returncode is not None  # reaped, no zombie
    # The process is gone from the table.
    rc = subprocess.run(["kill", "-0", str(pid)], capture_output=True).returncode
    assert rc != 0


async def _feed_all(enc: Mp3Encoder, pcm: bytes) -> None:
    try:
        for off in range(0, len(pcm), 8192):
            await enc.feed(pcm[off : off + 8192])
    finally:
        await enc.close()
