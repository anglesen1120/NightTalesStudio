"""Fake VieNeu for tests (D14): scripted status, headers, PCM chunks,
delays, mid-stream exceptions — driven through httpx.MockTransport."""

from __future__ import annotations

import asyncio
import json
import threading
from typing import Any

import httpx

PCM_HEADERS = {
    "content-type": "audio/pcm",
    "x-sample-rate": "48000",
    "x-request-id": "spk-fake01",
}


class _ChunkStream(httpx.AsyncByteStream):
    def __init__(self, chunks, delays, raise_after, raise_exc, gate, gate_at):
        self._chunks = list(chunks)
        self._delays = list(delays)
        self._raise_after = raise_after
        self._raise_exc = raise_exc
        self._gate = gate
        self._gate_at = gate_at
        self._i = 0

    async def __aiter__(self):
        for i, chunk in enumerate(self._chunks):
            if i < len(self._delays) and self._delays[i]:
                await asyncio.sleep(self._delays[i])
            if self._gate is not None and i == self._gate_at:
                while not self._gate.is_set():
                    await asyncio.sleep(0.01)
            if self._raise_after is not None and i >= self._raise_after:
                raise self._raise_exc
            self._i = i + 1
            yield chunk

    async def aclose(self) -> None:
        pass


class FakeVieneu:
    """Scripted VieNeu; records request bodies for shape assertions."""

    def __init__(
        self,
        status: int = 200,
        headers: dict[str, str] | None = None,
        chunks: list[bytes] | None = None,
        delays: list[float] | None = None,
        raise_after: int | None = None,
        raise_exc: BaseException | None = None,
        error_body: Any = None,
        stall: bool = False,
        gate: threading.Event | None = None,
        gate_at: int | None = None,
    ) -> None:
        self.status = status
        self.headers = dict(PCM_HEADERS if headers is None else headers)
        self.chunks = chunks or []
        self.delays = delays or []
        self.raise_after = raise_after
        self.raise_exc = raise_exc or httpx.ReadError("fake upstream broke")
        self.error_body = error_body
        self.stall = stall
        self.gate = gate
        self.gate_at = gate_at
        self.streams: list[_ChunkStream] = []
        self.requests: list[dict[str, Any]] = []

    @property
    def call_count(self) -> int:
        return len(self.requests)

    @property
    def consumed(self) -> int:
        """Chunks of the first stream actually yielded (drain observability)."""
        return self.streams[0]._i if self.streams else 0

    async def handler(self, request: httpx.Request) -> httpx.Response:
        body = request.content or b""
        try:
            parsed = json.loads(body)
        except json.JSONDecodeError:
            parsed = None
        self.requests.append(
            {
                "path": request.url.path,
                "authorization": request.headers.get("authorization"),
                "json": parsed,
            }
        )
        if self.status != 200:
            payload = json.dumps(
                self.error_body
                if self.error_body is not None
                else {"error": {"message": "fake upstream error"}}
            ).encode()
            return httpx.Response(self.status, content=payload, headers=self.headers)
        if self.stall:
            return httpx.Response(
                200, headers=self.headers, stream=_NeverStream()
            )
        stream = _ChunkStream(
            self.chunks,
            self.delays,
            self.raise_after,
            self.raise_exc,
            self.gate,
            self.gate_at if self.gate_at is not None else len(self.chunks) - 1,
        )
        self.streams.append(stream)
        return httpx.Response(200, headers=self.headers, stream=stream)

    def transport(self) -> httpx.MockTransport:
        return httpx.MockTransport(self.handler)


class _NeverStream(httpx.AsyncByteStream):
    async def __aiter__(self):
        await asyncio.sleep(3600)
        yield b""

    async def aclose(self) -> None:
        pass
