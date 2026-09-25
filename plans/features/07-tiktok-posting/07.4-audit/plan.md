# 07.4-audit — TikTok Content Posting API audit: evidence pack, internal checklist, post-approval enablement

> Folder of `plans/features/07-tiktok-posting/README.md` (contract). Story **07.4.1** (high-level `../../07-tiktok-posting.md` §4 "07.4.1 Audit submission", §6 row 07.4.1-T1). Tasks: `07.4.1-T1` (M 1.0), `07.4.1-T2` (S 0.5 — the declared split of 07.4.1-T1's post-approval half, README §0/§9). Every identifier below is copied verbatim from the README (§1, §3 D1–D19, §4 CR-1–CR-12, §7, §8.1, §10) or from a signed plan cited inline.

## 1. Scope

**Why.** TikTok lets an API client post publicly only after it passes the Content Posting API **audit**; until then every post is restricted to private viewing, only `SELF_ONLY` is accepted, at most 5 users may post in a 24-hour window, and the client carries a 24-hour active-creator cap set from the volume claimed in the audit application (K9, K10). The audit also grades the **required UX** (K12, §1.3 U1–U13) and the **intended use** (K11). This folder is the non-code work of getting there: build the evidence, prove on staging that the shipped behaviour satisfies U1–U13, hand the pack to the filing task, drive the response loop, then flip the audit status and re-verify on production.

**In scope.**
- `07.4.1-T1` — assemble the evidence pack from the working private path (07.1–07.3) and the shipped screen (10.7.3-T1): screencast covering **U1–U13**, both consent-text variants, data-handling summary, intended-use statement, claimed 24-h active-creator volume (`TikTokOptions.MonthlyActiveCreators`), OAuth error taxonomy and rate-limit/`fail_reason` evidence; then run the **internal U1–U13 checklist** against the live screen and the server rules.
- `07.4.1-T2` — after the verdict: set `TikTokOptions.AuditStatus = approved`, enable public posting (full `privacy_level_options`), re-run the U1–U13 checklist on production, release the `tiktok_publish` rollout (CR-6). The rejection path is specified too.
- Driving the audit **response loop** with a written log, and flagging every screen deviation as a CR.

**Out of scope (do not duplicate — C10, CR-7).** The filing itself (`13.4.3-T1 TikTok audit submission (R2)`, `plans/13-compliance.md:87-90, 153`). UI design `10.7.3-T0` and screen `10.7.3-T1` (`plans/10-web-ui.md:189-195, 306, 381`, route `/episodes/:id/publish/tiktok`) — **this folder has no UI task**; the owner rule (web/UI needs an approved design first) is satisfied by 10.7.3-T0, not here. All product code (07.1.1-T1 … 07.3.3-T1). KPIs (08) and the compliance pack (`13.5.1-T1`). If the checklist fails, the defect goes to the owning 07.x task or to Lead10S as a CR — never fixed here.

**Flags and phase.** All of plan 07 is **S · R2**. Every route is behind `RequireFeature("tiktok_publish")`; recurring jobs register only while the flag is on (D1, 06 D1 pattern). While `AuditStatus ≠ approved` the flag is owner-only (`feature_flags.rules` = owner user ids, no percentage rollout — CR-6, D4). `07.4.1-T2` is the only task that unlocks public posting and any rollout.

**Effort.** 1.0 + 0.5 = **1.5 d raw**; plan-07 totals **stay 7.75 raw / 9.3 buffered** (README §9, CR-1).

## 2. Confirmed facts

### 2.1 TikTok facts used here (README §1.1, all ✅ read 2026-09-25)

| Id | Fact (verbatim essentials) | Used by |
|---|---|---|
| K1 | init `POST /v2/post/publish/video/init/`, scope `video.publish`; "**Each user access_token is limited to 6 requests per minute**"; 403 `spam_risk_too_many_posts`, `spam_risk_user_banned_from_posting`, `reached_active_user_cap`, `unaudited_client_can_only_post_to_private_accounts`, `url_ownership_unverified`, `privacy_level_option_mismatch`; 401 `access_token_invalid`, `scope_not_authorized`; 429 `rate_limit_exceeded` | probes, intended-use statement |
| K2 | `POST /v2/post/publish/creator_info/query/`, **20 requests/min per token**; returns `creator_username`, `creator_nickname`, `privacy_level_options`, `comment_disabled`, `duet_disabled`, `stitch_disabled`, `max_video_post_duration_sec`; public options `PUBLIC_TO_EVERYONE`/`MUTUAL_FOLLOW_FRIENDS`/`SELF_ONLY`, private options `FOLLOWER_OF_CREATOR`/`MUTUAL_FOLLOW_FRIENDS`/`SELF_ONLY`; its error struct comes back **with HTTP 200** for `spam_risk_*`/`reached_active_user_cap` | U1, U2, U5, U6, U13 |
| K3 | `post_info.privacy_level` required and must be one of `privacy_level_options`; `post_info.title` **max 2200 UTF-16 runes**; `disable_duet`/`disable_stitch`/`disable_comment`; `brand_content_toggle` **required**; `brand_organic_toggle`; `is_aigc`; `source_info.source` ∈ `FILE_UPLOAD` \| `PULL_FROM_URL` | U4, U7, U12 |
| K7 | status `POST /v2/post/publish/status/fetch/`, **30 requests/min per token**; `status` ∈ `PROCESSING_UPLOAD` \| `PROCESSING_DOWNLOAD` \| `SEND_TO_USER_INBOX` \| `PUBLISH_COMPLETE` \| `FAILED`; `fail_reason` list; `publicaly_available_post_id` (public moderated posts only) | U11, public post |
| K8 | cancel is **best-effort**, only during download/processing (`publish_not_cancellable` when final) | [OPEN] §11 |
| K9 | unaudited client: **private** only, **up to 5 users may post in a 24-hour window**, **only `SELF_ONLY`**; going public later needs the account owner to switch the account public then each post's privacy to "Everyone" | D4 gate, claimed volume, U13 |
| K10 | **24-hour active-creator cap per API client** from the audit-application usage estimate, plus a **posting cap of about 15 posts/day/creator account shared across all Direct Post clients** | `MonthlyActiveCreators`, CR-8 |
| K11 | intended use: authentic creators posting original content, wide audience — "a utility tool to help upload contents to the account(s) you or your team manages" is **not acceptable**; clients must not be limited to test applications | intended-use statement |
| K12 | Required UX (verbatim obligations; full list = §1.3 U1–U13) | the checklist |
| K13 | `expires_in` **86400 (24 h)**, `refresh_expires_in` **31536000 (365 d)**; refresh **may return a new `refresh_token` which must be stored** | reauth/incident evidence |
| K15 | scopes `user.info.basic`, `video.publish`, `video.upload`; the user may grant only a subset | connect evidence |
| K16 | `PULL_FROM_URL`: `https`, **verified domain/URL prefix**, no redirect, reachable for the whole download; "**If video resources are already on API Clients' servers, do not use FILE_UPLOAD; use PULL_FROM_URL instead**" | D2 evidence |
| K17 | client secret stays confidential, never in an open-source project or shared with a third party; no added promotional watermarks | §8 |

### 2.2 Contract facts (README §1.2 C-rows)

C1 `channel_connections(...)` · C2 `publications(... platform_payload jsonb (upload session, **TikTok settings**), error_code)` · C3 Publication states + "cannot leave `awaiting_consent` without a `consent_at` recorded **for that video**" + credits reserve/settle/refund · C4 routes + `Idempotency-Key`/ETag/If-Match/ProblemDetails `code` · C5 09 §5.4's four TikTok codes (`tiktok_invalid_post_settings`, `tiktok_video_too_long` 400; `tiktok_cannot_post_now`, `tiktok_creator_cap` 429) — superseded by CR-3 · C6 06 owns the tables/machinery (D3–D5, D14, D17, D20, YR4, YR10, YR25) · C7 `ETags.Publication` `"pu-<xmin>"` + `If-Match`, `POST /publications/{id}/consent { consentTextVersion }`, 400 `consent_text_outdated`, `consent_source = user`, `Episode.MarkScheduled/MarkPublished` · C8 events · C9 Api never enqueues; `ISystemWorkQuery.PendingAsyncT`; `PublicationErrorCodes` once in Domain · C10 13 owns the filing · C11 10.7.3-T1 screen contract · C12 02.8.3 autopilot is YouTube-shaped.

### 2.3 Decisions implemented here

D2/D3/D6/D7/D9 are the behaviour being evidenced; D4 (`AuditStatus`, pre-audit `SELF_ONLY` intersection, owner-only flag), D16 and D18/D19 are switched on or reported here; D5 (`tt.v1.music`/`tt.v1.branded`, `consent_text_outdated`), D8 (2200 UTF-16, `is_aigc = true`), D10 (error table + 401 recovery ladder), D11 (`canPost`/`blockReason`), D14 (security/audit), D15 (reuse map) and README §10 TT4 (unknown client error ⇒ incident, never `expired`).

**Legend.** ✅ from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]. Only ✅ K-rows are quoted as vendor behaviour.

## 3. Domain and data

**No new tables, columns, migrations or DTOs.** Every column below is quoted from `plans/00-overview.md` §6 as cited by the README (C1, C2) and is read-only here.

```sql
-- 1. the TikTok connection used for the evidence runs
SELECT id, user_id, platform, external_account_id, display_name, status,
       oauth_client_id, token_expires_at, last_verified_at, revoked_at
FROM channel_connections WHERE platform = 'tiktok' AND status = 'active';

-- 2. the private runs the screencast shows (never group by publish_at — F21)
SELECT id, episode_id, channel_connection_id, status, privacy, publish_at, consent_at,
       consent_text_version, consent_source, contains_synthetic_media, external_post_id, error_code,
       platform_payload->'tiktok'->>'publishId' AS tiktok_publish_id
FROM publications WHERE platform = 'tiktok' ORDER BY consent_at DESC;

-- 3. incidents raised during a run (url_ownership_unverified / scope_not_authorized);
--    platform_incidents (00:293) has no status column: the open-row guard is the partial
--    unique (platform, oauth_client_id) while cleared_at IS NULL
SELECT platform, oauth_client_id, error_code, raised_at, cleared_at
FROM platform_incidents WHERE cleared_at IS NULL;

-- 4. the audit trail the pack cites (ids/codes only — D14); audit_logs (00:307) is scoped by
--    subject_user_id/visibility, not by user_id
SELECT actor_user_id, subject_user_id, action, entity_type, entity_id, created_at
FROM audit_logs WHERE action LIKE 'publishing.%' ORDER BY created_at DESC;

-- 5. events emitted by a consent→publish run (outbox_messages (00:317) column is `type`)
SELECT type, occurred_at, payload->>'status' AS status, payload->>'errorCode' AS error_code
FROM outbox_messages WHERE type = 'publication_updated.v1' ORDER BY occurred_at DESC;
```

`[INFERENCE]` the existing indexes cover these run-window queries (`audit_logs (subject_user_id, created_at)`, 00:307; the outbox `type`/`occurred_at` lookups) — no new index is needed. Note `audit_logs` is **not** tenant-filtered by `user_id`: it carries `subject_user_id` + `visibility (user|internal)` and is read as the owner's own activity history. `platform_payload->'tiktok'` is the D8 object (`privacyLevel`, `disableComment`, `disableDuet`, `disableStitch`, `brandContentToggle`, `brandOrganicToggle`, `isAigc`, `publishId?`, `videoUrlExpiresAt?`); the pack cites `publishId` presence/absence, never the presigned URL.

### 3.1 Evidence register (canonical artefact table)

Assembled by 07.4.1-T1 as rows of the reusable compliance evidence pack owned by `13.5.1-T1`, using that pack's conventions exactly: index `docs/compliance/evidence-<yyyy-mm-dd>/index.md` (repo, text only — **no binary ever enters the repo, and never `/Volumes/Project`**), binaries in `~/.nts/compliance/evidence-<yyyy-mm-dd>/EV<n>-<slug>.mp4|.png|.json` on the FileVault disk, columns `| Id | Flow | What it proves | Captured at | App version | Binary | sha256 | Redaction checked | Owning task | Verdict |`, numbering `EV1…EVn` **assigned by the pack** (07 uses slugs), `Verdict ∈ {proven, provisional, gap}`, the redaction grep `VP7_RedactionClean` (06.5.1-T1:117: no token/e-mail/IP/video-id pattern in the repo copy), the synthetic tenant `demo+<n>@invalid`, and retention ≥ 24 months. Rows may be added only under 13.5.1-T1's additional-artefacts rule: the TikTok **flows** (connect · publish form · per-video consent · disconnect + revoke explanation — names verbatim) or a citing task that names the artefact it needs, which is exactly `13.4.3-T1` §2's required-UX matrix. 07 creates **no second pack** and writes no filing (C10); the submitted-copy location is 13's `[OPEN]` (`13.4.3-T1` Design: `docs/compliance/tiktok-audit-<yyyy-mm-dd>.md` is its [INFERENCE] path, and "whether the submitted copy lives here or inside `07.4.1-T1`'s own output" is [OPEN] and must be reconciled at implementation).

| Slug | Artefact | Flow / who needs it | What it proves |
|---|---|---|---|
| `tiktok-post-screen-screencast.mp4` | continuous take of `/episodes/:id/publish/tiktok` (nickname, privacy list with no default, unchecked + greyed-out toggles, disclosure, music declaration, preview, status) | flow **publish form**; `13.4.3-T1` §2 | U1–U13 — every U-row has a timestamp in the take's index |
| `tiktok-consent-texts.json` | `tt.v1.music` + `tt.v1.branded` renderings, vi + en, each with its `consent_text_version` | flow **per-video consent** | U9, D5 |
| `tiktok-data-handling.md` | text artefact (cited as an index row, no binary) | `13.4.3-T1` §1/§5 | stored fields per connection/publication, retention, deletion routes; no token/`open_id` |
| `tiktok-intended-use.md` | text artefact | `13.4.3-T1` §1 | K11 statement (authentic creators, wide audience, not a "utility tool") + K17 secret/watermark assertions |
| `tiktok-volume.md` | text artefact | `13.4.3-T1` §0; CR-8 | `TikTokOptions.MonthlyActiveCreators` — equal to the configured value, ≤ 5 posting users/24 h while unaudited (K9) |
| `tiktok-errors-limits.json` | machine-readable dump, no secrets | `13.4.3-T1` §3/§5 | recorded `oauth/token`/`oauth/revoke` taxonomy, the D10 code table, observed 6/20/30/10 rpm |
| `tiktok-internal-checklist.md` | the U1–U13 table below, signed | `13.4.3-T1` §2 | the item→screen→task→evidence mapping the filing cites, with no failing row lacking a CR |
| `tiktok-response-log.md` | exchange log, mirrors `13.4.3-T1` §7 | `13.4.3-T1` §7 | every reviewer exchange closed |

The rows are handed to `13.5.1-T1` (which assigns `EV1…EVn` and writes `Verdict`); the same capture run also covers the **connect** and **disconnect + revoke explanation** flows, whose screens belong to 07.1.2/10.7.1 — captured once and cited, never re-taken per filing.

**Semantics that must not drift.** (a) `publications.publish_at` for TikTok is the **consent instant**, for ordering only (D3, F21) — count `status` transitions, never `publish_at` (CR-9, 08 K24/F17). (b) `publications.privacy` holds the TikTok wire enum **verbatim** (`SELF_ONLY`, `MUTUAL_FOLLOW_FRIENDS`, `PUBLIC_TO_EVERYONE`, `FOLLOWER_OF_CREATOR`; D8/F17). (c) `is_aigc = true` always (U12). (d) While `AuditStatus ≠ approved`, allowed privacy = `creator_info.privacy_level_options ∩ { SELF_ONLY }` (D4). (e) posting writes **no** `credit_ledger` row and no refund (D16, F19).

## 4. Use cases

**UC-1 — Build the evidence pack (07.4.1-T1, primary).** Given the shipped private path (07.1–07.3) and the shipped screen (10.7.3-T1) on staging, When the operator performs one full private post per consent variant and captures the screencast, both consent texts, the data-handling, intended-use, volume and error/limit artefacts, Then the pack exists, is redacted per §8, and `13.4.3-T1` can file without asking for missing inputs.

**UC-2 — Run the internal U1–U13 checklist (07.4.1-T1).** Given the live screen and running app, When the operator walks U1–U13 against server behaviour (not just the UI), Then each row is `pass` with an evidence pointer, `fail` with a CR to the owning 07.x task or to Lead10S, or `deviation-accepted` with a written justification. Given a tampered request (privacy not in options, creator-disabled interaction enabled, branded + `SELF_ONLY`, over-long caption, over-long video, stale consent version), When sent, Then the server refuses it with the D10 code — the server, not the screen, is the acceptance instrument.

**UC-3 — Hand off and drive the response loop (07.4.1-T1).** Given a complete pack, When `13.4.3-T1` files and the reviewer answers, Then every question is answered in `tiktok-response-log.md` (5 working days target) and mirrored into `13.4.3-T1` §7 and any requested product change becomes a CR — never an unlogged code change.

**UC-4 — Post-approval enablement (07.4.1-T2).** Given approval, When `TikTokOptions.AuditStatus` is set to `approved`, Then the full `privacy_level_options` is selectable, one **public** post on production ends `published` with `external_post_id` set when `publicaly_available_post_id` is non-empty, the U1–U13 checklist is re-run on production, `tiktok_publish` may roll out per CR-6, and the verdict + numbers are recorded for 13.

**UC-5 — Rejection / abort (07.4.1-T2 branch).** Given a rejection or no verdict by the planned window, Then `AuditStatus` stays `pending` (or becomes `rejected` to stop pretending), the flag stays owner-only, the reviewer's reasons become follow-up CRs, private posting keeps working, and no rollout happens.

**UC-6 — Incident during an evidence run (both tasks).** Given `url_ownership_unverified` or `scope_not_authorized` appears, Then it is a **platform incident** on `oauth_client_id` (P1, D10/F06), never a per-user failure: the publication returns to `scheduled`, `nts-worker incident clear` is the only sweep (06 D14), and the run is repeated after the clear.

## 5. API and events

**No new routes, DTOs, events or error codes.** This folder exercises the contract; names are copies from C4 / D10 / D11 / D16.

| Route (C4) | Used to evidence | U |
|---|---|---|
| `GET /channels/{id}/tiktok/creator-info` | nickname, live `privacyLevelOptions`, `commentDisabled`/`duetDisabled`/`stitchDisabled`, `maxVideoPostDurationSec`, `canPost`/`blockReason` | U1, U2, U5, U6, U13 |
| `GET /publications/consent-text?locale=&variant=` (CR-2) | both variants, vi + en | U9 |
| `POST /episodes/{id}/publications` (`tiktok` object per CR-2) | create with `TikTokPostSettings`, `Idempotency-Key` | U4, U7, U8, U12 |
| `POST /publications/{id}/consent` (`consentTextVersion`, `If-Match`) | consent → `scheduled`, `consent_source = user` | U10 |
| `PATCH` / `DELETE /publications/{id}` (`If-Match`) | pre-upload edit/cancel only | — |
| `POST /channels/{platform}/connect`, `GET /channels/{platform}/callback`, `DELETE /channels/{id}` | connect/reconnect/disconnect evidence | U13 |

DTOs quoted: `TikTokCreatorInfoDto { channelId, nickname, avatarUrl, privacyLevelOptions[], commentDisabled, duetDisabled, stitchDisabled, maxVideoPostDurationSec, canPost, blockReason? }` (D11); `TikTokPostSettings { privacyLevel, disableComment, disableDuet, disableStitch, disclosureEnabled, brandOrganic, brandedContent }` (D5); `CreateTikTokPublicationCommand(episodeId, channelConnectionId, caption, TikTokPostSettings settings, consentTextVersion)` (D3); `ConsentPublicationCommand` (C7).

**Checklist negative probes → exact expected codes (D10):**

| Probe | Expected |
|---|---|
| privacy not in `privacy_level_options` / `privacy_level_option_mismatch` | 400 `tiktok_invalid_post_settings` |
| `brandedContent = true` with `privacy = SELF_ONLY` | 400 `tiktok_invalid_post_settings` |
| caption > 2200 UTF-16 code units | 400 `tiktok_invalid_post_settings` (`TikTokMetadataRules.Caption`, D8) |
| duration > `max_video_post_duration_sec` | 400 `tiktok_video_too_long` |
| stale `consentTextVersion` (variant mismatch) | 400 `consent_text_outdated` |
| `canPost = false` (HTTP 200 + `spam_risk_*`/`reached_active_user_cap`) | 409 `tiktok_cannot_post_now` + `blockReason` |
| `spam_risk_too_many_posts` at init | 429 `tiktok_creator_cap` (same publication `error_code`) |
| our own inbound throttle | 429 `tiktok_rate_limited` (the only rate 429 we emit) |
| `url_ownership_unverified` / `unaudited_client_can_only_post_to_private_accounts` | `platform_incident` + P1, publication back to `scheduled` |
| `reached_active_user_cap` | `tiktok_active_user_cap` + P2 (`tiktok_active_user_cap_hit_total`) |
| TikTok `rate_limit_exceeded` | backoff only: `next_attempt_at` + jitter, `tiktok_rate_limit_backoff_total{endpoint}` |
| callback whose granted scopes lack `video.publish` | `302 /channels?error=tiktok_scope_missing`, no row, no alert (F25) |

**Events** (unchanged, C8): `publication_updated.v1 { publicationId, episodeId, userId, status, errorCode? }`, `channel_connection_changed.v1`, `platform_incident_raised/cleared.v1`; realtime `PublicationUpdated` carries **ids only** (`IRealtimeNotifier`).

## 6. UI

**This folder designs and implements no UI.** Owner rule 2026-09-25: web/UI work needs an approved design first; in this slice that design is **10.7.3-T0 — UI design**, a hard dependency of **10.7.3-T1** per CR-7 (`10.7.3-T1 "TikTok post screen (R2) | FE | L | depends 07"`, route `/episodes/:id/publish/tiktok`, `plans/10-web-ui.md:189-195, 306, 381`).

- README **§1.3 U1–U13** is the design's input list **verbatim** and the acceptance list for 10.7.3-T1; 07.4.1-T1's checklist is a row-per-U walk against the running app. A design "improvement" is an **audit risk** (README §5 residual risk).
- Server-side enforcement that makes the screen honest: U1/U2 → 07.2.1-T1; U3–U10, U12, U13 → 07.2.2-T1; U11 → 07.3.2-T1 + 10.7.4. The screen's own written behaviour to preserve: nickname shown, privacy dropdown with no default, unchecked toggles (greyed out when creator-disabled), disclosure toggle + "Your brand"/"Branded content", music declaration, preview, `is_aigc` (C11).
- Any mismatch found (missing grey-out, a default privacy, a rewritten declaration, a missing preview) becomes a **CR to Lead10S** with the U-row id and screencast timestamp; never patched here, and 10.7.3-T1 must not start before 10.7.3-T0 is approved against §1.3.
- Out of slice: the channels-page TikTok connect card (`10.7.1`, id created by Lead10S per CR-7) and 10.7.4's post-status view.

## 7. Jobs and integrations

**No new jobs, flags or config keys.** This folder observes the machinery built by 07.3 and 06 while producing evidence:

| Job | Cadence / claim | Observed for |
|---|---|---|
| `PublishTikTokJob` | queued by `PublicationScheduledForUploadHandler` at consent; by id, `[JobPolicy(Retries = 3)]` exponential for transport errors; one init, never re-init while non-terminal (D7, D16) | U10, U11, D7 (no second post after a forced lost response) |
| `PollTikTokStatusJob` | recurring 20 s while the flag is on; `ISystemWorkQuery.PendingAsyncT` (≤ 25 rows, `status IN ('uploading','processing')`, ordered by `next_attempt_at`), one command per row; sets `next_attempt_at` for the next poll (D16, C9) | U11 status ladder; `fail_reason → error_code` map (D9/D10) once per family |
| `TokenHealthJob` | shared daily (06.4.2), TikTok branch per D13: refresh rotates both tokens under the connection-row `FOR UPDATE`; a known-grant token-endpoint error → connection `expired` + `tiktok_reauth_required`, any other token-endpoint error → incident (README TT4) | the error/limit artefact, the 401 recovery ladder (F24) |
| `nts-worker incident clear` | the **only** sweep that clears a hold (06 D14/YR25) | UC-6 |

Reused from 06 D15 (never re-invented): `oauth_states` (+`browser_nonce_hash`, YR10), `channel_connections.oauth_client_id` pinning, `ITokenProtector` purpose `tokens.v1/{connectionId}`, `platform_incidents` + `IPlatformIncidentStore.RaiseAsync`, `publication_updated.v1`, `IEmailSender` + the template catalog, `[JobPolicy]`, `ETags.Publication` + `If-Match`, `RequireFeature`, `Episode.MarkScheduled/MarkPublished`, `IRealtimeNotifier`. **Not reused** (so never cited as evidence): `POST /publications/cancel-by-link`, the veto-window machinery, the quota ledger (06 D15/YR3, YR26, YR27).

**Integrations.** TikTok developer portal (audit application form, screencast upload, the `PULL_FROM_URL` domain/URL-prefix verification from `07.1.1-T1`). Secrets: `nts/<env>/api/TIKTOK_CLIENT_SECRET` per env (D14, 11; exact store CLI is `11.2.2-T1`'s ⚠️). Alerting (CR-10): `tiktok_active_user_cap_hit_total > 0` → P2; the existing `platform_incident_open > 0` covers `url_ownership_unverified`; the LB access log drops the query string for `/api/v1/channels/tiktok/callback`. `11.6.2-T1` carries the rollout rules 07.4.1-T2 releases.

## 8. Security and privacy

- **Nothing secret in the pack:** no access/refresh token, no `open_id`, no `client_secret`, no presigned URL, no `code_verifier`, no `browser_nonce_hash`. `TikTokCreatorInfoDto.avatarUrl` is a 2-hour-TTL CDN URL (K2) — fine inside the screencast of a test account, never persisted (only `nickname`/`avatar_url` reach the connection, D11/D14).
- **Redaction before hand-off:** creator identity only from the owner's test account; other users' rows never shown; `platform_incidents`/`audit_logs` citations carry ids/codes only (D14: no nicknames, no captions in audit `data`).
- **Audit trail:** the runs generate only the existing actions `publishing.channel_connected|channel_disconnected|publication_created|consented|cancelled|incident_raised|incident_cleared` (06 D17); the pack quotes them and invents no new action name.
- **Secrets:** this folder never reads a token — it works through the app and `nts-worker`. the intended-use artefact asserts K17: `client_secret` stays server-side, is never shared with a third party and never embedded in an open-source project; no promotional watermark is added to creator content.
- **Pack access and retention:** owner + the 13 owner (`13.4.3-T1`/`13.5.1-T1`); stored inside the compliance evidence pack (13 owns layout/retention) — 07 creates no second pack and no second retention rule.
- **Tenant filter:** owner-scoped tables (`channel_connections`, `publications`, `data_deletion_requests`) are read under the owner's account; the operator-side read-only statements of §3 are the documented exception, quoted here for transparency. `audit_logs` is **not** filtered by `user_id` — its scope is `subject_user_id` (+ `visibility user|internal`) and must be filtered explicitly; `platform_incidents` is keyed by `(platform, oauth_client_id)` while open (`cleared_at IS NULL`, 00:293).
- **Pack location rule:** the index is text-only in the repo (`docs/compliance/evidence-<yyyy-mm-dd>/index.md`); every binary (screencast, dumps) lives in `~/.nts/compliance/evidence-<yyyy-mm-dd>/` on the FileVault disk — never in the repo, never on `/Volumes/Project` (13.5.1-T1 D14/O7/T5) — and the `VP7_RedactionClean` grep result is recorded in the index's `Redaction checked` column (06.5.1-T1:117).
- **No PII in metrics:** `tiktok_publish_attempts_total{outcome}`, `tiktok_rate_limit_backoff_total{endpoint}`, `tiktok_active_user_cap_hit_total`, `tiktok_post_latency_seconds` — enum labels only (D18).

## 9. Test plan

No product code is written here, so verification is **checklist runs plus scripted probes**; automated tests exist only where the evidence is a code path owned by 07.2/07.3 (this folder adds no test file unless the owning task has none).

Automated (produced when building the error/limit artefact, owned by the code tasks, cited here):
- `NightTales.Api.IntegrationTests` (Testcontainers, real PostgreSQL) with `FakeTikTokServer` from `NightTales.TestKit` (Kestrel, hand-written, **no mocking library**): `creator_info` public/private option sets, `comment_disabled`, 200-with-error families; init ok + each 403/429 code + 1-hour URL expiry; status every terminal transition and each `fail_reason` family; token with **rotating** refresh; revoke; per-endpoint request counters for the limiter (D17, F22).
- `NightTales.Application.UnitTests` for the D9/D10 maps and the D5 validation matrix (hand-written fakes from `NightTales.TestKit`).
- `NightTales.Architecture.Tests` unchanged; the checklist cites the guards it relies on (one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command; only `UnitOfWorkBehavior` calls `SaveChangesAsync`).

Probes (this folder's own work; output pasted into the checklist artefact's probe column):

| # | Exercise | Expected observation |
|---|---|---|
| P1 | render the screen on staging twice (fresh reload) | `creator-info` fetched each render (U1); nickname shown; counter increments |
| P2 | create with privacy ∉ options, a creator-disabled interaction enabled, branded + `SELF_ONLY`, 2201-unit caption, over-long video, stale `consentTextVersion` | the six D10 codes of §5, one per probe |
| P3 | force a lost response after init (kill/restart the worker mid-command) | `publish_id` already committed, **no second init**, no second post (D7/F04); after `FAILED` a new init is allowed |
| P4 | walk the status ladder with `FakeTikTokServer` (`PROCESSING_DOWNLOAD` → `PUBLISH_COMPLETE`; `FAILED` per `fail_reason`) | U11 status shown; `external_post_id` = `publicaly_available_post_id[0]` when non-empty, else null; one `error_code` per family |
| P5 | disconnect the test connection while a publication is `scheduled` | revoke called, row `revoked`, `data_deletion_requests(scope = channel, reason = in_app_revoke, due_at = now + 7 d)` created |
| P6 | (07.4.1-T2, production) one public post after approval | `privacy` = a real option, `PUBLISH_COMPLETE` → `published`, `is_aigc = true`, U1–U13 re-run green |

Given/When/Then acceptance checks live in each task file (§ `## Acceptance checks`). Anything not reproducible with a probe stays a documented manual observation with a screencast timestamp — never a placeholder.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 07.4.1-T1 | M (1.0) | BA | S · R2 | 07.3.2-T1, 07.3.3-T1, 10.7.3-T1, 13.1.1-T1, 13.1.2-T1 | 13.4.3-T1 (07 CR-8), 07.4.1-T2 |
| 07.4.1-T2 | S (0.5) | BA + Ops | S · R2 | 07.4.1-T1, 13.4.3-T1 | — (unlocks public posting + `tiktok_publish` rollout, 07 CR-6) |

Rows above are the canonical README §8.1 rows. Task files: `07.4.1-T1-audit-evidence-pack-internal-checklist.md`, `07.4.1-T2-post-approval-enablement-checklist-rerun.md`.

**Re-estimate.** 1.5 d raw (M 1.0 + S 0.5), 0 buffered at folder level; plan-07 totals **7.75 raw / 9.3 buffered unchanged** — 07.4.1-T2 is a declared split of 07.4.1-T1's post-approval half, so the recomposition is cost-neutral (CR-1, README §9). Cost drivers: (a) the screencast must be one continuous take over U1–U13 — re-takes are the schedule risk; (b) the response loop is externally paced and not counted as effort; (c) 07.4.1-T2 is idle until TikTok answers, which is why it is a separate task.

**DoR conditions this folder must satisfy** (README §9): (1) the real `oauth/token`/`oauth/revoke` taxonomy recorded by 07.1.1-T1 → the error/limit artefact can cite it; (2) `PULL_FROM_URL` verification + presigned-URL TTL proven on staging (CR-4, 07.3.1-T1) → needed by the private-run evidence; (3) the 10.7.3-T0 design checked against §1.3 U1–U13 **before** 10.7.3-T1 starts → this folder's checklist is the instrument; (4) the `[OPEN]` items below are not DoR blockers (out of the audited path).

## 11. Rollout

**Flag and gates.** `tiktok_publish` is owner-only (`feature_flags.rules` = owner user ids, **no** percentage rollout) until `AuditStatus = approved`; recurring jobs (`PollTikTokStatusJob`, TikTok branch of `TokenHealthJob`) register only for the boundary owner scope (CR-6, D1, 06 D1 pattern). Sequence: `07-connect` → `07-screen-contract` → `07-post-private` → **`07-audit-ready`** (07.4.1-T1 + 10.7.3-T1 shipped → `13.4.3-T1` files) → **`07-public`** (07.4.1-T2: `AuditStatus = approved`, full option list, one public post, U1–U13 re-verified on production) → percentage rollout per CR-6.

**Rollback.** Set `TikTokOptions.AuditStatus` back to `pending` (config value, no migration, `IOptionsMonitor` reload): the option list collapses to `creator_info ∩ { SELF_ONLY }`, the flag returns to owner ids, recurring jobs deregister for non-owner scopes; in-flight `uploading|processing` publications keep polling (status calls do not need the flag) and published posts are untouched. Private posting keeps working throughout — by K9 the app is never "down" because of the audit.

**Monitoring during the window.** `tiktok_publish_attempts_total{outcome}`, `tiktok_post_latency_seconds` (init → `PUBLISH_COMPLETE`), `tiktok_active_user_cap_hit_total` (P2 at > 0), `tiktok_rate_limit_backoff_total{endpoint}`, plus `platform_incident_open > 0` for `url_ownership_unverified`. Volume guard: the live 24-h active-creator count must stay under the claimed `MonthlyActiveCreators` (K10) — the volume artefact's number is a commitment, not a wish.

**[OPEN] (do not silently add).** (a) `POST /v2/post/publish/cancel/` is best-effort and only useful during the download window (K8) — no 07 task implements it; recorded here so the audit answer is "not used". (b) The inbox mode (`video.upload`, `SEND_TO_USER_INBOX`) is out of scope; if it ever appears in a status response it becomes `failed` `tiktok_upload_failed` + alert (D9). (c) A distributed rate limiter is only needed if the Worker scales out (D6).

Cross-references: `../README.md` (§1 K/C/U, §2 F01–F25, §3 D1–D19, §4 CR-1–CR-12, §5 sequencing, §7 gates, §8.1, §9, §10 rulings) · `../../07-tiktok-posting.md` §4/§6 · `../06-youtube-posting/README.md` (D3–D5, D9, D11, D13–D17, D20, YR3, YR4, YR10, YR25–YR27) · `../13-compliance/` (`13.1.1-T1`, `13.1.2-T1`, `13.2.1-T1`, `13.3.1-T1`, `13.4.3-T1`, `13.5.1-T1`) · `../11-worker-deployment/` (`11.2-*`, `11.6-operations/11.6.2-T1`) · siblings `07.1-app-connect/`, `07.2-post-screen/`, `07.3-publish/`.
