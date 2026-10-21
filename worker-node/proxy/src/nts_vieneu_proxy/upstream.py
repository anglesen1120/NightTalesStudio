"""VieNeu upstream client (README D2).

Exactly three upstream methods, each with a fixed path — the proxy has no code
path to any other VieNeu route (D2):

- ``speech_stream`` → ``POST /v1/audio/speech`` (streaming, this task)
- ``voices`` → ``GET /v1/voices`` (used by 04.1.3-T1's voice cache)
- ``health`` → ``GET /health`` (used by 04.1.4-T1)
"""

from __future__ import annotations

import asyncio
import json
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator

import httpx

from .config import Settings
from .errors import ProxyError

_ERR_BODY_LIMIT = 4096

# D8: the live ``GET /health`` check is bounded at a fixed 2 s, independent of
# UPSTREAM_CONNECT_TIMEOUT_S — it must fail fast so /health stays responsive
# while speech requests are streaming.
HEALTH_TIMEOUT_S = 2.0


class UpstreamSpeech:
    """Handle on an in-flight VieNeu speech response (yielded by speech_stream)."""

    def __init__(self, response: httpx.Response, gap_s: float) -> None:
        self._response = response
        self._gap_s = gap_s
        self.status = response.status_code
        self.headers = response.headers
        self.request_id = response.headers.get("x-request-id")

    async def aiter_pcm(self) -> AsyncIterator[bytes]:
        """Stream response body chunks (PCM on a 200).

        L2: each socket read is bounded by ``gap_s`` here — the httpx read
        timeout is off for speech streams because it would also bound
        waiting for the response headers, which belongs to the caller's
        FIRST_AUDIO_DEADLINE_S commit deadline instead.
        """
        it = self._response.aiter_bytes()
        try:
            while True:
                try:
                    chunk = await asyncio.wait_for(anext(it), timeout=self._gap_s)
                except StopAsyncIteration:
                    return
                yield chunk
        finally:
            await it.aclose()
    async def error_message(self) -> str | None:
        """Read ≤ 4 KiB of a non-200 body; return ``error.message`` or None (T3)."""
        buf = bytearray()
        try:
            it = self._response.aiter_bytes()
            try:
                while len(buf) < _ERR_BODY_LIMIT:
                    try:
                        buf.extend(
                            await asyncio.wait_for(anext(it), timeout=self._gap_s)
                        )
                    except StopAsyncIteration:
                        break
            finally:
                await it.aclose()
        except (httpx.HTTPError, TimeoutError):
            return None
        try:
            message = json.loads(bytes(buf[:_ERR_BODY_LIMIT]))["error"]["message"]
        except (json.JSONDecodeError, KeyError, TypeError):
            return None
        return message if isinstance(message, str) else None


def parse_retry_after(headers: httpx.Headers) -> float | None:
    """D5/V3: integer-seconds ``Retry-After`` only; HTTP-date/garbage → None."""
    raw = headers.get("retry-after")
    if raw is None:
        return None
    raw = raw.strip()
    return float(int(raw)) if raw.isdigit() else None


class VieneuClient:
    """One shared ``httpx.AsyncClient`` for all three VieNeu calls."""

    def __init__(self, settings: Settings, transport: httpx.AsyncBaseTransport | None = None) -> None:
        self._connect_timeout_s = settings.upstream_connect_timeout_s
        self._gap_s = float(settings.upstream_read_gap_s)
        self._client = httpx.AsyncClient(
            base_url=settings.vieneu_base_url,
            # Loopback-only upstream: HTTP(S)_PROXY/NO_PROXY env vars must
            # never redirect VieNeu traffic (P1).
            trust_env=False,
            timeout=httpx.Timeout(
                connect=settings.upstream_connect_timeout_s,
                # L2: no client read timeout — for streaming calls it would
                # also bound waiting for response headers, which belongs to
                # the FIRST_AUDIO_DEADLINE_S commit deadline. Body reads
                # enforce UPSTREAM_READ_GAP_S per chunk (aiter_pcm); the
                # non-streaming calls set their own per-request timeout.
                read=None,
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
            yield UpstreamSpeech(response, self._gap_s)

    async def voices(self) -> list[dict]:
        """VieNeu ``GET /v1/voices`` → the ``data`` array (V7).

        Raises ``ProxyError(upstream_auth_failed)`` on 401/403 and
        ``ProxyError(upstream_unavailable)`` on connect/timeout/5xx/any other
        non-200 or a malformed body; the caller (voice_cache.py) records the
        code as a refresh failure, not a request failure.
        """
        try:
            resp = await self._client.get(
                "/v1/voices",
                # Small list, never on the hot path: fixed 5 s read timeout.
                timeout=httpx.Timeout(5.0, connect=self._connect_timeout_s),
            )
        except httpx.HTTPError:
            raise ProxyError(
                "upstream_unavailable", 502, "upstream voices request failed"
            ) from None
        if resp.status_code in (401, 403):
            raise ProxyError(
                "upstream_auth_failed", 502, "upstream rejected the proxy key"
            )
        if resp.status_code != 200:
            raise ProxyError(
                "upstream_unavailable", 502, "upstream returned an unexpected status"
            )
        try:
            data = resp.json()["data"]
        except (json.JSONDecodeError, KeyError, TypeError):
            data = None
        if not isinstance(data, list):
            raise ProxyError(
                "upstream_unavailable", 502, "upstream voices body malformed"
            )
        return data

    async def health(self) -> dict[str, Any]:
        """VieNeu ``GET /health`` (no auth, V7) with a fixed 2 s timeout (D8).

        Returns the V7 body on 200. Anything else that *answered* — a non-200
        status or a body that is not a JSON object — proves the server is
        reachable but not healthy, so it comes back as ``{"status": "error"}``
        and the caller reports ``vieneu_unhealthy`` (D8: "non-200 or
        ``status != "ok"``"). Only a connect error or the 2 s timeout means
        ``vieneu_unreachable``, which raises ``ProxyError(upstream_unavailable)``.

        The timeout is fixed and independent of ``UPSTREAM_CONNECT_TIMEOUT_S``:
        health must fail fast so ``/health`` stays responsive under load.
        """
        try:
            resp = await self._client.get(
                "/health", timeout=httpx.Timeout(HEALTH_TIMEOUT_S)
            )
        except httpx.HTTPError:
            raise ProxyError(
                "upstream_unavailable", 502, "upstream health request failed"
            ) from None
        if resp.status_code != 200:
            return {"status": "error"}
        try:
            data = resp.json()
        except (ValueError, TypeError):  # JSONDecodeError is a ValueError
            data = None
        if not isinstance(data, dict):
            return {"status": "error"}
        return data

    async def aclose(self) -> None:
        await self._client.aclose()
