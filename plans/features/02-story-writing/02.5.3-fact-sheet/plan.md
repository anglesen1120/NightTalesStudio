# 02.5.3 — Fact sheet (build, review, approve, write from facts)

> Refines: `../../02-story-writing.md` §4 (stories 02.5.3, 02.5.4, 02.5.5), §5.8 (fact-sheet API), §5.9 (similarity), FR-02-4/FR-02-8.
> Contract: `../README.md` (D2, D5, D6, D7, D8, D9, D13, D14, D19, D21; F12, F13, F18, F21, F26, F29).
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs (URL + date) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

**Stories covered**

| Story | Title | Size | Depends on |
|---|---|---|---|
| 02.5.3 | Build the fact sheet | M | 02.5.1/02.5.2 (fetched sources), 02.2.1 (`ITextClient`) |
| 02.5.4 | Review and approve the fact sheet | S | 02.5.3 |
| 02.5.5 | Write from the approved facts | S | 02.5.4, 02.2.1, 02.2.2 |

**In scope** — the true-crime research path from fetched `story_sources` to a defensible, editable, versioned `fact_sheets` row: the fact-extraction prompt + strict schema, the deterministic validator (alias mapping, dedupe, verbatim-copy drop, source-overlap score via 02.5.6), the `IFactSheetGuard` the story pipeline calls, and backend-built `fact_sources` in `story_versions.content`.

**Out of scope** — fetching/extraction of source text (02.5.1/02.5.2), the similarity scorer itself (02.5.6 owns `ScoreFact`), the writer prompt/pipeline (02.2), self-review (02.6.1), the SPA components (10 owns the UI; this plan fixes the contract only), the credits ledger (P is metering-only, D1).

**Contract restated for this folder** — model output is **statements + `source_aliases`**; the backend assigns fact ids `f1…` and maps aliases `s1…s10` to `story_sources.id`. The model never emits a UUID or a URL (D6, F12).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| 1 | `ITextClient.CallAsync<T>(TextCallRequest, ct) → ValueTask<TextCallResult<T>>`; `TextStep.Facts` exists; `TextCallRequest(RunContext, TextStep, SystemPrompt, UserDataJson, SchemaName, Schema BinaryData, MaxOutputTokens, PromptCacheKey, PromptVersion)` ✅ | README D21 |
| 2 | The fact-sheet job uses the same `ITextClient`/`RunContext`; `IFactExtractor.ExtractAsync(FactExtractionRequest, ct) → TextCallResult<FactsV1>` is the fixed port name ✅ | README D21 |
| 3 | Fact-sheet prompt = `prompts/facts/v1.md`; schema = `prompts/schemas/facts.v1.json`; `prompt_version` composite e.g. `rel=…;sys=facts.v1;schema=facts.v1` ✅ | README D5 |
| 4 | Untrusted data rule: fact-sheet calls send `{"sources":[{"alias":"s1","title":"…","text":"…"}]}`; the model sees only `s1…s10` ✅ | README D6 |
| 5 | Outcome mapping: schema-invalid/`max_output_tokens` → `InvalidOutput` (one retry at job level → `fact_sheet_invalid_output`); refusal/`content_filter` → `Moderated` → `fact_sheet_moderated`; funds → `provider_funds_exhausted`; other 4xx → `fact_sheet_generation_failed` ✅ | README D7, D8 |
| 6 | `generation_runs.kind = fact_sheet`; partial unique `(episode_id, kind) WHERE episode_id IS NOT NULL AND status IN ('queued','running')` → 409 `fact_sheet_generation_in_progress`; `StaleGenerationSweeperJob` covers every kind ✅ | README D2 |
| 7 | Fact-sheet ETag = `"fs-<fact_sheets.id>"`; `If-Match` required on `PUT /episodes/{id}/fact-sheet` and `POST …/fact-sheet/approve`; missing → 428 `if_match_required`; stale → 412 `fact_sheet_conflict` ✅ | README D9, D8 |
| 8 | `IFactSheetGuard.EnsureApprovedAsync(Guid episodeId, Topic topic, ct) → ErrorOr<ApprovedFactSheet>`; `ApprovedFactSheet(Guid? FactSheetId, int? Version, IReadOnlyList<FactForWriter> Facts, IReadOnlyDictionary<string, Guid> AliasToSourceId)` with `ApprovedFactSheet.None` ✅ | README D21 |
| 9 | F13: approve needs ≥ 5 facts and ≥ 2 distinct `fetched` url sources **each cited by ≥ 1 kept fact**, with distinct `content_sha256`; fewer than 5 facts → 409 `fact_sheet_sources_insufficient`, fewer than 2 distinct such sources → 409 `story_sources_required` ✅ | README F13, D8 |
| 10 | F18: score each **fact statement** against its sources (same language); exclude `user_note` from the corpus; NFC + per-language `n` ✅ | README F18 |
| 11 | F12: stories store `fact_sheet_id`; approve checks it is the current approved sheet (`story_fact_sheet_outdated`); fact ids stable, never reused. F26: editing allowed only in `draft`/`story_review`, never while `rendering` ✅ | README F12, F26, D8 |
| 12 | D13 action keys: `episode.fact_sheet_generation_requested`, `episode.fact_sheet_saved`, `episode.fact_sheet_approved`; `[Audited(action, Category.Episodes, Visibility.User)]` in P. D19: never log source text/statements ✅ | README D13, D19 |
| 13 | `ICreditLedger` + `IBudgetGuard` guard the paid call; `CreditOperation.FactSheet`; `IUsageMeter.RecordAsync` writes `usage_records` per call ✅ | README D1, D14 |
| 14 | 02.5.6 `SimilarityScorer`/`ScoreFact` is the only scorer; this feature calls it, never reimplements ✅ | README F18, D21 |
| 15 | 02 §4 02.5.3: given ≥ 1 `fetched` source; statements paraphrased in the target language, never copied sentences; a fact without a valid `source_id` is dropped; result saved as `fact_sheets` (`status = draft`, `version = n`); `EpisodeUpdated` fires ✅ | `../../02-story-writing.md` §4 |

**Open questions closed here** — (a) who assigns fact ids → backend, monotonic `max_fact_id` (F12); (b) "never copied" enforcement → 02.5.6 `ScoreFact` overlap of the statement vs its cited sources; above the block threshold the fact is dropped and counted (F18); (c) a `user_note` source is allowed for user-added facts and is labelled "your note" (02 §4 02.5.4).

## 3. Domain and data

**Aggregate** `FactSheet` (Domain `Research/`) — one immutable version per row; editing creates a new row (D9 ETag = row id).

```csharp
// src/NightTales.Domain/Research/FactSheet.cs
public sealed class FactSheet {
    public Guid Id { get; private init; }                 // = ETag "fs-<id>"
    public Guid EpisodeId { get; private init; }
    public int Version { get; private init; }             // 1-based, monotonic per episode
    public FactSheetStatus Status { get; private set; }   // Draft | Approved | Superseded
    public IReadOnlyList<FactItem> Facts { get; private set; }
    public int MaxFactId { get; private set; }            // highest id ever assigned in this episode
    public string ContentSha256 { get; private set; }     // canonical JSON of Facts
    public Guid? GenerationRunId { get; private init; }   // null for hand-edited versions
    public string PromptVersion { get; private init; }    // "user" for hand edits
    public Guid CreatedBy { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
}
public enum FactSheetStatus { Draft = 0, Approved = 1, Superseded = 2 }
```

**Value object** `FactItem` (stored inside `facts jsonb`, no own table) and the model output contract:

```csharp
public sealed record FactItem(string Id, string Statement, IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<string> SourceAliases, decimal? SourceOverlap, FactConfidence Confidence, FactOrigin Origin);
// Id "f1".."fN" backend-assigned, never reused; SourceIds mapped from aliases (≥ 1); SourceAliases kept for display order
// SourceOverlap = 02.5.6 ScoreFact result (null when unscored); Origin = Model | User
public enum FactOrigin { Model = 0, User = 1 }
public enum FactConfidence { High = 0, Medium = 1, Low = 2 }   // Low = advisory hint only, never dropped (T53)

// Application Features/Research/Facts/FactsV1.cs — what the model returns
public sealed record FactsV1(IReadOnlyList<FactDraftV1> Facts);
public sealed record FactDraftV1(string Statement, IReadOnlyList<string> SourceAliases, FactConfidence Confidence);
```

**EF mapping** (`FactSheetConfiguration`, table `fact_sheets`): `id uuid PK` (= ETag `fs-<id>`) · `episode_id uuid FK → episodes ON DELETE CASCADE` · `version int` (unique `(episode_id, version)`) · `status smallint` (`Draft`/`Approved`/`Superseded`; partial unique `(episode_id) WHERE status = 1`) · `facts jsonb` (array of `FactItem`; optional GIN `jsonb_path_ops`) · `source_aliases jsonb` (alias `s1…s10` → `story_sources.id`, persisted per version — CR-4; sources can change after generation) · `input_truncated bool` (true when the extraction input was truncated — CR-4) · `max_fact_id int` (monotonic per episode) · `content_sha256 char(64)` · `generation_run_id uuid null FK → generation_runs` · `prompt_version text` (`"user"` for hand edits) · `created_by uuid FK → users` · `created_at timestamptz` · `approved_by uuid null` / `approved_at timestamptz null`.

Migration `2026xxxx_AddFactSheets` (expand → migrate → contract): create table + indexes; no backfill (new feature). Rollback = drop table.

**Invariants** — (1) every `FactItem.SourceIds` non-empty and every id ∈ the episode's `story_sources`; (2) ids unique within the episode and strictly increasing across versions (`MaxFactId` only grows); (3) `content_sha256` = SHA-256 of `JsonSerializer.Serialize(Facts, canonicalOptions)`; (4) at most one `Approved` row per episode — approving a new version demotes the prior `Approved` row to `Superseded` **in the same transaction** (T54); (5) `Version = max(version)+1`; (6) `source_aliases` is written on every version so `AliasToSourceId` survives later source edits (D21, CR-4).

**Source text input (owned by 02.5.1)** — the extraction text of a `story_sources` row is `extracted_text` for `kind = url` and **`note_text`** for `kind = user_note` (≤ 3,000 chars, `user_note` only — README CR-4). Both map into `FactSourceInput.Text`; a `user_note` row is never excluded for a missing `extracted_text`. `note_text` is the user's own text, so it is excluded from the similarity corpus (F18) but included in fact extraction.

## 4. Use cases

All commands are MediatR `IRequest<ErrorOr<T>>` in Application `Features/Research/…`, `[Audited]` per D13, guarded by `IBudgetGuard` + `ICreditLedger` (D1).

**4.1 `GenerateFactSheetCommand(Guid EpisodeId, string IdempotencyKey) → ErrorOr<FactSheetSummaryDto>`** — authorize series owner; preconditions: topic `true_crime`; episode status ∈ {`draft`,`story_review`} else 409 `episode_invalid_state`; ≥ 1 `fetched` source else 409 `fact_sheet_sources_insufficient`; no active `fact_sheet` run else 409 `fact_sheet_generation_in_progress`; reused idempotency key → the existing run's result. `IBudgetGuard.CheckAsync` → 429 `spend_cap_reached`; `ICreditLedger.ReserveAsync(userId, fact_sheet, episodeId, bound)`. Creates `generation_runs` (`kind = fact_sheet`, `trigger = user`, `status = queued`, `counts_toward_limit = false` — D2 counts only `story`), enqueues `GenerateFactSheetJob(generationRunId)`, returns 202 `{ runId }`.

**4.2 `UpdateFactSheetCommand(Guid EpisodeId, string IfMatch, FactSheetContentDto Content)`** — `IfMatch` missing → 428 `if_match_required`; ≠ current `fs-<id>` → 412 `fact_sheet_conflict` (D9). Episode status must be `draft`/`story_review`; `story_approved` or `rendering` → 409 `episode_invalid_state` (F26). Items with an existing `id` keep it and their order; `null` → next `MaxFactId + 1`, never reused; added facts MUST have ≥ 1 `sourceIds` (a `user_note` source is allowed); invalid body → 400 `fact_sheet_invalid_content` (T51).

**4.3 `ApproveFactSheetCommand(Guid EpisodeId, string IfMatch)`** — same ETag rules; current version must be `Draft`; fewer than 5 facts → 409 `fact_sheet_sources_insufficient`; ≥ 2 distinct cited `fetched` url sources with distinct `content_sha256` else 409 `story_sources_required` (F13). Sets `status = Approved`, `approved_by/at`, raises `FactSheetApproved` + outbox `EpisodeUpdated`.

**4.4 `GetFactSheetQuery(Guid EpisodeId)`** — current (draft or approved) version with `etag`, `facts` (resolved source title/url/alias), `maxFactId`.

**DTOs**

```csharp
public sealed record FactSheetSummaryDto(Guid Id, int Version, string Status, string Etag, int FactCount, DateTimeOffset CreatedAt);
public sealed record FactSheetDto(Guid Id, int Version, string Status, string Etag, IReadOnlyList<FactItemDto> Facts, string PromptVersion, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);
public sealed record FactItemDto(string Id, string Statement, IReadOnlyList<FactSourceRefDto> Sources, decimal? SourceOverlap, string Origin);
public sealed record FactSourceRefDto(Guid SourceId, string Alias, string Title, string Url, bool IsUserNote);
public sealed record FactSheetContentDto(IReadOnlyList<FactItemInputDto> Facts);
public sealed record FactItemInputDto(string? Id, string Statement, IReadOnlyList<Guid> SourceIds, FactConfidence? Confidence); // Id null = new fact
```

## 5. API and events

| Method | Route | Body / headers | Success | Errors |
|---|---|---|---|---|
| `POST` | `/episodes/{id}/fact-sheet/generate` | `Idempotency-Key` header | 202 `{ runId }` | 409 `episode_invalid_state`, 409 `fact_sheet_sources_insufficient`, 409 `fact_sheet_generation_in_progress`, 429 `spend_cap_reached` |
| `GET` | `/episodes/{id}/fact-sheet` | — | 200 `FactSheetDto?` + `ETag` | 403 |
| `PUT` | `/episodes/{id}/fact-sheet` | `If-Match: "fs-<id>"`, `FactSheetContentDto` | 200 `FactSheetSummaryDto` + new `ETag` | 428 `if_match_required`, 412 `fact_sheet_conflict`, 409 `episode_invalid_state`, 400 validation |
| `POST` | `/episodes/{id}/fact-sheet/approve` | `If-Match` | 200 `FactSheetSummaryDto` | 428, 412, 409 `episode_invalid_state`, 409 `story_sources_required`, 409 `fact_sheet_sources_insufficient` |

Outbox/SignalR: `EpisodeUpdated` with **ids only** — `{ episodeId, seriesId }` (NOTIFY body `{ userId, event: "episode_updated", ids, data: null }`) — on generate-complete, edit and approve; the sheet id, status and version travel in the fact-sheet DTO the client refetches (09 README BD11/WT54). Domain events: `FactSheetGenerated`, `FactSheetSaved`, `FactSheetApproved`. No new event type (F24 spirit).

OpenAPI note — every D9 route documents `If-Match` as required and the `fs-` ETag as opaque.

## 6. UI

Screens (10 owns the SPA; this plan fixes behaviour + copy keys):

- **Episode research tab** — fact-sheet panel. States: *empty* ("No fact sheet yet — add sources, then build the sheet"), *generating* (progress via `EpisodeUpdated`), *draft* (editable list, per-fact source chips, overlap-warning chip when `sourceOverlap` > block threshold), *approved* (read-only + "Edit" → new draft version, offers reopen when a story is approved), *error* (`fact_sheet_sources_insufficient`, `fact_sheet_generation_in_progress`, `spend_cap_reached`).
- Per fact: statement (multiline), a `confidence` badge (`high`/`medium`/`low`; `low` is an **advisory hint only** — the fact is never hidden or dropped, T53), source chips (alias + title, tooltip URL; `user_note` chips labelled **"your note" / "ghi chú của bạn"**), delete, add-fact (requires ≥ 1 source). When the run set `input_truncated`, the panel shows "some source text was shortened" with the affected source chips.
- Copy keys: `factSheet.{empty,generate,generating,approve,edit,userNoteLabel,overlapWarning,reopenStoryHint}` + `factSheet.errors.{code}` (vi/en). A11y: labelled fact list, delete buttons carry `aria-label` with the fact index; ETag conflicts surface a non-destructive "reload" banner.

## 7. Jobs and integrations

**`GenerateFactSheetJob(Guid generationRunId)`** — Hangfire, `default` queue, **retry 0** (D7/F23); crash recovery via `StaleGenerationSweeperJob` (D2). Stages: `queued → running` (reload episode + `fetched` sources; re-check state else fail `fact_sheet_generation_failed` detail `sources_changed`) → `writing` (build `FactExtractionRequest` with the per-source char cap + total token budget, 02.5.3-T1; `IFactExtractor.ExtractAsync` → `TextCallResult<FactsV1>`) → `checking` (moderation pre-check per source chunk, D12 free; `FactSheetValidator.Validate` with `ScoreFact` from 02.5.6) → `saving` (insert `fact_sheets` `status = draft`, `version = n`, `max_fact_id`; set `generation_runs.result_fact_sheet_id`; settle reservation D1; `IUsageMeter.RecordAsync` D14; outbox `EpisodeUpdated`) → `done`.

Failure mapping (D7/D8): `InvalidOutput` → one retry inside the job, then `fact_sheet_invalid_output` (not counted, D2); `Moderated` → `fact_sheet_moderated` + `moderation_events{stage = fact_sheet}`; `ProviderFundsExhausted` → `provider_funds_exhausted`; `ProviderError`/`ProviderUnavailable` → `fact_sheet_generation_failed`. On failure the reservation is refunded and usage is still recorded when the response carried it.

**Integrations** — OpenAI Responses via `ITextClient` (`TextStep.Facts`), model `OpenAi:Models:Facts` (default `gpt-6-luna`), effort `OpenAi:Effort:Facts` (default `low` ⚠️), `max_output_tokens` = `price_versions.limits.facts.max_output_tokens` (4,000; README §10 T56) (default 4,000), timeout 60 s (D7). `omni-moderation-latest` pre-check on each source chunk (D12, free) before the paid call.

## 8. Security and privacy

- **Prompt injection (F21/D6)** — source texts go in the single JSON data object, never templated into the system prompt; the system prompt declares all string values material, not instructions. Injection fixtures must yield schema-valid facts that ignore the instruction (02.2.3-T2).
- **Fabricated sourcing (F12)** — the model never returns ids/URLs; aliases are mapped server-side; a fact citing an unknown alias is dropped; `fact_sources` is always backend-built from `story_sources`.
- **Copying (F18)** — `source_overlap` (02.5.6) over the statement vs its cited sources; above the block threshold the fact is dropped, not shown. Cross-language translation copying is a documented residual risk.
- **Data exposure / retention (D19)** — statements can contain sensitive case details: never logged; `fact_sheets` are deleted with the episode.
- **Stale approval (F12)** — approve checks `story.fact_sheet_id` = current approved sheet id → 409 `story_fact_sheet_outdated`; the job re-checks at start (D21).
- **Authorization** — every command requires series ownership (09 authz); cross-episode access → 403.

## 9. Test plan

Unit (Domain/Application, `FakeTextClient`, `FakeClock`, `FakeBudgetGuard` — D20):

- `FactSheetValidator_{DropsFactWithUnknownAlias,DropsFactWithNoAlias,KeepsKnownAliasesOnly_WhenPartiallyUnknown,DropsStatementEmptyOrOver600Chars,DedupesIdenticalStatements_FirstWins,DropsVerbatimCopyAboveOverlapThreshold,AssignsSequentialIds_FromMaxFactIdPlusOne,NeverReusesId_AcrossVersions,CapsFacts_AtMaxFacts,ContentSha256_IsStableForSameFacts}`
- `FactInputBudget_{AssignsAliasesS1ToS10_InCreatedOrder,ExcludesSources_AboveMaxSources,TruncatesPerSource_HeadAndTail_WhenOverCharCap,ReducesTotal_WhenOverTokenBudget_NeverExceedsCap,KeepsUserNoteUntruncated}`
- `OpenAiFactExtractor_{NeverSerialisesSourceId_OrUrl,UsesSchemaNameFactsV1_AndStepFacts,MapsInvalidOutputToInvalidOutput,MapsRefusalToModerated}`, `FactsInjectionFixture_KeepsHostileTextInsideTheDataObjectStringValue`
- `GenerateFactSheet_{NoFetchedSources_409SourcesInsufficient,ActiveRunExists_409GenerationInProgress,BudgetExceeded_429SpendCapReached}`, `UpdateFactSheet_{NewFactGetsNextId_ExistingIdsPreserved,DeletedFactId_IsNeverReusedInLaterVersion}`, `ApproveFactSheet_{RequiresTwoDistinctCitedFetchedSources,RejectsStaleEtag_412,RejectsWhenEpisodeStoryApproved_409,SupersedesPreviousApprovedRow_OnlyOneApprovedPerEpisode}`
- `IFactSheetGuard_{ReturnsNone_WhenNoApprovedSheet,ReturnsApprovedSheet_WithAliasMap}`, `FactSourcesBuilder_{BuildsFromStorySources_NeverFromModelOutput,UnknownFactRef_ReportedNotBuilt}`

Integration (`PostgresFixture`, `OPENAI_LIVE_TESTS` off): `PostFactSheetGenerate_Returns202_AndCreatesDraftVersion`; `PutFactSheet_MissingIfMatch_Returns428`; `PutFactSheet_StaleEtag_Returns412FactSheetConflict`; `PostFactSheetApprove_ThenGenerateStory_UsesApprovedFacts`; `GenerateStoryJob_StaleFactSheetId_FailsRunNotCounted`.

E2E (02 §7): sources → fact sheet → approve → story with backend `fact_sources`; the injection fixture leaks no instruction.

## 10. Tasks and re-estimate

| Task | Title | Story | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 02.5.3-T1 | Fact prompt + `facts.v1` schema + `OpenAiFactExtractor` + input budget/truncation | 02.5.3 | M | 02.5.2, 02.2.1-T1 | T2 |
| 02.5.3-T2 | `fact_sheets` entity + `GenerateFactSheetCommand` + job + `FactSheetValidator` | 02.5.3 | M | T1, 02.2.2-T3 | 02.5.4-T1, 10.5.5-T1 (10 CR-6) |
| 02.5.4-T1 | Edit/approve with `fs-` ETag + fact-id stability + F13/F26 rules | 02.5.4 | S | 02.5.3-T2 | 02.5.5-T1, 10.5.5-T1 (10 CR-6) |
| 02.5.5-T1 | `IFactSheetGuard` + `FactForWriter` + `FactSourcesBuilder` + staleness | 02.5.5 | S | 02.5.4-T1, 02.2.1, 02.2.2-T2 (port records, CR-24) | 02.2.2-T2 soft (CR-24) |

**Total 3.0 d** (matches the folder assignment; the high-level 02 §6 estimate for 02.5.3–02.5.5 was 3.0 d, unchanged — F30's missing persistence/prompt work is absorbed by the M sizes).

### Open items for the TL

Resolved by TL ruling (README §10 T51–T54, 2026-09-23): `fact_sheet_invalid_content` (400) is adopted (T51); the D6/F12 reading — model returns statements + `source_aliases` only — is confirmed as CR-21 (T52); `confidence` (high|medium|low) is kept in `facts.v1` and in stored facts, with `low` as an advisory UI hint that is never dropped (T53); `Superseded` is accepted, the story link is `story_versions.fact_sheet_id`, and the staleness check lives in 02.3.2-T1/02.3.3-T1 via `IFactSheetGuard` (T54).

Remaining:

- ⚠️ `OpenAi:Effort:Facts` default `low` is unmeasured (D7 says measured in 02.1.3); fact extraction may need `medium`. Re-check with 02.1.3 output.
- ⚠️ `Facts:MaxInputTokens` default must be re-checked against the 10-source × per-source cap once 02.5.2's extraction sizes are known.
- ⚠️ `input_truncated` (CR-4) is recorded by 02.5.3-T1's budget and surfaced to the UI; confirm the label copy with 10.

## 11. Rollout

- No new feature flag: the path is gated by the episode `topic = true_crime`; `source_finder` (02.7) is unrelated.
- Task order: T1 → T2 → 02.5.4-T1 → 02.5.5-T1. Migration `AddFactSheets` (expand only, additive, no backfill) ships before the endpoints/job deploy.
- Monitoring: `openai_tokens_total{operation=fact_sheet}`, `openai_cost_usd_total{operation=fact_sheet}`, `fact_sheet_invalid_output_total`, `moderation_flags_total{stage=fact_sheet}`; alert on the `fact_sheet_generation_failed` rate and `provider_funds_exhausted`.
- Rollback: hide the generate/edit/approve endpoints; the story path then fails closed with `fact_sheet_required` (no other feature reads `fact_sheets`); drop the table last.
