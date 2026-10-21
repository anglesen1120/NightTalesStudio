# Plan 10.2.1 — Session, errors and idempotency (web UI core)

Stories 10.2.1 (session and silent refresh), 10.2.2 (error handling), 10.2.3 (idempotency interceptor) of
`plans/10-web-ui.md` §6 (10.2.1 M, 10.2.2 S, 10.2.3 XS). Contract: `plans/features/10-web-ui/README.md` §3
(D1–D18) and §8.1; authority order DECISIONS.md + `plans/00-overview.md` > `plans/01–13` > this folder.

Tasks in this folder: **10.2.2-T1** (`AppError` mapper + form error binding, S 0.5), **10.2.1-T1** (session store,
single-flight refresh, guards, activity gate, M 1.0), **10.2.3-T1** (`Idempotency-Key` per user intent, XS 0.25) —
1.75 d raw, all P · M1. Status: **10.2.2-T1 implemented** (#123, `669f082`), **10.2.1-T1 implemented** (#126,
`6ae07ec`; as-built API in its task §Notes), **10.2.3-T1 todo**. The paths below were written before the code;
where they differ, the code and the task Notes win.

## 1. Scope

In scope (P, M1):
- Error surface: one client mapper from ProblemDetails → `AppError` (D9), the field-error binder for typed reactive
  forms, and the three shared UI pieces the rest of wave 1 reuses (`FormErrorAlert`, `RetryCountdown`,
  `ConflictDialog`), plus the `errors` i18n scope.
- Session: `SessionStore` (in-memory access token), port + repository, the HTTP interceptor with the single-flight
  refresh and one retry, `provideAppInitializer` boot restore, `authGuard` / `mfaEnrolmentGuard`, `sanitizeReturnUrl`,
  the `SESSION_SCOPED` reset fan-out, and the D7 activity gate (`ActivityTracker`).
- Idempotency: `IdempotencyKeys` / `IdempotencyIntent` + the interceptor that attaches the header once per user
  intent and reuses it on retry.

Out of scope (owned elsewhere, only consumed):
- i18n infrastructure, Transloco config, the key-parity CI check — 10.2.5-T1 (`../10.2.4-realtime-i18n-shell/`).
- Realtime/hub reconnect, the `realtime.paused` bar, `resync` coalescing — 10.2.4-T1. This folder only publishes
  `ActivityTracker.isActive()`, `SessionStore.ensureFreshAccessToken()` and the `SESSION_SCOPED` reset hook.
- Design tokens, theme, Material setup, `PageHeader`/`EmptyState`/`ErrorState`/`LoadingState`/`StatusChip`/
  `ConfirmDialog`/`CharCounter` — 10.1.3-T1 (D11).
- Generated API client `@shared/api`, `core/http/withIfMatch.ts`, `readEtag` — 10.1.4-T1 (D5).
- Login/MFA/reset screens, `ReauthDialog`, `mfaEnrolmentRequired` page — 10.3.3-T1 / 10.3.3-T3.
- Cursor lists and polling helpers (`core/state/cursor-list.ts`, `shared/util/cursor-page.ts`,
  `core/state/visible-poll.ts`) — owned by 10.2.6-T1 (WR4) and only consumed here; this folder guarantees that a
  background poll never triggers a silent refresh while idle (D7, WR11).
- `roleGuard` for admin/owner routes — S phase (D6).

## 2. Confirmed facts

- ✅ Access token lives **in memory only**; `localStorage`/`sessionStorage` are banned by lint outside
  `core/prefs`, which may persist only the UI language (README.md:139 (D6), README.md:109 (D2, D6 rule)).
- ✅ Session bootstrap = `provideAppInitializer` → one `POST /api/v1/auth/refresh` with the cookie; guarded routes
  wait; 401 → anonymous (README.md:139-147; `plans/10-web-ui.md` 10.2.1 rule 2).
- ✅ Interceptor rules: `Authorization: Bearer` on `/api/v1/**` except `/auth/login*`, `/auth/refresh`,
  `/auth/forgot-password`, `/auth/reset-password`; on 401 (`session_revoked`, expired) **one** shared refresh
  (single-flight promise, concurrent 401s wait) and exactly **one** retry of the original request; refresh failure →
  `SessionEnded` → `/login?returnUrl=`; `withCredentials` not needed (same origin) (README.md:139-147).
- ✅ Refresh is on demand only: before a request when `expiresAt − now < 30 s`, or on 401. **No timer-driven
  refresh** (README.md:139-147, README.md:148-152 (D7)).
- ✅ `returnUrl` is accepted only when it matches `^/(?![/\\])[^\s]*$` and is not an auth page; else `/dashboard`
  (README.md:147).
- ✅ Logout = `POST /auth/logout` → `SessionEnded` (also on refresh failure and on `refresh_token_reused`); all
  feature stores reset through `SESSION_SCOPED` providers (`reset()`), realtime stops, router → `/login`. A
  user-initiated logout follows the dirty guard; a terminal `SessionEnded` discards drafts without prompting (README.md:145).
- ✅ Activity gate: `core/session/activity.ts` observes `pointerdown`, `keydown`, `wheel`, `touchstart` (throttled
  1/10 s) and `document.visibilityState`; `isActive()` = input within `idleWindow`, owner/admin 25 min and user
  13 days (just below the server limits); a refresh **not** caused by a user action happens only when `isActive()`,
  otherwise realtime pauses; polling runs only while visible **and** active; a user action after the window simply
  sends its request and the server decides (README.md:148-152, README.md:190 (D17)).
- ✅ `AppError { code, status, fieldErrors: Record<string, string[]>, retryAfterSec?, traceId?, problem }` is built
  by `AppErrorMapper` (10.2.2-T1) from ProblemDetails; network error → `network_error`; 413 → `payload_too_large`;
  unknown code → `unknown_error` + `traceId` shown; `detail` is never rendered raw; every code of 09 §5.4 + 02 D8
  used by wave-1 screens has `errors.<code>` in vi + en; `validation_failed` / `*_invalid_content` →
  `bindFieldErrors(form, error)`; 412 → `ConflictDialog`; 409 `episode_invalid_state` → refetch, then show `errors.episode_invalid_state` with the **refetched**
  `EpisodeDetailDto.nextActions` (C8; the error body has no action list);
  428 is a client bug (dev console only); 429 `rate_limited` / `story_generation_limit` → `RetryCountdown` from
  `Retry-After` (README.md:159-165).
- ✅ Backend contract (`plans/features/09-backend-api/09.3-auth/plan.md:75-95` route table):
  `POST /auth/login` → 200 `{ accessToken, expiresAt, role, amr }` + `__Secure-nts_rt` cookie;
  `POST /auth/login/mfa` → same token body; `POST /auth/refresh` → 200 `{ accessToken, expiresAt }` + rotated
  cookie, 401 `refresh_token_reused`, 401 `session_revoked`, 403 `forbidden` (Origin);
  `POST /auth/logout` → 204 + cleared cookie, 401 `session_revoked`, 403 `forbidden`;
  `POST /auth/reauth` → 200 `{ reauthenticatedAt }`. Cookie `__Secure-nts_rt` is
  `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` (09.3-auth/plan.md:70-72).
- ✅ Refresh rotation semantics: the presented row is rotated; **a second refresh with the same cookie is treated as
  reuse** and revokes the family → the SPA must never refresh in parallel
  (`09.3-auth/09.3.2-T1-login-refresh-logout.md:136-138`, `:201`, `:237-238`). This is the security reason the
  single-flight promise in 10.2.1-T1 is not an optimisation.
- ✅ Access-token TTL: `Auth:Access:UserMinutes` 15, `Auth:Access:AdminMinutes` 5,
  `Auth:Access:ClockSkewSeconds` 30; session limits owner/admin 12 h absolute / 30 min idle, user 14 d / none
  (`09.3-auth/plan.md:125-131`, `09.3-auth/plan.md:63`). So a client-side 30 s freshness margin matches the
  server clock-skew allowance.
- ✅ `Idempotency-Key` header: present, 1–128 chars, printable ASCII `0x21–0x7E`; missing on a required route →
  400 `idempotency_key_required`; same key with a different body → 422 `idempotency_key_reused`
  (`09.1.4-T1-problem-details-etag-helpers.md` Design filters; `plans/09-backend-api.md:5.4`).
- ✅ Validation errors: any `Error.Type == Validation` → 400 `validation_failed` with `errors` grouped by
  `metadata["field"]`; 429 with `metadata["retryAfterSeconds"]` sets `Retry-After`; unknown code → 500
  `internal_error`; `traceId` comes from `Activity.Current`
  (`09.1.4-T1-problem-details-etag-helpers.md` "Mapping rules" and "Exceptions").
- ✅ Plan 02 field-error codes and shapes: `story_invalid_content` / `fact_sheet_invalid_content` /
  `bible_invalid_content` are 400 with field errors in `errors`; `keywords_flagged` carries
  `errors[] { segment, index, rule }` (409) — an array of objects, not a field map
  (`plans/features/02-story-writing/README.md:255-300`; `plans/09-backend-api.md:5.4`).
- ✅ D5 helper layout this folder must match: repositories return `Promise<T>` (`firstValueFrom`), stores never see
  `HttpResponse`, `core/http/` holds `withIfMatch`, `readEtag` and the idempotency helper, only
  `features/*/data-access/**` and `core/**/data-access` import `@shared/api` (README.md:133-138).
- ✅ D16 a11y baseline, D15 test stack (Vitest via `@angular/build:unit-test` + jsdom, hand-written fakes
  implementing the domain ports, Playwright + `@axe-core/playwright`, `workers: 1`, one login per spec file)
  (README.md:188, README.md:182-187).
- ✅ This folder is M1 and unblocked in the graph: 10.2.2-T1 ← 10.1.4-T1, 10.2.5-T1, 09.1.4-T1; 10.2.1-T1 ←
  10.1.4-T1, 10.2.2-T1, 09.3.2-T1; 10.2.3-T1 ← 10.2.1-T1 (README.md:277 §8.1 rows).
- ⚠️ The backend answers 403 `forbidden` on refresh when `Origin` is missing/foreign (`09.3-auth/plan.md:80`,
  `09.3.2-T1:158`). In a same-origin browser deployment this cannot happen from JS; the client treats it as a
  configuration bug (dev console + `unknown_error`), **not** as a session end. Unverified in a browser run.
- ⚠️ Whether the 401 on `POST /auth/refresh` also carries `Retry-After`/`traceId` is not stated; the mapper must
  tolerate both absent (`[INFERENCE]`).
- ⚠️ `idempotency_key_reused` (422) has no wave-1 route that can legitimately produce it in P (only 10.5.2-T1
  generate is marked paid/intent-based); the client maps it as a bug signal. `[INFERENCE]`.
- ✅ WR9/WR10/WR11 (TL rulings, `README.md:352-354`): the interceptor refreshes + replays **only** a 401 with no
  code, `unauthorized` (the App-layer code an expired access token carries — `ErrorCodes.Unauthorized`,
  `09.1.4-T1:21`; 09 configures no `OnChallenge`/`JwtBearerEvents`, so no challenge form can be assumed),
  `unauthenticated` or an `invalid_token` challenge on a non-`/auth` route — the other eight codes pass
  through; every refresh runs inside `navigator.locks.request('nts-refresh', …)` with a token re-check after
  acquiring it; requests carry a `USER_ACTION` `HttpContext` flag and a background 401 refreshes only while
  `isActive()`.
- ✅ WR8 (`README.md:351`): the store surface adds `mode: 'full' | 'enrolment'`, `beginEnrolment(token)`,
  `logout()` (POST then end the session without `returnUrl`) and `anonymousGuard`; enrolment mode is never
  `'authenticated'` and there is no client `markReauthenticated`.
- ✅ WR4/WR15 (`README.md:347`, `:358`): `ConflictDialog`/`RetryCountdown`/`bindFieldErrors`/`FormErrorAlert` are
  10.2.2-T1's; `CharCounter`/`ConfirmDialog`/`PageHeader`/`ErrorState`/`LoadingState`/`StatusChip`/`Stepper`/
  `CopyableCode` are 10.1.3-T1's; `cursor-list`/`cursor-page`/`visible-poll` are 10.2.6-T1's;
  `safe-filename`/`nts-sanitizer` are 10.1.6-T1's; the invented story-level conflict code is removed and
  `series_conflict` is in the 412 set.

## 3. Domain and data

Client-only, no database and no persistence: the only storage this folder touches is process memory. `core/prefs` (language only)
is the single exemption from the D2 storage ban and is owned by 10.2.5-T1.

Models (`web/src/app/core/…`, plain interfaces/types — no Angular imports in `domain`-like files):
- `core/errors/app-error.ts` — `interface AppError { code: string; status: number; kind: AppErrorKind;
  fieldErrors: Readonly<Record<string, readonly string[]>>; retryAfterSec?: number; traceId?: string; problem?: ProblemDetails }`
  (D9); `type AppErrorKind = 'validation' | 'auth' | 'conflict' | 'precondition' | 'rate_limit' | 'not_found' |
  'feature_disabled' | 'client_bug' | 'network' | 'server' | 'unknown'`; guards `isConflict`, `isRateLimited`,
  `isValidation`.
- `core/errors/error-code.ts` — `const ERROR_CODES = { validationFailed: 'validation_failed', … } as const` with
  one entry per code in 09 §5.4 + 02 D8 that wave-1 screens can receive; `type KnownErrorCode = …`;
  `isKnownCode(code: string)`; `kindFor(code, status)`.
- `core/session/token-pair.ts` — `interface TokenPair { accessToken: string; expiresAt: number }` (`expiresAt` =
  epoch ms parsed from the ISO `expiresAt` the API returns); `interface SessionUser { role: PlatformRole; amr:
  string[] }`; `type PlatformRole = 'user' | 'admin' | 'owner'` (snake_case role in the JWT, 09.3.2-T1:46).
- `core/session/session-end.ts` — `type SessionEndReason = 'logout' | 'expired' | 'revoked' | 'reused' |
  'restore_failed'`.

Ports and stores:
- `core/session/session.api.ts` — abstract class `SessionApi { abstract refresh(): Promise<TokenPair>;
  abstract logout(): Promise<void>; }` (DTO ↔ model mapping happens in the repository, D5).
- `core/session/data-access/session.repository.ts` — `@Injectable() HttpSessionRepository implements SessionApi`:
  the only file in this folder importing `@shared/api` (D2/D5); maps `expiresAt` ISO → epoch ms; `logout()` uses
  `firstValueFrom` and ignores the 204 body.
- `core/session/session-store.ts` — `@Injectable({providedIn: 'root'}) SessionStore` (session-scoped by nature,
  D6). Private writable backing exposes readonly accessToken/expiresAt/role/amr/me/enrolmentToken/endReason/
  restoreSettled/generation views (10.2.1-T1); status is computed across anonymous/restoring/authenticated/
  refreshing/ended. The shell owns GET me and fills it synchronously through setMe.
  Derived: hasFeature(key: string): boolean (reads me()?.features),
  `mode = computed<'full'|'enrolment'>(() => enrolmentToken() ? 'enrolment' : 'full')` (WR8),
  `isAuthenticated = computed(() => status() === 'authenticated')`. Async: `restore(): Promise<void>`,
  `ensureFreshAccessToken(opts?: { userAction?: boolean }): Promise<string|null>`,
  `signIn(token: TokenPair, role: PlatformRole, amr: readonly string[]): void`,
  `setMe(summary: MeSummary | null): void`, `beginEnrolment(token: string): void`, `logout(): Promise<void>`,
  `authHeader(): string|null`,
  `signOut(reason: SessionEndReason): void`, `reset(): void`.
- State kept outside signals: `private refreshInFlight: Promise<string|null> | null` (the single-flight promise,
  never exposed) and `private restorePromise` (guards await it).
- `core/session/me-summary.ts` — `interface MeSummary { uiLocale: 'vi' | 'en'; role: PlatformRole;
  features: readonly string[]; displayName: string; email: string }`: the in-memory view of `GET /me`
  (`09.3.4-T1`); every field is grounded in `MeDto(Guid Id, string DisplayName, string Email, Language UiLocale,
  PlatformRole Role, …, IReadOnlyList<string> Features)` (`09.3.4-T1-me-endpoints.md:22-24`, `Language` serialised
  as `"vi"`/`"en"`); the shell's `data-access` maps `MeDto` from `@shared/api` into this view — no wire type is
  hand-declared here.
- `core/session/refresh-lock.ts` — `REFRESH_LOCK_NAME = 'nts-refresh'` and `RefreshLock.run(fn)` over
  `navigator.locks.request(name, { mode: 'exclusive' }, fn)` with an in-process mutex fallback (WR10): every refresh
  runs inside it and re-checks the token after acquiring it, so two tabs never present the same rotating cookie
  (C1 reuse detection, `09.3.2-T1:136`).
- `core/session/session-scope.ts` — `interface SessionScoped { reset(): void }` and
  `const SESSION_SCOPED = new InjectionToken<SessionScoped[]>('SESSION_SCOPED')` (multi: true); every root-level
  feature store (10.4.x, 10.5.x) provides itself under it; `signOut` iterates and calls `reset()`.
- `core/session/activity.ts` — root ActivityTracker: private writable lastInputAt/tick/isVisible backing,
  readonly same-named views; one 10 s tick interval, idleWindowMs computed(role-aware),
  `isActive = computed(() => tick() >= 0 && Date.now() - lastInputAt() < idleWindowMs())`,
  `isVisible` reflects document.visibilityState; methods
  `start()` / `stop()`; constants `ACTIVITY_WINDOWS = { admin: 25*60_000, user: 13*86_400_000 }` and
  `ACTIVITY_THROTTLE_MS = 100` (no timer-driven refresh, D7).
- `core/http/idempotency-keys.ts` — `@Injectable({providedIn:'root'}) IdempotencyKeys { begin(intent: string):
  IdempotencyIntent; newKey(): string }` and `class IdempotencyIntent { readonly key: string; readonly intent:
  string; settle(): void; isSettled: boolean }`; key = `crypto.randomUUID()`.
- `core/http/http-context-tokens.ts` — `const IDEMPOTENCY_INTENT = new HttpContextToken<IdempotencyIntent|null>(
  () => null)`; `const SILENT_REQUEST = new HttpContextToken<boolean>(() => false)` (`true` = do not refresh for
  this request: boot refresh, logout, auth screens' own calls); `const USER_ACTION = new HttpContextToken<boolean>(
  () => false)` (WR11: set by the store methods a UI event calls; a 401 without it refreshes only while
  `isActive()`).
- `core/http/http-context.ts` (helper) — `withIdempotency(intent)`, `silently()` and `userAction()` return `HttpContext` mutators;
  repositories use them so a store never touches `HttpContext` directly (D5: stores never see `HttpResponse`).
Implementation ownership/lifetime: 10.2.1-T1 owns root SessionStore/ActivityTracker and guards; all writable
signals remain private with public readonly views. Replace immutable arrays/objects, derive flags with computed.
Guards inject synchronously and await memoized `restore(): Promise<void>`, never a boolean restoreSettled view.
Shell children use authChildGuard; enrolment setup is outside that full-session shell with its own guard.
Session generation increments on identity replacement/end; restore/refresh capture/check it after awaits,
including failure and finally ownership, so late A completions cannot affect B. Deferred Router resolution
captures Injector during construction and uses injector.get(Router) in signOut, never method-local inject().

10.2.2-T1 owns the error binder: control namespace `server: { messageKeys: readonly string[] }`, preserves local
validator errors, replaces server errors per submit, clears that namespace on edit/resubmit, and returns unmapped
pointers to FormErrorAlert. Containers own DestroyRef-scoped form-state notification bridges, not the binder.
RetryCountdown initializes remaining to zero, reads required input after binding, re-arms one cleaned timer and
emits once per arm at zero; destruction does not emit. Tests await whenStable without forced change detection.

No domain table, no cache, no persisted draft: story drafts stay in memory (D18, F25) and this folder adds no
storage of its own.

## 4. Use cases

1. **Boot restore (page reload).** `provideAppInitializer` → `SessionStore.restore()` → `POST /auth/refresh`
   (`SILENT_REQUEST`) → 200 `{accessToken, expiresAt}` (+ rotated cookie, not readable by JS) → `signIn(...)`,
   `restoreSettled = true`; 401 → anonymous, `endReason = null` (a visitor is not an error); network failure →
   anonymous + `restoreSettled = true` + dev-console `network_error` (D6, D9).
2. **Guarded navigation while restoring.** authGuard/authChildGuard await memoized restore Promise,
   inspect current full-session mode/status, return true or sanitized login/setup UrlTree, never navigate as
   a guard side effect or await restoreSettled boolean. Setup uses separate enrolment guard outside shell.
3. **Pro-active refresh.** Interceptor: if a non-silent `/api/v1/**` request has `expiresAt − now < 30_000`, await
   `ensureFreshAccessToken({ userAction: req.context.get(USER_ACTION) })` then attach `Authorization: Bearer
   <authHeader()>`; a background request refreshes only while `isActive()` (WR11) and N parallel requests share one
   promise and one `RefreshLock` acquisition (WR10).
4. **401 matrix (WR9).** Retryable (refresh once, then replay once, non-`/auth` routes): 401 with no code,
   `unauthorized`, `unauthenticated`, or an `invalid_token` challenge. An idle-gated refusal (WR11) returns `null`
   and **keeps** the session (the caller sees the original 401 and pauses); only a failed refresh or a replay that
   401s again ends it. Never replayed — the response reaches the caller:
   `reauth_required` (10.3.3-T3 re-runs the store-level action through `ReauthDialog`), `invalid_credentials`,
   `invalid_mfa_code`, `mfa_token_invalid`, `mfa_enrolment_required` (`beginEnrolment`, WR8), `hub_ticket_invalid`
   (10.2.4-T1 takes a new ticket), `refresh_token_reused` (`signOut('reused')`), `session_revoked`
   (`signOut('revoked')`). A retryable 401 whose replay also 401s → `signOut('revoked')`; `/auth/*` is never
   retried.
5. **`refresh_token_reused`.** Refresh answers 401 `refresh_token_reused` → `signOut('reused')` → `/login`
   with `errors.refresh_token_reused` as the reason banner; the user is warned that other devices were signed out.
6. **Logout.** The user menu calls `SessionStore.logout()` (WR8) → `SessionApi.logout()` → 204 → `signOut('logout')`:
   `SESSION_SCOPED` reset (feature stores clear, realtime stops through its own subscription to `status`), router →
   `/login` **without** `returnUrl`; a user-initiated logout follows the dirty guard, while a terminal `SessionEnded` discards drafts without prompting (README.md:145).
7. **Idle expiry (no user action).** A hub reconnect or a poll tries to refresh while `isActive()` is false →
   `ensureFreshAccessToken()` **refuses** (returns `null` without a call); realtime stays paused and its bar offers
   "Continue" (10.2.4-T1). A user action after the window is sent as-is; the server's 401 `session_revoked`
   is then handled by case 4/5.
8. **Form validation.** A 400 `validation_failed` (or `story_invalid_content`, `source_url_invalid`, …) is mapped
   to `AppError` with `fieldErrors`; the page calls `bindFieldErrors(form, error)` → keys like `title`,
   `segments.2.text`, `voiceId` land on the matching `AbstractControl` (camelCase path = control path, D22
   segment addresses for segments, D18); unmapped keys are returned and shown in the form-level alert; focus moves
   to the first invalid control and it gets `aria-describedby` (D9, D16).
9. **Conflict.** 412 (`story_version_conflict`, `series_conflict`, `fact_sheet_conflict`, `bible_conflict`,
   `setting_conflict`; WR15 removed the invented story-level conflict code) → `AppError.kind = 'conflict'`. This folder owns
   the `ConflictDialog` component and its contract; the 412 **flow** (when it opens, keeping the unsaved input in
   memory until a choice) is owned by the consuming editor, 10.5.10-T1 (WR4).
10. **Invalid state / rate limit.** 409 `episode_invalid_state` → the page refetches the episode and shows
    `errors.episode_invalid_state` with the `nextActions` of the refetched `EpisodeDetailDto` (C8 — not part of the
    error envelope); 429 `rate_limited` /
    `story_generation_limit` / `source_fetch_limit_reached` → `RetryCountdown` from `Retry-After`, the action stays
    disabled until 0 (D9).
11. **Client bug signals.** 428 `if_match_required`, 400 `idempotency_key_required`, 422
    `idempotency_key_reused` → generic user message + dev-console line with code/status/traceId only (no body);
    413 → `payload_too_large` copy (D9).
12. **Retry of a paid action.** The store creates one `IdempotencyIntent` per user intent
    (`intent = keys.begin('episode.generate')`), the repository sends it via `withIdempotency(intent)`; the
    interceptor attaches `Idempotency-Key: intent.key` on every attempt, including the 401 retry and a manual
    "Retry" of a `network_error` attempt; `intent.settle()` once the call returns a non-retryable answer, so the
    next user click gets a new key (10.2.3-T1, README.md:133-138).
13. **Unknown code.** Any code not in `ERROR_CODES` → `unknown_error` + `errors.unknown_error` copy and the
    `traceId` shown in the alert footer so the owner can quote it (D9).
14. **Enrolment-only session (WR8).** A login that answers `mfa_enrolment_required` calls `beginEnrolment(token)`:
    `mode() === 'enrolment'`, `status()` is not `'authenticated'`, so `GET /me` and the hub never start; the Bearer
    header uses the enrolment token for `/me/mfa/setup` and `/me/mfa/verify`; `mfaEnrolmentGuard` allows only
    `/account/security/mfa-setup`; a successful enrolment calls `signIn(...)`, which clears the enrolment token.
    `anonymousGuard` sends an authenticated `full` session away from `/login`, `/forgot-password` and
    `/reset-password` to `/dashboard`.

## 5. API and events

Routes consumed by this folder (route/verb/envelope from the signed backend plans; no new route is invented):

| Route | Task | Success | Errors the client maps |
|---|---|---|---|
| `POST /api/v1/auth/refresh` | 10.2.1-T1 | 200 `{ accessToken, expiresAt }` + rotated `__Secure-nts_rt` | 401 `session_revoked`, 401 `refresh_token_reused`, 403 `forbidden`, network |
| `POST /api/v1/auth/logout` | 10.2.1-T1 | 204 + cleared cookie | 401 `session_revoked` (treated as success), 403 `forbidden` |
| `POST /api/v1/auth/login`, `/auth/login/mfa` | consumed (producer 10.3.3-T1) | 200 `{ accessToken, expiresAt, role, amr }` | `invalid_credentials`, `mfa_required`, `mfa_enrolment_required`, `invalid_mfa_code`, `mfa_token_invalid`, `email_not_verified`, `rate_limited` |
| `POST /api/v1/auth/reauth` | consumed (10.3.3-T3) | 200 `{ reauthenticatedAt }` | 401 `reauth_required`, `invalid_credentials`, `invalid_mfa_code` |
| `POST /api/v1/auth/forgot-password`, `/auth/reset-password` | consumed (10.3.3-T1) | 202 / 204 | 400 `validation_failed`, 400 `breached_password`, 429 `rate_limited` |
| every `/api/v1/**` business route | consumed | — | whole 09 §5.4 + 02 D8 catalogue via `AppErrorMapper` |
| paid `/api/v1/**` POSTs (wave 1: episode story generate) | 10.2.3-T1 | — | 400 `idempotency_key_required`, 422 `idempotency_key_reused` |

Envelope: ProblemDetails (`type`, `title`, `status`, `code`, `traceId`, `errors?` + allow-listed metadata keys),
written by 09.1.4-T1 and fixed by `plans/features/09-backend-api/README.md:191`; the client reads `code`, `status`,
`traceId` and `errors`, ignores `type`/`instance`, and never renders `detail` (D9). The error envelope carries **no
action list**: on 409 `episode_invalid_state` the page refetches the episode and reads `nextActions` from the
`EpisodeDetailDto` it gets back (README.md:61 C8;
`09.5.1-episodes/09.5.1-T1-episode-commands-queries-pagination.md:126`, 09.5.3 adds the field) — the client never
parses actions out of a ProblemDetails body. `Retry-After` (seconds) is read from the header, not the body
(`09.1.4-T1:Mapping rules`).
Snapshot availability is recorded in [integration readiness](../integration-readiness.md), audited develop
`00e7d5d5415579c7df55a66dc2177cbc2e31eb78` (2026-10-02). Refresh/logout/basic login/reauth exist; the table's
MFA login/pre-auth/enrolment response is a future 09.3.2-T3 contract, reset routes await 09.3.3-T1 (+09.2.6-T3),
and shell GET/PATCH `/api/v1/me` awaits 09.3.4-T1. Management `/me/mfa/*` and mail infrastructure do not satisfy
those prerequisites. Real-system acceptance for each missing integration remains blocked on its named owner.
Wire ProblemDetails uses optional `errors` → internal fieldErrors, flattened scalar metadata (not nested),
HTTP Retry-After → retryAfterSec, and parsed body → problem; these internal fields are not a success/error DTO.

401 handling (WR9): the interceptor refreshes once and replays once **only** for a 401 with no code,
`unauthorized` (the shape an expired access token actually carries, `09.1.4-T1:21`), `unauthenticated`, or an
`invalid_token` challenge on a non-`/auth` route; an idle-gated refusal keeps the session (WR11). The eight pass-through codes
(`reauth_required`, `invalid_credentials`, `invalid_mfa_code`, `mfa_token_invalid`, `mfa_enrolment_required`,
`hub_ticket_invalid`, `refresh_token_reused`, `session_revoked`) reach the caller unchanged; of those,
`session_revoked` ends the session (`signOut('revoked')`) and `refresh_token_reused` ends it as `'reused'`.
`GET/PATCH /me` answers 401
`unauthenticated` when the token outlived the account (`09.3.4-T1-me-endpoints.md:49`, `:52`, `:100`); that code is
absent from the 09 §5.4 catalogue and from `09.1.4-T1`'s `ErrorCodes`, so the client maps `unauthenticated` to
`errors.session_revoked` (the mapper's 401 fallback `errors.unauthorized` is a terminal-only path) — [OPEN]
cross-plan gap below.

Events (realtime, D8) — this folder publishes no hub event; it consumes none. It only makes the session state
observable for 10.2.4-T1: `SessionStore.status`, `SessionStore.endReason`, `ActivityTracker.isActive`. The hub is
aborted server-side on family revocation (`NOTIFY security`, `09.3-auth/plan.md:87-93`); the client reaction is
10.2.4-T1's `onclose` path, which calls `ensureFreshAccessToken()` and ends the session when that fails (this
folder guarantees that contract).

## 6. UI

No new routed screen. Surfaces and components this folder owns or changes:

| Surface | Where | Task | Notes |
|---|---|---|---|
| `FormErrorAlert` (shared) | `web/src/app/shared/ui/form-error-alert/` | 10.2.2-T1 | inputs `error = input<AppError | null>(null)`, `unmappedKeys = input<readonly string[]>([])`; renders `role="alert"`, the `errors.<code>` message, an optional `traceId` footer, a `@defer`-free inline `RetryCountdown` when `retryAfterSec` is set; no output needed (the page's submit button owns retry) |
| `RetryCountdown` (shared, D11) | `web/src/app/shared/ui/retry-countdown/` | 10.2.2-T1 | inputs `seconds: number`, `messageKey?: string`; output `elapsed`; 1 s `setInterval` cleared in `DestroyRef`; `aria-live="polite"` announces only on finish (D16) |
| `ConflictDialog` (shared, D11) | `web/src/app/shared/ui/conflict-dialog/` | 10.2.2-T1 | component + contract only (the 412 invocation flow is 10.5.10-T1's, WR4); inputs `open`, `messageKey`, `detailKeys?: string[]`; outputs `reload`, `saveAsNew`, `cancel` (same three strings from `afterClosed()`); Material dialog with focus trap + focus restore, ESC = cancel, no colour-only emphasis |
| `ReauthDialog` reuse | — | 10.3.3-T3 | not built here; it uses the same mapper for `reauth_required`/`invalid_mfa_code` |
| Session end banner | `web/src/app/features/auth/ui/session-ended-banner/` | 10.3.3-T1 (consumer) | this folder only supplies `endReason` + the copy keys |
| Login page error area | `web/src/app/features/auth/ui/login-page/` | 10.3.3-T1 (consumer) | uses `FormErrorAlert` + the binder |

Copy keys (vi + en, **same key sets** in both files; owner = this folder for these paths):

`web/src/assets/i18n/errors/{vi,en}.json`:
- `errors.<code>` for every code of 09 §5.4 + 02 D8 reachable from wave-1 screens: `validation_failed`,
  `unauthorized`, `unauthenticated` (alias → `errors.session_revoked`, see §5), `not_found`, `forbidden`,
  `episode_invalid_state`, `episode_conflict`, `concurrency_conflict`,
  `series_archived`, `series_topic_locked`, `series_conflict`, `voice_language_mismatch`, `if_match_required`,
  `idempotency_key_required`, `idempotency_key_reused`, `rate_limited`, `service_unavailable`, `internal_error`,
  `network_error`, `payload_too_large`, `unknown_error`, `session_revoked`, `refresh_token_reused`,
  `invalid_credentials`, `mfa_required`, `mfa_enrolment_required`, `invalid_mfa_code`, `mfa_token_invalid`,
  `reauth_required`, `email_not_verified`, `feature_disabled`, `story_version_conflict`,
  `fact_sheet_conflict`, `bible_conflict`, `story_invalid_content`, `fact_sheet_invalid_content`,
  `bible_invalid_content`, `story_generation_in_progress`, `fact_sheet_generation_in_progress`,
  `find_sources_in_progress`, `story_generation_limit`, `source_fetch_limit_reached`, `story_sources_required`,
  `story_fact_sheet_outdated`, `story_attestation_required`, `story_too_similar_to_source`, `story_moderated`,
  `fact_sheet_required`, `fact_sheet_sources_insufficient`, `source_url_invalid`, `source_domain_not_allowed`,
  `source_duplicate`, `source_limit_reached`, `source_candidate_not_proposed`, `bible_proposal_not_pending`,
  `autopilot_authorization_required`, `autopilot_topic_locked`, `autopilot_pause_reason_active`,
  `autopilot_settings_invalid`, `keywords_flagged`, `invalid_mfa_code`, `mfa_locked_untrusted`.
- UI-level leaves: `errors.form.summaryTitle`, `errors.form.unmappedCount` (`{{count}}`), `errors.unknown.trace`
  (`{{traceId}}`), `errors.action.retry`, `errors.action.dismiss`, `errors.retryIn` (`{{seconds}}`),
  `errors.retryNow`, `errors.conflict.title`, `errors.conflict.reload`, `errors.conflict.saveAsNew`,
  `errors.conflict.cancel`.

`web/src/assets/i18n/core/{vi,en}.json` (`session` sub-object owned here; 10.2.4-T1 owns `realtime.*`,
10.2.6-T1 owns `shell.*` — agreed with the sibling folder):
`core.session.signedOut.title/body`, `core.session.expired.title/body`, `core.session.revoked.title/body`,
`core.session.reused.title/body`, `core.session.restoreFailed.body`, `core.session.signIn`,
`core.session.signOut`, `core.session.reauthRequired.title/body`.

Per-control messages read `errors.server.messageKeys[0]` written by bindFieldErrors, preserving local-validator
messages in the consumer's mat-error and its documented aria-describedby association. A
`FormFieldError` component is **not** built (absent from D11, `README.md:168`, and from WR4, `README.md:347`; adding
one would need TL sign-off).

a11y per surface (D16): alert region `role="alert"` + `aria-live="polite"` for the session banner; every invalid
control keeps `aria-invalid` and is linked with `aria-describedby`; focus moves to the first invalid control after a
`validation_failed`; the dialog traps focus, restores it to the trigger and closes on ESC; `RetryCountdown` is not
announced every second; contrast of the error/alert colours ≥ 4.5:1 in both themes; the countdown and the banner are
readable at 360 px without horizontal scroll and honour `prefers-reduced-motion`.

## 7. Jobs and integrations

No background job, no queue, no worker interaction.

Timers and listeners owned here:
- `ActivityTracker`: passive DOM listeners (`pointerdown`, `keydown`, `wheel`, `touchstart`) registered in
  `start()` and removed in `DestroyRef.onDestroy`; the 1/10 s throttle is a timestamp comparison, not a timer; a
  10 s `tick` signal (one `setInterval`) exists only to let `isActive()` be a `computed` — it is cleared in
  `DestroyRef`.
- `RetryCountdown`: one 1 s `setInterval`, cleared in `DestroyRef`; it never polls the server.
- No refresh timer (D7, README.md:139-147), no polling here (`visible-poll` belongs to D17 consumers), no
  `setTimeout` on session end (the redirect is immediate).

Integration points published for others:
- `ActivityTracker.isActive()` / `isVisible` → 10.2.4-T1 (gate reconnect) and `core/state/visible-poll.ts`
  consumers (10.4.3-T2) — a poll must not run while idle (D7, D17).
- `SessionStore.status` literal union for consumers (pinned with 10.2.4-T1 on 2026-09-24):
  `'anonymous' | 'restoring' | 'authenticated' | 'refreshing' | 'ended'`. A consumer stops its own resource
  (hub, poll) only on `'anonymous'` or `'ended'` and (re)starts it on `'authenticated'`; `'restoring'` and
  `'refreshing'` are **transient** and must never be read as a sign-out — a pro-active token refresh would
  otherwise tear down the hub every few minutes.
- `SessionStore.ensureFreshAccessToken({ userAction })` → 10.2.4-T1 hub ticket connect (D8, background: it passes
  `userAction: false` and refreshes only while `isActive()`) and 10.3.3-T3 re-auth (user action).
- `SessionStore.logout()` (WR8) → 10.2.6-T1 user menu (no `returnUrl`); `SessionStore.mode` +
  `beginEnrolment(token)` → 10.3.3-T3/10.3.3-T1 (the enrolment-only flow walks `/me/mfa/setup` →
  `/me/mfa/verify` → `signIn`); `anonymousGuard` is wired on the auth routes by 10.3.3-T1.
- `SessionStore.me` (`MeSummary | null`) + `setMe()` → 10.2.6-T1 shell header (`displayName`, `email`, `role`,
  `uiLocale`) and 10.2.5-T1's `LocaleService`; `SessionStore.hasFeature(key)` → 10.2.6-T1 shell and flag-gated
  blocks (D14). The shell owns the `GET /me` request; this folder only holds and clears the view.
- `SESSION_SCOPED` → every root-level feature store of 10.4.x/10.5.x registers `reset()`.
- `AppErrorMapper`, `bindFieldErrors`, `FormErrorAlert`, `RetryCountdown`, `ConflictDialog` → 10.3.3-T1,
  10.4.2-T1/T2, 10.4.3-T1/T2, 10.5.x.
- `IdempotencyKeys` / `withIdempotency` → 10.5.2-T1 (generate) and any later paid command.

## 8. Security and privacy

- **Token handling.** Access token only in the `SessionStore` signal (memory); never written to
  `localStorage`/`sessionStorage`/cookies/indexedDB, never in a URL, never in the hub query string (D6, D8,
  README.md:139). The refresh cookie is `HttpOnly` and never read by JS (09.3-auth/plan.md:70-72).
- **No secret content in logs or errors.** The interceptor logs nothing by default; on a mapped error only
  `code`, `status` and `traceId` reach the dev console (never `problem.detail`, never a request body or an
  `Authorization` header). Playwright fails on any console message containing a token-shaped string (10.1.6-T1
  fixture) and the store test asserts the token is absent from `localStorage`/`sessionStorage` after a full login
  cycle.
- **Single flight is a security control (WR10).** Two parallel refreshes with the same cookie are reported by the
  server as theft (`refresh_token_reused` → family revoked, `09.3.2-T1:136-138`, `:201`). `ensureFreshAccessToken()`
  therefore coalesces the in-process callers on one promise **and** runs the refresh inside
  `navigator.locks.request('nts-refresh', …)`, re-checking the token after acquiring the lock, so a second tab never
  presents the rotating cookie; a regression test asserts exactly one `SessionApi.refresh()` call for N concurrent
  401s and for two tabs sharing a fake lock.
- **Open redirect.** `sanitizeReturnUrl(raw)` implements `^/(?![/\\])[^\s]*$`, rejects absolute URLs, protocol
  -relative `//`, backslashes, whitespace/newlines, `javascript:`, and the auth pages (`/login`,
  `/reset-password`, `/verify-email`, `/forgot-password`), and falls back to `/dashboard` (README.md:147).
- **Origin / CSRF.** Cookies are `SameSite=Strict` and the API checks `Origin` against `App:PublicOrigin`; the
  client adds nothing (no custom CSRF header, no `withCredentials`) and must not send the cookie-carrying calls
  from another origin (D3, `09.3-auth/plan.md:72`).
- **XSS.** Error text is interpolated (`{{ }}`) only; `detail` and any server-supplied HTML are never bound with
  `[innerHTML]`, and `bypassSecurityTrust*` remains lint-forbidden (D2, D4). `errors[]` from `keywords_flagged` is
  rendered as structured rows `{segment, index, rule}` mapped through `errors.keywords_flagged.rules.<rule>` (key family superseded by 10 CR-54: `errors.keywordsFlaggedRule.<rule>`, PR #181), never
  as markup.
- **Idempotency.** A UUID per user intent, 36 chars (within the API's 1–128 printable-ASCII rule), regenerated only
  when a new intent starts; it is not a capability and is safe to send (no secret in it), but it is still never
  logged. GETs and unmarked requests carry no header so the server's `RequireIdempotencyKey` filter keeps its
  meaning.
- **Enrolment-only session.** The enrolment token is memory-only (`SessionStore.enrolmentToken`, cleared by
  `signIn`/`signOut`/`reset`, WR8) and is never persisted or logged; in enrolment mode `status` is not
  `'authenticated'`, so no user-scoped request (`GET /me`, hub, feature stores) can run.
- **No client re-auth state.** The server owns `reauthenticated_at`; the client never calls a client-side
  `markReauthenticated` (WR8) — `reauth_required` is answered by re-running the store action after `ReauthDialog`.
- **ETag.** Where a page sends `If-Match` it uses `withIfMatch(etag)` from 10.1.4-T1 with the exact strong ETag the
  server issued; `*` is never sent (it is not matched, `09.1.4-T1:ETags`). A stale ETag surfaces as
  `kind: 'conflict'` and opens `ConflictDialog`, never a silent overwrite.
- **Storage ban.** This folder adds no `localStorage`/`sessionStorage`/`document.cookie`/`document.write`/`eval`
  use (D2 lint), and no `any` (typed `AppError` end to end).

## 9. Test plan

Unit/component (Vitest via `@angular/build:unit-test`, jsdom, TestBed zoneless, hand-written fakes; no mocking
library — D15):

| Area | Spec (proposed path) | Cases |
|---|---|---|
| Mapper | `web/src/app/core/errors/app-error-mapper.spec.ts` | ProblemDetails → `AppError`; `errors` regrouping per field; `validation_failed` vs `story_invalid_content`; unknown code → `unknown_error` + `traceId`; missing code but 400 → `validation_failed`; 401 `refresh_token_reused` → `kind: 'auth'`; 412 → `conflict`; 429 + `Retry-After: 30` → `retryAfterSec: 30`; 428 → `client_bug`; 413 → `payload_too_large`; `HttpErrorResponse` with `status: 0` → `network_error`; HTML body (proxy 502) → `server`; `detail` never copied into a message key |
| Binder | `web/src/app/core/errors/bind-field-errors.spec.ts` | flat `title`; nested `segments.2.text` (D18 segment address); `FormArray` index remap after reorder; unmapped key returned and counted; errors on a control that does not exist yet (added later) are returned unmapped, not swallowed |
| Store | `web/src/app/core/session/session-store.spec.ts` | boot restore success/failure; `signIn` sets `status`; `ensureFreshAccessToken` returns the in-flight promise (one port call for 5 concurrent calls) inside `RefreshLock`; refresh when `expiresAt − now < 30 s`; a background refresh while `isActive()` is false makes no call, `userAction: true` (WR11) always refreshes; the token is re-checked after acquiring the lock (WR10); refresh failure → `signOut(reason)` + every `SESSION_SCOPED.reset()` called once; `beginEnrolment` keeps `mode: 'enrolment'` and `status` out of `'authenticated'`; `logout()` ends without `returnUrl`; `anonymousGuard`; `hasFeature`; token never in storage |
| Interceptor | `web/src/app/core/session/session.interceptor.spec.ts` (HttpTestingController) | `Bearer` (`authHeader()`) attached to `/api/v1/**`; not attached to the 4 excluded auth routes; WR9: retryable 401 (no code / `unauthenticated` / `invalid_token` challenge) → one refresh + one replay, all eight pass-through codes → no replay, a replay that 401s again → session end; `/auth/*` never replayed; WR11: `USER_ACTION` decides the refresh while idle; the refresh call itself is `silent` and not retried; the replayed request keeps body/headers and the `Idempotency-Key` |
| Return URL | `web/src/app/core/session/return-url.spec.ts` | accepts `/episodes/1?tab=x`; rejects `//evil`, `/\evil`, `https://evil`, `javascript:`, whitespace variant, `/login`, `/reset-password` → `/dashboard` |
| Activity | `web/src/app/core/session/activity.spec.ts` (fake clock) | throttle 1/10 s; window 25 min admin / 13 d user; `visibilitychange` → `isVisible`; `stop()` removes listeners |
| Idempotency | `web/src/app/core/http/idempotency-keys.spec.ts` (+ interceptor spec) | `begin()` → UUID; two rapid calls in one intent → same header value; `settle()` then a new intent → different key; unmarked request → no header; 401-retry keeps the same key; `idempotency_key_required` mapping |
| Components | `web/src/app/shared/ui/{form-error-alert,retry-countdown,conflict-dialog}/*.spec.ts` | alert shows the `errors.<code>` key + traceId footer; unmapped-keys summary; countdown emits `elapsed` at 0 and clears its interval; dialog emits the three outputs, ESC = `cancel`, focus restored |

E2E (Playwright, `web/e2e/session.spec.ts`, compose stack, `workers: 1`, `@axe-core/playwright`; the specs of
10.1.6-T1 add the CSP/Trusted-Types fixture):
- reload after login restores the session without a second login (one `/auth/refresh` on boot, asserted by
  `page.route` counting);
- a burst of 5 parallel API calls after token expiry issues **one** `/auth/refresh` (route counter) and all 5
  succeed;
- server-side revoke (revoke the family through the API/test hook) → next request lands on
  `/login?returnUrl=…`, the banner carries `errors.session_revoked`;
- logout returns to `/login` and a reload stays anonymous;
- `?returnUrl=//evil.example` lands on `/dashboard` after sign-in (open-redirect regression);
- axe scan on `/login` (the D9 alert regions are the a11y surface of this folder) and no CSP violation event;
- **two-tab WR10 coverage** (only the Playwright suite can do it — jsdom has no Web Locks and the unit spec stubs
  `navigator.locks` with a `FakeLock`): two `page`s in one browser context (one cookie jar) load a guarded route
  after the access token expires and the route counter asserts **exactly one** `POST /api/v1/auth/refresh`.

Fakes: `web/src/app/core/session/testing/fake-session.api.ts` (scriptable `refresh()` — success / 401 code /
network error / deferred promise for the single-flight test) and
`web/src/app/core/testing/fake-idempotent-repository.ts` (records the key per call). No HTTP in store tests.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 10.2.2-T1 | `AppError` mapper + form error binding | S (0.5) | 10.1.4-T1, 10.2.5-T1, 09.1.4-T1 |
| 10.2.1-T1 | Session store, single-flight refresh, guards, activity gate | M (1.0) | 10.1.4-T1, 10.2.2-T1, 09.3.2-T1 |
| 10.2.3-T1 | Idempotency-Key per user intent | XS (0.25) | 10.2.1-T1 |

Raw 1.75 d; matches README §8.1 rows and `plans/10-web-ui.md` §6 (M + S + XS = 1.75). No re-estimate: the three
sizes are unchanged by this detailing pass. Blocks (from §8.1): 10.2.2-T1 → 10.2.1-T1, 10.3.3-T1, 10.4.2-T2;
10.2.1-T1 → 10.2.3-T1, 10.2.4-T1, 10.2.6-T1, 10.3.3-T1; 10.2.3-T1 → 10.5.2-T1.

Cross-folder notes (no CR needed — the sibling folders list these ids already):
- `10.2.5-T1` (i18n, in `10.2.4-realtime-i18n-shell/`) must register the `errors` scope and include it in the
  key-parity check; the `errors` scope and the `core.session.*` leaves are owned by 10.2.2-T1 / 10.2.1-T1
  (namespace agreed with the sibling folder on 2026-09-24).
- `10.2.4-T1` consumes `ActivityTracker.isActive()`, `SessionStore.ensureFreshAccessToken()`/`status`, and must
  call `SessionStore.signOut('revoked')` when its post-close refresh fails (D8).
- Seam agreed with `10.2.4/10.2.5/10.2.6` on 2026-09-24 (peer message, recorded here so the contract is not
  renegotiated): i18n ownership is `errors/*` (separate scope, 10.2.2-T1) + `session.*` inside the core
  `{vi,en}.json` scaffold that 10.2.5-T1 creates with `common.*` only; 10.2.6-T1 consumes `SessionStore.me`,
  `hasFeature`, `status` and does not define them; the hub stop on sign-out is 10.2.4-T1's subscription to
  `SessionStore.status` (this folder exposes no extra observable).
- `10.1.4-T1` owns `core/http/withIfMatch.ts`, `readEtag` and the generated `@shared/api`. `10.2.1-T1` creates
  `core/http/http-context-tokens.ts` (`SILENT_REQUEST`, `USER_ACTION`) and `core/http/http-context.ts`
  (`silently()`, `userAction()`); `10.2.3-T1` extends both additively (`IDEMPOTENCY_INTENT`, `withIdempotency()`) and
  adds
  `core/http/{idempotency-keys,idempotency.interceptor}.ts`.
- 10.5.2-T1 must pass an `IdempotencyIntent` into its generate repository call: `POST
  /api/v1/episodes/{id}/story/generate` **requires** the `Idempotency-Key` header — "required (max 128 chars);
  missing → 400 `idempotency_key_required`"
  (`02.2.2-generate-story-job/02.2.2-T2-generate-story-command-endpoint.md:28`, `:114`; route table
  `02.2.2-generate-story-job/plan.md:402`, semantics `:268`). No hedge: the header is asserted unconditionally.
- `[OPEN]` cross-plan gap (CR candidate for 09, not applied here): `GET/PATCH /me` returns 401 `unauthenticated`
  (`09.3.4-T1-me-endpoints.md:49`, `:52`, `:100`) but the code is absent from the 09 §5.4 catalogue and from
  `09.1.4-T1`'s `ErrorCodes` (which has `unauthorized`). Owner: 09 (constant + catalogue row); until then the client
  maps `unauthenticated` to `errors.session_revoked`.
- `[INFERENCE]` 02 job-outcome codes (`story_generation_failed`, …) are not HTTP codes and get no `errors.<code>`
  key here; 10.5.x maps `generation_runs.error_code` through its own scope.

## 11. Rollout

Phase P, milestone M1, no feature flag (README.md:277 §8.1 "Phase" column is `P` for all three tasks; D14 flags are
not involved).

Order and integration:
1. 10.2.2-T1 first (mapper + binder + the three shared components + `errors` scope): it is the only hard input that
   10.2.1-T1 needs besides 10.1.4-T1 and the 09.3.2-T1 backend route.
2. 10.2.1-T1 next: `provideAppInitializer` restore + interceptor + guards + `ActivityTracker`; wire
   `provideSession()` into `app.config.ts` (10.1.1-T1's file, one added provider line recorded in the task Outputs)
   and `sessionInterceptor`/`idempotencyInterceptor` into the `provideHttpClient(withInterceptors([...]))` chain
   (idempotency **before** session so a retried request keeps its key).
3. 10.2.3-T1 last: additive, no consumer until 10.5.2-T1.

Gate `10-W1-session` (README.md:256-261): with the 10.3.3-T1/T3 screens in place the owner logs in with password +
TOTP, reloads (session restored), is signed out after the server revokes the family, reconnects the hub after a
backstop close without a refetch storm, and realtime pauses when idle. This folder's own exit criteria: the
folder's Vitest specs green, `web/e2e/session.spec.ts` green with axe clean, one `/auth/refresh` per boot and per
burst, no token in any storage, and both languages rendering every key it owns.

Rollback: no data, no migration; reverting the commit removes the providers and the app falls back to anonymous
pages (login remains usable because `auth` owns its own calls). Backend prerequisites: 09.1.4-T1 and 09.3.2-T1
merged for the e2e acceptance; with only 10.1.4-T1 + a contract-true fake the unit/component tests still run
(soft edge).
