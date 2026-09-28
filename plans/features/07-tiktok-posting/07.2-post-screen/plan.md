# 07.2 — Post screen backend: creator info, validation matrix and consent

> Contract: `../README.md` §0 (07.2), §1 K/C, §1.3 U1–U13, §2, §3 D1–D19, §4 CR-2/3/7, §7 gates, §8.1 canonical rows, §10 TT3/6/7/9/10/11.
> Tasks: **07.2.1-T1** (`GetTikTokCreatorInfoQuery`, `IExternalLookup` member, `GET /channels/{id}/tiktok/creator-info`), **07.2.2-T1** (`CreateTikTokPublicationCommand`, `TikTokPostSettings` matrix, pre-audit `SELF_ONLY`, two consent variants, variant-bound consent), **07.2.2-T2** (consent e-mail).
> Siblings `../07.1-app-connect/`, `../07.3-publish/`, `../07.4-audit/`. No UI task here: the screen is **10.7.3-T1**, whose **10.7.3-T0** (UI design, Area UX) must be approved first; §1.3 U1–U13 is that design's input and this folder's server-side acceptance list.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Reference convention: **`F-<n>` = this plan §2**; `K<n>`/`C<n>`/`F<n>`/`D<n>`/`TT<n>`/`U<n>` = `../README.md` (K §1.1, C §1.2, F §2, D §3, TT §10, U §1.3).

## 1. Scope
| Surface | Owner |
|---|---|
| `GET /channels/{id}/tiktok/creator-info`, `GetTikTokCreatorInfoQuery`, the `IExternalLookup` member + implementation, the `creator_info` bucket of `ITikTokRateLimiter` | 07.2.1-T1 |
| `CreateTikTokPublicationCommand`, `TikTokPostSettings`, `TikTokMetadataRules`, `Publication.CreateTikTok`, the `tiktok` object on `POST /episodes/{id}/publications`, variant derivation + variant-bound consent, `GET /publications/consent-text?locale=&variant=`, the consent texts | 07.2.2-T1 |
| The TikTok deliverable of the `publication_consented` e-mail (variant copy vi/en, allow-listed params) | 07.2.2-T2 |

Implements **D4**, **D5**, **D8**, **D11** and the D3/D6/D10/D15/D16 parts touching these tasks. Out of scope: publish/poll jobs, `fail_reason` map, caps/alerting (07.3); app, scopes, domain verification, connect/disconnect, re-auth, `TokenHealthJob`, `OAuthErrorClassifier` (07.1); evidence pack + `AuditStatus = approved` (07.4); the screen (10.7.3-T0/T1); the filing (13.4.3-T1); `POST /v2/post/publish/cancel/` and the `video.upload` inbox stay [OPEN] (README §11); autopilot never targets TikTok (D3, CR-5). No table/migration: writes `publications`, reads `channel_connections`/`episodes`/`assets` (06 `_06_*` + 09.2.2). No `credit_ledger` row, no refund (D16/F19).

## 2. Confirmed facts
| Id | Fact | Source |
|---|---|---|
| F-1 | Creator state: `POST /v2/post/publish/creator_info/query/`, scope `video.publish`, **20/min per token**; wire `creator_nickname`, `creator_avatar_url` (2 h TTL), `privacy_level_options`, `comment_disabled`, `duet_disabled`, `stitch_disabled`, `max_video_post_duration_sec`; public options `PUBLIC_TO_EVERYONE`/`MUTUAL_FOLLOW_FRIENDS`/`SELF_ONLY`, private `FOLLOWER_OF_CREATOR`/`MUTUAL_FOLLOW_FRIENDS`/`SELF_ONLY`; `spam_risk_*`/`reached_active_user_cap` come back **with HTTP 200** | ✅ K2 |
| F-2 | Init facts the validator enforces: `privacy_level` required, ∈ the option set; caption **≤ 2200 UTF-16 runes**; `disable_duet`/`disable_stitch`/`disable_comment`; `brand_content_toggle` **required**; `brand_organic_toggle`; `is_aigc`. Unaudited client: private only, ≤ 5 posting users/24 h, only `SELF_ONLY`. Other wire 403s: `privacy_level_option_mismatch`, `spam_risk_too_many_posts`, `spam_risk_user_banned_from_posting`, `unaudited_client_can_only_post_to_private_accounts` | ✅ K3, K9, K1 |
| F-3 | Creator-info contract: `GET /channels/{id}/tiktok/creator-info` → `TikTokCreatorInfoDto { channelId, nickname, avatarUrl, privacyLevelOptions[], commentDisabled, duetDisabled, stitchDisabled, maxVideoPostDurationSec, canPost, blockReason? }`; nothing persisted, no cross-user cache; `canPost = false` + `blockReason` on a non-`ok` `error.code` even at HTTP 200, or any cap; token via `ITokenService` (refresh < 5 min); ownership check (another user's channel → 404), 09's `paid` policy; `IExternalLookup` is the one documented CQRS exception (00 §5.2:211) and 07.2.1-T1 adds `GetTikTokCreatorInfoAsync(connectionId, ct)` to it — extend the interface if 09 declared it, never a second one | ✅ D11, D6, F15, D14, TT11 |
| F-4 | Required UX enforced server-side: U1 nickname per render · U2 stop when the creator cannot post · U3 duration ≤ the creator limit · U4 caption ≤ 2200 UTF-16, nothing appended · U5 privacy only from the option set, no default · U6 interactions off by default, greyed when creator-disabled · U7 disclosure off by default, ≥ 1 flag when on · U8 branded ≠ `SELF_ONLY` · U9 the declaration variant · U10 upload only after the express consent click · U12 `is_aigc = true` · U13 `SELF_ONLY` only while unaudited | ✅ §1.3 |
| F-5 | `CreateTikTokPublicationCommand(episodeId, channelConnectionId, caption, TikTokPostSettings settings, consentTextVersion)`; no date/time field; `publish_at = consent_at` at consent, ordering only, never read as a publish time; TikTok gets no `publishAt` | ✅ D3, TT6, CR-9 |
| F-6 | `TikTokPostSettings { privacyLevel, disableComment, disableDuet, disableStitch, disclosureEnabled, brandOrganic, brandedContent }`; privacy ∈ options and non-empty; `brandedContent ⇒ ≠ SELF_ONLY`; `!disclosureEnabled ⇒ both false`; `disclosureEnabled ⇒ ≥ 1`; `disableDuet`/`disableStitch` false only when the creator did not disable them; duration over the limit → 400 `tiktok_video_too_long`. Closed defaults (TT7): a missing `privacyLevel`, a `false` `disableX` where the creator disabled it, or `disclosureEnabled = true` with no option → 400 `tiktok_invalid_post_settings` | ✅ D5, TT7 |
| F-7 | Variants `tt.v1.music` (no commercial / "Your brand" only) and `tt.v1.branded` (branded, alone or with "Your brand"); embedded `Templates/Consent/tt.v1.<variant>.<locale>.md` vi/en; `TikTokOptions.ConsentTextVersion` lists them; the variant is derived from the disclosure selection, never a client choice (disagreement → 400 `consent_text_outdated`) | ✅ D5, TT9 |
| F-8 | Metadata: `publications.privacy` = the wire enum verbatim (`SELF_ONLY`, `MUTUAL_FOLLOW_FRIENDS`, `PUBLIC_TO_EVERYONE`, `FOLLOWER_OF_CREATOR`); `platform_payload.tiktok` = `{ privacyLevel, disableComment, disableDuet, disableStitch, brandContentToggle, brandOrganicToggle, isAigc, publishId?, videoUrlExpiresAt? }`, never tokens; `description`/`tags` null. Columns in play: `status, title, description, tags, privacy, publish_at, next_attempt_at, consent_at, consent_text_version, consent_source, autopilot_authorization_id, veto_notice_sent_at, veto_ends_at, contains_synthetic_media, external_post_id, platform_payload jsonb` | ✅ D8, F17, C2/C3 |
| F-9 | Consent: `POST /publications/{id}/consent { consentTextVersion }` → `awaiting_consent → scheduled`; stale version → 400 `consent_text_outdated`; `consent_source = user`; audit `publishing.consented`; `Episode.MarkScheduled` same transaction; `If-Match` with `ETags.Publication` `"pu-<xmin>"`. `GET /publications/consent-text?locale=` gains `variant=music\|branded` → `{ version, text }`; the create body gains the optional `tiktok` object (`disableComment`, `disableDuet`, `disableStitch`, `disclosureEnabled`, `brandOrganic`, `brandedContent`); `title` doubles as the caption; `description`/`tags`/`publishAt` are null for TikTok | ✅ C7, CR-2, F20 |
| F-10 | Codes: CR-3 for 09 §5.4 — `tiktok_invalid_post_settings`/`tiktok_video_too_long` 400, `tiktok_cannot_post_now` 409, `tiktok_account_not_private` 409 (07 CR-14), `tiktok_creator_cap`/`tiktok_rate_limited` 429, `consent_text_outdated` 400; new publication `error_code` values live once in Domain `PublicationErrorCodes` with the `tiktok_` prefix (D10 is the single code table); one vi/en copy key per D10 code (`errors.tiktok.*`), the screen shows `blockReason` when `canPost = false` | ✅ CR-3, TT3, D10, D18 |
| F-11 | Reused 06 blocks: `oauth_states` + `browser_nonce_hash`, client pinning, `ITokenProtector` (`tokens.v1/{connectionId}`), `platform_incidents`, `publication_updated.v1`/`channel_connection_changed.v1`, `IEmailSender` + catalog, `[JobPolicy]`, `ISystemWorkQuery.PendingAsyncT`, `ETags.Publication` + `If-Match`, `RequireFeature`, `Episode.MarkScheduled/MarkPublished`, `IRealtimeNotifier`; not reused: cancel-by-link, veto machinery; no `credit_ledger` row, no refund | ✅ D15, D16 |
| F-12 | Pipeline: every route behind `RequireFeature("tiktok_publish")`, `Idempotency-Key` on POSTs that call a platform, ETag/If-Match on updates, policies `paid` (BD14) and `auth`, `RequireIfMatch()` → 428 `if_match_required`, `RequireIdempotencyKey()` → 400 `idempotency_key_required`; duration = `assets.duration_sec numeric(9,2)` via `episodes.final_video_asset_id` (written by `IngestRenderOutputJob` from `output.narration_seconds`); `ErrorCodes` is a closed class and the status comes from `Api/Http/ErrorCatalogue.cs` (a code absent there fails a test; `validation_failed` + `field` is the request-validation code); `FakeTikTokServer` (TestKit, Kestrel, hand-written) serves both option sets, the 200-error families and per-endpoint counters | ✅ D1, C4, D17; 09's `paid` policy + `RequireIfMatch`/`RequireIdempotencyKey` = `09.6.1/plan.md:110` and 09 D7; duration source = 00 §6:288 + `09.6.3-T2:38`; `ErrorCodes`/`ErrorCatalogue` = `06.3.1-T1:159` |
| F-13 | ⚠️ D6 defines `ITikTokRateLimiter` + the `creator_info` bucket, but §8.1 attaches "rate limiter" to **07.3.1-T1** (later than 07.2.1-T1) | ✅ D6, §8; [OPEN] O-1 |

## 3. Domain and data
| Object | Use here | Lock |
|---|---|---|
| `channel_connections` | read by id under the tenant filter (`platform='tiktok'`, `status`, `display_name`, `oauth_client_id`); tokens only via `ITokenService` | plain read |
| `publications` | the only write target: one `INSERT`, then the consent update | `INSERT`; consent under the EF `xmin` token + `If-Match` |
| `episodes` | `Episode.MarkScheduled` in the consent transaction; `final_video_asset_id` for the duration rule | same transaction |
| `assets` | `duration_sec` of `final_video_asset_id` (F-12) — nullable: a missing asset or NULL duration makes V9 unevaluable and answers 409 `episode_invalid_state`, never "unknown ⇒ allow" | read-only, index `assets (episode_id)` (WT53: the asset block reads `size_bytes`/`duration_sec`) |
| `outbox_messages` | `publication_updated.v1` on create and consent | same transaction (09.2.4-T1) |
| `email_messages` + catalog | `publication_consented` per consent | `IEmailSender.QueueAsync` before the handler's single `SaveChangesAsync` |
| `audit_logs` | `publishing.publication_created`, `publishing.consented` (ids/codes only) | `[Audited]`, same transaction |

**Pinned by D8/F17.** `privacy` = the wire enum verbatim (not YouTube's `public/unlisted/private`); `title` = caption, `description` = `tags` = `NULL`; `publish_at` `NULL` at create, `= consent_at` at consent; `consent_text_version` = the variant id, `consent_source = 'user'`, `autopilot_authorization_id` stays `NULL`; `contains_synthetic_media = true` (U12); `next_attempt_at`/`veto_ends_at`/`veto_notice_sent_at` untouched (no veto window, TT10). `platform_payload.tiktok` has **one writer, at create** (mirror of 06.2.1-T1:270): `privacyLevel`, `disableComment`, `disableDuet`, `disableStitch`, `brandContentToggle`, `brandOrganicToggle`, `isAigc`; `publishId`/`videoUrlExpiresAt` are 07.3.1-T1's.


**Domain types** (`Domain/Publishing/TikTok/`, pure): `TikTokPostSettings(PrivacyLevel, DisableComment, DisableDuet, DisableStitch, DisclosureEnabled, BrandOrganic, BrandedContent)` (D5 verbatim); `TikTokMetadataRules { const int CaptionMaxUtf16Units = 2200; int CaptionUtf16Units(string); ErrorOr<Success> ValidateCaption(string, int); ErrorOr<Success> ValidateDisclosure(TikTokPostSettings); }`; `TikTokConsentVariants { const string Music = "tt.v1.music"; const string Branded = "tt.v1.branded"; TikTokConsentVariant For(TikTokPostSettings); }`; `Publication.CreateTikTok(episodeId, channelConnectionId, caption, settings, now)` — the TikTok sibling of 06.2.1-T1's `Create` (both `AwaitingConsent` + `ContainsSyntheticMedia = true`), an addition to a 06-owned file ([OPEN] O-2), never a second aggregate.
**Errors (TT3/F-10, pipeline F-12).** Domain `PublicationErrorCodes` gains `tiktok_cannot_post_now` here (07.3 adds the rest of CR-3). Wire codes into the closed `ErrorCodes` + `ErrorCatalogue`: `tiktok_invalid_post_settings` 400, `tiktok_video_too_long` 400, `tiktok_cannot_post_now` 409, `tiktok_rate_limited` 429, `consent_text_outdated` 400, `validation_failed` 400, `idempotency_key_required` 400, `if_match_required` 428, `not_found` 404, `feature_disabled` 403.
**Idempotency.** `Idempotency-Key` required on create: same key + body → same `201`/`ETag`, no second row (09 D6); same key + different body → 409 `idempotency_conflict`. Consent is idempotent on `xmin`; a repeat is 409 `publication_invalid_state`.

## 4. Use cases
| UC | Actor | Flow | Rules |
|---|---|---|---|
| UC-1 | the screen on every render; then the declaration lookup | creator-info → nickname + options + interaction flags + duration limit + `canPost`/`blockReason`; consent-text → `{ version, text }` | U1/U2/U5/U6/U9/U13 |
| UC-2 | user picks privacy/interactions/disclosure/caption and presses Post (or is blocked) | create with the `tiktok` object → `201 awaiting_consent` + `ETag`; while the creator cannot post: `canPost = false` + `blockReason`, create refused 409 `tiktok_cannot_post_now`, nothing written | U3–U8, U12, TT7 |
| UC-3 | user confirms the declaration | consent + `If-Match` → 200 `scheduled`, `consent_source = user`, `consent_at`/`consent_text_version`/`publish_at` written, episode `scheduled`, one e-mail | U9/U10, TT6/TT9 |
| UC-4 | tampered client | every rule re-checked server-side; a tampered privacy, disabled interaction, branded+private, over-long caption, over-long video or stale variant never reaches the database | U5–U8, D4/D5 |
| UC-5 | unaudited client | allowed set = options ∩ `{ SELF_ONLY }`; anything else 400; a public account (`PUBLIC_TO_EVERYONE` in the options) → 409 `tiktok_account_not_private` (07 CR-14); flag owner-only | D4, U13 |

## 5. API and events
| Method | Route | Pipeline | Body / response | Owner |
|---|---|---|---|---|
| GET | `/api/v1/channels/{id}/tiktok/creator-info` | `RequireFeature("tiktok_publish")` → `RequireAuthorization()` → `paid` | → `200 TikTokCreatorInfoDto`; `404 not_found`; `429 tiktok_rate_limited` + `Retry-After`; `503` from the catalogue when the platform is unreachable | 07.2.1-T1 |
| GET | `/api/v1/publications/consent-text?locale=&variant=` | `RequireFeature(...)` → `RequireAuthorization()` → `auth` | → `200 ConsentTextDto { version, text }`; `400 validation_failed` (field `locale`/`variant`); variant absent → `music` [DECISION] | 07.2.2-T1 (extends 06's route, CR-2) |
| POST | `/api/v1/episodes/{id}/publications` | 06's route + `RequireFeature("tiktok_publish")` for `platform = tiktok`, `RequireIdempotencyKey()`, `paid` | `{ title, description?, tags?, privacy, publishAt?, tiktok?: { disableComment, disableDuet, disableStitch, disclosureEnabled, brandOrganic, brandedContent } }`; the three content fields must be null for TikTok → `201 PublicationDto` + `ETag: "pu-<xmin>"` | 07.2.2-T1 (extends 06.2.1-T1) |
| POST | `/api/v1/publications/{id}/consent` | 06's route + `RequireIfMatch()` | `{ consentTextVersion }` → `200 PublicationDto` + new `ETag` | 07.2.2-T1 (extends 06.2.2-T1) |
| PATCH/DELETE | `/api/v1/publications/{id}` | unchanged | 06's; `DELETE` closes the pre-upload window only (TT10) | 06.2.2-T3 / 06.2.6-T1 |


Consent-text resolution: `branded` → `tt.v1.branded`, `music` → `tt.v1.music`, absent → `music` [DECISION: the never-commercial text is the safe superset for U9]; allowed set = `TikTokOptions.ConsentTextVersion`.
**Request types** (Application): `TikTokCreatorInfoDto(Guid ChannelId, string Nickname, string AvatarUrl, IReadOnlyList<string> PrivacyLevelOptions, bool CommentDisabled, bool DuetDisabled, bool StitchDisabled, int MaxVideoPostDurationSec, bool CanPost, string? BlockReason)` — D11 field names, verbatim; the wire names (`creator_nickname`, `creator_avatar_url`) never leave `TikTokClient`; `TikTokPostRequest(DisableComment, DisableDuet, DisableStitch, DisclosureEnabled, BrandOrganic, BrandedContent)` (CR-2 field list, [INFERENCE] name); `GetTikTokCreatorInfoQuery(Guid ChannelId)`; `CreateTikTokPublicationCommand(Guid EpisodeId, Guid ChannelConnectionId, string Caption, TikTokPostSettings Settings, string? ConsentTextVersion)` (D3 verbatim); `GetConsentTextQuery(string Locale, string? Variant)` → `ConsentTextDto(string Version, string Text)` (CR-2 shape).
**ProblemDetails from this folder** (D10/CR-3): 400 `tiktok_invalid_post_settings` (`errors.tiktok.invalidSettings`), 400 `tiktok_video_too_long` (`errors.tiktok.tooLong`), 400 `consent_text_outdated` (`errors.consent.outdated`), 400 `validation_failed`, 400 `idempotency_key_required`, 409 `tiktok_cannot_post_now` (`errors.tiktok.cannotPostNow`), 429 `tiktok_rate_limited` (`errors.rateLimited`), 428 `if_match_required`, 404 `not_found`.
**Events.** `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }` — one on create (`awaiting_consent`), one on consent (`scheduled`); realtime `PublicationUpdated` ids only; no new event type.

## 6. UI
**No UI here.** The screen is **10.7.3-T1** (`/episodes/:id/publish/tiktok`); by the owner rule (2026-09-25) **10.7.3-T0 — UI design (Area UX) is a hard dependency of 10.7.3-T1** and takes README §1.3 U1–U13 as its input (CR-7). This folder hands that design the contract and never designs it.

| U | Server-side enforcement |
|---|---|
| U1/U2 | creator-info returns `nickname` (+ `avatarUrl`, 2 h TTL) every render; `canPost = false` + `blockReason` on the 200-error case and any cap; create → 409 `tiktok_cannot_post_now` |
| U3/U4 | duration rule on `assets.duration_sec` → 400 `tiktok_video_too_long`; caption 1…2200 UTF-16 units, nothing appended |
| U5/U6 | `privacyLevel` required, ∈ the returned option set, no default; `disableDuet`/`disableStitch`/`disableComment` false only when the creator did not disable them |
| U7/U8 | disclosure on ⇒ ≥ 1 flag, off ⇒ both false, `brand_content_toggle` still sent (`false` when off); branded + `SELF_ONLY` → 400 `tiktok_invalid_post_settings` |
| U9/U10 | variant derived from the selection, text from the consent-text route; the row is only `awaiting_consent` and a job is scheduled only by the consent transition |
| U12/U13 | `is_aigc = true` in `platform_payload.tiktok`; unaudited ⇒ allowed set = options ∩ `{ SELF_ONLY }` |

Copy keys `errors.tiktok.*` vi+en (D18); the screen renders `blockReason` when `canPost = false`.

## 7. Jobs and integrations

| Integration | Contract | Owner |
|---|---|---|
| `IExternalLookup.GetTikTokCreatorInfoAsync(connectionId, ct)` | one outbound `creator_info/query/` call; reads `error.code` on HTTP 200 too; nothing persisted/cached (TT11) | 07.2.1-T1 |
| `ITokenService` | access token, refresh when < 5 min left (D11/D13) | 07.1.2-T2 |
| `ITikTokRateLimiter` | buckets keyed `(connectionId, endpoint)`: `creator_info` 20/min here; `init`/`status`/`token`/`revoke` by 07.1.2-T1 / 07.3.1-T1 | declared by 07.2.1-T1 ([OPEN] O-1) |
| `IEmailSender.QueueAsync` | `publication_consented` per consent, before the handler's single `SaveChangesAsync` | 07.2.2-T2 |
| Outbox | `publication_updated.v1` on create/consent; the Worker's `PublicationScheduledForUploadHandler` (06.2.3-T2) re-reads the row and schedules `PublishTikTokJob` for `platform = 'tiktok'` immediately (no `publishAt`) — TT1; this folder never enqueues | 07.2.2-T1 / 07.3.1-T1 |
| Job | none registered here; no `[JobPolicy]`, no recurring job | — |
| Config | `TikTokOptions` only (D19): `AuditStatus`, `ConsentTextVersion`, `CaptionMaxUtf16Units` 2200, `RateLimits:CreatorInfoPerMinute` 20, `Transfer` (unused here) | class declared by 07.1.1-T1 |

**Logs.** One line per lookup (`channelId`, `outcome ∈ ok|200_error|unauthorized|throttled|transient`, `durationMs`, TikTok `log_id?`) and `event = tiktok_validation_rejected` per refusal (`code` + field names) — never nickname, caption or option list. No new metric (07.3 owns the D18 instruments).

## 8. Security and privacy
- **Tenant filter.** `IUserOwned` on every read (09.2.3-T1): another user's `channelId`/`episodeId`/`publicationId` → **404 `not_found`**, never 403; no `IgnoreQueryFilters` here.
- **Authorization.** All routes `RequireFeature("tiktok_publish")` (D1) → `RequireAuthorization()`; creator-info and create on `paid` (F-3: an authenticated outbound call per render must not be a free amplifier), consent-text on `auth`.
- **Audit attributes** (exactly one per request type, 09.3.6-T1): creator-info → `[NotAudited]` [DECISION: it persists nothing; its trace is the log line, a per-render audit row would be noise]; create and consent → `[Audited]` (`publishing.publication_created` / `publishing.consented`), ids + codes only (`{ episodeId, publicationId, platform: "tiktok", errorCode? }`) — never caption, nickname, avatar, privacy list or tokens (D14/D15).
- **Secrets/tokens.** `TIKTOK_CLIENT_SECRET` (`nts/<env>/api/TIKTOK_CLIENT_SECRET`, 11) is never read here (creator-info uses the user's access token); tokens are unprotected only inside `ITokenService` under `ITokenProtector` purpose `tokens.v1/{connectionId}`; no DTO, log, audit row, exception or `platform_payload` carries them.
- **Third-party data.** `creator_info` is fetched live per render and never stored beyond the connection's `display_name`/avatar (07.1.2-T1) — no cross-user cache, no nickname in logs.
- **Consent evidence.** `consent_text_version` records which variant the user accepted; the text is an embedded, boot-validated resource, never a row. Caption ≤ 2200 UTF-16 units at the validator; the body ≤ 1 MiB (`MaxRequestBodySize`, 09 BD12); `privacy` is a closed enum so no free text reaches `platform_payload`.
- **i18n.** Consent texts and copy keys exist in vi and en; a missing locale falls back to `en` and logs the fallback.

## 9. Test plan
| Project | Tests |
|---|---|
| `NightTales.Domain.UnitTests` | `TikTokMetadataRulesTests` (caption 0/1/2199/2200/2201 UTF-16 units incl. a surrogate pair and a vi tone-mark pair; the `ValidateDisclosure` matrix; branded + `SELF_ONLY` rejected), `TikTokConsentVariantTests`, `PublicationCreateTikTokTests` (status, verbatim privacy, nulls, payload once, no veto fields) |
| `NightTales.Application.UnitTests` | `GetTikTokCreatorInfoQueryHandlerTests` (ok; each 200-error family; unknown 200 code → generic reason + `log_id`; the 401 ladder; bucket denied; nothing persisted: the fake is called once and `SaveChanges` never runs), `CreateTikTokPublicationCommandValidatorTests` (a theory per matrix row asserting code + field), `ConsentVariantBindingTests` (the stored payload drives the variant), `GetConsentTextQueryHandlerTests` (both variants × vi/en, default, unknown) |
| `NightTales.Api.IntegrationTests` (real PostgreSQL 17, `PostgresFixture` + `FakeTikTokServer`) | `TikTokCreatorInfoEndpointTests` (200 shape; 404 cross-tenant and non-TikTok id; 403 `feature_disabled`; `paid` throttle; the 200-error case; fake counters), `TikTokPublicationCreateTests` (`AuditStatus = pending` ⇒ `privacy = SELF_ONLY`; each tampered field → its code; one `publication_updated.v1` per transition; episode `scheduled`; the idempotent replay), `TikTokConsentTests` (428 without `If-Match`; 400 stale variant; 409 `tiktok_cannot_post_now`; `publish_at = consent_at`), `TikTokConsentEmailTests` (one variant-aware mail; allow-listed params; rollback on a queue failure) |
| `NightTales.Architecture.Tests` | one audit attribute per new request type; exactly one `IExternalLookup` declaration (TT11); every emitted code in `ErrorCatalogue`; handler size rule; no second `IEmailSender` caller |
| TestKit | `FakeTikTokServer` gains `creator_info` (both option sets, each disabled-interaction toggle, the 200-error families, a 401 and a 429 mode); hand-written fakes only (D17) |

Gates: `07-screen-contract` (live option set; the 200-error case sets `canPost = false`; both variants vi/en; every tampered body refused with the D10 codes) and `07-post-private`'s `SELF_ONLY` + `is_aigc = true` sentence.

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 07.2.1-T1 | S (0.5) | BE | 07.1.2-T1, 07.1.2-T2, 09.2.1-T1 (soft) | 07.2.2-T1, 10.7.3-T1 (07 CR-7, soft) | S · R2 · flag `tiktok_publish` |
| 07.2.2-T1 | M (1.0) | BE + Security | 07.2.1-T1, 06.2.2-T1 | 07.2.2-T2, 07.3.1-T1, 10.7.3-T1 (07 CR-7, soft) | S · R2 · flag `tiktok_publish` |
| 07.2.2-T2 | XS (0.25) | BE | 07.2.2-T1, 06.2.2-T2, 09.2.6-T3 | — | S · R2 · flag `tiktok_publish` |

Identical to `../README.md` §8.1 (canonical). Folder total **1.75 d raw / 2.1 d buffered**, composing into the plan's 7.75/9.3 (§10 O2) — no CR to `00-overview.md` §4.1.

## 11. Rollout
- **Order.** 07.1 (connect) → **07.2** → 07.3 (publish) → 07.4 (audit). Nothing here is filed before 07.4.1-T1; `AuditStatus = approved` (07.4.1-T2) is the only step that unlocks public posting and flag rollout (README §5).
- **Flag.** `tiktok_publish` owner-only (`feature_flags.rules` = owner user ids, no percentage rollout, recurring jobs only for the owner scope — CR-6); while `AuditStatus ≠ approved` only `SELF_ONLY` is accepted (D4); flag off ⇒ all routes 403 `feature_disabled`.
- **No migration, no backfill.** Rows created before a rollback stay `awaiting_consent` and are inert (no job without a consent; `DELETE /publications/{id}` closes that window, TT10).
- **Rollback.** Unset the flag rule (or remove the `RequireFeature` call) — additive, no data change; the `_06_*` migrations are 06's and are not touched. **Verification:** `07-screen-contract` on staging with a real private test account, then `07-post-private` (a real post ends `published` with `privacy = SELF_ONLY`, `is_aigc = true`).
- **[OPEN].** **O-1** `ITikTokRateLimiter` is needed by 07.2.1-T1 but §8.1 attaches "rate limiter" to 07.3.1-T1 — this plan declares the port + the in-memory bucket in 07.2.1-T1 and leaves `init`/`status`/`token`/`revoke` + the job-deferral rule (`next_attempt_at` + jitter, D6) to 07.3.1-T1; alternative: 07.3.1-T1 owns both and 07.2.1-T1 uses a TestKit limiter. Needs a ruling. **O-2** `Publication.CreateTikTok` and the TikTok branch of the shared create/consent handlers extend 06-owned files (`06.2.1-T1`, `06.2.2-T1`) — confirm the seam (canonical §8.1 lists `06.2.2-T1`). **O-3** TT11: 07.2.1-T1 checks whether 09 already declared `IExternalLookup`; if so with another member shape, the member name is 09's. **O-4** CR-3 must land the statuses in `plans/09-backend-api.md` §5.4 before the integration tests assert them. **O-5** the exact `publication_consented` param allow-list is confirmed against 06.2.2-T2's catalog entry at implementation (WT30).
