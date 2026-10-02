#!/usr/bin/env python3
"""check_catalog.py -- prove seed/music-tracks.v1.json obeys D8 and agrees with LICENSES.csv.

Contract: plans/features/05-video-assembly-subtitles/05.3-music/05.3.2-T1-music-curation-licence-records.md
D8:       plans/features/05-video-assembly-subtitles/README.md (music-tracks.v1 schema, gain formula)

Usage:
    check_catalog.py --catalog FILE [--licenses FILE] [--json]
    check_catalog.py --self-test

Defaults (repo-relative, resolved from this file's repo root):
    --catalog  seed/music-tracks.v1.json
    --licenses worker-node/music/LICENSES.csv

Exit codes: 0 ok / 1 any rule or consistency error (all listed) / 2 usage or environment.
Read-only, stdlib only, no network. The audio files themselves are never opened.

JSON rule codes: revision_not_positive, track_id_format, track_id_not_file_stem,
track_id_duplicate, file_duplicate, file_extension, license_unknown, attribution_required,
attribution_not_allowed, scope_unknown, scope_license_mismatch, moods_unknown, moods_count,
duration_below_minimum, loudness_missing, sha256_format, bgm_volume_range,
bgm_volume_formula_mismatch, active_missing.
CSV consistency codes: csv_row_missing, csv_field_mismatch, csv_moods_mismatch,
csv_evidence_missing.

seed/fixtures/music/expected.json asserts the expected code is *present* among the returned
codes (this tool reports every violation it finds, so one fixture may raise more than one).
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import io
import json
import math
import re
import sys
import tempfile
from pathlib import Path
from urllib.parse import urlsplit

REPO = Path(__file__).resolve().parents[3]
SCHEMA = "music-tracks.v1"

LICENSE_ENUM = ("CC0-1.0", "CC-BY-4.0", "YouTube-Audio-Library")
SCOPE_ENUM = ("youtube", "any")
SCOPE_BY_LICENSE = {"YouTube-Audio-Library": "youtube", "CC0-1.0": "any", "CC-BY-4.0": "any"}
MOODS = ("dark_ambient", "suspense", "temple", "meditation", "noir", "calm")
EXTENSIONS = (".mp3", ".m4a")
ATTRIBUTION_LICENSE = "CC-BY-4.0"

ID_RE = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
SHA_RE = re.compile(r"^[0-9a-f]{64}$")
ID_MAX = 40
MIN_DURATION_SEC = 60.0
MAX_DURATION_SEC = 3600.0
LOUDNESS_SCALE = 2
MUSIC_BED_LUFS = -34.0
GAIN_MIN, GAIN_MAX = 0.03, 0.40

CSV_BASE_COLUMNS = [
    "file", "title", "artist", "source_url", "license", "license_url", "attribution",
    "mood_tags", "sha256", "duration_s", "loudness_lufs", "added_at",
]
CSV_APPENDED_COLUMNS = ["track_id", "platform_scope", "evidence_url", "retrieved_at"]

JSON_CODES = (
    "revision_not_positive", "track_id_format", "track_id_not_file_stem", "track_id_duplicate",
    "file_duplicate", "file_extension", "license_unknown", "attribution_required",
    "attribution_not_allowed", "scope_unknown", "scope_license_mismatch", "moods_unknown",
    "moods_count", "duration_below_minimum", "loudness_missing", "sha256_format",
    "bgm_volume_range", "bgm_volume_formula_mismatch", "active_missing",
    "duration_above_maximum", "loudness_scale", "title_blank", "artist_blank",
    "source_url_invalid", "music_catalog_invalid",
)
CSV_CODES = ("csv_row_missing", "csv_field_mismatch", "csv_moods_mismatch", "csv_evidence_missing")
ALL_CODES = JSON_CODES + CSV_CODES

FIXTURE_EVIDENCE_URL = "https://example.invalid/evidence/fixture"
FIXTURE_RETRIEVED_AT = "2026-09-26T00:00:00Z"
FIXTURE_ADDED_AT = "2026-09-26T00:00:00Z"


class CatalogError(Exception):
    """Usage or environment problem: exit 2, no rule evaluation possible."""


def bgm_volume(loudness_lufs: float) -> float:
    """D8: round(clamp(10**((MusicBedLufs - loudnessLufs) / 20), 0.03, 0.40), 3)."""
    raw = 10 ** ((MUSIC_BED_LUFS - float(loudness_lufs)) / 20.0)
    return round(min(max(raw, GAIN_MIN), GAIN_MAX), 3)


def _err(code: str, detail: str, track: str | None = None) -> dict:
    return {"code": code, "track": track, "detail": detail}


def _is_number(value) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _is_text(value) -> bool:
    return isinstance(value, str)


def _stem(name: str) -> str:
    cut = name.rfind(".")
    return name[:cut] if cut > 0 else name


def _is_iso8601(value) -> bool:
    if not _is_text(value) or not value.strip():
        return False
    text = value.strip()
    try:
        dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
        return True
    except ValueError:
        pass
    try:
        dt.date.fromisoformat(text)
        return True
    except ValueError:
        return False


class DuplicateKeyError(ValueError):
    """A JSON object repeats a property: the last value would silently win."""


def _reject_duplicate_keys(pairs: list) -> dict:
    seen: set = set()
    for key, _value in pairs:
        if key in seen:
            raise DuplicateKeyError(f"duplicate JSON property {key!r}")
        seen.add(key)
    return dict(pairs)


def _loads_strict(text: str):
    return json.loads(text, object_pairs_hook=_reject_duplicate_keys)


def _is_https_url(value) -> bool:
    if not _is_text(value):
        return False
    try:
        parts = urlsplit(value)
        host = parts.hostname
    except ValueError:
        return False
    return parts.scheme == "https" and bool(host)


def check_json(doc: dict) -> tuple[list[dict], dict]:
    """Apply the D8 JSON rules. Returns (errors, stats)."""
    errors: list[dict] = []
    revision = doc.get("revision")
    if not isinstance(revision, int) or isinstance(revision, bool) or revision <= 0:
        errors.append(_err("revision_not_positive", f"revision={revision!r}"))

    tracks = doc.get("tracks")
    if not isinstance(tracks, list):
        raise CatalogError("catalog has no tracks array")
    if not tracks:
        # An empty list would deactivate every track the database holds.
        errors.append(_err("music_catalog_invalid", "tracks is empty"))

    seen_ids: set[str] = set()
    seen_files: set[str] = set()
    histogram = {mood: 0 for mood in MOODS}
    active = 0

    for track in tracks:
        if not isinstance(track, dict):
            errors.append(_err("active_missing", "track is not an object"))
            continue
        label = track.get("id") if _is_text(track.get("id")) else track.get("file")

        track_id = track.get("id")
        if not _is_text(track_id) or not ID_RE.match(track_id) or len(track_id) > ID_MAX:
            errors.append(_err("track_id_format", f"id={track_id!r}", label))
        else:
            if track_id in seen_ids:
                errors.append(_err("track_id_duplicate", f"id={track_id!r}", track_id))
            seen_ids.add(track_id)

        name = track.get("file")
        if not _is_text(name) or not name:
            errors.append(_err("file_extension", f"file={name!r}", label))
        else:
            if Path(name).suffix.lower() not in EXTENSIONS:
                errors.append(_err("file_extension", f"file={name!r}", label))
            if name in seen_files:
                errors.append(_err("file_duplicate", f"file={name!r}", label))
            seen_files.add(name)
            if _is_text(track_id) and _stem(name) != track_id:
                errors.append(
                    _err("track_id_not_file_stem", f"id={track_id!r} file={name!r}", label)
                )

        for key, code in (("title", "title_blank"), ("artist", "artist_blank")):
            value = track.get(key)
            if not _is_text(value) or not value.strip():
                errors.append(_err(code, f"{key}={value!r}", label))
        if not _is_https_url(track.get("sourceUrl")):
            errors.append(_err("source_url_invalid", f"sourceUrl={track.get('sourceUrl')!r}", label))

        license_value = track.get("license")
        if license_value not in LICENSE_ENUM:
            errors.append(_err("license_unknown", f"license={license_value!r}", label))
        attribution = track.get("attribution")
        has_attribution = _is_text(attribution) and bool(attribution.strip())
        if license_value == ATTRIBUTION_LICENSE and not has_attribution:
            errors.append(_err("attribution_required", "CC-BY-4.0 needs attribution", label))
        if license_value != ATTRIBUTION_LICENSE and has_attribution:
            errors.append(_err("attribution_not_allowed", f"attribution={attribution!r}", label))

        scope = track.get("platformScope")
        if scope not in SCOPE_ENUM:
            errors.append(_err("scope_unknown", f"platformScope={scope!r}", label))
        elif license_value in SCOPE_BY_LICENSE and scope != SCOPE_BY_LICENSE[license_value]:
            errors.append(
                _err("scope_license_mismatch", f"{license_value} -> {scope!r}", label)
            )

        moods = track.get("moods")
        if not isinstance(moods, list):
            errors.append(_err("moods_count", f"moods={moods!r}", label))
        else:
            if not 1 <= len(moods) <= 3:
                errors.append(_err("moods_count", f"count={len(moods)}", label))
            for mood in moods:
                if mood not in MOODS:
                    errors.append(_err("moods_unknown", f"mood={mood!r}", label))
                else:
                    histogram[mood] += 1

        duration = track.get("durationSec")
        if not _is_number(duration) or duration < MIN_DURATION_SEC:
            errors.append(_err("duration_below_minimum", f"durationSec={duration!r}", label))
        elif duration > MAX_DURATION_SEC:
            errors.append(_err("duration_above_maximum", f"durationSec={duration!r}", label))

        loudness = track.get("loudnessLufs")
        if not _is_number(loudness):
            errors.append(_err("loudness_missing", f"loudnessLufs={loudness!r}", label))
        elif round(loudness, LOUDNESS_SCALE) != loudness:
            errors.append(_err("loudness_scale", f"loudnessLufs={loudness!r}", label))

        digest = track.get("sha256")
        if not _is_text(digest) or not SHA_RE.match(digest):
            errors.append(_err("sha256_format", f"sha256={digest!r}", label))

        gain = track.get("bgmVolume")
        if not _is_number(gain) or gain < GAIN_MIN or gain > GAIN_MAX:
            errors.append(_err("bgm_volume_range", f"bgmVolume={gain!r}", label))
        elif _is_number(loudness) and round(gain, 3) != bgm_volume(round(loudness, LOUDNESS_SCALE)):
            errors.append(
                _err(
                    "bgm_volume_formula_mismatch",
                    f"bgmVolume={gain!r} expected={bgm_volume(loudness)!r}",
                    label,
                )
            )

        if not isinstance(track.get("active"), bool):
            errors.append(_err("active_missing", f"active={track.get('active')!r}", label))
        elif track["active"]:
            active += 1

    stats = {"revision": revision, "tracks": len(tracks), "active": active, "moods": histogram}
    return errors, stats


def read_licenses(path: Path) -> tuple[list[str], list[dict]]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise CatalogError(f"cannot read licenses file {path}: {exc.strerror}") from exc
    reader = csv.reader(io.StringIO(text))
    try:
        header = next(reader)
    except StopIteration:
        raise CatalogError(f"licenses file {path} is empty (no header)") from None
    if header[: len(CSV_BASE_COLUMNS)] != CSV_BASE_COLUMNS:
        raise CatalogError(f"unreadable CSV header in {path}: {header[:12]}")
    rows = []
    for raw in reader:
        if not raw:
            continue
        rows.append({column: (raw[i] if i < len(raw) else "") for i, column in enumerate(header)})
    return header, rows


def check_csv(tracks: list, header: list[str], rows: list[dict]) -> list[dict]:
    errors: list[dict] = []
    by_id: dict[str, dict] = {}
    for row in rows:
        track_id = (row.get("track_id") or "").strip()
        if track_id:
            by_id.setdefault(track_id, row)

    fields = [
        ("file", "file"),
        ("sha256", "sha256"),
        ("license", "license"),
        ("platform_scope", "platformScope"),
    ]
    for track in tracks:
        if not isinstance(track, dict):
            continue
        track_id = track.get("id")
        if not _is_text(track_id) or not ID_RE.match(track_id) or len(track_id) > ID_MAX:
            continue  # a malformed id cannot be matched to a row; track_id_format already fired
        row = by_id.get(track_id)
        if row is None:
            errors.append(_err("csv_row_missing", "no CSV row for this track", track_id))
            continue
        for column, key in fields:
            if (row.get(column) or "") != (track.get(key) if _is_text(track.get(key)) else ""):
                errors.append(
                    _err(
                        "csv_field_mismatch",
                        f"{column}={row.get(column)!r} json={track.get(key)!r}",
                        track_id,
                    )
                )
        moods = track.get("moods") if isinstance(track.get("moods"), list) else []
        csv_moods = {part.strip() for part in (row.get("mood_tags") or "").split(";") if part.strip()}
        if csv_moods != set(moods):
            errors.append(
                _err(
                    "csv_moods_mismatch",
                    f"mood_tags={row.get('mood_tags')!r} moods={moods!r}",
                    track_id,
                )
            )
        evidence = (row.get("evidence_url") or "").strip()
        if not evidence.startswith(("http://", "https://")):
            errors.append(_err("csv_evidence_missing", f"evidence_url={evidence!r}", track_id))
        if not _is_iso8601(row.get("retrieved_at")):
            errors.append(
                _err("csv_evidence_missing", f"retrieved_at={row.get('retrieved_at')!r}", track_id)
            )

    known = {
        track.get("id")
        for track in tracks
        if isinstance(track, dict) and _is_text(track.get("id"))
    }
    for track_id in by_id:
        if track_id not in known:
            errors.append(
                _err("csv_field_mismatch", "CSV row has no catalog track", track_id)
            )
    return errors


def validate_paths(catalog_path: Path, licenses_path: Path) -> dict:
    if not catalog_path.is_file():
        raise CatalogError(f"catalog not found: {catalog_path}")
    try:
        doc = _loads_strict(catalog_path.read_text(encoding="utf-8"))
    except DuplicateKeyError as exc:
        return {
            "ok": False,
            "revision": None,
            "tracks": 0,
            "active": 0,
            "moods": {mood: 0 for mood in MOODS},
            "errors": [_err("music_catalog_invalid", str(exc))],
            "catalog": str(catalog_path),
            "licenses": str(licenses_path),
        }
    except (OSError, ValueError) as exc:
        raise CatalogError(f"cannot read catalog {catalog_path}: {exc}") from exc
    if not isinstance(doc, dict):
        raise CatalogError(f"catalog {catalog_path} is not a JSON object")
    if doc.get("schema") != SCHEMA:
        raise CatalogError(f"catalog schema is {doc.get('schema')!r}, expected {SCHEMA!r}")

    json_errors, stats = check_json(doc)
    header, rows = read_licenses(licenses_path)
    errors = json_errors + check_csv(doc.get("tracks") or [], header, rows)
    return {
        "ok": not errors,
        "revision": stats["revision"],
        "tracks": stats["tracks"],
        "active": stats["active"],
        "moods": stats["moods"],
        "errors": errors,
        "catalog": str(catalog_path),
        "licenses": str(licenses_path),
    }


def json_line(report: dict) -> str:
    payload = {
        "ok": report["ok"],
        "revision": report["revision"],
        "tracks": report["tracks"],
        "active": report["active"],
        "moods": report["moods"],
        "errors": report["errors"],
    }
    return json.dumps(payload)


def print_human(report: dict) -> None:
    print(f"catalog:  {report['catalog']}")
    print(f"licenses: {report['licenses']}")
    print(f"revision: {report['revision']}  tracks: {report['tracks']}  active: {report['active']}")
    print("moods: " + " ".join(f"{mood}={report['moods'][mood]}" for mood in MOODS))
    for item in report["errors"]:
        print(f"ERROR {item['code']} [{item['track']}] {item['detail']}")
    print("ok" if report["ok"] else f"FAILED ({len(report['errors'])} errors)")


def csv_text(rows: list[dict]) -> str:
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(CSV_BASE_COLUMNS + CSV_APPENDED_COLUMNS)
    for row in rows:
        writer.writerow([row.get(column, "") for column in CSV_BASE_COLUMNS + CSV_APPENDED_COLUMNS])
    return buffer.getvalue()


def fixture_csv_rows(tracks: list) -> list[dict]:
    """Derive a CSV row per fixture track so only the injected defect fires."""
    rows = []
    for track in tracks:
        if not isinstance(track, dict):
            continue
        rows.append(
            {
                "file": track.get("file") or "",
                "title": track.get("title") or "",
                "artist": track.get("artist") or "",
                "source_url": track.get("sourceUrl") or "",
                "license": track.get("license") or "",
                "license_url": track.get("licenseUrl") or "",
                "attribution": track.get("attribution") or "",
                "mood_tags": ";".join(track.get("moods") or []),
                "sha256": track.get("sha256") or "",
                "duration_s": f"{track.get('durationSec', '')}",
                "loudness_lufs": f"{track.get('loudnessLufs', '')}",
                "added_at": FIXTURE_ADDED_AT,
                "track_id": track.get("id") or "",
                "platform_scope": track.get("platformScope") or "",
                "evidence_url": FIXTURE_EVIDENCE_URL,
                "retrieved_at": FIXTURE_RETRIEVED_AT,
            }
        )
    return rows


def _boundary_cases() -> list[tuple[str, dict, list[str]]]:
    """Synthesized catalogs: (case name, catalog doc, codes that must be present)."""
    digest_a = "a" * 64
    digest_b = "b" * 64
    cases: list[tuple[str, dict, list[str]]] = []

    def doc(track: dict, revision: int = 1) -> dict:
        return {"schema": SCHEMA, "revision": revision, "tracks": [track]}

    def track(**over) -> dict:
        base = {
            "id": "ambient-pad-01",
            "file": "ambient-pad-01.mp3",
            "title": "Boundary",
            "artist": "Boundary",
            "sourceUrl": "https://example.invalid/t",
            "license": "CC0-1.0",
            "licenseUrl": "https://creativecommons.org/publicdomain/zero/1.0/",
            "attribution": None,
            "platformScope": "any",
            "moods": ["calm"],
            "durationSec": 120.0,
            "loudnessLufs": -20.0,
            "bgmVolume": bgm_volume(-20.0),
            "sha256": digest_a,
            "active": True,
        }
        base.update(over)
        return base

    long_ok = "a" * 39 + "1"  # 40 chars
    long_bad = "a" * 40 + "1"  # 41 chars
    cases.append(("boundary: id 40 chars ok", doc(track(id=long_ok, file=f"{long_ok}.mp3")), []))
    cases.append(("boundary: id 41 chars", doc(track(id=long_bad, file=f"{long_bad}.mp3")), ["track_id_format"]))
    cases.append(("boundary: id uppercase", doc(track(id="Ambient", file="Ambient.mp3")), ["track_id_format"]))
    cases.append(("boundary: id leading dash", doc(track(id="-a", file="-a.mp3")), ["track_id_format"]))
    cases.append(("boundary: id trailing dash", doc(track(id="a-", file="a-.mp3")), ["track_id_format"]))
    cases.append(("boundary: gain clamp top (-34)", doc(track(loudnessLufs=-34.0, bgmVolume=0.40)), []))
    cases.append(("boundary: gain clamp top (-80)", doc(track(loudnessLufs=-80.0, bgmVolume=0.40)), []))
    cases.append(("boundary: gain clamp bottom (-3.5)", doc(track(loudnessLufs=-3.5, bgmVolume=0.03)), []))
    cases.append(("boundary: gain above clamp", doc(track(loudnessLufs=-34.0, bgmVolume=0.155)), ["bgm_volume_formula_mismatch"]))
    cases.append(("boundary: gain out of range", doc(track(bgmVolume=0.5)), ["bgm_volume_range"]))
    cases.append(("boundary: gain missing", doc(track(bgmVolume=None)), ["bgm_volume_range"]))
    cases.append(("boundary: duration 60.0 ok", doc(track(durationSec=60.0)), []))
    cases.append(("boundary: duration 59.9", doc(track(durationSec=59.9)), ["duration_below_minimum"]))
    cases.append(("boundary: duration missing", doc(track(durationSec=None)), ["duration_below_minimum"]))
    cases.append(("boundary: sha256 uppercase", doc(track(sha256=digest_a.upper())), ["sha256_format"]))
    cases.append(("boundary: sha256 63 hex", doc(track(sha256="a" * 63)), ["sha256_format"]))
    cases.append(("boundary: loudness -inf float", doc(track(loudnessLufs=float("-inf"), bgmVolume=None)), ["loudness_missing"]))
    cases.append(("boundary: loudness '-inf' string", doc(track(loudnessLufs="-inf")), ["loudness_missing"]))
    cases.append(("boundary: attribution_not_allowed", doc(track(attribution="credited")), ["attribution_not_allowed"]))
    cases.append(("boundary: scope unknown", doc(track(platformScope="tiktok")), ["scope_unknown"]))
    cases.append(("boundary: scope license mismatch", doc(track(license="YouTube-Audio-Library", platformScope="any")), ["scope_license_mismatch"]))
    cases.append(("boundary: moods count 0", doc(track(moods=[])), ["moods_count"]))
    cases.append(("boundary: moods count 4", doc(track(moods=["calm", "noir", "temple", "suspense"])), ["moods_count"]))
    cases.append(("boundary: moods not a list", doc(track(moods="calm")), ["moods_count"]))
    cases.append(("boundary: active missing", doc({k: v for k, v in track().items() if k != "active"}), ["active_missing"]))
    cases.append(("boundary: active wrong type", doc(track(active="true")), ["active_missing"]))
    cases.append(("boundary: file extension", doc(track(file="ambient-pad-01.wav")), ["file_extension"]))
    cases.append(("boundary: id not file stem", doc(track(id="pad-01", file="ambient-pad-01.mp3")), ["track_id_not_file_stem"]))
    cases.append(("boundary: license unknown", doc(track(license="MIT")), ["license_unknown"]))
    cases.append(
        (
            "boundary: revision zero",
            {"schema": SCHEMA, "revision": 0, "tracks": [track()]},
            ["revision_not_positive"],
        )
    )
    cases.append(
        (
            "boundary: duplicates",
            {"schema": SCHEMA, "revision": 1, "tracks": [track(), track(sha256=digest_b)]},
            ["track_id_duplicate"],
        )
    )
    cases.append(
        (
            "boundary: file duplicate",
            {"schema": SCHEMA, "revision": 1, "tracks": [track(), track(id="pad-02", file="ambient-pad-01.mp3")]},
            ["file_duplicate"],
        )
    )
    return cases


def _csv_cases(tmp: Path) -> list[tuple[str, Path, list[str]]]:
    """Synthesized JSON+CSV pairs: (case name, catalog path, codes that must be present)."""
    track = {
        "id": "fixture-csv-track",
        "file": "fixture-csv-track.mp3",
        "title": "Fixture CSV",
        "artist": "Fixture CSV",
        "sourceUrl": "https://example.invalid/t",
        "license": "CC0-1.0",
        "licenseUrl": "https://creativecommons.org/publicdomain/zero/1.0/",
        "attribution": None,
        "platformScope": "any",
        "moods": ["calm"],
        "durationSec": 100.0,
        "loudnessLufs": -20.0,
        "bgmVolume": bgm_volume(-20.0),
        "sha256": "c" * 64,
        "active": True,
    }
    catalog = tmp / "synthetic-catalog.json"
    catalog.write_text(
        json.dumps({"schema": SCHEMA, "revision": 1, "tracks": [track]}), encoding="utf-8"
    )
    row = fixture_csv_rows([track])[0]

    cases: list[tuple[str, Path, list[str]]] = []

    def write(name: str, rows: list[dict]) -> Path:
        path = tmp / name
        path.write_text(csv_text(rows), encoding="utf-8")
        return path

    cases.append(("csv: matching row", write("csv-ok.csv", [row]), []))
    cases.append(("csv: row missing", write("csv-missing.csv", []), ["csv_row_missing"]))
    cases.append(
        ("csv: wrong sha256", write("csv-sha.csv", [dict(row, sha256="d" * 64)]), ["csv_field_mismatch"])
    )
    cases.append(
        ("csv: wrong file", write("csv-file.csv", [dict(row, file="other.mp3")]), ["csv_field_mismatch"])
    )
    cases.append(
        ("csv: wrong license", write("csv-license.csv", [dict(row, license="MIT")]), ["csv_field_mismatch"])
    )
    cases.append(
        (
            "csv: wrong platform_scope",
            write("csv-scope.csv", [dict(row, platform_scope="youtube")]),
            ["csv_field_mismatch"],
        )
    )
    cases.append(
        (
            "csv: wrong mood tags",
            write("csv-moods.csv", [dict(row, mood_tags="calm;noir")]),
            ["csv_moods_mismatch"],
        )
    )
    cases.append(
        (
            "csv: empty evidence url",
            write("csv-evidence.csv", [dict(row, evidence_url="")]),
            ["csv_evidence_missing"],
        )
    )
    cases.append(
        (
            "csv: bad retrieved_at",
            write("csv-retrieved.csv", [dict(row, retrieved_at="26/09/2026")]),
            ["csv_evidence_missing"],
        )
    )
    cases.append(
        (
            "csv: unknown row with track_id",
            write("csv-extra.csv", [row, dict(row, track_id="ghost-track-01")]),
            ["csv_field_mismatch"],
        )
    )
    cases.append(
        (
            "csv: placeholder row allowed",
            write("csv-placeholder.csv", [row, dict(row, track_id="", evidence_url="", retrieved_at="")]),
            [],
        )
    )
    return cases


def self_test() -> int:
    failures: list[str] = []
    passes = 0
    seen_codes: set[str] = set()
    licenses_file = REPO / "worker-node/music/LICENSES.csv"
    before = licenses_file.read_bytes() if licenses_file.is_file() else None

    fixtures_dir = REPO / "seed/fixtures/music"
    if not fixtures_dir.is_dir():
        print(f"FAIL self-test: fixtures dir missing: {fixtures_dir}")
        return 1
    try:
        expected = json.loads((fixtures_dir / "expected.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        print(f"FAIL self-test: expected.json unreadable: {exc}")
        return 1

    with tempfile.TemporaryDirectory(prefix="check-catalog-selftest-") as tmp_name:
        tmp = Path(tmp_name)

        for name in sorted(expected):
            catalog_path = fixtures_dir / f"{name}.json"
            spec = expected[name]
            if not catalog_path.is_file():
                failures.append(f"{name}: fixture file missing ({catalog_path.name})")
                continue
            try:
                doc = json.loads(catalog_path.read_text(encoding="utf-8"))
            except ValueError as exc:
                failures.append(f"{name}: fixture is not JSON ({exc})")
                continue
            csv_path = tmp / f"{name}.csv"
            csv_path.write_text(csv_text(fixture_csv_rows(doc.get("tracks") or [])), encoding="utf-8")
            try:
                report = validate_paths(catalog_path, csv_path)
            except CatalogError as exc:
                failures.append(f"{name}: unexpected environment error: {exc}")
                continue
            codes = [item["code"] for item in report["errors"]]
            seen_codes.update(codes)
            if report["ok"] != bool(spec["ok"]):
                failures.append(f"{name}: ok={report['ok']} expected {spec['ok']} (codes={codes})")
                continue
            if spec["code"] is not None and spec["code"] not in codes:
                failures.append(f"{name}: expected code {spec['code']} not in {codes}")
                continue
            passes += 1
            print(f"PASS fixture {name} ({spec['code'] or 'ok'})")

        unlisted = sorted(
            path.stem
            for path in fixtures_dir.glob("*.json")
            if path.stem != "expected" and path.stem not in expected
        )
        if unlisted:
            failures.append(f"fixtures not listed in expected.json: {unlisted}")

        for name, doc, want in _boundary_cases():
            errors, _stats = check_json(doc)
            codes = [item["code"] for item in errors]
            seen_codes.update(codes)
            if not want and errors:
                failures.append(f"{name}: expected no errors, got {codes}")
                continue
            missing = [code for code in want if code not in codes]
            if missing:
                failures.append(f"{name}: missing {missing} in {codes}")
                continue
            passes += 1

        for name, catalog_path, want in _csv_cases(tmp):
            rows_header, rows = read_licenses(catalog_path)
            doc = json.loads((tmp / "synthetic-catalog.json").read_text(encoding="utf-8"))
            json_errors, _stats = check_json(doc)
            errors = json_errors + check_csv(doc["tracks"], rows_header, rows)
            codes = [item["code"] for item in errors]
            seen_codes.update(codes)
            if not want and errors:
                failures.append(f"{name}: expected no errors, got {codes}")
                continue
            missing = [code for code in want if code not in codes]
            if missing:
                failures.append(f"{name}: missing {missing} in {codes}")
                continue
            passes += 1

        valid_doc = json.loads((fixtures_dir / "valid-catalog.json").read_text(encoding="utf-8"))
        report = validate_paths(fixtures_dir / "valid-catalog.json", tmp / "valid-catalog.csv")
        line = json_line(report)
        problems = []
        if "\n" in line:
            problems.append("not a single line")
        for key in ("ok", "revision", "tracks", "active", "moods", "errors"):
            if f'"{key}"' not in line:
                problems.append(f"missing key {key}")
        if report["ok"] is not True or valid_doc["revision"] != report["revision"]:
            problems.append("valid fixture did not report ok with its revision")
        if problems:
            failures.append(f"json line shape: {problems}")
        else:
            passes += 1

        missing_codes = sorted(set(ALL_CODES) - seen_codes)
        if missing_codes:
            failures.append(f"self-test does not exercise every code: {missing_codes}")
        else:
            passes += 1

    after = licenses_file.read_bytes() if licenses_file.is_file() else None
    if before != after:
        failures.append("LICENSES.csv was modified by the self-test (must be read-only)")
    else:
        passes += 1

    if failures:
        for item in failures:
            print(f"FAIL {item}")
        print(f"self-test: {passes} passed, {len(failures)} failed")
        return 1
    print(f"self-test: {passes} passed, 0 failed ({len(ALL_CODES)} rule codes exercised)")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="check_catalog.py",
        description="Validate seed/music-tracks.v1.json (D8) against worker-node/music/LICENSES.csv",
    )
    parser.add_argument("--catalog", metavar="FILE", default=None)
    parser.add_argument("--licenses", metavar="FILE", default=None)
    parser.add_argument("--json", action="store_true", dest="as_json")
    parser.add_argument("--self-test", action="store_true", dest="self_test")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    catalog = Path(args.catalog) if args.catalog else REPO / "seed/music-tracks.v1.json"
    licenses = Path(args.licenses) if args.licenses else REPO / "worker-node/music/LICENSES.csv"
    try:
        report = validate_paths(catalog, licenses)
    except CatalogError as exc:
        print(f"check_catalog: {exc}", file=sys.stderr)
        return 2
    if args.as_json:
        print(json_line(report))
    else:
        print_human(report)
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
