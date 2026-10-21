# 09.3.1, 09.3.2, 09.3.3, 09.10.4 — Auth: identity, sessions, MFA, password reset (P)

> Parent: ../README.md (Part B)
> Status: task **09.3.1-T1 implemented 2026-10-01** (PR #59); task **09.3.2-T1 implemented 2026-10-01** (branch `feature/09.3.2-T1-login`; `ITokenIssuer`/`IRefreshTokenService` declared in `Infrastructure/Security` behind the Application port `IAuthSessionService`, same precedent as `ICredentialVerifier`). SEC-01 (placeholder breached list) open for the owner. Other tasks in this folder: not implemented.

## 1. Scope

This folder details the wave-2 **P** part of epic 09.3 (auth) plus **09.10.4-T1** (BD4). The phase-S
register/verify task (09.3.1-T3) is deleted with phase S (DEC 10.14):

| Task | In scope | Out of scope (owner) |
|---|---|---|
| 09.3.1-T1 | `AddIdentityCore<AppUser>` wiring, `users`/role mapping (D8, D9), password policy + breached-password check, `Registration:Mode`, `POST /auth/register` mode gate (403 `registration_closed`), `user_consents` + `IConsentWriter` (W2 CR-5) | the register/verify flow itself and invite tokens (phase S, dropped by DEC 10.14) |
| (deleted) | the phase-S register/verify/CAPTCHA tasks are deleted by DEC 10.14; no row here | — |
| 09.3.2-T1 | `ITokenIssuer` (ES256), `refresh_tokens` + `IRefreshTokenService`, `POST /auth/login` (password step), `POST /auth/refresh`, `POST /auth/logout`, `SessionLimits` | per-request security-stamp check and CAPTCHA (phase S, dropped by DEC 10.14) |
| 09.3.2-T3 | `auth_challenges`, pre-auth cookie, `POST /auth/login/mfa` | TOTP maths, enrolment endpoints (09.10.4-T1) |
| 09.3.3-T1 | `user_action_tokens`, `POST /auth/forgot-password`, `POST /auth/reset-password`, revoke-all-families on reset | progressive delay, CAPTCHA, `captcha_required` (phase S, dropped by DEC 10.14) |
| 09.10.4-T1 | `ITotpVerifier`, `ITotpSecretStore`, recovery codes, `/me/mfa/*`, `POST /auth/reauth`, `IReauthGuard`, `ISecurityAlerter` port, role/session limit values | user management (the admin surface was phase S, dropped by DEC 10.14), owner-action queue, trusted devices |

Parent contract: `../README.md` Part B (W1–W17, WF01–WF16, BD1–BD16, §B8.1) and Part A D3–D17 (pipeline order,
UnitOfWork, idempotency, errors, persistence, tenant filter, outbox, jobs, test harness, API conventions, ETags).

## 2. Confirmed facts

- ✅ W1: `Microsoft.AspNetCore.Authentication.JwtBearer` + `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` **10.0.12**, MIT, net10.0.
- ✅ W6: Identity's `VerifyTwoFactorTokenAsync`/recovery codes are **replayable inside the window** and W7: the authenticator key and recovery codes are plain `user_tokens` values — hence BD4 (own `ITotpVerifier`, Data-Protection secret, SHA-256 recovery codes, `users.last_totp_step` CAS).
- ✅ W12 (Part A N14): Hangfire polls every 15 s → no auth work is put on a scheduled job; all side effects are outbox-driven (D11).
- ✅ W17 / W2-O1: P has **one seeded owner and no public sign-up**; register + verify were the phase-S 09.3.1-T3, deleted with phase S (WF05, DEC 10.14).
- ✅ BD2: `AddIdentityCore<AppUser>()` + roles + EF stores; `PasswordOptions.RequiredLength = 12`, no composition rules; `LockoutOptions.AllowedForNewUsers = false`; `User.RequireUniqueEmail = true`; `Registration:Mode ∈ closed | invite | open` (P: `closed`).
- ✅ BD3: access token ES256 (PEM secret `nts/<env>/api/JWT_SIGNING_KEY`, `kid` = first 16 hex of SHA-256 of the public key), `iss = "nts-<env>"`, `aud = "nts-api"`, claims `sub`, `role`, `sid`, `amr` (`pwd`/`otp`/`rc`), `iat`, `exp`; lifetime user 15 min, admin/owner 5 min, `ClockSkew = 30 s`; refresh token 32 random bytes base64url, cookie `__Secure-nts_rt`, rotation + reuse detection, owner/admin 12 h absolute / 30 min idle, user 14 days; `user_action_tokens` kinds `password_reset` (60 min); the `email_verify` kind (24 h) went with the phase-S verify task (DEC 10.14); fragment links; `IReauthGuard.RequireRecentAsync`.
- ✅ BD4: RFC 6238 SHA-1/6 digits/30 s/±1 step, `FixedTimeEquals`, `UPDATE users SET last_totp_step = @step WHERE … (last_totp_step IS NULL OR last_totp_step < @step)` → 0 rows = replay; secret Data-Protection purpose `identity.totp.v1` in `user_tokens` name `totp_secret`; 10 × 10-char Crockford base32 recovery codes as SHA-256 in `user_tokens` name `recovery_codes`, single use; `POST /auth/login` for owner/admin always `mfa_required` + `{ mfaToken }` + `__Secure-nts_mfa` (5 min); `auth_challenges`; 5 wrong codes revoke the challenge; IP change audited, not rejected; unknown e-mail runs the dummy hash; `POST /auth/reauth { password, code }` sets `reauthenticated_at`; 5 wrong re-auth attempts per family revoke it (401 `session_revoked`); enrolment-only token (`scope = mfa_enrol`) until an owner has MFA, every other route 404. [RULED — README §B10 WT29, WT31, WT34, WT36, WT37] Auth commands return **success-typed outcomes** (`LoginOutcome`, `RefreshOutcome.ReuseDetected`, `MfaOutcome.WrongCode`, `ReauthOutcome.Failed`) so counters, revocations, audit rows and alert e-mails commit before the endpoint maps them to 401 (WT29); `ITotpVerifier.VerifyAsync(userId, code, TotpSlot slot, ct)` with `Pending` used only by `VerifyMfaCommand` and `Active` by login and reauth (WT31); the fallback authorization policy rejects `scope = mfa_enrol` and the two enrolment routes use policy `MfaEnrolment` (WT34); reuse is detected only when the presented row is `revoked_reason = 'rotated'`, any other revoked row answers 401 `session_revoked` with no e-mail (WT36); every family revocation emits `NOTIFY security` (user id) and the hub aborts that user's connections, `Hubs:MaxConnectionMinutes` = the role's access-token lifetime (owner 5, user 15), no stamp cache in P (WT37).
- ✅ D8: snake_case naming, Identity tables renamed `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `user_tokens`, `role_claims`; `timestamptz`; enum-as-text + `HasEnumCheck`; ids UUIDv7 from `Guid.CreateVersion7()`; aggregates map `Version` → `xmin`.
- ✅ D9 + W2 CR-5: 09.2.2 `InitialCore` does **not** create `refresh_tokens`, `auth_challenges`, `user_action_tokens`, `user_consents`; each is created by its owner's migration (this folder).
- ✅ D10: tenant filter `"tenant"`; other users' rows answer **404 `not_found`**.
- ✅ D13 harness: `ApiFactory`, `TestAuthHandler` (`X-Test-User`/`X-Test-Role`), `WorkerHost` + `InlineJobScheduler`, `FakeClock`, real PostgreSQL 17 via `PostgresFixture` on `NTS_TEST_PG` (09 W3 CR-35; migrated template DB); **no mocking library** — hand-written fakes.
- ✅ D14: base `/api/v1`, camelCase JSON, enums snake_case, cursor pagination; D7 error catalogue (`Api/Http/ErrorCatalogue.cs`), `ToHttpResult`/`ToNoContentResult` helpers.
- ✅ 09 §5.4 codes used here: `validation_failed` 400, `breached_password` 400, `email_not_verified` 403, `invalid_credentials` 401, `mfa_required` 401, `invalid_mfa_code`/`mfa_token_invalid` 401, `session_revoked` 401, `refresh_token_reused` 401, `reauth_required` 401, `not_found` 404, `forbidden` 403, `registration_closed` 403, `rate_limited` 429.
- ⚠️ [INFERENCE] names not present in Part A/BD text and therefore listed in §10 "Open items": `users` no; but `ICurrentUser.SessionId`, `IBreachedPasswordChecker`, `IUserActionTokenService`, `UserActionKind`, `TotpSlot`, `IRecoveryCodeService`, `ISecurityAlerter`, metric names, e-mail template names, `user_tokens` names `totp_secret_pending`; the interim `IMfaChallengeService`/`PendingMfaChallengeService` names no longer exist (WT39). [RULED — README §B10 WT31, WT39]
- ⚠️ BD4 mentions `ISecurityAlerter` and the D9 security-event pipeline (11.6.2, 09.10.4 S) — P only writes the audit row and calls the port; e-mail fan-out to owners is S.

## 3. Domain and data

All four tables are **expand-only** migrations named `<yyyyMMddHHmm>_09_<What>` (D9). PostgreSQL DDL is given per owner task.

| Table | Migration owner | DDL highlights |
|---|---|---|
| `user_consents` | 09.3.1-T1 | `id uuid pk`, `user_id uuid not null references users(id)`, `document text not null` (enum-as-text + CHECK `tos`/`privacy`), `version text not null`, `accepted_at timestamptz not null`, `ip inet null`; index `(user_id, document)`. W2 CR-5 owner. |
| `refresh_tokens` | 09.3.2-T1 | per BD3 + W2 CR-1 columns, plus `reauth_attempts smallint NOT NULL DEFAULT 0` and `reauth_last_attempt_at timestamptz` for BD4's 5-wrong-attempts rule ([INFERENCE], see Open items); unique `(token_hash)`, index `(family_id)`, index `(user_id)` , partial index `(expires_at) where revoked_at is null`, CHECK `reauth_attempts between 0 and 5`. |
| `auth_challenges` | 09.3.2-T3 | `id`, `user_id`, `token_hash` unique, `preauth_cookie_hash`, `user_agent text null`, `ip inet null` (logged, never enforced), `attempts smallint not null default 0`, `expires_at`, `used_at`; index `(user_id, expires_at)`. |
| `user_action_tokens` | 09.3.3-T1 | `id`, `user_id`, `kind text` + CHECK `password_reset` only (the `email_verify` kind is deleted with phase S, DEC 10.14), `token_hash` unique, `created_at`, `expires_at`, `used_at`; index `(user_id, kind)`. |

No new columns on `users`: `two_factor_enabled`, `last_totp_step`, `security_stamp`, `status`, `role`, `ui_locale`,
`last_login_at` all come from `InitialCore` (00 §6, D9). 09.10.4-T1 adds **no** migration — TOTP state lives in
`users.last_totp_step` and Identity's `user_tokens`. `trusted_devices` (D9, 09.3.x) is **not** created in P: no P task
references a trusted device (BD4 makes an IP change audited-only); see §10 Open items.

Domain types are added by these tasks:
- `PlatformRole { User, Admin, Owner }` (D16) — used by `AppUser.Role`.
- `UserActionKind { PasswordReset }` (the `EmailVerify` member is deleted with phase S, DEC 10.14) (09.3.3-T1).
- `SessionLimits(TimeSpan Absolute, TimeSpan? Idle)` + `SessionLimits.For(PlatformRole)` (09.3.2-T1) — the values BD3/09 §4 fix: owner/admin 12 h / 30 min, user 14 d / none.
- `LegalDocument { Tos, Privacy }` (09.3.1-T1) [INFERENCE name].

## 4. Use cases

1. **Owner signs in (P happy path).** `POST /auth/login` with password → 401 `mfa_required` + `{ mfaToken }` + pre-auth cookie (owner/admin always, BD4) → `POST /auth/login/mfa` with the TOTP code → 200 access token (5 min for owner) + `__Secure-nts_rt` cookie. Until the owner has enrolled, `/auth/login` answers 401 `mfa_enrolment_required` with an enrolment-only token; `/me/mfa/setup` + `/me/mfa/verify` enrol; every other route answers 404 (BD4).
2. **Silent refresh.** SPA calls `POST /auth/refresh` with the cookie and an `Origin` header → the presented token is rotated (old row `revoked_at = now`, `replaced_by_id` = new row), a new access token is returned; reuse is detected **only** when the presented row has `revoked_reason = 'rotated'` → it revokes the family → 401 `refresh_token_reused` + audit `account_security` + owner e-mail, while any other revoked row answers 401 `session_revoked` with no e-mail (BD3; WT36), and the family revocation emits `NOTIFY security` so the hub aborts the user's live sockets (WT37). [RULED — README §B10 WT36, WT37]
3. **Idle/absolute expiry.** A refresh after 30 min of owner inactivity, or after the family's 12 h absolute deadline, revokes the family → 401 `session_revoked`, and emits `NOTIFY security` (WT37); the user signs in again. [RULED — README §B10 WT37]
4. **Sensitive action needs re-auth.** A handler calls `IReauthGuard.RequireRecentAsync(5 min)`; the guard reads `refresh_tokens.reauthenticated_at` of the caller's `sid` family (live, not the JWT) → 401 `reauth_required`; `POST /auth/reauth` sets it. 5 wrong attempts revoke the family.
5. **Forgot password (no enumeration).** `POST /auth/forgot-password` always answers 202 with the same body and timing budget; an existing user gets a link `/reset-password#t=<token>` (fragment) valid 60 min, single use; a reset revokes **all** families, rotates the stamp and e-mails the user.
6. **(S) Visitor registers.** `POST /auth/register` (mode `open`/`invite`) → 202 identical for a new and an existing e-mail; new → user `active`, `email_confirmed = false`, consent rows, verification e-mail; existing → "someone tried to register" e-mail. `POST /auth/verify-email` confirms; unverified users get 403 `email_not_verified` on paid routes.

## 5. API and events

All routes are Minimal-API endpoint groups (`IEndpointGroup`, D14) under `/api/v1`, JSON camelCase, cookie
`__Secure-nts_rt` (`HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`) and pre-auth cookie `__Secure-nts_mfa`
(same flags, 5 min). Every cookie-authenticated POST verifies `Origin` = `App:PublicOrigin` (BD3/BD16) → else 403 `forbidden`.

| Route | Owner | Success | Errors |
|---|---|---|---|
| `POST /auth/register` | 09.3.1-T1 (gate; the S flow is dropped by DEC 10.14) | 202 `{ }` | 403 `registration_closed`, 400 `validation_failed`, 400 `breached_password` |
| `POST /auth/login` | 09.3.2-T1 | 200 `{ accessToken, expiresAt, role, amr }` + `__Secure-nts_rt` | 400 `validation_failed`, 401 `invalid_credentials`, 403 `email_not_verified`, 401 `session_revoked`, 401 `mfa_required`, 401 `mfa_enrolment_required` [INFERENCE code] |
| `POST /auth/login/mfa` | 09.3.2-T3 | 200 tokens + cookie | 401 `invalid_mfa_code`, 401 `mfa_token_invalid`, 400 `validation_failed` |
| `POST /auth/refresh` | 09.3.2-T1 | 200 `{ accessToken, expiresAt }` + rotated cookie | 401 `refresh_token_reused`, 401 `session_revoked`, 403 `forbidden` (Origin) |
| `POST /auth/logout` | 09.3.2-T1 | 204 + cleared cookie | 401 `session_revoked`, 403 `forbidden` |
| `POST /auth/forgot-password` | 09.3.3-T1 | 202 `{ }` (always) | 400 `validation_failed`, 429 `rate_limited` |
| `POST /auth/reset-password` | 09.3.3-T1 | 204 | 400 `validation_failed`, 400 `breached_password` |
| `POST /auth/reauth` | 09.10.4-T1 | 200 `{ reauthenticatedAt }` | 401 `invalid_credentials`, 401 `invalid_mfa_code`, 401 `session_revoked`, 401 `reauth_required` |
| `POST /me/mfa/setup` | 09.10.4-T1 | 200 `{ secret, otpauthUri }` | 401 `reauth_required`, 409 `concurrency_conflict` |
| `POST /me/mfa/verify` | 09.10.4-T1 | 200 `{ recoveryCodes[10] }` | 401 `invalid_mfa_code`, 400 `validation_failed` |
| `POST /me/mfa/recovery-codes` | 09.10.4-T1 | 200 `{ recoveryCodes[10] }` | 401 `reauth_required` |
| `DELETE /me/mfa` | 09.10.4-T1 | 204 | 403 `forbidden` (admin/owner), 401 `reauth_required` |

Events (D11 outbox, ids/enums only): `UserSecurityChanged` → `user_security_changed.v1` (written in the same
transaction whenever `security_stamp` rotates; its phase-S consumer is dropped by DEC 10.14); `EmailRequested` → `email_requested.v1`
is **not** written here — tasks call `IEmailSender.QueueAsync` (09.2.6-T3, BD1/BD9). Protected actions are plain
`ICommand<ErrorOr<…>>` through the D4 pipeline (`LoggingBehavior` → `AuthorizationBehavior` → `ValidationBehavior` →
`UnitOfWorkBehavior` → `IdempotencyBehavior` → `AuditBehavior`); token/consent writes join the handler's transaction
(D5), so a failed login writes no audit row but a failed *revocation* does not either.

Metrics (09.9.3-T1 exporter, [INFERENCE names]): `auth_login_total{result}`, `auth_refresh_total{result}`,
`auth_mfa_total{result}`, `auth_reauth_failures_total`, `auth_action_token_total{kind}`, `auth_session_revoked_total`.

Config keys and defaults (all [INFERENCE names], secrets via the secret manager):

| Key | Default | Used by |
|---|---|---|
| `Registration:Mode` | `closed` | 09.3.1-T1 |
| `Registration:DisposableDomains` | empty list (P) | — (the check was a phase-S task, dropped by DEC 10.14) |
| `Auth:Access:UserMinutes` / `Auth:Access:AdminMinutes` | `15` / `5` | 09.3.2-T1 |
| `Auth:Access:ClockSkewSeconds` | `30` | 09.3.2-T1 |
| `Auth:Session:AdminAbsoluteHours` / `Auth:Session:AdminIdleMinutes` | `12` / `30` | 09.3.2-T1 (values fixed by BD3) |
| `Auth:Session:UserAbsoluteDays` | `14` | 09.3.2-T1 |
| `Auth:Refresh:CookieName` | `__Secure-nts_rt` | 09.3.2-T1 |
| `Auth:Mfa:ChallengeMinutes` / `Auth:Mfa:MaxAttempts` | `5` / `5` | 09.3.2-T3 |
| `Auth:Reauth:WindowMinutes` / `Auth:Reauth:MaxAttempts` | `5` / `5` | 09.10.4-T1 |
| `Auth:ActionToken:PasswordResetMinutes` | `60` (the former `:EmailVerifyHours` = 24 is deleted with phase S, DEC 10.14) | 09.3.3-T1 |
| `Security:BreachedPasswords:Mode` | `list` (`range` \| `list` \| `off`) | 09.3.1-T1 |
| `App:PublicOrigin` | — (required) | Origin check, reset/verify links |

## 6. UI

Consumed by plan 10 (not implemented here): login screen (password step → MFA step with the pre-auth cookie),
the owner/admin placement/enrolment page shown when the API answers `mfa_enrolment_required`, the reset page at
`/reset-password#t=…` (the SPA reads the token from the fragment and POSTs it), the verify-email page, and the
re-auth modal for sensitive actions. `/admin/*` shows 404 until the acting owner/admin has MFA (BD4).

## 7. Jobs and integrations

- No Hangfire job is introduced here (W12): refreshes are request-driven and all side effects go through the outbox (D11).
- E-mail is queued through `IEmailSender.QueueAsync(EmailMessage) → ErrorOr<Guid>` (09.2.6-T3, BD9) — never inline, never
  SMTP; `EmailMessage(Guid UserId, string Template, Language Locale, IReadOnlyDictionary<string, string> Params)`
  (`09.2.6-T3-email-pipeline.md:21-24`), so params are a string dictionary and the locale is the `Language` enum (D16,
  converted from `users.ui_locale`); `email_messages.params` is emptied once the row reaches `sent`.
  **Registered in `EmailTemplateCatalog` (verified at `09.2.6-T3-email-pipeline.md:78-88`):** `password_reset`
  (`displayName`, `link`, `expiresMinutes`), `refresh_token_reuse` (`displayName`, `occurredAt`, `device`, `sessionsLink` —
  BD3's reuse-detection warning) and `password_changed` (`displayName`, `occurredAt`), all token-free so nothing sensitive is
  stored. `email_verify` / `email_verify_duplicate` are deleted with phase S (DEC 10.14).
  An unknown template is `Error.Validation("email_template_unknown")`, a param key outside the allow-list
  `Error.Validation("email_template_keys")`, and all four file variants (vi/en × txt/html) must exist or that host
  refuses to start.
  **Deleted with phase S (DEC 10.14):** the "recovery code used" notice and the owner fan-out templates
  (`security_mfa_enrolled`, `security_mfa_disabled`, `security_reauth_failed` [INFERENCE names]) are not registered at all.
  Addresses are never logged; only `to_hash` exists (BD9).
- Integrations: the TOTP secret is protected with `IDataProtectionProvider.CreateProtector("identity.totp.v1")` **directly, not through `ITokenProtector`** (WT43), over the Data Protection key ring (09.9.4-T1); `IAuditWriter`
  (09.3.6-T1) for auth events; `ISecurityAlerter` (port declared here, e-mail/SIEM fan-out is S). [RULED — README §B10 WT43]

## 8. Security and privacy

- **Least privilege**: `/me/mfa/*` and `/auth/reauth` require an authenticated user; `IReauthGuard` gates re-enrolment,
  recovery-code regeneration and (in later plans) channel connect and admin settings.
- **Secrets/tokens**: JWT signing key, TOTP secrets and recovery codes are never logged and never returned twice;
  TOTP secrets are Data-Protection-protected (purpose `identity.totp.v1`); recovery codes and all action/refresh tokens
  are stored hashed; comparisons use `CryptographicOperations.FixedTimeEquals`; logging redaction is 09.9.3-T2's job (BD16).
- **Enumeration + timing**: register, forgot-password and login (unknown e-mail) run a fixed dummy-hash path and return
  the same status/body within the same budget.
- **Cookies/CSRF**: `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`, short `Max-Age`; `Origin` check on every
  cookie-authenticated POST (BD3, BD16); reset/verify tokens travel in the URL **fragment** only.
- **Tenant/404**: every query goes through the D10 tenant filter; another user's data answers 404 `not_found`.
- **Audit**: `account_security` rows (login success/failure, MFA enrol/disable, recovery-code use, re-auth failure,
  refresh reuse, session revoked, password reset) written in the same transaction (BD15); `ip`/`user_agent` only on
  security events; `data` never holds tokens, e-mails or story text.
- **Rollback/replay**: refresh rotation is atomic (single transaction, `SELECT … FOR UPDATE` on the presented row);
  `last_totp_step` CAS prevents TOTP replay; action tokens are claimed by one `UPDATE … WHERE used_at IS NULL … RETURNING`.

## 9. Test plan

Harness (D13): `ApiFactory`, `TestAuthHandler`, `WorkerHost` + `InlineJobScheduler`, `FakeClock`, real PostgreSQL 17
via `PostgresFixture` on `NTS_TEST_PG` (09 W3 CR-35); hand-written fakes only (no mocking library). Integration classes (namespace `NightTales.Api.Tests.Features.Auth`):

- `IdentitySetupTests`: `Login_DummyHashTiming_IsWithinBudget`, `PasswordPolicy_TwelveChars_NoCompositionRules`,
  `Register_ClosedMode_ReturnsRegistrationClosed`.
- `RefreshTokenTests`: `Login_OwnerWithoutMfa_ReturnsMfaRequired`, `Refresh_RotatesToken_AndRetiresOldRow`,
  `Refresh_ReusedToken_RevokesFamily_Returns401`, `Refresh_AdminIdleExpired_RevokesFamily`,
  `Refresh_AdminAbsoluteTwelveHours_CannotBeExceeded`, `Logout_RevokesFamily_AndClearsCookie`,
  `Refresh_WithoutMatchingOrigin_ReturnsForbidden`, `Refresh_TwoConcurrentRequests_OneWins_NoFamilySplit`.
- `MfaLoginTests`: `LoginMfa_WrongCode_FiveTimes_RevokesChallenge`, `LoginMfa_IpChanged_IsAuditedNotRejected`,
  `LoginMfa_RecoveryCode_RedeemsOnce_AndRevokesOtherFamilies`, `LoginMfa_ChallengeReused_ReturnsMfaTokenInvalid`.
- `TotpTests`: `Totp_SameCodeTwice_SecondIsReplay`, `Totp_WindowPlusMinusOne_Accepts`, `RecoveryCodes_Regenerate_ReplacesAll`,
  `MfaVerify_Activates_AndRotatesStamp`, `MfaDelete_AdminForbidden`, `Reauth_FiveWrongAttempts_RevokesFamily`,
  `ReauthGuard_WithoutRecentReauth_ReturnsReauthRequired`.
- Ruled additions [RULED — README §B10 WT29, WT34, WT35]: every 401 test above also asserts the **committed DB state** after the response (reauth counters, family revocation, audit rows), because commands return success-typed outcomes (WT29); `EnrolmentTokenScopeTests` walks every route with an `mfa_enrol` token and expects 404 everywhere except `/me/mfa/setup` and `/me/mfa/verify` (WT34); `TotpGuessRateLimitTests` posts >= 10 wrong codes per user across challenges and reauth within 15 min and expects 429 `rate_limited` with `Retry-After: 900` on `/auth/login` and `/auth/login/mfa` plus an `account_security` audit row (WT35).
- `PasswordResetTests`: `ForgotPassword_UnknownEmail_SameResponseAndTiming`, `ResetToken_UsedTwice_SecondIsValidationFailed`,
  `ResetToken_ExpiredAfterSixtyMinutes_IsRejected`, `Reset_Password_RevokesAllFamilies_AndRotatesStamp`,
  `Reset_WithBreachedPassword_Returns400` (S-phase extra): `Register_DuplicateEmail_RespondsIdentically`,
  `VerifyEmail_TokenIsSingleUse_AndTwentyFourHours`.
- Unit: `Rfc6238TotpVerifierTests` (published RFC 6238 vectors, ±1 window), `SessionLimitsTests` (role → 12 h/30 min vs 14 d),
  `BreachedPasswordCheckerTests` (only the first 5 hex chars leave the server), `CrockfordBase32Tests`.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.3.1-T1 | Identity setup, password policy and consents | BE | S (0.5) | 09.2.2-T2 | 09.2.5-T2, 09.3.2-T1, 13.2.1-T1 (13 CR-5) |
| 09.3.2-T1 | Login, refresh-token families and logout | BE | M (1.0) | 09.3.1-T1 | 09.3.2-T3, 09.10.4-T1, 09.3.3-T1, 09.3.4-T1, 09.3.5-T1, 09.8.1-T1, 10.2.1-T1 (10 CR-8), 06.1.2-T1 (06 CR-8), 07.1.2-T1 |
| 09.10.4-T1 | TOTP MFA, recovery codes and re-auth | BE | M (1.0) | 09.3.2-T1, 09.9.4-T1 | 09.3.2-T3, 09.2.6-T2, 10.3.3-T3 (10 CR-8), 06.1.2-T1 (06 CR-8) — the former admin consumers 10.9.4-T2 and the plan-12 row 12.5.1-T1 are deleted by DEC 10.14 |
| 09.3.2-T3 | MFA login step | BE | M (1.0) | 09.3.2-T1, 09.10.4-T1 | 10.3.3-T3 (10 CR-8) |
| 09.3.3-T1 | Forgot and reset password | BE | S (0.5) | 09.3.2-T1, 09.2.6-T3 | 10.3.3-T1 (10 CR-8), 06.2.2-T2 (06 CR-8), 10.3.3-T0 |

Folder total: **4.0 d raw** — the five rows above: 09.3.1-T1 0.5, 09.3.2-T1 1.0, 09.10.4-T1 1.0, 09.3.2-T3 1.0, 09.3.3-T1 0.5. The phase-S row deleted by DEC 10.14 is out of the total. Wave-2 P total 21.5 d, plan 09 56.0 d raw (B9).

### Open items

- [RESOLVED 2026-10-01, 09.3.2-T1] **Family id at request time:** `ICurrentUser` gained `Guid? SessionId` (from the `sid` claim; `HttpCurrentUser`, `ScopedCurrentUser`, `TestCurrentUser`) so BD3's `IReauthGuard` can read the caller's family live.
- [OPEN] **No catalogue code** for a missing/expired/unknown refresh cookie or an `email_confirmed = false` owner/admin login: this plan maps both to existing codes (`session_revoked`; `mfa_enrolment_required` is a new code absent from 09 §5.4) [INFERENCE] — the TL should rule whether to extend the catalogue.
- [RESOLVED 2026-09-24] **MFA challenge ownership:** 09.3.2-T1 ships password login for users **without** MFA and 09.3.2-T3 edits that same login handler to create the challenge for MFA users (as 03.4.2-T1 edits `PollRenderJob`); the interim `PendingMfaChallengeService` is removed. [RULED — README §B10 WT22, WT39]
- [RESOLVED 2026-09-24] **Template names** — the templates this wave needs are in `EmailTemplateCatalog` (`09.2.6-T3-email-pipeline.md:78-88`): `password_reset`, `refresh_token_reuse` and `password_changed`, verified in that file. Params are token-free, unknown template → `email_template_unknown`, unknown key → `email_template_keys`, and all four file variants (vi/en × txt/html) must exist or that host fails at startup. `email_verify`, `email_verify_duplicate` and the MFA notices (`security_mfa_enrolled`, `security_mfa_disabled`, `security_reauth_failed`, "recovery code used") are deleted with phase S by DEC 10.14 and are not registered at all.
- [OPEN] **`ISecurityAlerter`** (BD4/09 §4 09.10.4): P declares the port and writes the audit row; the owner e-mail fan-out and SIEM delivery (11.6.2) were phase S, dropped by DEC 10.14 — confirm this split is acceptable at the 09-W2-auth gate.
- [RESOLVED 2026-09-24] **`refresh_tokens.reauth_attempts` / `reauth_last_attempt_at`:** BD4 requires "5 wrong re-auth attempts per family revoke it" but W2 CR-1's `refresh_tokens` column list has no counter, so 09.3.2-T1 creates the two columns in `_09_RefreshTokens` and 09.10.4-T1 reads/increments them [INFERENCE] — increment is `WHERE family_id = @f AND revoked_at IS NULL` and rotation copies both values to the successor row. [RULED — README §B10 WT32]
- [OPEN] **Registration mode source + enum:** BD2 (README.md:663) binds `Registration:Mode ∈ closed | invite | open` as config, but 09 §4 story 09.10.6 (`plans/09-backend-api.md:287`) makes registration mode an owner-editable non-secret setting `registration.mode` with only `open`/`closed` (read through the phase-S settings store, dropped by DEC 10.14). P reads the config value behind an `IRegistrationPolicyProvider` seam; the TL only needs to rule whether `invite` stays in the enum.
- [RESOLVED by DEC 10.14] **`trial.credits` (same story, `plans/09-backend-api.md:287`) is deleted:** there is no credit system (plan 12 deleted), so a registration writes no grant, and `registration.disposable_domains` is gone with its phase-S check. The ledger stays 02.2.2's metering-only port (W13, `ICreditLedger`) — it records OpenAI spend, never credits.
- [RESOLVED by DEC 10.14] **`trusted_devices`** is not created: the table was phase S, as was the `mfa_locked_untrusted` brute-force work that used it.
- [INFERENCE] `user_tokens` name `totp_secret_pending` for an unverified enrolment secret; `LegalDocument { Tos, Privacy }`; `UserActionKind { PasswordReset }` (the `EmailVerify` member is deleted with phase S, DEC 10.14); the config keys, metric names and cookie/spin-off names in §5.

## 11. Rollout

- **P order (must hold):** 09.3.1-T1 → 09.3.2-T1 → 09.10.4-T1 → 09.3.2-T3 → 09.3.3-T1 (B5, B8.1). The owner can log in
  with password + TOTP only after 09.10.4-T1 and 09.3.2-T3 have landed; 09.3.2-T1 ships password login for users **without** MFA and 09.3.2-T3 edits that same handler to create the challenge for MFA users, so there is no interim `PendingMfaChallengeService`/DI shim ([RULED — README §B10 WT22, WT39]); before T3 lands, owner/admin login answers 401 `mfa_required`/`mfa_enrolment_required` with no challenge.
- **Migrations:** four expand-only migrations in this order (`09_UserConsents`, `09_RefreshTokens`, `09_AuthChallenges`,
  `09_UserActionTokens`); no destructive change, so a rollback is an app-version rollback plus leaving the tables in place.
  `Registration:Mode` stays `closed` in P (W2-O1); it is the **P-only default** — the owner-editable setting
  `registration.mode` (`system_settings`, phase-S settings store and screen, dropped by DEC 10.14) would supersede it behind
  `IRegistrationPolicyProvider` once that read side lands, and the app never flips the mode itself.
- **Data Protection:** the TOTP secret and the key ring require 09.9.4-T1 (`PersistKeysToDbContext<AppDbContext>` +
  `ProtectKeysWithCertificate` with the PFX secret `DP_KEK`, WF15); on a missing/wrong KEK the app starts degraded and
  must not read empty TOTP secrets (12.8.3 alert path).
- **Gate 09-W2-auth:** owner logs in with password + TOTP, refresh rotates, reuse revokes, re-auth guards a sensitive
  route, reset by e-mail works in the local stack (B7); no phase-S task is part of this gate (DEC 10.14).
