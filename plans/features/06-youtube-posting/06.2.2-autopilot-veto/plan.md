# 06.2.2 Autopilot veto window — detailed plan

> Detailed plan for high-level `../../06-youtube-posting.md` epic 06.2.2 (autopilot publications and the veto window), contract `../README.md` §3 (D7, D8, D11, D13, D14, D17–D20) and §8.1.
> Tasks in this folder: **06.2.2-T3** (`AutopilotConsentJob` + `PATCH /publications/{id}` with re-validation) and **06.2.2-T4** ("Approve now" one-tap consent from the notice, fallback path).
> Sibling halves of the same story live in `../06.2.1-publication-consent/`: 06.2.1-T1 (publication create + DTO + episode summary), 06.2.2-T1 (consent command), 06.2.2-T2 (notice e-mail + cancel link), 06.2.6-T1 (cancel).
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope.**

- **06.2.2-T3** — the veto-window consent writer and the owner edit path:
  - `AutopilotConsentJob`: recurring Worker job that turns an autopilot publication whose veto window has ended into `scheduled` by writing `consent_at`, `consent_text_version`, `consent_source = autopilot` (D8, C2) — but only when the notice really was delivered, the series is not paused, the authorizing record is current and the channel connection is `active`.
  - `PATCH /publications/{id}`: owner edit of `title`/`description`/`tags`/`privacy`/`publishAt` during `awaiting_consent` and during `scheduled` before upload, with full metadata re-validation, the moderation gate (02.8.2), `If-Match` and — for an edit inside the veto window — the edit itself recording the owner's consent (D8, C9).
- **06.2.2-T4** — "Approve now" from the veto notice: the deep-link CTA in the `publication_veto_notice` e-mail and the per-video one-tap consent it drives, which is also the fallback path if the audit does not accept series-level authorization (owner decision O3 in `../README.md` §10).

**Out of scope (owned elsewhere, referenced as contract).**

| Item | Owner |
|---|---|
| `POST /publications/{id}/consent` (user consent), `PublicationDto`, `ETags.Publication`, episode `publications` summary | 06.2.1-T1, 06.2.2-T1 |
| `publication_veto_notice` e-mail queueing, `platform_payload.vetoNoticeEmailId`, cancel-link minting into `user_action_tokens` | 06.2.2-T2 |
| `DELETE /publications/{id}`, `POST /publications/cancel-by-link` | 06.2.6-T1 |
| Upload (`IYouTubeClient`/`GoogleYouTubeClient`), `PublishYouTubeJob`, quota reservation (`IQuotaLedger`), `OAuthErrorClassifier` + `platform_incidents`, polling (`PollYouTubeProcessingJob`), go-live (`PublicationGoLiveJob`), token health (`TokenHealthJob`) | 06.2.3-*, 06.3.1-T1, 06.2.5-T1, 06.4.2-* |
| `autopilot_settings`, `series_autopilot_authorizations`, pause/resume, the orchestrator that calls 06.2.1-T1 | 02.8.3-T1/T2/T4 |
| Moderation gate API (`IGateEvaluator` / gate verdicts) | 02.8.2 |
| Veto list UI, run log, calm-down of the notice copy | 10.4.3-T2, 10.7.1, 10.7.2 |
| Consent legal texts (`pc.v1`) and `ILegalAcceptance` | 13.1.x, 13.2.1-T1 |

**Flag and phase.** Both tasks ship behind `RequireFeature("youtube_publish")`; `AutopilotConsentJob` is registered only while the flag is on, same rule as 02.8.3-T1 [DECISION D1].

## 2. Confirmed facts

| Id | Fact | Evidence |
|---|---|---|
| F-1 | `AutopilotConsentJob` runs recurring every minute; after the veto window it writes `consent_at`, `consent_text_version`, `consent_source = autopilot`, `autopilot_authorization_id`; it writes **no** consent when `veto_notice_sent_at` is empty or the series is paused (02 CR-14) | ✅ `../README.md` §1 C2 (from 02.8.3 `plan.md:38`, 02.8.3-T4:185,197) |
| F-2 | Batch ≤ 50 rows `WHERE status = 'awaiting_consent' AND autopilot_authorization_id IS NOT NULL AND veto_ends_at <= now` with `FOR UPDATE SKIP LOCKED`; the job reads `(id, user_id)` first, then works per user under a `SystemContext`; skip (no write) when the notice is not `sent`, the series is paused, the authorization is revoked or not current, or the connection is not `active` | ✅ `../README.md` §3 D8 |
| F-2a | **[BLOCKER — fixed here]** D8's single due-row claim (`veto_ends_at <= now`) cannot be the only place `veto_notice_sent_at` is written: 02's trigger 11 pauses the series when `publications.veto_notice_sent_at IS NULL` **30 minutes after `created_at`** (`02.8.3-T4:31`, `02.8.3-T4/plan.md:260`), while `veto_ends_at` defaults to 12 h out (`02.8.3/plan.md:56-63`) — every ordinary autopilot publication would sit unstamped past the 30-minute mark and get paused before its window even matters. §4 UC-1 below is split into a **notice-stamp pass** (no `veto_ends_at` predicate, runs every tick) and the existing **due-consent pass**, so the stamp lands within one tick of delivery regardless of window length | ✅ `02.8.3-T4-pause-triggers-spend-caps-alerts.md:31,143,168`; `02.8.3-T4/plan.md:224,260,456,535` (06.2.2-T3 is "observed only" for trigger 11 — it must not itself pause or cancel, only stamp) |
| F-3 | `PATCH /publications/{id}` takes title, description, tags, privacy, publishAt; allowed only in `awaiting_consent` or in `scheduled` before upload; it re-runs metadata validation and the moderation gate; a passing edit inside the window **is** the owner's consent (`consent_source = user`, `consent_at` = edit time) | ✅ `../README.md` §3 D8; 10.4-series-autopilot `plan.md:67` B19, `plan.md:232` U16 |
| F-4 | ETag `"pu-<xmin>"` (06 CR-2 adds `Publication(uint xmin)` → `"pu-{xmin}"` to `src/NightTales.Application/Common/ETags.cs`, which today holds `Episode`→`"ep-"`, `Series`→`"se-"` and `Matches(string? ifMatch, string current)`, strong exact); `If-Match` is required on consent, `PATCH` and `DELETE` | ✅ 09.1.4-T1:53, `09.1.4-error-mapping/plan.md:44`; `../README.md` §3 D7, §2 F04 |
| F-5 | A D9 route called without `If-Match` → **428 `if_match_required`** from the endpoint filter (`src/NightTales.Api/Http/Filters/RequireIfMatchFilter.cs`, extension `RequireIfMatch()`); a lost `xmin` race after the `If-Match` check → **409 `concurrency_conflict`** | ✅ `features/09-backend-api/README.md:200`; 09.1.4-T1:69, :84; `plans/09-backend-api.md:390`, `:401` |
| F-6 | Missing consent reaching the upload job is refused by the domain (`Publication.StartUpload` without `consent_at`), so the job fails loudly (defence in depth) | ✅ `../README.md` §3 D9; high-level `06.2.2` "Given any code path …" |
| F-7 | `veto_window_hours` 2..48 (default 12) lives on `series.autopilot_settings`; `Autopilot:PublishLeadMinutes` 120 "must stay equal to 06.2.4's lead time" - now read from `YouTubeOptions` | ✅ 02.8.3 `plan.md:56-63`; `../README.md` §1 C10 + §3 D11 (06 CR-3) |
| F-8 | **TL ruling YR1**: `veto_ends_at = min(now + series.autopilot_settings.veto_window_hours, (publishAt ?? now) − YouTubeOptions.PublishLeadMinutes)` because 02.8.3 creates late videos with a shortened veto (02.8.3 `plan.md:146`); when that is < `now + YouTubeOptions.MinVetoWindowHours`, `CreatePublication` rejects with **400 `validation_failed`, field `publishAt`** (02 already treats it as a missed slot) | ✅ TL ruling YR1, 2026-09-25; 02.8.3 `plan.md:146`; `../README.md` §3 D11 |
| F-9 | The authorizing record is `series_autopilot_authorizations(version, text_version, text_hash, topic, channel_connection_id, cadence, privacy, accepted_at, revoked_at)`; `series.current_autopilot_authorization_id` points at the current one; any covered-field change revokes it and clears the pointer | ✅ 02.8.3-T2:79-88; 00 §6 row in 02.8.3 `plan.md:42` |
| F-10 | `publications` columns available to this feature: `status, title, description, tags, privacy, publish_at, next_attempt_at, consent_at, consent_text_version, consent_source, autopilot_authorization_id, veto_notice_sent_at, veto_ends_at, contains_synthetic_media, external_post_id, platform_payload` | ✅ `../README.md` §1 C11 (00 §6); `../README.md` §3 D6/D20 |
| F-11 | `veto_notice_sent_at` is written by (this) 06.2.2-T3 from the delivery status of the e-mail id stored by 06.2.2-T2 in `platform_payload.vetoNoticeEmailId` | ✅ `../README.md` §3 D7 |
| F-12 | E-mail port to reuse: `interface IEmailDeliveryStatus { Task<ErrorOr<EmailDelivery>> GetAsync(Guid emailMessageId, CancellationToken ct); }` with `enum EmailStatus { Queued, Sent, Failed }` and `EmailDelivery(Id, Status, Attempts, LastErrorCode, …)` → delivered means `Status == EmailStatus.Sent`; the read is tenant-filtered, so it must run under `ISystemContext.For(userId)`. The notice template is `publication_veto_notice`, texts rendered by 10.7.2 | ✅ `09.2.6-worker-hangfire/09.2.6-T3-email-pipeline.md:26-29`, `:175`; `../README.md` §1 C6, §3 D7 |
| F-13 | Outbox event `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }`; `IRealtimeNotifier` `PublicationUpdated` carries ids `{ publicationId, episodeId }` and **no** `RealtimeData` (WT54) | ✅ `../README.md` §3 D13; §2 F13 |
| F-14 | Audit actions are a closed list: `publishing.publication_created`, `publishing.consented`, `publishing.consent_autopilot`, `publishing.cancelled`, … (ids and codes only, never titles) | ✅ `../README.md` §3 D17 |
| F-15 | Quota exhaustion and any other "try later" outcome returns the publication to `scheduled` + `next_attempt_at`; a broken OAuth client holds publications in `scheduled` while a `platform_incidents` row is open | ✅ `../README.md` §3 D10/D14; §1 C3 (06 CR-4 applied in 02) |
| F-16 | The veto list UI already calls `PATCH /publications/{id}` with `If-Match`, `POST /publications/{id}/consent` ("Approve now") and `DELETE`; 412 opens `ConflictDialog`; the row model derives `vetoRemainingMs` from `vetoEndsAt` | ✅ 10.4.3-T2:29, :58, :65, :105, :113; 10.4-series-autopilot `plan.md:232` |
| F-17 | Veto window copy/keys owned by 10.4.3-T2 (`autopilot.veto.title\|empty\|timeLeft\|edit\|cancel\|approveNow…`); e-mail templates by 10.7.2 | ✅ 10.4.3-T2:70; `../README.md` §8.1 06.5.1-T1 row (depends on 10.7.2-T1) |
| F-21 | **[peer-pinned, twice-corrected]** `platform_payload.preAudit`/`requestedPrivacy` are written once, at **create** time; `publications.privacy` is already the enforced `"private"` from create for a pre-audit row, and — **retracted 2026-09-25, this is a *permanent* freeze, not a provisional one**: `privacy` never gets corrected back even after `AuditStatus` flips to `approved` (only the *next* autopilot run, created after the flip, goes public; D12, `02.8.3-T3:156`). A `preAudit`-locked row sits in the PATCH-editable states (`awaiting_consent`/`scheduled`) for its entire pre-upload life. **Ownership settled by YR20 (2026-09-25, closes O-14):** 06.2.1-T1 is the sole writer of both keys, at create time; 06.2.4-T1's earlier "seeded the first time this planner runs" claim is superseded. This task still writes neither key | ✅ peer messages 2026-09-25; 06 README D12; `02.8.3-T3:156` |
| F-22 | Job bodies live in Application (`src/NightTales.Application/Features/<Area>/Jobs/<Job>.cs`), registration in the Worker (`src/NightTales.Worker/RecurringJobs/<Job>Registration.cs`) through `IJobScheduler.AddOrUpdateRecurring`, and queue/retry policy comes from the Application attribute `[JobPolicy(Queue = JobQueue.…, Retries = n)]` translated by Infrastructure's `JobPolicyFilterProvider` — no Hangfire attribute on a job class | ✅ 02.8.3-T1:93, :98, :153, :156; `features/09-backend-api/README.md:284` |
| F-18 | Test projects are pinned: `NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `NightTales.Api.IntegrationTests` (real PostgreSQL 17, `PostgresFixture`), `NightTales.Architecture.Tests`, fakes in `NightTales.TestKit`, no mocking library | ✅ `features/09-backend-api/README.md:121` (D2) |
| F-19 | Error codes available today: `publication_consent_required` (409), `if_match_required` (428), `concurrency_conflict` (409), `feature_disabled` (404), `channel_reauth_required`; `validation_failed` with an `errors` map keyed by field | ✅ `plans/09-backend-api.md:419`, `:390`, `:401`; `features/09-backend-api/README.md:191,200`; 02.8.3-T2:79 (`errors["textHash"]`) |

## 3. Domain and data

### 3.1 Aggregates

`Publication` (`src/NightTales.Domain/Publishing/Publication.cs`) is created by 06.2.1-T1, which also exposes `PreAudit` (bool, from `platform_payload.preAudit`, set once at create, F-21) on the aggregate; this folder adds **two** behaviours (`ConsentFromAuthorization` is already declared and implemented by 06.2.1-T1, TL-corrected 2026-09-25 — this task **calls** it, it does not redeclare it) plus one domain event pair.

`Publication.ConsentFromAuthorization(string textVersion, Guid authorizationId, DateTimeOffset now)` (06.2.1-T1's real signature, `06.2.1-T1-publication-aggregate.md:59`, `plan.md:80,251`; note the argument order — `textVersion` first): guards `status == AwaitingConsent`; writes `ConsentAt`, `ConsentTextVersion`, `ConsentSource = "autopilot"`, `AutopilotAuthorizationId`; raises `PublicationUpdated` (outbox, D13). It does **not** touch `next_attempt_at` — 06.2.1-T1's own implementation leaves it as-is, so the Worker's `PublicationScheduledForUploadHandler` (06.2.3-T2) falls back to `QueueTime` derived from `publish_at − PublishLeadMinutes`, exactly like the manual-consent path (06.2.2-T1). This task adds a `veto_notice_sent_at IS NOT NULL` precondition of its own **before** calling it (I2), since the shared method has no notice-delivery concept.

```csharp
// Domain/Publishing/Publication.cs — added by 06.2.2-T3
public ErrorOr<Success> MarkNoticeDelivered(DateTimeOffset now);
// guards: status == awaiting_consent; veto_notice_sent_at is null (idempotent)
// effects: veto_notice_sent_at = now; no status change; raises no domain event (F-2a)

public ErrorOr<Success> ApplyOwnerEdit(
    PublicationMetadata metadata, string consentTextVersion, DateTimeOffset now, bool implicitConsent);
// guards: status in { awaiting_consent, scheduled }; external_post_id is null;
//         metadata already validated by YouTubeMetadataRules (06.2.1-T1)
// effects: replaces title/description/tags/publish_at unconditionally;
//          Privacy is permanently frozen while PreAudit (F-21, I9, freeze confirmed permanent 2026-09-25):
//            PreAudit == true  && metadata.Privacy != "private" -> refuses, PublicationPrivacyLocked (no write at all, not even to other fields)
//            PreAudit == true  && metadata.Privacy == "private" -> proceeds normally (no-op on Privacy, matches the frozen value)
//            PreAudit == false -> publications.privacy = metadata.Privacy
//          this method never writes platform_payload.preAudit or .requestedPrivacy (06.2.1-T1 owns both, F-21)
//          when implicitConsent && status == awaiting_consent -> consent_at = now,
//          consent_text_version = consentTextVersion, consent_source = "user", status = scheduled;
//          veto_ends_at is NEVER modified; raises PublicationEdited
```

`PublicationMetadata` is the value object owned by 06.2.1-T1 (`title`, `description`, `tags`, `privacy`, `publishAt`) — this task reuses it, it does not declare a second one.

Domain events (both mapped to the outbox by 09.1.2's interceptor, ids only): `PublicationConsented(PublicationId, EpisodeId, UserId, Source)` and `PublicationEdited(PublicationId, EpisodeId, UserId, Status)`.

### 3.2 Invariants

| # | Invariant | Enforced by |
|---|---|---|
| I1 | A publication leaves `awaiting_consent` only through `ConsentFromAuthorization`, `ApplyOwnerEdit(implicitConsent: true)` or 06.2.6-T1's cancel | domain state machine |
| I2 | Autopilot consent requires a delivered notice: `veto_notice_sent_at IS NOT NULL` when `autopilot_authorization_id IS NOT NULL` (D8, 02 CR-14) | `ConsentFromAuthorization` guard + the job's skip list |
| I3 | The job writes no consent while `series.autopilot_paused_at IS NOT NULL`, while the publication's authorization is not the series' current one or is revoked, or while the connection is not `active` | handler preconditions (read under the same transaction) |
| I4 | `veto_ends_at` is immutable after create (the notice quoted a deadline); only `ConsentFromAuthorization`, `ApplyOwnerEdit`, cancel and upload change `status` | domain: no setter, `ApplyOwnerEdit` never touches it |
| I5 | An edit that moves `publishAt` earlier may not shrink the remaining veto below `YouTubeOptions.MinVetoWindowHours` (YR1 applied at edit time): `min(now + veto_window_hours, publishAt − PublishLeadMinutes) − now ≥ MinVetoWindowHours` must still hold, otherwise 400 `validation_failed` field `publishAt` | handler validation ([DECISION] local, §10 O-8) |
| I6 | `contains_synthetic_media = true` and `selfDeclaredMadeForKids = false` are never editable through `PATCH` | the PATCH DTO does not expose them |
| I7 | An edit after consent (`scheduled`) never rewrites consent fields and never touches a row with `external_post_id` set | `ApplyOwnerEdit` guard |
| I8 | One active publication per `(episode_id, platform)` — partial unique from D6; this feature never inserts | D6 index |
| I9 | `ApplyOwnerEdit` never writes `platform_payload.preAudit` or `.requestedPrivacy` (06.2.1-T1 owns both); while `PreAudit` is true, an attempted `Privacy` change away from `"private"` is refused whole (no field is written, not just `Privacy`) — the freeze is permanent, so accepting a PATCH that could never take effect would mislead the owner (F-21) | domain: `ApplyOwnerEdit` |

### 3.3 Tables, columns, indexes

No new table and no new column: this folder consumes what 06.1.2-T1/T2, 06.2.1-T1 and 06.3.1-T1 create (F-10, D20). It adds **one index only**, in its own expand-only migration.

| Object | Definition | Use |
|---|---|---|
| `publications` (existing) | `status text`, `autopilot_authorization_id uuid null`, `veto_ends_at timestamptz null`, `veto_notice_sent_at timestamptz null`, `consent_at timestamptz null`, `consent_text_version text null`, `consent_source text null`, `platform_payload jsonb`, `external_post_id text null` | veto scan, edit guard |
| `publications (status, next_attempt_at) WHERE status IN (…)` (existing, D18) | partial index | not selective enough for the veto scan (range on `veto_ends_at`, not `next_attempt_at`) |
| **`ix_publications_veto_scan`** (new) | `CREATE INDEX CONCURRENTLY ix_publications_veto_scan ON publications (veto_ends_at) WHERE status = 'awaiting_consent'` | the due-consent pass's batch claim without a seq scan |
| **`ix_publications_veto_notice_pending`** (new, F-2a) | `CREATE INDEX CONCURRENTLY ix_publications_veto_notice_pending ON publications (id) WHERE status = 'awaiting_consent' AND veto_notice_sent_at IS NULL` | the notice-stamp pass's claim; a row falls out of the index the instant it is stamped, so the index only ever covers genuinely pending rows |
| `series` / `series_autopilot_authorizations` / `autopilot_settings` (existing) | see F-9 | pause + authorization currency checks, `veto_window_hours` |
| `channel_connections (user_id)` (existing, D18) | index | connection `active` check per batch row |
| `email_messages` + `IEmailDeliveryStatus` (existing) | see F-12 | `veto_notice_sent_at` write |

Migration file: `src/NightTales.Infrastructure/Persistence/Migrations/<timestamp>_06_PublicationsVetoScanIndex.cs`, **expand-only**, no down migration dropping data:

```csharp
migrationBuilder.Sql(
  "CREATE INDEX CONCURRENTLY ix_publications_veto_scan ON publications (veto_ends_at) WHERE status = 'awaiting_consent';",
  suppressTransaction: true);
migrationBuilder.Sql(
  "CREATE INDEX CONCURRENTLY ix_publications_veto_notice_pending ON publications (id) WHERE status = 'awaiting_consent' AND veto_notice_sent_at IS NULL;",
  suppressTransaction: true);
```

`CONCURRENTLY` requires a non-transactional migration; the plan records that this is the only such migration in plan 06 and that a rolled-back deploy leaves an invalid index which `DROP INDEX CONCURRENTLY` cleans up (11 rollout §11).

### 3.4 Measured access paths (performance)

| Query | Plan | Bound |
|---|---|---|
| Veto scan | `Index Scan ix_publications_veto_scan` | ≤ 50 rows per tick |
| Per-row precondition batch | one query per batch (not per row) for series pause + authorization currency + connection status, keyed by the ≤ 50 ids | 3 queries per tick, never 3 × 50 |
| Notice delivery refresh | `IEmailDeliveryStatus.GetAsync` only for rows with `veto_notice_sent_at IS NULL` and a non-null `platform_payload->>'vetoNoticeEmailId'` | ≤ 50 calls per tick |
| Edit | `SELECT … FOR UPDATE` on one row + the moderation gate call | 1 row |

## 4. Use cases

### UC-1a — Notice delivery is stamped as soon as it happens (06.2.2-T3, F-2a)

`StampDueNoticeDeliveryCommand(int BatchSize = 50) : IRequest<ErrorOr<NoticeStampBatchResult>>`
`src/NightTales.Application/Features/Publishing/Commands/StampDueNoticeDelivery/{StampDueNoticeDeliveryCommand.cs,StampDueNoticeDeliveryHandler.cs}`

```csharp
public sealed record NoticeStampBatchResult(int Scanned, int Stamped, int StillPending);
// StampDueNoticeDeliveryCommand itself carries [NotAudited("orchestrator; the per-row StampNoticeDeliveredCommand carries its own [NotAudited] marker")].
```

**D5 (09.2.1-T1:23):** `SaveChangesAsync` is only ever called by `UnitOfWorkBehavior`, never by a handler directly — so this command is a pure **orchestrator**, not a writer. Claim (no `veto_ends_at` predicate — this pass must not wait for the window): `ISystemWorkQuery.PendingAsync<Publication>(p => p.Status == PublicationStatus.AwaitingConsent && p.VetoNoticeSentAt == null && p.PlatformPayload.VetoNoticeEmailId != null, batch, ct)` (read-only). For each candidate, `await sender.Send(new StampNoticeDeliveredCommand(candidate.PublicationId, candidate.UserId, candidate.NoticeEmailId), ct)` — a **new per-row command**, `[NotAudited("not a consent; only stamps veto_notice_sent_at from delivery status")]`, `src/NightTales.Application/Features/Publishing/Commands/StampNoticeDelivered/{StampNoticeDeliveredCommand.cs,StampNoticeDeliveredHandler.cs}`, dispatched through the standard pipeline so each row gets its own transaction. That handler: `IEmailDeliveryStatus.GetAsync(noticeEmailId)`; `Status == EmailStatus.Sent` → reload the row and `publication.MarkNoticeDelivered(now)` (trivial domain setter: `veto_notice_sent_at = now`, no status change, no consent) → success; anything else → a typed "not yet delivered" result that the orchestrator counts as `StillPending`, not a failure. This pass never writes `consent_at`, never touches `series`/`authorization`/`connection`, and never raises `PublicationConsented`/`PublicationEdited` — it exists solely so that trigger 11's 30-minute check (`02.8.3-T4:31`) sees a stamp that reflects real delivery instead of the due-consent pass's multi-hour delay.

### UC-1b — Veto window ends, the job writes autopilot consent (06.2.2-T3)

`ConsentDueAutopilotPublicationsCommand(int BatchSize = 50) : IRequest<ErrorOr<AutopilotConsentBatchResult>>`
`src/NightTales.Application/Features/Publishing/Commands/ConsentDueAutopilotPublications/{ConsentDueAutopilotPublicationsCommand.cs,ConsentDueAutopilotPublicationsHandler.cs}`

```csharp
public sealed record AutopilotConsentBatchResult(
    int Scanned, int Consented, int SkippedNoticeUndelivered, int SkippedSeriesPaused,
    int SkippedAuthorization, int SkippedConnection, int SkippedCandidates);

public sealed record ConsentCandidate(Guid PublicationId, Guid UserId, Guid SeriesId,
    Guid? AuthorizationId, Guid ChannelConnectionId, DateTimeOffset VetoEndsAt, string? NoticeEmailId);
```

**No new port for the claim.** UC-1b reuses 09's pinned cross-tenant bypass `ISystemWorkQuery.PendingAsync<Publication>(p => p.Status == PublicationStatus.AwaitingConsent && p.AutopilotAuthorizationId != null && p.VetoEndsAt <= now, batch, ct)` (`Application/Abstractions/ISystemWorkQuery.cs`, impl `Infrastructure/Persistence/Queries/SystemWorkQuery.cs`; wave-1 member at `09.2.3-T1:36`, "no port widening" at `09.2.6-T3:107`); it takes no `ORDER BY` and no lock hint ([OPEN] O-9). UC-1a (above) uses the same bypass with a different predicate (no `veto_ends_at` filter). **D5 (09.2.1-T1:23):** `SaveChangesAsync` is only ever called by `UnitOfWorkBehavior` — `ConsentDueAutopilotPublicationsCommand` is therefore an orchestrator only (claim + dispatch + counters); the actual per-row write happens in a new nested command, `[Audited("publishing.consent_autopilot")]` (category/visibility per D17, 09.3.6-T1 shorthand) `ConsentAutopilotPublicationCommand(Guid PublicationId, Guid UserId) : ICommand<ErrorOr<Success>>` (`src/NightTales.Application/Features/Publishing/Commands/ConsentAutopilotPublication/{ConsentAutopilotPublicationCommand.cs,ConsentAutopilotPublicationHandler.cs}`), dispatched per candidate via `sender.Send(...)` under the owner's `SystemContext` — the standard pipeline (Authorization → Validation → UnitOfWork → Idempotency → Audit) gives **each row its own transaction**, covering the consent write, the notice queue call, the audit row and the outbox row together; a failure in one row's command fails only that row (counted as a skip/error) and never rolls back rows already committed earlier in the same tick. That inner handler reloads through 09's `IAppDbContext` (`db.Query<Publication>()`, tenant filter for free, `09.4.2-5.3/plan.md:196`) inside the owner's `SystemContext`, where `FOR UPDATE` plus the `xmin` token decide any race.

**Orchestrator algorithm** (`[NotAudited("orchestrator; the per-row ConsentAutopilotPublicationCommand carries its own [Audited] marker")]` `ConsentDueAutopilotPublicationsCommand`, no writes of its own):

0. Runs after UC-1a in the same tick, so a notice delivered seconds ago is already stamped by the time this pass reads it.
1. `now = IClock.UtcNow`; claim ≤ `BatchSize` candidates via `ISystemWorkQuery.PendingAsync<Publication>(…)` (read-only, no lock held past the read).
2. For each candidate, in claim order: `var result = await sender.Send(new ConsentAutopilotPublicationCommand(candidate.PublicationId, candidate.UserId), ct);` — one dispatch per row, each with its own pipeline-managed transaction. Fold the `ErrorOr` result into the batch counters (`Consented` on success; the specific `Skipped…` counter on a typed skip code).
3. Return `AutopilotConsentBatchResult` with the counters. A dispatch failure (thrown exception, e.g. DB down) propagates and fails the whole tick, same as before — Hangfire retries the job.

**Per-row algorithm** (`ConsentAutopilotPublicationCommand(Guid PublicationId, Guid UserId) : ICommand<ErrorOr<Success>>`, standard pipeline under the candidate's own `SystemContext`):

1. Reload the publication through `IAppDbContext`/`db.Query<Publication>()` `FOR UPDATE` (skip via a typed "row moved on" result if it is no longer `awaiting_consent`).
2. Five checks, in order: (a) `publications.status = 'awaiting_consent'` still holds; (b) `veto_notice_sent_at IS NOT NULL` — normally already true from UC-1a; if still null (delivery only confirmed between the two passes, or the id was missing at tick start) return the "not yet delivered" skip code, this handler never queries `IEmailDeliveryStatus` itself; (c) `series.autopilot_paused_at IS NULL`; (d) `series.current_autopilot_authorization_id = publication.autopilot_authorization_id` and that authorization has `revoked_at IS NULL`; (e) the connection is `active`. Any failure → typed skip `ErrorOr`, **no write at all** (F-2); the row stays `awaiting_consent` and is retried next tick, except the pause case which 02 owns.
3. All five pass → `publication.ConsentFromAuthorization(YouTubeOptions.ConsentTextVersion, authorizationId, now)` (06.2.1-T1's signature, arg order `textVersion, authorizationId, now`); `IPublicationNotices.QueueConsentedAsync(publicationId, userId, consentSource: "autopilot", ct)` (**YR9** — CR to 06.2.2-T1/T2: extend the existing `IPublicationNotices` port, `06.2.1-T1:265`, with this method and add the `consentSource` key + autopilot copy "approved automatically after the veto window" to the `publication_consented` template's allow-list; not assumed to exist today, tracked as [OPEN] O-15); audit `publishing.consent_autopilot` with `{ publicationId, authorizationId, consentTextVersion }`; the domain event puts `publication_updated.v1` on the outbox (→ SignalR `PublicationUpdated { publicationId, episodeId }` and 02's orchestrator). **This handler never calls `SaveChangesAsync`** — the standard `UnitOfWorkBehavior` commits consent + notice queue + audit + outbox together as this command's one transaction (D5), so a failure anywhere in step 3 rolls back only this row and leaves earlier-committed rows in the same tick untouched. **No job is enqueued directly** (YR3): the row is left `scheduled`, and 06.2.3-T2's outbox subscriber picks up the upload — this handler never calls `IJobScheduler`/`IPublishScheduler`.

Validation: none user-facing; the option values are validated at start-up by `YouTubeOptionsValidator` (06 README D11).

Authorization: system job, no HTTP principal; the job is registered in the Worker job allow-list (09.2.6) and opens a per-user `SystemContext` [INFERENCE on the allow-list type name — the task verifies it against 09.2.6].

Idempotency: `ConsentFromAuthorization` is idempotent by construction (`consent_at is null` guard → a second call returns `PublicationAlreadyConsented` and the handler treats it as a skip, not an error); safe under multiple Worker instances because each per-row `ConsentAutopilotPublicationCommand` reloads the row `FOR UPDATE` and the `xmin` concurrency token decides the winner if two instances race on the same candidate (O-9) — the orchestrator's claim itself holds no lock.

Error codes: none returned to a client. A whole-batch failure (DB down) throws so Hangfire retries.

Audit: `publishing.consent_autopilot` per consented row, ids only (F-14).

### UC-2 — Owner edits inside the veto window (06.2.2-T3)

`EditPublicationCommand(Guid PublicationId, string Title, string Description, IReadOnlyList<string> Tags, string Privacy, DateTimeOffset? PublishAt, string? ConsentTextVersion) : IRequest<ErrorOr<PublicationDto>>` — `ConsentTextVersion` added under **YR9** (TL ruling 2026-09-25).
`src/NightTales.Application/Features/Publishing/Commands/EditPublication/{EditPublicationCommand.cs,EditPublicationHandler.cs,EditPublicationValidator.cs}`

1. Load the publication through the owner-scoped read (`WHERE id = @id AND user_id = @currentUser` → a miss is a uniform **404**, never 403).
2. Status guard: `awaiting_consent`, or `scheduled` with `external_post_id IS NULL`. Otherwise 409 `publication_invalid_state` (§5, O-5).
3. Metadata validation with the 06.2.1-T1 rules (`YouTubeMetadataRules`): title 1–100 chars, no `<`/`>`; description ≤ 5,000 UTF-8 bytes after the attribution; tags per G4 ≤ 500 counted with commas and implicit quotes and ≤ 15 × 30 chars; `privacy ∈ {public, unlisted, private}`; `publishAt ≥ now + PublishLeadMinutes` or null. Validation always runs against the **requested** value, whether or not it will be the effective one (step 4a). Failures → 400 `validation_failed` with an `errors` map keyed `title|description|tags|privacy|publishAt`.
4. **Pre-audit lock (F-21, permanent freeze confirmed 2026-09-25):** a `preAudit`-locked row sits in `awaiting_consent`/`scheduled` for its **entire** pre-upload life and `publications.privacy` is already the enforced `"private"` from create — and never gets corrected back, even after a later audit approval (only the *next* run publishes). So:
   - 4a. If `publication.PreAudit && metadata.Privacy != "private"` → the **whole edit is refused**, 409 `publication_privacy_locked` (O-2), body copy along the lines of "this video is permanently locked private due to the pending audit; cancel it and let the next run publish instead" — accepting the PATCH into a key that will never take effect would mislead the owner, so this task never writes `requestedPrivacy`.
   - 4b. If `publication.PreAudit && metadata.Privacy == "private"` (the owner resubmitted the current effective value, or never touched it), the edit proceeds normally for every field; `Privacy` is a no-op.
   - 4c. If `publication.PreAudit` is false, `Privacy` writes the `privacy` column directly, unchanged from before.
5. Veto-window guard I5 (YR1 at edit time): recompute `min(now + veto_window_hours, (publishAt ?? now) − PublishLeadMinutes) − now ≥ MinVetoWindowHours`; otherwise 400 `validation_failed` field `publishAt`.
6. Moderation re-run: `IContentModerator.CheckAsync([title, description, ...tags], ModerationStage.AutopilotGate, ct)` (02.4.1-T1's port, used by `02.8.2-T2:60`) — **not** `IAutoApprovalGate`, which has no publication method and explicitly excludes publication/consent from its scope (`02.8.2/plan.md:15`); a blocking verdict → 400 `validation_failed`, `errors.moderation` = the blocking categories (O-11).
7. **Consent-by-edit gate (YR9):** `consents = publication.Status == awaiting_consent && command.ConsentTextVersion == YouTubeOptions.ConsentTextVersion` (exact match, not merely present). When `command.ConsentTextVersion` is absent or does not match the current version, the edit still applies in full (metadata, privacy-lock rule) but the row **stays** `awaiting_consent` — no error, no partial rejection, just no consent written. `ApplyOwnerEdit(metadata, consentTextVersion, now, implicitConsent: consents)` — the method itself performs the 4a/4b/4c privacy decision, since `PreAudit` is the aggregate's own state.
8. When `consents` is true: `IPublicationNotices.QueueConsentedAsync(publicationId, userId, consentSource: "user", ct)` (**YR9**, same port/CR as UC-1b step 5, [OPEN] O-15 — not a raw `IEmailSender` call). `UnitOfWorkBehaviour` commits; `PublicationEdited` → outbox `publication_updated.v1`; audit `publishing.publication_updated` for **every** edit (**YR21**, closes O-1 — the same action name regardless of whether the edit also wrote consent).
9. Response `200` with the fresh `PublicationDto` and `ETag: "pu-<xmin>"`; a lost `xmin` race after the `If-Match` check → 409 `concurrency_conflict`.

Idempotency: the same body with the same `If-Match` twice → the second attempt fails the ETag compare (412), which is the correct answer for a stale editor; an identical body with a fresh ETag is a no-op write and returns 200.

### UC-3 — Approve now from the veto notice (06.2.2-T4)

1. The notice e-mail (06.2.2-T2) contains an "Approve now" button whose target comes from `AuthLinks.PublicationApprove(Uri origin, Guid publicationId, Guid episodeId)` (`Application/Features/Auth/Password/AuthLinks.cs`, added under 06 CR-6, mirroring `PasswordReset` at `09.3.3-T1:76`): `$"{origin.GetLeftPart(UriPartial.Authority)}/publications/approve#p={publicationId:D}&e={episodeId:D}"`, origin = the 09-owned `App:PublicOrigin` (`09.2.6-T3:109`) — ids in the fragment (never in the path or query), so the link never leaks into a `Referer`, and no token is needed because the page requires a signed-in owner (D7's precedent for the cancel page).
2. The approve page (route contract handed to 10 wave 2 / 10.4.3-T2, O-3) requires a session; it resolves the publication from `episodeId` (`GET /episodes/{id}/publications`, D6), verifies the row is `awaiting_consent`, and calls `POST /publications/{id}/consent { consentTextVersion }` with `If-Match` from the fetched row.
3. The API applies 06.2.2-T1's rules: text version must equal `YouTubeOptions.ConsentTextVersion`, otherwise 400 `consent_text_outdated`; success → `consent_source = user`, audit `publishing.consented`, e-mail `publication_consented`, outbox `publication_updated.v1`.
4. A repeat tap (already `scheduled` with the same text version and `consent_source = user`) → **200 no-op**, so a double tap or a mail-client prefetch never errors.
5. **Fallback semantics (O3).** The approve path is independent of `series.current_autopilot_authorization_id`: when the audit rejects series-level authorization, or the authorization is revoked, the per-video notice + approve tap still works (the job in UC-1 skips, the owner still consents). Test T4-AC3 proves exactly this.

## 5. API and events

### 5.1 Routes

| Method | Route | Task | Contract |
|---|---|---|---|
| `PATCH` | `/api/v1/publications/{id}` | 06.2.2-T3 | body `{ title, description, tags[], privacy, publishAt }`; `If-Match` **required**; 200 `PublicationDto` + `ETag` |
| `POST` | `/api/v1/publications/{id}/consent` | 06.2.2-T1 (T4 consumes) | body `{ consentTextVersion }`; `If-Match` required |
| `GET` | `/api/v1/episodes/{id}/publications` | 06.2.1-T1 (T4 consumes) | `{ items }`, each with `etag`, `vetoEndsAt`, `consentAt`, `status` |

Endpoint file: `src/NightTales.Api/Endpoints/Publishing/PublicationEndpoints.cs` (the group created by 06.2.1-T1); pipeline: `RequireFeature("youtube_publish")` → `RequireAuthorization()` → rate policy `auth` → `RequireIfMatch()` (F-5) → the response ETag from `ETags.Publication(xmin)` (`"pu-<xmin>"`, F-4).

### 5.2 Status codes and error codes (`PATCH`)

| Status | Code | When |
|---|---|---|
| 200 | — | applied; response carries the new `ETag` |
| 400 | `validation_failed` | metadata rule failure, or the veto-window guard I5; `errors` keyed by field |
| 400 | `consent_text_outdated` | only on consent (06.2.2-T1) — listed here because the veto notice and the approve page share the flow |
| 404 | `not_found` | another user's id or no such id (uniform, no existence leak); also `404 feature_disabled` when the flag is off |
| 409 | `publication_invalid_state` | status not editable (`uploading`/`processing`/`published`/`failed`/`cancelled`) or `external_post_id` set |
| 409 | `publication_privacy_locked` | `PreAudit` is true and the requested `Privacy` is not `"private"` — the whole edit is refused (O-2) |
| 409 | `concurrency_conflict` | `xmin` race after the `If-Match` check (F-5) |
| 412 | `publication_conflict` | stale `If-Match` → the UI opens `ConflictDialog` (F-16) |
| 428 | `if_match_required` | no `If-Match` header (F-5) |

### 5.3 Events

| Event | Payload | Consumers |
|---|---|---|
| outbox `publication_updated.v1` | `{ publicationId, episodeId, userId, status, errorCode? }` (F-13) | SignalR `PublicationUpdated` ids `{ publicationId, episodeId }`, no `RealtimeData` (WT54); 02's orchestrator; 10.4.3-T2 refreshes the run log / veto row |
| SignalR `PublicationUpdated` | ids only | web (10.4.3-T2, 10.2.4-T1 hub) |

No new event is introduced by this folder: the job and the PATCH both reuse `publication_updated.v1`. The veto notice itself is queued by 06.2.2-T2 through `IEmailSender.QueueAsync` and produces no outbox event of its own.

## 6. UI

| Consumer | What it renders from this folder's contract |
|---|---|
| `10.4.3-T2` `VetoList` + `publication.http-repository.ts` + `VetoListStore` | row edit form (title/description/privacy/publish_at) → `PATCH` with `If-Match`; "Approve now" → `POST …/consent`; `412` → `ConflictDialog`; countdown from `vetoEndsAt` (`vetoRemainingMs`); a row disappears when this job consents (realtime `PublicationUpdated` → refresh) |
| `02.8.3-T3` | the publication status this job writes (`scheduled` before upload) is the run outcome input (02 CR-4) |
| `10.7.2` (templates) | `publication_veto_notice` copy; 06.2.2-T4 adds the `{{approve_url}}` key + the `email.publication_veto_notice.approveNow` copy key |
| `10.7.1` | notification list entry for the requested kind `PublicationVetoNotice` → the same link from `AuthLinks.PublicationApprove` |
| `10.4.3-T2` (veto-list Edit dialog, `10.4-series-autopilot/plan.md` U16) | **client contract for the privacy lock (F-21, O-2, TL-requested 2026-09-25):** the Edit dialog already has `PublicationDto.PreAudit`/`RequestedPrivacy` from the same fetch it uses to build the form (06 CR-10). Primary UX: when `PreAudit` is true, the dialog disables the privacy control and shows a locked-copy note instead of posting a doomed change. Defense in depth: if a client bypasses that (stale form, race with another tab), the `PATCH` still answers 409 `publication_privacy_locked`; 10.4.3-T2 maps it to a copy key (e.g. `publication.edit.privacyLocked`) the same way it already maps `publication_invalid_state`/`publication_conflict`, not a generic error toast |
| approve page (10 wave 2) | O-3 CR: consume `#p=&e=` and call `POST …/consent` with the fetched `If-Match` |

Copy keys handed over (no key is owned by this folder): `autopilot.veto.approveNow` (existing, 10.4.3-T2), plus new keys requested from 10.7.1/10.7.2: `email.publication_veto_notice.approveNow`, `publication.approve.done`, `publication.approve.expired` (the last two for the approve page, O-3). Server-side strings are none: the API returns codes only.

## 7. Jobs and integrations

### 7.1 `AutopilotConsentJob` (06.2.2-T3)

Job body in Application (F-22): `src/NightTales.Application/Features/Publishing/Jobs/AutopilotConsentJob.cs`. One recurring job runs **two commands per tick, in order** (F-2a): the notice-stamp pass must go first so a just-delivered notice is stamped before the due-consent pass ever reads it.

```csharp
[JobPolicy(Queue = JobQueue.Publish, Retries = 3)]   // "publish" per D8; JobQueue member name is 09's
public sealed class AutopilotConsentJob(ISender sender)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await sender.Send(new StampDueNoticeDeliveryCommand(), ct);          // UC-1a, no veto_ends_at predicate
        await sender.Send(new ConsentDueAutopilotPublicationsCommand(), ct); // UC-1b, existing due-consent claim
    }
}
```

Registration, flag-gated as 02.8.3-T1 (D1): `src/NightTales.Worker/RecurringJobs/AutopilotConsentJobRegistration.cs`, `IJobScheduler.AddOrUpdateRecurring<AutopilotConsentJob>("autopilot-consent", "*/1 * * * *", j => j.RunAsync(CancellationToken.None))` — **[INFERENCE]** on `IJobScheduler`'s exact generic overload; 02.8.3-T1:98 quotes a static-looking `AutopilotPlanJob.RunAsync()` call, which this task treats as shorthand rather than copying literally, because a `static` method cannot close over a primary-constructor-injected `ISender` in valid C#.

| Property | Value | Rationale |
|---|---|---|
| Schedule | every minute | F-1 |
| Queue | `publish` | D8; keeps consent and upload in one lane so consent→upload ordering holds |
| Retries | Hangfire default automatic retry (10 attempts, exponential) on infrastructure faults only; **no** retry for a business skip | a skip is a normal outcome (F-2); retrying it would just re-read |
| Timeout | 55 s soft guard (`DisableConcurrentExecution`); the batch is ≤ 50 rows | a tick must never overlap the next one |
| Concurrency | single instance per tick; the orchestrator's claim holds no lock, but each per-row command reloads `FOR UPDATE` so two Worker replicas racing the same candidate resolve via the `xmin` token, not `SKIP LOCKED` (O-9) | D8/D18 |
| External calls | `IEmailDeliveryStatus.GetAsync` only in the UC-1a pass, only for rows with a present notice id and `veto_notice_sent_at IS NULL`; UC-1b makes 3 bulk precondition queries per batch and no delivery-status call of its own; no Google call in either pass | D8, F-2a; §3.4 |
| Metrics | **none of its own**: `11.6-operations/plan.md:71` freezes the autopilot metric set and 11.6.2-T1's rule count, so nothing new is published; skip reasons are a structured log field, and 02's trigger 11 (`_VetoNoticeUndelivered`, `02.8.3-T4:168`) surfaces the undelivered case through `autopilot_pause_total{reason}` | no new metric, no new alert rule (O-4) |

### 7.2 Failure map (job)

| Case | Behaviour |
|---|---|
| Notice status `queued`/`sending`/`failed`/`bounced` | skip, no write, counter `notice_undelivered`; retried next tick, so a slow delivery resolves itself |
| `platform_payload->>'vetoNoticeEmailId'` missing (06.2.2-T2 never ran) | UC-1a has nothing to claim (its predicate requires the id to be present); UC-1b's guard also skips; the row stays `awaiting_consent` and 02's trigger 11 (`_VetoNoticeUndelivered`, 30 min after `created_at`) pauses the series — the test row `06.2.2-T3 \| observed only` (`02.8.3-T4:456`) means this job never cancels or expires such a row, it only ever stamps or skips (O-4) |
| Series paused | skip, no write (F-1, 02 CR-14); 02's resume path re-enables the tick |
| Authorization revoked/superseded | skip with `authorization`; 02.8.3-T2's revocation already moved the run to `awaiting_authorization` |
| Connection not `active` (`expired`/`revoked`) | skip with `connection`; the publication waits for a reconnect (06.4.1-T1 cancels on disconnect, D15) |
| Open `platform_incidents` row for the client | the job is unaffected (no Google call); it may still record consent — the *upload* is what holds (D14) |
| Concurrency: the owner PATCHed or consented in the same second | the per-row `FOR UPDATE` re-read sees the new status → skip, no double consent |
| A notice delivered between UC-1a and UC-1b in the same tick | UC-1b's own guard (`veto_notice_sent_at IS NOT NULL`) still catches an already-stamped row; a row that became `Sent` mid-tick simply waits one more minute — never a false pause, because UC-1a runs every tick and 30 min is 30 ticks |
| Database unavailable | throw → Hangfire retry; nothing was written because the whole batch is one transaction per user |
| More than 50 rows due | the rest is picked up next minute (bounded work per tick, D18) |

### 7.3 Integrations reused (no second mechanism)

| Port | Owner | Use here |
|---|---|---|
| `IEmailDeliveryStatus.GetAsync(Guid emailMessageId, CancellationToken ct)` under `ISystemContext.For(userId)` | 09.2.6-T3 | notice delivery → `veto_notice_sent_at` (F-12) |
| `IPublicationNotices.QueueConsentedAsync` (new method, [OPEN] O-15) | 06.2.2-T1/T2 (CR) | **YR9**: this task calls it after both UC-1b's autopilot consent and UC-2's consent-by-edit; `IEmailSender` is never called directly from this folder (06.2.2-T2 still separately owns the veto-notice queue on the same `IPublicationNotices` port) |
| `IOutbox` (via domain events + 09.1.2 interceptor) | 09 | `publication_updated.v1` |
| `IRealtimeNotifier` | 09.8.1-T1 | `PublicationUpdated` ids only |
| `[Audited]` behaviour | 09.3.6 | audit rows |
| `RequireFeature` | 09.9.2-T2 | flag on both routes and on job registration |
| `IContentModerator` (02.4.1-T1) | 02.4.1-T1 | moderation re-run on edit (`ModerationStage.AutopilotGate`) — **no** local copy of the gates |
| `IClock` | Domain/Common | `now` everywhere; tests use `FakeClock` |

### 7.4 Approve-now integration (06.2.2-T4)

Approval link: `AuthLinks.PublicationApprove(Uri origin, Guid publicationId, Guid episodeId)` in `src/NightTales.Application/Features/Auth/Password/AuthLinks.cs` (added under the already-open 06 CR-6, mirroring `AuthLinks.PasswordReset` at `09.3.3-T1:76`): `$"{origin.GetLeftPart(UriPartial.Authority)}/publications/approve#p={publicationId:D}&e={episodeId:D}"`. The origin is the 09-owned `App:PublicOrigin`, already supplied to minters at send time (`09.2.6-T3:109`, WT17) — no 06-local key and no new DI port. The notice catalog gains one key, `{{approve_url}}`, filled at send time by 06.2.2-T2's minter together with the cancel link; because it is token-free it never enters `email_messages.params` (WT30) while `EmailTemplateProvider.ValidateOrThrow()` (`09.2.6-T3:95`) still guarantees the key and all four locale files exist.

## 8. Security and privacy

| Requirement | How |
|---|---|
| Feature flag | `RequireFeature("youtube_publish")` on `PATCH`/consent; the job registers only while the flag is on (D1) |
| Owner-only | the publication load uses `WHERE id = @id AND user_id = @currentUser`; a foreign or absent id → uniform **404** (never 403, no existence leak); no admin path |
| CSRF/state | `PATCH`/consent are bearer-authenticated with `If-Match`; the approve link carries **no** token (a token would be a second, unmanaged capability — the cancel link is the only capability URL, D7); ids live in the fragment so they never reach a server log |
| Idempotency + ETag | `If-Match` mandatory (428), stale → 412, `xmin` race → 409 `concurrency_conflict`; consent repeat → 200 no-op |
| No secret material | this folder never touches `access_token_enc`/`refresh_token_enc`, the upload session URI or the OAuth client secret; `PATCH` cannot change connection or token fields |
| Tenant-isolated audit | `publishing.consented`, `publishing.consent_autopilot` and the edit action store ids and codes only (F-14); never title/description/tags/channel names |
| Logs | structured, ids only: `PublicationId`, `UserId`, `EpisodeId`, `AuthorizationId`, `Status`, skip `Reason`. Titles, descriptions, e-mail addresses and tokens are never logged (D17); the veto notice body is not logged either |
| Rate limiting | `auth` policy on both routes (D8/S class of writes); the job is internal |
| Data minimisation | the job reads 7 columns + `platform_payload->>'vetoNoticeEmailId'`; the approve link exposes only two UUIDs |
| Retention/duties | the job writes no new personal data; deleting it is 06.4.3-T1's `IDeletionScopeHandler` for scope `channel` (D15) |

## 9. Test plan

Projects (F-18): `tests/NightTales.Domain.UnitTests`, `tests/NightTales.Application.UnitTests`, `tests/NightTales.Api.IntegrationTests`, `tests/NightTales.Architecture.Tests`; fakes/fixtures in `tests/NightTales.TestKit`. xunit.v3 + Shouldly, integration on real PostgreSQL 17 via `PostgresFixture`, `FakeClock` for every time-dependent rule, `FakeGoogleServer` where Google is involved (not in this folder: the job makes no Google call). **No mocking library.**

| Layer | Area | Cases (ids referenced by the task files) |
|---|---|---|
| Domain.UnitTests | `Publication` | consent guards (notice missing, already consented, wrong status), `ApplyOwnerEdit` implicit vs explicit consent, `veto_ends_at` immutability, edit refused when `external_post_id` set |
| Application.UnitTests | `ConsentDueAutopilotPublicationsHandler` | only delivered notices consent; paused series skip; revoked/superseded authorization skip; non-active connection skip; batch size honoured; per-user `SystemContext` applied; skip never writes; idempotent second run |
| Application.UnitTests | `EditPublicationHandler` | status allow-list, field validation map, veto-window guard I5 (YR1 at edit time), moderation-gate rejection, implicit consent fields, `publication_updated.v1` emitted once |
| Api.IntegrationTests | HTTP | `PATCH` 200 + new ETag; 428 without `If-Match`; 412 with a stale one; 409 `publication_invalid_state` in `uploading`; 404 for another user's id; 404 `feature_disabled` with the flag off; consent repeat 200; approve link flow end-to-end (GET episode publications → consent) |
| Api.IntegrationTests | job in-process | the job runs with real PostgreSQL rows (jobs run in-process per 09 D2): a due row with a delivered notice → `scheduled` + `consent_at` + audit row + one outbox row; the same row with an undelivered notice → unchanged |
| Architecture.Tests | layering | Domain free of EF/HTTP; no Infrastructure type referenced from Application; the job only through `ISender` |
| Manual (staging, 06-publish gate) | — | an autopilot publication reaches `scheduled` after the window; a PATCH inside the window persists and consents; the approve link from a real mail client lands on the row and consents |

Determinism: the PT-day/DST rules of 06.3.1-T1 are out of scope here; every test pins `now` with `FakeClock`. Integration tests isolate one schema per test class through `PostgresFixture`, and no test depends on wall-clock minutes (the job is invoked directly, never via its cron).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.2.2-T3 | `AutopilotConsentJob` + `PATCH /publications/{id}` with re-validation | M (1.0) | 06.2.2-T2, 02.8.3-T1, 02.8.3-T2 | 06.2.2-T4, 02.8.3-T3, 10.4.3-T2 (soft), 08.4.2-T1 (08 CR-5) |
| 06.2.2-T4 | "Approve now" one-tap consent from the notice (fallback path) | XS (0.25) | 06.2.2-T3 | 06.5.1-T1, 10.7.4-T2 (10 CR-15) |

Phase and flag for both rows: `P · M1 · flag youtube_publish` (equals `../README.md` §8.1).

Folder total 1.25 d, which matches `../README.md` §9 (+0.5 for 06.2.2-T3: PATCH re-validation and the authorization checks). `../README.md` §9 counted 06.2.2-T4 inside 06.2 publishing (8.5 d for the whole epic); this folder does not change the epic total.

### [OPEN] items

| # | Item | Default until answered | Owner |
|---|---|---|---|
| O-1 | *(closed by YR21, 2026-09-25)* audit action for every metadata edit | `publishing.publication_updated`, for every edit regardless of whether it also wrote consent (recorded in 06.5.1-T1's evidence list, 06 CR) | closed |
| O-2 | Three new publication error codes needed in 09 §5.4 (the catalogue has `episode_conflict`, `story_version_conflict`, … but no publication rows) | one CR (06 CR-2) covering all three, one vocabulary with 06.2.1-T1 (final, 2026-09-25): **412 `publication_conflict`** (mirrors `episode_conflict`, 09 README:360), **409 `publication_invalid_state`** (mirrors `episode_invalid_state`, 09.1.4-T1:30), **409 `publication_privacy_locked`** (PreAudit permanently freezes `privacy`, F-21) — missing header stays 428 `if_match_required` | TL / 09 |
| O-3 | Page route for the approve-now deep link (`/publications/approve#p=&e=`) and its two copy keys — the origin needs no decision, it is `App:PublicOrigin` ✅ (`09.2.6-T3:109`) | T4 ships the link + the catalog key; the page is plan 10 wave 2's, and the veto row of 10.4.3-T2 already performs the same action | TL, 10 wave 2 |
| O-4 | Max hold for a publication whose notice was never delivered | **no hold, no cancel, no expiry**: UC-1a keeps trying every tick and 02's trigger 11 (`_VetoNoticeUndelivered`, 30 min after `created_at`) owns the pause — 02.8.3-T4:456 marks 06.2.2-T3 `observed only`, so a 06-side cancel would mask that trigger. Only if the TL wants a valve beyond 02's pause does a key (e.g. `YouTube:Veto:MaxHoldDays`) come back | TL, 02.8.3-T4 |
| O-5 | 409 code for a non-editable status or a duplicate active publication (the catalogue has `publication_consent_required` but no invalid-state code) | resolved by O-2's vocabulary: `publication_invalid_state` 409 | TL / 09 |
| O-6 | Add `ix_publications_veto_scan` to D18's index list and to 00 §6 | T3 ships the index in its own expand-only migration | TL, 06 README D18/D20 |
| O-7 | Rate policy for `PATCH /publications/{id}` (`auth` vs `paid`) | `auth` | TL, 09 |
| O-8 | Confirm the frozen-`veto_ends_at` corollary of YR1 (an edit may not shrink the remaining window below `MinVetoWindowHours`; the window never moves) | T3 enforces I5 as written | TL (YR1 follow-up) |
| O-14 | *(closed by YR20, 2026-09-25)* competing claimants for `platform_payload.preAudit`/`requestedPrivacy` | single writer ruled: **06.2.1-T1** (create time); 06.2.4-T1's "seeded the first time this planner runs" claim is superseded. This task still writes neither key | closed |
| O-15 | `IPublicationNotices` (06.2.2-T1/T2-owned, `06.2.1-T1:265`) has only the veto-notice call today; UC-1b and UC-2 need `QueueConsentedAsync(publicationId, userId, consentSource, ct)` plus a `consentSource` key + autopilot copy on the `publication_consented` template's allow-list | CR to 06.2.2-T1/T2 to add the method + the template key; this task assumes neither exists yet and does not call `IEmailSender` directly | TL, 06.2.2-T1, 06.2.2-T2 |
| O-9 | D8 says the batch claim uses `FOR UPDATE SKIP LOCKED`, but 09's pinned bypass `ISystemWorkQuery.PendingAsync<T>` (09.2.3-T1:36) takes no lock hint or ordering — **still open**: YR22 settled only the two-pass wording (O-12), not this | claim without locks; the race is closed by the per-row `FOR UPDATE` plus the `xmin` concurrency token (one writer wins, the other counts a skip). Ask the TL to amend the D8 sentence in 06 README §3 | TL, 06 README D8 |
| O-12 | *(closed by YR22, 2026-09-25)* README D8's two-pass wording | 06 README §3 now describes both passes (F-2a, UC-1a/UC-1b); this task's design matches it | closed |
| O-10 | *(resolved 2026-09-25)* no repository port and no claim port: both UC-1a/UC-1b claims use 09's `ISystemWorkQuery.PendingAsync<Publication>`, per-row work uses `IAppDbContext` | closed | — |
| O-11 | Code for an edit blocked by the `IContentModerator` verdict (no moderation-specific code exists; `IAutoApprovalGate` explicitly excludes publication/consent, `02.8.2/plan.md:15`) | default 400 `validation_failed` with `errors.moderation = <blocking categories>`; a dedicated 409/422 code only if the TL prefers one | TL / 02.8.2 |

## 11. Rollout

1. **Flag off** (default): both routes answer 404 `feature_disabled`; the job is not registered; nothing else in plan 06 changes behaviour.
2. **Staging (06-connect gate first)**: connect a channel, create an autopilot publication with `veto_window_hours = 2`, watch the notice arrive, wait for the window, confirm the job consented (`consent_at`, `consent_source = autopilot`, audit `publishing.consent_autopilot`) and the publication is `scheduled`. Then PATCH inside a second window (title + privacy) and confirm the implicit consent and the approve link from a real mail client.
3. **Migration**: `_06_PublicationsVetoScanIndex` is expand-only and runs with `CONCURRENTLY` (no lock, no data change). Rollback = `DROP INDEX CONCURRENTLY ix_publications_veto_scan` after the code that needs it is off; no data loss in either direction. `CONCURRENTLY` inside a Hangfire/startup migration bundle must be applied outside a transaction (the migration marks itself `suppressTransaction`).
4. **Guard rail**: the 06-publish gate (README §7) covers the veto path: "an autopilot publication passes the veto window and uploads" and "a forced lost-response retry creates no second video" (the latter belongs to 06.2.3-T2, listed here because the veto path feeds it).
5. **Rollback of behaviour**: disabling the flag stops the job; an already-consented publication still uploads (06.2.3-T2 is flag-gated independently), and `PATCH` returns 404, so no half-state is possible.
6. **Post-deploy check**: on a normal day no series is paused with `reason = veto_notice_undelivered` and no publication sits in `awaiting_consent` past its window; a hit is 02's trigger 11 and routes to O-4 (no 06 metric exists to watch, F-2a keeps the stamp timely so this should be rare).
