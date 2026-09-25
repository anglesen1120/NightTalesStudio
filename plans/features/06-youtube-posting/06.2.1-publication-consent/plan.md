# 06.2.1 — Publication create, consent and cancel (detailed plan)

Sections follow `../../00-overview.md` §10.1. Contract: `../README.md` §3 D1–D20 (authority), `../../00-overview.md` §5.2/§6/§7, `../README.md` §8.1 (canonical dependencies).
Tasks in this folder: `06.2.1-T1`, `06.2.2-T1`, `06.2.2-T2`, `06.2.6-T1` (2.0 d). Sibling folders own the rest of plan 06: `06.1-connect/`, `06.2.2-autopilot-veto/` (06.2.2-T3/T4), `06.2.3-upload/`, `06.2.4-schedule-status/`, `06.3.1-quota-audit/`, `06.4-revocation/`.

## 1. Scope

Story ids covered: **06.2.1** (create publication), **06.2.2** consent half — T1 (consent command + domain guard) and T2 (consent e-mail, veto notice e-mail with single-use cancel link, cancel-by-link route), **06.2.6** (cancel before upload + quota release).

In scope:

- `Publication` aggregate, its status machine and invariants (`awaiting_consent → scheduled → uploading → processing → published | failed | cancelled`), `YouTubeMetadataRules` (title/description/tags/privacy/publishAt per G4), `ETags.Publication` (`"pu-<xmin>"`), the attribution append from `render-params.v1.music.attribution`, the `_06_Publications` expand-only migration, and the `PublicationsSummaryDto` fill on episode detail (TL-approved third query, C7).
- `POST /episodes/{id}/publications`, `GET /episodes/{id}/publications`, `POST /publications/{id}/consent`, `POST /publications/cancel-by-link`, `DELETE /publications/{id}`.
- E-mails `publication_consented` (every consent) and `publication_veto_notice` (autopilot, with the single-use cancel link), their template registration into 09.2.6-T3's catalog and the `publication_cancel` token kind (06 CR-6).

Out of scope (owned elsewhere, listed so no reader guesses):

- Autopilot consent job, veto-window PATCH and "approve now" — `06.2.2-T3/T4` (`06.2.2-autopilot-veto/`); this folder only writes `veto_ends_at` and the notice hand-off seam that T2 fills.
- Upload, session URI, `PublishYouTubeJob`, OAuth error classifier, incidents — `06.2.3-*`. `platform_incidents` is read (not written) here.
- Quota ledger implementation and the ETA endpoint — `06.3.1-T1`; this folder only calls `IQuotaLedger.ReleaseAsync`.
- Pre-audit privacy mapping, `publishAt` omission, go-live, `PollYouTubeProcessingJob`, `PublicationGoLiveJob`, `published_at` — `06.2.4-T1` / `06.2.5-T1` (D12).
- Disconnect, token health, deletion handlers — `06.4-*`.
- Multi-client settings/removal guard (`06.1.2-T3`, S), fairness (S), evidence pack (`06.5.1-T1`, BA).

High-level sections refined: `../../06-youtube-posting.md` L61-70 (06.2.1, 06.2.2), L86-88 (06.2.6); re-estimate per `../README.md` §9.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| G3 | `status.publishAt` may be set only when the privacy status is `private`; ISO 8601; past date publishes at once; `invalidPublishAt` 400 | `../README.md:33` ✅ |
| G4 | Title ≤ 100 characters, no `<` `>`; description ≤ **5000 bytes**, no `<` `>`; tags ≤ **500 characters**, commas count and a tag containing a space counts its implicit quotes | `../README.md:34` ✅ |
| G6 | `uploadStatus ∈ deleted, failed, processed, rejected, uploaded`; `privacyStatus ∈ private, public, unlisted` | `../README.md:36` ✅ |
| C1 | `CreatePublicationCommand(episodeId, channelConnectionId, title, description, tags, privacy, publishAt, containsSyntheticMedia: true, autopilotAuthorizationId)`; 409 `publication_consent_required`/validation → run `failed`; 429 quota → pause trigger 8 | `../README.md:49`; `02-story-writing/02.8.3-cadence-publishing/plan.md:455` ✅ |
| C6 | Reuse only: `IEmailSender.QueueAsync(EmailMessage) → ErrorOr<Guid>`, `IEmailDeliveryStatus.GetAsync`, template catalog, token minting at send time (WT17), `ITokenProtector.Protect(purposeSuffix, plaintext)`, `RequireFeature("youtube_publish")` → 404 `feature_disabled`, `[Audited]`, rate policies `auth`/`paid`/`hub`; codes `publication_consent_required`, `youtube_upload_failed`, `youtube_quota_exhausted`, `youtube_rejected` | `../README.md:54` ✅ |
| C7 | `EpisodeDetailDto.Publications` is `PublicationsSummaryDto?` and stays `null` in P; 06 either extends query (2) or adds a **third query approved by the TL** | `../README.md:55`; `09-backend-api/09.4.2-5.3-queries/plan.md:166,197` ✅ |
| C8 | Attribution is `render-params.v1.music.attribution` frozen at render time; 06.2.1-T1 reads it from the params (ruling T9 beats 05.3 `plan.md:102,155`) | `../README.md:56`; `05-video-assembly-subtitles/README.md:332-336,553` ✅ |
| C9 | Veto-list client: `Publication { id, episodeId, platform, status, title, privacy, publishAt, consentAt, consentSource, etag }`; `PATCH` sends `If-Match`, 412 → ConflictDialog; consent and `DELETE` are issued from the row | `../README.md:57`; `10-web-ui/10.4-series-autopilot/plan.md:156-159,249-252` ✅ |
| C11 | `publications` columns per 00 §6 (incl. `consent_at`, `consent_text_version`, `consent_source`, `autopilot_authorization_id`, `veto_notice_sent_at`, `contains_synthetic_media`, `external_post_id`, `platform_payload jsonb`, `error_code`) | `../README.md:59`; `../../00-overview.md:290` ✅ |
| D6 | Aggregate, invariants, `YouTubeMetadataRules`, routes, `PublicationDto`, `PublicationsSummaryDto` fill | `../README.md:104-108` ✅ |
| D7 | ETag `"pu-<xmin>"`, `If-Match` on consent/PATCH/cancel, `pc.v1` text version check → 400 `consent_text_outdated`, `consent_source = user`, audit `publishing.consented`, e-mail per consent, cancel token kind/route/rate policy | `../README.md:110-114` ✅ |
| D10 | `IQuotaLedger.TryReserveAsync/ReleaseAsync`; release only when YouTube never received the insert; PT day via ICU | `../README.md:124` ✅ |
| D11 | `YouTubeOptions`: `PublishLeadMinutes` 120, `MinVetoWindowHours` 2, `AuditStatus`, `ConsentTextVersion` `pc.v1`, `Quota:UnitsPerDay` 100 | `../README.md:126` ✅ |
| D12 | Queue time = now or `publish_at − PublishLeadMinutes`; the episode `ready → scheduled` call is made by **06.2.2-T1** when scheduling (F15) | `../README.md:128` ✅ |
| D13 | Outbox `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }` → `IRealtimeNotifier` `PublicationUpdated` with `{ publicationId, episodeId }`, no `RealtimeData` (WT54) | `../README.md:130` ✅ |
| D17 | Feature flag on every route, owner-only (tenant 404 for others), no Google password, cancelled-with-token single-use+hashed, audit actions `publishing.publication_created`/`publishing.consented`/`publishing.cancelled`, logs never carry titles/descriptions/tokens/channel names | `../README.md:138` ✅ |
| D18 | Indexes `publications (status, next_attempt_at) WHERE status IN ('awaiting_consent','scheduled','uploading','processing')`, `publications (channel_connection_id)`, the D6 partial unique; episode detail +1 indexed query only | `../README.md:140` ✅ |
| D19 | TestKit: `FakeGoogleServer`, tests on real PostgreSQL (Testcontainers), `FakeClock`; no mocking library | `../README.md:142` ✅ |
| D20 | Migration `_06_Publications` (incl. `veto_ends_at`) owned by 06.2.1-T1; expand-only | `../README.md:144` ✅ |
| ETags | `ETags.Episode(uint) → "ep-{xmin}"`, `ETags.Series(uint) → "se-{xmin}"`, `ETags.Matches(ifMatch, current)` (strong, exact, `*` rejected); `RequireIfMatch()` → 428 `if_match_required`; `DbUpdateConcurrencyException` → 409 `concurrency_conflict` | `09-backend-api/09.1.4-error-mapping/plan.md:44`; `09.1.4-T1-problem-details-etag-helpers.md:53-60,200` ✅ |
| Email | `EmailMessage(Guid UserId, string Template, Language Locale, IReadOnlyDictionary<string,string> Params)`; `IEmailSender.QueueAsync` queue-only; `IEmailDeliveryStatus.GetAsync`; `IEmailTokenMinter.MintAsync(userId, template, parameters, ct)` mints inside `SendEmailJob` (WT17); `EmailMessage : IUserOwned` so foreign reads are 404; templates `Templates/Email/<name>.<locale>.{txt,html}` | `09.2.6-worker-hangfire/09.2.6-T3-email-pipeline.md:21-34,42-58,107,168` ✅ |
| Tokens | `user_action_tokens(id, user_id, kind text CHECK, token_hash text unique, created_at, expires_at, used_at)`; owner 09.3.3-T1; `PasswordResetTokenMinter` is the WT17 precedent | `09.3-auth/09.3.3-T1-forgot-and-reset-password.md:55-67,124` ✅ |
| Codes | 09 catalogue style: 409 `concurrency_conflict`, 412 `episode_conflict` (CR-7 additions) prove entity-specific 412 codes are the pattern | `09-backend-api/README.md:341,360` ✅ |

Open questions carried into the task files as `[OPEN]`: the `publication_*` error-code rows (§4), `publications.published_at` (06 CR-1 extension, §3), `platform_payload.quotaReservedDay` ownership (§7), and the 10-wave-2 owner of the `/publications/cancel` page (§6).

## 3. Domain and data

### 3.1 Aggregates and invariants

`Publication : AggregateRoot, IUserOwned` — `src/NightTales.Domain/Channels/Publication.cs` [OPEN: 00 §5.2:180 lists Domain folders `… Channels/ Credits/ Compliance/` and no `Publishing/` folder while Application has one (`00-overview.md:196`); the aggregate sits next to `ChannelConnection` in `Domain/Channels/` unless the TL adds `Domain/Publishing/`].

Statuses (`PublicationStatus`, string-converted, values taken from the client model at `10.4-series-autopilot/plan.md:154-155` ✅): `awaiting_consent`, `scheduled`, `uploading`, `processing`, `published`, `failed`, `cancelled`. No `published_private_locked` status (D12) — pre-audit rows end `published` with `privacy = private` and `platform_payload.preAudit = true`.

Creation authority: `Publication.Create(Guid userId, Guid episodeId, Guid channelConnectionId, PublicationMetadata metadata, Guid? autopilotAuthorizationId, DateTimeOffset? vetoEndsAt, string? platformPayloadJson, DateTimeOffset now)` returns `ErrorOr<Publication>`. `metadata.Privacy` already carries the **effective** value (D12: forced to `private` when the create handler reads `YouTubeOptions.AuditStatus != Approved`, §4.1); `platformPayloadJson` carries `{ preAudit: true, requestedPrivacy: "<submitted>" }` only in that case.

Invariants enforced in the aggregate and by the database:

1. One active publication per episode and platform: partial unique `(episode_id, platform) WHERE status NOT IN ('failed','cancelled')` (D6). A second create in the same state → 409 `publication_invalid_state`.
2. `contains_synthetic_media = true` and `selfDeclaredMadeForKids = false` are always written (G5; no API path turns them off).
3. Creating requires the connection to be `active` and owned by the same user; the episode must be `ready` (`EpisodeInvalidState` otherwise) and owned by the same user.
4. Consent is a precondition of upload: `Publication.StartUpload(now)` refuses when `consent_at IS NULL` → `publication_consent_required` (defence in depth, D9; NFR-06-1 in the high-level plan).
5. Metadata is validated **before** the aggregate is constructed (`YouTubeMetadataRules`), so no invalid row can be persisted.
6. `publish_at IS NULL` or `publish_at >= now + YouTubeOptions.PublishLeadMinutes` (D6).
7. `privacy` is the **intended final** visibility (`public|unlisted|private`); the YouTube-side mapping (`private` + `publishAt` on insert, G3) belongs to 06.2.4-T1.

Consent transitions:

- `Publication.Consent(string textVersion, string currentTextVersion, DateTimeOffset now)` — `awaiting_consent → scheduled`, sets `consent_at`, `consent_text_version`, `consent_source = user`; rejects when `textVersion != currentTextVersion` → `consent_text_outdated`; rejects any other source state; raises `PublicationUpdated` with D13's unchanged shape — the Api-side handler never schedules a job itself, a Worker-side subscriber does (YR3, §7).
- `Publication.ConsentFromAuthorization(string textVersion, Guid authorizationId, DateTimeOffset now)` — the autopilot path, used by `AutopilotConsentJob` (06.2.2-T3); same field writes with `consent_source = autopilot`.
- `Publication.Cancel(DateTimeOffset now)` — `awaiting_consent|scheduled → cancelled`; anything else (including `uploading`) → `publication_invalid_state`.
- Both consent methods raise `PublicationUpdated` for the outbox (D13's unchanged shape, no `queueAt` — YR3) and log audit `publishing.consented` / `publishing.consent_autopilot` in the handler (`[Audited]`, D17).

### 3.4 Veto window (TL ruling YR1)

When `AutopilotAuthorizationId is not null`, `Create` stores
`veto_ends_at = min(now + series.autopilot_settings.veto_window_hours, (publish_at ?? now) − YouTubeOptions.PublishLeadMinutes)`,
because 02.8.3 creates late videos with a shortened veto (`02.8.3-cadence-publishing/plan.md:146`; `veto_window_hours` 2..48, default 12, `02.8.3/plan.md:56-63`). If that instant is `< now + YouTubeOptions.MinVetoWindowHours`, `CreatePublicationCommand` returns 400 `validation_failed` with `errors.publishAt` and writes no row — defence in depth, because 02 already treats the slot as missed. Manual publications carry `veto_ends_at = null`. `veto_ends_at` is immutable after create: `PATCH` (06.2.2-T3) only refuses to shrink the remaining window below `MinVetoWindowHours`, and `AutopilotConsentJob` claims rows by `veto_ends_at <= now` (D8).

### 3.2 Value objects and rules

```csharp
// src/NightTales.Domain/Channels/YouTubeMetadataRules.cs
public static class YouTubeMetadataRules
{
    public const int TitleMaxChars = 100;
    public const int DescriptionMaxBytes = 5000;      // G4: bytes, not chars
    public const int TagsMaxLength = 500;             // G4: commas + implicit quotes count
    public const int TagsMaxCount = 15;               // 02 D3
    public const int TagMaxChars = 30;                // 02 D3

    public static void AppendAttribution(ref string description, string? attribution);
    // null/empty → unchanged; else description + "\n\nMusic: " + attribution (C8)
    public static ErrorOr<Success> Validate(string title, string description,
        IReadOnlyList<string> tags, string privacy, DateTimeOffset? publishAt,
        DateTimeOffset now, int publishLeadMinutes);
    public static int TagsLength(IReadOnlyList<string> tags);   // Σ len + 2 per tag containing ' ' + 1 per comma
}
```

`Validate` rules: title 1–100 chars, no `<`/`>`; description non-empty, ≤ 5000 UTF-8 bytes (`Encoding.UTF8.GetByteCount`, **after** `AppendAttribution`), no `<`/`>`; tags count ≤ 15, each ≤ 30 chars, `TagsLength ≤ 500`; `privacy ∈ {public, unlisted, private}`; `publishAt` null or ≥ `now + lead`. Violations are `Error.Validation` with `metadata["field"] ∈ {title, description, tags, privacy, publishAt}` → the 09.1.4-T1 writer turns them into 400 `validation_failed` with the `errors` map.

`ETags.Publication(uint xmin) => $"\"pu-{xmin}\""` is added to the existing helper (06 CR-2) — no second ETag scheme, `Matches` is reused unchanged.

`YouTubeOptions` (section `YouTube`, validated at start, D11) is the **only** source of `PublishLeadMinutes` (120), `MinVetoWindowHours` (2), `AuditStatus`, `ConsentTextVersion` (`pc.v1`), `Quota:UnitsPerDay`; 02 reads the same instance (06 CR-3).

### 3.3 Table and migration (owned here)

`Migrations/<yyyyMMddHHmm>_06_Publications` (expand-only, D20; DDL is the deliverable contract of 06.2.1-T1):

```sql
CREATE TABLE publications (
    id                        uuid PRIMARY KEY,
    user_id                   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    episode_id                uuid NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
    channel_connection_id     uuid NOT NULL REFERENCES channel_connections(id) ON DELETE RESTRICT,
    platform                  text NOT NULL CHECK (platform IN ('youtube','tiktok')),
    status                    text NOT NULL CHECK (status IN
                              ('awaiting_consent','scheduled','uploading','processing','published','failed','cancelled')),
    title                     text NOT NULL,
    description               text NOT NULL,
    tags                      text[] NOT NULL DEFAULT '{}',
    privacy                   text NOT NULL CHECK (privacy IN ('public','unlisted','private')),
    publish_at                timestamptz NULL,
    next_attempt_at           timestamptz NULL,
    veto_ends_at              timestamptz NULL,
    consent_at                timestamptz NULL,
    consent_text_version      text NULL,
    consent_source            text NULL CHECK (consent_source IN ('user','autopilot')),
    autopilot_authorization_id uuid NULL REFERENCES series_autopilot_authorizations(id) ON DELETE SET NULL,
    veto_notice_sent_at       timestamptz NULL,
    contains_synthetic_media  boolean NOT NULL,
    self_declared_made_for_kids boolean NOT NULL DEFAULT false,
    external_post_id          text NULL,
    external_url              text NULL,
    published_at              timestamptz NULL,
    platform_payload          jsonb NULL,
    error_code                text NULL,
    created_at                timestamptz NOT NULL,
    updated_at                timestamptz NOT NULL
);

CREATE UNIQUE INDEX ux_publications_active_episode_platform
    ON publications (episode_id, platform) WHERE status NOT IN ('failed','cancelled');
CREATE INDEX ix_publications_status_next_attempt_at
    ON publications (status, next_attempt_at)
    WHERE status IN ('awaiting_consent','scheduled','uploading','processing');
CREATE INDEX ix_publications_channel_connection_id ON publications (channel_connection_id);
CREATE INDEX ix_publications_user_id_created_at ON publications (user_id, created_at DESC);
```

- Every `publications` column of `../../00-overview.md:290` is present; `veto_ends_at` is the D20 addition, and `published_at` + `external_url` are the two additions proposed by this folder (**06 CR-1 extension**, `[OPEN]` for the TL): `published_at` is what `PublicationsSummaryDto.LastPublishedAt` reads and is written by 06.2.5-T1 at the `→ published` transition (a derived `updated_at` would drift because `TokenHealthJob` also rewrites `external_post_id`, D16); `external_url` is the canonical `https://www.youtube.com/watch?v=<external_post_id>` written with `external_post_id` by 06.2.3-T2, so the DTO carries no URL-building logic.
- `ix_publications_user_id_created_at` serves the 10.4.3-T2 veto list and the owner's publication list; `ix_publications_status_next_attempt_at` serves `AutopilotConsentJob` and `PublishYouTubeJob` batching (D18); the partial unique is the D6 invariant, not a query index.
- `platform_payload` keys are owned by their writers: `uploadSessionEnc` (06.2.3-T2, encrypted, D9), `vetoNoticeEmailId` (06.2.2-T2, D7), `preAudit` + `requestedPrivacy` (**this folder's 06.2.1-T1, at create time** — advisor-fixed 2026-09-25; deciding pre-audit at create, not later, is what keeps the veto notice's preview and the row in agreement from the first read, D12/C4).
- No data migration step (new table) — expand-only, nothing to backfill; no contract step (no legacy writer exists).

## 4. Use cases

### 4.1 `CreatePublicationCommand` (06.2.1-T1)

```csharp
public sealed record CreatePublicationCommand(
    Guid EpisodeId, Guid ChannelConnectionId, string Title, string Description,
    IReadOnlyList<string> Tags, string Privacy, DateTimeOffset? PublishAt,
    Guid? AutopilotAuthorizationId) : ICommand<ErrorOr<PublicationDto>>;
```

Handler steps: (1) tenant-scoped load of the episode + `render_jobs` latest row (`OrderByDescending(j => j.Id)`, WT49) to read `params.music.attribution`; (2) load the connection, require `active` and owned → 409 `channel_not_active`; (3) episode `ready` → else 409 `episode_invalid_state`; (4) `AppendAttribution` then `YouTubeMetadataRules.Validate` against the **submitted** privacy → 400 `validation_failed` with field keys; (5) autopilot branch: `vetoEndsAt = min(now + series.autopilot_settings.veto_window_hours, (publishAt ?? now) − PublishLeadMinutes)` (YR1/§3.4), 400 `validation_failed` field `publishAt` when it falls below `now + MinVetoWindowHours`; the series settings are read from 02-owned tables (read-only here); (6) **pre-audit branch (D12/C4, advisor-fixed 2026-09-25):** read `YouTubeOptions.AuditStatus`; `!= Approved` → effective `privacy = "private"`, `platformPayloadJson = { preAudit: true, requestedPrivacy: <submitted> }`; else effective `privacy` = submitted, no stamp — deciding this before `Publication.Create` is what keeps 06.2.2-T2's notice (queued moments later) truthful from the first read; (7) `Publication.Create(...)` with the effective metadata + stamp; (8) add + save (UnitOfWork transaction); (9) audit; (10) outbox.

- Persistence surface: this folder declares **no** per-aggregate repository port — the write side is 09's `IAppDbContext` (`db.Publications`, `db.Query<Publication>()`, tenant filter for free), the pattern used by `09.4.2-5.3-queries/plan.md:196`. `06.2.2-T3` adds only `IPublicationConsentReader.ClaimDueAsync(now, batch, ct)` (its own `FOR UPDATE SKIP LOCKED` system-job claim, the `ISystemWorkQuery` style of `09.2.3-T1`) and reuses `db.Query<Publication>()` for the per-row work.
- Validation: FluentValidation mirrors step 4 for shape only (`Title` not empty/≤ 100, `Tags` count, `Privacy` in set, `PublishAt` round-trip); the byte-level rules live in the domain so a second caller cannot bypass them.
- Authorization: authenticated user; `RequireFeature("youtube_publish")`; owner-only through the tenant filter (404 for another user's episode/connection).
- Idempotency: no `Idempotency-Key`; the partial unique makes a duplicate create a 409 `publication_invalid_state`, which is the desired answer for the orchestrator's retry (C1 maps it to run `failed`).
- Errors: 400 `validation_failed`; 404 `not_found`/`feature_disabled`; 409 `episode_invalid_state` / `channel_not_active` / `publication_invalid_state`.
- 02 integration: 02.8.3 calls this **in-process** (`02.8.3/plan.md:455`, C1), so `AutopilotAuthorizationId` is a command-only parameter and is not accepted from the public route body ([INFERENCE], consistent with the orchestrator being a Worker job with no bearer token).

### 4.2 Consent, cancel, queries

| Use case | Request | Effect | Idempotency / errors |
|---|---|---|---|
| `ConsentPublicationCommand` (06.2.2-T1) | `POST /publications/{id}/consent` `{ consentTextVersion }` + `If-Match` | `awaiting_consent → scheduled`; `consent_at`, `consent_text_version`, `consent_source = user`; audit `publishing.consented`; enqueue `PublishYouTubeJob` at the computed queue time; `Episode.MarkScheduled` (D12) | Re-consent on an already `scheduled` row is a no-op returning the same DTO (202-style repeatable read of the same ETag value); wrong text version 400 `consent_text_outdated`; missing header 428 `if_match_required`; stale ETag 412 `publication_conflict`; non-`awaiting_consent` state 409 `publication_invalid_state` |
| `CancelPublicationCommand` (06.2.6-T1) | `DELETE /publications/{id}` + `If-Match` | `awaiting_consent|scheduled → cancelled`; quota release (§7); audit `publishing.cancelled` | Repeated cancel returns 204 (already `cancelled` ⇒ success, no second event); `uploading|processing|published` → 409 `publication_invalid_state` with a body telling the client to use YouTube Studio; stale ETag 412 `publication_conflict` |
| `CancelPublicationByLinkCommand` (06.2.2-T2) | `POST /publications/cancel-by-link` `{ publicationId, token }` (anonymous, rate policy `auth`) | same effect as cancel, no `If-Match` (the token is the proof), audit `publishing.cancelled` with `actor = system:link` | Token row claimed with `UPDATE user_action_tokens SET used_at = now() WHERE token_hash = @h AND used_at IS NULL AND expires_at > now() RETURNING id` (single use); any failure → 400 `cancel_token_invalid` (one generic code, constant-time comparison, no token echo, no enumeration); a publication no longer in `awaiting_consent` → 400 `cancel_token_invalid` (never leaks the state) |
| `GetEpisodePublicationsQuery` (06.2.1-T1) | `GET /episodes/{id}/publications` → `{ items: PublicationDto[] }` | owner-scoped read; no paging (a handful per episode, D6) | — |
| episode-detail summary (06.2.1-T1) | inside `GetEpisodeByIdQuery` | one extra indexed query returning `PublicationsSummaryDto(Published, Scheduled, LastPublishedAt)` | read-only, no event (09.4.2 `plan.md:190`) |

Consent text: `YouTube:ConsentTextVersion` = `pc.v1` (D7/D11); the texts are `Templates/Consent/pc.v1.<locale>.md` (embedded, rendered by 10.7.2, owned by 13.1.x/10 — handed over, not implemented here). The command only compares the version string.

`PublicationDto` (D6, extended by **06 CR-11** — now official per README §4, `preAudit` + `requestedPrivacy`, 10.4 client model updated by the TL) — single wire shape for the list, create and consent responses:

```csharp
public sealed record PublicationDto(Guid Id, Guid EpisodeId, PublicationChannelDto Channel, string Status,
    string Title, string Description, IReadOnlyList<string> Tags, string Privacy, bool PreAudit,
    string? RequestedPrivacy, DateTimeOffset? PublishAt, DateTimeOffset? VetoEndsAt, DateTimeOffset? ConsentAt,
    string? ConsentSource, string? ExternalPostId, string? ExternalUrl, string? ErrorCode,
    DateTimeOffset? NextAttemptAt, string Etag);
public sealed record PublicationChannelDto(Guid Id, string DisplayName);
```

**06 CR-11 (YR20; semantics finalized 2026-09-25 after `Y06Veto`'s redesign).** `Privacy` is always the row's *effective* value (`"private"` while `PreAudit` is true, permanently — 06.2.1-T1 stamps `PreAudit`/`RequestedPrivacy` once at create and nothing ever writes them again, not this folder, not 06.2.4-T1, not 06.2.2-T3). `RequestedPrivacy` is a frozen **historical** record of what the owner originally asked for, non-null only while `PreAudit` is true — not a live input: 06.2.2-T3's `ApplyOwnerEdit` rejects a `privacy`-changing `PATCH` outright (409 `publication_privacy_locked`) while `PreAudit` is true rather than writing anywhere, so the field's only purpose is letting the UI explain *why* the row is locked and what the owner originally requested, not to signal an editable value. Without it the lock is unexplained: the owner sees `privacy = "private"` with no record of having asked for something else and no reason given. **This is a wire-contract change and needs its own CR to be applied**: `../README.md` §3 D6's `PublicationDto` shape, and `10-web-ui/10.4-series-autopilot/plan.md:156-158`'s client `Publication` TypeScript model (add `preAudit: boolean; requestedPrivacy: PublishPrivacy | null`, additive, non-breaking) — until that CR lands, this DTO shape is this folder's proposal, not yet reflected in either target document.

No token, no channel OAuth id, no other `platform_payload` key and no `autopilot_authorization_id` in the DTO (D17: tokens never leave the process; the client does not need the authorization id).

## 5. API and events

| Method | Route | Status | Notes |
|---|---|---|---|
| POST | `/api/v1/episodes/{id}/publications` | 201 + `ETag: "pu-<xmin>"` + `Location` | body `{ channelConnectionId, title, description, tags[], privacy, publishAt? }`; `[Audited]` |
| GET | `/api/v1/episodes/{id}/publications` | 200 `{ items }` | no paging (D6) |
| POST | `/api/v1/publications/{id}/consent` | 200 + `ETag` | body `{ consentTextVersion }`; `RequireIfMatch()`; `[Audited]` |
| DELETE | `/api/v1/publications/{id}` | 204 | `RequireIfMatch()`; `[Audited]` |
| POST | `/api/v1/publications/cancel-by-link` | 204 | anonymous, rate policy `auth`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |
| (existing) | `/api/v1/episodes/{id}` | 200 | `publications` block filled by the third query (C7) |

- Every route sits behind `RequireFeature("youtube_publish")` (404 `feature_disabled`, D1/C6) and returns 404 `not_found` for another user's row (tenant filter, D17).
- `If-Match` handling reuses 09.1.4-T1's `RequireIfMatch()` filter and `ETags.Matches`; the comparison is strong and exact (`*` rejected), so a stale tab cannot consent (F04). A `DbUpdateConcurrencyException` that escapes the compare is mapped by the UnitOfWork to 409 `concurrency_conflict` (09 README:168).
- Error codes needed beyond the existing catalogue: `publication_invalid_state` (409, wrong status and the duplicate-active create), `publication_conflict` (412, stale `If-Match`), `channel_not_active` (409), `publication_privacy_locked` (409, owned by 06.2.2-T3's `ApplyOwnerEdit`, YR20) — plus the D7-mandated `consent_text_outdated` (400) and `cancel_token_invalid` (400). `consent_text_outdated` and `cancel_token_invalid` are decided in D7; the first three publication/channel codes plus `publication_privacy_locked` are **06 CR-9** (YR5 — not CR-2), mirroring the 09 pair `episode_invalid_state` 409 / `episode_conflict` 412 (`09-backend-api/README.md:360`).
- Outbox events (D13): `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }` on every status change (create → `awaiting_consent`, consent → `scheduled`, cancel → `cancelled`); `email_requested.v1` from 09.2.6-T3 for both e-mails. No new event names.
- SignalR: `IRealtimeNotifier` `PublicationUpdated` with `{ publicationId, episodeId }` and **no** `RealtimeData` (`RealtimeData` stays reserved for `RenderProgress`, WT54) — the handler is 09.8.1-T1's, fed by the outbox handler.
- OpenAPI: additive; no breaking change to 09's schema. The 201 response declares the `ETag` header.

## 6. UI

Consumers only; no UI work in this folder.

| Consumer | What it uses | Status |
|---|---|---|
| `10.4-series-autopilot/10.4.3-T2` (veto row) | `Publication { …, etag }` for the veto list; `POST /publications/{id}/consent` and `DELETE /publications/{id}` from the row; `PATCH` + `If-Match` with 412 → `ConflictDialog` (C9, `10.4-series-autopilot/plan.md:156-159,249-252`) | signed plan 10 wave 1 ✅ |
| episode detail (`10.2.x`) | `publications` block (`PublicationsSummaryDto`) + the ETag of the episode is unaffected | `PublicationsSummaryDto` field list is `[INFERENCE]` at `09.4.2-5.3/plan.md:167`; this folder is the consumer-side confirmation of `(Published, Scheduled, LastPublishedAt)` |
| consent dialogue | text `pc.v1` rendered by `10.7.2-T1` from `Templates/Consent/pc.v1.<locale>.md`; the create/consent screens render `consent_text_version` verbatim so the 400 is practically unreachable | hand-over to 10 wave 2 |
| `/publications/cancel#p=…&t=…` page | the veto e-mail's cancel link target; token in the fragment (never in the query string, never sent to the server as a header) | **owner is a 10-wave-2 task, id not yet assigned** — `[OPEN]`, record as a CR to `10-web-ui/README.md` when that folder is detailed |
| copy keys handed over (vi/en) | `publication.create.*` (form labels, `publishAt` hint "Scheduled on YouTube for …"), `publication.consent.*` (dialogue title, `pc.v1` body, approve), `publication.veto.*` (list title, remaining time, cancel), `publication.cancel.*` (notice, "uploaded — manage in YouTube Studio"), `publication.error.publication_invalid_state` / `.publication_conflict` / `.consent_text_outdated` / `.cancel_token_invalid` / `.channel_not_active` / `.episode_invalid_state` / `.feature_disabled` | key names are the deliverable; the catalogue files are 10's |

Client rules handed over: 412 `publication_conflict` → refetch the row and re-render (never re-send the same `If-Match`); 409 `publication_invalid_state` → refetch (the state, and with it the allowed actions, lives on the row, not in the error body — 09 README:191 is a closed envelope with no action list).

## 7. Jobs and integrations

| Job / call | Owner | Interaction with this folder |
|---|---|---|
| `PublishYouTubeJob` (queue `publish`) | 06.2.3-T2 | enqueued by `06.2.3-T2`'s `PublicationScheduledForUploadHandler`, an outbox subscriber to `publication_updated.v1` (D13's unchanged shape) confirmed **implemented**, not merely proposed, via IRC with `Y06Upload` 2026-09-25: it re-reads the row, derives `queueAt = NextAttemptAt ?? QueueTime(PublishAt, PublishLeadMinutes, now)` and calls its own `IPublishScheduler.EnqueueAtAsync(publicationId, when, ct)`; not called directly by `ConsentPublicationHandler` (the Api's `IJobScheduler` registration throws, `09-backend-api/README.md:288`); re-checks consent via `Publication.StartUpload`, which this folder's aggregate implements. **Flagged, not resolved here**: a per-attempt dedup gap against at-least-once redelivery (D9's quota-reserve-before-session-commit ordering makes a duplicate `EnqueueAtAsync` call for the same attempt unsafe, not merely wasteful) — reported to `Y06Upload` 2026-09-25.
| `AutopilotConsentJob` (recurring 1 min) | 06.2.2-T3 | calls `Publication.ConsentFromAuthorization` (implemented here) and the same `publication_updated.v1`/audit path, so there is exactly one consent implementation |
| `SendEmailJob` (queue `email`) | 09.2.6-T3 | renders `publication_consented.<locale>` / `publication_veto_notice.<locale>` and runs `PublicationCancelTokenMinter` (06.2.2-T2) at send time (WT17); 06 CR-6 adds `AuthLinks.PublicationCancel(origin, publicationId, token)` (token after `#` — the invariant `09.3.3-T1:173` already pins) and the two catalog rows; the origin is the 09-owned `App:PublicOrigin`, supplied to minters at send time (`09.2.6-T3:109`) ✅ — never a 06-local key |
| `TokenHealthJob`, disconnect, deletion | 06.4-* | reads/cancels publications; `DELETE /channels/{id}` cancels `awaiting_consent|scheduled` rows (D15) — the same `Cancel` domain method |
| `IQuotaLedger` (`TryReserveAsync`/`ReleaseAsync`) | 06.3.1-T1 | 06.2.6-T1 calls `ReleaseAsync` for a reservation recorded in `platform_payload.quotaReservedDay`; the upload job clears/owns that key (D10) — `[OPEN]`: confirm `quotaReservedDay` as a second `platform_payload` key owned by 06.2.3-T2, or drop the release call from 06.2.6-T1 at DoR |

- Pre-audit visibility (D12/C4) needs no consent-time planner: 06.2.1-T1 decides and stamps `platform_payload.preAudit`/`requestedPrivacy` and the effective `privacy` **at create**, once, permanently (advisor-fixed 2026-09-25 — an earlier draft deferred the flip to a consent-time "upload planner", which both contradicted D12's actual rule and left the veto notice, queued at create, previewing the wrong privacy; a later proposal for a queue-time correction was also retracted 2026-09-25 — confirmed with `Y06Status`: per `02.8.3-T3:156` a pre-audit slot stays private for that publication forever, only the *next* autopilot run goes public). 06.2.2-T1 and 06.2.4-T1 only ever read `platform_payload.preAudit`/`requestedPrivacy`; neither writes them. `PublicationDto` exposes both (06 CR-11, §4.2) so a `PATCH` attempt is explained, not silently accepted — YR20 closes the earlier write-path question: nothing ever writes `requestedPrivacy` after create; a `PATCH` to a non-private value on a pre-audit row is refused outright, 409 `publication_privacy_locked` (06.2.2-T3's `ApplyOwnerEdit`). The `IJobScheduler` question is **resolved, not open**: YR3 (below).

Failure map for the three commands (all `ErrorOr`, no exceptions across the boundary):

| Failure | Answer |
|---|---|
| Metadata invalid (title/description/tags/privacy/publishAt) | 400 `validation_failed` with field keys; no row written |
| Episode not `ready`, or another user's episode | 409 `episode_invalid_state` / 404 `not_found` |
| Connection not `active` | 409 `channel_not_active` |
| Active publication exists / wrong state for the command | 409 `publication_invalid_state` |
| `If-Match` missing / stale | 428 `if_match_required` / 412 `publication_conflict` |
| Consent text version differs from `YouTube:ConsentTextVersion` | 400 `consent_text_outdated` (client refetches the text) |
| Cancel token unknown/expired/used, or the row left `awaiting_consent` | 400 `cancel_token_invalid` (single generic code) |
| Flag off | 404 `feature_disabled` |
| E-mail queueing fails (template/minter error before the row is built) | the **whole** command fails — no `Publication`/`Episode` write either, since queueing now runs inside the same transaction as the aggregate it extends (YR9; supersedes an earlier "command still commits" draft); a delivery failure *after* queueing (SMTP down) is unaffected — `SendEmailJob` retries 5× (09.2.6-T3) and 06.2.2-T3 treats an unsent veto notice as "no consent" (C2) |
| Concurrency (`DbUpdateConcurrencyException` after commit attempt) | 409 `concurrency_conflict` (09 UnitOfWork mapping) |

Timeouts and bounds: both commands are single-transaction, 2–4 indexed statements plus the outbox/email-message inserts (YR3/YR9), no external call and no `IJobScheduler` call from this folder — no HTTP call to Google happens in this folder, so there is no external timeout to configure here. Performance: the create path is O(1) queries + one `render_jobs` read; the summary is one aggregate query; no N+1 anywhere; batches belong to the sibling jobs.

## 8. Security and privacy

| Threat | Control |
|---|---|
| A stale browser tab consents to metadata the user never saw | `If-Match: "pu-<xmin>"` is required on consent and cancel (D7/F04); missing → 428, mismatched → 412 |
| A stolen session publishes silently | every consent sends `publication_consented` (C6, 06.2.2 high-level L66); re-consent of an already-`scheduled` row is a documented no-op, not a second upload |
| A cancel link leaks via Referer/logs/history | token lives in the fragment (`#p=…&t=…`), the minted row stores only the hash, the route is `Referrer-Policy: no-referrer` + `Cache-Control: no-store`, single use via `used_at`, expiry `min(veto_ends_at, now + 48 h)` (YR8) plus a `subject_id` check (`ConsumeAsync` returns the subject, the route requires it equal `publicationId`) — the token row is scoped to one publication by construction, not just by the status guard, 09.3.3-T1 DDL as extended by YR8 |
| Foreign user or foreign object access | tenant filter on `IUserOwned` for every read (404 for others); the connection, episode and publication are all owner-scoped; no route accepts a user id |
| Feature shipped half-off | `RequireFeature("youtube_publish")` on every route and job registration (D1) |
| Secrets or PII in logs | no title, description, tag, channel name, token or session URI in any log line or audit `data`; audit actions carry ids and codes only (`publishing.publication_created`, `publishing.consented`, `publishing.cancelled`, all `[Audited]`); the code and token are redacted by the 09.9.3-T2 processors |
| Cross-site request forgery on the bearer routes | all four authenticated routes need a bearer token (no cookie auth), so the CSRF surface is the cookie-auth admin surface only; `cancel-by-link` is unauthenticated by design and its only capability is "cancel this one publication" |
| Data retention | publications are kept after disconnect (`IDeletionScopeHandler` for `channel` nulls tokens/metadata but keeps rows, D15) and after account deletion are removed by the `user_id` cascade; the summary query never exposes another user's rows |

## 9. Test plan

Projects (09 README D2): `tests/NightTales.Domain.UnitTests`, `tests/NightTales.Application.UnitTests`, `tests/NightTales.Api.IntegrationTests` (all DB/HTTP tests, in-process Worker), `tests/NightTales.TestKit` (fakes/fixtures). xunit.v3 + Shouldly; no mocking library; real PostgreSQL via Testcontainers; `FakeClock` for every time assertion; `FakeGoogleServer` is not needed by this folder (no Google call happens here) — it is listed only as the D19 asset used by the sibling upload folder.

Unit:

- `YouTubeMetadataRulesTests`: 5001-byte description rejected after appending a long attribution (`AppendAttribution` runs first); a 5000-byte description that becomes 5001 with `\n\nMusic: …` rejected; `TagsLength("a b")` counts the implicit quotes; commas count; 16 tags rejected; a 31-char tag rejected; `<`/`>` rejected in title and description; `publishAt` at `now + 119 min` rejected, at `now + 121 min` accepted, `null` accepted.
- `PublicationTests`: illegal transitions refused (`cancel` while `uploading`, `consent` while `scheduled` no-op semantics, `StartUpload` without `consent_at`); `ConsentFromAuthorization` writes `consent_source=autopilot`; create sets `contains_synthetic_media=true` and `selfDeclaredKid=false` unconditionally.
- `ETagsTests` in `NightTales.Application.UnitTests` (`ETags` lives in `Application/Common`, so this assertion cannot sit in the Domain test project; added to 09.1.4-T1's suite by 06 CR-2): `ETags.Publication(7) == "\"pu-7\""`; `Matches("\"pu-6\"", "\"pu-7\"")` false; `Matches("W/\"pu-7\"", …)` false; `Matches("*", …)` false.

Application:

- `CreatePublicationHandlerTests` (Application.UnitTests, handler-level with TestKit doubles): episode not `ready` → `EpisodeInvalidState`; connection `revoked` → `channel_not_active`; attribution `null` leaves the description untouched; attribution present appends exactly `\n\nMusic: <value>`; the veto window is `min(now + veto_window_hours, (publishAt ?? now) − PublishLeadMinutes)` and a shortened window below `MinVetoWindowHours` yields 400 `validation_failed` field `publishAt` with no row written (YR1); a manual create leaves `veto_ends_at` null.
- `ConsentPublicationHandlerTests`: version `pc.v2` → `consent_text_outdated`; version `pc.v1` on `awaiting_consent` → `scheduled` with the three consent fields and one `publication_updated.v1`; consent on `cancelled` → `publication_invalid_state`.
- `CancelPublicationHandlerTests`: `scheduled → cancelled` + audit + one outbox event; second cancel is a success no-op (no second event); `uploading` → `publication_invalid_state`; release called when (and only when) `platform_payload.quotaReservedDay` is present.

Integration (`NightTales.Api.IntegrationTests`, real PostgreSQL):

- Create → 201 with `ETag: "pu-<xmin>"`, the row committed with the byte-exact description and the partial unique present (`SELECT indexdef` assertion); second create → 409 `publication_invalid_state`; another user's episode → 404; flag off → 404 `feature_disabled`; `publishAt` 1 h ahead → 400 `validation_failed` with `errors.publishAt`.
- Consent: no `If-Match` → 428; stale value → 412; correct value → 200, `status='scheduled'`, `consent_at` set, `consent_source='user'`, `consent_text_version='pc.v1'`, one `outbox_messages` row `publication_updated.v1` whose payload carries ids/status only, one `audit_log` row `publishing.consented`.
- Cancel: `DELETE` with `If-Match` → 204 and `status='cancelled'`; after upload started (row forced to `uploading`) → 409.
- Cancel-by-link: mint a token through the `IEmailTokenMinter` path (or insert a hashed row directly), first call → 204 + `used_at` set; second call with the same token → 400 `cancel_token_invalid`; expired token → 400; a token for another user's publication → 400 (never 404, never a state leak); rate policy `auth` returns 429 after the configured burst.
- Episode detail: the publications block is non-null and equals the three aggregated numbers for seeded rows (`Published=2, Scheduled=1, LastPublishedAt=…` from `published_at`), and the request emits exactly three queries (asserted with `ReadDbContext` command interception, C7).

e2e: none here (Playwright owns the UI paths in 11.5.1-T1).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.2.1-T1 | Publication aggregate, create command, YouTube limits, attribution, ETag, episode summary | M (1.0) | 06.1.2-T2, 09.5.3-T1, 09.7.1-T1, 05.3.2-T3 (soft) | 06.2.2-T1, 02.8.3-T3, 10.7.2-T1 (10 CR-15), 07.2.2-T1 (07 CR-11), 10.7.2-T0 |
| 06.2.2-T1 | Consent command + domain guard (`pc.v1` text, If-Match) | S (0.5) | 06.2.1-T1 | 06.2.2-T2, 06.2.6-T1, 06.2.3-T2, 10.7.2-T1 (10 CR-15), 07.2.2-T1 (07 CR-11) |
| 06.2.2-T2 | Consent e-mail, veto notice e-mail with single-use cancel link, cancel-by-link route | S (0.5) | 06.2.2-T1, 09.2.6-T3, 09.3.3-T1 | 06.2.2-T3, 10.7.4-T2 (10 CR-15), 07.2.2-T2 (07 CR-11) |
| 06.2.6-T1 | Cancel command (before upload) + quota release | S (0.5) | 06.2.2-T1, 06.3.1-T1 | 06.4.1-T1, 10.7.4-T1 (10 CR-15), 13.3.2-T1 (13 CR-17) |

The table is identical to `../README.md` §8.1 (canonical). Folder total 2.0 d.

Re-estimate vs the high-level plan (`../../06-youtube-posting.md`, `../README.md` §9): 06.2.1 was `S` ≈ 0.5 in the high-level split and is `M (1.0)` here; the +0.5 is the ETag/attribution/summary/byte-accurate-limits work listed in §9 of the review README, already counted in the plan-06 total of 15.25 P / 16.5 d. No further deviation is proposed by this folder.

## 11. Rollout

- **Flag:** all four routes behind `RequireFeature("youtube_publish")`; the jobs in §7 are registered only while the flag is on (D1, as 02.8.3-T1). Default off in staging until the 06-connect gate passes.
- **Migration order:** `_06_Publications` (this folder) after `_06_ChannelConnections` + `_06_OAuthStates` (06.1.2-T1/T2) because of the FK on `channel_connections`, and after `_06_PlatformQuotaUsage` is not required (no FK). Expand-only: no backfill, no contract step, so it can ship independently of the code and is reversible by dropping the table while the flag is off.
- **Monitoring:** `publication_updated.v1` counts by status; a `failed` rate alert on plan 06's P1 budget; `email_messages` failure rate for the two new templates (alert if `publication_veto_notice` fails for a series, since an undelivered notice blocks consent by design, C2); `cancel-by-link` 400 rate (a spike means a broken link or a brute-force attempt).
- **Rollback:** flip the flag off — routes answer 404 `feature_disabled`, jobs stop being registered, existing rows stay readable in the DB with no orphan writer; no destructive migration to undo.
- **Gates touched:** 06-publish (manual publication → consent → e-mail → upload; an autopilot publication passing the veto window) needs `06.2.1-T1` + `06.2.2-T1/T2`; 06-audit-ready needs the evidence pack, which quotes the `pc.v1` text version and the consent rows written here.
