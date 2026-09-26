"""Settings.from_env — D13 table validation."""

from __future__ import annotations

import pytest

from nts_vieneu_proxy.config import Settings
from nts_vieneu_proxy.errors import ConfigError


def test_config_rejects_non_loopback_host(env):
    for bad in ("0.0.0.0", "localhost", "::1", "192.168.1.5"):
        with pytest.raises(ConfigError) as ei:
            Settings.from_env({**env, "PROXY_HOST": bad})
        assert str(ei.value) == "invalid config: PROXY_HOST"


def test_config_rejects_short_keys_without_echoing_value(env):
    for var, val in (("PROXY_API_KEY", "short-secret"), ("VIENEU_API_KEY", "x" * 31)):
        with pytest.raises(ConfigError) as ei:
            Settings.from_env({**env, var: val})
        assert str(ei.value) == f"invalid config: {var}"
        assert val not in str(ei.value)
        assert val not in repr(ei.value)


def test_config_rejects_bad_key_charset(env):
    """P1: keys go into HTTP headers — non-ASCII, non-printable and
    space-containing values are ConfigError(<VAR>), never echoing the value."""
    for var in ("PROXY_API_KEY", "VIENEU_API_KEY"):
        for bad in ("k" * 31 + "é", "k" * 39 + " ", "k" * 39 + "\t"):
            with pytest.raises(ConfigError) as ei:
                Settings.from_env({**env, var: bad})
            assert str(ei.value) == f"invalid config: {var}"
            assert bad not in str(ei.value)
            assert bad not in repr(ei.value)


def test_config_first_audio_deadline_must_exceed_admission_wait(env):
    with pytest.raises(ConfigError) as ei:
        Settings.from_env(
            {**env, "FIRST_AUDIO_DEADLINE_S": "60", "ADMISSION_WAIT_S": "60"}
        )
    assert str(ei.value) == "invalid config: FIRST_AUDIO_DEADLINE_S"
    with pytest.raises(ConfigError):
        Settings.from_env(
            {**env, "FIRST_AUDIO_DEADLINE_S": "40", "ADMISSION_WAIT_S": "60"}
        )


def test_config_rejects_vieneu_base_url_with_path(env):
    for bad in (
        "http://127.0.0.1:8000/v1",
        "http://127.0.0.1:8000/",
        "https://127.0.0.1:8000",
        "http://localhost:8000",
        "http://0.0.0.0:8000",
        "http://127.0.0.1:8000x",
    ):
        with pytest.raises(ConfigError) as ei:
            Settings.from_env({**env, "VIENEU_BASE_URL": bad})
        assert str(ei.value) == "invalid config: VIENEU_BASE_URL"


def test_config_defaults(env):
    s = Settings.from_env(env)
    assert s.proxy_host == "127.0.0.1"
    assert s.proxy_port == 8001
    assert s.mp3_bitrate == "192k"
    assert s.upstream_connect_timeout_s == 5
    assert s.first_audio_deadline_s == 90
    assert s.proxy_max_concurrent == 4
    assert s.ffmpeg_path is not None  # ffmpeg exists on this machine


def test_config_bad_int_and_bitrate(env):
    with pytest.raises(ConfigError) as ei:
        Settings.from_env({**env, "PROXY_MAX_CONCURRENT": "lots"})
    assert str(ei.value) == "invalid config: PROXY_MAX_CONCURRENT"
    with pytest.raises(ConfigError) as ei:
        Settings.from_env({**env, "MP3_BITRATE": "64k"})
    assert str(ei.value) == "invalid config: MP3_BITRATE"


def test_config_ffmpeg_missing_is_not_config_error(env, monkeypatch):
    monkeypatch.setenv("PATH", "/nonexistent-dir")
    s = Settings.from_env({**env, "FFMPEG_PATH": "definitely-not-ffmpeg-xyz"})
    assert s.ffmpeg_path is None
