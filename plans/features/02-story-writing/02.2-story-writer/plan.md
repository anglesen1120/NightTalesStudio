# 02.2 — Story writer: OpenAI adapter, prompt catalog, validator, metering

> Refines: `../../../02-story-writing.md` §4 (Epic 02.2: 02.2.1 Structured output, 02.2.3 Inputs from series, 02.2.4 Metering).
> Contract: `../README.md` (D3–D7, D14, D21; F21–F23, F35; facts M1, O1–O9, P5).
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

The **provider adapter and the deterministic guardrails around it**. No HTTP route, no job: `GenerateStoryJob` (02.2.2-T3) orchestrates and calls the steps defined here. Everything is a pure Domain type, an Application port, or the one Infrastructure adapter that talks to OpenAI.

## 1. Scope

In scope (3.5 d, M1 wave 1): the **prompt catalog** (tracked `prompts/`, `manifest.json` + sha256, embedded resources, startup validation, composite `prompt_version` — D5/F22); **prompt assembly** (closed placeholder substitution + one JSON **data object** for untrusted material — D6/F21 — plus injection fixtures); the **output contract** (`StoryContentV1` for `story.v1`, `StoryDraftValidator`, `KeywordRules`, derived fields — D3/D4, CR-5); the **OpenAI adapter** (`OpenAiTextClient : ITextClient`, Responses API, strict JSON schema, SDK retries off, one resilience layer, D7 outcome mapping — O1–O9, F23); `IStoryWriter.WriteAsync` only (D21 step 1); and **metering** (`price_versions` P subset, `TextCostCalculator`, `IUsageMeter`, `IStoryCostEstimator` — D14, F35, CR-2).

Out of scope (no sibling drift): `GenerateStoryCommand`/endpoint/`generation_runs`/`story_versions`/`GenerateStoryJob`/sweeper/invalid-output retry loop/stage transitions/`EpisodeUpdated` → 02.2.2-T1/T2/T3 · `IContentModerator`/`moderation_events`/refusal→`story_moderated` → 02.4.1-T1 · `ReviewAsync`/`ReviseAsync`/`review.v1`/`audit.v1` model section → 02.6.1/02.6.2 · `IFactExtractor`/fact prompt/`FactForWriter`/alias mapping → 02.5.3-T1/02.5.5-T1 · `StoryQualityChecker`/`SimilarityScorer` → 02.3.4-T1/02.5.6-T1 · preset bodies, `story.v1.json`, structure vocab, length-table **values** → 02.1.1-T0/02.1.3-T2.

⚠️ **M0 must be done** (02.1.1-T0 schema/length table, 02.1.3-T2 frozen presets) before integration testing with real prompts; code is written against placeholder files first.

## 2. Confirmed facts

| # | Fact used here |
|---|---|
| M1 | MPT returns plain text + its own Initialization block → cannot validate our schema; M0 uses the story harness (F01) |
| O1 | NuGet `OpenAI` 2.14.0 (2026-09-15), MIT, net10.0 ✅ |
| O2 | `ResponsesClient` + `ResponseTextFormat.CreateJsonSchemaFormat(name, schema, jsonSchemaIsStrict: true)` ✅ |
| O3 | `gpt-6-luna` $0.10 in / $0.01 cached / $0.125 cache-write / $0.50 out per 1M; `gpt-6-sol` $2 / $0.20 / $2.50 / $10; > 272K input → 2× in, 1.5× out; 128K max output ✅ |
| O4 | `max_output_tokens` covers visible **and reasoning** tokens; usage = `input_tokens`, `input_tokens_details.cached_tokens` (+`cache_write_tokens`), `output_tokens`, `output_tokens_details.reasoning_tokens`; cut-off → `incomplete_details.reason = max_output_tokens` ✅ |
| O5 | `reasoning.effort` ∈ `none…max`; `store` defaults **true**; `safety_identifier`, `prompt_cache_key` exist ✅ |
| O6 | Refusal = content part `type:"refusal"`; content filter → `incomplete_details.reason="content_filter"` ✅; no documented code for blocked **input** ⚠️ |
| P5 | Keywords English, 3–12 words, no person names, cap `ceil(D × K)`, K = 6 images/min ✅ |

Not verified (⚠️, carried as [OPEN] items in §10): the SDK 2.14.0 option that disables internal retries; the `x-request-id` accessor shape; `reasoning.effort = none` with strict JSON schema on `gpt-6-luna`.

## 3. Domain and data

### 3.1 Domain types (pure, no I/O)

All under `src/NightTales.Domain/` (folders per 00 §5.2 / F38):

- `Stories/Content/StoryContentV1.cs` (+ `StorySegmentV1`, `StoryStructureV1`) — D3 records: `Title`, `Hook`, `Paragraphs`, `Ending`, `Description`, `Tags`, `ContentWarnings`, `Structure`; a segment = `Text`, `Keywords`, `FactRefs` (paragraphs add `Beat`); structure = `SubGenre`, `Setting`, `Opening`, `TwistType`, `NarratorMode`.
- `Stories/Content/StoryRules.cs` — D3/D4 limits + `LengthTarget` + approved `FactRefs` allow-list + structure vocab.
- `Stories/Content/ValidationResult.cs` — `ValidationIssue`: `RuleId`, `Severity`, `Kind`, `Segment` (D22 addressing), `Evidence`, `RepairDirection`.
- `Stories/Content/StoryDraftValidator.cs` — `ValidationResult Validate(StoryContentV1, StoryRules)` (D21 step 2).
- `Stories/Keywords/KeywordRules.cs` — `IsValid(string)`, `Check(string)`; shared with 03.2.2 (CR-5).
- `Stories/Content/DerivedStoryFields.cs` — `static From(StoryContentV1)` → `Body`, `Keywords`, `WordCount` (D3).
- `Stories/Length/LengthTargetResolver.cs` — `LengthTarget Resolve(LengthTier, Language, PriceLimits)` (D21).
- `Usage/{TextCostCalculator,PriceRates,PriceLimits,PriceVersion}.cs` — pure `decimal Compute(PriceRates, TokenUsage)` (D14).

### 3.2 Validator rule ids (stable; used by the corrective note and `audit.v1`)

| Rule id | Check (D3) |
|---|---|
| `title_too_long`, `metadata_has_markup` | ≤ 100 chars; `title`/`description` carry no markup, URL/domain, @handle, #hashtag, e-mail or phone number (02 T70) |
| `hook_too_long` | hook ≤ 3 sentences |
| `keyword_invalid`, `keyword_count_out_of_range` | each keyword passes `KeywordRules` (English, 3–12 words, no person names, no gore list); 1–2 per segment (exactly 1 at `short`; 02 T65) |
| `segment_count_out_of_range` | within `LengthTarget.MinSegments..MaxSegments` |
| `length_out_of_range`, `length_advisory` | tokens > 15 % outside `MinTokens..MaxTokens` (blocking) / inside 15 % (advisory) |
| `text_has_markup` | no markdown, brackets (incl. `(` `)` — 02 T72), list markers or URLs in `text` |
| `description_too_long`, `tags_invalid` | description ≤ 1,000 chars; ≤ 15 tags, each ≤ 30 chars, total ≤ 400 (F36, same limits as 06.2.1); a tag rejects URL, `<`, `>`, `,`, `#` (02 T70) |
| `fact_ref_unknown` | `fact_refs` ⊆ approved sheet ids (true crime) or empty |
| `beat_invalid`, `narrator_mode_invalid`, `warning_invalid` | D3 enums |
| `structure_vocab_unknown` | free string not in `structure-vocab.v1.json` → stored `other:<text>` by the mapper only (the model emits the plain string); advisory, never fails a run |
| `structure_text_invalid` | unmapped structure free string > 40 chars or with URL/markup — blocking; the mapper strips a model-supplied `other:` prefix (02 T71) |
| `control_characters_invalid` | NUL or another C0 control character other than LF, CR and TAB in a free-text field — blocking; the persisting paths (edit 02.3.1-T1, job 02.2.2-T3) never reach Postgres with one (QA-1) |

The validator never throws and never truncates: it returns issues; the job decides (D21, D7 `InvalidOutput`).

### 3.3 Length targets (D4; values owned by 02.1.1-T0, resolved here)

| Tier | vi tokens | en tokens | Segments | writer / review / revision `max_output_tokens` |
|---|---|---|---|---|
| `short` | 195–260 | 150–200 | 4–5 | 2,500 / 1,200 / 1,500 |
| `standard` | 1,300–2,080 | 1,000–1,600 | 20–32 | 6,000 / 2,500 / 6,000 |
| `long` | 2,600–3,900 | 2,000–3,000 | 40–60 | 10,000 / 3,500 / 10,000 |

Token rule: NFC-normalise, split on Unicode whitespace, each non-empty token = 1 (vi token = syllable, V1). `Story:Length:Rate:vi` = 260 syllables/min, `:en` = 200 words/min — per-language measured rates (02 T103; en ⚠️[INFERENCE], read with vi voice `Hải Đăng`, corroborated by 01.3.1-T4 `wpm 192.5`). K = 6 → segments ≈ `ceil(D × K ÷ 1.5)` for `standard`/`long`; the `short` row is set by its beat floor (`setup → twist → reveal`, one paragraph each, `reveal` optionally folded into `ending`; 02 T65). `short` writer cap is 2,500 (02 T66).

### 3.4 Data

**`price_versions` P subset** (created here — CR-2/F35; 12.1.2 adds credit columns at M2): `version int PK` (monotonic, carried by `RunContext.PriceVersion`), `active_from timestamptz` (`IPriceCatalog.GetActive(queuedAt)` = newest `active_from <= queued_at`), `rates jsonb` = `{ "<model>": { in, cached, cache_write, out } }` per 1M tokens (O3), `limits jsonb` = `{ "story": { "short": { writer, review, revision }, … } }` (D4), `config_hash text` = sha256 of the seed config, `created_at`. Seeded from config `OpenAi:Pricing` at startup when the hash is unknown; rows are **immutable** — a historical `usage_records.price_version` must stay resolvable, so a bad seed is corrected by a **new** row.

**`usage_records`** (table owned by 09; columns written here, D14): `provider`, `operation`, `model`, `input_tokens`, `cached_input_tokens`, `output_tokens`, `reservation_id`, `episode_id`, `estimated`, `cost_usd`, `price_version`, `provider_request_id`. Operations for this feature: `story`; the enum also carries `story_review`, `story_revision`, `fact_sheet`, `bible_proposal`, `idea`, `source_find`, `moderation` (CR-4). Written in its **own transaction** so a failed job keeps its cost.

**`story_versions` columns this feature fills** (table owned by 02.2.2-T1): `content` (`story.v1` + backend-added `schema_version`, `fact_sources`), `prompt_version`, `word_count`, `keywords`, `body`. `fact_sources: [{ source_id, title, url }]` is built by the backend from `story_sources`; **the model never emits URLs or source ids** (D3).

## 4. Use cases

- **UC1** load + validate the prompt catalog at boot (Worker/Api start) → catalog in memory, or **fail start-up** naming the file/placeholder (D5).
- **UC2** assemble the writer prompt (`OpenAiStoryWriter.WriteAsync`) → system prompt (static) + preset + shared rules + one JSON data object (D6).
- **UC3** call OpenAI and map the outcome (`OpenAiTextClient.CallAsync<T>`) → `TextCallResult<StoryContentV1>` (D7), never an exception for control flow.
- **UC4** validate a draft + derive fields (`StoryDraftValidator` + `DerivedStoryFields`) → `ValidationResult`; `body`, `keywords`, `word_count` on every save.
- **UC5** meter a call (`IUsageMeter.RecordAsync`) → one `usage_records` row, cost from the run's `price_version` (D14).
- **UC6** estimate the worst case pre-dispatch (`IStoryCostEstimator.WorstCaseAsync`) → `UsageBound` for `IBudgetGuard`/`ICreditLedger.ReserveAsync` (D1, D21). Per **CR-6 / T46** the story bound is the **full pipeline** — writer + review + revision + review #2, each at its `max_output_tokens` — plus two moderation calls at cost 0; it supersedes 02 §5.7's "writer + review + one revision" bound (F16).

**Invalid-output handshake [DECISION, clarifies F23/D7].** `WriteAsync` returns `InvalidOutput` with the validator rule ids. The **job** (02.2.2-T3) owns the single retry: it re-invokes with `RunContext.Attempt = 2` and a `StoryWriteRequest.ViolatedRules` corrective note that appears as a **data-object field listing rule ids only** (`{"task":"write_story","corrective_note":{"violated_rules":["length_out_of_range","keyword_invalid"]}}`). The invalid text is **never** echoed back (D6: untrusted, and it wastes tokens). Second failure → job sets `story_invalid_output` (D8), `counts_toward_limit = false` (D2). The writer retries nothing itself.

## 5. API and events

No endpoint, no new event type (F24 keeps `EpisodeUpdated`; CR-7 is 02.2.2's). Ports, signatures fixed by D21:

```csharp
// Application/Abstractions/Text/ITextClient.cs
ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct) where T : class;

public sealed record TextCallRequest(RunContext Run, TextStep Step, string SystemPrompt,
    string UserDataJson, string SchemaName, BinaryData Schema, int MaxOutputTokens,
    string PromptCacheKey, string PromptVersion);

public enum TextStep { Writer, Review, Revision, Facts, Bible, Ideas, Novelty, Finder }
```

```csharp
// Application/Features/Stories/Abstractions/IStoryWriter.cs
ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct);
ValueTask<TextCallResult<StoryReviewV1>>  ReviewAsync(StoryReviewRequest req, CancellationToken ct);   // 02.6.1
ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct); // 02.6.2
```

Only `WriteAsync` is implemented here. The two others are declared so the job compiles against the final shape; **their DI registration is absent until 02.6.1/02.6.2 land**, so a missing implementation is a compile/DI-time error, never a runtime surprise.

`TextCallResult<T>` (D7) is a discriminated union (abstract record + sealed cases): `Ok(T Value, TokenUsage Usage, string? RequestId)` · `InvalidOutput(TokenUsage? Usage, string? RequestId, IReadOnlyList<string> RuleIds)` · `Moderated(TokenUsage? Usage, string? RequestId, IReadOnlyList<string> Categories)` · `ProviderFundsExhausted(string? RequestId)` · `ProviderError(int StatusCode, string? RequestId)` · `ProviderUnavailable(string? RequestId)`. Usage is present whenever the response carried it, **including on failures** (D14).

`StoryWriteRequest(RunContext Run, SeriesSnapshot Series, EpisodeSnapshot Episode, LengthTarget Length, BibleSnapshot? Bible, IReadOnlyList<FactForWriter>? Facts, IReadOnlyList<string> AvoidPlots, IReadOnlyList<string>? ViolatedRules)`.

## 6. UI

None. No route, no DTO the UI reads. Per D1 there is **no credits UI in P**; `generation.stage` (CR-7) is 02.2.2's. The only user-visible effects are latency and the `error_code` the job surfaces.

## 7. Jobs and integrations

- **Consumed by `GenerateStoryJob`** (02.2.2-T3): stage `writing` → `IStoryWriter.WriteAsync`; stage `checking` → `StoryDraftValidator.Validate`; stage `saving` → `DerivedStoryFields.From`. Each call records usage via `IUsageMeter` (D21, D14).
- **OpenAI adapter** (`src/NightTales.Infrastructure/OpenAI/`): `OpenAiTextClient` on one typed `ResponsesClient` (`IHttpClientFactory`), `ResponseTextFormat.CreateJsonSchemaFormat(name, schema, jsonSchemaIsStrict: true)` (O2). Per call: `model` from `OpenAi:Models:{step}` (default `gpt-6-luna`), `reasoning.effort` from `OpenAi:Effort:{step}` (`Writer` default `medium`, owner 2026-09-25; other steps `low` ⚠️), `max_output_tokens` from the request, `store = false` (O5), `safety_identifier = sha256hex(user_id)`, `prompt_cache_key = "<step>:<preset>"`, header `X-Client-Request-Id = "<generation_run_id>:<step>:<attempt>"`.
- **Retry budget (F23, D7):** SDK internal retry **disabled** (⚠️ option name in 2.14.0 — `ClientRetryPolicy(maxRetries: 0)` or the `OpenAIClientOptions` equivalent); one `Microsoft.Extensions.Http.Resilience` pipeline per call — timeout (writer 90 s, review 45 s, facts 60 s, others 30 s), 2 attempts on 429/5xx/timeout, jittered backoff honouring `Retry-After`; Hangfire retry 0 for the story job (02.2.2-T3).
- **Outcome mapping** exactly per the D7 table, incl. `429 *_spend_limit_exceeded` / `credit_balance_exhausted` → `ProviderFundsExhausted`; `x-request-id` read from `ClientResult.GetRawResponse().Headers` → `usage_records.provider_request_id` (O8 ⚠️).
- **Prompt catalog** loaded at boot (D5); no disk reads at request time (embedded resources).

## 8. Security and privacy

- **Injection (D6, F21):** all untrusted material (brief, bible, facts, avoid-plots, structure plan, corrective note) travels in **one JSON object** in the user message, serialised with `System.Text.Json`; the system prompt states the two-tier rule — the control keys (`task`, `length`, `structure_plan`, fact ids) are parameters to follow, every material string is written about and never an instruction (02 T69). No untrusted text is substituted into a template placeholder; the placeholder list is closed (D5): `language_name`, `tier_minutes`, `token_range`, `segment_range`, `keyword_rules`, `ear_rules`, `overused_phrases`, `structure_vocab` — all server-derived.
- **Caching order (O9):** system (static) → preset → shared rules → data object **last**; the first three together are the cacheable head (≥ 1,024 tokens — 02 T67). `store = false` (O5).
- **Logging (D19):** no prompt, brief, story/fact/bible text, source text, title or URL is ever logged — logs carry ids, codes, token counts, model, latency, `prompt_version`, registrable domains only.
- **Attribution and keys:** `safety_identifier = sha256(user_id)` (hex); `OPENAI_API_KEY` from the runtime secret manager (01/11), never in `appsettings.json`.
- **Cost safety:** every call sets `max_output_tokens` from the run's `price_version` (D14); `IBudgetGuard` (D1) runs before dispatch; the 272K-input surcharge (O3) is a calculator/estimator rule, never a silent 2× surprise.

## 9. Test plan

Unit (no network). Plan-level contract: **one test per rule id of §3.2 and per outcome of D7**; the exhaustive named lists live in each task file's `## Tests`.

- `StoryDraftValidator` (10) — e.g. `Validate_TitleOver100Chars_TitleTooLong`, `Validate_Tokens15PercentOver_LengthOutOfRange`, `Validate_FactRefNotInApprovedSheet_FactRefUnknown`, `Validate_ValidDraft_NoBlockingIssues`.
- `DerivedStoryFields` / `KeywordRules` / `LengthTargetResolver` / `TextCostCalculator` — the 3 cost worked examples + the 272K surcharge case; `KeywordRules_ThirteenWordKeyword_Invalid`; `LengthTargetResolver_StandardVi_Returns1300To2080And20To32Segments`.
- `PromptCatalog` / `PromptAssembler` — missing preset / sha256 mismatch / unknown placeholder fail at start-up; the data object is a single JSON value; system prompt first, data object last; one test per injection fixture.
- `OpenAiTextClient` / `UsageMeter` — one test per D7 outcome; `…_429ThenOk_RetriesOnceAndSucceeds`; `UsageMeter_FailedJob_StillWritesRow`.

Integration (TestKit, no live OpenAI): `GenerateStoryJob_InvalidThenValid_RetriesOnceWithCorrectiveNoteRulesOnly` (second request's data object has `violated_rules`, **not** the first draft's text), `GenerateStoryJob_TwoInvalidOutputs_FailsRunNotCounted`, arch test `PromptCatalog_NoDiskReadAtRequestTime` (no `File.`/`FileStream`/`Directory.` in the prompt-loading path; D5, F22).

Manual (M1 §7): one live `WriteAsync` per tier × language with `OPENAI_LIVE_TESTS=1` (D20); record token/latency numbers for D4/12.1.1.

## 10. Tasks and re-estimate

| Task | Title | Size | Area | Depends on | Blocks |
|---|---|---|---|---|---|
| 02.2.3-T1 | Prompt catalog + manifest + startup validation | S (0.5) | BE | 02.1.4-T1, 02.1.1-T0 | 02.2.3-T2, 02.2.1-T2 |
| 02.2.3-T2 | Prompt assembly + data object + injection tests | S (0.5) | BE | 02.2.3-T1 | 02.2.1-T2 |
| 02.2.1-T1 | `StoryContentV1` + `StoryDraftValidator` + derived fields + `KeywordRules` | M (1.0) | BE | 02.1.1-T0 | 02.2.1-T2, 02.3.4-T1, 03.2.2 |
| 02.2.1-T2 | `OpenAiTextClient` + `OpenAiStoryWriter.WriteAsync` | M (1.0) | BE | 02.2.1-T1, 02.2.3-T2, 02.2.4-T1 | 02.2.2-T3 |
| 02.2.4-T1 | `price_versions` P subset + `TextCostCalculator` + `IUsageMeter` + `IStoryCostEstimator` | S (0.5) | BE | 09 persistence | 02.2.1-T2, 02.2.2-T2, 09.6.1-T1 (09 W2 CR-9), 12.8.1-T1, 12.1.4-T1, 12.1.2-T1, 12.5.2-T1 |

Total **3.5 d** — matches README §9 (02.2 writer 2.0 → 3.5: prompt catalog, adapter hardening, retry budget, `price_versions` in P; F22, F23, F35). Order: 02.2.4-T1 ∥ 02.2.3-T1 → 02.2.3-T2 ∥ 02.2.1-T1 → 02.2.1-T2.

**Open items for the TL** (contract gaps found here; README not edited):

- [OPEN] **SDK retry-off option name** in `OpenAI` 2.14.0 unverified (⚠️). If it cannot be disabled, D7's "SDK retries off" needs a workaround (strip the SDK retry handler, or keep the SDK default and drop Polly to 1 attempt) — changes the F23 retry budget, so it needs a ruling.
- [OPEN] **`x-request-id` accessor** (`ClientResult.GetRawResponse().Headers`) is a System.ClientModel pattern not shown in OpenAI docs (O8 ⚠️); if unavailable, `provider_request_id` is best-effort null.
- [OPEN] **`IStoryWriter` with two unimplemented methods**: D21 fixes one interface, but `ReviewAsync`/`ReviseAsync` are owned by a later wave. Either accept a split (`IStoryWriter` + `IStoryReviewer`) or accept that their DI registration lands with 02.6.1 — this plan assumes the latter. Confirm.
- [OPEN] **`reasoning.effort = none`** with strict JSON schema on `gpt-6-luna` unverified; the `Writer` default is `medium` (owner 2026-09-25, D7) and the other steps default to `low`. ⚠️ measure in 02.1.3.
- [OPEN] **`IPriceCatalog`** (D21 `GetActive(queuedAt)`) has no declared owner; this plan puts it in 02.2.4-T1 (`Infrastructure/Persistence/Queries/PriceCatalog.cs`). Confirm 02.2, not 12.
- ⚠️ **Prompt files do not exist yet** at M1 start (02.1.4-T1/02.1.1-T0 produce them); 02.2.3-T1 must run against placeholder files and the M0→M1 gate must be explicit.

## 11. Rollout

- **Flag:** none — inert until `GenerateStoryJob` calls it. The effective gate is **02-M1-core** (`../README.md` §7): generate → edit → approve end-to-end with metering.
- **Sequence:** 02.2.4-T1 ∥ 02.2.3-T1 → 02.2.3-T2 ∥ 02.2.1-T1 → 02.2.1-T2 → 02.2.2-T3 wires the job.
- **Config added:** `OpenAi:Models:{step}`, `OpenAi:Effort:{step}`, `OpenAi:Pricing` (rates → `price_versions` seed), `Story:Length:Rate:{vi|en}`, `OpenAi:RequestTimeout:{step}`; secrets (`OPENAI_API_KEY`) from the secret manager.
- **Observability:** metrics `openai_tokens_total{operation,model,kind}`, `openai_cost_usd_total{operation}`, `story_invalid_output_total` (D19); logs carry `prompt_version`, `generation_run_id`, `step`, `attempt`, `provider_request_id`, token counts — never content.
- **Rollback:** swap in the TestKit `FakeTextClient` via config locally; in production, disable generation at the endpoint (02.2.2) — this feature has no independent switch.
- **Post-M0 follow-up:** once 02.1.3-T2 freezes the presets, re-run the live per-tier × language write to re-calibrate `Story:Length:Rate` and the D4 `max_output_tokens` (CR-6).
