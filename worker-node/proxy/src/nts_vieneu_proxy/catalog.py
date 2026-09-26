"""Voice catalog `voices.v1` loader (README D9, check order per T14).

Check order, first failure wins: JSON parse → file-level keys and types →
each entry in file order (keys/types → `id` → `slug` → other fields) →
cross-entry duplicates (id, slug) → ≥ 1 active voice per language
(revision ≥ 1 only). Codes: `voice_id_invalid`, `voice_slug_mismatch`,
everything else `catalog_invalid` — entry-level failures carry the entry's
id when it is a usable string, otherwise ``None``.
"""

from __future__ import annotations

import json
import re
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .errors import CatalogError

SCHEMA = "voices.v1"

_FILE_KEYS = frozenset({"schema", "revision", "vieneuCommit", "voices"})
_ENTRY_KEYS = frozenset(
    {
        "id",
        "slug",
        "displayName",
        "language",
        "gender",
        "tags",
        "active",
        "previewStorageKey",
    }
)
_LANGUAGES = frozenset({"vi", "en"})
_GENDERS = frozenset({"female", "male", "neutral"})
_TAGS = frozenset(
    {
        "storyteller",
        "natural",
        "news",
        "deep",
        "warm",
        "calm",
        "bright",
        "whisper",
        "northern",
        "central",
        "southern",
    }
)

_VOICE_ID_RE = re.compile(r"[^\W_][\w .\-]{0,63}")
_COMMIT_RE = re.compile(r"[0-9a-fA-F]{40}")
_PREVIEW_KEY_RE = re.compile(r"voice-previews/([a-z0-9-]+)\.v(\d+)\.mp3")


@dataclass(frozen=True)
class CatalogVoice:
    id: str
    slug: str
    display_name: str
    language: str
    gender: str
    tags: tuple[str, ...]
    active: bool
    preview_storage_key: str | None


@dataclass(frozen=True)
class Catalog:
    revision: int
    vieneu_commit: str
    voices: tuple[CatalogVoice, ...]
    ids: frozenset[str]


def _is_int(v: Any) -> bool:
    return isinstance(v, int) and not isinstance(v, bool)


def validate_voice_id(voice_id: str) -> bool:
    """D9 id rules under Python `str` semantics (T7)."""
    if not isinstance(voice_id, str):
        return False
    if unicodedata.normalize("NFC", voice_id) != voice_id:
        return False
    if voice_id != voice_id.strip():
        return False
    if not _VOICE_ID_RE.fullmatch(voice_id):
        return False
    if "-Female" in voice_id or "-Male" in voice_id or ":" in voice_id:
        return False
    if voice_id.endswith("-V2"):
        return False
    return True


def slugify(name: str) -> str:
    """D9 slug: NFD → drop combining marks → đ/Đ→d → lower →
    runs of [^a-z0-9] → `-` → trim `-`."""
    marks_dropped = "".join(
        ch for ch in unicodedata.normalize("NFD", name) if not unicodedata.combining(ch)
    )
    lowered = marks_dropped.replace("đ", "d").replace("Đ", "d").lower()
    return re.sub(r"[^a-z0-9]+", "-", lowered).strip("-")


def _entry_id_str(entry: Any) -> str | None:
    return entry["id"] if isinstance(entry, dict) and isinstance(entry.get("id"), str) else None


def _check_entry(entry: Any) -> CatalogVoice:
    if not isinstance(entry, dict):
        raise CatalogError("catalog_invalid", None)
    keys = set(entry.keys())
    entry_id = _entry_id_str(entry)
    if keys != _ENTRY_KEYS:
        raise CatalogError("catalog_invalid", entry_id)
    typed = (
        ("id", str),
        ("slug", str),
        ("displayName", str),
        ("language", str),
        ("gender", str),
        ("active", bool),
    )
    for key, typ in typed:
        if type(entry[key]) is not typ:
            raise CatalogError("catalog_invalid", entry_id)
    if not isinstance(entry["tags"], list) or not all(
        type(t) is str for t in entry["tags"]
    ):
        raise CatalogError("catalog_invalid", entry_id)
    if entry["previewStorageKey"] is not None and type(entry["previewStorageKey"]) is not str:
        raise CatalogError("catalog_invalid", entry_id)

    voice_id = entry["id"]
    if not validate_voice_id(voice_id):
        raise CatalogError("voice_id_invalid", voice_id)

    slug = entry["slug"]
    if slug != slugify(voice_id):
        raise CatalogError("voice_slug_mismatch", voice_id)

    display_name = entry["displayName"]
    if not (1 <= len(display_name.strip()) <= 40):
        raise CatalogError("catalog_invalid", voice_id)
    if entry["language"] not in _LANGUAGES:
        raise CatalogError("catalog_invalid", voice_id)
    if entry["gender"] not in _GENDERS:
        raise CatalogError("catalog_invalid", voice_id)
    tags = entry["tags"]
    if len(tags) > 4 or len(set(tags)) != len(tags) or not set(tags) <= _TAGS:
        raise CatalogError("catalog_invalid", voice_id)
    key = entry["previewStorageKey"]
    if key is not None:
        m = _PREVIEW_KEY_RE.fullmatch(key)
        if m is None or m.group(1) != slug or int(m.group(2)) < 1:
            raise CatalogError("catalog_invalid", voice_id)

    return CatalogVoice(
        id=voice_id,
        slug=slug,
        display_name=display_name,
        language=entry["language"],
        gender=entry["gender"],
        tags=tuple(tags),
        active=entry["active"],
        preview_storage_key=key,
    )


def load_catalog(path: str | Path) -> Catalog:
    try:
        raw = Path(path).read_text(encoding="utf-8")
    except OSError as exc:
        raise CatalogError("catalog_invalid", None, f"unreadable: {exc.strerror or exc}") from exc
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise CatalogError("catalog_invalid", None, f"json: {exc.msg}") from exc

    # File level: keys and types.
    if not isinstance(data, dict) or set(data.keys()) != _FILE_KEYS:
        raise CatalogError("catalog_invalid", None)
    if data["schema"] != SCHEMA:
        raise CatalogError("catalog_invalid", None)
    if not _is_int(data["revision"]) or data["revision"] < 0:
        raise CatalogError("catalog_invalid", None)
    if not (
        isinstance(data["vieneuCommit"], str)
        and _COMMIT_RE.fullmatch(data["vieneuCommit"])
    ):
        raise CatalogError("catalog_invalid", None)
    if not isinstance(data["voices"], list):
        raise CatalogError("catalog_invalid", None)

    # Per entry, in file order.
    voices = [_check_entry(entry) for entry in data["voices"]]

    # Cross-entry: duplicate ids, duplicate slugs.
    seen_ids: set[str] = set()
    seen_slugs: set[str] = set()
    for v in voices:
        if v.id in seen_ids:
            raise CatalogError("catalog_invalid", v.id)
        if v.slug in seen_slugs:
            raise CatalogError("catalog_invalid", v.id)
        seen_ids.add(v.id)
        seen_slugs.add(v.slug)

    # ≥ 1 active voice per language — only once revision ≥ 1 (D9).
    revision = data["revision"]
    if revision >= 1:
        for lang in _LANGUAGES:
            if not any(v.language == lang and v.active for v in voices):
                raise CatalogError("catalog_invalid", None)

    return Catalog(
        revision=revision,
        vieneu_commit=data["vieneuCommit"],
        voices=tuple(voices),
        ids=frozenset(v.id for v in voices),
    )
