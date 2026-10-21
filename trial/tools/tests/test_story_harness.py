"""Unit tests for trial/tools/story_harness.py + story_validator.py (02.1.3-T0).

Offline only: no network, no Keychain read, no paid call (the one redirect test
uses two loopback stub servers). Covers the task's Tests list plus the CLI and
robustness paths the acceptance checks need without the 02.1.1 presets
(dry-run with no key material, a missing preset/schema, a preset missing a
shared placeholder, an unknown model, a non-loopback tap, a tap that is down
with the Keychain untouched, the D10 `current_run` lock, stale files in a reused
cell dir, the strict D14 rates pre-flight) and the phase-D gate findings
(D-TIMEOUT, D-SHORTBEATS, D-FACTS, D-ATOMIC/D-retry-failed, D-HASH, D-FM,
D-RUNID, D-REDIRECT, D-SHAPE, D-LOCK, D-BOUND).

Run:
    uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests -p test_story_harness.py -v
"""

import hashlib
import http.server
import io
import json
import os
import re
import socket
import sys
import tempfile
import threading
import unicodedata
import unittest
from pathlib import Path
from unittest import mock

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import story_harness as sh  # noqa: E402
import story_validator as sv  # noqa: E402

LENGTH = json.loads((TOOLS / "LENGTH.v1.json").read_text(encoding="utf-8"))
LENGTH_PATH = TOOLS / "LENGTH.v1.json"
O3_PRICES = TOOLS / "tests" / "fixtures" / "prices-o3.json"
REPO_ROOT = TOOLS.parents[1]
REAL_PROMPTS = REPO_ROOT / "prompts"

STUB_PRESET = """---
id: ghost.vi
topic: ghost
language: vi
version: v1
role: preset
target_tier: all
---

Language {{language_name}} · {{tier_minutes}} min · {{token_range}} tokens · {{segment_range}} segments

{{keyword_rules}}

{{ear_rules}}

{{overused_phrases}}

{{structure_vocab}}
"""

COPIED_PROMPT_FILES = (
    "system/writer.v1.md",
    "shared/ear-rules.vi.v1.md",
    "shared/overused.vi.v1.txt",
    "shared/ear-rules.en.v1.md",
    "shared/overused.en.v1.txt",
    "shared/keyword-rules.v1.md",
    "shared/structure-vocab.v1.json",
    "schemas/story.v1.json",
)

# D-HASH: exactly the files load_prompt_bundle hashes for a vi ghost cell.
RELATIVE_PROMPT_FILES = (
    "system/writer.v1.md",
    "story/ghost.vi.v1.md",
    "shared/ear-rules.vi.v1.md",
    "shared/overused.vi.v1.txt",
    "shared/keyword-rules.v1.md",
    "shared/structure-vocab.v1.json",
    "schemas/story.v1.json",
)

COPIED_FIXTURE_FILES = ("briefs.vi.json", "briefs.en.json",
                        "fact-sheet.true_crime.v1.json")

# D-PRICES-TESTS: the tests carry their own prices file, so they never assert
# what trial/prices.json happens to contain today (a sol row or a cache_write
# rate may be added by 01 at any time).
LUNA_NO_CACHE_WRITE = {"gpt-6-luna": {"input": 0.10, "cached_input": 0.01,
                                      "output": 0.50}}

D10 = re.compile(r"^\d{8}-\d{4}-(vi|en)-(landscape|portrait)-\d{2}$")


def write_prices(root: Path, models: dict, name: str = "prices.json") -> Path:
    path = root / name
    path.write_text(json.dumps({"models": models}), encoding="utf-8")
    return path


def output_payload(story_doc, usage=None, status="completed", request_id="resp_test"):
    payload = {"id": request_id, "status": status,
               "output": [{"content": [{"type": "output_text",
                                        "text": json.dumps(story_doc, ensure_ascii=False)}]}]}
    if usage is not None:
        payload["usage"] = usage
    return payload


def limits_for(tier="short", lang="vi", **overrides):
    values = dict(LENGTH[tier][lang])
    values.update(overrides)
    return values


def toks(count, word="tu"):
    return " ".join([word] * count)


def vi_phrase(phrase, times):
    return " ".join([phrase] * times)


def story(**overrides):
    """A well-formed `short` vi story: 4 segments, 215 tokens, 1 keyword each.
    215 sits inside the T103 short vi band 195–260."""
    value = {
        "title": "Ngọn đèn trên tháp",
        "hook": {"text": vi_phrase("Đêm ấy tôi nghe tiếng chân", 9),
                 "keywords": ["empty staircase at midnight"], "fact_refs": []},
        "paragraphs": [
            {"beat": "setup", "text": vi_phrase("Tôi mở cửa nhưng không có ai", 9),
             "keywords": ["locked door in hallway"], "fact_refs": []},
            {"beat": "twist", "text": vi_phrase("Chiếc chìa khoá vẫn còn trên bàn", 9),
             "keywords": ["kitchen light left on"], "fact_refs": []},
        ],
        "ending": {"text": vi_phrase("Tôi không dám kể lại chuyện này", 5),
                   "keywords": ["quiet room at dawn"], "fact_refs": []},
        "description": "Một đêm mất ngủ trong ngôi nhà cũ.",
        "tags": ["ghost story", "old house"],
        "content_warnings": ["supernatural"],
        "structure": {"sub_genre": "family haunting", "setting": "old house",
                      "opening": "a whispered account",
                      "twist_type": "the narrator caused it",
                      "narrator_mode": "first_person"},
    }
    value.update(overrides)
    return value


def rules(issues):
    return [issue["rule"] for issue in issues]


def make_prompts_dir(root: Path, preset_body: str = STUB_PRESET,
                     write_preset: bool = True, write_schema: bool = True) -> Path:
    for relative in COPIED_PROMPT_FILES:
        if not write_schema and relative.startswith("schemas/"):
            continue
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text((REAL_PROMPTS / relative).read_text(encoding="utf-8"),
                          encoding="utf-8")
    (root / "fixtures").mkdir(parents=True, exist_ok=True)
    (root / "story").mkdir(parents=True, exist_ok=True)
    for name in COPIED_FIXTURE_FILES:
        source = REAL_PROMPTS / "fixtures" / name
        if source.exists():
            (root / "fixtures" / name).write_text(source.read_text(encoding="utf-8"),
                                                  encoding="utf-8")
    if write_preset:
        # FR-1: one preset per topic x language, so a two-file matrix assembles
        # every cell from its own language's preset + shared blocks.
        for topic in sh.TOPICS:
            for lang in sh.LANGS:
                (root / "story" / f"{topic}.{lang}.v1.md").write_text(preset_body,
                                                                      encoding="utf-8")
    return root


class CliCase(unittest.TestCase):
    """Shared CLI harness: temp prompts/out/NTS_HOME, no network, no Keychain."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.prompts = make_prompts_dir(self.root / "prompts")
        self.out = self.root / "stories"
        self.nts_home = self.root / "nts-home"
        self.prices = write_prices(self.root, LUNA_NO_CACHE_WRITE)
        self.env = mock.patch.dict(os.environ, {"NTS_HOME": str(self.nts_home)})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.addCleanup(self.tmp.cleanup)

    def argv(self, *extra, dry_run=True):
        base = ["--preset", "ghost", "--lang", "vi", "--brief", "Một người gác đèn.",
                "--prompts-dir", str(self.prompts), "--out", str(self.out),
                "--length", str(LENGTH_PATH), "--prices", str(self.prices)]
        if dry_run:
            base.append("--dry-run")
        return base + list(extra)

    def argv_briefs(self, *extra, dry_run=True, lang="vi", out=None):
        """Same harness argv but with no inline brief, so --brief-ids/--briefs-file win.
        `lang=None` omits `--lang`, i.e. FR-1's per-brief language selection."""
        base = ["--prompts-dir", str(self.prompts), "--out", str(out or self.out),
                "--length", str(LENGTH_PATH), "--prices", str(self.prices)]
        if lang:
            base += ["--lang", lang]
        if dry_run:
            base.append("--dry-run")
        return base + list(extra)


class TokenCountTest(unittest.TestCase):
    def test_token_count_vi_syllables_and_en_words_counts_nonempty_tokens(self):
        # vi: a token is a syllable (D4/V1); runs of whitespace count once.
        self.assertEqual(sv.token_count("Tôi  nghe   tiếng chân", "vi"), 4)
        self.assertEqual(sv.token_count("  \n\t ", "vi"), 0)
        self.assertEqual(sv.token_count("the old house by the sea", "en"), 6)
        # NFC normalisation, not NFD (a combining mark is not its own token).
        self.assertEqual(sv.token_count("tiếng", "vi"), 1)
        self.assertEqual(sv.token_count("tie\u0301ng", "vi"), 1)


class ValidateTest(unittest.TestCase):
    def test_validate_wellformed_short_story_returns_no_issues(self):
        self.assertEqual(sv.validate(story(), "short", "vi", limits_for()), [])

    def test_validate_title_over_100_returns_blocking_issue(self):
        issues = sv.validate(story(title="x" * 101), "short", "vi", limits_for())
        self.assertEqual(rules(issues), ["title_too_long"])
        self.assertEqual(issues[0]["severity"], "blocking")
        self.assertEqual(issues[0]["segment"], "title")

    def test_validate_keyword_with_person_name_returns_blocking_issue(self):
        changed = story()
        changed["hook"]["keywords"] = ["Doctor John Smith letter"]
        issues = sv.validate(changed, "short", "vi", limits_for())
        self.assertIn("keyword_person_name", rules(issues))
        issue = [i for i in issues if i["rule"] == "keyword_person_name"][0]
        self.assertEqual(issue["severity"], "blocking")
        self.assertEqual(issue["segment"], "hook")

    def test_keyword_person_name_rule_is_mid_phrase_capitalisation_and_honorifics(self):
        """FR-2 / T85: a sentence-case keyword's leading capital is not a name; a
        capitalised *mid-phrase* token, or a title/honorific, is blocking."""
        def person_name(phrase):
            changed = story()
            changed["hook"]["keywords"] = [phrase]
            return "keyword_person_name" in rules(
                sv.validate(changed, "short", "vi", limits_for()))

        self.assertFalse(person_name("Empty altar in a small shrine at dawn"))
        self.assertFalse(person_name("empty altar in a small shrine at dawn"))
        self.assertTrue(person_name("empty altar in a Small shrine at dawn"))
        self.assertTrue(person_name("Church of Saint Mary at dawn"))
        self.assertTrue(person_name("letter sent to the detective at dawn"))
        self.assertFalse(person_name("photos on the windowsill at dawn"))
        self.assertEqual(sv.HONORIFICS, frozenset(
            ("mr", "mrs", "ms", "dr", "sir", "lady", "captain", "detective",
             "officer", "professor", "saint")))

    def test_validate_three_keywords_in_one_segment_returns_issue(self):
        changed = story()
        changed["hook"]["keywords"] = ["one two three", "four five six", "seven eight nine"]
        issues = sv.validate(changed, "short", "vi", limits_for())
        issue = [i for i in issues if i["rule"] == "keyword_count_out_of_range"]
        self.assertTrue(issue)
        self.assertEqual(issue[0]["severity"], "blocking")
        self.assertEqual(issue[0]["segment"], "hook")

    def test_validate_too_few_segments_returns_advisory_issue(self):
        issues = sv.validate(story(), "standard", "vi", limits_for("standard"))
        issue = [i for i in issues if i["rule"] == "segment_count_out_of_range"]
        self.assertTrue(issue)
        # 02 T99: advisory in the M0 trial validator pending the post-A/B D4 decision.
        self.assertEqual(issue[0]["severity"], "advisory")
        self.assertIn("4 segments", issue[0]["evidence"])

    def test_validate_length_out_is_advisory_outside_15_percent_and_in(self):
        wide = limits_for("standard", min_tokens=1000, max_tokens=2000,
                          min_segments=4, max_segments=5)
        sixteen = story()
        sixteen["hook"]["text"] = toks(250)
        sixteen["paragraphs"][0]["text"] = toks(250)
        sixteen["paragraphs"][1]["text"] = toks(300)
        sixteen["ending"]["text"] = toks(40)          # 840 tokens = 16% below 1000
        length = [i for i in sv.validate(sixteen, "standard", "vi", wide)
                  if i["rule"] == "length_out_of_range"]
        self.assertEqual(len(length), 1)
        # 02 T99: advisory in the M0 trial validator pending the post-A/B D4 decision.
        self.assertEqual(length[0]["severity"], "advisory")
        self.assertIn("840 tokens", length[0]["evidence"])

        eight = story()
        eight["hook"]["text"] = toks(270)
        eight["paragraphs"][0]["text"] = toks(280)
        eight["paragraphs"][1]["text"] = toks(330)
        eight["ending"]["text"] = toks(40)            # 920 tokens = 8% below 1000
        length = [i for i in sv.validate(eight, "standard", "vi", wide)
                  if i["rule"] == "length_out_of_range"]
        self.assertEqual(len(length), 1)
        self.assertEqual(length[0]["severity"], "advisory")
        self.assertIn("920 tokens", length[0]["evidence"])

        inside = story()
        inside["hook"]["text"] = toks(280)
        inside["paragraphs"][0]["text"] = toks(290)
        inside["paragraphs"][1]["text"] = toks(350)
        inside["ending"]["text"] = toks(80)           # exactly 1000 tokens
        self.assertEqual([i for i in sv.validate(inside, "standard", "vi", wide)
                          if i["rule"] == "length_out_of_range"], [])

    def test_validate_markdown_bracket_and_url_in_text_are_blocking(self):
        changed = story()
        # T72: `(` and `)` are part of the rejected markup set.
        changed["hook"]["text"] = "Bản nháp (chưa xong) http://example.invalid\n- mục một"
        issues = sv.validate(changed, "short", "vi", limits_for())
        found = rules(issues)
        for rule in ("markdown_or_bracket", "url_in_text", "list_marker"):
            self.assertIn(rule, found)
            self.assertEqual([i for i in issues if i["rule"] == rule][0]["severity"],
                             "blocking")
        parens_only = story()
        parens_only["hook"]["text"] = "Bản nháp (chưa xong) nhưng vẫn kể tiếp được"
        self.assertIn("markdown_or_bracket",
                      rules(sv.validate(parens_only, "short", "vi", limits_for())))

    def test_validate_short_tier_beat_floor(self):
        beats = story()
        beats["paragraphs"] = [
            {"beat": "setup", "text": vi_phrase("Tôi nghe tiếng gõ ngoài cửa", 6),
             "keywords": ["knocking at the door"], "fact_refs": []},
            {"beat": "twist", "text": vi_phrase("Cửa mở ra nhưng hành lang trống", 6),
             "keywords": ["empty hallway again"], "fact_refs": []},
            {"beat": "reveal", "text": vi_phrase("Người gõ cửa chính là tôi", 6),
             "keywords": ["mirror in the hallway"], "fact_refs": []},
        ]
        self.assertNotIn("short_beats_missing",
                         rules(sv.validate(beats, "short", "vi", limits_for())))

        folded = story()          # reveal folded into ending: setup -> twist
        self.assertNotIn("short_beats_missing",
                         rules(sv.validate(folded, "short", "vi", limits_for())))

        wrong = story()
        wrong["paragraphs"] = [
            {"beat": "twist", "text": vi_phrase("Tôi mở cửa nhưng không có ai", 6),
             "keywords": ["locked door in hallway"], "fact_refs": []},
            {"beat": "setup", "text": vi_phrase("Chiếc chìa khoá vẫn còn trên bàn", 6),
             "keywords": ["kitchen light left on"], "fact_refs": []},
        ]
        issue = [i for i in sv.validate(wrong, "short", "vi", limits_for())
                 if i["rule"] == "short_beats_missing"]
        self.assertTrue(issue)
        # D-SHORTBEATS / QA-02D-3: the contract says reported, not blocking.
        self.assertEqual(issue[0]["severity"], "advisory")
        self.assertEqual([i for i in sv.blocking_issues(sv.validate(wrong, "short", "vi",
                                                                   limits_for()))
                          if i["rule"] == "short_beats_missing"], [])

    def test_validate_unknown_fact_ref_without_sheet_is_blocking(self):
        changed = story()
        changed["paragraphs"][0]["fact_refs"] = ["f7"]
        issues = sv.validate(changed, "short", "vi", limits_for())
        self.assertIn("unknown_fact_ref", rules(issues))
        with_sheet = sv.validate(changed, "short", "vi", limits_for(fact_ids=["f7"]))
        self.assertNotIn("unknown_fact_ref", rules(with_sheet))

    def test_validate_non_latin_script_is_blocking(self):
        """vi-true_crime-b9 leaked a Hebrew sentence into the narration and into
        video_script.txt; any non-Latin letter in a text is now blocking."""
        for sample in ("Hồ sơ không nối nó với אדם nào.",        # Hebrew
                       "Người đàn ông nói привет trước khi đi.",  # Cyrillic
                       "Tờ giấy ghi 会議 hai lần."):              # CJK
            with self.subTest(sample=sample):
                changed = story()
                changed["paragraphs"][0]["text"] = sample
                issues = sv.validate(changed, "short", "vi", limits_for())
                found = [i for i in issues if i["rule"] == "non_latin_script"]
                self.assertTrue(found)
                self.assertEqual(found[0]["severity"], "blocking")
                self.assertEqual(found[0]["segment"], "p:0")

    def test_validate_pure_vietnamese_diacritics_pass_script_rule(self):
        changed = story()
        changed["paragraphs"][0]["text"] = (
            "Đêm ấy tôi nghe tiếng chạm nhẹ vào cửa sổ, rồi tiếng phấn kêu "
            "trên mặt bảng đen đã lau.")
        changed["paragraphs"][1]["text"] = (
            "Người trước tôi chuyển đi trong đêm, để lại chiếc ghế quay hướng "
            "ra cửa và nửa nén hương.")
        issues = sv.validate(changed, "short", "vi", limits_for())
        self.assertNotIn("non_latin_script", rules(issues))

        en_doc = story()
        en_doc["paragraphs"][0]["text"] = "A plain English sentence about the road."
        issues = sv.validate(en_doc, "short", "en", limits_for())
        self.assertNotIn("non_latin_script", rules(issues))

    def test_validate_latin_modifier_letters_pass_script_rule(self):
        """QA D8: U+02BC MODIFIER LETTER APOSTROPHE and U+02BB MODIFIER LETTER
        TURNED COMMA are Latin script but their Unicode name lacks "LATIN", so
        the name check alone flagged them."""
        for sample in ("Tên là \u02bcon ấy.",              # MODIFIER LETTER APOSTROPHE
                       "Chữ \u02bbokina\u02bb ở đây."):     # MODIFIER LETTER TURNED COMMA
            with self.subTest(sample=sample):
                changed = story()
                changed["paragraphs"][0]["text"] = sample
                issues = sv.validate(changed, "short", "vi", limits_for())
                self.assertNotIn("non_latin_script", rules(issues))
                self.assertEqual(sv._non_latin_letters(sample), [])

    def test_validate_meta_narration_pipeline_terms_are_blocking(self):
        cases = [
            ("vi", "Brief nhắc đến khác biệt giữa hai lời khai."),
            ("vi", "Không có dữ kiện nào ở đây cho biết điều đó."),
            ("vi", "Ở một câu chuyện như thế này, người kể phải giữ giới hạn."),
            ("vi", "Tài liệu được cung cấp không nhắc tên anh ta."),
            ("vi", "Người kể chuyện này chỉ biết sự việc qua hồ sơ."),
            ("vi", "Không có dữ liệu nào về cuộc gọi thứ hai."),
            # SEC-0928b-3: "thông tin … cung cấp" / "thông tin|dữ liệu … tôi|mình có|được"
            ("vi", "Thông tin được cung cấp không nhắc tên anh ta."),
            ("vi", "Tôi chỉ kể lại phần thông tin mà mình có."),
            ("vi", "Dữ liệu mà tôi được ghi lại không đầy đủ."),
            ("en", "The case is described as wrongful conviction in the brief."),
            ("en", "The fact sheet offers no account of the conviction."),
            # SEC-0928b-3: hyphenated/joined fact-sheet, possessive brief,
            # first-person "the facts I was given".
            ("en", "The fact-sheet lists three witnesses."),
            ("en", "The factsheet lists three witnesses."),
            ("en", "My brief says the man left at dusk."),
            ("en", "Our brief mentions a second caller."),
            ("en", "The facts I was given do not name the street."),
            ("en", "The information we were provided ends at the river."),
            ("en", "The records I have been given stop in March."),
            ("en", "The material provided here does not tell us more."),
            ("en", "The sources provided here are silent on the appeal."),
            ("en", "There is no data on the appeal in the file."),
            # CRV-0928b-1/CRV-0928b-2: the narrowed terms must still fire in
            # their framed forms — the fact table, facts handed over, the data
            # frame on "đầu vào/đầu ra", the story-frame, the participle branch
            # for "requested", the clause-final "available", self-narration.
            ("vi", "Bảng dữ kiện chỉ có ba dòng."),
            ("vi", "Dữ kiện được cung cấp không nhắc tên anh ta."),
            ("vi", "Dữ liệu đầu vào không có ngày tháng."),
            ("vi", "Kết quả đầu ra giống hệt lần trước."),
            ("vi", "Ở một câu chuyện như vậy, người kể phải giữ giới hạn."),
            ("en", "The facts requested were never sent."),
            ("en", "Work only from the material available."),
            ("en", "Your narrator was given only half the file."),
        ]
        for lang, sample in cases:
            with self.subTest(lang=lang, sample=sample):
                changed = story()
                changed["paragraphs"][0]["text"] = sample
                issues = sv.validate(changed, "short", lang, limits_for())
                found = [i for i in issues if i["rule"] == "meta_narration"]
                self.assertTrue(found, f"no meta_narration for {sample!r}")
                self.assertEqual(found[0]["severity"], "blocking")

    def test_validate_meta_narration_innocent_text_passes(self):
        """QA D3: ordinary in-story prose — a document in the file, an approved
        form, an oil pipeline, a storyteller character — is not pipeline talk."""
        cases = [
            ("vi", "Theo hồ sơ, ông rời chợ một mình; trang lời khai không ghi giờ."),
            ("vi", "Anh ấy đọc một tài liệu ngắn rồi đặt xuống bàn."),
            ("vi", "Tài liệu trong hồ sơ không nhắc tên anh ta."),
            ("vi", "Người kể chuyện dừng lại và rót thêm trà."),
            ("vi", "Đơn xin đã được duyệt từ tuần trước."),
            ("vi", "Người nghe gọi đến chương trình và kể lại chuyện đó."),
            ("en", "According to the file, he left the market alone that Tuesday."),
            ("en", "The evidence provided by the neighbour was the whole case."),
            ("en", "Water ran through the pipeline toward the coast."),
            ("en", "The evidence provided to the court was thin."),
            ("en", "There was a brief silence, then a knock."),
            ("en", "She read a brief letter and put it down again."),
            ("en", "The listener called the station and described the car."),
            # SEC-0928b-3 prose negatives: the new phrases must not fire on
            # ordinary in-story talk.
            ("en", "She read the fact of the matter in his face."),
            ("vi", "Anh ấy có thông tin về vụ án từ nhiều năm trước."),
            # CRV-0928b-1: ghost-story body language ("thò đầu ra ngoài cửa
            # sổ", "gục đầu vào vai mẹ"), a story a character heard, and the
            # police sorting the facts of a case.
            ("vi", "Cô thò đầu ra ngoài cửa sổ."),
            ("vi", "Anh gục đầu vào vai mẹ mà khóc."),
            ("vi", "Tôi chưa từng nghe một câu chuyện như vậy."),
            ("vi", "Cảnh sát xếp lại các dữ kiện của vụ án."),
            # CRV-0928b-2: a hand-over of files, a radio-play narrator
            # character, and an adjective before the verb.
            ("en", "He handed over the requested files without a word."),
            ("en", "The narrator of the old radio play had a cold voice."),
            ("en", "The evidence available was a single shoe."),
            # CRV-0928c-2/CRV-0928c-3: the article+adjective "brief", the
            # everyday "thông tin" sentences (police/case talk, a neighbour's
            # account, a narrator's source) and a listener's "câu chuyện như
            # thế này" are prose, not the pipeline's own frame.
            ("en", "The brief silence that followed told him she was gone."),
            ("en", "After the brief struggle the hallway went quiet."),
            ("en", "She kept the brief note folded in her coat."),
            ("en", "It retains the brief background of a man he never met."),
            ("vi", "Cảnh sát không có thông tin gì về người đàn ông ấy."),
            ("vi", "Gia đình thiếu thông tin về nơi anh làm việc."),
            ("vi", "Thông tin được cung cấp bởi người hàng xóm dẫn họ tới bờ sông."),
            ("vi", "Thông tin tôi có được từ bà cụ bán nước kể lại."),
            ("vi", "Tôi chưa bao giờ nghe câu chuyện như thế này."),
        ]
        for lang, sample in cases:
            with self.subTest(lang=lang, sample=sample):
                changed = story()
                changed["paragraphs"][0]["text"] = sample
                issues = sv.validate(changed, "short", lang, limits_for())
                self.assertNotIn("meta_narration", rules(issues), sample)

    def test_validate_nfc_normalises_before_meta_and_stock_matching(self):
        """SEC-0928b-3: decomposed (NFD) Vietnamese must match the same phrase
        lists as its composed form — the check used to run on the raw text, so
        NFD "dữ kiện"/"thông tin được cung cấp" and NFD "Tôi hiểu…" slipped past
        while `non_latin_script` normalised."""
        meta_nfd = unicodedata.normalize("NFD", "Thông tin được cung cấp không đầy đủ.")
        self.assertNotEqual(meta_nfd, "Thông tin được cung cấp không đầy đủ.")
        changed = story()
        changed["paragraphs"][0]["text"] = meta_nfd
        self.assertIn("meta_narration",
                      rules(sv.validate(changed, "short", "vi", limits_for())))

        reveal_nfd = unicodedata.normalize("NFD", "Tôi hiểu ra vì sao cánh cửa mở sẵn.")
        reveal = story()
        reveal["paragraphs"][0]["beat"] = "reveal"
        reveal["paragraphs"][0]["text"] = reveal_nfd
        self.assertIn("stock_reveal_opener",
                      rules(sv.validate(reveal, "short", "vi", limits_for())))

        # NFD innocent prose stays innocent: the normalisation must not turn
        # ordinary text into a match.
        innocent = story()
        innocent["paragraphs"][0]["text"] = unicodedata.normalize(
            "NFD", "Anh ấy đọc một tài liệu ngắn rồi đặt xuống bàn.")
        self.assertNotIn("meta_narration",
                         rules(sv.validate(innocent, "short", "vi", limits_for())))

    def test_validate_stock_reveal_opener_is_blocking_on_reveal_and_ending(self):
        reveal = story()
        reveal["paragraphs"][0]["beat"] = "reveal"
        reveal["paragraphs"][0]["text"] = (
            "Tôi hiểu người phải trả món nợ ấy không phải cha nữa.")
        issues = sv.validate(reveal, "short", "vi", limits_for())
        found = [i for i in issues if i["rule"] == "stock_reveal_opener"]
        self.assertTrue(found)
        self.assertEqual(found[0]["severity"], "blocking")

        folded = story()   # reveal folds into ending at short (T65)
        folded["ending"]["text"] = "I understood the price was mine."
        issues = sv.validate(folded, "short", "en", limits_for())
        self.assertIn("stock_reveal_opener", rules(issues))
        self.assertEqual([i for i in issues
                          if i["rule"] == "stock_reveal_opener"][0]["segment"],
                         "ending")

        # Same formula on another beat is the overused list's job, not this rule.
        other = story()
        other["paragraphs"][0]["text"] = "Tôi hiểu ra vì sao cánh cửa mở sẵn."
        self.assertNotIn("stock_reveal_opener",
                         rules(sv.validate(other, "short", "vi", limits_for())))

    def test_validate_stock_reveal_opener_needs_a_sentence_start(self):
        """QA D4: only the sentence-opening formula is the stock tic. A
        realisation verb mid-sentence, a quoted line and a line with a speech
        tag are ordinary prose; vi's third-person copy is still banned."""
        negatives = [
            ("vi", "Khi bước vào, tôi hiểu vì sao tên ấy trở lại."),
            ("vi", "Cô đứng đó và nhận ra cánh cửa đã mở từ lâu."),
            ("en", "Then I understood the price of my promise."),
            ("en", "She was quiet. I understand, he said, and closed the file."),
            ("en", 'She said, "I understand the risk," and closed the file.'),
        ]
        for lang, sample in negatives:
            with self.subTest(lang=lang, sample=sample):
                changed = story()
                changed["paragraphs"][0]["beat"] = "reveal"
                changed["paragraphs"][0]["text"] = sample
                issues = sv.validate(changed, "short", lang, limits_for())
                self.assertNotIn("stock_reveal_opener", rules(issues), sample)

        # The third-person copy on a reveal/twist beat is still the formula.
        for sample in ("Cô đứng đó và hiểu ra mọi chuyện đã quá muộn.",
                       "Ông ấy hiểu ra tất cả sau khi nghe tin."):
            with self.subTest(third_person=sample):
                changed = story()
                changed["paragraphs"][0]["beat"] = "twist"
                changed["paragraphs"][0]["text"] = sample
                self.assertIn("stock_reveal_opener",
                              rules(sv.validate(changed, "short", "vi", limits_for())))

        # Same third-person copy off the reveal beats stays out of this rule.
        off_beat = story()
        off_beat["paragraphs"][0]["text"] = "Cô ấy hiểu ra mọi chuyện ngay lúc đó."
        self.assertNotIn("stock_reveal_opener",
                         rules(sv.validate(off_beat, "short", "vi", limits_for())))

        # "now I know" is the same formula.
        now_i_know = story()
        now_i_know["paragraphs"][0]["beat"] = "reveal"
        now_i_know["paragraphs"][0]["text"] = "Now I know what the bottle asked of me."
        self.assertIn("stock_reveal_opener",
                      rules(sv.validate(now_i_know, "short", "en", limits_for())))

    def test_validate_stock_reveal_opener_en_matches_adverb_forms(self):
        """CRV-0928b-3: vi allows an adverb between the pronoun and the verb
        ("tôi chợt hiểu…"), so the en formula needs the same optional slot —
        otherwise the same stock tic blocks in vi and passes in en."""
        for adverb in ("suddenly", "finally", "now", "then", "only"):
            sample = f"I {adverb} understood what the letter meant."
            with self.subTest(adverb=adverb):
                changed = story()
                changed["paragraphs"][0]["beat"] = "reveal"
                changed["paragraphs"][0]["text"] = sample
                self.assertIn(
                    "stock_reveal_opener",
                    rules(sv.validate(changed, "short", "en", limits_for())), sample)

        # The adverb slot must not weaken the sentence-start gate: the same
        # words mid-sentence stay ordinary prose.
        mid_sentence = story()
        mid_sentence["paragraphs"][0]["beat"] = "reveal"
        mid_sentence["paragraphs"][0]["text"] = (
            "It was then I suddenly understood what the letter meant.")
        self.assertNotIn(
            "stock_reveal_opener",
            rules(sv.validate(mid_sentence, "short", "en", limits_for())))

    def test_starts_sentence_matches_its_regex_definition(self):
        """PRF-0928b-1: the linear left-walk keeps the rule the regex stated —
        start of text, a line break, or a terminator (optionally behind a
        closing quote/bracket) followed by only whitespace."""
        cases = [
            ("I understand.", "I", True),
            ("Tôi hiểu. Tôi hiểu.", "Tôi hiểu", True),
            ('He said, "Stop." I understand.', "I understand", True),
            ("He said, 'Stop.'\tI understand.", "I understand", True),
            ("Câu này chưa hết I understand", "I understand", False),
            ('He said, "Stop."\nI understand.', "I understand", True),
            ("   I understand.", "I understand", True),
            ("Hết rồi.\n\n  I understand.", "I understand", True),
            ('Hết rồi." I understand.', "I understand", True),
            (r'He said he would "I understand" later.', "I understand", False),
            ("Hết rồi. I understand", "I understand", True),
        ]
        for text, token, expected in cases:
            with self.subTest(text=text, token=token):
                self.assertEqual(
                    sv._starts_sentence(text, text.index(token)), expected, text)

    def test_validate_beat_order_regression_is_advisory(self):
        changed = story()
        changed["paragraphs"] = [
            {"beat": "setup", "text": vi_phrase("Tôi mở cửa nhưng không có ai", 6),
             "keywords": ["locked door in hallway"], "fact_refs": []},
            {"beat": "clue", "text": vi_phrase("Trang sách ghi một cái tên khác", 6),
             "keywords": ["wet ink on paper"], "fact_refs": []},
            {"beat": "build_up", "text": vi_phrase("Tiếng gõ lại vang lên lần nữa", 6),
             "keywords": ["knocking in empty room"], "fact_refs": []},
        ]
        issues = sv.validate(changed, "standard", "vi", limits_for("standard"))
        found = [i for i in issues if i["rule"] == "beat_order"]
        self.assertTrue(found)
        self.assertEqual(found[0]["severity"], "advisory")
        self.assertEqual(found[0]["segment"], "p:2")

        self.assertNotIn(
            "beat_order",
            rules(sv.validate(story(), "standard", "vi", limits_for("standard"))))


class SubstitutionTest(unittest.TestCase):
    def test_substitute_unknown_placeholder_raises_environment_error(self):
        with self.assertRaises(sh.EnvironmentError):
            sh.substitute("hello {{bogus}}", {})
        with self.assertRaises(sh.EnvironmentError):
            sh.substitute("hello {{language_name}}", {"other": "x"})
        self.assertEqual(
            sh.substitute("a {{language_name}} b", {"language_name": "Vietnamese"}),
            "a Vietnamese b")


class UserDataLengthTest(unittest.TestCase):
    """README T97 + TL 2026-09-28: the D6 `length` object carries the
    per-segment budget `units_per_segment = round(target_tokens /
    min_segments)` — mid-band, not floor, so the model aims mid-band."""

    EXPECTED = {
        ("short", "vi"): 57,      # 228 / 4
        ("short", "en"): 44,      # 175 / 4 = 43.75
        ("standard", "vi"): 84,   # 1690 / 20 = 84.5 (banker's rounding)
        ("standard", "en"): 65,   # 1300 / 20
        ("long", "vi"): 81,       # 3250 / 40 = 81.25
        ("long", "en"): 62,       # 2500 / 40 = 62.5 (banker's rounding)
    }

    def length_object(self, tier: str, lang: str) -> dict:
        user = json.loads(sh.build_user_data("Một người gác đèn.", tier,
                                             LENGTH[tier][lang], []))
        return user["length"]

    def test_units_per_segment_exact_value_per_tier_and_language(self):
        for (tier, lang), expected in self.EXPECTED.items():
            with self.subTest(tier=tier, lang=lang):
                self.assertEqual(self.length_object(tier, lang)["units_per_segment"],
                                 expected)
                self.assertEqual(sh.units_per_segment(LENGTH[tier][lang]), expected)

    def test_units_per_segment_is_a_positive_integer_for_every_table_row(self):
        for tier in LENGTH:
            for lang in LENGTH[tier]:
                with self.subTest(tier=tier, lang=lang):
                    value = self.length_object(tier, lang)["units_per_segment"]
                    self.assertIsInstance(value, int)
                    self.assertGreater(value, 0)

    def test_length_object_key_order_is_stable(self):
        length = self.length_object("standard", "en")
        self.assertEqual(list(length), ["tier", "tokens", "segments",
                                        "units_per_segment", "target_tokens"])
        self.assertEqual(length["target_tokens"], 1300)  # mid of 1000-1600
        raw = sh.build_user_data("b", "standard", LENGTH["standard"]["en"], [])
        self.assertIn('"length":{"tier":"standard","tokens":[1000,1600],'
                      '"segments":[20,32],"units_per_segment":65,'
                      '"target_tokens":1300}', raw)

    def test_target_tokens_exact_value_per_tier_and_language(self):
        """README T107: `target_tokens` = `round((min_tokens + max_tokens) / 2)`,
        round half to even (T97) — asserted as literals rather than recomputed
        from the same table the code reads (CRV-0928c-6). vi/en `standard` are
        the README's own examples."""
        expected = {
            ("short", "vi"): 228,      # (195 + 260) / 2 = 227.5 -> 228 (even)
            ("short", "en"): 175,      # (150 + 200) / 2
            ("standard", "vi"): 1690,  # README T107
            ("standard", "en"): 1300,  # README T107
            ("long", "vi"): 3250,      # (2600 + 3900) / 2
            ("long", "en"): 2500,      # (2000 + 3000) / 2
        }
        self.assertEqual(sorted(expected),
                         sorted((tier, lang) for tier in LENGTH for lang in LENGTH[tier]))
        for (tier, lang), value in expected.items():
            with self.subTest(tier=tier, lang=lang):
                limits = LENGTH[tier][lang]
                self.assertEqual(sh.target_tokens(limits), value)
                self.assertEqual(self.length_object(tier, lang)["target_tokens"], value)
                self.assertGreaterEqual(value, limits["min_tokens"])
                self.assertLessEqual(value, limits["max_tokens"])

    def test_non_positive_min_segments_exits_2_with_one_message(self):
        for bad in (0, -1):
            with self.subTest(min_segments=bad):
                with self.assertRaises(sh.EnvironmentError):
                    sh.units_per_segment({"min_tokens": 100, "min_segments": bad})


class DerivationTest(unittest.TestCase):
    def test_body_join_matches_d3_derivation(self):
        doc = story()
        expected = "\n\n".join([doc["hook"]["text"], doc["paragraphs"][0]["text"],
                                doc["paragraphs"][1]["text"], doc["ending"]["text"]])
        self.assertEqual(sv.derive_body(doc), expected)
        self.assertEqual(sh.derive_body(doc), expected)

    def test_keywords_order_is_hook_then_paragraphs_then_ending(self):
        doc = story(
            hook={"text": "h", "keywords": ["k1"], "fact_refs": []},
            paragraphs=[{"beat": "setup", "text": "p1", "keywords": ["k2"], "fact_refs": []},
                        {"beat": "twist", "text": "p2", "keywords": ["k3"], "fact_refs": []}],
            ending={"text": "e", "keywords": ["k4"], "fact_refs": []})
        self.assertEqual(sv.derive_keywords(doc), ["k1", "k2", "k3", "k4"])
        self.assertEqual(sh.derive_keywords(doc), ["k1", "k2", "k3", "k4"])


class CostAndUsageTest(unittest.TestCase):
    def test_compute_cost_uses_cached_and_cache_write_rates(self):
        rates = sh.load_rates(O3_PRICES)
        usage = {"input_tokens": 1000, "cached_tokens": 400,
                 "cache_write_tokens": 100, "output_tokens": 500}
        # luna: 600x0.10 + 400x0.01 + 100x0.125 + 500x0.50 = 326.5 per 1M
        self.assertAlmostEqual(sh.compute_cost("gpt-6-luna", usage, rates), 0.0003265)
        # sol: 600x2 + 400x0.20 + 100x2.50 + 500x10 = 6530 per 1M
        self.assertAlmostEqual(sh.compute_cost("gpt-6-sol", usage, rates), 0.00653)
        self.assertIsNone(sh.compute_cost("gpt-6-luna", {}, rates))
        self.assertIsNone(sh.compute_cost("gpt-6-astra", usage, rates))
        self.assertIsNone(sh.compute_cost("gpt-6-luna",
                                          {"input_tokens": 10, "output_tokens": 5}, {}))

    def test_compute_cost_is_none_when_a_used_rate_is_missing(self):
        # D-PRICES-TESTS: a temp rates file, not trial/prices.json, so adding a
        # cache_write rate for real does not break this test.
        with tempfile.TemporaryDirectory() as tmp:
            path = write_prices(Path(tmp), LUNA_NO_CACHE_WRITE)
            rates = sh.load_rates(path)
        usage = {"input_tokens": 1000, "cached_tokens": 0,
                 "cache_write_tokens": 100, "output_tokens": 500}
        self.assertIsNone(sh.compute_cost("gpt-6-luna", usage, rates))
        self.assertIn("cache_write", sh.cost_note("gpt-6-luna", usage, rates))
        no_write = dict(usage, cache_write_tokens=0)
        # hone: 1000x0.10 + 500x0.50 = 350 per 1M
        self.assertAlmostEqual(sh.compute_cost("gpt-6-luna", no_write, rates), 0.00035)

    def test_require_call_rates_rejects_a_model_without_a_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = write_prices(Path(tmp), LUNA_NO_CACHE_WRITE, name="prices-no-sol.json")
            rates = sh.load_rates(path)
            with self.assertRaises(sh.EnvironmentError):
                sh.require_call_rates("gpt-6-sol", rates, path)
            self.assertEqual(sh.require_call_rates("gpt-6-luna", rates, path)["input"],
                             0.10)

    def test_metrics_usage_records_cached_and_reasoning_tokens(self):
        payload = {"usage": {"input_tokens": 4020,
                             "input_tokens_details": {"cached_tokens": 3072,
                                                      "cache_write_tokens": 512},
                             "output_tokens": 1940,
                             "output_tokens_details": {"reasoning_tokens": 620}}}
        self.assertEqual(sh.usage_record(payload),
                         {"input_tokens": 4020, "cached_tokens": 3072,
                          "cache_write_tokens": 512, "output_tokens": 1940,
                          "reasoning_tokens": 620})
        self.assertEqual(sh.usage_record({"status": "incomplete"}), {})


class ClassifyTest(unittest.TestCase):
    def test_classify_maps_cap_cut_refusal_and_429_to_d7_outcomes(self):
        self.assertEqual(sh.classify(200, {"status": "completed"}), ("ok", None))
        self.assertEqual(
            sh.classify(200, {"incomplete_details": {"reason": "max_output_tokens"}}),
            ("invalid_output", "max_output_tokens"))
        self.assertEqual(
            sh.classify(200, {"output": [{"content": [{"type": "refusal"}]}]}),
            ("moderated", None))
        self.assertEqual(
            sh.classify(200, {"incomplete_details": {"reason": "content_filter"}}),
            ("moderated", "content_filter"))
        self.assertEqual(
            sh.classify(429, {"error": {"code": "spend_limit_exceeded"}}),
            ("funds_exhausted", None))
        self.assertEqual(
            sh.classify(429, {"error": {"code": "credit_balance_exhausted"}}),
            ("funds_exhausted", None))
        self.assertEqual(sh.classify(429, {"error": {"code": "rate_limit_exceeded"}}),
                         ("provider_error", None))
        self.assertEqual(sh.classify(400, {"error": {"code": "invalid_request"}}),
                         ("provider_error", None))
        self.assertEqual(sh.classify(503, {}), ("provider_unavailable", None))
        self.assertEqual(sh.classify(0, {"error": "transport", "detail": "refused"}),
                         ("provider_unavailable", None))


class HarnessCliTest(CliCase):
    def test_dry_run_writes_request_json_and_no_key_material(self):
        self.assertEqual(sh.main(self.argv()), 0)
        run_dir = self.out / "vi-ghost-inline-luna-short"
        request = json.loads((run_dir / "request.json").read_text(encoding="utf-8"))
        self.assertEqual(request["model"], "gpt-6-luna")
        self.assertEqual(request["endpoint"], "/responses")
        self.assertEqual(request["tap_base_url"], sh.DEFAULT_TAP_BASE_URL)
        self.assertEqual(request["body"]["max_output_tokens"], 2500)
        self.assertEqual(request["body"]["store"], False)
        self.assertEqual(len(request["body"]["input"]), 2)
        self.assertEqual(request["body"]["text"]["format"]["name"], "story")
        self.assertEqual(request["body"]["reasoning"], {"effort": "medium"})
        self.assertEqual(request["length"], LENGTH["short"]["vi"])
        self.assertGreater(request["request_body_bytes"], 0)
        self.assertIn("system/writer.v1.md", request["prompt_files"])
        self.assertEqual(sorted(p.name for p in run_dir.iterdir()), ["request.json"])
        user = json.loads(request["body"]["input"][1]["content"][0]["text"])
        self.assertEqual(sorted(user), ["avoid_plots", "bible", "brief", "facts",
                                        "length", "structure_plan", "task"])
        self.assertEqual(sorted(user["length"]), ["segments", "target_tokens",
                                                  "tier", "tokens",
                                                  "units_per_segment"])
        self.assertEqual(user["length"], {"tier": "short", "tokens": [195, 260],
                                          "segments": [4, 5],
                                          "units_per_segment": 57,
                                          "target_tokens": 228})
        self.assertEqual(user["task"], "write_story")
        self.assertEqual(user["facts"], [])
        raw = (run_dir / "request.json").read_text(encoding="utf-8")
        self.assertNotIn("Authorization", raw)
        self.assertNotIn("sk-", raw)
        # dry-run never touches the tap's shared current_run file
        self.assertFalse((self.nts_home / "current_run").exists())

    def test_missing_preset_exits_2_with_one_message(self):
        prompts = make_prompts_dir(self.root / "p2", write_preset=False)
        code = sh.main(["--preset", "ghost", "--lang", "vi", "--brief", "x", "--dry-run",
                        "--prompts-dir", str(prompts), "--out", str(self.out),
                        "--length", str(LENGTH_PATH)])
        self.assertEqual(code, 2)

    def test_missing_schema_exits_2(self):
        prompts = make_prompts_dir(self.root / "p3", write_schema=False)
        code = sh.main(["--preset", "ghost", "--lang", "vi", "--brief", "x", "--dry-run",
                        "--prompts-dir", str(prompts), "--out", str(self.out),
                        "--length", str(LENGTH_PATH)])
        self.assertEqual(code, 2)

    def test_preset_missing_shared_placeholder_exits_2(self):
        preset = STUB_PRESET.replace("{{ear_rules}}", "ear rules inline")
        prompts = make_prompts_dir(self.root / "p4", preset_body=preset)
        code = sh.main(["--preset", "ghost", "--lang", "vi", "--brief", "x", "--dry-run",
                        "--prompts-dir", str(prompts), "--out", str(self.out),
                        "--length", str(LENGTH_PATH)])
        self.assertEqual(code, 2)

    def test_unknown_model_exits_2(self):
        code = sh.main(self.argv("--model", "gpt-6-astra"))
        self.assertEqual(code, 2)

    def test_non_loopback_tap_url_exits_2_before_reading_the_key(self):
        for url in ("https://api.openai.com/v1", "http://0.0.0.0:8002/v1",
                    "http://127.0.0.1:8002", "http://127.0.0.1:8002/v2"):
            with self.subTest(url=url):
                with mock.patch.object(sh.subprocess, "run",
                                       side_effect=AssertionError("keychain must not be read")):
                    code = sh.main(self.argv("--tap-base-url", url, dry_run=False))
                self.assertEqual(code, 2)

    def test_tap_down_exits_2_before_reading_the_key(self):
        probe = socket.socket()
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
        probe.close()                      # the port is now free => connection refused
        argv = ["--preset", "ghost", "--lang", "vi", "--brief", "x",
                "--prompts-dir", str(self.prompts), "--out", str(self.out),
                "--length", str(LENGTH_PATH),
                "--tap-base-url", f"http://127.0.0.1:{port}/v1"]
        with mock.patch.object(sh.subprocess, "run",
                              side_effect=AssertionError("keychain must not be read")):
            code = sh.main(argv)
        self.assertEqual(code, 2)
        self.assertFalse(self.out.exists(), "no cell dir is written before the tap check")

    def test_sol_cell_exits_2_when_prices_has_no_sol_row(self):
        # D-PRICES-TESTS: the temp prices file has only luna, so this stays
        # independent of the real sol row in trial/prices.json.
        argv = ["--preset", "ghost", "--lang", "vi", "--brief", "x", "--model", "gpt-6-sol",
                "--prompts-dir", str(self.prompts), "--out", str(self.out),
                "--length", str(LENGTH_PATH), "--prices", str(self.prices)]
        with mock.patch.object(sh.subprocess, "run",
                              side_effect=AssertionError("keychain must not be read")):
            code = sh.main(argv)
        self.assertEqual(code, 2)

    def test_live_cell_writes_tap_run_id_and_clears_stale_files(self):
        run_dir = self.out / "vi-ghost-inline-luna-short"
        run_dir.mkdir(parents=True)
        for stale in ("story.json", "video_script.txt", "video_terms.json"):
            (run_dir / stale).write_text("stale from a crashed run\n", encoding="utf-8")
        fake_response = {"status": 400, "payload": {"error": {"code": "invalid_request"}},
                         "raw": "{}", "retry_after": None, "request_id": "req_test"}
        argv = self.argv("--prices", str(O3_PRICES), dry_run=False)
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", lambda *a, **k: dict(fake_response)), \
                mock.patch.object(sh.subprocess, "run",
                                  side_effect=AssertionError("keychain must not be read")):
            code = sh.main(argv)
        self.assertEqual(code, 0)          # a recorded non-ok cell is not a harness failure
        metrics = json.loads((run_dir / "metrics.json").read_text(encoding="utf-8"))
        self.assertEqual(metrics["outcome"], "provider_error")
        self.assertFalse(metrics["json_valid"])
        self.assertEqual(metrics["http_status"], 400)
        self.assertEqual(metrics["cost_usd"], None)
        self.assertIn("usage absent", metrics["notes"])
        # an unpriced paid cell must still move the --max-usd accumulator
        self.assertGreater(metrics["cost_bound_usd"], 0)
        for gone in ("story.json", "video_script.txt", "video_terms.json"):
            self.assertFalse((run_dir / gone).exists(), f"{gone} survived the rerun")
        self.assertEqual(sorted(p.name for p in run_dir.iterdir()),
                         ["metrics.json", "request.json", "response.json"])
        request = json.loads((run_dir / "request.json").read_text(encoding="utf-8"))
        self.assertTrue(D10.match(request["tap_run_id"] or ""))
        self.assertTrue(request["tap_run_id"].endswith("-vi-landscape-01"))
        # the batch held the 01 lock while the cell ran and released it in `finally`
        self.assertFalse((self.nts_home / "current_run").exists())
        self.assertEqual([p.name for p in self.nts_home.iterdir()
                          if p.name.endswith(".tmp")], [])
        self.assertNotIn("never-printed", json.dumps(metrics))
        self.assertNotIn("never-printed", json.dumps(request))

    def test_run_lock_reuses_our_id_swaps_language_and_release_only_removes_ours(self):
        state = {"id": None, "owned": set()}
        first = sh.lock_for("vi", state)
        self.assertTrue(D10.match(first or ""))
        self.assertTrue(first.endswith("-vi-landscape-01"))
        self.assertEqual((self.nts_home / "current_run").read_text(encoding="utf-8").strip(),
                         first)
        self.assertEqual(sh.lock_for("vi", state), first)      # reused, not rewritten
        second = sh.lock_for("en", state)                      # language swap: we own it
        self.assertTrue(second.endswith("-en-landscape-01"))
        self.assertEqual((self.nts_home / "current_run").read_text(encoding="utf-8").strip(),
                         second)
        self.assertEqual(sorted(p.name for p in self.nts_home.iterdir()
                                if p.name.endswith(".tmp")), [])
        # a foreign id is never taken over, and release leaves it alone
        foreign = "20261001-1030-vi-landscape-07"
        (self.nts_home / "current_run").write_text(foreign + "\n", encoding="utf-8")
        with self.assertRaises(sh.EnvironmentError):
            sh.lock_for("vi", {"id": None, "owned": set()})
        self.assertEqual((self.nts_home / "current_run").read_text(encoding="utf-8").strip(),
                         foreign)
        sh.release_run_lock({second, first})
        self.assertTrue((self.nts_home / "current_run").exists())
        sh.release_run_lock({foreign})
        self.assertFalse((self.nts_home / "current_run").exists())

    def test_foreign_lock_exits_2_before_any_call(self):
        self.nts_home.mkdir(parents=True, exist_ok=True)
        foreign = "20261001-1030-en-portrait-04"
        (self.nts_home / "current_run").write_text(foreign + "\n", encoding="utf-8")
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses",
                                  side_effect=AssertionError("no call with a foreign lock")), \
                mock.patch.object(sh.subprocess, "run",
                                  side_effect=AssertionError("keychain must not be read")):
            code = sh.main(self.argv("--prices", str(O3_PRICES), dry_run=False))
        self.assertEqual(code, 2)
        self.assertEqual((self.nts_home / "current_run").read_text(encoding="utf-8").strip(),
                         foreign)
        self.assertFalse(self.out.exists())

    def test_cost_bound_charges_every_token_at_the_highest_rate(self):
        rates = sh.load_rates(self.prices)                  # luna row, no cache_write
        usage = {"input_tokens": 1000, "cached_tokens": 0,
                 "cache_write_tokens": 100, "output_tokens": 500}
        # highest known luna rate = output 0.50 => (1600 tokens) x 0.50 / 1M
        self.assertAlmostEqual(
            sh.cost_bound_usd("gpt-6-luna", usage, rates, 4000, limits_for()), 0.0008)
        # no usage at all: the input term is the request body's UTF-8 bytes
        # (D-BOUND / PERF-02D-2), never the smaller D4 word count
        self.assertAlmostEqual(
            sh.cost_bound_usd("gpt-6-luna", {}, rates, 12000, limits_for()),
            (12000 + 2500) * 0.50 / 1_000_000)
        self.assertIsNone(sh.cost_bound_usd("gpt-6-astra", usage, rates, 0, limits_for()))

    def test_ok_cell_writes_six_files_and_bounds_an_unpriceable_cost(self):
        model_story = story()
        payload = output_payload(model_story, usage={
            "input_tokens": 5000,
            "input_tokens_details": {"cached_tokens": 100, "cache_write_tokens": 500},
            "output_tokens": 900,
            "output_tokens_details": {"reasoning_tokens": 300}})
        fake = {"status": 200, "payload": payload,
                "raw": json.dumps(payload), "retry_after": None, "request_id": "req_test"}
        argv = self.argv(dry_run=False)
        captured = io.StringIO()
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", lambda *a, **k: dict(fake)), \
                mock.patch("sys.stdout", captured):
            code = sh.main(argv)
        self.assertEqual(code, 0)
        run_dir = self.out / "vi-ghost-inline-luna-short"
        self.assertEqual(sorted(p.name for p in run_dir.iterdir()),
                         ["metrics.json", "request.json", "response.json", "story.json",
                          "video_script.txt", "video_terms.json"])
        metrics = json.loads((run_dir / "metrics.json").read_text(encoding="utf-8"))
        self.assertEqual(metrics["outcome"], "ok")
        self.assertTrue(metrics["json_valid"])
        self.assertEqual(metrics["provider_request_id"], "req_test")
        self.assertEqual(metrics["usage"], {"input_tokens": 5000, "cached_tokens": 100,
                                           "cache_write_tokens": 500,
                                           "output_tokens": 900, "reasoning_tokens": 300})
        # the temp prices file has no cache_write rate: null cost, honest bound, no $0
        self.assertIsNone(metrics["cost_usd"])
        self.assertAlmostEqual(metrics["cost_bound_usd"],
                               6500 * 0.50 / 1_000_000)
        self.assertAlmostEqual(metrics["charge_usd"], metrics["cost_bound_usd"])
        self.assertIn("cache_write", metrics["notes"])
        self.assertIn("estimated=true", captured.getvalue())
        self.assertEqual(metrics["validator_issues"], [])
        self.assertEqual(json.loads((run_dir / "story.json").read_text(encoding="utf-8")),
                         model_story)
        self.assertEqual((run_dir / "video_script.txt").read_text(encoding="utf-8"),
                         sv.derive_body(model_story) + "\n")
        self.assertEqual(json.loads((run_dir / "video_terms.json").read_text(
            encoding="utf-8")), sv.derive_keywords(model_story))
        # the lock is released in `finally`, so start_run.sh can start again
        self.assertFalse((self.nts_home / "current_run").exists())

    def test_response_json_redacts_relay_identity_and_length_table_is_relative(self):
        """SEC-0928c-2/SEC-0928c-1: response.json is evidence, not the verbatim
        upstream payload — the relay's identity fields and the opaque reasoning
        blobs are replaced — and request.json records the length table
        repo-relative, never the host layout."""
        model_story = story()
        payload = output_payload(model_story)
        payload["safety_identifier"] = "user-relay-xyz"
        payload["prompt_cache_key"] = "relay-key"
        payload["output"][0]["encrypted_content"] = "blob"
        fake = {"status": 200, "payload": payload, "raw": json.dumps(payload),
                "retry_after": None, "request_id": "req_test"}
        captured = io.StringIO()
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", lambda *a, **k: dict(fake)), \
                mock.patch("sys.stdout", captured):
            code = sh.main(self.argv(dry_run=False))
        self.assertEqual(code, 0)
        run_dir = self.out / "vi-ghost-inline-luna-short"
        response_text = (run_dir / "response.json").read_text(encoding="utf-8")
        for secret in ("user-relay-xyz", "relay-key", "blob"):
            self.assertNotIn(secret, response_text)
        self.assertIn("<redacted>", response_text)
        request = json.loads((run_dir / "request.json").read_text(encoding="utf-8"))
        length_table = request["length_table"]
        self.assertFalse(length_table.startswith("/"))
        self.assertNotIn("NightTalesStudio", length_table)
        if (REPO_ROOT / "trial" / "tools" / "LENGTH.v1.json").exists():
            self.assertEqual(length_table, "trial/tools/LENGTH.v1.json")
        else:
            self.assertEqual(length_table, "LENGTH.v1.json")
        # redaction must not touch the story the cell produced
        self.assertEqual(json.loads((run_dir / "story.json").read_text(encoding="utf-8")),
                         model_story)


    def test_transport_timeout_is_not_retried_and_every_attempt_is_recorded(self):
        calls = []

        def fake(base_url, body, api_key, client_request_id, timeout=None):
            calls.append(timeout)
            return {"status": 0, "payload": {"error": "transport", "detail": "timed out"},
                    "raw": "", "retry_after": None, "request_id": None}

        captured = io.StringIO()
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", side_effect=fake), \
                mock.patch("sys.stdout", captured):
            code = sh.main(self.argv("--timeout", "7", dry_run=False))
        self.assertEqual(code, 0)
        # D-TIMEOUT: a transport timeout is still in flight upstream and billed,
        # so it is never retried and the cell is provider_unavailable.
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0], 7.0)
        run_dir = self.out / "vi-ghost-inline-luna-short"
        metrics = json.loads((run_dir / "metrics.json").read_text(encoding="utf-8"))
        self.assertEqual(metrics["outcome"], "provider_unavailable")
        self.assertEqual(metrics["http_status"], 0)
        self.assertEqual(metrics["attempt"], 1)
        self.assertEqual([a["http_status"] for a in metrics["attempts"]], [0])
        self.assertGreater(metrics["cost_bound_usd"], 0)
        self.assertAlmostEqual(metrics["charge_usd"], metrics["cost_bound_usd"])
        self.assertEqual(metrics["timeout_s"], 7.0)
        self.assertEqual(sorted(p.name for p in run_dir.iterdir()),
                         ["metrics.json", "request.json", "response.json"])

    def test_server_error_is_retried_and_both_attempts_are_charged(self):
        usage = {"input_tokens": 5000, "cached_tokens": 0, "output_tokens": 900}
        first = {"status": 503, "payload": {}, "raw": "{}", "retry_after": None,
                 "request_id": "r1"}
        second = {"status": 200, "payload": output_payload(story(), usage=usage),
                  "raw": "{}", "retry_after": None, "request_id": "r2"}
        calls = []

        def fake(*args, **kwargs):
            calls.append(1)
            return dict(first if len(calls) == 1 else second)

        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", side_effect=fake), \
                mock.patch.object(sh.time, "sleep", lambda *_: None), \
                mock.patch("sys.stdout", io.StringIO()):
            code = sh.main(self.argv(dry_run=False))
        self.assertEqual(code, 0)
        self.assertEqual(len(calls), 2)
        metrics = json.loads((self.out / "vi-ghost-inline-luna-short"
                              / "metrics.json").read_text(encoding="utf-8"))
        self.assertEqual(metrics["outcome"], "ok")
        self.assertEqual(metrics["attempt"], 2)
        self.assertEqual([a["http_status"] for a in metrics["attempts"]], [503, 200])
        bound = metrics["attempts"][0]["cost_bound_usd"]
        cost = metrics["attempts"][1]["cost_usd"]
        self.assertGreater(bound, 0)
        self.assertAlmostEqual(cost, (5000 * 0.10 + 900 * 0.50) / 1_000_000)
        # PERF-02D-1: the guard charge covers both paid calls, not only the last.
        self.assertAlmostEqual(metrics["charge_usd"], bound + cost)
        self.assertIn("retried once", metrics["notes"])
        self.assertEqual(metrics["cost_usd"], cost)

    def test_malformed_story_shape_records_invalid_output_and_metrics(self):
        payload = {"status": "completed",
                   "output": [{"content": [{"type": "output_text",
                                            "text": json.dumps({"hook": "x",
                                                                "paragraphs": ["y"]})}]}]}
        fake = {"status": 200, "payload": payload, "raw": json.dumps(payload),
                "retry_after": None, "request_id": "r"}
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", lambda *a, **k: dict(fake)), \
                mock.patch("sys.stdout", io.StringIO()):
            code = sh.main(self.argv(dry_run=False))
        self.assertEqual(code, 0)
        run_dir = self.out / "vi-ghost-inline-luna-short"
        metrics = json.loads((run_dir / "metrics.json").read_text(encoding="utf-8"))
        # D-SHAPE / SEC-02D-2: a malformed story never reaches derive/validate,
        # and the paid cell is still recorded.
        self.assertEqual(metrics["outcome"], "invalid_output")
        self.assertEqual(metrics["shape_error"], "hook_not_object")
        self.assertIn("schema_shape", metrics["incomplete_reason"])
        self.assertFalse(metrics["json_valid"])
        self.assertEqual(sorted(p.name for p in run_dir.iterdir()),
                         ["metrics.json", "request.json", "response.json"])

    def test_cell_done_ignores_a_torn_or_empty_metrics_file(self):
        run_dir = self.out / "vi-ghost-inline-luna-short"
        run_dir.mkdir(parents=True)
        self.assertFalse(sh._cell_done(run_dir))                      # no metrics
        (run_dir / "metrics.json").write_text("{", encoding="utf-8")   # torn write
        self.assertFalse(sh._cell_done(run_dir))
        (run_dir / "metrics.json").write_text("", encoding="utf-8")
        self.assertFalse(sh._cell_done(run_dir))
        (run_dir / "metrics.json").write_text(json.dumps({"outcome": "ok"}), encoding="utf-8")
        self.assertTrue(sh._cell_done(run_dir))
        self.assertEqual(sh._cell_outcome(run_dir), "ok")
        # a recorded failure is a finished cell (only --retry-failed re-runs it)
        (run_dir / "metrics.json").write_text(
            json.dumps({"outcome": "provider_unavailable"}), encoding="utf-8")
        self.assertTrue(sh._cell_done(run_dir))
        self.assertEqual(sh._cell_outcome(run_dir), "provider_unavailable")

    def test_retry_failed_reruns_a_failure_but_keeps_the_evidence(self):
        run_dir = self.out / "vi-ghost-inline-luna-short"
        run_dir.mkdir(parents=True)
        (run_dir / "metrics.json").write_text(
            json.dumps({"outcome": "provider_unavailable"}), encoding="utf-8")
        payload = output_payload(story())
        fake = {"status": 200, "payload": payload, "raw": json.dumps(payload),
                "retry_after": None, "request_id": "r"}
        argv = self.argv("--only-missing", dry_run=False)
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses",
                                  side_effect=AssertionError("failed cell must be skipped")), \
                mock.patch("sys.stdout", io.StringIO()):
            self.assertEqual(sh.main(argv), 0)
        self.assertFalse((self.out / "vi-ghost-inline-luna-short-r2").exists())
        with mock.patch.object(sh, "assert_tap", lambda url: None), \
                mock.patch.object(sh, "load_secret", lambda service: "never-printed"), \
                mock.patch.object(sh, "post_responses", lambda *a, **k: dict(fake)), \
                mock.patch("sys.stdout", io.StringIO()):
            self.assertEqual(sh.main(argv + ["--retry-failed"]), 0)
        retry_dir = self.out / "vi-ghost-inline-luna-short-r2"
        self.assertEqual(json.loads((retry_dir / "metrics.json").read_text(
            encoding="utf-8"))["outcome"], "ok")
        # the failed dir is kept as evidence
        self.assertEqual(json.loads((run_dir / "metrics.json").read_text(
            encoding="utf-8"))["outcome"], "provider_unavailable")

    def test_run_id_with_more_than_one_cell_exits_2_before_any_call(self):
        briefs = str(self.prompts / "fixtures" / "briefs.vi.json")
        argv = self.argv_briefs("--preset", "ghost", "--briefs-file", briefs,
                                "--brief-ids", "b1,b2", "--run-id", "coll")
        with mock.patch.object(sh, "post_responses",
                              side_effect=AssertionError("no call may happen")):
            self.assertEqual(sh.main(argv), 2)
        self.assertFalse(self.out.exists())

    def test_unsafe_run_id_and_brief_id_exit_2(self):
        for bad in ("../x", "a/b", "..", ".hidden", "x" * 65, "bad\nid"):
            with self.subTest(run_id=bad):
                self.assertEqual(sh.main(self.argv("--run-id", bad)), 2)
        bad_file = self.root / "bad-briefs.json"
        bad_file.write_text(json.dumps([{"id": "a/../../b", "topic": "ghost",
                                        "brief": "x"}]), encoding="utf-8")
        code = sh.main(self.argv_briefs("--briefs-file", str(bad_file),
                                        "--brief-ids", "a/../../b"))
        self.assertEqual(code, 2)
        self.assertFalse(self.out.exists())

    def test_prompt_manifest_mismatch_exits_2_and_records_digests(self):
        digests = {relative: hashlib.sha256((self.prompts / relative).read_bytes()).hexdigest()
                   for relative in RELATIVE_PROMPT_FILES}
        manifest = self.prompts / "manifest.json"
        manifest.write_text(json.dumps({
            "release": "test.1",
            "files": {relative: {"version": "v1", "sha256": digest}
                      for relative, digest in digests.items()}}), encoding="utf-8")
        self.assertEqual(sh.main(self.argv()), 0)
        request = json.loads((self.out / "vi-ghost-inline-luna-short"
                              / "request.json").read_text(encoding="utf-8"))
        self.assertEqual(request["prompt_files"], digests)
        self.assertIn("rel=test.1", request["prompt_version"])

        drifted = self.prompts / "story" / "ghost.vi.v1.md"
        drifted.write_text(drifted.read_text(encoding="utf-8") + "\nextra line\n",
                           encoding="utf-8")
        stderr = io.StringIO()
        with mock.patch("sys.stderr", stderr):
            code = sh.main(self.argv("--out", str(self.out / "second")))
        self.assertEqual(code, 2)
        self.assertIn("story/ghost.vi.v1.md", stderr.getvalue())

    def test_preset_front_matter_is_not_sent_to_the_model(self):
        self.assertEqual(sh.main(self.argv()), 0)
        request = json.loads((self.out / "vi-ghost-inline-luna-short"
                              / "request.json").read_text(encoding="utf-8"))
        head = request["body"]["input"][0]["content"][0]["text"]
        # D-FM: the `---` front matter of the preset reaches no request.
        self.assertNotIn("target_tier: all", head)
        self.assertNotIn("id: ghost.vi", head)
        self.assertIn("Language Vietnamese", head)

    def test_true_crime_cell_sends_only_its_briefs_facts(self):
        (self.prompts / "story" / "true_crime.vi.v1.md").write_text(STUB_PRESET,
                                                                   encoding="utf-8")
        briefs = str(self.prompts / "fixtures" / "briefs.vi.json")
        argv = self.argv_briefs("--preset", "true_crime", "--briefs-file", briefs,
                                "--brief-ids", "b7", "--tier", "standard")
        self.assertEqual(sh.main(argv), 0)
        request = json.loads((self.out / "vi-true_crime-b7-luna-standard"
                              / "request.json").read_text(encoding="utf-8"))
        user = json.loads(request["body"]["input"][1]["content"][0]["text"])
        # D-FACTS: the reopened-file brief gets only its own facts, projected onto
        # the D6 shape (no `brief_id` bookkeeping reaches the model). The sheet
        # now carries 6-10 facts per true_crime brief (2026-09-28 pre-review).
        b7_ids = ["f1", "f2", "f7", "f8", "f9", "f10", "f11", "f12"]
        self.assertEqual([fact["id"] for fact in user["facts"]], b7_ids)
        self.assertEqual(sorted(user["facts"][0]), ["id", "sources", "statement"])
        self.assertEqual(request["length"]["fact_ids"], b7_ids)

        argv = self.argv_briefs("--preset", "true_crime", "--briefs-file", briefs,
                                "--brief-ids", "b9", "--tier", "standard",
                                "--out", str(self.out / "b9"))
        self.assertEqual(sh.main(argv), 0)
        request = json.loads((self.out / "b9" / "vi-true_crime-b9-luna-standard"
                              / "request.json").read_text(encoding="utf-8"))
        user = json.loads(request["body"]["input"][1]["content"][0]["text"])
        self.assertEqual([fact["id"] for fact in user["facts"]],
                         ["f4", "f5", "f6", "f20", "f21", "f22",
                          "f23", "f24", "f25", "f26"])

    def test_two_briefs_files_matrix_yields_one_cell_per_brief_language(self):
        """FR-1: 9 ids x {vi,en} x 2 models = 36 distinct cells, each assembled
        from its own brief's language preset + shared blocks, with no `--lang`."""
        vi_file = self.prompts / "fixtures" / "briefs.vi.json"
        en_file = self.prompts / "fixtures" / "briefs.en.json"
        argv = self.argv_briefs("--matrix", "--tier", "standard",
                                "--briefs-file", str(vi_file),
                                "--briefs-file", str(en_file), lang=None)
        with mock.patch.object(sh, "post_responses",
                               side_effect=AssertionError("--dry-run must not call")):
            self.assertEqual(sh.main(argv), 0)
        names = sorted(path.name for path in self.out.iterdir() if path.is_dir())
        self.assertEqual(len(names), 36)
        self.assertEqual(len(set(names)), 36)                    # vi-… never overwrites en-…
        fixtures = {lang: {str(brief["id"]): brief
                           for brief in json.loads(path.read_text(encoding="utf-8"))}
                    for lang, path in (("vi", vi_file), ("en", en_file))}
        for name in names:
            lang, preset, brief_id, model, tier = name.split("-")
            with self.subTest(cell=name):
                self.assertIn(model, ("luna", "sol"))
                request = json.loads((self.out / name / "request.json")
                                     .read_text(encoding="utf-8"))
                # The cell's language, preset and language name come from its brief.
                self.assertEqual(request["language"], lang)
                self.assertEqual(request["language_name"], sh.LANGUAGE_NAMES[lang])
                self.assertEqual(request["preset"], preset)
                self.assertEqual(request["tier"], tier)
                self.assertEqual(request["brief_id"], brief_id)
                self.assertEqual(preset, fixtures[lang][brief_id]["topic"])
                # Its own language's preset + shared blocks were hashed in.
                self.assertIn(f"story/{preset}.{lang}.v1.md", request["prompt_files"])
                self.assertIn(f"shared/ear-rules.{lang}.v1.md", request["prompt_files"])
                self.assertIn(f"shared/overused.{lang}.v1.txt", request["prompt_files"])
                head = request["body"]["input"][0]["content"][0]["text"]
                self.assertIn(sh.LANGUAGE_NAMES[lang], head)
                self.assertIn(f"preset={preset}.{lang}.v1", request["prompt_version"])
                user = json.loads(request["body"]["input"][1]["content"][0]["text"])
                self.assertEqual(user["brief"], fixtures[lang][brief_id]["brief"].strip())

    def test_lang_filter_skips_other_language_briefs_and_exits_2_when_none_remain(self):
        """FR-1: `--lang` only filters. A conflicting brief is skipped with a note
        and a batch with nothing left is exit 2, never a silent duplicate."""
        en_file = self.prompts / "fixtures" / "briefs.en.json"
        captured = io.StringIO()
        with mock.patch("sys.stdout", captured):
            code = sh.main(self.argv_briefs("--matrix", "--briefs-file", str(en_file)))
        self.assertEqual(code, 2)                                # --lang vi, all briefs en
        note = captured.getvalue()
        self.assertIn("skipping brief b1", note)
        self.assertIn("language=en", note)
        if self.out.exists():
            self.assertFalse([path for path in self.out.iterdir() if path.is_dir()])

        # The same file passes the filter and yields its 9 briefs x 2 models.
        self.assertEqual(sh.main(self.argv_briefs("--matrix", "--briefs-file", str(en_file),
                                                  lang="en")), 0)
        names = sorted(path.name for path in self.out.iterdir() if path.is_dir())
        self.assertEqual(len(names), 18)
        self.assertTrue(all(name.startswith("en-") for name in names))

    def test_brief_ids_resolve_per_file_and_language(self):
        """FR-1: a duplicated id (b1 in vi and en) selects both cells, not the
        last file read."""
        vi_file = self.prompts / "fixtures" / "briefs.vi.json"
        en_file = self.prompts / "fixtures" / "briefs.en.json"
        argv = self.argv_briefs("--briefs-file", str(vi_file), "--briefs-file", str(en_file),
                                "--brief-ids", "b1", "--tier", "standard", lang=None)
        self.assertEqual(sh.main(argv), 0)
        names = sorted(path.name for path in self.out.iterdir() if path.is_dir())
        self.assertEqual(names, ["en-ghost-b1-luna-standard", "vi-ghost-b1-luna-standard"])

        # An explicit --lang keeps only its own file's copy of that id.
        self.assertEqual(sh.main(self.argv_briefs("--briefs-file", str(vi_file),
                                                 "--briefs-file", str(en_file),
                                                 "--brief-ids", "b1,b4", "--tier", "standard",
                                                 lang="en", out=self.out / "en2")), 0)
        names = sorted(path.name for path in (self.out / "en2").iterdir() if path.is_dir())
        self.assertEqual(names, ["en-ghost-b1-luna-standard", "en-spiritual-b4-luna-standard"])

    def test_validate_only_accepts_the_briefs_fact_ids(self):
        doc = story()
        doc["paragraphs"][0]["fact_refs"] = ["f1"]
        doc["paragraphs"][1]["fact_refs"] = ["f2"]
        story_path = self.root / "tc.json"
        story_path.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
        base = ["--validate-only", str(story_path), "--tier", "short", "--lang", "vi",
                "--prompts-dir", str(self.prompts)]
        captured = io.StringIO()
        with mock.patch("sys.stdout", captured):
            self.assertEqual(sh.main(base), 1)              # no sheet: f1/f2 unknown
        self.assertIn("unknown_fact_ref", captured.getvalue())

        with_facts = base + ["--brief-id", "b7", "--briefs-file",
                             str(self.prompts / "fixtures" / "briefs.vi.json")]
        captured = io.StringIO()
        with mock.patch("sys.stdout", captured):
            self.assertEqual(sh.main(with_facts), 0)
        self.assertEqual(json.loads(captured.getvalue()), [])

        doc["paragraphs"][0]["fact_refs"] = ["f4"]          # another brief's fact
        story_path.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
        captured = io.StringIO()
        with mock.patch("sys.stdout", captured):
            self.assertEqual(sh.main(with_facts), 1)
        self.assertIn("f4", captured.getvalue())

        with_sheet = base + ["--facts", str(self.prompts / "fixtures"
                                            / "fact-sheet.true_crime.v1.json")]
        captured = io.StringIO()
        with mock.patch("sys.stdout", captured):
            self.assertEqual(sh.main(with_sheet), 0)

    def test_first_lock_acquisition_links_and_a_race_exits_2(self):
        real_link = os.link
        linked = []

        def counting_link(src, dst):
            linked.append((str(src), str(dst)))
            return real_link(src, dst)

        with mock.patch.object(sh.os, "link", side_effect=counting_link):
            run_id = sh.take_run_lock("vi", set())
        self.assertTrue(D10.match(run_id or ""))
        self.assertEqual(len(linked), 1)                     # SEC-02D-4: os.link
        self.assertEqual((self.nts_home / "current_run").read_text(encoding="utf-8").strip(),
                         run_id)
        self.assertEqual([p.name for p in self.nts_home.iterdir()
                          if p.name.endswith(".tmp")], [])

        linked.clear()
        with mock.patch.object(sh.os, "link", side_effect=counting_link):
            swapped = sh.take_run_lock("en", {run_id})
        self.assertTrue(swapped.endswith("-en-landscape-01"))
        self.assertEqual(linked, [])                         # swap keeps os.replace

        race_home = self.root / "race-home"
        stderr = io.StringIO()
        with mock.patch.dict(os.environ, {"NTS_HOME": str(race_home)}), \
                mock.patch.object(sh.os, "link", side_effect=FileExistsError), \
                mock.patch("sys.stderr", stderr):
            with self.assertRaises(sh.EnvironmentError):
                sh.take_run_lock("vi", set())

    def test_help_lists_every_flag(self):
        captured = io.StringIO()
        with mock.patch("sys.stdout", captured):
            with self.assertRaises(SystemExit) as ctx:
                sh.build_parser().parse_args(["--help"])
        self.assertEqual(ctx.exception.code, 0)
        text = captured.getvalue()
        flags = {option for action in sh.build_parser()._actions
                 for option in action.option_strings}
        for flag in sorted(flags):
            self.assertIn(flag, text)
        self.assertTrue({"--timeout", "--retry-failed", "--facts"} <= flags)


class ShapeTest(unittest.TestCase):
    def test_safe_json_and_extract_story_survive_deep_or_bad_json(self):
        deep = "[" * 20000 + "]" * 20000
        self.assertIsNone(sh._safe_json(deep))               # RecursionError caught
        story, reason = sh.extract_story(
            {"output": [{"content": [{"type": "output_text", "text": deep}]}]})
        self.assertIsNone(story)
        self.assertTrue(reason.startswith("json_invalid"))

    def test_shape_reason_and_derivations_tolerate_malformed_stories(self):
        self.assertEqual(sv.shape_reason("not a dict"), "not_an_object")
        self.assertEqual(sv.shape_reason({"hook": "x"}), "hook_not_object")
        self.assertEqual(sv.shape_reason({"hook": {}, "ending": {},
                                          "paragraphs": ["y"]}),
                         "paragraphs_not_list_of_objects")
        self.assertEqual(sv.shape_reason({"hook": {"keywords": "abc"}, "ending": {},
                                          "paragraphs": []}), "keywords_not_list")
        self.assertIsNone(sv.shape_reason({"hook": {}, "ending": {}, "paragraphs": []}))
        # SEC-02D-2: deriving a malformed story must not raise.
        self.assertEqual(sv.derive_body({"hook": "x", "paragraphs": 5}), "")
        self.assertEqual(sv.derive_keywords({"hook": "x", "paragraphs": 5}), [])
        self.assertEqual(sv.derive_body({"hook": {"text": "h"},
                                         "paragraphs": [{"text": "p"}],
                                         "ending": {"text": "e"}}), "h\n\np\n\ne")


class RedirectTest(unittest.TestCase):
    def test_redirect_is_not_followed_and_the_key_never_leaves_the_first_hop(self):
        # Not one of the CRV-0928b findings: this stub closed the connection
        # with the client's POST body still unread, so the kernel answered with
        # RST and `post_responses`' `exc.read()` died with ConnectionResetError
        # (measured 5/60 rounds; 0/60 once the body is drained —
        # .nts/qa-0928b/QaPromptValidator/repro_redirect_rst.py). Reading the
        # body first makes the close graceful and the test deterministic.
        seen = {"second_hits": 0, "second_auth": None, "first_auth": None}

        class Second(http.server.BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802 - http.server API
                self.rfile.read(int(self.headers.get("Content-Length") or 0))
                seen["second_hits"] += 1
                seen["second_auth"] = self.headers.get("Authorization")
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b"{}")

            def log_message(self, *args):
                pass

        second = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Second)
        second_port = second.server_address[1]

        class First(http.server.BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802 - http.server API
                self.rfile.read(int(self.headers.get("Content-Length") or 0))
                seen["first_auth"] = self.headers.get("Authorization")
                self.send_response(302)
                self.send_header("Location",
                                 f"http://127.0.0.1:{second_port}/v1/responses")
                self.end_headers()

            def log_message(self, *args):
                pass

        first = http.server.ThreadingHTTPServer(("127.0.0.1", 0), First)
        servers = (first, second)
        for server in servers:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            result = sh.post_responses(f"http://127.0.0.1:{first.server_address[1]}/v1",
                                       {"model": "gpt-6-luna"}, "secret-key", "req-1",
                                       timeout=5)
        finally:
            for server in servers:
                server.shutdown()
                server.server_close()
        self.assertEqual(result["status"], 302)
        self.assertEqual(seen["first_auth"], "Bearer secret-key")
        self.assertEqual(seen["second_hits"], 0,
                         "the redirect target must never receive a request")
        self.assertIsNone(seen["second_auth"])
        # D-REDIRECT: a 3xx is a provider error, never a retry or a re-send.
        self.assertEqual(sh.classify(result["status"], result["payload"]),
                         ("provider_error", None))


if __name__ == "__main__":
    unittest.main()
