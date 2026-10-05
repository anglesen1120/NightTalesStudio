#!/usr/bin/env python3
"""Check the phase-P legal manifest, locale files and Api/Worker configuration.

Defaults resolve from this script's repository, independent of the working directory.
Use --manifest, --legal-dir, --api-settings and --worker-settings for fixture inputs.
Exit codes: 0 consistent, 1 check/selftest failed, 2 invalid CLI usage.
The later registry task owns DB checks and additional document kinds.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import shutil
import sys
import tempfile

REPO_ROOT = Path(__file__).resolve().parents[2]
LEGAL_DIR = REPO_ROOT / "web/src/content/legal"
FIXTURES = REPO_ROOT / "tests/fixtures/legal"
DOCUMENTS = {"tos": ("terms", "Tos"), "privacy": ("privacy", "Privacy")}
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")
# Preserve JSON strings (including URLs and escaped quotes) while removing comments.
JSON_TOKEN = re.compile(r'"(?:[^"\\]|\\.)*"|//[^\r\n]*|/\*[\s\S]*?\*/')
# Like legal-parity.py, headings are column-zero with a space after the marker.
# Count all h1-h3 levels; compare its numbered h2 slots separately below.
HEADING = re.compile(r"^(#{1,3}) \S", re.MULTILINE)
HEADING_NUMBER = re.compile(r"^## ([0-9]+)\.", re.MULTILINE)


def without_comments(text: str) -> str:
    """Keep line numbers stable for JSON errors; never strip inside a string."""
    return JSON_TOKEN.sub(
        lambda match: re.sub(r"[^\r\n]", " ", match.group())
        if match.group().startswith("/") else match.group(),
        text,
    )


def read_json(path: Path, errors: list[str], *, comments: bool = False) -> dict | None:
    try:
        text = path.read_text(encoding="utf-8")
        document = json.loads(without_comments(text) if comments else text)
    except json.JSONDecodeError as error:
        errors.append(f"{path}: {error.lineno}: invalid JSON: {error.msg}")
        return None
    except (OSError, UnicodeError) as error:
        errors.append(f"{path}: 1: cannot read UTF-8 JSON: {str(error).splitlines()[0]}")
        return None
    if not isinstance(document, dict):
        errors.append(f"{path}: 1: expected a JSON object")
        return None
    return document


def heading_shape(text: str) -> tuple[list[int], list[str]]:
    """Compare levels in order and numbered slots, not translated heading text."""
    return (
        [len(match.group(1)) for match in HEADING.finditer(text)],
        HEADING_NUMBER.findall(text),
    )


def check(manifest_path: Path, legal_dir: Path, api_settings: Path, worker_settings: Path) -> list[str]:
    errors: list[str] = []
    manifest = read_json(manifest_path, errors)
    versions: dict[str, str] = {}
    if manifest is not None:
        for kind, (prefix, _) in DOCUMENTS.items():
            entry = manifest.get(kind)
            if not isinstance(entry, dict):
                errors.append(f"{manifest_path}: 1: missing or invalid {kind} document entry")
                continue
            version = entry.get("version")
            if not isinstance(version, str) or len(version) > 32 or not VERSION.fullmatch(version):
                errors.append(f"{manifest_path}: 1: {kind} version must be digits.digits.digits (at most 32 characters)")
                continue
            versions[kind] = version
            texts: dict[str, str] = {}
            for lang in ("vi", "en"):
                path = legal_dir / f"{prefix}.{version}.{lang}.md"
                try:
                    texts[lang] = path.read_text(encoding="utf-8")
                except (OSError, UnicodeError) as error:
                    errors.append(f"{path}: 1: missing or unreadable {kind} {lang} file: {str(error).splitlines()[0]}")
            if len(texts) == 2 and heading_shape(texts["vi"]) != heading_shape(texts["en"]):
                errors.append(f"{legal_dir / f'{prefix}.{version}.en.md'}: 1: {kind} heading drift between vi/en (h1-h3 levels or numbered h2 slots)")

    for path in (api_settings, worker_settings):
        settings = read_json(path, errors, comments=True)
        if settings is None:
            continue
        legal = settings.get("LegalDocuments")
        current = legal.get("Current") if isinstance(legal, dict) else None
        for kind, (_, key) in DOCUMENTS.items():
            configured = current.get(key) if isinstance(current, dict) else None
            name = f"LegalDocuments:Current:{key}"
            if not isinstance(configured, str) or not configured:
                errors.append(f"{path}: 1: missing or invalid {name}")
            elif kind in versions and configured != versions[kind]:
                # JSON encoding keeps even malformed multi-line values on one diagnostic line.
                errors.append(f"{path}: 1: {name} {json.dumps(configured)} differs from manifest {json.dumps(versions[kind])}")
    return errors


def selftest() -> int:
    """Exercise the committed baseline and failing fixture variants, without the real tree."""
    baseline = FIXTURES / "consistent"
    cases = (
        ("consistent", False),
        ("missing-document", True),
        ("non-semver", True),
        ("missing-en", True),
        ("heading-drift", True),
        ("api-drift", True),
        ("worker-drift", True),
    )
    failures: list[str] = []
    for name, expected_failure in cases:
        with tempfile.TemporaryDirectory(prefix="nts-legal-selftest-") as directory:
            root = Path(directory) / "case"
            try:
                shutil.copytree(baseline, root)
                manifest = root / "manifest.json"
                api = root / "api.json"
                worker = root / "worker.json"
                content = root / "content"
                if name in ("missing-document", "non-semver"):
                    shutil.copyfile(FIXTURES / f"{name}.json", manifest)
                elif name == "missing-en":
                    (content / "terms.1.0.0.en.md").unlink()
                elif name == "heading-drift":
                    shutil.copyfile(FIXTURES / "heading-drift.en.md", content / "terms.1.0.0.en.md")
                elif name in ("api-drift", "worker-drift"):
                    shutil.copyfile(FIXTURES / "config-drift.json", api if name == "api-drift" else worker)
                errors = check(manifest, content, api, worker)
                passed = bool(errors) == expected_failure
            except (OSError, UnicodeError) as error:
                errors = [str(error)]
                passed = False
            print(f"selftest {name}: {'ok' if passed else 'FAIL'}")
            if not passed:
                failures.append(name)
                for error in errors:
                    print(error, file=sys.stderr)
    if failures:
        print(f"selftest failed: {', '.join(failures)}", file=sys.stderr)
        return 1
    print("selftest passed (7 cases)")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=LEGAL_DIR / "manifest.json")
    parser.add_argument("--legal-dir", type=Path, default=LEGAL_DIR)
    parser.add_argument("--api-settings", type=Path, default=REPO_ROOT / "src/NightTales.Api/appsettings.json")
    parser.add_argument("--worker-settings", type=Path, default=REPO_ROOT / "src/NightTales.Worker/appsettings.json")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)
    if args.selftest:
        supplied = sys.argv[1:] if argv is None else argv
        if supplied != ["--selftest"]:
            parser.error("--selftest cannot be combined with input overrides")
        return selftest()
    errors = check(args.manifest, args.legal_dir, args.api_settings, args.worker_settings)
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1
    print("legal-versions: OK (tos/privacy, vi/en, Api/Worker)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
