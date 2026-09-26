#!/usr/bin/env python3
"""M0 check of `seed/style-templates.v1.json` against the README D1 rules (03.1.1-T1).

Used in M0, before any C# exists; superseded at M1 by `StyleTemplateRules.Validate`
and the seed test `SeedFile_AllTemplatesPassRules` (03.1.1-T2). stdlib only:

    uv run --no-project --python 3.12 trial/tools/check_style_templates.py seed/style-templates.v1.json
    uv run --no-project --python 3.12 trial/tools/check_style_templates.py --self-test

Exit codes: 0 ok / 1 check failed (rule name printed) / 2 usage error.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from copy import deepcopy
from pathlib import Path

EXPECTED_ROWS = 7
MIN_LENGTH = 60
MAX_LENGTH = 600
SLUG_MIN = 3
SLUG_MAX = 40
TERM = "{term}"
SCHEMA = "style-templates.v1"
NEGATIVE_CLAUSE_VERSION = "v1"
TOPICS = ("ghost", "spiritual", "true_crime")
TRUE_CRIME_TOPIC = "true_crime"
TRUE_CRIME_PEOPLE_RULES = ("no faces", "no identifiable people")
FORBIDDEN = ("no gore", "no text", "watermark")
SLUG_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
PREVIEW_KEY_PREFIX = "style-previews/"
PREVIEW_KEY_SUFFIX = ".v1.webp"

Problem = tuple


def validate_row(index: int, row: object) -> list[Problem]:
    """Return [(rule, message)] for one template row."""
    if not isinstance(row, dict):
        return [("row_shape", f"row {index}: not a JSON object")]
    name = row.get("name")
    template = row.get("template")
    if not isinstance(name, str):
        problems: list[Problem] = [("slug", f"row {index}: name missing or not a string")]
    elif not SLUG_MIN <= len(name) <= SLUG_MAX or not SLUG_RE.match(name):
        problems = [("slug", f"{name}: not a {SLUG_MIN}-{SLUG_MAX} char slug")]
    else:
        problems = []
    if not isinstance(template, str):
        problems.append(("row_shape", f"{index}/{name}: template missing or not a string"))
        return problems
    text = template.strip()
    count = text.count(TERM)
    if count != 1:
        problems.append(("term_count", f"{name}: {count} x {TERM}, expected exactly 1"))
    if not MIN_LENGTH <= len(text) <= MAX_LENGTH:
        problems.append(("length", f"{name}: {len(text)} chars, expected {MIN_LENGTH}..{MAX_LENGTH}"))
    if "\n" in template or "\r" in template:
        problems.append(("linebreak", f"{name}: template contains a line break"))
    if '"' in template:
        problems.append(("quote", f'{name}: template contains a double quote'))
    lowered = template.lower()
    for word in FORBIDDEN:
        if word in lowered:
            problems.append(
                ("forbidden", f"{name}: contains {word!r} — the negative clause is appended by ScenePromptBuilder (D1)")
            )
    hint = row.get("topicHint")
    if hint not in TOPICS:
        problems.append(("topic_hint", f"{name}: topicHint {hint!r} not in {TOPICS}"))
    if not isinstance(row.get("active"), bool):
        problems.append(("active", f"{name}: active must be a JSON boolean"))
    preview_key = row.get("previewStorageKey", "missing")
    expected_key = f"{PREVIEW_KEY_PREFIX}{name}{PREVIEW_KEY_SUFFIX}"
    if preview_key == "missing" or (preview_key is not None and preview_key != expected_key):
        problems.append(
            (
                "preview_storage_key",
                f"{name}: previewStorageKey must be present, null in v1, or {expected_key!r} (03.1.2-T1 fills it)",
            )
        )
    if hint == TRUE_CRIME_TOPIC:
        for phrase in TRUE_CRIME_PEOPLE_RULES:
            if phrase not in lowered:
                problems.append(("true_crime_people_rule", f"{name}: true_crime template must contain {phrase!r}"))
    return problems


def validate_doc(doc: object) -> list[Problem]:
    """Return [(rule, message)] for a whole seed document."""
    if not isinstance(doc, dict):
        return [("schema", "root is not a JSON object")]
    problems: list[Problem] = []
    if doc.get("schema") != SCHEMA:
        problems.append(("schema", f"schema is {doc.get('schema')!r}, expected {SCHEMA!r}"))
    if doc.get("negativeClauseVersion") != NEGATIVE_CLAUSE_VERSION:
        problems.append(
            ("negative_clause_version", f"negativeClauseVersion is {doc.get('negativeClauseVersion')!r}, expected {NEGATIVE_CLAUSE_VERSION!r}")
        )
    rows = doc.get("templates")
    if not isinstance(rows, list):
        problems.append(("row_count", "templates is missing or not an array"))
        return problems
    if len(rows) != EXPECTED_ROWS:
        problems.append(("row_count", f"{len(rows)} rows, expected {EXPECTED_ROWS}"))
    names = [row.get("name") for row in rows if isinstance(row, dict)]
    if len(set(names)) != len(names):
        problems.append(("unique_names", f"duplicate name in {names}"))
    true_crime = 0
    for index, row in enumerate(rows):
        problems.extend(validate_row(index, row))
        if isinstance(row, dict) and row.get("topicHint") == TRUE_CRIME_TOPIC:
            true_crime += 1
    if true_crime < 2:
        problems.append(("true_crime_count", f"{true_crime} true_crime rows, expected >= 2"))
    return problems


def check_file(path: Path) -> int:
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError as error:
        print(f"usage: cannot read {path}: {error}", file=sys.stderr)
        return 2
    try:
        doc = json.loads(raw)
    except json.JSONDecodeError as error:
        print(f"FAIL json: {path}: {error}", file=sys.stderr)
        return 1
    problems = validate_doc(doc)
    if problems:
        for rule, message in problems:
            print(f"FAIL {rule}: {message}", file=sys.stderr)
        return 1
    print(f"{EXPECTED_ROWS} templates ok")
    return 0


# --- self-test ---------------------------------------------------------------------------

FIXTURE_TEMPLATE = (
    "Test illustration of {term}. Muted palette, paper texture, soft daylight, wide framing, "
    "figures only as distant shapes, calm mood."
)
FIXTURE_TRUE_CRIME = (
    "Test sketch of {term}. Monochrome, rough hatching, one hard light, places and objects only, "
    "no faces, no identifiable people, sober mood."
)


def _fixture() -> dict:
    topics = ["ghost", "ghost", "ghost", "spiritual", "spiritual", TRUE_CRIME_TOPIC, TRUE_CRIME_TOPIC]
    rows = []
    for index, topic in enumerate(topics):
        template = FIXTURE_TRUE_CRIME if topic == TRUE_CRIME_TOPIC else FIXTURE_TEMPLATE
        rows.append(
            {
                "name": f"test-style-{index}",
                "topicHint": topic,
                "active": True,
                "previewStorageKey": None,
                "template": template,
            }
        )
    return {"schema": SCHEMA, "negativeClauseVersion": NEGATIVE_CLAUSE_VERSION, "templates": rows}


def _padded(length: int) -> str:
    base = "Test illustration of {term}"
    filler = "x" * (length - len(base) - 2)
    return f"{base}, {filler}"


def self_test() -> int:
    cases: list[tuple[str, dict, set]] = []

    def mutate(label: str, edit, expected: set) -> None:
        doc = _fixture()
        edit(doc)
        cases.append((label, doc, expected))

    cases.append(("valid fixture", _fixture(), set()))
    mutate("no {term}", lambda d: d["templates"][0].__setitem__("template", "Test illustration without a placeholder, muted palette and soft light, calm mood."), {"term_count"})
    mutate("two {term}", lambda d: d["templates"][0].__setitem__("template", FIXTURE_TEMPLATE.replace("{term}", "{term} {term}")), {"term_count"})
    mutate("59 chars", lambda d: d["templates"][0].__setitem__("template", _padded(59)), {"length"})
    mutate("601 chars", lambda d: d["templates"][0].__setitem__("template", _padded(601)), {"length"})
    mutate("trailing newline", lambda d: d["templates"][0].__setitem__("template", FIXTURE_TEMPLATE + "\n"), {"linebreak"})
    mutate("double quote", lambda d: d["templates"][0].__setitem__("template", FIXTURE_TEMPLATE.replace("Muted palette", 'Muted "palette"')), {"quote"})
    mutate("no gore", lambda d: d["templates"][0].__setitem__("template", FIXTURE_TEMPLATE + " No gore."), {"forbidden"})
    mutate("6 rows", lambda d: d["templates"].pop(0), {"row_count"})
    mutate("preview key set (canonical)", lambda d: d["templates"][0].__setitem__("previewStorageKey", "style-previews/test-style-0.v1.webp"), set())
    mutate("preview key set (wrong key)", lambda d: d["templates"][0].__setitem__("previewStorageKey", "style-previews/test.webp"), {"preview_storage_key"})
    mutate("preview key missing", lambda d: d["templates"][0].pop("previewStorageKey"), {"preview_storage_key"})
    mutate(
        "true_crime without people rule",
        lambda d: d["templates"][5].__setitem__("template", FIXTURE_TRUE_CRIME.replace("no faces, no identifiable people", "empty scene")),
        {"true_crime_people_rule"},
    )

    failures = 0
    for label, doc, expected in cases:
        rules = {rule for rule, _ in validate_doc(deepcopy(doc))}
        if rules != expected:
            print(f"FAIL self-test: {label}: rules {sorted(rules)} != expected {sorted(expected)}", file=sys.stderr)
            failures += 1
    if failures:
        print(f"self-test FAILED ({failures}/{len(cases)} cases)", file=sys.stderr)
        return 1
    print(f"self-test ok ({len(cases)} cases)")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Check seed/style-templates.v1.json against the D1 rules (03.1.1-T1).")
    parser.add_argument("seed", nargs="?", help="path to style-templates.v1.json")
    parser.add_argument("--self-test", action="store_true", help="run the built-in failure cases")
    args = parser.parse_args(argv)
    if args.self_test:
        if args.seed:
            print("usage: pass either a seed file or --self-test, not both", file=sys.stderr)
            return 2
        return self_test()
    if not args.seed:
        print("usage: check_style_templates.py <seed/style-templates.v1.json> | --self-test", file=sys.stderr)
        return 2
    return check_file(Path(args.seed))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
