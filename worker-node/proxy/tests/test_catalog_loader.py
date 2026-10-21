"""catalog.py — D9 rules, check order (T14), slugify, revision-1 seed."""

from __future__ import annotations

import unicodedata

import pytest

from nts_vieneu_proxy.catalog import load_catalog, slugify, validate_voice_id
from nts_vieneu_proxy.errors import CatalogError
from .conftest import (
    SEED_FILE,
    VOICE_EN,
    VOICE_VI,
    catalog_doc,
    voice_entry,
    write_catalog,
)


def _load(tmp_path, doc) -> None:
    load_catalog(write_catalog(tmp_path, doc))


def _err_code(exc_info) -> str:
    return exc_info.value.code


REV1_ACTIVE = {
    ("Mai Anh", "vi"), ("Đức Trí", "vi"), ("Thiền Tâm Đức", "vi"), ("Thiện Minh", "vi"),
    ("Adam", "en"), ("Thục Đoan", "en"), ("Phạm Tuyên", "en"), ("Thái Sơn", "en"),
}


def test_catalog_seed_file_is_revision_1_selection():
    # Revision 1 = the 04.2.1-T1 selection; unselected revision-0 entries stay inactive (04.2.2-T1).
    cat = load_catalog(SEED_FILE)
    assert cat.revision == 1
    assert cat.vieneu_commit == "c1390abbdb2eedcdf58eafb546966c06ce27af71"
    assert {(v.id, v.language) for v in cat.voices if v.active} == REV1_ACTIVE
    assert {v.id for v in cat.voices if not v.active} == {VOICE_VI, VOICE_EN}
    assert len(cat.voices) == 10
    vi = next(v for v in cat.voices if v.id == VOICE_VI)
    en = next(v for v in cat.voices if v.id == VOICE_EN)
    assert (vi.language, vi.gender, vi.slug, vi.active) == ("vi", "male", "hai-dang", False)
    assert (en.language, en.gender, en.slug, en.active) == ("en", "female", "ngoc-huyen", False)
    raw = SEED_FILE.read_bytes().decode("utf-8")
    assert unicodedata.is_normalized("NFC", raw)


def test_catalog_slugify_vietnamese_names():
    assert slugify("Hải Đăng") == "hai-dang"
    assert slugify("Ngọc Huyền") == "ngoc-huyen"
    assert slugify("Thiện Minh") == "thien-minh"
    assert slugify("Đặng Thuỳ  Dương") == "dang-thuy-duong"
    assert slugify("Voice  X!!") == "voice-x"
    assert slugify("a__b.c") == "a-b-c"


@pytest.mark.parametrize(
    "bad_id",
    ["A-Female", "A-Male", "x:y", "Name-V2", "-leading", "_under", "x" * 65, " padded ", "", "tab\tname"],
)
def test_catalog_rejects_female_male_colon_v2_ids(tmp_path, bad_id):
    doc = catalog_doc([voice_entry(bad_id, slugify(bad_id))])
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "voice_id_invalid"
    assert ei.value.voice_id == bad_id


def test_catalog_rejects_nfd_id(tmp_path):
    nfd = unicodedata.normalize("NFD", "Hải Đăng")
    assert nfd != "Hải Đăng"
    doc = catalog_doc([voice_entry(nfd, "hai-dang")])
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "voice_id_invalid"


def test_catalog_rejects_slug_mismatch(tmp_path):
    doc = catalog_doc([voice_entry("Hải Đăng", "wrong-slug")])
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "voice_slug_mismatch"
    assert ei.value.voice_id == "Hải Đăng"


def test_catalog_rejects_preview_key_wrong_slug(tmp_path):
    """D9 previewStorageKey: slug part must be the entry's own slug."""
    doc = catalog_doc([voice_entry(
        "Hải Đăng", "hai-dang",
        previewStorageKey="voice-previews/ngoc-huyen.v1.mp3")])
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "catalog_invalid"
    assert ei.value.voice_id == "Hải Đăng"

def test_catalog_accepts_preview_key_own_slug(tmp_path):
    doc = catalog_doc([voice_entry(
        "Hải Đăng", "hai-dang",
        previewStorageKey="voice-previews/hai-dang.v2.mp3")])
    cat = load_catalog(write_catalog(tmp_path, doc))
    assert cat.ids == {"Hải Đăng"}

def test_catalog_rejects_duplicate_ids(tmp_path):
    doc = catalog_doc(
        [
            voice_entry("Hải Đăng", "hai-dang"),
            voice_entry("Hải Đăng", "hai-dang", language="en", gender="female"),
        ]
    )
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "catalog_invalid"
    assert ei.value.voice_id == "Hải Đăng"


def test_catalog_check_order_id_wins_over_slug(tmp_path):
    """T14: invalid id reports voice_id_invalid even though slug also mismatches."""
    doc = catalog_doc([voice_entry("bad:id", "also-wrong")])
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "voice_id_invalid"


def test_catalog_rejects_unknown_file_and_entry_keys(tmp_path):
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, catalog_doc([voice_entry("A", "a")], extra=1))
    assert _err_code(ei) == "catalog_invalid"
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, catalog_doc([voice_entry("A", "a", extra=1)]))
    assert _err_code(ei) == "catalog_invalid"
    assert ei.value.voice_id == "A"


def test_catalog_rev0_needs_no_active_voice(tmp_path):
    doc = catalog_doc([voice_entry("Hải Đăng", "hai-dang", active=False)])
    cat = load_catalog(write_catalog(tmp_path, doc))
    assert cat.ids == {"Hải Đăng"}


def test_catalog_rev1_requires_active_per_language(tmp_path):
    doc = catalog_doc(
        [voice_entry("Hải Đăng", "hai-dang", language="vi", active=False)],
        revision=1,
    )
    with pytest.raises(CatalogError) as ei:
        _load(tmp_path, doc)
    assert _err_code(ei) == "catalog_invalid"
    assert ei.value.voice_id is None


def test_validate_voice_id_accepts_seed_ids():
    assert validate_voice_id("Hải Đăng")
    assert validate_voice_id("Ngọc Huyền")
    assert validate_voice_id("Adam bựa")
    assert not validate_voice_id("Adam bựa ")
