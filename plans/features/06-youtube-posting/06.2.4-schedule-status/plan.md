# 06.2.4 · 06.2.5 — Scheduling (lead time, `publishAt`, pre-audit) and processing status (poll, go-live, episode status)

> Folder: `plans/features/06-youtube-posting/06.2.4-schedule-status/`. Tasks: **06.2.4-T1** (S 0.5) and **06.2.5-T1** (M 1.0) per `../README.md` §8.1. Phase **P · M1**, behind flag `youtube_publish` (only the recurring registration is flag-gated; both tasks add no route).
> Contract: `../README.md` §1 (G3, G6), §2 (F13, F15, F06, F07), §3 **D11, D12, D13** + D10/D18/D19/D20, §8.1. Consumers: `../../features/02-story-writing/02.8.3-cadence-publishing/02.8.3-T3-publish-handoff-pre-audit-mode.md`, 02.8.3 `plan.md:436-443`, 10.4.3-T2, 06.5.1-T1, 11.6.2-T1.
> Legend: ✅ verified (file:line or URL) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In.** The two closing steps of the 06.2 publishing epic, after the upload job of 06.2.3-T2:

- `06.2.4-T1` — the upload *request* decision only: `privacyStatus`/`publishAt` sent to `videos.insert` ✅ G3; a `publishAt` is included **only** for `Privacy == "public"` non-pre-audit rows (YouTube itself flips it to public at that instant); every other case (pre-audit, an owner-intended `private` row, catch-up) **omits** `publishAt` and sends the row's own `Privacy` as-is (forced `private` only in pre-audit), and no go-live job is ever scheduled for it ✅ README D12 + `02.8.3-T3:60-71,162` + YR2. The queue instant itself (lead time) is **not** this task's — README ruling **YR3** moved it to 06.2.2-T1's pure `PublicationQueueTime.For`, invoked by 06.2.3-T2's Worker-side outbox handler.
- `06.2.5-T1` — `PollYouTubeProcessingJob` (60 s, ≤ 120 min) mapping `uploadStatus` / `failureReason` / `rejectionReason` ✅ G6 → publication status; `PublicationGoLiveJob` at `publish_at + 5 min`, retry every 15 min for 120 min ✅ README D12 / 06 §4 (06.2.5); `publication_updated.v1` outbox + SignalR `PublicationUpdated` (ids only) ✅ D13 + `09.8.1-T1:114-129`; episode `ready → scheduled → published` via publications ✅ `00-overview.md:336`, D12/F15.

**Out (consume, never fork).** Consent/veto/journal/e-mails (06.2.2-T1/T2/T3), resumable upload transport, session URI, `OAuthErrorClassifier`, `FakeGoogleServer` (06.2.3-T1/T2/T3), the quota ledger and the PT-day maths (06.3.1-T1, D10), disconnect/token health/`platform_incidents` (06.4.x, D14/D16), cancel (06.2.6-T1), the audit evidence pack (06.5.1-T1). **No HTTP route, no new table, no migration** in this folder — D20 assigns `_06_Publications` (incl. `veto_ends_at`) to 06.2.1-T1, and every column used here already exists in `00-overview.md:290`.

**Success.** A publication created by the owner or by autopilot uploads at the right instant, cannot go public while the project is unaudited, ends in `published` (or `published` + `privacy=private` in pre-audit), and `02.8.3-T3` can map the outcome to `published` / `published_private_locked` from an event that carries ids only.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| G3 | `status.publishAt` can be set only when the video is `private`, ISO 8601, only if the video was never published; a past date publishes at once; `invalidPublishAt` 400 | 06 README G3 ✅ |
| G6 | `status.uploadStatus ∈ deleted, failed, processed, rejected, uploaded`; `failureReason ∈ codec, conversion, emptyFile, invalidFile, tooSmall, uploadAborted`; `rejectionReason ∈ claim, copyright, duplicate, inappropriate, legal, length, termsOfUse, trademark, uploaderAccountClosed, uploaderAccountSuspended`; `privacyStatus ∈ private, public, unlisted`; `processingDetails` is owner-only | 06 README G6 ✅ |
| G1 | `videos.list` = 1 unit of the 10,000-unit/day bucket (not the 100/day upload bucket); quota day = PT | 06 README G1 ✅ |
| C4 | Pre-audit: upload `private`, **no** `publishAt`; P reads `YouTube:AuditStatus` through `IOptionsMonitor` | 06 README C4 (`02.8.3-T3:162`, 09 WT27/W2 CR-10) ✅ |
| C5 | Go-live: `PublicationGoLiveJob` at `publish_at + 5 min`, every 15 min for 2 h; 02's fallback at `publish_at + 10 min` | 06 README C5 (`02.8.3 plan.md:39,442`) ✅ |
| D11 | One option class `YouTubeOptions`: `PublishLeadMinutes` 120, `MinVetoWindowHours` 2, `AuditStatus`, `Poll:IntervalSeconds` 60, `Poll:MaxMinutes` 120, `GoLive:DelayMinutes` 5, `GoLive:RetryMinutes` 15, `GoLive:MaxMinutes` 120 | 06 README D11 ✅ |
| D12 | Queue time = now or `publishAt − PublishLeadMinutes`; job enqueued at consent; pre-audit ends `published` with `privacy = private` and `platform_payload.preAudit = true` (no new status); poll 60 s ≤ 120 min; `processed` → `published` (or stays `scheduled` while `publishAt` is future → go-live at `publishAt + 5 min`, every 15 min for 120 min, then `failed` `youtube_go_live_unconfirmed` + admin alert); `failed`/`rejected` → `failed`; `Episode.MarkScheduled/MarkPublished` in the same transaction | 06 README D12 ✅ |
| D13 | Outbox `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }` → `IRealtimeNotifier` `PublicationUpdated` with `{ publicationId, episodeId }` and **no data** | 06 README D13 + F13 ✅ |
| YR1 | [DECISION] TL ruling 2026-09-25: `veto_ends_at = min(now + veto_window_hours, (publishAt ?? now) − PublishLeadMinutes)`; if that is `< now + MinVetoWindowHours` → `CreatePublication` rejects 400 `validation_failed` field `publishAt`; 02 now reads `PublishLeadMinutes`/`MinVetoWindowHours`/`AuditStatus` from `YouTubeOptions` (06 CR-3) and treats quota exhaustion as `scheduled` (06 CR-4) | parent ruling, recorded in 06 README §3/§10 ✅ |
| M1 | `Publication`: `awaiting_consent → scheduled → uploading → processing → published | failed | cancelled`; cannot leave `awaiting_consent` without `consent_at` | `00-overview.md:333` (06 README §7) ✅ |
| M2 | Episode: `ready → scheduled → published (via publications)` | `00-overview.md:336` ✅ |
| T1 | `publications` columns: `status, title, description, tags, privacy, publish_at, next_attempt_at, consent_at, consent_text_version, consent_source, autopilot_authorization_id, veto_notice_sent_at, contains_synthetic_media, external_post_id, platform_payload jsonb, error_code` | `00-overview.md:290` ✅ |
| T2 | D18 index `publications (status, next_attempt_at) WHERE status IN ('awaiting_consent','scheduled','uploading','processing')` + `publications (channel_connection_id)` | 06 README D18 ✅ |
| R1 | `IRealtimeNotifier.NotifyAsync(Guid userId, RealtimeEvent e, CancellationToken ct)`; `RealtimeEvent(RealtimeEventName Name, RealtimeIds Ids, RealtimeData? Data = null)`; `PublicationUpdated` ∈ the four names; `Data` is `null` for every event except `RenderProgress`; body ≤ 8 KB; `pg_notify` **inside the caller's transaction**; the client refetches `GET /episodes/{id}` | `09.8.1-realtime/plan.md:114-129, 79-94, 176, 203` ✅ |
| R2 | `IRealtimeNotifier`, hub and `PgNotifyRealtimeNotifier` are owned by 09.8.1-T1 (consumer 06 = this take, 06 CR-8) | `09.8.1-realtime/plan.md:35, 252` ✅ |
| O1 | Outbox: `[OutboxEvent("name.v1")]` on the event; `outbox_messages(type, payload jsonb, user_id, occurred_at, next_attempt_at, attempts, processed_at, failed_at, error, trace_parent)`; dispatch `WHERE processed_at IS NULL AND failed_at IS NULL AND next_attempt_at <= now() ORDER BY occurred_at LIMIT @n FOR UPDATE SKIP LOCKED`, per-message DI scope under `ISystemContext.For(user_id)`, 10 attempts then dead-letter; handlers idempotent by contract (at-least-once) | `09-backend-api/README.md:259-275` ✅ |
| O2 | Jobs: `IJobScheduler.Enqueue/Schedule/AddOrUpdateRecurring/RemoveRecurringIfExists`; job classes `Application/Features/<F>/Jobs/<Name>Job.cs` implementing `IBackgroundJob`, ids-only (architecture test); `[JobPolicy(Queue = JobQueue.Publish, Retries = 0)]` (default 0 retries); queues `default, render, publish, email`; recurring registered at Worker start via `IRecurringJobRegistration`; the Api's `IJobScheduler` throws | `09-backend-api/README.md:276-288` ✅ |
| O3 | Validation error shape: `validation_failed` 400 with `errors` grouped by camelCase field | `09-backend-api/README.md:188`; `09-backend-api.md:375` ✅ |
| O4 | System jobs: outbox/Worker jobs run under `SystemContext(userId)`; `IgnoreQueryFilters` only in `Features/Admin` + allow-listed system jobs; a missing `userId` fails the job | `02.8.3 plan.md:35` (09 §4) ✅ |
| P1 | 02.8.3 hand-off: `PublishAt: preAudit ? null : run.SlotAt`, `ContainsSyntheticMedia: true`; pre-audit → `privacy = private`, `publishAt = null` **in the recorded request**, go-live check skipped, run outcome `published_private_locked`; flipping `audit_status` between creation and go-live leaves that slot private | `02.8.3-T3:34, 60-71, 107-109, 156, 162` ✅ |
| P2 | 02.8.3's time-based fallbacks make its run machine self-healing; `scheduled → published` fallback evaluated at `publish_at + 10 min` off `PublicationUpdated` | `02.8.3 plan.md:436-443` ✅ |
| P3 | `youtube_quota_used` is an 09.9.3 metric; 11.6.2-T1 asserts arrival of the D13 metric list and owns the alert rules | `11.6-operations/plan.md:40, 53` ✅ |
| O5 | `YouTubeOptions` + `YouTubeOptionsValidator` are **06.1.2-T1's** (`src/NightTales.Application/Abstractions/Publishing/YouTubeOptions.cs`), which **declares** `ActiveClientId`, `Clients` (`YouTube:Clients:<clientId>:SecretRef`), `Http:TimeoutSeconds`, `Pkce`, `AuthorizationEndpoint`, `TokenEndpoint`, `RevokeEndpoint`, `ApiBaseUrl`, `OAuthStateMinutes`, `Scopes`, `PublishLeadMinutes`, `MinVetoWindowHours`, `AuditStatus`, `ConsentTextVersion`, `Quota:UnitsPerDay` **and `Poll:*`/`GoLive:*`** (06.2.3-T1's own read confirms "it already carries … `Poll`, `GoLive`") — but its validator (`:207`) and its tests (`:250`) only cover `ActiveClientId`/`Clients`/scopes/https endpoints; **`Poll:*`/`GoLive:*` have no validator range checks anywhere** — recorded as a CR to `06.1.2-T1` (§10), not fixed here. **06.2.4-T1 no longer reads `YouTubeOptions` at all** (YR3 moved the lead-time computation to `06.2.2-T1`'s `PublicationQueueTime.For`) | `06.1-connect/06.1.2-T1-connect-callback.md:57, 207, 250`; `06.2.3-upload/06.2.3-T1-…md:78-81, 194` ✅ |
| O6 | `Publication` methods owned by 06.2.3-T2: `StartUpload`, `AttachUploadSession`, `MarkProcessing(videoId)` (`uploading → processing`: sets `external_post_id`, **clears `next_attempt_at`**, `error_code = null`), `MarkUploadedExternal`, `RescheduleUpload(errorCode, nextAttemptAt)`, `FailUpload`, `HoldForIncident` — each raising `publication_updated.v1`; `platform_payload` is written through the typed helper `PublicationPayload.With(...)`; **`Episode.MarkScheduled` is explicitly assigned to 06.2.2-T1 *or* 06.2.5-T1** | `06.2.3-upload/plan.md:56, 58, 60, 133`; `06.2.3-T2:60, 75, 142, 147, 149` ✅ |
| O7 | Error codes are a **closed** `static class ErrorCodes` (`src/NightTales.Application/Common/Errors/ErrorCodes.cs`); the HTTP status comes from `Api/Http/ErrorCatalogue.cs`, not `ErrorType`; a code absent from the catalogue fails a test — 06.3.1-T1 states the reuse rule ("no new error constant") | `09-backend-api/README.md:187`; `06.3.1-quota-audit/06.3.1-T1-…md:159`; its `plan.md:45` ✅ |
| O8 | Quota deferral keeps `status = 'scheduled'`, `error_code = youtube_quota_exhausted`, `next_attempt_at` = next PT midnight, **`external_post_id` still NULL** (the upload never started); an incident hold sets `next_attempt_at = null` + `error_code = platform_incident` | `06.2.3-upload/plan.md:58, 60`; `06.2.3-T2:147, 149` ✅ |
| O9 | [SUPERSEDED — README YR3] `IPublishScheduler` never shipped. The real mechanism: the Api **never** enqueues; a consent transition to `scheduled` (user, `PATCH`, or autopilot) writes the ordinary `publication_updated.v1` (D13 shape, **no** `queueAt` field); the Worker's `PublicationScheduledForUploadHandler` (`IOutboxHandler<PublicationUpdated>`, owned by 06.2.3-T2) re-reads the row and calls `IJobScheduler.Enqueue`/`Schedule<PublishYouTubeJob>` at `PublicationQueueTime.For(publishAt, PublishLeadMinutes, now)` — a **pure Domain function declared by 06.2.2-T1**, reused by the handler; quota retries and incident clears reschedule through the same handler | README `../README.md:258` (YR3) ✅ |
| O10 | `platform_payload.upload` is written by **06.2.4-T1**, read **verbatim** by `PublishYouTubeJob` to build `VideoInsertRequest` (`privacyStatus`, `publishAt?`; never from live `YouTubeOptions.AuditStatus`); shape asserted by 06.2.3-T2's own acceptance test with `platform_payload.upload = { privacyStatus: "private", preAudit: true }`. **This task writes no `queueAt`/`next_attempt_at`** — that instant is `06.2.2-T1`'s `PublicationQueueTime.For`, recomputed independently by the Worker handler (O9) | `06.2.3-upload/06.2.3-T2-…md:58, 136, 160, 192` ✅ |

## 3. Domain and data

**No DDL.** Every column exists (`00-overview.md:290`); the two indexes of D18 are created by 06.2.1-T1's expand-only `_06_Publications` migration. If a column turns out to be missing at implementation, stop and raise a CR — do not add a column here.

**Types.** `Publication` is the aggregate of 06.2.1-T1 (`src/NightTales.Domain/Publishing/Publication.cs`). Both tasks extend it in partial files (the 09 convention for feature-owned aggregate methods: `Episode.cs` is 09.5.1's, 02 adds `Episode.Story.cs` — `09-backend-api/README.md:339` ✅):

```csharp
// src/NightTales.Domain/Publishing/Publication.Schedule.cs      (06.2.4-T1)
public sealed record PublicationUploadPlan(string PrivacyStatus, DateTimeOffset? PublishAt);
public static ErrorOr<PublicationUploadPlan> PlanUpload(PublicationMetadata m, PublicationPayload payload, DateTimeOffset now);
        // reads Payload.PreAudit/Publication.Privacy/Publication.PublishAt (06.2.1-T1's stamps + the row); no options, no AuditStatus lookup,
        // no queue-time arithmetic (YR3 moved that to 06.2.2-T1's PublicationQueueTime.For, used by the Worker handler, O9)
public ErrorOr<Success> ApplyUploadPlan(PublicationUploadPlan plan, DateTimeOffset now);   // writes platform_payload.upload only
public bool IsPreAudit { get; }

// src/NightTales.Domain/Publishing/Publication.Processing.cs    (06.2.5-T1) — additive; MarkProcessing stays 06.2.3-T2's (O6)
public ErrorOr<PublicationProcessingOutcome> MarkProcessed(string uploadStatus, string? failureReason,
        string? rejectionReason, string? privacyStatus, DateTimeOffset now);   // primitives only — YouTubeVideoStatus is Application's (06.2.3-T1:58), Domain never references it
public ErrorOr<Success> MarkRejected(string rejectionReason, DateTimeOffset now);
public ErrorOr<Success> MarkProcessingFailed(string errorCode, DateTimeOffset now);   // processing -> failed
public ErrorOr<Success> MarkLive(DateTimeOffset now);                                  // scheduled|processing -> published
public ErrorOr<Success> MarkGoLiveUnconfirmed(DateTimeOffset now);                      // -> failed, error_code set
// no SetProcessingDeadline: 06.2.3-T2's MarkProcessing(videoId) already seeds
// platform_payload.processing { startedAt, deadlineAt, polls = 0 } at upload completion (O6);
// 06.2.5-T1 only increments polls / advances next_attempt_at on each re-claim
// src/NightTales.Domain/Episodes/Episode.Publishing.cs          (06.2.5-T1) — MarkScheduled is legitimately ours too (O6:133)
public ErrorOr<Success> MarkScheduled(Guid publicationId, DateTimeOffset now);   // only when still ready
public ErrorOr<Success> MarkPublished(Guid publicationId, DateTimeOffset now);   // scheduled -> published
```

No new publication status, no `Episode.MarkPublicationFailed`: `00-overview.md:336` defines no `scheduled → ready` edge and `02.8.3-T3:83` says the episode is "unchanged" on a publication failure, so the episode **stays `scheduled`** and only the publication row goes `failed` (a re-publish path is 02's decision, not this folder's). `ErrorOr<T>` is the repo-wide result type ✅ `09-backend-api/README.md:188`. `YouTubeOptions.AuditStatus` (D11) is consumed by **06.2.1-T1 at create time only** (it stamps `Privacy`/`requestedPrivacy`/`preAudit` there) — `PlanUpload`/`ApplyUploadPlan` never read `YouTubeOptions` at all (O5); they compute `PrivacyStatus` from the already-stamped `Payload.PreAudit`/`Publication.Privacy`/`Publication.PublishAt` (invariant 1). The queue instant itself is **not** this task's concern (O9/O10, YR3).

**`platform_payload` is the only carrier** (no new columns, D12/F13). The typed helper `PublicationPayload.With(...)` and its key map are **06.2.3-T2's** (`06.2.3-upload/plan.md` §3.2, `06.2.3-T2:75`, CR-15 one writer per field): this folder only **adds keys** to that map, never a second record or a second JSON writer.

| Key | Owner (writer) | Content |
|---|---|---|
| `uploadSessionEnc` | 06.2.3-T2 | encrypted session URI (D9) — never logged |
| `vetoNoticeEmailId` | 06.2.2-T2 | delivery-status lookup |
| `lastGoogleReason` / `heldByIncidentId` | 06.2.3-T2/T3 (also written by 06.2.5-T1's poller/go-live job on an incident hold, YR17) | failure detail, incident hold |
| **`requestedPrivacy`** | **06.2.1-T1 at create only; permanently read-only elsewhere** | the owner's true visibility choice; 10.4.3-T2/consent dialogue render `preAudit ? requestedPrivacy : privacy` |
| **`preAudit`** (top-level) | **06.2.1-T1 at create only; permanently read-only elsewhere** | D12's durable pre-audit marker — `true` ⇒ the row's `Privacy` (below) was forced to `"private"` at create because the audit was not approved yet, and stays that way for that slot no matter what `AuditStatus` does afterwards (`02.8.3-T3:156`); **absent (not `false`) on an approved row** — every reader that keys off it must use `IS DISTINCT FROM 'true'`, never `= 'false'` |
| **`upload`** | **06.2.4-T1**, computed once at consent/`PATCH` time | `{ privacyStatus, publishAt: DateTimeOffset?, preAudit }` — `privacyStatus`/`publishAt` are the **exact request values** `PublishYouTubeJob` sends to `videos.insert`, read verbatim (O10); `upload.preAudit` is a plain copy of the top-level `preAudit` (never a second source of truth); no `queueAt` (that instant is 06.2.2-T1's `PublicationQueueTime.For`, O9); `privacyStatus` computation is invariant 1 below |
| **`processing`** | **06.2.3-T2 seeds it** (`startedAt`, `deadlineAt`, `polls = 0`, at `MarkProcessing(videoId)`); **06.2.5-T1 only increments `polls`** and advances `next_attempt_at` on re-claim — never re-derives `deadlineAt` | `{ startedAt, deadlineAt, polls }` |
| **`goLive`** | **06.2.5-T1** | `{ attempts, lastCheckedAt, scheduledAt }` |

`Publication.Privacy` (the row column, not a payload key) is the **final/intended** visibility: submitted value if the audit was approved at create, else forced `"private"` — stamped once by 06.2.1-T1 and never rewritten by any file in this folder (invariant 2). It is **not** always what gets sent to `videos.insert`: G3 requires `privacyStatus = private` whenever a `publishAt` is included in the request (scheduling); `upload.privacyStatus` carries the actual per-request value, computed fresh each time `Plan()` runs from the row's immutable `Privacy`/`PreAudit`/`PublishAt` (invariant 1).

**No correction of the row, by design (README D12 + `02.8.3-T3:156`, TL-confirmed).** A publication planned while pre-audit stays `Privacy = "private"` for that slot **forever**, even if `AuditStatus` flips to `approved` before or after upload: `02.8.3-T3:156` is explicit — "the already-created private publication stays private for that slot … the next run is live". `Privacy`/`requestedPrivacy`/top-level `preAudit` are never written by this task; only `upload` (a per-plan, re-derivable computation, never authoritative for the row) is.

**Invariants (all enforced in the domain, tested in `NightTales.Domain.UnitTests`).**

1. **`upload.privacyStatus`/`publishAt` computation (G3 fixed):** a `publishAt` is included in the request **only** when `Publication.Privacy == "public"` **and** `Payload.PreAudit` **is not `true`** (the key is absent — `NULL`, not `false` — on every approved row; a check must use "is not true", never "== false") — in that one case `privacyStatus = "private"` + `publishAt = Publication.PublishAt` (G3: scheduling requires `private`; YouTube itself promotes the video to `public` at that instant). In every other case the request omits `publishAt` and sends `privacyStatus = Publication.Privacy` as-is: this covers pre-audit (`private`, forced), an owner-intended `private` row that happens to carry a future `publish_at` (uploading with that `publishAt` would let YouTube publish a video the owner marked private — never do this), and the immediate/catch-up case (which sends the row's real `Privacy`, e.g. `public`, unchanged — catch-up never forces `private`). `unlisted` + a future `publish_at` is rejected outright (invariant 4) because neither branch above can express it. This is computed **fresh, every time `Plan()` runs**, from immutable inputs (`Privacy`, `PreAudit`, `PublishAt`) — never stored or corrected independently of those inputs.
2. `Publication.Privacy`, `requestedPrivacy` and the top-level `preAudit` are 06.2.1-T1's create-time stamp and are **permanently read-only** to this task and to `PublishYouTubeJob`: a publication planned while pre-audit uploads `private` with no `publishAt` and stays that way even if `AuditStatus` flips to `approved` before or during upload (`02.8.3-T3:156`, D12) — this task never re-evaluates `AuditStatus` to correct an existing row, only to plan a fresh one (invariant 1 recomputes `upload.privacyStatus`/`publishAt`, but that recomputation always yields the same answer for the same immutable inputs, so it is not a "correction").
3. Catch-up: if `publishAt ≤ now + 1 min` (or is `null`), the request omits `publishAt` (G3 would publish at once anyway or there is nothing to schedule) and no go-live job is scheduled — the poller resolves the publication to `published`. Queue timing itself (when `PublishYouTubeJob` actually runs) is **not** computed here — 06.2.2-T1's `PublicationQueueTime.For`, invoked by the Worker's `PublicationScheduledForUploadHandler` (O9), owns that arithmetic entirely; this task has no `queueAt`/lead-time formula of its own.
4. `unlisted` + a future `publishAt` is rejected (`validation_failed`, field `publishAt`): G3 allows `publishAt` only with `private`, and YouTube publishes at that instant, so a scheduled *unlisted* slot cannot be expressed by one insert ([OPEN] §10 item 2).
5. `awaiting_consent` leaves only with `consent_at` (M1) — `PlanUpload` refuses otherwise (`publication_consent_required`, 06 README C6).
6. `ApplyUploadPlan` is CAS on the current status (`WHERE id = @id AND status = @expected`); a replay from the at-least-once outbox/job is a no-op returning `Success` without side effects (O1).
7. Terminal states (`published`, `failed`, `cancelled`) are absorbing; a late poll result for a `cancelled` publication is dropped (06.2.6-T1 may cancel between the poll and the write) and counted in a metric.
8. `publish_at`, `next_attempt_at` are `timestamptz` (UTC instants); no local-time arithmetic in this folder — the only PT-day maths lives in 06.3.1-T1 (D10).
9. `platform_payload` mutations always go through `Apply…`/`Mark…` so `updated_at` (and therefore ETag `"pu-<xmin>"`, D7/06 CR-2) moves when the status or plan changes; the 10.4.3-T2 row can never consent to a plan it did not read.

**Queries used (no N+1, no full scans).**

```sql
-- 06.2.5-T1 processing claim. NULL is included because 06.2.3-T2's MarkProcessing CLEARS next_attempt_at (O6:142);
-- the first claim sets it to now + IntervalSeconds, and platform_payload.processing.deadlineAt bounds the loop.
SELECT id, user_id, channel_connection_id, external_post_id, platform_payload
  FROM publications
 WHERE status = 'processing' AND (next_attempt_at IS NULL OR next_attempt_at <= now())
 ORDER BY channel_connection_id, next_attempt_at NULLS FIRST   -- one videos.list call per 50 ids of one connection
 LIMIT 50 FOR UPDATE SKIP LOCKED;

-- 06.2.5-T1 go-live claim. external_post_id IS NOT NULL excludes quota-deferred rows (O8: scheduled + next PT
-- midnight + no video id), which would otherwise be "confirmed" against a NULL video and then failed with a P1 alert.
-- IS DISTINCT FROM 'true' (not = 'false'): 06.2.1-T1 writes no preAudit key at all on an approved row, so the
-- column is NULL there -- '= false' would wrongly exclude every normal, approved slot from going live.
SELECT id, user_id, channel_connection_id, external_post_id, platform_payload
  FROM publications
 WHERE status = 'scheduled' AND next_attempt_at <= now()
   AND external_post_id IS NOT NULL
   AND (platform_payload->>'preAudit') IS DISTINCT FROM 'true'
 ORDER BY channel_connection_id, next_attempt_at
 LIMIT 50 FOR UPDATE SKIP LOCKED;
```

## 4. Use cases

Both tasks are internal (no route, no bearer, no CSRF surface). The only callers are the consent/PATCH path (06.2.2-T1/T3), the outbox handlers, the two jobs and `nts-worker`/`InlineJobScheduler` in tests.

| # | Use case | Input / DTO | Validation | Authorization | Idempotency | Errors |
|---|---|---|---|---|---|---|
| UC1 | `PlanPublicationUpload` (06.2.4-T1) — a pure Domain function `06.2.2-T1`/`06.2.2-T3` call as part of consenting/`PATCH`ing a publication, in the **same transaction** as the `scheduled` status write, before that transaction's ordinary `publication_updated.v1` event fires (D13/YR3); re-checked (never recomputed differently) by `PublishYouTubeJob` before `StartUploadAsync`. No queue-time computation, no scheduler call, no `AuditStatus` re-check — `Privacy`/top-level `preAudit` are 06.2.1-T1's create-time stamp, read-only here (invariant 2) | `PublicationUploadPlan(PrivacyStatus, PublishAt?)` — pure function of `(Publication.Privacy, Payload.PreAudit, Publication.PublishAt)`, no command/DTO of its own (called in-process by the consent handler) | `consent_at` set; `Publication.Privacy ∈ {public, unlisted, private}` (06.2.1-T1's `YouTubeMetadataRules`); `unlisted` + a future `publish_at` is rejected (invariant 4) | owner path (`ISender` under the user's context) **or** allow-listed system job (O4), via whichever consent command calls it | pure function, no side effect of its own; `ApplyUploadPlan`'s CAS (invariant 6) is the only write and absorbs a replay | `publication_consent_required` 409 (from the calling consent command), `validation_failed` 400 (field `publishAt`) |
| UC2 | `RecordProcessingStatus` (06.2.5-T1, poller) | `YouTubeVideoStatus(videoId, uploadStatus, failureReason?, rejectionReason?, privacyStatus?)` per row (06.2.3-T1's type, returned by `GetVideosAsync`) | the snapshot's `videoId` must equal `external_post_id` | system job; per-user `SystemContext` | CAS on `(id, status)`; a repeated `processed` on a `published` row is a no-op | `youtube_upload_failed`, `youtube_rejected` |
| UC3 | `ConfirmLive` (06.2.5-T1, go-live job) | `privacyStatus` from `videos.list` | only `scheduled` rows where `platform_payload.upload.publishAt` was actually sent (i.e. `Privacy == "public"` and not pre-audit, invariant 1), `external_post_id` set and a past `publish_at` (claim SQL, §3) | system job | CAS `scheduled → published`; an already-`published` row is a no-op | `youtube_go_live_unconfirmed` after `GoLive:MaxMinutes` |
| UC4 | Episode status (06.2.5-T1) | `(EpisodeId, PublicationId)` | episode must exist and be owned by the publication's user | same transaction as UC2/UC3 | CAS on the episode status; `MarkPublished` from `scheduled` only | `episode_invalid_state` (09.5.1 codes) — logged, never fails the job |

**Error codes.** `ErrorCodes` is a **closed** `static class` (`Application/Common/Errors/ErrorCodes.cs`) with its HTTP status in `Api/Http/ErrorCatalogue.cs` (O7) — this folder adds **no** new constant and no local error factory. The one UC1 rejection (`unlisted` + future `publishAt`) maps to the existing `ErrorCodes.ValidationFailed` (400, field `publishAt` in `errors`); `youtube_upload_failed` and `youtube_rejected` are D12's names (existing publication `error_code` values); `youtube_go_live_unconfirmed` is now a **ratified** `error_code` value (README ruling YR5) — the earlier [OPEN] item asking for a catalogue row is closed.

**Audit.** D17's 06 action list gains `publishing.publication_updated` (README ruling YR21) — this task's/06.2.5-T1's status transitions (`processing`, `published`, `failed`) SHOULD be recorded with `[Audited]` under that action (category `publishing`, ids/status only, no title/description). Previously this section said "no new audit action"; YR21 supersedes that. The transition trail is otherwise the row (`status`, `error_code`, `updated_at`) plus `publication_updated.v1` (D13).

## 5. API and events

**No new route, no new status code.** The two tasks contribute one outbox type and one SignalR event, both already fixed by D13/F13 and R1.

```jsonc
// outbox_messages.type = "publication_updated.v1"   (user_id = publications.user_id, O1)
{ "publicationId": "…", "episodeId": "…", "userId": "…", "status": "published", "errorCode": null }
//  → Worker handler → IRealtimeNotifier.NotifyAsync(userId,
//        new RealtimeEvent(RealtimeEventName.PublicationUpdated,
//                          new RealtimeIds(PublicationId: publicationId, EpisodeId: episodeId), Data: null), ct)
```

- Emitted **once per transition**: `scheduled` by 06.2.2-T1's consent command / 06.2.2-T3's autopilot consent job (the writers of the consent transition; 06.2.4-T1's plan write rides in that same transaction and emits nothing of its own), and `processing`, `published`, `failed` by 06.2.5-T1 — inside the same transaction as the status write (`pg_notify` is transactional, R1) so a rollback emits nothing.
- `RealtimeIds` is `{ publicationId, episodeId }`; `Data = null` (WT54 forbids data outside `RenderProgress`); the body stays ≤ 8 KB (ids only) and an oversized payload is dropped with a metric instead of truncated ✅ `09.8.1-T1:203`.
- `status` is the 00 §7 publication status; `errorCode` is `null` except for terminal failures, and is one of `youtube_upload_failed`, `youtube_rejected`, `youtube_go_live_unconfirmed` (a ratified `error_code` value, README ruling YR5 — §4's earlier catalogue-row gap is closed); it is never returned over HTTP in P.
- The client never patches state from the event: it refetches `GET /episodes/{id}` ✅ `09.8.1-T1:176`; `GET /episodes/{id}/publications` (06.2.1-T1, D6) is the row-level read.

## 6. UI

No screen is built here. The consumers and the copy keys handed over:

| Consumer | Reads | Notes |
|---|---|---|
| 10.4.3-T2 veto list (`10.4-series-autopilot/plan.md:156-159, 249-252` ✅ C9) | `PublicationDto { status, publishAt, privacy, requestedPrivacy, etag }`; `PATCH` with `If-Match`, 412 → ConflictDialog | this folder keeps `updated_at` (hence the ETag) moving on every status change (invariant 10); render `preAudit ? requestedPrivacy : privacy` so the owner sees the visibility they asked for, not the temporary pre-audit lock (06.2.1-T1's `requestedPrivacy` key, §3) |
| Episode page (10 wave 2) | `PublicationsSummaryDto(int Published, int Scheduled, DateTimeOffset? LastPublishedAt)` — filled by 06.2.1-T1 (D6/F14) | [OPEN] the exact rendering task is not pinned in the 10 README at the time of writing; the DTO contract is 06.2.1-T1's |
| 02.8.3-T3 run machine | `PublicationUpdated` (ids) → refetch | maps `published` + `platform_payload.preAudit` to `published_private_locked` (P1) |

Copy keys to hand over (10's i18n catalogue, vi/en, no copy written here; **[INFERENCE: key names proposed by this plan — 10's catalogue owns the final spelling]**): `publications.status.scheduled`, `…processing`, `…published`, `…failed`, `…cancelled`; `publications.pre_audit_private` ("uploaded privately — the API project is not audited yet"); `publications.go_live_unconfirmed`; `publishing_temporarily_unavailable` (D14, shown while a `platform_incidents` row is open). [OPEN] the pre-audit banner wording is 02.8.3-T3's (`02.8.3-T3:82`) — hand both keys over together.

## 7. Jobs and integrations

**`PollYouTubeProcessingJob`** — `src/NightTales.Application/Features/Publishing/Jobs/PollYouTubeProcessingJob.cs`, `sealed`, `IBackgroundJob`, `[JobPolicy(Queue = JobQueue.Publish, Retries = 3)]` (transport errors only), public method `RunAsync(Guid publicationId, CancellationToken ct)` per row (ids only, O2).

- Registered recurring in the Worker by `src/NightTales.Worker/Jobs/PublishingRecurringRegistrations.cs` as `IRecurringJobRegistration` (`AddOrUpdateRecurring("publishing-poll-processing", cron, …)`) **only while `youtube_publish` is on**, and removed with `RemoveRecurringIfExists` when it goes off (D1, O2).
- Interval `YouTube:Poll:IntervalSeconds` (60). One run claims ≤ 50 rows (SQL in §3 — `next_attempt_at IS NULL OR ≤ now()`, since 06.2.3-T2's `MarkProcessing` clears it, O6), groups them by `channel_connection_id`, and issues one `videos.list` per group with ≤ 50 ids (1 unit per call, G1 — the 10,000-unit bucket; **the quota ledger is not touched**: D10's ledger only counts uploads, and the poller never reserves).
- Access token through 06.2.3-T2's `ITokenService.GetAccessTokenAsync(connectionId)`; the token is never logged.
- On first claim, `platform_payload.processing = { startedAt = now, deadlineAt = now + Poll:MaxMinutes, polls = 1 }`; every non-terminal re-claim sets `next_attempt_at = now + IntervalSeconds` and `polls++`.
- At `deadlineAt` with no terminal status → `MarkProcessingFailed("youtube_upload_failed")` + warning log `youtube processing poll expired {publicationId}` (ids only) + admin alert (same channel as D12's go-live alert).

**`PublicationGoLiveJob`** — `…/Jobs/PublicationGoLiveJob.cs`, `[JobPolicy(Queue = JobQueue.Publish, Retries = 3)]`, `RunAsync(Guid publicationId, CancellationToken ct)`.

- Scheduled by the poller (`IJobScheduler.Schedule<PublicationGoLiveJob>` at `publish_at + GoLive:DelayMinutes` = +5 min) when `MarkProcessed` finds that `platform_payload.upload.publishAt` **was actually sent** (i.e. `Privacy == "public"` and not pre-audit, 06.2.4-T1 invariant 1) and is in the future; **never** scheduled when `upload.publishAt` is null (pre-audit, private, or catch-up — invariant 1, P1).
- Each run: `videos.list(part=status)` for the `external_post_id` (never `null` — the claim in §3 excludes rows without one, O8); `privacyStatus = public` → `MarkLive` + episode `MarkPublished` + `publication_updated.v1`; otherwise `GoLive.attempts++` and `Schedule(now + GoLive:RetryMinutes)` until `attempts × GoLive:RetryMinutes ≥ GoLive:MaxMinutes` (8th retry, 120 min after the first miss — D12/C5 "every 15 min for 2 h") → `failed` `youtube_go_live_unconfirmed`, `error_code` set, warning log + admin alert (D12); the publication row's `privacy` is left as uploaded.
- It is the **only** writer of `scheduled → published` on the go-live path; 02.8.3's fallback at `publish_at + 10 min` is a read-only self-heal check over `PublicationUpdated` (P2) and must never write.
- Open `platform_incidents` for the connection's client ⇒ skip (the incident-clear path re-schedules, D14); `next_attempt_at` stays `null` as D14 requires.

**Failure map (Google signal → our state).** Episode column: 00 §7 has no `scheduled → ready` edge and `02.8.3-T3:83` treats a publication failure as episode-**unchanged**, so a `failed` publication never reverts the episode; a re-publish is 02's decision, out of this folder's scope.

| Signal (G6) | Publication | `error_code` | `next_attempt_at` | Episode | Outbox / realtime | Alert |
|---|---|---|---|---|---|---|
| `uploadStatus = processed`, `upload.publishAt` was sent and is future (`Privacy=="public"`, not pre-audit) | `scheduled` (from `processing`) | null | `publish_at + 5 min` | `MarkScheduled` (no-op if already) | `publication_updated.v1` (`scheduled`) | — |
| `uploadStatus = processed`, `upload.publishAt` is null (pre-audit, private, or catch-up) | `published` | null | null | `MarkPublished` | `publication_updated.v1` (`published`) | — |
| `uploadStatus = failed` (`failureReason`) | `failed` | `youtube_upload_failed` (+ `failureReason` in the log, ids only) | null | **unchanged** (stays `scheduled`) | `publication_updated.v1` (`failed`) | warn |
| `uploadStatus = rejected` (`rejectionReason`) | `failed` | `youtube_rejected` (+ `rejectionReason` logged) | null | **unchanged** | `publication_updated.v1` (`failed`) | warn |
| `uploadStatus = uploaded/deleted` after `deadlineAt` | `failed` | `youtube_upload_failed` | null | **unchanged** | `publication_updated.v1` | warn |
| `privacyStatus = public` at go-live | `published` | null | null | `MarkPublished` | `publication_updated.v1` | — |
| go-live budget exhausted | `failed` | `youtube_go_live_unconfirmed` | null | **unchanged** | `publication_updated.v1` | **P1** |
| 401/`invalid_grant`/5xx during `videos.list` | unchanged | unchanged | `now + 60 s` (bounded by `deadlineAt`) | unchanged | — | — |

Timeouts: the HTTP timeout lives in `GoogleYouTubeClient` (06.2.3-T1); this folder only bounds the poll window (`Poll:MaxMinutes`) and the go-live window (`GoLive:MaxMinutes`). DST: `publish_at` is an absolute instant — no local arithmetic here (contrast 06.3.1-T1's PT day, D10).

## 8. Security and privacy

- **Ids only** everywhere: logs, metrics, outbox payload, SignalR body (O1, R1). No title, description, channel name, video title, token or storage key — the pre-audit metric/log uses `{publicationId}` and `{preAudit}` only (mirrors `02.8.3-T3:91`).
- `platform_payload` is never logged wholesale: it contains the encrypted upload session URI (`UploadSessionEnc`, D9) — log a redacted shape or ids only.
- Jobs are allow-listed system jobs: read `(id, user_id)`, then run under `SystemContext.For(user_id)` with query filters on (O4); a missing `user_id` fails the job. No `IgnoreQueryFilters` in this folder.
- No user-triggered job path: the Api's `IJobScheduler` throws (O2), so a user cannot force a poll, a go-live check or a re-plan of someone else's publication.
- Owner-only data access on UC1's owner path (tenant filter, 404 for another user's publication); UC2–UC4 are system-scoped.
- Pre-audit mode is compliance-relevant, not cosmetic: it is what keeps an unaudited project private (00 §7, DECISIONS §10.12). Any change that could publish an unaudited video is a defect, not a tuning choice — invariant 2 is the guard.
- Flag `youtube_publish` gates the recurring registration only; the routes that reach UC1 are already gated by 06.2.1-T1/D1.
- Secrets: none added (C12 unchanged); the access token comes from 06.2.3-T2's token service.

## 9. Test plan

Projects per D2 (`09-backend-api/README.md:121`, verified): `NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `NightTales.Api.IntegrationTests` (DB/HTTP **and** Worker jobs run in-process), `NightTales.Architecture.Tests`, `NightTales.TestKit` (fakes/fixtures — **no mocking library**). xunit.v3 + Shouldly; integration tests on real PostgreSQL 17 via `PostgresFixture`; time via `FakeClock`; Google via `FakeGoogleServer`; no sleeps (`09-backend-api/README.md:290-300`).

- `FakeGoogleServer` (D19, file owned by 06.2.3-T1) gains the `videos.list` scenario builder this task needs: a per-video-id scripted sequence of `uploadStatus`/`failureReason`/`rejectionReason`/`privacyStatus` (`processing → processed`, `failed`, `rejected`, `processed + private` then `public`). Additive, same file — never a second fake.
- Unit: `PlanUpload_*` (public+future-publishAt sends `private`+`publishAt`, private+future-publishAt omits `publishAt`, unlisted+future-publishAt rejected, pre-audit omits `publishAt`, catch-up/immediate sends the row's own `Privacy` — no lead-time/queue arithmetic, that is 06.2.2-T1's), `MarkProcessed_*`, `Episode.Publishing_*` transitions (including illegal transitions).
- Integration (in-process Worker + `InlineJobScheduler`): poller maps each G6 combination; go-live retry budget; `publication_updated.v1` written once per transition and consumed by the realtime handler; a replayed outbox message is a no-op; episode status in the same transaction (assert with an induced failure that **neither** row changed).
- Architecture: no route added, job signatures ids-only, no `IgnoreQueryFilters`, one fake server.
- Manual, on staging with the real test channel: pre-audit upload (private, no `publishAt`, `psql` shows `platform_payload->>'preAudit' = 'true'`), then a public slot end-to-end after the audit flag flips.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.2.4-T1 | Lead time, `publishAt`, pre-audit mode (omit `publishAt`) | S (0.5) | 06.2.3-T2 | 02.8.3-T3 |
| 06.2.5-T1 | Processing poll + go-live job + `PublicationUpdated` + episode status | M (1.0) | 06.2.3-T2, 09.8.1-T1 | 06.5.1-T1, 02.8.3-T3, 10.7.4-T1 (10 CR-15) |

Folder total **1.5 d raw** — identical to README §8.1/§9 (the `+0.5` for 06.2.5-T1 was already counted there).

**[OPEN] items recorded, not invented.** (Several earlier items are now **resolved** by README §10 rulings YR2/YR3/YR5/YR21, kept below as a record.)

**Resolved by ruling, not open anymore:**
- Pre-audit `publish_at` on the row — **YR2**: the row always keeps `publish_at = run.SlotAt`; only the upload request/plan omits it. Matches this folder's invariant 1/2 without a code change.
- `youtube_go_live_unconfirmed` catalogue row — **YR5**: it is now a ratified `error_code` value.
- Audit action for a status transition — **YR21**: `publishing.publication_updated` is added to D17's list; adopted in §4.
- The `06.2.2-T1` wiring/back-edge question and "queue at consent" — **YR3**: the Api never enqueues; 06.2.2-T1's consent transition just writes the ordinary `publication_updated.v1`, and 06.2.3-T2's Worker-side `PublicationScheduledForUploadHandler` does the actual scheduling via 06.2.2-T1's own `PublicationQueueTime.For`. No forward edit, no port, no back-edge.

**Still open:**

1. How 02 learns `preAudit` at run-outcome time: `publication_updated.v1` carries ids only (D13), so 02 must read the flag from its own run row or from a `preAudit` field added to `PublicationDto` (a D6 change, 06.2.1-T1's folder — peer confirms `PublicationDto` now exposes `PreAudit`/`RequestedPrivacy`, 06 CR-10). Not decided here.
2. `unlisted` + a future `publishAt` cannot be expressed by one insert (G3: `publishAt` is `private`-only and YouTube publishes at that instant). 06.2.4-T1 rejects it (`validation_failed`, field `publishAt`); the alternative — upload `private` without `publishAt`, then flip `privacyStatus` at `publish_at` with `videos.update` — is extra scope (write quota, a new job branch) the TL must accept or drop.
3. Single writer for the go-live transition: 02.8.3 evaluates its `scheduled → published` fallback at `publish_at + 10 min` off `PublicationUpdated` (`02.8.3 plan.md:442`); `PublicationGoLiveJob` owns the write and 02 must stay read-only — recorded as a coordination note for 02.8.3-T3/T4.
4. New metric/alert names (`youtube_go_live_unconfirmed_total`, the processing-expiry warning) are [INFERENCE]: 09.9.3 owns the metric catalogue and 11.6.2-T1 the alert rules (`11.6-operations/plan.md:40`); D12's "admin alert" reuses the P1 channel of D14.
5. `YouTubeOptions`'s `Poll:*`/`GoLive:*` members are declared by 06.1.2-T1 but have **no validator range checks** anywhere in that task's file or tests — **CR to `06.1.2-T1`**: add `Poll:IntervalSeconds`, `Poll:MaxMinutes`, `GoLive:DelayMinutes`, `GoLive:RetryMinutes`, `GoLive:MaxMinutes` ranges to its `YouTubeOptionsValidator` (consumed by 06.2.5-T1's jobs; O5).

## 11. Rollout

1. Order: 06.2.3-T2 (upload job) → **06.2.4-T1** → **06.2.5-T1** → 02.8.3-T3 (`06-youtube-posting/README.md` §5).
2. No migration, no flag change. No config key added by this folder: `06.2.4-T1` reads no `YouTubeOptions` member at all (O5); `06.2.5-T1`'s jobs read the existing `YouTube:Poll:*`/`YouTube:GoLive:*` keys (06.1.2-T1's, pending the validator-range CR of §10 item 5).
3. First run on staging (Testing project, 7-day refresh tokens, never real content): pre-audit path first — the only state available until the audit passes — then, once `YouTube:AuditStatus = approved`, one public slot end-to-end. Gate **06-publish** in `../README.md` §7.
4. Monitoring: 09.9.3's Serilog/OTel (ids only), `youtube_quota_used` untouched by this folder, plus the go-live/processing warnings surfaced through 11.6.2-T1's rules; `outbox_lag_seconds` already covers the event path (P3).
5. Rollback: switch `youtube_publish` off → the Worker unregisters the recurring poller (`RemoveRecurringIfExists`) and no new job is scheduled; in-flight rows stay `processing`/`scheduled` with their `next_attempt_at` (no data corruption), and on re-enable the next poll tick and the go-live re-schedule resume them (`platform_payload` state persists, nothing lives in memory). A stuck `scheduled` row's **upload enqueue** is re-issued by 06.2.3-T2's `PublicationScheduledForUploadHandler` the next time its `publication_updated.v1` is replayed or by an operator re-emitting that event — no *upload* enqueue call originates in this folder (YR3); `06.2.5-T1`'s own recurring poller and `PublicationGoLiveJob`/`IJobScheduler.Schedule` calls are unaffected and continue as described in §7 — [INFERENCE: the exact operator command is 11.6.3's runbook surface].
6. Evidence for 06.5.1-T1: the `publications` rows with their `error_code`/`platform_payload`, the `publication_updated.v1` outbox trail, and the `v1` pre-audit run outcome `published_private_locked` from 02.8.3-T3.
