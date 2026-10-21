# 02.8.2 — Auto-approval gates (feature plan)

> Refines: `../../02-story-writing.md` story **02.8.2**, Epic 02.8 (§4), §5.4 rubric, §5.9 similarity, §7 test plan, §8 risks; `../../00-overview.md` §6 (`autopilot_runs`, `moderation_events`, `audit_logs`, `story_versions`), §7 (Episode), §8 (routes).
> Contract: `../README.md` — facts §1 (O4, O7, P2, P5), findings F05/F20/F31/F36, decisions D2/D9/D12/D13/D16/D17/D21/D22, gates §7, task template §6. Template: `../../00-overview.md` §10.1.
> Legend: ✅ verified (source) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

| | |
|---|---|
| Story | **02.8.2 Auto-approval gates** — *As the channel owner I want safe episodes to go through on their own so that the pipeline never waits for me without reason.* Must (P) |
| Tasks | 02.8.2-T1 gate engine: story gate + video gate + `gate_results` + audit/History + true-crime hold (M 1.0) · 02.8.2-T2 content gate: moderation over narration/metadata/scene prompts + real-world detector (S 0.5) · 02.8.2-T3 thresholds config + dry-run calibration (S 0.5) = **2.0 d** (README §9: 1.5 → 2.0, F31) |
| In scope | `IAutoApprovalGate` (story gate + video gate) with a fixed check list; the `gates.v1` `gate_results` payload stored on `autopilot_runs.gate_results`; auto-approval through the **same** `ApproveStoryCommand` / `ApproveVideoCommand` a user calls, as a system actor (D13 keys, History via `audit_logs`); the true-crime hold; the owner-notification path for holds and gate failures; `IContentModerator` stage `autopilot_gate` over narration, title, description, tags and the **expanded scene prompts**; the real-world-framing detector (lexicon + dated-incident pattern + `real_world_claims[]`, F31); the threshold configuration and the dry-run calibration mode with its report |
| Out of scope | Idea generation, novelty, variety (02.8.1-T1/T2, 02.8.4-T1); the run state machine, slot planner, pause/resume, spend caps and the alert e-mail (02.8.3-T1/T4 — this feature *returns* a gate verdict and *writes* the streak that T4 reads); render internals and the quality checks themselves (05.5.1, 09.6.3 — we consume `render_jobs.quality_report`); publication/consent/upload (06.2.2-T3, 06.2.4); the `ApproveStory` / `ApproveVideo` endpoints, ETag rules and the true-crime attestation guard (02.3.2-T1, 02.3.3-T1, 09.7.1); the story editor and its hints (02.3.4-T1); the Autopilot tab UI (10.4.3-T1); the `moderation_events` writer for the human path (02.4.1-T1) |
| Acceptance (story, restated) | Fiction episode (`ghost`, `spiritual`): checkpoint #1 is auto-approved when self-review passes the score threshold after at most one revision, moderation passes over narration + metadata + scene prompts, there is no real-person/real-event signal, similarity to sources and to past episodes is below threshold and length is in range; otherwise the episode stays in `story_review` and the owner is notified. True-crime fact sheet and story **always** wait for the owner. A rendered video: checkpoint #2 is auto-approved when the 05.5.1 quality checks pass. Every auto-approval writes `audit_logs` and appears in the episode History. Two gate failures in a row pause the series (02.8.3-T4). |

The story's phrase "named private individuals" is **not** implemented as named-entity recognition — F31: a fiction detector cannot tell an invented character name from a real person. The real-world signal is the three-signal test in §4 UC3 (framing lexicon, dated incident, model `real_world_claims[]`). Every hit routes to the owner, never to a block.

## 2. Confirmed facts

| # | Fact used here | Status / source |
|---|---|---|
| O7 | `omni-moderation-latest` is **free**; 13 categories (`sexual`, `sexual/minors`, `harassment`, `harassment/threatening`, `hate`, `hate/threatening`, `illicit`, `illicit/violent`, `self-harm`, `self-harm/intent`, `self-harm/instructions`, `violence`, `violence/graphic`). No documented text-length limit ⚠️ | ✅ README §1 O7 (developers.openai.com/api/docs/guides/moderation) |
| D12 | Moderation port `IContentModerator.CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, ct)`, texts chunked ≤ 8,000 chars ⚠️; stages `brief`, `draft`, `autopilot_gate`; blocking categories on the human `draft` path are `sexual/minors`, `self-harm/instructions`, `illicit/violent`, `hate/threatening`; usage recorded with `operation = moderation`, cost 0; every flag writes `moderation_events` with `excerpt` ≤ 300 chars | ✅ README D12 |
| D16 | Seven rubric dimensions 1–5 with written anchors: hook · pacing · atmosphere · natural language · originality · factual care (true crime only, else `null`) · length fit. The model review and the human raters use the same anchors | ✅ README D16 |
| D22 | `audit.v1` on `story_versions.audit_report`: `local.issues[]` (`severity`, `kind`, `segment`), `moderation { flagged, categories, checked_at }`, `model_review { scores, issues[], real_world_claims[] }`, `revision { applied, fixed_issue_ids, review_after }`, `similarity { score, n, corpus, segments[] }`. Segment addressing `title`/`hook`/`p:<i>`/`ending`/`meta`; `evidence` ≤ 200 chars of **our own draft** | ✅ README D22 |
| 05.5.1 | `ffprobe` checks: video + audio streams present; duration within **±5 %** of the TTS audio duration; resolution matches the aspect; integrated loudness **−16 to −14 LUFS**; subtitles present if enabled. A failure marks the render `failed` with `render_quality_failed` and writes details into `render_jobs.quality_report` | ✅ `../../05-video-assembly-subtitles.md` §4 (05.5.1), 09 §5.4 `render_quality_failed` job outcome, never an HTTP response (05 CR-3) |
| 03 | `video_terms[i] = ScenePromptBuilder.Build(style, keyword[i]).Text` (template + platform `NegativeClause`; the gate moderates `.ModerationText`, 03 D1, 03 CR-3); keywords are English, 3–12 words, capped at `ceil(D × K)` with K = 6 (P5) | ✅ `../../03-image-generation.md` §5.2, FR-03-1/FR-03-3 |
| 05.5.1-T2 | `render_jobs.quality_report jsonb` exists in 00 §6 | ✅ `../../05-video-assembly-subtitles.md` §10 contract change |
| P2 | `09.3.6` activity log is S in 00 §4.1, but CR-3 moves `audit_logs` + `AuditBehavior` + `[Audited]` to **P**; `GET /me/activity` and `GET /episodes/{id}/activity` stay S | ✅ README P2, CR-3 |
| F20 | Moderation of generated text existed only for autopilot; `IContentModerator` now runs on the brief and on every final draft on the human path too. Refusal (`ResponseContentPartKind.Refusal`) and `content_filter` are separated | ✅ README F20, O6 |
| F36 | Metadata limits match 06.2.1: title ≤ 100 chars, tags ≤ 15 × ≤ 30 chars, ≤ 400 chars total, no `<`/`>` in title/description ⚠️ confirm against 06.2.1 | ✅ README F36 |
| — | `SeriesQuality`-style server-side gate libraries do not exist in .NET 10; the gate is plain C# in Domain + Application. No new NuGet package is introduced by this feature | [INFERENCE] |

Open questions closed here: "which thresholds" → §3.3 with defaults; "what exactly is checked" → §3.1/§3.2; "how the calibration is done" → T3 §Design. Still open: ⚠️ whether the model review's `scores` are stable enough to gate on a mean (T3 measures the distribution over the first 10 episodes); ✅ the `quality_report` field names are fixed by `render-quality.v1` (05 README D10, 05 CR-6; T1 still reads defensively — a missing field is `pass = false` with `detail = "field_absent"`, never an exception).

## 3. Domain and data

### 3.1 Story gate check list (`IAutoApprovalGate.EvaluateStoryAsync`)

Source of every input is the current `story_versions` row (via `audit_report` = `audit.v1`, D22) plus `story_versions.similarity_score`, `word_count`, `content`, `keywords`. Check ids are stable strings; they appear in `gate_results`, in `audit_logs.data` and in the UI run log.

| # | Check id | Value | Threshold (default) | Fail → |
|---|---|---|---|---|
| 1 | `self_review_present` | `audit_report.model_review != null && stale == false` | must be true | owner |
| 2 | `self_review_mean_score` | mean of the non-null `model_review.scores` dimensions (D16) | ≥ `Autopilot:Gates:Story:MinMeanReviewScore` = **3.5** | owner |
| 3 | `self_review_min_dimension` | min of the non-null dimensions | ≥ `…:MinDimensionScore` = **3.0** | owner |
| 4 | `blocking_objective_issues` | count of `local.issues ∪ model_review.issues` with `severity = blocking && kind = objective` | = 0 | owner |
| 5 | `revision_count` | `revision.applied ? 1 : 0` | ≤ `…:MaxRevisions` = **1** (D21) | owner |
| 6 | `moderation_draft` | `audit_report.moderation.flagged` | must be false | owner |
| 7 | `moderation_gate` | T2 result over narration + metadata + scene prompts | no category above its threshold | owner |
| 8 | `real_world_signal` | T2 detector result (framing lexicon / dated incident / `real_world_claims[]`) | no hit | owner |
| 9 | `similarity_sources` | `story_versions.similarity_score` | ≤ `Autopilot:Gates:Story:MaxSourceSimilarity` = **0.05** (was the `Similarity:WarnAbove` default; 02.5.6-T2 calibrated `WarnAbove` to 0.02 and `BlockAbove` to 0.45 (amended rule 3, owner 2026-10-17), so 0.05 is now looser than the warn level and stricter than the human block; the value is an 02.8.2-T3 decision, STATUS `CAL-S50-1`) | owner |
| 10 | `similarity_past_episodes` | `SimilarityScorer.Score` vs the past-episode corpus, `ISimilarityCorpusProvider.GetPastEpisodesAsync(seriesId, excludeEpisodeId, 50, ct)` (02.5.6; the **sources** corpus of check 9 comes from `LoadAsync(episodeId, lang, ct)`, D21, and is already folded into `story_versions.similarity_score` by the job) | ≤ `…:MaxPastEpisodeSimilarity` = **0.10** | owner |
| 11 | `length_in_range` | `word_count` vs the D4 band for `length_tier × language` | inside the band (±15 % advisory band counts as pass, F07/D4) | owner |
| 12 | `keyword_rules` | `KeywordRules` violations (03 FR-03-3) and `keywords.Count` vs `ceil(D × 6)` | 0 violations, count ≤ cap | owner |
| 13 | `content_warnings_allowed` | `content.content_warnings` ⊆ `…:AllowedContentWarnings` | subset (default `violence, death, crime, supernatural, disturbing`) | owner |
| 14 | `fact_sheet_current` | true crime only: `story_versions.fact_sheet_id` == the approved sheet id (F12) | equal | owner |
| 15 | `true_crime_hold` | `series.topic == true_crime` | **always fail** (the hold, 02.8.2 ¶2 / DECISIONS §10.12) | owner |

- Checks 14 and 15 only run for `true_crime`. Check 15 is evaluated **after** 1–14 so the run log still shows the full picture for the owner and for the dry-run report.
- The gate returns `Pass = checks.All(pass)`; `true_crime` therefore always returns `Pass = false`.
- Checks 1–5 and 11–13 are pure (Domain). Checks 6–8 read persisted results (no paid call — T2 runs at `draft` time inside the story job, D12) except when the gate itself is invoked for a **dry run of an edited story**: then the gate calls `IContentModerator` itself (free, D12) and the detector again. [DECISION] The gate never calls a paid model.
- Thresholds are resolved from a **profile** name, not from raw numbers: `series.autopilot_settings.gate_thresholds_ref` (`"default"` | `"strict"` | `"loose"`) → `Autopilot:Gates:Profiles:{name}` in configuration. A series with no ref uses `default`. This keeps the series jsonb small and lets the owner change a threshold once (10.4.3 "advanced").

### 3.2 Video gate check list (`EvaluateVideoAsync`)

Input: the `render_jobs` row of the finished render (`status = succeeded`), its `quality_report`, the `assets` row and the episode's `series.aspect`.

| # | Check id | Value | Threshold | Fail → |
|---|---|---|---|---|
| 1 | `quality_report_present` | `render_jobs.quality_report != null` | must be true | owner |
| 2 | `quality_report_pass` | `quality_report.pass` (`render-quality.v1`, 05 README D10) (05 CR-6) | true | owner |
| 3 | `duration` | `abs(quality_report.duration_sec − tts_duration_sec) / tts_duration_sec` | ≤ **0.05** (05.5.1) | owner |
| 4 | `loudness` | `quality_report.integrated_lufs` | in **[−16, −14]** (05.5.1) | owner |
| 5 | `resolution_aspect` | `quality_report.width/height` vs `series.aspect` | match | owner |
| 6 | `subtitles_present` | `quality_report.subtitles_present` when `render_jobs.params.subtitles.enabled` | true when enabled | owner |
| 7 | `failed_scenes` | `quality_report.images.skipped.length` + `quality_report.images.moderated_count` (03 D7, 03 CR-3) | = 0 | owner |
| 8 | `image_moderation` | `moderation_events` for this `render_job_id` with `source = openai_image` | none | owner |
| 9 | `asset_integrity` | `assets.sha256 == quality_report.sha256` and `assets.duration_sec > 0` | equal / true | owner |
| 10 | `subtitle_sync` | `quality_report.hints` (`render-quality.v1`, 05 D10) | does not contain `subtitle_sync_suspect` (checked only when `render_jobs.params.subtitles.enabled`) | owner (05 CR-6) |

- A render that failed 05.5.1 never reaches this gate: 09.6.3 already ends the job `failed` with `render_quality_failed` and the episode returns to `story_approved` (00 §7). Check 2 exists as defence in depth, and check 7 catches the **silent** case MPT creates by skipping a keyword after its own retries (M4 in 01 §2) — a video with a missing image must not auto-publish.
- Any failure → the episode stays in `video_review`, the run ends `gate_failed`, the owner is notified (T4 pauses after two in a row).

### 3.3 Threshold configuration (`Autopilot:Gates`)

```jsonc
{
  "Autopilot": {
    "Gates": {
      "DryRun": true,                      // T3: record only, never auto-approve
      "DryRunMaxEpisodes": 10,             // after N evaluated runs the owner decides (T3 §Design)
      "Profiles": {
        "default": {
          "Story": { "MinMeanReviewScore": 3.5, "MinDimensionScore": 3.0, "MaxRevisions": 1,
                     "MaxSourceSimilarity": 0.05, "MaxPastEpisodeSimilarity": 0.10,
                     "AllowedContentWarnings": ["violence","death","crime","supernatural","disturbing"] },
          "Moderation": {
            "BlockedCategories": ["sexual/minors","self-harm/instructions","illicit/violent","hate/threatening"],
            "MaxScore": { "harassment": 0.5, "hate": 0.3, "illicit": 0.5, "self-harm": 0.3,
                          "sexual": 0.5, "violence": 0.6, "violence/graphic": 0.3 }
          },
          "RealWorld": { "RequireIncidentCoOccurrence": true }
        },
        "strict": { "Story": { "MinMeanReviewScore": 4.0, "MinDimensionScore": 3.5, "MaxSourceSimilarity": 0.03,
                              "MaxPastEpisodeSimilarity": 0.05 } }
      }
    }
  }
}
```

- `strict` is a partial overlay on `default` (missing keys inherit). `loose` exists only as a documented example and is **not** shipped enabled — [DECISION] loosening a gate is an owner decision recorded in the run log, never a default.
- Every threshold appears in `gate_results` as the `threshold` of its check, so a verdict is reproducible from the stored run row alone. That is what makes T3's calibration possible without re-evaluating.
- Config is bound with `IOptionsMonitor<AutopilotGateOptions>` so a change does not need a restart (the DB-backed `SettingsChanged` path of 09.10.3 was dropped by DEC 10.14; these are appsettings values).

### 3.4 `gate_results` payload (`gates.v1`) on `autopilot_runs.gate_results`

00 §6 already gives `autopilot_runs.gate_results jsonb`; this feature defines its shape. Story and video results are merged into the same object (they happen at different times in the same run).

```json
{
  "schema_version": "gates.v1",
  "mode": "dry_run",
  "profile": "default",
  "evaluated_at": "2026-10-08T21:26:04Z",
  "story": {
    "pass": false,
    "hold": true,
    "attempt": 1,
    "story_version_id": "018f…",
    "episode_status_at_evaluation": "story_review",
    "checks": [
      { "id": "self_review_mean_score", "value": 3.29, "threshold": 3.5, "pass": false, "detail": "mean of 6 non-null dimensions" },
      { "id": "true_crime_hold", "value": true, "threshold": null, "pass": false, "detail": "topic=true_crime" }
    ],
    "failures": ["self_review_mean_score", "true_crime_hold"]
  },
  "video": {
    "pass": true,
    "render_job_id": "018f…",
    "asset_id": "018f…",
    "checks": [ { "id": "loudness", "value": -15.1, "threshold": [-16, -14], "pass": true, "detail": "LUFS" } ],
    "failures": []
  },
  "similarity": { "sources": 0.021, "past_episodes": 0.044, "corpus_max": 50 },
  "real_world": { "hit": false, "signals": [] },
  "moderation": { "flagged": false, "categories": [] }
}
```

- `detail` never contains story text: it is a code (`"field_absent"`, `"mean of 6 non-null dimensions"`) or a number. `evidence` excerpts stay in `audit_report`/`moderation_events` (D19/D22), not here.
- `hold` is `true` only for the true-crime hold, so T4 and the UI can distinguish "held for the owner by design" from "failed a check".
- `mode` is `live` or `dry_run` (T3). The run-log UI shows a `dry_run` badge.

### 3.5 Tables, indexes and constraints

No new table. Touched columns:

| Table | Use | Change |
|---|---|---|
| `autopilot_runs` | `gate_results` (jsonb), `outcome`, `pause_reason`, `episode_id`, `idea_id` | none (00 §6 + CR-4 `slot_at`); 02.8.3-T1 adds `state`/`step` |
| `story_versions` | `audit_report`, `similarity_score`, `content`, `word_count`, `fact_sheet_id` | read-only |
| `render_jobs` | `quality_report`, `params`, `status` | read-only (05.5.1-T2) |
| `assets` | `sha256`, `duration_sec` | read-only |
| `moderation_events` | one row per flag with `stage = autopilot_gate`, `source = openai_text` | none |
| `audit_logs` | `autopilot.story_auto_approved`, `autopilot.video_auto_approved`, plus the underlying `episode.story_approved` / video approve rows | none (CR-3) |
| `series` | `autopilot_settings.gate_thresholds_ref`, `autopilot_paused_at`, `autopilot_pause_reason` | none; 02.8.3-T1 adds `tone`/`banned_themes` (CR) |

Derived, no column: the **gate-failure streak** = the number of consecutive most-recent terminal `autopilot_runs` of the series whose `outcome = 'gate_failed'`:

```sql
SELECT count(*) FROM (
  SELECT outcome FROM autopilot_runs
  WHERE series_id = @seriesId AND outcome IS NOT NULL
  ORDER BY slot_at DESC LIMIT 2
) t WHERE t.outcome = 'gate_failed';
```

T4 calls this through `IGateFailureStreakReader.GetStreakAsync(seriesId, ct)` (Application/Abstractions) instead of a new column. An index on `(series_id, slot_at DESC)` covers it (added by 02.8.3-T1).

## 4. Use cases

All of these are **internal application services**, not HTTP endpoints: the autopilot orchestrator (02.8.3-T1) calls them. No new route, no new error code in the HTTP catalogue (D8) — a gate verdict is not a client error.

| # | Use case | Service (Application) | Called by | Result |
|---|---|---|---|---|
| UC1 | Evaluate the story gate for a run | `StoryGateEvaluator.EvaluateAsync(Guid runId, int attempt, CancellationToken ct) → ValueTask<ErrorOr<StoryGateOutcome>>` | `AutopilotRunOrchestrator` on `StoryGenerated` (autopilot trigger) and on each 15-min tick while `state = story_gate` | `StoryGateOutcome(StoryGateResult Result, bool AutoApproved, GateFailureReason? Reason)` |
| UC2 | Auto-approve the story | `AutopilotApprovalService.ApproveStoryAsync(Guid runId, StoryGateResult r, ct)` | UC1 on pass and `mode = live` | sends `ApproveStoryCommand(episodeId, "\"sv-<currentVersionId>\"", null, ApprovalSource.Autopilot)` under `SystemContext(ownerId)`; writes `autopilot.story_auto_approved` |
| UC3 | Evaluate the video gate for a run | `VideoGateEvaluator.EvaluateAsync(Guid runId, CancellationToken ct) → ValueTask<ErrorOr<VideoGateOutcome>>` | orchestrator on the render-done event while `state = video_gate` | `VideoGateOutcome(VideoGateResult Result, bool AutoApproved)` |
| UC4 | Auto-approve the video | `AutopilotApprovalService.ApproveVideoAsync(Guid runId, VideoGateResult r, ct)` | UC3 on pass and `mode = live` | `ApproveVideoCommand(episodeId, note: "autopilot")`; writes `autopilot.video_auto_approved` |
| UC5 | Record a dry-run verdict | inside UC1/UC3 | T3 | writes `gate_results` with `mode = dry_run`, never calls an approve command, returns `AutoApproved = false, Reason = DryRun` |
| UC6 | Hold for the owner | `AutopilotNotificationService.NotifyOwnerActionRequiredAsync(runId, reason, ct)` | UC1/UC3 on any failure or hold | outbox event `AutopilotOwnerActionRequired` (e-mail + SignalR `EpisodeUpdated`); the episode status is **unchanged** (`story_review` / `video_review`) |
| UC7 | True-crime hold | inside UC1 | — | `hold = true`, `Pass = false`, reason `true_crime_hold`; never an approve call, even in a dry run |

Authorization: every service runs under `SystemContext(ownerId)` from the run row (`autopilot_runs.user_id`); the approve commands re-check the actor and the ETag like any other caller (D9), and `ApprovalSource.Autopilot` is rejected by the domain for `topic = true_crime` with `episode_invalid_state` (02.3.2-T1) — the second, independent guard behind check 15.

Idempotency: each call is keyed by `autopilot:{seriesId}:{slotAt:O}:story_gate:{attempt}` and `…:video_gate`. The orchestrator only calls a step when `autopilot_runs.step` is behind it (02.8.3-T1), and `ApproveStoryCommand` itself is idempotent through the episode state guard (`story_review → story_approved` is a legal transition; a second call finds `story_approved` and returns `episode_invalid_state`, which UC2 treats as *already approved* when `approved_story_version_id == current_story_version_id`).

Audit and History (D13):

| Action | Actor | Category / visibility | `data` (ids, versions, scores, codes only) |
|---|---|---|---|
| `episode.story_approved` | system (`SystemContext`) | `episodes` / `user` | `episodeId, seriesId, storyVersionId, approvalSource: "autopilot"` |
| `autopilot.story_auto_approved` | system | `episodes` / `user` | `runId, episodeId, attempt, mode, failures[], checks: [{id, pass}], meanReviewScore, sourceSimilarity, pastEpisodeSimilarity` |
| `autopilot.video_auto_approved` | system | `episodes` / `user` | `runId, episodeId, renderJobId, failures[], checks: [{id, pass}], loudness, durationSec` |
| `autopilot.owner_action_required` | system | `episodes` / `user` | `runId, episodeId, reason, failures[]` |

`autopilot.owner_action_required` is **new** and must be added to the D13 list (open item for the TL). The video approve command writes its own `episodes`-category row (09.7.1-T1) — the gate's row is additional, so the History shows both "video approved" and "approved by autopilot" (02.8.2 ¶4).

## 5. API and events

No new HTTP route. Consumed and raised contracts:

| Direction | Contract | Owner |
|---|---|---|
| consume | `ApproveStoryCommand(Guid EpisodeId, string IfMatch, TrueCrimeAttestationDto? Attestation, ApprovalSource Source = User)` → `ErrorOr<StoryETag>`; `ApprovalSource { User, Autopilot }` | 02.3.2-T1 |
| consume | `ApproveVideoCommand(Guid EpisodeId, string? Note)` → `ErrorOr<Success>` | 09.7.1-T1 |
| consume | outbox `StoryGenerated { EpisodeId, SeriesId, UserId, GenerationRunId, StoryVersionId, Trigger }`, `StoryGenerationFailed { … ErrorCode, Trigger }` | 02.2.2-T3 |
| consume | outbox render-done (episode `video_review`, `MarkRendered`) and `VideoRejected` | 09.6.3-T1 / 09.7.1-T1 |
| consume | `story_versions.audit_report` (`audit.v1`), `render_jobs.quality_report` (05.5.1-T2) | 02.6.1-T1 / 05.5.1-T2 |
| raise | `autopilot.story_auto_approved` / `autopilot.video_auto_approved` / `autopilot.owner_action_required` audit rows | CR-3 |
| raise | outbox `AutopilotOwnerActionRequired { UserId, SeriesId, RunId, EpisodeId, Reason, Failures[], CopyKey }` → `SendEmailJob` (09.2.6) with template `autopilot_owner_action_required` (plan-local, like 06's `channel_connected`) | this feature |
| raise | SignalR `EpisodeUpdated` (00 §8) for the episode-level change | 09.8.1 |

[OPEN → CR request] The run log (10.4.3-T1) needs to update when a run changes state without an episode change (e.g. `gate_failed`, `paused`). 00 §8 lists four SignalR events only. Requested: `AutopilotRunUpdated { seriesId, runId, slotAt, state, outcome, pauseReason }`, published by the outbox handler in the Worker and forwarded by `IRealtimeNotifier` to group `user:{id}` (09.8.1). Listed under §10 open items; until then the run log polls on tab focus [INFERENCE: acceptable for one owner].

E-mail template `autopilot_owner_action_required` (vi/en, D19 — no story text, no URLs of sources): series title, episode title, slot, the failing check ids in words, a deep link to `/episodes/{id}`, and for a true-crime hold the sentence "fact sheet and story wait for your approval" (10.4.3 copy).

## 6. UI

Reference: `../../10-web-ui.md` story **10.4.3** (Autopilot tab + run log, 10.4.3-T1) and 10.5.2 (episode detail). This feature supplies the data; it designs no screen.

| 10.4.3 element | Backend field | States |
|---|---|---|
| Run row in the run log | `autopilot_runs.outcome`, `state`, `pause_reason`, `slot_at` | `planned` · `producing` · `waiting_owner` (amber) · `gate_failed` (red) · `published` (green) · `published_private_locked` (grey + lock) · `cancelled_by_owner` · `paused` · `failed` |
| Gate verdict detail | `gate_results` (`gates.v1`): per check `id`, `value`, `threshold`, `pass` | a `dry_run` badge when `mode = dry_run`; failing checks first |
| "Why did it not publish?" | `outcome` + `pause_reason` + `gate_results.story.failures[]` | copy keys below |
| Advanced thresholds | `series.autopilot_settings.gate_thresholds_ref` → profile picker (`default`/`strict`) | warning text when `strict` is chosen |

Copy keys (vi/en; both locales required by NFR i18n):

| Key | vi | en |
|---|---|---|
| `autopilot.run.state.planned` | Đã lên kế hoạch | Planned |
| `autopilot.run.state.producing` | Đang sản xuất | Producing |
| `autopilot.run.state.waiting_owner` | Chờ bạn duyệt | Waiting for you |
| `autopilot.run.gate.dry_run_badge` | Chạy thử (chưa tự duyệt) | Dry run (not auto-approved) |
| `autopilot.gate.failed.self_review_mean_score` | Điểm tự đánh giá thấp hơn ngưỡng | Self-review score below threshold |
| `autopilot.gate.failed.blocking_objective_issues` | Còn lỗi bắt buộc phải sửa | Blocking objective issues remain |
| `autopilot.gate.failed.similarity_sources` | Quá giống nguồn tham khảo | Too similar to a source |
| `autopilot.gate.failed.similarity_past_episodes` | Quá giống tập trước | Too similar to a past episode |
| `autopilot.gate.failed.real_world_signal` | Có dấu hiệu chuyện thật / người thật | Real-person / real-event signal |
| `autopilot.gate.failed.moderation_gate` | Nội dung bị kiểm duyệt gắn cờ | Content flagged by moderation |
| `autopilot.gate.hold.true_crime` | Tập true-crime luôn chờ bạn duyệt | True-crime episodes always wait for you |
| `autopilot.gate.failed.video_failed_scenes` | Thiếu hình ở một số cảnh | Missing images in some scenes |
| `autopilot.gate.failed.video_loudness` | Âm lượng ngoài khoảng cho phép | Loudness outside the allowed range |
| `autopilot.gate.failed.video_subtitle_sync` | Phụ đề có thể lệch thời gian | Subtitles may be out of sync (05 CR-6) |

Accessibility: the verdict list is a `<dl>` with the check id as the term and the value/threshold as the description (screen-reader order = failing checks first); colour is never the only signal (icon + text per state). The dry-run badge carries `aria-label` with the full sentence.

## 7. Jobs and integrations

- **No new job.** The gate runs inside the orchestrator's step: triggered by the 15-min `AutopilotPlanJob` tick (time-based steps) and by outbox handlers (`StoryGenerated`, render-done, `PublicationUpdated`) — all owned by 02.8.3-T1. This feature contributes `StoryGateEvaluator`, `VideoGateEvaluator`, `AutopilotApprovalService`, `AutopilotNotificationService`, `GateResultWriter` and `IGateFailureStreakReader`.
- **External calls:** `IContentModerator` (`omni-moderation-latest`, free, D12) and nothing else. No paid model call, so the gate cannot fail on funds; if moderation is unavailable (5xx after the D7 retry budget) the gate result is `moderation_gate = fail` with `detail = "moderator_unavailable"` → owner review, never a silent pass. [DECISION: fail closed]
- **Timeouts:** the moderator call uses the shared resilience pipeline (30 s, 2 attempts, D7).
- **Retry:** the gate itself is not retried; the **story** may be regenerated once (D17: ≤ 2 generations per run), which is T1's `attempt` counter.
- **Integrations:** 02.3.2-T1 (approve commands, ETag), 09.7.1-T1 (video approve), 02.5.6-T1 (`SimilarityScorer`, `ISimilarityCorpusProvider`), 02.6.1-T1 (`audit.v1`), 05.5.1-T2 (`quality_report`), 03 FR-03-1 (scene-prompt expansion for T2), 02.4.1-T1 (`IContentModerator`, `moderation_events`), 09.2.6 (`SendEmailJob`), 02.8.3-T1/T4 (run state, streak, pause), 10.4.3-T1 (run log).

## 8. Security and privacy

| Threat | Control |
|---|---|
| The gate silently approves content the owner would reject | Fail-closed on every unknown: a missing `quality_report` field, an unavailable moderator, a `null` `audit_report`, or a stale model review all produce `pass = false`. `true_crime` can never auto-approve (check 15 + the domain guard). Dry-run mode is the **default** (T3) |
| The gate becomes a way to bypass the attestation guard | `ApprovalSource.Autopilot` is rejected by the domain for `true_crime` (`episode_invalid_state`); the gate never supplies `Attestation` |
| Prompt/feedback loop: the model's own review decides approval | The mean-score threshold (3.5) is a *floor*, not a quality measure; the deterministic checks (1, 3, 4, 5, 9, 10, 11, 12, 13) are computed from stored data, and T3's calibration measures the false-approve rate on real episodes before `DryRun` is turned off |
| Leaking content into logs, metrics or audit rows | `gate_results.detail` is a code or a number; `audit_logs.data` holds ids/scores/codes only (D13); logs carry `runId`, check ids and codes (D19). Excerpts live only in `moderation_events` (≤ 300 chars, D12) and `audit_report` (≤ 200 chars, D22) |
| Moderation false negatives on Vietnamese | The moderator is one of three signals (checks 6–8); the framing lexicon is per language; the similarity and length checks are language-independent. Residual risk documented in T3's report |
| Retention | `gate_results` is part of `autopilot_runs`, deleted with the episode/series (D19); `moderation_events` retention per 13 ⚠️ confirm with 13.3 |

## 9. Test plan

Domain/unit (`tests/NightTales.Domain.UnitTests/Stories/Gates/`, `tests/NightTales.Application.UnitTests/Features/Autopilot/`):

| Test | Expects |
|---|---|
| `StoryGate_AllChecksPass_ReturnsPass` | `Pass = true`, 13 checks evaluated (fiction), `failures` empty |
| `StoryGate_MeanScoreBelowThreshold_Fails` | `self_review_mean_score` fails; no approve call issued |
| `StoryGate_StaleModelReview_Fails` | `self_review_present` fails (`stale = true`) |
| `StoryGate_OneBlockingObjectiveIssue_Fails` | check 4 fails; advisory/taste issues do not |
| `StoryGate_TrueCrime_AlwaysHolds` | `Pass = false`, `hold = true`, `AutoApproved = false` even with every other check green |
| `StoryGate_ContentWarningOutsideAllowList_Fails` | `self_harm_mention` fails check 13 |
| `StoryGate_ProfileOverlay_StrictRaisesThresholds` | `strict` merges over `default`; `MinMeanReviewScore = 4.0` |
| `VideoGate_QualityReportAbsent_FailsClosed` | `quality_report_present` fails, no exception |
| `VideoGate_SkippedKeyword_Fails` | check 7 fails with one entry in `quality_report.images.skipped` (03 CR-3) |
| `VideoGate_LoudnessOutsideRange_Fails` | −13.5 LUFS fails check 4 |
| `VideoGate_SubtitlesEnabledButAbsent_Fails` | check 6 fails |
| `GateResultWriter_MergesStoryThenVideo_PreservesStoryChecks` | one `gates.v1` object with both blocks |
| `GateStreak_TwoConsecutiveGateFailures_ReturnsTwo` | the SQL above, also with a `published` run in between → 0 |
| `AutopilotApproval_DryRun_NeverCallsApproveCommand` | fake command bus records zero sends; `mode = dry_run` stored |

Integration (`tests/NightTales.Api.IntegrationTests/Features/Autopilot/`, real PostgreSQL 17 via `PostgresFixture` + `FakeTextClient`/`FakeContentModerator` from D20):

| Test | Expects |
|---|---|
| `AutoApproveStory_EndToEnd_SetsStoryApprovedAndWritesAudit` | episode `story_approved`, `approved_story_version_id` set, two audit rows, History (`GET /episodes/{id}/activity` when S lands) contains both |
| `AutoApproveStory_SecondCall_IsIdempotent` | a repeated orchestrator step does not create a second version or a second approval |
| `AutoApproveStory_TrueCrime_LeavesStoryInReview` | status unchanged, `AutopilotOwnerActionRequired` in the outbox |
| `AutoApproveVideo_EndToEnd_SetsReady` | episode `ready`, `autopilot.video_auto_approved` written |
| `GateFailure_TwoRunsInARow_StreakIsTwo` | 02.8.3-T4 sees the streak (cross-feature test lives in 02.8.3-T4) |

Manual: the T3 dry-run calibration over the first 10 real episodes (this is the acceptance evidence for the story, not a unit test).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.8.2-T1 | Gate engine: story gate + video gate + `gate_results` + audit/History + true-crime hold | M 1.0 | 02.3.2-T1, 02.6.1-T1, 02.5.6-T1, 05.5.1-T2, 02.4.1-T1 | 02.8.2-T2, 02.8.2-T3, 02.8.3-T1/T4 |
| 02.8.2-T2 | Content gate: moderation over narration/metadata/scene prompts + real-world detector | S 0.5 | 02.8.2-T1, 02.4.1-T1, 03.1.1-T1 (templates), 02.6.1-T1 | 02.8.2-T3, 08.4.4-T1 (08 CR-5) |
| 02.8.2-T3 | Thresholds config + dry-run calibration | S 0.5 | 02.8.2-T1, 02.8.2-T2 | 02.8.3-T1 (live gates), 10.4.3-T2 (10 CR-6) |
| **Total** | | **2.0 d** | | |

High-level 02 §6 had 02.8.2-T1 M + T2 S = 1.5 d; README §9 re-estimated 1.5 → 2.0 d, explained by the calibration dry-run (F31). The third task (T3) is the extra 0.5 d; T1 and T2 keep their sizes. The totals therefore match README §9 exactly.

### Open items for the TL

1. ~~`autopilot.owner_action_required` missing from D13~~ **Accepted** (README §10 T1): added to D13's action-key list. §4's audit table uses it directly, no longer conditional.
2. ~~SignalR `AutopilotRunUpdated`~~ **Rejected** (README §10 T2): no new event in P. The run log re-fetches on `EpisodeUpdated` and polls every 30 s while the tab is open (02.8.3 plan §5/§6).
3. ~~Thresholds owner-editable at runtime~~ **No** (README §10 T3): thresholds stay in appsettings per profile; no `system_settings` migration.
4. **Threshold profiles vs raw numbers** — unchanged by the TL round: a profile name (`gate_thresholds_ref`) is chosen; per-series numeric overrides are out of P.
5. ✅ **`quality_report` field names** (`pass`, `duration_sec`, `tts_duration_sec`, `integrated_lufs`, `width`, `height`, `subtitles_present`, `hints`, `images.skipped`, `images.moderated_count` (03 D7, CR-3), `sha256`) are confirmed by `render-quality.v1` (05 README D10) and `render_jobs.params.subtitles.enabled` by `render-params.v1` (D11) (05 CR-6); T1 still reads them defensively (absent → fail closed).

## 11. Rollout

1. **Order:** T2 (detector + moderation wiring, no gate decisions yet) → T1 (engine + `gate_results` + approvals) → T3 (thresholds + dry run) → 02.8.3-T1 wires the gate into the run state machine.
2. **Default is dry run.** `Autopilot:Gates:DryRun = true` until the owner has reviewed `Autopilot:Gates:DryRunMaxEpisodes = 10` evaluated episodes (T3 §Design). In dry run the pipeline never auto-approves: every fiction episode waits for the owner, so the feature cannot publish anything on its own before it is trusted.
3. **Flipping to live** is an owner action: read the calibration report, then set `DryRun = false` in configuration. The change is recorded in the run log by the `mode` field of every subsequent verdict, so "when did gates go live" is answerable from `autopilot_runs`.
4. **Monitoring:** metrics `autopilot_gate_total{gate,result}`, `autopilot_gate_check_failed_total{check}`, `autopilot_gate_duration_seconds`, `moderation_flags_total{stage="autopilot_gate",category}` (D19), `autopilot_gate_dry_run_remaining`. Alert (11.6.2) if `autopilot_gate_total{result="fail"}` exceeds 50 % of verdicts over 24 h — a sign the thresholds are wrong, not that the model got worse.
5. **Rollback:** set `DryRun = true` (one config change, no migration). The gate then only records. `gate_results` rows stay valid; nothing else changes. Reverting T1's code is not needed for a threshold problem.
6. **Per-series off-switch:** a series with `autopilot_settings.gate_thresholds_ref` pointing at a missing profile falls back to `default` and logs a warning — it never disables the gate.
