"""Data-driven D14 fixture suite for the voice catalog loader (04.2.2-T1).

`seed/fixtures/voices/` holds one fixture per D9 rule; `expected.json` is the
machine-readable mapping file → {code, voiceId}. The same files are fed to the
C# `VoiceCatalogRules` suite (04.2.2-T2), so both readers are tested against
the identical bytes.
"""

from __future__ import annotations

import json
import unicodedata
from pathlib import Path

import pytest

from nts_vieneu_proxy.catalog import load_catalog
from nts_vieneu_proxy.errors import CatalogError

FIXTURE_DIR = Path(__file__).resolve().parents[3] / "seed" / "fixtures" / "voices"
EXPECTED = json.loads((FIXTURE_DIR / "expected.json").read_text(encoding="utf-8"))
SEED_FILE = Path(__file__).resolve().parents[3] / "seed" / "voices.v1.json"


def _all_fixture_files() -> list[Path]:
    return [
        p for p in sorted(FIXTURE_DIR.glob("*.json")) if p.name != "expected.json"
    ]


@pytest.mark.parametrize(
    "path",
    [FIXTURE_DIR / name for name in sorted(EXPECTED) if name.startswith("valid-")],
    ids=lambda p: p.name,
)
def test_catalog_fixture_valid_files_load(path: Path) -> None:
    cat = load_catalog(path)
    assert cat.voices
    # The loader NFC-checks ids; a fixture that loads proves the bytes are NFC.
    for v in cat.voices:
        assert unicodedata.normalize("NFC", v.id) == v.id


@pytest.mark.parametrize(
    "path",
    [
        FIXTURE_DIR / name
        for name in sorted(EXPECTED)
        if name.startswith("invalid-")
    ],
    ids=lambda p: p.name,
)
def test_catalog_fixture_invalid_files_raise_expected_code(path: Path) -> None:
    want = EXPECTED[path.name]
    with pytest.raises(CatalogError) as ei:
        load_catalog(path)
    assert ei.value.code == want["code"]
    assert ei.value.voice_id == want["voiceId"]


def test_catalog_fixture_every_file_listed_in_expected() -> None:
    on_disk = {p.name for p in _all_fixture_files()}
    assert on_disk == set(EXPECTED)
    # And the inverse: every expectation maps to a real file (no stale rows).
    assert len(on_disk) == len(EXPECTED) == 30


def test_catalog_seed_file_loads() -> None:
    cat = load_catalog(SEED_FILE)
    assert cat.revision >= 0
    assert cat.voices


def test_catalog_seed_file_is_valid_revision_ge_1() -> None:
    cat = load_catalog(SEED_FILE)
    assert cat.revision >= 1
