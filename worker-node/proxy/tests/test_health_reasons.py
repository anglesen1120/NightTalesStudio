"""``build_health`` / ffmpeg probe units (04.1.4-T1, README D8).

Direct units for the parts the route tests exercise only through one path:
the ``reasons`` order/dedup matrix, ``missing_upstream`` evaluation rules,
live ``in_flight``, the fixed 2 s upstream call, and the startup probe
(including a real ffmpeg, D14).
"""

from __future__ import annotations

import re
import shutil
import subprocess
from datetime import datetime, timezone

import httpx
import pytest

from nts_vieneu_proxy import __version__, health
from nts_vieneu_proxy.catalog import load_catalog
from nts_vieneu_proxy.errors import ProxyError
from nts_vieneu_proxy.health import FfmpegProbe, build_health, probe_ffmpeg
from nts_vieneu_proxy.upstream import VieneuClient

from .conftest import catalog_doc, voice_entry, write_catalog

STARTED_AT = datetime(2026, 9, 25, 3, 0, 0, tzinfo=timezone.utc)


class _StubVieneu:
    """``VieneuClient.health`` stand-in: a V7 body, or a ProxyError."""

    def __init__(self, result: dict | None = None, exc: BaseException | None = None):
        self.result = result if result is not None else {"status": "ok"}
        self.exc = exc

    async def health(self) -> dict:
        if self.exc is not None:
            raise self.exc
        return self.result


class _StubCache:
    def __init__(self, names=(), last_error=None, ever_succeeded=False):
        self._names = frozenset(names)
        self.last_error = last_error
        self.ever_succeeded = ever_succeeded

    async def names(self) -> frozenset[str]:
        return self._names


class _State:
    """The subset of the app state ``build_health`` reads (D8)."""

    def __init__(
        self,
        catalog,
        *,
        vieneu=None,
        cache=None,
        probe=None,
        active_pipelines=(),
    ) -> None:
        self.vieneu = vieneu or _StubVieneu()
        self.voice_cache = cache or _StubCache(names=catalog.ids, ever_succeeded=True)
        self.catalog = catalog
        self.ffmpeg_probe = probe or FfmpegProbe(True, "9.0.2", True)
        self.started_at = STARTED_AT
        self.active_pipelines = active_pipelines


def _catalog(tmp_path, voices):
    return load_catalog(write_catalog(tmp_path, catalog_doc(voices)))


THREE = [
    voice_entry("Anh Khôi", "anh-khoi", language="vi", gender="male"),
    voice_entry("Hải Đăng", "hai-dang", language="vi", gender="male"),
    voice_entry("Ngọc Huyền", "ngoc-huyen", language="en", gender="female"),
    voice_entry("Retired", "retired", language="vi", gender="female", active=False),
]


async def test_missing_upstream_covers_inactive_ids_and_is_sorted(tmp_path):
    cat = _catalog(tmp_path, THREE)
    state = _State(cat, cache=_StubCache(names=["Hải Đăng"], ever_succeeded=True))
    status, body = await build_health(state)
    assert status == 503
    assert body["reasons"] == ["catalog_voice_missing"]
    # Active or not (D8), sorted: "Retired" is active=false and still listed.
    assert body["catalog"]["missing_upstream"] == ["Anh Khôi", "Ngọc Huyền", "Retired"]
    assert body["catalog"]["voices"] == 4
    assert body["catalog"]["revision"] == 0
    assert body["catalog"]["ok"] is False


async def test_missing_upstream_not_evaluated_before_first_successful_refresh(tmp_path):
    cat = _catalog(tmp_path, THREE)
    state = _State(cat, cache=_StubCache(names=[], last_error="upstream_unavailable"))
    status, body = await build_health(state)
    assert status == 503
    assert body["reasons"] == ["vieneu_unreachable"]
    assert body["catalog"]["missing_upstream"] == []
    assert body["catalog"]["ok"] is False


async def test_vieneu_unreachable_is_not_duplicated_by_a_failed_refresh(tmp_path):
    cat = _catalog(tmp_path, THREE)
    state = _State(
        cat,
        vieneu=_StubVieneu(exc=ProxyError("upstream_unavailable", 502, "down")),
        cache=_StubCache(names=cat.ids, last_error="upstream_unavailable", ever_succeeded=True),
    )
    status, body = await build_health(state)
    assert status == 503
    assert body["reasons"] == ["vieneu_unreachable"]
    assert body["vieneu"] == {"ok": False}


async def test_vieneu_unhealthy_keeps_the_v7_detail_fields(tmp_path):
    cat = _catalog(tmp_path, THREE)
    state = _State(
        cat,
        vieneu=_StubVieneu(
            {"status": "degraded", "backend": "onnx", "max_streams": 2, "active": 1, "waiting": 3}
        ),
    )
    status, body = await build_health(state)
    assert status == 503
    assert body["reasons"] == ["vieneu_unhealthy"]
    assert body["vieneu"] == {
        "ok": False,  # D8: not healthy, even though it answered
        "backend": "onnx",
        "max_streams": 2,
        "active": 1,
        "waiting": 3,
    }


async def test_auth_failed_from_the_cache_alone_fails_health(tmp_path):
    cat = _catalog(tmp_path, THREE)
    state = _State(
        cat,
        cache=_StubCache(names=cat.ids, last_error="upstream_auth_failed", ever_succeeded=True),
    )
    status, body = await build_health(state)
    assert status == 503
    assert body["reasons"] == ["vieneu_auth_failed"]
    assert body["catalog"]["ok"] is False  # a failed refresh never looks OK
    assert body["vieneu"]["ok"] is True  # /health needs no key, so it can pass


@pytest.mark.parametrize(
    "probe,expected",
    [
        (FfmpegProbe(False, None, False), ["ffmpeg_missing"]),
        (FfmpegProbe(False, None, True), ["ffmpeg_missing"]),  # missing wins
        (FfmpegProbe(True, "9.0.2", False), ["libmp3lame_missing"]),
    ],
)
async def test_ffmpeg_probe_flags_set_exactly_one_reason(tmp_path, probe, expected):
    cat = _catalog(tmp_path, THREE)
    status, body = await build_health(_State(cat, probe=probe))
    assert status == 503
    assert body["reasons"] == expected
    assert body["ffmpeg"]["ok"] is probe.ok


async def test_proxy_block_reports_live_in_flight_and_started_at(tmp_path):
    cat = _catalog(tmp_path, THREE)
    active = {"p1", "p2"}
    state = _State(cat, active_pipelines=active)
    _, body = await build_health(state)
    assert body["proxy"]["in_flight"] == 2  # read per call, not at startup
    active.add("p3")
    _, body = await build_health(state)
    assert body["proxy"]["in_flight"] == 3
    assert body["proxy"]["version"] == __version__
    assert body["proxy"]["started_at"] == "2026-09-25T03:00:00Z"


# --- VieneuClient.health (D8: 2 s, non-200 = unhealthy = reachable) ---


def _client(settings, handler):
    return VieneuClient(settings, transport=httpx.MockTransport(handler))


async def test_health_call_uses_the_fixed_two_second_timeout(settings):
    """D8: 2 s on every phase, independent of UPSTREAM_CONNECT_TIMEOUT_S."""
    seen: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["timeout"] = request.extensions.get("timeout")
        return httpx.Response(200, json={"status": "ok"})

    client = _client(settings, handler)
    assert await client.health() == {"status": "ok"}
    assert seen["timeout"] == {"connect": 2.0, "read": 2.0, "write": 2.0, "pool": 2.0}
    assert settings.upstream_connect_timeout_s != 2  # the config value is unrelated


async def test_health_call_returns_the_v7_body(settings):
    body = {"status": "ok", "backend": "onnx", "max_streams": 1, "active": 0, "waiting": 0}
    client = _client(settings, lambda request: httpx.Response(200, json=body))
    assert await client.health() == body


@pytest.mark.parametrize("status", [500, 503, 401])
async def test_health_call_maps_a_non_200_to_unhealthy_not_unreachable(settings, status):
    # A response proves reachability; D8 makes that `vieneu_unhealthy`.
    client = _client(settings, lambda request: httpx.Response(status, json={"status": "error"}))
    assert await client.health() == {"status": "error"}


@pytest.mark.parametrize(
    "exc",
    [httpx.ConnectError("refused"), httpx.ReadTimeout("too slow")],
)
async def test_health_call_maps_connect_and_timeout_to_unreachable(settings, exc):
    def handler(request):
        raise exc

    with pytest.raises(ProxyError) as err:
        await _client(settings, handler).health()
    assert err.value.code == "upstream_unavailable"


async def test_health_call_maps_a_non_object_body_to_unhealthy(settings):
    # The server answered, so it is reachable; a body that is not a V7 object
    # cannot be "ok" (D8).
    client = _client(settings, lambda request: httpx.Response(200, content=b"[1, 2]"))
    assert await client.health() == {"status": "error"}
    bad_json = _client(
        settings, lambda request: httpx.Response(200, content=b"{not json")
    )
    assert await bad_json.health() == {"status": "error"}


# --- probe_ffmpeg (D8/D13: one run at startup, never fatal) ---


def test_probe_ffmpeg_reports_real_ffmpeg():
    path = shutil.which("ffmpeg")
    if path is None:
        pytest.skip("ffmpeg is not installed on this machine")
    probe = probe_ffmpeg(path)
    assert probe.ok is True
    assert probe.libmp3lame is True
    assert re.fullmatch(r"\d+\.\d+[\w.\-]*", probe.version or "")  # "9.0.2"-shaped


def test_probe_ffmpeg_missing_binary_is_not_an_error():
    assert probe_ffmpeg(None) == FfmpegProbe(ok=False, version=None, libmp3lame=False)
    # A path that does not exist must not raise either (D8: no exit).
    assert probe_ffmpeg("/nonexistent/ffmpeg-binary") == FfmpegProbe(
        ok=False, version=None, libmp3lame=False
    )


def test_probe_ffmpeg_timeout_is_not_ok(monkeypatch):
    def boom(*args, **kwargs):
        raise subprocess.TimeoutExpired(cmd="ffmpeg", timeout=health.FFMPEG_PROBE_TIMEOUT_S)

    monkeypatch.setattr(health.subprocess, "run", boom)
    assert probe_ffmpeg("ffmpeg") == FfmpegProbe(ok=False, version=None, libmp3lame=False)


def test_probe_ffmpeg_unparseable_version_does_not_fail_the_probe(monkeypatch):
    class _Proc:
        returncode = 0
        stdout = b"something else entirely\nlibmp3lame  whatever\n"

    monkeypatch.setattr(health.subprocess, "run", lambda *a, **k: _Proc())
    assert probe_ffmpeg("ffmpeg") == FfmpegProbe(ok=True, version=None, libmp3lame=True)
