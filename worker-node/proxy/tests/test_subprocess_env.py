"""D13.1 (04 README): no ffmpeg child may inherit a proxy credential.

The proxy process holds ``PROXY_API_KEY`` and ``VIENEU_API_KEY`` in its own
environment; both spawn sites (the D4 encoder in ``transcode.Mp3Encoder`` and
the D8 startup probe in ``health._run_ffmpeg``) must hand ffmpeg a minimal
allow-list env instead. The fake ffmpeg below writes its real child env to a
file, so the assertion is on what the kernel actually passed, not on our own
helper.
"""

from __future__ import annotations

import os
import re
import shlex
from pathlib import Path

from nts_vieneu_proxy.config import Settings
from nts_vieneu_proxy.health import probe_ffmpeg
from nts_vieneu_proxy.transcode import Mp3Encoder, child_env

from .conftest import base_env

# Distinctive, printable-ASCII, >= 32 chars: printable + no space is what
# D13/P1 accepts for bearer tokens.
PROXY_SECRET = "sk-proxy-" + "A" * 40
VIENEU_SECRET = "vk-vieneu-" + "B" * 40
MARKER_NAME = "NTS_TEST_MARKER"
MARKER_VALUE = "marker-value-must-not-leak"
RELAY_TOKEN = "relay-token-must-not-leak"

_CREDENTIAL_KEY_RE = re.compile(r"(?i)(key|token|secret|password)")
_SECRETS = (PROXY_SECRET, VIENEU_SECRET, MARKER_VALUE, RELAY_TOKEN)


def _write_fake_ffmpeg(tmp_path: Path, encoder_dump: Path, probe_dump: Path) -> str:
    """Executable stub that dumps its env (encoder vs probe, by argv)."""
    script = tmp_path / "fake-ffmpeg"
    script.write_text(
        "#!/bin/sh\n"
        'case "$*" in\n'
        f"  *-encoders*|*-version*) out={shlex.quote(str(probe_dump))} ;;\n"
        f"  *) out={shlex.quote(str(encoder_dump))} ;;\n"
        "esac\n"
        'env > "$out"\n'
        'echo "ffmpeg version fake-9.9.9"\n'
        "exit 0\n",
        encoding="utf-8",
    )
    script.chmod(0o755)
    return str(script)


def _dump_lines(text: str) -> dict[str, str]:
    lines = {}
    for line in text.splitlines():
        if "=" in line:
            name, value = line.split("=", 1)
            lines[name] = value
    return lines


def _assert_clean(label: str, text: str) -> dict[str, str]:
    dumped = _dump_lines(text)
    # Not vacuous: PATH survived the allow-list.
    assert "PATH" in dumped, f"{label}: fake ffmpeg dumped no PATH: {text!r}"
    for name in dumped:
        assert not _CREDENTIAL_KEY_RE.search(name), f"{label}: credential key leaked: {name}"
    for secret in _SECRETS:
        assert secret not in text, f"{label}: secret travelled to the child: {secret}"
    for name in ("PROXY_API_KEY", "VIENEU_API_KEY", MARKER_NAME, "RELAY_TOKEN"):
        assert name not in dumped, f"{label}: {name} reached the child"
    return dumped


def _settings_with_fake(tmp_path: Path, monkeypatch, fake: str) -> Settings:
    """Real Settings path, with the D13 secrets actually exported."""
    env = dict(base_env(tmp_path))
    env["FFMPEG_PATH"] = fake
    env["PROXY_API_KEY"] = PROXY_SECRET
    env["VIENEU_API_KEY"] = VIENEU_SECRET
    env[MARKER_NAME] = MARKER_VALUE
    env[RELAY_TOKEN] = RELAY_TOKEN
    for name, value in env.items():
        monkeypatch.setenv(name, value)
    return Settings.from_env()


def test_child_env_is_allowlist_only(monkeypatch):
    monkeypatch.setenv("PROXY_API_KEY", PROXY_SECRET)
    monkeypatch.setenv("VIENEU_API_KEY", VIENEU_SECRET)
    monkeypatch.setenv(MARKER_NAME, MARKER_VALUE)
    monkeypatch.setenv(RELAY_TOKEN, RELAY_TOKEN)

    child = child_env()

    assert set(child) <= {"PATH", "HOME", "LANG", "LC_ALL", "TMPDIR"}
    assert "PATH" in child  # present in any real pytest process
    for name in ("PROXY_API_KEY", "VIENEU_API_KEY", MARKER_NAME, RELAY_TOKEN):
        assert name not in child
    # child_env() is a copy, never the live mapping.
    assert child is not os.environ
    assert child["PATH"] == os.environ["PATH"]


async def test_encoder_spawn_env_excludes_secrets(tmp_path, monkeypatch):
    encoder_dump = tmp_path / "encoder.env"
    probe_dump = tmp_path / "probe.env"
    fake = _write_fake_ffmpeg(tmp_path, encoder_dump, probe_dump)
    settings = _settings_with_fake(tmp_path, monkeypatch, fake)
    assert settings.ffmpeg_path == fake

    enc = Mp3Encoder(settings.ffmpeg_path, settings.mp3_bitrate)
    await enc.start()
    try:
        await enc.wait()
    finally:
        await enc.kill()

    _assert_clean("encoder", encoder_dump.read_text(encoding="utf-8"))


def test_probe_spawn_env_excludes_secrets(tmp_path, monkeypatch):
    encoder_dump = tmp_path / "encoder.env"
    probe_dump = tmp_path / "probe.env"
    fake = _write_fake_ffmpeg(tmp_path, encoder_dump, probe_dump)
    settings = _settings_with_fake(tmp_path, monkeypatch, fake)
    assert settings.ffmpeg_path == fake

    probe = probe_ffmpeg(settings.ffmpeg_path)

    # The stub answers `ffmpeg version fake-9.9.9` on every invocation.
    assert probe.ok
    assert probe.version == "fake-9.9.9"
    _assert_clean("probe", probe_dump.read_text(encoding="utf-8"))
    assert not encoder_dump.exists()
