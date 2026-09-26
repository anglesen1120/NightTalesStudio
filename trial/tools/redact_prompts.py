#!/usr/bin/env python3
"""redact_prompts.py — replace prompt text in spike samples with its SHA-256
(03.4.1-T1, 03 README D12: samples hold hashes, never prompt text).

Two modes:

  text mode (default): every `term='…'` / `term="…"` value — the form MPT's
      material.py logs use (`term={search_term!r}`) — becomes
      `term=<prompt sha256=…16-hex-prefix…>`. The 16-hex prefix keeps log lines
      readable; the full hash is in stub-calls.jsonl.

  --json: walk a parsed JSON document; every list of strings under a key named
      `terms`, `video_terms`, `search_terms` or `prompt` is replaced by
      `"<prompt sha256=…16-hex-prefix…>"` entries, and a string under those keys
      becomes the same placeholder. Emits the document pretty-printed on stdout.

Run:  uv run --no-project --python 3.12 trial/tools/redact_prompts.py [flags] [< IN | --in FILE]
      [> OUT | --out FILE]
Exit codes (01 D9): 0 ok · 1 self-test/check failed · 2 usage error.
"""

import argparse
import hashlib
import json
import re
import sys

PROMPT_KEYS = ("terms", "video_terms", "search_terms", "prompt", "script", "video_script")
# Any quoted value that carries the platform negative clause is a styled prompt.
CLAUSE_HINT = "no real person likeness."

# term='…' / term="…" as logged by `{search_term!r}`: the quote ends the value at
# the next unescaped matching quote (repr() escapes \n, \t, \x.. and \'.
TERM_RE = re.compile(
    r"term=\'(?:[^\'\\]|\\.)*\'"   # term='...' single-quoted
    r'|term="(?:[^"\\]|\\.)*"'     # term="..." double-quoted
)
# A prompt-bearing key on its own line in a JSON dump (MPT logs the whole task
# body at INFO on creation: `logger.success(f"Task created: {utils.to_json(task)}")`).
KEY_LINE_RE = re.compile(
    r'^(?P<indent>\s*)"(?P<key>' + "|".join(PROMPT_KEYS) + r')":\s*(?P<value>.+?)(?P<comma>,?)$',
    re.MULTILINE,
)
# An already-published marker. Re-hashing one of these would publish a hash of a
# hash (useless for attribution, and different on every pass), so `marker()`
# refuses to hash its own output — that is what makes both modes idempotent
# (03.4.1-T1: repeated passes over the committed samples used to corrupt them).
MARKER_RE = re.compile(r"^<prompt sha256=[0-9a-f]{16}>$")
# A bare quoted string inside such a dump that ends with the negative clause.
CLAUSE_ITEM_RE = re.compile(r'"(?P<value>(?:[^"\\]|\\.)*' + re.escape(CLAUSE_HINT) + r')"')
# MPT's subtitle.py logs every ASR correction as
# `… - Script: <narration>, Subtitle: <corrected narration>` (WARNING, lines 274/286).
# Both operands are story text, so both are hashed in place — the line keeps its
# timestamp, level, file:line anchor and Merged/Corrected-vs-Mismatch marker.
NARRATION_RE = re.compile(
    r"(?P<head>- Script: )(?P<script>.*), Subtitle: (?P<subtitle>[^\n]*)$",
    re.MULTILINE,
)


def eprint(*a):
    print(*a, file=sys.stderr)


def marker(text):
    """The published form of a prompt value: first 16 hex of its sha256."""
    if MARKER_RE.fullmatch(text):
        return text
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]
    return f"<prompt sha256={digest}>"


def _mark_quoted(quoted):
    """Hash a JSON/JSON-ish quoted literal, preserving its value form."""
    try:
        value = json.loads(quoted)
    except ValueError:
        value = _repr_content(quoted.strip('"'))
    return json.dumps(marker(value))



def _repr_content(raw):
    """Decode a repr()-style quoted literal: \\' \\n \\t \\xHH — enough for MPT logs."""
    out = []
    i = 0
    while i < len(raw):
        ch = raw[i]
        if ch != "\\":
            out.append(ch)
            i += 1
            continue
        i += 1
        if i >= len(raw):
            out.append("\\")
            break
        esc = raw[i]
        if esc == "n":
            out.append("\n")
        elif esc == "t":
            out.append("\t")
        elif esc == "r":
            out.append("\r")
        elif esc == "\\":
            out.append("\\")
        elif esc in ("'", '"'):
            out.append(esc)
        elif esc == "x" and i + 2 < len(raw):
            try:
                out.append(chr(int(raw[i + 1 : i + 3], 16)))
            except ValueError:
                out.append("\\x" + raw[i + 1 : i + 3])
            i += 2
        else:
            out.append(esc)
        i += 1
    return "".join(out)


def _hash_quoted_on_line(line):
    """Hash every quoted string on one line (used inside a prompt array dump)."""
    return re.sub(r'"(?:[^"\\]|\\.)*"', lambda m: _mark_quoted(m.group(0)), line)


def redact_text(text):
    """Replace every prompt/story value with its marker.

    Four shapes occur in the MPT log: `term='…'` (the per-image lines), the
    full task-body dump MPT logs at INFO on creation (`"video_script": "…"` and
    a `"video_terms": [ … ]` array), stray styled prompts whose value ends with
    the platform negative clause, and subtitle.py's correction lines
    (`… - Script: <narration>, Subtitle: <corrected narration>`) where both
    operands are story text.
    """
    def repl(match):
        token = match.group(0)
        quoted = token[len("term=") :]
        inner = quoted[1:-1]
        if token[len("term=")] == "'":
            value = _repr_content(inner)
        else:
            try:
                value = json.loads(quoted)
            except ValueError:
                value = _repr_content(inner)
        return f"term={marker(value)}"

    text = TERM_RE.sub(repl, text)

    out = []
    depth = 0
    for line in text.splitlines(keepends=True):
        eol = "\n" if line.endswith("\n") else ""
        stripped = line[:-1] if eol else line
        match = KEY_LINE_RE.match(stripped)
        if match:
            value = match.group("value").rstrip(",").rstrip()
            head = f'{match.group("indent")}"{match.group("key")}":'
            if value.startswith('"'):
                tail = "," if match.group("comma") else ""
                out.append(f"{head} {_mark_quoted(value)}{tail}{eol}")
                continue
            # An array (or any other container) on this line: hash what it holds
            # (never the key literal itself) and keep hashing until its `[`/`]`
            # depth returns to zero.
            colon = line.index(":", line.index(f'"{match.group("key")}"'))
            line = line[: colon + 1] + _hash_quoted_on_line(line[colon + 1 :])
            depth += line.count("[") - line.count("]")
            out.append(line.rstrip("\n") + eol)
            continue
        if depth > 0:
            line = _hash_quoted_on_line(line)
            depth += line.count("[") - line.count("]")
        out.append(line)
    text = "".join(out)

    text = NARRATION_RE.sub(
        lambda m: f'{m.group("head")}{marker(m.group("script"))}'
        f', Subtitle: {marker(m.group("subtitle"))}',
        text,
    )

    return CLAUSE_ITEM_RE.sub(lambda m: _mark_quoted(m.group(0)), text)


def redact_json(doc):
    """Walk a JSON tree, hashing prompt-bearing strings in place."""
    if isinstance(doc, dict):
        for key, value in doc.items():
            if key in PROMPT_KEYS:
                if isinstance(value, list):
                    doc[key] = [marker(item) if isinstance(item, str) else item for item in value]
                elif isinstance(value, str):
                    doc[key] = marker(value)
            else:
                doc[key] = redact_json(value)
        return doc
    if isinstance(doc, list):
        return [redact_json(item) for item in doc]
    return doc


def self_test():
    failures = []

    def expect(name, cond, detail=""):
        if cond:
            print(f"PASS selftest:{name}")
        else:
            print(f"FAIL selftest:{name}{': ' + detail if detail else ''}")
            failures.append(name)

    # (a) the task's case: term='a, b' becomes the hash marker
    out = redact_text("openai image generation failed: term='a, b', detail=HTTP 400")
    want = marker("a, b")
    expect("term-comma", f"term={want}" in out, out)
    expect("term-comma-gone", "'a, b'" not in out)

    # (b) double-quoted repr (term contains an apostrophe)
    out = redact_text('generating image via openai-compatible endpoint: model=gpt, term="it\'s a term", size=1024x1536')
    want = marker("it's a term")
    expect("term-apostrophe", f"term={want}" in out, out)
    expect("term-apostrophe-gone", "it's a term" not in out)

    # (c) several terms on the line each get their own marker
    line = "term='one' term='two'"
    out = redact_text(line)
    expect("two-terms", out.count("<prompt sha256=") == 2, out)
    expect("two-terms-distinct", marker("one") in out and marker("two") in out)

    # (d) hash correctness: 16-hex prefix of the real sha256
    expect("hash-prefix", marker("x") == f"<prompt sha256={hashlib.sha256(b'x').hexdigest()[:16]}>")

    # (d2) idempotency: a marker is never hashed again, in either mode. A second
    # pass used to publish sha256 of the marker (03.4.1-T1 sample corruption).
    expect("marker-idempotent", marker(marker("x")) == marker("x"))
    one_line = '    "video_terms": ["a", "b"],\n'
    red_once = redact_text(one_line)
    expect("text-idempotent", redact_text(red_once) == red_once, red_once)
    doc = {"data": {"terms": ["a", "b"], "script": "story", "state": 1}}
    json_once = redact_json(json.loads(json.dumps(doc)))
    json_twice = redact_json(json.loads(json.dumps(json_once)))
    expect("json-idempotent", json_once == json_twice, json.dumps(json_once))
    expect(
        "json-marker-shape",
        json_once["data"]["terms"] == [marker("a"), marker("b")]
        and json_once["data"]["script"] == marker("story")
        and json_once["data"]["state"] == 1,
        json.dumps(json_once),
    )

    # (e) text without terms is unchanged
    plain = "audio duration: 35.1 seconds"
    expect("no-term-passthrough", redact_text(plain) == plain)

    # (e2) the task-body dump MPT logs at INFO on creation: `"video_script"` line
    dump = '    "video_script": "The lighthouse keeper counted seven lamps.",\n    "video_count": 1\n'
    out = redact_text(dump)
    expect("dump-script", marker("The lighthouse keeper counted seven lamps.") in out, out)
    expect("dump-script-gone", "lighthouse keeper" not in out)
    expect("dump-other-keys", '"video_count": 1' in out)

    # (e3) a multi-line `"video_terms": [ … ]` array: every item becomes a marker
    arr = (
        '        "video_terms": [\n'
        '            "styled prompt one,\\n  no gore, no text",\n'
        '            "styled prompt two, no gore, no text"\n'
        "        ],\n"
        '        "video_count": 1\n'
    )
    out = redact_text(arr)
    expect("dump-array-key-kept", '"video_terms": [' in out, out)
    expect("dump-array-items", out.count("<prompt sha256=") == 2, out)
    expect("dump-array-gone", "styled prompt" not in out)
    expect("dump-array-closes", '"video_count": 1' in out)

    # (e4) a single-line array dump
    out = redact_text('"search_terms": ["a", "b"],')
    expect("dump-inline", out.count("<prompt sha256=") == 2 and "search_terms" in out, out)

    # (e5) a stray styled prompt that is not inside a dump
    out = redact_text('note: prompt "cinematic image of a boat, no real person likeness." used')
    expect("clause-net", "<prompt sha256=" in out and "cinematic image" not in out, out)

    # (e6) subtitle.py correction line: `- Script: <narration>, Subtitle: <corrected>`
    # — both operands are story text and are hashed, the line keeps its shape.
    line = (
        '2026-09-26 19:41:02 | WARNING | "./app/services/subtitle.py:274": correct - '
        "Merged/Corrected - Script: yet the fires still answered the sea, "
        "Subtitle: yet the fire still answered the sea\n"
    )
    out = redact_text(line)
    expect("subtitle-script-gone", "answered the sea" not in out, out)
    expect("subtitle-structure", "Merged/Corrected - Script: " in out and ", Subtitle: " in out, out)
    expect("subtitle-two-markers", out.count("<prompt sha256=") == 2, out)
    expect("subtitle-idempotent", redact_text(out) == out, out)

    # (f) JSON mode: terms lists become markers
    doc = {"state": 1, "terms": ["styled prompt one", "styled prompt two"], "task_id": "t1"}
    red = redact_json(doc)
    expect(
        "json-terms",
        red["terms"] == [marker("styled prompt one"), marker("styled prompt two")],
        json.dumps(red["terms"]),
    )
    expect("json-other-fields", red["state"] == 1 and red["task_id"] == "t1")
    expect("json-no-prompt", "styled prompt one" not in json.dumps(red))

    # (g) JSON mode: nested search_terms and a scalar prompt
    doc = {"data": {"search_terms": ["inner term"], "params": {"prompt": "scalar prompt"}}}
    red = redact_json(doc)
    expect("json-nested", red["data"]["search_terms"] == [marker("inner term")])
    expect("json-scalar", red["data"]["params"]["prompt"] == marker("scalar prompt"))

    # (h) JSON mode: non-list/non-str under the key stays untouched
    doc = {"terms": 5}
    red = redact_json(doc)
    expect("json-non-string", red["terms"] == 5)

    return 0 if not failures else 1


def main():
    p = argparse.ArgumentParser(description="Redact prompt text from spike samples (03.4.1-T1).")
    p.add_argument("--json", action="store_true", dest="json_mode", help="treat input as one JSON document")
    p.add_argument("--in", dest="infile", default="", help="input file (default: stdin)")
    p.add_argument("--out", dest="outfile", default="", help="output file (default: stdout)")
    p.add_argument("--self-test", action="store_true", help="run the in-process checks")
    args = p.parse_args()

    if args.self_test:
        sys.exit(self_test())

    try:
        text = open(args.infile, "r", encoding="utf-8").read() if args.infile else sys.stdin.read()
    except OSError as exc:
        eprint(f"redact_prompts.py: cannot read input: {exc}")
        sys.exit(2)

    if args.json_mode:
        try:
            doc = json.loads(text)
        except ValueError as exc:
            eprint(f"redact_prompts.py: input is not JSON: {exc}")
            sys.exit(2)
        out = json.dumps(redact_json(doc), ensure_ascii=False, indent=2) + "\n"
    else:
        out = redact_text(text)

    try:
        if args.outfile:
            with open(args.outfile, "w", encoding="utf-8") as fh:
                fh.write(out)
        else:
            sys.stdout.write(out)
    except OSError as exc:
        eprint(f"redact_prompts.py: cannot write output: {exc}")
        sys.exit(2)
    return 0


if __name__ == "__main__":
    main()
