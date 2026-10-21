# 02.5.2-T0 spike report — extractor (SmartReader vs trafilatura) + robots.txt RFC 9309 subset

Date: 2026-09-26, completed 2026-09-28 (candidate A half) · Authors: PythonSpikes + SmartReaderSpike
(subagents, M0 wave 1 / owner-decisions-0928) · Task:
`plans/features/02-story-writing/02.5.1-sources-fetcher/02.5.2-T0-extraction-robots-library-spike.md`

Both halves measured: corpus + trafilatura (2026-09-26) and SmartReader (2026-09-28, .NET SDK
10.0.401). **Decision: SmartReader 0.11.1** — §1.5.

Corpus location (supersedes the task file's illustrative `…/spike/` path, per the wave-1
orchestrator instruction): `trial/research/corpus/` —
`corpus.json` (manifest), `corpus.sha256` (`sha256sum -c` manifest), `html/<id>.html` (20 pages; a
gitignored, regenerable cache — §3), `corpus.trafilatura.metrics.json` /
`corpus.trafilatura.diacritics.txt` and `corpus.smartreader.metrics.json` /
`corpus.smartreader.diacritics.txt` (per-page results, same shape). Helpers:
`trial/tools/fetch_research_corpus.py` (stdlib, robots-aware, 1 req/s per host),
`trial/tools/extract_corpus_trafilatura.py` (offline; `uv run --no-project --python 3.12 --with
trafilatura==2.2.0`), and the throwaway `tools/extract-spike/` console (deleted after the run, §3 —
a line-for-line C# port of the Python scorer: same regexes, same ≥60-char paragraph collector
(AngleSharp `QuerySelectorAll("p")`, same chrome-tag skip set), same nearest-rank percentiles,
`new SmartReader.Reader(url, html).GetArticle()` ×5 per page offline; probe-set identical on all
20 pages — `sourceParagraphs` counts match `corpus.trafilatura.metrics.json` exactly).

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

| id | kind | trafilatura title | traf. chars | traf. boiler | traf. recall | traf. diac | traf. p50 ms | traf. p95 ms | traf. distinct/5 runs | traf. fail |
|---|---|---|---|---|---|---|---|---|---|---|
| `vi-news-01-vnexpress` | news | ✅ | 2029 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (446) | 7.3 | 11.5 | 1/5 | ok |
| `vi-news-02-vnexpress` | news | ✅ | 5060 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (974) | 7.4 | 7.9 | 1/5 | ok |
| `vi-news-03-tuoitre` | news | ✅ | 2506 (≥800) | 0 hits / 0.000% | ❌/❌ | ✅ (516) | 22.4 | 23.8 | 1/5 | ok |
| `vi-news-04-tuoitre` | news | ✅ | 4181 (≥800) | 0 hits / 0.000% | ❌/❌ | ✅ (776) | 17.8 | 18.4 | 1/5 | ok |
| `vi-news-05-thanhnien` | news | ✅ | 1463 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (296) | 16.2 | 16.3 | 1/5 | ok |
| `vi-news-06-thanhnien` | news | ✅ | 3345 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (625) | 8.6 | 11.3 | 1/5 | ok |
| `vi-news-07-dantri` | news | ✅ | 3386 (≥800) | 1 hits / 0.207% | ✅/✅ | ✅ (782) | 10.6 | 13.7 | 1/5 | ok |
| `vi-news-08-dantri` | news | ✅ | 3001 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (630) | 11.2 | 11.4 | 1/5 | ok |
| `vi-longform-09-wordpress` | longform | ✅ | 4544 (≥800) | 2 hits / 0.396% | ✅/❌ | ✅ (770) | 16.8 | 17.7 | 1/5 | ok |
| `vi-longform-10-inlinejs` | longform | ✅ | 2720 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (543) | 14 | 15.1 | 1/5 | ok |
| `en-news-11-bbc` | news | ✅ | 7114 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (13) | 24.5 | 87.8 | 1/5 | ok |
| `en-news-12-reuters` | news | ✅ | 6018 (≥1200) | 0 hits / 0.000% | ✅/✅ | ✅ (1) | 11.6 | 13.4 | 1/5 | ok |
| `en-news-13-apnews` | news | ✅ | 839 (<1200) | 0 hits / 0.000% | —/— | ✅ (0) | 7.1 | 7.6 | 1/5 | ok |
| `en-news-14-nytimes` | news | ✅ | 6308 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (1) | 10.6 | 23.7 | 1/5 | ok |
| `en-news-15-guardian` | news | ✅ | 8612 (≥1200) | 0 hits / 0.000% | ❌/✅ | ✅ (1) | 10.5 | 12.5 | 1/5 | ok |
| `en-news-16-npr` | news | ✅ | 10748 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (0) | 9.8 | 11.3 | 1/5 | ok |
| `wiki-17-en-hanoi` | wiki | ✅ | 62050 (≥1200) | 2 hits / 0.023% | ✅/✅ | ✅ (893) | 249.1 | 263.4 | 1/5 | ok |
| `gov-18-usa` | gov | ✅ | 2840 (≥800) | 1 hits / 0.246% | ✅/✅ | ✅ (0) | 22.3 | 23 | 1/5 | ok |
| `spa-19-excalidraw` | spa | ✅ | 57 (≥0) | 0 hits / 0.000% | —/— | ✅ (0) | 1.6 | 2.3 | 1/5 | short |
| `paywall-20-nytimes` | paywall | ✅ | 14206 (≥400) | 1 hits / 0.049% | ✅/❌ | ✅ (0) | 13.2 | 14.1 | 1/5 | ok |

Whole-run peak RSS (process `ru_maxrss` after 20 pages × 5 runs): **172 MB**, as recorded in the
committed `corpus.trafilatura.metrics.json` (`peakRssBytes` 172 163 072, macOS bytes). ⚠️
run-dependent: re-runs measure 162–172 MB; only the committed value is the citable one.

Trafilatura aggregates (20 pages): titles found **20/20**; above the editorial floor **19/20** — the miss is
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

SmartReader 0.11.1 over the same frozen corpus, same scorer (the §1.2 method notes apply verbatim —
the C# harness is a port of the Python one; `sr.` columns are the same eight metrics; `readable` is
SmartReader's own `IsReadable` verdict, recorded because 02.5.2-T1 maps it to `IsMainTextFound`):

| id | kind | sr. title | sr. chars | sr. boiler | sr. recall | sr. diac | sr. p50 ms | sr. p95 ms | sr. distinct/5 runs | sr. readable | sr. fail |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `vi-news-01-vnexpress` | news | ✅ | 2125 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (465) | 22.6 | 140.4 | 1/5 | true | ok |
| `vi-news-02-vnexpress` | news | ✅ | 5496 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (1018) | 23.8 | 25.8 | 1/5 | true | ok |
| `vi-news-03-tuoitre` | news | ✅ | 3367 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (684) | 84.7 | 90.5 | 1/5 | true | ok |
| `vi-news-04-tuoitre` | news | ✅ | 4554 (≥800) | 0 hits / 0.000% | ❌/❌ | ✅ (850) | 37.9 | 38.2 | 1/5 | true | ok |
| `vi-news-05-thanhnien` | news | ✅ | 2852 (≥800) | 0 hits / 0.000% | ✅/❌ | ✅ (570) | 19.0 | 30.0 | 1/5 | true | ok |
| `vi-news-06-thanhnien` | news | ✅ | 3636 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (673) | 12.7 | 13.9 | 1/5 | true | ok |
| `vi-news-07-dantri` | news | ✅ | 3399 (≥800) | 1 hits / 0.206% | ✅/✅ | ✅ (787) | 12.5 | 12.8 | 1/5 | true | ok |
| `vi-news-08-dantri` | news | ✅ | 2793 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (590) | 12.8 | 13.3 | 1/5 | true | ok |
| `vi-longform-09-wordpress` | longform | ✅ | 7299 (≥800) | 2 hits / 0.247% | ✅/❌ | ✅ (1288) | 25.7 | 27.9 | 1/5 | true | ok |
| `vi-longform-10-inlinejs` | longform | ✅ | 2610 (≥800) | 0 hits / 0.000% | ✅/✅ | ✅ (526) | 22.4 | 37.3 | 1/5 | true | ok |
| `en-news-11-bbc` | news | ✅ | 6903 (≥1200) | 0 hits / 0.000% | ❌/❌ | ✅ (11) | 24.9 | 40.4 | 1/5 | true | ok |
| `en-news-12-reuters` | news | ✅ | 5962 (≥1200) | 0 hits / 0.000% | ✅/✅ | ✅ (1) | 24.6 | 25.6 | 1/5 | true | ok |
| `en-news-13-apnews` | news | ✅ | 1226 (≥1200) | 0 hits / 0.000% ⚠️ | —/— | ✅ (0) | 22.7 | 23.5 | 1/5 | false | nav-dominant |
| `en-news-14-nytimes` | news | ✅ | 8286 (≥1200) | 0 hits / 0.000% | ✅/✅ | ✅ (5) | 12.6 | 14.5 | 1/5 | true | ok |
| `en-news-15-guardian` | news | ✅ | 9064 (≥1200) | 0 hits / 0.000% | ❌/✅ | ✅ (1) | 15.9 | 16.1 | 1/5 | true | ok |
| `en-news-16-npr` | news | ✅ | 10858 (≥1200) | 0 hits / 0.000% | ✅/❌ | ✅ (0) | 23.6 | 42.9 | 1/5 | true | ok |
| `wiki-17-en-hanoi` | wiki | ✅ | 70081 (≥1200) | 1 hits / 0.010% | ❌/✅ | ✅ (957) | 398.9 | 526.6 | 1/5 | true | ok |
| `gov-18-usa` | gov | ✅ | 252 (<800) | 0 hits / 0.000% | ✅/❌ | ✅ (0) | 24.5 | 40.4 | 1/5 | false | under-extracted |
| `spa-19-excalidraw` | spa | ✅ | 0 (≥0) | 0 hits / 0.000% | —/— | ✅ (0) | 0.3 | 0.4 | 1/5 | false | empty |
| `paywall-20-nytimes` | paywall | ✅ | 14622 (≥400) | 1 hits / 0.048% | ✅/❌ | ✅ (0) | 5.9 | 6.3 | 1/5 | true | ok |

SmartReader aggregates (20 pages): titles found **20/20**; above the editorial floor **19/20** — the
miss is `gov-18-usa` (252 chars of link-directory text vs its 800-char floor, `IsReadable=false` —
a real under-extraction, §1.5), not the SPA; Vietnamese diacritics intact **10/10 vi pages** (20/20
clean overall); boilerplate leakage ≤ **0.247 %** of extracted chars by the keyword regex (max:
the WordPress long-form, 2 hits; 17/20 zero hits) — ⚠️ keyword-regex blind spots on `en-news-13`
(~34 % of its 1 226 chars is the Al Jazeera nav banner "Skip links…Sponsored Content" that no
listed term matches) and `wiki-17` (infobox furniture kept in `TextContent`); both are
*structural* dirt, invisible to the regex — recorded under failure behaviour, not quality;
**deterministic on every page** (1 distinct output per 5 runs); p50 **0.3–398.9 ms**, p95 max
**526.6 ms** — both on `wiki-17-en-hanoi` (70 081 chars extracted). Cold-JIT shows in the first
row's p95 (140.4 ms vs a 22.6 ms median); per-page p50 is the meaningful number. Whole-run peak RSS
(`ru_maxrss` via `getrusage`, the same metric the Python harness records):
**163 MB** (`peakRssBytes` 163 201 024, macOS bytes, committed `corpus.smartreader.metrics.json`)
vs trafilatura's 172 MB — same machine, same metric, run-dependent for both.

### 1.3 SPA + paywall outcomes and the `source_no_text` threshold

| page | trafilatura outcome | SmartReader outcome | reading |
|---|---|---|---|
| `spa-19-excalidraw` (client-rendered, 6 862 B of shell HTML) | returns **57 chars** of text (the page's static `<noscript>`/footer copy), no exception, title found | returns **0 chars**, `IsReadable=false`, no exception, title found | both far below any article floor → `source_no_text`; SmartReader is even cleaner (empty, not nav text). The "empty/nav-only" calibration case |
| `en-news-13-apnews` (Al Jazeera live blog) | 839 chars of live-blog text | **1 226 chars, ~34 % of it nav banner** ("Skip links…Sponsored Content" — a keyword-regex blind spot), `IsReadable=false` | the third failure shape: nav-dominant but non-empty text. Under the chosen extractor `IsReadable=false` already maps it to `source_no_text` — and the calibrated threshold flags it by length too (see below) |
| `gov-18-usa` (directory page) | 2 840 chars | **252 chars** of header/link-directory text, `IsReadable=false` | candidate-A-only under-extraction; `IsReadable=false` + below-threshold chars → `source_no_text`, arguably the desired verdict for a non-article page |
| `paywall-20-nytimes` | ⚠️ **not observed as a paywall**: every paywalled outlet tried (nytimes/economist/atlantic HTTP 403, wsj robots-disallowed, telegraph 402) blocked the simple client outright; the page actually fetched is `newyorker.com`, served whole (14 206 chars, first paragraph recalled) | same page: **14 622 chars** extracted, `IsReadable=true`, body end-to-end | a 403 to our client is a *blocked* fetch, not partial text. The "partial text" path of 02.5.2-T1 therefore has **no spike evidence** and stays a design assumption to verify in T1/T4 (mark it `[OPEN]`) |

**Recommended `source_no_text` threshold: 1 275 chars** (`Fetcher:MinTextChars`) — frozen with both
candidates measured, derived per the task's rule from the **chosen** extractor. Candidate list for
the calibration (SmartReader textChars, ascending): `spa` 0 · `gov-18` 252 · `en-news-13` 1 226
(nav-dominant, `IsReadable=false`) · `vi-news-01` **2 125 — the shortest clean genuine article** ·
`vi-longform-10` 2 610 … 60 % of 2 125 = **1 275**. At 1 275 every genuine article passes under
SmartReader (min clean article 2 125) and all three observed failure shapes route to
`source_no_text` by length alone — the SPA (0), the under-extracted directory page (252) and the
nav-dominant live blog (1 226). Under trafilatura the same 1 275 would additionally reject its
`en-news-13` output (839 chars of mostly live-blog chrome) — arguably correct — and its shortest
clean article is `vi-news-05` at 1 463, which still passes. The earlier 500 proposal (60 % of
trafilatura's 839-char `en-13`) does not survive candidate A's data: it would let SmartReader's
1 226-char nav dump through while sitting barely above the noise floor relative to real articles.
The task file's starting guess was 200 (`plan.md` §10 [OPEN] 8); noise-floor extractions measure
0–57 chars, so even 500 was ~9× over the floor — but the purpose of the threshold is to reject
*nav-only or truncated* extractions, and 1 275 does that for every failure shape both candidates
produced. `IsReadable=false` remains the primary `IsMainTextFound` signal; the threshold is the
second gate.

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

SmartReader's half of the same check (raw file `trial/research/corpus/corpus.smartreader.diacritics.txt`,
same columns, same probe method — probe-paragraph sets are identical to trafilatura's on all 20
pages): every page `nfc_roundtrip_equal=True`, `mojibake_seqs=none`; all 10 vi pages
`probe_exact_diacritic_match=True`, `base_only_difference=False`, diacritic char counts 465–1 288
per page (broadly comparable to trafilatura's — a few % lower on `vi-news-08` and
`vi-longform-10`, higher on the other 8). The two `probe=None` rows are the same no-probe cases
(`spa-19-excalidraw`, `en-news-13-apnews`).

### 1.4 Candidates — licence/version/date verified at spike time

| candidate | version | licence | release date | check |
|---|---|---|---|---|
| SmartReader | 0.11.1 | Apache-2.0 | 2026-07-25 | `api.nuget.org` registration index, 2026-09-26 ✅ |
| AngleSharp (SmartReader dep) | 1.8.2 | MIT | 2026-09-18 | `api.nuget.org` registration index ✅ |
| trafilatura | 2.2.0 | Apache-2.0 | 2026-07-31 (and it is the latest on PyPI) | `pypi.org/pypi/trafilatura/2.2.0/json` ✅ |
| TurnerSoftware.RobotsExclusionTools (rejected) | 0.9.1 (latest; the NuGet version list stops here) | MIT | 2022-08-09 (E4; the NuGet registration index no longer exposes a catalog entry for it) | E4 + `api.nuget.org` flat-container version list ✅ |

### 1.5 Decision

**SmartReader 0.11.1 (in-process .NET)** — the task's default rule applies: SmartReader passed all
three thresholds, so the trafilatura-sidecar branch and the written Python exception are not needed.
Numbers, per the acceptance requirement:

| threshold in the rule | SmartReader 0.11.1, this corpus | trafilatura 2.2.0, this corpus |
|---|---|---|
| Vietnamese diacritics intact on ≥ 19/20 pages | **20/20** (10/10 vi: NFC round-trip equal, no mojibake digraph, exact-diacritic probe matched, `base_only=False` everywhere; `corpus.smartreader.diacritics.txt`) | **10/10 vi pages intact** (same checks; `corpus.trafilatura.diacritics.txt`) |
| main-text recall on ≥ 18/20 pages | **19/20 after manual audit of every proxy miss** (proxy: first `<p>` 14/18, last `<p>` 8/18 — see the recall note below). Sole genuine failure: `gov-18-usa` (252 chars of link-directory text vs trafilatura's 2 840; a non-article page already flagged `IsReadable=false` → `source_no_text`) | proxy: first **15/20**, last **8/20** ⚠️ — same artefact class; misses are furniture/byline `<p>`s |
| boilerplate leakage < 10 % of extracted chars on ≥ 18/20 pages | **20/20 well under** by the keyword regex: worst case 0.247 % (WordPress long-form). ⚠️ two regex blind spots by structure: `en-news-13` (~34 % nav banner) and `wiki-17` (infobox in `TextContent`) — disclosed in §1.2, both caught by `IsReadable`/threshold downstream | **20/20 well under**: worst case 0.396 % |

Recall note (the task's "manual 5-point sample" made auditable): all 14 proxy-miss events (4 first,
10 last) across 12 pages were traced to their source `<p>` in the saved HTML and classified —
**10 furniture** (VnExpress verification-box caption `class="note-img-text"`, Tuổi Trẻ
subscription promo, thanhnien copyright line, WordPress reader comment, France24 YouTube-consent
and error `<p>`s, PBS/New Yorker newsletter prompts), **2 standfirsts** (`vi-news-04-tuoitre`,
`en-news-15-guardian`), **1 CSS-polluted source `<p>`** (`wiki-17-en-hanoi` — SmartReader *did*
extract the real lead "Hanoi … is the capital", only the inline-style junk differs), **1 real
failure** (`gov-18-usa`'s closing content paragraph absent with the rest of the body). A
classifier cross-check over the same texts agreed on 9/11 rows; its two article_body calls were
manually overruled by the source context (`class="note-img-text"` box; subscription promo —
[INFERENCE]-quality disagreements, not used as evidence). Genuine recall: **19/20 ≥ 18/20**.

Per-metric deltas worth recording (all inside the thresholds):
- `gov-18-usa`: under-extraction 2 840 → 252 chars + `IsReadable=false` — candidate-A only.
- `en-news-13-apnews`: SmartReader returns 1 226 chars with `IsReadable=false`; ~34 % is nav
  banner the keyword regex can't see — the threshold (1 275) flags it by length too.
- `wiki-17-en-hanoi`: SmartReader keeps the infobox in `TextContent` (70 081 chars) — dirtier
  than trafilatura's 62 050 but recall-safe (the real lead is present verbatim).
- `IsReadable=false` pages can still return non-empty text (`en-13` 1 226, `gov-18` 252) →
  T1's adapter MUST gate `IsMainTextFound` on `IsReadable` **and** `TextChars ≥ threshold`,
  never on non-empty text alone. This is contract evidence for `IArticleExtractor`.
- Latency: comparable per-page p50s (SmartReader 0.3–398.9 ms, trafilatura 1.6–249.1 ms); both
  peak on the Wikipedia page (p95 526.6 ms vs 263.4 ms — immaterial at fetch cadence).
- Memory: whole-run `ru_maxrss` 163 MB (SmartReader, .NET runtime included) vs 172 MB (trafilatura).

Deployment (D10, the deciding factor): SmartReader is a netstandard2.0/2.1 library — zero processes,
zero runtimes, zero IPC inside a container whose contract is "no DB, no storage, no OpenAI, minimal
surface". trafilatura would need the written Python exception (F19; DECISIONS §10.13 keeps
background work in C#), a second base image, a localhost HTTP contract, its own health check and
CVE stream, and the robots/SSRF rules enforced inside a runtime we do not control. Not chosen.

`source_no_text` threshold: **1 275 chars** (`Fetcher:MinTextChars`), frozen — derivation in §1.3
(60 % of the 2 125-char shortest clean article under the chosen extractor; every observed failure
shape — 0 / 252 / 1 226 chars — lands below it).

T1 hand-off per the task file: `IArticleExtractor` (Domain), the §2 robots spec, threshold 1 275.

### 1.6 Proposed `DECISIONS.md` §3 text (for the TL to apply)

```text
- [DECISION] Main-text extractor: SmartReader 0.11.1 (Apache-2.0, released 2026-07-25; depends on
  AngleSharp 1.8.2 / MIT / 2026-09-18), in-process — chosen by spike 02.5.2-T0 over trafilatura
  2.2.0 (Apache-2.0, 2026-07-31) on the frozen 20-page corpus (trial/research/corpus/,
  corpus.sha256; evidence:
  plans/features/02-story-writing/02.5.1-sources-fetcher/spike-report.md §1). Thresholds:
  Vietnamese diacritics intact 20/20 (10/10 vi), genuine article recall 19/20 (sole miss
  gov-18-usa, a non-article directory page SmartReader flags IsReadable=false → source_no_text
  anyway), boilerplate leakage ≤ 0.247 % of extracted chars on all 20 pages; deterministic,
  p50 ≤ 399 ms, peak RSS 163 MB. trafilatura passed the same thresholds but loses on deployment
  cost: a Python sidecar would need the F19 exception and adds a second runtime, health check and
  CVE stream to a minimal-surface container (D10). Known limitations recorded: gov-18
  under-extraction (252 vs 2 840 chars), en-13 nav-banner leak (IsReadable=false catches it),
  wiki-17 infobox kept in TextContent, and IsReadable=false pages may still return text — T1
  gates IsMainTextFound on IsReadable AND TextChars ≥ threshold.
- [DECISION] robots.txt is honoured by an in-house RFC 9309 subset parser (spec: spike-report.md
  §2). TurnerSoftware.RobotsExclusionTools 0.9.1 is rejected: last release 2022-08-09, predates
  RFC 9309, unmaintained (E4).
- [DECISION] `source_no_text` threshold (`Fetcher:MinTextChars`) = 1 275 chars — 60 % of the
  2 125-char shortest clean article under SmartReader; every observed failure shape (0 / 252 /
  1 226 chars: SPA, directory page, nav-dominant live blog) lands below it.
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
- Cleanup: the task's throwaway `tools/extract-spike/` console (candidate A harness) was created,
  run and deleted per Step 7 — the durable evidence is `corpus.smartreader.metrics.json`,
  `corpus.smartreader.diacritics.txt` and the §1.2 table. The harness was a ~230-line net10.0
  top-level console: read `corpus.json`, sha256-check `html/<id>.html`, then per page
  `new SmartReader.Reader(url, html).GetArticle()` ×5 with `Stopwatch` timing and the identical
  scoring functions ported to C# (`Norm` = NFC + whitespace-collapse, `BaseLetters` = NFD minus
  combining marks, AngleSharp `QuerySelectorAll("p")` with the same chrome-tag skip set — probe
  sets verified identical to the Python collector's on all 20 pages, the same
  BOILERPLATE/MOJIBAKE/VI_DIACRITIC regexes, nearest-rank p50/p95, `ru_maxrss` via `getrusage`
  P/Invoke — `Process.PeakWorkingSet64` reports 0 on macOS .NET 10, so the P/Invoke mirrors the
  Python metric exactly; the JSON records it as `peakRssMethod`). The two `trial/tools/` scripts
  stay as the reproducible fetch/score harness.
  `grep -rn SmartReader src/` → 0 hits (no `src/` exists; the spike folder was the only reference
  and it is deleted).

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
# candidate A ran in the throwaway tools/extract-spike/ net10.0 console (deleted per Step 7):
#   cd tools/extract-spike && dotnet run -- --corpus trial/research/corpus        # → exit 0, 20 rows
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

Executed 2026-09-28 for candidate A: `dotnet run -- --corpus ../../trial/research/corpus` in
`tools/extract-spike` (since deleted) → 20 rows, SmartReader 0.11.1 + AngleSharp 1.8.2.0,
deterministic (1 distinct output per page over 5 runs), peak RSS `ru_maxrss` 163 201 024 B
committed in `corpus.smartreader.metrics.json` (`peakRssMethod` = `getrusage(RUSAGE_SELF)
.ru_maxrss`); `corpus.smartreader.diacritics.txt` → every page `nfc_roundtrip_equal=True`,
`mojibake_seqs=none`, `base_only_difference=False` where a probe paragraph matched; the
probe-paragraph sets are identical to the trafilatura run's on all 20 pages.

Metric → column map (the task's eight metrics): title → `trafilatura title` / `sr. title`; text
length → `traf. chars` / `sr. chars`; boilerplate leakage → `traf. boiler` / `sr. boiler`;
main-text recall → `traf. recall` / `sr. recall` (proxy, see §1.5); Vietnamese diacritics →
`traf. diac` / `sr. diac`; latency → `traf. p50/p95` / `sr. p50/p95`; memory → whole-run peak RSS
in §1.2 (172 MB trafilatura, 163 MB SmartReader); failure behaviour → `traf. fail` / `sr. fail` +
§1.3.
