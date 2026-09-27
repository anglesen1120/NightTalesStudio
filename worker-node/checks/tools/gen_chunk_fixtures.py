#!/usr/bin/env python3
"""gen_chunk_fixtures.py — freeze MPT's own chunking output as drift fixtures (05.5.1-T1).

Run with **MPT's interpreter** (its dependencies are needed to import MPT):

    $MPT_ROOT/.venv/bin/python worker-node/checks/tools/gen_chunk_fixtures.py            # regenerate
    $MPT_ROOT/.venv/bin/python worker-node/checks/tools/gen_chunk_fixtures.py --check    # drift guard

`$MPT_ROOT` defaults to `$NTS_HOME/src/mpt`, `$NTS_HOME` to `<repo>/.nts`
(override with `--mpt-root DIR`).

Inputs: the tracked story texts `trial/fixtures/*.txt` plus small synthetic edge
texts (decimal `2.5`/`1,000`, markdown separator lines, underscores, pause tags,
CJK punctuation, empty). One JSON file per input:

    {"input": "trial/fixtures/spike-story-en.txt" | "synthetic:<id>",
     "input_sha256": "<sha256 of input_text, UTF-8>",
     "input_text": "<the exact text the MPT functions were called with>",
     "normalized": "<normalize_script_for_subtitle_matching(input_text)>",
     "punctuations": ["?", ",", …],          # app/models/const.py PUNCTUATIONS
     "chunks": ["…", …]}                     # split_string_by_punctuations(normalized)

`input_text` is committed so that `render_check.py --self-test` can reproduce
every fixture with the **ported** functions (drift guard in both directions);
it is story text already tracked in `trial/fixtures/`, never anything from
`.nts` (`script.json` content is never committed, D14/T10).

Exit codes (01 D9): 0 fixtures written / no drift · 1 drift (committed files
differ from a fresh run) · 2 usage or environment error (MPT root or import
failing, unreadable input).
"""

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import unicodedata

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
DEFAULT_OUT = os.path.join(REPO, "worker-node", "checks", "fixtures", "chunking")
DEFAULT_NTS_HOME = os.path.join(REPO, ".nts")

# Tracked story/test texts (plan 05 D14: text fixtures only, no media).
TRACKED_INPUTS = (
    "spike-story-en.txt",
    "spike-story-vi.txt",
    "tts-en-5min.txt",
    "tts-vi-5min.txt",
    "tts-en-1000.txt",
    "tts-vi-1000.txt",
    "wiring-en.txt",
)

# Synthetic edge cases for the two ported functions.
SYNTHETIC_INPUTS = (
    ("synthetic-decimal",
     "The fee is 2.5% of 1,000 years, charged at 2.5%\nSecond line stays whole."),
    ("synthetic-markdown-separator",
     "Story title\n---\n***\n___\nbody line\n\n- - -\ntail line"),
    ("synthetic-underscore",
     "under_score_script line\nand a_second one"),
    ("synthetic-pause-tags",
     "First sentence [pause: 2s] second one [pause: nope] third [PAUSA] end."),
    ("synthetic-cjk-punctuation",
     "第一句。第二句？第三句！"),
    ("synthetic-empty", ""),
)


class Usage(Exception):
    """exit 2: usage or environment error."""


def mpt_root_default():
    nts_home = os.environ.get("NTS_HOME") or DEFAULT_NTS_HOME
    return os.path.join(nts_home, "src", "mpt")


def load_mpt(mpt_root):
    """Import MPT's own chunking functions (`app.utils.utils`, `app.models.const`)."""
    if not os.path.isdir(os.path.join(mpt_root, "app")):
        raise Usage("MPT root has no app/ package: %s" % mpt_root)
    sys.path.insert(0, mpt_root)
    try:
        from app.models import const
        from app.utils.utils import (normalize_script_for_subtitle_matching,
                                     split_string_by_punctuations)
    except Exception as exc:  # noqa: BLE001 - any import failure is an environment error
        raise Usage("cannot import MPT from %s: %s: %s" % (mpt_root, type(exc).__name__, exc))
    return normalize_script_for_subtitle_matching, split_string_by_punctuations, list(const.PUNCTUATIONS)


def read_inputs():
    """[(file_name, input_label, text)] — tracked fixtures first, then synthetics."""
    items = []
    for name in TRACKED_INPUTS:
        path = os.path.join(REPO, "trial", "fixtures", name)
        try:
            with open(path, encoding="utf-8") as handle:
                raw = handle.read()
        except OSError as exc:
            raise Usage("cannot read input %s: %s" % (path, exc))
        items.append((os.path.splitext(name)[0] + ".json",
                      "trial/fixtures/" + name,
                      unicodedata.normalize("NFC", raw)))
    for name, text in SYNTHETIC_INPUTS:
        items.append((name + ".json", "synthetic:" + name,
                      unicodedata.normalize("NFC", text)))
    return items


def build_documents(normalize, split, punctuations):
    documents = []
    for file_name, label, text in read_inputs():
        normalized = normalize(text)
        documents.append((file_name, {
            "input": label,
            "input_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
            "input_text": text,
            "normalized": normalized,
            "punctuations": punctuations,
            "chunks": split(normalized),
        }))
    return documents


def serialize(doc):
    return json.dumps(doc, ensure_ascii=False, indent=2) + "\n"


def write_all(directory, documents):
    os.makedirs(directory, exist_ok=True)
    for file_name, doc in documents:
        with open(os.path.join(directory, file_name), "w", encoding="utf-8") as handle:
            handle.write(serialize(doc))


def main(argv):
    parser = argparse.ArgumentParser(
        prog="gen_chunk_fixtures.py",
        description="write/re-check MPT's chunking output as drift fixtures (05.5.1-T1)")
    parser.add_argument("--mpt-root", default=None,
                        help="MPT checkout (default $NTS_HOME/src/mpt)")
    parser.add_argument("--out", default=None,
                        help="fixture directory (default worker-node/checks/fixtures/chunking)")
    parser.add_argument("--check", action="store_true",
                        help="regenerate into a temp dir and diff byte-for-byte (exit 1 on drift)")
    args = parser.parse_args(argv)

    mpt_root = os.path.abspath(args.mpt_root or mpt_root_default())
    out_dir = os.path.abspath(args.out or DEFAULT_OUT)
    try:
        normalize, split, punctuations = load_mpt(mpt_root)
        documents = build_documents(normalize, split, punctuations)
    except Usage as exc:
        print("gen_chunk_fixtures.py: %s" % exc, file=sys.stderr)
        return 2

    if not args.check:
        try:
            write_all(out_dir, documents)
        except OSError as exc:
            print("gen_chunk_fixtures.py: cannot write %s: %s" % (out_dir, exc), file=sys.stderr)
            return 2
        print("gen_chunk_fixtures.py: wrote %d fixtures to %s" % (len(documents), out_dir))
        return 0

    if not os.path.isdir(out_dir):
        print("gen_chunk_fixtures.py: no committed fixtures in %s" % out_dir, file=sys.stderr)
        return 1
    temp_dir = tempfile.mkdtemp(prefix="chunk-fixtures-")
    drift = []
    try:
        write_all(temp_dir, documents)
        fresh = sorted(os.listdir(temp_dir))
        committed = sorted(name for name in os.listdir(out_dir)
                           if name.endswith(".json"))
        for name in sorted(set(fresh) | set(committed)):
            if name not in committed:
                drift.append("missing committed fixture %s" % name)
                continue
            if name not in fresh:
                drift.append("stale committed fixture %s" % name)
                continue
            with open(os.path.join(temp_dir, name), "rb") as handle:
                want = handle.read()
            with open(os.path.join(out_dir, name), "rb") as handle:
                have = handle.read()
            if want != have:
                drift.append("%s differs" % name)
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)

    if drift:
        print("gen_chunk_fixtures.py: drift against %s: %s"
              % (out_dir, "; ".join(drift)), file=sys.stderr)
        return 1
    print("gen_chunk_fixtures.py: %d fixtures match %s (MPT %s)"
          % (len(documents), out_dir, os.path.basename(mpt_root)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
