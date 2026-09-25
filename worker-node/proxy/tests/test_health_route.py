"""``GET /health`` (04.1.4-T1): proxy-health.v1 over the real app (README D8).

Every reason code is exercised end to end through ``create_app`` + ASGI, with
a scripted VieNeu (MockTransport) and an injected ``FfmpegProbe``. The D8
frontier is covered explicitly: a failed *voice refresh* is not masked by
stale cached names, and ``catalog_voice_missing`` is only evaluated after one
successful refresh.
"""

from __future__ import annotations

import asyncio
import json
from contextlib import asynccontextmanager
from dataclasses import replace

import httpx

from nts_vieneu_proxy import __version__
from nts_vieneu_proxy.app import create_app
from nts_vieneu_proxy.catalog import load_catalog
from nts_vieneu_proxy.health import FfmpegProbe

from .conftest import DUMMY_KEY, catalog_doc, voice_entry, write_catalog

HEALTH_PATH = "/health"  # the proxy's own route (D2)
VIENEU_KEY = "v" * 40  # conftest.base_env's VIENEU_API_KEY
TICK_S = 0.02  # fake clock: far below CATALOG_CHECK_INTERVAL_S's 30 s floor
TICKS = TICK_S * 6  # ≥ 2 background refreshes have landed

CATALOG_VOICES = [
    voice_entry("Hải Đăng", "hai-dang", language="vi", gender="male"),
    voice_entry("Ngọc Huyền", "ngoc-huyen", language="en", gender="female"),
]
UPSTREAM_ALL = [{"id": "Hải Đăng"}, {"id": "Ngọc Huyền"}]

HEALTH_OK_BODY = {
    "status": "ok",
    "backend": "onnx",
    "max_streams": 1,
    "active": 0,
    "waiting": 0,
    "sample_rate": 48000,
}
FFMPEG_OK = FfmpegProbe(ok=True, version="9.0.2", libmp3lame=True)


class FakeVieneu:
    """Scripted VieNeu for ``/health`` and ``/v1/voices`` (D14, MockTransport).

    ``voices_script`` entries are a ``data`` list (200), an ``int`` status, or
    an exception instance (raised); once exhausted the last entry repeats, so
    extra background refresh ticks stay idempotent for assertions.
    """

    def __init__(
        self,
        *,
        health_status: int = 200,
        health_body: dict | None = None,
        health_exc: BaseException | None = None,
        voices_script: list | None = None,
    ) -> None:
        self.health_status = health_status
        self.health_body = HEALTH_OK_BODY if health_body is None else health_body
        self.health_exc = health_exc
        self.voices_script = list(voices_script) if voices_script is not None else [UPSTREAM_ALL]
        self._last: object = []
        self.calls: list[str] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        path = request.url.path
        self.calls.append(path)
        if path == "/health":  # VieNeu's own health (V7)
            if self.health_exc is not None:
                raise self.health_exc
            return httpx.Response(self.health_status, json=self.health_body)
        assert path == "/v1/voices", path
        script = self.voices_script.pop(0) if self.voices_script else self._last
        if not isinstance(script, BaseException):
            self._last = script
        if isinstance(script, BaseException):
            raise script
        if isinstance(script, int):
            return httpx.Response(script, json={"error": {"message": "boom"}})
        return httpx.Response(200, json={"object": "list", "data": script})

    def transport(self) -> httpx.MockTransport:
        return httpx.MockTransport(self.handler)


def _settings(settings):
    """Shrink the refresh interval (a startup rule) so ticks happen in-test."""
    return replace(settings, catalog_check_interval_s=TICK_S)


def _app(settings, catalog, fake: FakeVieneu, probe: FfmpegProbe = FFMPEG_OK):
    app = create_app(settings, catalog, vieneu_transport=fake.transport())
    # __main__ sets this before uvicorn starts; tests inject the probe.
    app.state.ffmpeg_probe = probe
    return app


@asynccontextmanager
async def _client(app):
    async with app.router.lifespan_context(app):  # warms the voice cache
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://proxy"
        ) as client:
            yield client


def _catalog(tmp_path, voices=CATALOG_VOICES):
    return load_catalog(write_catalog(tmp_path, catalog_doc(voices)))


async def test_health_ok_returns_200_and_empty_reasons(settings, tmp_path):
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu())
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert body["reasons"] == []
    assert body["catalog"]["ok"] is True
    assert body["catalog"]["missing_upstream"] == []
    assert body["proxy"]["in_flight"] == 0


async def test_health_body_schema_field_exact(settings, tmp_path):
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu())
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    body = resp.json()
    assert body["schema"] == "proxy-health.v1"  # literal (D8)
    started_at = body["proxy"]["started_at"]
    assert isinstance(started_at, str) and started_at.endswith("Z")
    assert body == {
        "schema": "proxy-health.v1",
        "status": "ok",
        "reasons": [],
        "vieneu": {
            "ok": True,
            "backend": "onnx",
            "max_streams": 1,
            "active": 0,
            "waiting": 0,
        },
        "ffmpeg": {"ok": True, "version": "9.0.2", "libmp3lame": True},
        "catalog": {"ok": True, "revision": 0, "voices": 2, "missing_upstream": []},
        "proxy": {"version": __version__, "started_at": started_at, "in_flight": 0},
    }


async def test_health_vieneu_unreachable_reason_and_503(settings, tmp_path):
    fake = FakeVieneu(health_exc=httpx.ConnectError("refused"))
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert body["status"] == "error"
    assert "vieneu_unreachable" in body["reasons"]
    assert body["vieneu"] == {"ok": False}
    assert body["catalog"]["ok"] is True  # the voice list is a separate check


async def test_health_vieneu_unhealthy_reason(settings, tmp_path):
    fake = FakeVieneu(health_body={"status": "error", "backend": "onnx"})
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert body["reasons"] == ["vieneu_unhealthy"]
    assert body["vieneu"]["ok"] is False  # D8: not healthy, though reachable
    assert body["vieneu"]["backend"] == "onnx"  # the V7 detail is still reported


async def test_health_vieneu_non_200_is_unhealthy_not_unreachable(settings, tmp_path):
    fake = FakeVieneu(health_status=500)
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert body["reasons"] == ["vieneu_unhealthy"]
    assert "vieneu_unreachable" not in body["reasons"]


async def test_health_voice_refresh_401_reports_vieneu_auth_failed(settings, tmp_path):
    fake = FakeVieneu(
        voices_script=[401], health_body=dict(HEALTH_OK_BODY)  # /health stays 200
    )
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert "vieneu_auth_failed" in body["reasons"]
    assert "vieneu_unreachable" not in body["reasons"]
    assert body["catalog"]["ok"] is False


async def test_health_auth_failure_after_good_refresh_not_masked_by_stale_cache(
    settings, tmp_path
):
    fake = FakeVieneu(voices_script=[UPSTREAM_ALL, 401])
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        await asyncio.sleep(TICKS)  # the 401 refresh has landed
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert "vieneu_auth_failed" in body["reasons"]
    assert "catalog_voice_missing" not in body["reasons"]
    # The stale name set is still served (D8) — but it never masks the failure.
    assert await app.state.voice_cache.names() == {"Hải Đăng", "Ngọc Huyền"}
    assert body["catalog"]["ok"] is False
    assert body["catalog"]["missing_upstream"] == []


async def test_health_voice_list_never_fetched_reports_unreachable_not_catalog_invalid(
    settings, tmp_path
):
    fake = FakeVieneu(voices_script=[httpx.ConnectError("refused")])
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert "vieneu_unreachable" in body["reasons"]
    assert "catalog_voice_missing" not in body["reasons"]
    assert "catalog_invalid" not in body["reasons"]
    assert body["catalog"]["ok"] is False
    assert body["catalog"]["missing_upstream"] == []


async def test_health_ffmpeg_missing_reason(settings, tmp_path):
    probe = FfmpegProbe(ok=False, version=None, libmp3lame=False)
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu(), probe)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert body["reasons"] == ["ffmpeg_missing"]
    assert body["ffmpeg"] == {"ok": False, "version": None, "libmp3lame": False}


async def test_health_libmp3lame_missing_reason(settings, tmp_path):
    probe = FfmpegProbe(ok=True, version="9.0.2", libmp3lame=False)
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu(), probe)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert body["reasons"] == ["libmp3lame_missing"]
    assert body["ffmpeg"]["ok"] is True  # the binary itself is fine


async def test_health_catalog_voice_missing_reason_lists_ids(settings, tmp_path):
    fake = FakeVieneu(voices_script=[[{"id": "Hải Đăng"}]])
    app = _app(_settings(settings), _catalog(tmp_path), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert "catalog_voice_missing" in body["reasons"]
    assert body["catalog"]["missing_upstream"] == ["Ngọc Huyền"]
    assert body["catalog"]["ok"] is False


async def test_health_catalog_voice_missing_ignores_aliases(settings, tmp_path):
    voices = [voice_entry("Hải Đăng", "hai-dang", language="vi", gender="male")]
    # Upstream carries the id only inside another entry's aliases (F07/D9).
    fake = FakeVieneu(voices_script=[[{"id": "Thiện Minh", "aliases": ["Hải Đăng"]}]])
    app = _app(_settings(settings), _catalog(tmp_path, voices), fake)
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    body = resp.json()
    assert "catalog_voice_missing" in body["reasons"]
    assert body["catalog"]["missing_upstream"] == ["Hải Đăng"]


async def test_health_multiple_reasons_combine(settings, tmp_path):
    fake = FakeVieneu(
        health_exc=httpx.ConnectError("refused"), voices_script=[401]
    )
    probe = FfmpegProbe(ok=False, version=None, libmp3lame=False)
    app = _app(_settings(settings), _catalog(tmp_path), fake, probe)
    async with _client(app) as client:
        await asyncio.sleep(TICKS)
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 503
    reasons = resp.json()["reasons"]
    assert set(reasons) == {"vieneu_unreachable", "ffmpeg_missing", "vieneu_auth_failed"}
    assert reasons.count("vieneu_unreachable") == 1


async def test_health_never_requires_bearer(settings, tmp_path):
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu())
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)  # no Authorization header at all
    assert resp.status_code == 200
    assert resp.json()["schema"] == "proxy-health.v1"


async def test_health_response_never_contains_api_keys_or_input_text(settings, tmp_path):
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu())
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    body = resp.json()
    text = resp.text
    assert DUMMY_KEY not in text  # PROXY_API_KEY
    assert VIENEU_KEY not in text  # VIENEU_API_KEY
    assert "bearer" not in text.lower()
    assert set(body) == {"schema", "status", "reasons", "vieneu", "ffmpeg", "catalog", "proxy"}


async def test_health_emits_one_d7_line_per_request(settings, tmp_path, capsys):
    app = _app(_settings(settings), _catalog(tmp_path), FakeVieneu())
    async with _client(app) as client:
        resp = await client.get(HEALTH_PATH)
    assert resp.status_code == 200
    lines = [
        json.loads(line) for line in capsys.readouterr().out.splitlines() if line.startswith("{")
    ]
    health_lines = [line for line in lines if line.get("route") == "/health"]
    assert len(health_lines) == 1  # D7: one line per request, every route
    line = health_lines[0]
    assert line["method"] == "GET"
    assert line["status"] == 200
    assert line["http_status"] == 200
    assert isinstance(line["wall_ms"], int)
    assert line["error_code"] is None
