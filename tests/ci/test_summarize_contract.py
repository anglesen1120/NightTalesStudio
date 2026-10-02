"""Tests for tools/ci/summarize-contract.py (09.9.5-T1, security finding SEC-01).

The summary is written into `$GITHUB_STEP_SUMMARY` from the findings of the PR's own OpenAPI document,
so every field in it is attacker-controlled: a path or a finding text could carry a link, an image, a
code span or an HTML tag. The table must render all of them as literal text.

Run:
    python3 -m unittest discover -s tests/ci -v
    python3 -m pytest tests/ci
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "tools" / "ci" / "summarize-contract.py"

#: The five table columns plus the two borders: a cell that leaks a `|` adds a sixth separator.
ROW_SEPARATORS = 6


def finding(**overrides: str) -> dict:
    """One ERR finding, shaped like oasdiff's `--format json` output."""
    entry = {
        "id": "response-required-property-removed",
        "text": "removed the required property `title` from the response with the `200` status",
        "level": 3,
        "operation": "GET",
        "path": "/api/v1/series",
    }
    entry.update(overrides)
    return entry


class SummarizeContractTest(unittest.TestCase):
    """Every untrusted field renders as literal text, and the table never breaks."""

    def render(self, *findings: dict) -> str:
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "findings.json"
            path.write_text(json.dumps(list(findings) or [finding()]), encoding="utf-8")

            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(path)],
                capture_output=True,
                text=True,
                check=True,
            )

        return result.stdout

    def row(self, summary: str) -> str:
        """The one finding row of the table."""
        rows = [line for line in summary.splitlines() if line.startswith("| <code>")]
        self.assertEqual(len(rows), 1, summary)
        return rows[0]

    def test_a_finding_renders_as_a_table_with_the_remediation(self) -> None:
        # The positive control: without it, the escaping cases below could pass on an empty summary.
        summary = self.render()

        self.assertIn("| Level | Check | Operation | Path | Finding |", summary)
        self.assertIn("**1 ERR / 0 WARN finding(s).**", summary)
        self.assertIn("**Remediation:**", summary)

    def test_link_cannot_become_active(self) -> None:
        summary = self.render(finding(text="[click](https://evil.example)"))

        self.assertNotIn("](", summary)
        self.assertIn("&#91;click&#93;&#40;", summary)

    def test_image_cannot_become_active(self) -> None:
        summary = self.render(finding(text="![x](https://evil.example/i.png)"))

        self.assertNotIn("![", summary)

    def test_code_span_cannot_become_active(self) -> None:
        row = self.row(self.render(finding(text="`rm -rf /`")))

        # The remediation paragraph below the table is ours and keeps its own backticks, so the
        # assertion is scoped to the finding row.
        self.assertNotIn("`", row)
        self.assertIn("&#96;rm -rf /&#96;", row)

    def test_html_tag_cannot_become_active(self) -> None:
        summary = self.render(finding(text="<img src=x onerror=alert(1)>"))

        row = self.row(summary)
        self.assertNotIn("<img", row)
        self.assertIn("&lt;img src=x onerror=alert&#40;1&#41;&gt;", row)

    def test_a_backslash_escaped_pipe_cannot_break_the_row(self) -> None:
        summary = self.render(finding(text=r"a \| b"))

        self.assertIn("&#92;&#124;", summary)
        self.assertEqual(self.row(summary).count("|"), ROW_SEPARATORS, self.row(summary))

    def test_a_bare_pipe_cannot_break_the_row(self) -> None:
        summary = self.render(finding(path="/api/v1/series|/api/v1/episodes"))

        self.assertEqual(self.row(summary).count("|"), ROW_SEPARATORS, self.row(summary))

    def test_cr_and_lf_cannot_break_the_row(self) -> None:
        summary = self.render(finding(text="first\r\nsecond\nthird\rfourth"))

        row = self.row(summary)
        self.assertIn("first second third fourth", row)
        self.assertEqual(row.count("|"), ROW_SEPARATORS, row)

    def test_every_column_is_escaped(self) -> None:
        row = self.row(
            self.render(finding(id="<b>id</b>", operation="G`T", path="/api/v1/`x`", text="**bold**"))
        )

        for markup in ("<b>", "`", "**"):
            self.assertNotIn(markup, row)
        self.assertIn("&lt;b&gt;id&lt;/b&gt;", row)

    def test_missing_members_render_as_a_dash(self) -> None:
        summary = self.render({"level": 2})

        self.assertIn("| <code>WARN</code> | — | — | — | — |", summary)

    def test_unreadable_input_is_reported_without_a_traceback(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "findings.json"
            path.write_bytes(b"\xff\xfe[]")

            result = subprocess.run(
                [sys.executable, str(SCRIPT), str(path)],
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("The summary is unavailable", result.stdout)
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
