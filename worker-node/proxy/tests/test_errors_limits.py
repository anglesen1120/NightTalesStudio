"""D3 limit tests (04.1.2-T2): counting body limit (never over-read),
input code-point limit, JSON check, first-failure-wins order."""

from __future__ import annotations

import json
import socket

import httpx
import pytest

from nts_vieneu_proxy.app import MAX_BODY_BYTES, create_app, read_body
from nts_vieneu_proxy.errors import ProxyError

from .conftest import DUMMY_KEY, VOICE_VI, fast_settings, sine_pcm
from .fakes import FakeVieneu
from .wire_server import ByteCounter, serve_app, stop_server

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PCM = sine_pcm(0.2)


def _body(**over) -> dict:
    b = {"input": "Xin chào.", "voice": VOICE_VI}
    b.update(over)
    return b


async def _client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


def _request(chunks: list[bytes], headers: dict[str, str] | None = None):
    """Starlette Request fed by a scripted ASGI receive channel."""
    sent: list[int] = []

    async def receive():
        if chunks:
            msg = {
                "type": "http.request",
                "body": chunks.pop(0),
                "more_body": bool(chunks),
            }
            sent.append(len(msg["body"]))
            return msg
        return {"type": "http.disconnect"}

    scope = {
        "type": "http",
        "method": "POST",
        "path": "/v1/audio/speech",
        "headers": [
            (k.lower().encode(), v.encode()) for k, v in (headers or {}).items()
        ],
    }
    from starlette.requests import Request

    return Request(scope, receive), sent


async def _chunks(n: int = 5, size: int = 65_536):
    """Async body → httpx sends it chunked (no Content-Length), so the
    counting limit — not the declared length — decides."""
    for _ in range(n):
        yield b"z" * size

async def test_speech_body_over_262144_bytes_413_body_too_large_without_full_read(
    settings, catalog
):
    """Counting limit: the read stops as soon as the count exceeds the
    limit — the tail chunks are never consumed."""
    # 1) Declared Content-Length > limit → rejected before ANY read.
    # 2) Chunked (no Content-Length): stops right after crossing the limit.
    parts = [b"y" * 200_000] * 3  # 600 000 total; crossing at 400 000
    req1, sent1 = _request(
        list(parts), headers={"content-length": str(MAX_BODY_BYTES * 3)}
    )
    with pytest.raises(ProxyError) as ei1:
        await read_body(req1)
    assert ei1.value.code == "body_too_large"
    assert sent1 == []  # receive() was never called: no byte was read

    req2, sent2 = _request(list(parts))
    with pytest.raises(ProxyError) as ei2:
        await read_body(req2)
    assert ei2.value.code == "body_too_large"
    assert sent2 == [200_000, 200_000]  # the third chunk was never consumed

    # 3) End-to-end through the app: chunked, no Content-Length.
    fake = FakeVieneu(chunks=[PCM])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post(
            "/v1/audio/speech",
            content=_chunks(),
            headers=AUTH,
        )
    assert resp.status_code == 413
    assert resp.json()["error"]["code"] == "body_too_large"
    assert fake.call_count == 0


async def test_speech_input_20000_code_points_accepted(settings, catalog):
    fake = FakeVieneu(chunks=[PCM])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post(
            "/v1/audio/speech", json=_body(input="a" * 20_000), headers=AUTH
        )
    assert resp.status_code == 200
    assert fake.call_count == 1


async def test_speech_input_20001_code_points_413_input_too_long(
    settings, catalog
):
    fake = FakeVieneu(chunks=[PCM])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post(
            "/v1/audio/speech", json=_body(input="a" * 20_001), headers=AUTH
        )
    assert resp.status_code == 413
    assert resp.json()["error"]["code"] == "input_too_long"
    assert fake.call_count == 0


async def test_speech_non_json_body_400_invalid_request(settings, catalog):
    fake = FakeVieneu(chunks=[PCM])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post(
            "/v1/audio/speech",
            content=b"<xml>nope</xml>",
            headers={**AUTH, "content-type": "application/json"},
        )
    assert resp.status_code == 400
    assert resp.json()["error"]["code"] == "invalid_request"
    assert fake.call_count == 0


async def test_speech_d3_check_order_first_failure_wins(settings, catalog):
    """D3 order: body size → JSON → input → format → voice; the first
    failure answers."""
    fake = FakeVieneu(chunks=[PCM])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        # bad JSON AND bad voice → JSON wins
        r = await c.post(
            "/v1/audio/speech", content=b"{", headers=AUTH
        )
        assert r.json()["error"]["code"] == "invalid_request"
        # empty input AND bad format AND bad voice → input wins
        r = await c.post(
            "/v1/audio/speech",
            json=_body(input="", response_format="wav", voice="Nope"),
            headers=AUTH,
        )
        assert r.json()["error"]["code"] == "invalid_request"
        # long input AND bad voice → input_too_long wins
        r = await c.post(
            "/v1/audio/speech",
            json=_body(input="a" * 20_001, voice="Nope"),
            headers=AUTH,
        )
        assert r.json()["error"]["code"] == "input_too_long"
        # bad format AND bad voice → format wins
        r = await c.post(
            "/v1/audio/speech",
            json=_body(response_format="wav", voice="Nope"),
            headers=AUTH,
        )
        assert r.json()["error"]["code"] == "unsupported_format"
        # only bad voice → voice_not_in_catalog
        r = await c.post(
            "/v1/audio/speech", json=_body(voice="Nope"), headers=AUTH
        )
        assert r.json()["error"]["code"] == "voice_not_in_catalog"
        # oversized body wins over everything (413, never parsed)
        r = await c.post(
            "/v1/audio/speech",
            content=_chunks(),
            headers=AUTH,
        )
        assert r.json()["error"]["code"] == "body_too_large"
    assert fake.call_count == 0


def test_speech_declared_content_length_over_limit_413_before_reading_body(
    settings, catalog
):
    """Real socket: a declared Content-Length over MAX_BODY_BYTES is refused
    before any body byte is read — the client sends only the headers (never
    the announced body) and still gets 413, with a server-side read count of
    0 (no deadlock waiting for a body that never arrives)."""
    counter = ByteCounter(
        create_app(
            fast_settings(settings, vieneu_base_url="http://127.0.0.1:1"),
            catalog,
        )
    )
    server, port, thread = serve_app(counter)
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5) as sock:
            sock.settimeout(5)
            sock.sendall(
                (
                    "POST /v1/audio/speech HTTP/1.1\r\n"
                    f"Host: 127.0.0.1:{port}\r\n"
                    "Content-Type: application/json\r\n"
                    f"Authorization: Bearer {DUMMY_KEY}\r\n"
                    f"Content-Length: {MAX_BODY_BYTES + 1}\r\n"
                    "Connection: close\r\n\r\n"
                ).encode()
            )
            data = b""
            try:
                while b"\r\n\r\n" not in data:
                    data += sock.recv(65536)
            except socket.timeout:
                pass
            assert b" 413 " in data.split(b"\r\n", 1)[0], data[:200]
            assert counter.bytes_read == 0
    finally:
        stop_server(server, thread)
