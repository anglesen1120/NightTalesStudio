"""D2 route allow-list tests (04.1.2-T2): every non-D2 path is a 404
``route_not_found``, a wrong method on a D2 path is a 405
``method_not_allowed`` + ``Allow``, docs/openapi are gone, and rejections
emit a D7 line with ``route`` = "other"/route name — never the raw path."""

from __future__ import annotations

import json

import httpx

from nts_vieneu_proxy.app import create_app

from .conftest import DUMMY_KEY, sine_pcm
from .fakes import FakeVieneu

AUTH = {"Authorization": f"Bearer {DUMMY_KEY}"}


async def _client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


def _logs(capsys) -> list[dict]:
    return [
        json.loads(line)
        for line in capsys.readouterr().out.splitlines()
        if line.strip().startswith("{")
    ]


async def test_allow_list_unknown_path_404_route_not_found_no_upstream_call(
    settings, catalog, capsys
):
    fake = FakeVieneu(chunks=[sine_pcm(0.05)])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.get("/v1/models", headers=AUTH)
    assert resp.status_code == 404
    assert resp.json()["error"]["code"] == "route_not_found"
    assert fake.call_count == 0


async def test_allow_list_post_v1_voices_404_no_upstream_call(
    settings, catalog
):
    """POST /v1/voices — VieNeu's own upstream route name — does not exist
    on the proxy (D2: no code path to it)."""
    fake = FakeVieneu(chunks=[sine_pcm(0.05)])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.post("/v1/voices", json={}, headers=AUTH)
    assert resp.status_code == 404
    assert resp.json()["error"]["code"] == "route_not_found"
    assert fake.call_count == 0


async def test_allow_list_get_speech_405_with_allow_post(settings, catalog):
    fake = FakeVieneu(chunks=[sine_pcm(0.05)])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.get("/v1/audio/speech", headers=AUTH)
    assert resp.status_code == 405
    assert resp.headers["allow"] == "POST"
    assert resp.json()["error"]["code"] == "method_not_allowed"
    assert fake.call_count == 0


async def test_allow_list_docs_redoc_openapi_404(settings, catalog):
    fake = FakeVieneu(chunks=[sine_pcm(0.05)])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        for path in ("/docs", "/redoc", "/openapi.json"):
            resp = await c.get(path)
            assert resp.status_code == 404, path
            assert resp.json()["error"]["code"] == "route_not_found"
    assert fake.call_count == 0


async def test_allow_list_404_logs_route_other_without_raw_path(
    settings, catalog, capsys
):
    secretish = "/admin/drop-tables?key=supersecret"
    fake = FakeVieneu(chunks=[sine_pcm(0.05)])
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    async with await _client(app) as c:
        resp = await c.get(secretish, headers=AUTH)
    assert resp.status_code == 404
    lines = _logs(capsys)
    hit = [l for l in lines if l.get("error_code") == "route_not_found"]
    assert hit and hit[-1]["route"] == "other"
    assert hit[-1]["method"] == "GET"
    assert hit[-1]["status"] == 404 and hit[-1]["http_status"] == 404
    # The raw path (incl. query) is never logged (D7).
    out = json.dumps(lines)
    assert "drop-tables" not in out and "supersecret" not in out
