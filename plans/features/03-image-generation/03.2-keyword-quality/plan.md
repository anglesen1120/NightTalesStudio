# 03.2 — Keyword quality (scene budget, render keyword linter)

> Refines: `../../../03-image-generation.md` §4 epic 03.2 (stories **03.2.1**, **03.2.2**), FR-03-3, NFR-03-1, G3, G4, §5.3
> Contract: `../README.md` — **D3** (scene budget), **D4** (submit fields), **D8** (worst case), **D10** (codes), **D11** (linter), **F03**, **F10**, **F17**, **F18**, **CR-4**, **CR-9**, **CR-13**; `../../02-story-writing/README.md` **D3**, **D4**, **D22**, **CR-5**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.**
- 03.2.1 *Keyword cap* — "As the platform I want a cap on keywords so that cost stays predictable." Must · S (detailed 0.5 d).
- 03.2.2 *Keyword linter* — "As the platform I want risky keywords blocked so that fewer images are moderated and no real person is depicted." Must · M (detailed 0.75 d).

**In scope.**
- `RenderImagePlanner` (Domain): clip seconds, needed images, `MaxTerms`, deterministic trim, hints, slot → segment map, and `IImageCostEstimator.WorstCase` (03.2.1-T1).
- `RenderKeywordLinter` + `NameCorpus` (Domain) on top of 02's `KeywordRules` (03.2.2-T1).
- Wiring the linter into the story quality checker, `ApproveStoryCommand` and `RenderEpisodeCommand` → 409 `keywords_flagged` (03.2.2-T2).

**Out of scope.**
- `KeywordRules` itself (02.2.1-T1, 02 CR-5) and the keyword prompt rules (02.1.2-T1).
- The editor chips and the disabled "Make video" button (10.5; this feature supplies the codes).
- Moderation of scene prompts (02.8.2-T2 on the autopilot path; human path → README §10 O6).
- The render command and jobs themselves (09.6.1–09.6.3); this feature supplies pure services they call.

**Refines** 03.2.1 "trimmed/merged to ≤ `ceil(D × K)`" → D3 (F03: MPT's image count is `ceil(audio ÷ clip)`, so the lever is `video_clip_duration` + a term cap with spares; "merge" dropped).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| K1 | MPT generates images in term order and stops when `successes × clip ≥ audio`; skipped terms are replaced by the next term; clips loop when short (I7, I8) | README §1 ✅ |
| K2 | `video_clip_duration` default 5 (I13) → 12 images/min unless we set it | README §1 ✅ |
| K3 | Story keywords: 1–2 per segment, English, 3–12 words; derived `keywords` = hook ++ paragraphs ++ ending in order; segment addresses `hook`, `p:<i>`, `ending` | 02 README D3, D22 ✅ |
| K4 | D4 per-language rates (02 T103): vi 260 syllables/min, en 200 words/min (en ⚠️ `[INFERENCE]`: measured with the vi voice, T103) (config `Story:Length:Rate:{lang}`); tiers short ≤ 1 min, standard 5–8, long 10–15 | 02 README D4 ✅ |
| K5 | `KeywordRules.Check(string) → KeywordViolation` (word count, ASCII charset, name heuristic, gore list) in `Domain/Stories/Keywords/`; the validator reports `keyword_invalid` | 02.2.1-T1 ✅ |
| K6 | `IStoryRenderInputProvider.GetAsync(storyVersionId) → StoryRenderInput(VideoSubject, VideoScript, Keywords)`; 09.6.1 calls it with `approved_story_version_id` | 02 README D21, T28 ✅ |
| K7 | Fact sheets: `facts jsonb (id, statement, source_ids, confidence)`, one approved sheet per episode; `story_sources.title` for fetched url sources | 00 §6; 02 README CR-4, T54 ✅ |
| K8 | `StoryQualityChecker.Check(StoryContentV1, StoryCheckContext) → QualityReport` produces hints on every save; objective blocking issues block approve | 02.3.4-T1 ✅ |
| K9 | 10.5: flagged keywords → red chips, "Make video" disabled | 10 §4 ✅ (P10) |

**Closed here.** Cap formula and trim order (D3). Name corpus = source titles + approved fact statements (D11). `keyword_limit_exceeded` withdrawn (F18).

## 3. Domain and data

### 3.1 Types

| File | Type / signature |
|---|---|
| `Domain/Rendering/Images/RenderImagePlanInput.cs` | `sealed record RenderImagePlanInput(IReadOnlyList<SegmentKeywords> Segments, int ScriptTokens, Language Language, LengthTier Tier, ImagePlanOptions Options)` |
| `Domain/Rendering/Images/ImagePlanOptions.cs` | `sealed record ImagePlanOptions(int ClipSeconds, double SpareShare, double SparseShare, int RateVi, int RateEn)` — from `Images:*` and `Story:Length:Rate:*` |
| `Domain/Rendering/Images/RenderImagePlan.cs` | `sealed record RenderImagePlan(IReadOnlyList<PlannedTerm> Terms, int ClipSeconds, int NeededImages, int MaxTerms, int TrimmedCount, IReadOnlyList<string> HintCodes)`; `PlannedTerm(int Slot, string Segment, string Keyword)` |
| `Domain/Rendering/Images/RenderImagePlanner.cs` | `static RenderImagePlan Plan(RenderImagePlanInput input)` |
| `Domain/Stories/Keywords/SegmentKeywords.cs` | `sealed record SegmentKeywords(string Segment, IReadOnlyList<string> Keywords)` |
| `Domain/Stories/Keywords/NameCorpus.cs` | `sealed class NameCorpus { static NameCorpus From(IEnumerable<string> texts); static NameCorpus Empty; bool Matches(string keyword, out string matchedRun) }` |
| `Domain/Stories/Keywords/RenderKeywordLinter.cs` | `static IReadOnlyList<ValidationIssue> Lint(IReadOnlyList<SegmentKeywords> segments, NameCorpus names)` |
| `Application/Abstractions/Rendering/IImageCostEstimator.cs` | `CostBound WorstCase(Aspect aspect, int maxTerms, int priceVersion)` (implementation in Application, reads `IPriceCatalog`) |
| `Application/Abstractions/Rendering/INameCorpusProvider.cs` | `ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct)` |

`ScriptTokens` = `StoryText.Tokenize(videoScript).Count` (02 D4 token rule).

### 3.2 `render_jobs.params.image_plan` (jsonb, non-content only, P8)

```json
{ "clip_seconds": 10, "needed": 30, "max_terms": 34, "planned": 34, "trimmed": 2,
  "hints": ["scenes_trimmed"], "slot_segments": ["hook", "p:0", "p:0", "p:1", "…"],
  "style_prompt_version": "style=dark-folklore-ink@1a2b3c4d;neg=v1" }
```

`slot_segments` holds addresses only (no keyword text). It lets 03.4.2-T1 turn a failed `slot` into a scene the UI can highlight.

### 3.3 Migration

None (`render_jobs.params` is jsonb, 00 §6).

## 4. Use cases

| Use case | Change | Error |
|---|---|---|
| `StoryQualityChecker` (02.3.4) | adds `RenderKeywordLinter` issues (`keyword_name_detected`, blocking objective) and the planner hints (`scenes_trimmed`, `scenes_sparse`, advisory) to the quality report on every save | — (hints) |
| `ApproveStoryCommand` (02.3.2) | runs the linter with the episode's `NameCorpus`; blocking issue → reject | 409 `keywords_flagged` + `errors[] { segment, index, rule }` |
| `RenderEpisodeCommand` (09.6.1) | re-runs the linter (defence in depth; the fact sheet may have changed); computes the plan; `IImageCostEstimator.WorstCase` → `IBudgetGuard.CheckAsync` (CR-4); stores `image_plan` | 409 `keywords_flagged`; 429 `spend_cap_reached` |
| `SubmitRenderJob` (09.6.3) | recomputes the plan + `ScenePromptBuilder` prompts from the approved version; if `style_prompt_version` or `planned` differs from params → overwrite params and log `image_plan_recomputed` (template changed between command and submit) | — |

The linter needs `SegmentKeywords`, which `StoryRenderInput` does not carry today (K6) → **CR-13** to 02.3 (T28): add `IReadOnlyList<SegmentKeywords> Segments` to `StoryRenderInput`.

## 5. API and events

- No new route. `keywords_flagged` (409) is added to 09 §5.4 (CR-9) with `errors[]` using 02 D22 addressing: `{ "segment": "p:3", "index": 1, "rule": "keyword_name_detected" }`.
- No new event.

## 6. UI

Owned by 10.5 (CR-7). Copy keys delivered here:

| Key | en | vi |
|---|---|---|
| `quality.keyword_name_detected` | This scene names a real person. Describe the place or object instead. | Cảnh này nêu tên người thật. Hãy tả nơi chốn hoặc đồ vật thay vào đó. |
| `quality.scenes_trimmed` | Some scenes will not be drawn because the video is short. The first scene of each paragraph is kept. | Một số cảnh sẽ không được vẽ vì video ngắn. Cảnh đầu tiên của mỗi đoạn được giữ lại. |
| `quality.scenes_sparse` | Few scenes for this length: images will repeat. Add a second scene to some paragraphs. | Ít cảnh so với độ dài: hình sẽ lặp lại. Hãy thêm cảnh thứ hai cho vài đoạn. |
| `errors.keywords_flagged` | Some scenes must be changed before the video can be made. | Cần sửa một số cảnh trước khi làm video. |

## 7. Jobs and integrations

No job or external call. `INameCorpusProvider` reads `story_sources` and the approved `fact_sheets` row through `IReadDb` (tenant filter on).

## 8. Security and privacy

| Threat | Control |
|---|---|
| A real victim/suspect name reaches the image model (G4) | `KeywordRules` name heuristic + `NameCorpus` from the case's own sources and facts, checked at save, approve and render |
| Over-blocking common words as names ("Old Town Road") | only runs of ≥ 2 capitalised tokens from the **episode's** sources/facts; fiction has an empty corpus |
| Keywords in logs or params | params hold counts and segment addresses only; linter issues carry the rule and address, not the keyword text, in logs (02 D19) |

## 9. Test plan

- **Planner (unit):** clip 10 → needed = `ceil(seconds ÷ 10)`; spare 15 %; tier cap; trim order (second keywords from the end first, then first keywords from the end); `scenes_sparse` threshold; slot/segment map order; zero keywords; Short vi/en and Long en boundary cases.
- **Estimator (unit):** worst case = `maxTerms × (p95 tokens × out rate + 200 × text rate)`; placeholder 6,000 tokens when unmeasured.
- **Linter (unit):** folding of Vietnamese names (`Nguyễn Văn An` → `nguyen van an`), `Đ` → `d`; 2-consecutive-token rule; run-start stop list; English names; empty corpus; KeywordRules issues pass through; D22 addresses.
- **Integration:** approve with a name from an approved fact → 409 `keywords_flagged`; render after the fact sheet gains a name → 409; `image_plan` stored with no keyword text (`RenderJobParams_ContainNoKeywordText`).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 03.2.1-T1 | `RenderImagePlanner` + `IImageCostEstimator` | BE | **S (0.5)** | 03.1.1-T2, 02.2.1-T1, 02.2.4-T1 | 03.2.2-T2, 03.4.2-T1, 09.6.1-T1, 09.6.2-T1 |
| 03.2.2-T1 | `RenderKeywordLinter` + `NameCorpus` + `INameCorpusProvider` | BE | **S (0.5)** | 02.2.1-T1, 02.5.3-T2 (fact sheets) | 03.2.2-T2, 09.6.1-T1 (03 CR-17) |
| 03.2.2-T2 | Wiring: quality checker, approve, render → `keywords_flagged` | BE | **XS (0.25)** | 03.2.1-T1, 03.2.2-T1, 02.3.2-T1, 02.3.4-T1, 09.6.1-T1 | 10.5 |

Total **1.25 d** (high-level 1.5 d; README §9).

### Open items for the TL

- [OPEN] `SpareShare = 0.15` and `SparseShare = 0.7` are proposals; the first 10 M1 renders report `needed`, `called`, `unattempted` (D6) and the values are tuned in config.
- [OPEN] A fiction series could reuse a real name the model invented; no corpus exists for fiction, so only the `KeywordRules` heuristic applies (same residual as 02.2.1-T1 Notes).

## 11. Rollout

- **Flag:** none.
- **Order:** 03.2.1-T1 → 03.2.2-T1 → 03.2.2-T2 (after 02.3.2-T1, 02.3.4-T1 and 09.6.1-T1 exist).
- **Config:** `Images:ClipSeconds = 10`, `Images:SpareShare = 0.15`, `Images:SparseShare = 0.7`, `Images:MaxSkippedShare = 0.10` (used by 03.4.2).
- **Monitoring:** `render_image_plan_terms{tier}` histogram, `keywords_flagged_total{rule}`.
- **Rollback:** the linter's name rule can be disabled with `Images:Linter:NameCorpusEnabled = false` (KeywordRules stay on); clip seconds is config.
