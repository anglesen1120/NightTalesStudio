# 02.3 — Checkpoint #1: edit, approve/reopen, true-crime attestation, quality hints

> Refines: `../../02-story-writing.md` §4 epic 02.3 (stories 02.3.1–02.3.4), §6 (mapping).
> Contract: `../README.md` (D3, D4, D8, D9, D12, D13, D21, D22; F11, F12, F13, F25, F29; CR-3, CR-4, CR-7, CR-8, CR-12, CR-32).
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: 02.3.2-T1 doing (Session 21, 2026-10-05); implementation written, gates pending. This is not acceptance or release evidence; task checkboxes remain unchecked until exercised.

## 1. Scope

Checkpoint #1 is the human control point between "a draft exists" and "this exact text is what gets rendered". It owns four stories:

| Story | Title | Priority · Size | Outcome |
|---|---|---|---|
| 02.3.1 | Edit creates a version | Must · S | `PUT /episodes/{id}/story` writes a new `story_versions` row (`source = edited`) under an `If-Match` story ETag; stale ETag → 412 |
| 02.3.2 | Approve locks the script | Must · S | `POST …/story/approve` sets `episodes.approved_story_version_id` and `story_approved`; `POST …/story/reopen` returns to `story_review`; render reads the approved body + keywords |
| 02.3.3 | True-crime attestation | Must · S | true-crime approve requires an explicit attestation tick against the current text version and ≥ 2 cited fetched sources; both recorded in `audit_logs` |
| 02.3.4 | Quality hints | Should · M | `StoryQualityChecker` flags objective + taste issues before approve; story read models expose `currentStory`, the version list and version detail |

**In scope:** edit command + no-op rule + local re-checks; approve/reopen + precondition order; `IStoryRenderInputProvider` hand-off; the true-crime guard on generate, the attestation contract (`ta.v1`) and the F13 source rule; the quality rule table; the three story read models.
**Out of scope:** generation and the job pipeline (02.2.2-T3, D21); prompt/preset content (02.1); the review model call and revision (02.6.1/02.6.2); similarity scoring itself (02.5.6 — this feature *reads* `similarity_score`); fact-sheet authoring/approval (02.5.3/02.5.4); video keywords and the **"Make video"** gate (10.5.3 — the keyword *count* check here is a hint only and never blocks approve); autopilot approval (02.8.2, which calls the same `ApproveStoryCommand` with `ApprovalSource = autopilot`).

## 2. Confirmed facts

Referenced, not restated (README ids):

| Id | Used for |
|---|---|
| D3 | `story.v1` content shape, derived fields (`body`, `keywords`, `word_count`), validator rules |
| D4 | length bands, token rule, segments, ±15 % objective boundary, `max_output_tokens` per step |
| D8 | the full error catalogue: `story_version_conflict` 412, `story_invalid_content` 400, `episode_invalid_state` 409, `story_fact_sheet_outdated` 409, `story_sources_required` 409, `story_attestation_required` 400, `story_too_similar_to_source` 409, `fact_sheet_required` 409, `if_match_required` 428 |
| D9 | story ETag = `"sv-<current_story_version_id>"`; `If-Match` required on PUT story / approve / reopen; 428 missing, 412 stale |
| D12 | `IContentModerator`; `draft` stage; blocking categories `sexual/minors`, `self-harm/instructions`, `illicit/violent`, `hate/threatening` block approval; `moderation_events` |
| D13 | `[Audited]` + action keys `episode.story_version_saved`, `episode.story_approved`, `episode.story_reopened`, `episode.true_crime_attested`; `data` holds ids/versions/scores/codes only |
| D21 | `IStoryWriter`, `StoryDraftValidator`, `StoryQualityChecker`, `IContentModerator`, `SimilarityScorer`, `RunContext`, `ITextClient`, `LengthTargetResolver`; domain events `StoryApproved { EpisodeId, SeriesId, UserId, StoryVersionId, ApprovalSource }`, `StoryReopened`; `IFactSheetGuard.EnsureApprovedAsync` |
| D22 | `story_versions.audit_report` (`audit.v1`) layout; segment addressing `title \| hook \| p:<i> \| ending \| meta`; `model_review.stale` |
| F11 | approve needs `If-Match` (a second tab or a revision must not silently change the approved version) |
| F12 | the story stores `fact_sheet_id`; approve checks it is the current approved sheet |
| F13 | ≥ 2 distinct `fetched` url sources, **each cited by ≥ 1 kept fact**, with distinct `content_sha256` |
| F25 | PUT sends the full `story.v1`; the server recomputes derived fields, local checks and similarity; the model review becomes `stale` with **no paid call** |
| F29 | `story_not_in_review` is dead → `episode_invalid_state` (CR-8) |
| CR-4 | `story_versions` + `parent_version_id`, `generation_run_id`, `fact_sheet_id`, `schema_version` |
| CR-7 | episode detail `currentStory { versionId, version, source, etag, content, qualityHints, auditReport, similarityScore, factSheetId }` |
| CR-12 | `GET /episodes/{id}/story/versions/{versionId}` (this feature confirms it) |

## 3. Domain and data

### 3.1 `story_versions` (owned by 02.2.2-T1; columns this feature writes)

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | |
| `episode_id` | `uuid` FK, `ON DELETE CASCADE` | |
| `version` | `int` | 1-based, sequential per episode |
| `source` | `text` | `generated \| revised \| edited` (02.3.1 writes `edited`) |
| `parent_version_id` | `uuid?` | the previous current version (CR-4) |
| `generation_run_id` | `uuid?` | null for `edited` |
| `fact_sheet_id` | `uuid?` | copied from the run; approve compares it to the current approved sheet (F12) |
| `schema_version` | `text` | `story.v1` |
| `content` | `jsonb` | `story.v1` + backend-added `fact_sources` (D3) |
| `body` | `text` | derived (D3) |
| `keywords` | `jsonb` | ordered `string[]` (D3) |
| `word_count` | `int` | D4 token rule |
| `audit_report` | `jsonb` | `audit.v1` (D22) |
| `similarity_score` | `numeric(6,4)?` | written by the job / recomputed on edit (F25) |
| `created_by` | `uuid` | |
| `created_at` | `timestamptz` | |

Indexes/constraints: `UNIQUE (episode_id, version)`, `INDEX (episode_id, version DESC)`. No `story_versions` column is added here (all exist via CR-4). The feature does add the `episodes.approved_at` column below (owner 2026-10-05, 02 CR-32), so the former feature-wide no-migration statement is superseded.

### 3.2 `episodes` columns used

Existing columns: `status` (09 §7), `current_story_version_id uuid?`, `approved_story_version_id uuid?` (02.3.2), `length_tier`, `topic`. **New (02 CR-32, owner 2026-10-05):** `approved_at timestamptz NULL`, added by a generated EF migration in 02.3.2-T1. `Episode.ApproveStory` sets it; `Episode.ReopenStory` clears it. `ApprovalSource { User, Autopilot }` is a Domain `Stories` enum because Domain approval events consume it; the Application command accepts the same enum.

### 3.3 Attestation: audit, not a column [DECISION]

Story 02.3.3 requires the tick to be stored in `audit_logs` with the story version id. The `audit_logs` row (`episode.true_crime_attested`, D13, CR-3) **is** the record; no `attestation` column is added to `story_versions`. The request carries the tick, the handler validates it against the configured text version and audits it in the same transaction as the approval. Rationale: an attestation is an event with an author and a time, not version state; a column would be overwritten by a later reopen/approve and lose the history the audit table keeps for free.

### 3.4 Attestation text [DECISION, ✅ T29]

Text version `ta.v1`, two languages, embedded resources (same mechanism as D5): files `prompts/shared/attestation.ta.v1.vi.md` and `prompts/shared/attestation.ta.v1.en.md`, with manifest keys **relative to `prompts/`** (`shared/attestation.ta.v1.vi.md`, README §10 T27). Content (vi/en) covers the three clauses of story 02.3.3: (1) facts are supported by the listed sources; (2) no private person is accused of something the sources do not establish; (3) victims are treated respectfully. `Story:TrueCrime:AttestationVersion` (default `ta.v1`) is the only accepted value; an accepted tick whose version differs from the current one is rejected (`story_attestation_required`), so a wording change invalidates old ticks. ✅ T29 (resolved): both files join the D5 catalog and manifest; a wording change is a **new version `ta.v2`** and stored ticks keep their own version; delivery is `currentStory.attestation { required, textVersion, text, acceptedAt }` (extends CR-7) with no extra route.

### 3.5 Quality rules and report

`StoryQualityChecker.Check(StoryContentV1 content, StoryCheckContext ctx) → QualityReport` (D21, Domain `Stories/Quality/`). `QualityReport(IReadOnlyList<QualityIssue> Issues, QualityCounters Counters)`; `QualityIssue(RuleId, Severity, Kind, Segment, Evidence, MessageKey, RepairDirection, Measured, Threshold)` with `Severity = blocking|advisory`, `Kind = objective|taste`, and `Segment` per D22. The checker is **pure** and reuses `KeywordRules` (CR-5) and `LengthTargetResolver` (D21). The rule table is fixed in 02.3.4-T1.

### 3.6 `audit_report` mutation matrix

| Event | `local` | `moderation` | `model_review` | `similarity` | `revision` |
|---|---|---|---|---|---|
| job writes a draft (02.2.2-T3) | set | set | set | set | set |
| 02.3.1 edit | recomputed | recomputed (free, D12); on failure the previous verdict is kept with `stale = true` | scores kept, `stale = true` | recomputed (free) | carried over |
| 02.3.2 approve | untouched | re-run once when the entire report or block is absent, malformed, stale or unavailable; fresh nonblocking verdict commits with successful approval; rejected fresh verdict write-back rolls back with the failed Unit of Work | untouched | untouched | untouched |

`moderation.stale` is a boolean **inside** the existing `audit_report.moderation` block (mirroring `model_review.stale`), set by 02.3.1 when the moderator check could not run. This JSON property adds no column; it does not eliminate the separate `episodes.approved_at` migration (02 CR-32). Approve clears stale/unavailable markers only with a successful fresh verdict and atomic commit (§4.2 step 7). Edit/approve moderation runs outside `ModerationScope`, currently unmetered; keep the existing warning, with metering owned by plan 12 / 12.1.x (owner 2026-10-05). No synthetic generation run or reservation is created.

## 4. Use cases

All commands return `ErrorOr<T>`; ProblemDetails `code` per D8. ETags are D9 (`sv-<versionId>`).

### 4.1 `EditStoryCommand(Guid EpisodeId, StoryContentV1 Content, string IfMatch)` → `ErrorOr<StoryVersionDto>`

1. Load the episode + the current story version (tracked).
2. **State**: editable only in `draft` or `story_review`. `story_approved` → 409 `episode_invalid_state` (the UI offers reopen); `story_generating`, `rendering` and every other status → 409 `episode_invalid_state`.
3. **If-Match**: absent → 428 `if_match_required`; `sv-<guid>` ≠ `current_story_version_id` → 412 `story_version_conflict`.
4. **Validate**: `StoryDraftValidator.Validate(Content, StoryRules)` (D3). Failure → 400 `story_invalid_content` with per-field `errors[]`; nothing is written.
5. **No-op rule**: canonicalise (NFC, trim, `\n` normalise) `body`, `keywords`, `fact_refs` and the metadata block; if all are equal to the current version → return the current version unchanged (200, **same ETag**, no row, no audit). Editing and saving without a change must not create history.
6. **Recompute** (F25): derived fields; `audit_report.local` via `StoryQualityChecker`; `audit_report.similarity` via `SimilarityScorer` (local, free); `IContentModerator` on the edited narration + metadata (D12 `draft` stage — free, not a paid model call, so "no paid call on edit" holds); `audit_report.model_review.stale = true` keeping the previous scores.
7. **Write**: `story_versions` (`source = edited`, `parent_version_id` = previous current, `version` = max + 1, `fact_sheet_id` copied); set `episodes.current_story_version_id`; audit `episode.story_version_saved` (`data { episode_id, version_id, version }`); outbox `EpisodeUpdated` (CR-7).
8. No domain event: D21 lists only `StoryApproved`/`StoryReopened` for 02.3; edit is an `EpisodeUpdated` only [DECISION].

### 4.2 `ApproveStoryCommand(Guid EpisodeId, string IfMatch, AttestationInput? Attestation)` → `ErrorOr<ApprovedStoryDto>`

Precondition order is observable and tested in this order. Owner-scoped loading precedes it (unknown/foreign episode → 404). Rows 3–6 describe the real gate owned by 02.3.3-T1 and its prerequisites; 02.3.2-T1 explicitly registers `NullStoryApprovalGate` (success, accepted text version null), which implements no fact-sheet, source, similarity or attestation checks. This task establishes the gate seam/order, not those real checks.

| # | Check | Failure |
|---|---|---|
| 1 | status is `story_review`; already `story_approved` **with a matching If-Match** → 200 no-op (idempotent re-approve) | 409 `episode_invalid_state` |
| 2 | `If-Match` present and equal to `sv-<current_story_version_id>` | 428 `if_match_required` / 412 `story_version_conflict` |
| 3 | **true-crime only** (`topic = true_crime`): the version's `fact_sheet_id` = the episode's currently approved sheet (F12) | 409 `story_fact_sheet_outdated` |
| 4 | **true-crime only**: ≥ 2 distinct `fetched` url sources, each cited by ≥ 1 kept fact, distinct `content_sha256` (F13) | 409 `story_sources_required` |
| 5 | **any topic, whenever the episode has url sources** (02 §5.9 "only true-crime and sourced episodes are checked"; FR-02-10): recompute the score first if it is null (**T31**: `IsScorable` → `Score(...).Score` over the sources corpus only, as in D21 stage 6), then `SimilarityApprovalGuard.Classify(score, options)` (Domain, 02.5.6-T1) must not be `Block` | 409 `story_too_similar_to_source` |
| 6 | **true-crime only**: `Attestation.Accepted = true` and `Attestation.TextVersion` = `Story:TrueCrime:AttestationVersion` | 400 `story_attestation_required` |
| 7 | `audit_report.moderation` has no blocking category (D12). When the entire report or block is absent, malformed, `stale = true` or `unavailable = true`, re-run `IContentModerator` once outside `ModerationScope`, without trusting kept categories as fresh. Re-check failure or a fresh blocking verdict rejects approval (owner R1, 2026-10-05); a fresh nonblocking verdict is written back with stale/unavailable false only with successful approval | 409 `story_moderated` (D8 HTTP table, ✅ T30) |
| 8 | commit | — |

On success: `episodes.approved_story_version_id = current_story_version_id`, `status = story_approved`; audit `episode.story_approved` and, for true crime, `episode.true_crime_attested` (`data { story_version_id, text_version }`); outbox domain event `StoryApproved { EpisodeId, SeriesId, UserId, StoryVersionId, ApprovalSource = user }` (autopilot calls the same handler with `ApprovalSource = autopilot`). The order puts the cheap idempotent checks first, then the deterministic checks — the true-crime fact-sheet and source rules (3–4) and the source-similarity block (5, which applies to any sourced episode per §5.9) — and only then the human action, the attestation tick (6), so a tick is never collected for an approval that cannot proceed; the moderation verdict is refreshed last (7), exactly once before the commit.

Approval also sets `episodes.approved_at = now` (02 CR-32). Fresh moderation write-back, approval, audit and outbox share one Unit of Work: a rejected fresh verdict or later failure rolls the write-back back; the next approve reruns moderation. Only successful approval commits the fresh verdict atomically. `NullStoryApprovalGate` returns no accepted attestation version, so this task writes no real true-crime attestation audit.

### 4.3 `ReopenStoryCommand(Guid EpisodeId, string IfMatch)` → `ErrorOr<EpisodeStatusDto>`

`story_approved` only (else 409 `episode_invalid_state`); `If-Match` required (428/412, D9) against the approved current version. Sets `status = story_review` and clears `approved_story_version_id` and `approved_at` [DECISION — an approval is not reusable after reopen]; audit `episode.story_reopened`; outbox `StoryReopened { EpisodeId, SeriesId, UserId, StoryVersionId }`. Never allowed from `rendering` (409).

### 4.4 Queries (02.3.4-T2)

`GetCurrentStoryQuery` feeds `currentStory` (CR-7); `GetStoryVersionsQuery` → version list; `GetStoryVersionQuery(EpisodeId, VersionId)` → version detail. All are read-only, project `qualityHints` from `audit_report` (local + model issues filtered to hints), and never return another user's episode (09 authorization).

**CR-7 hand-off (dependency merged 2026-10-05):** 02.3.4-T2 (Session 22) merged as #156 (`cf4625c`) and is integrated into the Session 21 worktree. `ApproveStory_ReviewStateCurrentEtag_SetsApprovedVersionAndStatus_ExposesCurrentStoryEtag` now GETs the episode after approve and checks the reviewed `sv-` value in `currentStory.etag`, preserving the episode header/body `ep-<xmin>` ETag. Test written, not executed; GET acceptance remains unchecked and all Session 21 gates pending after the full gate failed (`GATE_RC=2`, 1,224 failures).

### 4.5 `IStoryRenderInputProvider` hand-off (02.3.2-T1)

`IStoryRenderInputProvider.GetAsync(Guid storyVersionId, CancellationToken ct) → ErrorOr<StoryRenderInput>`; `StoryRenderInput(Guid StoryVersionId, string Body, IReadOnlyList<string> Keywords, string Title, string Language, LengthTier Tier, IReadOnlyList<SegmentKeywords> Segments)` (03 CR-13). ✅ T28 (resolved): the port is in D21 owned by 02.3; 09.6.1 resolves `episodes.approved_story_version_id` itself and passes it in — **never** `current_story_version_id` — and fails `episode_invalid_state` when the episode has no approved version (CR-18). The render path (05) passes `Body` as MPT `video_script`; `video_terms` come from `Segments` through the 03 planner and builder (03 D3/D4, CR-13).

## 5. API and events

| Method | Route | Auth | Body | Success | Failures |
|---|---|---|---|---|---|
| PUT | `/api/v1/episodes/{id}/story` | owner | `{ content: <story.v1> }` + `If-Match` | 200 `StoryVersionDto` + `ETag` | 400 `story_invalid_content`, 409 `episode_invalid_state`, 412 `story_version_conflict`, 428 `if_match_required` |
| POST | `/api/v1/episodes/{id}/story/approve` | owner | `{ attestation?: { accepted, textVersion } }` + `If-Match` | 200 `ApprovedStoryDto` | 400 `story_attestation_required`, 409 `episode_invalid_state` / `story_fact_sheet_outdated` / `story_sources_required` / `story_too_similar_to_source` / `story_moderated`, 412, 428 |
| POST | `/api/v1/episodes/{id}/story/reopen` | owner | — + `If-Match` | 200 `{ status, currentStory }` | 409, 412, 428 |
| GET | `/api/v1/episodes/{id}/story/versions` | owner | — | 200 `StoryVersionSummaryDto[]` | 404 |
| GET | `/api/v1/episodes/{id}/story/versions/{versionId}` | owner | — | 200 `StoryVersionDetailDto` | 404 |

The new approve/reopen POST routes omit the early runtime `RequireIfMatchFilter` so owner-scoped 404 and state 409 precede missing/stale-header 428/412 (including idempotent re-approve). OpenAPI still declares the required `If-Match` header. Existing PUT story runtime filtering is unchanged. Both POST responses carry the `sv-` ETag in the body and response header.

`StoryVersionDto { versionId, version, source, etag, body, keywords, wordCount, qualityHints[], similarityScore }`. `StoryVersionSummaryDto { versionId, version, source, createdAt, wordCount, etag, similarityScore, approved }`. `QualityHintDto { ruleId, severity, kind, segment, messageKey, evidence, repairDirection? }`.

```http
PUT /api/v1/episodes/7f1c…/story
If-Match: "sv-3b2a9c10-…"
{ "content": { "title": "…", "hook": { "text": "…", "keywords": ["…"], "fact_refs": [] }, "paragraphs": [ … ], "ending": { … }, "description": "…", "tags": ["…"], "content_warnings": ["crime"], "structure": { … } } }

→ 200
ETag: "sv-9d4e77ab-…"          # the NEW version id: an edit always advances the ETag unless it is a no-op
```
```json
{ "versionId": "9d4e77ab-…", "version": 4, "source": "edited", "etag": "\"sv-9d4e77ab-…\"",
  "body": "…", "keywords": ["…"], "wordCount": 903,
  "qualityHints": [ { "ruleId": "Q-KW-01", "severity": "advisory", "kind": "objective",
                      "segment": "p:3", "messageKey": "quality.keyword_count_per_segment", "evidence": "0 keywords" } ],
  "similarityScore": 0.021 }
```

Events: `StoryApproved`, `StoryReopened` (domain → outbox → `EpisodeUpdated` for the UI). Edit emits `EpisodeUpdated` only. No new SignalR event in P (README §10 T2 precedent).

## 6. UI

Angular 22 feature `web/src/app/features/episodes/` (10.5.x; layout per `../../10-web-ui/README.md` D2, 10 CR-11):

- **Editor** (`story-editor.component.ts`) — one textarea per segment (`title`, `hook`, `p:<i>`, `ending`, `meta`) per CR-9/10.5.3; keyword chips per segment (1–2, from `KeywordRules`); read-only `beat` label; a dirty guard; save sends the full `story.v1` with the current ETag. 412 → a "changed in another tab" banner with reload; 409 `episode_invalid_state` → a "reopen to edit" action.
- **Quality panel** (`quality-hints.component.ts`) — groups `qualityHints` by `kind` (objective first) and `severity`; each row shows the `messageKey` copy + `evidence` + `repairDirection`; a stale model review renders `quality.model_review_stale` with the previous scores greyed out; similarity shows `warn`/`block` states from `Similarity:WarnAbove`/`BlockAbove`.
- **Approve bar** (`story-approve-bar.component.ts`) — the button is disabled **only** by gate-enforced state the client can see: a blocking category in `auditReport.moderation` (the 409 `story_moderated` condition) or an unmet true-crime precondition. `qualityHints` are **warnings only and never disable the button** — the checker is advisory by construction (02.3.4-T1) and a `severity: blocking` hint means "the server enforces this elsewhere" (validator at save, D12, true-crime gate), not "this client must refuse"; the true-crime flow opens the attestation dialog.
- **Attestation dialog** (`attestation-dialog.component.ts`) — renders `currentStory.attestation.text` (vi/en by the UI language) and requires the checkbox before approve; shows the two clauses of the F13 rule when sources are insufficient (`story_sources_required`).
- **Version history** (`story-versions.component.ts`) — the list from `GET …/story/versions`, each row opening the detail route in a diff view; the approved version is badged.
- `currentStory` (CR-7) is the single source for the editor, the bar and the panel; no extra round trip.

The UI contract is one-directional: the API section 5 is authoritative; the components above are named so 10.5.x can bind to them. This plan does not own the UI files (10 owns them) — it owns the payloads.

## 7. Jobs and integrations

- **No new Hangfire job.** Editing and approving are synchronous commands; their outbound provider call is draft moderation (D12), including a one-shot approve re-check when needed. Both run outside `ModerationScope`, currently unmetered (plan 12 / 12.1.x open item, owner 2026-10-05); this is not per-call metering evidence.
- **No paid call on edit** (F25): `ITextClient` is never invoked by 02.3; `model_review` only becomes `stale`.
- **Reads:** `IContentModerator` (D12), `SimilarityScorer` (D21/02.5.6), `IFactSheetGuard` (D21/02.5.3), `LengthTargetResolver` (D21/02.2), `IAuditLogger`/`AuditBehavior` (D13/CR-3), outbox → `EpisodeUpdated`.
- **Renders** consume `IStoryRenderInputProvider` (05.5.1); 05 must never read `current_story_version_id` directly, or a reopened story would be rendered.

## 8. Security and privacy

- **Ownership** on every route (09): a story, its versions and its attestation are only visible to the episode owner; 404 (not 403) for a foreign id.
- **Concurrency** is a security property here, not a nicety: `If-Match` on PUT/approve/reopen prevents a stale tab (or an autopilot run) from approving text the user never saw (F11).
- **Audit `data` carries ids, versions, scores and codes only** — never story text, brief or facts (D13). The attestation audit row holds `story_version_id` and `text_version`.
- **Logging** (D19): ids, codes, rule ids, counts — never story text, titles or descriptions. `moderation_events.excerpt` ≤ 300 chars is the single exception, owned by D12.
- **Attestation is a liability control** (story 02.3.3): the accepted text version is validated server-side against config, so a client cannot approve with an outdated or invented attestation text.
- No new secret, no new external surface; the true-crime gates read only existing tables.

## 9. Test plan

Unit (`tests/NightTales.Application.UnitTests/Features/Stories/`):

| Test | Asserts |
|---|---|
| `EditStory_StaleEtag_Returns412VersionConflict` | F11, D9 |
| `EditStory_MissingIfMatch_Returns428` | D9 |
| `EditStory_ApprovedEpisode_Returns409InvalidState` | story 02.3.2 reopen rule |
| `EditStory_IdenticalContent_DoesNotCreateVersion_AndKeepsEtag` | the no-op rule |
| `EditStory_InvalidContent_Returns400WithFieldErrors` | D3 validator |
| `EditStory_SetsModelReviewStale_AndMakesNoTextCall` | F25 |
| `EditStory_RecomputesLocalIssuesAndSimilarity` | F25, D22 |
| `EditStory_TrueCrime_KeepsFactSheetIdFromPreviousVersion` | F12 |
| `StoryApprovalGate_TrueCrimeFactSheetStale_ReturnsFactSheetOutdated` | F12 |
| `ApproveStory_FewerThanTwoCitedSources_Returns409SourcesRequired` | F13 |
| `ApproveStory_TwoSourcesOneCited_Returns409SourcesRequired` | F13 (a source with no kept fact does not count) |
| `ApproveStory_SameSourceSha256Twice_Returns409SourcesRequired` | F13 (`content_sha256` distinctness) |
| `TrueCrimeSourceRule_SourceCitedOnlyByDroppedFact_Fails` | F13 |
| `ApproveStory_SimilarityAboveBlock_Returns409TooSimilar` | D8 |
| `ApproveStory_TrueCrimeWithoutAttestation_Returns400` | story 02.3.3 |
| `ApproveStory_StaleAttestationVersion_Returns400` | `ta.v1` drift |
| `ApproveStory_Fiction_SkipsAttestationAndTrueCrimeGates` | scope |
| `ApproveStory_BlockingModerationCategory_Returns409Moderated` | D12 |
| `ApproveStory_PreconditionOrder_FactSheetBeforeSourcesBeforeSimilarityBeforeAttestation` | the order table (each code fires before later checks) |
| `ApproveStory_SourcedFictionEpisode_SimilarityAboveBlock_Returns409` | 02 §5.9 (source-scoped, not topic-scoped) |
| `ApproveStory_AlreadyApprovedSameEtag_IsIdempotent200` | |
| `ApproveStory_RaisesStoryApproved_WithApprovalSourceUser` | D21 |
| `ReopenStory_FromApproved_ReturnsToReviewAndClearsApproval` | story 02.3.2 |
| `ReopenStory_WhileRendering_Returns409` | |
| `Check_<Rule>_*` (one test per rule id, 02.3.4-T1) | the full rule table |
| `Check_IdenticalInput_ProducesIdenticalOrderedIssues` | determinism |
| `Check_ReportCarriesNoSourceTextOrUrls` | D22, D19 |
| `GetStoryVersions_OrdersDescending_AndFlagsCurrentAndApproved` | 02.3.4-T2 |

Integration (`tests/NightTales.Api.IntegrationTests/`): `ApproveStory_ThenRenderHandoff_UsesApprovedBodyAndKeywords`, `ApproveThenReopenThenEdit_SecondApproveUsesTheEditedVersion`, `ForeignEpisode_EditReturns404`, `EpisodeDetail_IncludesCurrentStoryBlock`.

Manual: run the true-crime flow on a real episode (sources → sheet → story → edit → hint → attest → approve) and confirm the render receives the approved body/keywords.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 02.3.1-T1 | Edit command with ETag/If-Match | S (0.5) | 02.2.2-T1 |
| 02.3.2-T1 | Approve/reopen commands + render hand-off | S (0.5) | 02.3.1-T1 |
| 02.3.3-T1 | Attestation + sources validation | S (0.5) | 02.3.2-T1, 02.5.3-T2, 02.5.6-T1 |
| 02.3.4-T1 | `StoryQualityChecker` rules | M (1.0) | 02.2.1-T1 |
| 02.3.4-T2 | Story read models | S (0.5) | 02.2.2-T1, 02.3.4-T1 |

**Total 3.0 d** (README §9 keeps this feature at 3.0, up from the high-level 2.5 — read models + `If-Match` on approve, F11/F25). Owner authorized a generated EF migration for nullable `episodes.approved_at` (02 CR-32, 2026-10-05); no new project or flag.

### Open items for the TL

The original items below were decided by the TL on 2026-09-23 (README §10). Current owner decisions and open hand-offs (2026-10-05) follow the table; the former "nothing is open" statement is superseded:

| Item raised | Ruling | Applied in |
|---|---|---|
| Render hand-off port absent from D21 | ✅ **T28** — `IStoryRenderInputProvider` is now in D21 (owner 02.3) with `GetAsync(Guid storyVersionId, ct)`; 09.6.1 resolves `approved_story_version_id` and passes it in, never `current_story_version_id` (CR-18) | plan §4.5, 02.3.2-T1 |
| Attestation text files absent from D5's catalog | ✅ **T29** — `prompts/shared/attestation.ta.v1.{vi,en}.md` join the D5 catalog and manifest (relative keys, T27); a wording change is a new version `ta.v2` and stored ticks keep their version; delivery via `currentStory.attestation` (extends CR-7, no extra route) | plan §3.4, 02.3.3-T1, 02.3.4-T2 |
| Approve blocked by moderation had no HTTP code | ✅ **T30** — 409 `story_moderated`, added to D8's HTTP table | plan §4.2 step 7, §5, 02.3.2-T1 |
| Null `similarity_score` at approve | ✅ **T31** — a null score never blocks by being null; when the episode has url sources, approve recomputes it (pure, local) and then applies the block threshold; with no corpus it stays null | plan §4.2 step 5, 02.3.3-T1 |
| `Story:Quality:RequireFactRefs` default | ✅ **T32** — the flag is **not** used as a trigger: for true crime a paragraph with `beat ≠ setup` and empty `fact_refs` always emits the **advisory** hint `fact_refs_missing`, never blocking | 02.3.4-T1, 02.3.3-T1 |
| "Versions list route missing" / "`source_fetch_limit_reached` missing" | ✅ **T33** — both already exist (00 §8 `GET /episodes/{id}/story/versions`; D8); only the version-detail route goes to CR-12 | 02.3.4-T2 |

Remaining, owned elsewhere: the version-detail route is CR-12 (00 §8). `QualityIssue.Severity = blocking` is a presentation flag meaning "the server enforces this rule elsewhere" — it never gates approve and never disables the UI button (plan §6), so it needs no contract change.

- **Decided, 02 CR-32:** 02.3.2-T1 adds nullable `episodes.approved_at` with a generated EF migration; the newest migration test owns `migrations[^1]`, and the previous owner is demoted.
- **Decided, R1:** absent entire `audit_report` uses the same one-shot moderation refresh as an absent/malformed/stale/unavailable block; failure is closed with 409 `story_moderated`, never an absent-report bypass. Rejected fresh verdict write-back rolls back; the next attempt reruns.
- **Decided, owner 2026-10-05 — API host prerequisite:** API edit/approve uses the real moderator and requires the existing shared `OpenAi:ApiKey` (same Worker key), failing startup when missing. This overrides "no config key" only as a new API requirement for an existing key name; no new key name is introduced. API environment injection in `tools/dev/up.sh` is authorized as an edit only, without stack/dev-DB operations. Host DI/test-host correction and startup verification remain pending after the full gate failed (`GATE_RC=2`); no green-gate claim.
- **Open, plan 12 / 12.1.x:** define metering for edit/approve moderation calls outside `ModerationScope`, without a generation run or reservation. Current calls are unmetered and retain the moderator warning (owner 2026-10-05).
- **Dependency merged; acceptance pending, CR-7 / 02.3.4-T2 (#156, `cf4625c`):** Session 21's approve→GET `currentStory.etag` and episode `ep-<xmin>` assertions are written in `ApproveStory_ReviewStateCurrentEtag_SetsApprovedVersionAndStatus_ExposesCurrentStoryEtag`, not executed or passed; the 02.3.2-T1 acceptance checkbox remains unchecked.

## 11. Rollout

- **Order:** 02.3.1-T1 → 02.3.2-T1 → 02.3.4-T1/T2 (parallel with 02.3.2) → 02.3.3-T1 once 02.5.3-T2 (fact sheet) and 02.5.6-T1 (similarity) land. The explicit `NullStoryApprovalGate` registration remains until 02.3.3-T1 replaces it; no real fact-sheet, source, similarity or attestation gate is delivered by 02.3.2-T1.
- **Migrations:** 02.3.2-T1 adds nullable `episodes.approved_at timestamptz` with a generated EF migration (owner 2026-10-05, 02 CR-32). Existing CR-4 columns (`story_versions.parent_version_id/fact_sheet_id/schema_version`, `audit_report`) remain owned by 02.2.2-T1. The previous feature-wide "no migration" statement is superseded.
- **Flags:** no new flag. `source_finder` (02.7) is unrelated; autopilot approval reuses the same command and is gated by 02.8.3.
- **Rollback:** all four stories are additive. Disabling the true-crime gates (config) is **not** an option — they are the liability control of 02.3.3; rollback means reverting the deploy.
- **Exit evidence for 02-M1-core (not yet exercised by Session 21):** edit → hint → approve → reopen → edit → approve through the API, approved version read by render, and no-op edit leaves no new version. Edit/approve moderation is currently unmetered outside `ModerationScope`; per-call metering is an open plan 12 / 12.1.x requirement, not claimed intact.
