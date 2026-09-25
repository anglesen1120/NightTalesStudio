# 07.3 — Publish (TikTok Direct Post init, status poll, caps and errors)

Phase **S · R2**, flag `tiktok_publish` (owner-only until 07.4.1-T2, CR-6). Three tasks, **M + M + S = 2.5 d raw / 3.0 buffered**. Contract: `../README.md` (§1 K/C/U, §2 F01–F25, §3 D1–D19, §8.1, §10 TT1–TT12). This folder turns a consented TikTok publication into one post exactly once, tracks it to a terminal state, and owns the cap/error/alert surface. **No UI work** (10.7.3-T0/T1); backend tasks state which screen surface consumes them.

## 1. Scope

| Task | Title | Size | Deliverable |
|---|---|---|---|
| 07.3.1-T1 | Init (`PULL_FROM_URL`) + `ITikTokRateLimiter` | M (1.0) | `PublishTikTokJob`, `ITikTokApiClient`/`TikTokApiClient` (`init`), `ITikTokRateLimiter`, presigned source URL via CR-4, `publish_id` persisted before polling |
| 07.3.2-T1 | Status poll + `fail_reason` map | M (1.0) | `PollTikTokStatusJob`, `TikTokStatusMap`, `RecordTikTokStatusCommand`, terminal/reconciliation rules, `tiktok_post_latency_seconds` |
| 07.3.3-T1 | Caps, errors, alerting | S (0.5) | complete D10 surface (`PublicationErrorCodes`), incident hold for `url_ownership_unverified`/`scope_not_authorized`, cap metrics + P2 alert rule, vi/en copy keys |

In scope: everything that happens **after** consent (`status = scheduled`) and before the audit hand-off.

Out of scope and owned elsewhere: the TikTok app, scopes and `PULL_FROM_URL` domain verification (07.1.1-T1, gate `07-connect`); connect/callback/disconnect and the token lifecycle (07.1.2-T1/T2, D12/D13); `creator_info` lookup (07.2.1-T1, D11); validation matrix + consent variants (07.2.2-T1/T2, D5); the post screen (10.7.3, CR-7); evidence pack and filing (07.4.1-T1/T2, 13.4.3-T1, C10).

Explicitly **not** built here (F01/TT10/§11 [OPEN]): the chunked-upload protocol in production (K4/K5 live only in `FakeTikTokServer`), `POST /v2/post/publish/cancel/`, the inbox (`video_upload`/`SEND_TO_USER_INBOX`) mode, `cancel-by-link` (no TikTok equivalent, F18), the YouTube quota ledger (TT12), and a distributed rate-limit store (D6, [OPEN] only if the Worker is scaled out).

## 2. Confirmed facts

**TikTok API (✅ `../README.md` §1.1, read 2026-09-25; nothing else is assumed)**

- K1 init `POST /v2/post/publish/video/init/`, scope `video.publish`, **6 req/min per user access token**; `invalid_param` 400; `spam_risk_too_many_posts`, `spam_risk_user_banned_from_posting`, `reached_active_user_cap`, `unaudited_client_can_only_post_to_private_accounts`, `url_ownership_unverified`, `privacy_level_option_mismatch` 403; `access_token_invalid`, `scope_not_authorized` 401; `rate_limit_exceeded` 429; 5xx retryable. `access_token_invalid` means **invalid or expired** (F24).
- K3 init body: `post_info.privacy_level` (required, ∈ `privacy_level_options`), `post_info.title` (caption, ≤ **2200 UTF-16 runes**), `disable_duet`, `disable_stitch`, `disable_comment`, `video_cover_timestamp_ms`, `brand_content_toggle` (**required**), `brand_organic_toggle`, `is_aigc`; `source_info.source ∈ FILE_UPLOAD | PULL_FROM_URL`; response `data.publish_id` (≤ 64), `data.upload_url` (FILE_UPLOAD only).
- K7 status `POST /v2/post/publish/status/fetch/`, **30 req/min per token**; `status ∈ PROCESSING_UPLOAD | PROCESSING_DOWNLOAD | SEND_TO_USER_INBOX | PUBLISH_COMPLETE | FAILED`; `fail_reason` families per D9; `publicaly_available_post_id` (list<int64>, only for public moderated posts — our field is `externalPostId`, TT5); `uploaded_bytes`.
- K16 `PULL_FROM_URL` requires an `https` URL on a domain/URL prefix verified in the app's URL-properties widget (DNS TXT), **no redirect**, reachable for the whole download (**timeout 1 hour** after the task starts); "If video resources are already on API Clients' servers, do not use `FILE_UPLOAD`; use `PULL_FROM_URL` instead".
- K9 while unaudited only `SELF_ONLY` is allowed and ≤ 5 posting users per 24 h; K10 caps ≈ 15 posts/day/creator shared across clients plus our client-level 24-h active-creator cap from the audit filing.

**Contract (✅ C-rows)** — `publications` columns incl. `platform_payload jsonb (upload session, TikTok settings)` and `next_attempt_at` (C2, `00-overview.md:291`); the `00 §7` state machine `scheduled → uploading → processing → published | failed | cancelled` with the consent precondition (C3); the shared `publication_updated.v1`/realtime contract (C8); the Api-never-enqueues rule and `ISystemWorkQuery.PendingAsync<T>` claims (C9, TT1/TT2); 06 building blocks (C6, D15).

**Reuse, verified on disk**

- `Publication.StartUpload(now)` refuses without `ConsentAt`, `scheduled → uploading`, `uploading → uploading` is a resume no-op (`06.2.1-T1-publication-aggregate.md:61`); `Publication.HoldForIncident(Guid incidentId, DateTimeOffset nextAttemptAt)` and `Publication.ReleaseHold(...)` (`06.2.3-T3-oauth-error-classifier-incidents.md:15`); hold semantics = status unchanged, **deferred** `next_attempt_at` (never `NULL`), `error_code = platform_incident`, `platform_payload.heldByIncidentId`, delay `5 × Poll:IntervalSeconds` (YR17/YR24).
- `IPlatformIncidentStore.RaiseAsync(platform, oauthClientId, errorCode, now, ct) → PlatformIncidentRaiseResult { IncidentId, IsNew, AffectedConnections }`, `HasOpenIncidentAsync → Task<ErrorOr<Guid?>>`, `ClearAsync`; single writer (YR25), cleared only by `nts-worker incident clear`.
- `ITokenService.GetAccessTokenAsync(connectionId)` refreshes under `FOR UPDATE` on the connection row (06.2.3-T2, D9/F21).
- Poll-job pattern to mirror (`06.2.5-T1-processing-poll-go-live-publication-updated-episode-status.md`): `RunTickAsync(CancellationToken)` recurring entry (ids-only arch rule), `IRecurringJobRegistration` in `src/NightTales.Worker/Jobs/PublishingRecurringRegistrations.cs`, claims through the pinned port then `ISender` → one command per row, open-incident rows held (never `NULL`), `next_attempt_at = now + IntervalSeconds` on every non-terminal re-claim, claim index = 06 D18's partial `(status, next_attempt_at) WHERE status IN (…)`.
- `IBlobStore` (`features/09-backend-api/09.7-video-review-storage/09.7.2-T1-blob-store-presign.md:29-47`): `OpenReadAsync(bucket, key, ct)`, bucket `nt-media-<env>`, key `media/{userId}/{episodeId}/{renderJobId}/video.mp4`, `CreateReadUrl` presigned GET with `Expires` + `Protocol.HTTPS` (⚠️ synchronous `GetPreSignedURL` at 4.x; `PresignTtlMinutes` is validated 1..15 — **CR-4 raises the ceiling and adds `GetSignedUrlAsync(bucket, key, ttl, ct) → ErrorOr<Uri>`**, D2/[OPEN] O-2).
- `IJobScheduler.AddOrUpdateRecurring<TJob>(id, cron, call)` / `RemoveRecurringIfExists(id)` / `IRecurringJobRegistration`, `[JobPolicy(Queue = …, Retries = …)]`, `JobQueue.Publish` (`09.2.6-T1-worker-host-job-scheduler.md:27-30`, 06.2.5-T1:70).
- `ISystemWorkQuery.PendingAsync<T>(where, batch, ct) → IAsyncEnumerable<(Guid Id, Guid UserId)>`, bypass allow-listed outside `Features/Admin` (`09.2.3-T1:36`); `ISystemContext.For(userId)`.
- `PublicationErrorCodes` single declaration in Domain with the `tiktok_` prefix (TT3, 06 YR27).

**Decisions that bind this folder** — D2 (only `PULL_FROM_URL`, presigned TTL ≥ `UrlTtlMinutes` = 120, no redirect), D6 (`ITikTokRateLimiter`: init 6, creator-info 20, status 30, token 10, revoke 10 per minute per connection; one in-flight call per connection; denied acquire defers the job, never fails the publication), D7 (`publish_id` committed before the first poll, never re-init while non-terminal, status endpoint must report `FAILED`/404 before a re-init), D8 (`platform_payload.tiktok` key set), D9/D10 (status + error tables), D16 (`[JobPolicy(Retries = 3)]`, poll 20 s / max 30 min), D18 (metrics + i18n), D19 (`TikTokOptions` is the single source of every value), TT1/TT2 (enqueue + claim), TT3/TT5 (codes + wire spelling), TT10 (no cancel), TT12 (no quota ledger).

Evidence legend: ✅ verified on the cited line · ⚠️ unverified · [DECISION] here or in the README · [OPEN] · [INFERENCE].

## 3. Domain and data

**No new tables, no migration.** Everything this folder writes lives on `publications` (C2) and `platform_incidents` (C6): status, `privacy`, `platform_payload`, `external_post_id`, `error_code`, `next_attempt_at`, `publish_at`. 07 writes **no** `credit_ledger` row and no `platform_quota_usage` row (F19/O6/TT12).

**State transitions written here** (all through `Publication` members, never a raw `UPDATE`; `SaveChangesAsync` only in `UnitOfWorkBehavior`, `09.2.1-T1:23`):

| From | Trigger | To | Side effects |
|---|---|---|---|
| `scheduled` | `PublishTikTokJob` (TT1) | `uploading` | `Publication.StartTikTokUpload(now)`; `next_attempt_at = now + 20 s`; `platform_payload.tiktok.videoUrlExpiresAt`; outbox `publication_updated.v1 { status: "uploading" }`; realtime ids-only |
| `uploading` | init ok, `publish_id` committed | `uploading` | `platform_payload.tiktok.publishId`; `next_attempt_at` set; still `uploading` (download not acked) |
| `uploading` | poll `PROCESSING_DOWNLOAD` (first ack) or `PROCESSING_UPLOAD` | `processing` | `platform_payload.tiktok.lastStatus`; `publish_at` untouched (= `consent_at`, TT6) |
| `processing` | poll `PUBLISH_COMPLETE` | `published` | `external_post_id = publicaly_available_post_id[0]` when non-empty else `null` (a private/unmoderated post has no public id — **success**, not an error, D9); `Episode.MarkPublished` in the same transaction; latency histogram observed |
| `uploading \| processing` | poll `FAILED` (+ retryable `fail_reason`) | unchanged `processing` | `next_attempt_at` backoff, ≤ 3 attempts (D9) |
| `uploading \| processing` | poll `FAILED` (terminal `fail_reason`) | `failed` | `error_code` from D9/D10; `Episode` stays `scheduled` (no publish) |
| `uploading \| processing` | poll deadline (`Poll:MaxMinutes` = 30) | `failed` | `tiktok_poll_timeout`; `publish_id` retained for support |
| `scheduled \| uploading` | `url_ownership_unverified`, `unaudited_client_can_only_post_to_private_accounts` (residual race past the account-privacy gate ⇒ our bug), `scope_not_authorized`, or an open client incident | back to `scheduled` (from `scheduled`: unchanged) | `error_code = platform_incident`, `heldByIncidentId`, deferred `next_attempt_at` (never `NULL`); P1 via the existing alert |
| held row | `nts-worker incident clear` | `scheduled` | 06.2.3-T3's `ReleaseHold` sweep reschedules + emits `publication_updated.v1 { status: "scheduled" }`; **06.2.3-T2's `PublicationScheduledForUploadHandler` (TT1) is the caller that re-drives it** — it re-reads the row and schedules a fresh `PublishTikTokJob`, because the poll claim (`uploading\|processing`) never sees a `scheduled` row |

**`platform_payload.tiktok` — exact key set (D8 ✅; do not add keys)** `{ privacyLevel, disableComment, disableDuet, disableStitch, brandContentToggle, brandOrganicToggle, isAigc, publishId?, videoUrlExpiresAt? }`. Never a token, never the presigned URL itself, never a caption. `privacyLevel` is the TikTok wire enum verbatim (D8/F17); the same value is in `publications.privacy` for the shared KPIs (CR-9).

> Declared extension (**contract gap, reported**): D8 enumerates the settings keys only, but the publish/poll path this folder owns needs a small bookkeeping subset in the same object — `initAttempt`, `initAt`, `pollAttempts`, `lastStatus`, `lastFailReason`, `uploadedBytes` — mirroring 06's `platform_payload.processing.*` (`polls`, `heldByIncidentId`). `initAttempt = { n, startedAtUtc, lastDisposition }` is the **pre-call marker** that makes D7's "never re-init while non-terminal" enforceable across a Hangfire re-entry (written and committed *before* the `init` POST, with `lastDisposition ∈ sent | retryableResponse` so a 429/5xx retry is still allowed and a lost-response re-entry is not); `initAt` is what the `Poll:MaxMinutes` deadline compares against; `pollAttempts` bounds the retryable `fail_reason` retries; `uploadedBytes` is the only progress signal TikTok gives (K7). These six keys are the complete extension; the TL folds them into D8 at DoR and no other key may be added.

> Contract note (⚠️ reported): D7's prose says the init response writes `publish_id` + `video_url`, while D8's authoritative key list carries only `videoUrlExpiresAt`. This plan follows **D8**: the presigned URL is recomputed from the render artifact key on every attempt and never persisted; only its expiry is recorded so an attempt cannot hand TikTok a URL that dies inside the 1-hour download window (K16).

**Upsert/idempotency primitives**

- A publication row is claimed by **id** (`PublishTikTokJob`, one row) or by the shared port (`PollTikTokStatusJob`, `ISystemWorkQuery.PendingAsync<Publication>(p => p.Status == uploading || p.Status == processing, 25, ct)`), then processed by a single command per row (TT2, 06 YR26) so a poisoned row cannot abort a batch.
- Every transition is guarded by an expected-status precondition, so an at-least-once claim of an already-terminal row is a **no-op** (never a second `published` event): the command re-reads the tracked row under `ISystemContext.For(userId)` and the domain member returns `Errors.Conflict` when the status does not match; the row lock is EF's tracked-entity `UPDATE` (`xmin` concurrency token, the same one behind `ETags.Publication` = `"pu-<xmin>"`, C7).
- Re-init guard (D7): `publishId` present ⇒ never call `init` again while the status is non-terminal; a status poll may only lead to a new `init` (new publication row from the screen) after `FAILED`, and to a re-init of the *same* row only when `publishId` is absent and the row is `scheduled` (the post-incident-clear path).

**Claim index.** 06 D18 owns the partial `(status, next_attempt_at) WHERE status IN (…, 'processing')` index. TikTok adds `uploading` to the candidate set, so 07.3.1-T1 must confirm the predicate covers `'uploading'` and, if not, extend it in 07's own migration (`_07_…`, appended after `_06_…`, never edited in place) — a missing predicate is a sequential scan on every tick.

**Jobs/holds read this, not a table**

```sql
-- PollTikTokStatusJob candidate claim (port call, then one command per row)
SELECT id, user_id FROM publications
 WHERE platform = 'tiktok'
   AND status IN ('uploading','processing')
   AND (next_attempt_at IS NULL OR next_attempt_at <= now())
 ORDER BY next_attempt_at
 LIMIT 25;
-- terminal guard inside the command (single writer, TT2)
UPDATE publications SET status = @to, external_post_id = @postId, error_code = @code,
       next_attempt_at = @next, platform_payload = @payload, updated_at = now()
 WHERE id = @id AND status = @from;   -- 0 rows ⇒ idempotent no-op
```

Held rows always carry a **concrete** `next_attempt_at` (`5 × Poll:IntervalSeconds`), never `NULL` (YR17/YR24) — a `NULL`-held row would head-block the 25-row batch and a raw-SQL "loop until empty" sweep would never end.

## 4. Use cases

- **UC-1 consent → published (happy path, manual).** G: a TikTok publication with `consent_at` set and a valid connection. W: consent writes `publication_updated.v1`; 06.2.3-T2's `PublicationScheduledForUploadHandler` sees `platform = 'tiktok'`, schedules `PublishTikTokJob` (no `publishAt`, so immediate, TT1); the job presigns the render artifact, calls `init` once with `PULL_FROM_URL`, commits `publishId` + `uploading`; the poll job takes it to `processing` and then `published`. T: one init request, one post, `external_post_id` set or `null`, `tiktok_post_latency_seconds` observed once.
- **UC-2 duplicate claim / worker restart.** G: a row already `published` (or `failed`/`cancelled`). W: a duplicated Hangfire delivery re-runs the job. T: no HTTP call, no outbox row, no second post (guarded UPDATE affected 0 rows).
- **UC-3 rate limit.** G: 7 init attempts inside one minute for one connection. W: the 7th acquire is denied. T: no `init` request, `next_attempt_at = now + RetryAfter ± jitter`, `tiktok_rate_limit_backoff_total{endpoint="init"}` incremented, the publication is **not** failed (D6).
- **UC-4 pushback from TikTok (429/5xx).** G: TikTok answers `rate_limit_exceeded` or 500. W: the call is retried per `[JobPolicy(Retries = 3)]` and then deferred. T: row not `failed`, no user-visible error code, `tiktok_publish_attempts_total{outcome="retry"}`.
- **UC-5 creator cap / ban / content rejection (terminal).** G: `FAILED` with `spam_risk_user_banned_from_posting` / `spam_risk_text` / `reached_active_user_cap`. W: the map applies. T: `failed` with `tiktok_creator_banned` / `tiktok_content_rejected` / `tiktok_active_user_cap`, the cap metric increments and the P2 alert fires, no retry.
- **UC-6 our client is broken.** G: `url_ownership_unverified` (403) or `scope_not_authorized` (401 on a fresh token). W: `IPlatformIncidentStore.RaiseAsync("tiktok", oauth_client_id, code, now, ct)` + the row returns to `scheduled` held. T: exactly one `platform_incidents` row per client (`IsNew = false` on the second publication), publications held with `error_code = platform_incident`, P1 alert from the existing rule; only `nts-worker incident clear` releases them.
- **UC-7 poll deadline.** G: TikTok never leaves `PROCESSING_DOWNLOAD`. W: at `now > start + 30 min`. T: `failed` `tiktok_poll_timeout`, `publishId` still in `platform_payload` for support, one `publication_updated.v1`.
- **UC-8 lost init response.** G: TX-A has committed `initAttempt` for attempt *n* and the process dies while the `init` POST is in flight (or the socket times out with no response). W: the job re-runs. T: the re-entry sees `initAttempt != null && publishId == null && lastDisposition = "sent"` and **never calls `init` again** — the row goes `failed` `tiktok_upload_failed` (`platform_incident` instead when the failure class is client-level), leaving the user-visible recovery a new publication row. A re-entry after a **retryable HTTP response** (429/5xx, `lastDisposition = "retryableResponse"`) may re-init, per K1's own retryability. Satisfies the `07-post-private` gate's "a forced lost-response retry creates no second post" (F04/D7/D16).
- **UC-9 unaudited drift.** G: `TikTokOptions.AuditStatus = pending` while the stored `privacyLevel` is `PUBLIC_TO_EVERYONE` (only reachable if it was written before the gate), **or** the account went public after consent (`creator_info.privacy_level_options` contains `PUBLIC_TO_EVERYONE`, K9). W: the job runs. T: fail-closed — no `init` call, `failed` `tiktok_invalid_post_settings` / `tiktok_account_not_private` respectively (07 CR-14), a warning log naming the publication and client but no caption. Defence in depth for D4/07.2.2-T1.

## 5. API and events

No new inbound route: this folder is Worker-only. It consumes the routes 07.2.1/07.2.2 and 10.7.3 own (`POST /episodes/{id}/publications` with the `tiktok` object, `POST /publications/{id}/consent`, `GET /publications/consent-text?locale=&variant=` — C4/CR-2) and never answers HTTP itself.

**Outbox events (emitted by the transitions above; TT1/C8)** `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }` on every status change; a held row keeps its status and is re-announced as `scheduled` on release by 06.2.3-T3's `ReleaseHold` sweep. Realtime `PublicationUpdated` carries ids only (C8). No new event type is introduced: `platform_incident_raised.v1` / `platform_incident_cleared.v1` stay owned by 06.2.3-T3's `IPlatformIncidentStore` (07 calls `RaiseAsync` and raises nothing itself).

**ProblemDetails codes this surface produces** (declared once in Domain `PublicationErrorCodes`, TT3/06 YR27; the wire `code` is stable and lowercase snake, C4):

| code | HTTP | who returns it |
|---|---|---|
| `tiktok_invalid_post_settings` | 400 | 07.2.2-T1's create/consent validator; 07.3.1-T1/07.3.3-T1 record it as a publication `error_code` only on the UC-9 fail-closed path |
| `tiktok_video_too_long` | 400 | 07.2.2-T1 (U3) |
| `tiktok_cannot_post_now` | 409 | 07.2.1-T1 (creator info, HTTP-200 error) and the publication failure code |
| `tiktok_creator_cap` | 429 | 07.2.1-T1 / 07.3.2-T1 failure code |
| `tiktok_rate_limited` | 429 | 09's inbound rate policy (never emitted by a job) |
| `consent_text_outdated` | 400 | 07.2.2-T1 (shared with 06) |

Job-time failures are **not** HTTP: they become `publications.error_code` ∈ `{ tiktok_active_user_cap, tiktok_creator_banned, tiktok_content_rejected, tiktok_video_invalid, tiktok_poll_timeout, tiktok_upload_failed, tiktok_reauth_required, tiktok_cannot_post_now, tiktok_creator_cap, platform_incident }` (D9/D10/CR-3) and are surfaced to the screen through `PublicationDto.errorCode` (10.7.3 renders `errors.tiktok.*`, D18).

**Metrics (D18)** `tiktok_publish_attempts_total{outcome}` (outcome ∈ `success|retry|failed|backoff`), `tiktok_rate_limit_backoff_total{endpoint}`, `tiktok_active_user_cap_hit_total`, `tiktok_post_latency_seconds` (histogram, observed once per publication at `PUBLISH_COMPLETE`). No nickname, caption, `open_id` or publication id is ever a label.

## 6. UI

No UI task in this slice (the screen is 10.7.3, its design gate is 10.7.3-T0, CR-7). This folder is the backend those surfaces read:

- `GET /channels/{id}/tiktok/creator-info` (07.2.1-T1) supplies `privacyLevelOptions` and the `canPost`/`blockReason` the screen shows when TikTok refuses (K2/U2).
- `GET /publications/consent-text?locale=&variant=` (07.2.2-T1) supplies the declaration the user accepts (U9/TT9).
- `PublicationDto.status` + `errorCode`: the screen's live pipeline view (U11) reads `uploading → processing → published` and maps `errorCode` through `errors.tiktok.*` (D18). Because `publish_at` is the consent instant (TT6/D3), the screen must not render a scheduled date — there is none.
- The screen must not expose a cancel button for a TikTok publication that is already `uploading` (TT10: `DELETE /publications/{id}` closes the pre-upload window only; `Publication.Cancel` accepts `awaiting_consent|scheduled` — 06.2.6-T1:30).

## 7. Jobs and integrations

| Job | Registration | Claim | Policy / cadence | Notes |
|---|---|---|---|---|
| `PublishTikTokJob` | queued by 06.2.3-T2's `PublicationScheduledForUploadHandler` when `platform = 'tiktok'` (TT1); `Application/Features/Publishing/Jobs/`, ids-only | by publication id | `[JobPolicy(Queue = JobQueue.Publish, Retries = 3)]`, exponential for transport errors | exactly one `init`; never re-init while non-terminal; `RunAsync(Guid publicationId, CancellationToken ct)` |
| `PollTikTokStatusJob` | `IRecurringJobRegistration` in `src/NightTales.Worker/Jobs/PublishingRecurringRegistrations.cs`, stable id `tiktok-status-poll`, registered **only while `tiktok_publish` is on**, else `RemoveRecurringIfExists` (06.2.5-T1:70 pattern) | `ISystemWorkQuery.PendingAsync<Publication>` ≤ 25 rows, `status IN ('uploading','processing')`, ordered by `next_attempt_at`, then one `RecordTikTokStatusCommand` per row (TT2) | recurring, target 20 s (`TikTok:Poll:IntervalSeconds`, D16); per-row spacing is governed by `next_attempt_at`; `[INFERENCE]` the sub-minute tick is verified against `IJobScheduler.AddOrUpdateRecurring(id, cron, …)` — if the scheduler's cron granularity is minute-based, register the closest tick and keep 20 s spacing on `next_attempt_at` (no busy re-schedule chain) | `RunTickAsync(CancellationToken)`; independent per row |
| `TokenHealthJob` (06.4.2) | shared | shared | daily | TikTok branch is 07.1.2-T2's, not this folder's; this folder only calls `ITokenService.GetAccessTokenAsync` |

Integrations: `ITikTokApiClient` (typed `HttpClient`, base URL + version from `TikTokOptions`, JSON source-generated, one `SemaphoreSlim(1,1)` per connection so sequential calls never interleave, every call through `ITikTokRateLimiter.TryAcquireAsync(connectionId, endpoint, ct) → ErrorOr<Unit>`); `IBlobStore.GetSignedUrlAsync` (CR-4, TTL = `UrlTtlMinutes` = 120, no redirect); `ITokenService`; `IPlatformIncidentStore`; `ISender`/`IOutboxWriter`/`IRealtimeNotifier`; `FakeTikTokServer` (TestKit) in tests only.

`TikTokOptions` (D19) is the single source of every value here — `ActiveClientId`, `Clients:<clientId>:SecretName`, `AuditStatus`, `ConsentTextVersion`, `Transfer` (`pull_from_url` only), `UrlTtlMinutes` = 120, `Poll:IntervalSeconds` = 20, `Poll:MaxMinutes` = 30, `RateLimits:{InitPerMinute=6, CreatorInfoPerMinute=20, StatusPerMinute=30, TokenPerMinute=10}`, `MonthlyActiveCreators`, `CaptionMaxUtf16Units` = 2200. No other key is read in this folder; `AuditStatus` is read through `IOptionsMonitor` (never cached at start).

## 8. Security and privacy

- **Tenant**: every job resolves its rows through the pinned `ISystemWorkQuery` port or an id, then works inside `ISystemContext.For(userId)`; a cross-user id can never be processed, and an API-scope lookup of another user's publication is 404 (D14, 09.2.3-T1).
- **Secrets**: no token, `open_id`, client secret, presigned URL or `log_id` ever appears in a log message, an exception, an outbox payload, a metric label or `platform_payload` (D14). The access token is fetched through `ITokenService` per call and never written to `platform_payload`.
- **Audit**: only the actions already declared for this domain (`publishing.publication_created|consented|cancelled|incident_raised|incident_cleared`, 06 D17) — the init/poll path adds **no** audit action and therefore writes no audit row (the job-dispatched commands are `[NotAudited]`; 09.3.6-T1's arch test demands exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command). Incident rows are audited by 06.2.3-T3's store.
- **Abuse**: the outbound surface is bounded by D6's buckets and by "one in-flight call per connection"; the `init` call is only reachable through a consented row, so no user can force an outbound call at will.
- **Data minimisation**: nothing from `creator_info` is persisted by this folder (the job fetches it only for the D4 account-privacy gate, 07 CR-14); `platform_payload.tiktok` holds only the D8 keys; the caption lives in `publications.title` (already handled by 07.2.2-T1).
- **Unaudited containment**: `AuditStatus ≠ approved` ⇒ the fail-closed checks in UC-9 (stored privacy + account privacy), the owner-only flag (CR-6) and the ≤ 5 posting users/24 h reality (K9) mean a defect here cannot publish publicly.
- **Privacy of logs**: a `log_id` from TikTok's error body is the only vendor identifier logged (D10's unknown-code row), never content.

## 9. Test plan

Projects (per repo convention, hand-written fakes from `NightTales.TestKit`, **no mocking library**, D17):

| Suite | File | Covers |
|---|---|---|
| `NightTales.Domain.UnitTests` | `Publishing/TikTok/TikTokStatusMapTests.cs` | every `status`/`fail_reason` combination in D9 → (publication status, `error_code`, retryable?, `next_attempt_at`); the unknown-code default (`tiktok_upload_failed`); `externalPostId` null-vs-set (TT5) |
| `NightTales.Domain.UnitTests` | `Publishing/TikTok/PublicationTikTokTransitionsTests.cs` | `StartTikTokUpload` refuses without `ConsentAt`; `uploading → uploading` is a resume no-op; terminal rows return `Errors.Conflict` (idempotency); hold members set a concrete `next_attempt_at` |
| `NightTales.Application.UnitTests` | `Publishing/TikTok/PublishTikTokJobTests.cs` | one `init` per publication; `publishId` persisted before any poll; no re-init while non-terminal; transport-unknown → `failed` `tiktok_upload_failed` and **no second init**; 429/5xx deferral; UC-9 fail-closed; presigned TTL = 120 min and no redirect |
| `NightTales.Application.UnitTests` | `Publishing/TikTok/PollTikTokStatusJobTests.cs` | claim → one command per row; `PROCESSING_*`/`PUBLISH_COMPLETE`/`FAILED` mapping; retryable `fail_reason` ≤ 3 attempts; poll deadline → `tiktok_poll_timeout`; held rows skipped and not failed; latency observed once |
| `NightTales.Application.UnitTests` | `Publishing/TikTok/TikTokRateLimiterTests.cs` | per-endpoint buckets (6/20/30/10/10) with `FakeClock`; denied acquire returns the right `RetryAfter`; buckets are per connection, not global; a denied acquire never throws |
| `NightTales.Api.IntegrationTests` | `TikTok/TikTokPublishTests.cs` (real PostgreSQL via `PostgresFixture`, `FakeClock`, `FakeTikTokServer`, `InlineJobScheduler`, in-process Worker services, no sleeps) | `Consent_ThenScheduled_Handler_EnqueuesPublishTikTokJob_ForTikTokOnly` (TT1); `Init_PullFromUrl_OneRequest_ThenPublishIdCommitted`; `Poll_ProcessingDownload_ThenComplete_PublishesWithPublicId` and `…_PrivatePost_HasNullExternalPostId`; `FailedFamilies_MapToErrorCodes` (one theory row per D9 `fail_reason`); `LostInitResponse_Retry_CreatesNoSecondPost`; `RateLimitDenied_DefersJob_DoesNotFail`; `ActiveUserCap_RaisesMetricAndAlert_NoRetry`; `UrlOwnershipUnverified_RaisesOneIncidentPerClient_AndHoldsRow` + `IncidentClear_ReschedulesTikTokRow_ThroughOutbox`; `AuditCommitless_CommandsAreNotAudited`; `RealtimeNotify_InsideTransaction` (rollback leaves no NOTIFY) |
| `NightTales.Architecture.Tests` | existing rules | job classes take ids only (`PublishTikTokJob.RunAsync(Guid, CancellationToken)`); every command carries one audit attribute; `PublicationErrorCodes` declares each `tiktok_` code exactly once; `ITikTokRateLimiter`/`ITikTokApiClient` live in Application/Infrastructure, never in Domain |
| `NightTales.Api.IntegrationTests` | `TikTok/TikTokI18nTests.cs` | every D10 code has one vi and one en copy key (`errors.tiktok.*`), and no key is orphaned |

`FakeTikTokServer` (D17) serves `oauth/token` (rotating refresh), `oauth/revoke`, `creator_info` (public/private options, `comment_disabled`, the HTTP-200 error family), `init` (ok, each 403/429 code, the 1-hour URL expiry, the **chunk** path), `status/fetch` (all transitions, every `fail_reason` family, `publicaly_available_post_id` filled/empty) and counts requests per endpoint for the limiter tests. The production client has no chunk code (D2) — the chunk branch exists only here.

Quality gates tested positively: a `FakeTikTokServer` request counter asserts **exactly one** `init`; a `FakeClock` advances the poll deadline; `InlineJobScheduler` records the enqueue calls the assertions read.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 07.3.1-T1 | M (1.0) | BE | S · R2 · flag `tiktok_publish` | 07.2.2-T1, 06.2.3-T2, 09.7.2-T1 | 07.3.2-T1, 07.3.3-T1 |
| 07.3.2-T1 | M (1.0) | BE | S · R2 · flag `tiktok_publish` | 07.3.1-T1, 09.8.1-T1 | 07.3.3-T1, 07.4.1-T1 |
| 07.3.3-T1 | S (0.5) | BE | S · R2 · flag `tiktok_publish` | 07.3.1-T1, 07.3.2-T1, 11.6.2-T1 (soft) | 07.4.1-T1 |
| **Folder total** | **2.5 raw / 3.0 buffered** | | | | |

Re-estimate vs the high-level plan (`plans/07-tiktok-posting.md` §11): 3.0 → **2.5** raw — 07.3.1-T1 −1.0 because `PULL_FROM_URL` removes the chunked uploader (F01/O1, CR-1) and the presigned URL is one store method (CR-4); 07.3.2-T1 stays at 1.0 in this recomposition (the `fail_reason` map, retry classes and reconciliation are real work, F11). This matches `../README.md` §9 ("07.3 posting 3.0 → 2.5") and the S total 7.75 / 9.3 that needs no CR to `00 §4.1` (O2).

## 11. Rollout

1. **Owner-only, unaudited** (`AuditStatus = pending`, CR-6): `feature_flags.tiktok_publish` rules = owner user ids only, no percentage rollout; the recurring id `tiktok-status-poll` registers only for that scope and is removed with `RemoveRecurringIfExists` when the flag is off (06 D1 pattern).
2. **Gate `07-connect`** must be green before any real `init` (domain verification + presigned TTL proven on staging, DoR conditions 1–2).
3. **Gate `07-post-private`** is this folder's acceptance: a real post to the owner's private account ends `published` with `privacy = SELF_ONLY`, `is_aigc = true`, `external_post_id` set or `null`; a forced lost-response retry creates no second post; `reached_active_user_cap`, `spam_risk_too_many_posts`, `url_ownership_unverified`, `rate_limit_exceeded` and every `fail_reason` family pass against `FakeTikTokServer`.
4. **Flag stays owner-only** until 07.4.1-T2 flips `AuditStatus = approved` after 13.4.3-T1; only then may the flag be rolled out and the full option set be selectable (D4/O4).
5. **Observability at rollout**: `tiktok_active_user_cap_hit_total > 0` (P2) and the existing `platform_incident_open > 0` cover `url_ownership_unverified` (CR-10); LB access log drops the query string for `/api/v1/channels/tiktok/callback` (CR-10, another folder).
6. **Rollback**: turn the flag off (jobs unregistered, no new `init`); already-`uploading`/`processing` rows finish polling because the poll job is the only path to a terminal state — a hard stop leaves them until the poll deadline finalises them as `tiktok_poll_timeout` (bounded by `Poll:MaxMinutes`).

**[OPEN] carried out of this folder** — (O-1) `POST /v2/post/publish/cancel/` and the inbox mode stay out of scope (TT10, `../README.md` §11); (O-2) CR-4's `GetSignedUrlAsync` TTL > 15 min and non-redirect behaviour must be proven on staging (09.7.2-T1's `PresignTtlMinutes` is validated 1..15 ⚠️); (O-3) a distributed limiter is needed only if the Worker is scaled beyond one instance per env (D6), and the flag's owner-only rules are the containment until then; (O-4) the sub-minute recurring cadence must be confirmed against `IJobScheduler.AddOrUpdateRecurring` (see §7).
