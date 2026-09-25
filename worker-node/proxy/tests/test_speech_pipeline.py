"""app.py — /v1/audio/speech pipeline component + wire tests (D14)."""

from __future__ import annotations

import asyncio
import json
import os
import socket
import stat
import subprocess
import threading
import time
import unicodedata

import httpx
import pytest
import requests
import uvicorn

from nts_vieneu_proxy.app import _BoundedStreamResponse, create_app
from nts_vieneu_proxy.errors import AbortStream
from nts_vieneu_proxy.upstream import VieneuClient
from starlette.requests import ClientDisconnect
from types import SimpleNamespace

from .conftest import DUMMY_KEY, VOICE_EN, VOICE_VI, fast_settings, sine_pcm
from .fakes import FakeVieneu

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PCM_500MS = sine_pcm(0.5)


def _body(**over) -> dict:
    b = {"input": "Xin chào.", "voice": VOICE_VI}
    b.update(over)
    return b


def _app(settings, catalog, fake: FakeVieneu):
    return create_app(settings, catalog, vieneu_transport=fake.transport())


async def _client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


def _logs(capsys) -> list[dict]:
    out = capsys.readouterr().out
    return [
        json.loads(line)
        for line in out.splitlines()
        if line.startswith('{"ts"')
    ]


async def _await_log_line(capsys, needle: str, timeout_s: float = 5.0) -> dict:
    """Poll capsys for a log line without blocking the event loop (P1:
    detached close emits AFTER the error response is returned)."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        for line in _logs(capsys):
            if needle in json.dumps(line):
                return line
        await asyncio.sleep(0.02)
    raise AssertionError(f"no log line containing {needle!r} within {timeout_s}s")


# --- happy path / upstream body ---------------------------------------------


@pytest.mark.asyncio
async def test_speech_happy_path_streams_mp3_frame_sync(settings, catalog, capsys):
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    assert resp.headers["content-type"] == "audio/mpeg"
    assert resp.headers["cache-control"] == "no-store"
    assert resp.headers["x-request-id"].startswith("px-")
    mp3 = resp.content
    assert len(mp3) > 0
    assert mp3[0] == 0xFF and (mp3[1] & 0xE0) == 0xE0  # MP3 frame sync, no ID3

    lines = _logs(capsys)
    assert len(lines) == 1
    line = lines[0]
    assert line["status"] == 200 and line["http_status"] == 200
    assert line["audio_seconds"] == pytest.approx(0.5, abs=0.001)
    assert line["bytes_out"] == len(mp3)
    assert line["upstream_rid"] == "spk-fake01"
    assert line["ttfb_ms"] is not None and line["wall_ms"] is not None
    assert "Xin chào" not in json.dumps(lines[0], ensure_ascii=False)


@pytest.mark.asyncio
async def test_speech_upstream_body_is_fixed_d3_shape(settings, catalog):
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post(
            "/v1/audio/speech",
            json=_body(model="mpt-model", speed=1.5, instructions=" spooky ", extra=1),
            headers=AUTH,
        )
    assert resp.status_code == 200
    assert fake.call_count == 1
    sent = fake.requests[0]["json"]
    assert sent == {
        "model": "vieneu-v3-turbo",
        "input": "Xin chào.",
        "voice": VOICE_VI,
        "response_format": "pcm",
        "stream_format": "audio",
        "sample_rate": 48000,
    }
    assert fake.requests[0]["authorization"] == f"Bearer {'v' * 40}"


@pytest.mark.asyncio
async def test_speech_voice_nfc_normalised_before_allow_list(settings, catalog, capsys):
    nfd = unicodedata.normalize("NFD", VOICE_VI)
    assert nfd != VOICE_VI
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(voice=nfd), headers=AUTH)
    assert resp.status_code == 200
    assert fake.requests[0]["json"]["voice"] == VOICE_VI  # NFC goes upstream
    line = _logs(capsys)[0]
    assert line["voice"] == VOICE_VI


@pytest.mark.asyncio
async def test_speech_inactive_catalog_voice_accepted(settings, tmp_path, capsys):
    from .conftest import catalog_doc, voice_entry, write_catalog
    from nts_vieneu_proxy.catalog import load_catalog

    doc = catalog_doc([voice_entry(VOICE_VI, "hai-dang", active=False)])
    cat = load_catalog(write_catalog(tmp_path, doc))
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, cat, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    assert fake.call_count == 1


# --- D3 rejections ------------------------------------------------------------


@pytest.mark.asyncio
async def test_speech_unknown_voice_400_no_upstream_call(settings, catalog, capsys):
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post(
            "/v1/audio/speech", json=_body(voice="Ghost"), headers=AUTH
        )
    assert resp.status_code == 400
    body = resp.json()
    assert body["error"]["code"] == "voice_not_in_catalog"
    assert body["error"]["type"] == "proxy_error"
    assert fake.call_count == 0
    line = _logs(capsys)[0]
    assert line["status"] == 400 and line["error_code"] == "voice_not_in_catalog"


@pytest.mark.asyncio
async def test_speech_unsupported_format_400(settings, catalog):
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post(
            "/v1/audio/speech", json=_body(response_format="wav"), headers=AUTH
        )
    assert resp.status_code == 400
    assert resp.json()["error"]["code"] == "unsupported_format"
    assert fake.call_count == 0


@pytest.mark.asyncio
async def test_speech_blank_input_400(settings, catalog):
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        for bad in ("   ", "", "\n\t "):
            resp = await client.post(
                "/v1/audio/speech", json=_body(input=bad), headers=AUTH
            )
            assert resp.status_code == 400
            assert resp.json()["error"]["code"] == "invalid_request"
    assert fake.call_count == 0


@pytest.mark.asyncio
async def test_speech_unauthorized_401(settings, catalog, capsys):
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post(
            "/v1/audio/speech",
            json=_body(),
            headers={"Authorization": "Bearer wrong-but-long-enough-key-value-xx"},
        )
        resp2 = await client.post("/v1/audio/speech", json=_body())
    assert resp.status_code == 401 and resp2.status_code == 401
    assert resp.json()["error"]["code"] == "proxy_unauthorized"
    assert fake.call_count == 0
    assert len(_logs(capsys)) == 2  # even 401s emit the D7 line


# --- upstream failures before commit -----------------------------------------


@pytest.mark.asyncio
async def test_speech_empty_upstream_audio_502_not_200(settings, catalog, capsys):
    """F11: upstream 200 with zero PCM must not become a 200 to MPT."""
    fake = FakeVieneu(chunks=[])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_empty_audio"
    line = _logs(capsys)[0]
    assert line["status"] == 502 and line["http_status"] == 502


@pytest.mark.asyncio
async def test_speech_bad_sample_rate_header_502(settings, catalog, capsys):
    fake = FakeVieneu(
        chunks=[PCM_500MS],
        headers={
            "content-type": "audio/pcm",
            "x-sample-rate": "44100",
            "x-request-id": "spk-bad",
        },
    )
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_bad_format"
    assert _logs(capsys)[0]["status"] == 502


@pytest.mark.asyncio
async def test_speech_first_audio_timeout_504(settings, catalog, capsys):
    """P1/T15 addendum: a pre-commit error after a valid 200 is answered at
    once; the upstream drain runs detached, holding the slot until the
    (zero-byte) gap timeout measured from DRAIN START, not t0."""
    s = fast_settings(
        settings, first_audio_deadline_s=0.3, upstream_read_gap_s=1
    )
    fake = FakeVieneu(stall=True)  # 200 PCM headers, then no bytes at all
    app = _app(s, catalog, fake)
    t0 = time.monotonic()
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    elapsed = time.monotonic() - t0
    assert resp.status_code == 504
    assert resp.json()["error"]["code"] == "upstream_first_audio_timeout"
    assert elapsed < 2  # answered at the deadline, not after the drain
    # Slot still held by the detached drain (pipeline stays in the active set).
    assert len(app.state.active_pipelines) == 1
    # Zero upstream bytes → gap clock starts at drain start (~1 s), not t0.
    for _ in range(150):
        if not app.state.active_pipelines:
            break
        await asyncio.sleep(0.02)
    assert app.state.active_pipelines == set()
    line = await _await_log_line(capsys, "upstream_first_audio_timeout")
    assert line["status"] == 504


@pytest.mark.asyncio
async def test_speech_upstream_rejected_relays_message(settings, catalog):
    fake = FakeVieneu(status=400, error_body={"error": {"message": "bad voice upstream"}})
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 400
    assert resp.json()["error"]["code"] == "upstream_rejected"
    assert "bad voice upstream" in resp.json()["error"]["message"]


@pytest.mark.asyncio
async def test_speech_upstream_5xx_is_502(settings, catalog):
    fake = FakeVieneu(status=503)
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_unavailable"


@pytest.mark.asyncio
async def test_speech_ffmpeg_missing_500(settings, catalog):
    s = fast_settings(settings, ffmpeg_path=None)
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(s, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 500
    assert resp.json()["error"]["code"] == "transcode_failed"


# --- post-commit aborts ------------------------------------------------------


@pytest.mark.asyncio
async def test_speech_upstream_break_after_commit_aborts_and_logs_502(
    settings, catalog, capsys
):
    # raise_after=1 errors only while feeding the SECOND chunk; the first
    # chunk's MP3 output must commit the 200 before the break lands.
    fake = FakeVieneu(chunks=[PCM_500MS, PCM_500MS], delays=[0, 0.3], raise_after=1)
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        with pytest.raises(Exception):  # ASGITransport re-raises the abort
            await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    line = _logs(capsys)[0]
    assert line["status"] == 502
    assert line["error_code"] == "upstream_stream_broken"
    assert line["http_status"] == 200  # status line was already sent


@pytest.mark.asyncio
async def test_speech_ffmpeg_nonzero_after_commit_aborts_transcode_failed(
    settings, catalog, capsys, tmp_path
):
    fake_ffmpeg = tmp_path / "ffmpeg-fake"
    fake_ffmpeg.write_text(
        "#!/bin/sh\nprintf 'FAKEMP3DATA'\nsleep 0.3\nexit 1\n"
    )
    fake_ffmpeg.chmod(fake_ffmpeg.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    s = fast_settings(settings, ffmpeg_path=str(fake_ffmpeg))
    fake = FakeVieneu(chunks=[PCM_500MS], delays=[0.05])
    app = _app(s, catalog, fake)
    async with await _client(app) as client:
        with pytest.raises(Exception):
            await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    line = _logs(capsys)[0]
    assert line["status"] == 502
    assert line["error_code"] == "transcode_failed"
    assert line["http_status"] == 200


@pytest.mark.asyncio
async def test_speech_odd_pcm_bytes_warns_pcm_odd_tail(settings, catalog, capsys):
    odd = PCM_500MS[:-1]  # odd byte count
    fake = FakeVieneu(chunks=[odd])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    line = _logs(capsys)[0]
    assert "pcm_odd_tail" in (line.get("warnings") or [])
    assert line["audio_seconds"] == pytest.approx(0.5, abs=0.01)


# --- wire test: real uvicorn + requests ----------------------------------------


def _wait_log_499(capsys, timeout_s: float = 8.0) -> dict:
    deadline = time.time() + timeout_s
    seen_out = ""
    while time.time() < deadline:
        seen_out = capsys.readouterr().out
        for line in seen_out.splitlines():
            if line.startswith('{"ts"') and '"status":499' in line:
                return json.loads(line)
        time.sleep(0.05)
    raise AssertionError(f"no 499 log line within {timeout_s}s; got: {seen_out[-500:]}")


def _serve(app):
    """Run the app on a real uvicorn socket; returns (server, port, thread)."""
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    config = uvicorn.Config(
        app, host="127.0.0.1", port=port, log_level="error", access_log=False
    )
    server = uvicorn.Server(config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()
    for _ in range(200):
        if server.started:
            break
        time.sleep(0.02)
    assert server.started
    return server, port, thread


def test_speech_client_disconnect_kills_ffmpeg_logs_499(settings, catalog, capsys):
    chunks = [sine_pcm(0.1) for _ in range(50)]
    fake = FakeVieneu(chunks=chunks, delays=[0.05] * len(chunks))
    app = _app(settings, catalog, fake)
    server, port, thread = _serve(app)

    try:
        resp = requests.post(
            f"http://127.0.0.1:{port}/v1/audio/speech",
            json=_body(),
            headers=AUTH,
            stream=True,
            timeout=15,
        )
        assert resp.status_code == 200
        next(resp.iter_content(64))  # one chunk then hang up
        resp.close()

        line = _wait_log_499(capsys)
        assert line["error_code"] == "client_disconnected"
        assert line["http_status"] == 200

        # ffmpeg must be reaped: no ffmpeg child of this pytest process.
        children = subprocess.run(
            ["pgrep", "-P", str(os.getpid())], capture_output=True, text=True
        ).stdout.split()
        ffmpeg_children = [
            pid
            for pid in children
            if "ffmpeg"
            in subprocess.run(
                ["ps", "-p", pid, "-o", "comm="], capture_output=True, text=True
            ).stdout
        ]
        assert ffmpeg_children == []
        assert app.state.active_pipelines == set()
    finally:
        server.should_exit = True
        thread.join(timeout=10)


# --- T15: drain upstream on client disconnect ----------------------------------


def test_disconnect_drains_upstream_holds_slot_until_drain_end(
    settings, catalog, capsys
):
    """T15: after a 499 the proxy reads the upstream stream to its end
    (VieNeu finishes synthesis) and the proxy slot is held until then."""
    gate = threading.Event()
    chunks = [sine_pcm(0.5) for _ in range(4)]
    fake = FakeVieneu(chunks=chunks, gate=gate)  # blocks before the last chunk
    s = fast_settings(settings, proxy_max_concurrent=1)
    app = _app(s, catalog, fake)

    server, port, thread = _serve(app)
    url = f"http://127.0.0.1:{port}/v1/audio/speech"

    try:
        resp1 = requests.post(url, json=_body(), headers=AUTH, stream=True, timeout=15)
        assert resp1.status_code == 200
        next(resp1.iter_content(64))
        resp1.close()  # client disconnect → drain must take over

        second = []

        def call_second():
            r2 = requests.post(url, json=_body(), headers=AUTH, timeout=30)
            second.append(r2.status_code)

        t2 = threading.Thread(target=call_second, daemon=True)
        t2.start()
        # Slot is held for the drain: the second request must not reach
        # VieNeu while the drain is parked on the gate.
        for _ in range(100):
            assert fake.call_count == 1, "slot released before the drain ended"
            time.sleep(0.01)
        assert fake.consumed < len(chunks)

        gate.set()
        t2.join(timeout=20)
        assert second == [200]
        assert fake.consumed == len(chunks)  # all PCM drained, not dropped

        line = _wait_log_499(capsys)
        assert line["error_code"] == "client_disconnected"
        assert line["http_status"] == 200
        assert app.state.active_pipelines == set()
    finally:
        gate.set()
        server.should_exit = True
        thread.join(timeout=10)


def test_disconnect_drain_gap_timeout_closes_upstream(
    settings, catalog, capsys
):
    """T15 bound: an upstream that stalls mid-drain is abandoned after
    UPSTREAM_READ_GAP_S without a byte — the pipeline still closes."""
    chunks = [sine_pcm(0.5), sine_pcm(0.5)]
    fake = FakeVieneu(chunks=chunks, delays=[0.0, 3600])  # stall before chunk 2
    s = fast_settings(settings, upstream_read_gap_s=1)
    app = _app(s, catalog, fake)
    server, port, thread = _serve(app)
    url = f"http://127.0.0.1:{port}/v1/audio/speech"

    try:
        resp = requests.post(url, json=_body(), headers=AUTH, stream=True, timeout=15)
        assert resp.status_code == 200
        next(resp.iter_content(64))
        resp.close()

        t0 = time.time()
        line = _wait_log_499(capsys, timeout_s=20)
        elapsed = time.time() - t0
        # Gap timeout, not the 3600s stall and not REQUEST_MAX_S.
        assert elapsed < 20
        assert line["error_code"] == "client_disconnected"
        assert fake.consumed == 1  # stalled chunk never consumed
        assert app.state.active_pipelines == set()
    finally:
        server.should_exit = True
        thread.join(timeout=10)


# --- P1 review regressions ---------------------------------------------------


@pytest.mark.asyncio
async def test_speech_feed_error_drains_upstream_to_eof(settings, catalog, capsys, tmp_path):
    """P1/T15: when ffmpeg's stdin dies mid-stream the pump keeps reading
    (httpx streams cannot be re-iterated), so the upstream body is consumed
    to EOF and the slot is held until then; the error is still reported."""
    ffmpeg = tmp_path / "ffmpeg-dies"
    ffmpeg.write_text("#!/bin/sh\nhead -c 1 >/dev/null\nexit 0\n")
    ffmpeg.chmod(ffmpeg.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    s = fast_settings(settings, ffmpeg_path=str(ffmpeg), upstream_read_gap_s=3)
    fake = FakeVieneu(
        chunks=[PCM_500MS] * 4, delays=[0.0, 0.3, 0.1, 0.1]
    )
    app = _app(s, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 500
    assert resp.json()["error"]["code"] == "transcode_failed"
    line = await _await_log_line(capsys, "transcode_failed")
    assert line["status"] == 500
    assert line["audio_seconds"] == pytest.approx(2.0, abs=0.01)  # all 4 chunks
    assert fake.consumed == 4  # upstream read to EOF by the pump
    assert app.state.active_pipelines == set()  # slot released at drain end


@pytest.mark.asyncio
async def test_send_stall_is_request_max_exceeded_502(settings):
    """P1: a client that stops reading cannot hold the slot — each ASGI send
    is bounded by UPSTREAM_READ_GAP_S and REQUEST_MAX_S (both log as 502
    request_max_exceeded, per the P1 ruling)."""
    s = fast_settings(settings, upstream_read_gap_s=0.2, request_max_s=10)

    async def gen():
        yield b"data"

    pipe = SimpleNamespace(
        t0=asyncio.get_running_loop().time(),
        settings=s,
        outcome=None,
        close=lambda: asyncio.sleep(0),
        body=lambda first: gen(),
    )
    resp = _BoundedStreamResponse(pipe, b"data")

    async def stalled_send(message):
        await asyncio.sleep(3600)  # client never drains the socket

    with pytest.raises(AbortStream) as ei:
        await resp.stream_response(stalled_send)
    assert ei.value.code == "request_max_exceeded"
    assert pipe.outcome == (502, "request_max_exceeded")


@pytest.mark.asyncio
async def test_send_past_request_max_is_request_max_exceeded_502(settings):
    """P1: the REQUEST_MAX_S wall is checked around every client send, not
    only between body chunks."""
    s = fast_settings(settings, upstream_read_gap_s=5, request_max_s=0.05)

    async def gen():
        yield b"data"

    pipe = SimpleNamespace(
        t0=asyncio.get_running_loop().time() - 1.0,  # already past the wall
        settings=s,
        outcome=None,
        close=lambda: asyncio.sleep(0),
        body=lambda first: gen(),
    )
    resp = _BoundedStreamResponse(pipe, b"data")

    async def send(message):
        return None

    with pytest.raises(AbortStream) as ei:
        await resp.stream_response(send)
    assert ei.value.code == "request_max_exceeded"
    assert pipe.outcome == (502, "request_max_exceeded")


@pytest.mark.asyncio
async def test_speech_non_ascii_auth_header_401(settings, catalog):
    """P1: a Bearer header with non-ASCII bytes is a 401, never a 500."""
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post(
            "/v1/audio/speech",
            json=_body(),
            headers=[(b"authorization", b"Bearer " + b"\xff" * 40)],
        )
    assert resp.status_code == 401
    assert resp.json()["error"]["code"] == "proxy_unauthorized"


@pytest.mark.asyncio
async def test_speech_missing_or_nonstring_voice_400(settings, catalog, capsys):
    """P1/D3 step 5: missing or non-string voice is a catalog miss (400
    voice_not_in_catalog), not a generic invalid_request."""
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        no_voice = _body()
        del no_voice["voice"]
        r1 = await client.post("/v1/audio/speech", json=no_voice, headers=AUTH)
        r2 = await client.post(
            "/v1/audio/speech", json=_body(voice=42), headers=AUTH
        )
        r3 = await client.post(
            "/v1/audio/speech", json=_body(voice=None), headers=AUTH
        )
    for r in (r1, r2, r3):
        assert r.status_code == 400
        assert r.json()["error"]["code"] == "voice_not_in_catalog"
    assert fake.call_count == 0


@pytest.mark.asyncio
async def test_speech_ignored_fields_log_is_bounded(settings, catalog, capsys):
    """D7 bound (P1): ≤ 16 names, each ≤ 64 chars, then '+<n> more'."""
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    over = {f"x{i}": 1 for i in range(20)}
    over["long"] = None
    over["x" * 100] = None
    async with await _client(app) as client:
        resp = await client.post(
            "/v1/audio/speech", json=_body(**over), headers=AUTH
        )
    assert resp.status_code == 200
    line = await _await_log_line(capsys, '"ignored_fields"')
    fields = line["ignored_fields"]
    assert len(fields) == 17  # 16 names + "+<n> more"
    assert fields[-1] == "+6 more"
    assert all(len(f) <= 64 for f in fields[:-1])


@pytest.mark.asyncio
async def test_vieneu_client_ignores_proxy_env(settings, monkeypatch):
    """P1: trust_env=False — HTTP(S)_PROXY must never redirect VieNeu calls."""
    monkeypatch.setenv("HTTP_PROXY", "http://127.0.0.1:9")
    monkeypatch.setenv("HTTPS_PROXY", "http://127.0.0.1:9")
    c = VieneuClient(settings)  # no transport: proxy mounts would show here
    assert not c._client._mounts
    await c.aclose()


@pytest.mark.asyncio
async def test_precommit_client_disconnect_logs_499_not_500(settings, catalog, capsys):
    """P1: a client disconnect before commit (here: mid body-read) logs 499
    client_disconnected, not 500 internal_error."""
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)

    async def receive():
        return {"type": "http.disconnect"}

    sent = []

    async def send(message):
        sent.append(message)

    scope = {
        "type": "http",
        "asgi": {"version": "3.0"},
        "http_version": "1.1",
        "method": "POST",
        "scheme": "http",
        "path": "/v1/audio/speech",
        "raw_path": b"/v1/audio/speech",
        "query_string": b"",
        "root_path": "",
        "headers": [(b"authorization", f"Bearer {DUMMY_KEY}".encode())],
        "client": ("127.0.0.1", 12345),
        "server": ("127.0.0.1", 80),
    }
    with pytest.raises(ClientDisconnect):
        await app(scope, receive, send)
    assert fake.call_count == 0  # upstream never opened
    line = _logs(capsys)[0]
    assert line["status"] == 499
    assert line["error_code"] == "client_disconnected"
    assert line["http_status"] is None  # nothing was sent


@pytest.mark.asyncio
async def test_teardown_survives_emit_failure(
    settings, catalog, capsys, monkeypatch
):
    """P1: a reqlog emit() failure must never skip cleanup — the pipeline
    leaves the active set and releases the slot."""
    import nts_vieneu_proxy.app as app_mod

    monkeypatch.setattr(app_mod, "emit", lambda entry: (_ for _ in ()).throw(RuntimeError("log sink dead")))
    fake = FakeVieneu(chunks=[PCM_500MS])
    app = _app(settings, catalog, fake)
    async with await _client(app) as client:
        resp = await client.post("/v1/audio/speech", json=_body(), headers=AUTH)
    assert resp.status_code == 200
    assert app.state.active_pipelines == set()
    assert not app.state.speech_slots.locked()
