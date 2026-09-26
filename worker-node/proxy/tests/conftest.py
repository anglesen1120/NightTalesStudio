"""Shared fixtures and helpers (D14 test kit).

No secrets are read: dummy keys ≥ 32 chars only. Catalog fixtures are written
to tmp_path; the rev-0 seed file is loaded once per session from the repo.
"""

from __future__ import annotations

import json
import math
import struct
import sys
from dataclasses import replace
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from nts_vieneu_proxy.catalog import load_catalog  # noqa: E402
from nts_vieneu_proxy.config import Settings  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[3]
SEED_FILE = REPO_ROOT / "seed" / "voices.v1.json"

DUMMY_KEY = "k" * 40
VOICE_VI = "Hải Đăng"
VOICE_EN = "Ngọc Huyền"


def base_env(tmp_path: Path, catalog_path: Path | None = None) -> dict[str, str]:
    """Minimal valid D13 environment."""
    return {
        "PROXY_API_KEY": DUMMY_KEY,
        "VIENEU_API_KEY": "v" * 40,
        "VIENEU_BASE_URL": "http://127.0.0.1:8000",
        "VOICE_CATALOG_FILE": str(catalog_path or SEED_FILE),
    }


@pytest.fixture
def env(tmp_path: Path) -> dict[str, str]:
    return base_env(tmp_path)


@pytest.fixture
def catalog(tmp_path: Path):
    """A rev-0 catalog: both seed voices active."""
    return load_catalog(SEED_FILE)


PROXY_TESTS_DIR = Path(__file__).resolve().parent


def pytest_configure(config: pytest.Config) -> None:
    """Force pytest-asyncio auto mode.

    The pinned D14 command runs pytest from the repo root, where
    worker-node/proxy/pyproject.toml (``asyncio_mode = "auto"``,
    ``testpaths = ["tests"]``) is not discovered. pytest-asyncio >= 1.4 only
    converts a coroutine test into an async item at collection time
    (``pytest_pycollect_makeitem``), keyed off the marker **or** the mode, so
    adding ``@pytest.mark.asyncio`` after collection cannot work; the mode
    option must be set here, before this directory is collected.
    """
    config.option.asyncio_mode = "auto"


def pytest_collection_modifyitems(config: pytest.Config, items: list) -> None:
    """Scope the session to this suite when no pytest config was discovered.

    That is the D14 root invocation (``uv run --project worker-node/proxy
    pytest -q`` run from the repo root): pytest is invoked with no path, so it
    walks the whole repo and would also pull in unrelated sibling test trees.
    """
    if config.inifile is not None:
        return
    items[:] = [
        item
        for item in items
        if Path(str(item.path)).resolve().is_relative_to(PROXY_TESTS_DIR)
    ]


@pytest.fixture
def settings(env, tmp_path: Path) -> Settings:
    s = Settings.from_env(env)
    # Shrink the commit deadline so timeout tests stay fast; bypasses the
    # 10 s floor, which is a startup rule, not a pipeline invariant.
    return replace(s, first_audio_deadline_s=90)


def fast_settings(s: Settings, **over) -> Settings:
    return replace(s, **over)


def sine_pcm(seconds: float, rate: int = 48000, freq: float = 440.0) -> bytes:
    """16-bit little-endian mono sine PCM, generated in-test (D14)."""
    n = int(seconds * rate)
    frames = (
        struct.pack("<h", int(12000 * math.sin(2 * math.pi * freq * i / rate)))
        for i in range(n)
    )
    return b"".join(frames)


def write_catalog(tmp_path: Path, doc: dict) -> Path:
    p = tmp_path / "voices.test.json"
    p.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
    return p


def catalog_doc(voices: list[dict], **file_over) -> dict:
    doc = {
        "schema": "voices.v1",
        "revision": 0,
        "vieneuCommit": "c" * 40,
        "voices": voices,
    }
    doc.update(file_over)
    return doc


def voice_entry(id: str, slug: str, **over) -> dict:
    entry = {
        "id": id,
        "slug": slug,
        "displayName": id,
        "language": "vi",
        "gender": "male",
        "tags": [],
        "active": True,
        "previewStorageKey": None,
    }
    entry.update(over)
    return entry
