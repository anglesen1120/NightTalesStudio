"""reqlog.py — D7 field set, one line per request, logical vs HTTP status."""

from __future__ import annotations

import json

import httpx
import pytest

from nts_vieneu_proxy.app import create_app

from .conftest import DUMMY_KEY, VOICE_VI, sine_pcm
from .fakes import FakeVieneu

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PCM = sine_pcm(0.5)

_D7_REQUIRED = {
    "ts",
    "rid",
    "route",
    "method",
    "voice",
    "chars",
    "audio_seconds",
    "vieneu_ms",
    "ffmpeg_ms",
    "ttfb_ms",
    "max_gap_ms",
    "admission_wait_ms",
    "bytes_out",
    "status",
    "http_status",
    "error_code",
    "upstream_rid",
    "ignored_fields",
    "wall_ms",
}
_D7_FAILURE_ONLY = {"ffmpeg_stderr_tail", "warnings"}


def _lines(capsys) -> list[dict]:
    return [
        json.loads(l)
        for l in capsys.readouterr().out.splitlines()
        if l.startswith('{"ts"')
    ]


@pytest.mark.asyncio
async def test_reqlog_one_line_per_request_without_input_text(settings, catalog, capsys):
    fake = FakeVieneu(chunks=[PCM])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app), base_url="http://t"
    ) as client:
        secret = "Một đêm khuya, khu rừng cũ thì thầm."
        resp = await client.post(
            "/v1/audio/speech",
            json={"input": secret, "voice": VOICE_VI},
            headers=AUTH,
        )
        assert resp.status_code == 200
        resp2 = await client.post(
            "/v1/audio/speech",
            json={"input": secret, "voice": VOICE_VI},
            headers={"Authorization": "Bearer wrong-wrong-wrong-wrong-wrong-wrong"},
        )
        assert resp2.status_code == 401

    raw = capsys.readouterr().out
    lines = _lines_from(raw)
    assert len(lines) == 2
    for line in lines:
        assert _D7_REQUIRED <= set(line)
        assert not (set(line) - _D7_REQUIRED - _D7_FAILURE_ONLY), set(line)
        assert secret not in json.dumps(line, ensure_ascii=False)
        assert secret.split(",")[0] not in raw  # belt and braces on raw stdout
    assert lines[0]["status"] == 200
    assert lines[1]["status"] == 401
    assert lines[0]["voice"] == VOICE_VI


def _lines_from(raw: str) -> list[dict]:
    return [json.loads(l) for l in raw.splitlines() if l.startswith('{"ts"')]


@pytest.mark.asyncio
async def test_reqlog_status_logical_vs_http_status(settings, catalog, capsys):
    """Post-commit abort: logical status 502, http_status 200 (F13)."""
    fake = FakeVieneu(chunks=[PCM, PCM], delays=[0, 0.3], raise_after=1)
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app), base_url="http://t"
    ) as client:
        with pytest.raises(Exception):
            await client.post(
                "/v1/audio/speech",
                json={"input": "Xin chào.", "voice": VOICE_VI},
                headers=AUTH,
            )
    line = _lines(capsys)[0]
    assert line["status"] == 502
    assert line["http_status"] == 200
    assert line["error_code"] == "upstream_stream_broken"
    assert line["audio_seconds"] > 0  # PCM that did arrive is still counted
