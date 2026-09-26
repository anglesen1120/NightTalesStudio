"""D6 error mapping tests (04.1.2-T2): upstream status → proxy error,
body shape, and one reachable scenario per code in ``errors.CODES``."""

from __future__ import annotations

import asyncio
import json
from contextlib import suppress

import httpx
import pytest

from nts_vieneu_proxy.app import SpeechPipeline, create_app
from nts_vieneu_proxy.errors import CODES, ProxyError, error_body, map_upstream_status

from .conftest import DUMMY_KEY, VOICE_VI, fast_settings, sine_pcm
from .fakes import FakeVieneu
from .wire_server import wait_for

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PCM = sine_pcm(0.2)


def _body(**over) -> dict:
    b = {"input": "Xin chào.", "voice": VOICE_VI}
    b.update(over)
    return b


def _app(settings, catalog, fake):
    return create_app(settings, catalog, vieneu_transport=fake.transport())


async def _client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


def _logs(capsys) -> list[dict]:
    return [
        json.loads(line)
        for line in capsys.readouterr().out.splitlines()
        if line.strip().startswith("{")
    ]


# --- unit-level mapping -------------------------------------------------------


async def test_errors_upstream_400_maps_to_upstream_rejected_with_truncated_message(
    settings, catalog
):
    fake = FakeVieneu(status=400, error_body={"error": {"message": "x" * 500}})
    async with await _client(_app(settings, catalog, fake)) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 400
    err = resp.json()["error"]
    assert err["code"] == "upstream_rejected"
    assert err["message"] == "x" * 200  # D2 cap
    # Unit check of the default when VieNeu sends no error.message.
    e = map_upstream_status(400, None)
    assert e.code == "upstream_rejected" and e.message == "upstream rejected request"


async def test_errors_upstream_401_maps_to_502_upstream_auth_failed(
    settings, catalog
):
    fake = FakeVieneu(status=401)
    async with await _client(_app(settings, catalog, fake)) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_auth_failed"


async def test_errors_upstream_500_maps_to_502_upstream_unavailable(
    settings, catalog
):
    fake = FakeVieneu(status=500)
    async with await _client(_app(settings, catalog, fake)) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_unavailable"


async def test_errors_upstream_connect_error_maps_to_502_upstream_unavailable(
    settings, catalog
):
    def boom(request):
        raise httpx.ConnectError("refused", request=request)

    app = create_app(settings, catalog, vieneu_transport=httpx.MockTransport(boom))
    async with await _client(app) as c:
        resp = await c.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_unavailable"


def test_errors_body_shape_matches_d2_and_has_no_input_text():
    body = error_body("invalid_request", "some message")
    assert set(body) == {"error"}
    assert set(body["error"]) == {"message", "type", "code"}
    assert body["error"]["type"] == "proxy_error"
    assert body["error"]["code"] == "invalid_request"
    # D2: message is capped at 200 chars and never echoes input text.
    assert len(error_body("invalid_request", "m" * 999)["error"]["message"]) == 200


def test_proxy_error_refuses_unknown_code():
    with pytest.raises(AssertionError):
        ProxyError("not_a_d6_code", 500, "x")


# --- every D6 code reachable --------------------------------------------------

# Post-commit aborts / log-only outcomes: no HTTP status line carries the
# code; the D7 line is the assertion surface (per the task's acceptance).
_LOG_ONLY = {
    "upstream_stream_broken": 502,
    "request_max_exceeded": 502,
    "client_disconnected": 499,
    "internal_error": 500,
}


async def _drain_done(app):
    """Wait (off-loop) for detached teardowns to finish and release slots."""
    loop = asyncio.get_running_loop()
    await loop.run_in_executor(
        None,
        lambda: wait_for(
            lambda: not app.state.active_pipelines, 15.0, "drain end"
        ),
    )


@pytest.mark.parametrize("code", sorted(CODES))
async def test_errors_each_d6_code_reachable(
    code, settings, catalog, capsys, monkeypatch
):
    """One scripted fake-VieNeu/request per D6 code; asserts status +
    ``error.code`` (abort/log-only codes are asserted on the D7 line)."""
    s = settings
    fake = FakeVieneu(chunks=[PCM])
    payload = _body()
    headers = AUTH

    if code == "proxy_unauthorized":
        headers = {"Authorization": "Bearer wrong"}
    elif code == "invalid_request":
        pass  # payload overridden below
    elif code == "unsupported_format":
        payload["response_format"] = "wav"
    elif code == "voice_not_in_catalog":
        payload["voice"] = "Not A Voice"
    elif code == "input_too_long":
        payload["input"] = "a" * 20_001
    elif code == "body_too_large":
        pass  # streamed body below
    elif code == "proxy_busy":
        s = fast_settings(settings, admission_wait_s=0)
    elif code == "upstream_busy":
        s = fast_settings(settings, admission_wait_s=0)
        fake = FakeVieneu(status=429)
    elif code == "upstream_rejected":
        fake = FakeVieneu(status=400)
    elif code == "upstream_auth_failed":
        fake = FakeVieneu(status=403)
    elif code == "upstream_unavailable":
        fake = FakeVieneu(status=503)
    elif code == "upstream_bad_format":
        fake = FakeVieneu(
            headers={"content-type": "text/plain", "x-sample-rate": "48000"}
        )
    elif code == "upstream_first_audio_timeout":
        s = fast_settings(
            settings, first_audio_deadline_s=0.05, upstream_read_gap_s=0.05
        )
        fake = FakeVieneu(stall=True)
    elif code == "upstream_empty_audio":
        fake = FakeVieneu(chunks=[])
    elif code == "transcode_failed":
        s = fast_settings(settings, ffmpeg_path=None)
    elif code == "upstream_stream_broken":
        fake = FakeVieneu(chunks=[PCM, PCM], delays=[0, 0.05], raise_after=1)
    elif code == "request_max_exceeded":
        s = fast_settings(settings, upstream_read_gap_s=10, request_max_s=0.05)
        fake = FakeVieneu(chunks=[PCM] * 8, delays=[0.05] * 8)
    elif code == "client_disconnected":
        fake = FakeVieneu(chunks=[PCM] * 60, delays=[0.01] * 60)
    elif code == "internal_error":
        pass  # monkeypatched below
    elif code in ("route_not_found", "method_not_allowed"):
        pass  # path/method overridden below
    else:
        raise AssertionError(f"no scenario for {code}")

    app = _app(s, catalog, fake)
    if code == "internal_error":
        monkeypatch.setattr(
            SpeechPipeline,
            "admit",
            lambda self: (_ for _ in ()).throw(RuntimeError("boom")),
        )

    async with await _client(app) as c:
        if code == "proxy_busy":
            # Exhaust the semaphore; acquire times out immediately.
            for _ in range(s.proxy_max_concurrent):
                await app.state.speech_slots.acquire()
            try:
                resp = await c.post("/v1/audio/speech", json=payload, headers=headers)
            finally:
                for _ in range(s.proxy_max_concurrent):
                    app.state.speech_slots.release()
        elif code == "route_not_found":
            resp = await c.post("/nope", json=payload, headers=headers)
        elif code == "method_not_allowed":
            resp = await c.get("/v1/audio/speech", headers=headers)
        elif code == "invalid_request":
            resp = await c.post(
                "/v1/audio/speech", content=b"not json", headers=headers
            )
        elif code == "body_too_large":
            # Chunked (no Content-Length): the counting limit decides.
            async def chunks():
                for _ in range(5):
                    yield b"z" * 65_536

            resp = await c.post(
                "/v1/audio/speech",
                content=chunks(),
                headers=headers,
            )
        elif code == "client_disconnected":
            task = asyncio.ensure_future(
                c.post("/v1/audio/speech", json=payload, headers=headers)
            )
            await asyncio.sleep(0.15)
            task.cancel()
            with suppress(asyncio.CancelledError):
                await task
            resp = None
        elif code in _LOG_ONLY:
            with suppress(Exception):
                await c.post("/v1/audio/speech", json=payload, headers=headers)
            resp = None
        else:
            resp = await c.post("/v1/audio/speech", json=payload, headers=headers)

    if code in _LOG_ONLY:
        await _drain_done(app)
        # ``readouterr`` drains capsys — accumulate across the drain wait.
        lines: list[dict] = []

        def probe() -> bool:
            lines.extend(_logs(capsys))
            return any(
                l.get("error_code") == code and l.get("status") == _LOG_ONLY[code]
                for l in lines
            )

        await asyncio.get_running_loop().run_in_executor(
            None, lambda: wait_for(probe, 15.0, f"D7 line for {code}")
        )
        return

    assert resp is not None
    assert resp.status_code == CODES[code], resp.text
    assert resp.json()["error"]["code"] == code
    if CODES[code] == 429:
        assert resp.headers["retry-after"] == "5"
    # A pre-commit error after a valid upstream 200 (504) answers at once
    # but its D7 line is emitted by the detached drain — poll for it.
    # ``readouterr`` drains capsys, so accumulate inside the probe.
    lines: list[dict] = []

    def probe() -> bool:
        lines.extend(_logs(capsys))
        return any(
            l.get("error_code") == code and l.get("status") == CODES[code]
            for l in lines
        )

    await asyncio.get_running_loop().run_in_executor(
        None, lambda: wait_for(probe, 15.0, f"D7 line for {code}")
    )
