# 02.5.6 — Similarity check

> Refines: `../../../02-story-writing.md` §4 (epic 02.5, story **02.5.6**), §5.9, FR-02-10
> Contract: `../README.md` — **F18**, **D4** (token rule), **D8** (`story_too_similar_to_source`), **D21** (port ownership + stage 6), **D22** (`audit.v1.similarity`), **D19** (no content in logs), **CR-7**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

**Story.** 02.5.6 *Similarity check* — "As the platform I want to catch copied text so that stories stay original." Must · **S** (detailed 0.75 d, §10).

**In scope.** `TextNormalizer`/`Shingler`/`ShingleSet` (Domain, pure); `SimilarityScorer.Score` and `ScoreFact` (for 02.5.3-T2); `ISimilarityCorpusProvider` (sources + past-episodes corpora); the approval guard → 409 `story_too_similar_to_source` (wired by 02.3.2-T1); the `audit.v1.similarity` projection and the `story_versions.similarity_score` write; threshold calibration (02.5.6-T2).

**Out of scope.** Embeddings/semantic similarity → [OPEN] 02.8.1-T2 (F32); lexical only. Cross-language copying → documented residual (F18). Idea novelty (02.8.1-T2 owns its Jaccard). Autopilot gate thresholds (02.8.2-T3 only reads `BlockAbove`). No new route, job, table or migration.

**Refines** §5.9 (single 8-token shingle, warn 5 %, block 15 %) — corrected by F18: per-language `n`, NFC, exclude `user_note`, per-segment shares, fact-statement scoring.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| S1 | §5.9: normalise (lowercase, strip punctuation, keep vi diacritics), 8-word shingles, share of story shingles found in any source text, warn > 5 %, block > 15 % ⚠️ calibrate on the M1 sample stories; "only true-crime and sourced episodes are checked" | `02-story-writing.md` §5.9 ✅ |
| S2 | FR-02-10: a story whose word overlap with any source text exceeds the threshold cannot be approved | §4 ✅ |
| S3 | F18: 8-token vi shingles are 8 **syllables** (≈ 3–4 words) → names/dates/collocations inflate the score; cross-language ≈ 0; `user_note` would count as copying. Fix: NFC + per-language `n` (vi 10, en 8 ⚠️), exclude `user_note`, score fact statements | README F18 ✅ |
| S4 | D4 token rule: NFC, split on Unicode whitespace, each non-empty token = 1; vi token = syllable (V1); column keeps the name `word_count` | README D4 ✅ |
| S5 | D8: `story_too_similar_to_source` = 409, raised by Approve when the score is above the block threshold | README D8 ✅ |
| S6 | D22: `audit.v1.similarity` = `{ score, n, corpus, segments: [{ segment, share }] }`; addressing = `title \| hook \| p:<0-based> \| ending \| meta` | README D22 ✅ |
| S7 | D21 fixes the signatures and makes the generation-time score **advisory**: the block happens at approve | README D21 ✅ |
| S8 | 02.2.2-T3: a corpus failure degrades to `similarityScore = null` + a `similarity_unavailable` local advisory issue, never a failed run | `02.2.2-generate-story-job/plan.md` ✅ |
| S9 | 02.8.2 needs `GetPastEpisodesAsync(seriesId, excludeEpisodeId, max, ct)`, max 50; 02.8.1-T2 consumes `Normalize`/`Hash`/`ShingleSet`/`NFor` | `02.8.2-*`, `02.8.1-*` ✅ |
| S10 | Peer contract **CheckpointSimilarityPlan** (2026-09-24) froze the names: `SimilarityOptions(Language, N, WarnAbove, BlockAbove, MinTokens)`, `SimilarityResult(double Score, int N, string Corpus, IReadOnlyList<SegmentOverlap> Segments)`, `CorpusText(Guid SourceId, string Text)`, `ScoreFact(...) → double`, config `Similarity:WarnAbove/BlockAbove/MinTokens/N:vi/N:en`, `Research:FactSheet:CopiedFactThreshold` (0.5 when frozen, owned here; calibrated to 0.05 by 02.5.6-T2 and dead since 02.5.3-T2, the live key is `Similarity:FactOverlapBlock`) | `local://CheckpointSimilarityPlan` ✅ |
| S11 | Corpus text comes from object storage via `ISourceTextStore.ReadAsync(extractedTextKey, ct)` (02.5.2-T2); source texts are capped at 300,000 chars | README D10 ✅ |
| S12 | `story_versions.similarity_score numeric(5,4)` exists; `similarityScore` is in `currentStory` | 02 §6 · README CR-7 ✅ |
| S13 | No provider call, no cost, no metering: `usage_records` untouched (D14). `System.IO.Hashing` `XxHash64` is MIT/net10.0 ⚠️ verify package + licence at T1 step 1 | README D14 ✅ |

**Closed here.** Per-language `n` replaces §5.9's single 8; the cross-language gap is accepted (§8); §5.9's "true-crime **and** sourced" is read as **sourced** (§10) — any topic with url sources is scored, 0 sources → null score (S8).

## 3. Domain and data

### 3.1 Domain types — `src/NightTales.Domain/Stories/Similarity/`

| File | Type / signature | Notes |
|---|---|---|
| `TextNormalizer.cs` | `static string Normalize(string text, Language lang)` | NFC → `ToLowerInvariant` → strip punctuation → keep diacritics → collapse whitespace → trim. `lang` is part of the frozen signature (S10); today both languages split on whitespace. |
| `Shingler.cs` | `static ulong[] Hash(string normalized, int n)` · `static IReadOnlySet<ulong> Shingles(ReadOnlySpan<string> tokens, int n)` | tokenise on `char.IsWhiteSpace`; hash each n-window with `XxHash64.HashToUInt64(utf8(window joined by '\u001f'))`; `[]` when `tokens.Length < n`. The unit-separator join makes `a b c` ≠ `ab c`. |
| `ShingleSet.cs` | `static IReadOnlySet<ulong> From(ulong[] hashes)` | for 02.8.1-T2's Jaccard. |
| `SimilarityOptions.cs` | `record SimilarityOptions(Language Language, int N, double WarnAbove, double BlockAbove, int MinTokens)` · `static For(Language, SimilaritySettings)` · `static int NFor(Language)` | `NFor` = configured `Similarity:N:<lang>` (defaults vi 10, en 8). |
| `StoryText.cs` / `StorySegment.cs` | `record StoryText(IReadOnlyList<StorySegment>)` · `static From(StoryContentV1)` · `record StorySegment(string Address, string Text)` | addresses `title`, `hook`, `p:0…n`, `ending` (S6). `meta` (description/tags) is **not** scored. |
| `CorpusText.cs` | `record CorpusText(Guid SourceId, string Text)` | `SourceId` = `story_sources.id` or `episode_id`; never logged (D19). |
| `SegmentOverlap.cs` / `SimilarityResult.cs` | `record SegmentOverlap(string Segment, double Share)` · `record SimilarityResult(double Score, int N, string Corpus, IReadOnlyList<SegmentOverlap> Segments)` · `object ToAuditSection()` | `Corpus` ∈ `"sources" \| "past_episodes"`; segments = shares > 0, desc, top 10; `ToAuditSection` emits the D22 shape exactly. |
| `SimilarityScorer.cs` | `static SimilarityResult Score(StoryText, IReadOnlyList<CorpusText>, SimilarityOptions)` (D21 verbatim) · `static bool IsScorable(StoryText, IReadOnlyList<CorpusText>, SimilarityOptions)` · `static double ScoreFact(string, IReadOnlyList<CorpusText>, SimilarityOptions)` | pure, static, no I/O. |
| `SimilarityVerdict.cs` / `SimilarityApprovalGuard.cs` | `enum SimilarityVerdict { Pass, Warn, Block }` · `static SimilarityVerdict Classify(double? similarityScore, SimilarityOptions opt)` | `null → Pass`; `> BlockAbove → Block`; `> WarnAbove → Warn`; else `Pass` (strict `>`). |

### 3.2 Semantics (frozen)

- Story shingles = **union** over segments; corpus shingles = union over texts; `Score = |story ∩ corpus| / |story|` ∈ [0, 1] — the share of the story's shingles found in **any** corpus text (S1). Union, not multiset: a phrase repeated in the story is not double-counted, so the score cannot exceed 1.
- Per-segment share = the same restricted to one segment; emitted only when `> 0`.
- `N` = the language value actually used, carried on the result so `audit.v1.similarity.n` is self-describing.
- **`null`, not zero**: `Score` stays non-nullable (D21), so the skip is decided first — `IsScorable` is false when the story has `< MinTokens` (50) tokens or the corpus is empty; the caller then writes `similarity_score = null` with no audit section, and approve treats `null` as pass (S8).
- `ScoreFact` = share of the statement's shingles present in its **cited** sources; statement `< n` tokens → `0` (no overlap claim).
- Deterministic: seeded `XxHash64`, identical inputs → identical score.

### 3.3 Config (owned here)

```jsonc
{
  "Similarity": {
    "WarnAbove": 0.02, "BlockAbove": 0.45, "FactOverlapBlock": 0.05, "MinTokens": 50,   // calibrated by 02.5.6-T2 (CR-10, 2026-10-17; BlockAbove by the amended rule 3, owner 2026-10-17); the S1 defaults 0.05 / 0.15 / 10 / 8 / 500 were guesses
    "N": { "vi": 14, "en": 14 },
    "Corpus": { "MaxSourceTexts": 10, "MaxSourceTextChars": 300000, "MaxPastEpisodes": 50 },
    "Perf": { "BudgetMs": 163 }            // ceil(2 × 81.375 ms): median of the p95 of 10 runs at n = 14, load sensitive; report: tests/NightTales.Domain.UnitTests/Stories/Similarity/Calibration/similarity-calibration-report.json
  },
  "Research": { "FactSheet": { "CopiedFactThreshold": 0.05 } }   // dead key (no code consumer); the live key is Similarity:FactOverlapBlock, both 0.05 (STATUS CAL-S50-2)
}
```

`SimilaritySettings` (Application, bound from `Similarity`) is the only config the Domain sees, via `SimilarityOptions.For`. `SimilaritySettingsValidator` (`ValidateOnStart`) fails boot when `BlockAbove <= WarnAbove`, `WarnAbove < 0`, `BlockAbove > 1`, any `N < 2`, `MinTokens < 1`, `MaxSourceTexts < 1`, `MaxPastEpisodes < 0`, `MaxSourceTextChars < 1`, `Perf.BudgetMs < 1`. A missing language falls back to `en` + a warning (never a disabled check).

### 3.4 Data — no new tables, no migration

| Object | Access | Owner |
|---|---|---|
| `story_versions.similarity_score numeric(5,4)` (nullable) | write (job/edit), read (approve, 02.8.2-T1) | 02.2.2-T1 column · **here** the value |
| `story_versions.audit_report->'similarity'` | write (job/edit) | 02.2.2-T1 · **here** the section |
| `story_sources` (`kind='url'`, `status='fetched'`, `extracted_text_key`, `text_chars`) | read | 02.5.1-T1 |
| object storage (`extracted_text_key`) | read via `ISourceTextStore` | 02.5.2-T2 |
| `story_versions.body` of the series' earlier episodes | read | 02.2.2-T1 |

## 4. Use cases

| # | Use case | Entry point (owner) | Behaviour |
|---|---|---|---|
| UC1 | Score a generated draft | `GenerateStoryJob` stage 6 `scoring` (02.2.2-T3, D21) | corpus = `LoadAsync(episodeId, lang, ct)`; `IsScorable` false → skip (`null`); else `Score` → `similarity_score` + `audit_report.similarity` in the same insert. Corpus failure → `null` + `similarity_unavailable` advisory (S8); never fails the run. |
| UC2 | Re-score on edit | `EditStoryCommand` (02.3.1-T1) | same against the sources corpus; recomputes the score and audit section, leaves the model-review parts untouched. |
| UC3 | Block approval | `ApproveStoryCommand` (02.3.2-T1) → `SimilarityApprovalGuard.Classify(score, options)` | `Block` → 409 `story_too_similar_to_source` (D8); `Warn` → `Q-SIM-01` (02.3.4-T1); `Pass` → nothing. No stale score: the edit recomputes it (UC2) and, per TL T31, approve recomputes it when `null` with url sources present. The 409 is source-scoped: 02.3.2-T1 runs the similarity check at **step 5** for any topic with url sources (02.3.3-T1 3d; the true-crime gates are 3b/3c/3e). |
| UC4 | Score a fact statement | fact-sheet validator (02.5.3-T2) → `ScoreFact` | statement vs its cited sources; `> CopiedFactThreshold` → an issue whose severity 02.5.3-T2 owns. |
| UC5 | Past-episode corpus | 02.8.2-T1 gate check 10 → `GetPastEpisodesAsync(seriesId, excludeEpisodeId, 50, ct)` | the series' other episodes' current story-version `body`, newest first. Not used by UC1. |
| UC6 | Warn in the UI | read model (10.5.x) | `currentStory.similarityScore` + per-segment shares → editor hints. |

**Authorization/idempotency.** No authorization of its own: it runs inside commands that already authorize `episodeId` ownership; pure and idempotent by construction. No audit-log row (D13) — the edit/approve rows already record `similarity_score` as a number only.

## 5. API and events

**No new route.** Changes owned elsewhere that this feature feeds:

| Surface | Change | Owner |
|---|---|---|
| `POST /episodes/{id}/story/approve` | 409 `story_too_similar_to_source` when blocked (D8) | 02.3.2-T1 |
| `GET /episodes/{id}` → `currentStory` | `similarityScore` (nullable) + `auditReport.similarity` (CR-7, D22) | 02.3.4-T2 / 09.5.3 |
| `POST /episodes/{id}/story` (edit) | response carries the recomputed `similarityScore` | 02.3.1-T1 |

**Events.** None. No outbox message, no SignalR event; `EpisodeUpdated` (09.5.3) needs no new field.

## 6. UI

Owned by 10.5.x (CR-7/CR-9); this feature supplies the numbers and the copy keys.

| State | Trigger | Copy key | Behaviour |
|---|---|---|---|
| Silent | `score ≤ WarnAbove` | — | nothing |
| Warn | `WarnAbove < score ≤ BlockAbove` | `story.similarity.warn` (`Q-SIM-01`) | advisory banner + overlapping segments highlighted from `auditReport.similarity.segments`; edit and approve allowed |
| Block | `score > BlockAbove` | `story.similarity.block` (`Q-SIM-02`) | approve disabled with the reason; the 409 is **source-scoped** (any topic, whenever the episode has url sources — 02.3.2-T1 step 5). The banner lists the overlapping segments and links the cited sources (`fact_sources` titles/URLs, never corpus text) |
| Unknown | `score == null` with ≥ 1 fetched source | `story.similarity.unavailable` | shown for a degraded run (S8); never blocking |

a11y: the banner is `aria-live="polite"`; segment highlight is colour **plus** icon and text, never colour alone.

## 7. Jobs and integrations

| Item | Detail |
|---|---|
| Job | **None.** Called synchronously inside `GenerateStoryJob` (stage 6) and `EditStoryCommand`; no Hangfire job, no queue, no retry policy. |
| I/O | Only `LoadAsync`/`GetPastEpisodesAsync`; failures are caught by the **caller** → `null` + `similarity_unavailable` (S8). The caller applies a 10 s budget (02.2.2-T3 / 02.3.1-T1), not this feature. |
| External calls | None — no OpenAI, no MPT. `usage_records` untouched (D14). |
| Metrics / logging (D19) | `similarity_score{corpus}`, `similarity_unavailable_total{reason}`, `similarity_duration_seconds`; one log line per score: `episodeId`, `storyVersionId`, `corpus`, `n`, `score`, `segmentsOverWarn`, `durationMs`. Labels and ids only — no text, no URLs. |

## 8. Security and privacy

| Threat / rule | Control |
|---|---|
| Corpus leakage | Corpus text never leaves the process: read → normalise → hash; only hashes and aggregate shares survive. Nothing written back, nothing logged (D19). |
| The user's own text counted as copying | `user_note` excluded (F18); only `kind='url'`, `status='fetched'` sources are read. |
| Cross-language copying | **Residual, accepted** (F18): an English source paraphrased into Vietnamese scores ≈ 0. Mitigations: facts-only writer input (02.5.5), the fact prompt's paraphrase rule, `ScoreFact` for copied fact sentences. Documented in `DECISIONS.md` §3 (CR-10) and the T2 report. |
| Hash collision | `XxHash64` for speed on ≤ 10 × 300k-char corpora; a collision can only **raise** a score (birthday bound ≈ 10⁻⁷ over ≤ ~10⁶ shingles ⚠️ T2 confirms). Fix would be `HashToUInt128`, not a threshold change. |
| Resource exhaustion | `MaxSourceTexts` 10, `MaxSourceTextChars` 300k, `MaxPastEpisodes` 50 bound the work; one `HashSet<ulong>` per text. |
| Retention | Nothing stored by this feature; scores and audit sections are deleted with the episode (13.3 / D19). |

## 9. Test plan

| Level | Cases |
|---|---|
| Normalisation | `Normalize_VietnameseDiacritics_AreKept` (`“Tôi đã đi”` → `tôi đã đi`), `…_VietnameseQuotes_AreStripped`, `…_Dashes_AreStripped`, `…_MultipleSpaces_Collapse`, `…_NfcAndDecomposedForms_AreEqual`, `…_TabsAndNewlines_BecomeSpace` |
| Shingling | `Hash_FewerTokensThanN_ReturnsEmpty`, `Hash_WindowBoundary_IsUnambiguous`, `Hash_SameTextTwice_IsDeterministic`, `Shingles_DuplicateWindow_IsDeduplicated` |
| Scoring | `Score_IdenticalText_IsOne`, `Score_UnrelatedText_IsZero`, `Score_LightParaphrase_IsBetween_0_02_And_0_10`, `Score_MostlyCopiedStory_IsAbove_0_20`, `Score_VietnameseUsesN10_EnglishUsesN8`, `IsScorable_StoryShorterThanMinTokens_IsFalse`, `IsScorable_EmptyCorpus_IsFalse`, `Score_ShareIsOverStoryShingles_NotCorpus`, `Score_PerSegmentShare_IsExactForCopiedParagraph`, `Score_SegmentsSortedDescAndTop10` |
| Fact | `ScoreFact_StatementCopiedFromItsSource_IsAboveThreshold`, `ScoreFact_StatementShorterThanN_IsZero`, `ScoreFact_StatementFromAnotherSource_IsLow` |
| Guard / audit | `Guard_ScoreAboveBlock_IsBlock`, `Guard_ScoreAtBlock_IsWarn`, `Guard_NullScore_IsPass`, `ToAuditSection_MatchesD22Shape`, `ToAuditSection_NoContentBeyondAddresses` |
| Config | `SimilaritySettingsValidator_BlockNotAboveWarn_Fails`, `SimilaritySettingsValidator_NBelowTwo_Fails`, `SimilarityOptions_For_MissingLanguageFallsBackToEnN` |
| Integration | `GenerateStoryJob_WithFetchedSources_WritesSimilarityScoreAndAuditSection`, `GenerateStoryJob_WithoutSources_WritesNullScoreAndNoAuditSection`, `EditStoryCommand_RecomputesScore`, `ApproveStoryCommand_BlockedStory_Returns409StoryTooSimilarToSource`, `CorpusReadFailure_DegradesToNullAndAdvisoryIssue`, `CorpusProvider_ExcludesUserNoteAndNonFetchedSources` |
| Perf | `Score_StandardStoryAgainst10x300kChars_WithinBudget` — a Standard story (~1,000 tokens) vs 10 × 300,000 chars; assert p95 ≤ `Similarity:Perf:BudgetMs` over 20 runs and record the measured value for T2 |

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 02.5.6-T1 | `SimilarityScorer` + `TextNormalizer`/`Shingler` + `ISimilarityCorpusProvider` + approval guard | BE | **S (0.5)** | 02.5.1-T1, 02.5.2-T2, 02.2.1-T1 | 02.5.6-T2, 02.2.2-T3, 02.3.1-T1, 02.3.2-T1, 02.8.2-T1, 02.5.3-T2, 02.8.1-T2 |
| 02.5.6-T2 | Threshold calibration (`n`, warn/block, fact threshold, perf budget) | BE + BA | **XS (0.25)** | 02.5.6-T1, 02.1.3-T1 | 02.8.2-T3 |

Total **0.75 d** — matches README §9 ("0.5 → 0.75, per-language shingles + calibration, F18"). No buffer change.

### Open items for the TL

- **[DONE → STATUS `CAL-S50-3`] Perf budget.** `Similarity:Perf:BudgetMs = 500` was a placeholder; 02.5.6-T2 recorded 163 (`ceil(2 × 81.375 ms)`, the median p95 of 10 runs at n = 14, load sensitive). The `10 × 300,000` char corpus shape is fixed by D10 and S9.
- **[DECISION] `Q-SIM-02` scope resolved: source-scoped.** §5.9's "only true-crime **and** sourced episodes are checked" is ambiguous (union vs conjunction); the landed reading is **sourced** — any topic with url sources is checked (02.3.2-T1 step 5, 02.3.3-T1 3d, 02.3.4-T1 `Q-SIM-02`). 0 sources → skip, `null` (S8).
- **[OPEN] Human-path past-episode score.** UC1 scores only the **sources** corpus (D21 stage 6); past-episode similarity is evaluated only by the autopilot gate (02.8.2-T1). Storing a human-path past-episode score is a second corpus read in stage 6 ≈ +0.1 d — not included.
- **[OPEN] Dilution.** The score is a share of the whole story, so one copied paragraph inside a long story can fall under `BlockAbove`. Per-segment shares are stored (D22) and T2 reports the dilution; a per-segment **block** rule would extend the frozen `SimilarityOptions`/guard contract (S10) and is a TL decision.
- **[OPEN] `ScoreFact` severity.** This feature owns the threshold (0.05 after 02.5.6-T2, live key `Similarity:FactOverlapBlock`; 0.5 when frozen); 02.5.3-T2 owns whether the resulting issue is advisory or blocking, and the fact-sheet UX.
- **[OPEN] `meta` not scored.** D22 lists `meta` as addressable; the scorer deliberately excludes `description`/`tags` (FR-02-10 is about prose overlap). Say so before T1 freezes `StoryText.From` if it should change.
- **[OPEN] `similarity_unavailable_total{reason}` label set.** S8 names the issue; a closed reason set (`storage_error`, `db_error`, `timeout`) is owned by 02.5.6-T1 (moved from 02.2.2-T3, which ships without a corpus provider).
- **[OPEN] `plans/features/README.md` index row** for `02.5.6-similarity/` may be expected by the folder convention; it is outside this feature folder and was not edited.

## 11. Rollout

- **Flag:** none — no route and no job, so nothing to gate; it ships active with the writer (02.2.2).
- **Order:** 02.5.6-T1 → 02.5.6-T2 → 02.2.2-T3 stage 6 → 02.3.1-T1 / 02.3.2-T1 → 02.8.2-T1 → 02.5.3-T2.
- **Migration order:** none (§3.4).
- **Fail-open by design:** a missing score never blocks; only a computed score `> BlockAbove` does. A false block therefore requires a real lexical overlap above `BlockAbove` (0.45 after 02.5.6-T2, amended rule 3, owner 2026-10-17; it was 15 %), so partial copying between `WarnAbove` 0.02 and 0.45 only warns (task 02.5.6-T2 Delivery notes, STATUS `CAL-S50-1`).
- **Monitoring (D19):** `similarity_score{corpus}` histogram, `similarity_unavailable_total{reason}`; alert when the latter rises — it silently degrades FR-02-10.
- **Rollback:** `Similarity:BlockAbove = 1.0` (warn-only) via config, no deploy; the score is still stored and the gate still reads it.
- **Calibration gate:** T2's report must show the sample-set separation before the thresholds are called final; the values live in config, so re-calibration is a config change (or a `DECISIONS.md` §3 entry when material, D15 precedent).
