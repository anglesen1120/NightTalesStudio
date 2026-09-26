"""Wire-test harness (D14): real uvicorn servers on ephemeral loopback
ports, driven with ``requests`` — MPT's client library — so aborts, gaps
and disconnects are real TCP behaviour, not transport mocks.

Two servers per test:

- ``WireVieneu``: a scripted VieNeu (pure ASGI, no FastAPI). Status,
  headers, PCM chunks with per-chunk delays, mid-stream aborts (raising
  inside the ASGI callable = real socket abort).
- the proxy app itself, served by uvicorn.

All delays/timeouts scale by ``WIRE_SCALE`` (env, default 1.0) for slow
CI (task edge cases).
"""

from __future__ import annotations
import asyncio
import json
import os
import socket
import threading
import time
from dataclasses import dataclass, field

import uvicorn

WIRE_SCALE = float(os.environ.get("WIRE_SCALE", "1.0"))


@dataclass
class WireSpec:
    """One scripted VieNeu response for POST /v1/audio/speech."""

    status: int = 200
    headers: dict[str, str] | None = None
    chunks: list[bytes] = field(default_factory=list)
    delays: list[float] = field(default_factory=list)
    # Raise inside the ASGI app after N body sends → real socket abort.
    abort_after: int | None = None


_PCM_HEADERS = {"content-type": "audio/pcm", "x-sample-rate": "48000"}

# The app lifespan warm-up (04.1.3-T1) GETs /v1/voices before uvicorn binds
# the port; answering it with the seed voices keeps the refresh cheap and
# successful (a scripted PCM spec would hang startup on its delays).
_VOICES_BODY = {
    "object": "list",
    "data": [{"id": "Hải Đăng"}, {"id": "Ngọc Huyền"}],
}


class WireVieneu:
    """Scripted VieNeu ASGI app; speech specs are popped per request (the
    last spec repeats). Reads/records speech requests; the ``GET /v1/voices``
    warm-up is answered from ``_VOICES_BODY`` and recorded separately in
    ``voice_requests`` so it never consumes a speech spec or a delay."""

    def __init__(self, specs: list[WireSpec]) -> None:
        assert specs, "scripted fake needs at least one response spec"
        self.specs = list(specs)
        self.requests: list[dict] = []
        self.voice_requests: list[dict] = []
        self.sent: list[int] = []  # body chunks sent per request
        self.lock = threading.Lock()

    async def __call__(self, scope, receive, send) -> None:
        if scope["type"] == "lifespan":
            while True:
                msg = await receive()
                if msg["type"] == "lifespan.startup":
                    await send({"type": "lifespan.startup.complete"})
                elif msg["type"] == "lifespan.shutdown":
                    await send({"type": "lifespan.shutdown.complete"})
                    return
            return
        assert scope["type"] == "http"
        if scope["path"] == "/v1/voices":
            # Voice-cache warm-up: fixed cheap JSON, no spec pop, no delays.
            with self.lock:
                self.voice_requests.append(
                    {"path": scope["path"], "method": scope["method"]}
                )
            payload = json.dumps(_VOICES_BODY, ensure_ascii=False).encode("utf-8")
            await send(
                {
                    "type": "http.response.start",
                    "status": 200,
                    "headers": [(b"content-type", b"application/json")],
                }
            )
            await send({"type": "http.response.body", "body": payload})
            return
        body = bytearray()
        while True:
            msg = await receive()
            if msg["type"] == "http.request":
                body += msg.get("body", b"")
                if not msg.get("more_body"):
                    break
        try:
            parsed = json.loads(bytes(body))
        except (json.JSONDecodeError, UnicodeDecodeError):
            parsed = None
        with self.lock:
            self.requests.append(
                {
                    "path": scope["path"],
                    "method": scope["method"],
                    "json": parsed,
                    "authorization": next(
                        (
                            v.decode("latin-1")
                            for k, v in scope["headers"]
                            if k == b"authorization"
                        ),
                        None,
                    ),
                }
            )
            spec = self.specs.pop(0) if len(self.specs) > 1 else self.specs[0]

        if spec.status != 200:
            payload = json.dumps(
                {"error": {"message": "fake vieneu error"}}
            ).encode()
            await send(
                {
                    "type": "http.response.start",
                    "status": spec.status,
                    "headers": [
                        (b"content-type", b"application/json"),
                        (b"retry-after", str(spec.headers["retry-after"]).encode()),
                    ]
                    if spec.headers and "retry-after" in spec.headers
                    else [(b"content-type", b"application/json")],
                }
            )
            await send({"type": "http.response.body", "body": payload})
            return

        headers = {**_PCM_HEADERS, **(spec.headers or {})}
        await send(
            {
                "type": "http.response.start",
                "status": 200,
                "headers": [
                    (k.encode("latin-1"), v.encode("latin-1"))
                    for k, v in headers.items()
                ],
            }
        )
        sent = 0
        for i, chunk in enumerate(spec.chunks):
            if i < len(spec.delays) and spec.delays[i]:
                await asyncio.sleep(spec.delays[i] * WIRE_SCALE)
            if spec.abort_after is not None and sent >= spec.abort_after:
                raise RuntimeError("fake vieneu socket abort")
            await send(
                {
                    "type": "http.response.body",
                    "body": chunk,
                    "more_body": True,
                }
            )
            sent += 1
        with self.lock:
            self.sent.append(sent)
        await send({"type": "http.response.body", "body": b""})


class ByteCounter:
    """ASGI wrapper counting body bytes the inner app actually receives —
    i.e. the bytes it asked for via ``receive()`` (server-side read)."""

    def __init__(self, app) -> None:
        self.app = app
        self.bytes_read = 0
        self.lock = threading.Lock()

    async def __call__(self, scope, receive, send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        async def counting_receive():
            msg = await receive()
            if msg["type"] == "http.request":
                with self.lock:
                    self.bytes_read += len(msg.get("body", b""))
            return msg

        await self.app(scope, counting_receive, send)


def serve_app(app) -> tuple[uvicorn.Server, int, threading.Thread]:
    """uvicorn.Server on an ephemeral loopback port (per the task: the
    port comes from ``server.servers[0].sockets`` after startup)."""
    config = uvicorn.Config(
        app, host="127.0.0.1", port=0, log_level="warning", access_log=False
    )
    server = uvicorn.Server(config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()
    deadline = time.time() + 10 * WIRE_SCALE
    while not server.started:
        if time.time() > deadline:
            raise RuntimeError("uvicorn did not start")
        time.sleep(0.01)
    port = server.servers[0].sockets[0].getsockname()[1]
    return server, port, thread


def stop_server(server: uvicorn.Server, thread: threading.Thread) -> None:
    server.should_exit = True
    thread.join(timeout=10 * WIRE_SCALE)


def wait_log(capsys, predicate, timeout_s: float = 10.0) -> dict:
    """Poll capsys for a D7 line satisfying ``predicate`` (the log may be
    written from the uvicorn thread)."""
    deadline = time.time() + timeout_s * WIRE_SCALE
    seen = ""
    while time.time() < deadline:
        out = capsys.readouterr().out
        seen = out
        for line in out.splitlines():
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            if predicate(d):
                return d
        time.sleep(0.02)
    raise AssertionError(f"no matching log line; saw: {seen[-800:]}")


def wait_for(predicate, timeout_s: float = 10.0, what: str = "condition") -> None:
    deadline = time.time() + timeout_s * WIRE_SCALE
    while time.time() < deadline:
        if predicate():
            return
        time.sleep(0.02)
    raise AssertionError(f"timed out waiting for {what}")
