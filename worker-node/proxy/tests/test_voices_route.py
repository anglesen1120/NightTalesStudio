"""GET /v1/audio/voices (04.1.3-T1): active catalog ∩ upstream cache (D2/D9)."""

from __future__ import annotations

import httpx

from nts_vieneu_proxy.app import create_app

from .conftest import DUMMY_KEY, catalog_doc, voice_entry, write_catalog
from nts_vieneu_proxy.catalog import load_catalog

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}
PROXY_VOICES_PATH = "/v1/audio/voices"  # the proxy's route (D2)
_UPSTREAM_VOICES_PATH = "/v1/voices"    # VieNeu's route (V7)

CATALOG_VOICES = [
    voice_entry("Hải Đăng", "hai-dang", language="vi", gender="male"),
    voice_entry("Ngọc Huyền", "ngoc-huyen", language="en", gender="female"),
    voice_entry("Ghost", "ghost", language="en", gender="male"),  # active, not upstream
    voice_entry("Retired", "retired", language="vi", gender="female", active=False),
]


def _scripted_transport(responses, calls: list) -> httpx.MockTransport:
    """VieNeu /v1/voices fake: pops scripted responses, counts upstream calls.

    Scripts are either a ``list`` of ``data`` entries (→ 200 V7 body) or an
    ``int`` HTTP status (→ that status with a stub error body). Once the
    script is exhausted the LAST scripted response repeats, so extra refresh
    ticks are idempotent for assertions.
    """
    last: list = [None]

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == _UPSTREAM_VOICES_PATH
        calls.append(1)
        if responses:
            last[0] = responses.pop(0)
        script = last[0] if last[0] is not None else []
        if isinstance(script, int):
            return httpx.Response(script, json={"error": {"message": "boom"}})
        return httpx.Response(200, json={"object": "list", "data": script})

    return httpx.MockTransport(handler)


def _app(settings, catalog, transport):
    return create_app(settings, catalog, vieneu_transport=transport)


async def _client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


async def test_voices_route_returns_active_catalog_voices_present_upstream(
    settings, tmp_path
):
    cat = load_catalog(write_catalog(tmp_path, catalog_doc(CATALOG_VOICES)))
    calls: list = []
    transport = _scripted_transport(
        [[{"id": "Hải Đăng"}, {"id": "Ngọc Huyền"}, {"id": "Retired"}]], calls
    )
    app = _app(settings, cat, transport)
    async with app.router.lifespan_context(app):  # warms the cache once
        async with await _client(app) as client:
            resp = await client.get(PROXY_VOICES_PATH, headers=AUTH)
    assert resp.status_code == 200
    assert resp.json() == {
        "voices": [
            {"id": "Hải Đăng", "name": "Hải Đăng", "language": "vi", "gender": "male"},
            {
                "id": "Ngọc Huyền",
                "name": "Ngọc Huyền",
                "language": "en",
                "gender": "female",
            },
        ]
    }


async def test_voices_route_excludes_inactive_even_if_upstream_has_it(
    settings, tmp_path
):
    cat = load_catalog(write_catalog(tmp_path, catalog_doc(CATALOG_VOICES)))
    transport = _scripted_transport([[{"id": "Retired"}]], [])
    app = _app(settings, cat, transport)
    async with app.router.lifespan_context(app):
        async with await _client(app) as client:
            resp = await client.get(PROXY_VOICES_PATH, headers=AUTH)
    assert resp.status_code == 200
    assert resp.json() == {"voices": []}


async def test_voices_route_excludes_alias_only_upstream_match(settings, tmp_path):
    cat = load_catalog(write_catalog(tmp_path, catalog_doc(CATALOG_VOICES)))
    # Upstream has no id "Hải Đăng"; it only appears inside another entry's
    # aliases — F07: an alias never counts as present.
    transport = _scripted_transport(
        [[{"id": "Thiện Minh", "aliases": ["Hải Đăng"]}]], []
    )
    app = _app(settings, cat, transport)
    async with app.router.lifespan_context(app):
        async with await _client(app) as client:
            resp = await client.get(PROXY_VOICES_PATH, headers=AUTH)
    assert resp.status_code == 200
    ids = [v["id"] for v in resp.json()["voices"]]
    assert "Hải Đăng" not in ids
    assert "Thiện Minh" not in ids  # upstream-only names are never returned


async def _assert_401(settings, catalog, headers) -> None:
    transport = _scripted_transport([[{"id": "Hải Đăng"}]], [])
    app = _app(settings, catalog, transport)
    async with app.router.lifespan_context(app):
        async with await _client(app) as client:
            resp = await client.get(PROXY_VOICES_PATH, headers=headers)
    assert resp.status_code == 401
    body = resp.json()
    assert body["error"]["code"] == "proxy_unauthorized"
    assert body["error"]["type"] == "proxy_error"


async def test_voices_route_requires_bearer_401(settings, catalog):
    await _assert_401(settings, catalog, {})


async def test_voices_route_wrong_bearer_401(settings, catalog):
    await _assert_401(settings, catalog, {"Authorization": "Bearer wrong-key"})


async def test_voices_route_never_calls_vieneu_voices_endpoint(settings, catalog):
    calls: list = []
    transport = _scripted_transport([[{"id": "Hải Đăng"}]], calls)
    app = _app(settings, catalog, transport)
    async with app.router.lifespan_context(app):
        after_warm = len(calls)  # cache warm-up already happened
        async with await _client(app) as client:
            resp = await client.get(PROXY_VOICES_PATH, headers=AUTH)
        assert resp.status_code == 200
        assert len(calls) == after_warm  # request path is cache-only
