# 07.1 — TikTok app registration, OAuth connect and connection security

Ids **07.1.1-T1**, **07.1.2-T1**, **07.1.2-T2** · Phase **S · R2** · flag `tiktok_publish` · folder `plans/features/07-tiktok-posting/07.1-app-connect/`

Contract: `../README.md` (§1 K/C + §1.3 U1–U13, §2 F, §3 D1–D19, §4 CRs, §5 sequencing, §7 gates, §8.1 canonical rows, §10 O1–O6 + TT1–TT12); 06 building blocks reused verbatim (C6, D15). Legend: ✅ docs/source · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

Owns the **TikTok app and the connection lifecycle**: app registration/verification (Login Kit + Content Posting API, redirect URIs, privacy/ToS URLs, `video.publish` scope + evidence, `PULL_FROM_URL` domain ownership, recorded OAuth error taxonomy) and connect → callback → upsert, reconnect, disconnect/revoke, re-auth, `oauth_client_id` pinning, the TikTok token-error classifier, connect e-mail. **3 tasks, 2.0 d** (README §9: 0.5 / 1.0 / 0.5, unchanged).

Out of scope: creator-info + validation/consent (`07.2-post-screen`); init/status/caps (`07.3-publish`); audit pack and the `AuditStatus = approved` flip (`07.4-audit`); **UI implementation** — `10.7.3-T1`, whose hard prerequisite is the design task **10.7.3-T0** (owner rule 2026-09-25, README §1.3/CR-7), so this folder has **no UI task** and changes no web surface; the shared tables/ports/patterns owned by 06 (`_06_OAuthStates`, `_06_ChannelConnections`, `oauth_states`, `channel_connections`, `platform_incidents`, `IOAuthClientResolver`, `ITokenProtector`, `IPlatformIncidentStore`, event names) and 09 (`IReauthGuard`, `ILegalAcceptance`, `IEmailSender`, `ISystemWorkQuery`, `IJobScheduler`, `idempotency_keys`). **No migration is added here** — every column written exists (C1, C6).

## 2. Confirmed facts

| Id | Fact | Evidence |
|---|---|---|
| K2 | `POST /v2/post/publish/creator_info/query/` (scope `video.publish`), **20 req/min per token**; returns `creator_avatar_url` (TTL 2 h), `creator_nickname`, `privacy_level_options`, `comment_disabled`, `duet_disabled`, `stitch_disabled`, `max_video_post_duration_sec`; its error struct arrives with **HTTP 200** for `spam_risk_*` / `reached_active_user_cap` | README K2 ✅ |
| K9 / K10 | Unaudited: private only, ≤ 5 posting users / 24 h, only `SELF_ONLY`; a 24-h active-creator cap per API client set by the audit-claimed volume; ≈15 posts/day/creator | README K9/K10 ✅ |
| K13 | Authorize `https://www.tiktok.com/v2/auth/authorize/` (`client_key`, `scope`, `response_type=code`, `redirect_uri`, `state`, `disable_auto_auth`); token `POST https://open.tiktokapis.com/v2/oauth/token/` (form: `client_key`, `client_secret`, `code`, `grant_type`, `redirect_uri`, `code_verifier`) → `open_id`, `scope`, `access_token`, `expires_in` 86400, `refresh_token`, `refresh_expires_in` 31536000; **refresh may return a new `refresh_token` that must be stored**; `code_verifier` only for mobile/desktop; revoke `POST https://open.tiktokapis.com/v2/oauth/revoke/` (`client_key`, `client_secret`, `token`) | README K13 ✅ |
| K14 / K15 | Redirect URI: absolute `https`, static, ≤ 512 chars, ≤ 10 per app. Scopes `user.info.basic`, `video.publish`, `video.upload`; the user may grant a **subset** | README K14/K15 ✅ |
| K16 / K17 | `PULL_FROM_URL`: `https`, DNS-verified domain/URL prefix, no redirect, reachable for the whole download; "do not use `FILE_UPLOAD`" for server-side content. The client secret stays confidential, never embedded or shared | README K16/K17 ✅ |
| C1 | `channel_connections(user_id, platform youtube\|tiktok, external_account_id, display_name, scopes text[], access_token_enc, refresh_token_enc, token_expires_at, status active\|revoked\|expired\|error, oauth_client_id, last_verified_at, revoked_at)` — **no avatar column** | 00 §6:289 ✅ |
| C4 | `GET /channels`, `POST /channels/{platform}/connect`, `GET /channels/{platform}/callback`, `DELETE /channels/{id}`; `Idempotency-Key` on platform-spending POSTs; ProblemDetails with a stable `code` | 00 §8:348-372 ✅ |
| C6 | 06 owns `oauth_states` (+ `browser_nonce_hash`), `_06_ChannelConnections`, `IOAuthClientResolver`/`ConfigOAuthClientResolver`, `ITokenProtector` purpose `tokens.v1/{connectionId}`, `platform_incidents` + `IPlatformIncidentStore.RaiseAsync` + `nts-worker incident clear`, audit names `publishing.*` | README C6 ✅ |
| C8 | `channel_connection_changed.v1`; `platform_incident_raised/cleared.v1`; realtime ids-only | README C8 ✅ |
| D12 | Scope **`video.publish` only**; `disable_auto_auth=1`; 10-min single-use state row with `code_verifier_protected = NULL` for web; anonymous callback (policy `auth`): claim → nonce → exchange with the client **bound to the state** → granted-scope gate → `creator_info` once → upsert → audit → e-mail → `302`; reconnect `FOR UPDATE` re-protect + re-pin (06 YR11); `DELETE` revoke 3× backoff then wipe, cancel `awaiting_consent\|scheduled` publications, null tokens, `status = revoked`, outbox, `data_deletion_requests(scope = channel, reason = in_app_revoke, due_at = now + 7 d)` | README §3 D12 ✅ |
| D13 | Access 24 h / refresh 365 d; **always store the returned pair**; refresh writes both encrypted values inside the connection-row `FOR UPDATE`, same transaction as the read; `TokenHealthJob` daily, ≤ 500 connections/run, skips clients with an open incident; token-endpoint errors classified per D10 | README §3 D13 ✅ |
| D14 | Channel routes filter `user_id`, another user's id → 404; tokens/`open_id` never in DTOs, logs, audit `data`, exceptions; audit `publishing.channel_connected\|channel_disconnected` (ids/codes only); secret `nts/<env>/api/TIKTOK_CLIENT_SECRET`; LB access log drops the query string for `/api/v1/channels/tiktok/callback` | README §3 D14 ✅ |
| D6 | `ITikTokRateLimiter.TryAcquireAsync(Guid connectionId, TikTokEndpoint endpoint, CancellationToken ct) → ErrorOr<Unit>`; buckets `init` 6/min, `creator_info` 20/min, `status` 30/min, `token` 10/min, `revoke` 10/min (last two [INFERENCE]); in-flight per connection = 1; a denied acquire defers the job, never fails the publication | README §3 D6 ✅ |
| D10 | Error table + **401 ladder**: refresh once + retry in the same command → still 401 ⇒ per-connection `expired` + `tiktok_reauth_required`; an incident only when the token endpoint itself fails or ≥ 2 distinct connections of one `oauth_client_id` fail within 1 h; an unknown 200-error is logged with `log_id` and treated as `tiktok_upload_failed` | README §3 D10 ✅ |
| D18 | Metrics `tiktok_publish_attempts_total{outcome}`, `tiktok_rate_limit_backoff_total{endpoint}`, `tiktok_active_user_cap_hit_total`, `tiktok_post_latency_seconds`; one vi/en copy key per D10 code | README §3 D18 ✅ |
| D19 | One `TikTokOptions` (section `TikTok`, `IOptionsMonitor`, validated at start): `ActiveClientId`, `Clients:<clientId>:SecretName`, `AuditStatus`, `ConsentTextVersion`, `Transfer`, `UrlTtlMinutes` 120, `Poll:*`, `RateLimits:*`, `MonthlyActiveCreators`, `CaptionMaxUtf16Units` 2200; no other key read anywhere | README §3 D19 ✅ |
| TT4 / TT11 / TT12 | Unknown/ambiguous refresh or revoke error ⇒ **platform incident first**; only a named grant/token error marks `expired`; 07.1.1-T1 records the real names. `IExternalLookup` is the sanctioned port name (`00-overview.md:211`, `07-tiktok-posting.md:85`) — extend it, never a parallel convention. 06's `platform_quota_usage` is **not** used by TikTok | README §10 ✅ |
| S-06 | `oauth_states(id, user_id, platform, state_hash, code_verifier_protected, oauth_client_id, refresh_family_id, created_at, expires_at, used_at)`; state = 32 random bytes base64url; verifier protected with purpose `oauth.v1`; `__Host-nts_oauth` HttpOnly/Secure/SameSite=Lax/Path=/ 10 min with its hash in `browser_nonce_hash` and a constant-time check (06 YR10); `RaiseAsync` itself writes `publishing.incident_raised` + `platform_incident_raised.v1` when `IsNew` (06 YR25) | 06 README D3, YR10, YR25 ✅ |
| S-06b | `OAuthErrorClassifier.Classify(YouTubeFailure)` is **YouTube-typed** (`06.2.3-T3:29-31`) and that task's cutover rule bans "a second switch over Google reasons" outside it (`:53`) | `06.2.3-upload/06.2.3-T3-*.md` ✅ |
| S-09 | `IReauthGuard.RequireRecentAsync(TimeSpan, ct) → ErrorOr<Success>` → 401 `reauth_required` (connect ≤ 5 min); `ILegalAcceptance.EnsureCurrentAsync` → 409 `legal_acceptance_required`; `IEmailSender.QueueAsync(EmailMessage) → ErrorOr<Guid>` + `Templates/Email/<name>.<locale>.{txt,html}` (`channel_connected` registered by 06.1.2-T2/09.2.6-T3); every command has exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` (09.3.6-T1) | 09 (auth/email), 06 C6 ✅ |
| ⚠️ | The real `oauth/token` / `oauth/revoke` error codes and descriptions against our staging client, and those endpoints' documented rate limits | recorded by 07.1.1-T1 (TT4; D6 [INFERENCE]) |

## 3. Domain and data

| Table | Owner | What 07.1 writes |
|---|---|---|
| `oauth_states` | 06 | per attempt: `platform = 'tiktok'`, `state_hash` = SHA-256 of 32 random bytes base64url, `code_verifier_protected = NULL` (web, K13), `oauth_client_id` = active client key, `refresh_family_id` = caller's `sid`, `browser_nonce_hash`, `expires_at = now + 10 min`, `used_at` |
| `channel_connections` | 06 | `platform = 'tiktok'`, `external_account_id = open_id`, `display_name = creator_nickname`, `scopes = { 'video.publish' }`, `access_token_enc`/`refresh_token_enc` (purpose `tokens.v1/{connectionId}`), `token_expires_at = now + expires_in`, `status`, `oauth_client_id` (pinned), `last_verified_at`, `revoked_at` |
| `platform_incidents` | 06.2.3-T2 | only via `IPlatformIncidentStore.RaiseAsync` (client-level OAuth failures) |
| `data_deletion_requests` | 13.3.1-T1 | on disconnect: `scope = 'channel'`, `target_id = connectionId`, `reason = 'in_app_revoke'`, `due_at = now + 7 d` |
| `idempotency_keys` / `outbox_messages` / `audit_logs` | 09 | connect POST key; `channel_connection_changed.v1`; `publishing.channel_connected` / `channel_disconnected` |

**No avatar column exists** (C1) ⇒ the callback persists **only** the nickname in `display_name`; the 2-h-TTL avatar is served live by `GET /channels/{id}/tiktok/creator-info` (07.2.1-T1, D11) ⚠️ (README D12/D14 say "avatar on the connection"; reported as a gap).

**State machine.**

| From | Event | To | Effects (one transaction) |
|---|---|---|---|
| — | callback success, unseen `open_id` | `active` | insert; tokens protected; audit `publishing.channel_connected`; `channel_connected` e-mail queued; outbox |
| `active` | reconnect (same user + `open_id`) | `active` | `FOR UPDATE`; tokens re-protected under the **same** id; `oauth_client_id` re-pinned; `last_verified_at = now`; audit + outbox |
| `active` | another **user**, same `open_id` | — | no write; `302 /channels?error=channel_already_connected`; unique `(platform, external_account_id) WHERE status <> 'revoked'` is the backstop |
| `active` | named grant/token error, or 401 after one refresh + retry | `expired` | tokens kept; `status = 'expired'`; outbox; its publications fail `tiktok_reauth_required` |
| `expired` | re-auth via the connect route | `active` | as reconnect |
| `active` | `DELETE /channels/{id}` | `revoked` | revoke at TikTok (3× backoff) then tokens nulled; `revoked_at = now`; `awaiting_consent\|scheduled` publications `cancelled`; deletion request; audit + outbox |
| `revoked` | `DELETE` again | `revoked` | 204, no second deletion row (partial unique, 06 YR13) |

**Transactions and locks — no DB transaction is held across HTTP.**
```sql
-- claim (committed on its own, so a lost response cannot replay)
UPDATE oauth_states SET used_at = now()
 WHERE state_hash = @h AND platform = 'tiktok' AND used_at IS NULL AND expires_at > now()
 RETURNING id, user_id, oauth_client_id, refresh_family_id, browser_nonce_hash;

-- upsert: 0 rows ⇒ insert, 1 row same user ⇒ reconnect, other user ⇒ channel_already_connected
SELECT * FROM channel_connections
 WHERE platform = 'tiktok' AND external_account_id = @openId AND status <> 'revoked'
 FOR UPDATE;
```
The connection id (UUIDv7) is generated **before** `ITokenProtector.Protect(id.ToString(), …)` (06 D4); disconnect and refresh take the row `FOR UPDATE` **before** any write (YR11). Indexes relied on, already shipped (06 D18): `channel_connections (user_id)`, unique `(platform, external_account_id) WHERE status <> 'revoked'`, unique `oauth_states (state_hash)`, partial unique `(platform, oauth_client_id) WHERE cleared_at IS NULL` — the path is O(1) on these, no new index.

`TikTokOptions` (D19) is declared by 07.1.2-T1 from the values 07.1.1-T1 records; validation fails startup on a missing `ActiveClientId`, an unknown client key, a non-`https` redirect URI or `UrlTtlMinutes < 120`.

## 4. Use cases

- **UC-1 Connect** — **G** owner + flag on + re-auth ≤ 5 min + current legal acceptance. **W** `POST /channels/tiktok/connect` with `Idempotency-Key`. **T** 200 `{ authorizationUrl }` whose query is exactly `client_key`, `scope=video.publish`, `response_type=code`, `redirect_uri`, `state`, `disable_auto_auth=1` (no `code_verifier`, K13); one state row as §3; `__Host-nts_oauth` set; the user's previous unused TikTok row stamped used (≤ 1 live attempt per user, which also bounds the callback's outbound calls).
- **UC-2 Callback success** — row claimed in one `UPDATE … RETURNING`; nonce matches (constant time); bound family live; code exchanged with the state's client; `scope` contains `video.publish`; one `creator_info`; one `active` row with pinned client, decryptable tokens and the audit/outbox/e-mail rows; `302 /channels?connected=tiktok`.
- **UC-3 Scope refused** — no row, no audit, no alert; `302 /channels?error=tiktok_scope_missing` (F25: a callback never answers JSON and a user's refusal is never pageable).
- **UC-4 Duplicate account** — user B resolving A's `open_id`: no write, `302 /channels?error=channel_already_connected` ⚠️ (D12 states 409; the callback cannot return JSON — reported).
- **UC-5 Reconnect** — same row, new tokens under the existing id, client re-pinned, `302 /channels?connected=tiktok`.
- **UC-6 Disconnect** — revoke with the pinned client (3× backoff), then tokens NULL, `revoked`, publications `cancelled`, 7-day deletion request, outbox + audit, 204; a revoke failure is logged/alerted, never a 5xx; a repeat is 204 with no second request.
- **UC-7 Re-auth** — `status = 'expired'` visible on `/channels`; re-running connect (`disable_auto_auth=1`) returns the **same** row to `active`.
- **UC-8 Unknown token error** — the connection is not marked `expired`; one `platform_incidents` row for that `oauth_client_id` (P1 via 11.6.2-T1), body logged with `log_id` only; `TokenHealthJob` skips that client until `nts-worker incident clear` (TT4, 06 D14/YR25).
- **UC-9 Flag off / not owner** — connect, callback and `DELETE` → 404 `feature_disabled`.

## 5. API and events

| Route | Policy | Result | Codes |
|---|---|---|---|
| `POST /channels/tiktok/connect` | bearer + `RequireFeature("tiktok_publish")` + `IReauthGuard.RequireRecentAsync(5 min)` + `ILegalAcceptance.EnsureCurrentAsync`; `Idempotency-Key` required | 200 `{ authorizationUrl }` | 401 `reauth_required`, 409 `legal_acceptance_required`, 404 `feature_disabled`, 400 `validation_failed` |
| `GET /channels/tiktok/callback?code&state` | **anonymous**, policy `auth`, `[AllowAnonymousRequest]`, never JSON | `302 /channels?connected=tiktok` \| `302 /channels?error=<code>` | `oauth_state_invalid`, `oauth_denied`, `oauth_exchange_failed`, `tiktok_scope_missing`, `channel_already_connected` ⚠️; never an alert |
| `DELETE /channels/{id}` | bearer + flag; idempotent (3 revoke attempts, ≤ 6 s) | 204 | 404 `not_found` (another user's id) |
| `GET /channels` (06.1.2-T1 owns; 07.1 extends the projection) | bearer | `ChannelConnectionDto { id, platform, externalAccountId, displayName, status, lastVerifiedAt, connectedAt }` (06 D4) | — |
| `GET /channels/{id}/tiktok/creator-info` | 07.2.1-T1 (D11) — seam only | — | — |

`error_code` values this folder writes onto a publication: `tiktok_reauth_required`, `platform_incident` (D10; declared once in Domain `PublicationErrorCodes`, TT3). **Events** (C8): outbox `channel_connection_changed.v1 { channelConnectionId, userId, platform: "tiktok", status }` on insert, reconnect, `expired`, `revoked`; incidents raise their own events (06 YR25). No `publication_updated.v1`, no realtime message here (that is 07.3's flow).

## 6. UI

**No UI task in this folder** (owner rule; design `10.7.3-T0`, implementation `10.7.3-T1`, README §1.3/CR-7). Supplied to 10.7.1's TikTok connect card (id created by Lead10S): the connect round trip (call the connect route, navigate the **top-level browser** to `authorizationUrl`, return on the `302` to `/channels?connected=tiktok` — a navigation, not a JSON call); the `error` query values → `errors.tiktok.scopeMissing`, `errors.oauth.denied`, `errors.oauth.exchangeFailed`, `errors.oauth.stateInvalid`, `errors.channels.alreadyConnected`; the `status` values shown (`active`, `expired` ⇒ re-auth affordance, `revoked`); plus `errors.tiktok.reauthRequired`, `errors.legal.acceptanceRequired`, `errors.reauthRequired`, `errors.feature.disabled` (existing). The post screen consumes 07.2/07.3 and shows no date field (D3/TT6); none of it is implemented here.

## 7. Jobs and integrations

- **No new job.** `TokenHealthJob` (06.4.2, daily, shared, `[JobPolicy]` from 06) gains its **TikTok branch** here (07.1.2-T2): ≤ 500 `active` TikTok connections whose client has no open incident, `ISystemWorkQuery.PendingAsyncT` claims + one command per row (06 YR26), refresh under the row `FOR UPDATE`, store the returned pair (D13), classify per D10/TT4; ids-only.
- **E-mail:** `channel_connected` (06.1.2-T2's catalog entry in 09.2.6-T3, `Templates/Email/channel_connected.<locale>.{txt,html}`, vi+en), queued with `IEmailSender.QueueAsync` **inside** the upsert transaction (a queueing failure fails the command, 06 D3), sent by `SendEmailJob`; no new template name.
- **Outbound:** `https://www.tiktok.com/v2/auth/authorize/`, `https://open.tiktokapis.com/v2/oauth/token/`, `https://open.tiktokapis.com/v2/oauth/revoke/`, `POST open.tiktokapis.com/v2/post/publish/creator_info/query/`; base URLs from options; retries only as D10 allows.
- **DNS/storage:** 07.1.1-T1 registers the storage host's `PULL_FROM_URL` domain/URL prefix (DNS TXT) — the gate for 07.3's presigned URL (CR-4). No blob call here.
- **Observability:** logs `tiktok.connect.started|completed|failed`, `tiktok.token.refresh_failed|rotated`, `tiktok.incident.classified`, `tiktok.oauth.escalated`, `tiktok.revoke.failed` — with `channelConnectionId`, `oauthClientId`, `errorCode`, `log_id`, never `open_id`/nickname/tokens. Metrics: `tiktok_connection_events_total{event}`, `tiktok_oauth_errors_total{endpoint,kind}`, `tiktok_token_refresh_total{outcome}` (all D18) plus the reused `tiktok_rate_limit_backoff_total{endpoint}`. Alerts: existing 11.6.2-T1 rules only (`platform_incident_open > 0`, P1).

## 8. Security and privacy

- **Tenant:** every route/command filters `user_id`; another user's id → 404 `not_found` (never 403); cross-tenant reads here are counts only (connections per `oauth_client_id`).
- **Secrets:** `client_secret` read per call by name (`TikTokOptions.Clients:<clientId>:SecretName` → `nts/<env>/api/TIKTOK_CLIENT_SECRET`); never in config, logs, exceptions, DTOs or the repo (K17); masked by the Serilog destructuring policy; a log-scan test asserts absence.
- **Tokens:** `ITokenProtector` purpose `tokens.v1/{connectionId}`, id generated before protection; only `open_id` and `display_name` are persisted as account identifiers; never in `platform_payload`, DTOs, audit `data` or exceptions.
- **OAuth binding:** single-use 10-min state row + `__Host-nts_oauth` (HttpOnly/Secure/SameSite=Lax/Path=/), SHA-256 compared with `CryptographicOperations.FixedTimeEquals` (06 YR10); PKCE deliberately absent for web (K13 — the secret is the proof); connect needs re-auth ≤ 5 min + current legal acceptance; the authorize URL is returned in a **body**, so no third party can force a connect; a new connect invalidates the user's previous unused TikTok state row (≤ 1 live attempt).
- **Callback hygiene:** anonymous, no cookie auth (CSRF-neutral), constant-time nonce check; the LB access log drops the query string for `/api/v1/channels/tiktok/callback` (CR-10, mirror 06 YR23) so the `code` never reaches logs.
- **Least privilege:** scope `video.publish` only (O5, F13) — it covers Direct Post and `creator_info` (K1/K2), so `user.info.basic` is **not** requested.
- **Privacy/retention:** disconnect files the 7-day `data_deletion_requests` row; the 13.3.1-T1 **channel** scope nulls tokens and drops the account identifiers, while the `platform_payload` purge belongs to the **publication** scope (that column is on `publications`, 00 §6:291, not on `channel_connections`); `revoked_at` stays for the audit trail; nothing from `creator_info` is cached across users (D14/F15); the transient avatar is never stored.
- **Arch tests (09.3.6-T1):** callback `[AllowAnonymousRequest]`; exactly one audit attribute per command; no tenant-filter bypass in these handlers; the TikTok secret name appears in no `appsettings*.json`.

## 9. Test plan

Projects: **NightTales.Domain.UnitTests**, **NightTales.Application.UnitTests**, **NightTales.Api.IntegrationTests** (real PostgreSQL 17, `PostgresFixture`), **NightTales.Architecture.Tests**; hand-written fakes from **NightTales.TestKit** only (`FakeTikTokServer` per D17: token with rotating refresh + invalid code + an unknown body, revoke ok/500, creator_info option sets, per-endpoint counters; plus `FakeClock`, `TestAuthHandler`, `ApiFactory`, `InlineJobScheduler`, `RecordingEmailSender`, `RecordingIncidentStore`).

| # | Test (project) | Assertion |
|---|---|---|
| T1 | AssertAuthorizationUrl_NoPkce (Application.UnitTests) | exactly the six query fields incl. `disable_auto_auth=1`; no `code_verifier` |
| T2 | CreateState_HashedStateAndNonce (Api.IntegrationTests) | row as §3, `state_hash = SHA256(state) ≠ state`, `code_verifier_protected IS NULL`, `expires_at ≈ now+10m`; same `Idempotency-Key` returns the same URL with no second row; a new connect invalidates the previous unused row |
| T3 | Callback_HappyPath (Api.IntegrationTests) | 302 `?connected=tiktok`; row with `open_id`/nickname/`{video.publish}`/`active`/`token_expires_at ≈ now+86400s`; tokens decrypt under `tokens.v1/{id}`; pinned client; audit + outbox + one e-mail row |
| T4 | Callback_ReplayOrWrongBrowser (Api.IntegrationTests) | 302 `oauth_state_invalid`, no row, state stays claimed |
| T5 | Callback_ScopeMissing (Api.IntegrationTests) | granted `user.info.basic` only → 302 `tiktok_scope_missing`, 0 connections, 0 audit rows, 0 incidents |
| T6 | Callback_DuplicateOpenId (Api.IntegrationTests) | 302 `channel_already_connected`, still one row, no 5xx |
| T7 | Reconnect_Repins (Api.IntegrationTests) | same id, both ciphertexts changed, new `oauth_client_id`, `last_verified_at` advanced |
| T8 | Disconnect_RevokesWipesDeletionRequest (Api.IntegrationTests) | revoke once with the pinned `client_key`; tokens NULL; `revoked`; publications `cancelled`; one deletion row `due_at ≈ now+7d`; audit; 204 |
| T9 | Disconnect_RevokeFails_StillWipes (Api.IntegrationTests) | 500 ×3 → 204, tokens NULL, one warning, no 4th attempt |
| T10 | Refresh_RotatesOrKeepsOldRefresh (Api.IntegrationTests) | both ciphertexts change on rotation; when the response omits `refresh_token` the old ciphertext stays (never NULL) |
| T11 | Classifier_NamedVsUnknown (Application.UnitTests) | named ⇒ `expired` + outbox with **no** `RaiseAsync`; unknown/unparseable ⇒ `RaiseAsync` once, no connection write, no second incident; 5xx ⇒ transient |
| T12 | Job_SkipsIncidentClientsAndCaps (Api.IntegrationTests) | incident client skipped, resumes after clear, ≤ 500 rows, one command per row |
| T13 | Connect_GuardFailures (Api.IntegrationTests) | 401 `reauth_required`; 409 `legal_acceptance_required`; 404 `feature_disabled`; foreign `{id}` → 404 |
| T14 | Architecture_AttributesPinningSecrets (Architecture.Tests) | callback `[AllowAnonymousRequest]`; one audit attribute per command; no tenant bypass; secret name in no `appsettings*.json` |

No test asserts wiring, mocked echoes or source text; `FakeTikTokServer` counters are used only where a limiter/backoff claim is under test (D17).

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 07.1.1-T1 | S (0.5) | Ops/BA | 13.1.1-T1 (soft), 13.1.2-T1 (soft) | 07.1.2-T1 | S · R2 |
| 07.1.2-T1 | M (1.0) | BE + Security | 07.1.1-T1 (soft), 06.1.2-T1, 06.1.2-T2, 09.3.2-T1, 09.9.4-T1, 13.2.1-T1 | 07.1.2-T2, 07.2.1-T1, 10.7.3-T1 (07 CR-7, soft) | S · R2 · flag `tiktok_publish` |
| 07.1.2-T2 | S (0.5) | BE + Security | 07.1.2-T1, 06.1.2-T2, 09.2.6-T3 | 07.2.1-T1 | S · R2 · flag `tiktok_publish` |

**2.0 d** raw, unchanged (README §9: 07.1.1-T1 loses the doc-reading work — now §1 — but gains the `PULL_FROM_URL` domain verification). Order `07.1.1-T1 (soft) → 07.1.2-T1 → 07.1.2-T2`; `07.2.1-T1` follows `07.1.2-T2` (README §5). 07.1.1-T1 can start against a contract-true fake; its acceptance run needs the real app, secret and DNS record.

## 11. Rollout

- `tiktok_publish` ships **off**; `feature_flags.rules` = owner user ids, no percentage rollout; D16's recurring jobs register only for the boundary owner scope (CR-6). Connect works while unaudited so the audit evidence can be produced on the owner's account; `AuditStatus` stays `pending` until 07.4.1-T2 (D4).
- **Data:** no migration, no backfill; rollback = flip the flag (routes answer 404 `feature_disabled`); connections stay visible and revocable.
- **Config:** the `TikTok` section per env + secret `nts/<env>/api/TIKTOK_CLIENT_SECRET`; one redirect URI per env (staging and prod are separate app entries, K14).
- **Gates:** `07-connect` (README §7) plus the DoR conditions — the recorded token/revoke taxonomy and rate limits (⚠️ → ✅) before the flag leaves owner-only; [OPEN] deferred: `POST /v2/post/publish/cancel/` and inbox (`video.upload`) mode (README §11.4).
- **Observability at rollout:** §7 logs + all 7 D18 metrics + `platform_incident_open > 0` (P1).
