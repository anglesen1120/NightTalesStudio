# 09.3-account-security-s — Account security, activity and sessions (wave 3, S)

## 1. Scope

This folder makes account takeover hard and auditable, and it is the only writer of the six artefacts §C0 brief 1 names. Total **3.5 raw days** (§C8); every task is `S · M2`, none is gated by a feature flag, and none is a UI task.

| Task | This folder builds | Decisions | Findings |
|---|---|---|---|
| 09.3.1-T2 | `IBreachedPasswordChecker` over the HIBP Pwned Passwords range API (`Add-Padding: true`) with a bundled top-N seed and fail-open; registration hardening (disposable-domain check, `email_not_verified` gate test, CAPTCHA and rate-limit wiring). It does **not** add a second register handler | §C3 D3 | F01, F07 |
| 09.3.2-T2 | `IStampValidator` (claim compare, ≤ 30 s cache, outbox eviction, `LISTEN`-reconnect clear) + `ISecurityEventWriter.RotateStampAsync` + the credential-event matrix | §C3 D9 | F11 |
| 09.3.3-T2 | `auth_failure_counters` (W3 CR-1) + `ICaptchaVerifier` (Cloudflare Turnstile) + the CAPTCHA gate, progressive delay and `AuthFailureSweepJob` | §C3 D10 | F02, F07, F08 |
| 09.3.3-T3 | MFA failure detector (`mfa_failed_notice`, ceiling `mfa_locked_untrusted`), `trusted_devices` (`__Host-nts_device`), the fixed evaluation order, the re-auth attempt limit | §C3 D11 | F09 |
| 09.3.6-T2 | `/me/activity` + `/episodes/{id}/activity` projection over `audit_logs` with the frozen `messageKey` allow-list | §C3 D11, D18 | F10 |
| 09.3.7-T1 | `GET /me/sessions`, `DELETE /me/sessions/{id}`, `POST /me/sessions/revoke-all`, `session_new_device` | §C3 D12 | — |

Out of scope (owned elsewhere, never re-implemented): register/verify/resend and `ModeAwareRegistrationFlow` (**09.3.1-T3**), login/refresh/logout (**09.3.2-T1**), the MFA login step (**09.3.2-T3**), forgot/reset (**09.3.3-T1**), rate-policy definitions (**09.3.5-T1**), the audit writer (**09.3.6-T1**), the e-mail pipeline (**09.2.6-T3**), admin surface (**09.9-admin-surface/**), settings/secrets (**09.10-settings/**), owner governance and public cancel links (**09.10.4-owner-governance/**), billing admin routes and the `trial.credits` grant (plan **12**, §C3 D13), DSAR/export (**13**). Not in this wave either: impersonation, `POST /auth/check-password`, a second `/admin` group, a new outbox event type, Redis (W3-TL5), any API that returns a secret or raw `audit_logs.data` (§C3 D20).

Consumed signed ids (never re-opened): `09.2.3-T2`, `09.3.1-T1`, `09.3.1-T3`, `09.3.2-T1`, `09.3.2-T3`, `09.3.3-T1`, `09.3.5-T1`, `09.3.6-T1`, `09.5.1-T1`, `09.2.4-T1`, `09.2.6-T3`, `09.8.1-T1`, `09.10.4-T1` (Parts A/B), plus plan 10's consumers through W3 CR-14.

Change requests this folder relies on (**PROPOSED**, not yet applied — a writer may start against the current text, but the acceptance check is against the new text once the TL applies it, README §C4): **W3 CR-1** (`auth_failure_counters`), **CR-1b/1c/1d** (indexes on `users`, `refresh_tokens`, `trusted_devices`), **CR-3** (`captcha_invalid` 400, `captcha_unavailable` 503), **CR-7** (`anon` policy + the `pwnedpasswords`/`turnstile` clients), **CR-8** (`session_new_device`, `mfa_failed_notice`), **CR-9** (`RegisterCommand(..., string? CaptchaToken)`), **CR-10** (folder-truth line), **CR-14** (10's `Depends on` cells).

Gate owned here: **09-W3-auth-hardening** (§C8), walked on the local stack; it blocks the DoR verdict, not the merge of these task files.

## 2. Confirmed facts

| Id | Fact used here | Evidence |
|---|---|---|
| W3-1 | Pwned Passwords is free, unauthenticated; `GET https://api.pwnedpasswords.com/range/{first 5 SHA-1 chars}` returns suffix+count; `Add-Padding: true` pads to 800–1,000 rows with count 0; no rate limit; **no licence or attribution requirement** on the range API (CC BY 4.0 covers only the breach/paste APIs, which we never call) | README §C1 ✅ |
| W3-2 | Turnstile siteverify: `POST https://challenges.cloudflare.com/turnstile/v0/siteverify`, form-encoded or JSON, always JSON back; required `secret`, `response`; optional `remoteip`, `idempotency_key`; token ≤ 2,048 chars, valid 300 s, **single use**; replay → `timeout-or-duplicate`; response has `success`, `error-codes`, `hostname`, `challenge_ts` | README §C1 ✅ |
| W3-3 | Turnstile: CAPTCHA-free, privacy-preserving, no CDN traffic, widget types Managed/Non-interactive/Invisible, docs page WCAG 2.2 AA compliant | README §C1 ✅ |
| W3-4 | Turnstile's "no request cap" is ⚠️ secondary sources; hCaptcha's Publisher plan monetises ML data labelling; reCAPTCHA is Enterprise-only with a billing instrument | README §C1 ⚠️ |
| W3-7 | §5.4 already has `captcha_required`, `breached_password`, `email_not_verified`, `not_found`, `validation_failed`, `rate_limited`, `mfa_locked_untrusted`, `session_revoked`, `reauth_required`; there is **no** code for a failed/again CAPTCHA or an unreachable siteverify → W3 CR-3 | README §C1 ✅ |
| W3-9 | Columns already exist: `users(security_stamp, role, status, last_totp_step, two_factor_enabled, last_login_at, owner_dispute_freeze_until)`, `refresh_tokens(family_id, revoked_at, expires_at, ip, user_agent, reauthenticated_at, reauth_attempts, reauth_last_attempt_at)`, `auth_challenges`, `trusted_devices`, `audit_logs(category, visibility, actor_type, subject_user_id, ip, user_agent, data)`, `user_action_tokens`, `email_messages`. **No table exists for per-(account, IP/device) login-failure counters** | README §C1, `00-overview.md` §6 ✅ |
| W3-10 | 09.3.1-T3 owns `POST /auth/register` + `/auth/verify-email` + resend, `ModeAwareRegistrationFlow`, `RequireVerifiedEmailFilter`, `Registration:Mode`, `IRegistrationPolicyProvider`; its mode set is `closed \| invite \| open` (BD2 wrote two values; fixed by W3 CR-12) | README §C1 ✅ |
| W3-12 | Task-file header format of a sound wave: H1 `# <id> — <title>`, one row `\| Story \| Size \| Area \| Depends on \| Blocks \| Phase / flag \|`, then the 9 headings `Goal, Inputs, Design, Steps, Outputs, Acceptance checks, Tests, Failure handling and edge cases, Notes` | README §C1 ✅ |
| — | `SaveChangesAsync` is called only by `UnitOfWorkBehavior`; every new command carries exactly one of `[Audited]/[NotAudited]/[AllowAnonymousRequest]` (09.3.6-T1 arch test) | Part B ✅ |
| — | `audit_logs` already has `(subject_user_id, created_at DESC)`, `(entity_type, entity_id) WHERE entity_id IS NOT NULL`, `(created_at, id)` — the two feed queries need no new index | README §C3.2 ✅ |

Legend: ✅ verified on disk, ⚠️ secondary/uncertain source, [DECISION] owner/TL-ruled, [OPEN] unresolved elsewhere, [INFERENCE] derived, not read.

## 3. Domain and data

**New table (W3 CR-1).** `auth_failure_counters`: `id uuid`, `scope text` (`account`/`ip`/`device`), `key_hash text` (HMAC-SHA256 with `Auth:CounterPepper` — no raw e-mail, IP or device string is ever stored), `failures int`, `window_started_at timestamptz`, `last_failure_at timestamptz`, `delay_until timestamptz NULL`, `captcha_required_at timestamptz NULL`, `user_id uuid NULL` (set for `scope = account`), `updated_at timestamptz`; unique `(scope, key_hash)`; `(updated_at)` for the sweep. Rows older than 24 h are deleted hourly in batches of ≤ 5,000.

**Indexes added to existing tables (same CR).** `refresh_tokens (user_id, revoked_at, expires_at)` (CR-1c — `/me/sessions`, revoke-all, family checks, 09.3.7-T1), `trusted_devices (user_id, expires_at)` (CR-1d — the device lookup on the MFA step, 09.3.3-T3); CR-1b (`users`) and CR-1e/1f belong to other folders.

**Used as they are.** `users(security_stamp, last_totp_step, role, status, two_factor_enabled, last_login_at)`, `auth_challenges`, `trusted_devices`, `refresh_tokens`, `user_action_tokens`, `email_messages`, `audit_logs`, `outbox_messages`, `platform_incidents`.

**Value semantics that must not be mixed up.**
- `key_hash` is a peppered **HMAC**, not a plain SHA-256 of the value; the pepper (`Auth:CounterPepper`) is never logged or returned.
- A `trusted_devices` row is bound to `refresh_family_id`, revoked with that family and by **every** credential event, and it never skips TOTP.
- The trusted-device cookie is `__Host-nts_device` (32 random bytes, `Secure`, `HttpOnly`, `Path=/`, no `Domain`); only its hash is stored.
- `SessionDto(id, createdAt, lastUsedAt, expiresAt, ip, userAgent, deviceLabel, isCurrent)` and `ActivityItemDto(occurredAt, category, messageKey, params, targetType, targetId, actorLabel, reason, ip, device)` — verbatim from §C3.1 (09 §4.3.6).
- `audit_logs.action` **is** the wire `messageKey` (W3-TL2); the §C3.4 set is released in one go; `params` is built only from the listed keys; `data jsonb` is never returned; `ip`/`device` are filled only when `subject_user_id = @me AND category = 'account_security'`.
- `RegistrationMode` ∈ `{ closed, invite, open }` (§C3 D15); `RegistrationSettings` extends the signed `RegistrationPolicy` record with `int TrialCredits`.

**Options (bound, frozen names).** `SecurityOptions`: `StampCacheSeconds 30`, `CounterWindowMinutes 30`, `CaptchaAfterFailures 3`, `ProgressiveDelayMaxSeconds 60`, `MfaFailureWindowMinutes 30`, `MfaFailedThreshold 5`, `MfaCeilingPerDay 50`, `ReauthWindowSeconds 300`, `ReauthMaxAttempts 5`, `TrustedDeviceDays 30`, `NewDeviceWindowDays 90`. `PwnedPasswordsOptions`: `BaseUrl` (`https://api.pwnedpasswords.com`), `TimeoutSeconds 5`, `CacheMinutes 10`, `CacheSizeLimit 2000`, `BundledSeedEnabled true`, `FailOpen true`. `CaptchaOptions`: `Enabled`, `Provider turnstile`, `SiteKey`, `SecretKey SecretString`, `VerifyUrl` (`https://challenges.cloudflare.com/turnstile/v0/siteverify`), `TimeoutSeconds 5`, `FailOpen true`, `AlertThrottleMinutes 5`; `SecretKey` comes from the `captcha` settings integration (W3 CR-2) and is never sent to the SPA.

**Single-writer rule.** `users.security_stamp` is assigned only inside `ISecurityEventWriter`; a scan test fails if it is assigned anywhere else (F11).

## 4. Use cases

| # | Actor / trigger | Outcome | Task |
|---|---|---|---|
| UC-1 | Anonymous registers (`mode = open`) with a breached password | 400 `breached_password`; the check is inline (no standalone endpoint, §C3 D3) | 09.3.1-T2 |
| UC-2 | Register with a disposable domain | Rejected through the signed registration validation; no second register path | 09.3.1-T2 |
| UC-3 | HIBP unreachable during register | Fail-open + one audit row + a throttled alert; `breached_password_check_total{outcome="unavailable"}` | 09.3.1-T2 |
| UC-4 | Unverified account calls a protected route | 403 `email_not_verified` (signed `RequireVerifiedEmailFilter`), asserted by a hardening test | 09.3.1-T2 |
| UC-5 | Any credential event (suspend, role change, MFA enrol/reset/disable, recovery-code use, password change/reset, force reset, revoke-all) | One transaction: new `security_stamp`, one `UserSecurityChanged` outbox row, the user's SignalR connections aborted | 09.3.2-T2 |
| UC-6 | A JWT whose `security_stamp`/`sid` no longer matches | 401 `session_revoked` within ≤ 30 s; the client refreshes or fails; the cache is evicted by the outbox event and cleared when `LISTEN` reconnects | 09.3.2-T2 |
| UC-7 | 3 wrong passwords for one (account, IP/device) pair, then a 4th attempt without a token | 400 `captcha_required`; the counter is in PostgreSQL, so both Api instances see it | 09.3.3-T2 |
| UC-8 | A valid Turnstile token is presented | Verified once, the pair's counters cleared, login proceeds | 09.3.3-T2 |
| UC-9 | Token wrong, expired (300 s) or replayed (`timeout-or-duplicate`) | 400 `captcha_invalid` (one code, not an oracle); the SPA resets the widget | 09.3.3-T2 |
| UC-10 | Further failures without a valid token | Progressive delay 1, 2, 4 … ≤ 60 s as 429 `rate_limited` + `Retry-After`; window 30 min | 09.3.3-T2 |
| UC-11 | Siteverify unreachable | `/auth/login`: fail-open + audit + throttled P2 alert; `/auth/register`: 503 `captcha_unavailable` | 09.3.3-T2 |
| UC-12 | 5 wrong TOTP codes in 30 min | `mfa_failed_notice` + audit (+ a D9 security event for admin/owner) | 09.3.3-T3 |
| UC-13 | 50 wrong codes in 24 h from an untrusted device | 429 `mfa_locked_untrusted` until an e-mailed reset or 24 h; a trusted device is never delayed by the detector/ceiling and still needs TOTP | 09.3.3-T3 |
| UC-14 | 5 wrong re-auth attempts for one family | That family is revoked → 401 `session_revoked` | 09.3.3-T3 |
| UC-15 | A user opens the activity feed | Cursor page of `ActivityItemDto` (≤ 100, default 20) from the §C3.4 allow-list only; no `data`, no admin id/IP, `actorLabel = "Support team"` for support rows | 09.3.6-T2 |
| UC-16 | A user opens the episode activity tab | Same shape, `entity_type = 'episode' AND entity_id = @id AND subject_user_id = @me`; another user's episode → 404 `not_found` | 09.3.6-T2 |
| UC-17 | A user lists sessions | ≤ 20 newest active families, `isCurrent` on the calling one | 09.3.7-T1 |
| UC-18 | A user deletes one session | 204, that family revoked; another user's id → 404 `not_found` | 09.3.7-T1 |
| UC-19 | A user revokes all sessions | 204, every family **except the current one** revoked | 09.3.7-T1 |
| UC-20 | A login from an IP/device not seen in 90 days | `session_new_device` (link to `/account/security`, **no token**) + an `account_security` audit row | 09.3.7-T1 |

## 5. API and events

Routes owned by this folder (§C3.1 verbatim subset — verb, path, policy, contract, codes):

| Route | Policy | Request → Response | Codes |
|---|---|---|---|
| `POST /auth/register` (extension only) | anonymous (`anon` rate policy, + `captcha_required` gate in mode `open`) | `RegisterCommand(string Email, string Password, IReadOnlyList<ConsentAcceptance> Consents, string? InviteCode, string? UiLocale, string? CaptchaToken)` — W3 CR-9; 09.3.1-T3 owns the handler | 201 (identical body for a new and an existing e-mail), 400 `validation_failed`/`breached_password`/`captcha_required`/`captcha_invalid`, 403 `registration_closed`, 503 `captcha_unavailable` |
| `POST /auth/login`, `POST /auth/login/mfa`, `POST /auth/forgot-password`, `POST /auth/reauth` (gate only) | anonymous + `auth` policy | bodies gain an optional `captchaToken` (same field name everywhere); handlers unchanged | 400 `captcha_required` then `captcha_invalid`; 429 `rate_limited` + `Retry-After`; 429 `mfa_locked_untrusted` |
| `GET /me/activity?category=&from=&to=&cursor=&limit=` | All | → `{ items: ActivityItemDto[], nextCursor }` | 200, 400 `validation_failed` |
| `GET /episodes/{id}/activity?cursor=&limit=` | All, tenant filter | same shape filtered to `entity_type = 'episode' AND entity_id = @id AND subject_user_id = @me` | 200, 404 `not_found` |
| `GET /me/sessions` | All | → `{ items: SessionDto[] }` | 200 |
| `DELETE /me/sessions/{id}` | All | → 204 | 204, 404 `not_found` |
| `POST /me/sessions/revoke-all` | All | → 204 (keeps the current family) | 204 |
| `POST /auth/check-password` | — | **Not created** (a standalone endpoint would be a password oracle) | — [DECISION §C3 D3] |

Events: **no new event type** (§C3.3). This folder reuses `UserSecurityChanged` (stamp + family revocation, same transaction as the rotation) and `email_requested.v1` (through 09.2.6-T3's port).

Codes and i18n: new `captcha_invalid` 400, `captcha_unavailable` 503 (W3 CR-3, applied together with 12's CR-7); reused `captcha_required` 400, `breached_password` 400, `email_not_verified` 403, `validation_failed` 400, `rate_limited` 429, `mfa_locked_untrusted` 429, `session_revoked` 401, `reauth_required` 401, `registration_closed` 403, `not_found` 404. Every code has an `error.*` key and every §C3.4 action an `activity.<action>` key, in **vi and en** (09 DoD, W3-TL2).

CAPTCHA widget contract (consumed by 10.3.3-T2, §C3.6): field name `captchaToken` in the JSON body of the five routes above; the widget renders only after 400 `captcha_required` (and always on `/register` when `mode = open`); `siteKey` from build config (public); on `captcha_invalid` the SPA calls `turnstile.reset` and clears the field; on `captcha_unavailable` **`/auth/register` stays fail-closed** (retry/`RetryCountdown`, never a token-less submit) while every other route fails open (09 W3 CR-34); one token per request, ≤ 300 s old, single use; never on `/cancel-request`.

## 6. UI

No UI is implemented here and this folder adds **no `<story>-T0 — UI design` task** (all six tasks are backend). Consumers, under the owner rule that every UI feature needs an approved design first: the CAPTCHA widget on the auth forms (**10.3.3-T2**, `Depends on 09.3.3-T2`, W3 CR-14), the activity feed on `/me/activity` and the episode activity tab (**10**, W3 CR-14), the sessions list and "sign out everywhere else" on `/me/sessions*` (**10**, W3 CR-14), 10's MFA screens that read the detector/ceiling errors, and the trusted-device toggle. Each lives in `plans/features/10-web-ui/**` with its own approved design task; this folder fixes only the wire shapes (`ActivityItemDto`, `SessionDto`), the field name `captchaToken`, the codes the SPA branches on, and the copy constraints (a reachable `captcha_unavailable` retry, never a dead end).

## 7. Jobs and integrations

| Kind | Name / key | Contract |
|---|---|---|
| Job | `AuthFailureSweepJob` | hourly, `[JobPolicy(Retries = 0)]`; deletes `auth_failure_counters` older than 24 h in batches of ≤ 5,000; ids-only arguments; logs the deleted count only |
| Job | existing `TokenHealthJob` (06/07), 08's `OpsAlertSweepJob` | unchanged, not owned here |
| Outbound client | `pwnedpasswords` (W3 CR-7) | attempt 5 s / total 8 s, ≤ 1 transport retry, no retry on 4xx; `Add-Padding: true`, `User-Agent: nts-api/<version>`; only the first 5 SHA-1 hex chars leave the process; cache 10 min, ≤ 2,000 entries |
| Outbound client | `turnstile` (W3 CR-7) | attempt 5 s / total 8 s, ≤ 1 retry with the **same** `idempotency_key`; `remoteip` sent; only `error-codes` logged |
| Rate policy | `anon` 60 req/min per resolved IP, fixed window, `Retry-After` in `OnRejected` (09.3.5-T1, W3 CR-7) | applied to `POST /auth/register`, `POST /reports`, `POST /security/cancel-links/{kind}/{id}` |
| E-mail templates (W3 CR-8) | `mfa_failed_notice`: `displayName, failedCount, windowMinutes, securityUrl`; `session_new_device`: `displayName, device, ip, at, securityUrl` (no token) | all four variants per name (vi/en × txt/html) or the host fails at startup; `params` nulled once sent |
| Integration | SignalR connection abort | through 09.8.1-T1's notifier inside the stamp-rotation transaction (soft) |
| Metrics | `auth_failures_total{scope,outcome}`, `captcha_challenges_total{outcome}`, `breached_password_check_total{outcome}`, `stamp_cache_evictions_total{reason}`, `sessions_revoked_total{reason}` | names frozen; `outcome ∈ {allowed, challenged, blocked, unavailable}` |

## 8. Security and privacy

OWASP ASVS L2 for the new surfaces; each row is asserted by a test in the owning task.

- **Abuse / brute force**: per-(account, IP/device) counting in PostgreSQL only (W3-TL5) — never in memory, never cookie-only; CAPTCHA after 3; progressive delay ≤ 60 s; no account lockout; an admin/owner with MFA enrolled is never hard-locked.
- **Breached passwords**: only 5 hex chars leave the process, `Add-Padding: true`, cached prefix; the password, its hash, the prefix and the request URI are never logged; the check is inline on register/reset/change only.
- **CAPTCHA**: one code for wrong/again/expired/replayed (no oracle); the token is consumed exactly once and never reused across login and MFA steps; fail-open on outage + audit + throttled alert, except `/auth/register` which fails closed with 503 `captcha_unavailable`.
- **Session theft**: family reuse detection, per-request `security_stamp` + `sid` check with a ≤ 30 s cache, revoke-all keeps the current family, `trusted_devices` never skip TOTP.
- **Token handling**: ≥ 256-bit, stored hashed, single-use, constant-time compare, never in a log line or a URL path; a log-scan test seeds every token type and asserts zero hits in logs/traces/audit/exports.
- **Tenant isolation and PII**: `auth_failure_counters` holds peppered HMACs only; the activity projection never returns `audit_logs.data`, fills `ip`/`device` only for the caller's own `account_security` rows and uses `actorLabel = "Support team"` for `support` rows; `/episodes/{id}/activity` keeps `subject_user_id = @me` so the tenant predicate is never dropped.
- **Registration enumeration**: identical body/timing for an existing e-mail, the disposable-domain check before the lookup, constant-time invite comparison; 09.3.1-T2 must not add a second register path or a timing-differentiating error.
- **Secrets**: `Auth:CounterPepper` and `CaptchaOptions.SecretKey` (`SecretString`, purpose chain owned by 09.10.1-T2 / the `captcha` settings row) are never returned, logged, exported or traced; the SPA sees only `siteKey`.

## 9. Test plan

Harness: Part A D13 — `NightTales.Api.IntegrationTests` on Testcontainers, `NightTales.TestKit` hand-written fakes (no mocking library), `NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `Architecture.Tests` (audit-attribute rule). Every 401/403/404 case asserts DB state after the response, not just the status code (WT29).

| Task | Tests (by project) |
|---|---|
| 09.3.1-T2 | `Application.UnitTests`: `BreachedPasswordCheckerTests` (match, no-match, padding rows count 0, cache hit, fail-open); `Api.IntegrationTests`: `Register_HardeningTests` (400 `breached_password`, the `email_not_verified` gate matrix, disposable domain, identical body/timing for an existing e-mail), `PwnedPasswordsLogScanTests` (no prefix/URI in logs) |
| 09.3.2-T2 | `Application.UnitTests`: `SecurityEventWriterTests` (one `UserSecurityChanged` per rotation, stamp rotated once, rollback leaves neither); `Api.IntegrationTests`: `StampValidationTests` (stale stamp → 401 `session_revoked`, revoked family → 401, ≤ 30 s window, outbox eviction, cache cleared on `LISTEN` reconnect), `SecurityStampWriterScanTests` (no assignment outside the writer) |
| 09.3.3-T2 | `Domain.UnitTests`: `ProgressiveDelayTests` (1,2,4…60 s); `Api.IntegrationTests`: `AuthFailureCounterTests` (3 failures → `captcha_required`, success clears, 30-min window), `CaptchaGateTests` (`captcha_invalid` for wrong/again/replayed, `idempotency_key` reused on retry, single use, fail-open with audit, 503 on register), `TwoInstanceCounterTests`, `AuthFailureSweepJobTests` (only > 24 h rows, batch ≤ 5,000) |
| 09.3.3-T3 | `Api.IntegrationTests`: `MfaFailureDetectorTests` (5/30 min → `mfa_failed_notice` + audit; 50/24 h → the ceiling for untrusted devices only), `TrustedDeviceOrderTests` (the 4-case matrix: trusted during ceiling, untrusted during ceiling, trusted after a credential event, family revoked), `ReauthLimitTests` (5 wrong → family revoked → 401 with the DB row asserted) |
| 09.3.6-T2 | `Application.UnitTests`: `ActivityProjectionTests` (allow-list only, no `data`, `{count}`/`{device}` params, `actorLabel`); `Api.IntegrationTests`: `ActivityFeedTests` (cursor paging, another user's subject rows absent, episode filter + 404, plan uses `(subject_user_id, created_at DESC)` / `(entity_type, entity_id)`), `ActivityI18nTests` (every §C3.4 action has vi+en keys) |
| 09.3.7-T1 | `Api.IntegrationTests`: `SessionsTests` (≤ 20 newest, `isCurrent`, revoke one family, revoke-all keeps the current, another user's id → 404 with the DB row asserted), `NewDeviceNoticeTests` (90-day window, e-mail queued with the allow-listed params, no token, `account_security` audit row) |

Manual gate **09-W3-auth-hardening** (§C8) closes the folder: 3 wrong passwords → `captcha_required` while the owner still signs in from the trusted device; a bundled-seed password → 400 `breached_password`; a new-device login e-mails `session_new_device`; `/me/sessions` lists it, `DELETE` kills only it, revoke-all keeps the current family; after an admin suspends a user, that user's next request answers 401 `session_revoked` within 30 s.

## 10. Tasks and re-estimate

Canonical rows (§C8.1 verbatim; each task file repeats its own row in the header):

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.3.1-T2 | S (0.5) | BE | S · M2 | 09.3.1-T1, 09.3.1-T3; 09.3.3-T2 soft (`ICaptchaVerifier`) | 09.10.6-T1 soft, 10.3.2-T1 (09 W3 CR-hand-off) |
| 09.3.2-T2 | M (1.0) | BE | S · M2 | 09.3.2-T1, 09.2.4-T1; 09.8.1-T1 soft (connection abort) | 09.10.4-T2, 09.10.4-T4, 09.10.5-T1, 09.3.3-T3 soft |
| 09.3.3-T2 | S (0.5) | BE | S · M2 | 09.3.3-T1, 09.3.5-T1 (W3 CR-7); 09.10.1-T1 soft (`captcha` catalog row, W3 CR-2) | 09.3.3-T3, 09.3.1-T2 soft, 10.3.3-T2 (W3 CR-14), 13.3.4-T1 |
| 09.3.3-T3 | M (1.0) | BE | S · M2 | 09.3.2-T3, 09.10.4-T1, 09.3.3-T2, 09.2.6-T3; 09.3.2-T2 soft | — (10's MFA screens) |
| 09.3.6-T2 | S (0.5) | BE | S · M2 | 09.3.6-T1; 09.5.1-T1 soft | 09.10.5-T1 soft, 10.8.5-T1 (W3 CR-14) |
| 09.3.7-T1 | S (0.5) | BE | S · M2 | 09.3.2-T1, 09.2.6-T3 (W3 CR-8) | 09.10.5-T1 soft, 10.8.5-T1 (W3 CR-14) |

Re-estimate: **no delta** — this folder is exactly the 3.5 raw days §C8/§C9 record for it, and §C9 proposes no re-estimate CR (`00-overview.md` §4.1's 09 S row stays 18.5 / 22.2). Per task: 09.3.1-T2 S (narrowed by F01, but the breach client + bundled seed + hardening tests fill the half day); 09.3.2-T2 M (one writer, cache, `LISTEN` eviction, the credential-event matrix); 09.3.3-T2 S (new counter table and Turnstile client, but the rate policies are 09.3.5-T1's); 09.3.3-T3 M (detector + trusted devices + re-auth limit + the order matrix); 09.3.6-T2 S (projection + allow-list + cursor, the index already exists); 09.3.7-T1 S (three routes + the new-device e-mail). The 20 % buffer absorbs any CAPTCHA/counter growth.

Task files: `09.3.1-T2-breached-password-and-registration-hardening.md`, `09.3.2-T2-security-stamp-and-event-writer.md`, `09.3.3-T2-auth-failure-counters-and-captcha.md`, `09.3.3-T3-mfa-failure-detector-trusted-devices.md`, `09.3.6-T2-activity-feed-projection.md`, `09.3.7-T1-sessions-management.md`.

## 11. Rollout

- **Phase and flags**: all six tasks are `S · M2`, none gated by a feature flag, no M3/R2 item in this slice (§C8). `Registration:Mode` (09.3.1-T3, extended by 09.10.6-T1) and the `captcha` integration row (W3 CR-2, 09.10.1-T1) are configuration.
- **Order**: migration first (W3 CR-1 + CR-1b/1c/1d in the wave-3 migration batch); then 09.3.3-T2 → 09.3.3-T3 → 09.3.1-T2 (soft `ICaptchaVerifier`); 09.3.2-T2 before the governance/admin consumers (09.10.4-T2/T4, 09.10.5-T1); 09.3.6-T2 and 09.3.7-T1 before 09.10.5-T1's detail bundle and before 10's security screens (W3 CR-14).
- **Enablement**: `CaptchaOptions.Enabled` and `PwnedPasswordsOptions.FailOpen` come from configuration with the defaults above; counters and detector are active as soon as the migration lands — no backfill, both tables start empty and counters are ephemeral (≤ 24 h).
- **Monitoring**: `auth_failures_total{scope,outcome}`, `captcha_challenges_total{outcome}`, `breached_password_check_total{outcome}` (an `unavailable` spike means HIBP/Turnstile is down and the fail-open path is live), `stamp_cache_evictions_total{reason}`, `sessions_revoked_total{reason}`; the 09-W3-auth-hardening gate is walked on the local stack before the DoR verdict.
- **Degradation**: an HIBP/Turnstile outage must not fail `/health/ready` (health checks read caches only) and never dead-ends a user — the fail-open path keeps the progressive delay and `/auth/register` answers 503 `captcha_unavailable` instead of a false "invalid".
- **Rollback**: the migration is revertible (one new table + three indexes, no destructive change); disabling `CaptchaOptions.Enabled` removes the challenge while keeping the delay; the stamp cache and the counters expire on their own.
