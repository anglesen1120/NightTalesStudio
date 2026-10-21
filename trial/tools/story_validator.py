"""Throwaway `story.v1` validator for the M0 story harness (02.1.3-T0).

Pure functions, no I/O: callers pass the story, the tier, the language and the
length limits. Rule ids and severities follow README D3 (as amended by
preset-spec.v1.md §7, T65, T70, T72); the harness copies the issue list into
`metrics.json.validator_issues` after every paid call.

Deleted when 02.2.1-T1 `StoryDraftValidator` exists. Product code must never
import this module. Run standalone:

    uv run --no-project --python 3.12 trial/tools/story_validator.py story.json \
        --tier short --lang vi
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import unicodedata
from pathlib import Path

BLOCKING = "blocking"
ADVISORY = "advisory"

TITLE_MAX_CHARS = 100
HOOK_MAX_SENTENCES = 3
KEYWORD_MIN_WORDS = 3
KEYWORD_MAX_WORDS = 12
KEYWORDS_PER_SEGMENT_MIN = 1
KEYWORDS_PER_SEGMENT_MAX = 2
DESCRIPTION_MAX_CHARS = 1000
TAGS_MAX = 15
TAG_MAX_CHARS = 30
TAGS_TOTAL_MAX_CHARS = 400

# D4 minutes, used only for the keyword total cap ceil(D x K), K = 6 (F09).
TIER_MINUTES_MAX = {"short": 1.0, "standard": 8.0, "long": 15.0}
KEYWORDS_PER_MINUTE = 6

# T65: the `short` body is setup -> twist -> reveal, one paragraph each;
# `reveal` may fold into `ending` (2 paragraphs). Other tiers keep six beats.
SHORT_BEATS = ("setup", "twist", "reveal")
ALL_BEATS = ("setup", "build_up", "clue", "twist", "reveal", "aftermath")

_SENTENCE_END = re.compile(r"[.!?…]+(?=\s|$)")
_URL = re.compile(r"(?:https?://|www\.)", re.IGNORECASE)
_LIST_MARKER = re.compile(r"(?m)^\s*(?:[-*•]|\d+[.)])\s+")
_MARKUP = re.compile(r"[\[\](){}<>]|[*_`#]")
_ASCII_WORD = re.compile(r"^[A-Za-z][A-Za-z'’-]*$")
_CAPITALISED = re.compile(r"^[A-Z]")

# T85 / 02.2.1-T1 (c) R5 partial: the M0 person-name heuristic is *mid-phrase*
# capitalisation — a capitalised token **after the first**, so an ordinary
# sentence-case or Title Case keyword is not a violation — plus this
# title/honorific list matched on whole tokens, case-insensitively. The full R5
# (bible character names and `fact_sources` person names, D-case-insensitive)
# belongs to 02.2.1-T1's `KeywordRules`.

# Story-text script rule: every letter in narration must be Latin (vi carries
# Latin + Vietnamese diacritics; en ASCII Latin). Non-letters (digits,
# punctuation, whitespace, symbols) are always allowed — the ear rules own
# digits/markup. A non-Latin run would reach video_script.txt and TTS as
# mojibake. (2026-09-28 AI pre-review: vi-true_crime-b9 leaked Hebrew.)
# Two Latin-script modifier letters carry no "LATIN" in their Unicode name, so
# the name check alone would flag them (QA D8): U+02BC MODIFIER LETTER
# APOSTROPHE and U+02BB MODIFIER LETTER TURNED COMMA (Hawaiian ʻokina).
_LATIN_NAME_EXEMPT = frozenset(("\u02bc", "\u02bb"))


def _non_latin_letters(text: str) -> list[str]:
    """Letters in `text` whose Unicode script is not Latin. Vietnamese
    diacritics are LATIN code points, so a pure vi string passes and a
    Hebrew/Cyrillic/CJK letter does not."""
    return [ch for ch in unicodedata.normalize("NFC", text or "")
            if unicodedata.category(ch).startswith("L")
            and ch not in _LATIN_NAME_EXEMPT
            and "LATIN" not in unicodedata.name(ch, "")]


# Meta-narration / pipeline vocabulary that must never be spoken (the
# pre-review's R4: true-crime stories narrated the brief and the missing
# facts). Per-language lists; matched case-insensitively on segment text.
# Narrowed 2026-09-28 (QA D3): only phrases that cannot be in-story talk — a
# bare "tài liệu" (a document in the file), "được duyệt" (an approved form) or
# "pipeline" (an oil line) is ordinary prose, so a term must carry the
# pipeline's own frame: the brief, a fact sheet, "the material provided", or
# the narrator speaking about the data it was given.
_META_RE = {
    "vi": re.compile(
        r"\bbrief\b|\bdata\b|\bjson\b|"
        # CRV-0928b-1: a bare "dữ kiện" is everyday police/prose vocabulary
        # ("cảnh sát xếp lại các dữ kiện của vụ án"), so a fact term must carry
        # the pipeline's own frame: the fact table, or facts handed to the
        # writer.
        r"bảng\s+dữ\s+kiện|"
        # CRV-0928c-3: a "… cung cấp bởi/cho/từ <agent>" participle is prose
        # ("thông tin được cung cấp bởi người hàng xóm"), so the pipeline frame
        # only counts when no agent or recipient follows.
        r"dữ\s+kiện\s+(?:đã\s+|được\s+)?cung\s+cấp(?!\s+(?:bởi|cho|từ)\b)|"
        r"tài liệu\s+(?:đã\s+|được\s+)?cung cấp(?!\s+(?:bởi|cho|từ)\b)|"
        r"dữ liệu\s+(?:được\s+cung cấp(?!\s+(?:bởi|cho|từ)\b)|"
        r"(?:tôi|mình)\s+có(?!\s+(?:được\s+)?từ\b))|"
        # The missing-facts disclaimer stays ("không có dữ kiện nào", "thiếu
        # dữ liệu"); only the bare noun is gone. CRV-0928c-3: bare "thông tin"
        # is everyday talk ("không có thông tin gì về người đàn ông ấy"), so it
        # only counts in the disclaimer shape "… thông tin nào".
        r"(?:không có|thiếu)\s+(?:dữ liệu|dữ kiện)|"
        r"(?:không có|thiếu)\s+thông tin\s+nào\b|"
        # CRV-0928b-1: a bare "đầu vào/đầu ra" is body language ("thò đầu ra
        # ngoài cửa sổ", "gục đầu vào vai mẹ"), so it needs a data frame.
        r"(?:dữ liệu|thông tin|dữ kiện|nội dung|văn bản|kết quả|phần|đoạn)"
        r"\s+đầu\s+(?:vào|ra)\b|"
        r"đầu\s+(?:vào|ra)\s+(?:của|cho)\s+"
        r"(?:hệ thống|mô hình|chương trình|mô-đun|bài viết|văn bản|đoạn|"
        r"phần|prompt)\b|"
        r"(?:ở|từ|trong)\s+đầu\s+(?:vào|ra)\b|"
        r"người kể chuyện này|"
        # CRV-0928b-1: "một câu chuyện như vậy" is ordinary talk ("tôi chưa
        # từng nghe một câu chuyện như vậy"); the framing preposition marks the
        # writer speaking about the story it is building. CRV-0928c-3 gives
        # "như thế này" the same frame ("tôi chưa bao giờ nghe câu chuyện như
        # thế này" is a listener's line, not pipeline talk).
        r"(?:trong|ở|với|viết)\s+(?:một\s+)?câu chuyện như vậy|"
        r"(?:trong|ở|với|viết)\s+(?:một\s+)?câu chuyện như thế này|"
        r"(?:dữ kiện|dữ liệu|thông tin|tài liệu)\s+(?:đã\s+)?(?:được\s+)?duyệt\b|"
        # SEC-0928b-3: "thông tin được cung cấp" / "thông tin tôi có" — the most
        # common Vietnamese LLM disclaimer forms — were unmatched, so they were
        # spoken in the narration and reached TTS. CRV-0928c-3: the same
        # agent/recipient and narrator-source guards as above keep prose out.
        r"\bthông tin\s+(?:đã\s+)?(?:được\s+)?cung cấp(?!\s+(?:bởi|cho|từ)\b)|"
        r"\b(?:thông tin|dữ liệu)\s+(?:mà\s+)?(?:tôi|mình)\s+(?:có|được)"
        r"(?!\s+(?:được\s+)?từ\b)",
        re.IGNORECASE),
    "en": re.compile(
        # "the/this/in the/from the/per the brief" and "brief <meta-verb>" —
        # bare "a brief" is deliberately excluded (the adjective is idiomatic:
        # "a brief silence", "a brief letter"). CRV-0928c-2: "the" also takes
        # the adjective ("the brief silence", "the brief note", "the brief
        # background of a man"), so the article reading only counts when no
        # lowercase word follows — a noun ends the phrase ("per the brief.") or
        # the meta-verb branch below covers it ("the brief says …").
        r"\b(?:the|this|in the|from the|per the)\s+brief\b(?!\s+(?-i:[a-z]))|"
        r"\bbrief\s+(?:says|states|calls|mentions|describes|gives|asks|"
        r"requests|provides|supplies|carries)\b|"
        # SEC-0928b-3: the literal "fact sheet" missed the hyphenated/joined
        # forms ("fact-sheet", "factsheet"); possessive and first-person
        # framings ("my brief", "the facts I was given") also slipped through.
        r"fact[\s-]?sheet|"
        r"\b(?:my|our)\s+brief\b|"
        r"\bthe (?:facts|information|records) (?:i|we)"
        r"(?:\s+(?:was|were|have\s+been))?\s+(?:given|provided)\b|"
        r"data object|the prompt\b|"
        r"\bno data\b|"
        # "the pages/records/facts/details/material/sources provided|supplied|
        # requested|given" — the pipeline's own framing. A participle that
        # takes an agent or recipient is prose: "the evidence provided by the
        # neighbour", "the evidence provided to the court".
        r"\b(?:the|these|those)\s+(?:pages?|records?|accounts?|facts?|details|"
        r"information|data|evidence|material|sources?)\s+(?:provided|supplied|"
        r"requested|given)\b(?!\s+(?:by|to|for|from)\b)|"
        # CRV-0928b-2: "available" is a plain adjective mid-clause ("the
        # evidence available was a single shoe"), so it only counts when it
        # closes the clause — the disclaimer shape ("work from the material
        # available.").
        r"\b(?:the|these|those)\s+(?:pages?|records?|accounts?|facts?|details|"
        r"information|data|evidence|material|sources?)\s+available\b"
        r"(?=\s*[.!?…,;:)\]\u201d\u2019\u00bb]|\s*$)|"
        # CRV-0928b-2: bare "the requested" is ordinary prose ("the requested
        # files"); the participle branch above still covers "the facts
        # requested". "the narrator" must be the writer speaking about itself —
        # a story can have a narrator character ("the narrator of the old radio
        # play").
        r"\b(?:this|your)\s+narrator\b|"
        r"this account establishes|as supplied\b|"
        r"\b(?:story|content|writing|text|prompt|generation|render|image|video)"
        r"\s+pipeline\b",
        re.IGNORECASE),
}
# The stock reveal opener ("Tôi hiểu…" / "I understand…") is banned on the
# `reveal` beat, on `twist` (the reveal formula leaks there too — the review's
# vi-b5) and in `ending` (the reveal may fold into ending at `short`).
# QA D4 (2026-09-28): only a sentence that *opens* with the formula is the
# stock tic — a realisation verb mid-sentence ("Then I understood…"), inside a
# quoted line, or with a speech tag ("I understand, he said") is ordinary
# prose. vi also bans the third-person copy of the formula, anywhere in those
# beats ("cô ấy hiểu ra mọi chuyện").
_STOCK_REVEAL_OPENER = {
    "vi": re.compile(r"tôi\s+(?:đã\s+|mới\s+|chợt\s+)?(?:hiểu|nhận\s+ra)\b",
                     re.IGNORECASE),
    # CRV-0928b-3: vi allows an adverb between the pronoun and the verb
    # ("tôi chợt hiểu…"), so en must too, or the same stock tic passes in en
    # and blocks in the mirror language.
    "en": re.compile(r"(?:i\s+(?:(?:suddenly|finally|now|then|only)\s+)?"
                     r"(?:understand|understood|realise|realize|realised|"
                     r"realized|recognise|recognize|recognised|recognized)|"
                     r"now\s+i\s+know)\b", re.IGNORECASE),
}
_STOCK_REVEAL_THIRD_VI = re.compile(r"hiểu\s+ra\s+(?:mọi\s+chuyện|tất\s+cả)\b",
                                    re.IGNORECASE)
_QUOTED_SPAN = re.compile(r'"[^"\n]*"|“[^”\n]*”|«[^»\n]*»')
_EN_SPEECH_TAG = re.compile(
    r"\s*,\s*(?:\w+\s+){0,2}(?:said|says|asked|replied|answered|added|"
    r"whispered|murmured|continued|shouted|called|told)\b", re.IGNORECASE)


def _starts_sentence(text: str, pos: int) -> bool:
    """True when `pos` opens a sentence: start of text, a line break, or a
    terminator (optionally behind a closing quote/bracket) followed by nothing
    but whitespace.

    PRF-0928b-1: walks left over that whitespace (and the closing quotes)
    instead of copying `text[:pos]` and rescanning it, which made every
    candidate O(n) and a segment full of repeated openers quadratic."""
    i = pos
    while i and text[i - 1].isspace():
        i -= 1
    if i == 0:
        return True
    # The definition's `\n` branch: a line break with nothing but whitespace
    # after it (the skipped run is maximal, so the break can sit inside it).
    if "\n" in text[i:pos]:
        return True
    j = i
    while j and text[j - 1] in "\"'”’»)]":
        j -= 1
    return j > 0 and text[j - 1] in ".!?…"


def _stock_reveal_formula(text: str, lang: str) -> str:
    """The first banned stock-reveal formula in `text`, or "".

    `en`: the formula must open a sentence and must not sit inside a quoted
    span nor take a speech tag. `vi`: a sentence-opening first-person formula,
    or — anywhere — the third-person "hiểu ra mọi chuyện/tất cả" copy."""
    quotes = [(span.start(), span.end()) for span in _QUOTED_SPAN.finditer(text)]

    def quoted(pos: int) -> bool:
        return any(start <= pos < end for start, end in quotes)

    candidates = []
    for match in _STOCK_REVEAL_OPENER[lang].finditer(text):
        if not _starts_sentence(text, match.start()) or quoted(match.start()):
            continue
        if lang == "en" and _EN_SPEECH_TAG.match(text, match.end()):
            continue
        candidates.append(match)
    if lang == "vi":
        candidates.extend(_STOCK_REVEAL_THIRD_VI.finditer(text))
    if not candidates:
        return ""
    return min(candidates, key=lambda match: match.start()).group(0)

HONORIFICS = frozenset(("mr", "mrs", "ms", "dr", "sir", "lady", "captain",
                        "detective", "officer", "professor", "saint"))


def token_count(text: str, lang: str = "vi") -> int:
    """D4 token rule: NFC normalise, split on Unicode whitespace, count non-empty."""
    normalised = unicodedata.normalize("NFC", text or "")
    return sum(1 for token in re.split(r"\s+", normalised) if token)


def count_sentences(text: str) -> int:
    """Sentence count for the hook rule: terminator runs followed by space/end."""
    stripped = unicodedata.normalize("NFC", text or "").strip()
    if not stripped:
        return 0
    count = len(_SENTENCE_END.findall(stripped))
    return count if count else 1


def _segments(story: dict) -> list[tuple[str, dict]]:
    """(address, segment) in D3 order: hook, p:<i>, ending. Non-dict segments
    and a non-dict `story` are skipped, never raised on (SEC-02D-2)."""
    out: list[tuple[str, dict]] = []
    if not isinstance(story, dict):
        return out
    hook = story.get("hook")
    if isinstance(hook, dict):
        out.append(("hook", hook))
    paragraphs = story.get("paragraphs")
    if isinstance(paragraphs, list):
        for index, paragraph in enumerate(paragraphs):
            if isinstance(paragraph, dict):
                out.append((f"p:{index}", paragraph))
    ending = story.get("ending")
    if isinstance(ending, dict):
        out.append(("ending", ending))
    return out


def shape_reason(story) -> str | None:
    """D-SHAPE / SEC-02D-2: the minimal `story.v1` shape the harness derives from.

    Returns None when the story can be safely derived and validated, otherwise a
    short reason (`hook_not_object`, `paragraphs_not_list_of_objects`, …). The
    harness maps a failure to outcome `invalid_output` with reason
    `schema_shape:<reason>` instead of crashing after a paid call."""
    if not isinstance(story, dict):
        return "not_an_object"
    if not isinstance(story.get("hook"), dict):
        return "hook_not_object"
    if not isinstance(story.get("ending"), dict):
        return "ending_not_object"
    paragraphs = story.get("paragraphs")
    if not isinstance(paragraphs, list) or not all(isinstance(p, dict) for p in paragraphs):
        return "paragraphs_not_list_of_objects"
    for _, segment in _segments(story):
        for key in ("keywords", "fact_refs"):
            value = segment.get(key)
            if value is not None and not isinstance(value, list):
                return f"{key}_not_list"
        keywords = segment.get("keywords")
        if isinstance(keywords, list) and not all(isinstance(k, str) for k in keywords):
            return "keyword_not_string"
    return None


def derive_body(story: dict) -> str:
    """D3 derived `body` = hook.text + paragraphs texts + ending.text, blank-line
    joined. Non-dict segments are skipped (SEC-02D-2), so a malformed story
    cannot raise here."""
    return "\n\n".join(str(segment.get("text", "")) for _, segment in _segments(story))


def derive_keywords(story: dict) -> list[str]:
    """D3 derived `keywords` = hook ++ paragraphs ++ ending, in order. A segment
    without a `keywords` list contributes nothing (SEC-02D-2)."""
    out: list[str] = []
    for _, segment in _segments(story):
        keywords = segment.get("keywords")
        if isinstance(keywords, list):
            out.extend(str(keyword) for keyword in keywords)
    return out


def _keyword_issues(keyword: str, segment: str, add) -> None:
    text = str(keyword)
    words = text.split()
    if len(words) < KEYWORD_MIN_WORDS:
        add("keyword_too_short", BLOCKING, segment, f"{text!r} has {len(words)} word(s)")
    elif len(words) > KEYWORD_MAX_WORDS:
        add("keyword_too_long", BLOCKING, segment, f"{text!r} has {len(words)} word(s)")
    if not all(_ASCII_WORD.match(word) for word in words):
        add("keyword_not_english", BLOCKING, segment, text)
    # T85 / 02.2.1-T1 (c) R5 partial: only a capitalised token after the first is
    # a person-name signal; the first token may be capitalised (sentence case).
    if (any(_CAPITALISED.match(word) for word in words[1:])
            or any(word.lower() in HONORIFICS for word in words)):
        add("keyword_person_name", BLOCKING, segment, text)


def validate(story: dict, tier: str, lang: str, limits: dict) -> list[dict]:
    """Return the D3 issue list: [{"rule","severity","segment","evidence"}]."""
    issues: list[dict] = []

    def add(rule: str, severity: str, segment: str, evidence: str) -> None:
        issues.append({"rule": rule, "severity": severity,
                       "segment": segment, "evidence": evidence})

    if not isinstance(story, dict):
        add("not_a_story_object", BLOCKING, "meta", f"got {type(story).__name__}")
        return issues

    title = str(story.get("title", ""))
    if len(title) > TITLE_MAX_CHARS:
        add("title_too_long", BLOCKING, "title", f"{len(title)} chars")
    if "<" in title or ">" in title:
        add("title_has_angle_brackets", BLOCKING, "title", title)
    if _URL.search(title) or _MARKUP.search(title.replace("<", "").replace(">", "")):
        add("metadata_has_markup", BLOCKING, "title", title)

    segments = _segments(story)
    for address, segment in segments:
        text = str(segment.get("text", ""))
        if not text.strip():
            add("empty_segment", ADVISORY, address, "empty text")
        if _URL.search(text):
            add("url_in_text", BLOCKING, address, text[:120])
        if _MARKUP.search(text):
            add("markdown_or_bracket", BLOCKING, address, text[:120])
        if _LIST_MARKER.search(text):
            add("list_marker", BLOCKING, address, text[:120])
        non_latin = _non_latin_letters(text)
        if non_latin:
            add("non_latin_script", BLOCKING, address,
                f"non-Latin letters {sorted(set(non_latin))!r} in text")
        # SEC-0928b-3: meta/stock matching runs on NFC-normalised text (as
        # `_non_latin_letters` already does) — decomposed Vietnamese ("dữ kiện"
        # in NFD) otherwise never matches the phrase lists. The raw `text` is
        # kept for URL/markup/evidence so the reported excerpt matches the
        # artefact byte-for-byte.
        normalised = unicodedata.normalize("NFC", text)
        meta = _META_RE.get(lang)
        if meta:
            match = meta.search(normalised)
            if match:
                add("meta_narration", BLOCKING, address,
                    f"pipeline/meta term {match.group(0)!r} in text")
        # The reveal must not open with the stock "Tôi hiểu… / I understand…"
        # formula (pre-review R3). Checked on `reveal` and `twist` paragraphs —
        # the formula leaks into twist when the beats merge (vi-spiritual-b5) —
        # and on `ending` (the reveal may fold into it at `short`).
        if lang in _STOCK_REVEAL_OPENER and (address == "ending"
                                             or str(segment.get("beat")) in ("reveal", "twist")):
            formula = _stock_reveal_formula(normalised, lang)
            if formula:
                add("stock_reveal_opener", BLOCKING, address,
                    f"stock reveal formula {formula!r}")

        keywords = segment.get("keywords") or []
        if not isinstance(keywords, list):
            keywords = []
        if tier == "short":
            if len(keywords) != 1:
                add("keyword_count_out_of_range", BLOCKING, address,
                    f"{len(keywords)} keywords (short needs exactly 1)")
        elif not (KEYWORDS_PER_SEGMENT_MIN <= len(keywords) <= KEYWORDS_PER_SEGMENT_MAX):
            add("keyword_count_out_of_range", BLOCKING, address,
                f"{len(keywords)} keywords")
        for keyword in keywords:
            _keyword_issues(keyword, address, add)

        for ref in segment.get("fact_refs") or []:
            allowed = set(limits.get("fact_ids") or [])
            if str(ref) not in allowed:
                add("unknown_fact_ref", BLOCKING, address, str(ref))

    hook = story.get("hook") if isinstance(story.get("hook"), dict) else {}
    if count_sentences(str(hook.get("text", ""))) > HOOK_MAX_SENTENCES:
        add("hook_too_long", BLOCKING, "hook",
            f"{count_sentences(str(hook.get('text', '')))} sentences")

    paragraphs = [p for p in (story.get("paragraphs") or []) if isinstance(p, dict)]
    segment_count = len(paragraphs) + 2
    if segments:
        # T99 (owner 2026-09-26): segment count is advisory in the M0 trial
        # validator; the band values themselves are the T103 per-language
        # bands in LENGTH.v1.json.
        if not (int(limits["min_segments"]) <= segment_count <= int(limits["max_segments"])):
            add("segment_count_out_of_range", ADVISORY, "meta",
                f"{segment_count} segments, D4 band {limits['min_segments']}-{limits['max_segments']} (advisory, 02 T99)")

    if tier == "short":
        beats = [str(p.get("beat", "")) for p in paragraphs]
        if beats not in (list(SHORT_BEATS), list(SHORT_BEATS[:2])):
            # advisory (D-SHORTBEATS / QA-02D-3): the task contract says the
            # short beat floor is reported, not blocking; the D4 segment band
            # is the objective check.
            add("short_beats_missing", ADVISORY, "meta",
                f"beats {beats}, need {list(SHORT_BEATS)} or {list(SHORT_BEATS[:2])}")

    # Beat order (2026-09-28 pre-review R5): the canonical sequence is
    # non-decreasing — every build_up before the first clue, every clue before
    # twist, reveal before aftermath. Advisory: the prompt states the order;
    # this only reports a regression, mirroring short_beats_missing.
    last_rank = -1
    for index, paragraph in enumerate(paragraphs):
        beat = str(paragraph.get("beat", ""))
        if beat not in ALL_BEATS:
            add("unknown_beat", ADVISORY, f"p:{index}",
                beat if beat else "missing beat")
            continue
        rank = ALL_BEATS.index(beat)
        if rank < last_rank:
            add("beat_order", ADVISORY, f"p:{index}",
                f"beat {beat!r} after {ALL_BEATS[last_rank]!r}, order "
                + " -> ".join(ALL_BEATS))
        last_rank = max(last_rank, rank)

    body_tokens = token_count(derive_body(story), lang)
    minimum = int(limits["min_tokens"])
    maximum = int(limits["max_tokens"])
    # T99 (owner 2026-09-26): length is advisory in the M0 trial validator —
    # over- and under-length are reported, not blocked; the bands are the
    # T103 per-language values in LENGTH.v1.json.
    if body_tokens < minimum * 0.85 or body_tokens > maximum * 1.15:
        add("length_out_of_range", ADVISORY, "meta",
            f"{body_tokens} tokens, D4 {minimum}-{maximum}, >15% outside (advisory, 02 T99)")
    elif body_tokens < minimum or body_tokens > maximum:
        add("length_out_of_range", ADVISORY, "meta",
            f"{body_tokens} tokens, D4 {minimum}-{maximum}, inside 15%")

    keyword_total = len(derive_keywords(story))
    cap = math.ceil(TIER_MINUTES_MAX.get(tier, 1.0) * KEYWORDS_PER_MINUTE)
    if keyword_total > cap:
        add("keyword_count_out_of_range", BLOCKING, "meta",
            f"{keyword_total} keywords total, cap ceil(D x 6) = {cap}")

    description = str(story.get("description", ""))
    if len(description) > DESCRIPTION_MAX_CHARS:
        add("description_too_long", BLOCKING, "meta", f"{len(description)} chars")
    if _URL.search(description) or _MARKUP.search(description):
        add("metadata_has_markup", BLOCKING, "meta", description[:120])

    tags = [t for t in (story.get("tags") or []) if isinstance(t, str)]
    if len(tags) > TAGS_MAX:
        add("tags_too_many", BLOCKING, "meta", str(len(tags)))
    for tag in tags:
        if len(tag) > TAG_MAX_CHARS:
            add("tag_too_long", BLOCKING, "meta", tag)
        if _URL.search(tag) or any(ch in tag for ch in "<>,#"):
            add("metadata_has_markup", BLOCKING, "meta", tag)
    if sum(len(tag) for tag in tags) > TAGS_TOTAL_MAX_CHARS:
        add("tag_total_too_long", BLOCKING, "meta", str(sum(len(t) for t in tags)))

    seen: set[str] = set()
    for index, paragraph in enumerate(paragraphs):
        key = unicodedata.normalize("NFC", str(paragraph.get("text", "")))
        if key and key in seen:
            add("duplicate_paragraph", ADVISORY, f"p:{index}", key[:80])
        seen.add(key)

    return issues


def blocking_issues(issues: list[dict]) -> list[dict]:
    return [issue for issue in issues if issue["severity"] == BLOCKING]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="story_validator",
        description="Throwaway D3 validator for a story.v1 JSON file (02.1.3-T0).")
    parser.add_argument("story", help="path to a story.v1 JSON file")
    parser.add_argument("--tier", default="short", choices=["short", "standard", "long"])
    parser.add_argument("--lang", default="vi", choices=["vi", "en"])
    parser.add_argument("--limits", default=str(Path(__file__).with_name("LENGTH.v1.json")),
                        help="path to LENGTH.v1.json")
    args = parser.parse_args(argv)

    try:
        limits_path = Path(args.limits)
        if not limits_path.exists():
            print(f"story_validator: limits file not found: {limits_path}", file=sys.stderr)
            return 2
        table = json.loads(limits_path.read_text(encoding="utf-8"))
        limits = dict(table[args.tier][args.lang])
        story_path = Path(args.story)
        if not story_path.exists():
            print(f"story_validator: story file not found: {story_path}", file=sys.stderr)
            return 2
        story = json.loads(story_path.read_text(encoding="utf-8"))
    except (KeyError, ValueError, OSError) as exc:
        print(f"story_validator: {exc}", file=sys.stderr)
        return 2

    issues = validate(story, args.tier, args.lang, limits)
    print(json.dumps(issues, ensure_ascii=False, indent=2))
    return 1 if blocking_issues(issues) else 0


if __name__ == "__main__":
    sys.exit(main())
