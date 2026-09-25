"""VieNeu upstream client (README D2).

Exactly three upstream methods, each with a fixed path — the proxy has no code
path to any other VieNeu route (D2):

- ``speech_stream`` → ``POST /v1/audio/speech`` (streaming, this task)
- ``voices`` → ``GET /v1/voices`` (used by 04.1.3-T1's voice cache)
- ``health`` → ``GET /health`` (used by 04.1.4-T1)
"""

from __future__ import annotations

import json
from contextlib import asynccontextmanager
from typing import AsyncIterator

import httpx

from .config import Settings

_ERR_BODY_LIMIT = 4096


class UpstreamSpeech:
    """Handle on an in-flight VieNeu speech response (yielded by speech_stream)."""

    def __init__(self, response: httpx.Response) -> None:
        self._response = response
        self.status = response.status_code
        self.headers = response.headers
        self.request_id = response.headers.get("x-request-id")

    async def aiter_pcm(self) -> AsyncIterator[bytes]:
        """Stream response body chunks (PCM on a 200)."""
        async for chunk in self._response.aiter_bytes():
            yield chunk

    async def error_message(self) -> str | None:
        """Read ≤ 4 KiB of a non-200 body; return ``error.message`` or None (T3)."""
        buf = bytearray()
        try:
            async for chunk in self._response.aiter_bytes():
                buf.extend(chunk)
                if len(buf) >= _ERR_BODY_LIMIT:
                    break
        except httpx.HTTPError:
            return None
        try:
            message = json.loads(bytes(buf[:_ERR_BODY_LIMIT]))["error"]["message"]
        except (json.JSONDecodeError, KeyError, TypeError):
            return None
        return message if isinstance(message, str) else None


class VieneuClient:
    """One shared ``httpx.AsyncClient`` for all three VieNeu calls."""

    def __init__(self, settings: Settings, transport: httpx.AsyncBaseTransport | None = None) -> None:
        self._client = httpx.AsyncClient(
            base_url=settings.vieneu_base_url,
            # Loopback-only upstream: HTTP(S)_PROXY/NO_PROXY env vars must
            # never redirect VieNeu traffic (P1).
            trust_env=False,
            timeout=httpx.Timeout(
                connect=settings.upstream_connect_timeout_s,
                read=settings.upstream_read_gap_s,
                write=10.0,
                pool=settings.upstream_connect_timeout_s,
            ),
            transport=transport,
            headers={"Authorization": f"Bearer {settings.vieneu_api_key}"},
        )

    @asynccontextmanager
    async def speech_stream(self, body: dict) -> AsyncIterator[UpstreamSpeech]:
        async with self._client.stream(
            "POST", "/v1/audio/speech", json=body
        ) as response:
            yield UpstreamSpeech(response)

    async def voices(self) -> httpx.Response:
        return await self._client.get("/v1/voices")

    async def health(self) -> httpx.Response:
        return await self._client.get("/health")

    async def aclose(self) -> None:
        await self._client.aclose()
