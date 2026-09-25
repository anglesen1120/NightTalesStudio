# 03.4 — Failures (MPT image spike, image relay, outcome mapping)

> Refines: `../../../03-image-generation.md` §4 epic 03.4 (stories **03.4.1**, **03.4.2**) + new story **03.4.3** (image relay, README F05), FR-03-4, FR-03-5, NFR-03-1, G2, G6, G7, §5.4, §10 CR-3/CR-4
> Contract: `../README.md` — **D4** (submit fields), **D5** (relay), **D6** (`images.v1`), **D7** (outcome table), **D8** (metering), **D9** (quality), **D10** (codes), **D12** (node data), **D14** (test kit), **F01**, **F04**, **F05**, **F06**, **F08**, **F09**, **F15**, **CR-3**, **CR-5**, **CR-6**, **CR-9**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.**
- 03.4.1 *Spike: MPT image failure reporting* — "As the Tech Lead I want to know what the task API returns when one image is blocked so that the backend can react correctly." Must · S → **M** (detailed 1.0 d).
- 03.4.3 *Image relay* (new) — "As the platform I want every image call measured, paced and attributable to a render so that cost, rate limits and blocked scenes are visible without forking MPT." Must · M (detailed 1.25 d incl. the mock).
- 03.4.2 *Moderation handling* — "As a creator I want a clear message when an image is blocked so that I can fix it." Must · S → **M** (detailed 1.0 d).

**In scope.**
- Spike on the Mac with a scripted image stub: moderation, 429 bursts, 5xx, read timeout, all-fail, long styled terms, comma strings, order, clip duration, task API error text; output `worker-node/contract/images.md` + samples (03.4.1-T1).
- The relay inside `mpt-adapter` (D5), the `images.v1` report in the adapter status (D6), and the mock-mpt equivalent (03.4.3-T1, 03.4.3-T2).
- Backend: `ImageReportV1` DTO, `ImageOutcomeClassifier`, `quality_report.images`, `moderation_events`, image `usage_records` on every terminal state, retry classification, funds signal (03.4.2-T1).

**Out of scope.**
- The adapter's other routes, node provisioning, firewall (11.3.x); the render jobs' polling/ingest skeleton (09.6.3); the video review UI (10.5, copy via CR-7); the autopilot video gate (02.8.2-T1, reads our fields via CR-3).
- Deliberately provoking real moderation blocks against OpenAI (D14: never).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| X1 | A blocked image (400) is not retried and is skipped; the next term is used; clips loop; the task succeeds (I4, I6–I8) | README §1 ✅ |
| X2 | Only a total image failure fails the task (`materials`, I9) | README §1 ✅ |
| X3 | MPT logs the error message but not `error.code` (I6) and logs full prompts (I17) | README §1 ✅ |
| X4 | Empty image key → MPT sends no `Authorization` (I15) — the relay can own the key | README §1 ✅ |
| X5 | 429 retries: 3 attempts, waits 5 s + 15 s (I4); read timeouts never retried (I5) | README §1 ✅ |
| X6 | 00 §7: never auto-retry `image_moderated`; max 2 automatic retries on transient errors | 00 §7 ✅ |
| X7 | 09.6.3: `PollRenderJob` maps adapter failure codes; `IngestRenderOutputJob` builds URLs from `base_url` + task id; guards against duplicates | 09 §4 ✅ (P8) |
| X8 | 11.1.2 mock-mpt failure injection includes `image_moderated`; trigger keyword `__fail_image__` | 11 §3, §4 ✅ |
| X9 | 12.8.3 P subset: `provider_funds_exhausted` pause reason; "≥ 3 renders fail at the image stage within 10 min" is one trigger | 12 §4 12.8.3 ✅; 02 README D2 |
| X10 | `moderation_events(user_id, episode_id, source, stage, categories, excerpt)` | 00 §6 ✅ |
| X11 | 02.8.2-T1 `VideoGateInput(…, int ImageModerationEvents)`; check 7 `failed_scenes + keywords_skipped = 0` | 02.8.2-T1 ✅ (P5) |

**Closed here.** FR-03-5 semantics (D7). Where per-image data comes from (relay, D5/D6). Metering on failures (D8, F15). Retry classes per code (D7).

## 3. Domain and data

### 3.1 Node side (relay, 03.4.3-T1)

No database. Per-task JSONL `${ImageRelay:DataDir}/<adapterTaskId>.jsonl` (D5 fields), deleted by the adapter cleanup. In-memory plan registry `taskId → { slots: [sha256…], used: bitset, max_images, calls }`, rebuilt from the JSONL + the adapter's own task store on restart (⚠️ the adapter's task store is 11.3.5's design).

### 3.2 Backend types

| File | Type |
|---|---|
| `NightTales.RenderNode.Contract/ImageReportV1.cs` (created by 03.4.3-T1, consumed here; 11 CR-5) | DTO mirroring D6 (`Planned`, `Needed`, `Called`, `Succeeded`, `Failed[]`, `Unattempted`, `QualitySent`, `QualityEchoed[]`, `Size`, `Model`, `Usage[]`, `RelayRejections`) |
| `Domain/Rendering/Images/ImageOutcome.cs` | `sealed record ImageOutcome(ImageOutcomeKind Kind, string? ErrorCode, string? Detail, bool Retryable, IReadOnlyList<SkippedScene> Skipped, int ModeratedCount, bool FundsExhausted)`; `ImageOutcomeKind = Ok | OkWithSkips | Failed` |
| `Domain/Rendering/Images/ImageOutcomeClassifier.cs` | `static ImageOutcome Classify(MptTerminal terminal, ImageReportV1? report, IReadOnlyList<string> slotSegments, ImagePolicy policy)` — the D7 table |
| `Domain/Rendering/Images/ImagePolicy.cs` | `sealed record ImagePolicy(double MaxSkippedShare, IReadOnlySet<string> FundsCodes)` |
| `Domain/Rendering/Images/SkippedScene.cs` | `sealed record SkippedScene(int Slot, string Segment, string Reason)` — `Reason ∈ moderated | rate_limited | upstream_error | timeout | relay_rejected` |
| `Application/Features/Rendering/Images/RecordImageUsage.cs` | writes D8 rows, idempotent |
| `Application/Features/Rendering/Images/ImageModerationRecorder.cs` | writes `moderation_events` rows (D7) |

### 3.3 `render_jobs.quality_report.images` (owner: this feature; 05.5.1 owns the rest of `quality_report`)

```json
{ "schema": "images.v1", "needed": 30, "succeeded": 29, "trimmed_count": 0, "moderated_count": 1,
  "skipped": [ { "slot": 7, "segment": "p:4", "reason": "moderated" } ],
  "hints": ["images_skipped"], "quality": "medium", "style_prompt_version": "style=…;neg=v1" }
```

### 3.4 Migration

None: `quality_report` and `params` are jsonb; `moderation_events` and `usage_records` exist (09.2.2). `moderation_events.stage` gains the value `render_image` — free text today ⚠️ (if 09.2.2 made it a CHECK, the value is added in 09.2.2's migration before M1; CR-9 note).

## 4. Use cases

No new command. Changes inside the 09.6.3 jobs (hand-over; the code lives in 09.6.3's files, the logic here):

| Job step | Behaviour |
|---|---|
| `PollRenderJob` sees a terminal adapter state | read `images.v1` from the status; `RecordImageUsage` (own tx); `ImageOutcomeClassifier.Classify`; on `Failed` → render job `failed` with `error_code`/detail, episode → `story_approved`, `ImageModerationRecorder` when moderated, `ICreditLedger.RefundAsync` (metering-only in P), retry per `Retryable` (≤ 2, X6); on `Ok`/`OkWithSkips` → hand to ingest with the outcome stored |
| `IngestRenderOutputJob` | writes `quality_report.images` (§3.3) next to 05.5.1's checks; `ImageModerationRecorder` for moderated slots (row 2 of D7); raises `RenderSucceeded` |
| Funds signal | `FundsExhausted = true` → the 12.8.3 P-subset handler (pause reason `provider_funds_exhausted`) |
| Adapter cleanup | only after usage + events are written (F15) |

## 5. API and events

- Adapter status (node → Worker, private): `images` object = `images.v1` for terminal tasks (CR-5). Example in 03.4.3-T1.
- No public route change. `GET /episodes/{id}` already returns the latest render job; it adds `renderJob.images { needed, succeeded, skipped[] { segment, reason } }` (read model in 09.5.3; CR-7 for the UI).
- No new domain event; `RenderSucceeded` (02 CR-23) is unchanged.

## 6. UI

Owned by 10.5 (CR-7). Copy keys from this feature:

| Key | en | vi |
|---|---|---|
| `render.images.skippedBanner` | {count} scene(s) could not be drawn. The video repeats nearby images there. | Không vẽ được {count} cảnh. Video lặp lại hình gần đó ở những chỗ này. |
| `render.images.moderatedScene` | This scene was blocked by the image safety filter. Describe it differently and make the video again. | Cảnh này bị bộ lọc an toàn hình ảnh chặn. Hãy mô tả khác đi và làm lại video. |
| `errors.image_moderated` | No video was made: too many scenes were blocked by the image safety filter. | Chưa làm được video: quá nhiều cảnh bị bộ lọc an toàn hình ảnh chặn. |
| `errors.image_generation_failed` | Images could not be created right now. We will try again automatically. | Hiện chưa tạo được hình. Hệ thống sẽ tự thử lại. |

## 7. Jobs and integrations

| Path | Timeouts / retries | Failure mapping |
|---|---|---|
| MPT → relay (loopback) | MPT read timeout 300 s (I5); relay answers within `RequestDeadlineSeconds` 270 s < 300 s (pacing ≤ 90 s, read and relay retries clipped to the deadline, DoR DR-1) | relay 429 `relay_rate_limited` → MPT retries 5 s/15 s then skips |
| Relay → OpenAI | connect 10 s, read `min(180 s, deadline left)` (the relay answers first); retries only upstream 429 `rate_limit_exceeded` (≤ 2, `Retry-After` ≤ 30 s) | quota codes → passed through, reported as funds; 5xx passed through (MPT retries) |
| Worker → adapter status | 09.6.2 resilience | missing `images` on a terminal task → D7 row 8 / `OkWithSkips` unknown → `estimated` usage (D8) |

## 8. Security and privacy

| Threat | Control |
|---|---|
| Node image key leak via MPT config or logs (F09) | key only in the relay process (node secret, 11.3.2); MPT config has `openai_image_api_keys = []` |
| Relay used as an open proxy by anything on the node | loopback bind; only `POST /v1/images/generations`; only prompts registered by an active plan (`relay_unknown_prompt`); per-task cap |
| Unstyled or fragment prompts (F04) | `relay_prompt_unstyled` |
| Content in relay logs/JSONL | hashes, codes, counts only (D5, D12) |
| Content in `moderation_events` | `excerpt` = the keyword only (≤ 300 chars), admin view audited (09.9, `content_viewed`) |
| Double billing on retries | relay retries only 429 rate limits (not billed); never a read timeout (same rule as MPT, I5) |

Retention: relay JSONL deleted at cleanup; `moderation_events` follow 13 retention; `usage_records` kept with `episode_id` nulled on deletion (02 D19).

## 9. Test plan

- **Spike (manual, scripted):** P1–P12 of 03.4.1-T1 with the stub.
- **Relay (unit + component):** request checks in order; pacing (token bucket with a fake clock); attribution incl. duplicate hashes and two tasks; cap; quota vs rate-limit 429; JSONL has no prompt text; restart rebuilds counts.
- **Classifier (unit):** every row of D7 + boundaries (`skippedShare` = 0.10 and 0.1001); no report; funds codes.
- **Integration (Worker + mock-mpt):** `MOCK_IMAGE_REPORT=ok|moderated:3|skipped:0.15|funds|none` → job state, episode state, `moderation_events`, `usage_records` counts, retries, cleanup order.
- **Contract:** adapter status sample from the spike replayed against mock-mpt and the relay's report builder (11.3.5 contract tests).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 03.4.1-T1 | Spike: MPT image failure behaviour with an image stub | Worker | **M (1.0)** | 01.2.2-T5 (MPT runs), 01.3.1-T2 (tap, for P12) | 03.3.1-T1, 03.4.3-T1, 05.4.1 (images part), 11.1.2-T1 |
| 03.4.3-T1 | Image relay in `mpt-adapter` + `images.v1` in status | Worker/BE | **M (1.0)** | 03.4.1-T1, 11.3.5-T1, 03.1.1-T2 (clause), 03.3.1-T2 (quality value) | 03.4.3-T2, 03.4.2-T1 |
| 03.4.3-T2 | mock-mpt `images.v1` report + `MOCK_IMAGE_REPORT` | BE | **XS (0.25)** | 03.4.3-T1 (shape), 11.1.2-T1 | 03.4.2-T1 |
| 03.4.2-T1 | Outcome mapping, `quality_report.images`, moderation events, metering, retries | BE | **M (1.0)** | 03.2.1-T1, 03.4.3-T2, 09.6.3-T1, 02.2.4-T1 | 02.8.2-T1 (CR-3), 10.5 (CR-7) |

Total **3.25 d** (high-level 1.0 d; README §9).

### Open items for the TL

- ✅ Resolved (11 CR-5): `mpt-adapter` is .NET 10 (11 D3). It may not reference Domain, so the relay's config carries the clause text with the startup hash self-check (03.1.1-T2).
- [OPEN] Whether a blocked image request is billed at all ⚠️ — the spike cannot show it (stub); the first real block in M0/M1 is checked in the Costs dashboard; D8 records `usage` only when the response carries it.
- [OPEN] README §10 O2 (fail > 10 % skipped) is the owner's.

## 11. Rollout

- **Order:** 03.4.1-T1 (M0) → 11.3.5-T1 → 03.4.3-T1 → 03.4.3-T2 → 03.4.2-T1.
- **Flag:** `ImageRelay:Enabled` on the node (default true). With `false` the node MPT config points at OpenAI directly with the key (emergency only): the adapter then returns `images = null`, the backend falls back to D7 row 8 / estimated usage, and an alert fires (`image_report_missing_total`).
- **Config:** node `ImageRelay:{Port 8091, Model, Quality, Moderation auto, ImagesPerMinute 5, MaxWaitSeconds 90, UpstreamReadSeconds 180, RequestDeadlineSeconds 270, DataDir}`; backend `Images:MaxSkippedShare 0.10`.
- **Monitoring:** `image_calls_total{status,error_code}`, `image_relay_wait_seconds`, `image_report_missing_total`, `render_images_skipped_total{reason}`, `moderation_events_total{source=openai_image,style}`; alert when `moderated ÷ called` > 5 % over 24 h per style (retire the template, 03 §8).
- **Rollback:** relay off (above); classifier thresholds are config.
