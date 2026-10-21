"""UpstreamVoiceCache (04.1.3-T1): refresh semantics per README D8/D9."""

from __future__ import annotations

import asyncio

import httpx

from nts_vieneu_proxy.app import create_app
from nts_vieneu_proxy.upstream import VieneuClient
from nts_vieneu_proxy.voice_cache import UpstreamVoiceCache

from .conftest import DUMMY_KEY
from .test_voices_route import PROXY_VOICES_PATH, _scripted_transport
AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
TICK_S = 0.02  # fake clock: interval far below CATALOG_CHECK_INTERVAL_S's 30 s min


def _cache(settings, responses, calls: list | None = None) -> UpstreamVoiceCache:
    calls = calls if calls is not None else []
    client = VieneuClient(settings, transport=_scripted_transport(responses, calls))
    return UpstreamVoiceCache(client, interval_s=TICK_S)


async def _next_tick() -> None:
    # Give the background loop one sleep+refresh cycle.
    await asyncio.sleep(TICK_S * 3)


async def test_voice_cache_refresh_replaces_names_on_success(settings):
    calls: list = []
    cache = _cache(settings, [[{"id": "Thiện Minh"}], [{"id": "Hải Đăng"}]], calls)
    await cache.start()
    try:
        assert await cache.names() == {"Thiện Minh"}
        await _next_tick()
        assert await cache.names() == {"Hải Đăng"}
        assert cache.last_error is None
        assert cache.ever_succeeded is True
    finally:
        await cache.stop()


async def test_voice_cache_refresh_keeps_stale_names_on_upstream_error(settings):
    cache = _cache(settings, [[{"id": "Hải Đăng"}], 502])
    await cache.start()
    try:
        assert await cache.names() == {"Hải Đăng"}
        assert cache.last_error is None
        await _next_tick()
        # Stale names survive; the latest refresh's failure is still visible.
        assert await cache.names() == {"Hải Đăng"}
        assert cache.last_error == "upstream_unavailable"
        assert cache.ever_succeeded is True
    finally:
        await cache.stop()


async def test_voice_cache_refresh_auth_failure_reports_auth_failed(settings):
    cache = _cache(settings, [[{"id": "Hải Đăng"}], 401])
    await cache.start()
    try:
        await _next_tick()
        assert await cache.names() == {"Hải Đăng"}
        assert cache.last_error == "upstream_auth_failed"
    finally:
        await cache.stop()


async def test_voice_cache_start_cold_boot_failure_yields_empty_set_and_last_error(
    settings, catalog
):
    calls: list = []
    transport = _scripted_transport([503], calls)
    app = create_app(settings, catalog, vieneu_transport=transport)
    async with app.router.lifespan_context(app):
        cache = app.state.voice_cache
        assert await cache.names() == frozenset()
        assert cache.last_error == "upstream_unavailable"
        assert cache.ever_succeeded is False
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://t"
        ) as client:
            resp = await client.get(PROXY_VOICES_PATH, headers=AUTH)
        # The route itself never calls upstream: 200 with an empty list.
        assert resp.status_code == 200
        assert resp.json() == {"voices": []}
        assert len(calls) == 1  # only the boot refresh hit VieNeu


async def test_voice_cache_names_excludes_aliases(settings):
    cache = _cache(
        settings,
        [[{"id": "Thiện Minh", "name": "Thiện Minh", "aliases": ["Anh Khôi"]}]],
    )
    await cache.start()
    try:
        names = await cache.names()
        assert names == {"Thiện Minh"}
        assert "Anh Khôi" not in names
    finally:
        await cache.stop()


async def test_voice_cache_stop_is_clean_and_idempotent(settings):
    cache = _cache(settings, [[{"id": "Hải Đăng"}]])
    await cache.start()
    await cache.stop()
    assert cache._task is None
    await cache.stop()  # second call is a no-op
    assert await cache.names() == {"Hải Đăng"}  # kept after stop
