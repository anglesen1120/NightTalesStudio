# 02.7 — Source finder assistant (feature plan)

> Refines: `../../../02-story-writing.md` Epic 02.7 (stories 02.7.1, 02.7.2), §5.7; `../../../10-web-ui.md` 10.5.8; DECISIONS §10.10, §10.12, §10.13.
> Contract: `../README.md` (facts W1, J1, J2, O1–O9; findings F19, F33; decisions D1, D2, D6, D7, D8, D10, D11, D14, D18, D19, D21; §10 T17, T38, T39, T55, T56).
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ verified (source + date) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet. Behind feature flag `source_finder`. Phase P.

## 1. Scope

| | |
|---|---|
| Stories | **02.7.1** Spike: link finder, option A vs option B (Must, P) · **02.7.2** "Find sources for me" (Should, P; used by autopilot 02.8.3 for true crime) |
| Tasks | 02.7.1-T1 spike A vs B (M) · 02.7.2-T1 allow-list governance (S) · 02.7.2-T2 `FindSourcesCommand` + candidates + endpoints (S) · 02.7.2-T3 finder runner (L; **M if option A wins**) = **4.0 d** (3.0 d with A) |
| In scope | Spike and decision (D18). The allow-list of research sites and how it is reviewed. The command, run and candidate model. The runner behind `ISourceFinder` (option A fully; option B at design level only, built only if B wins). Metering and budget. The autopilot entry point |
| Out of scope | Fetching page text: the 02.5.2 fetcher does it after a candidate is **added** through `AddSourceCommand`. Autopilot orchestration (02.8.3 calls the command with `trigger = autopilot`). UI layout (10.5.8). Any access to YouTube/TikTok/social platforms (D11, DECISIONS §10.10) |
| Acceptance (story, restated) | Given flag `source_finder` on and a go from 02.7.1, When "Find sources" is requested, Then a `find_sources` run returns **candidate links only**, taken from allow-listed sites. Nothing is added automatically. Every candidate the user picks goes through `AddSourceCommand` and the normal fetcher. The finder never logs in, never posts and never touches blocked domains |

## 2. Confirmed facts

| # | Fact | Status / source |
|---|---|---|
| W1 | Responses `web_search` tool; .NET `ResponseTool.CreateWebSearchTool()`; citations as `url_citation` annotations (URL, title, location); price **$10 / 1k calls** + search content tokens at model rates | ✅ README W1 |
| W2 | Domain filtering: `filters.allowed_domains` (**up to 100**) or `filters.blocked_domains` (up to 100). Domains are given **without** scheme. The full list of consulted URLs is returned when `include: ["web_search_call.action.sources"]` is set; sources ≥ citations | ✅ https://developers.openai.com/api/docs/guides/tools-web-search §"Domain filtering", §"Sources" (read 2026-09-23) |
| W3 | How the .NET SDK 2.14.0 exposes `filters` and `include` for `CreateWebSearchTool()` | ⚠️ not seen in the README snippet; confirm in the spike. Fallback: send the raw tool JSON through the SDK's `BinaryContent` overload |
| W4 | Whether `web_search` can be combined with a strict `text.format` JSON schema on `gpt-6-luna` | ⚠️ spike measures it. The design does not depend on it: links come from annotations, not from model text |
| J1 | jev-ultrafast is Python, MIT and "MVP". It drives an **existing Chrome over remote debugging** and shares its profile. There are no headless/container instructions. No frames, shadow roots or pop-ups | ✅ README J1 (headless ⚠️) |
| J2 | TypeSafe Jev: $0.042 / 1M input tokens, output free; English primary; legal terms **not read** | ✅ price / ⚠️ terms |
| D18 | A wins if success ≥ B − 10 points **and** cost ≤ $0.05 per search. B additionally needs a written exception to DECISIONS §10.13 (C#-only) and the TypeSafe terms read | README D18 |
| T17 / T39 | `find_sources` runs are **episode-level**. The `find_sources` fetch-task result body is `{ status: done, candidates[] }` / `{ status: failed, errorCode }` | README §10 |

[INFERENCE] Option A cost per search ≈ 1–3 web-search calls ($0.01–0.03) + a few thousand tokens at luna rates (< $0.001). That is inside the D18 $0.05 bound unless the model loops on searches. The spike caps tool calls (§7).

## 3. Domain and data

**New table `source_candidates`** (CR-4 addition; `IUserOwned`, deleted with the episode):

| Column | Type | Rule |
|---|---|---|
| `id` | uuid (v7) PK | |
| `user_id` | uuid NOT NULL | from the episode (09.2.3) |
| `episode_id` | uuid NOT NULL FK → episodes ON DELETE CASCADE | |
| `generation_run_id` | uuid NOT NULL FK → generation_runs | the `find_sources` run that proposed it |
| `url` | text NOT NULL | as cited, ≤ 2,048 chars |
| `normalized_url` | text NOT NULL | D11 normaliser (shared with 02.5.1-T1) |
| `registrable_domain` | text NOT NULL | PSL (02.5.1-T1) |
| `title` | text NULL | from the citation, ≤ 300 chars, never rendered as HTML |
| `site_name` | text NOT NULL | from the allow-list entry |
| `rank` | smallint NOT NULL | 1…10, citation order |
| `status` | text NOT NULL | `proposed` / `added` / `dismissed` (CHECK) |
| `source_id` | uuid NULL FK → story_sources | set when `added` |
| `created_at`, `updated_at` | timestamptz | |

Indexes: unique `(episode_id, normalized_url)`, so a URL is proposed once per episode even across runs; index `(episode_id, status, rank)`.

Invariants:
- A candidate exists only if its URL passed the **D11 URL rules** and its registrable domain is on the **allow-list** at proposal time. The runner discards everything else before insert.
- Only annotation URLs (`url_citation`, or `sources` when citations are empty) are candidates. **Text URLs written by the model are ignored** (G7).
- `added` implies `source_id` is set and the source went through every 02.5.1 cap (10 per episode, 30 fetches per 24 h, duplicates).
- The run never writes `story_sources`.

**Allow-list** `config/source-finder/allowed-sites.v1.json` (tracked, reviewed by PR; 02.7.2-T1):

```json
{ "version": 1,
  "sites": [ { "domain": "vi.wikipedia.org", "name": "Wikipedia (vi)", "language": "vi", "category": "encyclopedia",
               "terms_url": "https://…", "robots_checked": true, "automated_access_basis": "⚠️ to be recorded",
               "status": "candidate", "reviewed_at": null, "reviewer": null } ] }
```

- Only entries with `status = approved` are used at runtime.
- `approved` requires `automated_access_basis` (a quoted clause or a documented API permission), `reviewed_at` and `reviewer`.
- Startup check: fail start if any entry matches a D11 blocked domain (registrable domain or subdomain), if there are more than 100 approved entries (W2), or if an entry has a scheme or path.

`generation_runs` usage: `kind = find_sources`, episode-level (T17). [DECISION] A user-typed query is stored in the outbox payload of `SourceFindRequested` only (≤ 300 chars, deleted with the outbox row). It is not in `generation_runs`, Hangfire arguments or logs (D19, 09.2.6 ids-only). Without a typed query the job derives it again from the episode title + brief. The run keeps `models`, `prompt_version`, usage and outcome.

## 4. Use cases

| # | Use case | Rules | Result / errors |
|---|---|---|---|
| U1 | `FindSourcesCommand(Guid EpisodeId, string? Query, GenerationTrigger Trigger)` | Flag `source_finder` on (else 404 `feature_disabled`). Episode in `draft` or `story_review` (else 409 `episode_invalid_state`). `Idempotency-Key` required (400 `idempotency_key_required`, 422 on reuse). Query ≤ 300 chars, default = episode title + brief first 300 chars. One active `find_sources` run per episode (409 `find_sources_in_progress` — new D8 code, T59). `IBudgetGuard.CheckAsync(worstCase)` (429 `spend_cap_reached`). `ICreditLedger.ReserveAsync(userId, CreditOperation.SourceFind, runId, bound)` | 202 `{ generationRunId }`; outbox `SourceFindRequested` → `FindSourcesJob(generationRunId)` (option A) or a `fetch_tasks` row (option B) |
| U2 | `ListSourceCandidatesQuery(EpisodeId)` | Tenant filter (404 for others) | 200 `{ items: [{ id, url, title, siteName, rank, status, sourceId }] }` newest run first, then rank |
| U3 | `AddSourceCandidateCommand(EpisodeId, CandidateId)` | Candidate `proposed`; calls `AddSourceCommand(url)` **in the same transaction**, so all 02.5.1 caps and codes apply unchanged | 201 with the new source; the candidate becomes `added` + `source_id`. Errors from 02.5.1 pass through (`source_limit_reached`, `source_fetch_limit_reached`, `source_duplicate`, `source_domain_not_allowed`) |
| U4 | `DismissSourceCandidateCommand(EpisodeId, CandidateId)` | `proposed` → `dismissed`; idempotent | 204 |
| U5 | Autopilot (02.8.3, true crime) | Same command, `trigger = autopilot`, key `autopilot:{seriesId}:{slotAt:O}:find_sources`. The autopilot then adds the top-ranked candidates through U3 until 3 are fetched or candidates run out; owner approval of the fact sheet is still required (02.8.2) | If flag off / no-go / 0 candidates → the run ends `waiting_owner` with reason "add sources" (F33) |

Audit (D13, added keys — T59): `episode.sources_find_requested`, `episode.source_candidate_added`, `episode.source_candidate_dismissed`. `data` = ids, counts, run id. No URLs.

## 5. API and events

New routes (CR-12 to 00 §8):

| Route | Status codes |
|---|---|
| `POST /api/v1/episodes/{id}/sources/find` body `{ "query"?: string }`, header `Idempotency-Key` | 202, 400 `validation_failed`/`idempotency_key_required`, 404 `not_found`/`feature_disabled`, 409 `episode_invalid_state`/`find_sources_in_progress`, 422 `idempotency_key_reused`, 429 `spend_cap_reached` |
| `GET /api/v1/episodes/{id}/sources/candidates` | 200, 404 |
| `POST /api/v1/episodes/{id}/sources/candidates/{candidateId}/add` | 201, 404, 409 (`source_*` codes from 02.5.1, `source_candidate_not_proposed`), 429 `source_fetch_limit_reached` |
| `POST /api/v1/episodes/{id}/sources/candidates/{candidateId}/dismiss` | 204, 404 |

Events: `EpisodeUpdated` on run start and finish (the detail DTO gains `generation.kind = find_sources`, CR-7). No new SignalR event type.

## 6. UI

10.5.8 (Should, P). The research page shows a "Find sources" button (flag on), with the stage "Searching…" while the run is active. Results list: title, site name, "open" link (`rel="noopener noreferrer"`, new tab), and Add / Dismiss buttons. Nothing is pre-ticked.

Copy keys (vi / en):

| Key | vi | en |
|---|---|---|
| `sources.find.button` | Tìm nguồn giúp tôi | Find sources for me |
| `sources.find.running` | Đang tìm nguồn… | Searching for sources… |
| `sources.find.empty` | Không tìm thấy nguồn phù hợp trên các trang được phép. Hãy dán liên kết bạn tự tìm. | No suitable sources on the allowed sites. Paste links you found yourself. |
| `sources.find.note` | Chỉ là gợi ý — hãy tự kiểm tra từng nguồn trước khi thêm. | Suggestions only — check each source before adding it. |
| `error.find_sources_in_progress` | Đang tìm nguồn cho tập này. | A source search is already running for this episode. |
| `error.find_sources_generation_failed` | Không tìm được nguồn lúc này. Vui lòng thử lại sau. | Could not search for sources right now. Please try again later. |

## 7. Jobs and integrations

**Option A (expected winner, D18): `FindSourcesJob(Guid generationRunId)`** in the Worker (it calls only OpenAI and fetches no pages, so it does not need the fetcher container).
- Request through `ITextClient` (`TextStep.Finder`), Responses API, `store = false`, model `OpenAi:Models:Finder` (default `gpt-6-luna`), effort `OpenAi:Effort:Finder` (`low`), `max_output_tokens` from `price_versions.limits.finder` (2,000; T12/T56).
- Tool: `web_search` with `filters.allowed_domains` = approved allow-list domains (≤ 100, W2), `include: ["web_search_call.action.sources"]`, `tool_choice: auto`.
- Prompt `prompts/finder/v1.md` + `prompts/schemas/finder.v1.json`. The data object (D6) holds `{ task: find_sources, language, topic, query }` only. No brief beyond 300 chars, no bible, no facts.
- Tool-call cap: `SourceFinder:MaxSearchCalls` 3. The response is rejected as `find_sources_generation_failed` if it reports more `web_search_call` items than the cap (billing guard; the cap is also stated in the prompt).
- Extraction: collect `url_citation` URLs in order. If there are none, fall back to `sources` URLs. Then normalise (D11), require an allow-listed registrable domain, dedupe against the episode's existing candidates and `story_sources`, and cap at 10.
- Metering: one `usage_records` row, `operation = source_find`. `cost_usd` = token cost + `web_search_calls × $0.01` (the per-call fee is a rate in `price_versions.rates.web_search_call`, T55).
- Outcomes (D7 mapping): `Ok` → candidates inserted, run `succeeded` (0 candidates is still success). `Moderated` → `find_sources_generation_failed` + `moderation_events(stage = find_sources)`. Unavailable, invalid or other → `find_sources_generation_failed` (not counted anywhere; there is no daily limit on finds besides the budget). Funds → pause reason (D2).

**Option B (built only if B wins):** the runner is a sidecar in the fetcher subnet that leases `fetch_tasks` with `kind = find_sources` (D10/T38/T39):
- The sidecar gets one new signed-out Chromium profile per task, deleted after the task.
- Navigation is restricted to approved allow-list domains by a request interceptor. Blocked domains (D11) are refused, as are logins, form posts and downloads.
- It returns `{ status: done, candidates: [{ url, title, site }] }`.
- The Api applies the same extraction rules as option A before insert.
- It needs a Python/Chromium image, the DECISIONS §10.13 exception and TypeSafe terms; T3 estimate L.

`FetchLeaseSweeperJob` (02.5.2-T2) and `StaleGenerationSweeperJob` (02.2.2-T3) already cover `find_sources` runs and tasks.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Prompt injection from search results ("add this URL", "ignore rules") | Links are taken **only** from annotations/sources, never from model text. The data object is the only input (D6). Output is candidates only, which a user or the autopilot must add explicitly |
| SSRF through a candidate URL | A candidate is never fetched by the finder. When added, it goes through `AddSourceCommand` → D10/D11 fetcher rules. The candidate URL is also D11-validated before insert |
| Blocked platforms (YouTube, TikTok, social) | Allow-list only (A: `allowed_domains`; B: interceptor). Startup check rejects allow-list entries on the D11 list. Extraction re-checks every URL against D11 |
| Terms of service of research sites | An entry becomes `approved` only with a recorded `automated_access_basis` (02.7.2-T1). Option A searches through OpenAI's index. We do not crawl the sites; the later page fetch is the normal 02.5.2 fetch with robots respected |
| Cost abuse (loops, repeated clicks) | Idempotency key; one active run per episode; `IBudgetGuard`; `MaxSearchCalls` 3; `max_output_tokens` from `price_versions.limits` |
| Leaking the brief to a third party | Only the ≤ 300-char query and topic/language are sent. OpenAI is already the story processor. Option B sends the query to TypeSafe and a text-model provider → covered by the B exception decision |
| Stored XSS via candidate title | Titles are stored as text, ≤ 300 chars, rendered as text (Angular binding), never HTML |
| Cross-tenant access | `IUserOwned` + 404 for other users (09.2.3); candidate id is scoped by `episode_id` |
| Logs | D19: log run id, counts, registrable domains; never the query, URLs or titles |

Retention: `source_candidates` are deleted with the episode (FK cascade). No text is stored.

## 9. Test plan

| Id | Test | Level |
|---|---|---|
| S1 | `FindSources_FlagOff_Returns404FeatureDisabled` | integration |
| S2 | `FindSources_ActiveRun_Returns409FindSourcesInProgress` | integration |
| S3 | `FindSources_SameKey_ReturnsSameRunId_NoSecondRun` | integration |
| S4 | `FindSources_OverBudget_Returns429SpendCapReached` | integration (FakeBudgetGuard) |
| S5 | `Extract_ModelTextUrl_NotInAnnotations_Ignored` | unit |
| S6 | `Extract_BlockedDomainCitation_Discarded` (youtube.com, m.facebook.com) | unit |
| S7 | `Extract_NonAllowListedDomain_Discarded` | unit |
| S8 | `Extract_DuplicateOfExistingSource_Discarded`; `Extract_CapsAtTen` | unit |
| S9 | `Extract_NoCitations_FallsBackToSources` | unit |
| S10 | `Job_MoreThanMaxSearchCalls_FailsFindSourcesGenerationFailed` | unit (FakeTextClient) |
| S11 | `AddCandidate_AppliesSourceCapsAndMarksAdded` (11th source → 409 `source_limit_reached`, candidate stays `proposed`) | integration |
| S12 | `AllowList_EntryOnBlockedDomain_FailsStartup`; `AllowList_Over100Approved_FailsStartup` | unit |
| S13 | `Candidates_OtherUser_Returns404` | integration |
| S14 | Spike report: 10 cases × {A, B} with success, precision, cost, latency | manual (02.7.1-T1) |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.7.1-T1 | Spike option A vs option B + go/no-go | M 1.0 | 02.7.2-T1 (candidate allow-list), 01.2.4 (story key) | 02.7.2-T3, DECISIONS §10.10 |
| 02.7.2-T1 | Allow-list governance | S 0.5 | — | 02.7.1-T1, 02.7.2-T3 |
| 02.7.2-T2 | `FindSourcesCommand` + `source_candidates` + endpoints | S 0.5 | 02.2.2-T1 (`generation_runs`), 02.5.1-T1 (`AddSourceCommand`, normaliser, PSL) | 02.7.2-T3, 02.8.3-T1 |
| 02.7.2-T3 | Finder runner | L 2.0 (M 1.0 if A) | 02.7.1-T1 (decision), 02.7.2-T2, 02.2.1-T2 (`ITextClient`) | 02.8.3 true-crime research step |
| **Total** | | **4.0 d** (3.0 d with A) | | |

Matches README §9 (4.0 d; 3.0 d if option A).

### Open items for the TL
1. New D8 code `find_sources_in_progress` (409) and `source_candidate_not_proposed` (409); new D13 keys (§4). → recorded as T59.
2. `source_candidates` table and the four routes → CR-4 / CR-12.
3. W3 ⚠️: .NET SDK surface for `filters` / `include` on the web-search tool; the fallback is raw tool JSON.
4. `price_versions.rates.web_search_call` (0.01 USD) must be seeded by 02.2.4-T1 (T55).
5. Allow-list initial entries are all `candidate` until a human records the automated-access basis; with zero `approved` entries the feature cannot run → the flag stays off until 02.7.2-T1 approves ≥ 3 sites per language.

## 11. Rollout

1. 02.7.2-T1 lands the allow-list file (all `candidate`), then the review approves sites.
2. 02.7.1-T1 runs the spike with the approved list; the D18 verdict is written to DECISIONS §10.10.
3. On **go (A)**: build 02.7.2-T2 + T3 (M). On **go (B)**: T3 at L, after the §10.13 exception. On **no-go**: build nothing further. The flag stays off, and 02.8.3 true-crime runs stop at `waiting_owner` "add sources" (F33).
4. Migration `AddSourceCandidates` (expand only). Rollback = flag off; the table can stay.
5. Enable `source_finder` for the owner. Monitor `openai_cost_usd_total{operation="source_find"}`, candidates per run and the added-rate for 2 weeks.
