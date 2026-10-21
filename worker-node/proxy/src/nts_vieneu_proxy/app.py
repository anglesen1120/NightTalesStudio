"""FastAPI app: ``POST /v1/audio/speech`` streaming pipeline (D2–D7).

Auth (D2) runs before the body is read; D3 checks run in order (step 1 is
the counting ``read_body`` limit). D5 admission (semaphore + upstream-429
retry inside one ``ADMISSION_WAIT_S`` budget) lives in
``SpeechPipeline.admit``; ``_RouteGate`` enforces the D2 allow-list before
any route. After commit, any failure aborts the connection (X2); exactly
one D7 log line is emitted per request.
"""

from __future__ import annotations

import asyncio
import hmac
import json
import secrets
import unicodedata
from contextlib import AsyncExitStack, asynccontextmanager, suppress
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, AsyncIterator

import httpx
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response, StreamingResponse
from starlette.exceptions import HTTPException as StarletteHTTPException
from starlette.requests import ClientDisconnect
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from .catalog import Catalog
from .config import Settings
from .errors import AbortStream, ProxyError, error_body, map_upstream_status
from .health import build_health
from .reqlog import RequestLog, emit
from .transcode import Mp3Encoder
from .upstream import UpstreamSpeech, VieneuClient, parse_retry_after
from .voice_cache import UpstreamVoiceCache

ROUTE = "/v1/audio/speech"
ROUTE_VOICES = "/v1/audio/voices"  # 04.1.3-T1
ROUTE_HEALTH = "/health"  # 04.1.4-T1
# D2 allow-list: path -> allowed methods (the contract, not the router —
# the gate must answer 405 even for routes a later task has not added yet).
_ALLOWED_ROUTES: dict[str, frozenset[str]] = {
    ROUTE: frozenset({"POST"}),
    ROUTE_VOICES: frozenset({"GET"}),
    ROUTE_HEALTH: frozenset({"GET"}),
}
MAX_BODY_BYTES = 262_144  # D3 step 1
_MAX_INPUT_CHARS = 20_000
_KNOWN_FIELDS = {"input", "voice", "response_format"}
_UPSTREAM_SAMPLE_RATE = "48000"


def _utc_ms() -> str:
    """Request-arrival timestamp: ISO-8601 UTC, milliseconds, Z (D7)."""
    return (
        datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    )


@dataclass
class SpeechRequest:
    input: str
    voice: str  # NFC-normalised catalog id (D3 step 5)
    ignored_fields: list[str] = field(default_factory=list)

    def upstream_body(self) -> dict[str, Any]:
        """The fixed D3 shape; never forwards MPT's model/speed/instructions."""
        return {
            "model": "vieneu-v3-turbo",
            "input": self.input,
            "voice": self.voice,
            "response_format": "pcm",
            "stream_format": "audio",
            "sample_rate": 48000,
        }


def _check_auth(request: Request, settings: Settings) -> None:
    """D2: Bearer PROXY_API_KEY via hmac.compare_digest, before body parsing."""
    # Compare bytes: a header carrying non-ASCII/latin-1 bytes is a 401,
    # never a UnicodeEncodeError → 500 (P1). The configured key is printable
    # ASCII — config.py rejects anything else at startup.
    auth = request.headers.get("authorization", "").encode("latin-1", "replace")
    expected = f"Bearer {settings.proxy_api_key}".encode("ascii")
    if not hmac.compare_digest(auth, expected):
        raise ProxyError("proxy_unauthorized", 401, "invalid or missing bearer token")


async def read_body(request: Request, limit: int = MAX_BODY_BYTES) -> bytes:
    """D3 step 1: count bytes as they arrive; never buffer past ``limit``.

    A declared ``Content-Length`` over the limit is rejected before reading;
    otherwise the stream is counted and 413 ``body_too_large`` is raised as
    soon as the count exceeds the limit — the rest is never consumed.
    """
    try:
        declared = int(request.headers.get("content-length", "-1"))
    except ValueError:
        declared = -1  # malformed: let the counting read decide
    if declared > limit:
        raise ProxyError(
            "body_too_large", 413, f"request body exceeds {limit} bytes"
        )
    buf = bytearray()
    async for chunk in request.stream():
        buf.extend(chunk)
        if len(buf) > limit:
            raise ProxyError(
                "body_too_large", 413, f"request body exceeds {limit} bytes"
            )
    return bytes(buf)


def _parse_and_check(body: bytes, catalog: Catalog) -> SpeechRequest:
    """D3 steps 2–6; step 1 (MAX_BODY_BYTES) is ``read_body``."""
    try:
        data = json.loads(body)
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise ProxyError("invalid_request", 400, "request body must be a JSON object") from None
    if not isinstance(data, dict):
        raise ProxyError("invalid_request", 400, "request body must be a JSON object")

    text = data.get("input")
    if not isinstance(text, str) or not text.strip():
        raise ProxyError("invalid_request", 400, "input must be a non-empty string")
    if len(text) > _MAX_INPUT_CHARS:
        raise ProxyError("input_too_long", 413, "input exceeds 20000 characters")

    fmt = data.get("response_format")
    if fmt is not None and fmt != "mp3":
        raise ProxyError("unsupported_format", 400, "only response_format mp3 is supported")

    voice = data.get("voice")
    if not isinstance(voice, str):
        # D3 step 5 / D6: missing or non-string voice is a catalog miss.
        raise ProxyError("voice_not_in_catalog", 400, "voice is not in the catalog")
    v = unicodedata.normalize("NFC", voice.strip())
    if v not in catalog.ids:
        raise ProxyError("voice_not_in_catalog", 400, "voice is not in the catalog")

    ignored = sorted(set(data) - _KNOWN_FIELDS)
    return SpeechRequest(input=text, voice=v, ignored_fields=ignored)




class SpeechPipeline:
    """One request's owned resources: slot, upstream response, ffmpeg, pump.

    `close()` is idempotent, shielded, and emits the D7 log line exactly once.
    """

    def __init__(
        self,
        settings: Settings,
        vieneu: VieneuClient,
        request: SpeechRequest,
        log: RequestLog,
        t0: float,
        active: set["SpeechPipeline"],
        slots: asyncio.Semaphore,
    ) -> None:
        self.settings = settings
        self.vieneu = vieneu
        self.req = request
        self.log = log
        self.t0 = t0
        self.deadline = t0 + settings.first_audio_deadline_s
        self._active = active
        self._active.add(self)
        self._slots = slots
        self.stack = AsyncExitStack()
        self.up: UpstreamSpeech | None = None
        self.enc: Mp3Encoder | None = None
        self.pump: asyncio.Task[None] | None = None
        self.slot_held = False
        self.committed = False
        self.closed = False
        self.outcome: int | tuple[int, str] | None = None
        self.pcm_bytes = 0

        self._last_write: float | None = None
        self._max_gap_s = 0.0
        self._upstream_post_sent: float | None = None
        self._last_upstream_byte: float | None = None
        self._ffmpeg_done: float | None = None
        # D4/T15: set the moment VieNeu answers 200 — before (and independent
        # of) the PCM header checks, because its stream slot is already held.
        self._upstream_200 = False
        self._upstream_eof = False
        self._draining = False
        self._feed_error: OSError | None = None
        self._close_task: asyncio.Task[None] | None = None

    # --- admission -----------------------------------------------------------

    async def admit(self) -> None:
        """D5: proxy slot + upstream-429 retries inside ONE budget.

        ``ADMISSION_WAIT_S`` counts from arrival; the commit deadline
        (``FIRST_AUDIO_DEADLINE_S``, always > the budget, D13) bounds the
        headers wait of every upstream attempt — the httpx read timeout
        deliberately does not (QA note L2). VieNeu 429 → sleep
        ``min(Retry-After or 2, 5, budget left)`` and retry (V3).
        """
        budget_end = self.t0 + self.settings.admission_wait_s
        # A free slot is taken unconditionally — wait_for(timeout=0) would
        # otherwise race the timeout callback against an instant acquire.
        if self._slots.locked():
            try:
                await asyncio.wait_for(
                    self._slots.acquire(),
                    timeout=max(budget_end - _loop().time(), 0.0),
                )
            except TimeoutError:
                raise ProxyError(
                    "proxy_busy", 429, "proxy admission budget spent", retry_after=5
                ) from None
        else:
            await self._slots.acquire()
        self.slot_held = True

        while True:
            try:
                async with asyncio.timeout_at(self.deadline):
                    self._upstream_post_sent = _loop().time()
                    cm = self.vieneu.speech_stream(self.req.upstream_body())
                    up = await cm.__aenter__()
            except TimeoutError:
                raise ProxyError(
                    "upstream_first_audio_timeout",
                    504,
                    "no upstream audio before the deadline",
                ) from None
            except httpx.HTTPError as exc:
                raise ProxyError(
                    "upstream_unavailable",
                    502,
                    f"upstream connect failed: {type(exc).__name__}",
                ) from exc
            if up.status != 429:
                # Keep the response open through the pipeline's exit stack.
                self.stack.push_async_exit(cm.__aexit__)
                self.up = up
                self.log.admission_wait_ms = int(
                    (_loop().time() - self.t0) * 1000
                )
                return
            # VieNeu saturated (V3): close this response and retry inside
            # the shared admission budget.
            await cm.__aexit__(None, None, None)
            wait = min(
                parse_retry_after(up.headers) or 2.0,
                5.0,
                budget_end - _loop().time(),
            )
            if wait <= 0:
                raise ProxyError(
                    "upstream_busy",
                    429,
                    "upstream admission budget spent",
                    retry_after=5,
                )
            await asyncio.sleep(wait)

    # --- pipeline phases -----------------------------------------------------

    async def open_until_first_mp3(self) -> bytes:
        """admit → upstream_open → first_audio → ffmpeg's first MP3 bytes."""
        if self.settings.ffmpeg_path is None:
            # Missing binary is not a startup error (D8); fail here instead.
            raise ProxyError("transcode_failed", 500, "ffmpeg binary not found")
        await self.admit()  # D5: semaphore + upstream-429 retries, one budget
        assert self.up is not None
        self.log.upstream_rid = self.up.request_id
        if self.up.status != 200:
            raise map_upstream_status(
                self.up.status, await self.up.error_message()
            )
        # D4/T15: from here VieNeu holds its synthesis slot until the body is
        # read to EOF — the drain gate is this 200, not the format checks.
        self._upstream_200 = True
        ctype = self.up.headers.get("content-type", "")
        if not ctype.startswith("audio/pcm"):
            raise ProxyError(
                "upstream_bad_format", 502, f"unexpected upstream content-type: {ctype[:40]}"
            )
        if self.up.headers.get("x-sample-rate") != _UPSTREAM_SAMPLE_RATE:
            raise ProxyError("upstream_bad_format", 502, "unexpected upstream sample rate")

        self.enc = Mp3Encoder(self.settings.ffmpeg_path, self.settings.mp3_bitrate)
        try:
            await self.enc.start()
        except OSError as exc:
            raise ProxyError(
                "transcode_failed", 500, f"ffmpeg failed to start: {type(exc).__name__}"
            ) from exc
        self.pump = asyncio.create_task(self._pump_in())

        remaining = self.deadline - _loop().time()
        try:
            chunk = await asyncio.wait_for(self.enc.read(), timeout=max(remaining, 0.0))
        except TimeoutError:
            raise ProxyError(
                "upstream_first_audio_timeout", 504, "no upstream audio before the deadline"
            ) from None

        if self.pump.done():
            # Upstream finished before (or as) the first MP3 bytes arrived.
            exc = self.pump.exception()
            if exc is not None:
                raise self._pump_error(exc) from exc
            if self._feed_error is not None:
                raise ProxyError(
                    "transcode_failed", 500, "ffmpeg stdin broke while feeding PCM"
                ) from self._feed_error
            if self.pcm_bytes == 0:
                # F11/D4: never commit a 200 for zero speakable audio.
                raise ProxyError(
                    "upstream_empty_audio", 502, "upstream finished with zero audio bytes"
                )
        if chunk == b"":
            # ffmpeg ended without producing a single MP3 byte.
            if not self.pump.done():
                try:
                    await asyncio.wait_for(
                        asyncio.shield(self.pump), timeout=max(remaining, 0.1)
                    )
                except TimeoutError:
                    pass
            exc = self.pump.exception() if self.pump.done() else None
            if exc is not None:
                raise self._pump_error(exc) from exc
            if self._feed_error is not None:
                raise ProxyError(
                    "transcode_failed", 500, "ffmpeg stdin broke while feeding PCM"
                ) from self._feed_error
            try:
                rc = await asyncio.wait_for(
                    self.enc.wait(), timeout=max(remaining, 0.1)
                )
            except Exception:
                rc = -1
            if rc != 0:
                raise ProxyError(
                    "transcode_failed", 500, f"ffmpeg exited with code {rc}"
                )
            if self.pcm_bytes == 0:
                raise ProxyError(
                    "upstream_empty_audio", 502, "upstream finished with zero audio bytes"
                )
            raise ProxyError(
                "transcode_failed", 500, "ffmpeg produced no audio before exiting"
            )
        return chunk

    @staticmethod
    def _pump_error(exc: BaseException) -> ProxyError:
        """Pre-commit pump failure → D6 rows (not AbortStream).

        ffmpeg-stdin OSErrors never reach here (the pump records them in
        ``_feed_error`` and keeps draining); a pump exception means the
        upstream read failed.
        """
        return ProxyError("upstream_unavailable", 502, "upstream read failed")

    async def _pump_in(self) -> None:
        """Upstream PCM → ffmpeg stdin, then close stdin. D4 task 1.

        The pump is the only upstream reader (T15 addendum): an httpx stream
        cannot be iterated twice, so after an ffmpeg-stdin failure it keeps
        reading and discarding until upstream EOF — VieNeu's synthesis (and
        slot) only finishes when its stream ends.
        """
        try:
            assert self.up is not None and self.enc is not None
            async for chunk in self.up.aiter_pcm():
                self.pcm_bytes += len(chunk)
                self._last_upstream_byte = _loop().time()
                if self._draining:
                    continue
                try:
                    await self.enc.feed(chunk)
                except OSError as exc:
                    # ffmpeg's stdin is dead: record it, drain the rest.
                    self._feed_error = self._feed_error or exc
                    self._draining = True
            self._upstream_eof = True
        finally:
            if self.enc is not None:
                await self.enc.close()  # EOF lets ffmpeg flush the MP3 tail

    async def _join_pump(self, code: str) -> None:
        """Await the pump and translate its failure into an AbortStream/code."""
        assert self.pump is not None
        try:
            await self.pump
        except asyncio.CancelledError:
            raise
        except httpx.HTTPError as exc:
            raise AbortStream(code) from exc
        except Exception as exc:
            raise AbortStream(code) from exc

    # --- post-commit body ----------------------------------------------------

    async def body(self, first: bytes) -> AsyncIterator[bytes]:
        """Response iterator. Raising after commit aborts the socket (X2)."""
        self.outcome = None
        try:
            yield self._write(first)
            while True:
                if _loop().time() > self.t0 + self.settings.request_max_s:
                    raise AbortStream("request_max_exceeded")
                chunk = await self._read_or_fail()
                if chunk == b"":
                    break
                yield self._write(chunk)
            assert self.enc is not None
            rc = await self.enc.wait()
            self._ffmpeg_done = _loop().time()
            self.log.ffmpeg_ms = self._ffmpeg_ms()
            if self._feed_error is not None or rc != 0:
                raise AbortStream("transcode_failed")
            await self._join_pump("upstream_stream_broken")
            self.outcome = 200
        except AbortStream as exc:
            self.outcome = (exc.log_status, exc.code)
            raise
        except (asyncio.CancelledError, GeneratorExit, OSError):
            self.outcome = self.outcome or (499, "client_disconnected")
            raise
        except BaseException as exc:
            self.outcome = (500, "internal_error")
            raise
        finally:
            await self.close()

    def _write(self, chunk: bytes) -> bytes:
        now = _loop().time()
        if self.log.ttfb_ms is None:
            self.log.ttfb_ms = int((now - self.t0) * 1000)
        else:
            assert self._last_write is not None
            self._max_gap_s = max(self._max_gap_s, now - self._last_write)
        self._last_write = now
        self.log.bytes_out += len(chunk)
        return chunk

    async def _read_or_fail(self) -> bytes:
        """Next MP3 chunk, racing the pump so upstream errors surface early.

        A gap longer than UPSTREAM_READ_GAP_S is an upstream break (D4).
        """
        assert self.enc is not None
        read_t = asyncio.ensure_future(self.enc.read())
        try:
            gap = float(self.settings.upstream_read_gap_s)
            done, _ = await asyncio.wait(
                {read_t, self.pump},  # type: ignore[list-item]
                timeout=gap,
                return_when=asyncio.FIRST_COMPLETED,
            )
            if not done:
                raise AbortStream("upstream_stream_broken")
            if read_t in done:
                return read_t.result()
            if self.pump is not None and self.pump in done and self.pump.exception() is not None:
                read_t.cancel()
                with suppress(BaseException):
                    await read_t
                await self._join_pump("upstream_stream_broken")
                raise AbortStream("upstream_stream_broken")
            # Pump finished first: bound the MP3 flush tail by the gap too —
            # a dead ffmpeg must surface as transcode_failed, not a hang (P1).
            try:
                return await asyncio.wait_for(read_t, timeout=gap)
            except TimeoutError:
                raise AbortStream("transcode_failed") from None
        finally:
            if not read_t.done():
                read_t.cancel()
                with suppress(BaseException):
                    await read_t

    # --- teardown ------------------------------------------------------------

    async def close(self) -> None:
        """Idempotent, shielded cleanup; emits the D7 line exactly once."""
        if self.closed:
            return
        self.closed = True
        # asyncio.Task() bypasses the loop's task factory: uvicorn/anyio
        # registers factory-made tasks in the request's cancel scope and
        # cancels them on client disconnect — teardown (and the T15 drain)
        # must outlive the request task to hold the slot and log at drain end.
        finish = asyncio.Task(self._teardown())
        for _ in range(3):  # keep joining even under repeated cancellation
            try:
                await asyncio.shield(finish)
                break
            except asyncio.CancelledError:
                if finish.done():
                    break
                continue
        with suppress(BaseException):
            await asyncio.shield(finish)

    def needs_drain(self) -> bool:
        """Upstream answered 200 and its stream is not finished (D4/T15).

        The gate is the 200 itself, not whether the PCM header checks passed:
        VieNeu holds its stream slot from the moment it answers, so a 200
        whose Content-Type/X-Sample-Rate fail validation is still drained to
        EOF instead of being closed early.
        """
        return self.up is not None and self._upstream_200 and not self._upstream_eof

    def close_detached(self) -> None:
        """Fire-and-forget close for a pre-commit error after a valid 200.

        The error response must go out at once while the upstream drain runs
        detached and keeps holding the slot (T15 addendum). A plain
        ``asyncio.Task`` is not cancel-scope-tracked, and the pipeline stays
        in ``_active`` — which keeps this task referenced — until teardown
        ends.
        """
        if self._close_task is None:
            self._close_task = asyncio.Task(self.close())

    async def _teardown(self) -> None:
        """cleanup → leave the active set → D7 line.

        Lives in its own task so the log line is still emitted at drain end
        if the caller of ``close()`` was cancelled or generator-closed.
        """
        try:
            await self._cleanup()
        finally:
            self._active.discard(self)
            self._finish_log()
            try:
                emit(self.log)  # a log failure must never abort the teardown
            except Exception:
                pass

    async def _cleanup(self) -> None:
        """Kill local resources, drain upstream (T15), release the slot.

        Once the upstream answered 200 we keep reading and discarding its
        body until the stream ends, so VieNeu finishes the synthesis and
        releases its slot — disconnecting early leaked the upstream slot for
        minutes (SPIKE), and the same holds for a 200 that fails the PCM
        header checks. Bounded by UPSTREAM_READ_GAP_S without a byte and
        REQUEST_MAX_S since arrival; a read error or bound hit means we just
        close upstream — nothing else is possible.
        """
        drain = self.needs_drain()
        try:
            if drain:
                # The pump keeps reading but discards instead of feeding ffmpeg.
                self._draining = True
            if self.pump is not None and not self.pump.done() and not drain:
                self.pump.cancel()
            if self.enc is not None:
                await self.enc.kill()
                if self._ffmpeg_done is None:
                    self._ffmpeg_done = _loop().time()
            if drain:
                await self._drain_upstream()
            if self.pump is not None:
                if not self.pump.done():
                    self.pump.cancel()
                with suppress(BaseException):
                    await self.pump
            with suppress(Exception):
                await self.stack.aclose()
        finally:
            # A hard-killed teardown must never leak the semaphore — even if
            # that abandons the drain (nothing else possible at that point).
            if self.slot_held:
                self._slots.release()
                self.slot_held = False

    async def _drain_upstream(self) -> None:
        """Consume/discard the rest of the upstream body (T15).

        With a live pump it keeps reading (``_draining`` makes it discard
        instead of feed) and we bound its finish. With no live pump
        (pre-commit failure — including a 200 that failed the PCM header
        checks, so no pump was ever started — or the pump already dead) we
        read directly.
        """
        pump = self.pump
        gap = float(self.settings.upstream_read_gap_s)
        hard = self.t0 + self.settings.request_max_s
        start = _loop().time()  # zero-byte drain: gap clock starts HERE (P1)
        if pump is not None and not pump.done():
            while not pump.done():
                last = self._last_upstream_byte or start
                budget = min(last + gap, hard) - _loop().time()
                if budget <= 0:
                    break
                try:
                    await asyncio.wait_for(asyncio.shield(pump), timeout=budget)
                    break
                except TimeoutError:
                    continue  # re-evaluate last-byte gap / hard deadline
                except BaseException:
                    break  # pump failed: nothing else possible
            return
        if pump is not None:
            # The pump started but is already dead: its iteration ended, so a
            # second aiter_pcm() would just raise StreamConsumed — give up.
            return
        # No pump was ever started (pre-commit failure): read directly.
        assert self.up is not None
        it = self.up.aiter_pcm()
        try:
            while True:
                budget = min(gap, hard - _loop().time())
                if budget <= 0:
                    break
                try:
                    chunk = await asyncio.wait_for(anext(it), timeout=budget)
                except StopAsyncIteration:
                    self._upstream_eof = True
                    break
                self.pcm_bytes += len(chunk)
                self._last_upstream_byte = _loop().time()
        except BaseException:
            pass  # read error/cancellation: stack.aclose() closes upstream
        finally:
            with suppress(BaseException):
                await it.aclose()

    def _ffmpeg_ms(self) -> int | None:
        if self._ffmpeg_done is None or self._last_upstream_byte is None:
            return None
        return max(0, int((self._ffmpeg_done - self._last_upstream_byte) * 1000))

    def _finish_log(self) -> None:
        log = self.log
        end = _loop().time()
        log.wall_ms = int((end - self.t0) * 1000)
        if self._upstream_post_sent is not None and self._last_upstream_byte is not None:
            log.vieneu_ms = int(
                (self._last_upstream_byte - self._upstream_post_sent) * 1000
            )
        if log.ffmpeg_ms is None:
            log.ffmpeg_ms = self._ffmpeg_ms()
        if self._last_write is not None:
            log.max_gap_ms = int(self._max_gap_s * 1000)
        log.audio_seconds = round(self.pcm_bytes / 2 / 48000, 3)
        if self.pcm_bytes % 2:
            log.warnings = (log.warnings or []) + ["pcm_odd_tail"]

        if self.committed:
            if self.outcome == 200:
                log.status = 200
                log.error_code = None
            elif isinstance(self.outcome, tuple):
                log.status, log.error_code = self.outcome
            else:
                log.status, log.error_code = 500, "internal_error"
        # Non-committed failures: route already set status/http_status/error_code.

        failed = log.status not in (None, 200)
        if failed and self.enc is not None:
            tail = self.enc.stderr_tail
            if tail:
                log.ffmpeg_stderr_tail = tail


class _AppState:
    def __init__(self, settings: Settings, catalog: Catalog, vieneu: VieneuClient) -> None:
        self.settings = settings
        self.catalog = catalog
        self.vieneu = vieneu
        self.speech_slots = asyncio.Semaphore(settings.proxy_max_concurrent)
        self.active_pipelines: set[SpeechPipeline] = set()


class _BoundedStreamResponse(StreamingResponse):
    """StreamingResponse enforcing REQUEST_MAX_S and the per-send gap (P1).

    Every ASGI send is bounded by UPSTREAM_READ_GAP_S and by the
    t0 + REQUEST_MAX_S wall; a stalled or over-long send raises
    ``AbortStream("request_max_exceeded")`` (502 in the D7 line — the same
    code for both bounds, per the P1 ruling). The ``finally`` below runs the
    generator's ``aclose()`` so ``p.body``'s own ``finally: close()`` — and
    therefore the T15 upstream drain — still executes when a send aborts
    mid-iteration (plain ``async for`` would leave the generator suspended).
    """

    def __init__(self, pipe: SpeechPipeline, first: bytes, **kwargs: Any) -> None:
        super().__init__(pipe.body(first), **kwargs)
        self._pipe = pipe

    async def stream_response(self, send: Send) -> None:
        pipe = self._pipe
        hard = pipe.t0 + pipe.settings.request_max_s
        gap = float(pipe.settings.upstream_read_gap_s)

        def raise_max() -> AbortStream:
            # The generator only sees a GeneratorExit at aclose() — record the
            # real outcome here so the D7 line says 502, not 499.
            pipe.outcome = (502, "request_max_exceeded")
            return AbortStream("request_max_exceeded")

        async def bounded_send(message: Message) -> None:
            if _loop().time() > hard:
                raise raise_max()
            try:
                await asyncio.wait_for(send(message), timeout=gap)
            except TimeoutError:
                raise raise_max() from None

        try:
            await super().stream_response(bounded_send)
        finally:
            aclose = self.body_iterator.aclose()
            try:
                await aclose
            except (GeneratorExit, Exception):
                pass
            except BaseException:
                # CancelledError/KeyboardInterrupt: run the cleanup anyway so
                # the slot and upstream drain are not abandoned, then let the
                # cancellation propagate to the caller.
                with suppress(BaseException):
                    await self._pipe.close()
                raise
            with suppress(BaseException):
                await self._pipe.close()

class _RouteGate:
    """D2 allow-list as pure ASGI: runs before FastAPI's router, so a
    rejected request never reaches a handler or ``VieneuClient``.

    Unknown path → 404 ``route_not_found``; known path + other method →
    405 ``method_not_allowed`` + ``Allow``. Every rejection emits one D7
    line (``route`` = the D2 route for a 405, ``"other"`` for a 404 — the
    raw path is never logged).
    """

    def __init__(self, app: ASGIApp) -> None:
        self._app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self._app(scope, receive, send)
            return
        path = scope["path"]
        method = scope["method"]
        allowed = _ALLOWED_ROUTES.get(path)
        if allowed is not None and method in allowed:
            await self._app(scope, receive, send)
            return
        if allowed is None:
            _emit_reject_log(scope, 404, "route_not_found")
            await _raw_json(
                send, 404, error_body("route_not_found", "route not found")
            )
        else:
            allow = ", ".join(sorted(allowed))
            _emit_reject_log(scope, 405, "method_not_allowed")
            await _raw_json(
                send,
                405,
                error_body("method_not_allowed", "method not allowed"),
                [(b"allow", allow.encode())],
            )


def _emit_reject_log(scope: Scope, status: int, code: str) -> None:
    """D7 line for a request rejected before any handler ran."""
    emit(
        RequestLog(
            ts=_utc_ms(),
            rid=f"px-{secrets.token_hex(4)}",
            route=scope["path"] if scope["path"] in _ALLOWED_ROUTES else "other",
            method=scope["method"],
            status=status,
            http_status=status,
            error_code=code,
            wall_ms=0,
        )
    )


async def _raw_json(
    send: Send, status: int, body: dict, extra_headers: list | None = None
) -> None:
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    headers = [(b"content-type", b"application/json")] + (extra_headers or [])
    await send(
        {
            "type": "http.response.start",
            "status": status,
            "headers": headers,
        }
    )
    await send({"type": "http.response.body", "body": payload})


async def _on_proxy_error(request: Request, exc: ProxyError) -> JSONResponse:
    """ProxyError escaping a route (future endpoints); speech handles its own."""
    status = exc.http_status
    _emit_reject_log(request.scope, status, exc.code)
    headers = (
        {"Retry-After": str(exc.retry_after)} if exc.retry_after is not None else None
    )
    return JSONResponse(error_body(exc.code, exc.message), status_code=status, headers=headers)


async def _on_http_exception(
    request: Request, exc: StarletteHTTPException
) -> JSONResponse:
    """Router-level 404/405 → the D6 shape (the gate normally answers first)."""
    if exc.status_code == 404:
        status, code = 404, "route_not_found"
    elif exc.status_code == 405:
        status, code = 405, "method_not_allowed"
    else:
        status, code = 500, "internal_error"
    _emit_reject_log(request.scope, status, code)
    return JSONResponse(
        error_body(code, getattr(exc, "detail", "") or code),
        status_code=status,
        headers=getattr(exc, "headers", None),
    )


async def _on_validation_error(
    request: Request, exc: RequestValidationError
) -> JSONResponse:
    _emit_reject_log(request.scope, 400, "invalid_request")
    return JSONResponse(error_body("invalid_request", "invalid request"), status_code=400)


def create_app(
    settings: Settings,
    catalog: Catalog,
    *,
    vieneu_transport: httpx.AsyncBaseTransport | None = None,
) -> FastAPI:
    vieneu = VieneuClient(settings, transport=vieneu_transport)
    voice_cache = UpstreamVoiceCache(vieneu, settings.catalog_check_interval_s)
    state = _AppState(settings, catalog, vieneu)

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        # 04.1.3-T1: warm the upstream voice cache before serving requests;
        # stop its refresh task on shutdown (it owns no request resources).
        await voice_cache.start()
        try:
            yield
        finally:
            await voice_cache.stop()
        await vieneu.aclose()

    app = FastAPI(
        docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan
    )
    app.add_middleware(_RouteGate)
    app.add_exception_handler(ProxyError, _on_proxy_error)
    app.add_exception_handler(StarletteHTTPException, _on_http_exception)
    app.add_exception_handler(RequestValidationError, _on_validation_error)
    app.state.app_state = state
    app.state.settings = settings
    app.state.catalog = catalog
    app.state.vieneu = vieneu
    app.state.speech_slots = state.speech_slots
    app.state.active_pipelines = state.active_pipelines
    app.state.voice_cache = voice_cache

    @app.post(ROUTE, response_model=None)
    async def speech(request: Request) -> Response:
        log = RequestLog(
            ts=_utc_ms(),
            rid=f"px-{secrets.token_hex(4)}",
            route=ROUTE,
            method="POST",
        )
        t0 = _loop().time()
        p: SpeechPipeline | None = None
        try:
            _check_auth(request, settings)
            req = _parse_and_check(await read_body(request), catalog)
            log.voice = req.voice
            log.chars = len(req.input)
            log.ignored_fields = req.ignored_fields
            p = SpeechPipeline(
                settings, vieneu, req, log, t0, state.active_pipelines, state.speech_slots
            )
            first = await p.open_until_first_mp3()
        except ProxyError as exc:
            log.status = exc.http_status
            log.http_status = exc.http_status
            log.error_code = exc.code
            log.wall_ms = int((_loop().time() - t0) * 1000)
            if p is not None:
                if p.needs_drain():
                    # T15 addendum: answer at once; the upstream drain runs
                    # detached and keeps holding the slot until VieNeu ends.
                    p.close_detached()
                else:
                    await p.close()  # emits the line
            else:
                emit(log)
            headers = (
                {"Retry-After": str(exc.retry_after)} if exc.retry_after is not None else None
            )
            return JSONResponse(
                error_body(exc.code, exc.message),
                status_code=exc.http_status,
                headers=headers,
            )
        except (asyncio.CancelledError, ClientDisconnect):
            # Pre-commit client loss is a 499, never a 500 (P1).
            log.status = 499
            log.error_code = "client_disconnected"
            log.wall_ms = int((_loop().time() - t0) * 1000)
            try:
                if p is not None:
                    if p.needs_drain():
                        p.close_detached()
                    else:
                        await p.close()
                else:
                    emit(log)
            finally:
                raise
        except BaseException:
            log.status = 500
            log.error_code = log.error_code or "internal_error"
            log.wall_ms = int((_loop().time() - t0) * 1000)
            if p is not None:
                if p.needs_drain():
                    p.close_detached()
                else:
                    await p.close()
            else:
                emit(log)
            raise

        p.committed = True
        log.http_status = 200
        return _BoundedStreamResponse(
            p,
            first,
            media_type="audio/mpeg",
            headers={
                "Cache-Control": "no-store",
                "X-Request-Id": log.rid,
            },
        )
    # --- 04.1.3-T1: voices endpoint + upstream voice cache (separate block) ---

    @app.get("/v1/audio/voices", response_model=None)
    async def list_voices(request: Request) -> JSONResponse:
        # D2 auth first (same helper as /v1/audio/speech); the request path
        # never calls VieNeu — it reads the lifespan-owned cache only (D9).
        # One D7 line per request, same as the speech route.
        log = RequestLog(
            ts=_utc_ms(),
            rid=f"px-{secrets.token_hex(4)}",
            route="/v1/audio/voices",
            method="GET",
        )
        t0 = _loop().time()
        try:
            _check_auth(request, settings)
        except ProxyError as exc:
            log.status = exc.http_status
            log.http_status = exc.http_status
            log.error_code = exc.code
            log.wall_ms = int((_loop().time() - t0) * 1000)
            emit(log)
            return JSONResponse(
                error_body(exc.code, exc.message), status_code=exc.http_status
            )
        upstream_names = await request.app.state.voice_cache.names()
        cat: Catalog = request.app.state.catalog
        voices = [
            {
                "id": v.id,
                "name": v.display_name,
                "language": v.language,
                "gender": v.gender,
            }
            for v in cat.voices
            if v.active and v.id in upstream_names
        ]
        log.status = 200
        log.http_status = 200
        log.wall_ms = int((_loop().time() - t0) * 1000)
        emit(log)
        return JSONResponse({"voices": voices})

    # --- 04.1.4-T1: GET /health (proxy-health.v1, D8) — separate block ---
    # D2: no auth (loopback only) and the body carries no secrets (D8).
    # `app.state.ffmpeg_probe` is set by the entry point (`__main__.py`) before
    # uvicorn starts: the D8 ffmpeg probe is process-start I/O, not request work.
    app.state.started_at = datetime.now(timezone.utc)

    @app.get("/health", response_model=None)
    async def health(request: Request) -> JSONResponse:
        log = RequestLog(
            ts=_utc_ms(),
            rid=f"px-{secrets.token_hex(4)}",
            route="/health",
            method="GET",
        )
        t0 = _loop().time()
        status_code, body = await build_health(request.app.state)
        log.status = status_code
        log.http_status = status_code
        log.wall_ms = int((_loop().time() - t0) * 1000)
        emit(log)  # D7: one line per request, every route
        return JSONResponse(body, status_code=status_code)

    return app


def _loop() -> asyncio.AbstractEventLoop:
    return asyncio.get_running_loop()
