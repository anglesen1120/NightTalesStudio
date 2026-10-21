#!/usr/bin/env python3
"""02.5.2-T0: fetch and freeze the 20-page extraction corpus.

stdlib only.  One HTTP GET per page (plus one robots.txt per host and, for
listing slots, one listing GET).  Writes:
  <out>/html/<id>.html      the raw bytes as received
  <out>/corpus.json         manifest (id, url, sha256, bytes, robot verdict, ...)
  <out>/corpus.sha256       `sha256sum -c` manifest over html/<id>.html

Usage: uv run --no-project --python 3.12 trial/tools/fetch_research_corpus.py [--out DIR]
Exit: 0 ok, 1 a slot stayed empty, 2 usage.

This is the spike harness, not the production fetcher (02.5.2-T1 implements the full D11 rule set).
Its fetch caps: per-page compressed and decompressed bytes <= MAX_BYTES, <= MAX_REDIRECTS
http/https redirect hops, a 25 s timeout, and a 1.1 s floor between requests to one host.  The
5 MiB snapshot budget is only warned about: the saved HTML is a regenerable, gitignored cache and
the durable evidence is corpus.json + corpus.sha256 (see the spike report's notes).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import urllib.robotparser
import zlib
from html.parser import HTMLParser

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_OUT = os.path.join(REPO, "trial", "research", "corpus")
UA = "NightTalesStudioBot/0.1.0 (+https://nighttales.example/bot)"
TIMEOUT = 25
HOST_GAP = 1.1  # seconds between two requests to the same host
MAX_BYTES = 3_000_000
TOTAL_BUDGET = 5 * 1024 * 1024
MAX_REDIRECTS = 3  # D11: follow at most 3 hops per fetch; a 4th hop fails the fetch

# id, lang, kind, candidate listing URLs, article link index to take, expected min text chars
SLOTS = [
    ("vi-news-01-vnexpress", "vi", "news", ["https://vnexpress.net/thoi-su"], 0, 800),
    ("vi-news-02-vnexpress", "vi", "news", ["https://vnexpress.net/the-gioi"], 0, 800),
    ("vi-news-03-tuoitre", "vi", "news", ["https://tuoitre.vn/thoi-su.htm", "https://tuoitre.vn/"], 0, 800),
    ("vi-news-04-tuoitre", "vi", "news", ["https://tuoitre.vn/kinh-doanh.htm", "https://tuoitre.vn/"], 0, 800),
    ("vi-news-05-thanhnien", "vi", "news", ["https://thanhnien.vn/thoi-su/", "https://thanhnien.vn/"], 0, 800),
    ("vi-news-06-thanhnien", "vi", "news", ["https://thanhnien.vn/the-gioi/", "https://thanhnien.vn/"], 0, 800),
    ("vi-news-07-dantri", "vi", "news", ["https://dantri.com.vn/xa-hoi.htm", "https://dantri.com.vn/"], 0, 800),
    ("vi-news-08-dantri", "vi", "news", ["https://dantri.com.vn/the-gioi.htm", "https://dantri.com.vn/"], 0, 800),
    ("vi-longform-09-wordpress", "vi", "longform", ["https://toidicodedao.com/", "https://toidicodedao.com/blog/"], 0, 800),
    ("vi-longform-10-inlinejs", "vi", "longform", ["https://kenh14.vn/", "https://vietnamnet.vn/"], 0, 800),
    ("en-news-11-bbc", "en", "news", ["https://www.bbc.com/news", "https://www.bbc.com/", "https://www.france24.com/en/", "https://www.dw.com/en/top-stories/s-9097"], 0, 1200),
    ("en-news-12-reuters", "en", "news", ["https://www.reuters.com/world/", "https://www.reuters.com/", "https://www.axios.com/", "https://www.cbsnews.com/"], 0, 1200),
    ("en-news-13-apnews", "en", "news", ["https://apnews.com/hub/world-news", "https://apnews.com/", "https://www.aljazeera.com/news/", "https://www.independent.co.uk/news/world"], 0, 1200),
    ("en-news-14-nytimes", "en", "news", ["https://www.nytimes.com/section/world", "https://abcnews.go.com/US", "https://www.cbc.ca/news"], 0, 1200),
    ("en-news-15-guardian", "en", "news", ["https://www.theguardian.com/international"], 0, 1200),
    ("en-news-16-npr", "en", "news", ["https://www.npr.org/sections/news/", "https://www.npr.org/", "https://text.npr.org/", "https://www.pbs.org/newshour/"], 0, 1200),
    ("wiki-17-en-hanoi", "en", "wiki", ["https://en.wikipedia.org/wiki/Hanoi"], -1, 1200),
    ("gov-18-usa", "en", "gov", ["https://www.usa.gov/"], -1, 800),
    ("spa-19-excalidraw", "en", "spa", ["https://excalidraw.com/"], -1, 0),
    ("paywall-20-nytimes", "en", "paywall", ["https://www.nytimes.com/", "https://www.economist.com/", "https://www.theatlantic.com/", "https://www.wsj.com/", "https://www.telegraph.co.uk/", "https://www.newyorker.com/"], 1, 400),
]

ARTICLE_HINT = re.compile(
    r"(/\d{4}/\d{2}/\d{2}/)|(/\d{4}/\d{2}/)|(-[0-9]{5,}\.html?$)|(-[0-9]{5,}\.chn$)|(/article[s]?/)|(/news/)|(/story/)",
    re.I,
)
TITLE_SUFFIX = re.compile(r"\s*[|\-–—]\s*[^|\-–—]{0,40}$")


class Anchors(HTMLParser):
    """Collect (href, text) and <title> text plus a flag for one <p> sample list."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.anchors: list[tuple[str, str]] = []
        self.title: str = ""
        self.paragraphs: list[str] = []
        self._href: str | None = None
        self._buf: list[str] = []
        self._in_title = False
        self._in_p = False
        self._skip = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag in ("script", "style", "noscript"):
            self._skip += 1
        if tag == "a":
            self._href = dict(attrs).get("href")
            self._buf = []
        elif tag == "title":
            self._in_title = True
        elif tag == "p":
            self._in_p = True
            self._buf = []

    def handle_endtag(self, tag: str) -> None:
        if tag in ("script", "style", "noscript"):
            self._skip = max(0, self._skip - 1)
        if tag == "a" and self._href is not None:
            self.anchors.append((self._href, " ".join("".join(self._buf).split())))
            self._href, self._buf = None, []
        elif tag == "title":
            self._in_title = False
        elif tag == "p":
            if self._in_p:
                text = " ".join("".join(self._buf).split())
                if text:
                    self.paragraphs.append(text)
            self._in_p = False

    def handle_data(self, data: str) -> None:
        if self._skip:
            return
        if self._in_title:
            self.title += data
        elif self._href is not None or self._in_p:
            self._buf.append(data)


class CappedRedirects(urllib.request.HTTPRedirectHandler):
    """D11 hop cap: at most MAX_REDIRECTS hops, and only into http/https.

    urllib's default handler follows 10 hops to any destination it is handed, so a hostile or
    compromised listing host could steer the fetch further than the URL vetted against robots.txt.
    """

    max_redirections = MAX_REDIRECTS

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[no-untyped-def]
        target = urllib.parse.urlsplit(newurl)
        if target.scheme not in ("http", "https") or not target.netloc:
            return None  # -> the 3xx surfaces as an HTTPError, so the fetch fails instead
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def gunzip_capped(compressed: bytes) -> bytes | None:
    """Inflate a gzip body, holding at most MAX_BYTES of output; None means "refuse the body".

    The read cap in fetch() bounds the *compressed* bytes only, so a small gzip stream that expands
    by orders of magnitude (decompression bomb) would still be materialised whole.  The cap is
    therefore applied to the decompressed output too, and a stream that exceeds it - or that is
    corrupt/truncated - is refused like any other transport failure.
    """
    try:
        obj = zlib.decompressobj(16 + zlib.MAX_WBITS)
        out = obj.decompress(compressed, MAX_BYTES + 1)
    except (OSError, zlib.error):
        return None
    if len(out) > MAX_BYTES or obj.unconsumed_tail:
        return None
    return out


def fetch(url: str, last: dict[str, float]) -> tuple[bytes, int, str, str]:
    host = urllib.parse.urlsplit(url).netloc.lower()
    gap = HOST_GAP - (time.monotonic() - last.get(host, 0.0))
    if gap > 0:
        time.sleep(gap)
    last[host] = time.monotonic()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), CappedRedirects)
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "text/html,*/*", "Accept-Encoding": "gzip, identity"})
    try:
        with opener.open(req, timeout=TIMEOUT) as resp:
            raw = resp.read(MAX_BYTES + 1)
            body = raw[:MAX_BYTES]
            if resp.headers.get("Content-Encoding", "").lower() == "gzip":
                inflated = gunzip_capped(body)
                if inflated is None:
                    return b"", 0, "", url
                body = inflated
            return body, resp.status, resp.headers.get("Content-Type", ""), resp.geturl()
    except urllib.error.HTTPError as exc:  # type: ignore[attr-defined]
        return b"", exc.code, exc.headers.get("Content-Type", "") if exc.headers else "", url
    except Exception:  # noqa: BLE001 - any transport failure is the same outcome here
        return b"", 0, "", url


def robots_verdict(url: str, cache: dict[str, urllib.robotparser.RobotFileParser | None], last: dict[str, float]) -> tuple[bool, str | None]:
    parts = urllib.parse.urlsplit(url)
    origin = f"{parts.scheme}://{parts.netloc}"
    if origin not in cache:
        body, status, _ctype, _final = fetch(origin + "/robots.txt", last)
        if status == 0 or status >= 500:
            # D11: timeout / connection failure / 5xx -> disallow the whole origin
            # (the fetch is refused as source_blocked_by_robots, not retried)
            parser = urllib.robotparser.RobotFileParser()
            parser.disallow_all = True
            cache[origin] = parser
        elif status >= 400 or not body:
            cache[origin] = None  # D11: 4xx (incl. 404) or empty file -> allow all
        else:
            parser = urllib.robotparser.RobotFileParser()
            parser.parse(body.decode("utf-8", "replace").splitlines())
            cache[origin] = parser
    parser = cache[origin]
    if parser is None:
        return True, None
    return parser.can_fetch(UA, url), str(parser.crawl_delay(UA) or parser.crawl_delay("*") or "") or None


def article_links(html: str, base: str) -> list[str]:
    parser = Anchors()
    try:
        parser.feed(html)
    except Exception:  # noqa: BLE001
        return []
    host = urllib.parse.urlsplit(base).netloc.lower()
    strict: list[str] = []
    relaxed: list[str] = []
    for href, text in parser.anchors:
        if not href or href.startswith(("#", "mailto:", "javascript:")):
            continue
        absolute = urllib.parse.urljoin(base, href)
        split = urllib.parse.urlsplit(absolute)
        if split.netloc.lower() != host or len(text) < 25:
            continue
        clean = urllib.parse.urlunsplit((split.scheme, split.netloc, split.path, split.query, ""))
        if ARTICLE_HINT.search(split.path):
            if clean not in strict:
                strict.append(clean)
        elif split.path.count("/") >= 2 and len(split.path) >= 25 and len(text) >= 40:
            if clean not in relaxed:
                relaxed.append(clean)
    return strict or relaxed


def expected_title(raw_title: str) -> str:
    title = " ".join(raw_title.split())
    trimmed = TITLE_SUFFIX.sub("", title).strip()
    return (trimmed or title)[:60]


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--only-missing", action="store_true",
                    help="keep pages already in corpus.json; fetch only empty slots")
    args = ap.parse_args(argv)
    failures = 0

    slot_ids = [slot[0] for slot in SLOTS]
    duplicate_ids = sorted({sid for sid in slot_ids if slot_ids.count(sid) > 1})
    if duplicate_ids:
        print(f"[fetch] refusing to run: duplicate slot id(s): {', '.join(duplicate_ids)}",
              file=sys.stderr)
        return 2

    out = os.path.abspath(args.out)
    html_dir = os.path.join(out, "html")
    os.makedirs(html_dir, exist_ok=True)

    prior: dict[str, dict] = {}
    prior_path = os.path.join(out, "corpus.json")
    if args.only_missing and os.path.isfile(prior_path):
        with open(prior_path, encoding="utf-8") as fh:
            prior = {p["id"]: p for p in json.load(fh)["pages"]}

    entries: list[dict] = []
    seen_urls: set[str] = {p["url"] for p in prior.values() if p.get("url")}
    last: dict[str, float] = {}
    robots: dict[str, urllib.robotparser.RobotFileParser | None] = {}
    for slot_id, lang, kind, listings, index, min_chars in SLOTS:
        if args.only_missing and slot_id in prior and prior[slot_id].get("htmlSha256"):
            entries.append(prior[slot_id])
            continue
        entry = {
            "id": slot_id, "lang": lang, "kind": kind, "url": None, "listingUrl": None,
            "htmlSha256": None, "bytes": None, "httpStatus": None, "contentType": None,
            "robotsAllowed": None, "crawlDelaySeconds": None, "expectedTitleContains": None,
            "expectedTextCharsMin": min_chars, "titleFromHtml": None, "notes": "",
            "paragraphCount": None,
        }
        notes: list[str] = []
        html: bytes = b""
        final_url = ""
        for listing in listings:
            allowed, delay = robots_verdict(listing, robots, last)
            entry["robotsAllowed"], entry["crawlDelaySeconds"] = allowed, delay
            if not allowed:
                notes.append(f"robots-disallowed:{listing}")
                continue
            body, status, ctype, final = fetch(listing, last)
            entry["httpStatus"] = status
            if status != 200 or not body:
                notes.append(f"listing-http-{status}:{listing}")
                continue
            entry["listingUrl"] = listing
            if index < 0:
                html, final_url = body, final
                break
            text = body.decode("utf-8", "replace")
            links = article_links(text, final)
            if not links:
                notes.append(f"no-article-link:{listing}")
                continue
            candidates = [l for l in links if l not in seen_urls]
            if not candidates:
                notes.append(f"all-links-duplicate:{listing}")
                continue
            target = candidates[index] if index < len(candidates) else candidates[-1]
            if index >= len(candidates):
                notes.append(f"index-{index}-missing-used-last")
            allowed_a, delay_a = robots_verdict(target, robots, last)
            if not allowed_a:
                notes.append(f"robots-disallowed:{target}")
                continue
            entry["crawlDelaySeconds"] = delay_a
            body_a, status_a, ctype_a, final_a = fetch(target, last)
            if status_a != 200 or not body_a:
                notes.append(f"article-http-{status_a}:{target}")
                continue
            html, final_url, ctype = body_a, final_a, ctype_a
            entry["httpStatus"] = status_a
            entry["url"] = target
            seen_urls.add(target)
            break
        else:
            failures += 1
            entry["notes"] = "; ".join(notes) or "no candidate worked"
            entries.append(entry)
            continue

        if entry["url"] is None:
            entry["url"] = final_url
        digest = hashlib.sha256(html).hexdigest()
        path = os.path.join(html_dir, f"{slot_id}.html")
        with open(path, "wb") as fh:
            fh.write(html)
        parser = Anchors()
        try:
            parser.feed(html.decode("utf-8", "replace"))
        except Exception:  # noqa: BLE001
            notes.append("html-parse-error")
        entry.update({
            "htmlSha256": digest,
            "bytes": len(html),
            "contentType": ctype or None,
            "titleFromHtml": " ".join(parser.title.split()) or None,
            "expectedTitleContains": expected_title(parser.title) or None,
            "paragraphCount": len([p for p in parser.paragraphs if len(p) >= 40]),
            "notes": "; ".join(notes),
        })
        entries.append(entry)
        print(f"[fetch] {slot_id}: {entry['httpStatus']} {len(html):>7} B  {entry['url']}", flush=True)

    total = sum(e["bytes"] or 0 for e in entries)
    manifest = {
        "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "userAgent": UA,
        "note": "02.5.2-T0 frozen corpus. expectedTitleContains is derived from the saved HTML <title> "
                "at fetch time; expectedTextCharsMin is the editorial floor from the task file.",
        "totalBytes": total,
        "pageCount": len([e for e in entries if e["htmlSha256"]]),
        "pages": entries,
    }
    with open(os.path.join(out, "corpus.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2, ensure_ascii=False, sort_keys=False)
        fh.write("\n")
    with open(os.path.join(out, "corpus.sha256"), "w", encoding="utf-8") as fh:
        for entry in entries:
            if entry["htmlSha256"]:
                fh.write(f"{entry['htmlSha256']}  html/{entry['id']}.html\n")

    print(f"[fetch] {manifest['pageCount']}/20 pages, {total} bytes, failures={failures}")
    if total > TOTAL_BUDGET:
        print(f"[fetch] WARNING total {total} B exceeds the 5 MiB snapshot budget", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
