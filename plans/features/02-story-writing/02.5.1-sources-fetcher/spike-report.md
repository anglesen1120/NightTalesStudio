# 02.5.2-T0 spike report — extractor (SmartReader vs trafilatura) + robots.txt RFC 9309 subset

Date: 2026-09-26 · Author: PythonSpikes (subagent, M0 wave 1) · Task:
`plans/features/02-story-writing/02.5.1-sources-fetcher/02.5.2-T0-extraction-robots-library-spike.md`

Scope of this run: the **Python half only** — corpus, trafilatura measurements, the robots spec.
Candidate A (SmartReader, .NET) is `[OPEN]` pending a .NET SDK on the trial machine; every
SmartReader metric cell is `n/a:pending-SDK` and **no extractor is chosen yet** (see §1.5).

Corpus location (supersedes the task file's illustrative `…/spike/` path, per the wave-1
orchestrator instruction): `trial/research/corpus/` —
`corpus.json` (manifest), `corpus.sha256` (`sha256sum -c` manifest), `html/<id>.html` (20 pages; a
gitignored, regenerable cache — §3), `corpus.trafilatura.metrics.json` (per-page results). Helpers: `trial/tools/fetch_research_corpus.py`
(stdlib, robots-aware, 1 req/s per host) and `trial/tools/extract_corpus_trafilatura.py` (offline;
`uv run --no-project --python 3.12 --with trafilatura==2.2.0`).

## 1. Deliverable 1 — extractor comparison

### 1.1 Corpus

20 pages, 8 205 055 bytes of HTML, fetched 2026-09-26 (`corpus.json` `generatedAt`
`2026-09-26T12:42:48Z`), all 20 sha256-verified (`sha256sum -c corpus.sha256` → 20/20 OK). No two
pages share a URL. Composition as required: 8 Vietnamese news (2 each vnexpress.net / tuoitre.vn /
thanhnien.vn / dantri.com.vn), 2 Vietnamese long-form (1 WordPress: toidicodedao.com; 1 inline-JS-heavy:
kenh14.vn), 6 English news outlets, 1 Wikipedia article, 1 government page, 1 client-rendered SPA,
1 paywall-slot page.

Site swaps (recorded per page in `corpus.json.notes`; composition preserved, substitutions are
same-role outlets with a named checklist page):

| slot | intended | actually fetched | cause |
|---|---|---|---|
| `en-news-11-bbc` | bbc.com | france24.com | bbc.com unreachable to urllib (transport failure, both `/news` and `/`) |
| `en-news-12-reuters` | reuters.com | cbsnews.com | reuters.com robots.txt disallows our token; axios.com answered 403 |
| `en-news-13-apnews` | apnews.com | aljazeera.com (live blog) | apnews.com 403 to a simple client |
| `en-news-14-nytimes` | nytimes.com | cbc.ca | nytimes.com 403; abcnews.go.com served no discoverable article link |
| `en-news-16-npr` | npr.org | pbs.org/newshour | npr.org unreachable to urllib; text.npr.org robots-disallowed |
| `paywall-20-nytimes` | nytimes.com | newyorker.com | nytimes/economist/atlantic 403, wsj robots-disallowed, telegraph 402 |

`robots.txt` was consulted per host before fetching (`urllib.robotparser`, UA token
`NightTalesStudioBot`); disallowed targets were skipped and substituted. Two hosts set a non-null
`crawl-delay` — pbs.org `1` and usa.gov `10` (both `corpus.json` `crawlDelaySeconds`; the per-slot
field records the last-checked article origin — slot `en-news-16-npr` resolved to pbs.org — and no host
exceeded the T43 10 s ceiling). npr.org itself sets no `Crawl-delay`. Requests were spaced ≥ 1.1 s per
host (the spike tool paces requests but does not enforce `crawl-delay`; T43 is the T1 fetcher's rule, §2).

### 1.2 Per-page metrics

Metrics per the task file: `title` (extracted title contains `expectedTitleContains` — itself derived
from the saved HTML `<title>` at fetch time, so it proves "found the headline", not "headline is
right"), `chars` (whitespace-normalised text length vs `expectedTextCharsMin`), `boiler` (`hits` =
regex occurrences, `%` = matched-term chars / text chars — **not** whole-line chars: extractors emit
paragraph-per-line text, so a line-based share would report one matched paragraph as 100 %
leakage), `recall` (first/last source `<p>` (≥ 60 chars, chrome tags skipped) present in extracted
text), `diac` (NFC round-trip clean + no mojibake digraph + Vietnamese diacritics present for vi
pages + the longest surviving source paragraph matching in its exact diacritic form; raw output in
`corpus.trafilatura.diacritics.txt`), `p50`/`p95` ms over 5 runs (nearest-rank over the five samples,
so p50 **is** the median run — the earlier revision of the tool ranked p50 at 2 of 5 and understated
every value; the table below is the regenerated, corrected run), `stable` (distinct outputs over
5 runs), `fail` (empty / short / nav-only / partial kind).

| id | kind | SmartReader | trafilatura title | traf. chars | traf. boiler | traf. recall | traf. diac | traf. p50 ms | traf. p95 ms | traf. distinct/5 runs | traf. fail |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `vi-news-01-vnexpress` | news | n/a:pending-SDK | ✅ | 2029 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (446) | 7.3 | 11.5 | 1/5 | ok |
| `vi-news-02-vnexpress` | news | n/a:pending-SDK | ✅ | 5060 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (974) | 7.4 | 7.9 | 1/5 | ok |
| `vi-news-03-tuoitre` | news | n/a:pending-SDK | ✅ | 2506 (≥800) | 0 hits / 0.000% | ❌/❌ | ✅ (516) | 22.4 | 23.8 | 1/5 | ok |
| `vi-news-04-tuoitre` | news | n/a:pending-SDK | ✅ | 4181 (≥800) | 0 hits / 0.000% | ❌/❌ | ✅ (776) | 17.8 | 18.4 | 1/5 | ok |
| `vi-news-05-thanhnien` | news | n/a:pending-SDK | ✅ | 1463 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (296) | 16.2 | 16.3 | 1/5 | ok |
| `vi-news-06-thanhnien` | news | n/a:pending-SDK | ✅ | 3345 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (625) | 8.6 | 11.3 | 1/5 | ok |
| `vi-news-07-dantri` | news | n/a:pending-SDK | ✅ | 3386 (≥800) | 1 hits / 0.207% | ✅/✅ | ✅ (782) | 10.6 | 13.7 | 1/5 | ok |
| `vi-news-08-dantri` | news | n/a:pending-SDK | ✅ | 3001 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (630) | 11.2 | 11.4 | 1/5 | ok |
| `vi-longform-09-wordpress` | longform | n/a:pending-SDK | ✅ | 4544 (≥800) | 2 hits / 0.396% | ✅/❌ | ✅ (770) | 16.8 | 17.7 | 1/5 | ok |
| `vi-longform-10-inlinejs` | longform | n/a:pending-SDK | ✅ | 2720 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (543) | 14 | 15.1 | 1/5 | ok |
| `en-news-11-bbc` | news | n/a:pending-SDK | ✅ | 7114 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (13) | 24.5 | 87.8 | 1/5 | ok |
| `en-news-12-reuters` | news | n/a:pending-SDK | ✅ | 6018 (≥1200) | 0 hits / 0.000% | ✅/✅ | ✅ (1) | 11.6 | 13.4 | 1/5 | ok |
| `en-news-13-apnews` | news | n/a:pending-SDK | ✅ | 839 (<1200) | 0 hits / 0.000% | —/— | ✅ (0) | 7.1 | 7.6 | 1/5 | ok |
| `en-news-14-nytimes` | news | n/a:pending-SDK | ✅ | 6308 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (1) | 10.6 | 23.7 | 1/5 | ok |
| `en-news-15-guardian` | news | n/a:pending-SDK | ✅ | 8612 (≥1200) | 0 hits / 0.000% | ❌/✅ | ✅ (1) | 10.5 | 12.5 | 1/5 | ok |
| `en-news-16-npr` | news | n/a:pending-SDK | ✅ | 10748 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (0) | 9.8 | 11.3 | 1/5 | ok |
| `wiki-17-en-hanoi` | wiki | n/a:pending-SDK | ✅ | 62050 (≥1200) | 2 hits / 0.023% | ✅/✅ | ✅ (893) | 249.1 | 263.4 | 1/5 | ok |
| `gov-18-usa` | gov | n/a:pending-SDK | ✅ | 2840 (≥800) | 1 hits / 0.246% | ✅/✅ | ✅ (0) | 22.3 | 23 | 1/5 | ok |
| `spa-19-excalidraw` | spa | n/a:pending-SDK | ✅ | 57 (≥0) | 0 hits / 0.000% | —/— | ✅ (0) | 1.6 | 2.3 | 1/5 | short |
| `paywall-20-nytimes` | paywall | n/a:pending-SDK | ✅ | 14206 (≥400) | 1 hits / 0.049% | ✅/❌ | ✅ (0) | 13.2 | 14.1 | 1/5 | ok |

Whole-run peak RSS (process `ru_maxrss` after 20 pages × 5 runs): **172 MB**, as recorded in the
committed `corpus.trafilatura.metrics.json` (`peakRssBytes` 172 163 072, macOS bytes). ⚠️
run-dependent: re-runs measure 162–172 MB; only the committed value is the citable one. SmartReader
memory: `n/a:pending-SDK`.

Aggregates (20 pages): titles found **20/20**; above the editorial floor **19/20** — the miss is
`en-news-13-apnews` (839 chars vs its 1 200-char floor; the Al Jazeera live blog that replaced the
403-blocked apnews.com), **not** the SPA: `spa-19-excalidraw` has a 0-char floor by construction and
therefore passes (`lengthOk=True`, 57 chars) — it is the §1.3 `source_no_text` calibration case, not
a floor miss; Vietnamese diacritics intact **10/10 vi pages**; boilerplate leakage ≤ **0.396 %** of
extracted chars (max: the WordPress long-form, 2 hits); output **deterministic on every page**
(1 distinct output per 5 runs, so the task file's run-to-run-variance strike does not apply to
2.2.0 on this corpus); p50 **1.6–249.1 ms**, p95 max **263.4 ms** (committed run, latency is
run-dependent — the max is `wiki-17-en-hanoi`, 62 050 chars). Both figures are the corrected
nearest-rank statistics of §1.2 (p50 = median of the 5 runs); the pre-fix revision reported
2.2–280.0 ms and 304.2 ms for the same pages with p50 taken at rank 2 of 5.

### 1.3 SPA + paywall outcomes and the `source_no_text` threshold

| page | trafilatura outcome | reading |
|---|---|---|
| `spa-19-excalidraw` (client-rendered, 6 862 B of shell HTML) | returns **57 chars** of text (the page's static `<noscript>`/footer copy), no exception, title found | `TextChars` far below any article floor → maps to `source_no_text` with a ~200+ char threshold. This is the calibration case for the "nav-only extraction" side |
| `paywall-20-nytimes` | ⚠️ **not observed as a paywall**: every paywalled outlet tried (nytimes/economist/atlantic HTTP 403, wsj robots-disallowed, telegraph 402) blocked the simple client outright; the page actually fetched is `newyorker.com`, served whole (14 206 chars, first paragraph recalled) | a 403 to our client is a *blocked* fetch, not partial text. The "partial text" path of 02.5.2-T1 therefore has **no spike evidence** and stays a design assumption to verify in T1/T4 (mark it `[OPEN]`) |

**Recommended `source_no_text` threshold: 500 chars** (`Fetcher:MinTextChars`), provisional.
Derivation: the shortest page in the corpus that yielded genuine article text is
`en-news-13-apnews` (the Al Jazeera live blog) at **839 chars**; 60 % of 839 ≈ **503 → 500**.
The conservative alternative, using only full single-article pages, is the shortest Vietnamese news
page at **1 463 chars** → 60 % ≈ **878**; 878 would still classify the SPA (57 chars) correctly but
would also reject short real articles, so the 839-char calibration is the recommendation and the
878 figure is the fallback if T4 finds the 500 boundary too permissive. ⚠️ `[OPEN]`: this number is
derived from candidate B alone; freeze it after the SmartReader half runs so both candidates share
one calibration. The task file's starting guess was 200 (`plan.md` §10 [OPEN] 8); the measurements
here show the empty/nav-only extraction sits at ~57 chars, so 500 keeps a ~9× margin over the
observed noise floor while staying below the shortest genuine article.

Raw diacritics comparison (full file: `trial/research/corpus/corpus.trafilatura.diacritics.txt`;
`probe` = longest surviving source paragraph matched in its exact diacritic form, `base-only` = it
matched only after stripping diacritics, which would be a diacritic loss):

```text
id                        lang  nfc_roundtrip_equal  mojibake_seqs  vi_diacritic_chars  probe  base-only
vi-news-01-vnexpress      vi    True                 none           446                 True   False
vi-news-02-vnexpress      vi    True                 none           974                 True   False
vi-news-03-tuoitre        vi    True                 none           516                 True   False
vi-news-04-tuoitre        vi    True                 none           776                 True   False
vi-news-05-thanhnien      vi    True                 none           296                 True   False
vi-news-06-thanhnien      vi    True                 none           625                 True   False
vi-news-07-dantri         vi    True                 none           782                 True   False
vi-news-08-dantri         vi    True                 none           630                 True   False
vi-longform-09-wordpress  vi    True                 none           770                 True   False
vi-longform-10-inlinejs   vi    True                 none           543                 True   False
… 8 en pages, 1 wiki, 1 gov, 1 SPA, 1 paywall: all nfc_roundtrip_equal=True, mojibake_seqs=none
  (SPA and the Al Jazeera live blog: no probe paragraph ≥ 60 chars survived → probe=None)
```

Candidate A's half of this check is `n/a:pending-SDK`.

### 1.4 Candidates — licence/version/date verified at spike time

| candidate | version | licence | release date | check |
|---|---|---|---|---|
| SmartReader | 0.11.1 | Apache-2.0 | 2026-07-25 | `api.nuget.org` registration index, 2026-09-26 ✅ |
| AngleSharp (SmartReader dep) | 1.8.2 | MIT | 2026-09-18 | `api.nuget.org` registration index ✅ |
| trafilatura | 2.2.0 | Apache-2.0 | 2026-07-31 (and it is the latest on PyPI) | `pypi.org/pypi/trafilatura/2.2.0/json` ✅ |
| TurnerSoftware.RobotsExclusionTools (rejected) | 0.9.1 (latest; the NuGet version list stops here) | MIT | 2022-08-09 (E4; the NuGet registration index no longer exposes a catalog entry for it) | E4 + `api.nuget.org` flat-container version list ✅ |

### 1.5 Decision

`[OPEN]` — blocked on the .NET half. The task's rule is **"choose SmartReader unless it fails a
threshold"**, and SmartReader could not be measured on this machine (no .NET SDK), so no number stands
behind either side of that rule. Completing the decision requires only candidate A against the frozen
corpus — the manifest + saved HTML in `trial/research/corpus/` make that a pure offline run.

The rule's three thresholds, read against the measured candidate B (numbers printed, per acceptance):

| threshold in the rule | trafilatura 2.2.0, this corpus | candidate A |
|---|---|---|
| Vietnamese diacritics intact on ≥ 19/20 pages | **10/10 vi pages intact** (NFC round-trip equal, no mojibake digraph, exact-diacritic probe matched; raw output `corpus.trafilatura.diacritics.txt`) | n/a:pending-SDK |
| main-text recall on ≥ 18/20 pages (the task defines this as a **manual 5-point sample** of first and last article paragraph) | proxy: first source paragraph found in **15/20**, last source paragraph in **8/20** ⚠️ — the proxy's source-side "last `<p>`" is a footer/related block on most pages, and the 3 first-paragraph misses (`vi-news-03/04-tuoitre`, `en-news-15-guardian`) are pages whose lead `<p>` on the source side is byline/furniture. The proxy therefore measures the *proxy*, not the rule; the rule itself needs the manual sample and is recorded as `[OPEN]` | n/a:pending-SDK |
| boilerplate leakage < 10 % of extracted chars on ≥ 18/20 pages | **20/20 well under**: worst case 0.396 % (WordPress long-form, 2 regex hits); 15/20 pages have zero hits | n/a:pending-SDK |

Trafilatura's measured profile is in §1.2 — deterministic, fast, clean diacritics, negligible
boilerplate — but it is a **fallback, not a recommendation**: adopting it still requires the written
Python exception (F19; DECISIONS §10.13 keeps background work in C#) and the second-runtime costs
listed in the task file (second base image, localhost contract, health check, own CVE stream,
robots/SSRF rules inside a runtime we do not control). If the finished comparison puts both
candidates inside the thresholds, the task's tie-break gives the decision to SmartReader on
deployment cost.

`source_no_text` threshold recommendation: **500 chars** (provisional; derivation in §1.3 — the task
file's 200-char guess sits only ~3.5× above the measured 57-char nav-only floor, too thin a margin
for nav extraction that varies per page, while 500 sits between that floor and the shortest genuine
article at 839 chars). ⚠️ `[OPEN]`: re-derive once candidate A's numbers exist, so both candidates
share one calibration.

### 1.6 Proposed `DECISIONS.md` §3 text (for the TL to apply)

```text
- [DECISION, pending SDK] Main-text extractor: decision deferred pending the SmartReader
  (in-process .NET) half of spike 02.5.2-T0. trafilatura 2.2.0 (Apache-2.0, released 2026-07-31)
  measured over the frozen 20-page corpus (trial/research/corpus/, corpus.sha256): see
  plans/features/02-story-writing/02.5.1-sources-fetcher/spike-report.md §1. Default remains
  SmartReader 0.11.1 (Apache-2.0) unless it fails the recorded thresholds; a trafilatura sidecar
  would need the F19 Python exception in writing.
- [DECISION] robots.txt is honoured by an in-house RFC 9309 subset parser (spec: spike-report.md
  §2). TurnerSoftware.RobotsExclusionTools 0.9.1 is rejected: last release 2022-08-09, predates
  RFC 9309, unmaintained (E4).
```

## 2. Deliverable 2 — robots.txt RFC 9309 subset spec (normative)

This is the contract 02.5.2-T1 implements and 02.5.2-T4 asserts. `TurnerSoftware.RobotsExclusionTools`
0.9.1 (MIT) is **rejected**: last release 2022-08-09, predates RFC 9309 (E4) — the `Allow` directive
and longest-match rule we depend on postdate it.

| Aspect | Rule |
|---|---|
| Fetch | `GET https://{host}/robots.txt`, subject to **every** D11 rule (scheme, port, no IP literals, global-unicast check, `ConnectCallback` re-check, no proxy, no cookies, ≤ 3 redirects, decompressed cap, `text/plain` or `text/html`). Timeout 5 s (`Fetcher:RobotsTimeoutSeconds`). Its own size cap: 512 KiB decompressed; larger → treat as disallow |
| Cache | in-memory, keyed by `scheme://host:port` (a robots file is per-origin), TTL 24 h (`Fetcher:RobotsCacheHours`), LRU 1,000 origins; a `Disallow` verdict is cached too (a 5xx host must not be retried on every page) |
| Encoding | UTF-8; a BOM is stripped; invalid UTF-8 → decode with replacement and parse (never throw) |
| Line handling | split on `\n` (a trailing `\r` is stripped); ignore blank lines; `#` starts a comment (only outside a value, i.e. everything from the first `#` to end-of-line is dropped); a line without `:` is ignored; the field name is lowercased and trimmed, the value is trimmed of leading whitespace only (trailing spaces are significant in a path) |
| Groups | consecutive `user-agent` lines form one group; a new `user-agent` after a non-`user-agent` line starts a new group; `allow`/`disallow` lines attach to the **current** group; directives before any `user-agent` are ignored |
| Group selection | exact, case-insensitive match on the token `nighttalesstudiobot` (the UA token is the part before `/`) wins; otherwise the `*` group; if neither exists → allow all |
| Rule matching | path = the percent-decoded path + query of the request URI; a rule value is a path prefix; `*` matches any run of characters; a trailing `$` anchors the end; the **longest** matching rule value wins (counting the pattern's length, not the matched span); on equal length `allow` wins over `disallow`; an empty `disallow` value matches nothing (allow all); a rule value without a leading `/` is treated as if it had one |
| Directives ignored | `sitemap`, `host`, `request-rate`, `visit-time`, and every unknown field. **`crawl-delay` is honoured** [DECISION T43]: the per-host interval becomes `max(1 s, crawlDelay)`, capped at **10 s** — a `crawl-delay` above 10 s means the host wants less traffic than our own page budget allows, so the fetch is refused with `source_blocked_by_robots` rather than waiting. Config `Fetcher:CrawlDelayMaxSeconds = 10`. A malformed or negative `crawl-delay` is ignored (treated as absent, so the 1 s floor applies) |
| Limits | ≤ 1,000 rules per selected group; beyond that the extra rules are dropped and the file is treated as allow-all for the dropped part — a deliberately permissive degradation with a `robots_rule_limit_exceeded` log |
| Failure semantics (D11) | `2xx` → parse. `3xx` → follow ≤ 3 redirects (each re-checked) then parse; more → disallow. `4xx` (incl. 404) → **allow all**. `5xx`, timeout, connection failure, or unparsable-after-1 MiB of garbage → **disallow all** with `source_blocked_by_robots`. A robots fetch failure is **never** retried within the same task |
| UA token | `nighttalesstudiobot` — the lowercased first token of `Fetcher:UserAgent` (`NightTalesStudioBot/0.1.0 (+https://…/bot)`), derived at start-up, not hard-coded |

Worked examples (each is a T1 unit test oracle):

1. `User-agent: NightTalesStudioBot` group exists next to `User-agent: *` → our group wins (exact group beats wildcard).
2. `Disallow: /` + `Allow: /a` → `/a` allowed, `/b` denied.
3. `Disallow: /*.pdf$` → `/x.pdf` denied; `/x.pdf?y=1` allowed (the `$` anchor ends the match at `.pdf`; a query is part of the path expression, so the longest-match rule still applies — implementer note: match against `path + query`, `/x.pdf?y=1` does not end in `.pdf`).
4. `Disallow:` (empty) → allow all.
5. Two equal-length rules, one `Allow` and one `Disallow` → `allow` wins the tie.
6. A group for another bot (`User-agent: SomeOtherBot` with `Disallow: /`) does not apply → falls through to `*`, or allow-all if no `*` group.

Frozen port for T1 (the task file's contract, copied verbatim):

```csharp
// src/NightTales.Domain/Research/IArticleExtractor.cs   (Domain: no HTTP, no HTML dependency)
public interface IArticleExtractor
{
    ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct);
}
public sealed record ArticleExtraction(string? Title, string Text, int TextChars, bool IsMainTextFound);
```

`ArticleExtraction.Text` is whitespace-normalised NFC, no HTML, no markdown. `IsMainTextFound == false`
(or `TextChars < SourceNoTextThreshold`) → the caller maps to `source_no_text`.

## 3. Notes for the record

- Corpus fetched 2026-09-26 with UA `NightTalesStudioBot/0.1.0 (+https://nighttales.example/bot)`;
  `robots.txt` consulted per host and disallowed hosts were skipped or substituted (notes in
  `corpus.json`). Crawl-delay observed per origin is recorded in `corpus.json` (`crawlDelaySeconds`).
- Site substitutions are recorded per page in `corpus.json` (`notes`); reuters (robots-disallowed for
  our token), apnews/nytimes (HTTP 403 to a simple client) and npr/bbc (transport failure) were
  replaced by same-role outlets — the composition rule "2 vi per site, 6 en outlets, wiki, gov, SPA,
  paywall" is preserved.
- ✅ Snapshot size **and tracking** (decided in the wave-1 review round): the saved HTML totals
  **8 205 055 bytes (~7.8 MiB)**, over the task file's 5 MiB feature-folder budget (line 136:
  "store them under the feature folder only if total ≤ 5 MiB; otherwise keep the manifest + per-page
  sha256 and note that the snapshots were discarded"). The task file's own over-budget fallback is
  therefore what ships: the HTML snapshots are **not committed** — a root `.gitignore` rule ignores
  `trial/research/corpus/html/`, so the third-party article copies (NYT/New Yorker/Reuters-substitute/
  BBC/France24/Guardian/NPR-substitute/PBS/AFP pages plus a 1.1 MB Wikipedia dump) stay out of git
  history. What *is* committed is the durable evidence: `corpus.json` (manifest), `corpus.sha256`
  (per-page sha256, `sha256sum -c` verifiable) and the derived `corpus.trafilatura.*` files; the HTML
  is a local cache rebuilt with the §4 fetch command. Consequently the fetcher's `TOTAL_BUDGET`
  (5 MiB) stays a **warning** and the fetch keeps exit 0 on a 20/20 corpus — the budget guards a
  committed snapshot, and a hard failure would break the documented reproduction path for exactly the
  corpus we froze. The fetcher additionally caps what one source can do to the harness: redirects
  (≤ 3 hops, http/https targets only, per D11) and the **decompressed** body (the same `MAX_BYTES`,
  3 000 000 B, so a gzip bomb is refused rather than materialised) — verified offline against a
  localhost stub (gzip bomb refused, 4-hop chain refused, 3 hops followed, `file://` redirect
  refused, plain and gzip bodies unchanged). It remains the spike harness, not the production
  fetcher: D11's IP-literal / global-unicast / `ConnectCallback` re-check and the per-hop robots
  re-check are 02.5.2-T1's contract (`02.5.1-sources-fetcher/plan.md` D11 row,
  `02.5.2-T1-safe-http-source-fetcher.md`), and D11's "raw HTML never stored" is a further reason
  the snapshot is a local, uncommitted cache rather than a repository artefact.
- Manual 10-page live extraction check (plan §9): `[OPEN]` — the corpus table above covers the same
  pages; a separate live run adds nothing to the offline reproducibility the task requires.
- Cleanup: the task's throwaway `tools/extract-spike/` console was never created (Python half only);
  the two `trial/tools/` scripts are the reproducible harness and are kept.

## 4. Reproduction and verification

```sh
# fetch the corpus (robots-aware, ~1 req/s/host; this is the reproduction path for the gitignored
# html/ snapshots - they are rebuilt here, not committed; ≤ 3 redirect hops, per-page caps)
uv run --no-project --python 3.12 trial/tools/fetch_research_corpus.py            # → 20/20, exit 0
# verify the frozen corpus without network access (manifest + sha256 are the committed evidence)
cd trial/research/corpus && sha256sum -c corpus.sha256                            # → 20× ": OK", exit 0
# re-run candidate B (offline)
uv run --no-project --python 3.12 --with trafilatura==2.2.0 \
    trial/tools/extract_corpus_trafilatura.py                                     # → exit 0, 20 rows
# usage error, not a traceback (D9 / 02 T100): both tools exit 2 on bad input
uv run --no-project --python 3.12 --with trafilatura==2.2.0 \
    trial/tools/extract_corpus_trafilatura.py --runs 0                            # → "--runs must be >= 1", exit 2
```

Executed 2026-09-26 with the results quoted above: `sha256sum -c` → 20/20 OK; extraction → 20 rows,
`trafilatura 2.2.0`, deterministic (1 distinct output per page over 5 runs), peak RSS 172 MB
(committed run; ⚠️ run-dependent — re-runs measured 162–172 MB, see §1.2). The extraction was
re-run (`generatedAt` 2026-09-26T14:08:17Z) after the p50 statistic fix, so
`corpus.trafilatura.metrics.json` and the §1.2 table come from that run: every non-latency field
(text chars, titles, recall, boilerplate, diacritics, `textSha256Modal`, `distinctOutputs`) and
`corpus.trafilatura.diacritics.txt` are byte-identical to the pre-fix run — only `latencyP50ms`/
`latencyP95ms` (now the correct nearest-rank values) and `peakRssBytes` move.
`corpus.trafilatura.diacritics.txt` → every page `nfc_roundtrip_equal=True`, `mojibake_seqs=none`,
`base_only_difference=False` where a probe paragraph matched. Each page's `htmlSha256`,
`expectedTitleContains`, `expectedTextCharsMin` and every fetch-time robots verdict are recorded in
`corpus.json`. The fetcher's `robots_verdict` implements the D11 failure split exactly: 4xx (incl.
404) → allow, 5xx / timeout / connection failure → disallow the origin (`source_blocked_by_robots`).

Metric → column map (the task's eight metrics): title → `trafilatura title`; text length →
`traf. chars`; boilerplate leakage → `traf. boiler`; main-text recall → `traf. recall` (proxy, see
§1.5); Vietnamese diacritics → `traf. diac`; latency → `traf. p50/p95`; memory → whole-run peak RSS
in §1.2; failure behaviour → `traf. fail` + §1.3. Every SmartReader cell is `n/a:pending-SDK`.
