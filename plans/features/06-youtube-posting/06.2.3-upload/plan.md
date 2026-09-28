# 06.2.3 — Upload: `IYouTubeClient`, `PublishYouTubeJob`, OAuth error classifier and platform incidents (detailed plan)

> Contract: `../README.md` §1 facts (G7, G8, C3, C6, C11, C12), §2 findings F02/F03/F05/F07/F21, §3 decisions **D9** (upload), **D10** (quota ledger), **D13** (events), **D14** (platform incidents), **D18** (performance), **D19** (testing), **D20** (migrations), §8.1 canonical dependencies.
> High-level: `../../06-youtube-posting.md` §4 06.2.3 (six Given/When/Then bullets).
> Template: `../../00-overview.md` §10.1. Legend: ✅ verified (`path:line`) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-25. Nothing implemented yet.

## 1. Scope

**In scope (P · M1, 3 tasks, 3.25 d).** The Application port `IYouTubeClient` and its real implementation `GoogleYouTubeClient` on `Google.Apis.YouTube.v3` 1.76.0.4262 + `Google.Apis.Auth` 1.76.0 (G8); the resumable-upload surface of `FakeGoogleServer`; `PublishYouTubeJob` with token refresh, quota reservation/release, streaming from object storage, the D9 error map, and the encrypted persisted session URI; `OAuthErrorClassifier`, the `platform_incidents` table, the `nts-worker incident clear <id> --operator <name> --note "<text>"` verb (YR16) and the P1 alert.

**Out of scope (owned elsewhere).** `IQuotaLedger` implementation and the ETA route (06.3.1-T1, consumed here); `AutopilotConsentJob` and the consent write paths that enqueue the upload (06.2.2-T1/T3); lead time / `publishAt` / pre-audit `AuditStatus` handling at schedule time (06.2.4-T1); `PollYouTubeProcessingJob` / `PublicationGoLiveJob` / `Episode.MarkPublished` (06.2.5-T1); disconnect, `TokenHealthJob`, the daily data refresh and the deletion handler (06.4.1-T1, 06.4.2-T1/T2, 06.4.3-T1); the `Publication` aggregate itself, `YouTubeMetadataRules` and `_06_Publications` (06.2.1-T1); the veto e-mails and `user_action_tokens` (06.2.2-T2); email templates and `SendEmailJob` (09.2.6-T3).

**Explicitly not built in P.** No download/delete of videos on the user's channel (Stories in Studio own deletion); no chunk-level parallel upload (one stream, one chunk buffer — D18); no upload progress over SignalR (`RealtimeData` is reserved for `RenderProgress`, WT54); no retry of `youtube_rejected` (a rejected video is terminal for that publication).

**Cross-cutting rules.** Every route stays behind `RequireFeature("youtube_publish")` and every recurring registration only exists while the flag is on (D1) — the upload job is not recurring; it is enqueued per publication, so the flag is checked at enqueue (06.2.2-T1) **and** re-checked inside the job before the first Google call. No second e-mail, flag, audit or token mechanism is introduced: the upload path reuses `IAuditWriter`, `IEmailSender` (only for the 06.2.2 templates, not here), `IFeatureFlags`, `ITokenProtector`, `IClock`.

## 2. Confirmed facts

| Id | Fact | Evidence |
|---|---|---|
| G7 | Resumable upload: session URI lives one week; chunks in multiples of 256 KB except the last; probe = empty `PUT` with `Content-Range: bytes */<total>` → `308` + `Range`; `404` = expired session, restart | `../README.md:22` ✅ |
| G8 | `Google.Apis.YouTube.v3` 1.76.0.4262 + `Google.Apis.Auth` 1.76.0, Apache-2.0, net6.0+; `ResumableUpload.ResumeAsync(Uri)` and `CreateFromUploadUri(Uri, Stream, options)` exist; `MinimumChunkSize = 256 KB`, default 10 MB | `../README.md:23`; google-api-dotnet-client `ResumableUpload.cs:111, 506, 524` ✅ |
| C3 | 02 expects: `quotaExceeded` → `scheduled` + `next_attempt_at`; 401/`invalid_grant` → `channel_reauth_required`; `invalid_client`/`unauthorized_client` → platform incident, connection unchanged | `../README.md:30` (02.8.3-T4:15, 28-31) ✅ |
| C6 | 09 surfaces to reuse: `IEmailSender.QueueAsync`, `ITokenProtector.Protect(purposeSuffix, plaintext)` purpose `tokens.v1/{connectionId}`, `IBlobStore.OpenReadAsync`, `[Audited]`, error codes `youtube_quota_exhausted`, `youtube_upload_failed`, `youtube_rejected` | `../README.md:33`, `features/09-backend-api/README.md` BD1/BD3/BD9–BD14 ✅ |
| C11 | `publications(… status, title, description, tags, privacy, publish_at, next_attempt_at, consent_at, consent_text_version, consent_source, autopilot_authorization_id, veto_notice_sent_at, contains_synthetic_media, external_post_id, platform_payload)`; `channel_connections(… access_token_enc, refresh_token_enc, token_expires_at, status active/revoked/expired/error, oauth_client_id, last_verified_at, revoked_at)` | `../../00-overview.md:289-290` ✅ |
| C12 | Secrets `nts/<env>/api/YOUTUBE_CLIENT_SECRET` (API role); P settings are environment values (09.10 is S) | `../README.md:37` (11 README D15, 11.2.2-T1:48) ✅ |
| F1 | `publications.error_code` exists (`text NULL`) and `platform_payload` is `jsonb` — the error map writes both | `../../00-overview.md:290` ✅ |
| F2 | Publication machine `awaiting_consent → scheduled → uploading → processing → published \| failed \| cancelled`; it cannot leave `awaiting_consent` without `consent_at`; Episode `ready → scheduled → published` **via publications** | `../../00-overview.md:336`, `features/02-story-writing/02.8.3-cadence-publishing/plan.md:40-41` ✅ |
| F3 | Job convention: `Application/Abstractions/Jobs/` holds `IBackgroundJob` (sealed classes, resolved from DI), `enum JobQueue { Default, Render, Publish, Email }`, `[JobPolicy(Queue = …, Retries = …)]`, `IJobScheduler`, `IRecurringJobRegistration`; the queue is the lowercase enum name; the global 10-attempt `AutomaticRetryAttribute` is removed, so the default is **0**; a start-time check resolves every `IBackgroundJob` in Application and fails the Worker boot if one is missing | `features/09-backend-api/09.2.6-worker-hangfire/09.2.6-T1-worker-host-job-scheduler.md:19-43, 84` ✅ |
| F4 | Job classes live in Application (`Application/Features/Email/Jobs/SendEmailJob.cs` for `SendEmailJob`), public methods take **ids only** (`Jobs_TakeIdsOnly` architecture rule) | `09.2.6-T3-email-pipeline.md:102-104`; `09.1.2-T1-architecture-rules.md:38` ✅ |
| F5 | Blob read for the render's MP4: `Task<ErrorOr<BlobObject>> IBlobStore.OpenReadAsync(string bucket, string key, CancellationToken ct)`, `BlobObject(Stream Content, string ContentType, long? Length)`; bucket `nt-media-<env>`; key `media/{userId}/{episodeId}/{renderJobId}/video.mp4` (the caller owns the stream) | `features/09-backend-api/09.7-video-review-storage/09.7.2-T1-blob-store-presign.md:32-34, 96` ✅ |
| F6 | Outbox event names match `^[a-z_]+\.v[0-9]+$`; the dispatcher is a `BackgroundService`; handlers are `IOutboxHandler<T>` | `09.2.4-T1-outbox-interceptor-and-dispatcher.md:79`, `09.1.2-T1-architecture-rules.md:37` ✅ |
| F7 | 02.8.3-T4 consumes the incident path as the outbox events `PlatformIncidentRaised`/`PlatformIncidentCleared` matched to the authorization's `channel_connection_id`, and maps `PublicationUpdated` with `status = failed` + `error_code` onto the pause reasons `youtube_quota_exhausted`/`upload_failed`/`video_rejected`; `ChannelConnectionChanged` drives `channel_not_active`. Auto-resume does not exist: the owner still presses Resume | `features/02-story-writing/02.8.3-cadence-publishing/02.8.3-T4-pause-triggers-spend-caps-alerts.md:26-48, 189-190` ✅ |
| F8 | Metrics/alert route: `youtube_quota_used` is an existing metric and rule 10 is "YouTube quota > 80 % → warn"; the rule set is 15 rules, each asserted at load, and one drill is provoked end to end; thresholds are owned by 08 §5.2 | `features/11-worker-deployment/11.6-operations/11.6.2-T1-telemetry-dashboards-and-alerts.md:15, 30, 43, 66, 75` ✅ |
| F9 | The Worker CLI convention is `nts-worker <verb>` (the Worker binary with arguments, no web listener), writing `audit_logs` with actor `system:operator` | `features/11-worker-deployment/README.md:242` ✅ |
| F10 | Client consumers: 10.4.3-T2 owns the veto list + `PublicationRepository`; 10.5.1-T1 owns `EpisodeDetailDto` with the optional `publications` block; `PublicationStatus` is exactly `awaiting_consent \| scheduled \| uploading \| processing \| published \| failed \| cancelled` | `features/10-web-ui/10.4-series-autopilot/10.4.3-T2-autopilot-run-log-calibration-veto.md:20, 28-29, 154`; `10.5.1-episodes-editor/plan.md:29` ✅ |
| F11 | **Seam ownership.** `IYouTubeClient`, `GoogleYouTubeClient`, `YouTubeOptions` + `YouTubeOptionsValidator` and the `AddYouTubePublishing(configuration)` registration are **declared by 06.1.2-T1** (`06.1-connect/06.1.2-T1-connect-callback.md:54-73, 186-192`); its options class already carries `ApiBaseUrl`, `TokenEndpoint`, `RevokeEndpoint`, `Http:TimeoutSeconds`, `Pkce`, `ActiveClientId`, `Clients`, `PublishLeadMinutes`, `MinVetoWindowHours`, `AuditStatus`, `ConsentTextVersion`, `Quota`, `Poll`, `GoLive`. This folder **adds** the four upload members, `UploadBaseUrl` + `Upload:*` and the upload tests to those same files — never a second interface, class, options binding or registration extension (CR-16) | `06.1-connect/06.1.2-T1-connect-callback.md:54-73, 186-192` ✅; `../README.md` D9/D11 |
| F12 | **One refresh path, ownership confirmed.** `ITokenService.GetAccessTokenAsync(connectionId)` (refresh < 5 min, under `FOR UPDATE`, re-read after the lock, D9:120) is **declared and shipped by 06.1.2-T2** — "the token columns through `ITokenService.GetAccessTokenAsync(connectionId)`" and "the purpose string is the contract" (`06.1.2-T2-channel-connection.md:110, 183`); `06.1-connect/plan.md:19` lists it as "one refresh path (`ITokenService`, F21/D9)". 06.4.2's plan §10.1 still records the declaring task as an [OPEN] item — stale, closeable against this evidence. This folder **consumes** it and declares no parallel token port | `06.1-connect/06.1.2-T2-channel-connection.md:110, 183`; `06.1-connect/plan.md:19` ✅ |
| F13 | **Upload plan is sticky (shape finalized by Y06Status, 2026-09-25).** 06.2.4-T1 supplies `IPublicationUploadPlanner.Plan(publication, now) → PublicationUploadPlan(PreAudit, Privacy, PublishAt?, VetoEndsAt)` — **`QueueAt` was dropped from this record** (06.2.4-T1 has zero scheduler dependency under YR3) — and persists it in `platform_payload.upload`; the job builds `VideoInsertRequest` from that payload **verbatim**, never from live `YouTubeOptions.AuditStatus` (pre-audit ⇒ `privacyStatus = private`, no `publishAt`). **G3 fix (Y06Status):** `platform_payload.upload.publishAt` is included only when `Privacy == "public"` and not pre-audit — a `Privacy == "private"` row with a future `publish_at` omits `publishAt` entirely, so this job never risks auto-publishing a video the owner marked private; the job still reads `upload.publishAt` verbatim either way | `agent://Y06Status` (06.2.4-T1 writer), 2026-09-25; 02.8.3-T3:156 ✅ |
| F14 | **Upload length and aggregate ownership.** The MP4 total is `assets.size_bytes` (`../../00-overview.md:288`) and `IBlobStore.OpenReadAsync` returns a **nullable** `BlobObject.Length` (09.7.2-T1:32-34), so the blob is a cross-check only and the stream is never drained to measure. `Publication.StartUpload(DateTimeOffset)` is already declared by 06.2.1-T1 (`06.2.1-T1-publication-aggregate.md:58`), which persists through `AppDbContext`/`DbSet<Publication>` (`:202-203`) — this folder declares no aggregate member it owns and no repository port (CR-18, CR-20) | `../../00-overview.md:288`; `features/09-backend-api/09.7-video-review-storage/09.7.2-T1-blob-store-presign.md:32-34`; `06.2.1-publication-consent/06.2.1-T1-publication-aggregate.md:58, 202-203` ✅ |
| D9 | Upload contract: the whole job sequence, the port member list, the error map, "the stream is never buffered whole; one chunk buffer per job; parallelism of queue `publish` = 2" | `../README.md:117-122` [DECISION] |
| D10 | `IQuotaLedger.TryReserveAsync(platform, now) → ErrorOr<QuotaReservation(DateOnly Day)>` (atomic insert + `UPDATE … WHERE units_used < units_limit RETURNING`), `ReleaseAsync(reservation)` only when YouTube never received the insert; PT day via `TimeZoneInfo ("America/Los_Angeles")`; `YouTube:Quota:UnitsPerDay` = 100 | `../README.md:123` [DECISION] |
| D13 | Outbox `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }`, `channel_connection_changed.v1 { connectionId, userId, status }`, `platform_incident_raised.v1 { incidentId, platform, oauthClientId, affectedConnections }`, `platform_incident_cleared.v1 { incidentId, platform, oauthClientId }`; SignalR `PublicationUpdated` with ids only, no `RealtimeData` | `../README.md:135` [DECISION] |
| D14 | `platform_incidents(id, platform, oauth_client_id, error_code, raised_at, cleared_at, cleared_by, note)`, partial unique `(platform, oauth_client_id) WHERE cleared_at IS NULL`; while open the client's publications stay `scheduled` with `next_attempt_at = null`, `TokenHealthJob` skips the client, the UI shows `publishing_temporarily_unavailable`; cleared by `nts-worker incident clear <id> --note "<text>"` (audit `support`, `internal`) which re-schedules held publications with jitter; P1 alert through 11.6.2-T1 | `../README.md:137` [DECISION] |
| D18 | Streamed uploads (one chunk buffer), jobs batch ≤ 50 rows with `SKIP LOCKED`, indexes `publications (status, next_attempt_at) WHERE status IN ('awaiting_consent','scheduled','uploading','processing')` and `publications (channel_connection_id)`, `videos.list` batched by 50 | `../README.md:144` [DECISION] |
| D19 | `FakeGoogleServer` in `NightTales.TestKit` (Kestrel, hand-written, no mocking library) must expose the token endpoint, revoke, `channels.list`, resumable upload (session create, chunk `308` + `Range`, probe, `404` expiry, **drop-after-last-chunk**), `videos.insert` errors (`quotaExceeded`, `invalidPublishAt`) and `videos.list` transitions; `GoogleYouTubeClient` takes base URLs from options so tests point at the fake; integration tests on real PostgreSQL 17 via `PostgresFixture`; PT-day tests across DST with `FakeClock` | `../README.md:145` [DECISION] |
| D20 | `_06_PlatformIncidents` is one expand-only migration | `../README.md:146` [DECISION] |

## 3. Domain and data

### 3.1 Aggregates and state transitions

`Publication` (owned by 06.2.1-T1, D6) gains the upload transitions this folder drives; they are added to the same file family, never as a parallel aggregate:

| Member | From → to | Preconditions / effects |
|---|---|---|
| `Publication.StartUpload(now)` **(06.2.1-T1:58 — already declared, relied upon, not redeclared; YR18: idempotent for `uploading`)** | `scheduled` → `uploading`, or `uploading` → `uploading` (no-op, "resume") | refuses when `consent_at IS NULL` (NFR-06-1 defence in depth, D9), when `channel_connection_id` is stale, or when `status` is anything other than `scheduled`/`uploading`; called again on an already-`uploading` row (the F02 lost-response retry) succeeds as a no-op instead of refusing, so the job proceeds to the session/probe/resume step (§4 UC1 step 4) rather than failing the retry; on the `scheduled → uploading` transition it sets `platform_payload.uploadStartedAt` and increments `platform_payload.uploadAttempt` (unchanged on the idempotent `uploading → uploading` path) |
| `Publication.AttachUploadSession(string protectedSessionUri)` | `uploading` (no status change) | replaces `platform_payload.uploadSessionEnc`; only ever written before the first byte leaves the process |
| `Publication.MarkProcessing(string videoId)` | `uploading` → `processing` | sets `external_post_id = videoId`, clears `next_attempt_at`, sets `error_code = null` |
| `Publication.MarkUploadedExternal(string videoId)` | `uploading`/`processing` → `processing` | the reconcile path when `external_post_id` was already set (a lost response); no second upload |
| `Publication.RescheduleUpload(string errorCode, DateTimeOffset nextAttemptAt, string? googleReason)` | `uploading` → `scheduled` | sets `error_code`, `next_attempt_at`, `platform_payload.lastGoogleReason`; keeps the session URI when the session is still usable. The outbox payload is unchanged (D13); `PublicationScheduledForUploadHandler` (UC4) re-reads `next_attempt_at` off this row when it sees the `scheduled` transition and schedules `PublishYouTubeJob` there — without it the row would sit `scheduled` forever with nothing to wake it (TL-flagged 2026-09-25, corrected with `agent://Y06Publication`) |
| `Publication.FailUpload(string errorCode, string? googleReason)` | `uploading` → `failed` | terminal for this publication; `error_code` ∈ {`channel_reauth_required`, `youtube_upload_failed`, `youtube_rejected`} |
| `Publication.HoldForIncident(Guid incidentId, DateTimeOffset nextAttemptAt)` | `uploading` → `scheduled` | `error_code = PublicationErrorCodes.PlatformIncident`, `next_attempt_at = nextAttemptAt` (YR24: **a deferred instant, never null** — the caller passes `now + 5 × Poll:IntervalSeconds`, reusing YR17's key), `platform_payload.heldByIncidentId = incidentId`. No quota touch here. |
| `Publication.ReleaseHold(DateTimeOffset nextAttemptAt, DateTimeOffset now)` (YR27-4) | `scheduled`/`processing` (no status change) | clears `error_code` and `platform_payload.heldByIncidentId`, sets `next_attempt_at = nextAttemptAt` (jittered for `scheduled`, `now` for `processing` — YR17); raises `PublicationUpdated { …, Status = <current> }` (D13 unchanged shape) so the outbox interceptor writes the row — 06.2.3-T3's clear command calls this on tracked entities instead of a raw `UPDATE`, so the event actually fires (a set-based SQL statement raises no domain event, F6) |

Invariants preserved: at most one active publication per `(episode_id, platform)`; `contains_synthetic_media = true` always (D6); a publication in `failed` never returns to the queue without an explicit user re-publish (a new row); `external_post_id` once set is never cleared here (06.4.2-T1 clears it when the video is gone).

### 3.2 Columns and payload keys this folder owns

- `publications.status`, `next_attempt_at`, `error_code`, `external_post_id`, `platform_payload` (C11, F1).
- `platform_payload` keys written here: `uploadSessionEnc` (Data-Protection ciphertext of the session URI), `uploadStartedAt`, `uploadAttempt` (int), `lastGoogleReason` (the raw Google `reason` string, e.g. `quotaExceeded`, `invalidPublishAt`), `quotaReservedDay` (the PT `DateOnly` of an outstanding reservation; written at reserve, cleared on terminal success and on release — 06.2.6-T1's cancel path releases only when the key is present), `heldByIncidentId` (uuid, while held), `processing { startedAt, deadlineAt, polls }` (written with `MarkProcessing` for 06.2.5-T1's poller). **Not here:** `upload`/`preAudit` (06.2.4-T1's sticky upload plan, read verbatim), `vetoNoticeEmailId` (06.2.2-T2).
- Never in `platform_payload`: access/refresh tokens (they live in `channel_connections`), the title/description, or the plaintext session URI.

### 3.3 New table (expand-only migration)

```sql
-- src/NightTales.Infrastructure/Persistence/Migrations/<ts>_06_PlatformIncidents.cs   (D20; expand-only)
CREATE TABLE platform_incidents (
    id              uuid        NOT NULL PRIMARY KEY,
    platform        text        NOT NULL,
    oauth_client_id text        NOT NULL,
    error_code      text        NOT NULL,
    raised_at       timestamptz NOT NULL,
    cleared_at      timestamptz NULL,
    cleared_by      text        NULL,
    note            text        NULL
);
CREATE UNIQUE INDEX ux_platform_incidents_open
    ON platform_incidents (platform, oauth_client_id) WHERE cleared_at IS NULL;
CREATE INDEX ix_platform_incidents_raised_at ON platform_incidents (raised_at DESC);
```

Entity `src/NightTales.Domain/Publishing/PlatformIncident.cs` (plain entity, no `IUserOwned` — an incident is platform-wide, not tenant data), configuration `src/NightTales.Infrastructure/Persistence/Configurations/PlatformIncidentConfiguration.cs` (partial unique index via `.HasFilter("cleared_at IS NULL")`). Columns are exactly D14's list; no extra column, no soft-delete column.

**Ownership note (CR-22):** D20 assigns `_06_PlatformIncidents` to 06.2.3-T3, but 06.2.3-T2 must read the table (D9 "no open incident for the client") and T3 *depends on* T2, so a table created in T3 would not exist when T2's integration tests run. The table, the entity, the port and the migration move to **06.2.3-T2**; T3 keeps `OAuthErrorClassifier`, the clear-path sweep, the CLI verb and the P1 alert (the store implementation, the entity and the raise path ship in T2). Recorded as a CR request to `../README.md` §3 D20 / §4 (renumbered CR-22, not CR-9: `06.2.1-T1`/`06.2.2-T1` already use "06 CR-9" for the unrelated `queueAt` outbox fix, `06.2.2-T1-consent-command.md:55` — this folder's own CR list is local until the TL resequences it at README merge time).

### 3.4 Indexes (D18) used by this folder's queries

- `publications (status, next_attempt_at) WHERE status IN ('awaiting_consent','scheduled','uploading','processing')` — the job's own read is by primary key (one row, `FOR UPDATE`); the clear command's per-incident reschedule (06.2.3-T3, YR24) is the only sweep and uses this index.
- `publications (channel_connection_id)` — the clear command's reschedule selects the held publications of an incident's `oauth_client_id` by this index (YR24: holds are lazy and per row; there is no raise-time sweep).
- `channel_connections (user_id)` — token refresh reads one connection by id (`FOR UPDATE`); the index serves the owner-scoped join the job performs for `userId`.
- `ux_platform_incidents_open` (§3.3) — makes raising idempotent (`ON CONFLICT DO NOTHING`) and the `HasOpen` read a single index probe.

### 3.5 What this folder does **not** change

No column is added to `publications` or `channel_connections`; `publications.error_code`/`platform_payload` already exist (F1). No change to `assets` or `render_jobs`: the MP4 key is read from the episode's `final_video` asset row (`assets.storage_key`, `assets.kind = 'video'`), which 06.2.1-T1's publication create already resolves.

## 4. Use cases

### UC1 — `PublishYouTubeCommand` (Worker-invoked, the only upload path)

```csharp
// src/NightTales.Application/Features/Publishing/Upload/PublishYouTubeCommand.cs
public sealed record PublishYouTubeCommand(Guid PublicationId) : IRequest<ErrorOr<UploadOutcome>>;

public enum UploadOutcome { Processing, Rescheduled, HeldByIncident, ReauthRequired, Failed, Reconciled }

public sealed class PublishYouTubeHandler(
    IAppDbContext db, ITokenService tokens, IYouTubeClient youtube, IBlobStore blobs,
    IQuotaLedger quota, IPlatformIncidentStore incidents, IFeatureFlags flags, IClock clock,
    IUnitOfWork uow, ILogger<PublishYouTubeHandler> log)   // application-layer persistence (CR-20) + 06.1.2-T2's ITokenService (F12)
{
    public Task<ErrorOr<UploadOutcome>> Handle(PublishYouTubeCommand cmd, CancellationToken ct);
}
```

Sequence (D9), each step a distinct early exit:

1. **Feature flag.** `flags.IsEnabledAsync("youtube_publish", ct)` false → `Error.NotFound(ErrorCodes.FeatureDisabled)` (the job returns without touching the row; the enqueue side already hides the surface).
2. **Lock the row.** `ISystemWorkQuery` resolves `(id, user_id)`, then `db.Publications` (`IAppDbContext`) reads it with `SELECT … FOR UPDATE` (no `SKIP LOCKED`: one id per job); 06.2.1-T1's persistence seam, not a new repository port (CR-20). Missing → `Error.NotFound(ErrorCodes.PublicationNotFound)`; the job logs `publication_missing` and returns.
3. **Reconcile first, then the early-run guard.** `external_post_id IS NOT NULL` → `youtube.GetVideosAsync(token, [id])`; `uploadStatus = uploaded|processed` → `Publication.MarkUploadedExternal` + outbox `publication_updated.v1` + `UploadOutcome.Reconciled`; `failed|rejected` → the terminal mapping of §7. **No second upload is ever started on this path** (F02). **YR27-1:** otherwise, `next_attempt_at IS NOT NULL AND next_attempt_at > clock.UtcNow` → return `UploadOutcome.Rescheduled` as a no-op **without touching quota or any other state** — this guards a duplicate or early outbox redelivery (the same publication scheduled twice) from running the upload before its own deferred instant.
4. **Consent + connection.** `status` must be `scheduled` **or** `uploading` (YR18: `StartUpload` is idempotent on an already-`uploading` row — the F02 lost-response retry reaches this step again and must proceed to probe/resume, not be refused) and `consent_at` non-null (06.2.1-T1's `Publication.StartUpload` refuses from any other status — NFR-06-1; the refusal surfaces as `publication_consent_required`); the connection must be `active`; connection status `expired`/`revoked` → `FailUpload("channel_reauth_required")` + outbox.
5. **Incident gate.** `var openIncidentId = await incidents.HasOpenIncidentAsync("youtube", connection.OAuthClientId, ct)` (`Guid?`, null = no open incident); non-null → `Publication.HoldForIncident(openIncidentId.Value, clock.UtcNow + TimeSpan.FromSeconds(5 * options.Value.Poll.IntervalSeconds))` (YR24: `next_attempt_at` is a **deferred** instant, reusing YR17's `5 × Poll:IntervalSeconds` key — never `null`), no Google call, no quota consumed, and the handler returns `HeldByIncident` (this is the D14 hold: a broken OAuth client must not burn quota or fail user publications). The deferred `next_attempt_at` is what makes the hold *lazy*: `PublicationScheduledForUploadHandler` (UC4) schedules the next check at that instant with no special-case guard needed, since the value is never null — if the incident is still open when that run happens, this step holds again with a fresh deferred instant; 06.2.3-T3's clear command is the only path that wakes the row immediately.
6. **Reserve quota.** **YR27-2:** if `platform_payload.quotaReservedDay` is already set (a resume of an existing session from a prior run), **skip the reserve entirely** — the unit was already consumed for this session-create; go straight to **step 7** (the token is still needed before building the request at step 8). First `userQuota.TryReserveAsync(publication.UserId, clock.UtcNow, ct)` (**ST1: the per-user slot is taken before the global unit**); `user_upload_limit_reached` → `RescheduleUpload(PublicationErrorCodes.UserUploadLimitReached, NextPtMidnight(clock) + jitter(≤ 10 min))` and **no** global reserve in this run; on success set `reservedUserThisRun = true`, then Otherwise `quota.TryReserveAsync("youtube", clock.UtcNow, ct)`, and on success set the local flag `reservedThisRun = true`; (06 CR-13) error `youtube_quota_exhausted` → `RescheduleUpload("youtube_quota_exhausted", next PT midnight + jitter ≤ 10 min)`; the row's `next_attempt_at` update is what `PublicationScheduledForUploadHandler` (UC4) re-reads on the resulting `scheduled` transition — a reschedule with no re-enqueue path would strand the row forever (TL-flagged 2026-09-25). **Release semantics (YR19, corrected):** the unit is released in `finally` **only** when `reservedThisRun` is `true` **and** either the `StartUploadAsync` (session-create) request issued **in this run** was never sent (the flag `sessionCreateSent` is set the instant that call is issued), **or** Google's response to it is `quotaExceeded`/`uploadLimitExceeded`. Gating on `reservedThisRun` matters on the resume path (step 6 skipped the reserve): without it, a transport failure during probe/resume would see `sessionCreateSent == false` (no session-create was issued *this* run) and incorrectly hand back the unit an *earlier* run already consumed. Any other response to session-create — including a 4xx validation rejection — **consumes** the unit even though the video was never accepted; only these two conditions release, and only for a unit reserved in the same run.
7. **Access token.** `ITokenService.GetAccessTokenAsync(connectionId, ct)` (F12) — refresh when `token_expires_at - now < 5 min`, under `SELECT … FOR UPDATE` on the `channel_connections` row with a re-read after the lock (F21); credentials only through `IOAuthClientResolver` (D5); a concurrent refresh (another job or a health run) wins the lock and the loser re-reads the fresh token instead of presenting the refresh token twice.
8. **Session.** `VideoInsertRequest` is built **from `platform_payload.upload` verbatim** (F13: `privacyStatus`, `publishAt?`, `preAudit`; never from live `AuditStatus`) with `length = assets.size_bytes` (F14, `BlobObject.Length` only cross-checked). `platform_payload.uploadSessionEnc` present → unprotect (`ITokenProtector.Unprotect`, purpose `tokens.v1/{connectionId}`) and `ProbeUploadAsync`: `Complete(videoId)` → `MarkProcessing`; `Partial(nextByte)` → `UploadStreamPositioning.EnsureAtAsync(stream, nextByte, ct)` (seek when `CanSeek`, else discard in 256 KB blocks, constant memory) then `ResumeUploadAsync` — the client's contract is "read from the current position and start the first `Content-Range` there" (CR-17 requests a byte-range read so the discard disappears); `Expired` → **YR27-3: the restart is a new `videos.insert` session-create call, so it reserves a fresh unit first** (`quota.TryReserveAsync` again, even though step 6 may have skipped the reserve for this run under YR27-2's resume-skip — on success here, set `reservedThisRun = true` for *this* run); a reserve failure here goes to the same `quotaExceeded` branch as step 6; on success, `StartUploadAsync` opens the new session and overwrites `platform_payload.quotaReservedDay` with the new day (the stale reservation from the dead session is superseded, not double-counted). Absent (no prior session) → `StartUploadAsync(token, VideoInsertRequest, length)`, then **persist the protected session URI and commit before the first byte** (`Publication.AttachUploadSession` + `IUnitOfWork.CommitAsync`); a crash after this point resumes instead of re-uploading (F02/F03).
9. **Stream.** `blobs.OpenReadAsync(StorageBuckets.Media, asset.StorageKey, ct)` → `ResumeUploadAsync(token, sessionUri, stream, length, progress: null, ct)`; the stream is disposed in `finally`; one chunk buffer only (the SDK's `ChunkSize`, §7).
10. **Terminal.** `MarkProcessing(result.VideoId)` (also writes `platform_payload.processing { startedAt, deadlineAt = startedAt + Poll:MaxMinutes }` for 06.2.5-T1's poller and clears `platform_payload.quotaReservedDay`, F13/Y06Publication) + outbox `publication_updated.v1 { status = processing }` + `UploadOutcome.Processing`. `Episode.MarkScheduled` is **not** called here (it belongs to 06.2.2-T1/06.2.5-T1).

**Validation.** `PublicationId` is non-empty (`Guid.Empty` → `Error.Validation(ErrorCodes.ValidationFailed)`); all Google-side validation (title/description/tags/publishAt) already ran at create (`YouTubeMetadataRules`, D6) — the job never re-validates user text and never logs it.

**Authorization.** No HTTP route; the job is a system job. The job body runs under `ISystemContext.For(userId)` after reading `(id, user_id)` (the 09.2.6 pinned pattern), so the tenant filter still applies to every subsequent read and a foreign id is unreachable.

**Idempotency.** Five independent guards: the row lock (one runner per publication), `external_post_id` set → reconcile only, the early-run guard (`next_attempt_at` in the future → no-op, no quota/state touch, YR27-1), `status` must be `scheduled` or `uploading` (a `processing`/`published`/`failed` row is a no-op returning the current outcome), and `StartUpload` itself is idempotent on `uploading` (YR18) so a retry re-enters at probe/resume instead of being refused — and, on that resume path, step 6 skips a second quota reserve when `quotaReservedDay` is already set (YR27-2). A Hangfire retry after a committed `MarkProcessing` therefore does nothing.

**Error codes written to `publications.error_code`** (the values 02.8.3-T4 and the UI consume): `youtube_quota_exhausted`, `channel_reauth_required`, `platform_incident`, `youtube_upload_failed`, `youtube_rejected`. **Audit:** the upload itself writes no `audit_logs` row (it is machine work on data the user already consented to); `IPlatformIncidentStore.RaiseAsync`/`ClearAsync` write their own audit rows internally (YR25: the store is the single writer, never the caller) — see §UC3/§UC2.

### UC2 — `ClearPlatformIncidentCommand` (operator CLI)

```csharp
// src/NightTales.Application/Features/Publishing/Incidents/ClearPlatformIncidentCommand.cs
public sealed record ClearPlatformIncidentCommand(Guid IncidentId, string Operator, string Note) : IRequest<ErrorOr<IncidentClearOutcome>>;
public sealed record IncidentClearOutcome(Guid IncidentId, bool AlreadyCleared, int PublicationsRescheduled);
```

- Validation (YR16): `Operator` required, 1–100 chars after trim; `Note` required, 1–500 chars after trim (an operator must state what was fixed); unknown `IncidentId` → `Error.NotFound(ErrorCodes.IncidentNotFound)`.
- Authorization: no HTTP route; invoked by `nts-worker incident clear <id> --operator <name> --note "<text>"` on the host (F9, YR16). The verb is the only caller; there is no admin route in P.
- Idempotency: `cleared_at IS NULL` guard inside `UPDATE … WHERE id = @id AND cleared_at IS NULL RETURNING`; a second call returns `AlreadyCleared = true`, reschedules nothing, and writes no second audit row.
- Effects (one transaction): `cleared_at = now`, `cleared_by = @operator` (YR16: the operator's name, not a fixed string), `note`; every held `scheduled` publication of that `(platform, oauth_client_id)` → `next_attempt_at = now + jitter(0–60 s)`, `error_code = null`, `platform_payload.heldByIncidentId` removed; every held `processing` publication of the same client (YR17) → `next_attempt_at = now` (no jitter — 06.2.5-T1's poller resumes on its next tick), `platform_payload.heldByIncidentId` removed; the unchanged `publication_updated.v1 { …, status }` per touched row (batched ≤ 50 per `SaveChanges`); outbox `platform_incident_cleared.v1`; audit `publishing.incident_cleared` (category `support`, visibility `internal`, actor `system:operator`, `ActorLabel = @operator` (YR16), `data = { incidentId, platform, oauthClientId, rescheduled }` — ids, counts and the operator's name only).
- The CLI prints `id=<guid> alreadyCleared=<bool> rescheduled=<n>` and exits 0; `--operator`/`--note` missing → exit 1 with usage. Resume of *series* is not this command's job (02.8.3-T4: the owner still presses Resume).

### UC3 — raise path (shared by upload and token health)

`IPlatformIncidentStore.RaiseAsync(string platform, string oauthClientId, string errorCode, DateTimeOffset now, CancellationToken ct) → ErrorOr<PlatformIncidentRaiseResult(Guid IncidentId, bool IsNew, int AffectedConnections)>`: `INSERT … ON CONFLICT (platform, oauth_client_id) WHERE cleared_at IS NULL DO NOTHING RETURNING id`; on conflict re-read the open row (`IsNew = false`) so a storm of `invalid_client` answers from 50 publications produces **one** row, **one** outbox `platform_incident_raised.v1`, **one** audit `publishing.incident_raised` (YR25, written by the store itself), and **one** P1 alert (F05/D14). `AffectedConnections` counts the `active` connections pinned to that client.

### UC5 — error classification (shared with 06.4.2)

```csharp
// src/NightTales.Application/Features/Publishing/Incidents/OAuthErrorClassifier.cs
public sealed record YouTubeFailure(string? GoogleReason, int? HttpStatus, bool IsTransport);
public enum OAuthErrorOutcome { Transient, TokenRevoked, PlatformIncident, Quota, Validation }
public sealed record OAuthErrorClassification(OAuthErrorOutcome Outcome, string ErrorCode, string? GoogleReason);

public static class OAuthErrorClassifier
{
    public static OAuthErrorClassification Classify(YouTubeFailure failure);
}
```

`Classify` is pure and takes no Google type (Application must not reference `Google.Apis.*`; `GoogleYouTubeClient` maps `GoogleApiException` → `YouTubeFailure` in Infrastructure). It is the single mapping shared by the upload path and `TokenHealthJob` (06.4.2-T1) and consumed by `PublishYouTubeHandler` (§7 map) and `TokenHealthJob`'s `invalid_grant`/`invalid_client` branches (D16). `Quota` is separate from `PlatformIncident` because the two have opposite outcomes (reschedule vs. hold).

### UC4 — schedule/re-enqueue on any transition to `scheduled`: outbox subscriber, not a synchronous seam (06 CR-9, corrected 2026-09-25 with `agent://Y06Publication`)

The Api's `IJobScheduler` registration throws by design ("the Api schedules through the outbox", `features/09-backend-api/09.2.6-worker-hangfire/09.2.6-T1-worker-host-job-scheduler.md:42`), so 06.2.2-T1's `ConsentPublicationHandler` (Api-side) cannot call a scheduler port directly. **The outbox payload is unchanged from D13** — `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }`, no added field — so this task's subscriber derives the delay itself instead of receiving it:

```csharp
// src/NightTales.Application/Features/Publishing/Events/PublicationScheduledForUploadHandler.cs
public sealed class PublicationScheduledForUploadHandler(
    IAppDbContext db, IJobScheduler jobs, IOptions<YouTubeOptions> options, IClock clock) : IOutboxHandler<PublicationUpdated>
{
    public async Task HandleAsync(PublicationUpdated e, CancellationToken ct)
    {
        if (e.Status != "scheduled") return;
        var row = await db.Publications.AsNoTracking().SingleOrDefaultAsync(p => p.Id == e.PublicationId, ct);
        if (row is null) return;                                                          // gone by the time this ran; nothing to schedule
        if (row.Status != PublicationStatus.Scheduled) return;                            // re-check current state, not the event's snapshot
        if (row.ExternalPostId is not null) return;                                       // already has a video; the job's own reconcile handles it
        var queueAt = row.NextAttemptAt                                                   // quota reschedule, incident hold, or incident clear already computed this
            ?? PublicationQueueTime.For(row.PublishAt, options.Value.PublishLeadMinutes, clock.UtcNow);   // D12 (pure, Domain, declared by 06.2.2-T1); publish_at − lead time, or now — the fresh-consent case only
        var delay = queueAt - clock.UtcNow;
        if (delay <= TimeSpan.Zero) jobs.Enqueue<PublishYouTubeJob>((j, jobCt) => j.RunAsync(e.PublicationId, jobCt));
        else jobs.Schedule<PublishYouTubeJob>((j, jobCt) => j.RunAsync(e.PublicationId, jobCt), delay);
    }
}
```

**No held-guard needed (YR24, revised).** An earlier draft special-cased `error_code == "platform_incident"` to stop a held row from scheduling, because `HoldForIncident` used to set `next_attempt_at = NULL`, which the `?? PublicationQueueTime.For(...)` fallback misread as a fresh consent and scheduled for roughly `now` — a hold → event → enqueue → hold loop. **YR24 fixes the root cause instead of papering over it:** `HoldForIncident` now always sets a real **deferred** `next_attempt_at` (`now + 5 × Poll:IntervalSeconds`, §3.1), so `row.NextAttemptAt` is never `null` for a held row and the fallback path is never taken. The handler naturally schedules the next check at that deferred instant through the same branch every other reschedule uses — this **is** YR24's "lazy" re-check: when that run happens, `PublishYouTubeHandler`'s incident gate (UC1 step 5) either finds the incident cleared and proceeds, or holds again with a fresh deferred instant. 06.2.3-T3's clear command additionally wakes the row **immediately** by writing a `next_attempt_at` of `now` (or with jitter) before the same event fires — the lazy poll and the explicit clear are two paths to the same outcome, not two different mechanisms.

**Why one member covers every other path:** `next_attempt_at` is already the field this folder's own `RescheduleUpload` (quota, UC1 step 6), `HoldForIncident` (UC1 step 5, YR24) and 06.2.3-T3's incident-clear sweep (UC2) write on their transition to `scheduled` — the handler only needs to prefer it over D12's formula when present. On a fresh consent (06.2.2-T1/06.2.2-T3), `next_attempt_at` is `NULL`, so the handler falls back to `PublicationQueueTime.For` (D12's rule, declared by 06.2.2-T1 — YR3; 06.2.1-T1's `Publication.Consent`/`ConsentFromAuthorization` computes the same formula for `platform_payload.upload`, but this handler recomputes independently rather than reading that internal payload — no cross-folder read of another task's payload key). One subscriber, no `IPublishScheduler` wrapper (`IJobScheduler`, 09.2.6-T1, is already Worker-safe — CR-10 records the seam as superseded), and one idempotency property protects every scheduling path: `IJobScheduler.Schedule` has no explicit job id, so a redelivered outbox event (at-least-once, F6) may schedule a second Hangfire job, and UC1's row lock + `status` guard + `external_post_id` reconcile + early-run guard (YR27-1) make the second run a safe no-op.

## 5. API and events

**No new HTTP route, no new request/response DTO.** This folder is reached through existing routes owned by 06.2.1-T1/06.2.2-T1 (`../../00-overview.md:362`) and exposes its results through their DTOs:

| Surface | Effect this folder has |
|---|---|
| `GET /episodes/{id}/publications` → `200 { items }` | `PublicationDto.status` advances `scheduled → uploading → processing → published\|failed`; `errorCode` carries one of the five codes above; `nextAttemptAt` is `null` when held by an incident; `externalPostId`/`externalUrl` appear once the video exists |
| `PATCH /publications/{id}` (`If-Match`) | only `awaiting_consent`/`scheduled`; a `scheduled` row held by an incident (`nextAttemptAt == null`) is still editable |
| `DELETE /publications/{id}` | `cancelled` + quota release (06.2.2-T1/06.2.6-T1); no upload job may then run — the job's status guard returns a no-op |
| `GET /publishing/youtube/eta` (06.3.1-T1) | unaffected; the quota release on a pre-send failure makes the slot available again |

Status codes of the existing routes are unchanged (the upload never turns a user request into a 5xx; it turns into a publication status).

**Outbox events written here (Worker handlers, ids only, F6):**

| Event | Payload | Raised when | Consumers |
|---|---|---|---|
| `publication_updated.v1` | `{ publicationId, episodeId, userId, status, errorCode? }` (D13, **unchanged shape** — YR3: no `queueAt` field is added) | every persisted status change (processing, rescheduled, held, failed, reconciled) **and** every transition to `scheduled` (consent, quota reschedule, incident clear) | → `IRealtimeNotifier` SignalR `PublicationUpdated` ids `{ publicationId, episodeId }`, **no `RealtimeData`** (WT54); → 02.8.3-T4's `PublicationUpdatedHandler` (pause reasons on `failed` + `error_code`, F7); → **this folder's own `PublicationScheduledForUploadHandler`** (UC4) on any `scheduled` status — it re-reads the row for `next_attempt_at`/`publish_at` itself rather than reading a payload field |
| `channel_connection_changed.v1` | `{ connectionId, userId, status }` (D13) | `invalid_grant` → connection `expired` | 02.8.3-T4 trigger 6; UI channel state |
| `platform_incident_raised.v1` | `{ incidentId, platform, oauthClientId, affectedConnections }` (D13) | first `invalid_client`/`unauthorized_client` (upload or token health) | 02.8.3-T4 trigger 7 (`oauth_client_incident`); operator alert |
| `platform_incident_cleared.v1` | `{ incidentId, platform, oauthClientId }` (D13) | `incident clear` | 02.8.3-T4 trigger 7 (rechecker stops blocking); 06.4.2-T1 resumes the client |

The handlers live in `src/NightTales.Application/Features/Publishing/Events/` (`PublicationScheduledForUploadHandler`, `PublicationUploadedHandler`, `ChannelConnectionChangedHandler`, `PlatformIncidentRaisedHandler`, `PlatformIncidentClearedHandler`). `platform_incident_*` handlers never touch user-facing text; they only forward.

**SignalR.** `PublicationUpdated` with ids only (D13/WT54); clients refetch `GET /episodes/{id}` (10 README C7). No `uploading` percentage is ever sent.

## 6. UI

No web task is authored in this folder; the copy and the surface are handed over.

| Consumer (10 task) | What it renders from this folder | Copy keys handed over |
|---|---|---|
| `10.4.3-T2` (veto list, `PublicationRepository`, `publication.model.ts`) | `PublicationStatus` `uploading`/`processing` chips, `errorCode` on a finished row, `nextAttemptAt == null` on a held row | `publishing.status.uploading`, `publishing.status.processing`, `publishing.temporarily_unavailable` (the D14 token `publishing_temporarily_unavailable`, keyed snake_case per the 10 convention), `publishing.quota.exhausted`, `publishing.failed.reauth_required`, `publishing.failed.upload`, `publishing.rejected.<reason>` |
| `10.5.1-T1` (episode detail, `EpisodeDetailDto.publications?`) | the same status/error on the episode page after the `PublicationUpdated` refetch (10 README C7/C8) | the same keys (one dictionary, no second copy) |
| `10.7.x` (copy/i18n deck) | receives the key list above with `vi`/`en` strings | — |

Rules the UI must not break: no upload progress bar (no `RealtimeData`, WT54); a held publication shows the neutral "temporarily unavailable" copy and **no reconnect prompt** (the connection is unchanged — F05); a `failed`/`channel_reauth_required` row is the only place a reconnect call-to-action appears.

## 7. Jobs and integrations

| Job | Queue / policy | Trigger | Recurring id |
|---|---|---|---|
| `PublishYouTubeJob` (`src/NightTales.Application/Features/Publishing/Jobs/PublishYouTubeJob.cs`, `sealed`, `[JobPolicy(Queue = JobQueue.Publish, Retries = 5)]`, `RunAsync(Guid publicationId, CancellationToken ct)` — ids only, F3/F4) | `publish` (parallelism **2**, D9) | `PublicationScheduledForUploadHandler` (UC4, outbox subscriber on `publication_updated.v1`) — consent (06.2.2-T1/T3), quota reschedule (UC1 step 6) and incident clear (06.2.3-T3) all reach it through the same event | none (enqueued per publication) |
| `PublishRecurringRegistration` (`src/NightTales.Worker/RecurringJobs/PublishRecurringRegistration.cs`) | — | none in P; the file exists only to hold the `IRecurringJobRegistration` seam 06.2.5-T1 adds (`PollYouTubeProcessingJob`, `PublicationGoLiveJob`) — **registered only while `youtube_publish` is on** (D1) | — |
| `incident clear` (CLI, `src/NightTales.Worker/Cli/IncidentCommands.cs`) | not Hangfire | operator | — |

**Retry policy.** Hangfire `Attempts = 5` with the default exponential backoff for the job as a whole; the backoff only applies to transport failures (see the map). Inside one run the client retries a chunk `PUT` up to 3 times with 1/2/4 s + jitter, then surfaces `youtube_transport` so Hangfire's attempt counter applies. A `404` on the session is not an error: it restarts the session in the same run.

**External calls.** `IYouTubeClient.StartUploadAsync` / `ProbeUploadAsync` / `ResumeUploadAsync` / `GetVideosAsync` (G7/G8); `IBlobStore.OpenReadAsync` (F5); `IQuotaLedger.TryReserveAsync`/`ReleaseAsync` (D10); `ITokenProtector.Protect`/`Unprotect` (C6). Timeouts: HTTP `HttpClient` timeout 100 s for the whole chunk request (a chunk is ≤ 10 MB; the SDK's `ChunkSize` default per G8), `StartUploadAsync` 30 s, `ProbeUploadAsync` 15 s, `GetVideosAsync` 30 s; the job's own `CancellationToken` is honoured by every call (Hangfire shutdown requeues).

**Failure map (D9, verbatim to the task table; release semantics per YR19).**

| Google condition | Quota | Publication | `error_code` | `next_attempt_at` | Outbox / alert |
|---|---|---|---|---|---|
| `quotaExceeded`, `uploadLimitExceeded` (as the session-create response) | release (YR19: always releases regardless of timing) | `scheduled` | `youtube_quota_exhausted` | next PT midnight + jitter ≤ 10 min | `publication_updated.v1` |
| HTTP 401 / `invalid_grant` **during token refresh** (step 7, session-create never sent) | release (YR19: never sent) | `failed` | `channel_reauth_required` | `null` | `channel_connection_changed.v1` (connection → `expired`) + `publication_updated.v1` |
| HTTP 401 / `invalid_grant` **as the session-create response** (step 8, request was sent) | consumed (YR19: sent, not `quotaExceeded`) | `failed` | `channel_reauth_required` | `null` | `channel_connection_changed.v1` (connection → `expired`) + `publication_updated.v1` |
| `invalid_client`, `unauthorized_client` **during token refresh** (session-create never sent) | release (YR19: never sent) | `scheduled` (held) | `platform_incident` | `now + 5 × Poll:IntervalSeconds` (YR24: deferred, never `null`) | `platform_incident_raised.v1` (idempotent) + `publication_updated.v1` |
| `invalid_client`, `unauthorized_client` **as the session-create response** (request was sent) | consumed (YR19: sent, not `quotaExceeded`) | `scheduled` (held) | `platform_incident` | `now + 5 × Poll:IntervalSeconds` (YR24: deferred, never `null`) | `platform_incident_raised.v1` (idempotent) + `publication_updated.v1` |
| 4xx validation (`invalidPublishAt`, `invalidTitle`, `invalidDescription`, …) **as the session-create response** | consumed (YR19: any non-quota response to a sent request consumes, including 4xx) | `failed` | `youtube_upload_failed` | `null` | `publication_updated.v1`; raw reason in `platform_payload.lastGoogleReason` |
| 5xx / timeout / `HttpRequestException` mid-chunk (session already created) | consumed (bytes may have arrived) | stays `uploading` | unchanged | unchanged | Hangfire retry with resume |
| retries exhausted after 5 attempts | consumed | `failed` | `youtube_upload_failed` | `null` | `publication_updated.v1` |
| session `404` (expired) — restart | **reserves a fresh unit (YR27-3)**: released only under the same two conditions (never sent / `quotaExceeded`) | stays `uploading` | unchanged | unchanged | none (restart in the same run) |
| `videos.list` → `rejected` + `rejectionReason` | — | `failed` | `youtube_rejected` | `null` | `publication_updated.v1` (→ 02 pause reason `video_rejected`) |

**`sessionCreateSent`/`reservedThisRun` flags (YR19, corrected).** `sessionCreateSent` is set to `true` the instant the `StartUploadAsync` HTTP call is *issued in this run* (not when it succeeds) — so a session-create call that Google rejects with any code still counts as "sent" and consumes the unit; only a token-refresh failure at step 7 (which prevents the call from ever being issued) counts as "never sent" and releases. `reservedThisRun` is set to `true` only when *this execution* actually calls `quota.TryReserveAsync` (step 6, or step 8 on a session-404 restart) — a resume that skipped the reserve at step 6 (YR27-2) never sets it, so a probe/resume failure in that run cannot hand back a unit an *earlier* run consumed. The step-5 incident gate is a separate, earlier case: it short-circuits before step 6 even reserves a unit, so there is nothing to release there — `HoldForIncident` never touches the ledger. `ReleaseAsync` in `finally` runs when `reservedThisRun && (!sessionCreateSent || classification == Quota)`. The **per-user** reservation is released on exactly the same branch and conditions (`reservedUserThisRun == true` and the same predicate), so the per-user counter and the global unit move together (**ST2**, F25); step 8's session-404 restart also re-reserves both. (06 CR-13b)

`youtube_go_live_unconfirmed` and the poll/go-live jobs are 06.2.5-T1's; `failed` + `upload_failed` maps to 02's pause reason `upload_failed` (F7).

**Batching / N+1.** One publication per job run; one `videos.list` call per reconcile (single id); a raise is a single-row `HoldForIncident(incidentId, nextAttemptAt)` on the row the job already locks — **no raise-time batch sweep across the client's other publications (YR24)**; the clear command's per-incident reschedule (06.2.3-T3) loads the held publications of that incident as **tracked entities** (batch ≤ 200, looped until none remain within the one command) and calls `Publication.ReleaseHold(...)` on each, one `SaveChangesAsync()` per batch (YR27-4: never a raw set-based `UPDATE` for rows that must raise a domain event — the outbox interceptor only fires from `[OutboxEvent]` events on tracked aggregates, F6); `SaveChanges` once per upload run (twice when the session URI must commit before the first byte — the second commit is the terminal state).

**Metrics.** `youtube_upload_attempts_total{outcome}` (bounded: `processing`, `rescheduled`, `held`, `failed`, `reconciled`), `youtube_upload_bytes_total`, `youtube_quota_used` (09.9.3 owns the metric; rule 10 at 80 % — F8), `platform_incident_open{platform,oauth_client_id}` (new, cardinality bounded by the number of OAuth clients), `platform_incident_cleared_total`. Logs carry `publicationId`, `connectionId`, `oauthClientId`, `errorCode`, `googleReason`, `attempt` — never a title, a description, a token or a session URI.

## 8. Security and privacy

- **Session URI is a capability.** It is stored only as `ITokenProtector.Protect` ciphertext with the same purpose root as the tokens, `tokens.v1/{connectionId}` (D4/F03); it never appears in DTOs, logs, exceptions, audit `data` or the outbox payload. It is written **before** the first byte and only replaced after a `404` restart.
- **Tokens.** Never leave the Worker process; never in a DTO, log, exception message or audit row (D4). Refresh happens under `SELECT … FOR UPDATE` on `channel_connections` with a re-read, so two concurrent jobs cannot present the same refresh token twice (F21, which is otherwise a Google-side revocation trigger). Credentials only through `IOAuthClientResolver`; the client secret is read from `nts/<env>/api/YOUTUBE_CLIENT_SECRET` (C12) and wipes on dispose.
- **Tenant isolation.** The job resolves `(id, user_id)` under the system query and then runs every read inside `ISystemContext.For(userId)`, so a foreign publication id behaves as missing (404 semantics, D10 of 09).
- **CSRF / state.** Not applicable on this path (no cookie-authenticated browser request); the connect/callback state machine is 06.1.2-T1's. The cancel-by-link token is 06.2.2-T2's.
- **Idempotency and double-upload.** Row lock + `external_post_id` reconcile + `status = scheduled` guard (UC1) — the single highest-value safety property of this folder (gate `06-publish`).
- **Quota integrity.** `TryReserve` is a single atomic `UPDATE … WHERE units_used < units_limit RETURNING`; `Release` only on a documented pre-send path (step 6), so the ledger never over-releases. A failure after the session exists consumes the unit (a video row may exist on Google).
- **Secrets in logs.** A redaction test asserts the captured Serilog output contains no `ya29.`, no `Bearer`, no `upload_id=`, no title and no channel name.
- **Operator action.** `incident clear` requires `--operator` and `--note` (YR16), is audited (`publishing.incident_cleared`, `support`/`internal`, actor `system:operator`, `ActorLabel` = the operator's name), and is idempotent; no HTTP surface exists, so an admin session is not a sufficient (nor necessary) credential — the CLI runs on the host.
- **Nothing sensitive in outbox payloads**: ids, status and a bounded code only (F6/D13).

## 9. Test plan

Projects (canonical names, 09 README Part A D2): `tests/NightTales.Application.UnitTests` (pure rules), `tests/NightTales.Api.IntegrationTests` (**every DB, HTTP and Worker-job test; the Worker host runs in-process**), `tests/NightTales.TestKit` (fakes). xunit.v3 + Shouldly; **no mocking library** (hand-written fakes, D19/09 N10-N11); real PostgreSQL 17 via `PostgresFixture`; `FakeClock` for time; `FakeGoogleServer` (Kestrel) for Google.

| Project / class | Cases |
|---|---|
| `tests/NightTales.Application.UnitTests/Publishing/OAuthErrorClassifierTests.cs` | the 14 classification cases of 06.2.3-T3: each Google reason → outcome, `invalid_client` ordered before `invalid_grant`, a present reason always beats `IsTransport`, every outcome maps to a known error code |
| `tests/NightTales.Application.UnitTests/Publishing/` (`UploadFailureMapTests`, `PtDayTests`, `PublicationUploadTransitionsTests`) | quota → release+reschedule; client-broken → incident+hold; transport/session-expired → stay `uploading`; PT midnight across spring-forward and fall-back with bounded jitter; `StartUpload` without consent and from a terminal status is invalid |
| `tests/NightTales.Api.IntegrationTests/Publishing/YouTubeUploadClientTests.cs` (`[Trait("Category","YouTube")]`) | 06.2.3-T1's full list: session create with `containsSyntheticMedia = true`, 256 KB chunking, `GuardedStream` (no full buffer), probe `Partial`/`Complete`/`Expired`, `404` restart, drop-after-last-chunk, a resume that starts the first `Content-Range` at the positioned offset (`UploadStreamPositioning.EnsureAtAsync` on a seekable and on a non-seekable stream), a total that disagrees with `assets.size_bytes` failing before any Google call, quota/`uploadLimitExceeded`/validation/refresh mappings, no token or session URI in logs, startup validation of `UploadBaseUrl` |
| `tests/NightTales.Api.IntegrationTests/Publishing/PublishYouTubeJobTests.cs` | 06.2.3-T2's full list: happy path (request built from `platform_payload.upload` verbatim, `platform_payload.processing` + `quotaReservedDay` cleared), no-consent refusal, lost-response resume with one session create, encrypted session URI written before the first byte, reconcile on an existing `external_post_id`, quota reschedule to the next PT midnight (DST), incident hold without quota, `invalid_grant` → connection `expired`, five transport failures → `failed`, two concurrent runs presenting one refresh grant, terminal no-ops, flag off, blob opened and disposed once, no title/description/token in logs |
| `tests/NightTales.Api.IntegrationTests/Publishing/PlatformIncidentTests.cs` | 06.2.3-T3's full list: hold the **one** failing publication (YR24: per-row, not a batch sweep) with a deferred `next_attempt_at`, other publications of the client held lazily only on their own run, one row/event/alert per incident, connection unchanged, quota released only for the never-sent timing case (YR19), clear via tracked-entity `ReleaseHold` with jitter for `scheduled` and `now` for `processing` (YR17/YR27-4), idempotent second clear, `--operator`/`--note` required (YR16), unknown id, audit `support`/`internal` with `ActorLabel`, older-incident isolation, concurrent raises, metric 0 → 1 → 0, and the whole `PublishYouTubeJobTests` class still green after the classifier cutover |
| `tests/NightTales.Api.IntegrationTests/Publishing/PublicationScheduledForUploadHandlerTests.cs` | 06.2.3-T2's full list: `next_attempt_at` set schedules at that instant (covers both a quota reschedule and a YR24 hold — both carry a real deferred value, never `null`), no `next_attempt_at` falls back to `PublicationQueueTime.For`, a missing/gone row is a no-op, a non-`scheduled` status is a no-op, a redelivered event schedules twice but the job runs once effectively |
| `tests/NightTales.TestKit/Google/FakeGoogleServer*.cs` (extended, see T1) | session create, chunk `308` + `Range`, probe, `404` expiry, drop-after-last-chunk, `videos.insert` errors, `videos.list` transitions, call counters; the token endpoint and revoke come from 06.1.2-T1 |
| Architecture (`tests/NightTales.Architecture.Tests`, 09.1.2-T1) | `Jobs_TakeIdsOnly` covers `PublishYouTubeJob.RunAsync`; `InfrastructureOnly_GoogleApis` — no `Google.Apis.*` reference from Domain/Application/Api |

**Manual/staging (acceptance, not automated).** One private upload of a real short MP4 to the test channel through the seeded publication, asserting `external_post_id`, `processing`, then (with 06.2.5-T1 landed) `published`; `quotaExceeded`, `invalid_grant` and `invalid_client` are exercised against `FakeGoogleServer` only (gate `06-publish`).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.2.3-T1 | `IYouTubeClient` upload: resumable session, persisted session URI, streaming from storage, `FakeGoogleServer` | L (2.0) | 06.1.2-T2, 09.7.2-T1 | 06.2.3-T2 |
| 06.2.3-T2 | `PublishYouTubeJob` + token refresh + error mapping | M (1.0) | 06.2.3-T1, 06.2.2-T1, 06.3.1-T1, 09.2.6-T1 | 06.2.3-T3, 06.2.4-T1, 06.2.5-T1, 02.8.3-T4, 07.3.1-T1 (07 CR-11), 06.3.2-T1 (06 CR-18) |
| 06.2.3-T3 | `OAuthErrorClassifier` + platform incidents (hold, alert, clear verb) | XS (0.25) | 06.2.3-T2, 11.6.2-T1 (soft) | 06.4.2-T2, 08.2.2-T1 (08 CR-16) |

**Folder total 3.25 d** (L 2.0 + M 1.0 + XS 0.25) against README §9's 06.2 band (7.0 → 8.5 d for the whole epic): this folder's share is unchanged from the high-level sizing — the L was already the largest single item in 06.2, and the two additions found in the review (the persisted encrypted session URI and the incident gate inside the job) are absorbed by T2's M because the port and the table live in T1/T2 respectively. No re-estimate change is requested.

**CR requests (recorded here; `../README.md` §4 is the owner).**

| # | Target | Change |
|---|---|---|
| CR-10 | `../README.md` §3 D9 | **Superseded by YR3 (README §10), settled.** This file originally proposed `IPublishScheduler.EnqueueAtAsync` as the enqueue seam; YR3 rules that the Api never enqueues — every transition to `scheduled` (consent, quota reschedule, incident clear) emits the unchanged `publication_updated.v1` (no `queueAt`), and `PublicationScheduledForUploadHandler` (06.2.3-T2, UC4) re-reads the row and schedules via `PublicationQueueTime.For` (06.2.2-T1). No README change is requested under this number |
| CR-22 | `../README.md` §3 D20 | **Settled by YR4 (README §10): `platform_incidents` table, entity, `_06_PlatformIncidents` migration and `IPlatformIncidentStore` are 06.2.3-T2's; classifier, clear verb and metrics are 06.2.3-T3's.** (This folder's local number was renumbered from CR-9 during drafting to avoid colliding with an unrelated CR-9; YR4 now makes the ownership decision itself, so this row records the outcome rather than a pending request.) |
| CR-12 | `features/11-worker-deployment/11.6-operations/11.6.2-T1-…` + `features/11-worker-deployment/README.md` D17 | a 16th alert rule: `platform_incident_open{platform,oauth_client_id} > 0 for 5 min → page`. 11.6.2-T1's set is a closed 15 rules (its `:32-58`; the only YouTube rule is #10, quota > 80 %) and 09.9.3's custom metrics add only `youtube_quota_used`, so nothing carries this alert today: the metric/gauge name and the clear counter are **defined by 06.2.3-T3**, which also ships the rule file; 11.6.2-T1 must load it as rule 16 and D17 must own its threshold |
| CR-11 | `../README.md` §3 D17 (audit action codes) / `plans/09-backend-api.md` §5.4 error-code catalogue | `platform_incident` joins the `publications.error_code` vocabulary as one of the five `PublicationErrorCodes` values (`Domain/Publishing/PublicationErrorCodes.cs`, YR27-6) — the code is defined by this task (06.2.3-T2/T3) and must be added to the catalogue wherever `publications.error_code` is otherwise enumerated |
| CR-13 | `../README.md` §3 D19 | `FakeGoogleServer` ownership split: 06.1.2-T1 owns token/revoke/channels, 06.2.3-T1 adds the upload surface (one file, two writers is otherwise a merge hazard) |
| CR-14 | `features/02-story-writing/02.8.3-cadence-publishing/02.8.3-T4:26` | 02 attributes `PlatformIncidentRaised`/`PlatformIncidentCleared` to 06.4.2-T2; the upload path also raises them (06.2.3-T3) — one event, two producers, both must be listed |
| CR-15 | `../README.md` §3 D11 | `YouTubeOptions` lives at `src/NightTales.Application/Abstractions/Publishing/YouTubeOptions.cs` with `YouTubeOptionsValidator` and is bound once by 06.1.2-T1's `AddYouTubePublishing(configuration)` (`06.1-connect/06.1.2-T1-connect-callback.md:186-192`). This folder adds `UploadBaseUrl` + `Upload:*` to that class and **no** second extension; 06.2.4-T1's assumed path `Application/Features/Publishing/Options` (`agent://Y06Status`) is the divergence to settle in favour of 06.1.2-T1's file |
| CR-16 | `../README.md` §3 D9/D19 | one owner per seam file: 06.1.2-T1 declares `IYouTubeClient`, `GoogleYouTubeClient`, `YouTubeOptions`/validator, `AddYouTubePublishing`, the `FakeGoogleServer` token/revoke/channels endpoints and the `OAuthTokens`/`GoogleChannel` records; 06.2.3-T1 **adds** the four upload members, `UploadBaseUrl`/`Upload:*`, the fake's upload surface and the upload tests. No second interface/class/options binding; the job class is 06.2.3-T2's `Application/Features/Publishing/Jobs/PublishYouTubeJob.cs` (a Worker `IRecurringJobRegistration` only) |
| CR-17 | `features/09-backend-api/09.7-video-review-storage/09.7.2-T1-…` (BD10) | add a ranged read `OpenReadAsync(bucket, key, long fromByte, CancellationToken ct)`; without it a resume must discard `nextByte` bytes from the start of the object (constant memory, O(n) reads) because AWSSDK.S3 response streams are not reliably seekable |
| CR-18 | `features/09-backend-api/09.7-video-review-storage/09.7.2-T1-…`, `../../00-overview.md` §6 | the upload total is `assets.size_bytes`; `BlobObject.Length` is nullable and is a cross-check only. A disagreement (or a stream that ends before `size_bytes`) fails the publication with `youtube_upload_failed` before any Google call |
| CR-19 | `features/06-youtube-posting/06.4-revocation/plan.md` §10.1 item 1 | not a gap: `ITokenService.GetAccessTokenAsync(connectionId)` is already declared and shipped by 06.1.2-T2 (`06.1.2-T2-channel-connection.md:110, 183`; `06.1-connect/plan.md:19`). Close 06.4.2's stale [OPEN] against this evidence; 06.2.3-T2 consumes the port and ships no `IChannelTokens`/`ChannelTokenService` |
| CR-20 | `features/06-youtube-posting/06.2.1-publication-consent/06.2.1-T1-publication-aggregate.md` | the `Publication` aggregate and its persistence seam: 06.2.1-T1 already declares `Publication.StartUpload(DateTimeOffset)` (`:58`) and persists via `AppDbContext`/`DbSet<Publication>` (`:202-203`), so 06.2.3-T2 adds seven transitions (including `ReleaseHold`, YR27-4) to that aggregate and uses `IAppDbContext`, declaring **no** `IPublicationRepository`. If 06.2.1-T1 later introduces such a port, 06.2.3-T2 consumes it (one mechanism, cutover) |
| CR-23 | `features/06-youtube-posting/06.4-revocation/plan.md` R17 | **Resolved (2026-09-25 by `agent://Y06Revocation`).** `IPlatformIncidentStore.HasOpenIncidentAsync`'s pinned signature there read `Task<ErrorOr<bool>>`; this task's Design (§4 UC1 step 5, `plan.md` §3.1) uses `Task<ErrorOr<Guid?>>` (the lazy per-row hold needs the open incident's id to pass to `Publication.HoldForIncident(incidentId, nextAttemptAt)`) — ground truth is this task's shipped file plus YR24 (lazy per-row hold) and YR25 (single writer), not YR27 as originally miscited. `06.4-revocation/plan.md` R17, UC3 steps 165-166, and 06.4.2-T1's plan.md now all carry `Guid?`/`null` semantics; zero `ErrorOr<bool>` remain in that folder |

## 11. Rollout

1. **Migration first:** `_06_PlatformIncidents` (expand-only) ships before the Worker build that reads it; no backfill, no column change on `publications`.
2. **Flag:** `youtube_publish` off by default (D1). With it off, no `PublishYouTubeJob` is enqueued, the routes answer 404 `feature_disabled`, and the recurring registration is not created — the new code is inert.
3. **Order:** T1 (port + fake, no wiring) → T2 (job, enqueue seam, incident gate) → T3 (classifier, raise/clear, alert). Each task is independently deployable: T1 adds no runtime path, T2 is unreachable without the flag, T3 only reacts to codes the job already returns.
4. **Smoke on staging:** seeded owner + real test channel, one private upload; then the three failure paths against `FakeGoogleServer`; then the operator drill: force `invalid_client`, confirm the publication is held with `next_attempt_at = null`, the alert fires once, `nts-worker incident clear <id> --operator "<name>" --note "…"` reschedules it.
5. **Rollback:** turn the flag off (jobs stop being enqueued; an in-flight job finishes or is requeued into a queue with no worker only if the Worker is redeployed without the job type — prefer draining). The migration is left in place (expand-only); no data is destroyed. A publication left `uploading` by a rollback is recoverable because its session URI is persisted: re-enabling the flag resumes it.
6. **Observability:** dashboard panels for `youtube_quota_used` (rule 10 at 80 %) and the new incident metric; the drill record is the acceptance evidence (F8).
