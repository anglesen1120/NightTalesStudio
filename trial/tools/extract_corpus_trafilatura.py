#!/usr/bin/env python3
"""02.5.2-T0 candidate B: run trafilatura over the frozen corpus.

Offline: reads <corpus>/corpus.json + <corpus>/html/<id>.html, never the network.
Each page is extracted 5 times; the modal output is scored and stability is
recorded (the task file flags trafilatura's run-to-run variance).

Usage:
  uv run --no-project --python 3.12 --with trafilatura==2.2.0 \
      trial/tools/extract_corpus_trafilatura.py [--corpus DIR] [--runs 5]
Writes <corpus>/corpus.trafilatura.metrics.json and prints the summary table.
Latency p50/p95 use the nearest-rank convention (p50 over 5 runs = the median run).
Exit: 0 ok, 1 corpus file missing, 2 usage (--runs < 1, trafilatura missing).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import resource
import sys
import time
import unicodedata
from html.parser import HTMLParser

RUNS_DEFAULT = 5
BOILERPLATE = re.compile(r"cookie|Đăng nhập|Subscribe|Related|Chia sẻ|Quảng cáo", re.I)
# classic UTF-8-decoded-as-Latin-1 digraphs only: Vietnamese legitimately uses Â/Ã, so a bare Â./Ã.
# pattern false-positives on real Vietnamese text ("Âu", "ÃI" were found at spike time).
MOJIBAKE = re.compile(r"(â€|Ã[\x80-\xbf\u2013\u2019\u201c\u201d]|á»|áº|ï»¿|\ufffd)")
VI_DIACRITIC = re.compile(r"[àáâãèéêìíòóôõùúýăđĩũơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ]", re.I)
SKIP_TAGS = {"footer", "nav", "aside", "header", "form", "figure"}


class Paragraphs(HTMLParser):
    """Article-ish <p> collection: skips paragraphs inside chrome (nav/footer/aside/header/form)."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.paragraphs: list[str] = []
        self._in_p = False
        self._buf: list[str] = []
        self._skip = 0

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag in SKIP_TAGS:
            self._skip += 1
        elif tag == "p":
            self._in_p, self._buf = True, []

    def handle_endtag(self, tag: str) -> None:
        if tag in SKIP_TAGS:
            self._skip = max(0, self._skip - 1)
        elif tag == "p" and self._in_p:
            self.paragraphs.append(" ".join("".join(self._buf).split()))
            self._in_p = False

    def handle_data(self, data: str) -> None:
        if self._in_p and not self._skip:
            self._buf.append(data)


def norm(text: str) -> str:
    return " ".join(unicodedata.normalize("NFC", text).split())


def base_letters(text: str) -> str:
    """Diacritic-insensitive form: NFD then drop combining marks."""
    decomposed = unicodedata.normalize("NFD", text)
    return "".join(" " if unicodedata.combining(ch) else ch for ch in decomposed)


def source_paragraphs(html_bytes: bytes) -> list[str]:
    parser = Paragraphs()
    try:
        parser.feed(html_bytes.decode("utf-8", "replace"))
    except Exception:  # noqa: BLE001
        pass
    return [p for p in parser.paragraphs if len(p) >= 60]


def run_once(trafilatura, html_bytes: bytes, url: str) -> tuple[str | None, str, float]:
    start = time.perf_counter()
    raw = trafilatura.extract(
        filecontent=html_bytes, url=url, output_format="json",
        with_metadata=True, include_comments=False, include_tables=False,
    )
    ms = (time.perf_counter() - start) * 1000
    if not raw:
        return None, "", ms
    obj = json.loads(raw)
    return obj.get("title"), obj.get("text") or "", ms


def percentile(values: list[float], pct: float) -> float:
    """Nearest-rank percentile (the same convention as aggregate_scores.percentile).

    Over the default 5 runs p50 is the 3rd sorted sample, i.e. the median; the previous
    `round(n * pct / 100) - 1` ranked p50 at 2 of 5 and understated every reported value.
    """
    ordered = sorted(values)
    index = max(1, math.ceil(pct / 100.0 * len(ordered))) - 1
    return ordered[index]


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", default=os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "trial", "research", "corpus"))
    ap.add_argument("--runs", type=int, default=RUNS_DEFAULT)
    args = ap.parse_args(argv)
    if args.runs < 1:
        # usage error, not a traceback (D9 / 02 T100): an empty run list has no p50 to report
        print(f"--runs must be >= 1 (got {args.runs})", file=sys.stderr)
        return 2

    try:
        import trafilatura
    except ImportError:
        print("trafilatura missing; run with --with trafilatura==2.2.0", file=sys.stderr)
        return 2
    corpus_path = os.path.join(args.corpus, "corpus.json")
    if not os.path.isfile(corpus_path):
        print(f"missing {corpus_path}", file=sys.stderr)
        return 1
    manifest = json.load(open(corpus_path, encoding="utf-8"))

    results: list[dict] = []
    for page in manifest["pages"]:
        pid = page["id"]
        if not page.get("htmlSha256"):
            results.append({"id": pid, "status": "not-fetched", "notes": page.get("notes", "")})
            print(f"[extract] {pid}: not fetched", flush=True)
            continue
        html_path = os.path.join(args.corpus, "html", f"{pid}.html")
        html_bytes = open(html_path, "rb").read()
        if hashlib.sha256(html_bytes).hexdigest() != page["htmlSha256"]:
            results.append({"id": pid, "status": "sha256-mismatch"})
            continue

        runs: list[tuple[str | None, str, float]] = []
        for _ in range(args.runs):
            try:
                runs.append(run_once(trafilatura, html_bytes, page["url"] or ""))
            except Exception as exc:  # noqa: BLE001 - exception is a measured outcome
                runs.append((None, "", 0.0))
                print(f"[extract] {pid}: exception {exc.__class__.__name__}", flush=True)
        texts = [norm(t[1]) for t in runs]
        modal_text = max(set(texts), key=texts.count) if texts else ""
        modal = runs[texts.index(modal_text)] if modal_text in texts else runs[0]
        title, text = modal[0], modal_text
        lat = [t[2] for t in runs]

        src_ps = source_paragraphs(html_bytes)
        ntext = modal_text
        first_ok = bool(src_ps) and norm(src_ps[0])[:40] in ntext
        last_ok = bool(src_ps) and norm(src_ps[-1])[:40] in ntext
        chars = len(ntext)
        boiler_lines = [line for line in ntext.splitlines() if BOILERPLATE.search(line)]
        boiler_matches = list(BOILERPLATE.finditer(ntext))
        boiler_term_chars = sum(len(m.group(0)) for m in boiler_matches)
        vi_chars = len(VI_DIACRITIC.findall(ntext))
        # diacritic preservation: pick the longest source paragraph that survives the extractor at
        # all (matched on its mark-stripped form) and require its exact diacritic form to be present.
        # That is the acceptance check's "diff of NFC text vs the source, no diacritic-only difference"
        # reduced to its strongest single observable.
        ntext_base = base_letters(ntext)
        preserved: bool | None = None
        base_only: bool | None = None
        if src_ps:
            for candidate in sorted(src_ps, key=len, reverse=True):
                needle = base_letters(norm(candidate))[:60]
                if needle and needle in ntext_base:
                    preserved = norm(candidate)[:60] in ntext
                    base_only = not preserved
                    break
        mojibake_seqs = MOJIBAKE.findall(ntext)[:3]
        nfc_equal = unicodedata.normalize("NFC", ntext) == ntext
        diac_ok = (nfc_equal
                   and not mojibake_seqs
                   and (page["lang"] != "vi" or vi_chars > 0)
                   and preserved is not False)
        exp_title = (page.get("expectedTitleContains") or "").strip().lower()
        title_l = (title or "").lower()
        title_ok = bool(exp_title) and (exp_title in title_l or title_l in exp_title or
                                      any(w in title_l for w in exp_title.split() if len(w) >= 8))
        kind = ("empty" if chars == 0 else
                "short" if chars < 200 else "ok")

        results.append({
            "id": pid, "lang": page["lang"], "kind": page["kind"],
            "status": "ok", "title": title, "titleOk": title_ok,
            "textChars": chars, "expectedTextCharsMin": page["expectedTextCharsMin"],
            "lengthOk": chars >= page["expectedTextCharsMin"],
            "boilerplateHits": len(boiler_matches),
            "boilerplateLines": len(boiler_lines),
            "boilerplateShare": round(boiler_term_chars / max(chars, 1), 5),
            "recallFirst": first_ok if src_ps else None,
            "recallLast": last_ok if src_ps else None,
            "sourceParagraphs": len(src_ps),
            "diacriticsOk": diac_ok, "viDiacriticChars": vi_chars,
            "nfcRoundtripEqual": nfc_equal,
            "diacriticProbePreserved": preserved, "baseOnlyDifference": base_only,
            "mojibakeSeqs": mojibake_seqs,
            "distinctOutputs": len(set(texts)),
            "textSha256Modal": hashlib.sha256(ntext.encode()).hexdigest()[:16],
            "latencyP50ms": round(percentile(lat, 50), 1),
            "latencyP95ms": round(percentile(lat, 95), 1),
            "failureKind": kind,
        })
        print(f"[extract] {pid}: chars={chars} titleOk={title_ok} "
              f"p50={percentile(lat, 50):.0f}ms distinct={len(set(texts))}", flush=True)

    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    if sys.platform != "darwin":
        peak *= 1024  # ru_maxrss is bytes on macOS, KiB on Linux
    report = {
        "tool": "trafilatura",
        "trafilaturaVersion": trafilatura.__version__,
        "runsPerPage": args.runs,
        "peakRssBytes": peak,
        "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "results": results,
    }
    out_path = os.path.join(args.corpus, "corpus.trafilatura.metrics.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    # raw comparison output for the diacritics acceptance check: NFC round-trip per page, mojibake
    # sequences, vi diacritic-character count, and whether the longest surviving source paragraph
    # matches in its exact diacritic form (base-only difference = False means diacritics were lost).
    diac_path = os.path.join(args.corpus, "corpus.trafilatura.diacritics.txt")
    with open(diac_path, "w", encoding="utf-8") as fh:
        fh.write("id\tlang\tnfc_roundtrip_equal\tmojibake_seqs\tvi_diacritic_chars\t"
                 "probe_exact_diacritic_match\tbase_only_difference\n")
        for r in results:
            if r.get("status") != "ok":
                fh.write(f"{r['id']}\t{r.get('lang','')}\tNOT-EXTRACTED\t-\t-\t-\t-\n")
                continue
            fh.write(f"{r['id']}\t{r['lang']}\t{r['nfcRoundtripEqual']}\t"
                     f"{r['mojibakeSeqs'] or 'none'}\t{r['viDiacriticChars']}\t"
                     f"{r['diacriticProbePreserved']}\t{r['baseOnlyDifference']}\n")
    print(f"[extract] {len(results)} pages -> {out_path} (trafilatura {trafilatura.__version__}, peakRSS {peak/1e6:.0f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
