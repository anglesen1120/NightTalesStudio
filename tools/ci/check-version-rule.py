#!/usr/bin/env python3
"""Fail a pull request that changes the API contract incompatibly without versioning it.

The contract gate of 09 README BD12 and 09.9.5-T1 (DECISION 09.9-D4). `oasdiff breaking` — pinned by
`tools/ci/install-oasdiff.sh` — reports the changes of the PR's OpenAPI document against the document
generated from `main`; this script decides whether those changes may land.

    python3 tools/ci/check-version-rule.py <findings.json> <base.json> <head.json>

`findings.json` is oasdiff's `--format json` output: an array of changes, each with a `level`
(3 = ERR, 2 = WARN, 1 = INFO) and a `text`. Any of the three arguments may be `-` to read standard
input, so the diff can be piped straight in:

    oasdiff breaking --format json base.json head.json | check-version-rule.py - base.json head.json

The rule:

* No ERR finding -> the change is additive or cosmetic; pass. WARN and INFO findings never block.
* Any ERR finding -> the head document must, in addition to keeping the old surface working,
    1. still contain every path and every operation of the base document (an operation that vanished
       or was renamed inside `/api/v1` is never acceptable, however the version is bumped), and
    2. introduce a versioned path prefix the base does not have (e.g. `/api/v2`), and
    3. carry an `info.version` whose major is greater than the base's.
  Otherwise the change is an "unversioned breaking change": the script prints the ERR findings and
  the remediation and exits 1.

Exit codes: 0 = pass, 1 = an unversioned breaking change, 2 = the input could not be read or parsed
(fail closed: an unreadable input must never look like "no findings").

The same cases run without pytest:

    python3 tools/ci/check-version-rule.py --selftest
"""

from __future__ import annotations

import contextlib
import io
import json
import pathlib
import re
import sys
import tempfile
from typing import NamedTuple

#: oasdiff's severity levels, as serialized by `--format json`.
LEVEL_INFO = 1
LEVEL_WARN = 2
LEVEL_ERR = 3

#: The version segment of a path (`/api/v1`, `/api/v2`, ...): the "top-level prefix" a breaking
#: change has to introduce. `/health/live` has none and is therefore never a new prefix.
VERSION_PREFIX = re.compile(r"^(/[^/]+/v\d+)(?=/|$)")

#: `info.version`, set at build to `<major>.<minor>.<build>` (09.9.5-T1 design).
VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")

#: The keys of an OpenAPI path item that are operations rather than metadata.
HTTP_METHODS = frozenset({"get", "put", "post", "delete", "options", "head", "patch", "trace"})

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE_DIR = REPO_ROOT / "tests" / "fixtures" / "openapi"

REMEDIATION = (
    "remediation: keep /api/v1 exactly as it is and add the changed shape under a new prefix "
    "(e.g. /api/v2) with a major bump of info.version, or make the change additive."
)


class InputError(Exception):
    """An input this rule cannot read. It fails closed: an unknown input is never a pass."""


class Verdict(NamedTuple):
    """The rule's decision and the lines the job prints and repeats in the summary."""

    ok: bool
    lines: list[str]


def read_json(path: pathlib.Path) -> object:
    """Read one JSON document, or raise :class:`InputError` with the reason."""
    try:
        text = sys.stdin.read() if str(path) == "-" else path.read_text(encoding="utf-8")
    except OSError as error:
        raise InputError(f"cannot read {path}: {error}") from error
    except UnicodeError as error:
        # A file that is not UTF-8 (or a stdin stream that is not) is an input this rule cannot read:
        # without this clause the decode error escapes as a traceback and the process exits 1, which
        # is the exit code of an *unversioned breaking change* (CR-1).
        raise InputError(f"{path} is not valid UTF-8: {error}") from error

    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise InputError(f"{path} is not JSON: {error}") from error


def read_findings(path: pathlib.Path) -> list[dict]:
    """Read oasdiff's JSON findings, or raise :class:`InputError`."""
    document = read_json(path)
    if not isinstance(document, list):
        raise InputError(f"{path} is not an oasdiff findings array")

    for finding in document:
        if not isinstance(finding, dict) or not isinstance(finding.get("level"), int):
            raise InputError(f"{path} holds a finding without an integer level: {finding!r}")

    return document


def read_spec(path: pathlib.Path) -> dict:
    """Read one OpenAPI document, or raise :class:`InputError`."""
    document = read_json(path)
    if not isinstance(document, dict) or not isinstance(document.get("paths"), dict):
        raise InputError(f"{path} is not an OpenAPI document with a paths object")

    return document


def version_prefixes(document: dict) -> set[str]:
    """Every versioned path prefix of the document (`/api/v1`, `/api/v2`, ...)."""
    return {
        match.group(1)
        for path in document["paths"]
        if (match := VERSION_PREFIX.match(path)) is not None
    }


def major_version(document: dict, source: str) -> int:
    """The major of `info.version`, or raise :class:`InputError` when it is not `<major>.<minor>.<build>`."""
    version = document.get("info", {}).get("version") if isinstance(document.get("info"), dict) else None
    if not isinstance(version, str) or (match := VERSION.match(version)) is None:
        raise InputError(f"{source} has no <major>.<minor>.<build> info.version (got {version!r})")

    return int(match.group(1))


def vanished_operations(base: dict, head: dict) -> list[str]:
    """Every base path or operation the head document no longer contains."""
    vanished = []
    for path, base_item in base["paths"].items():
        head_item = head["paths"].get(path)
        if not isinstance(head_item, dict):
            vanished.append(f"{path} (path removed)")
            continue

        for method in base_item:
            if method in HTTP_METHODS and method not in head_item:
                vanished.append(f"{method.upper()} {path}")

    return sorted(vanished)


def describe(finding: dict) -> str:
    """One finding as a single line, for the job's output."""
    location = " ".join(
        str(finding[key]) for key in ("operation", "path") if finding.get(key)
    )
    return f"[ERR] {finding.get('id', '<no check>')} {location}: {finding.get('text', '')}".rstrip()


def evaluate(findings: list[dict], base: dict, head: dict) -> Verdict:
    """Apply the versioning rule to one findings list and the two documents."""
    errors = [finding for finding in findings if finding["level"] >= LEVEL_ERR]
    if not errors:
        return Verdict(
            True,
            [f"additive-only change: {len(findings)} finding(s), none at ERR (09.9.5-D4)"],
        )

    vanished = vanished_operations(base, head)
    new_prefixes = sorted(version_prefixes(head) - version_prefixes(base))
    base_major = major_version(base, "the base document")
    head_major = major_version(head, "the head document")

    if not vanished and new_prefixes and head_major > base_major:
        return Verdict(
            True,
            [
                f"versioned breaking change accepted: new prefix(es) {', '.join(new_prefixes)} and "
                f"info.version major {base_major} -> {head_major}",
                *(describe(finding) for finding in errors),
            ],
        )

    reasons = []
    if vanished:
        reasons.append("the base document's surface is not preserved: " + "; ".join(vanished))
    if not new_prefixes:
        reasons.append("the head document introduces no new versioned path prefix")
    if head_major <= base_major:
        reasons.append(
            f"info.version major did not grow ({base_major} -> {head_major})"
        )

    return Verdict(
        False,
        [
            f"unversioned breaking change: {len(errors)} ERR finding(s) — " + "; ".join(reasons),
            *(describe(finding) for finding in errors),
            REMEDIATION,
        ],
    )


def report(verdict: Verdict, stream) -> None:
    """Print the verdict, one line per fact, so the CI log and the summary read the same."""
    print("contract-rule:", *verdict.lines, sep="\n  ", file=stream)


def run(findings_path: pathlib.Path, base_path: pathlib.Path, head_path: pathlib.Path) -> int:
    """The CLI's one-shot path: read, evaluate, report, and return the exit code."""
    findings = read_findings(findings_path)
    base = read_spec(base_path)
    head = read_spec(head_path)
    verdict = evaluate(findings, base, head)
    report(verdict, sys.stdout)
    return 0 if verdict.ok else 1


def selftest() -> int:
    """Run the rule's cases against the committed fixtures. Returns 1 when any case fails."""
    def err(
        text: str = "removed the required property `title` from the response with the `200` status",
    ) -> dict:
        return {
            "id": "response-required-property-removed",
            "text": text,
            "level": LEVEL_ERR,
            "operation": "GET",
            "path": "/api/v1/series",
        }

    base = read_spec(FIXTURE_DIR / "base.json")
    additive = read_spec(FIXTURE_DIR / "additive.json")
    breaking = read_spec(FIXTURE_DIR / "breaking.json")
    versioned = read_spec(FIXTURE_DIR / "versioned-breaking.json")
    warning = {"id": "api-security-removed", "text": "removed the security requirement", "level": LEVEL_WARN}

    # A head that keeps the whole base surface, adds /api/v2 and bumps the major — the only shape that
    # may carry a break — is checked twice: once as the fixture, once with a path dropped, which must
    # fail even though the prefix and the major are there.
    versioned_without_a_path = {
        **versioned,
        "paths": {path: item for path, item in versioned["paths"].items() if path != "/api/v1/series"},
    }

    cases = [
        ("additive_change_passes", [], base, additive, True),
        ("warning_only_change_passes", [warning], base, breaking, True),
        ("breaking_change_without_version_fails", [err()], base, breaking, False),
        ("breaking_change_with_new_prefix_and_major_bump_passes", [err()], base, versioned, True),
        ("removed_path_fails_even_with_a_new_prefix", [err()], base, versioned_without_a_path, False),
    ]

    failures = []
    for name, findings, case_base, case_head, expected_ok in cases:
        verdict = evaluate(findings, case_base, case_head)
        status = "ok" if verdict.ok == expected_ok else "FAIL"
        print(f"selftest {name}: {status} ({verdict.lines[0]})")
        if verdict.ok != expected_ok:
            failures.append(name)

    # The two fail-closed paths, through the real CLI.
    with tempfile.TemporaryDirectory() as directory:
        work = pathlib.Path(directory)
        (work / "malformed.json").write_text("{ not json", encoding="utf-8")
        base_fixture = FIXTURE_DIR / "base.json"
        additive_fixture = FIXTURE_DIR / "additive.json"
        (work / "binary.json").write_bytes(b"\xff\xfe[]")
        io_cases = [
            ("missing_findings_file_fails_closed", [work / "absent.json", base_fixture, additive_fixture]),
            ("malformed_spec_fails_closed", [work / "malformed.json", base_fixture, additive_fixture]),
            ("not_utf8_fails_closed", [work / "binary.json", base_fixture, additive_fixture]),
        ]
        for name, arguments in io_cases:
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                code = main(["check-version-rule.py", *(str(argument) for argument in arguments)])
            status = "ok" if code == 2 else "FAIL"
            print(f"selftest {name}: {status} (exit {code})")
            if code != 2:
                failures.append(name)

    if failures:
        print(f"selftest failed: {', '.join(failures)}", file=sys.stderr)
        return 1

    print("selftest passed")
    return 0


def main(argv: list[str]) -> int:
    if len(argv) == 2 and argv[1] == "--selftest":
        return selftest()

    if len(argv) != 4:
        print(
            "usage: check-version-rule.py <findings.json> <base.json> <head.json>"
            " | check-version-rule.py --selftest",
            file=sys.stderr,
        )
        return 2

    try:
        return run(pathlib.Path(argv[1]), pathlib.Path(argv[2]), pathlib.Path(argv[3]))
    except InputError as error:
        print(f"contract-rule: fail closed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
