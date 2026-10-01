#!/usr/bin/env python3
"""Fail when a dependency advisory of a blocking severity is present.

The CI gate of 09 README W10 (09.9.4-T1): it reads the JSON of

    dotnet list NightTales.ci.slnf package --vulnerable --include-transitive --format json

and exits 1 when any top-level or transitive package carries a High or Critical advisory. The rule
lives here, in a small stdlib-only script, rather than in a shell one-liner, so it is reviewable and
can be exercised against a fixture.

Usage:
    python3 tools/ci/check-vulnerable.py <report.json>
    dotnet list ... --format json | python3 tools/ci/check-vulnerable.py

Exit codes: 0 = clean (or only allowed/low advisories), 1 = a blocking advisory, 2 = the input is not
a vulnerability report. A package whose advisory carries no severity is treated as blocking: an
unreadable severity must not pass silently.
"""

from __future__ import annotations

import datetime
import json
import pathlib
import sys

#: The severities that fail the job. `dotnet list --vulnerable` reports Low/Moderate/High/Critical.
BLOCKING_SEVERITIES = frozenset({"high", "critical"})

#: Time-boxed exceptions, one entry per package + advisory pair. An entry is only honoured until its
#: `until` date (ISO, inclusive), so an accepted vulnerability has to be reviewed again when it
#: expires. Never add an entry for a whole package id without an advisory URL.
#: {"package": "<id>", "advisory": "<url>", "until": "YYYY-MM-DD", "reason": "<why it is accepted>"}
ALLOW_LIST: tuple[dict[str, str], ...] = ()


def vulnerabilities(report: dict) -> list[dict[str, str]]:
    """Every advisory of the report, one entry per package and framework it was reported under."""
    findings: list[dict[str, str]] = []

    for project in report.get("projects", []):
        for framework in project.get("frameworks", []):
            for group in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(group, []):
                    for advisory in package.get("vulnerabilities", []):
                        findings.append(
                            {
                                "project": project.get("path", "<unknown>"),
                                "framework": framework.get("framework", "<unknown>"),
                                "id": package.get("id", "<unknown>"),
                                "resolved": package.get("resolvedVersion")
                                or package.get("requestedVersion")
                                or "?",
                                "severity": str(advisory.get("severity", "")).lower(),
                                "advisory": advisory.get("advisoryurl")
                                or advisory.get("advisoryUrl")
                                or "",
                            }
                        )

    return findings


def is_allowed(finding: dict[str, str], today: datetime.date) -> bool:
    """Whether the allow-list excuses this finding (same package and advisory, not yet expired)."""
    for entry in ALLOW_LIST:
        if entry["package"].lower() != finding["id"].lower():
            continue
        if entry["advisory"].lower() != finding["advisory"].lower():
            continue
        if datetime.date.fromisoformat(entry["until"]) < today:
            print(
                f"check-vulnerable: the allow-list entry for {finding['id']} expired on "
                f"{entry['until']} and no longer excuses {finding['advisory']}",
                file=sys.stderr,
            )
            continue

        print(f"check-vulnerable: allowed until {entry['until']}: {finding['id']} ({entry['reason']})")

        return True

    return False


def describe(finding: dict[str, str]) -> str:
    severity = finding["severity"] or "<no severity>"
    advisory = finding["advisory"] or "<no advisory url>"

    return f"{finding['id']} {finding['resolved']} [{severity}] {advisory} ({finding['framework']})"


def main(argv: list[str]) -> int:
    source = argv[1] if len(argv) > 1 else "-"

    try:
        text = sys.stdin.read() if source == "-" else pathlib.Path(source).read_text(encoding="utf-8")
    except OSError as error:
        print(f"check-vulnerable: cannot read the report: {error}", file=sys.stderr)

        return 2

    try:
        report = json.loads(text)
    except json.JSONDecodeError as error:
        print(f"check-vulnerable: the report is not JSON: {error}", file=sys.stderr)

        return 2

    if not isinstance(report, dict) or "projects" not in report:
        print(
            "check-vulnerable: the report has no 'projects' array — is this the output of "
            "`dotnet list package --vulnerable --include-transitive --format json`?",
            file=sys.stderr,
        )

        return 2

    today = datetime.date.today()
    findings = vulnerabilities(report)
    blocked = [
        finding
        for finding in findings
        if (finding["severity"] in BLOCKING_SEVERITIES or not finding["severity"])
        and not is_allowed(finding, today)
    ]

    for finding in findings:
        print(f"check-vulnerable: {describe(finding)}")

    if blocked:
        print(
            f"check-vulnerable: {len(blocked)} high or critical advisory(ies) in the dependency graph — "
            "bump the package or add a time-boxed allow-list entry in tools/ci/check-vulnerable.py",
            file=sys.stderr,
        )

        return 1

    print(f"check-vulnerable: no blocking advisory ({len(findings)} advisory(ies) reported, none blocking)")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
