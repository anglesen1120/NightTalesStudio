"""D5 admission tests (04.1.2-T2): proxy semaphore + upstream-429 retries
share one ``ADMISSION_WAIT_S`` budget; Retry-After parsing; Retry-After: 5
on both 429 codes; slot release after abort/disconnect."""

from __future__ import annotations

import asyncio
import json

import httpx
import pytest

from nts_vieneu_proxy.app import create_app
from nts_vieneu_proxy.upstream import parse_retry_after

from .conftest import DUMMY_KEY, VOICE_VI, fast_settings, sine_pcm
from .fakes import FakeVieneu

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PCM = sine_pcm(0.2)


def _body(**over) -> dict:
    b = {"input": "Xin chào.", "voice": VOICE_VI}
    b.update(over)
    return b


async def _client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


def _logs(capsys) -> list[dict]:
    return [
        json.loads(line)
        for line in capsys.readouterr().out.splitlines()
        if line.strip().startswith("{")
    ]


class SequencedVieneu(FakeVieneu):
    """FakeVieneu answering per-call statuses/headers from a script."""

    def __init__(self, script: list[tuple[int, dict | None]], **kw) -> None:
        super().__init__(**kw)
        self._script = list(script)
        self._default_headers = dict(self.headers)
    async def handler(self, request: httpx.Request) -> httpx.Response:
        if request.url.path != "/v1/audio/speech":
            return await super().handler(request)  # don't consume the script
        if self._script:
            status, headers = self._script.pop(0)
            self.status = status
            self.headers = {**self._default_headers, **(headers or {})}
        return await super().handler(request)






async def test_speech_upstream_429_budget_spent_returns_upstream_busy_retry_after_5(
    settings, catalog, capsys
):
    """Budget exhausted by retries → 429 upstream_busy + Retry-After: 5."""
    fake = SequencedVieneu(script=[(429, {"retry-after": "1"})] * 10)
    s = fast_settings(settings, admission_wait_s=1)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 429
    assert resp.headers["retry-after"] == "5"
    assert resp.json()["error"]["code"] == "upstream_busy"
    assert fake.call_count >= 1
    line = _logs(capsys)[-1]
    assert line["error_code"] == "upstream_busy" and line["status"] == 429


async def test_speech_upstream_429_retried_within_admission_budget(
    settings, catalog, monkeypatch
):
    """429 → wait (Retry-After) → retry succeeds; upstream called twice."""
    slept: list[float] = []
    real_sleep = asyncio.sleep

    async def spy(t):
        slept.append(t)
        await real_sleep(0.001)

    monkeypatch.setattr(asyncio, "sleep", spy)
    fake = SequencedVieneu(
        script=[(429, {"retry-after": "1"}), (200, None)],
        chunks=[PCM],
    )
    s = fast_settings(settings, admission_wait_s=5)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    assert fake.call_count == 2
    assert slept == [1.0]


async def test_speech_retry_after_capped_at_5_seconds(
    settings, catalog, monkeypatch
):
    """Retry-After: 60 is capped to 5 s (D5 min(Retry-After or 2, 5, left))."""
    slept: list[float] = []
    real_sleep = asyncio.sleep

    async def spy(t):
        slept.append(t)
        await real_sleep(0.001)

    monkeypatch.setattr(asyncio, "sleep", spy)
    fake = SequencedVieneu(
        script=[(429, {"retry-after": "60"}), (200, None)], chunks=[PCM]
    )
    s = fast_settings(settings, admission_wait_s=30)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    assert fake.call_count == 2
    assert slept == [5.0]


async def test_speech_retry_after_http_date_falls_back_to_2_seconds(
    settings, catalog, monkeypatch
):
    """An HTTP-date Retry-After is garbage (V3) → default 2 s wait."""
    slept: list[float] = []
    real_sleep = asyncio.sleep

    async def spy(t):
        slept.append(t)
        await real_sleep(0.001)

    monkeypatch.setattr(asyncio, "sleep", spy)
    fake = SequencedVieneu(
        script=[
            (429, {"retry-after": "Wed, 21 Oct 2015 07:28:00 GMT"}),
            (200, None),
        ],
        chunks=[PCM],
    )
    s = fast_settings(settings, admission_wait_s=30)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    assert slept == [2.0]


def test_parse_retry_after_unit():
    h = httpx.Headers({"retry-after": "3"})
    assert parse_retry_after(h) == 3.0
    assert parse_retry_after(httpx.Headers({})) is None
    assert parse_retry_after(httpx.Headers({"retry-after": "soon"})) is None
    assert (
        parse_retry_after(
            httpx.Headers({"retry-after": "Wed, 21 Oct 2015 07:28:00 GMT"})
        )
        is None
    )


async def test_speech_semaphore_full_returns_proxy_busy(
    settings, catalog, capsys
):
    """All slots held + no budget → 429 proxy_busy + Retry-After: 5."""
    fake = FakeVieneu(chunks=[PCM])
    s = fast_settings(settings, admission_wait_s=0)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    for _ in range(s.proxy_max_concurrent):
        await app.state.speech_slots.acquire()
    try:
        async with await _client(app) as c:
            resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    finally:
        for _ in range(s.proxy_max_concurrent):
            app.state.speech_slots.release()
    assert resp.status_code == 429
    assert resp.headers["retry-after"] == "5"
    assert resp.json()["error"]["code"] == "proxy_busy"
    assert fake.call_count == 0  # never reached upstream
    line = _logs(capsys)[-1]
    assert line["error_code"] == "proxy_busy" and line["status"] == 429


async def test_speech_admission_wait_ms_logged(settings, catalog, capsys):
    """D7: admission_wait_ms = arrival → first non-429 upstream answer."""
    fake = SequencedVieneu(
        script=[(429, {"retry-after": "0"}), (200, None)], chunks=[PCM]
    )
    s = fast_settings(settings, admission_wait_s=5)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    line = _logs(capsys)[-1]
    assert line["status"] == 200
    assert line["admission_wait_ms"] is not None
    assert line["admission_wait_ms"] >= 0


async def test_speech_slot_released_after_abort_and_disconnect(
    settings, catalog, capsys
):
    """Post-commit upstream break: slot returns to the pool (and on a
    second request the pipeline is admitted again)."""
    fake = FakeVieneu(chunks=[PCM, PCM], delays=[0, 0.05], raise_after=1)
    s = fast_settings(settings, upstream_read_gap_s=10)
    app = create_app(s, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        with pytest.raises(Exception):
            await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    # Slot must come back after teardown.
    deadline = asyncio.get_running_loop().time() + 5
    while app.state.active_pipelines and asyncio.get_running_loop().time() < deadline:
        await asyncio.sleep(0.02)
    assert app.state.active_pipelines == set()
    assert not app.state.speech_slots.locked()

    # Disconnect case: cancel mid-stream, slot is released again.
    fake2 = FakeVieneu(chunks=[PCM] * 60, delays=[0.01] * 60)
    app2 = create_app(s, catalog, vieneu_transport=fake2.transport())
    async with await _client(app2) as c:
        task = asyncio.ensure_future(
            c.post("/v1/audio/speech", json=_body(), headers=AUTH)
        )
        await asyncio.sleep(0.15)
        task.cancel()
        from contextlib import suppress

        with suppress(asyncio.CancelledError):
            await task
    deadline = asyncio.get_running_loop().time() + 5
    while (
        app2.state.active_pipelines
        and asyncio.get_running_loop().time() < deadline
    ):
        await asyncio.sleep(0.02)
    assert app2.state.active_pipelines == set()
    assert not app2.state.speech_slots.locked()
