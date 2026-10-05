"""Fixture-driven tests for the phase-P legal-version CI guard (13.2.1-T1)."""

from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "tools/ci/check-legal-versions.py"
FIXTURES = REPO_ROOT / "tests/fixtures/legal"


class LegalVersionsTest(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory(prefix="nts-legal-rule-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / "case"
        shutil.copytree(FIXTURES / "consistent", self.root)
        self.manifest = self.root / "manifest.json"
        self.content = self.root / "content"
        self.api = self.root / "api.json"
        self.worker = self.root / "worker.json"

    def rule(self, *extra: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--manifest", str(self.manifest),
             "--legal-dir", str(self.content), "--api-settings", str(self.api),
             "--worker-settings", str(self.worker), *extra],
            cwd=self.directory.name,
            capture_output=True, text=True, check=False,
        )

    def assert_failure(self, expected: str) -> subprocess.CompletedProcess[str]:
        result = self.rule()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn(expected, result.stderr)
        self.assertNotIn("OK", result.stdout)
        return result

    def set_version(self, version: object) -> None:
        document = json.loads(self.manifest.read_text(encoding="utf-8"))
        document["tos"]["version"] = version
        self.manifest.write_text(json.dumps(document), encoding="utf-8")

    def test_consistent_fixture_passes_from_another_directory(self) -> None:
        result = self.rule()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("legal-versions: OK", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_manifest_missing_document_fails(self) -> None:
        shutil.copyfile(FIXTURES / "missing-document.json", self.manifest)
        self.assert_failure("missing or invalid tos document entry")

    def test_manifest_missing_privacy_fails(self) -> None:
        self.manifest.write_text('{"tos": {"version": "1.0.0"}}', encoding="utf-8")
        self.assert_failure("missing or invalid privacy document entry")

    def test_non_semver_fails(self) -> None:
        shutil.copyfile(FIXTURES / "non-semver.json", self.manifest)
        self.assert_failure("tos version must be digits.digits.digits")

    def test_semver_boundaries(self) -> None:
        for value in (None, 1, "", "1.0", "1.0.0-beta", "1.0.0\n", "../1.0.0", "١.٠.٠", "1" * 29 + ".0.0"):
            with self.subTest(version=value):
                self.set_version(value)
                self.assert_failure("tos version must be digits.digits.digits")
        version = "1" * 28 + ".0.0"  # Exactly 32 characters.
        self.set_version(version)
        for locale in ("vi", "en"):
            shutil.copyfile(self.content / f"terms.1.0.0.{locale}.md", self.content / f"terms.{version}.{locale}.md")
        for settings in (self.api, self.worker):
            settings.write_text(json.dumps({"LegalDocuments": {"Current": {"Tos": version, "Privacy": "1.0.0"}}}), encoding="utf-8")
        result = self.rule()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_en_fails(self) -> None:
        (self.content / "terms.1.0.0.en.md").unlink()
        self.assert_failure("missing or unreadable tos en file")

    def test_missing_vi_fails(self) -> None:
        (self.content / "privacy.1.0.0.vi.md").unlink()
        self.assert_failure("missing or unreadable privacy vi file")

    def test_heading_count_drift_fails(self) -> None:
        shutil.copyfile(FIXTURES / "heading-drift.en.md", self.content / "terms.1.0.0.en.md")
        self.assert_failure("tos heading drift between vi/en")

    def test_added_unnumbered_h2_is_heading_drift(self) -> None:
        path = self.content / "terms.1.0.0.en.md"
        path.write_text(path.read_text(encoding="utf-8") + "\n## Extra section\n", encoding="utf-8")
        self.assert_failure("tos heading drift between vi/en")

    def test_heading_levels_drift_fails_with_same_count(self) -> None:
        path = self.content / "privacy.1.0.0.en.md"
        path.write_text(path.read_text(encoding="utf-8").replace("### Details", "## 2. Details"), encoding="utf-8")
        self.assert_failure("privacy heading drift between vi/en")

    def test_numbered_heading_slots_follow_parity_precedent(self) -> None:
        path = self.content / "terms.1.0.0.en.md"
        path.write_text(path.read_text(encoding="utf-8").replace("## 1.", "## 2."), encoding="utf-8")
        self.assert_failure("tos heading drift between vi/en")

    def test_api_settings_differing_fails(self) -> None:
        shutil.copyfile(FIXTURES / "config-drift.json", self.api)
        result = self.assert_failure('LegalDocuments:Current:Tos "1.0.1" differs from manifest "1.0.0"')
        self.assertIn(str(self.api), result.stderr)

    def test_worker_settings_differing_fails(self) -> None:
        shutil.copyfile(FIXTURES / "config-drift.json", self.worker)
        result = self.assert_failure('LegalDocuments:Current:Tos "1.0.1" differs from manifest "1.0.0"')
        self.assertIn(str(self.worker), result.stderr)

    def test_missing_settings_keys_fail(self) -> None:
        for value in ({}, {"LegalDocuments": []}, {"LegalDocuments": {"Current": {}}}):
            with self.subTest(settings=value):
                self.worker.write_text(json.dumps(value), encoding="utf-8")
                result = self.assert_failure("missing or invalid LegalDocuments:Current:Tos")
                self.assertIn("missing or invalid LegalDocuments:Current:Privacy", result.stderr)

    def test_all_independent_violations_are_reported(self) -> None:
        self.manifest.write_text('{}', encoding="utf-8")
        self.api.write_text('{}', encoding="utf-8")
        self.worker.write_text('{}', encoding="utf-8")
        result = self.assert_failure("missing or invalid tos document entry")
        self.assertEqual(len(result.stderr.splitlines()), 6)

    def test_unreadable_inputs_fail_closed(self) -> None:
        self.manifest.unlink()
        self.api.write_bytes(b"\xff\xfe")
        self.assert_failure("cannot read UTF-8 JSON")

    def test_invalid_json_and_schema_fail_closed(self) -> None:
        for text in ("{not JSON", "[]", "null"):
            with self.subTest(text=text):
                self.manifest.write_text(text, encoding="utf-8")
                result = self.rule()
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn(str(self.manifest), result.stderr)

    def test_manifest_comments_are_not_accepted(self) -> None:
        self.manifest.write_text('{// not appsettings\n}', encoding="utf-8")
        self.assert_failure("invalid JSON")

    def test_comment_parser_preserves_strings_and_line_numbers(self) -> None:
        self.api.write_text('{\n// comment\n"Url": "https://example.invalid/*path*/",\n"Quoted": "\\\"//not a comment",\n"LegalDocuments": {"Current": {"Tos": "1.0.0", "Privacy": "1.0.0"}}\n}', encoding="utf-8")
        self.assertEqual(self.rule().returncode, 0)
        self.api.write_text('{\n// comment\ninvalid\n}', encoding="utf-8")
        self.assert_failure(": 3: invalid JSON")

    def test_malformed_multiline_config_has_one_line_diagnostic(self) -> None:
        self.api.write_text(json.dumps({"LegalDocuments": {"Current": {"Tos": "1.0.0\nbad", "Privacy": "1.0.0"}}}), encoding="utf-8")
        result = self.assert_failure("differs from manifest")
        self.assertEqual(len(result.stderr.splitlines()), 1)

    def test_selftest_passes(self) -> None:
        result = subprocess.run([sys.executable, str(SCRIPT), "--selftest"], cwd=self.directory.name, capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("selftest passed (7 cases)", result.stdout)

    def test_usage_errors_return_two(self) -> None:
        for arguments in (("--unknown",), ("--manifest",), ("--selftest", "--manifest", str(self.manifest))):
            with self.subTest(arguments=arguments):
                result = subprocess.run([sys.executable, str(SCRIPT), *arguments], capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertIn("usage:", result.stderr)


if __name__ == "__main__":
    unittest.main()
