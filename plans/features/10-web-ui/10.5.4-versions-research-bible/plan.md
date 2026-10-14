# 10.5.4–10.5.8 — Versions and diff, research/fact sheet, approve/reopen, series bible, find sources (web UI wave 1)

> Refines `../../10-web-ui.md` §10.5 items 10.5.4–10.5.8 (lines 145–162). Contract: `../README.md` §3 (D1–D18) and §8.1; backend contracts: `features/02-story-writing/02.3-checkpoint-1/plan.md` §5, `02.5.1-sources-fetcher/plan.md` §5, `02.5.3-fact-sheet/plan.md` §5/§6, `02.5.4-T1-fact-sheet-edit-approve-etag.md`, `02.6.3-series-bible/plan.md` §5/§6, `02.7-source-finder/plan.md` §5/§6, `02.7.2-T2-find-sources-command-candidates-model-endpoints.md`.
> Legend: ✅ read from a file/URL (`path:line`) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> All five tasks are **P · M1**, FE, folder `10.5.4-versions-research-bible/`.

## 1. Scope

**In scope** (five screens/panels of the episode + series workspace; no backend work):
- **10.5.4-T1** — versions side panel on the episode editor route: version list, select two → word-level diff, "Restore as new version". Produces the shared `DiffView` + `shared/util/text-diff.ts`.
- **10.5.6-T1** — approve dialog (confirm, `cost-line` slot empty in P per D14, true-crime attestation block) and the reopen dialog/action; the 409/412 handling those two calls produce.
- **10.5.5-T1** — research page `/episodes/:id/research`: sources list (paste link / note, max 10), live-ish fetch status, retry, delete; fact-sheet editor (generate, edit facts, source chips, confidence badge, approve) and the true-crime gate that disables "Write story".
- **10.5.7-T1** — series bible panel `/series/:id/bible`: editable narrator/characters/places/rules/summaries with `If-Match`, proposal banner with ops + preview counts, accept/dismiss.
- **10.5.8-T1** — "Find sources" block on the research page behind flag `source_finder`: run, candidate list with site name + open link, tick-to-add via explicit Add buttons, dismiss.

**Out of scope:** the editor itself and its conflict dialog (10.5.3-T1), hints/annotations (10.5.10-T1), structure/scene chips (10.5.9-T1), self-review badge and similarity highlights (10.5.3-T2), autopilot/run log (10.4.3-T2), credits/quote/balance texts (D14 — S tasks 10.8.x), video review (10.6.x), any new API route or DTO (02 owns them).

**Assumed done before these tasks:** 10.5.3-T1 (episode editor route, `StoryStore`/`EpisodeStore`, dirty guard, `ConflictDialog`), 10.2.x (session, `AppErrorMapper`, `RealtimeService`), 10.1.3-T1 (Material theme + shared UI), 10.1.4-T1 (generated client), 10.2.5-T1 (Transloco scopes).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| V1 | `GET /api/v1/episodes/{id}/story/versions` → `StoryVersionSummaryDto[] { versionId, version, source, createdAt, wordCount, etag, similarityScore, approved, isCurrent }`, ordered `version DESC`; 404 for a foreign episode | 02.3 `plan.md:148`; 02.3.4-T2:48 ✅ |
| V2 | `GET /api/v1/episodes/{id}/story/versions/{versionId}` → `StoryVersionDetailDto { versionId, version, source, createdAt, createdBy, etag, content, body, keywords, wordCount, qualityHints[], auditReport, similarityScore, factSheetId }`; 404 for a `versionId` of another episode | 02.3.4-T2:49,53 ✅ |
| V3 | Story versions are **append-only/immutable** (`ValueGeneratedNever`, `AfterSaveBehavior = Throw`, `generation_run_id` NULL for `source = edited`) and there is **no restore route** in `00 §8` or in 02 | 02.2.2-T1:53; `00-overview.md:360` (route list, read in full 2026-09-24) ✅ |
| V4 | `PUT /api/v1/episodes/{id}/story` body `{ content: story.v1 }` + `If-Match`; 200 `StoryVersionDto` + `ETag`; 400 `story_invalid_content`, 409 `episode_invalid_state`, 412 `story_version_conflict`, 428 `if_match_required` | 02.3 `plan.md:145` ✅ |
| V5 | `POST …/story/approve` body `{ attestation?: { accepted, textVersion } }` + `If-Match`; 400 `story_attestation_required`, 409 `episode_invalid_state` / `story_fact_sheet_outdated` / `story_sources_required` / `story_too_similar_to_source` / `story_moderated`, 412, 428. `POST …/story/reopen` (no body) + `If-Match` → 200 `{ status, currentStory }` | 02.3 `plan.md:146-147` ✅ |
| V6 | `currentStory.attestation { required, textVersion, text, acceptedAt }` is the delivery of the attestation text (T29) — no `GET /meta/story-attestation` route; the dialog renders it in the UI language | 02.3.4-T2:135; `../README.md` C8 ✅ |
| V7 | Sources: `GET /episodes/{id}/sources` → `SourceDto[]` + `limits { maxPerEpisode, urlSourcesLast24h, maxUrlSourcesPerDay }`; `POST` `{ url }` **xor** `{ noteText }` → 201; `DELETE …/sources/{sourceId}` → 204; `POST …/sources/{sourceId}/retry` → 202. Codes: 400 `source_url_invalid` / `source_domain_not_allowed`, 409 `source_duplicate` / `source_limit_reached` / `episode_invalid_state`, 429 `source_fetch_limit_reached` (+`Retry-After`) | 02.5.1 `plan.md:225-228` ✅ |
| V8 | Source row fields `id, kind, url, title, status, errorCode, httpStatus, textChars, fetchedAt, isCited`; status transitions `pending → fetched/failed`, blocked domain → `rejected`; job codes `source_unreachable`, `source_too_large`, `source_blocked_by_robots`, `source_unsupported_type`, `source_no_text`; a `user_note` row is created `fetched` (no fetch task), note ≤ 3,000 chars; caps 10/episode, 30 url sources/user/24 h | 02.5.1 `plan.md:29-32, 181-183, 208-211, 216` ✅ |
| F1 | Fact sheet: `GET …/fact-sheet` 200 + `ETag: "fs-<id>"`, `PUT …/fact-sheet` (`If-Match`), `POST …/fact-sheet/approve` (`If-Match`), `POST …/fact-sheet/generate`. `GET …/fact-sheet` returns **200 with a nullable `FactSheetDto?`** and its **only** error is 403 — a missing sheet is *not* a 404 (02.5.3 `plan.md:122`); `PUT` errors are 428 `if_match_required` / 412 `fact_sheet_conflict` / 409 `episode_invalid_state` / 400 validation; approve is 428 / 412 / 409 `episode_invalid_state` / 409 `story_sources_required` / 409 `fact_sheet_sources_insufficient`; generate is 409 `episode_invalid_state` / 409 `fact_sheet_sources_insufficient` / 409 `fact_sheet_generation_in_progress` / 429 `spend_cap_reached` (02.5.3 `plan.md:122-126`) | 02.5.4-T1:17,24,31-33; 02.5.3 `plan.md:122-126` ✅ |
| F2 | `FactItem { Id, Statement, SourceIds, SourceAliases, SourceOverlap, Confidence, Origin }`, `FactConfidence { High, Medium, Low }` (wire `high|medium|low`); `low` is an advisory hint — never hidden or dropped (T53); `FactItemInputDto { Id?, Statement, SourceIds[], Confidence? }` (null id = new fact) | 02.5.3 `plan.md:74-79,113-114,135` ✅ |
| F3 | Fact-sheet states the backend must support: empty, generating (progress via `EpisodeUpdated`), draft (editable, overlap chip, "your note" chips), approved (read-only + Edit → new draft), error (`fact_sheet_sources_insufficient`, `fact_sheet_generation_in_progress`, `spend_cap_reached`). Hand-over copy keys `factSheet.{empty,generate,generating,approve,edit,userNoteLabel,overlapWarning,reopenStoryHint}` + `factSheet.errors.{code}` | 02.5.3 `plan.md:134-136` ✅ |
| F4 | `PUT …/fact-sheet` edits create a **new draft version** (never mutates the old row); approve supersedes the previous approved row; response `FactSheetSummaryDto` + new ETag; `GetFactSheetQuery` returns the latest version with resolved `FactSourceRefDto (title, url, alias, IsUserNote)` | 02.5.4-T1:25-35,72 ✅ |
| B1 | `GET /series/{id}/bible` → `BibleDto { seriesId, version, etag, content, proposal }`, `ETag: "sb-<version>"`; `PUT` requires `If-Match` (428 `if_match_required`, 412 `bible_conflict`, 400 `bible_invalid_content` with `errors` per field); `POST …/bible/proposal/accept` → 200 `{ version, etag }`; `POST …/bible/proposal/dismiss` → 204; both → 409 `bible_proposal_not_pending` when nothing is pending. `episode_summaries` is editable (title/summary, removable); `structure`/`approved_at` are re-derived server-side | 02.6.3 `plan.md` §4 U1–U4, §5 ✅ |
| B2 | `proposal { basedOnVersion, proposedFromEpisodeId, proposedAt, ops: [ { kind, summary?, name?, description?, text?, reason? } ], preview { characters, places, rules, summaryForEpisodeId } }`; `preview` is computed server-side so the UI can render the banner without resolving op semantics | 02.6.3 `plan.md` §4 U1 ✅ |
| B3 | **No bible SignalR event in P**: the panel refetches when the user opens/navigates to the bible route and after an approve on the episode page | 02.6.3 `plan.md` §5 (Events) ✅; `../README.md` D17 |
| S1 | Finder routes: `POST /episodes/{id}/sources/find` body `{ query? }` + `Idempotency-Key` → 202 (`Location`), 400 `validation_failed` / `idempotency_key_required`, 404 `feature_disabled`, 409 `episode_invalid_state` / `find_sources_in_progress`, 422 `idempotency_key_reused`, 429 `spend_cap_reached`; `GET …/sources/candidates` → 200; `POST …/candidates/{candidateId}/add` → 201 (02.5.1 codes + `source_candidate_not_proposed`); `POST …/candidates/{candidateId}/dismiss` → 204 | 02.7 `plan.md` §5; 02.7.2-T2:39-53 ✅ |
| S2 | `SourceCandidateDto(Id, Url, Title?, SiteName, Rank, Status, SourceId?, CreatedAt)` ordered `created_at desc, rank`; statuses `proposed|added|dismissed`; nothing is added automatically; the find run sets `generation.kind = find_sources` in the episode detail and emits `EpisodeUpdated` on start and finish | 02.7.2-T2:52-53; 02.7 `plan.md` §5 ✅ |
| S3 | Hand-over copy keys for the finder: `sources.find.{button,running,empty,note}`, `error.find_sources_in_progress`, `error.find_sources_generation_failed` | 02.7 `plan.md` §6 ✅ |
| E1 | Events the SPA consumes: `EpisodeUpdated`, `RenderProgress`, `PublicationUpdated`; clients refetch `GET /episodes/{id}` on events; the research/fact-sheet changes ride on `EpisodeUpdated{change="factSheet"}` | `00 §8`; `../README.md` C7; 02.5.4-T1:27 ✅ |
| D9c | ETag values: story `"sv-<id>"`, fact sheet `"fs-<id>"`, bible `"sb-<version>"`; 412 codes `story_version_conflict` / `fact_sheet_conflict` / `bible_conflict` | `../README.md` C10 (02 D9) ✅ |
| D10c | i18n: Transloco, `vi` default + `en`, one scope per feature, files `web/src/assets/i18n/<scope>/<lang>.json`, key style `scope.area.item`; keys handed over by other plans are used **verbatim** | `../README.md` D10 ✅ |
| D14c | P is metering-only: **no credit amount, quote or balance text in P**; `ConfirmDialog` has an optional `cost-line` slot that S tasks fill | `../README.md` D14, F12 ✅ |

**Cross-plan key discrepancy (⚠️ non-blocking):** D9 mandates the error key shape `errors.<code>`, while 02.5.3 §6 hands over `factSheet.errors.{code}` and 02.7 §6 hands over `error.<code>`. This plan registers the D9 canonical keys and the handed-over aliases side by side (§6) and records it as an [OPEN] item in §10 instead of editing the 02 files.

## 3. Domain and data

Client-side only (no database in this feature): models, ports, stores and signals. Mirrors the DTOs exactly; no extra wire fields (`../README.md` D18, D13).

**Models** — `web/src/app/features/episodes/domain/`:

```ts
// story-version.model.ts
export type VersionSource = 'generated' | 'edited' | 'revised' | (string & {});
export interface StoryVersionSummary { versionId: string; version: number; source: VersionSource; createdAt: string;
  wordCount: number; etag: string; similarityScore: number | null; approved: boolean; isCurrent: boolean; }
export interface StoryVersionDetail extends Omit<StoryVersionSummary,'similarityScore'|'approved'|'isCurrent'> {
  createdBy: string; content: StoryContentV1; body: string; keywords: string[]; qualityHints: QualityHint[];
  auditReport: AuditReport; similarityScore: number | null; factSheetId: string | null; }
// research.model.ts
export type SourceKind = 'url' | 'user_note';
export type SourceStatus = 'pending' | 'fetched' | 'failed' | 'rejected';
export interface ResearchSource { id: string; kind: SourceKind; url: string | null; title: string | null;
  status: SourceStatus; errorCode: string | null; httpStatus: number | null; textChars: number;
  fetchedAt: string | null; isCited: boolean; }
export interface SourceLimits { maxPerEpisode: number; urlSourcesLast24h: number; maxUrlSourcesPerDay: number; }
export type FactConfidence = 'high' | 'medium' | 'low';
export interface Fact { id: string; statement: string; sourceIds: string[]; sourceAliases: string[];
  sourceOverlap: number | null; confidence: FactConfidence; origin: 'model' | 'user'; }
export interface FactSourceRef { id: string; title: string | null; url: string | null; alias: string; isUserNote: boolean; }
export interface FactSheet { id: string; episodeId: string; version: number; status: 'draft'|'approved'|'superseded';
  etag: string; facts: Fact[]; sources: FactSourceRef[]; contentSha256: string; }
export interface SourceCandidate { id: string; url: string; title: string | null; siteName: string;
  rank: number; status: 'proposed'|'added'|'dismissed'; sourceId: string | null; createdAt: string; }
// story-approval.model.ts
export interface Attestation { required: boolean; textVersion: string; text: string; acceptedAt?: string | null; }
```

**Ports** (abstract classes, implemented only in `data-access`, `../README.md` D5):
- `StoryVersionRepository`: `list(episodeId): Promise<StoryVersionSummary[]>`; `detail(episodeId, versionId): Promise<StoryVersionDetail>`.
- `ResearchRepository`: `sources(episodeId)`, `addUrl(episodeId, url)`, `addNote(episodeId, note)`, `removeSource(episodeId, sourceId)`, `retrySource(episodeId, sourceId)`, `factSheet(episodeId)`, `generateFactSheet(episodeId, idempotencyKey)`, `saveFactSheet(episodeId, etag, content)`, `approveFactSheet(episodeId, etag)`.
- `StoryApprovalRepository` (extension of 10.5.3-T1's `StoryRepository`): `approve(episodeId, etag, attestation?)`, `reopen(episodeId, etag)`.
- `SourceFinderRepository`: `find(episodeId, query, idempotencyKey)`, `candidates(episodeId)`, `addCandidate(episodeId, candidateId)`, `dismissCandidate(episodeId, candidateId)`.
- `BibleRepository` (feature `series`): `get(seriesId)`, `save(seriesId, etag, content)`, `acceptProposal(seriesId)`, `dismissProposal(seriesId)`.
- Bible content (`bible.v1`, 02.6.3 §3.1/§3.2): the wire is **snake_case** and is mapped to the camelCase client model by explicit functions in `bible.mapper.ts` (D5) — `narrator { name, persona, voice_notes }` (persona required), `recurring_characters[].first_episode_id`, `places[]`, `rules[].text`, `episode_summaries[].{episode_id, approved_at}`; ids `ch`/`pl`/`rl` are server-assigned and never renumbered; `episode_summaries[].structure`/`approved_at` are server-derived on PUT and accept; the PUT body is `{ content }` without `schema_version`.

**Pure helpers:**
- `web/src/app/shared/util/text-diff.ts` — `diffWords(left, right): DiffHunk[]` over jsdiff 9.0.0 (`../README.md` FE14) with NFC + whitespace normalisation, **capped at 20,000 tokens per side** (a long story is ≤ 60 segments, C9); returns `{ segments: { kind: 'equal'|'insert'|'delete', text: string }[], added: number, removed: number }`.
- `web/src/app/shared/util/segment-address.ts` (owned by 10.5.3-T1, D18) is reused for addressing; the versions panel does not define a second formatter.
- `web/src/app/shared/ui/diff-view/diff-row.ts` — the generic `DiffRow { kind: 'add' | 'remove' | 'change'; label: string; before?: string; after?: string }` consumed by `DiffView`'s `rows` mode (WR16/P3; produced by 10.5.4-T1 per WR4).

**Stores** (signals only, no NgRx; `../README.md` D13):

| Store | Provided by | Signals | Methods |
|---|---|---|---|
| `VersionsStore` (`features/episodes/application/versions.store.ts`) | the editor route (not root) | `versions`, `left`, `right`, `leftDetail`, `rightDetail`, `diff = computed()`, `loading`, `loadingDiff`, `restoring`, `error: AppError \| null` | `load()`, `select(versionId, side)`, `restoreAsNewVersion(versionId)` |
| `ResearchStore` (`…/research.store.ts`) | the research route + the editor route (gate read-only) | `sources`, `limits`, `factSheet`, `runningKind: 'fetch'\|'fact_sheet'\|'find'\|null`, `busy`, `error`, `gate = computed<GateState>()`, `pendingCount = computed()` | `loadSources()`, `loadFactSheet()`, `addUrl()`, `addNote()`, `remove()`, `retry()`, `generate()`, `updateFact()`, `addFact()`, `removeFact()`, `save()`, `approve()`, `refresh()` |
| `ApprovalStore` (`…/approval.store.ts`) | the editor route | `attestation = computed()` (from the episode store), `approving`, `reopening`, `error`, `pendingConflict` | `approve(accepted)`, `reopen()` |
| `SourceFinderStore` (`…/source-finder.store.ts`) | the research route | `hasFeature`, `candidates`, `running`, `busyId`, `error` | `find(query?)`, `load()`, `add(id)`, `dismiss(id)` |
| `BibleStore` (`features/series/application/bible.store.ts`) | the bible route | `bible`, `dirty`, `saving`, `proposal = computed()`, `error`, `fieldErrors` | `load()`, `edit(section, index, patch)`, `addOp`, `removeSummary`, `save()`, `acceptProposal()`, `dismissProposal()` |

Invariants carried in the client:
- `gate = 'open'` only when `factSheet?.status === 'approved'` **and** ≥ 2 distinct `fetched` `url` sources are each cited by ≥ 1 fact (F13 mirror, V/F1); the client mirrors the server rule for copy only — the server stays authoritative (409 `story_sources_required`). `gate` returns `'open'` until the first successful `load()` of the instance, so an unloaded (editor-route) instance can never pre-disable the button.
- A missing fact sheet is **not an error**: `GET …/fact-sheet` answers 200 with a null body, so the research page renders `factSheet.empty` (generate enabled once ≥ 1 source is fetched; save/approve disabled because there is no `fs-<id>` to send). That GET's only error is 403 (02.5.3 `plan.md:122`). This matters on the true-crime landing screen, where the sheet is always absent on arrival.
- Restore is a **new version**, never a mutation (V3): the draft is replaced by the old `content`, then `PUT …/story` with the current `If-Match`.
- Fact edits preserve `confidence` when the user does not touch it and default a new fact to `high` (F2/T53); `low` is displayed as an advisory and never disables approve.
- Nothing is persisted in `localStorage`/`sessionStorage` (`../README.md` D2, D18/F25) — dirty state lives in the store.

## 4. Use cases

| # | Actor intent | Calls | States / UI | Errors |
|---|---|---|---|---|
| U1 | See earlier versions | `GET …/story/versions` | `loading` skeleton → list (empty state when the episode has none) | 404 → `ErrorState`; network → retry button |
| U2 | Compare two versions | `GET …/story/versions/{id}` × 2 (parallel, only when both are selected) | panel diff (`@defer (on interaction)`); selection defaults to `current` vs the previous one | 404 → row-level message, keep the other side |
| U3 | Restore an old version as a new one | confirm dialog → `PUT …/story` `{ content }`, `If-Match: currentStory.etag` | draft replaced, breadcrumb "restored from v{n}", toast `episodes.versions.restored` | 412 → `ConflictDialog` (D9); 409 `episode_invalid_state` → offer **Reopen** (10.5.6-T1); 400 `story_invalid_content` → never happens from own versions [INFERENCE]; 428 → console (D9) |
| U4 | Approve the script | `POST …/story/approve` `{ attestation? }` + `If-Match` | confirm dialog; true-crime needs the attestation tick before the button enables | 400 `story_attestation_required`; 409 `story_fact_sheet_outdated` / `story_sources_required` / `story_too_similar_to_source` / `story_moderated` / `episode_invalid_state`; 412 → `ConflictDialog`; 429 → `RetryCountdown` |
| U5 | Reopen an approved script | `POST …/story/reopen` + `If-Match` | confirm dialog with the "edits need reopening" note | 409 `episode_invalid_state` (not approved) → refresh the episode; 412 → `ConflictDialog` |
| U6 | Add a source (link or note) | `POST …/sources` `{ url }`/`{ noteText }` | inline form with `CharCounter` (3,000 for a note); optimistic "pending" row is **not** used — the POST result is the row | 400 `source_url_invalid` / `source_domain_not_allowed` (inline, on the input); 409 `source_duplicate` (point at the existing row); 409 `source_limit_reached` / 429 `source_fetch_limit_reached` (banner + `RetryCountdown` where `Retry-After` exists); 409 `episode_invalid_state` → banner with the `nextActions` from the episode |
| U7 | Watch fetch status | `GET …/sources` on `EpisodeUpdated`, on `resync`, and via an explicit "Refresh status" button | per-row chip: `pending` (spinner), `fetched` (title + domain), `failed` (reason from `errorCode`), `rejected` (blocked domain) | no polling (D17); see §7 |
| U8 | Retry / remove a source | `POST …/sources/{id}/retry`, `DELETE …/sources/{id}` | row buttons; delete asks for confirmation only when `isCited` | 409 `episode_invalid_state`, 429 on retry |
| U9 | Build the fact sheet | `POST …/fact-sheet/generate` + `Idempotency-Key` (10.2.3-T1) | "generating" state, progress from `EpisodeUpdated`/`generation` | 409 `fact_sheet_sources_insufficient` / `fact_sheet_generation_in_progress` / `episode_invalid_state`; 429 `spend_cap_reached` → banner, no quote shown in P (D14) |
| U10 | Edit / add / delete facts, then approve | `PUT …/fact-sheet` (`If-Match` from the loaded sheet), `POST …/fact-sheet/approve` | draft = editable list; approved = read-only + "Edit" creates the next draft | 412 `fact_sheet_conflict` → `ConflictDialog` (reload / save as new / cancel); 400 `fact_sheet_invalid_content` with `errors` → `bindFieldErrors`; 409 `story_sources_required` → the F13 copy |
| U11 | Unblock "Write story" | `gate` computed (no call) | the button on the editor page is disabled with the reason from `episodes.research.gate.writeStoryBlocked` | server stays authoritative |
| U12 | Edit the series bible | `PUT /series/{id}/bible` (`If-Match`) | sections with inline add/remove; "Save" enables only when dirty; `aria-live` "saved" | 412 `bible_conflict` → `series.bible.error.conflict` + reload banner (D9 dialog is not required: B1 says the UI re-fetches); 400 `bible_invalid_content` → per-field keys + `aria-describedby`; 428 → console |
| U13 | Accept / dismiss the bible proposal | `POST …/bible/proposal/accept|dismiss` | banner with `preview` counts + ops list; `role="status"` | 409 `bible_proposal_not_pending` → `series.bible.proposal.none` + refetch |
| U14 | Find sources (flag) | `POST …/sources/find` + `Idempotency-Key` → 202; `GET …/sources/candidates` | block visible only when `hasFeature('source_finder')`; "Searching…" while a `find_sources` run is active; nothing pre-ticked | 404 `feature_disabled` → hide the block + refetch `/me` (D14); 409 `find_sources_in_progress`; 429 `spend_cap_reached`; 422 `idempotency_key_reused` (client re-keys on a user-initiated retry) |
| U15 | Add / dismiss a candidate | `POST …/candidates/{id}/add`, `…/dismiss` | Add sends the normal source path; the candidate row shows the 02.5.1 error inline | 409 `source_duplicate` / `source_limit_reached` / `source_candidate_not_proposed`; 429 `source_fetch_limit_reached` |

Authorization: every call is owner-scoped by the API; the UI never hides a screen by role in P (the owner is the only actor, `../README.md` §1 C1–C3). Feature gating is by flag only (U14).

## 5. API and events

Routes consumed by this folder (all owned by 02 / `00 §8`; this folder adds none):

| Route | Verb | Used by | Request | Success | Errors |
|---|---|---|---|---|---|
| `/api/v1/episodes/{id}` | GET | all five (10.5.7-T1 issues one call for the proposal title when a proposal exists — P5) | — | 200 (C8 blocks) | 404 |
| `/api/v1/episodes/{id}/story/versions` | GET | 10.5.4-T1 | — | 200 `StoryVersionSummaryDto[]` | 404 |
| `/api/v1/episodes/{id}/story/versions/{versionId}` | GET | 10.5.4-T1 | — | 200 `StoryVersionDetailDto` | 404 |
| `/api/v1/episodes/{id}/story` | PUT | 10.5.4-T1 (restore) | `{ content }` + `If-Match: "sv-…"` | 200 `StoryVersionDto` + `ETag` | 400 `story_invalid_content`, 409 `episode_invalid_state`, 412 `story_version_conflict`, 428 |
| `/api/v1/episodes/{id}/story/approve` | POST | 10.5.6-T1 | `{ attestation?: { accepted, textVersion } }` + `If-Match` | 200 `ApprovedStoryDto` | 400 `story_attestation_required`, 409 `episode_invalid_state` / `story_fact_sheet_outdated` / `story_sources_required` / `story_too_similar_to_source` / `story_moderated` / **`keywords_flagged`** (`errors[] { segment, index, rule }`; added to the 09 §5.4 catalogue by 03 CR-9 — 03/README.md:202, 03.2.2-T2:22,29-32), 412, 428, 429 |
| `/api/v1/episodes/{id}/story/reopen` | POST | 10.5.6-T1 | `If-Match` | 200 `{ status, currentStory }` | 409, 412, 428 |
| `/api/v1/episodes/{id}/sources` | GET / POST | 10.5.5-T1 | GET `—`; POST `{ url }` xor `{ noteText }` | 200 `SourceDto[]` + `limits`; 201 `SourceDto` (normalised `url`) | see V7 |
| `/api/v1/episodes/{id}/sources/{sourceId}` | DELETE | 10.5.5-T1 | — | 204 | 404, 409 |
| `/api/v1/episodes/{id}/sources/{sourceId}/retry` | POST | 10.5.5-T1 | — | 202 | 409, 429 |
| `/api/v1/episodes/{id}/fact-sheet` | GET / PUT | 10.5.5-T1 | PUT `{ content }` + `If-Match: "fs-…"` | GET: 200 **nullable** `FactSheetDto?` (+ `ETag` when a row exists — no `fs-<id>` with a null body); PUT: 200 `FactSheetSummaryDto` + new `ETag` | GET: 403 only; PUT: 400 `fact_sheet_invalid_content`, 409 `episode_invalid_state`, 412 `fact_sheet_conflict`, 428 `if_match_required` |
| `/api/v1/episodes/{id}/fact-sheet/generate` | POST | 10.5.5-T1 | `Idempotency-Key` | 202 | 409 `episode_invalid_state` / `fact_sheet_sources_insufficient` / `fact_sheet_generation_in_progress`, 429 `spend_cap_reached` (+ 422 `idempotency_key_reused` from the shared idempotency middleware, 10.2.3-T1) |
| `/api/v1/episodes/{id}/fact-sheet/approve` | POST | 10.5.5-T1 | `If-Match` | 200 + `ETag` | 409 `episode_invalid_state` / `story_sources_required` / `fact_sheet_sources_insufficient`, 412, 428 |
| `/api/v1/episodes/{id}/sources/find` | POST | 10.5.8-T1 | `{ query? }` + `Idempotency-Key` | 202 | 400 `validation_failed` / `idempotency_key_required`, 404 `feature_disabled`, 409 `episode_invalid_state` / `find_sources_in_progress`, 422 `idempotency_key_reused`, 429 `spend_cap_reached` |
| `/api/v1/episodes/{id}/sources/candidates` | GET | 10.5.8-T1 | — | 200 `SourceCandidateDto[]` | 404 |
| `/api/v1/episodes/{id}/sources/candidates/{candidateId}/add` | POST | 10.5.8-T1 | — | 201 | 404, 409 `source_*` / `source_candidate_not_proposed`, 429 |
| `/api/v1/episodes/{id}/sources/candidates/{candidateId}/dismiss` | POST | 10.5.8-T1 | — | 204 | 404 |
| `/api/v1/series/{id}/bible` | GET / PUT | 10.5.7-T1 | PUT `{ content }` + `If-Match: "sb-<version>"` | 200 `BibleDto` + `ETag` / 200 `{ version, etag }` | 400 `bible_invalid_content`, 404, 412 `bible_conflict`, 428 |
| `/api/v1/series/{id}/bible/proposal/accept` | POST | 10.5.7-T1 | — | 200 `{ version, etag }` + `ETag` | 409 `bible_proposal_not_pending`, 404 |
| `/api/v1/series/{id}/bible/proposal/dismiss` | POST | 10.5.7-T1 | — | 204 | 409 `bible_proposal_not_pending`, 404 |
| `/api/v1/me` | GET | 10.5.8-T1 | — | 200 (`features[]`) | — |

**Events consumed** (D8, C7): `EpisodeUpdated` → the episode store refetches `GET /episodes/{id}`; the research store refetches `sources` + `fact-sheet` when `change === 'factSheet'` or a `find_sources` run starts/finishes; `resync` (reconnect / tab visible) → the same refetch, coalesced per store within 250 ms (**D8**). `RenderProgress` and `PublicationUpdated` are not used here. Bible: no event (B3) — refetch on navigation and after an approve on the episode page.

**No new routes or DTO fields are introduced by this folder.** Restore (U3) is composed from V2 + V4 because no restore route exists (V3) — recorded as an [OPEN] item in §10.

## 6. UI

### Screens and states

| Screen / component | Route | States | Files (`web/src/app/…`) |
|---|---|---|---|
| Versions panel | `/episodes/:id` (side panel, `?panel=versions&left=<id>&right=<id>` — [DECISION], plan 10 owns the client routes) | loading · empty · list · comparing (`@defer`) · restoring · conflict | `features/episodes/ui/versions-panel/versions-panel.component.ts(/.html)`, `shared/ui/diff-view/diff-view.component.ts` |
| Approve / reopen dialogs | dialog over `/episodes/:id` | confirm · attestation required · submitting · blocked (server 409) | `features/episodes/ui/approve-dialog/approve-dialog.component.ts`, `ui/attestation-block/attestation-block.component.ts`, `ui/reopen-dialog/reopen-dialog.component.ts` |
| Research page | `/episodes/:id/research` | empty (= 200 + null body, never an error) · loading · sources list · note/link form · generating · draft sheet · approved sheet · error · gate closed | `features/episodes/feature/research-page/research-page.component.ts(/.html)`, `ui/source-list/source-row/source-add-form/`, `ui/fact-sheet-panel/`, `ui/fact-row/` |
| Find-sources block | inside the research page | hidden (flag off) · idle · running · results · empty · error | `features/episodes/ui/source-finder-panel/source-finder-panel.component.ts` |
| Bible panel | `/series/:id/bible` | loading · empty bible · loaded · dirty · saving · saved · conflict · proposal pending | `features/series/feature/bible-page/bible-page.component.ts(/.html)`, `ui/bible-section/`, `ui/bible-proposal-banner/` |

Copy keys (vi/en; all new keys live in `web/src/assets/i18n/episodes/{vi,en}.json` — and `series/{vi,en}.json` for the bible — `../README.md` D10). Keys handed over by 02 are used **verbatim** (column "Source").

| Key | vi | en | Source |
|---|---|---|---|
| `episodes.versions.title` | Phiên bản | Versions | new |
| `episodes.versions.empty` | Chưa có phiên bản nào. | No versions yet. | new |
| `episodes.versions.selectTwo` | Chọn hai phiên bản để so sánh. | Select two versions to compare. | new |
| `episodes.versions.current` | Hiện tại | Current | new |
| `episodes.versions.approved` | Đã duyệt | Approved | new |
| `episodes.versions.words` | {count} từ | {count} words | new |
| `episodes.versions.source.generated` / `.edited` / `.revised` / `.other` | Do máy viết / Bạn sửa / Tự sửa một lần / Khác | Generated / Edited / Auto-corrected once / Other | new (fallback for unknown values) |
| `episodes.versions.diff.summary` | {added} chỗ thêm, {removed} chỗ bớt | {added} added, {removed} removed | new |
| `episodes.versions.diff.added` / `.removed` | Thêm / Bớt | Added / Removed | new |
| `episodes.versions.restore` | Khôi phục thành phiên bản mới | Restore as new version | new |
| `episodes.versions.restoreConfirm.title` | Tạo phiên bản mới từ bản cũ? | Create a new version from the old one? | new |
| `episodes.versions.restoreConfirm.body` | Bản cũ vẫn được giữ. Kịch bản hiện tại sẽ thay bằng nội dung của bản này. | The old version is kept. The current draft will be replaced with this content. | new |
| `episodes.versions.restored` | Đã tạo phiên bản mới từ bản cũ. | New version created from the old one. | new |
| `episodes.versions.needsReopen` | Kịch bản đã duyệt. Hãy mở lại để khôi phục bản cũ. | The script is approved. Reopen it to restore an old version. | new |
| `episodes.approve.button` | Duyệt kịch bản | Approve script | new |
| `episodes.approve.title` | Duyệt kịch bản này? | Approve this script? | new |
| `episodes.approve.body` | Video sẽ dùng đúng bản này. Muốn sửa sau khi duyệt thì phải mở lại. | The video will use exactly this version. Edits after approval need reopening. | new |
| `episodes.approve.confirm` / `.cancel` | Duyệt / Huỷ | Approve / Cancel | new |
| `episodes.approve.done` | Đã duyệt kịch bản. | Script approved. | new |
| `episodes.approve.attestation.accept` | Tôi đã đọc và xác nhận nội dung trên. | I have read and confirm the statement above. | new |
| `episodes.reopen.button` | Mở lại để sửa | Reopen to edit | new |
| `episodes.reopen.title` / `.body` | Mở lại kịch bản? / Kịch bản quay về bước duyệt. | Reopen the script? / The script goes back to review. | new |
| `episodes.reopen.confirm` / `.done` | Mở lại / Đã mở lại | Reopen / Reopened | new |
| `episodes.research.title` | Nguồn và dữ kiện | Sources and facts | new |
| `episodes.research.sources.add` / `.urlPlaceholder` / `.notePlaceholder` / `.addNote` / `.remove` / `.retry` / `.refresh` | Thêm nguồn / Dán liên kết… / Ghi chú của bạn… / Thêm ghi chú / Xoá / Thử lại / Cập nhật trạng thái | Add source / Paste a link… / Your note… / Add note / Remove / Retry / Refresh status | new |
| `episodes.research.sources.count` | {used}/{max} nguồn | {used}/{max} sources | new |
| `episodes.research.sources.cited` | Đang được dùng | Cited | new |
| `episodes.research.status.pending` / `.fetched` / `.failed` / `.rejected` | Đang tải / Đã tải / Lỗi / Bị chặn | Pending / Fetched / Failed / Blocked | new |
| `episodes.research.gate.writeStoryBlocked` | Cần duyệt bảng dữ kiện với ít nhất 2 nguồn đã tải trước khi viết kịch bản. | Approve the fact sheet with at least 2 fetched sources before writing the story. | new |
| `episodes.research.confidence.high` / `.medium` / `.low` | Chắc chắn / Vừa / Cần kiểm chứng | High / Medium / Needs checking | new |
| `factSheet.empty` | Chưa có bảng dữ kiện — thêm nguồn rồi tạo bảng. | No fact sheet yet — add sources, then build the sheet. | 02.5.3 §6 ✅ |
| `factSheet.generate` / `.generating` / `.approve` / `.edit` | Tạo bảng dữ kiện / Đang tạo… / Duyệt bảng dữ kiện / Sửa | Build fact sheet / Building… / Approve fact sheet / Edit | 02.5.3 §6 ✅ |
| `factSheet.userNoteLabel` | ghi chú của bạn | your note | 02.5.3 §6 ✅ |
| `factSheet.overlapWarning` | Dữ kiện này gần như chép nguyên văn nguồn. | This fact is nearly verbatim from the source. | 02.5.3 §6 ✅ |
| `factSheet.reopenStoryHint` | Sửa bảng dữ kiện sau khi duyệt sẽ cần mở lại kịch bản. | Editing the sheet after approval needs reopening the script. | 02.5.3 §6 ✅ |
| `sources.find.button` | Tìm nguồn giúp tôi | Find sources for me | 02.7 §6 ✅ |
| `sources.find.running` | Đang tìm nguồn… | Searching for sources… | 02.7 §6 ✅ |
| `sources.find.empty` | Không tìm thấy nguồn phù hợp trên các trang được phép. Hãy dán liên kết bạn tự tìm. | No suitable sources on the allowed sites. Paste links you found yourself. | 02.7 §6 ✅ |
| `sources.find.note` | Chỉ là gợi ý — hãy tự kiểm tra từng nguồn trước khi thêm. | Suggestions only — check each source before adding it. | 02.7 §6 ✅ |
| `sources.find.add` / `.dismiss` / `.open` | Thêm / Bỏ qua / Mở | Add / Dismiss / Open | new |
| `series.bible.title` | Bộ nhớ của bộ truyện | Series bible | 02.6.3 §6 ✅ |
| `series.bible.empty` | Truyện này chưa ghi nhớ gì. Nó sẽ tự đầy lên sau mỗi tập được duyệt. | Your series remembers nothing yet. It fills up as episodes are approved. | 02.6.3 §6 ✅ |
| `series.bible.narrator` / `.characters` / `.places` / `.rules` / `.summaries` | Người kể / Nhân vật định kỳ / Địa điểm / Quy tắc / Tóm tắt các tập | Narrator / Recurring characters / Places / Rules / Past episode summaries | 02.6.3 §6 ✅ |
| `series.bible.saved` | Đã lưu | Saved | 02.6.3 §6 ✅ |
| `series.bible.error.conflict` | Ai đó vừa thay đổi bộ nhớ này. Tải lại rồi lưu lại. | Someone changed this bible. Reload and save again. | 02.6.3 §6 ✅ |
| `series.bible.error.limits` + `series.bible.field.{too_many,too_long,duplicate,unknown_episode}` | Vượt giới hạn: {field} | Over the limit: {field} | 02.6.3 §6 ✅ |
| `series.bible.proposal.banner` | Có {n} thay đổi đề xuất từ tập “{title}” | {n} suggested changes from “{title}” | 02.6.3 §6 ✅ |
| `series.bible.proposal.accept` / `.dismiss` / `.none` / `.autoAccepted` | Chấp nhận / Bỏ qua / Không còn đề xuất nào / Tự động cập nhật (autopilot) | Accept / Dismiss / No proposal is pending / Updated automatically (autopilot) | 02.6.3 §6 ✅ |
| `errors.<code>` | per 09 §5.4 + 02 D8 | — | `../README.md` D9 ✅ |
| `errors.find_sources_in_progress` / `errors.find_sources_generation_failed` / `errors.source_blocked_by_robots` / `errors.source_unreachable` / `errors.source_too_large` / `errors.source_unsupported_type` / `errors.source_no_text` / `errors.story_attestation_required` / `errors.story_fact_sheet_outdated` / `errors.story_sources_required` / `errors.story_too_similar_to_source` / `errors.story_moderated` / `errors.fact_sheet_sources_insufficient` / `errors.fact_sheet_generation_in_progress` / `errors.bible_proposal_not_pending`, `errors.episode_invalid_state`, `errors.fact_sheet_invalid_content`, `errors.fact_sheet_conflict`, `errors.story_version_conflict`, `errors.bible_conflict`, `errors.source_url_invalid`, `errors.source_domain_not_allowed`, `errors.source_duplicate`, `errors.source_limit_reached`, `errors.source_fetch_limit_reached`, `errors.find_sources_in_progress`, `errors.find_sources_generation_failed` | mapped from the catalogue | — | D9 ✅ |
| `error.find_sources_in_progress` / `error.find_sources_generation_failed`, `factSheet.errors.{code}` | aliases of the two keys above (same text) | — | 02.7 §6, 02.5.3 §6 (⚠️ see §2 discrepancy note) |

Remaining new keys are enumerated per task in `## Design`/`## Steps` of each task file and live in the same two scope files: `episodes.research.{sources.empty, sources.deleteCitedConfirm, sources.truncated, facts.empty, facts.add, facts.delete, facts.statement, facts.requiresSource}` and `episodes.research.sources.{count,cited}` (10.5.5-T1); `sources.find.{title,queryLabel,queryPlaceholder,add,dismiss,open,added,dismissed,refresh,run}` (10.5.8-T1); `series.bible.{save,saving,reload,addCharacter,addPlace,addRule,addSummary,remove,narrator.name,narrator.persona,narrator.voiceNotes,rule.text,character.firstEpisode,limitReached,summary.title,summary.body,conflict.reload,proposal.bannerNoTitle,proposal.more,op.summary,op.character,op.place,op.rule,op.other}` (10.5.7-T1).

### Accessibility (D16)

- Versions panel: `role="list"`/`listitem` rows, each row a labelled button toggling into left/right slot; the diff region is `aria-live="off"` with a summary sentence (`episodes.versions.diff.summary`) in an `aria-live="polite"` status line; "Restore" is a real `<button>` inside the confirm dialog with focus trapped and restored.
- Diff is **never colour-only**: added/removed hunks carry a leading `+`/`−` glyph plus `aria-label` text; `prefers-reduced-motion` disables the panel slide.
- Approve dialog: the attestation block is a `<fieldset>` with a `<legend>`; the rendered attestation text is selectable text (no `innerHTML`); the confirm button is disabled until the checkbox is set when `attestation.required`; `ConfirmDialog` traps focus and returns it to the trigger.
- Research page: source status chips carry icon + text; the add-source input has a `<label>`; errors are linked with `aria-describedby`; the "Refresh status" button has `aria-label`; the gate message is `role="status"`.
- Bible page: the proposal banner is `role="status"` and announces `series.bible.proposal.banner`; accept/dismiss are focusable in that order; per-field errors linked with `aria-describedby`; sections are `<fieldset>`/`<legend>`; remove buttons carry `aria-label` with the entry name/index.
- All: ≥ 24 px targets, visible focus ≥ 3:1, both themes ≥ 4.5:1, 360 px without horizontal scroll, keyboard-only paths for versions diff selection, fact editing and bible sections; axe (WCAG 2.1 AA) in every e2e spec.

**Design (owner rule 2026-09-25).** `10.5.4-T0` is the folder's design gate (D19): wireframes + hi-fi mockups for the versions panel, approve/reopen dialogs + attestation, the research page, the find-sources block and the series bible panel, at 360/768/1280, dark + light, vi + en, with focus order/keyboard path/`aria-live` annotations. **Design link (recorded 2026-10-02):** review deck [`design/ui/index.html`](../../../../design/ui/index.html) → screen 05 [`screens/05-versions-research.html`](../../../../design/ui/screens/05-versions-research.html): versions [`#f-versions-list`](../../../../design/ui/screens/05-versions-research.html#f-versions-list) · [`#f-versions-diff`](../../../../design/ui/screens/05-versions-research.html#f-versions-diff) · [`#f-versions-restore`](../../../../design/ui/screens/05-versions-research.html#f-versions-restore) · [`#f-versions-conflict`](../../../../design/ui/screens/05-versions-research.html#f-versions-conflict); approve/reopen [`#f-approve-confirm`](../../../../design/ui/screens/05-versions-research.html#f-approve-confirm) · [`#f-approve-attestation`](../../../../design/ui/screens/05-versions-research.html#f-approve-attestation) · [`#f-approve-blocked`](../../../../design/ui/screens/05-versions-research.html#f-approve-blocked) · [`#f-reopen-confirm`](../../../../design/ui/screens/05-versions-research.html#f-reopen-confirm); research [`#f-research-empty`](../../../../design/ui/screens/05-versions-research.html#f-research-empty) · [`#f-research-sources`](../../../../design/ui/screens/05-versions-research.html#f-research-sources) · [`#f-research-draft`](../../../../design/ui/screens/05-versions-research.html#f-research-draft) · [`#f-research-gate-closed`](../../../../design/ui/screens/05-versions-research.html#f-research-gate-closed); find sources [`#f-find-hidden`](../../../../design/ui/screens/05-versions-research.html#f-find-hidden) · [`#f-find-results`](../../../../design/ui/screens/05-versions-research.html#f-find-results); bible [`#f-bible-loaded`](../../../../design/ui/screens/05-versions-research.html#f-bible-loaded) · [`#f-bible-proposal`](../../../../design/ui/screens/05-versions-research.html#f-bible-proposal); the deck also carries this folder's own coverage matrix frame [`#f-coverage`](../../../../design/ui/screens/05-versions-research.html#f-coverage). Full state → frame matrix: `10.5.4-T0-ui-design.md` §Outputs. **Owner approval: approved by the owner 2026-10-02** (deck v2 "Phòng thu", commit `192cc73`). Every task below lists `10.5.4-T0` as a hard `Depends on`.

## 7. Jobs and integrations

- **Realtime** (D8): each store subscribes through `RealtimeService.on('EpisodeUpdated', episodeId)` (unsubscribe in `DestroyRef`). `ResearchStore` refetches `sources` + `fact-sheet` when the payload says `change === 'factSheet'` or the run kind is `find_sources`; `VersionsStore` refetches the version list only while the panel is open; the episode store refetch updates `currentStory`/`generation` (10.5.3-T1). The bible panel does not subscribe (B3); when `proposal !== null` it issues exactly one `GET /api/v1/episodes/{proposedFromEpisodeId}` for the banner title (404/403 → the `series.bible.proposal.bannerNoTitle` fallback, P5/WR16).
- **No polling in this folder** (D17: the only wave-1 poll is the autopilot run log). Source fetch completion is observed through `EpisodeUpdated` + `resync` + the explicit "Refresh status" button. ⚠️ [OPEN] whether 02.5.1 outboxes `EpisodeUpdated` on source fetch completion is not confirmed in `02.5.1/plan.md`; if it does not, the manual refresh is the only path in P and the panel says "Refresh to update" — see §10.
- **No timers without `DestroyRef` cleanup**; `visible-poll` is not used here (D13/D17).
- **Finder integration** (02.7.2-T2): the run is a Worker job behind the `source_finder` flag; the SPA only starts it (202 + `Idempotency-Key`), lists candidates and calls add/dismiss. `EpisodeUpdated` on start/finish drives the "Searching…" state; `generation.kind === 'find_sources'` from the episode detail is the authoritative in-progress signal, so the block stays correct after a reload.
- **Fact-sheet generation** (02.5.3-T2) is a run too: `POST …/fact-sheet/generate` → 202 → progress from `EpisodeUpdated`/`generation`; no client polling.
- **Idempotency** (10.2.3-T1): one key per user intent for `…/fact-sheet/generate` and `…/sources/find`; the key is minted when the user clicks and **regenerated** after a 422 `idempotency_key_reused` before an explicit retry.
- No background fetch, no service worker, no `setInterval` on hidden tabs.

## 8. Security and privacy

- **No story, source or fact content in logs or client storage** (NFR-10-5, 02 D19, `../README.md` D2): the stores keep data in memory only; `localStorage`/`sessionStorage` are used solely by `core/prefs` for the UI language (lint-enforced). Analytics/console: no `console.log` of payloads; the interceptor never logs bodies.
- **No `innerHTML`/`bypassSecurityTrust*` anywhere** — story text, source titles, fact statements, attestation text and bible content are rendered with interpolation; the source URL is bound through `[href]` with `rel="noopener noreferrer" target="_blank"` and Angular's URL sanitizer; the diff renders text nodes only. A source `title` from a fetched page is untrusted content (it can contain markup) and is always interpolated (D2 bans `[innerHTML]`; CSP + Trusted Types as the second line, D4).
- **Tokens**: all calls go through the 10.2.1-T1 interceptor (`Authorization` header, in-memory token); nothing here touches the access token, and no request body/URL carries it. Same-origin relative URLs (D3) — no CORS, no credentials-free cross-origin calls.
- **Open redirect**: source/candidate links are external but rendered as plain `<a href>` with the sanitizer; no `returnUrl` handling in this folder.
- **Idempotency and ETag**: every mutating call either carries an `Idempotency-Key` (generate/find) or an `If-Match` (`PUT story`, approve, reopen, `PUT fact-sheet`, approve fact sheet, `PUT bible`). A 412 opens `ConflictDialog` — never a silent overwrite (D9); a 428 is treated as a client bug (console + generic message).
- **Retry-After** honoured via `RetryCountdown`; the action stays disabled until it reaches 0 (D9).
- **XSS/CSRF**: no cookies are read or written by JS (`withCredentials` not needed, D6); the refresh cookie is `HttpOnly; SameSite=Strict` (C1). No form posts outside Angular's HttpClient.
- **Privacy**: source URLs, titles and note text are user content — nothing is written to the URL bar (only ids in query params: `?panel=versions&left=&right=`), nothing to telemetry; the bible panel shows only the user's own series (API enforces tenancy, B1 404 rule).
- **P-only metering**: no price, credit or balance text anywhere (D14/F12) — the `cost-line` slot is rendered empty; a test asserts the absence of `credit`/`cost` strings in these screens' templates.
- **Deletion impact**: none (no local persistence; stores reset through `SESSION_SCOPED` on `SessionEnded`, D6 — each of the five stores implements `reset()`).

## 9. Test plan

**Vitest store tests** (jsdom, zoneless TestBed, hand-written fakes in `features/*/testing/`, no mocking library — D15):

| Store | Cases |
|---|---|
| `VersionsStore` | list success → `versions` ordered by the server; empty list → `empty` state; `AppError` mapping (404 → `ErrorState`, network → `network_error`); `select()` fetches only the missing side (two calls, never four); `restoreAsNewVersion` sends the version's `content` with the **current** ETag and sets `dirty=false` on success; 412 → `pendingConflict` and the draft unchanged; 409 → `needsReopen`; 428 → no user-visible error; `reset()` clears everything |
| `ResearchStore` | add url → POST body `{ url }`, row added from the 201; add note → `{ noteText }` and `CharCounter` limit 3,000; 400 `source_domain_not_allowed` → field error, list unchanged; 409 `source_duplicate` → pointer to the existing row; 409 `source_limit_reached` (10) → add disabled; 429 → `retryAfterSec` exposed; retry/remove success and 409; `gate` closed with 0/1 fetched url sources, closed when the sheet is `draft`, open with 2 cited fetched sources; `low` confidence fact does not close the gate; `save()` preserves `confidence` for untouched facts and defaults a new fact to `high`; 412 → `pendingConflict` + input kept in memory; 400 `fact_sheet_invalid_content` → `bindFieldErrors`; `EpisodeUpdated` with `change='factSheet'` → refetch coalesced; two events in 100 ms → one refetch |
| `ApprovalStore` | approve without attestation when `required` → button disabled / 400 mapped; approve sends `{ accepted, textVersion }` and the current ETag; 409 `story_fact_sheet_outdated` → message + link to research; 412 → `ConflictDialog` data; reopen success resets the approved flag; 409 → refetch episode |
| `SourceFinderStore` | flag off → `hasFeature=false`, no calls; 202 → `running` true, candidates reloaded on `EpisodeUpdated`; 409 `find_sources_in_progress`; 404 `feature_disabled` mid-session → hide + `/me` refetch; 422 on retry → new key sent (fake asserts two different keys); add/dismiss update the row status; nothing pre-ticked (assert no checkbox is checked) |
| `BibleStore` | load → sections; dirty tracking (typed form or signal edits) → `dirty`; save sends `If-Match` = loaded `etag` and rebases on the response `{ version, etag }`; 412 → `conflict` state and the edited content kept; 400 → `fieldErrors` keyed like `recurring_characters` / `episode_summaries[2].episode_id`; accept → `bible` refreshed + `proposal=null`; 409 → `proposal.none`; dismiss → 204 path |

**Component tests**: `VersionsPanel` (two selections → `DiffView` rendered with `@defer` resolved; restore button disabled while `restoring`; conflict dialog shown on 412), `DiffView` (added/removed counts, leading `+`/`−`, no markup injection with `<script>` text input), `ApproveDialog` (attestation gate, `cost-line` slot empty), `FactSheetPanel` (read-only when approved, Edit creates a draft), `BibleProposalBanner` (counts from `preview`, accept/dismiss order), `SourceFinderPanel` (hidden when the flag is off).

**Playwright e2e** (owner project, one login per spec file, TOTP helper per F08/D15; axe per D15):

| Spec (`web/e2e/`) | Scenario |
|---|---|
| `episodes-versions.spec.ts` | Generate → edit → save → open the versions panel → compare v1 with v2 (word diff visible, counts match the seeded edit) → Restore v1 → a **new** version v3 exists with v1's text and `source=edited`; axe |
| `episodes-approve-attestation.spec.ts` | True-crime episode without an approved sheet → approve blocked with `story_sources_required`; with an approved sheet + 2 fetched sources → approve requires the attestation tick → approve → status chip `story_approved` → Reopen → back to `story_review`; axe |
| `episodes-research.spec.ts` | Add a link (status `pending` → `fetched`: the e2e stack has no fetcher profile and no internet, so the fetched state is produced by a `page.route` response on `GET …/sources` — 10 WT-W7) + a note; add a blocked-domain link → inline `source_domain_not_allowed`; build the fact sheet → edit a fact, add one, delete one → approve → the story button unlocks; second tab: 412 conflict path; axe |
| `episodes-find-sources.spec.ts` | With the flag on: "Find sources" → "Searching…" → candidates listed with site names → Add one (appears in the sources list) and Dismiss one; nothing pre-ticked; with the flag off the block is absent; axe |
| `series-bible.spec.ts` | Open `/series/:id/bible`, edit a character, save (saved indicator), stale-ETag path in a second tab → conflict copy; approve a story → reopen the bible → the proposal banner with counts → Accept → content updated, banner gone; axe |

**Gate 10-W1-studio** (`../README.md` §7) is exercised by `episodes-research.spec.ts` + `episodes-approve-attestation.spec.ts` + `episodes-versions.spec.ts`; both languages are covered by running the vi spec file in `en` (`locale` fixture) and asserting the key set equality (10.2.5-T1 CI check).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 10.5.4-T1 | Versions panel, word diff, restore as new version | M (1.0) | 10.5.3-T1, 02.3.4-T2 |
| 10.5.4-T0 | UI design (versions/diff, approve & reopen, research page, find sources, bible) | M (1.0) | 10.1.3-T1, 09.5.3-T1 (soft) |
| 10.5.6-T1 | Approve / reopen dialogs + true-crime attestation | S (0.5) | 10.5.3-T1, 02.3.2-T1, 02.3.3-T1 |
| 10.5.5-T1 | Research page: sources, fetch status, fact-sheet editor, approve | L (2.0) | 10.5.3-T1, 02.5.1-T1, 02.5.3-T2, 02.5.4-T1 |
| 10.5.7-T1 | Series bible panel + proposal diff | M (1.0) | 10.4.2-T1, 10.5.4-T1, 02.6.3-T1, 02.6.3-T2 |
| 10.5.8-T1 | Find-sources suggestions (flag `source_finder`) | S (0.5) | 10.5.5-T1, 02.7.2-T2 |

Sum 6.0 d (1.0 `10.5.4-T0` design, D19, + 5.0), inside the 12.25 d + 1.0 design of epic 10.5 (`../README.md` §9 — unchanged, no re-estimate). Order of implementation: 10.5.4-T1 → 10.5.6-T1 → 10.5.5-T1 → 10.5.7-T1 → 10.5.8-T1 (10.5.8-T1 needs the research page; per WR5/§8.1 **10.5.7-T1 now depends on 10.5.4-T1** for `DiffView`, so it can no longer run in parallel with 10.5.5-T1). Edges: 10.5.4-T1 → 10.5.7-T1 and 10.5.5-T1 → 10.5.8-T1 (§8.1).

**Re-estimate: none.** Each task fits its §8.1 size; 10.5.5-T1 is the only L and carries four sub-areas (sources, status, fact sheet, gate) that the backend contracts already split.

**[OPEN] items raised by this folder** (for the TL at DoR; no file outside this folder is edited):
1. **No restore route** (V3): "Restore as new version" is composed client-side from `GET …/story/versions/{versionId}` + `PUT …/story`. If a server-side restore is wanted later, it is a 02 CR (a new route + a `source` value such as `restored`) — the client code path is unchanged.
2. **Error-key shape conflict**: 02.5.3 §6 hands over `factSheet.errors.{code}` and 02.7 §6 `error.<code>`, while `../README.md` D9 mandates `errors.<code>`. This folder ships both (aliases with identical text). Request: converge on D9 in the 02 files (02 CR).
3. **Source-fetch completion event**: `02.5.1/plan.md` does not state whether the API outboxes `EpisodeUpdated` when a fetch task completes. If it does not, P shows "Refresh to update" (manual). Request: state it in 02.5.1 §5 or add the outbox row. **Status after 10.5.5-T1 (Session 40, PR #TBD): still open** — the shipped page refetches sources + sheet on the payload-free coalesced episode event, so it is polling-free but fetch-completion-blind; the decision stays here (owner 02.5.1), the web receiver is 10.5.8-T1.
4. **`source_finder` flag delivery**: `../README.md` D14/O1 + 10 CR-10 add `features[]` to `GET /me`, but `09.3.4-T1` is not yet updated in this repo state. 10.5.8-T1 mitigates: absent `features` array → treat as flag **off** and hide the block (never an error).
5. **Cross-folder interface with the editor folder (`10.5.1-episodes-editor/`) — settled 2026-09-24**: 10.5.5-T1 provides `EPISODE_GATE` (`InjectionToken<Signal<GateState>>`, `GateState = 'not_applicable' | 'sheet_not_approved' | 'sources_insufficient' | 'open'`) from **route-level `providers`** on `/episodes/:id` (in `features/episodes/episodes.routes.ts`, beside the editor and research children) plus the key `episodes.research.gate.writeStoryBlocked`. The consumer injects it **optionally** (`inject(EPISODE_GATE, { optional: true })`, default `'open'`), so a missing provider degrades to the server's 409 `fact_sheet_required`; 10.5.2-T1 does not re-derive the F13 rule and does not import this feature folder (its `Depends on` cell does not name 10.5.5-T1). Accepted and applied by that folder's owner (ack 2026-09-24). **As built (10.5.5-T1, Session 40, PR #TBD; 10 CR-55):** the token is provided on the **page components** (`ResearchPageComponent`, `StoryReviewPage`), not on route-level `providers` — a route injector is never destroyed, so a route-scoped gate could not be re-read per episode and the editor's one-shot read would never run again. The consumer contract above is unchanged (`inject(EPISODE_GATE, { optional: true })`, default `'open'`). **Scope**: the gate is episode-scoped, so the create+generate route `/series/:id/episodes/new` (10.5.2-T1) intentionally resolves no token — no episode exists yet, therefore no fact sheet to check — and that route keeps the server's 409 `fact_sheet_required` as its authority.
6. **Cross-folder interface with 10.5.3-T2 (self-review badge)**: 10.5.4-T1 owns the version deep link `?panel=versions&left=<versionId>&right=<versionId>`; the "Automatically corrected once" diff link (10.5.1-episodes-editor plan §10 O10) targets exactly that link, with the `revised` version on the right and its predecessor on the left. No file outside this folder is edited.
7. **Overlap chip threshold** (10.5.5-T1): `FactItem.sourceOverlap` is a score (null when unscored) and the DTO carries no block threshold, so the chip renders whenever the score is non-null with the percentage in a tooltip; a strict client-side gate would need the threshold in the DTO (02 CR). **Status after 10.5.5-T1 (Session 40, PR #TBD): still open** — shipped as the advisory chip only (`fact-row`, copy `factSheet.overlapWarning`, never blocking); the decision belongs to this item (owner TL), the implementation to the next task touching `fact-row` (10.5.8-T1).
8. **Proposing episode title** (10.5.7-T1): `BibleDto.proposal` carries `proposedFromEpisodeId` but no title, while the hand-over copy needs `{ title }`. Per WR16/P5 the page issues one `GET /api/v1/episodes/{proposedFromEpisodeId}` when a proposal exists (never for a proposalless bible) and falls back to `series.bible.proposal.bannerNoTitle` on 404/403; a `title` in `preview` would remove that extra call and the fallback (02 CR).
9. **`ops[].kind` wire casing** (10.5.7-T1): only the C# op names (`AddSummary` / `AddCharacter` / `AddPlace` / `AddRule`) are visible in 02.6.3 §4 U4; the kind→label mapping normalises both spellings and falls back to `series.bible.op.other`, so an unforeseen value degrades to a generic label instead of an empty row.
10. **Per-row vs bulk candidate add** (10.5.8-T1): the high-level 10.5.8 says "the user ticks the ones to add", but the API offers per-candidate add only and 02.7 §6 hands over per-row Add/Dismiss with nothing pre-ticked; a bulk add would be a new 02 route.
11. **`keywords_flagged` reader contract** (10.5.6-T1 + 10.5.10-T1): approve also returns 409 `keywords_flagged` with `errors[] { segment, index, rule }` (03/README.md:202; 03.2.2-T2:22,29-32,39; 03.2 `plan.md:81,89,101`; the catalogue entry lands via 03 CR-9, so `02.3 plan.md:146` omits it) and the frozen key `errors.keywords_flagged` (03.2 `plan.md:101`). 10.5.6-T1 maps the code, never renders `detail`, does not route it through `bindFieldErrors` (the shape is not `Record<string, string[]>`) and hands the array to 10.5.10-T1's single `setKeywordFlags(errors)` entry point, keeping **no** copy (WR6); `keywordFlags`/`videoBlocked` then keep `Approve` and "Make video" disabled. This blocks through a **server gate**, so C11 ("quality hints never disable approve") is untouched, and 02.3.4-T1's older "UI disables approve while a blocking hint is visible" wording is superseded by 10 CR-11.

## 11. Rollout

- **Order inside the epic:** 10.5.3-T1 first (the editor route and stores already exist), then 10.5.4-T1 and 10.5.6-T1 (editor-adjacent, no new route), then 10.5.5-T1 (new lazy route `/episodes/:id/research`, linked from the editor's tab strip), then 10.5.7-T1 (`/series/:id/bible`, linked from the series page) and 10.5.8-T1 (inside the research page, flag-gated). For a **true-crime** series the create flow lands directly on the research route without calling generate (10.5.2-T1, owner ack 2026-09-24) — the page is therefore the post-creation landing screen for that topic and must render its zero-source empty state with no error and no redirect.
- **Flags:** `source_finder` gates only 10.5.8-T1; the block is invisible when off (and mid-session 404 `feature_disabled` hides it + refetches `/me`). The `youtube_publish` flag is untouched. The research page itself is **not** flag-gated — non-true-crime episodes see it as optional research (high-level 10.5.5).
- **Lazy loading and budget:** each route stays behind `loadComponent`; `DiffView` and the fact-sheet panel load through `@defer (on interaction | on viewport)` so the initial bundle of the episode route grows by < 8 kB gzip (`../README.md` D13, NFR-10-2 gate `tools/web/check-gzip-budget.mjs`).
- **Monitoring:** no new metric; the existing client error path (`AppErrorMapper` + trace id) covers these calls. e2e artefacts (trace/video) on failure per D15.
- **Rollback:** pure frontend — reverting the release removes the panels; no data migration, no server state. A half-shipped state is safe because every screen is read-through + ETag-protected writes.
- **Definition of Done for the folder:** all five tasks' acceptance checkboxes green; both languages; axe clean on all five specs; the 10-W1-studio gate scenario passes; `../README.md` §11 DoR signed after the review pass.

## 12. Angular implementation amendment (2026-10-02)

### Design evidence and implementation reconciliation notes

- Static evidence is in the approved deck [`05-versions-research.html`](../../../../design/ui/screens/05-versions-research.html), including versions `#f-versions-*`, approve/reopen `#f-approve-*`/`#f-reopen-*`, research `#f-research-*`, find-sources `#f-find-*`, bible `#f-bible-*`, and the artifact's `#f-coverage` inventory. Shared token evidence is [`tokens.css`](../../../../design/ui/assets/tokens.css).
- Static frames do not prove keyboard selection, focus restore, live-region timing or deferred loading. `DiffView` placeholder/loading/error/truncated states, keyboard comparison at 360 px and disabled/submitting form states follow the locked contract; flag to the owner only if a new visual state is needed (does not reopen the approval or block the task). Audio preview is not a capability of these routes; the deck must not turn any audio-looking affordance into scope.

### Angular boundaries and integration readiness

- Routed containers remain `features/episodes/feature/research-page` and `features/series/feature/bible-page`; panels and rows stay in their feature `ui/`; route-provided stores own orchestration; repositories own generated DTO/header/status mapping. `DiffView` remains the WR4 owner from `10.5.4-T1`, imported directly only at deferred consumers rather than re-exported through an eager feature barrel.
- Every store uses private writable/public readonly signals and replaces arrays/objects. A route/session/entity generation is captured for list/detail pairs, source refreshes, bible title lookup and proposal actions; late results for an old episode/series/selection are discarded and in-flight timers/listeners are destroyed. Typed forms use explicit form→command mappers, async form-state notification and namespaced server errors that preserve local validators and clear on edit/resubmit.
- Integration is blocked on the story/currentStory/version/research/bible owners listed in `integration-readiness.md`. The currently present basic episode detail is not story generation/editor readiness and must not be extended client-side. Nullable fact-sheet bodies remain a successful empty state; ETag/`If-Match`, nullable/absent values, enum fallbacks and root `errors` arrays are mapped explicitly in data access.

No task ID, size, phase, dependency or estimate changes in this amendment.
