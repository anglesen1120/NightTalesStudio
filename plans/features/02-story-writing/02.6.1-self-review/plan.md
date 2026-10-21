# 02.6.1 — Self-review and single revision: detailed plan

> Refines: `../../02-story-writing.md` §3 (02.6.1, 02.6.2), §5.4 (rubric), §5.8 (review issues), §6 (`02.6.1-T1`, `02.6.2-T1`).
> Contract: `../README.md` (D3–D7, D14–D16, D19–D22; F16, F17, F20, F31; §10 T23–T27).
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

**In scope (stories 02.6.1, 02.6.2).**

- One **model self-review** call per draft, after the free local checks, returning `review.v1`: rubric scores (D16), `issues[]` with a **closed** type list, `real_world_claims[]`, ear-rule answers, and a nullable `factual_care` block for true crime.
- Merge of that result into `story_versions.audit_report.model_review` (D22), so the checkpoint-1 UI, the edit path and the 02.8.2 gates all read one document.
- **Exactly one** automatic revision when the review contains ≥ 1 `blocking` + `objective` issue, a new `story_versions` row (`source = revised`, `parent_version_id`), then **review #2** for hints only.
- A **segment-drift** check on the revision → advisory `revision_drift`; it never blocks.
- Metering of both calls (`usage_records.operation = story_review` / `story_revision`, D14) inside the existing `generation_runs` row; **not** counted toward the FR-02-7 daily generation limit.
- The **seeded-defect acceptance set** (F17) that decides the review model (D15).

**Out of scope.** The local checker and its rules (02.3.4-T1); the writer prompt and `story.v1` validation (02.2.1-T1); the moderation port and `moderation_events` (02.4.1-T1); similarity scoring (02.5.6); the autopilot gates that consume `model_review` (02.8.2); the editor's `segment` chips (10.5.10). No HTTP route, no new table, no new error code.

**Deliverables.** `prompts/review/v1.md`, `prompts/revise/v1.md`, `prompts/schemas/review.v1.json`, `prompts/fixtures/seeded-defects/*.json`; Domain `Stories/Review/`; `IStoryWriter.ReviewAsync`/`ReviseAsync` + their partial `OpenAiStoryWriter` implementations; the job pipeline steps `reviewing`/`revising` (owned by 02.2.2-T3, wired here); config keys; tests.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| D21-4/5 | The pipeline steps are fixed: `reviewing` = `IStoryWriter.ReviewAsync(StoryReviewRequest, ct) → ValueTask<TextCallResult<StoryReviewV1>>`; `revising` = `IStoryWriter.ReviseAsync(StoryRevisionRequest, ct) → ValueTask<TextCallResult<StoryContentV1>>`, only when ≥ 1 issue is `blocking` + `objective`; then steps 2–4 run **once more** (review #2, no further revision) ✅ | README D21 |
| D21-IText | `ITextClient.CallAsync<T>(TextCallRequest req, ct)`; `TextCallRequest(RunContext Run, TextStep Step, string SystemPrompt, string UserDataJson, string SchemaName, BinaryData Schema, int MaxOutputTokens, string PromptCacheKey, string PromptVersion)`; `TextStep = Writer\|Review\|Revision\|Facts\|Bible\|Ideas\|Novelty\|Finder` ✅ | README D21 |
| D22 | `story_versions.audit_report` (`audit.v1`) already reserves `model_review { model, prompt_version, stale, scores{7}, issues[{id,severity,kind,segment,evidence,impact,repair_direction}], real_world_claims[] }` and `revision { applied, fixed_issue_ids, parent_version_id, review_after{scores,issues} }`; `segment` addressing is `title\|hook\|p:<0-based>\|ending\|meta`; `factual_care` is `null` for non-true-crime; `evidence` quotes ≤ 200 chars of our draft, never source text ✅ | README D22 |
| D16 | Seven rubric dimensions, 1–5 with written anchors in `prompts/fixtures/rubric.md` (02.1.3-T1); the same names are the `scores` keys: `hook`, `pacing`, `atmosphere`, `natural_language`, `originality`, `factual_care`, `length_fit` ✅ | README D16 + D22 |
| D14 | `IUsageMeter.RecordAsync(UsageItem)` writes one `usage_records` row per call in its own transaction; operations include `story_review` and `story_revision`; `TextCostCalculator` prices from the `price_versions` active at reserve time ✅ | README D14 |
| D4 | `LengthTargetResolver.Resolve(tier, lang, limits)` yields `ReviewMaxOutput` (1,200 / 2,500 / 3,500) and `RevisionMaxOutput` (1,500 / 6,000 / 10,000) for short/standard/long; `max_output_tokens` includes reasoning tokens (O4); a draft outside the token band is objective only when > 15 % outside ✅ | README D4, D21 |
| D5/D6 | `prompts/` catalog with `manifest.json` (keys **relative to `prompts/`**, T27), sha256, startup validation, composite `prompt_version`; untrusted material is one JSON data object, never substituted into a template ✅ | README D5, D6, T27 |
| D7 | Responses API, strict JSON schema, `store=false`, `reasoning.effort` from config, SDK retries off, one resilience layer (review 45 s, revision 90 s), outcome mapping to `TextCallResult` ✅ | README D7 |
| D15 | The **review model is the cheapest model that passes 02.6.1-T2**; the decision goes to `DECISIONS.md` §3 with the numbers ✅ | README D15 |
| D19 | Never log prompts or story text; log ids, codes, tokens, model, latency, `prompt_version`; metrics `openai_tokens_total{operation,model,kind}`, `openai_cost_usd_total{operation}`, `story_generation_duration_seconds{tier,stage}`, `story_invalid_output_total` ✅ | README D19 |
| D20 | TestKit provides `FakeTextClient` with scripted outcomes per step and `story.v1` sample drafts from `prompts/fixtures/`; live contract tests only with `OPENAI_LIVE_TESTS=1` ✅ | README D20 |
| Story 02.6.1 | Local checker first (free), then **one** model call; both merged into `story_versions.audit_report`; checks cover preset structure, setup-before-twist, bible continuity, fact-ref validity, tone rules, ear rules (02.1.5), length range ✅ | `../../02-story-writing.md` L149–151 |
| Story 02.6.2 | ≥ 1 `blocking` + `objective` → exactly one revision fixing those issues only; a new `story_versions` row (`source = revised`) becomes current; review runs once more for hints; taste issues are hints only; metered `story_review`/`story_revision`, priced inside the story generation price, not counted toward the daily limit ✅ | `../../02-story-writing.md` L153–156 |
| F16/CR-6 | Worst case is writer + review + revision + **review #2** = 4 calls; the reserve must include review #2 ✅ | README F16 |
| F17 | "The cheapest model that passes the M1 quality check" had no check — this feature's seeded-defect set is that check ✅ | README F17 |
| F20 | Human-path drafts are moderated too (free endpoint) — moderation is step 3, before review, so review never sees an unflagged harmful draft ✅ | README F20, D12 |
| F31 | `model_review.real_world_claims[]` is one of the three signals 02.8.2-T2 uses; this feature must emit it faithfully and never invent named-person claims ✅ | README F31, D22 |
| P-invariant | In P there are no credits; review/revision go through `ICreditLedger` (metering-only) and `IBudgetGuard` before each paid dispatch ✅ | README D1 |

## 3. Domain and data

**Domain (`src/NightTales.Domain/Stories/Review/`, F38).**

| Type | Shape / rule |
|---|---|
| `StoryReviewV1` | Model output mirror: `Scores`, `Issues`, `EarRuleAnswers`, `FactualCare?`, `RealWorldClaims`, `Summary`. Immutable record. |
| `ReviewScores` | The seven D16 keys as `int` 1–5; `FactualCare` is `int?` (null = n/a). |
| `ReviewIssueV1` | `Id` (`r1`…), `Type` (`ReviewIssueType`), `Severity` (`blocking\|advisory`), `Kind` (`objective\|taste`), `Segment`, `Evidence` (≤ 200 chars), `Impact`, `RepairDirection`. |
| `ReviewIssueType` + `ReviewIssueTypes` | Closed enum + `IsObjective(ReviewIssueType)` + `IsBlockingAllowed(type, severity)` (`blocking` only for objective types). |
| `EarRuleAnswer` | `QuestionId`, `Passed` (bool), `Segment`, `Evidence`. |
| `RealWorldClaim` | `Segment`, `Evidence`. |
| `RevisionDrift` | `SegmentCountChanged`, `ChangedSegments`, `AddedFindings`, `IsAdvisoryOnly = true`. |
| `RevisionPolicy` | `ShouldRevise(StoryReviewV1, int maxRevisions, bool revisionAlreadyApplied)`, `BlockingObjectiveIssues(StoryReviewV1)`. Pure, no I/O. |
| `SegmentDriftDetector` | `Detect(StoryContentV1 original, StoryContentV1 revised, IReadOnlyCollection<string> addressedSegments, double minSimilarity) → RevisionDrift`. Pure; reuses `TextNormalizer` (D21). |
| `AuditReportBuilder` | `MergeModelReview(string auditJson, StoryReviewV1, string model, string promptVersion) → string` and `MergeRevision(...)`; produces the D22 document. Pure JSON editing (no content outside the D22 keys). |
| `ReviewResultValidator` | Post-parse guard: enum membership, score range, evidence length, `Segment` regex `^(title\|hook\|ending\|meta\|p:\d+)$`, unique issue ids, `factual_care` non-null **iff** `topic = true_crime`. Failure ⇒ `InvalidOutput`. |

**Closed issue-type list** (the enum; `…` in the story text expanded, every member classified):

| Type | Kind | Severity allowed |
|---|---|---|
| `fact_ref_invalid` | objective | blocking, advisory |
| `fact_not_in_sheet` | objective | blocking |
| `missing_setup_before_twist` | objective | blocking |
| `tone_violation` | objective | blocking |
| `length_out_of_range` | objective | blocking (only when > 15 % outside D4) |
| `bible_contradiction` | objective | blocking |
| `ear_rule_violation` | objective | blocking, advisory |
| `keyword_rule_violation` | objective | blocking, advisory |
| `hook_missing` | objective | blocking |
| `ending_missing` | objective | blocking |
| `beat_missing`, `content_warning_missing` | objective | advisory |
| `markdown_or_url_in_text` | objective | blocking |
| `revision_drift` | objective | advisory only (platform-emitted, never model-emitted) |
| `weak_hook`, `flat_ending`, `pacing_issue`, `repetitive_phrasing` | taste | advisory |

Only `objective` + `blocking` issues enter the revision prompt or can block a gate.

**Persistence.** No new table, no migration. This feature writes only `story_versions.audit_report` (jsonb, owned by 02.2.2-T1) and `story_versions.source` / `parent_version_id` (02.6.2). `usage_records` rows are written by `IUsageMeter` (02.2.4-T1).

**Config.**

| Key | Default | Rule |
|---|---|---|
| `Story:Review:Enabled` | `true` | false ⇒ the `reviewing` stage is skipped, `model_review` omitted (gates then fail closed by their own rule) |
| `Story:Review:Effort` | `low` | `reasoning.effort` |
| `OpenAi:Models:Review` | `gpt-6-luna` | set by 02.6.1-T2 (D15) |
| `Story:Revision:Enabled` | `true` | false ⇒ no revision even when blocking issues exist |
| `Story:Revision:MaxRevisions` | `1` | must be `0` or `1` (D21); anything else fails start-up |
| `Story:Revision:MinTextSimilarity` | `0.6` | drift threshold for untouched segments |
| `OpenAi:Models:Revision` | `gpt-6-luna` | separate key (D7) |

## 4. Use cases

| # | Trigger | Flow |
|---|---|---|
| UC1 | `GenerateStoryJob` reaches `reviewing` | Input = the validated draft + the approved fact sheet (true crime) + bible snapshot + ear-rule questions + the D4 length target. One call. Result merged into `audit_report.model_review` (`stale = false`). |
| UC2 | Review has ≥ 1 `blocking` + `objective` issue, `Revision:Enabled`, `MaxRevisions > 0`, no `pause_reason` | `revising`: revision call with **only** those issues → validate (`StoryDraftValidator`) → local checker → moderation → review #2 → drift check. New version row `source = revised`. No second revision, whatever review #2 says. |
| UC3 | Review has only `taste`/advisory issues, or revision is disabled/exhausted | Save the generated version; the issues are hints in the UI; episode → `story_review`. |
| UC4 | Revision output fails `story.v1`/validator after the per-call invalid-output retry | The **original** draft is saved (`audit.revision.applied = false`, `reason = revision_invalid_output`); the run still succeeds. |
| UC5 | Review call itself returns `InvalidOutput` after its retry | Run fails `story_invalid_output` (not counted toward the limit, D2); no version row. |
| UC6 | Review returns `Moderated` | Run fails `story_moderated` + a `moderation_events` row (`stage = review`) — handled by 02.4.1-T1, triggered here. |
| UC7 | User edits the story (02.3.1-T1) | `model_review.stale = true`; **no** paid re-review in P. The next generation is a new run. |

Review and revision are never user-invokable: no command, no endpoint.

## 5. API and events

- **No new routes, no new error code** (D8 closed list is untouched). The read surface is `GET /episodes/{id}` (02.3.4-T2), which exposes `currentStory.review` = `{ scores, issueCounts { blocking, advisory }, hasDrift, stale }` plus the raw `audit_report` behind the editor endpoint. The UI must not need a second call.
- **Progress**: the existing `EpisodeUpdated` event carries `generation.stage`; `reviewing` and `revising` are already in the D2 stage enum, so the UI shows "Reviewing…"/"Revising…" with no new event type (F24/CR-7).
- **Domain events**: none new. `StoryGenerated` (D21) fires after `saving`, whether or not a revision ran; the `revised` row is reachable through `parent_version_id`.
- **Consumers of `model_review`**: 02.8.2-T1 story gate (`scores` + `issues`), 02.8.2-T2 (`real_world_claims`), 02.3.4-T2 read model, 10.5.10 editor chips. Their contracts are the D22 keys — do not rename a key.

## 6. UI

Checkpoint-1 story screen (10.5.x), read-only panel above the text:

- Seven rubric scores as a compact row (1–5), `factual_care` shown as `n/a` for fiction; mean score visible (the gate's `MinMeanReviewScore` compares to it).
- Issues grouped: **blocking** (objective) first, each with a `segment` chip that scrolls the editor (`p:3` → paragraph 3), the ≤ 200-char evidence quote, impact and repair direction; then **advisory** (taste) as hints with the same anchoring.
- `revision_drift` renders as a neutral badge ("the revision changed parts it was not asked to change") — never red, never blocking.
- When `stale = true` (after an edit) the panel shows "review outdated" and hides the scores' authority.
- True crime: `factual_care` block lists unsupported claims with their segment.
- No user action in this feature: the owner approves/edits/reopens in 02.3.

## 7. Jobs and integrations

- Orchestration stays in `GenerateStoryJob` (02.2.2-T3): `writing → checking → reviewing → [revising → checking → reviewing] → scoring → saving`. This feature supplies steps 4 and 5 and the `RevisionPolicy` decision the job asks for.
- Every call goes through `ITextClient.CallAsync<T>(TextCallRequest, ct)`; the `RunContext` rides **inside** `TextCallRequest` (D21) — `GenerationRunId` feeds `X-Client-Request-Id`, `PriceVersion` feeds the cost calculator, `Reservation` links the usage rows. Neither `ReviewAsync` nor `ReviseAsync` takes a `RunContext` parameter.
- Tokens: `MaxOutputTokens = LengthTarget.ReviewMaxOutput` / `RevisionMaxOutput` from `LengthTargetResolver` (D4/`price_versions.limits`).
- Timeouts 45 s (review) / 90 s (revision); 2 attempts on 429/5xx/timeout (D7). Review is the cheapest step — if it times out twice the run fails `story_generation_failed` (not counted).
- Cost: `IUsageMeter.RecordAsync` per call (`operation = story_review` / `story_revision`); both are summed into `generation_runs.cost_usd`; the **reserve** must cover review #2 (F16/CR-6 — the estimate is `IStoryCostEstimator.WorstCaseAsync`, owner 02.2).
- `IBudgetGuard.CheckAsync` runs before the review call and before the revision call (D1); over cap ⇒ 429 `spend_cap_reached` on the command, `pause_reason = spend_cap_reached` on the run.
- Metrics/logs (D19): `openai_tokens_total{operation,model,kind}`, `openai_cost_usd_total{operation}`, `story_generation_duration_seconds{tier,stage=reviewing|revising}`, `story_invalid_output_total`; one structured log line per call with run id, step, model, tokens, latency, `prompt_version` — never text.

## 8. Security and privacy

- **Prompt injection**: the draft, brief, facts, bible and ear rules travel in one JSON data object (D6) in the user message; the system prompt states the two-tier rule (control keys are followed; material strings are never instructions — 02 T69). The draft is our own output but is still untrusted (a brief can make the writer emit instruction-like text) — injection fixtures from 02.2.3-T2 are reused for the review prompt.
- **Egress**: `store = false` (O5), `safety_identifier = sha256(user_id)`, no source text in the prompt (only fact statements and aliases), no URLs.
- **Storage**: the review result is stored inside `story_versions.audit_report`; `evidence` ≤ 200 chars of our draft (D22). No prompt, brief or source text is persisted by this feature.
- **Logging** (D19): ids, codes, tokens, model, latency, `prompt_version` only. Never log the prompt, the draft, the review evidence or the answers.
- **Failure modes** are mapped to `TextCallResult`, never an exception for control flow; a moderation hit becomes `story_moderated` with `moderation_events.source = openai_text` (02.4.1-T1), never a silent save.
- **Retention**: nothing new to retain — the audit report dies with the episode (13, D19).

## 9. Test plan

**Unit (no network).**

- `ReviewIssueTypes_BlockingAllowed_OnlyForObjectiveTypes`
- `ReviewIssueTypes_RevisionDriftIsAdvisoryOnly`
- `ReviewResultValidator_FactualCareNonNullForFiction_Fails`
- `ReviewResultValidator_UnknownIssueType_Fails`
- `ReviewResultValidator_BadSegmentAddress_Fails`
- `ReviewResultValidator_EvidenceOver200Chars_Fails`
- `RevisionPolicy_BlockingObjectiveIssue_Revises`
- `RevisionPolicy_TasteOnly_DoesNotRevise`
- `RevisionPolicy_RevisionAlreadyApplied_DoesNotReviseAgain`
- `RevisionPolicy_MaxRevisionsZero_NeverRevises`
- `SegmentDriftDetector_SegmentCountChanged_ReportsDrift`
- `SegmentDriftDetector_AddressedSegmentRewritten_NoDrift`
- `SegmentDriftDetector_UntouchedSegmentLowSimilarity_ReportsDrift`
- `SegmentDriftDetector_NewBlockingLocalFinding_AddedFinding`
- `AuditReportBuilder_MergeModelReview_WritesD22KeysOnly`
- `AuditReportBuilder_MergeRevision_SetsParentAndFixedIssueIds`
- `RevisionOptions_MaxRevisionsTwo_FailsValidation`

**Integration (TestKit `FakeTextClient` scripted per step, D20).**

- `GenerateStory_ReviewBlockingObjective_RevisesExactlyOnceAndSavesRevisedRow`
- `GenerateStory_ReviewTasteOnly_SavesGeneratedRowWithHints`
- `GenerateStory_SecondReviewStillBlocking_SavesAndStops`
- `GenerateStory_RevisionInvalidOutput_SavesOriginalWithAppliedFalse`
- `GenerateStory_ReviewInvalidOutput_FailsRunStoryInvalidOutput`
- `GenerateStory_ReviewModerated_FailsRunStoryModerated`
- `GenerateStory_ReviewAndRevision_MeterTwoUsageRowsNotCountedTowardLimit`
- `GenerateStory_DriftDetected_AuditHasAdvisoryRevisionDriftOnly`
- `EditStory_AfterReview_MarksModelReviewStale` (02.3.1-T1 owner, asserted here)
- `GenerateStory_ReviewPromptReceivesDataObjectWithFactsAndBible`

**Acceptance (02.6.1-T2, live, `OPENAI_LIVE_TESTS=1`).** ≥ 20 seeded-defect drafts → recall ≥ 80 %, false-blocking ≤ 10 %, luna vs sol table.

**Manual.** One real standard-tier vi and en draft through the API; inspect `audit_report.model_review` and the UI panel.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.6.1-T1 | Review prompt + `review.v1` schema + `ReviewAsync` + audit merge | M (1.0) | 02.2.1-T2, 02.2.3-T1, 02.2.2-T1, 02.1.5-T1, 02.1.3-T1 (rubric) | 02.6.1-T2, 02.6.2-T1, 02.8.2-T1, 10.5.3-T2 (10 CR-6) |
| 02.6.1-T2 | Seeded-defect acceptance set + review-model decision | S (0.5) | 02.6.1-T1 | 02.8.2-T1 (model choice) |
| 02.6.2-T1 | Single revision + drift check + review #2 + metering | S (0.5) | 02.6.1-T1, 02.2.2-T3 | 02.8.2-T1 |

Raw 2.0 d (README §9: 02.6.1/02.6.2 moved 1.5 → 2.0 for the acceptance set, F17). No buffer added here (README §9 applies the 20 % at programme level). Gate 02-M1-core requires both features on every draft.

**Open items for the TL** (contract gaps found while writing this plan; the README is not edited):

1. **`ear_rule_answers` has no home in `audit.v1`.** The story text requires the review to check the 02.1.5 ear rules, and the per-question answers are evidence of that check, but D22's `model_review` lists only `scores`, `issues`, `real_world_claims`. Proposal: add an **additive** `model_review.ear_rule_answers: [{ question_id, passed, segment, evidence }]`; failed answers are also emitted as `ear_rule_violation` issues, so gates keep working either way. Needs a D22 amendment (or an explicit "answers are dropped, only issues are kept" ruling).
2. **`revision.drift` has no home either.** The advisory `revision_drift` is emitted as an issue inside `revision.review_after.issues` (contract-compatible) plus an additive `revision.drift { segment_count_changed, changed_segments[], added_findings[] }` for the UI badge. Confirm the additive key or rule that the badge reads the issue only.
3. **`usage_records.operation` for review #2.** D14 lists `story_review` once; review #2 is a second `story_review` row of the same run. Confirm that is intended (it is what 02.6.1-T2's cost table assumes) rather than a `story_review_2` value.
4. **Reserve must include review #2** (F16/CR-6). Confirm `IStoryCostEstimator.WorstCaseAsync` (02.2) is updated in the same milestone; otherwise Long drafts can pass the guard and still exceed the bound.
5. **Review on `long` tier latency.** D7 sets 45 s; a 3,500-token review of a 15-minute vi draft is unmeasured (⚠️ F16). Proposal: measure in 02.6.1-T2, raise the timeout if p95 > 45 s, and do not touch the NFR gate (Long is reported, not gated).
6. **`factual_care` in strict mode.** Strict mode requires every property in `required` and forbids omissions, so the nullable field is `"type": ["object", "null"]` with the object shape fully specified — ⚠️ [INFERENCE] from the JSON-Schema subset OpenAI accepts; confirm on a real strict call in T1.

## 11. Rollout

1. T1 lands with `Story:Review:Enabled = true`, `OpenAi:Models:Review = gpt-6-luna` (D7 default) and the review visible in the UI. No flag, no migration: the audit column already exists (02.2.2-T1).
2. T2 runs the acceptance set on luna and sol over the frozen fixtures, writes `trial/review/acceptance-<model>-<ts>.json` (untracked, T25 precedent) and records the decision in `DECISIONS.md` §3 (D15). Only then is `OpenAi:Models:Review` pinned; if sol wins, the cost table in 12.1.1/`price_versions` is re-checked (⚠️ the per-call bound grows ~20× for that step).
3. 02.6.2-T1 lands with `Story:Revision:Enabled = true`, `MaxRevisions = 1`. Revision is bounded by D21, so there is no runaway path; the kill switch is the config flag plus `Autopilot:Gates` for the autopilot channel.
4. M1 gate: every draft has a non-stale `model_review` and at most one revision; the seeded-defect thresholds hold on the frozen set; the drift badge never blocks.
5. Rollback: `Story:Review:Enabled = false` / `Story:Revision:Enabled = false`; already-written `audit_report` documents stay readable because the D22 keys are unchanged.
6. Re-run the acceptance set after any material review/revise prompt change (T2 is the re-calibration harness) and after a review-model change (D15 precedent).
