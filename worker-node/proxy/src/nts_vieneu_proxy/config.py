"""Environment configuration (README D13 table).

Every failure raises ``ConfigError(<VAR>)`` so ``str(err)`` is exactly
``invalid config: <VAR>`` — the variable name, never its value. The process
entry point prints that and exits 2.

`ffmpeg_path` is the exception: a missing binary is *not* a ConfigError (D8).
``Settings.ffmpeg_path`` is ``None`` then; health reports ``ffmpeg_missing``
and speech answers 500 ``transcode_failed`` at request time.
"""

from __future__ import annotations

import os
import re
import shutil
from dataclasses import dataclass
from typing import Mapping

from .errors import ConfigError

_VIENEU_BASE_URL_RE = re.compile(r"^http://127\.0\.0\.1:\d{1,5}$")
_MP3_BITRATES = ("128k", "160k", "192k")

# (attr, env, default, min, max): int scalars from the D13 table.
_INTS = (
    ("proxy_port", "PROXY_PORT", "8001", 1, 65535),
    ("upstream_connect_timeout_s", "UPSTREAM_CONNECT_TIMEOUT_S", "5", 1, 30),
    ("upstream_read_gap_s", "UPSTREAM_READ_GAP_S", "60", 10, 100),
    ("first_audio_deadline_s", "FIRST_AUDIO_DEADLINE_S", "90", 10, 100),
    ("admission_wait_s", "ADMISSION_WAIT_S", "80", 0, 80),
    ("request_max_s", "REQUEST_MAX_S", "1800", 60, 3600),
    ("proxy_max_concurrent", "PROXY_MAX_CONCURRENT", "4", 1, 32),
    ("catalog_check_interval_s", "CATALOG_CHECK_INTERVAL_S", "300", 30, 3600),
)


@dataclass(frozen=True)
class Settings:
    proxy_host: str
    proxy_port: int
    proxy_api_key: str
    vieneu_base_url: str
    vieneu_api_key: str
    voice_catalog_file: str
    mp3_bitrate: str
    upstream_connect_timeout_s: int
    upstream_read_gap_s: int
    first_audio_deadline_s: int
    admission_wait_s: int
    request_max_s: int
    proxy_max_concurrent: int
    catalog_check_interval_s: int
    ffmpeg_path: str | None

    @classmethod
    def from_env(cls, environ: Mapping[str, str] | None = None) -> "Settings":
        env = os.environ if environ is None else environ

        host = env.get("PROXY_HOST", "127.0.0.1")
        if host != "127.0.0.1":
            raise ConfigError("PROXY_HOST")

        values: dict[str, int] = {}
        for attr, var, default, lo, hi in _INTS:
            raw = env.get(var, default)
            try:
                n = int(raw, 10)
            except ValueError:
                raise ConfigError(var) from None
            if not (lo <= n <= hi):
                raise ConfigError(var)
            values[attr] = n

        if values["first_audio_deadline_s"] <= values["admission_wait_s"]:
            raise ConfigError("FIRST_AUDIO_DEADLINE_S")

        proxy_api_key = env.get("PROXY_API_KEY")
        if proxy_api_key is None or len(proxy_api_key) < 32:
            raise ConfigError("PROXY_API_KEY")

        vieneu_base_url = env.get("VIENEU_BASE_URL", "http://127.0.0.1:8000")
        if not _VIENEU_BASE_URL_RE.fullmatch(vieneu_base_url):
            raise ConfigError("VIENEU_BASE_URL")

        vieneu_api_key = env.get("VIENEU_API_KEY")
        if vieneu_api_key is None or len(vieneu_api_key) < 32:
            raise ConfigError("VIENEU_API_KEY")
        for var, key in (
            ("PROXY_API_KEY", proxy_api_key),
            ("VIENEU_API_KEY", vieneu_api_key),
        ):
            # Bearer tokens go into HTTP headers; only printable ASCII without
            # space is legal (rejects non-ASCII, control chars, spaces) (P1).
            if not all(0x21 <= ord(c) <= 0x7E for c in key):
                raise ConfigError(var)

        voice_catalog_file = env.get("VOICE_CATALOG_FILE")
        if not voice_catalog_file:
            raise ConfigError("VOICE_CATALOG_FILE")

        mp3_bitrate = env.get("MP3_BITRATE", "192k")
        if mp3_bitrate not in _MP3_BITRATES:
            raise ConfigError("MP3_BITRATE")

        ffmpeg_path = shutil.which(env.get("FFMPEG_PATH", "ffmpeg"))

        return cls(
            proxy_host=host,
            proxy_api_key=proxy_api_key,
            vieneu_base_url=vieneu_base_url,
            vieneu_api_key=vieneu_api_key,
            voice_catalog_file=voice_catalog_file,
            mp3_bitrate=mp3_bitrate,
            ffmpeg_path=ffmpeg_path,
            **values,
        )
