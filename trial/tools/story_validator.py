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
        if not (int(limits["min_segments"]) <= segment_count <= int(limits["max_segments"])):
            add("segment_count_out_of_range", BLOCKING, "meta",
                f"{segment_count} segments, D4 band {limits['min_segments']}-{limits['max_segments']}")

    if tier == "short":
        beats = [str(p.get("beat", "")) for p in paragraphs]
        if beats not in (list(SHORT_BEATS), list(SHORT_BEATS[:2])):
            # advisory (D-SHORTBEATS / QA-02D-3): the task contract says the
            # short beat floor is reported, not blocking; the D4 segment band
            # is the objective check.
            add("short_beats_missing", ADVISORY, "meta",
                f"beats {beats}, need {list(SHORT_BEATS)} or {list(SHORT_BEATS[:2])}")

    body_tokens = token_count(derive_body(story), lang)
    minimum = int(limits["min_tokens"])
    maximum = int(limits["max_tokens"])
    if body_tokens < minimum * 0.85 or body_tokens > maximum * 1.15:
        add("length_out_of_range", BLOCKING, "meta",
            f"{body_tokens} tokens, D4 {minimum}-{maximum}, >15% outside")
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
