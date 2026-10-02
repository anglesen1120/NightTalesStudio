# 10.3 — Auth and legal (P slice of 10.3.1, all of 10.3.3): owner login, MFA, enrolment, re-auth and the legal pages

> Detailed feature plan. Contract: `../README.md` §3 (D1–D18), §5, §6, §8.1, §9 (10.3 P part 1.5 → 2.5 d), §10 (O1–O4). Template: `../../00-overview.md` §10.1.
> Stories: 10.3.1-T1 (privacy/terms pages), 10.3.3-T1 (login password step, forgot, reset), 10.3.3-T3 (MFA login step, owner enrolment, recovery codes, re-auth dialog). All three are **P · M1**.
> Legend: ✅ read from a file (`path:line`, 2026-09-24) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

In scope (wave 1, M1):

| Task | Delivers |
|---|---|
| 10.3.1-T1 | Prerendered legal pages `/vi/privacy`, `/en/privacy`, `/vi/terms`, `/en/terms`; build-time Markdown → typed block list; version + effective date shown; SEO head (title/description/canonical/hreflang/og); vi + en. |
| 10.3.3-T1 | `/login` password step, `/forgot-password`, `/reset-password` (token from the URL fragment), `returnUrl` handling, 429 countdown, `SessionStore` hand-off. |
| 10.3.3-T3 | MFA step inside `/login` (TOTP **or** recovery code), owner enrolment at `/account/security/mfa-setup` (QR, manual key, verify, 10 recovery codes shown once, regenerate behind re-auth), shared `ReauthService` + `ReauthDialog` for `401 reauth_required`. |

Out of scope, named owner: register + verify-e-mail pages (`10.3.2-T1`, S, 09.3.1-T3); disabling MFA for non-owner users and the `/admin` enrolment redirect (`10.9.5-T1`, S, now XS per `../README.md:77`); landing/pricing (`10.3.1` S part); CAPTCHA widget (`captcha_required`, 09.3.3-T2/T3 S); credit/balance copy anywhere (D14); consent/re-acceptance dialog (13.2.1-T1, S); trusted devices (S).

Gates this folder feeds: `10-W1-session` (`../README.md:259`) — the owner logs in with password + TOTP through the UI, reloads (session restored), is signed out when the server revokes the family, and the re-auth dialog unblocks a `reauth_required` call.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| C1 | Access token ES256, owner/admin **5 min**, never in a URL; refresh cookie `__Secure-nts_rt` `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`, rotated each refresh; `POST /auth/refresh` requires `Origin = App:PublicOrigin`; owner/admin 12 h absolute, 30 min idle | `../README.md:54` ✅ |
| C2 | Owner/admin login: `POST /auth/login` → 401 `mfa_required { mfaToken }` + pre-auth cookie `__Secure-nts_mfa` (5 min) → `POST /auth/login/mfa { mfaToken, code \| recoveryCode }`; without TOTP → 401 `mfa_enrolment_required` + enrolment-only token (only `/me/mfa/setup`, `/me/mfa/verify` answer, everything else 404) | `../README.md:55`; `09.3-auth/plan.md:64,79-90`; `09.3-auth/09.3.2-T3-mfa-login-step.md:9-11,114-119` ✅ |
| C3 | `POST /auth/reauth { password, code }` → 200 `{ reauthenticatedAt }`; `IReauthGuard` route → 401 `reauth_required`; 5 wrong attempts revoke the family (→ 401 `session_revoked`); ≥ 10 wrong TOTP/recovery codes in 15 min → 429 `rate_limited` `Retry-After: 900` on `/auth/login` and `/auth/login/mfa` | `../README.md:56`; `09.3-auth/09.10.4-T1-totp-and-reauth.md:65,94,149` ✅ |
| C4 | Reset link `/reset-password#t=<token>`, 60 min, single use; `POST /auth/forgot-password` always 202, padded ≥ 300 ms (+ ≤ 50 ms jitter); `POST /auth/reset-password` → 204, revokes all families, rotates stamp | `../README.md:57`; `09.3-auth/09.3.3-T1-forgot-and-reset-password.md:9-11,78,92,152-153` ✅ |
| C5 | Registration closed in P: `POST /auth/register` → 403 `registration_closed`; register + verify-e-mail are S | `../README.md:58`; `09.3-auth/plan.md:79,91` ✅ |
| C6 | Enrolment contract: `POST /me/mfa/setup` → `{ secret, otpauthUri }` (`issuer=NightTales`, SHA1/6/30 s); `POST /me/mfa/verify { code }` → 200 `{ recoveryCodes[10] }`, `two_factor_enabled = true`, stamp rotated; `POST /me/mfa/recovery-codes` → 200 `{ recoveryCodes[10] }`; `DELETE /me/mfa` → 403 `forbidden` for owner/admin. **Setup and verify always require a recent re-auth unless the caller holds the enrolment-only token (`scope = mfa_enrol`, WT41)**; that token lives 15 min, has no refresh cookie and reaches only those two routes | `09.3-auth/09.10.4-T1-totp-and-reauth.md:90-105,146-147`; `09.3-auth/plan.md:87-89` ✅ |
| C7 | Error codes in play: `validation_failed` 400, `breached_password` 400, `invalid_credentials` 401, `mfa_required` 401, `invalid_mfa_code` 401, `mfa_token_invalid` 401, `session_revoked` 401, `reauth_required` 401, `forbidden` 403, `registration_closed` 403, `rate_limited` 429, `not_found` 404. `mfa_enrolment_required` is a new code, absent from 09 §5.4 [INFERENCE] | `09.3-auth/plan.md:36,80,207` ✅ |
| C8 | Login-failure state is committed (challenge `attempts`/`used_at`, reauth counters, audit rows) and the endpoint maps a success-typed outcome to 401 — so the 5th wrong MFA code makes the *next* call answer `mfa_token_invalid` | `09.3-auth/09.3.2-T3-mfa-login-step.md:73-76,116`; `09.10.4-T1-…:94,156` ✅ |
| C9 | Recovery-code login consumes the code (single use) and revokes every other family; `amr` = `rc` vs `otp` | `09.3-auth/09.3.2-T3-mfa-login-step.md:77,119` ✅ |
| C10 | Exactly one of `code`/`recoveryCode` per MFA request (validator, else 400 `validation_failed`); codes/secrets/tokens are never logged | `09.3-auth/09.3.2-T3-mfa-login-step.md:69,121` ✅ |
| C11 | Legal content is owned by 13.1.1-T1 (privacy) / 13.1.2-T1 (terms), vi + en, "published at `/privacy` with version and date"; the shown version must equal the version 13 records in `user_consents` | `plans/13-compliance.md:47-55` ✅; `../README.md:170` (D12) ✅ |
| C12 | Transports and layering: `outputMode: 'static'`, prerender only public pages; `web/src/app/{core,shared,features}`; D6 session rules (memory token, single-flight refresh, `returnUrl` regex `^/(?![/\\])[^\s]*$`); D9 `AppError`/`AppErrorMapper`/`bindFieldErrors`/`RetryCountdown` (10.2.2-T1, [WR4]); D10 Transloco scopes `auth` + `public` + `errors`; D11 shared UI; D13 typed Reactive Forms, OnPush, lazy; D15 Vitest/Playwright; D16 a11y baseline | `../README.md:109-199` ✅ |
| C13 | Cross-origin and headers: one origin, `connect-src 'self'`, `img-src 'self' data:`, `Referrer-Policy: no-referrer`, hash-based CSP, Trusted Types with `angular`, `angular#bundler`, `nts-sanitizer` (no `unsafe-bypass`) | `../README.md:124-132` ✅ |
| C14 | Legal pages must make **no API call** during prerender and render blocks without `innerHTML` (F18 → D12) | `../README.md:170`; `:91` ✅ |
| C15 | Idle gate: no timer-driven refresh; a non-user-action refresh (reconnect, poll) runs only while `isActive()`; login/reset pages never start a poll | `../README.md:148` (D7) ✅ |
| C16 | **TL rulings [WR1]–[WR16] bind this folder** (`../README.md:344-359`): [WR3] the Playwright suite runs in 11.5.1-T1's `e2e.yml`, not in `web.yml`; [WR4] owners `RetryCountdown`/`bindFieldErrors`/`AppErrorMapper` → 10.2.2-T1 and `CopyableCode`/`PageHeader`/`EmptyState`/`ErrorState`/`LoadingState`/`ConfirmDialog` → 10.1.3-T1; [WR5] `10.3.3-T3 → 10.1.6-T1` (`safe-filename.ts`); [WR8] the `SessionStore` surface is `signIn`/`beginEnrolment`/`logout()`/`anonymousGuard`/`mode`, with no client-side re-auth state; [WR9] the 401 pass-through list; [WR14] e2e hygiene (`page.route`/`page.clock` only, no in-app hooks) | `../README.md:344-359` ✅ |

## 3. Domain and data

No database and no persistence of secrets. Client domain models live in `features/*/domain` and import nothing but `domain` + `@shared/util` (D2). Routes/params/types come from `@shared/api` only inside `data-access` (D5).

### 3.1 `features/auth/domain`

```ts
// auth.model.ts
export type AuthRole = 'owner' | 'admin' | 'user';
export interface Session { accessToken: string; expiresAt: string; role: AuthRole; amr: Array<'pwd'|'otp'|'rc'>; }
export type LoginOutcome =
  | { kind: 'ok'; session: Session }
  | { kind: 'mfa'; mfaToken: string }                       // 401 mfa_required
  | { kind: 'enrolment'; token: string };                   // 401 mfa_enrolment_required
export interface MfaSetup { secret: string; otpauthUri: string; }
export type RecoveryCodes = readonly string[];              // exactly 10, shown once
export type LoginStep = 'password' | 'mfa';                 // T1 ships 'password'; T3 widens to the union
```

```ts
// auth.port.ts (abstract class = the port; impl in data-access, fake in testing/)
export abstract class AuthRepository {
  abstract login(email: string, password: string): Promise<Result<LoginOutcome>>;
  abstract loginMfa(mfaToken: string, code: string, mode: 'totp'|'recovery'): Promise<Result<Session>>; // T3 adds
  abstract forgotPassword(email: string): Promise<Result<void>>;
  abstract resetPassword(token: string, password: string): Promise<Result<void>>;
  abstract logout(): Promise<Result<void>>;
}
// mfa.port.ts  (T3)
export abstract class MfaRepository {
  abstract setup(): Promise<Result<MfaSetup>>;
  abstract verify(code: string): Promise<Result<RecoveryCodes>>;
  abstract regenerateRecoveryCodes(): Promise<Result<RecoveryCodes>>;
}
```

`Result<T>` is the core-wide union from 10.2.2-T1 (`{ ok: true; value } | { ok: false; error: AppError }`, D9). No DTO type leaves `data-access`.

### 3.2 `core/session` (T3 addition, singletons — D2)

```ts
// reauth.port.ts
export abstract class ReauthPort { abstract reauth(password: string, code: string): Promise<Result<{ reauthenticatedAt: string }>>; }
// reauth.service.ts
@Injectable({ providedIn: 'root' }) export class ReauthService {
  ensureReauthenticated(): Promise<Result<void>>;   // single-flight; opens ReauthDialog; resolves ok() after 200
  get isDialogOpen(): Signal<boolean>;
}
```

`ReauthService.ensureReauthenticated()` is what 10.4.3-T1 calls before enabling autopilot (C12 of `../README.md:65`). The interceptor (10.2.1-T1) refreshes and retries **once** only for a 401 with no code, `unauthenticated`, or an expired-token challenge on a non-`/auth` route; `reauth_required`, `invalid_credentials`, `invalid_mfa_code`, `mfa_token_invalid`, `mfa_enrolment_required`, `refresh_token_reused` and `session_revoked` pass through unchanged and are **never replayed** [WR9] — that pass-through is exactly what `ReauthDialog` depends on.

`SessionStore` (`core/session`, 10.2.1-T1) is the canonical surface [WR8]: `signIn(session)`, `beginEnrolment(token)`, `logout()` (calls `POST /auth/logout`, then ends the session **without** `returnUrl`) and `anonymousGuard`, plus `mode: 'full' | 'enrolment'` — in `enrolment` mode the status is not `'authenticated'`, so realtime and `GET /me` never start. There is **no** client-side re-auth setter and no client-side session-ender at all: re-auth state lives server-side on the `sid` family (`reauthenticated_at`), and ending a session is 10.2.1-T1's job. T1/T3 only *call* this surface (D6, [WR8]).

### 3.3 `features/public/domain` (T1)

```ts
export type LegalDoc = 'privacy' | 'terms';
export interface LegalBlock { readonly id: string; readonly kind: 'heading'|'paragraph'|'list'|'quote';
  readonly level?: 2|3; readonly runs?: readonly LegalRun[]; readonly items?: readonly (readonly LegalRun[])[]; }
export type LegalRun = { kind: 'text'; text: string } | { kind: 'link'; text: string; href: string } | { kind: 'emphasis'; text: string };
export interface LegalDocument { doc: LegalDoc; lang: 'vi'|'en'; version: string; effectiveDate: string; blocks: readonly LegalBlock[]; }
export abstract class LegalRepository { abstract get(doc: LegalDoc, lang: 'vi'|'en'): LegalDocument; } // synchronous: build-time content
```

### 3.4 Signals / stores

| Store | Signals (read) | Methods | Lifetime |
|---|---|---|---|
| `LoginStore` (T1) | `step: Signal<LoginStep>`, `submitting`, `error: Signal<AppError\|null>`, `mfaToken: Signal<string\|null>` (memory only), `rateLimitedUntil`, `enrolmentRedirect: Signal<boolean>` | `submitPassword(email, password)`, `backToPassword()`; T3 adds `submitMfa(code, mode)`, `useRecoveryMode(boolean)` | route-provided (`/login`), discarded on leave |
| `ForgotPasswordStore` (T1) | `submitting`, `sent`, `error` | `submit(email)` | route-provided |
| `ResetPasswordStore` (T1) | `hasToken`, `submitting`, `error`, `done` | `submit(password, confirm)` | route-provided |
| `EnrolmentStore` (T3) | `step: Signal<'loading'\|'show'\|'verify'\|'codes'\|'error'>`, `setup: Signal<MfaSetup\|null>`, `codes: Signal<RecoveryCodes\|null>`, `error`, `confirmedSaved` | `load()`, `verify(code)`, `regenerate()`, `confirmSaved()` | route-provided (`/account/security/mfa-setup`) |
| `ReauthService` (T3, root) | `isDialogOpen` | `ensureReauthenticated()` | root, session-scoped reset (D6) |

All stores are `@Injectable()` provided by the routed page except `ReauthService` (root). No NgRx (D13). `computed` derives `canSubmit = !submitting() && !rateLimited()`; nothing deep-copies on keystroke.

## 4. Use cases

| # | Use case | Call | States | Errors |
|---|---|---|---|---|
| U1 | Owner signs in with password | `POST /auth/login` | idle → submitting → (`mfa` step \| `enrolment` redirect \| session) | 401 `invalid_credentials` (`auth.login.invalidCredentials`), 400 `validation_failed` → field errors, 429 `rate_limited` → countdown, 403 `email_not_verified` (S path, mapped anyway) |
| U2 | Owner completes MFA | `POST /auth/login/mfa` | code mode / recovery mode → submitting → session | 401 `invalid_mfa_code`, 401 `mfa_token_invalid` → back to the password step with `auth.mfa.challengeExpired`, 400 `validation_failed`, 429 → countdown |
| U3 | Owner without TOTP is routed to enrolment | 401 `mfa_enrolment_required` from U1 | redirect to `/account/security/mfa-setup` (`SessionStore.beginEnrolment`) | — |
| U4 | Owner enrols TOTP | `POST /me/mfa/setup` → `POST /me/mfa/verify` | loading → show (QR + manual key) → verify → codes (once) → done | 401 `invalid_mfa_code` (retry, 5 tries), 409 `concurrency_conflict` → reload setup, 401 `reauth_required` when entered from an authenticated session → `ReauthService` |
| U5 | Owner regenerates recovery codes | `POST /me/mfa/recovery-codes` | confirm (re-auth dialog) → codes shown once | 401 `reauth_required`, 429 |
| U6 | User requests a reset link | `POST /auth/forgot-password` | idle → submitting → always `sent` (202) | 400 `validation_failed` (e-mail shape), 429 → countdown |
| U7 | User resets the password | `POST /auth/reset-password` | token present/absent → submitting → done | 400 `validation_failed` (expired/used/unknown token → "request a new link"), 400 `breached_password` (password field), 429 |
| U8 | A sensitive call needs fresh auth | any guarded call → 401 `reauth_required` → `POST /auth/reauth` | dialog → submitting → retry original call once | 401 `invalid_credentials`, 401 `invalid_mfa_code`, 401 `session_revoked` (5th failure → dialog closes, session ends), 429 → countdown inside the dialog |
| U9 | Visitor reads a legal page | none (build-time content) | content \| `EmptyState` | — |
| U10 | Visitor switches language of a legal page | link to the other `/vi|/en` route | — | — |

Post-conditions: U1/U2/U3 leave the access token in `SessionStore` only; U4/U5 leave no code in memory after the reveal is dismissed; U7 leaves nothing; U8 refreshes `reauthenticated_at` server-side only.

## 5. API and events

Consumed routes (all relative, same origin, D3; JSON camelCase, `ProblemDetails`):

| Route | Used by | Success | Notable failures |
|---|---|---|---|
| `POST /api/v1/auth/login` | T1 | 200 `{ accessToken, expiresAt, role, amr }` + `__Secure-nts_rt` | 401 `invalid_credentials`, 401 `mfa_required { mfaToken }`, 401 `mfa_enrolment_required { token }` [INFERENCE: body key], 400 `validation_failed`, 403 `email_not_verified`, 429 |
| `POST /api/v1/auth/login/mfa` | T3 | 200 tokens + cookie | 401 `invalid_mfa_code`, 401 `mfa_token_invalid`, 400 `validation_failed`, 429 |
| `POST /api/v1/auth/refresh` | core boot/refresh (10.2.1-T1) | 200 `{ accessToken, expiresAt }` | 401 `refresh_token_reused`, 401 `session_revoked` |
| `POST /api/v1/auth/logout` | shell (10.2.6-T1) | 204 | 401 `session_revoked` |
| `POST /api/v1/auth/forgot-password` | T1 | 202 `{}` (always, padded ≥ 300 ms) | 400 `validation_failed`, 429 |
| `POST /api/v1/auth/reset-password` | T1 | 204 | 400 `validation_failed`, 400 `breached_password` |
| `POST /api/v1/auth/reauth` | T3 | 200 `{ reauthenticatedAt }` | 401 `invalid_credentials`, 401 `invalid_mfa_code`, 401 `session_revoked`, 429 |
| `POST /api/v1/me/mfa/setup` | T3 | 200 `{ secret, otpauthUri }` | 401 `reauth_required` (unless enrolment token), 409 `concurrency_conflict` |
| `POST /api/v1/me/mfa/verify` | T3 | 200 `{ recoveryCodes[10] }` | 401 `invalid_mfa_code`, 400 `validation_failed`, 401 `reauth_required` (unless enrolment token) |
| `POST /api/v1/me/mfa/recovery-codes` | T3 | 200 `{ recoveryCodes[10] }` | 401 `reauth_required` |

Excluded: `DELETE /me/mfa` (S → 10.9.5-T1), `/auth/register`, `/auth/verify-email`, `/auth/resend-verification` (S, C5).

Events: **none**. Auth and legal pages never open the hub and never subscribe (D8). The only session-scoped reaction is a session end — `SessionStore.logout()`, or the interceptor ending the session (D6, [WR8]): every store here resets — `LoginStore` clears `mfaToken`, `EnrolmentStore` clears `codes` and `setup.secret`, `ReauthService` closes the dialog. `RetryCountdown` restarts from `Retry-After` on any 429.

## 6. UI

### 6.1 Routes (feature `auth`, lazy `loadComponent`, D2/D13)

| Path | Component | Guard | Notes |
|---|---|---|---|
| `/login` | `LoginPage` | `anonymousGuard` (10.2.1-T1) | One page, two steps (`step` signal): password → MFA. **The `mfaToken` never enters the URL** (C1/C13): a reload during the MFA step returns to the password step. |
| `/forgot-password` | `ForgotPasswordPage` | anonymous | Always shows the same "sent" panel (C4). |
| `/reset-password` | `ResetPasswordPage` | anonymous | Reads `location.hash` `#t=…`, then `history.replaceState(null, '', location.pathname)` **before** the POST (D6/F03). |
| `/account/security/mfa-setup` | `MfaSetupPage` | `mfaEnrolmentGuard` — **one** guard that accepts a full session (`mode: 'full'`) or the enrolment-only token (`mode: 'enrolment'`); never an AND-array, which would bounce the enrolment-only owner to `/login` | Reachable with a full session (re-auth required, WT41) or with the enrolment-only token (re-auth waived, C6); `mode === 'enrolment'` until the owner signs in again. |

Consumed query params: `returnUrl` on `/login` (D6, sanitised by 10.2.1-T1). Public routes (feature `public`, T1): `/vi/privacy`, `/en/privacy`, `/vi/terms`, `/en/terms` (prerendered) + `privacy`/`terms` → `/vi/...` redirect [INFERENCE, keeps 13's "published at `/privacy`" true]. `app.routes.server.ts`: 10.1.1-T1 ships `outputMode: 'static'` with only `{ path: '**', renderMode: RenderMode.Client }`, and **this folder's 10.3.1-T1** adds exactly these four `RenderMode.Prerender` entries (D1, [WR1]); the auth routes stay `RenderMode.Client`.

### 6.2 Screens, states and copy keys (scope `auth`, plus `public` and `errors`)

**LoginPage** — states: password step / MFA step / recovery mode / 429 countdown / server error alert / submitting.
Keys: `auth.login.title`, `auth.login.subtitle`, `auth.login.email`, `auth.login.password`, `auth.login.submit`, `auth.login.forgot`, `auth.login.invalidCredentials`, `auth.login.emailNotVerified`, `auth.mfa.title`, `auth.mfa.hint`, `auth.mfa.code`, `auth.mfa.recoveryCode`, `auth.mfa.useRecovery`, `auth.mfa.useTotp`, `auth.mfa.submit`, `auth.mfa.challengeExpired`, `auth.mfa.back`, `auth.rateLimited`, `auth.rateLimited.retryIn`.

**ForgotPasswordPage** — `auth.forgot.title`, `auth.forgot.hint`, `auth.forgot.email`, `auth.forgot.submit`, `auth.forgot.sent`, `auth.forgot.sentHint`, `auth.forgot.back`.

**ResetPasswordPage** — `auth.reset.title`, `auth.reset.password`, `auth.reset.passwordHint` (≥ 12 chars, C6/09 BD2), `auth.reset.confirm`, `auth.reset.submit`, `auth.reset.success`, `auth.reset.successHint`, `auth.reset.goToLogin`, `auth.reset.missingToken`, `auth.reset.requestNew`, `auth.reset.mismatch`.

**MfaSetupPage** — `auth.enrol.title`, `auth.enrol.intro`, `auth.enrol.qrAlt`, `auth.enrol.manualKey`, `auth.enrol.copy`, `auth.enrol.copied`, `auth.enrol.stepVerify`, `auth.enrol.code`, `auth.enrol.verify`, `auth.enrol.recoveryTitle`, `auth.enrol.recoveryWarning` ("these 10 codes are shown once"), `auth.enrol.download`, `auth.enrol.confirmSaved`, `auth.enrol.regenerate`, `auth.enrol.reLogin`, `auth.enrol.done`.

**ReauthDialog** (`shared/ui/dialog/reauth-dialog/`, scope `core`) — `core.reauth.title`, `core.reauth.reason`, `core.reauth.password`, `core.reauth.code`, `core.reauth.codeHint` (empty when the account has no TOTP), `core.reauth.submit`, `core.reauth.cancel`, `core.reauth.retry`, `core.reauth.failed`.

**Legal pages** (`public`) — `public.legal.updated`, `public.legal.version`, `public.legal.toc`, `public.legal.otherLanguage`, `public.legal.backHome`, `public.nav.privacy`, `public.nav.terms`, `public.legal.empty`, plus `errors.<code>` for every code in C7.

Every code in C7 gets a key in both `vi` and `en` (D9); no raw `detail` is shown.

### 6.3 A11y (D16, per screen)

- Login/MFA: `<form>` with a single `h1`; each field labelled by `<mat-label>` + `aria-describedby` pointing at its error; on submit failure focus moves to the first invalid control, on a non-field error to the alert (`role="alert"`); `autocomplete="username"`, `"current-password"`, and `autocomplete="one-time-code"` + `inputmode="numeric"` for the code; the MFA code field is `autofocus` after the step change *only when the user pressed submit* (avoid stealing focus on load) and the step change is announced through `LiveAnnouncer`.
- `RetryCountdown`: `aria-live="polite"`, the submit button stays `disabled` and carries `aria-disabled` with the remaining seconds in its `aria-describedby` text.
- Enrolment: QR `<img>` has `alt` = `auth.enrol.qrAlt`; the manual key is selectable text with a copy button whose result is announced (`LiveAnnouncer`, `auth.enrol.copied`); the recovery-code list is an ordered list of `code` elements with a copy button per code; the "shown once" warning is `role="alert"`; the download button announces the filename; the reveal must be confirmed (`confirmSaved`) before the page leaves — no focus trap beyond that.
- ReauthDialog: MatDialog focus trap, `aria-labelledby` = `core.reauth.title`, `aria-describedby` = `core.reauth.reason`, Escape = Cancel, focus restored to the triggering element, error text linked with `aria-describedby`, no `scrolledToEnd` gate here (that rule is the autopilot authorization text, C12 of `../README.md`).
- Legal pages: `h1` + `h2`/`h3` from the block list in order, `<main>` landmark, a table of contents linking to block ids, skip link from the shell (10.2.6-T1), language switch is an `<a>` with the target language in `lang`/`hreflang`, contrast ≥ 4.5:1 in both themes, 360 px without horizontal scroll, no information by colour alone.
- Every page: visible focus ≥ 3:1, targets ≥ 24 px, `prefers-reduced-motion` honoured (no reveal animations).

**Design (owner rule 2026-09-25).** `10.3.3-T0` is the folder's design gate (D19). The commit-pinned artifact and exact frame ids are recorded in §6.4 and `10.3.3-T0-ui-design.md` §Outputs. Static coverage exists, but the required approver identity/date remains ⚠️ [OPEN], so the gate is not closed; interactive acceptance remains separate. Every task below keeps `10.3.3-T0` as its hard `Depends on`.

### 6.4 Commit-pinned design evidence and implementation seams (2026-10-02)

The approved visual snapshot is the commit-pinned [`01-shell-auth.html`](https://github.com/anglesen1120/NightTalesStudio/blob/920b507d0fcbff2195de0e492235425f39ac0c37/design/ui/screens/01-shell-auth.html). The snapshot is static evidence only: it does not prove form notification, focus movement, fragment removal, route/session teardown, prerendering or a real API journey. The required approver identity/date is not present in the repository record, so `10.3.3-T0` remains open even though the visual direction is recorded.

| Existing task | Observed frame ids | Coverage boundary / T0 amendment |
|---|---|---|
| `10.3.1-T1` | `f-legal-privacy`, `f-legal-terms-anchor`, `f-legal-empty` | Base legal layouts are observed. T0 must not claim that a frame proves no-JS/prerender behavior; the implementation acceptance below proves that separately. |
| `10.3.3-T1` | `f-session-expired`, `f-login-password`, `f-login-submitting`, `f-login-invalid`, `f-login-429`, `f-login-server-error`; `f-forgot-form`, `f-forgot-submitting`, `f-forgot-sent`, `f-forgot-429`; `f-reset-form`, `f-reset-mismatch`, `f-reset-weak`, `f-reset-breached`, `f-reset-submitting`, `f-reset-expired`, `f-reset-success` | Password/forgot/reset visual states are observed. `returnUrl`, fragment stripping, async server-error notification and neutral forgot behavior are runtime acceptance, not additional frames. |
| `10.3.3-T3` | `f-login-mfa`, `f-login-mfa-recovery`, `f-login-mfa-error`, `f-login-mfa-expired`, `f-login-mfa-locked`; `f-enrol-loading`, `f-enrol-banner`, `f-enrol-show`, `f-enrol-verify-error`, `f-enrol-reauth`, `f-enrol-codes`, `f-enrol-gate`, `f-enrol-done`, `f-enrol-error`; `f-reauth`, `f-reauth-submitting`, `f-reauth-error`, `f-reauth-429` | Base MFA/enrolment/re-auth states are observed. QR generation, copy/download, show-once recovery codes, dialog focus and complete owner sign-in remain runtime/API acceptance. `f-login-captcha` is S-only (`10.3.3-T2`) reference and is not P completeness evidence. |
| `10.3.3-T0` | All auth/legal frame ids above | The approval record remains open. A future amendment must name the approver/date and keep static-frame coverage separate from interactive acceptance; it must not promote the CAPTCHA reference into P. |

Composition follows [implementation-roadmap.md §2–§3](../implementation-roadmap.md): routed pages own route-scoped stores and typed forms; feature `ui/` receives readonly `LoginViewModel`, `ResetViewModel`, `MfaSetupViewModel` or `LegalDocumentViewModel` values and emits task-specific submit/retry/cancel/copy/download intents. `ReauthDialog` stays the existing shared owner `10.3.3-T3`; foundation/error primitives keep their WR4 owners. No new shared primitive is proposed here.

Every async form boundary converts relevant control events/status changes to a template-read signal (or performs one boundary `markForCheck`) and clears the bridge through `DestroyRef`; tests use `whenStable` rather than forced change detection. Login, forgot, reset and enrolment stores reject late results after route destruction or a session-generation change. Leaving `/login` clears the MFA challenge, leaving enrolment clears the secret/recovery codes, and session end closes re-auth and invalidates every pending continuation.

Readiness is split exactly as [integration-readiness.md](../integration-readiness.md) records: password session/refresh/logout and re-auth plus MFA-management routes are present on snapshot `00e7d5d5415579c7df55a66dc2177cbc2e31eb78`; complete MFA sign-in/challenge/enrolment-token delivery waits for `09.3.2-T3`; forgot/reset waits for `09.3.3-T1`; profile `/me` and locale/features wait for `09.3.4-T1`; runtime legal acceptance waits for `13.2.1-T1`. Static privacy/terms pages wait only for the build-time content owners `13.1.1-T1`/`13.1.2-T1` and never call the future acceptance API.

## 7. Jobs and integrations

- **Build step (T1)**: `tools/web/legal-to-json.mjs` runs `ng build`-adjacent in `web.yml` (10.1.5-T1) and converts `web/src/content/legal/<prefix>.<version>.<lang>.md` (manifest key -> file prefix: `tos -> terms`, `privacy -> privacy`; 13 CR-34) into the typed block module `web/src/app/features/public/data-access/legal-content.generated.ts` (committed, formatted, excluded from lint like `shared/api`). It reads `web/src/content/legal/manifest.json` = `{ "<doc>": { "version": "1.0.0", "effectiveDate": "YYYY-MM-DD" } }`, authored by 13.1.1-T1/13.1.2-T1; it **fails** on: a manifest version with a missing `.<lang>.md`, a `vi`/`en` version mismatch, any raw HTML tag, an unsupported construct, or a missing file. `web.yml` — owned by 10.1.5-T1, which this task only extends with the `legal:gen`/`legal:gen:check` scripts and the Node-test invocation — re-runs it and fails on a `git diff` (drift check, same shape as `api:gen`). Because the content is a module, prerender needs **no** HTTP (C14) and the page has no loading state.
- **Timers**: `RetryCountdown` (10.2.2-T1, [WR4]) owns its 1 s interval, cleaned up through `DestroyRef`; it is the only timer in these screens. No `visible-poll` here (D17 lists only the autopilot run log). No hub, no reconnect, no proactive token refresh (D7): the login/reset pages exist precisely for the unauthenticated case, and the enrolment page performs only user-initiated calls.
- **Prerender**: `ng build` prerenders the four legal routes; the legal components must be SSR-safe — no `window`/`localStorage`/`navigator` access outside a `isPlatformBrowser` guard, head writes through `Title`/`Meta`/`DOCUMENT` (T1's `PublicSeoService`), which do run during prerender.
- **Integrations consumed, not produced**: `AppErrorMapper` + `RetryCountdown` + `bindFieldErrors` (10.2.2-T1), `SessionStore` + guards + `sanitizeReturnUrl` (10.2.1-T1), session-end reset via `SessionStore.logout()` (D6, [WR8]), `PageHeader`/`ErrorState`/`EmptyState`/`ConfirmDialog`/`CopyableCode` (10.1.3-T1, [WR4]), `safe-filename.ts` (10.1.6-T1, [WR5]), shell header `header-end` slot (10.2.6-T1, D14 — unused in P).

## 8. Security and privacy

- **Tokens**: the access token lives in `SessionStore` memory only; the `mfaToken` and the enrolment token live in a store signal, never in the URL, never in `localStorage`/`sessionStorage`, never in a cookie readable by JS (D6/NFR-10-5). Reload during MFA → password step.
- **Reset link**: token read from `location.hash` and removed with `history.replaceState` before the POST so it cannot be re-read from the history entry; `Referrer-Policy: no-referrer` (C13) is the second layer; the page never renders the token and never writes it to a store beyond the request argument.
- **Open redirect**: `returnUrl` is accepted only if `sanitizeReturnUrl()` (10.2.1-T1, D6 regex `^/(?![/\\])[^\s]*$`) returns it **and** the target is not an auth page; otherwise `/dashboard`.
- **XSS / Trusted Types**: no `[innerHTML]`, no `bypassSecurityTrust*`, no `document.write`, no `eval`; legal blocks are rendered as elements/`@for` (C14); link `href`s come from the generated content and are validated to be `https://`, `mailto:` or site-relative by the generator (an unknown scheme fails the build); the QR is an `<img src="data:image/png;base64,…">` from `qrcode` (allowed by `img-src … data:`).
- **Secrets in the DOM and in memory**: `otpauthUri` contains the secret and is used **only** as the QR source — never as an `<a href>`, never logged, never put in a copy button; the manual key may be copied (that is the point) but is cleared from `EnrolmentStore` when the page is left; the 10 recovery codes are held in a signal only while `step === 'codes'` and dropped on `confirmSaved()`/leave/download.
- **Never in logs/storage**: password, `mfaToken`, enrolment token, access token, TOTP code, recovery code, `secret`, `otpauthUri`. No `console.*` in these screens (D2 lint bans `subscribe()` in components; a console rule covers the rest). No e-mail address in a URL or in a log; error telemetry sends `code` + `traceId` only.
- **CSRF/Origin**: reset and login POSTs are same-origin relative calls; the refresh cookie is `SameSite=Strict` with `Path=/api/v1/auth` (C1); no `withCredentials` needed (D6); the reset page sends no `Origin` manipulation.
- **Brute force**: the UI performs exactly one request per submit (no retry loop), honours `429` + `Retry-After` with a disabled action, and never auto-retries an MFA code — a single-use/step-replay answer (`invalid_mfa_code`) is final for that attempt (C8/C10).
- **Enumeration**: `/forgot-password` shows one identical panel for every input (C4); the login screen shows the same `auth.login.invalidCredentials` for unknown e-mail and wrong password.
- **Re-auth**: `POST /auth/reauth` is user-initiated, single-flight (one dialog at most), and never guesses codes; after the 5th failure the dialog closes and the session ends (`session_revoked`, C3).
- **Downloads**: the recovery-codes file name goes through `shared/util/safe-filename.ts` (10.1.6-T1, [WR4]/[WR5]) — quotes, `/`, `\` and control characters are stripped, 100 chars max.
- **Baseline**: no third-party script/font on these pages (font is self-hosted, D11); the legal pages make no network request at all (asserted in e2e).

## 9. Test plan

Unit/component (Vitest via `@angular/build:unit-test`, jsdom, TestBed zoneless, D15; hand-written `features/auth/testing/fake-auth.repository.ts` + `fake-mfa.repository.ts` — **no mocking library**):

| Area | Cases |
|---|---|
| `LoginStore` (T1) | success → `SessionStore.signIn` called once; `mfa_required` → `step === 'mfa'` and the token kept in memory only; `mfa_enrolment_required` → `enrolmentRedirect`; `invalid_credentials` → `error.code` + no session; `rate_limited` → `rateLimitedUntil` set from `Retry-After`; `validation_failed` → field errors bound; double submit → one call (in-flight guard). |
| `ForgotPasswordStore` (T1) | 202 → `sent` true; 400 → no `sent`; 429 → countdown; the store shows the same copy for both known and unknown e-mail (fake returns 202 with and without a queued mail). |
| `ResetPasswordStore` (T1) | token parsed from `#t=…`; missing token → `hasToken` false and no call; `history.replaceState` called before the POST (spy on `History`); 400 → "request new link"; `breached_password` → password field error; 204 → `done`. |
| `EnrolmentStore` (T3) | `load` → `step 'show'` with `{secret, otpauthUri}`; `verify` ok → `codes` length 10 and `step 'codes'`; `invalid_mfa_code` → stays on `verify` with a retryable error; `409` → back to `load()`; `confirmSaved()` clears `codes`; regenerate requires a successful re-auth first. |
| `ReauthService` (T3) | single-flight: two concurrent calls → one dialog, one POST; success resolves both; `session_revoked` → the dialog closes and the session ends (10.2.1-T1); 429 → countdown, the action stays disabled; success → the original call is retried once and **no** client-side re-auth state is stored (the server owns it, [WR8]). |
| `ReauthDialog` component (T3) | empty code when the account has no TOTP (input hidden); wrong password → field error + dialog stays open; Escape cancels without a POST; focus restored to the trigger. |
| `LoginPage` component (T1/T3) | password→MFA step transition announces and focuses the code field only after submit; recovery-code toggle sends exactly one of the two fields; the error alert gets focus on a non-field error. |
| `LegalDocumentPage` component (T1) | renders heading/paragraph/list/quote/link/emphasis blocks without `innerHTML`; shows version + effective date; the language switch points at the sibling route; `EmptyState` when there are no blocks. |
| `legal-to-json.mjs` (T1) | converts the sample fixture; **rejects** raw HTML, an unknown scheme in a link and a missing language file with exit code ≠ 0 (Node test, invoked by `web.yml`'s test step — 10.1.5-T1 owns that file). |

E2E (Playwright, `web/e2e/auth/`, run by 11.5.1-T1's `e2e.yml` — `web.yml` has no compose/e2e [WR3]; owner project `workers: 1`, `test.describe.serial`, one login per spec file, TOTP helper from a local RFC 6238 implementation that waits for the next 30 s step and retries on `invalid_mfa_code`; the enrolled secret lives only in the git-ignored `web/e2e/.auth/owner-totp.json`, deleted in global teardown, with traces/videos off for auth specs and no in-app test hooks [WR14], D15/F08):

1. `auth-login.spec.ts` — wrong password → `auth.login.invalidCredentials` and no navigation; `returnUrl=https://evil.example`, `//evil.example`, `/\evil` and `/login` are all rejected before any login. P seeds **one owner and no non-MFA account** (register is closed, C5), so the authenticated half — correct password + TOTP → `/dashboard`, `returnUrl=/series` honoured, reload → still authenticated — runs in T3's `auth-mfa.spec.ts` with the owner TOTP helper (§10 `[OPEN]`).
2. `auth-mfa.spec.ts` — MFA step reached; one valid code accepted, the same code replayed → 401 path shown and a fresh code (next step) succeeds; recovery-code login → authenticated (and the code is single-use).
3. `auth-enrolment.spec.ts` — owner without TOTP is redirected to `/account/security/mfa-setup`; setup shows a QR + manual key; a wrong code keeps the page; a correct code reveals exactly 10 codes with the "shown once" warning; `confirmSaved` is required to continue; the download saves a `*.txt` with a safe name; after enrolment the page asks to sign in again.
4. `auth-reset.spec.ts` — `/forgot-password` shows the same panel for a known and an unknown address; the reset link's `#t=` is removed from the URL before the POST; a reused link shows "request a new link"; a weak/breached password keeps the link usable and shows the field error; success → `/login`.
5. `auth-reauth.spec.ts` — `page.route` stubs a 401 `reauth_required` on a guarded call (**no** in-app test hook, `page.route`/`page.clock` only [WR14]); `ReauthDialog` opens, a correct password + code unblocks it, the original call is retried exactly once, and a 5th wrong attempt (`session_revoked`) ends the session.
6. `legal.spec.ts` — `/vi/privacy`, `/en/privacy`, `/vi/terms`, `/en/terms` return 200, render `h1`, the version and the effective date, carry the `hreflang` alternates and the canonical link; **zero** requests to `/api/**` while loading (route interception asserts an empty list); no console CSP/Trusted-Type violation; a 360 px viewport has no horizontal scroll.

Axe (`@axe-core/playwright`, WCAG 2.1 AA tags) on `/login` (both steps), `/forgot-password`, `/reset-password`, `/account/security/mfa-setup`, the open `ReauthDialog` and all four legal pages; the D4 CSP fixture (a `securitypolicyviolation` event or a console CSP error fails the spec) runs in every spec.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 10.3.1-T1 | Privacy and terms pages (prerendered, vi/en) | S (0.5) | 10.1.3-T1, 10.1.6-T1, 10.2.5-T1, 13.1.1-T1, 13.1.2-T1 |
| 10.3.3-T0 | UI design (login, MFA, forgot/reset, re-auth, legal pages) | M (1.0) | 10.1.3-T1, 09.3.3-T1 (soft) |
| 10.3.3-T1 | Login (password step), forgot and reset pages | M (1.0) | 10.1.3-T1, 10.2.1-T1, 10.2.2-T1, 09.3.3-T1 |
| 10.3.3-T3 | MFA login step, owner enrolment, recovery codes, re-auth dialog | M (1.0) | 10.3.3-T1, 10.1.6-T1, 09.3.2-T3, 09.10.4-T1 |

Blocks (`../README.md:297-299`, [WR5]): 10.1.6-T1 blocks 10.3.1-T1 and 10.3.3-T3; 09.3.3-T1 blocks 10.3.3-T1; 09.3.2-T3 and 09.10.4-T1 block 10.3.3-T3; 10.3.3-T1 blocks 10.3.3-T3; 10.3.3-T3 blocks 10.4.3-T1. Also (10 CR-37): 10.3.1-T1 blocks 10.3.1-T2; 10.3.3-T1 blocks 10.3.3-T2. Also (10 CR-40): 10.3.3-T3 blocks 10.9.5-T1 (now detailed in `10.9-admin-core/`).

Re-estimate: 1.0 (`10.3.3-T0` design, D19) + 0.5 + 1.0 + 1.0 = **3.5 d raw** for the 10.3 P part — equal to `../README.md:322` ("10.3 P part 1.5 → 2.5", +1.0 because 10.3.3-T3 moved out of the S task 10.9.5-T1, F04). The folder needs no re-estimate of its own; 10.9.5-T1 simultaneously shrinks S 0.5 → XS 0.25 (`../README.md:322`). Buffered ≈ 3.0 d at the wave-1 factor.

Open items (`[OPEN]`, do not block the tasks):

- [OPEN] `mfa_enrolment_required` and the body key carrying the enrolment token are not in 09 §5.4 (`09.3-auth/plan.md:207` records the same gap). T1/T3 code against `{ token }` [INFERENCE]; if the TL rules a different key or code name, only `data-access/auth.repository.ts` changes.
- [OPEN] Two 10.2.1-T1 names are still unpinned: the `returnUrl` sanitiser (called `sanitizeReturnUrl` here) and the guard that admits a full session **or** the enrolment token (called `mfaEnrolmentGuard` here). The rest of the surface is fixed by [WR8] (`signIn`, `beginEnrolment`, `logout()`, `anonymousGuard`, `mode`), so a rename of those two is a one-line change in `data-access`/`application`, reconciled at the wave-1 integration point.
- [OPEN] Whether the enrolment token is delivered in the 401 body, in a cookie, or only as the header value (C6 says "enrolment-only token", the response shape is not pinned). T3 assumes the same shape as `mfaToken` (`{ token }`).
- [OPEN] `13.1.1-T1`/`13.1.2-T1` are not detailed yet (`../README.md:250`), so `manifest.json` keys/format are [INFERENCE] — the generator's failure modes are fixed here, the values are theirs.
- [OPEN] `legal-to-json.mjs` emitting a committed typed TS module (rather than a JSON asset fetched at runtime) is the reading of D12 that makes prerender work without HTTP (C14). Recorded for the wave-1 review; the alternative (a JSON asset) would need a prerender-time `HttpClient` call against a static file.
- [SETTLED] `captcha_required`, the progressive delay and `mfa_locked_untrusted` belong to 09.3.3-T2/T3 (S) and trusted devices are not created in P (`09.3-auth/plan.md:16,214`); the pre-wave-2 high-level text `plans/10-web-ui.md:106-107` does not apply to this folder. P handles exactly: 429 `rate_limited` + `RetryCountdown`, 400 `breached_password`, 400 `validation_failed`, 401 `invalid_credentials` | `mfa_required` | `mfa_enrolment_required` | `mfa_token_invalid` | `invalid_mfa_code` | `reauth_required` | `session_revoked`, 403 `registration_closed` (D9, `../README.md:157`).
- [OPEN] P seeds one owner and **no** non-MFA account (`09.3-auth/plan.md:27`), so 10.3.3-T1's Playwright file asserts only the negative/no-session paths; the successful-login and `returnUrl` assertions live in 10.3.3-T3's `auth-mfa.spec.ts` (owner TOTP helper). Do not re-add a non-MFA happy-path spec to T1 unless a dev account is seeded.

Interface contracts this folder **produces** for others: `ReauthService.ensureReauthenticated()` (consumed by 10.4.3-T1), the `ReauthDialog` shared component (D11), and the `public.legal.*` / `auth.*` key sets.

## 11. Rollout

1. Order inside the folder: **10.3.1-T1** and **10.3.3-T1** are independent (a peer may run them in parallel); **10.3.3-T3** starts after T1 (it extends `LoginStore` and `auth.repository.ts`) and needs 09.3.2-T3 + 09.10.4-T1 merged for its e2e (component tests can run against the fakes). 10.3.1-T1 additionally waits on the two 13 legal drafts (`../README.md:250`).
2. No feature flag: the auth pages are the only way into the app; the legal routes are public. `youtube_publish`/`source_finder` flags (D14) are irrelevant here — no credit copy, no `cost-line` slot filled (D14).
3. Merge order with the rest of wave 1: this folder lands inside `10-W1-session` (`../README.md:259`) together with 10.2.1/10.2.2/10.2.4/10.2.6; the legal pages join the `10-W1-foundation` e2e smoke once 10.1.6-T1's CSP fixture is green.
4. Rollback: no schema, no server switch, no migration — reverting the web image restores the previous SPA; the only server-side effect a rollback can leave behind is an already-enrolled TOTP secret (`two_factor_enabled = true`), which the owner keeps and which stays valid (09.10.4-T1 changes no schema).
5. Post-deploy smoke (staging): open `/vi/privacy` (no API call, correct version), sign in as the owner with password + TOTP, reload (session restored), trigger `reauth_required` and complete the dialog, then request a password reset for the owner mailbox and finish it in an incognito window.
6. Follow-ups recorded for later passes: S items 10.3.2-T1 (register/verify), 10.9.5-T1 (disable MFA, `/admin` redirect), 13.2.1-T1 (consent dialog), the S-phase CAPTCHA widget, and the CDK/`ng update` housekeeping (D1) — none of them change this folder's contracts.
