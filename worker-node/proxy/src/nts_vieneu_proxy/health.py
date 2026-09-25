"""``GET /health`` — the ``proxy-health.v1`` contract (README D8).

``build_health(state)`` is read-only and never raises for a failing check: a
failed check is a *reason* code (D8's closed set), not an exception. ``state``
is the Starlette app state, which carries the same objects the routes use:

- ``vieneu``       → ``VieneuClient`` (live ``GET /health``, fixed 2 s, V7/D8)
- ``voice_cache``  → ``UpstreamVoiceCache`` (the latest refresh decides, D8)
- ``catalog``      → ``Catalog`` (its ids vs the cached upstream name set, D9)
- ``ffmpeg_probe`` → ``FfmpegProbe``, set once before serving (D8/D13)
- ``started_at``   → ``datetime`` (proxy self-info)
- ``active_pipelines`` → the live pipeline ``set`` behind ``proxy.in_flight``

The body carries no secrets (D2) and the HTTP status is 200 only when
``reasons`` is empty; consumers key off the status and ``reasons``, never off
the ``status`` string.
"""

from __future__ import annotations

import re
import subprocess
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from . import __version__
from .errors import ProxyError

PROXY_VERSION = __version__
SCHEMA = "proxy-health.v1"

# D8/D13: one probe at process start, 5 s per command; a missing or hung
# binary never stops the process (health reports the reason instead).
FFMPEG_PROBE_TIMEOUT_S = 5.0
_FFMPEG_VERSION_RE = re.compile(r"ffmpeg version (\S+)")

# D8 reasons, exactly the upstream HTTP status of the live call:
# answering but not healthy is not the same as not being reachable.


@dataclass(frozen=True)
class FfmpegProbe:
    """Result of the one-off startup probe (D8/D13)."""

    ok: bool  # binary found and exited 0
    version: str | None  # None when unparseable — never fails the probe itself
    libmp3lame: bool


def _run_ffmpeg(ffmpeg_path: str, *args: str) -> tuple[str, int]:
    """Run one probe command; never raises. ``("", -1)`` = unusable binary."""
    try:
        proc = subprocess.run(
            [ffmpeg_path, *args],
            capture_output=True,
            timeout=FFMPEG_PROBE_TIMEOUT_S,
        )
    except (OSError, subprocess.SubprocessError):  # missing binary, 5 s timeout
        return "", -1
    return proc.stdout.decode("utf-8", "replace"), proc.returncode


def probe_ffmpeg(ffmpeg_path: str | None) -> FfmpegProbe:
    """The D8 startup probe: ``-version`` (version only) and ``-encoders``.

    ``ok`` = the binary was resolved (``Settings.ffmpeg_path`` is not None)
    and ``-encoders`` exited 0; ``libmp3lame`` is judged separately from its
    stdout. A missing binary → ``FfmpegProbe(False, None, False)`` and the
    process still starts: a missing ffmpeg is not a config error (D8/D13).
    Called before the event loop exists (``__main__``), so it cannot block it.
    """
    if ffmpeg_path is None:
        return FfmpegProbe(ok=False, version=None, libmp3lame=False)
    encoders_out, rc = _run_ffmpeg(ffmpeg_path, "-hide_banner", "-encoders")
    version_out, _ = _run_ffmpeg(ffmpeg_path, "-hide_banner", "-version")
    first_line = version_out.splitlines()[0] if version_out else ""
    match = _FFMPEG_VERSION_RE.search(first_line)
    return FfmpegProbe(
        ok=rc == 0,
        version=match.group(1) if match else None,
        libmp3lame="libmp3lame" in encoders_out,
    )


def _iso_z(value: datetime) -> str:
    """D8 example form: ``2026-09-25T03:00:00Z`` (UTC, seconds, ``Z``)."""
    return (
        value.astimezone(timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z")
    )


def _vieneu_detail(v: dict[str, Any] | None) -> dict[str, Any]:
    """The four V7 fields, present only when the live call answered (D8)."""
    if v is None:
        return {}
    return {key: v.get(key) for key in ("backend", "max_streams", "active", "waiting")}


async def build_health(state: Any) -> tuple[int, dict[str, Any]]:
    """D8: ``(200 | 503, proxy-health.v1 body)`` for the live subsystems."""
    reasons: list[str] = []

    v: dict[str, Any] | None = None
    try:
        # VieNeu /health needs no key (V7): it can only prove reachability.
        v = await state.vieneu.health()
    except ProxyError:
        reasons.append("vieneu_unreachable")
    else:
        if v.get("status") != "ok":
            reasons.append("vieneu_unhealthy")

    probe: FfmpegProbe = state.ffmpeg_probe
    if not probe.ok:
        reasons.append("ffmpeg_missing")
    elif not probe.libmp3lame:
        reasons.append("libmp3lame_missing")

    cache = state.voice_cache
    upstream_names = await cache.names()  # cached; the loop refreshes it
    # D8: the LATEST refresh decides, even while stale names stay cached — a
    # stale cache never masks a failed refresh.
    if cache.last_error == "upstream_auth_failed":
        reasons.append("vieneu_auth_failed")  # the only proof of VIENEU_API_KEY
    elif cache.last_error is not None and "vieneu_unreachable" not in reasons:
        reasons.append("vieneu_unreachable")
    if cache.ever_succeeded:
        # D8: every catalog id (active or not); an alias never counts (F07).
        missing = sorted(vid for vid in state.catalog.ids if vid not in upstream_names)
    else:
        missing = []  # not evaluated before the first successful refresh (D8)
    if missing:
        reasons.append("catalog_voice_missing")
    catalog_ok = cache.ever_succeeded and cache.last_error is None and not missing

    body: dict[str, Any] = {
        "schema": SCHEMA,
        "status": "ok" if not reasons else "error",
        "reasons": reasons,
        # D8: `ok` is the check's verdict, not mere reachability — an
        # answering-but-unhealthy VieNeu is `ok: false` next to
        # `vieneu_unhealthy` (the V7 detail fields are still reported).
        "vieneu": {
            "ok": v is not None and v.get("status") == "ok",
            **_vieneu_detail(v),
        },
        "ffmpeg": {
            "ok": probe.ok,
            "version": probe.version,
            "libmp3lame": probe.libmp3lame,
        },
        "catalog": {
            "ok": catalog_ok,
            "revision": state.catalog.revision,
            "voices": len(state.catalog.ids),
            "missing_upstream": missing,
        },
        "proxy": {
            "version": PROXY_VERSION,
            "started_at": _iso_z(state.started_at),
            # Live read-only snapshot: the pipeline set is owned by the app.
            "in_flight": len(state.active_pipelines),
        },
    }
    return (200 if not reasons else 503), body
