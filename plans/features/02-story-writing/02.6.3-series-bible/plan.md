# 02.6.3 — Series bible (feature plan)

> Refines: `../../../02-story-writing.md` story 02.6.3 (Epic 02.6), §5.1 (`Features/SeriesBible/`), §6 task 02.6.3-T1 (replaced by §10 below). Contract: `../README.md` — facts (O3–O5, P1, P3), findings F27, F21, F34, F38, decisions D1/D2/D5/D6/D7/D8/D9/D12/D13/D14/D19/D21/D22, task template §6, gates §7, index §8, re-estimate §9. Layout and templates: `../../../00-overview.md` §5.2, §6, §7, §8, §10.1.
> Legend: ✅ verified (source) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

| | |
|---|---|
| Story | **02.6.3 Series bible** — *As a creator I want my series to remember its narrator, places and past plots so that episodes feel like one channel.* Should · M |
| Tasks | 02.6.3-T1 entity + `GET/PUT /series/{id}/bible` with ETag + limits (S 0.5) · 02.6.3-T2 proposal job on `StoryApproved` + accept/dismiss + autopilot auto-accept (S 0.5) · 02.6.3-T3 bible in writer input (token budget) + continuity checks (S 0.5) = **1.5 d** |
| In scope | `series_bibles` table + `bible.v1` content schema and limits; auto-create with the series; `GET/PUT /series/{id}/bible` with `sb-` ETag (D9); `ProposeBibleUpdateJob` on outbox `StoryApproved`; append-only proposal ops; accept/dismiss; autopilot fiction auto-accept (F27); `BibleSnapshot` + budget + `avoid_plots` for the writer data object (D6); the review's bible input and the `bible_contradiction` rule; audit keys; tenant filter |
| Out of scope | UI (10.5.7 bible panel + proposal diff — this plan lists the states and copy keys only); autopilot scheduler, gates and run state machine (02.8.2/02.8.3 — they call `IIdeaPlanner`/commands); the review prompt itself (02.6.1 owns `prompts/review/v1.md`); source/fact-sheet data (02.5); SaaS credit rules (12, M2) |
| Acceptance (story, restated) | Given a series, `series_bibles` holds `content jsonb` editable through `GET/PUT /series/{id}/bible` with `If-Match`. Given an approved story, a background call proposes an update saved as `proposed_content`; the user accepts or dismisses it. Given generation, the writer receives the bible and must not repeat a listed plot; the self-review checks continuity against it |

The story's `POST /series/{id}/bible/proposal/accept|dismiss` (00 §8) is unchanged. F27 changes the proposal from "a new version to merge" to an **append-only op list** so accepting can never conflict with manual edits.

## 2. Confirmed facts

| # | Fact used here | Status / source |
|---|---|---|
| P1 | `ICreditLedger` is metering-only in P (`MeteringOnlyCreditLedger`, D1); the bible proposal is a paid generation and must still reserve + budget-check | ✅ README §1 P1, D1 |
| P3 | `story_generating → draft \| story_review` on failure: back to the previous state. Nothing in this feature changes an episode status | ✅ README §1 P3, 00 §7 |
| O3 | `gpt-6-luna` $0.10 in / $0.01 cached / $0.50 out per 1M; 1.05M context; 128K max output; Structured Outputs on Responses | ✅ README §1 O3 |
| O4 | `max_output_tokens` covers visible + reasoning tokens; a cut-off call has `incomplete_details.reason = max_output_tokens` | ✅ README §1 O4 |
| O5 | `store` defaults to **true**; we set `store = false` (D7) | ✅ README §1 O5 |
| O9 | Prompt caching needs a stable ≥ 1,024-token prefix; static first, dynamic data last | ✅ README §1 O9 |
| D8 | `bible_conflict` 412, `bible_proposal_not_pending` 409, `bible_invalid_content` 400, `if_match_required` 428 | ✅ README D8 |
| D9 | Bible ETag = `"sb-<series_bibles.version>"`; `If-Match` **required** on `PUT /series/{id}/bible` | ✅ README D9 |
| D13 | Audit keys `series.bible_updated`, `series.bible_proposal_accepted`, `series.bible_proposal_dismissed`; `data` holds ids/versions only | ✅ README D13 |
| D14 | Metering operation `bible_proposal`; `IUsageMeter.RecordAsync` writes its own transaction | ✅ README D14 |
| D19 | Retention: the `series_bibles` row is deleted with the series | ✅ README D19 |
| D21 | `StoryWriteRequest` carries `BibleSnapshot? bible` and `IReadOnlyList<string> avoidPlots`; `RunContext` travels with every call | ✅ README D21 |
| 12.1.1 | "Series-bible proposals and publishing are free (cost absorbed)" | ✅ `plans/12-billing-credits.md` §12.1.1 |
| — | 00 §6 `series_bibles`: `user_id, series_id (unique), version, content jsonb (narrator, recurring_characters, places, rules, episode_summaries), proposed_content jsonb, proposed_from_episode_id` | ✅ 00 §6 |
| — | 10.5.7: narrator, recurring characters, places, rules **and past episode summaries** are editable with `If-Match`; a pending proposal shows a diff with Accept/Dismiss | ✅ `plans/10-web-ui.md` 10.5.7 |

Closed by this plan: F27 (append-only ops, last 50 summaries, autopilot auto-accept). Still ⚠️: the exact truncation constants in T3 (24,000-char writer-snapshot budget, proven to converge against the T1 content limits) are first guesses to be measured with real bibles in M1.

## 3. Domain and data

### 3.1 Content schema `bible.v1` [DECISION]

Stored in `series_bibles.content`; `schema_version` is written by the backend, never by the model.

```json
{
  "schema_version": "bible.v1",
  "narrator": { "name": "Người kể chuyện", "persona": "…", "voice_notes": "…" },
  "recurring_characters": [
    { "id": "ch1", "name": "Bà Hoa", "description": "…", "first_episode_id": "0192…" }
  ],
  "places": [ { "id": "pl1", "name": "Ngôi nhà cuối ngõ", "description": "…" } ],
  "rules": [ { "id": "rl1", "text": "Không bao giờ nói tên thật của nhân vật phản diện." } ],
  "episode_summaries": [
    { "episode_id": "0192…", "title": "Tiếng gõ lúc nửa đêm",
      "summary": "≤ 200 chars", "structure": { "sub_genre": "haunted_house", "opening": "night", "twist_type": "revenant", "setting": "rural_house", "narrator_mode": "first_person", "beats": ["setup","clue","twist","reveal"] },
      "approved_at": "2026-10-04T21:03:11Z" }
  ]
}
```

- Id prefixes `ch`/`pl`/`rl` are **server-assigned** (`ch1`, `ch2`, …) and stable: `ApplyOps` never renumbers and never reuses an id. `ch`/`pl`/`rl` are chosen so they can never be confused with D22 segment addressing (`p:<n>`) or fact ids (`f1`).
- `narrator` is required (object with at least `persona`); the other arrays may be empty. `episode_summaries[].summary` is the only user-visible field the model writes verbatim.
- `episode_summaries[].structure` and `approved_at` are **server-derived** on PUT and on accept: `structure` from the referenced `story_versions.content.structure` (mapped to the vocab ids of `prompts/shared/structure-vocab.v1.json`, D3), `approved_at` from the approval transaction. A client PUT that sends them is ignored for those two fields; the rest of the summary is taken from the request.

### 3.2 Limits [DECISION] — enforced by `BibleContentValidator` (Domain, pure)

| Field | Limit | Config key (default) | Over-limit result |
|---|---|---|---|
| `narrator.name` | ≤ 80 chars | `Bible:Limits:NarratorNameChars` (80) | 400 `bible_invalid_content` |
| `narrator.persona` | ≤ 1,000 chars, non-empty | `Bible:Limits:NarratorPersonaChars` (1000) | 400 |
| `narrator.voice_notes` | ≤ 1,000 chars | `Bible:Limits:VoiceNotesChars` (1000) | 400 |
| `recurring_characters` | ≤ 30 entries | `Bible:Limits:MaxCharacters` (30) | 400 |
| `places` | ≤ 30 entries | `Bible:Limits:MaxPlaces` (30) | 400 |
| `rules` | ≤ 30 entries | `Bible:Limits:MaxRules` (30) | 400 |
| character/place `name` | ≤ 60 chars | `Bible:Limits:NameChars` (60) | 400 — added so the 02.6.3-T3 writer-snapshot budget has a provable upper bound on every field |
| character/place `description` | ≤ 300 chars | `Bible:Limits:DescriptionChars` (300) | 400 |
| rule `text` | ≤ 300 chars | `Bible:Limits:RuleChars` (300) | 400 |
| summary `title` | ≤ 100 chars | `Bible:Limits:SummaryTitleChars` (100) | 400 |
| summary `summary` | ≤ 200 chars | `Bible:Limits:SummaryChars` (200) | 400 (proposal path truncates instead, §4 U4) |
| `episode_summaries` count | unbounded in the DB | — | prompt uses the newest 50 (D4 `Bible:Prompt:MaxSummaries` = 50) |
| whole content (serialised) | ≤ 65,536 UTF-8 bytes | `Bible:Limits:MaxContentBytes` (65536) | 400 (also the D6 data-object budget) |
| duplicates | one summary per `episode_id`; unique normalised `name`/`text` per array | — | 400 |

Validation rules in `BibleContentValidator.Validate(SeriesBibleContent, BibleLimits) → IReadOnlyList<BibleFieldError>`: `BibleFieldError(string Field, string Code, string? Detail)` with `Field` = a JSON path (`episode_summaries[3].summary`) so the API can fill ProblemDetails `errors[]`. Codes: `too_long`, `too_many`, `empty`, `duplicate`, `unknown_episode`, `not_approved_episode`, `invalid_structure`, `read_only`. Also: no `<`/`>` in any string (the metadata rule of D3/F36 is applied for consistency), and `episode_id` must be a UUID of an **approved** episode of the same series.

### 3.3 Table and EF mapping (CR-4 owns the table; this task owns the migration)

`series_bibles` — 00 §6 plus `created_at`/`updated_at`:

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | UUIDv7 in .NET |
| `user_id` | `uuid` NOT NULL | denormalised from `series` at insert, never from the request (09.2.3); `IUserOwned` |
| `series_id` | `uuid` NOT NULL | FK → `series.id`, **unique** |
| `version` | `int` NOT NULL default 1 | bumped by PUT and by accept; the ETag source (D9) |
| `content` | `jsonb` NOT NULL | `bible.v1` |
| `proposed_content` | `jsonb` NULL | `BibleProposal` (`{ops:[…], based_on_version, proposed_at}`) |
| `proposed_from_episode_id` | `uuid` NULL | FK → `episodes.id`; the episode whose approval created/merged the proposal |
| `created_at`, `updated_at` | `timestamptz` NOT NULL | |

Indexes: `UX_series_bibles_series_id (series_id)`, `IX_series_bibles_user_id (user_id)` (tenant filter). Concurrency is the `version` column, not `xmin` (the ETag is `sb-<version>`, D9).

Files:

- `src/NightTales.Domain/Series/Bible/SeriesBible.cs` — entity + methods `static SeriesBible CreateDefault(Guid seriesId, Guid userId, IClock clock)`, `void ReplaceContent(SeriesBibleContent content, BibleLimits limits, IClock clock)` (validates, bumps `version`, clears nothing), `void SetProposal(BibleProposal proposal, Guid fromEpisodeId, IClock clock)`, `void ClearProposal(IClock clock)`, `IReadOnlyList<BibleOp> ApplyProposal(IClock clock)` (applies the pending ops to `content`, bumps `version`, clears the proposal, returns the applied ops).
- `src/NightTales.Domain/Series/Bible/SeriesBibleContent.cs` — `sealed record SeriesBibleContent(string SchemaVersion, BibleNarrator Narrator, IReadOnlyList<BibleCharacter> RecurringCharacters, IReadOnlyList<BiblePlace> Places, IReadOnlyList<BibleRule> Rules, IReadOnlyList<BibleEpisodeSummary> EpisodeSummaries)`; `BibleNarrator(string? Name, string Persona, string VoiceNotes)`; `BibleCharacter(string Id, string Name, string Description, Guid? FirstEpisodeId)`; `BiblePlace(string Id, string Name, string Description)`; `BibleRule(string Id, string Text)`; `BibleEpisodeSummary(Guid EpisodeId, string Title, string Summary, StoryStructureTags Structure, DateTimeOffset ApprovedAt)`.
- `src/NightTales.Domain/Series/Bible/BibleProposal.cs` — `sealed record BibleProposal(IReadOnlyList<BibleOp> Ops, int BasedOnVersion, DateTimeOffset ProposedAt)`; `sealed record BibleOp(BibleOpKind Kind, string? Summary, string? Name, string? Description, string? Text, string? Reason)`; `enum BibleOpKind { AddSummary, AddCharacter, AddPlace, AddRule }`.
- `src/NightTales.Domain/Series/Bible/BibleContentValidator.cs`, `BibleLimits.cs` (record bound from `IOptions<BibleOptions>`), `BibleErrors.cs` (`Error` factories: `BibleErrors.InvalidContent(errors)`, `Conflict`, `ProposalNotPending`, `IfMatchRequired`).
- `src/NightTales.Domain/Series/Bible/BibleOpNormalizer.cs` — `static string Normalize(string s)` (NFC, invariant lower, collapse whitespace, strip punctuation) used for dedupe; reuses the token rule of D4 for splitting only.
- `src/NightTales.Infrastructure/Persistence/Configurations/SeriesBibleConfiguration.cs` — `ToTable("series_bibles")`, `content`/`proposed_content` mapped `HasColumnType("jsonb")` through `BibleJsonContext` (source-generated `System.Text.Json`, snake_case naming policy, no relaxed escaping), unique index on `series_id`, index on `user_id`, `IUserOwned` global filter inherited.
- `src/NightTales.Infrastructure/Persistence/Migrations/<ts>_AddSeriesBible.cs` — created with `dotnet ef migrations add AddSeriesBible`. Expand → migrate → contract: create table + indexes, then a backfill that is a no-op on a greenfield DB but kept for safety:

```sql
INSERT INTO series_bibles (id, user_id, series_id, version, content, created_at, updated_at)
SELECT gen_random_uuid(), s.user_id, s.id, 1,
       jsonb_build_object('schema_version','bible.v1','narrator',
         jsonb_build_object('name', NULL, 'persona', '', 'voice_notes', ''),
         'recurring_characters','[]'::jsonb,'places','[]'::jsonb,'rules','[]'::jsonb,
         'episode_summaries','[]'::jsonb),
       now(), now()
FROM series s
WHERE NOT EXISTS (SELECT 1 FROM series_bibles b WHERE b.series_id = s.id);
```

- `src/NightTales.Infrastructure/Persistence/Configurations/SeriesConfiguration.cs` (owned by 09.4.1) gains a `HasOne<SeriesBible>()` navigation — **request to the 09.4.1 owner**, listed in §10 as a cross-feature item, not an edit by this feature.

`SeriesBible.CreateDefault` is called by `CreateSeriesHandler` (09.4.1) in the same transaction as the series insert — cross-feature request, §10.

## 4. Use cases

### U1 `GET /series/{id}/bible` — `GetBibleQuery(Guid SeriesId)`

- Query through `IReadDb` (no tracking), returns `BibleDto`:
  `{ seriesId, version, etag, content, proposal }` where `proposal` is `null` or
  `{ basedOnVersion, proposedFromEpisodeId, proposedAt, ops: [ { kind, summary?, name?, description?, text?, reason? } ], preview: { characters: n, places: n, rules: n, summaryForEpisodeId } }`.
  `preview` is computed server-side so the UI can render the diff banner without resolving op semantics (10.5.7).
- `ETag` header = `"sb-<version>"` and the same value in `etag`.
- Unknown series, another tenant's series, or a series of another user → **404** (09.2.3, no 403).
- No state change (CQRS rule): a missing row is a data-integrity error → 500 `series_bible_missing` and an alert; it is **not** auto-created by the query.

### U2 `PUT /series/{id}/bible` — `UpdateBibleCommand(Guid SeriesId, SeriesBibleContent Content)`

- `[Audited("series.bible_updated", Category.Episodes, Visibility.User)]`, `ICommand`.
- **`If-Match` required** (D9): missing header → 428 `if_match_required`; the header must equal the current `"sb-<version>"` (compared with the strong-ETag rules of RFC 9110, `W/` rejected) → else 412 `bible_conflict`.
- Body: `{ "content": { … } }` — full `bible.v1` **without** `schema_version` (ignored if sent). `episode_summaries` **is** editable (10.5.7): the client may edit `title`/`summary` and may remove an entry; `structure` and `approved_at` are re-derived server-side and any client value is ignored.
- Validation: `BibleContentValidator` (limits §3.2, `episode_id` must be an approved episode of this series). Failures → 400 `bible_invalid_content` with `errors[]` = the field list.
- Effect: `ReplaceContent` bumps `version`; the pending proposal is **kept** (its ops are still append-only and `based_on_version` stays the version they were computed against — see §4 U4). Response: 200 with `{ version, etag }` and the `ETag` header.
- Not a paid action: no `Idempotency-Key`, no ledger, no budget check.
- A repeat PUT with the same `If-Match` → the second call is 412 (the version already moved). The UI re-fetches on 412 (10.5.7) — documented, not "fixed" with a silent no-op, because the client's body may be stale.

### U3 `POST /series/{id}/bible/proposal/dismiss` — `DismissBibleProposalCommand(Guid SeriesId)`

- `[Audited("series.bible_proposal_dismissed", …)]`. No `If-Match` (D9 does not list it; a stale client cannot damage anything — dismiss is idempotent in effect).
- No pending proposal → 409 `bible_proposal_not_pending`. Otherwise clears `proposed_content` + `proposed_from_episode_id`, no version bump. 204.
- `data` = `{ seriesId, ops: n, proposedFromEpisodeId }` (ids and counts only, D13).

### U4 `POST /series/{id}/bible/proposal/accept` — `AcceptBibleProposalCommand(Guid SeriesId)`

- `[Audited("series.bible_proposal_accepted", …)]`. No `If-Match`.
- No pending proposal → 409 `bible_proposal_not_pending`.
- Effect: `ApplyProposal` applies every op onto the **current** content — append-only, so no conflict by construction (F27) — bumps `version`, clears the proposal. Response 200 `{ version, etag }` + `ETag`.
- Op semantics (`ApplyOps` in the Domain, reused by the autopilot auto-accept):
  - `AddSummary`: skip if a summary for that `episode_id` already exists (one summary per episode, invariant §3.2). Title/summary from the op; `structure`/`approved_at` filled from the episode's approved story version. If the op's summary is > 200 chars → truncate on a word boundary (never fail an accept for length).
  - `AddCharacter` / `AddPlace` / `AddRule`: skip when `BibleOpNormalizer.Normalize(name|text)` already exists in the array (dedupe, F27) or when the array is already at its limit (§3.2). Skipped ops are counted and returned so the handler can log `applied` vs `skipped` (counts only).
  - No op kind updates or deletes; the narrator is never touched by ops ([DECISION] the model has no `set_narrator` op — narrator changes are a manual edit in 10.5.7).
  - Ids for new characters/places/rules = `max(existing numeric suffix) + 1`, per prefix, computed at apply time.
- Accept after manual edits: safe (F27) — the ops only append. Accept twice → the second is 409 (the proposal was cleared).

### U5 Internal — proposal creation (`ProposeBibleUpdateJob`, §7)

Not an API use case; see §7. It writes `proposed_content` (no version bump) or, for an autopilot fiction approval, applies the ops immediately (version +1) and audits `series.bible_proposal_accepted` with `actor_type = system`.

### Authorization

Every use case runs through `AuthorizationBehavior` + the `IUserOwned` global filter; the owner is the only actor in P. `GET`/`PUT`/accept/dismiss are user commands on the user's own series; the job runs under `SystemContext(userId)` from the `StoryApproved` outbox payload (09.2.3) with filters **on**.

## 5. API and events

| Route | Verb | Auth | `If-Match` | Success | Errors |
|---|---|---|---|---|---|
| `/api/v1/series/{id}/bible` | GET | user JWT | no | 200 `BibleDto` + `ETag: "sb-<version>"` | 404 (unknown/other tenant) |
| `/api/v1/series/{id}/bible` | PUT | user JWT | **required** | 200 `{ version, etag }` + `ETag` | 428 `if_match_required`, 412 `bible_conflict`, 400 `bible_invalid_content`, 404 |
| `/api/v1/series/{id}/bible/proposal/accept` | POST | user JWT | no | 200 `{ version, etag }` + `ETag` | 409 `bible_proposal_not_pending`, 404 |
| `/api/v1/series/{id}/bible/proposal/dismiss` | POST | user JWT | no | 204 | 409 `bible_proposal_not_pending`, 404 |

OpenAPI fragment (PUT request):

```yaml
requestBody:
  content:
    application/json:
      schema:
        type: object
        required: [content]
        properties:
          content:
            $ref: '#/components/schemas/BibleContentV1'
```

ProblemDetails body for 400:

```json
{ "type": "https://nts/errors/bible_invalid_content", "title": "Bible content is not valid",
  "status": 400, "code": "bible_invalid_content",
  "errors": { "recurring_characters": ["too_many"], "episode_summaries[2].episode_id": ["not_approved_episode"] } }
```

Events:

- **No new SignalR event in P** [DECISION]. `00 §8` fixes the event list (`EpisodeUpdated`, `RenderProgress`, `PublicationUpdated`, `CreditBalanceChanged`). A pending bible proposal appears in `GET /series/{id}/bible`; the UI refreshes that route when the user opens/navigates to the bible panel and after an approve action on the episode page (the user is already there when a proposal is created). A `SeriesBibleUpdated` event is a §10 open item if 10.5.7 wants live banners.
- Outbox: `SeriesBibleProposalCreated` (internal, new, consumed by nothing in P; written so the fact is observable and a future `SeriesBibleUpdated` notification is a one-line handler). Payload `{ SeriesId, UserId, Version, ProposedFromEpisodeId, OpCount }`. Also `SeriesBibleUpdated { SeriesId, UserId, Version, ChangedBy }` on PUT and on accept. `D13` keys are the audit keys, not these event names — no conflict.
- `StoryApproved` (consumed): `{ EpisodeId, SeriesId, UserId, StoryVersionId, ApprovalSource (user|autopilot), OccurredAt }` — owner: 02.3.2-T1 (CheckpointSimilarityPlan, confirmed by the TL ruling of 2026-09-24).

## 6. UI

Reference stories: **10.5.7** (series bible panel + proposal diff, M) — this backend plan does not design screens. States and copy keys the backend must support:

| UI state (10.5.7) | Backend trigger | Copy key | vi | en |
|---|---|---|---|---|
| Empty bible | `content` has only an empty narrator and no arrays | `series.bible.empty` | "Truyện này chưa ghi nhớ gì. Nó sẽ tự đầy lên sau mỗi tập được duyệt." | "Your series remembers nothing yet. It fills up as episodes are approved." |
| Loaded | GET 200 | `series.bible.title` | "Bộ nhớ của bộ truyện" | "Series bible" |
| Sections | — | `series.bible.narrator` / `.characters` / `.places` / `.rules` / `.summaries` | "Người kể" / "Nhân vật định kỳ" / "Địa điểm" / "Quy tắc" / "Tóm tắt các tập" | "Narrator" / "Recurring characters" / "Places" / "Rules" / "Past episode summaries" |
| Save | PUT 200 | `series.bible.saved` | "Đã lưu" | "Saved" |
| Stale ETag | PUT 412 `bible_conflict` | `series.bible.error.conflict` | "Ai đó vừa thay đổi bộ nhớ này. Tải lại rồi lưu lại." | "Someone changed this bible. Reload and save again." |
| Missing `If-Match` | 428 | `series.bible.error.ifMatch` (a client bug) | — | — |
| Limits exceeded | 400 `bible_invalid_content` | `series.bible.error.limits` + per-field keys `series.bible.field.{too_many\|too_long\|duplicate\|unknown_episode}` | "Vượt giới hạn: {field}" | "Over the limit: {field}" |
| Proposal banner | `proposal != null` | `series.bible.proposal.banner` | "Có {n} thay đổi đề xuất từ tập “{title}”" | "{n} suggested changes from “{title}”" |
| Accept / Dismiss | POST accept / dismiss | `series.bible.proposal.accept` / `.dismiss` | "Chấp nhận" / "Bỏ qua" | "Accept" / "Dismiss" |
| No pending proposal | 409 `bible_proposal_not_pending` | `series.bible.proposal.none` | "Không còn đề xuất nào" | "No proposal is pending" |
| Autopilot auto-accept note | audit `series.bible_proposal_accepted` with `actor = system` | `series.bible.proposal.autoAccepted` | "Tự động cập nhật (autopilot)" | "Updated automatically (autopilot)" |

Accessibility: the diff banner is a `role="status"` live region announcing `series.bible.proposal.banner`; the accept/dismiss buttons are focusable in that order; the per-field errors are linked with `aria-describedby` to their inputs. No colour-only signal.

## 7. Jobs and integrations

### 7.1 `ProposeBibleUpdateJob` (new, Worker, Hangfire queue `default`)

Trigger chain: `Episode.ApproveStory(...)` → domain event `StoryApproved` → outbox (same transaction) → `OutboxDispatcher` (Worker `BackgroundService`, 09.2.4, every 5 s; (09 CR-14)) → `StoryApprovedBibleHandler` (Application `Features/SeriesBible/Events/StoryApprovedBibleProposalHandler.cs`) → `IJobScheduler.Enqueue<ProposeBibleUpdateJob>(seriesId, episodeId, generationRunId)`.

Handler steps (Worker, `SystemContext(userId)`):

1. Load the series + the approved `story_versions` row + the bible (tracked, tenant filters on).
2. Skip when the series is archived, when the episode's topic is `true_crime` **and** `ApprovalSource == autopilot` (cannot happen — true crime never auto-approves, defensive), or when a `generation_runs` row with `idempotency_key = bible:{episodeId}:{storyVersionId}` already exists (idempotency: the outbox is at-least-once; `episode_id` is NULL on this kind, so the key, not the episode column, is the guard).
3. `IBudgetGuard.CheckAsync(worstCase, ct)`; over a cap → write `generation_runs` `status = failed`, `error_code = spend_cap_reached`, `counts_toward_limit = false`, stop (no proposal; a nice-to-have must never break the budget).
4. Create the `generation_runs` row (D2): `kind = bible_proposal`, `series_id` NOT NULL, `episode_id` = **NULL** (series-level kind, D2 scope rule / T17; the approved episode is recorded in `series_bibles.proposed_from_episode_id` and in the outbox payload), `trigger` = `system` for `ApprovalSource = user`, `autopilot` for autopilot, `status = queued`, `counts_toward_limit = false`, `idempotency_key = bible:{episodeId}:{storyVersionId}`, `worst_case_usd`, `prompt_version`, `attempt = 1`.
5. `ICreditLedger.ReserveAsync(userId, CreditOperation.BibleProposal, referenceId: generationRunId, bound, ct)` (metering-only in P, D1).
6. Enqueue the job with ids only (09.2.6).
7. If step 4 violates the `(series_id, kind)` active-run index (another proposal for the same series is still `queued`/`running`), the handler does **not** fail. It re-schedules itself in 2 min through `IJobScheduler` (max 10 tries, then logs and drops it; a missed summary line is acceptable). Proposals of one series therefore run strictly one after another, and each one sees the ops the previous one applied. Test `ProposeBible_SecondApprovalWhileFirstRunning_RunsAfterFirst`.

Job steps (`src/NightTales.Application/Features/SeriesBible/Jobs/ProposeBibleUpdateJob.cs` + `…/ProposeBibleUpdateHandler.cs`; the Hangfire type lives in `src/NightTales.Worker/Jobs/`):

1. Load + re-check the budget; `generation_runs.status = running`, `stage = writing`, `started_at`.
2. Merge guard: if `proposed_from_episode_id == episodeId` → no-op (idempotent replay), settle with the recorded usage, done.
3. Build the prompt: system `prompts/bible/propose.v1.md`; data object (D6) = one JSON object:
   `{ "task": "propose_bible_update", "language": "vi", "topic": "ghost", "bible": { narrator, recurring_characters, places, rules, recent_episodes: [{title, summary}] (newest 50) }, "episode": { "title", "structure": {…}, "body": "…" } }`.
   `episode.body` is truncated to `Bible:Proposal:MaxStoryChars` (default 24,000 chars) from the end, silently — the opening carries the hook and the twist is usually early; note the truncation in `generation_runs.error_detail`? No: `error_detail` is for errors. Record a metric `bible_proposal_body_truncated_total` instead.
4. Call `ITextClient.CallAsync<BibleProposalV1>(req, ct)` (D7): `TextStep.BibleProposal`, model `OpenAi:Models:Bible` (default `gpt-6-luna`), `reasoning.effort` `OpenAi:Effort:BibleProposal` (default `low`), `max_output_tokens` from `price_versions.limits.bible_proposal.max_output_tokens` (default 1,200), schema `bible-proposal.v1.json` (strict), `store = false`, `safety_identifier = sha256(userId)`, `prompt_cache_key = "bible:propose"` (preset-scoped, never per-series — O9), header `X-Client-Request-Id = generationRunId:bible_proposal:attempt`.
   Outcome mapping (D7): `Ok` → continue; `InvalidOutput` → one retry at job level, then run fails `bible_proposal_invalid_output`; `Moderated` → run fails `bible_proposal_moderated` **and writes one `moderation_events` row** (`source = openai_text`, `stage = "bible_proposal"` — TL ruling #3, 2026-09-24: refusals on series/episode-level generation runs write `moderation_events` with `stage = kind`); `ProviderFundsExhausted` → run fails `provider_funds_exhausted` (pause reason for 02.8.3); `ProviderError` / `ProviderUnavailable` → `bible_proposal_generation_failed`. All three job outcome codes are **confirmed D8 additions** (TL ruling #3).
5. Post-process the ops (never trust them): keep only the four kinds; drop ops with empty fields; drop ops whose normalised name/text already exists in the content or in an earlier op of the same batch; cap the batch at `Bible:Proposal:MaxOps` (10) and per kind (`MaxCharacterOps` 3, `MaxPlaceOps` 3, `MaxRuleOps` 2); drop ops that would exceed a §3.2 limit. Dropped counts go to the log and to `usage_records`-adjacent metrics (`bible_proposal_ops_dropped_total{kind,reason}`).
6. Ensure the summary op: if the model returned no `AddSummary` for this episode, add one deterministically — `summary` = the story's `description` truncated to 200 chars on a word boundary, or `hook.text` if `description` is empty. Every approval therefore yields exactly one summary (F27).
7. Branch:
   - `ApprovalSource == user` → `SetProposal(proposal, episodeId)` (no version bump) + outbox `SeriesBibleProposalCreated`. The user accepts/dismisses in 10.5.7.
   - `ApprovalSource == autopilot` (fiction only) → `ApplyProposal()` immediately (version +1, no pending proposal), audit `series.bible_proposal_accepted` with `actor_type = system`, `data = { seriesId, fromVersion, toVersion, applied, skipped, proposedFromEpisodeId }`; outbox `SeriesBibleUpdated`. This is the F27 fix: autopilot fiction does not drift waiting for the owner.
   - Pending proposal from another episode → **merge**: `ops = existing.ops ++ new.ops` deduped (§4 U4 semantics), `based_on_version` stays at the current content version, `proposed_from_episode_id` becomes the newest episode id. No second paid call is needed for the merge itself. [DECISION] Merging (not replacing) because both episodes' facts are true; a replace would silently lose the first episode's summary. One pending proposal at a time (00 §6 stores it on the row).
8. Settle: `IUsageMeter.RecordAsync` already wrote one `usage_records` row per call (own transaction, D14) with `operation = bible_proposal`; `ICreditLedger.SettleAsync(reservationId, ct)`; `generation_runs.status = succeeded`, `stage = done`, `cost_usd`, `finished_at`.

Retries: Hangfire retry **0** for this job (same policy as the story job, D7/F23); a run left `running` by a crash is closed by `StaleGenerationSweeperJob` (02.2.2-T3; confirmed by TL ruling #3 to cover every `generation_runs.kind`, not only `story`).

Cost: the proposal is one `gpt-6-luna` call with ~4–8k input tokens (bible + story body) and ≤ 1,200 output tokens → ≈ $0.001–0.002. Absorbed (12.1.1). `IBudgetGuard` applies to every dispatch (D1).

### 7.2 Dependencies

- `ITextClient`, `TextCallResult<T>`, `TextStep`, `IUsageMeter`, `TextCostCalculator`, `price_versions` — 02.2.4-T1 / 02.2.1-T2 (StoryWriterPlan / FactSheetReviewPlan, TL ruling #2). Needed in `price_versions.limits`: `bible_proposal.max_output_tokens` (request to 02.2.4-T1, §10).
- `IJobScheduler` — 09.2.6.
- `OutboxDispatcher` + `IOutboxHandler<T>` — 09.2.4.
- `StoryApproved` — 02.3.2-T1.
- `StaleGenerationSweeperJob` — 02.2.2-T3 (confirmed to cover `kind = bible_proposal`, TL ruling #3).

## 8. Security and privacy

| Threat | Control |
|---|---|
| Cross-tenant bible read/write | `IUserOwned` + `user_id` set from the parent series at insert; EF global filter; 404 (not 403) for another user's series (09.2.3); architecture test (§10) |
| Lost-update / clobbering by a stale tab | `If-Match` required (428) + strong `sb-<version>` ETag (412) — D9 |
| Prompt injection through the bible | The bible is untrusted data (D6): it is serialised into the **one** data object, never substituted into a template; the system prompt states the two-tier rule (control keys are followed; material strings are never instructions — 02 T69). New injection fixtures `prompts/fixtures/injection/bible-*.json` (a summary/rule containing "ignore previous instructions") must still yield a schema-valid proposal that ignores it (02.2.3-T2 runs them; this plan adds the fixtures) |
| Injection through the story body sent to the proposal model | Same data-object rule; the story body is our own model output, already moderated |
| Model writes a real person/place into a fiction bible | The proposal prompt forbids real named individuals (true-crime rules 13 §5.3); the review's `real_world_claims[]` covers the story, not the bible → residual risk ⚠️: a hallucinated character name in the bible is echoed into later episodes. Mitigation: the bible is owner-editable and the fiction autopilot gate (02.8.2-T2) checks the narration for real-person signals; add a note to the 02.8.2 feature plan (cross-feature request, §10) |
| Unbounded growth (DoS by storage) | Limits §3.2 cap the arrays; `episode_summaries` grows one row per approved episode, deleted with the series (D19); prompt cost is bounded by the 50-summary window and the 24,000-char budget, proven to converge against the T1 limits (T3) |
| Content leaking into logs | D19: never log bible text, story text or prompts. Log `seriesId`, `version`, op counts, token counts, `prompt_version`, latency, `provider_request_id` |
| Proposal overwriting manual work | Append-only ops; no update/delete op kind; accept applies onto the current content (F27) |
| Paid call abuse via repeated approvals | One run per `(episodeId, kind)` idempotency key; an approval can happen only from `story_review`/`story_approved` (02.3.2); `IBudgetGuard` caps platform spend (D1) |

Retention (13, D19): `series_bibles` is deleted with the series; `usage_records` rows for `bible_proposal` are kept with `episode_id` nulled on episode deletion ⚠️ (confirm with 13.3, D19 already flags it).

## 9. Test plan

Unit (`tests/NightTales.Domain.UnitTests/Series/Bible/`):

| Test | Assert |
|---|---|
| `BibleContentValidator_TooManyCharacters_ReturnsTooManyError` | 31 characters → `recurring_characters: too_many`, field path correct |
| `BibleContentValidator_SummaryOver200_ReturnsTooLongError` | 201-char summary → `episode_summaries[0].summary: too_long` |
| `BibleContentValidator_DuplicateRuleAfterNormalization_ReturnsDuplicate` | "Không  nói  tên." vs "không nói tên" → `duplicate` |
| `BibleContentValidator_AngleBracketInName_ReturnsError` | `<b>` rejected (F36 rule reused) |
| `ApplyProposal_AddSummaryForExistingEpisode_SkipsAndKeepsFirst` | one summary per episode |
| `ApplyProposal_AddCharacterWithExistingNormalizedName_Skips` | dedupe by `BibleOpNormalizer` |
| `ApplyProposal_AtCharacterLimit_SkipsOpWithoutThrowing` | skipped, not failed; `applied`/`skipped` counts |
| `ApplyProposal_AssignsSequentialIds_NeverReuses` | `ch3` after a manual delete of `ch2` |
| `ApplyProposal_NeverTouchesNarrator` | no op kind changes the narrator |
| `ReplaceContent_BumpsVersion_AndKeepsProposal` | version +1, proposal intact |
| `BibleProposal_Serialisation_RoundTrips` | `bible.v1` + `bible-proposal.v1` JSON round-trip, snake_case |

Integration (`tests/NightTales.Api.IntegrationTests/SeriesBible/`, WebApplicationFactory + real PostgreSQL 17 via `PostgresFixture`):

| Test | Assert |
|---|---|
| `GetBible_OtherUsersSeries_Returns404` | tenant filter |
| `PutBible_WithoutIfMatch_Returns428` | `if_match_required` |
| `PutBible_WithStaleEtag_Returns412` | `bible_conflict` |
| `PutBible_ValidContent_BumpsVersionAndReturnsNewEtag` | `sb-2` in header + body |
| `PutBible_UnknownEpisodeInSummaries_Returns400` | `bible_invalid_content` + field path |
| `PutBible_EditableSummaries_KeepsServerDerivedStructureAndApprovedAt` | client-sent `structure` ignored |
| `PostAccept_WithoutProposal_Returns409` | `bible_proposal_not_pending` |
| `PostAccept_AppliesOpsAndBumpsVersion_ThenSecondCallReturns409` | idempotence by 409 |
| `PostDismiss_ClearsProposalWithoutVersionBump` | version unchanged |
| `CreateSeries_CreatesEmptyBible` | `series_bibles` row exists with version 1 (09.4.1 hook) |
| `StoryApproved_UserApproval_StoresPendingProposal_NoVersionBump` | with `FakeTextClient` scripted proposal |
| `StoryApproved_AutopilotApproval_AppliesOpsAndAuditsSystemActor` | version +1, audit `series.bible_proposal_accepted`, `actor_type = system` |
| `StoryApproved_TwiceForSameEpisode_ProducesOneProposal` | idempotency key + merge guard |
| `StoryApproved_WhileProposalPending_MergesOps` | ops concatenated, deduped, `based_on_version` unchanged |
| `StoryApproved_BudgetExceeded_FailsRunWithSpendCapReached` | `FakeBudgetGuard` denies; no proposal written; run `failed`, not counted |
| `ProposeBibleUpdate_ModelInvalidOutput_RetriesOnceThenFails` | `bible_proposal_invalid_output`, `attempt = 2` |
| `ProposeBibleUpdate_RecordsUsageWithOperationBibleProposal` | one `usage_records` row per call |

Manual: the dashboard/DB inspection is not needed; the audit row and `generation_runs` are asserted in the integration tests.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.6.3-T1 | `series_bibles` entity + `GET/PUT /series/{id}/bible` with ETag + limits | S 0.5 | 09.2.2 (EF/migrations), 09.4.1 (series CRUD hook), 09.1.3 (behaviours) | T2, T3, 10.5.7-T1 |
| 02.6.3-T2 | Proposal job on `StoryApproved` + accept/dismiss + autopilot auto-accept | S 0.5 | T1, 02.2.4-T1 (`ITextClient`/metering/`price_versions`), 02.3.2-T1 (`StoryApproved`), 09.2.4/09.2.6 | 02.8.3-T1 (auto-accept path), 10.5.7-T1 |
| 02.6.3-T3 | Bible in writer input (token budget) + continuity checks | S 0.5 | T1, 02.2.2-T3 (job assembles `StoryWriteRequest`), 02.6.1-T1 (review request) | 02.8.1-T1 (bible is an idea input), 02.8.2-T1 (continuity in the gate) |
| **Total** | | **1.5 d** | | |

Matches README §9 (02.6.3 bible: 1.0 → 1.5 d, "+0.5 for append-only proposals and the prompt budget", F27). No re-estimate difference.

### Open items for the TL

1. **D8 job-outcome codes and D12 stage for bible (and ideas)** — **resolved** by TL ruling #3 (2026-09-24): `bible_proposal_invalid_output`/`_moderated`/`_generation_failed` are confirmed D8 additions (same pattern for `ideas` in 02.8.1); a `Moderated` refusal on the series/episode-level run writes `moderation_events` with `stage = kind` (`"bible_proposal"` here), closing the earlier D12 stage gap.
2. **`autopilot_settings` `tone`/`banned_themes`** — **resolved** by TL ruling #3: 02.8.3-T1 owns the `autopilot_settings` schema and adds `tone` (string, ≤ 120) and `banned_themes` (string[], ≤ 20 × ≤ 60); 02.8.1 consumes them as a dependency, not a request against 00 §6 from this plan.
3. **Stale-run sweeper kinds** — **resolved** by TL ruling #3: `StaleGenerationSweeperJob` (02.2.2-T3) covers every `generation_runs.kind`, including `bible_proposal`.
4. **`price_versions.limits` needs the new step limits** — `bible_proposal.max_output_tokens` (1,200), `ideas.max_output_tokens` (1,500), `novelty.max_output_tokens` (400). **Resolved** by README §10 T12/T56. (D14 says the limits live in `price_versions.limits`); config fallbacks are specified in T2/T1 so this is not blocking.
5. **No `SeriesBibleUpdated` SignalR event** (§5). If 10.5.7 wants a live proposal banner, 00 §8 needs the event; today the panel refreshes on navigation.
6. **Cross-feature requests (not edits by this feature):** 09.4.1 `CreateSeriesHandler` inserts `series_bibles` via `SeriesBible.CreateDefault`; `SeriesConfiguration` gets the navigation; 02.8.2-T2 should note that a hallucinated real-person name in the bible is not covered by the story-level real-world detector.

## 11. Rollout

- **Feature flag:** none. The bible is additive and has no effect until `GET /series/{id}/bible` is called; the writer only receives `bible` when a snapshot exists. No flag is needed because a missing/empty bible is a valid no-op everywhere (`BibleSnapshot? bible` is nullable in D21).
- **Migration order:** `AddSeriesBible` (create + backfill) ships with 02.6.3-T1, before T2/T3. Backward compatible for one release (expand only; no column dropped).
- **Ship order:** T1 (entity + routes, verifiable with curl) → T3 (writer/review input; the bible starts influencing stories) → T2 (proposals). T2 last because it is the only paid path and the only writer of `proposed_content`.
- **Monitoring:** `bible_proposal_ops_dropped_total{kind,reason}` (a rising drop rate means the prompt or the caps drifted), `bible_proposal_body_truncated_total`, `openai_tokens_total{operation="bible_proposal"}`, `openai_cost_usd_total{operation="bible_proposal"}`, `bible_content_bytes` (histogram; approaching 65,536 means the limits need revisiting), `generation_runs{kind="bible_proposal",status}`.
- **Rollback:** set `Bible:Proposal:Enabled=false` (config, hot-reloadable through `DbSettingsProvider`, 09.10.3) → the outbox handler stops enqueuing; already-pending proposals stay visible and can be accepted/dismissed. Data rollback is not needed: the table is additive and the writer degrades to a bible-less prompt when the snapshot is absent.
- **M2 note:** `MeteringOnlyCreditLedger` is replaced by the 12 ledger; the `bible_proposal` operation has no price (absorbed, 12.1.1), so the reserve→settle cycle records cost with no credit movement. If the SaaS decides to charge for proposals, only the price table changes.
