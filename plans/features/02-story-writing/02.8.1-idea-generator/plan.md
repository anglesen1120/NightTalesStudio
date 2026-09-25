# 02.8.1 — Idea generator and variety guard (feature plan)

> Refines: `../../../02-story-writing.md` stories 02.8.1 (Epic 02.8) and 02.8.4, §5.1, §6 (replaces tasks 02.8.1-T1 and 02.8.4-T1). Contract: `../README.md` — facts (O3–O5, O7, P1, P3), findings F28, F31, F32, F33, F34, F38, decisions D1/D2/D3/D4/D6/D7/D8/D12/D13/D14/D15/D17/D19/D21/D22, task template §6, gates §7, index §8, re-estimate §9. Layout: `../../../00-overview.md` §5.2, §6, §7, §8, §10.1. Policy: `DECISIONS.md` §6, §10.12.
> Legend: ✅ verified (source) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

| | |
|---|---|
| Stories | **02.8.1 Idea generator** — *As the channel owner I want the system to come up with and pick episode ideas so that I do not have to.* Must (P) · M · **02.8.4 Variety guard** — *As the channel owner I want episodes that do not look mass-produced so that the channel stays monetizable.* Must (P) · S |
| Tasks | 02.8.1-T1 `episode_ideas` + idea prompt/schema + generation step (M 1.0) · 02.8.1-T2 novelty (lexical + judge) + content-policy pre-check + scoring/pick (S 0.5) · 02.8.4-T1 variety guard: structure tags, rotation, template-repeat block (S 0.5) = **2.0 d** |
| In scope | `episode_ideas` table (00 §6 + CR-4 `structure`/`novelty`); `ideas.v1` prompt + strict schema; the generation step (`IIdeaGenerator`); `IIdeaPlanner.ProposeAndPickAsync` — the service 02.8.2/02.8.3 call; novelty (lexical shingles + one judge call) and the content-policy pre-check; the scoring formula and the pick; `generation_runs` `kind = ideas` + metering `operation = idea`; the variety guard (rules over the last K episodes, steering, post-write verification) |
| Out of scope | The autopilot scheduler, `autopilot_runs` state machine, gates and pause triggers (02.8.2-T1/T2/T3, 02.8.3-T1…T4 — they **call** `IIdeaPlanner`); the UI (10.4.3 run log, 10.5.7 — this plan lists states and copy keys only); source finding (02.7, behind flag `source_finder`); the writer prompt itself (02.2.3) |
| Acceptance (story 02.8.1, restated) | Given a series with autopilot on (topic, language, tone, series bible, banned themes), the model proposes N ideas (title, logline, hook, setting); each idea is checked against past episodes and pending ideas and against the content policy; the best-scoring idea fills the next free slot and is stored in `episode_ideas` |
| Acceptance (story 02.8.4, restated) | Autopilot rotates settings, sub-genres, openings and scene styles, and blocks an idea whose structure repeats a recent episode's template |

## 2. Confirmed facts

| # | Fact used here | Status / source |
|---|---|---|
| O3 | `gpt-6-luna` $0.10 in / $0.01 cached / $0.50 out per 1M; Structured Outputs on Responses | ✅ README §1 O3 |
| O4 | `max_output_tokens` covers visible + reasoning tokens; cut-off → `incomplete_details.reason = max_output_tokens` | ✅ README §1 O4 |
| O7 | `omni-moderation-latest` is **free**; the category list; no documented text-length limit ⚠️ | ✅ README §1 O7 |
| O9 | Prompt caching needs a stable ≥ 1,024-token prefix; static first, dynamic data last | ✅ README §1 O9 |
| P1 | `ICreditLedger` is metering-only in P; `CreditOperation` includes `idea` (D1) | ✅ README §1 P1, D1 |
| D2 | `generation_runs`: `kind` includes `ideas`; `series_id` NOT NULL, `episode_id` nullable; partial unique `(series_id, kind) WHERE episode_id IS NULL AND status IN ('queued','running')` (TL ruling 2026-09-24) | ✅ README D2 |
| D3 | `story.v1` `structure` = `{ sub_genre, setting, opening, twist_type, narrator_mode }` mapped to `prompts/shared/structure-vocab.v1.json`; beats enum `setup|build_up|clue|twist|reveal|aftermath` | ✅ README D3 |
| D12 | `IContentModerator.CheckAsync(texts, stage, ct)`; stages `brief` / `draft` / `autopilot_gate`; `omni-moderation-latest`; `moderation_events` on every flag; usage `operation = moderation` cost 0 | ✅ README D12 |
| D14 | `usage_records.operation` includes `idea` (CR-4); `IUsageMeter.RecordAsync` writes its own transaction | ✅ README D14 |
| D17 | One recurring `AutopilotPlanJob` tick every 15 min; `autopilot_runs.slot_at` unique per series; per run ≤ 1 idea pick; idempotency keys `autopilot:{seriesId}:{slotAt:O}:{step}` | ✅ README D17 |
| D21 | `RunContext(Guid GenerationRunId, Guid UserId, Guid SeriesId, Guid? EpisodeId, ReservationId Reservation, GenerationTrigger Trigger, int Attempt, string PriceVersion)` (12 CR-17); every step is a service owned by one feature | ✅ README D21 + StoryWriterPlan contract 2026-09-24 |
| F28 | Three schedulers collapsed into one tick; `slot_at` is the idempotency key | ✅ README §2 F28 |
| F32 | "similarity below a threshold ⚠️" had no method → lexical shingles on title+logline+setting **plus** a cheap judge call against the last 100 summaries; embeddings [OPEN] | ✅ README §2 F32 |
| F33 | True-crime autopilot depends on 02.7 (go/no-go); if no-go the run stops at `waiting_owner` with "add sources" | ✅ README §2 F33 |
| — | 00 §6 `episode_ideas`: `user_id, series_id, title, logline, hook, score, similarity, status (proposed/picked/rejected/used), episode_id, rejected_reason`; CR-4 adds `structure jsonb`, `novelty jsonb` | ✅ 00 §6, README CR-4 |
| — | 02.8.1 story: ideas come from "the series bible, the story presets and optional research notes (02.7)" | ✅ `02-story-writing.md` §4 |
| — | DECISIONS §6: the "inauthentic content" policy (renamed 2025-07-15) makes mass-produced content non-monetizable; personal phase replaces human review with automated gates + a variety guard + a daily cap + a veto window (§10.12) | ✅ `DECISIONS.md` §6, §10.12 |
| — | CheckpointSimilarityPlan (confirmed): `TextNormalizer.Normalize(string, Language)` → NFC, invariant lower, strip punctuation, keep diacritics; `Shingler.Hash(string, int)` → `ulong[]` (XxHash64 over n-token windows); `ShingleSet.From(ulong[])`; `SimilarityOptions.NFor(lang)` = vi 10, en 8 | ✅ peer contract 2026-09-24 |
| — | StoryWriterPlan (confirmed): `StructurePlan` (Domain `Stories/Structure/StructurePlan.cs`) with `required` (single value per dimension) / `forbidden` (arrays per dimension); data-object key `structure_plan`; `EpisodeSnapshot(..., StructurePlan? Structure)` | ✅ peer contract 2026-09-24 |
| — | AutopilotOpsPlan (confirmed): 02.8.3 creates the `generation_runs` row (kind `ideas`, trigger `autopilot`, key `autopilot:{seriesId}:{slotAt:O}:idea`), reserves `idea` + budget-checks, calls `ProposeAndPickAsync`, then creates the episode and calls `MarkUsedAsync` | ✅ peer contract 2026-09-24 |

Closed by this plan: F32 (method fixed), F28 (the tick owns scheduling; this feature owns only the pick), F34 (`idea` is a metering operation). ⚠️ open: the novelty thresholds and the scoring weights (both configurable, to be calibrated), and whether ideas are cost-absorbed (plan §10 item 3).

## 3. Domain and data

### 3.1 Table `episode_ideas` (CR-4 owns it; this task owns the migration)

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | UUIDv7 |
| `user_id` | `uuid` NOT NULL | denormalised from `series`; `IUserOwned` (09.2.3) |
| `series_id` | `uuid` NOT NULL | FK → `series.id` |
| `generation_run_id` | `uuid` NOT NULL | FK → `generation_runs.id` (traceability: which paid call produced it) |
| `title` | `varchar(100)` NOT NULL | validated ≤ 100, no `<`/`>` |
| `logline` | `varchar(300)` NOT NULL | |
| `hook` | `varchar(300)` NOT NULL | |
| `setting` | `varchar(120)` NOT NULL | free text (the *tag* lives in `structure`) |
| `structure` | `jsonb` NOT NULL | `{ sub_genre, setting, opening, twist_type, narrator_mode, beats: [...], beat_signature: "setup>clue>twist>reveal" }` — D3 vocabulary, `other:<text>` for unmapped values |
| `novelty` | `jsonb` NULL | `{ lexical: { score, max_overlap_kind, max_overlap_id }, judge: { score, fit, hook_strength, reason, max_overlap_episode_id }, corpus: { episodes, pending_ideas, shingle_n } }` |
| `score` | `numeric(5,4)` NULL | the final weighted score (§3.4) |
| `similarity` | `numeric(5,4)` NULL | `max(lexical.score, judge.score)` — the column 00 §6 already defines; the UI shows it (10.4.3) |
| `status` | `varchar(16)` NOT NULL | `proposed`/`picked`/`rejected`/`used` (00 §6) |
| `rejected_reason` | `varchar(40)` NULL | `not_best`, `novelty_lexical`, `novelty_judge`, `policy_banned_theme`, `policy_moderated`, `variety_triple_repeat`, `variety_template_repeat`, `variety_tag_cap`(soft, only with a penalty), `invalid`, `duplicate` |
| `episode_id` | `uuid` NULL | FK → `episodes.id`; set with `status = used` |
| `created_at`, `updated_at` | `timestamptz` NOT NULL | |

Indexes and constraints: `IX_episode_ideas_series_status_created (series_id, status, created_at DESC)`; partial unique `UX_episode_ideas_episode (episode_id) WHERE episode_id IS NOT NULL` (one idea per episode); `IX_episode_ideas_generation_run (generation_run_id)`; check `status IN (…)`.

Migration `<ts>_AddEpisodeIdeas` (expand only) with the table + indexes; no backfill (greenfield).

### 3.2 Content schema `ideas.v1` [DECISION]

`prompts/schemas/ideas.v1.json` (strict: `additionalProperties: false`, every property required):

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["ideas"],
  "properties": {
    "ideas": {
      "type": "array", "minItems": 1, "maxItems": 10,
      "items": {
        "type": "object", "additionalProperties": false,
        "required": ["title", "logline", "hook", "setting", "structure", "beats"],
        "properties": {
          "title": { "type": "string" },
          "logline": { "type": "string" },
          "hook": { "type": "string" },
          "setting": { "type": "string" },
          "structure": {
            "type": "object", "additionalProperties": false,
            "required": ["sub_genre", "setting", "opening", "twist_type", "narrator_mode"],
            "properties": {
              "sub_genre": { "type": "string" }, "setting": { "type": "string" },
              "opening": { "type": "string" }, "twist_type": { "type": "string" },
              "narrator_mode": { "enum": ["first_person", "third_person"] } } },
          "beats": { "type": "array", "minItems": 3, "maxItems": 8,
                     "items": { "enum": ["setup","build_up","clue","twist","reveal","aftermath"] } }
        } } } }
}
```

`IdeaValidator` (Domain, pure) enforces what strict mode cannot (F08): `title` ≤ 100, `logline`/`hook` ≤ 300, `setting` ≤ 120, no `<`/`>` in any field, no markdown/list markers, `beats` with no immediate repetition and containing at least one of `twist`/`reveal`. Failures drop that candidate (reason `invalid`) rather than failing the run when at least one valid candidate remains; a batch with zero valid candidates fails the run `ideas_invalid_output`.

### 3.3 Table and EF mapping

Files:

- `src/NightTales.Domain/Autopilot/Ideas/EpisodeIdea.cs` — entity + methods `static EpisodeIdea CreateProposed(…, IdeaCandidate candidate, DateTimeOffset now)`, `void MarkPicked(decimal score, IClock clock)`, `void MarkRejected(string reason, IClock clock)`, `void MarkUsed(Guid episodeId, IClock clock)`, `void SetSignals(decimal score, decimal similarity, IdeaNoveltyDetail novelty, IClock clock)`.
- `src/NightTales.Domain/Autopilot/Ideas/IdeaCandidate.cs` — `record IdeaCandidate(string Title, string Logline, string Hook, string Setting, StoryStructureTags Structure, IReadOnlyList<string> Beats)`; `IdeaStructureTags` reuses `StoryStructureTags` from `Stories/Structure/` (D3) plus a `Beats` list. `IdeaStructure` (the jsonb payload) = `record IdeaStructure(StoryStructureTags Tags, IReadOnlyList<string> Beats, string BeatSignature)` with `static string Signature(IReadOnlyList<string> beats) => string.Join(">", beats)`.
- `src/NightTales.Domain/Autopilot/Ideas/IdeaNoveltyDetail.cs` — the jsonb payload of `novelty`.
- `src/NightTales.Domain/Autopilot/Ideas/IdeaValidator.cs`, `IdeaScorer.cs` (§3.4), `IdeaRejectionReason.cs` (const strings, one place for the reason codes).
- `src/NightTales.Domain/Stories/Structure/StructurePlan.cs` (owned here per the peer contract) — `record StructurePlan(StructureRequirements Required, StructureForbidden Forbidden)`; `record StructureRequirements(string? SubGenre, string? Setting, string? Opening, string? TwistType)`; `record StructureForbidden(IReadOnlyList<string> SubGenre, IReadOnlyList<string> Setting, IReadOnlyList<string> Opening, IReadOnlyList<string> TwistType)`.
- `src/NightTales.Domain/Stories/Structure/VarietyGuard.cs` — §3.5.
- `src/NightTales.Infrastructure/Persistence/Configurations/EpisodeIdeaConfiguration.cs` — table, `jsonb` columns via `IdeaJsonContext`, indexes/constraints above.
- Migration `<ts>_AddEpisodeIdeas`.

### 3.4 Scoring [DECISION, ⚠️ weights to calibrate]

`IdeaScorer.Score(IdeaSignals s, IdeaWeights w) → decimal` (pure):

```
noveltyComponent = 1 − max(s.LexicalScore, s.JudgeScore)      // 0..1, higher is fresher
varietyComponent = s.VarietyPenalty                            // 0 = clean, 0.5 = soft cap hit, 1 = hard block (never scored, rejected)
score = clamp01( w.Novelty*noveltyComponent + w.Fit*s.JudgeFit + w.Hook*s.JudgeHookStrength − w.Variety*varietyComponent )
```

Weights from `Autopilot:Ideas:Weights:{Novelty=0.45, Fit=0.30, Hook=0.25, Variety=0.35}`. `JudgeFit` / `JudgeHookStrength` come from the same judge call as the novelty score (§4 U3) so no extra call is paid. Ties break by the candidate's index in the batch (stable, deterministic). A candidate with `VarietyPenalty = 1` is rejected before scoring (`variety_triple_repeat` / `variety_template_repeat`).

### 3.5 Variety guard [DECISION]

`VarietyGuard` (Domain, pure) over the series' structure history:

```csharp
public static VarietyBatchAssessment AssessBatch(IReadOnlyList<EpisodeStructureRef> history, VarietyOptions opt);
public static VarietyDecision Evaluate(IdeaCandidate candidate, VarietyBatchAssessment batch, VarietyOptions opt);
public static IReadOnlyList<VarietyIssue> VerifyWritten(StoryStructureTags written, StructurePlan plan);

public sealed record VarietyBatchAssessment(IReadOnlyDictionary<string,int> SubGenre, … /* one per dimension */,
                                            IReadOnlyList<EpisodeStructureRef> Recent, IReadOnlyList<EpisodeStructureRef> LastFive,
                                            StructureRequirements SuggestedRequired, StructureForbidden SuggestedForbidden);
public sealed record VarietyDecision(decimal Penalty, string? RejectReason, IReadOnlyList<string> SoftViolations);
public sealed record VarietyIssue(string Rule, string Dimension, string Detail);
```

| Rule | Definition | Effect |
|---|---|---|
| R1 triple repeat | the candidate's `(sub_genre, opening, twist_type)` equals that of any of the last `WindowK` = **10** episodes | hard block, `variety_triple_repeat` |
| R2 tag share cap | adding the candidate would make a tag value exceed `TagShareMax` = **40 %** of the window (i.e. > 4 of 10 → 5 of 11) | soft, `variety_tag_cap` penalty 0.5, and the value is added to `forbidden` for the batch |
| R3 template repeat | the candidate's `beat_signature` **and** `opening` **and** `twist_type` all equal those of any of the last `TemplateWindowK` = **5** episodes | hard block, `variety_template_repeat` |
| R4 rotation | for each of the four dimensions, the least-used value in the window with count < `RotationMinCount` = **3** becomes `required` (one value per dimension, deterministic tie-break = the value with the lowest count, then alphabetical) | steering only (no rejection) |
| R5 first episodes | fewer than 2 episodes with a structure in the history | no requirements, no rejections (a new series must not be blocked) |

- `narrator_mode` is **not** a rotation dimension (it follows the preset/topic); it is not part of R1/R3.
- The batch assessment is computed once per run and passed to the idea prompt as `required_tags`/`forbidden_tags`, so the model mostly avoids violations; `Evaluate` still filters.
- `VerifyWritten` (called by the 02.8.2 gate, per the peer contract) returns `structure_plan_forbidden_used` (gate failure) and `structure_plan_missing` (advisory — the model maps free strings to vocab ids and may land on `other:*`). `narrator_mode` mismatches are ignored (the preset owns it).

`IEpisodeStructureHistory` (Application `Features/Autopilot/Ideas/Abstractions`):

```csharp
ValueTask<IReadOnlyList<EpisodeStructureRef>> GetRecentAsync(Guid seriesId, int max, CancellationToken ct);
public sealed record EpisodeStructureRef(Guid EpisodeId, string Title, DateTimeOffset ApprovedAt,
                                        StoryStructureTags Structure, string BeatSignature);
```

Implementation (`Infrastructure/Autopilot/EpisodeStructureHistory.cs`): one no-tracking SQL projection over `episodes e JOIN story_versions sv ON sv.id = e.approved_story_version_id` where `e.series_id = @seriesId AND e.approved_story_version_id IS NOT NULL`, ordered by `sv.created_at DESC` (⚠️ the version's creation time is a proxy for the approval time; a version can only be approved while current, so the order is right in practice), `LIMIT @max`. The structure tags come from `sv.content -> 'structure'`; `beat_signature` is derived from the paragraph `beat` values in order (`content -> 'paragraphs' -> i ->> 'beat'` joined with `>`), computed in SQL with `jsonb_array_elements` or in memory after the projection (choose in memory: ≤ 10 rows, clearer).

```sql
SELECT e.id, sv.title, sv.created_at, sv.content
FROM episodes e
JOIN story_versions sv ON sv.id = e.approved_story_version_id
WHERE e.series_id = @seriesId AND e.approved_story_version_id IS NOT NULL
ORDER BY sv.created_at DESC
LIMIT @max;
```

## 4. Use cases

The only entry point used by other features is `IIdeaPlanner` (Application `Features/Autopilot/Ideas/Abstractions`):

```csharp
public interface IIdeaPlanner
{
    ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(Guid seriesId, DateTimeOffset slotAt, RunContext run, CancellationToken ct);
    ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct);
}
public sealed record IdeaPick(Guid IdeaId, string Title, string Logline, string Hook, string Setting,
                              StoryStructureTags Structure, bool RequiresResearch, decimal Score, decimal Similarity);
```

`Error` cases: `IdeaErrors.NoIdea(IdeaRejectionReason reason)` → the caller (02.8.3) records the run outcome; `IdeaErrors.InvalidOutput()` / `Moderated()` / `GenerationFailed()` / `ProviderFundsExhausted()` / `SpendCapReached()` mirror D7.

### U1 `ProposeAndPickAsync` (called by 02.8.3-T1)

Preconditions: the caller has created the `generation_runs` row (kind `ideas`, trigger `autopilot`, `idempotency_key = autopilot:{seriesId}:{slotAt:O}:idea`, `counts_toward_limit = false`), reserved `CreditOperation.Idea` and passed the budget guard. Steps:

1. **Context** — `IIdeaContextProvider.GetAsync(seriesId, ct)`:
   `IdeaContext(string Topic, string Language, string? Tone, IReadOnlyList<string> BannedThemes, BibleSnapshot? Bible, IReadOnlyList<EpisodeStructureRef> History, IReadOnlyList<string> ResearchNotes, int Count)`.
   `Tone` and `BannedThemes` read from `series.autopilot_settings` (schema owned by 02.8.3-T1, TL ruling #3 — a dependency, not an open request; §10 item 1). `ResearchNotes` = up to 5 of the series' newest `story_sources` (any status): for `kind = user_note` the `note_text` truncated to 400 chars, for `kind = url` the `title` only (never the extracted text, D19); empty when there are none.
2. **Variety steering** — `VarietyGuard.AssessBatch(history, opt)`; `Required`/`Forbidden` go into the prompt.
3. **Generate** — `IIdeaGenerator.GenerateAsync(IdeaGenerationRequest, ct)` → one paid call (§7.1).
4. **Validate** — `IdeaValidator`; invalid candidates are dropped (`invalid`), duplicates within the batch by normalised title are dropped (`duplicate`); zero survivors → run fails `ideas_invalid_output`.
5. **Persist as `proposed`** — one row per surviving candidate, `generation_run_id` set (so the run log can show the pool).
6. **Novelty** (§4 U2) → per candidate `lexical` + one judge call.
7. **Policy** (§4 U3) → per candidate.
8. **Variety filter** (§3.5) → per candidate.
9. **Score + pick** (§3.4) → the winner `picked`, the others `rejected` with their reason (`not_best` when they only lost the ranking); `similarity` = `max(lexical, judge)`.
10. **Result** — `IdeaPick` with `RequiresResearch = (topic == true_crime)` (F33: 02.8.3 then needs sources before writing; if 02.7 is no-go, the run ends `waiting_owner` — that decision is 02.8.3's).
11. **Settle** — the caller settles the reservation; this service only reports. (02.8.3 owns settle/refund so one place decides the run outcome.)

### U2 Novelty

`IIdeaNoveltyChecker.CheckAsync(batch, corpus, ct) → ValueTask<ErrorOr<NoveltyBatchResult>>`:

- **Corpus** (`IIdeaCorpusProvider.GetAsync(seriesId, ct)`): (a) the newest 100 approved episodes of the series (`{ episodeId, title, summary }` from `story_versions.content` / the bible summaries — use `story_versions.title` + `content.description`? **Use the same source as the run log and the prompt: the newest 100 `story_versions` rows of approved episodes, `title` + `content.description` truncated to 200 chars**), and (b) the series' `episode_ideas` with `status IN ('proposed','picked')` (pending, not yet written) `{ ideaId, title, logline }`.
- **Lexical** (pure, Domain): for each candidate, normalise `title + " " + logline + " " + setting` with `TextNormalizer.Normalize(text, language)`, shingle with `Shingler.Hash(normalized, SimilarityOptions.NFor(language))` (vi 10, en 8 — CheckpointSimilarityPlan), Jaccard = `|A ∩ B| / |A ∪ B|` via `ShingleSet` against every corpus entry; keep the max. Cost 0, no I/O.
- **Judge** (one call for the whole batch): `prompts/ideas/novelty.v1.md` + `prompts/schemas/novelty.v1.json` (strict):
  ```json
  { "scores": [ { "index": 0, "max_overlap_episode_id": "uuid|null", "score": 0.0, "fit": 0.0, "hook_strength": 0.0, "reason": "≤ 300 chars" } ] }
  ```
  Input data object: `{ "task": "score_ideas", "language": "vi", "topic": "ghost", "ideas": [{index, title, logline, hook, setting}], "past_episodes": [{index, title, summary}] }` (≤ 100 past episodes, each ≤ 200 chars → ~22k chars ≈ 8k tokens; `max_output_tokens` 400; cost ≈ $0.001). `store = false`, `prompt_cache_key = "ideas:novelty"`, `TextStep.Novelty`.
  [DECISION] One call for the batch (not one per idea): cheaper, consistent, and the model sees the candidates together so it cannot score two similar candidates as both fresh. A batch response is a faithful extension of F32's per-idea `{max_overlap_episode_id, score, reason}`.
  Judge failure (`InvalidOutput` after one retry / `ProviderError` / `ProviderUnavailable`): **degrade to lexical only** — do not fail the run for a scoring helper; record `novelty.judge = null`, `IdeaScorer` then uses `noveltyComponent = 1 − lexical` and `Fit = 1.0`, `HookStrength = 0.5` (neutral) and the run continues. `Moderated` → treat as a judge failure too (the candidates are separately policy-checked in U3). `ProviderFundsExhausted` → propagate (the caller pauses).
- **Thresholds** (`Autopilot:Ideas:Novelty:{LexicalMax=0.35, JudgeMax=0.70}` ⚠️ calibrate in the 02.8.2-T3 dry run): `lexical > LexicalMax` → reject `novelty_lexical`; `judge.score > JudgeMax` → reject `novelty_judge`. Both are evaluated on the *raw* scores, not the weighted one.

### U3 Content-policy pre-check

`IIdeaPolicyChecker.CheckAsync(candidates, context, ct) → ValueTask<PolicyBatchResult>`:

1. **Banned themes** (deterministic, free): for each banned theme, normalise (NFC + invariant lower, diacritics kept) and test containment as a whole-word sequence against `title + logline + hook + setting` → reject `policy_banned_theme` (⚠️ substring matching is crude; the prompt also forbids them, so this is a backstop).
2. **Moderation**: `IContentModerator.CheckAsync([logline, hook], ModerationStage.Brief, ct)` per candidate (D12, `omni-moderation-latest`, free, `operation = moderation` cost 0). `flagged` → reject `policy_moderated` + a `moderation_events` row (`source = openai_text`, `stage = brief`, `categories`, `excerpt` ≤ 300 chars of the flagged text). The blocking-category list of D12 applies as-is for `brief` (any flag rejects).
3. **True-crime framing** (F31/F33): for `topic = true_crime`, a candidate whose text contains a framing phrase from the lexicon (vi/en "câu chuyện có thật", "based on a true story") or a dated-incident pattern (`\b(19|20)\d{2}\b` next to a crime noun) → **not rejected**, but `RequiresResearch = true` is forced and the idea is stored with a note in `novelty.reason`? No: keep the signal in `rejected_reason = null` and let `RequiresResearch` carry it (02.8.3 then requires sources). This mirrors F31's "any hit → owner review" for the *story* gate; at idea level the answer is "research required".
4. No moderation call at all when the batch is empty.

### U4 `MarkUsedAsync(ideaId, episodeId)`

- Called by 02.8.3 after the episode is created. `status = used`, `episode_id` set (the partial unique index enforces one idea per episode). Wrong status (`rejected`/`used`) → `Error.Conflict` `idea_invalid_state` (02.8.3 treats it as a bug, not a user error). Tenant filter on; `SystemContext(ownerId)`.

## 5. API and events

**No public route in P.** Ideas are produced and consumed by the autopilot run; the owner sees them in the run log (10.4.3) through the existing `GET /series/{id}/autopilot/runs` (02.8.3-T2), whose `idea` payload is filled from `autopilot_runs.idea_id` → `episode_ideas` (`{ id, title, logline, score, similarity, status, rejectedReason }` for the picked idea plus the rejected siblings of the same `generation_run_id`). The DTO shape is specified here so 02.8.3-T2 can project it:

```json
{ "idea": { "id": "…", "title": "…", "logline": "…", "hook": "…", "setting": "…",
            "structure": { "sub_genre": "haunted_house", "opening": "night", "twist_type": "revenant",
                           "setting": "rural_house", "narrator_mode": "first_person",
                           "beats": ["setup","clue","twist","reveal"], "beat_signature": "setup>clue>twist>reveal" },
            "score": 0.7421, "similarity": 0.0812, "status": "picked", "requiresResearch": false },
  "otherIdeas": [ { "id": "…", "title": "…", "score": 0.5, "similarity": 0.31, "rejectedReason": "novelty_judge" } ] }
```

Events: none new. `generation_runs` + `usage_records` are the observability; 02.8.3 emits `EpisodeUpdated` for the episode it creates. [OPEN] A `SeriesIdeasProposed` SignalR event is not needed in P (the run log is read on demand) — same reasoning as the bible (02.6.3 §5).

`generation_runs` row written by the caller: `kind = ideas`, `series_id` NOT NULL, `episode_id = NULL`, `trigger = autopilot`, `idempotency_key = autopilot:{seriesId}:{slotAt:O}:idea`, `counts_toward_limit = false`, `stage` ∈ `queued → scoring → saving → done` (reuse D2's stage list: `queued`, `scoring`, `saving`, `done`; `writing` is not used for ideas — `scoring` is the closest existing value and the D2 list is a closed enum, so **no new stage value is added**), `models jsonb = { "ideas": "gpt-6-luna", "novelty": "gpt-6-luna" }`, `prompt_version` composite, `attempt`.

## 6. UI

Reference stories: **10.4.3** (autopilot run log: "each run shows the idea picked (with rejected ideas and similarity), gate results, render and publication status, and the reason for any pause") and 10.4.3's advanced settings (gate thresholds). This plan supplies the data and the copy keys.

| UI state (10.4.3) | Backend source | Copy key | vi | en |
|---|---|---|---|---|
| Run with a picked idea | `autopilot_runs.idea_id` → `episode_ideas` | `autopilot.ideas.picked` | "Ý tưởng đã chọn: {title}" | "Idea picked: {title}" |
| Rejected ideas list | same `generation_run_id`, `status = rejected` | `autopilot.ideas.rejected` | "Ý tưởng bị loại ({n})" | "Rejected ideas ({n})" |
| Reason: too similar (lexical) | `novelty_lexical` | `autopilot.ideas.reason.noveltyLexical` | "Trùng từ ngữ với tập trước" | "Too close in wording to a past episode" |
| Reason: too similar (judge) | `novelty_judge` | `autopilot.ideas.reason.noveltyJudge` | "Nội dung quá giống tập {episode}" | "Too similar to episode {episode}" |
| Reason: banned theme | `policy_banned_theme` | `autopilot.ideas.reason.bannedTheme` | "Nằm trong chủ đề bị cấm" | "Uses a banned theme" |
| Reason: moderated | `policy_moderated` | `autopilot.ideas.reason.moderated` | "Bị kiểm duyệt nội dung chặn" | "Blocked by content moderation" |
| Reason: template repeat | `variety_template_repeat` | `autopilot.ideas.reason.templateRepeat` | "Lặp lại cấu trúc của tập gần đây" | "Repeats a recent episode's structure" |
| Reason: triple repeat | `variety_triple_repeat` | `autopilot.ideas.reason.tripleRepeat` | "Trùng thể loại/mở đầu/cú twist" | "Same sub-genre, opening and twist" |
| Reason: not best | `not_best` | `autopilot.ideas.reason.notBest` | "Không phải ý tưởng tốt nhất" | "Not the best-scoring idea" |
| Reason: invalid / duplicate | `invalid` / `duplicate` | `autopilot.ideas.reason.invalid` | "Không hợp lệ" | "Invalid output" |
| No idea at all | `NoIdea(reason)` → the run outcome is `failed` (02.8.3) | `autopilot.ideas.none` | "Không tạo được ý tưởng nào hôm nay" | "No idea could be generated" |
| True crime needs sources | `requiresResearch` | `autopilot.ideas.requiresResearch` | "Cần nguồn trước khi viết (tội phạm có thật)" | "Sources are needed before writing (true crime)" |
| Similarity shown | `similarity` | `autopilot.ideas.similarity` | "Trùng lặp {pct}%" | "{pct}% overlap" |

Accessibility: the run log row is a `<table>` with a caption; the reason keys are text, never colour-only; `similarity` is rendered as a percentage with the raw value in `title`.

## 7. Jobs and integrations

### 7.1 The generation step

`IIdeaGenerator.GenerateAsync(IdeaGenerationRequest req, ct) → ValueTask<TextCallResult<IdeasV1>>`, implemented by `OpenAiIdeaGenerator` (Infrastructure/OpenAI) on `ITextClient`:

- Request: `TextStep.Ideas`, model `OpenAi:Models:Ideas` (default `gpt-6-luna`), `reasoning.effort` `OpenAi:Effort:Ideas` (default `low`), `max_output_tokens` from `price_versions.limits.ideas.max_output_tokens` (seeded from `OpenAi:Pricing:Limits`; no runtime fallback — README §10 T56), schema `ideas.v1.json` (strict), `store = false`, `safety_identifier = sha256(userId)`, `prompt_cache_key = "ideas:generate"`, header `X-Client-Request-Id = {generationRunId}:ideas:{attempt}`.
- System prompt `prompts/ideas/generate.v1.md`: role (a story-idea writer for a narration channel), the topic/language/tone, the ban on real named individuals and on the banned themes, the four structure dimensions and the beat vocabulary, the requirement to honour `required_tags` and never use `forbidden_tags`, and the data-object rule (D6). `true_crime` gets an explicit "you propose an **angle**, never facts; the facts come from sources later" instruction (F33).
- Data object: `{ "task": "generate_ideas", "language": "vi", "topic": "ghost", "tone": "…", "count": 5, "banned_themes": ["…"], "bible": {…}, "recent_episodes": [{ "title", "summary" }], "required_tags": { "sub_genre": "…", "opening": "…" }, "forbidden_tags": { "sub_genre": ["…"], "opening": ["…"] }, "research_notes": [{ "kind": "user_note", "text": "…" }] }`.
  `recent_episodes` = the newest `Bible:Prompt:MaxSummaries` (50) bible summaries; the bible itself uses `BibleSnapshotBuilder` (02.6.3-T3) so the same budget applies.
- Outcome mapping (D7): `Ok` → validate; `InvalidOutput` → one retry at the caller level then run `ideas_invalid_output`; `Moderated` → run `ideas_moderated` **and writes one `moderation_events` row** (`source = openai_text`, `stage = "ideas"` — TL ruling #3, 2026-09-24: a refusal on the run's own paid call writes `stage = kind`, distinct from T2's per-candidate `stage = brief` pre-check); `ProviderFundsExhausted` → `provider_funds_exhausted` (pause); `ProviderError`/`ProviderUnavailable` → `ideas_generation_failed`. All three `*_` codes are **confirmed D8 additions** (TL ruling #3).

### 7.2 Timing inside the tick

`AutopilotPlanJob` (02.8.3-T1, every 15 min, D17) calls `ProposeAndPickAsync` for a slot inside `now + buffer_days`. Budget of the whole idea step: 2 calls (generate + judge) + ≤ 5 free moderation calls, ≈ $0.003 [INFERENCE]. The step must finish inside the tick window: `ideas` timeout 30 s (D7 "others 30 s") + judge 30 s → ≤ 60 s + moderation ≈ 70 s, far below 15 min. A slot whose idea step exceeds the tick is retried by the next tick with the same `idempotency_key` (the run row is unique per `(series, slot_at)`), so no duplicate idea is bought.

### 7.3 Dependencies

`ITextClient`, `TextStep`, `IUsageMeter`, `TextCostCalculator`, `price_versions` — 02.2.4-T1 / 02.2.1-T2 (TL ruling #2). `IContentModerator` — 02.4.1-T1. `ICreditLedger`/`IBudgetGuard` — D1. `BibleSnapshotBuilder` — 02.6.3-T3. `TextNormalizer`/`Shingler`/`ShingleSet`/`SimilarityOptions` — 02.5.6-T1 (CheckpointSimilarityPlan). `IJobScheduler` — 09.2.6.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Cross-tenant ideas | `episode_ideas.user_id` from the series; `IUserOwned` + global filter; `MarkUsedAsync` runs under `SystemContext(ownerId)` with filters on (09.2.3) |
| Prompt injection through the bible or research notes | Everything is inside the one D6 data object; research notes are truncated to 400 chars and the extracted source text is never sent (D19); injection fixtures for `recent_episodes`/`research_notes` are added to `prompts/fixtures/injection/` by this task |
| Generating real-person / defamatory ideas | The prompt forbids real named individuals (13 §5.3); `true_crime` ideas are angles only and force `RequiresResearch`; the story-level real-world detector (02.8.2-T2) and the owner approval of the fact sheet remain the real gate (F31, F33) |
| Mass-produced content (policy) | The variety guard (02.8.4) blocks template repeats, caps tag shares and steers rotation; DECISIONS §6/§10.12 accept the residual monetization risk knowingly for the owner's own fiction channel |
| Cost abuse via endless idea generation | `IBudgetGuard` (D1) before every paid dispatch; the judge is one call per batch; the caller's idempotency key prevents a re-pick per slot (D17) |
| Moderation bypass | Every candidate is moderated at `stage = brief` before any writer call (D12, T2's pre-check); the idea-generation call itself is also subject to the model's own refusal (`Moderated` → `stage = ideas`, above); the story is moderated again at `draft` |
| Content in logs | D19: log ids, counts, scores, token counts, `prompt_version`, latency — never idea text, bible text, notes or source titles |
| Retention | `episode_ideas` is deleted with the series (a series-level row, like `series_bibles`, D19); `usage_records` rows keep `episode_id` null ⚠️ confirm with 13.3 |

## 9. Test plan

Unit (`tests/NightTales.Domain.UnitTests/Autopilot/Ideas/`, `…/Stories/Structure/`):

| Test | Assert |
|---|---|
| `IdeaValidator_TitleOver100_DropsCandidate` | drop, not fail, while another candidate is valid |
| `IdeaValidator_BeatsWithoutTwist_DropsCandidate` | must contain `twist` or `reveal` |
| `IdeaValidator_AngleBracketInLogline_DropsCandidate` | no `<`/`>` |
| `IdeaValidator_AllCandidatesInvalid_ReturnsEmpty` | the caller then fails `ideas_invalid_output` |
| `IdeaScorer_HigherNoveltyAndFit_Wins` | weighted sum, clamped 0..1 |
| `IdeaScorer_TieBreaksByIndex` | deterministic |
| `IdeaScorer_HardBlockedCandidate_NotScored` | penalty 1 → rejected before scoring |
| `VarietyGuard_TripleRepeatWithin10_RejectsWithTripleRepeat` | R1 |
| `VarietyGuard_TemplateRepeatWithin5_RejectsWithTemplateRepeat` | R3 (signature + opening + twist) |
| `VarietyGuard_SameOpeningAndTwistButDifferentBeats_NotTemplateRepeat` | R3 requires all three |
| `VarietyGuard_TagAt4Of10_PenaltyAndForbidden` | R2 |
| `VarietyGuard_RotationPicksLeastUsedValue` | R4 + tie-break |
| `VarietyGuard_EmptyHistory_NoRequirementsNoRejections` | R5 |
| `VarietyGuard_VerifyWritten_ForbiddenTagUsed_ReturnsGateFailure` | `structure_plan_forbidden_used` |
| `VarietyGuard_VerifyWritten_MissingRequiredTag_ReturnsAdvisory` | `structure_plan_missing` |
| `VarietyGuard_NarratorModeNotARotationDimension` | narrator_mode ignored |
| `Jaccard_IdenticalTexts_IsOne_ParaphraseBelowThreshold` | lexical scorer with fixed strings, vi and en |
| `Jaccard_VietnameseTitles_UsesN10` | `SimilarityOptions.NFor(vi) == 10` |

Integration (`tests/NightTales.Api.IntegrationTests/Autopilot/Ideas/` + Worker job tests, `FakeTextClient`/`FakeContentModerator`/`FakeClock`/`FakeBudgetGuard` from the D20 kit):

| Test | Assert |
|---|---|
| `ProposeAndPick_PersistsAllCandidatesAndPicksHighest` | N rows, one `picked`, others `rejected` with reasons |
| `ProposeAndPick_LexicalOverThreshold_RejectsWithoutJudgeCall` | judge call count 0 when every candidate is lexically blocked? (it is one batch call — assert the judge is skipped when no candidate survives the lexical gate) |
| `ProposeAndPick_JudgeCallIsOnePerBatch` | exactly 1 judge call for N=5 |
| `ProposeAndPick_JudgeFails_DegradesToLexicalOnly` | run succeeds, `novelty.judge = null` |
| `ProposeAndPick_BannedThemeMatch_RejectsCandidate` | `policy_banned_theme` |
| `ProposeAndPick_ModeratorFlags_RejectsAndWritesModerationEvent` | `policy_moderated` + `moderation_events.stage = brief` |
| `ProposeAndPick_TrueCrime_SetsRequiresResearch` | F33 hand-off |
| `ProposeAndPick_VarietyBlocksAllCandidates_ReturnsNoIdea` | `NoIdea` + reason `variety_template_repeat` |
| `ProposeAndPick_InvalidOutput_RetriesOnceThenFailsRun` | `attempt = 2`, `ideas_invalid_output` |
| `ProposeAndPick_RecordsUsageWithOperationIdeaAndModeration` | `usage_records` rows (`idea` with cost, `moderation` with 0) |
| `ProposeAndPick_SecondCallSameSlot_IsIdempotentAtTheRunLevel` | the caller's unique `(series, slot_at)`; this service is not re-entered (assert the caller contract) |
| `MarkUsed_SetsStatusAndEpisode_ThenSecondCallReturnsConflict` | one idea per episode (partial unique index) |
| `MarkUsed_OtherUsersIdea_ReturnsNotFound` | tenant filter |
| `EpisodeStructureHistory_ReturnsNewestApprovedOnly` | drafts excluded, order by version creation desc |
| `EpisodeStructureHistory_BeatSignatureFromParagraphBeats` | derived from `content.paragraphs[].beat` |
| `Ideas_InjectionFixtureInResearchNotes_Ignored` | schema-valid output, instruction ignored |

Manual: read the run log for a real autopilot tick and confirm the picked idea plus the rejected reasons render (10.4.3, out of this plan's scope but the DTO must be right).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.8.1-T1 | `episode_ideas` + idea prompt/schema + generation step | M 1.0 | 02.6.3-T3 (bible snapshot), 02.2.4-T1 (`ITextClient`/metering), 09.2.2 (EF/migrations) | T2, 02.8.4-T1, 02.8.3-T1 |
| 02.8.1-T2 | Novelty (lexical + judge) + content-policy pre-check + scoring/pick | S 0.5 | T1, 02.5.6-T1 (`TextNormalizer`/`Shingler`), 02.4.1-T1 (`IContentModerator`), 02.6.3-T3 | 02.8.3-T1 |
| 02.8.4-T1 | Variety guard: structure tags, rotation, template-repeat block | S 0.5 | T1, 02.2.1-T1 (`StoryStructureTags`/vocab) | 02.8.2-T1 (gate verification), 02.8.3-T1 |
| **Total** | | **2.0 d** | | |

Matches README §9 (02.8.1/02.8.4 ideas + variety: 1.5 → 2.0 d, "+0.5 for the novelty judge", F32). No re-estimate difference.

### Open items for the TL

1. **`series.autopilot_settings` `tone`/`banned_themes`** — **resolved** by TL ruling #3 (2026-09-24): 02.8.3-T1 owns the `autopilot_settings` schema and adds `tone` (string ≤ 120) and `banned_themes` (string[] ≤ 20 × ≤ 60); this plan consumes them as a dependency on 02.8.3-T1, not a request against 00 §6.
2. **D8 job-outcome codes, D12 stage and the stale-run sweeper** — **resolved** by TL ruling #3: `ideas_invalid_output`/`_moderated`/`_generation_failed` (plus `provider_funds_exhausted` from D7) are confirmed D8 additions; a `Moderated` refusal on the idea-generation call and on the novelty judge call each write `moderation_events` with `stage = "ideas"` (kind-based, distinct from T2's per-candidate `stage = brief`); `StaleGenerationSweeperJob` (02.2.2-T3) is confirmed to cover `kind = ideas`.
3. **Is `idea` cost-absorbed?** D14 adds the metering operation `idea`; 12.1.1's priced list is `story_generate`, `fact_sheet`, `render` and says "series-bible proposals and publishing are free (cost absorbed)" — ideas are not mentioned. Request: state explicitly that ideas are cost-absorbed (free) in P/M2 like bible proposals, or add a price.
4. **`price_versions.limits` needs `ideas.max_output_tokens` (1,500) and `novelty.max_output_tokens` (400)** (D14 says the limits live there). **Resolved** by README §10 T12/T56 (seeded via `OpenAi:Pricing:Limits`, no runtime fallback).
5. **`generation_runs.stage` has no `generating`/`ideas` value.** This plan reuses `scoring`/`saving`/`done` from D2's closed list and does not add a value; if the run log wants a distinct "proposing ideas" stage, D2 needs the extra enum value.
6. **Embeddings for novelty remain [OPEN] (F32).** This plan implements lexical + judge; embeddings are not needed for M1 and would add a provider dependency.
7. **F33 hand-off:** `RequiresResearch = true` for true-crime ideas is only a flag. If 02.7 is no-go, 02.8.3 must end the run `waiting_owner` with "add sources"; that behaviour belongs to 02.8.3-T1 and is requested here.
8. **The novelty thresholds (0.35 / 0.70) and the scoring weights are uncalibrated ⚠️.** The 02.8.2-T3 dry run over the first N real runs is the calibration vehicle; both live in config so no code changes are needed.
9. **`episode_ideas` column gap.** 00 §6 / CR-4 list `user_id, series_id, title, logline, hook, score, similarity, status, episode_id, rejected_reason` + `structure jsonb`, `novelty jsonb`, but story 02.8.1 requires each idea to carry a **`setting`** (the story lists "title, logline, hook, setting") and traceability needs **`generation_run_id`**. This plan adds both columns (plus `created_at`/`updated_at`). Request: add them to 00 §6 and CR-4.
10. **Variety guard constants** (`WindowK=10`, `TemplateWindowK=5`, `TagShareMax=0.40`, `RotationMinCount=3`) are first guesses in config; no contract change, but the values should be recorded in `DECISIONS.md` §3 after the first calibration like the D15 model decision.
11. **`structure_plan` is a new top-level D6 data-object key with no formal contract entry.** README D6's literal example (`{ "task", "brief", "length", "bible", "facts", "avoid_plots" }`) predates the variety guard and does not show `structure_plan: { required, forbidden }` (02.8.4-T1). StoryWriterPlan, PresetsM0Plan and GenerateJobPlan already serialise/consume against this key by peer agreement (2026-09-24), but it is a **shared contract other feature plans depend on**, not a local 02.8.4 detail — it belongs as a top-level sibling of `bible`/`avoid_plots` (never nested inside `BibleSnapshot`, which stays series-memory-only for O9 cache placement). Request: add `structure_plan` to D6's example and to D21's `EpisodeSnapshot` field list in the README.

## 11. Rollout

- **Feature flags:** none of its own. The feature is reachable only through autopilot, which is behind the series' `autopilot_enabled` + the 02.8.3 authorization; a series without autopilot never generates an idea. `Autopilot:Variety:Enabled` (default true) can disable the guard's rejections (steering and penalties only) if it blocks too much during calibration.
- **Migration order:** `AddEpisodeIdeas` with T1, before T2. Expand only, no backfill.
- **Ship order:** T1 (generation step, verifiable by calling `ProposeAndPickAsync` from a test) → T2 (novelty/policy/scoring — until then the picker is "first valid candidate") → 02.8.4-T1 (variety) → 02.8.3-T1 wires the tick.
- **Monitoring:** `idea_generation_duration_seconds`, `idea_candidates_total{outcome}` (picked/rejected by reason), `idea_novelty_lexical_score` and `idea_novelty_judge_score` (histograms — the calibration input), `openai_tokens_total{operation="idea"|"novelty"}`, `openai_cost_usd_total{operation="idea"}`, `moderation_flags_total{stage="brief"}`, `variety_rejections_total{rule}`, `generation_runs{kind="ideas",status}`.
- **Rollback:** disable autopilot on the series (02.8.3) — no new ideas are produced; the `episode_ideas` rows are inert. `Autopilot:Variety:Enabled=false` relaxes the guard. No data rollback.
- **M2 note:** for SaaS users the idea generator is not exposed in P; if it is later, the same `IIdeaPlanner` runs under `SystemContext(userId)` and the ledger becomes real, so no structural change is needed.
