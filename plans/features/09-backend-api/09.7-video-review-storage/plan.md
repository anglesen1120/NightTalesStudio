# 09.7 — Video review (checkpoint #2), blob storage and the retention sweep (09.7.1–09.7.3)

> Parent: ../README.md (Part B)
> Refines: `../../../09-backend-api.md` §4 (epic 09.7, stories 09.7.1–09.7.3, L202–211), 00 §7/§8.
> Contract: `../README.md` — Part A **D2** (test projects), **D3**, **D5**–**D15**, **D17**, **D10** (system scope); Part B **B2** (WF03, WF15, WF16), **B3** (BD1, BD5, BD10, BD11, BD12, BD15, BD16), **B8.1**, **B10**; 11 README D14/D15/D20 H9.
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

| Story | Outcome |
|---|---|
| 09.7.1 approve/reject video | From `video_review`, `POST /episodes/{id}/video/approve` → `ready`; a reject with a reason moves the episode to `story_review` or re-renders it, per 00 §7; the reason is stored and both paths are audited |
| 09.7.2 signed URL | `IBlobStore` + `S3BlobStore` (the unowned port of WF03/W15, ruled to this task by BD10) and `GET /episodes/{id}/video-url` → `{ url, expiresAt }`, TTL 15 min, safe `Content-Disposition` (WF16), audited |
| 09.7.3 retention sweep | `RetentionSweepJob` daily: `assets.retention_until < now` → objects deleted, `assets.deleted_at` set; plus the `audit_logs` retention rule list of 09 §4 09.3.6 |

In scope: the two review commands + routes + audit + outbox events; the storage port, its S3 implementation, presigning and the storage health marker; the sweep job, its index and its audit phase.

Out of scope: render submit/poll/ingest (09.6.1-T1, 09.6.3-T1/T2), the episode read model (`GET /episodes/{id}`, 09.5.3-T1), `audit_logs` DDL, `AuditBehavior`, `IAuditData`/`IAuditWriter` and the arch test (09.3.6-T1, BD15), hard delete and account deletion (13.3.1-T1), bucket/IaC provisioning and lifecycle rules (11.4.1-T1), every screen and the player (10), `Episode.cs` ownership (09.5.1-T1).

## 2. Confirmed facts

- **W5** `AWSSDK.S3` **4.0.103.4** (v4 line), Apache-2.0; `GetPreSignedUrlRequest.ResponseHeaderOverrides.ContentDisposition` exists ✅; the client **method names are ⚠️ unverified** → pin them in 09.7.2-T1 step 1 with a compile spike.
- **W12 / N14** Hangfire recurring jobs are cron-based and polled every 15 s → `0 3 * * *` (UTC) is a safe daily cadence; a 10 s job would not be.
- **W15 / WF03** 02.5.2-T2 already calls `IBlobStore` "(09)" and no 09 task owned it; **BD10** gives the port, the S3 implementation and the video URL to 09.7.2-T1 (M), and 09.6.3-T2 also needs it. **BD1** lists the consumers: 02.5.2-T2, 09.6.3-T2, 09.7.3-T1, 13.3.1-T1.
- **BD10** `IBlobStore` (Application/Abstractions/Storage) with `PutAsync`, `OpenReadAsync`, `DeleteAsync`, `ExistsAsync`, `CreateReadUrl(bucket, key, ttl, downloadFileName) → Uri`; `S3BlobStore` on AWSSDK.S3 4.x; endpoint/credentials from `Storage:*` (shared secrets); buckets from 11 D15; **path-style for the dev emulator**; `GET /episodes/{id}/video-url` → `{ url, expiresAt }`, TTL **15 min**, audit row.
- **WF16** the video URL must not leak into logs or `Referer`; `Content-Disposition: attachment; filename*=UTF-8''<slug>.mp4` with the slug derived from the episode title, ASCII-folded, ≤ 80 chars; access writes `audit_logs` `episode.video_url_issued`.
- **00 §7 (L327)** `video_review → story_review` (reject video, fix story) **|** `video_review → rendering` (re-render); **09 §4 09.7.1 (L206–208)** "Given `video_review`, When `POST /episodes/{id}/video/approve`, Then → `ready`; reject with reason → `story_review` or re-render per 00 §7; reason stored".
- **BD5 (`../README.md`:675)** `RenderEpisodeCommand(Guid EpisodeId, string? IfMatch, string IdempotencyKey)` requires the episode in `story_approved` + an ETag, runs the linter/planner/estimator/budget guard, reserves `CreditOperation.Render`, creates the `RenderJob` + `Episode.RequestRender`, raises `render_requested.v1`; route `POST /episodes/{id}/render` → 202 + `Location: /render-jobs/{id}`.
- **09 §4 09.7.2 (L209–210)** a pre-signed GET URL valid ≤ 15 min with a `Content-Disposition` filename is returned; access is logged. **09 §4 09.7.3 (L211–212)** `RetentionSweepJob` (daily): when `retention_until < now`, objects are deleted from storage and `assets.deleted_at` is set; **default retention 30 days after `ready` [DECISION, tune in 12]**.
- **00 §6 (`assets`, L284)** `user_id, episode_id, kind (video|thumbnail|audio|subtitle|image), storage_key, content_type, size_bytes, duration_sec, sha256, retention_until, deleted_at`; files live in object storage. **D9**: `assets` is created by the `InitialCore` migration (09.2.2-T2) — no new table is needed by this story.
- **09 §4 09.3.6 (L147)** the ordered audit retention rule list, first match wins: (1) rows linked to a payment, refund or chargeback → kept as long as the payment record; (2) `account_security` rows → 24 months; (3) `actor_type = admin` or `visibility = internal` → 24 months; (4) every other `visibility = user` row → 12 months; account deletion follows 13 §5.4. The same line names "the retention job (09.7.3)" as its executor. 09.3.6-T1 owns the executable form, `Application/Audit/AuditRetentionRules.Ordered` (PaymentLinked → never; AccountSecurity → 730 d; AdminOrInternal → 730 d; else 365 d) — the sweep consumes that list instead of re-encoding the numbers (IRC from `W2EmailAudit`, 2026-09-24).
- **D10** cross-user sweeps read `(id, user_id)` pairs through `ISystemWorkQuery` and process each one under `ISystemContext.For`; `IgnoreQueryFilters(["tenant"])` is allow-listed for `RetentionSweepJob`, `DataDeletionJob`, `CreditReconciliationJob`, `SystemWorkQuery` only; other users' rows answer **404 `not_found`**.
- **D5** the pipeline opens the transaction before the handler; **D6** idempotency keys live inside it; **D7** the HTTP status comes from `ErrorCatalogue` keyed by code, `RequireIfMatch()` → 428 `if_match_required`, `RequireIdempotencyKey()` → 400 `idempotency_key_required`, `IfMatchHeader` binder → the command; **D15** episode ETag = `"ep-<episodes.xmin>"`, returned in the `ETag` header and the DTO, read after commit through the `ToHttpResult` async etag delegate; **D17** `episode_conflict` 412, `concurrency_conflict` 409, `service_unavailable` 503.
- **BD12 (`../README.md`:694)** `/health/ready` checks the DB, **storage `ExistsAsync` on a marker** and the node registry; **BD16 (`:704`)** "health endpoints read caches only" → the ready check reads a cached flag refreshed by a prober (the 11 pattern: `NodeHealthPoller` + `INodeHealthCache`), never a synchronous S3 HEAD per probe. **BD11** `IRealtimeNotifier.NotifyAsync(userId, e, ct)`; **BD14** rate-limit policies are `auth`, `paid`, `hub` (none applies to these three routes); **BD15 (`:700`)** `[Audited(action, Category, Visibility)]` is **required** on every `ICommand`, `AuditBehavior` fills `data` "from the command's `IAuditData` projection only", and `IAuditWriter.WriteAsync(AuditEntry, ct)` serves auth handlers, jobs and CLI verbs.
- **11 D15** buckets `nt-media-<env>`, `nt-public-<env>`, `nt-music-<env>`, `nt-backup-<env>` (dev: `nt-media-dev`, `nt-public-dev`, `nt-music-dev` by `tools/dev/storage-init.sh`), secrets `nts/<env>/STORAGE_ACCESS_KEY|STORAGE_SECRET_KEY`; **11 D14** the dev emulator is the `storage` compose service (`chrislusf/seaweedfs`, `server -s3`); **11 D20 H9** the application key has **no write** on `nt-public-<env>` and preview uploads use the operator key.
- **02.8.2 plan L220 / L193** consumes `ApproveVideoCommand(Guid EpisodeId, string? Note)` → `ErrorOr<Success>` and expects the outbox event `VideoRejected` from this story (plan L222); **02.8.3 plan L222** `VideoApproved` (user) drives `waiting_owner → publication_created`; plan L454 "09.7.1 video approve | `ApproveVideoCommand` | 412/409 → run `failed`"; 02.8.3-T3 L54 `VideoRejected` cancels the pending publication, and its task header L5 does **not** list 09.7.1-T1 in `Depends on` although its Inputs do → recorded here as a wave-DoR open item, the signed 02 file is not edited.
- **02.5.1 plan L269** `IExtractedTextStore` is an `IBlobStore` wrapper writing `research/{userId}/{episodeId}/{sourceId}/{sha256}.txt`; **BD7** 09.6.3-T2 writes `media/{userId}/{episodeId}/{renderJobId}/video.mp4` and `…/subtitles.srt`.
- **D2 / N11 / D13** the test projects are `tests/NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `NightTales.Infrastructure.UnitTests`, `NightTales.Architecture.Tests`, `NightTales.Api.IntegrationTests` (all DB and HTTP tests, Worker jobs in-process) and `NightTales.TestKit`; there is no S3 container in the stack (Testcontainers.PostgreSql only) → the storage contract test needs a new container ([OPEN], §9).

## 3. Domain and data

No new table. `assets` (D9) is extended by one **index-only** migration and by the resolution rules below.

**Aggregate additions.** `Episode.cs` is owned by 09.5.1-T1; this story adds a partial file `src/NightTales.Domain/Episodes/Episode.VideoReview.cs` [INFERENCE: partial-file name; 02 already extends `Episode` in partial files, D16].

```csharp
public enum RejectTarget { Story, ReRender }              // wire values story | re_render [RULED — README §B10 WT3]

public partial class Episode
{
    public DateTimeOffset? ReadyAt { get; private set; }
    public string? VideoRejectReason { get; private set; }      // ≤ 500 chars, safe text, never story text

    public ErrorOr<Success> ApproveVideo(DateTimeOffset now);                        // VideoReview → Ready, ReadyAt = now
    public ErrorOr<Success> RejectVideo(string reason, RejectTarget target, DateTimeOffset now);
    //                              video_review → story_review (target `story`) | story_approved (target `re_render`)
    //                              [RULED — README §B10 WT3]
}
```

Both return `episode_invalid_state` (409) from any state but `VideoReview`. Domain methods take `now` (D3), never the clock.

**Assets.** The `assets` entity is currently owned by 09.2.2-T2's `InitialCore` (D9); the approve and sweep paths need two small members [INFERENCE, [OPEN]]:

```csharp
public void ScheduleRetention(DateTimeOffset retentionUntil);   // re-stamps: overwrites the ingest's unapproved value (09.7.1-T1, WT56)
public void MarkDeleted(DateTimeOffset now);                    // idempotent (09.7.3-T1)
```

Checked against the landed siblings on 2026-09-24: 09.6.3-T2 writes "`assets` rows (00 §6 L288)" through "an `assets` writer" and names **no** `Asset` type, and it records that the table has no `render_job_id` column (its idempotency key is `storage_key + sha256`); 09.6.1-T1 never touches `assets`. So the entity type itself is still unowned — whichever task creates it (`09.2.2-T1`'s configurations or 09.6.3-T2 as first writer) must host these two methods; the [OPEN] below records it. 09.6.3-T2 also stamps `retention_until = now + Assets:UnapprovedRetentionDays` in its final transaction (WT56), so the approve path here **re-stamps** it rather than being the only writer.

`retention_until` is stamped **twice** ([RULED — README §B10 WT56]): the **ingest** (09.6.3-T2) stamps `now + Assets:UnapprovedRetentionDays` (30) on every live asset of the render — the "abandoned render" window — and **approve** (09.7.1-T1) **re-stamps** the same rows `now + Assets:RetentionDays` (30). The ingest stamp is in 09.6.3-T2's final transaction; this settles the former [OPEN] "who writes `retention_until`" (the earlier reading — stamping only "30 days after `ready`" — left every un-reviewed render's assets unsweepable). Assets without `retention_until` (e.g. `voice-previews/`, `style-previews/` objects, 11 D20 H9) are never swept; `ScheduleRetention` overwrites rather than skipping an existing value.

**EF configuration** (`Persistence/Configurations/AssetConfiguration.cs`, owned with the entity), index added by 09.7.3-T1:

```csharp
builder.HasIndex(a => a.RetentionUntil)
       .HasFilter("deleted_at IS NULL AND retention_until IS NOT NULL")
       .HasDatabaseName("ix_assets_retention_due");           // partial, keeps the sweep index-only
builder.Property(a => a.StorageKey).HasMaxLength(512);
builder.Property(a => a.Sha256).HasMaxLength(64);
```

The audit sweep needs its own index on the `audit_logs` keyset: `audit_logs (created_at, id)` ([RULED — README §B10 WT47]), created by 09.7.3-T1's migration (`ix_audit_logs_created_id`) — it is what makes `WHERE (created_at, id) > (…) ORDER BY created_at, id` an index-only keyset scan; `audit_logs` DDL itself stays 09.3.6-T1's.

**Bucket resolution** [INFERENCE, BD10 leaves the bucket to the caller]: `StorageBuckets.Media` for every `assets` row (09.6.3-T2's `media/…` keys), `StorageBuckets.Public` for published previews, `StorageBuckets.Music` for 05. A single helper `BlobLocator.BucketFor(AssetKind kind) → string` keeps the mapping in one place so 13.3.1-T1 can reuse it.

**Storage options** (`Storage:*`, values are secret-manager references per 11 D15):

| Key | Default | Note |
|---|---|---|
| `Storage:ServiceUrl` | dev `http://storage:8333` | SeaweedFS s3 endpoint |
| `Storage:Region` | `us-east-1` | ignored by the dev emulator |
| `Storage:ForcePathStyle` | `true` in Development, `false` otherwise | BD10 |
| `Storage:AccessKey` / `Storage:SecretKey` | — | secrets `nts/<env>/STORAGE_ACCESS_KEY|STORAGE_SECRET_KEY`; never logged |
| `Storage:Buckets:Media|Public|Music` | `nt-media-<env>` … | 11 D15 |
| `Storage:PresignTtlMinutes` | `15` | BD10/WF16; validated ≤ 15 |
| `Storage:HealthMarkerKey` | `_health/storage-ready.txt` | [INFERENCE] BD12 marker; written once at start into the media bucket because the app key has no write on `nt-public-<env>` (11 D20 H9) |
| `Storage:HealthProbeSeconds` | `30` | [INFERENCE] prober interval; `/health/ready` reads the cache, never storage (BD16) |
| `Assets:RetentionDays` | `30` | approved retention: approve re-stamps `now + this` |
| `Assets:UnapprovedRetentionDays` | `30` | ingest stamp for un-reviewed renders (WT56), read by 09.6.3-T2 |
| `RetentionSweep:BatchSize` | `500` | §7 |
| `RetentionSweep:MaxBatches` | `20` | §7 — guarantees a bounded run when a whole batch keeps failing |

## 4. Use cases

| # | Actor | Use case | Preconditions | Result |
|---|---|---|---|---|
| UC1 | owner (10) | approve the video | authentication, if-match matches, episode `video_review`, a final video asset exists | `ready`, `ReadyAt` set, retention **re-stamped** `now + Assets:RetentionDays` (WT56), `video_approved.v1`, `episode.video_approved` audit, `EpisodeUpdated` |
| UC2 | owner (10) | reject → fix the story | as UC1 | `story_review`, reason stored, `video_rejected.v1` (`target = story`), audit, `EpisodeUpdated` |
| UC3 | owner (10) | reject → re-render | as UC1 + `Idempotency-Key` | rejection recorded, episode `story_approved`, `video_rejected.v1` (`target = re_render`); the Worker's outbox handler starts a new render → `rendering` + a new `RenderJob` (route answers 202) |
| UC4 | autopilot (02.8.2-T1) | approve after the video gate passes | system scope on the run's user, episode `video_review` | as UC1 with `Note = "autopilot"`; the gate writes its extra `autopilot.video_auto_approved` row itself |
| UC5 | owner (10) | watch or download the video | episode readable, `final_video_asset_id` set, asset live | `{ url, expiresAt }`, 15 min, `Content-Disposition` attachment, `episode.video_url_issued` audit |
| UC6 | platform (job) | delete expired files | none (scheduled) | objects deleted, `assets.deleted_at` set, counters emitted |
| UC7 | platform (job) | expire audit rows | none (scheduled) | `audit_logs` rows past their rule window deleted by **keyset** over the `(created_at, id)` index (WT47), counters emitted |

## 5. API and events

```
POST /api/v1/episodes/{id}/video/approve      If-Match: "ep-<xmin>"   body { "note": "…" }       → 200
POST /api/v1/episodes/{id}/video/reject       If-Match, Idempotency-Key  body { "reason", "target" } → 200 | 202
GET  /api/v1/episodes/{id}/video-url                                                             → 200
```

```csharp
// Application/Features/VideoReview/Commands/
[Audited("episode.video_approved", Category.Episodes, Visibility.User)]
public sealed record ApproveVideoCommand(Guid EpisodeId, string? Note, string? IfMatch = null)
    : ICommand<ErrorOr<Success>>, IAuditData;                         // pinned name/signature: BD1, 02.8.2 plan L220

[Audited("episode.video_rejected", Category.Episodes, Visibility.User)]
public sealed record RejectVideoCommand(Guid EpisodeId, string Reason, RejectTarget Target,
                                        string? IfMatch = null, string? IdempotencyKey = null)
    : ICommand<ErrorOr<VideoReviewResult>>, IIdempotentCommand, IAuditData;   // D4/D6

public sealed record VideoReviewResult(Guid EpisodeId, string Status, RejectTarget? Target);

public sealed class ApproveVideoHandler : ICommandHandler<ApproveVideoCommand, ErrorOr<Success>>;   // sealed, D4
public sealed class RejectVideoHandler  : ICommandHandler<RejectVideoCommand, ErrorOr<VideoReviewResult>>;
```

`IAuditData` is the BD15 mechanism (`../README.md`:700: `AuditBehavior` takes `data` "from the command's `IAuditData` projection only") — both commands implement it, and the projection returns ids, the target and the status **only**: never `Reason`, never `Note`, never a key or a URL. The member/record names are 09.3.6-T1's ([INFERENCE], Open items); the shape used here:

```csharp
public AuditData Project() => new(EntityType: "episode", EntityId: EpisodeId,
    Data: new { episodeId, seriesId, target = "story", status = "story_review", reasonPresent = true });   // WT3: target wire values are `story` | `re_render`
```

`string? IfMatch = null` is an **optional third parameter** on the pinned `ApproveVideoCommand` so that 02.8.2-T1's in-process `ApproveVideoCommand(episodeId, note: "autopilot")` keeps compiling while D7's "`IfMatchHeader` binder → passed into the command" still holds for HTTP callers ([INFERENCE] + Open item). A null value means "caller is not HTTP" and skips only the stale-ETag check; the state check still applies.

Handler sequence (both commands): load the episode through `IAppDbContext` (tenant filter, D10) → `not_found` when absent or not the caller's → status must be `VideoReview` else `episode_invalid_state` → when `IfMatch is not null` compare it with `"ep-" + episode.Version` (strong, exact, D15) else 412 `episode_conflict` → domain call → approve: `ScheduleRetention(now + Assets:RetentionDays)` on the episode's live assets → raise the domain event (D11 outbox in the same `SaveChanges`) → return. UnitOfWork commits (D5); the endpoint reads the fresh ETag through `ToHttpResult`'s async etag delegate (D15). No handler calls `SaveChanges`, opens a transaction or sends another command.

Status codes and codes: 200 (approve; reject → `story_review`), 202 with **no** `Location` (reject → re-render: the render job does not exist yet, see [DECISION L1]), 404 `not_found` (unknown, foreign, or no final video asset), 409 `episode_invalid_state` (not `video_review`), 409 `concurrency_conflict` (an `xmin` race the ETag did not cover, D5), 412 `episode_conflict` (stale `If-Match`), 428 `if_match_required`, 400 `validation_failed` (`reason` 1–500 chars, `target` enum, `note` ≤ 500), 400 `idempotency_key_required` (reject only), 503 `service_unavailable` (storage or DB transport failure).

**[DECISION L1 → RULED WT3] reject → re-render (README §B10 WT3).** `RejectVideoCommand` records the rejection and moves `video_review → story_approved` (the successor state 00 §7 already uses for "user can re-render"), raises `video_rejected.v1` with `Target = re_render`, and **does not** call `RenderEpisodeCommand` itself — nothing is nested in its UnitOfWork; the route answers **202** [RULED — README §B10 WT3]. The Worker's outbox handler `VideoReRenderHandler` (`Application/Features/VideoReview/Events/VideoReRenderHandler.cs`, one of this task's files) sends it in its own scope and pipeline (D11: "a handler that changes data sends a command through `ISender`"), with the idempotency key `video-reject-rerender:{messageId}` [RULED — README §B10 WT3]:

```csharp
public sealed class VideoReRenderHandler : IOutboxHandler<VideoRejected>          // runs in the Worker, own DI scope
{
    public async ValueTask HandleAsync(VideoRejected e, OutboxContext ctx, CancellationToken ct)
    {
        if (e.Target is not RejectTarget.ReRender) return;                        // story_review needs no render
        var etag = await _etag.ForEpisodeAsync(e.EpisodeId, ct);                  // ETag read at send time (§ open item)
        var r = await _sender.Send(new RenderEpisodeCommand(e.EpisodeId, IfMatch: etag,
                         IdempotencyKey: $"video-reject-rerender:{ctx.MessageId:N}"), ct);   // BD5 signature
        if (r.IsError) throw new OutboxRetryException(r.FirstError.Code);         // 412/409 → dispatcher backoff (D11)
    }
}
```

`ctx.MessageId` is stable across dispatcher retries (D11), so the render reservation is idempotent per rejection; `IfMatch` is read at send time (an `xmin` change between the rejection and the send is a retryable 412, not a lost action). The endpoint answers **202** with `{ id, status: "story_approved", etag }`; the new render job reaches the client through `EpisodeUpdated` (BD11) and `GET /episodes/{id}` (09.5.3-T1), within the outbox poll interval (`Outbox:PollIntervalSeconds` = 5). A rejected render (`keywords_flagged` 409, `budget_exceeded` 402) leaves the episode at `story_approved`, exactly as a failed render does today.

Events and real-time:

| Event | `[OutboxEvent]` | Payload | Consumers |
|---|---|---|---|
| `VideoApproved` | `video_approved.v1` [INFERENCE] | `EpisodeId, SeriesId, UserId, AssetId, OccurredAt` | 02.8.3 (`waiting_owner → publication_created`, L222), 10 via `EpisodeUpdated` |
| `VideoRejected` | `video_rejected.v1` [INFERENCE] | `EpisodeId, SeriesId, UserId, Target, OccurredAt` (no reason text) | 02.8.2 plan L222, 02.8.3-T3 L54 (cancels a pending publication), `VideoReRenderHandler` |

`IRealtimeNotifier.NotifyAsync(userId, EpisodeUpdated { EpisodeId, Status, NextActions }, ct)` is called by the outbox handlers (09.8.1-T1, BD11) — not from the command handlers.

Get the video URL (09.7.2-T1): no command, so no `[Audited]`; the endpoint loads the episode (tenant filter) → 404 when absent, foreign, or without a live `final_video_asset_id`; resolves `storage_key`; `CreateReadUrl(bucket, key, TimeSpan.FromMinutes(15), $"{slug}.mp4")`; returns `{ url, expiresAt }` where `expiresAt` is the same instant the signature was built for. Because a `GET` has no Mediator pipeline, the endpoint opens the transaction itself around the audit write (IRC from `W2EmailAudit`, 2026-09-24 — `IAuditWriter.WriteAsync` joins the ambient transaction, never commits, and throws without one):

```csharp
await using var uow = await _unitOfWork.BeginAsync(ct);
await _audit.WriteAsync(new AuditEntry(
        Action: "episode.video_url_issued", Category: Category.Episodes, Visibility: Visibility.User,
        SubjectUserId: _currentUser.Id, ActorUserId: _currentUser.Id, EntityType: "episode", EntityId: id,
        Data: new { episodeId = id, assetId, expiresAt }, Ip: null, UserAgent: null), ct);   // no URL, no signature
var commit = await uow.CommitAsync(ct);                                       // must be checked before returning 200
```

`audit_logs.subject_user_id` is nullable and `ck_audit_logs_user_visible_has_subject` forbids a null subject on a `visibility = user` row, so the caller's id is always set here; `ip`/`user_agent` stay null for this action (it is not a security event). Index `(subject_user_id, created_at DESC)` plus the partial `(entity_type, entity_id)`.

## 6. UI

No screen, route or component is owned here. Consumers: **10.5.2** (episode detail — approve / reject with a target and a reason, the preview player, download), **10.4.3** (autopilot run log reads its own `autopilot.video_auto_approved` row, written by 02.8.2-T1, plus `episode.video_approved` from here, so History shows both). The contract the UI needs is exactly: approve and reject both send `If-Match`, **reject additionally sends `Idempotency-Key`** (without it: 400 `idempotency_key_required`; a repeat with the same key replays the stored response and raises no second event); 200 + `etag` after approve or reject-to-`story_review`; 202 + `etag` after reject-to-re-render (the render job arrives on `EpisodeUpdated`); and `{ url, expiresAt }` from `video-url` (never cached in a store; fetched on demand because it expires in 15 min).

## 7. Jobs and integrations

**`RetentionSweepJob`** (`Application/Features/Assets/Jobs/RetentionSweepJob.cs`, `sealed`, `IBackgroundJob`, no Hangfire attributes — D12; on the D10 allow-list for cross-tenant reads):

```csharp
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed class RetentionSweepJob : IBackgroundJob
{
    public async Task RunAsync(CancellationToken ct);          // ids-only rule: no other parameters (D12)
}
```

Registration in the Worker (`Worker/Jobs/RetentionSweepRegistration.cs`, `IRecurringJobRegistration`): id `retention_sweep`, cron `0 3 * * *` UTC (W12/N14), removed automatically if the registration disappears (09.2.6-T1).

Run sequence:

1. `pg_try_advisory_lock(hashtext('retention_sweep'))` in a short transaction; not acquired → log `skipped` and return (two Api/Worker replicas must not double-sweep).
2. **Asset phase**, at most `RetentionSweep:MaxBatches` (20) iterations of `RetentionSweep:BatchSize` (500) rows, and a run-local `failedIds` set pushed into the next query's `where` — together these guarantee progress even when every row of a batch fails (without both, the same 500 due rows would be returned forever and the "loop until a short batch" condition would never be reached). The query goes through the **pinned** wave-1 port (09.2.3-T1:36 / its plan:39) — `IAsyncEnumerable<(Guid Id, Guid UserId)> ISystemWorkQuery.PendingAsync<T>(Expression<Func<T,bool>> where, int batch, CancellationToken ct) where T : Entity, IUserOwned` — with `where = a => a.DeletedAt == null && a.RetentionUntil != null && a.RetentionUntil < now && !excludedIds.Contains(a.Id)`, `T = Asset`; **the port is not extended** ([OPEN] in §10). For each returned `(id, userId)`: open one `ISystemContext.For(userId)` scope (D10), load the asset in that scope (it carries `storage_key`/`kind`, which the pair projection does not), `IBlobStore.DeleteAsync(bucket, key, ct)` — a missing object is success (idempotent re-run) — then `MarkDeleted(now)`. A per-object failure adds the id to `failedIds`, increments `retention_sweep_asset_failures_total` and is skipped; the loop then moves on (a poison key must not block the sweep) and the row stays due for tomorrow.
3. **Audit phase**: the same bounded loop over 09.7.3-T1's own `IAuditRetentionStore` (audit rows are not `IUserOwned`, so the pinned `PendingAsync<T>` port cannot serve them) — the scan and the delete go by **keyset over the `(created_at, id)` index** (`WHERE (created_at, id) > (@afterCreatedAt, @afterId) ORDER BY created_at, id LIMIT @take`, cursor advanced every iteration; no `OFFSET`, no newest-first re-scan) [RULED — README §B10 WT47]: evaluate each candidate with 09.3.6-T1's `AuditRetentionRules.Ordered` first-match-wins per candidate (`AuditRetentionPredicate.Expired`) → delete the expired ids of that key range in one set-based `DELETE … WHERE id = ANY(@ids)`; `payment_linked` has `Retain = null` (never). The windows live in C# only, never duplicated in SQL — the earlier draft's SQL claimed payment-linked rows were "excluded by both predicates" while its user branch would in fact have deleted a 12-month-old `billing` row written by a user actor (IRC from `W2EmailAudit`, 2026-09-24); the unit test `Predicate_PaymentLinkedRule_NeverRetainsARow` pins the fixed behaviour, and `HasPaymentLink` is read from the real linkage columns even though it is always false in P (payments are S), so plan 12 lights the rule up without a code change. Logs and `audit_logs` rows use hashed user ids only (13 §5.4 handles account deletion with hashed ids — not this job).

   [INFERENCE] the payment-linkage columns behind `HasPaymentLink` are 09.3.6-T1's DDL (BD15). Its executable contract is `AuditRetentionRules.Ordered` — an `IReadOnlyList<AuditRetentionRule>` (`AuditRetentionRule(string Id, string Description, TimeSpan? Retain, Func<AuditRetentionRow, bool> Matches)`) with the rule ids in order `payment_linked` (`Retain = null`) · `account_security` (730 d) · `staff_or_internal` (730 d) · `user_visible` (365 d, always matches) over `AuditRetentionRow(Category, ActorType, Visibility, bool HasPaymentLink, DateTimeOffset CreatedAt)` (09.3.6 plan §3). A delegate predicate cannot be pushed into SQL, which is exactly why the decision is taken in C# over a bounded candidate batch.
4. Metrics (OTel via the 09.9.3-T1 conventions) [INFERENCE names]: `retention_sweep_assets_deleted_total`, `retention_sweep_asset_failures_total`, `retention_sweep_audit_rows_deleted_total`, `retention_sweep_batches_total`, `retention_sweep_duration_seconds`, and `retention_sweep_batches_capped_total{phase}` when `MaxBatches` is reached (the signal that the run did not finish its backlog). No metric carries a user id.
5. Log **one** summary line (`deleted`, `failed`, `auditDeleted`, `batches`, `capped`, duration) — ids and counts only, never keys or titles (BD16).

**Storage health (09.7.2-T1).** `StorageHealthProbe` (`BackgroundService`, every `Storage:HealthProbeSeconds` = 30, timeout 5 s) writes the marker if `ExistsAsync(media, Storage:HealthMarkerKey)` is false (`PutAsync` of a 0-byte `text/plain` body, idempotent) and stores `(IsHealthy, LastCheckedAt, LastErrorCode)` in the singleton `IStorageHealthCache` — the same shape as 11's `NodeHealthPoller` + `INodeHealthCache`. `/health/ready` (BD12) **reads the cache only** (BD16: "health endpoints read caches only"), so an LB probe never issues an S3 HEAD; a stale cache (older than 3 × the interval) or an unhealthy flag reports the `storage` component as **degraded while the probe still answers `200` with `Degraded` status** (database failure remains the only 503) [RULED — README §B10 WT38/WT15]. The marker lives in the media bucket because the app key cannot write `nt-public-<env>` (11 D20 H9).

**Integration points.** `IBlobStore` consumers (BD1): 02.5.2-T2 (`IExtractedTextStore`, `research/{userId}/{episodeId}/{sourceId}/{sha256}.txt`), 09.6.3-T2 (`media/{userId}/{episodeId}/{renderJobId}/video.mp4`, `…/subtitles.srt`), 09.7.3-T1 (delete), 13.3.1-T1 (deletion sweep by `user_id`). `RetentionSweepJob` is consumed by 13 (B8.1); the `render_jobs`/adapter side is untouched.

## 8. Security and privacy

- **Tenant isolation (D10).** Every read of `assets`/`episodes` inside a command goes through the tenant filter; a foreign or unknown episode is **404 `not_found`**, never 403. `IgnoreQueryFilters(["tenant"])` appears only in `RetentionSweepJob`'s and `SystemWorkQuery`'s allow-listed code paths, and every asset write happens inside `ISystemContext.For(userId)`.
- **Presigned URL (WF16/BD16).** TTL ≤ 15 min; the URL is never persisted, never audited verbatim, never logged (no `LogInformation` of the URL or of the `Uri` object; the redaction processor 09.9.3-T2 is a second line, not the only one); `Referrer-Policy: no-referrer` (BD12) stops the `Referer` leak; the download name is `attachment` + `filename*=UTF-8''<ascii-folded-slug>.mp4` with the slug stripped of `"`, `\`, CR/LF, `%` and control characters and truncated to 80 chars (RFC 6266/5987 form, so a header injection cannot happen).
- **Least privilege.** The application credentials write only the media bucket (11 D20 H9); no `DeleteBucket`, no ACL calls, no public-read grants: reads go through presigning only. Credentials arrive from the secret manager (11 D15) into `StorageSettings`; they are never logged and never serialised into an error message; a storage exception is mapped to 503 `service_unavailable` with a fixed description (BD16).
- **Integrity.** `PutAsync` computes SHA-256 while streaming and returns it; 09.6.3-T2 already derives `media/{userId}/…` from the authenticated user, and this task keeps that convention so a cross-tenant key cannot be produced.
- **Audit.** Both commands carry `[Audited]` (BD15) and implement `IAuditData`; the projection carries ids, the target and the status and never the `Reason`/`Note` text or a URL. The URL endpoint writes through `IAuditWriter` inside its own `IUnitOfWork` transaction (a `GET` has no pipeline) with `subject_user_id` = `actor_user_id` = the caller (the check constraint `ck_audit_logs_user_visible_has_subject` requires a subject on a user-visible row) and `ip`/`user_agent` null. The arch test in 09.3.6-T1 covers "every `ICommand` has `[Audited]` or `[NotAudited]`".
- **Retention.** Deletion is by key read from the database, never from a client-supplied string; the job holds no user token; logs carry counts and ids only.
- **Constant-time compare** is not needed here (no secret comparison); the ETag compare is a strong string compare of a value the client already sees (D15).
- **Rate limiting.** None of these routes is in BD14's policy list; the presign endpoint is a plain authenticated GET. [INFERENCE] no new policy; if abuse appears, the `paid` policy can be attached in 09.3.5-T1 without a contract change.

## 9. Test plan

Framework: D2/D13 — `NightTales.Api.IntegrationTests` with `ApiFactory`, `TestAuthHandler` (`X-Test-User`, `X-Test-Role`), `WorkerHost`, `InlineJobScheduler`, `FakeClock` and `PostgresFixture` (Testcontainers PostgreSQL); pure unit cases in `NightTales.Domain.UnitTests` (aggregate) and `NightTales.Application.UnitTests` (handlers with fakes, helpers); no mocking library, hand-written fakes only (`NightTales.TestKit/Storage/InMemoryBlobStore.cs`: a dictionary-backed `IBlobStore` that records calls and can be told to fail).

| Project / class | Cases |
|---|---|
| `NightTales.Api.IntegrationTests/Features/VideoReview/ApproveVideoTests.cs` | `ApproveVideo_FromVideoReview_Returns200WithNewEtag`; `ApproveVideo_WithoutIfMatch_Returns428IfMatchRequired`; `ApproveVideo_WithStaleIfMatch_Returns412EpisodeConflict`; `ApproveVideo_WhenNotVideoReview_Returns409EpisodeInvalidState`; `ApproveVideo_OfAnotherUsersEpisode_Returns404`; `ApproveVideo_ReStampsRetentionUntil30Days_OnEveryLiveAsset`; `ApproveVideo_WritesEpisodeVideoApprovedAuditRow_WithNoReasonText`; `ApproveVideo_RaisesVideoApprovedV1_ExactlyOnce`; `ApproveVideo_AsAutopilot_AcceptsNullIfMatch` |
| `NightTales.Api.IntegrationTests/Features/VideoReview/RejectVideoTests.cs` | `RejectVideo_ToStoryTarget_Returns200AndStoresReason`; `RejectVideo_ToStoryTarget_RaisesVideoRejectedV1_WithTargetStory`; `RejectVideo_ToReRender_Returns202AndLeavesStoryApproved`; `RejectVideo_ToReRender_IsIdempotent_WhenTheKeyRepeats`; `RejectVideo_WithoutIdempotencyKey_Returns400`; `RejectVideo_WithEmptyOr501CharReason_Returns400ValidationFailed`; `RejectVideo_WithUnknownTargetValue_Returns400`; `RejectVideo_OfAnotherUsersEpisode_Returns404` |
| `NightTales.Api.IntegrationTests/Features/VideoReview/VideoReRenderOutboxTests.cs` | `VideoReRenderHandler_CreatesExactlyOneRenderJob_ForAReRejectEvent`; `VideoReRenderHandler_IsIdempotent_AcrossDispatcherRetries`; `VideoReRenderHandler_IgnoresStoryTarget`; `VideoReRenderHandler_OnRenderError_RequestsOutboxRetry`; `VideoReRenderHandler_WhenRenderSucceeds_EpisodeBecomesRendering` |
| `NightTales.Api.IntegrationTests/Features/VideoReview/VideoUrlTests.cs` | `VideoUrl_ReturnsUrlAndExpiresAt_FifteenMinutesApart`; `VideoUrl_ContentDisposition_IsAsciiFoldedAttachmentWithMp4Suffix`; `VideoUrl_NeverWritesTheUrlToLogsOrAudit`; `VideoUrl_WithoutFinalVideoAsset_Returns404`; `VideoUrl_OfAnotherUsersEpisode_Returns404`; `VideoUrl_WritesEpisodeVideoUrlIssuedAuditRow_InItsOwnTransaction`; `VideoUrl_WhenAuditCommitFails_Returns503` |
| `NightTales.Api.IntegrationTests/Storage/S3BlobStoreTests.cs` (a new container — [OPEN]) | `PutAsync_ReturnsByteCountAndSha256`; `OpenReadAsync_RoundTripsContentAndType`; `ExistsAsync_FalseForMissingKey`; `DeleteAsync_IsIdempotentWhenKeyMissing`; `CreateReadUrl_ContainsSignatureAndExpiresAtTtl`; `S3BlobStore_BuildsClientWithPathStyle_WhenConfigured` (local, no container: assert the client config) |
| `NightTales.Api.IntegrationTests/Storage/StorageHealthTests.cs` | `HealthReady_Returns200WithDegradedStatus_WhenStorageIsUnhealthy` (WT38/WT15 — never 503 for storage); `HealthReady_DoesNotCallTheBlobStore` (a recording fake proves zero `ExistsAsync` calls per probe); `StorageHealthProbe_WritesMarkerOnce_WhenAbsent`; `HealthReady_Returns200WithDegradedStatus_WhenTheCacheIsStale`; `HealthReady_DatabaseFailure_Is503` (the one 503 case) |
| `NightTales.Application.UnitTests/Features/Assets/RetentionSweepTests.cs` (hand fakes) | `RetentionSweep_DeletesOnlyDueAndLiveAssets`; `RetentionSweep_SetsDeletedAt_AndSecondRunIsNoOp`; `RetentionSweep_ToleratesMissingObject_AndStillMarksDeleted`; `RetentionSweep_ContinuesAfterOneObjectFails`; `RetentionSweep_BatchesAt500`; `RetentionSweep_StopsAtMaxBatches_AndReportsCapped`; `RetentionSweep_ExcludesThisRunsFailuresFromTheNextQuery`; `RetentionSweep_ProcessesEachRowUnderItsOwnUserScope`; `RetentionSweep_LogsCountsOnly_NoKeys`; `RetentionSweep_ExitsWhenAdvisoryLockIsHeld` |
| `NightTales.Api.IntegrationTests/Features/Assets/AuditRetentionTests.cs` | `AuditRetention_KeepsPaymentLinkedRows_RegardlessOfAge`; `AuditRetention_KeepsAccountSecurityRowsFor24Months`; `AuditRetention_KeepsAdminAndInternalRowsFor24Months`; `AuditRetention_DeletesUserRowsAfter12Months`; `AuditRetention_DeletesAtMost500RowsPerBatch`; `AuditRetention_ScansByKeyset_NoOffset_AndUsesTheCreatedAtIdIndex` (WT47); `AuditRetention_UsesTheOrderedRulesFromAuditRetentionRules` |
| `NightTales.Domain.UnitTests/Episodes/EpisodeVideoReviewTests.cs` | `ApproveVideo_FromVideoReview_SetsReadyAndReadyAt`; `ApproveVideo_FromDraftOrReady_ReturnsEpisodeInvalidState`; `RejectVideo_ToStoryTarget_MovesToStoryReview`; `RejectVideo_ToReRender_MovesToStoryApproved`; `RejectVideo_StoresReasonAndTrimsIt`; `ScheduleRetention_ReStampsOverAnExistingValue` (WT56) |
| `NightTales.Application.UnitTests/Storage/SafeContentDispositionTests.cs`, `…/Features/Assets/VideoSlugTests.cs` | `SafeAttachment_StripsQuotesBackslashesAndNewlines`; `SafeAttachment_IsAsciiOnly_AndAtMost80Chars`; `SafeAttachment_ReturnsNullForNullName`; `VideoSlug_FoldsVietnameseDiacritics`; `VideoSlug_StripsPunctuation`; `VideoSlug_FallsBackToNighttales_WhenEmpty`; `VideoSlug_TruncatesAt80Chars` |
| Architecture (09.3.6-T1's / 09.1.2-T1's classes, covered here) | `EveryCommand_HasAuditedOrNotAudited`; `EveryAuditedCommand_ImplementsIAuditData`; `OnlyAllowListedTypes_IgnoreTheTenantFilter` (includes `RetentionSweepJob`, `SystemWorkQuery`) |

All are deterministic: `FakeClock` drives `now`; no `Thread.Sleep`; the sweep and the outbox handler are invoked directly through `WorkerHost`/`InlineJobScheduler`.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.7.1-T1 | Video approve/reject (checkpoint #2) | BE | S (0.5) | 09.6.3-T2 | 02.8.2-T1, 02.8.3-T3, 06.2.1-T1 (06 CR-8), 10.6.2-T1 (10 CR-15) |
| 09.7.2-T1 | `IBlobStore` + S3 and the signed video URL | BE | M (1.0) | 09.2.1-T1, 11.1.1-T2 | 09.6.3-T2, 09.7.3-T1, 02.5.2-T2, 06.2.3-T1, 13.3.1-T1 (06 CR-8), 10.6.2-T1 (10 CR-15), 13.3.3-T1, 07.3.1-T1 |
| 09.7.3-T1 | Retention sweep (assets + audit rows) | BE | S (0.5) | 09.7.2-T1, 09.3.6-T1 | — |

**Re-estimate: 2.0 d raw** for the folder (09.7.1 0.5 + 09.7.2 1.0 + 09.7.3 0.5), matching B8.1 and B9 ("09.7 1.5 → 2.0, 09.7.2 S → M because of `IBlobStore`, WF03"). No further change is proposed.

Open items:

- ✅ **Writer of `assets.retention_until` — RULED (WT56)**: the ingest (09.6.3-T2) stamps `retention_until = now + Assets:UnapprovedRetentionDays` (30) in its final transaction and approve (09.7.1-T1) **re-stamps** the same rows `now + Assets:RetentionDays` (30) [RULED — README §B10 WT56]. The former [OPEN] reading ("only 09.7.1-T1, 30 days after `ready`") is superseded: it left an un-reviewed render's assets with no `retention_until` at all, i.e. unsweepable.
- [OPEN] **`Asset` entity ownership** — 00 §6/D9 give the table to 09.2.2-T2, but the type is named nowhere: 09.6.3-T2 speaks of "an `assets` writer" and of "`assets` rows (00 §6 L288)" only, and 09.6.1-T1 never touches `assets`. The two members this story needs (`ScheduleRetention`, `MarkDeleted`) must land on whichever task creates the entity (09.2.2-T1's configurations or 09.6.3-T2, the first writer) — [INFERENCE] until then. Note also that 09.6.3-T2 keeps `assets` idempotent by `storage_key + sha256` because the table has no `render_job_id` column (its own Notes); nothing in this story depends on that column.
- [OPEN] **`IAuditData` / `IIdempotentCommand` member shapes** — BD15 (`../README.md`:700) mandates the projection but does not spell the member names (09.3.6-T1 owns them); the fifth parameter `IdempotencyKey` on `RejectVideoCommand` is D4/D6 applied to a route whose spend moved to the Worker. Confirm both against 09.3.6-T1 before implementation.
- [OPEN] **`AuditRetentionRules.Ordered` consumption** — 09.3.6-T1 exposes `IReadOnlyList<AuditRetentionRule>(string Id, string Description, TimeSpan? Retain, Func<AuditRetentionRow, bool> Matches)` over `AuditRetentionRow(Category, ActorType, Visibility, bool HasPaymentLink, DateTimeOffset CreatedAt)`, rule ids `payment_linked` (`Retain = null`) · `account_security` 730 d · `staff_or_internal` 730 d · `user_visible` 365 d (its `plan.md` §3, quoted exactly). Because `Matches` is a delegate it cannot be pushed into SQL, so 09.7.3-T1 evaluates the rules in C# over a bounded candidate batch loaded by its own `IAuditRetentionStore`; if the TL prefers a set-based SQL delete, 09.3.6-T1 would have to switch `Matches` to an expression-tree-friendly predicate (its own offer) — that is a CR to 09.3.6-T1, not a change here.
- [OPEN] **`ISystemWorkQuery` is pinned** — wave 1 signs exactly one member, `IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(Expression<Func<T,bool>> where, int batch, CancellationToken ct) where T : Entity, IUserOwned` (09.2.3-T1:36; its `plan.md`:39). 09.7.3-T1 therefore adds nothing to it: the assets sweep consumes `PendingAsync<Asset>`, and audit rows (not `IUserOwned`) go through the task-owned `IAuditRetentionStore`. Extending the pinned port was explored and rejected (IRC `W2EmailAudit`, 2026-09-24).
- [OPEN] **ETag source for `VideoReRenderHandler`** — the handler reads the episode ETag at send time through an `IReadDb`/episode-ETag accessor in a tenant-scoped (`ISystemContext.For`) read (09.1.4-T1's delegate is HTTP-shaped, and the pinned `ISystemWorkQuery` returns only `(Id, UserId)` pairs and cannot be extended); confirm the interface with 09.1.4-T1.
- [OPEN] **Storage contract test container** — D2/N11/D13 list `Testcontainers.PostgreSql` only; the S3 implementation needs a second container (MinIO or the 11 D14 SeaweedFS image) or the test degrades to `InMemoryBlobStore` plus a config-only assertion. New test dependency → implementation-time decision.
- [OPEN] **`audit_logs` payment-linkage columns** (§7 rule 1) — 09 §4 09.3.6 L147 states the rule; the columns/predicate are 09.3.6-T1's DDL (BD15). 09.7.3-T1 reads them from `AuditRetentionRules.Ordered` and never re-encodes the days.
- [OPEN] **Both-direction edge check (wave DoR)** — B8.1 lists `09.7.1-T1 → Blocks 02.8.3-T3`, and 02.8.3-T3's header (`02.8.3-T3-publish-handoff-pre-audit-mode.md`:5) does **not** list 09.7.1-T1 in `Depends on` although its Inputs line names it. The signed 02 file is not edited here; the wave check should add the back-edge.
- [INFERENCE] names introduced: `RejectTarget`, `VideoReviewResult`, `EpisodeStateDto`, `Episode.VideoReview.cs`, `Episode.ApproveVideo/RejectVideo`, `Asset.ScheduleRetention/MarkDeleted`, `BlobLocator.BucketFor`, `StorageSettings`, `StorageBuckets`, `IStorageHealthCache`/`StorageHealth`/`StorageHealthProbe`, `IAuditRetentionStore`/`AuditRetentionStore`/`AuditRetentionCandidate`, `AuditRetentionPredicate`, `VideoReRenderHandler`, `IEpisodeEtagReader`, `video_approved.v1`, `video_rejected.v1`, `Storage:HealthMarkerKey`, `Storage:HealthProbeSeconds`, `RetentionSweep:{BatchSize,MaxBatches,Enabled}`, the metric names in §7, `InMemoryBlobStore`.

## 11. Rollout

- **Order**: 09.7.2-T1 (unblocks 09.6.3-T2 and 02.5.2-T2) → 09.7.3-T1 and 09.7.1-T1 in parallel (both depend on storage-or-ingest work already landed).
- **Migrations**: exactly one, from 09.7.3-T1 — `ix_assets_retention_due` (index only, expand-only, no data change) plus `ix_audit_logs_created_id` on `audit_logs (created_at, id)` for the keyset sweep ([RULED — README §B10 WT47]). 09.7.1-T1 adds none (`assets` and `audit_logs` exist per D9/BD15).
- **Flags**: none (B8.1 `P · M1`). Both routes ship enabled; the sweep only has work after the first `ready`.
- **Env/secret prerequisites**: `Storage:*` values and `nt-media-<env>` (11.4.1-T1, 11 D15); in dev the compose `storage` service plus `tools/dev/storage-init.sh` (11 D14).
- **Observability**: the §7 metrics; `retention_sweep_batches_capped_total` or three days of `retention_sweep_asset_failures_total` mean a credential, bucket or backlog problem and should alert (11.6.2-T1 owns alerts).
- **Rollback**: the routes can be disabled by removing the endpoint group (no data migration); the sweep stops by deleting the recurring registration (`RemoveRecurringIfExists`, D12) or `RetentionSweep:Enabled=false` — objects already deleted are not recoverable, so before enabling it in prod, 11.4.1-T1's `nt-media-<env>` → `nt-backup-<env>` copy (11.6.1, 30 days) must be in place; `deleted_at` is set after the object delete, so a mid-batch crash is safe and the next run is a no-op for the rows already marked.
