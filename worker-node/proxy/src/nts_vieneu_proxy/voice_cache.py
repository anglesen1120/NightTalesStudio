"""Background cache of VieNeu's canonical voice names (README D8/D9).

``GET /v1/voices`` is refreshed every ``CATALOG_CHECK_INTERVAL_S`` (D13) by
a background task owned by the app lifespan; the request path never touches
the upstream. The latest refresh result always decides ``last_error`` —
a stale name set never masks a failed refresh (D8). Only ``data[].id``
counts (canonical names; ``aliases`` are read and discarded — an alias must
never satisfy ``id in names()``, F07).
"""

from __future__ import annotations

import asyncio
import json
import sys
from contextlib import suppress
from dataclasses import dataclass
from typing import Any

from .errors import ProxyError
from .upstream import VieneuClient


@dataclass
class _CacheState:
    names: frozenset[str]
    last_error: str | None  # D6 code of the LATEST refresh if it failed, else None
    ever_succeeded: bool
    last_refresh_monotonic: float


def _names_of(data: list[dict]) -> frozenset[str]:
    """Canonical ``data[].id`` values only — never ``aliases`` (F07)."""
    return frozenset(
        e["id"]
        for e in data
        if isinstance(e, dict) and isinstance(e.get("id"), str)
    )


def _log_refresh_failure(code: str) -> None:
    """One compact JSON warning on stdout; code only (D7-style, no bodies)."""
    sys.stdout.write(
        json.dumps({"event": "voice_cache_refresh_failed", "code": code}) + "\n"
    )
    sys.stdout.flush()


class UpstreamVoiceCache:
    def __init__(self, client: VieneuClient, interval_s: int) -> None:
        self._client = client
        self._interval_s = interval_s
        self._state = _CacheState(
            names=frozenset(),
            last_error=None,
            ever_succeeded=False,
            last_refresh_monotonic=0.0,
        )
        self._task: asyncio.Task[None] | None = None

    async def start(self) -> None:
        """Warm the cache synchronously, then spawn the refresh task."""
        await self._refresh()  # first request after boot sees real data (or a code)
        self._task = asyncio.Task(self._run())

    async def stop(self) -> None:
        """Cancel the refresh task; called from the app lifespan shutdown."""
        task, self._task = self._task, None
        if task is None:
            return
        task.cancel()
        with suppress(asyncio.CancelledError):
            await asyncio.shield(task)

    async def names(self) -> frozenset[str]:
        """Current cached set; immutable reference, never awaits I/O."""
        return self._state.names

    @property
    def last_error(self) -> str | None:
        """``upstream_auth_failed`` | ``upstream_unavailable`` | None (D8)."""
        return self._state.last_error

    @property
    def ever_succeeded(self) -> bool:
        return self._state.ever_succeeded

    @property
    def last_refresh_monotonic(self) -> float:
        """Loop-time of the last refresh attempt's completion (0 = none)."""
        return self._state.last_refresh_monotonic

    async def _run(self) -> None:
        while True:
            await asyncio.sleep(self._interval_s)
            await self._refresh()

    async def _refresh(self) -> None:
        """One refresh; never raises out — CancelledError passes through."""
        try:
            data = await self._client.voices()
        except asyncio.CancelledError:
            raise
        except ProxyError as exc:
            self._fail(exc.code)
        except Exception:
            self._fail("upstream_unavailable")
        else:
            self._state.names = _names_of(data)
            self._state.last_error = None
            self._state.ever_succeeded = True
            self._state.last_refresh_monotonic = asyncio.get_running_loop().time()

    def _fail(self, code: str) -> None:
        # Latest result decides (D8): keep `names`, overwrite `last_error`.
        self._state.last_error = code
        self._state.last_refresh_monotonic = asyncio.get_running_loop().time()
        _log_refresh_failure(code)
