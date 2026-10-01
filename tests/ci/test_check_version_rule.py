"""Tests for the OpenAPI contract gate of 09.9.5-T1.

Two layers:

* `VersioningRuleTest` drives `tools/ci/check-version-rule.py` as a subprocess — the same entry point
  the `contract` job runs — with crafted findings and the committed fixture specs, so the rule's
  decision and its inputs are exercised together.
* `FixtureOasdiffTest` proves the fixture pair itself: it runs the pinned oasdiff binary (the one
  `tools/ci/install-oasdiff.sh` installs) over `tests/fixtures/openapi` and asserts the levels the
  rule tests assume, then feeds that output back through the rule.

Run:
    python3 -m unittest discover -s tests/ci -v
    python3 -m pytest tests/ci                      # the classes are unittest.TestCase, so pytest
                                                    # collects them too
    python3 tools/ci/check-version-rule.py --selftest   # no runner and no oasdiff needed

`FixtureOasdiffTest` is skipped when no oasdiff binary is on the PATH or in `OASDIFF_BIN`; the other
cases need neither oasdiff nor pytest.
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "tools" / "ci" / "check-version-rule.py"
FIXTURES = REPO_ROOT / "tests" / "fixtures" / "openapi"

#: The oasdiff the gate installs, when a developer has one (install-oasdiff.sh puts it in ./bin). A
#: stale `OASDIFF_BIN` path must skip the class, not fail it.
def _installed_oasdiff() -> str | None:
    candidate = os.environ.get("OASDIFF_BIN") or shutil.which("oasdiff")
    if candidate and os.path.isfile(candidate) and os.access(candidate, os.X_OK):
        return candidate
    return None


OASDIFF = _installed_oasdiff()

#: The one ERR finding the `breaking` fixtures produce (`response-required-property-removed`).
ERROR_TEXT = "removed the required property `title` from the response with the `200` status"


def error_finding(text: str = ERROR_TEXT) -> dict:
    """One ERR finding, shaped exactly like oasdiff's `--format json` output."""
    return {
        "id": "response-required-property-removed",
        "text": text,
        "level": 3,
        "operation": "GET",
        "path": "/api/v1/series",
        "section": "paths",
        "fingerprint": "afdd277dd626",
    }


def warning_finding() -> dict:
    """One WARN finding: reported in the summary, never blocking."""
    return {
        "id": "api-security-removed",
        "text": "removed the security requirement",
        "level": 2,
        "operation": "GET",
        "path": "/api/v1/series",
    }


class VersioningRuleTest(unittest.TestCase):
    """The rule's decision, one case per acceptance check of the task file."""

    def setUp(self) -> None:
        self.work = pathlib.Path(tempfile.mkdtemp(prefix="nts-contract-rule-"))
        self.addCleanup(shutil.rmtree, self.work, ignore_errors=True)

    def spec(self, name: str) -> pathlib.Path:
        return FIXTURES / f"{name}.json"

    def rule(self, findings: list[dict], base: pathlib.Path, head: pathlib.Path):
        """Run the rule over one findings list and two specs, as the `contract` job does."""
        findings_path = self.work / "findings.json"
        findings_path.write_text(json.dumps(findings), encoding="utf-8")

        return subprocess.run(
            [sys.executable, str(SCRIPT), str(findings_path), str(base), str(head)],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_additive_change_passes(self) -> None:
        result = self.rule([], self.spec("base"), self.spec("additive"))

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("additive-only change", result.stdout)

    def test_warning_finding_does_not_block(self) -> None:
        result = self.rule([warning_finding()], self.spec("base"), self.spec("breaking"))

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("none at ERR", result.stdout)

    def test_breaking_change_without_version_fails(self) -> None:
        result = self.rule([error_finding()], self.spec("base"), self.spec("breaking"))

        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("unversioned breaking change", result.stdout)
        # The finding list is what the reviewer of the failed PR reads, so it has to be there.
        self.assertIn(ERROR_TEXT, result.stdout)
        self.assertIn("response-required-property-removed", result.stdout)

    def test_breaking_change_with_new_prefix_and_major_bump_passes(self) -> None:
        result = self.rule([error_finding()], self.spec("base"), self.spec("versioned-breaking"))

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("versioned breaking change accepted", result.stdout)
        self.assertIn("/api/v2", result.stdout)

    def test_removed_operation_fails_even_with_a_new_prefix(self) -> None:
        # A versioned prefix and a major bump are not a licence to drop an operation a shipped client
        # already calls: `/api/v1/series` has to stay, so the rule refuses this head document.
        versioned = json.loads(self.spec("versioned-breaking").read_text(encoding="utf-8"))
        del versioned["paths"]["/api/v1/series"]
        head = self.work / "head.json"
        head.write_text(json.dumps(versioned), encoding="utf-8")

        result = self.rule([error_finding()], self.spec("base"), head)

        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("unversioned breaking change", result.stdout)
        self.assertIn("GET /api/v1/series", result.stdout)

    def test_missing_findings_file_fails_closed(self) -> None:
        result = subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                str(self.work / "absent.json"),
                str(self.spec("base")),
                str(self.spec("additive")),
            ],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("fail closed", result.stderr)

    def test_malformed_spec_fails_closed(self) -> None:
        malformed = self.work / "malformed.json"
        malformed.write_text("{ not json", encoding="utf-8")
        findings = self.work / "findings.json"
        findings.write_text("[]", encoding="utf-8")

        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(findings), str(malformed), str(self.spec("additive"))],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("fail closed", result.stderr)

    def test_invalid_utf8_fails_closed(self) -> None:
        # A findings file that is not UTF-8 must not escape as a traceback: `UnicodeDecodeError` is a
        # `ValueError`, not an `OSError`, so it needs its own clause at the read boundary (CR-1).
        findings = self.work / "findings.json"
        findings.write_bytes(b"\xff\xfe[]")

        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(findings), str(self.spec("base")), str(self.spec("additive"))],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("fail closed", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_selftest_passes(self) -> None:
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--selftest"],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("selftest passed", result.stdout)


@unittest.skipUnless(OASDIFF, "no oasdiff binary: set OASDIFF_BIN or run tools/ci/install-oasdiff.sh")
class FixtureOasdiffTest(unittest.TestCase):
    """The fixtures produce the findings the rule tests above assume."""

    def oasdiff_findings(self, head: str) -> list[dict]:
        result = subprocess.run(
            [
                OASDIFF,
                "breaking",
                "--format",
                "json",
                str(FIXTURES / "base.json"),
                str(FIXTURES / f"{head}.json"),
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        return json.loads(result.stdout)

    def test_additive_fixture_has_no_breaking_finding(self) -> None:
        self.assertEqual(self.oasdiff_findings("additive"), [])

    def test_breaking_fixture_has_exactly_one_err_finding(self) -> None:
        findings = self.oasdiff_findings("breaking")

        self.assertEqual([finding["level"] for finding in findings], [3])
        self.assertEqual(findings[0]["id"], "response-required-property-removed")

    def test_versioned_breaking_fixture_has_the_same_err_finding(self) -> None:
        # The fixture pair proves the rule, not the tool: the head carries the same break as
        # `breaking.json` and is accepted only because of /api/v2 and the major bump.
        findings = self.oasdiff_findings("versioned-breaking")

        self.assertEqual([finding["level"] for finding in findings], [3])
        self.assertEqual(findings[0]["id"], "response-required-property-removed")

    def test_real_oasdiff_output_drives_the_rule(self) -> None:
        expected = {"additive": 0, "breaking": 1, "versioned-breaking": 0}
        for head, returncode in expected.items():
            with self.subTest(head=head):
                result = subprocess.run(
                    [
                        sys.executable,
                        str(SCRIPT),
                        "-",
                        str(FIXTURES / "base.json"),
                        str(FIXTURES / f"{head}.json"),
                    ],
                    input=json.dumps(self.oasdiff_findings(head)),
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(result.returncode, returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
