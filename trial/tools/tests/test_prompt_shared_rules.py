#!/usr/bin/env python3
"""Static checks for the M0 shared prompt rule files (plan 02.1).

Covers the Tests listed by the two task files:

  plans/features/02-story-writing/02.1-prompt-presets/02.1.2-T1-keyword-rules-examples.md
  plans/features/02-story-writing/02.1-prompt-presets/02.1.5-T1-ear-rules-overused-phrases.md

Manual / not-automatable checks are skipped with a reason:
  Harness_KeywordCounts_WithinCap      (needs the 02.1.3-T0 harness output)
  read one harness output aloud        (human; feeds the A/B natural_language score)

Run (no project, stdlib only):

  uv run --no-project --python 3.12 trial/tools/tests/test_prompt_shared_rules.py

The manifest checks read ``prompts/manifest.json``. To prove the *proposed*
entries without editing that (protected) file, point NTS_MANIFEST at a copy:

  NTS_MANIFEST=/tmp/manifest-with-entries.json uv run --no-project --python 3.12 \\
      trial/tools/tests/test_prompt_shared_rules.py
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SHARED = REPO / "prompts" / "shared"

KEYWORD_RULES = SHARED / "keyword-rules.v1.md"
EAR_VI = SHARED / "ear-rules.vi.v1.md"
EAR_EN = SHARED / "ear-rules.en.v1.md"
OVERUSED_VI = SHARED / "overused.vi.v1.txt"
OVERUSED_EN = SHARED / "overused.en.v1.txt"

MANIFEST = Path(os.environ.get("NTS_MANIFEST", REPO / "prompts" / "manifest.json"))

TOPICS = ("ghost", "spiritual", "true_crime")

# The hand-off text for 02.6.1-T1 (frozen here so its properties are checked; the
# consuming task pastes it into the review prompt). Acceptance: Q1 lists the four
# functions, Q2 mentions the emotion the scene already shows, and no question
# tells the reviewer to delete a phrase.
NATURALNESS_QUESTIONS = """\
NATURALNESS QUESTIONS (answer per flagged or suspect sentence; do not delete words mechanically)
Q1 Function: does this sentence move one of - fear, clue, choice, consequence? If none, say which one it should move and how.
Q2 Redundancy: does the scene already show this (an emotion named that the action already carries, a reaction the image already gives)? If yes, the sentence is the problem, not the word.
Q3 Explanation: is the sentence explaining what the reader/listener can already infer? (tension, motive, atmosphere) - prefer the shorter version that trusts the listener.
Q4 Overused phrase: if a phrase from overused.{lang}.v1.txt appears, does it do work here (once, deliberately) or is it filler? One deliberate use is allowed.
Q5 Read-aloud: read the paragraph at narration pace. Where do you stumble or want to go back? Quote the exact clause.
Q6 Register (vi): is this written Vietnamese rather than spoken? Name the written construction and give the spoken alternative.
"""

# spot-check; manual review is the gate. Deliberately small denylist of real-world
# brands and real person names, matched case-insensitively as substrings against
# all five shared files.
REAL_BRANDS = (
    "nike", "adidas", "coca-cola", "coca cola", "pepsi", "mcdonald", "starbucks",
    "google", "facebook", "youtube", "tiktok", "instagram", "netflix", "disney",
    "marvel", "apple", "samsung", "iphone", "amazon", "microsoft", "tesla",
    "bmw", "toyota", "honda", "grab", "shopee", "vinamilk", "vnpt", "viettel",
    "vinaphone", "mobifone",
)
REAL_NAMES = (
    "donald trump", "joe biden", "barack obama", "vladimir putin", "elon musk",
    "nguyen phu trong", "ho chi minh", "vo nguyen giap", "taylor swift",
    "cristiano ronaldo", "lionel messi", "kim jong",
)
URL_RE = re.compile(r"(?:https?://|www\.)", re.IGNORECASE)
# A bare domain ('example.com', 'nts.vn') is a URL too; URL_RE alone misses it.
DOMAIN_RE = re.compile(r"\b[a-z0-9][a-z0-9-]*\.(?:com|net|org|vn|io|co|info|edu)\b", re.IGNORECASE)
# Vietnamese precomposed letters (Latin-1 Supplement minus the signs, Latin
# Extended-A, Latin Extended Additional). 'x' (U+00D7) and '/' (U+00F7) are not
# letters and must not match, so they are excluded.
DIACRITIC_RE = re.compile("[\u00c0-\u00d6\u00d8-\u00f6\u00f8-\u00ff\u0100-\u017f\u1e00-\u1eff]")


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def front_matter(text: str) -> dict[str, str]:
    m = re.match(r"\A---\n(.*?)\n---\n", text, re.DOTALL)
    if not m:
        raise AssertionError("no '---'-fenced front-matter block at the top of the file")
    out: dict[str, str] = {}
    for line in m.group(1).splitlines():
        if not line.strip():
            continue
        key, _, value = line.partition(":")
        out[key.strip()] = value.strip()
    return out


def numbered_rows(text: str, letter: str) -> dict[int, str]:
    """Rows of a `| # | ... |` table whose first cell is R<n>/E<n>."""
    rows: dict[int, str] = {}
    for line in text.splitlines():
        m = re.match(rf"^\|\s*{letter}(\d+)\s*\|(.+)\|\s*$", line)
        if m:
            rows[int(m.group(1))] = m.group(2)
    return rows


def table_rows(section_lines: list[str]) -> list[list[str]]:
    rows: list[list[str]] = []
    for line in section_lines:
        s = line.strip()
        if not s.startswith("|"):
            continue
        cells = [c.strip() for c in s.strip("|").split("|")]
        if len(cells) == 3 and cells[0].isdigit():
            rows.append(cells)
    return rows


def sections(text: str) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    current: str | None = None
    for line in text.splitlines():
        m = re.match(r"^#{2,4} (.+?)\s*$", line)
        if m:
            current = m.group(1).strip()
            continue
        if current is not None:
            out.setdefault(current, []).append(line)
    return out


class FrontMatterAndHygiene(unittest.TestCase):
    """Acceptance: the shared rule files parse front-matter (preset-spec §1)."""

    def test_KeywordRules_FrontMatter_Contract(self) -> None:
        fm = front_matter(read(KEYWORD_RULES))
        self.assertEqual(fm.get("id"), "keyword-rules")
        self.assertEqual(fm.get("role"), "shared")
        self.assertEqual(fm.get("version"), "v1")
        self.assertEqual(fm.get("target_tier"), "all")
        self.assertNotIn("topic", fm)
        self.assertNotIn("language", fm)

    def test_EarRules_FrontMatter_Contract(self) -> None:
        for path, lang in ((EAR_VI, "vi"), (EAR_EN, "en")):
            with self.subTest(file=path.name):
                fm = front_matter(read(path))
                self.assertEqual(fm.get("id"), f"ear-rules.{lang}")
                self.assertEqual(fm.get("role"), "shared")
                self.assertEqual(fm.get("version"), "v1")
                self.assertEqual(fm.get("target_tier"), "all")
                self.assertEqual(fm.get("language"), lang)
                self.assertNotIn("topic", fm)

    def test_SharedRuleFiles_NoLiteralPlaceholderToken(self) -> None:
        """A literal token would be substituted into a preset and read back as
        an unresolved placeholder by the startup check (02.2.3-T1)."""
        for path in (KEYWORD_RULES, EAR_VI, EAR_EN, OVERUSED_VI, OVERUSED_EN):
            with self.subTest(file=path.name):
                text = read(path)
                self.assertNotIn("{", text.replace("{{", ""), f"{path.name} has a stray brace")
                self.assertNotIn("{{", text, f"{path.name} contains a literal placeholder token")

    def test_SharedRuleFiles_Utf8LfNoBomFinalNewline(self) -> None:
        for path in (KEYWORD_RULES, EAR_VI, EAR_EN, OVERUSED_VI, OVERUSED_EN):
            with self.subTest(file=path.name):
                raw = path.read_bytes()
                self.assertNotIn(b"\r", raw, f"{path.name} has CRLF/CR bytes")
                self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), f"{path.name} has a BOM")
                self.assertTrue(raw.endswith(b"\n"), f"{path.name} must end with one LF")
                self.assertFalse(raw.endswith(b"\n\n"), f"{path.name} has a trailing blank line")
                raw.decode("utf-8")


class KeywordRules(unittest.TestCase):
    """02.1.2-T1."""

    def test_KeywordRules_RuleNumbers_PresentAndUnique(self) -> None:
        text = read(KEYWORD_RULES)
        # `numbered_rows` keys a dict by rule number, so it hides a duplicated
        # row: count the raw table lines as well.
        raw = [int(n) for n in re.findall(r"^\|\s*R(\d+)\s*\|", text, re.MULTILINE)]
        self.assertEqual(sorted(raw), list(range(1, 14)), f"R1..R13, each exactly once, got {sorted(raw)}")
        rows = numbered_rows(text, "R")
        self.assertIn("english only", rows[2].lower())
        self.assertRegex(rows[3], r"3\s*[\u2013-]\s*12\s+words")
        self.assertIn("likeness", rows[5].lower())
        self.assertRegex(rows[9], r"ceil\(D\s*[\u00d7x*]\s*6\)")
        self.assertIn("never blocking", rows[9].lower())
        # One content anchor per rewritten rule, so an emptied or reverted row
        # fails instead of passing on the row number alone (CR-02C-3).
        self.assertIn("no digits", rows[3].lower())
        self.assertIn("mood word", rows[4].lower())
        self.assertIn("minor", rows[6].lower())
        self.assertIn("banknotes", rows[7].lower())
        self.assertIn("address", rows[12].lower())
        self.assertIn("instruction", rows[13].lower())

    def test_KeywordRules_GoodBadExamples_PerTopicAtLeastTen(self) -> None:
        secs = sections(read(KEYWORD_RULES))
        for topic in TOPICS:
            with self.subTest(topic=topic):
                self.assertIn(topic, secs, f"no '### {topic}' example section")
                rows = table_rows(secs[topic])
                self.assertEqual(len(rows), 10, f"{topic}: expected 10 good/bad pairs, got {len(rows)}")
                for number, good, bad in rows:
                    words = good.split()
                    self.assertTrue(3 <= len(words) <= 12, f"{topic} #{number} good ({len(words)} words): {good!r}")
                    self.assertRegex(
                        good,
                        r"^[A-Za-z' -]+$",
                        f"{topic} #{number} good cell breaks R3 (letters, spaces, apostrophe, inner hyphen only): {good!r}",
                    )
                    self.assertRegex(bad, r"\bR\d+\b", f"{topic} #{number} bad row names no violated rule: {bad!r}")

    def test_KeywordRules_NoRealNamesOrBrands_InExamples(self) -> None:
        for path in (KEYWORD_RULES, EAR_VI, EAR_EN, OVERUSED_VI, OVERUSED_EN):
            with self.subTest(file=path.name):
                text = read(path).lower()
                for term in REAL_BRANDS + REAL_NAMES:
                    self.assertNotIn(term, text, f"real brand/person name {term!r} appears in {path.name}")
                self.assertIsNone(URL_RE.search(text), f"a URL appears in {path.name}")
                self.assertIsNone(DOMAIN_RE.search(text), f"a bare domain appears in {path.name}")

    def test_KeywordRules_EnglishOnly_NoVietnameseInExamples(self) -> None:
        for path in (KEYWORD_RULES,):
            with self.subTest(file=path.name):
                hits = [
                    (n, line)
                    for n, line in enumerate(read(path).splitlines(), 1)
                    if DIACRITIC_RE.search(line)
                ]
                self.assertEqual(hits, [], f"{path.name} must be English-only; diacritic lines: {hits}")


class EarRules(unittest.TestCase):
    """02.1.5-T1."""

    def test_EarRules_ViEn_SameRuleNumbering(self) -> None:
        vi, en = numbered_rows(read(EAR_VI), "E"), numbered_rows(read(EAR_EN), "E")
        expected = list(range(1, 11))
        self.assertEqual(sorted(vi), expected, f"ear-rules.vi E1..E10, got {sorted(vi)}")
        self.assertEqual(sorted(en), expected, f"ear-rules.en E1..E10, got {sorted(en)}")

    def test_EarRules_NumbersDates_SpokenForm(self) -> None:
        vi, en = numbered_rows(read(EAR_VI), "E"), numbered_rows(read(EAR_EN), "E")
        self.assertIn("ba gi\u1edd s\u00e1ng", vi[3])
        self.assertIn("m\u00f9ng m\u1ed9t", vi[3])
        self.assertIn("three in the morning", en[3])
        self.assertIn("nineteen ninety-eight", en[3])

    def test_EarRules_AntiMechanicalRule_Present(self) -> None:
        for path in (EAR_VI, EAR_EN):
            with self.subTest(file=path.name):
                e10 = numbered_rows(read(path), "E")[10].lower()
                self.assertIn("prompt input", e10)
                self.assertIn("not a ban", e10)
                self.assertIn("once, deliberately", e10)
                self.assertIn("overused_phrases", e10)

    def test_EarRules_ViDiacriticsEnContractions_RulePresent(self) -> None:
        vi = numbered_rows(read(EAR_VI), "E")[9]
        en = numbered_rows(read(EAR_EN), "E")[9]
        self.assertIn("diacritic", vi.lower())
        self.assertIn("contraction", en.lower())
        # writer.v1.md allows first_person and third_person, so E9 must not lock
        # the vi address form to first person only (CR-02C-7).
        self.assertIn("third_person", vi)
        self.assertIn("third_person", en)

    def test_EarRules_EveryRuleRow_HasRuleText(self) -> None:
        """A gutted row must fail instead of silently passing (CR-02C-3)."""
        for path in (EAR_VI, EAR_EN):
            with self.subTest(file=path.name):
                rows = numbered_rows(read(path), "E")
                self.assertEqual(sorted(rows), list(range(1, 11)), f"{path.name}: E1..E10, got {sorted(rows)}")
                for number, row in rows.items():
                    cells = [c.strip() for c in row.strip("|").split("|")]
                    self.assertTrue(cells and cells[0], f"{path.name} E{number} rule cell is empty: {row!r}")

    def test_EarRules_E1_E2_E5_ContentPresent(self) -> None:
        for path, unit in ((EAR_VI, "syllables"), (EAR_EN, "words")):
            with self.subTest(file=path.name):
                e = numbered_rows(read(path), "E")
                self.assertIn("short spoken sentences", e[1].lower())
                self.assertIn(unit, e[1].lower())
                self.assertIn("parentheses", e[2].lower())
                self.assertIn("markdown", e[2].lower())
                self.assertIn("emoji", e[2].lower())
                self.assertIn("one idea per sentence", e[5].lower())


class OverusedLists(unittest.TestCase):
    """02.1.5-T1."""

    def test_OverusedLists_ViEn_AtLeastThirtyEntries(self) -> None:
        for path in (OVERUSED_VI, OVERUSED_EN):
            with self.subTest(file=path.name):
                lines = read(path).splitlines()
                self.assertEqual(len(lines), 30, f"{path.name}: expected 30 lines, got {len(lines)}")
                self.assertEqual(len(set(lines)), 30, f"{path.name}: duplicate phrases")
                for line in lines:
                    self.assertTrue(line.strip(), f"{path.name}: empty line")
                    self.assertEqual(line, line.strip(), f"{path.name}: line has surrounding spaces: {line!r}")

    def test_OverusedLists_NoNumberingOrComments(self) -> None:
        for path in (OVERUSED_VI, OVERUSED_EN):
            with self.subTest(file=path.name):
                for n, line in enumerate(read(path).splitlines(), 1):
                    self.assertFalse(line.startswith("#"), f"{path.name}:{n} is a comment")
                    self.assertIsNone(re.match(r"^\s*(?:[-*+]|\d+[.)])\s", line), f"{path.name}:{n} is numbered: {line!r}")
                    self.assertNotIn("---", line, f"{path.name}:{n} looks like front-matter")


class NaturalnessQuestions(unittest.TestCase):
    """Hand-off text for 02.6.1-T1 (see the module constant)."""

    def test_NaturalnessQuestions_SixQuestions_FunctionalNotWordLevel(self) -> None:
        questions = [l for l in NATURALNESS_QUESTIONS.splitlines() if re.match(r"^Q\d\b", l)]
        self.assertEqual([q[:2] for q in questions], [f"Q{i}" for i in range(1, 7)])
        self.assertIn("fear", questions[0])
        self.assertIn("clue", questions[0])
        self.assertIn("choice", questions[0])
        self.assertIn("consequence", questions[0])
        self.assertIn("already show", questions[1].lower())
        for q in questions:
            self.assertNotIn("delete", q.lower(), f"a question must not be word-level: {q!r}")


class Manifest(unittest.TestCase):
    """02.1.2-T1 / 02.1.5-T1 (T-2)."""

    EXPECTED = {
        "shared/keyword-rules.v1.md": KEYWORD_RULES,
        "shared/ear-rules.vi.v1.md": EAR_VI,
        "shared/ear-rules.en.v1.md": EAR_EN,
        "shared/overused.vi.v1.txt": OVERUSED_VI,
        "shared/overused.en.v1.txt": OVERUSED_EN,
    }

    def _check(self, keys: tuple[str, ...]) -> None:
        manifest = json.loads(read(MANIFEST))
        files = manifest.get("files", {})
        for key in keys:
            with self.subTest(key=key):
                self.assertIn(
                    key,
                    files,
                    f"prompts/manifest.json is missing the entry {key!r}; add "
                    f'"{key}": {{ "version": "v1", "sha256": "<sha256sum>" }} (protected file - integrator edit)',
                )
                digest = hashlib.sha256(self.EXPECTED[key].read_bytes()).hexdigest()
                self.assertEqual(files[key].get("version"), "v1")
                self.assertEqual(files[key].get("sha256"), digest)

    def test_Manifest_KeywordRules_HasSha256AndVersion(self) -> None:
        self._check(("shared/keyword-rules.v1.md",))

    def test_Manifest_EarAndOverusedFiles_HasSha256AndVersion(self) -> None:
        self._check((
            "shared/ear-rules.vi.v1.md",
            "shared/ear-rules.en.v1.md",
            "shared/overused.vi.v1.txt",
            "shared/overused.en.v1.txt",
        ))


class Manual(unittest.TestCase):
    """Checks the task files mark manual / provider-bound."""

    @unittest.skip("manual: needs the 02.1.3-T0 harness output (total keywords <= ceil(D x 6), 1-2 per segment)")
    def test_Harness_KeywordCounts_WithinCap(self) -> None:
        raise AssertionError

    @unittest.skip("manual: read one harness output per language aloud against E1/E5/E8 and record the stumbles")
    def test_ReadAloud_OneOutputPerLanguage(self) -> None:
        raise AssertionError


if __name__ == "__main__":
    unittest.main(verbosity=2)
