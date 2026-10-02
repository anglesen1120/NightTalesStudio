#!/usr/bin/env python3
"""Write the `contract` job's oasdiff findings as a Markdown table for the run summary.

    python3 tools/ci/summarize-contract.py <findings.json>

The file is oasdiff's `--format json` output (see `tools/ci/check-version-rule.py`). The summary is
the operator-facing surface of the gate (09.9-platform plan §6): it lists every finding oasdiff
reported — ERR and WARN — and the remediation, so a reviewer of the failed PR does not have to read
the raw step log.

This script never fails the step: the versioning rule is what fails the job, and a summary that
cannot read its input has to say so rather than hide it. Exit code is always 0.
"""

from __future__ import annotations

import json
import pathlib
import sys

LEVEL_WARN = 2
LEVEL_ERR = 3

LEVEL_NAMES = {1: "INFO", 2: "WARN", 3: "ERR"}

#: Every character a finding can carry that would start markup in the run summary, as a numeric
#: entity. One `str.translate` pass, so nothing produced here is escaped or re-escaped by a second
#: pass. `&`, `<`, `>`, `"` and `'` cover HTML; the rest are Markdown's inline syntax, which a raw
#: `<code>` element does **not** suppress (a `[link](…)` or `` `code` `` inside it is still parsed),
#: so a PR-controlled path or finding text could otherwise inject a link, an image or a tag into
#: `$GITHUB_STEP_SUMMARY` (SEC-01).
SUMMARY_ESCAPE = str.maketrans(
    {
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        '"': "&quot;",
        "'": "&#39;",
        "\\": "&#92;",
        "`": "&#96;",
        "*": "&#42;",
        "_": "&#95;",
        "[": "&#91;",
        "]": "&#93;",
        "(": "&#40;",
        ")": "&#41;",
        "!": "&#33;",
        "#": "&#35;",
        "~": "&#126;",
        "|": "&#124;",
    }
)

HEADING = "## OpenAPI contract diff (09.9.5-T1)"

REMEDIATION = (
    "**Remediation:** keep `/api/v1` exactly as it is and add the changed shape under a new prefix "
    "(e.g. `/api/v2`) with a major bump of `info.version`, or make the change additive. The rule is "
    "`tools/ci/check-version-rule.py` (09.9.5-D4)."
)


def read_findings(path: pathlib.Path) -> list[dict]:
    """oasdiff's findings, or a one-line reason the summary cannot be built."""
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise ValueError(f"the diff produced no findings file: {error}") from error
    except UnicodeError as error:
        raise ValueError(f"the findings file is not valid UTF-8: {error}") from error
    except json.JSONDecodeError as error:
        raise ValueError(f"the findings file is not JSON: {error}") from error

    if not isinstance(document, list):
        raise ValueError("the findings file is not an oasdiff findings array")

    return [finding for finding in document if isinstance(finding, dict)]


def cell(value: object) -> str:
    """One Markdown table cell: a missing member is a dash, everything else is literal text.

    The value comes from the PR's own document, so it is rendered as text and never as markup: a CR
    or LF would end the row, and :data:`SUMMARY_ESCAPE` turns every character that could start a tag,
    a link, an image, emphasis or a code span into a numeric entity.
    """
    if value in (None, ""):
        return "—"

    text = str(value).replace("\r\n", " ").replace("\r", " ").replace("\n", " ")
    return f"<code>{text.translate(SUMMARY_ESCAPE)}</code>"


def render(findings: list[dict]) -> list[str]:
    """The summary lines for one findings list."""
    if not findings:
        return [
            HEADING,
            "",
            "No breaking finding: the PR's document is identical to `main`'s, or every change oasdiff "
            "classifies as breaking is absent (INFO-level changes are not reported by "
            "`oasdiff breaking`).",
        ]

    lines = [
        HEADING,
        "",
        "| Level | Check | Operation | Path | Finding |",
        "|---|---|---|---|---|",
    ]
    for finding in sorted(findings, key=lambda item: -int(item.get("level") or 0)):
        level = LEVEL_NAMES.get(int(finding.get("level") or 0), str(finding.get("level")))
        lines.append(
            "| {level} | {check} | {operation} | {path} | {text} |".format(
                level=cell(level),
                check=cell(finding.get("id")),
                operation=cell(finding.get("operation")),
                path=cell(finding.get("path")),
                text=cell(finding.get("text")),
            )
        )

    errors = sum(1 for finding in findings if int(finding.get("level") or 0) >= LEVEL_ERR)
    warnings = sum(1 for finding in findings if int(finding.get("level") or 0) == LEVEL_WARN)
    lines += [
        "",
        f"**{errors} ERR / {warnings} WARN finding(s).** WARN findings are reported but never block; "
        "ERR findings pass only for a versioned breaking change.",
        "",
        REMEDIATION,
    ]
    return lines


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: summarize-contract.py <findings.json>", file=sys.stderr)
        return 0

    try:
        lines = render(read_findings(pathlib.Path(argv[1])))
    except ValueError as error:
        lines = [HEADING, "", f"The summary is unavailable: {error}"]

    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
