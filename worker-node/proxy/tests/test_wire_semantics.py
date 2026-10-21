"""Wire tests (04.1.2-T2, D14): the proxy and a scripted fake VieNeu each
on a real uvicorn socket, driven with ``requests`` — MPT's client library
(M3) — so abort/disconnect behaviour is real TCP semantics (X2)."""

from __future__ import annotations

import socket

import pytest
import requests

from nts_vieneu_proxy.app import create_app

from .conftest import DUMMY_KEY, fast_settings, sine_pcm
from .conftest import VOICE_VI
from .wire_server import (
    WIRE_SCALE,
    ByteCounter,
    WireSpec,
    WireVieneu,
    serve_app,
    stop_server,
    wait_log,
)

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PCM = sine_pcm(0.25)

# Timeout ceiling for individual socket reads; WIRE_SCALE widens it on
# slow CI (task edge cases) together with every fake delay.
R_TIMEOUT = 2.0 * WIRE_SCALE


def _body(**over) -> dict:
    b = {"input": "Xin chào.", "voice": VOICE_VI}
    b.update(over)
    return b


@pytest.fixture
def pair(settings, catalog):
    """(fake_vieneu, proxy_port, cleanup) — fake first so the proxy's
    ``vieneu_base_url`` can point at it."""
    servers = []

    def make(specs, app=None):
        fake = WireVieneu(specs)
        fserver, fport, fthread = serve_app(fake)
        servers.append((fserver, fthread))
        s = fast_settings(
            settings, vieneu_base_url=f"http://127.0.0.1:{fport}"
        )
        papp = app if app is not None else create_app(s, catalog)
        pserver, pport, pthread = serve_app(papp)
        servers.append((pserver, pthread))
        return fake, pport, papp

    yield make
    for server, thread in servers:
        stop_server(server, thread)


def test_wire_abort_after_commit_raises_chunked_encoding_error(
    pair, capsys
):
    """X2/D4: an upstream break after commit aborts the socket without the
    final chunk — MPT's client sees ChunkedEncodingError, never a clean
    200-with-truncated-body."""
    fake, port, app = pair(
        [WireSpec(chunks=[PCM] * 12, delays=[0.05] * 12, abort_after=4)]
    )
    resp = requests.post(
        f"http://127.0.0.1:{port}/v1/audio/speech",
        json=_body(),
        headers=AUTH,
        stream=True,
        timeout=R_TIMEOUT,
    )
    assert resp.status_code == 200
    received = b""
    with pytest.raises(requests.exceptions.ChunkedEncodingError):
        for chunk in resp.iter_content(4096):
            received += chunk
    assert received  # committed bytes arrived before the abort
    line = wait_log(capsys, lambda l: l.get("status") == 502)
    assert line["error_code"] == "upstream_stream_broken"
    assert line["http_status"] == 200  # status line was already sent


def test_wire_slow_trickle_under_short_timeout_succeeds(pair):
    """requests' timeout is per socket read, not total: a 5 s trickle with
    gaps under the timeout completes (MPT idle-timeout semantics, R1)."""
    fake, port, _ = pair(
        [WireSpec(chunks=[PCM] * 10, delays=[0.5] * 10)]
    )  # ≈5 s total, scaled
    resp = requests.post(
        f"http://127.0.0.1:{port}/v1/audio/speech",
        json=_body(),
        headers=AUTH,
        timeout=R_TIMEOUT,  # per-read; total ≫ timeout
    )
    assert resp.status_code == 200
    assert len(resp.content) > 0
    # The lifespan voice-cache refresh also hits the fake (GET /v1/voices).
    assert any(r["path"] == "/v1/audio/speech" for r in fake.requests)


def test_wire_401_before_body_read(settings, catalog, capsys):
    """Auth runs before body parsing: a 401 is answered without consuming
    a single body byte (server-side receive count stays 0)."""
    fake = WireVieneu([WireSpec(chunks=[PCM])])
    fserver, fport, fthread = serve_app(fake)
    s = fast_settings(
        settings, vieneu_base_url=f"http://127.0.0.1:{fport}"
    )
    counter = ByteCounter(create_app(s, catalog))
    server, port, thread = serve_app(counter)
    try:
        sock = socket.create_connection(("127.0.0.1", port), timeout=R_TIMEOUT)
        sock.settimeout(R_TIMEOUT)
        # Headers declare a 1 MiB body we then trickle — the 401 must come
        # back regardless, and the server must never consume it.
        head = (
            "POST /v1/audio/speech HTTP/1.1\r\n"
            f"Host: 127.0.0.1:{port}\r\n"
            "Content-Type: application/json\r\n"
            "Authorization: Bearer wrong-key\r\n"
            "Content-Length: 1048576\r\n"
            "Connection: close\r\n\r\n"
        ).encode()
        sock.sendall(head)
        sock.sendall(b"{" + b" " * 4095)  # a few KiB of the announced body
        data = b""
        try:
            while b"\r\n\r\n" not in data:
                data += sock.recv(65536)
        except socket.timeout:
            pass
        assert b" 401 " in data.split(b"\r\n", 1)[0], data[:120]
        try:
            sock.recv(65536)
        except socket.timeout:
            pass
        assert counter.bytes_read == 0
        sock.close()
        assert not [
            r for r in fake.requests if r["path"] == "/v1/audio/speech"
        ]  # the request never reached upstream
    finally:
        stop_server(server, thread)
        stop_server(fserver, fthread)
    line = wait_log(capsys, lambda l: l.get("error_code") == "proxy_unauthorized")
    assert line["status"] == 401


def test_wire_empty_audio_is_502_not_200(pair, capsys):
    """F11: an upstream 200 that ends with zero PCM is a 502, not a 200
    with an empty/truncated MP3 body."""
    fake, port, _ = pair([WireSpec(chunks=[])])
    resp = requests.post(
        f"http://127.0.0.1:{port}/v1/audio/speech",
        json=_body(),
        headers=AUTH,
        timeout=R_TIMEOUT,
    )
    assert resp.status_code == 502
    assert resp.json()["error"]["code"] == "upstream_empty_audio"


def test_wire_client_timeout_logs_499_client_disconnected(pair, capsys):
    """Client gives up mid-stream (requests read timeout) → the proxy's
    D7 line is 499 client_disconnected once the next send fails."""
    fake, port, _ = pair(
        [
            WireSpec(
                chunks=[PCM] * 12,
                delays=[0, 0, 0, 0, 0, 2.0, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05],
            )
        ]
    )
    resp = requests.post(
        f"http://127.0.0.1:{port}/v1/audio/speech",
        json=_body(),
        headers=AUTH,
        stream=True,
        # 1 s per-read timeout covers the first-MP3 wait; the scripted 2 s
        # upstream gap then exceeds it and the client gives up mid-stream.
        timeout=(R_TIMEOUT, 1.0 * WIRE_SCALE),
    )
    assert resp.status_code == 200
    it = resp.iter_content(4096)
    next(it)  # first MP3 chunk arrives before the upstream pause
    # urllib3's ReadTimeoutError inside iter_content surfaces as
    # requests' ConnectionError (initial-request timeouts are ReadTimeout).
    with pytest.raises(
        (requests.exceptions.ReadTimeout, requests.exceptions.ConnectionError)
    ):
        for _ in it:
            pass
    resp.close()  # client hung up — next proxy send fails
    line = wait_log(capsys, lambda l: l.get("status") == 499)
