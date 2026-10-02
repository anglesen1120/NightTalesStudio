# 10.7.1 — Channels page: connect (re-auth + legal gate), callback result, disconnect (web UI wave 2)

> Refines `../../10-web-ui.md` 10.7.1 and `../README.md` Part B. Contract: wave-1 D1–D18 + WR1–WR17 still apply; wave-2 facts WC1–WC10, findings WB1–WB8, decisions WD1–WD10 and rulings WT-W1–WT-W4 are the additions used here.
> Folder: `plans/features/10-web-ui/10.7.1-channels/` — this file + `10.7.1-T1-channels-connect.md`.
> Status: ready for implementation; **all** open items are listed in §10 with a fallback that needs no file outside this folder.

## 1. Scope

**Story in.** 10.7.1-T1 — the owner-facing **Channels** page: list the owner's YouTube connections, start a connect, render the callback outcome, and disconnect. It is the first P task of the new `publishing` feature (WD1) and the only task in this folder.

**User value.** A YouTube upload needs an `active` channel connection (`06.2.1-T1` requires one to create a publication). Today nothing in the SPA can create one: the connect has to be started from the API host by hand. After this task the owner connects a channel from the UI (with the re-auth and legal gates that the API enforces), sees exactly what the callback decided, and can revoke the connection in-app — which is also a YouTube policy duty (DECISIONS §10.6:277 "offer a clear, easy in-app way to revoke consent").

**In scope.**
- Route `/channels` (authenticated), lazily loaded inside the new `features/publishing` feature, plus its `NAV_ITEMS` entry and its `channels` i18n scope.
- `GET /api/v1/channels` list with statuses `active`/`revoked`/`expired`/`error`.
- Connect: `POST /api/v1/channels/youtube/connect` → `{ authorizationUrl, expiresAt }` → in-page handling of 401 `reauth_required` (via 10.3.3-T3's `ReauthService`) and 409 `legal_acceptance_required` (via 13.2.1-T1's dialog) → retry once → origin-checked top-level `window.location.assign(authorizationUrl)` (WD5, WD10).
- The callback **result** as a client-side outcome: the API's `302 /channels?connected=youtube` / `?error=<oauth_state_invalid|oauth_denied|oauth_exchange_failed|channel_already_connected>` is read once, shown once, and the query string is replaced out of history (WB5, WD5).
- Disconnect: `DELETE /api/v1/channels/{id}` behind 10.1.3-T1's `ConfirmDialog`, with the revoke + 7-day-deletion copy and the Google permissions link (WD5, DECISIONS §10.6:277).
- Vitest suites with hand-written fakes + `web/e2e/channels.spec.ts` (Playwright + axe), run by 11.5.1-T1's `e2e.yml` (WR3/WR14).

**Out of scope (explicit non-goals).**
- The publish form, consent step, publication status widgets, cancel and the veto e-mail pages — 10.7.2-T1, 10.7.4-T1, 10.7.4-T2 (this task blocks 10.7.2-T1 and 06.5.1-T1).
- Any backend work: `POST …/connect`, the callback, `GET /channels`, `DELETE /channels/{id}`, the re-auth guard, the legal gate, the `__Host-nts_oauth` cookie and the audit/outbox rows are 06.1.2-T1/T2 and 06.4.1-T1 (already signed).
- TikTok, multi-client settings, per-client counts (06.1.2-T3, S) and the admin channel view — out.
- Client polling of a channel's health: 06.4.2-T1's `TokenHealthJob` is the server's; the page shows only the status it reads.
- The 10.10.2-T1 journeys (that task consumes this page).

**Links to the high-level plan.** `../../10-web-ui.md` 10.7.1, §6/§11 rows; `../README.md` §B0 folder list, §B1 WC8, §B2 WB5, §B3 WD1/WD5/WD10, §B8.1 row for 10.7.1-T1.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| F1 | `GET /api/v1/channels` (bearer, `RequireFeature("youtube_publish")`) → `200 { items: ChannelConnectionDto[] }` ordered `connected_at DESC`, one SQL command, no paging, **no** ETag/`If-Match`, no token and no `oauth_client_id` in the projection. `ChannelConnectionDto { id, platform, externalAccountId, displayName, status, lastVerifiedAt, connectedAt }`; statuses `active`/`revoked`/`expired`/`error`. Flag off → `404 feature_disabled`. | `06-youtube-posting/06.1-connect/06.1.2-T2-channel-connection.md:96,111-114,125,160-161` ✅ |
| F2 | `POST /api/v1/channels/youtube/connect` (bearer, flag, rate policy `auth`) → `200 { authorizationUrl, expiresAt }`; filter order flag → authorization → rate limit → `RequireReauth(5 min)` → legal gate in the handler. Errors: `401 reauth_required` (no state row written), `409 legal_acceptance_required` with `errors.documents = ["tos","privacy"]` (sorted), `404 feature_disabled`, `404 not_found` (another platform), `429 rate_limited`, `503 service_unavailable` (client secret unreadable). **Deliberately not idempotent** — every call mints a fresh state; no `Idempotency-Key` and no ETag on this route. | `06.1-connect/06.1.2-T1-connect-callback.md:173,216-221`; `06.1-connect/plan.md:189-196,229,237` ✅ |
| F3 | The `200` response sets `Set-Cookie: __Host-nts_oauth` (HttpOnly, `Secure`, `SameSite=Lax`, `Path=/`, `Max-Age=600`) and the body is exactly `{ authorizationUrl, expiresAt }`; the nonce is never in the body, the URL or a log line. The callback requires that cookie (constant-time compare) and clears it on every outcome. | `06.1-connect/06.1.2-T1-connect-callback.md:169-171,225-228`; 06 README YR10:267 ✅ |
| F4 | `GET /api/v1/channels/youtube/callback?code&state` is a top-level browser navigation (anonymous; authenticated by the single-use state row **plus** the browser nonce). Success → `302 Location: /channels?connected=youtube`; the four failure outcomes → `302 Location: /channels?error=<oauth_state_invalid\|oauth_denied\|oauth_exchange_failed\|channel_already_connected>`. Both targets are constants; `Cache-Control: no-store` + `Referrer-Policy: no-referrer` on the 302 and the failure redirects; the `code`/`state`/`error` values are never interpolated into `Location`. | `06.1-connect/06.1.2-T1-connect-callback.md:158,174,212`; `06.1-connect/plan.md:240-243` ✅ |
| F5 | `oauth_state_invalid` and `channel_already_connected` are also **catalogue** codes; `oauth_denied` and `oauth_exchange_failed` exist **only** as redirect query values (YR5). | `06.1-connect/plan.md:223,247`; 06 README YR5:262 ✅ |
| F6 | `DELETE /api/v1/channels/{id}` (bearer, flag) → `204`; idempotent, **no** `If-Match`, no re-auth required (D15); Google revoke first (3 attempts, 250 ms/1 s/4 s + jitter, `AlreadyRevoked` = success, exhausted → tokens wiped anyway + metric + audit `failed`), tokens nulled, `status = revoked`, `awaiting_consent`/`scheduled` publications cancelled with a quota release only where `platform_payload.quotaReservedDay` was set, and a 7-day `data_deletion_requests` row (`scope = channel`, `reason = in_app_revoke`). Another user's id → `404`. Flag off → `404 feature_disabled`. | `06.4-revocation/06.4.1-T1-disconnect.md:6,53-64,101-105,199` ✅ |
| F7 | 06 raises **no** SignalR frame for a channel change: the successful callback writes the outbox row `channel_connection_changed.v1 { connectionId, userId, status }` (consumers: the 02 orchestrator and the UI's own refetch) and its comment says the redirect + refetch covers the browser. `RealtimeData` stays reserved for `RenderProgress` (WT54). | `06.1-connect/plan.md:249-254`; 00 §8 ✅ |
| F8 | 06 hands over the page contract and an **[INFERENCE]** copy-key list; "10 owns the final names": `channels.connect`, `channels.connected` ("YouTube channel {title} connected"), `channels.error.oauth_state_invalid`, `channels.error.oauth_denied`, `channels.error.oauth_exchange_failed`, `channels.error.channel_already_connected`, `channels.error.feature_disabled`, `channels.reauth_required`, `channels.legal_acceptance_required`; "no token ever appears in a URL fragment on this page". | `06.1-connect/plan.md:262-267` ✅ |
| F9 | `ReauthDialog`/`ReauthService` are 10.3.3-T3's (WR4): root-provided `ReauthService.ensureReauthenticated(): Promise<Result<void>>`, single-flight (concurrent callers share one dialog and one `POST /auth/reauth`), resolves `ok()` on `200 { reauthenticatedAt }` and **writes no client state** (WR8); `session_revoked` → `err` and 10.2.1-T1 ends the session after the 5th wrong attempt; the dialog has its own error input and no `scrolledToEnd` gate. | `10-web-ui/10.3-auth-legal/10.3.3-T3-mfa-enrolment-reauth.md:76-85,138-141,205-206` ✅ |
| F10 | `LegalAcceptanceDialog` (blocking `MatDialog`, checkbox-gated, `disableClose: true`) and its store (`acceptance`, `pending`, `missing`, `refresh()`, `accept(tos, privacy)`) are 13.2.1-T1's; its `index.ts` exports both, and the hand-off is that **10.2.2-T1's mapper** opens it on `legal_acceptance_required`/`legal_version_outdated` and re-runs the blocked action on success; the dialog never parses actions out of the problem body. Copy keys `account.legal.*`; error keys `errors.legal_acceptance_required`, `errors.legal_version_outdated`. | `13-compliance/13.1-legal-acceptance/13.2.1-T1-…md:90-110,256-261` ✅ |
| F11 | Shell: `core/shell/nav-items.ts` ships `NAV_ITEMS: NavItem[] = []` and "each **screen task** appends its own entry in the same PR that adds its child route (10.4.1-T1 Dashboard, 10.4.2-T1 Series, 10.5.1-T1 Episodes; S tasks 10.7–10.9 add Channels/…)" — i.e. an entry must carry its own `visible()` and never create a dead link; `shell.store` filters `NAV_ITEMS.filter(i => i.visible())` and sets the document title from the route's `data.titleKey`. | `10-web-ui/10.2.4-realtime-i18n-shell/10.2.6-T1-shell.md:27,56,69,100-102` ✅ / ⚠️ conflict with `10.4.1-T1:39` ("the shell nav entry is 10.2.6-T1's") → §10 [OPEN] 1 |
| F12 | Wave-1 wave-2 back-edges are already applied to `../README.md` §8.1: `10.2.6-T1` and `10.3.3-T3` both list `10.7.1-T1 (10 CR-15)` in **Blocks**, so this task's `Depends on` is upstream-consistent. | `../README.md:210,229` (§8.1 rows) ✅ |
| F13 | Feature layout conventions in this repo: `features/<name>/{domain,data-access,application,ui,feature}`, `<name>.routes.ts`, `index.ts`; pages live in `feature/<slug>-page.ts`, presentational pieces in `ui/`; `app.routes.ts` gets the lazy route; the task that adds the route also adds its i18n file. | `10-web-ui/10.4-series-autopilot/10.4.1-T1-dashboard.md:37-39,66-69`; `…/plan.md:31,273-277`; `10.5.1-episodes-editor/10.5.3-T1-…md:38-40` ✅ |
| F14 | The API's dev origin is `http://localhost:4200` (D3), so the SPA navigates cross-origin to Google and back; `__Host-` + `Secure` cookies are accepted on `localhost` in Chromium/Firefox ⚠️ (10.1.1-T2 verifies it). No CSP directive restricts a top-level navigation in the D4 header (`form-action 'self'` governs forms, `default-src 'none'` does not govern navigation; there is no `navigate-to`) ⚠️ confirmed by the e2e CSP fixture. | `../README.md:117-121` (D3), `:141` (D4) ✅/⚠️ |
| F15 | Google's own revoke page (`https://security.google.com/settings/security/permissions`) is the user-facing revocation channel the platform policy requires us to advertise, and an in-app revoke must revoke at Google immediately and delete related data within 7 days. | `DECISIONS.md:273-278` ✅ |
| F16 | Wave-1 shared components that exist for this page (owner per WR4): `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `StatusChip`, `ConfirmDialog` → 10.1.3-T1; `AppErrorMapper`/`AppError`/`Result<T>`/`RetryCountdown`/`FormErrorAlert` → 10.2.2-T1; `hasFeature(key)` on `core/session` (D14) → 10.2.1-T1/T2; `authGuard` → 10.2.1-T1; Transloco scopes → 10.2.5-T1. `IdempotencyKeys` is 10.2.3-T1's, `visible-poll` is 10.2.6-T1's, `safe-filename`/`download-blob` are 10.1.6-T1's. | `../README.md:168,246-260` (D11, WR4, WR5); `:214` (D14) ✅ |

## 3. Domain and data

**Feature placement (WD1).** New feature `web/src/app/features/publishing/` with the D2 five folders; the wave-1 lint rules apply unchanged (D2): `domain` imports no `@angular/*`/`rxjs`, `ui` injects no store/repository/`HttpClient`, only `data-access` imports `@shared/api`, cross-feature imports only through `index.ts`, no `any`, every component `OnPush`, no `subscribe()` in components, no `bindings`/`innerHTML`/`localStorage`.

```
web/src/app/features/publishing/
  domain/channel.model.ts               Channel, ChannelStatus, ConnectStart, ChannelResult (client-only)
  domain/channel.repository.ts          abstract class ChannelRepository        ← the port
  data-access/channel.http-repository.ts ChannelHttpRepository (HttpClient + firstValueFrom)
  data-access/channel.mapper.ts         DTO → Channel (explicit function, camelCase wire)
  application/channels.store.ts         ChannelsStore (route-provided, D13)
  ui/channel-row.ts                     presentational row (inputs/outputs only)
  ui/channels-result-banner.ts          presentational result banner (inputs/outputs only)
  feature/channels-page.ts              the routed container (the only component that injects the store — a `ui/` file may not, D2/README:110)
  testing/fake-channel.repository.ts    hand-written fake (D15: no mocking library)
  publishing.routes.ts                  exports PUBLISHING_ROUTES; /channels (authGuard, providers, data.titleKey)
  index.ts                              exports ChannelsStore, ChannelRepository, Channel, PUBLISHING_ROUTES
```

**Models ([INFERENCE]: file/feature names are new — no signed plan pins them; the *wire* shapes are F1–F6 verbatim).**

```ts
// domain/channel.model.ts  — mirrors ChannelConnectionDto exactly (no extra wire fields, D2/D5)
export type ChannelStatus = 'active' | 'revoked' | 'expired' | 'error';
export interface Channel {
  readonly id: string;                      // Guid
  readonly platform: string;                // 'youtube' in P
  readonly externalAccountId: string | null;
  readonly displayName: string | null;      // null after 13.3.1-T1 purges the row (06.1.2-T2-channel-connection.md:198)
  readonly status: ChannelStatus;
  readonly lastVerifiedAt: string | null;   // ISO-8601 UTC
  readonly connectedAt: string;
}
export type ChannelResult =
  | { readonly kind: 'connected' }
  | { readonly kind: 'error'; readonly code: ChannelResultErrorCode };
export type ChannelResultErrorCode =
  | 'oauth_state_invalid' | 'oauth_denied' | 'oauth_exchange_failed' | 'channel_already_connected';
export interface ConnectStart { readonly authorizationUrl: string; readonly expiresAt: string; }
```

The callback query values are a **closed set of four** (F4/F5). An unknown `error=` value is ignored entirely — it is neither rendered nor turned into a copy key (a server-only addition must ship copy in a later task, not a raw string in the DOM).

**Port (abstract class, D5).** `abstract class ChannelRepository { abstract list(): Promise<Channel[]>; abstract connectYouTube(): Promise<ConnectStart>; abstract disconnect(id: string): Promise<void>; }`. The repository returns `Promise<T>` via `firstValueFrom`, never an `HttpResponse`, and lets `HttpErrorResponse` reach `AppErrorMapper` (10.2.2-T1). It unwraps the `{ items: [...] }` envelope and maps `status` defensively: an unforeseen status string is mapped to `'error'` (never crashes the list) — [DECISION], because `ChannelStatus` is a client-side narrowing of a server column whose `CHECK` is `06.1.2-T2`'s.

**Store (`application/channels.store.ts`, `@Injectable()` provided by the route, D13).**

```ts
export class ChannelsStore {
  readonly channels      = signal<Channel[]>([]);
  readonly status        = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  readonly error         = signal<AppError | null>(null);      // page-level load failure only
  readonly connectState  = signal<'idle' | 'starting' | 'navigating'>('idle');
  readonly disconnecting = signal<string | null>(null);        // channel id
  readonly actionError   = signal<AppError | null>(null);      // connect/disconnect failure, non-blocking
  readonly result        = signal<ChannelResult | null>(null);  // callback outcome, read once
  readonly featureEnabled = computed(() => this.session.hasFeature('youtube_publish')); // D14

  load(): Promise<Result<void>>;                                 // GET /channels
  connectYouTube(): Promise<Result<'navigating' | 'blocked'>>;   // POST connect → URL allow-list (WD10/WT-W13) → assign
  disconnect(id: string): Promise<Result<void>>;                 // DELETE /channels/{id}
  readCallbackResult(query: { connected: string | null; error: string | null }): void; // once, then clear
  dismissResult(): void;
}
```
Rules: no optimistic state (a new channel appears only after the reload that follows `connected=youtube`); every method is `USER_ACTION` (WR11); no `localStorage`/`sessionStorage`; no timers (D13); `error` (blocking) and `actionError` (non-blocking) are separate signals so a failed disconnect never blanks the list.

**Reused, deliberately not used.**

| Concept | Verdict | Evidence |
|---|---|---|
| `AppErrorMapper` (10.2.2-T1) | **used** — every HTTP failure becomes an `AppError` | D9 |
| `ConfirmDialog` (10.1.3-T1) | **used** for disconnect (no `cost-line` — P has no credit copy, D14) | WR4; D14 |
| `ReauthDialog`/`ReauthService` (10.3.3-T3) | **used** on `401 reauth_required` | F9 |
| `LegalAcceptanceDialog` (13.2.1-T1) | **used** on `409 legal_acceptance_required` | F10 |
| `IdempotencyKeys` (10.2.3-T1) | **not used** — connect is deliberately non-idempotent and `DELETE` is idempotent by contract; no header on either route | F2, F6 |
| `ConflictDialog` / `If-Match` | **not used** — this folder has no mutable resource a client may conditionally write; `GET /channels` has no ETag | F1, F2 |
| `visible-poll` (10.2.6-T1) | **not used** — D17 limits polling to the autopilot run log, WR16 adds the veto tick; no channel poll exists | D17, WR16, F7 |
| `RealtimeService.subscribeEpisode` (10.2.4-T1) | **not used** — 06 raises no SignalR frame for a channel change; the redirect + refetch is the whole mechanism | F7 |
| `keywordFlags`/`setKeywordFlags` (10.5.10-T1) | **not used** — keyword moderation gates the make-video action (WD3, 10.6.1-T2); channel connect has no moderation input | WR6, WD3 |
| `EpisodeDetailStore` (10.5.x) | **not used** — the channel list is this page's own store; the episode page's publication block is 10.7.4-T1's | WD7 |
| `safe-filename`/`download-blob` (10.1.6-T1) | **not used** — the page downloads nothing (the channel list has no export and the connect e-mail has no attachment) | D4; F1 |
| `RetryCountdown` (10.2.2-T1) | **used** for `429 rate_limited` on connect (state-level store owns the API) | D9 |

## 4. Use cases

Every use case lists its call, states and errors. All three calls carry the `USER_ACTION` `HttpContext` flag (WR11); the interceptor's 401 matrix passes `reauth_required`/`session_revoked` through unchanged (WR9) — `reauth_required` is handled here, `session_revoked` ends the session (D6).

**UC1 — open the page / load the list.**
- Trigger: navigation to `/channels` (route guard `authGuard`; the lazy `publishing` chunk loads first).
- Call: `GET /api/v1/channels` → `{ items }`, one per page entry; called again after every successful connect-return or disconnect.
- States: `status = 'loading'` → `LoadingState` skeleton; `'ready'` with `channels.length === 0` → `EmptyState` + primary Connect; `'ready'` with rows → list + Connect (`channels.connectAnother` when at least one row exists); `'error'` → `ErrorState` with `errors.<code>` and a Retry action that re-runs `load()`.
- Errors: `404 feature_disabled` → hide the block, render the generic feature-off state `errors.feature_disabled` and stop retrying (D14; §10 [OPEN] 3 records the missing `/me` refresh); `429 rate_limited` → `RetryCountdown` from `Retry-After`, Retry disabled until 0; `network_error`/5xx/`unknown_error` → `ErrorState` (trace id shown from `AppError.traceId`, D9).
- Edge: the list is **not** cached between visits (D13/F23) — a returning owner always sees the current status.

**UC2 — start a connect.**
- Preconditions: `featureEnabled()` true; the Connect button enabled and not `connectState = 'starting'`.
- Call: `POST /api/v1/channels/youtube/connect` (no body, no `Idempotency-Key`, F2).
- Happy path: `200 { authorizationUrl, expiresAt }` → the store validates the URL (WD10 + **WT-W13**: `new URL(...)` with `protocol === 'https:'`, `hostname === 'accounts.google.com'`, `pathname === '/o/oauth2/v2/auth'`, no credentials, no explicit port, **and** the `redirect_uri` query parameter is the same-origin callback — its `origin === location.origin` and its `pathname === '/api/v1/channels/youtube/callback'`) → `connectState = 'navigating'` → `window.location.assign(authorizationUrl)` (top-level, WD5) so the API-set `__Host-nts_oauth` cookie (SameSite=Lax) rides the callback GET (F3). A URL failing the check is **not** navigated to: `actionError = <client guard>` + `channels.error.badAuthorizationUrl` (WD10).
- `401 reauth_required` → `await reauthService.ensureReauthenticated()` (F9); `ok` → retry the connect **once**; `err` → `actionError` (the dialog already showed the field error; `session_revoked` ends the session, `rate_limited` shows the countdown inside the dialog).
- `409 legal_acceptance_required` → **this page** opens 13.2.1-T1's dialog (the account feature's `LegalAcceptanceStore` + dialog through `@features/account/index.ts`) for the documents named in `AppError.fieldErrors['documents']` (sorted; the dialog refreshes `GET /me/legal` itself, F10) — the page owns this wiring because 13 hands it to 10.2.2-T1 (`13.1-legal-acceptance/plan.md:147-149`) while 10.2.2-T1's produced list has no legal branch (`10.2.2-T1-app-error-mapper.md:175-178`; [OPEN] 2); accepted → retry the connect **once**; not accepted → `actionError` with `errors.legal_acceptance_required` and the missing documents listed.
- Other errors: `409 channel_already_connected` → `actionError` with the same copy as the callback case (F5, F8); `404 feature_disabled` → as UC1; `404 not_found` → generic (only reachable if the route's platform changed); `429 rate_limited` → `RetryCountdown`, no automatic retry; `503 service_unavailable`/5xx/network → `actionError` with a Retry action.
- The retry is **exactly one** per gate (never a loop), and it is a fresh `POST` (a new state row is correct: the first failed attempt wrote none — F2).

**UC3 — the callback result.**
- Trigger: the browser lands on `/channels?connected=youtube` or `/channels?error=<code>` after Google → the API callback → the `302` (F4). The app boots normally (session restored by the app initializer, `authGuard` passes; if the refresh cookie was revoked the guard sends the user to `/login?returnUrl=/channels?...`, and the query survives that round trip — D6's `returnUrl` regex accepts it).
- Handling: read `ActivatedRoute.snapshot.queryParamMap` **once** in the page constructor, map to `ChannelResult` via the closed set (F4/F5), then `Location.replaceState('/channels')` (Angular's wrapper over `history.replaceState`) so the outcome is not in history and never re-renders on back/forward (WB5, WD5). `connected=youtube` additionally triggers `load()` so the new row is present and can name the channel in the banner (F8's `{title}`).
- States: `connected` → success banner `channels.connected` (with the top row's `displayName`; `channels.connectedNoTitle` if the list came back empty) + `aria-live` announcement; `error` → banner `channels.result.error.<code>`.
- Unknown `connected`/`error` values → ignored, URL still cleaned (never rendered).
- The result is shown once per landing: `dismissResult()` clears it; navigating away and back to `/channels` without query params shows nothing.

**UC4 — disconnect.**
- Trigger: the row's menu → `ConfirmDialog` (title/body/confirm copy in §6) which states the immediate Google revoke, the 7-day deletion and links the Google permissions page (F15).
- Call: `DELETE /api/v1/channels/{id}` → `204` (F6). `disconnecting = id` while in flight (row disabled, `aria-busy`).
- Success → `load()` and the `channels.disconnected` announcement in the live region.
- Errors: `404 not_found` (another user's row / already purged) → `load()` + `errors.not_found` — the list is the truth; `404 feature_disabled` → as UC1; `429`/5xx/`network_error` → `actionError` + Retry; a second identical `DELETE` is a no-op 204 by contract, so a double-submit is harmless (F6).
- The dialog cancel path issues no request and restores focus to the row menu trigger (D16).

**UC5 — flag off.** `featureEnabled()` false (or any response `404 feature_disabled`) → the page renders the generic feature-off state, no Connect/Disconnect actions, no retry loop, and the nav entry is hidden for the session (§11).

## 5. API and events

**Routes consumed (all under `/api/v1`, relative URLs — same origin, D3; no CORS, no `withCredentials`).**

| Verb | Path | Auth / flag | Client call site | Success | Errors handled here |
|---|---|---|---|---|---|
| GET | `/channels` | bearer + `youtube_publish` | `ChannelsStore.load()` | `200 { items: ChannelConnectionDto[] }` | `404 feature_disabled`, `429 rate_limited`, 5xx/`network_error` |
| POST | `/channels/youtube/connect` | bearer + flag + `RequireReauth(5 min)` + legal gate | `ChannelsStore.connectYouTube()` | `200 { authorizationUrl, expiresAt }` (+ `Set-Cookie: __Host-nts_oauth`) | `401 reauth_required`, `409 legal_acceptance_required`, `409 channel_already_connected`, `404 feature_disabled|not_found`, `429`, `503`, 5xx/network |
| GET | `/channels/youtube/callback?code&state` | anonymous (state + browser nonce) | **not called by the SPA** — the browser follows the `302` | `302 /channels?connected=youtube` | `302 /channels?error=<4 codes>` → UC3 |
| DELETE | `/channels/{id}` | bearer + flag | `ChannelsStore.disconnect(id)` | `204` | `404 not_found|feature_disabled`, `429`, 5xx/network |
| POST | `/auth/reauth` | session + `sid` family | **indirect** — 10.3.3-T3's `ReauthService` owns the call | `200 { reauthenticatedAt }` | `401 invalid_credentials|invalid_mfa_code|session_revoked`, `429` |
| GET/POST | `/me/legal`, `/me/legal/accept` | session (+ `Origin` check on POST) | **indirect** — 13.2.1-T1's store/dialog owns the calls | `200 LegalAcceptanceDto` | `409 legal_version_outdated`, `400 validation_failed` |

No `If-Match`, no `Idempotency-Key`, no paging, no ETag on any route this task calls (F1, F2, F6). `_api:gen_` output for these four routes lands through 10.1.4-T1's `@shared/api` import inside `data-access/` only (D5).

**Events.** None consumed and none produced. `channel_connection_changed.v1` is an outbox row written by the API (F7); the page's freshness comes from `load()` after its own action and on every navigation to the route. `RealtimeService` is not touched (F7) — no `subscribeEpisode`, no `on(...)`, no `resync` handler, and therefore no refetch storm concerns from D8.

## 6. UI

**Screens.** One routed page `/channels` (`features/publishing/feature/channels-page.ts` — **`feature/`, not `ui/`**: D2/`README:110` fails lint when a `ui` file injects a store, repository or `HttpClient`, and wave-1 containers sit in `feature/`, e.g. `features/dashboard/feature/dashboard-page.ts`, `10.4.1-T1:37`) + two presentational components (`ui/channel-row.ts`, `ui/channels-result-banner.ts`). The page injects `ChannelsStore`, `TranslocoService`, `Location`, `MatDialog` and 13.2.1-T1's legal store; the presentational components take inputs and emit outputs only (D2).

**States.**

| State | Rendering | Copy keys |
|---|---|---|
| loading | `PageHeader` + `LoadingState` skeleton (2 rows) | `channels.title` |
| empty (`ready`, 0 rows) | `EmptyState` + primary Connect | `channels.empty.title`, `channels.empty.body`, `channels.connect` |
| ready (≥ 1 row) | list of `channel-row` + Connect (`channels.connectAnother`) | `channels.title`, `channels.intro` |
| result banner | `channels-result-banner` above the list, `role="status"` + `aria-live="polite"`, `tabindex="-1"` focused on the callback landing | `channels.connected` / `channels.connectedNoTitle` / `channels.result.error.<code>` / `channels.result.dismiss` |
| connect in flight | Connect disabled, `aria-busy="true"`, spinner label; the page unloads on success | `channels.connecting` |
| action error | inline `alert` region (`FormErrorAlert`-styled, 10.2.2-T1) with `errors.<code>` + Retry | `errors.<code>`, `channels.retry` |
| blocking error (list) | `ErrorState` + Retry | `errors.<code>`, `common.retry` |
| feature off | `ErrorState` (generic) — no actions | `errors.feature_disabled` |

**`channel-row` inputs/outputs.** `@Input() channel: Channel`, `@Input() busy: boolean`; `@Output() disconnect = new EventEmitter<string>()`. Renders: `StatusChip` with the status (§copy below — text, never colour alone, D16), `displayName ?? channels.status.unknownChannel`, `channels.connectedOn {date}` and `channels.lastVerified {date}` (when non-null) via `Intl.DateTimeFormat` for `vi-VN`/`en-US` and an explicit time zone for the two dates (D10), `channels.platform.<youtube|other>`, the platform-neutral disable action (§copy hint for `expired`/`error`) and a `MatMenu` with the single item `channels.disconnect.action`, labelled `channels.disconnect.menuLabel` with the channel title. Disconnect is offered for `active`/`expired`/`error` (F6 step 2 handles any non-revoked row idempotently) and hidden for `revoked` (nothing left to revoke; the row is the record).

**Copy keys — scope `channels`, `web/src/assets/i18n/channels/{vi,en}.json` (vi = default, en; key style `scope.area.item`, D10).**

| Key | vi | en |
|---|---|---|
| `channels.title` | Kênh phát hành | Publishing channels |
| `channels.intro` | Kết nối kênh YouTube để đăng tập phim lên kênh của bạn. | Connect a YouTube channel to publish your episodes. |
| `channels.connect` | Kết nối YouTube | Connect YouTube |
| `channels.connectAnother` | Kết nối kênh khác | Connect another channel |
| `channels.connecting` | Đang chuyển tới Google… | Redirecting to Google… |
| `channels.empty.title` | Chưa có kênh nào | No channel yet |
| `channels.empty.body` | Bạn cần kết nối một kênh YouTube trước khi đăng tập phim. | Connect a YouTube channel before publishing an episode. |
| `channels.connected` | Đã kết nối kênh YouTube {title}. | YouTube channel {title} connected. |
| `channels.connectedNoTitle` | Đã kết nối kênh YouTube. | YouTube channel connected. |
| `channels.disconnected` | Đã ngắt kết nối kênh. | Channel disconnected. |
| `channels.disconnecting` | Đang ngắt kết nối… | Disconnecting… |
| `channels.disconnect.action` | Ngắt kết nối | Disconnect |
| `channels.disconnect.menuLabel` | Thao tác cho kênh {title} | Actions for channel {title} |
| `channels.disconnect.title` | Ngắt kết nối kênh YouTube? | Disconnect this YouTube channel? |
| `channels.disconnect.body` | Quyền truy cập sẽ bị thu hồi tại Google ngay lập tức và dữ liệu liên quan sẽ bị xoá trong vòng 7 ngày. Các tập đang chờ đăng sẽ bị huỷ. | Access is revoked at Google immediately and the related data is deleted within 7 days. Publications waiting to upload are cancelled. |
| `channels.disconnect.googlePermissions` | Quản lý quyền truy cập của Google | Manage Google permissions |
| `channels.disconnect.privacyLink` | Chính sách quyền riêng tư | Privacy policy |
| `channels.connectedOn` | Đã kết nối {date} | Connected {date} |
| `channels.lastVerified` | Xác minh lần cuối {date} | Last verified {date} |
| `channels.status.active` | Đang hoạt động | Active |
| `channels.status.expired` | Cần kết nối lại | Reconnect needed |
| `channels.status.error` | Có lỗi | Error |
| `channels.status.revoked` | Đã ngắt kết nối | Disconnected |
| `channels.status.expiredHint` | Google đã thu hồi quyền. Hãy kết nối lại để tiếp tục đăng. | Google revoked the grant. Reconnect to keep publishing. |
| `channels.status.errorHint` | Không đọc được quyền truy cập. Hãy kết nối lại. | The access grant could not be used. Reconnect. |
| `channels.status.unknownChannel` | Kênh đã xoá | Deleted channel |
| `channels.platform.youtube` | YouTube | YouTube |
| `channels.platform.other` | Nền tảng khác | Other platform |
| `channels.result.error.oauth_state_invalid` | Liên kết đã hết hạn hoặc đã được dùng — hãy bắt đầu lại. | This link expired or was already used — start again. |
| `channels.result.error.oauth_denied` | Bạn đã từ chối quyền truy cập tại Google. | You declined access at Google. |
| `channels.result.error.oauth_exchange_failed` | Google chưa hoàn tất kết nối — hãy thử lại. | Google did not complete the connection — try again. |
| `channels.result.error.channel_already_connected` | Kênh này đã được kết nối với một tài khoản Night Tales khác. | This channel is already connected to another Night Tales account. |
| `channels.result.dismiss` | Đóng | Dismiss |
| `channels.error.badAuthorizationUrl` | Không thể bắt đầu kết nối — hãy thử lại. | The connection could not be started — try again. |
| `channels.retry` | Thử lại | Retry |

Names are this task's [DECISION] over 06's [INFERENCE] list (F8): the four redirect outcomes render from the page-local `channels.result.error.<code>` keys (two of them have no catalogue entry at all — F5), while a ProblemDetails carrying `oauth_state_invalid`/`channel_already_connected` renders `errors.<code>` from 10.2.2-T1's set with identical wording (parity is asserted, §9). 06's proposed `channels.error.feature_disabled`, `channels.reauth_required` and `channels.legal_acceptance_required` are **not** created: the flag renders `errors.feature_disabled` and the two gates are the dialogs' own copy (`core.reauth.*`, `account.legal.*`) — one wording per situation, no duplicate strings (D10/WR15). Also added to the shared `errors` scope **by append only**: `errors.channel_already_connected`, `errors.oauth_state_invalid`; every other code used here must already exist (asserted in §9).

**Shell integration.** `core/shell/nav-items.ts` gains one entry — `{ id: 'channels', labelKey: 'shell.nav.channels', route: '/channels', visible: () => hasFeature('youtube_publish') }` — in the same PR as the route (F11); `web/src/assets/i18n/core/{vi,en}.json` gains `shell.nav.channels` (vi "Kênh phát hành", en "Channels"); the route sets `data: { titleKey: 'channels.title' }` so 10.2.6-T1's title path works unchanged. §10 [OPEN] 1 records the `visible()` signature and the 10.4.1-T1:39 conflict.

**Accessibility (D16, screen-specific).**
- The result banner is a live region (`role="status"`, `aria-live="polite"`) and receives programmatic focus (`tabindex="-1"`) on the callback landing so a keyboard/screen-reader user hears it after a full page load; it is dismissible by keyboard (`channels.result.dismiss`) and focus moves to the list heading after dismissal.
- Every status is text + icon inside `StatusChip`; no information by colour alone. `expired`/`error` rows carry the hint sentence, so the state is readable without the chip.
- Connect is a real `<button type="button">`; `aria-busy="true"` + `disabled` while starting; the spinner is decorative (`aria-hidden`) and the reduced-motion media query replaces it with static `channels.connecting` text.
- The row menu has an accessible name including the channel title; Escape closes it and returns focus to the trigger; the `ConfirmDialog`/`ReauthDialog`/`LegalAcceptanceDialog` traps focus and restores it to the invoking control on close (wave-1 dialogs).
- Errors are linked to their control/region (`aria-describedby`); the page's only form-ish control is the Connect button, so `actionError` uses an inline `alert` region with `role="alert"`.
- 360 px: rows stack (chip + title + dates + menu in one column), channel titles wrap (`overflow-wrap: anywhere`), the longest vi string (`channels.disconnect.body`) wraps without clipping and no horizontal scroll appears.
- Contrast ≥ 4.5:1 for text and ≥ 3:1 for the focus ring in both themes; every target ≥ 24 px.
- Keyboard path: skip link → nav → Connect (Enter) → dialog (if any) → confirm; the whole page is reachable without a pointer.

**Design (owner rule 2026-09-25, WD11).** `10.7.1-T0` is the folder's design gate: wireframes + hi-fi mockups for the `/channels` page (loading/empty/ready/connect-in-flight/action-error/blocking-error/feature-off), the callback result banner and the disconnect confirmation, at 360/768/1280, dark + light, vi + en, with focus order/keyboard path/`aria-live` annotations. **Design link (recorded 2026-10-02):** review deck [`design/ui/index.html`](../../../../design/ui/index.html) → screen 06 [`screens/06-render-publish.html`](../../../../design/ui/screens/06-render-publish.html): `/channels` page [`#f-channels-loading`](../../../../design/ui/screens/06-render-publish.html#f-channels-loading) · [`#f-channels-empty`](../../../../design/ui/screens/06-render-publish.html#f-channels-empty) · [`#f-channels-ready`](../../../../design/ui/screens/06-render-publish.html#f-channels-ready) · [`#f-channels-connect-busy`](../../../../design/ui/screens/06-render-publish.html#f-channels-connect-busy) · [`#f-channels-action-error`](../../../../design/ui/screens/06-render-publish.html#f-channels-action-error) · [`#f-channels-blocking-error`](../../../../design/ui/screens/06-render-publish.html#f-channels-blocking-error) · [`#f-channels-feature-off`](../../../../design/ui/screens/06-render-publish.html#f-channels-feature-off); rows [`#f-channels-rows`](../../../../design/ui/screens/06-render-publish.html#f-channels-rows); result banner [`#f-channels-connected`](../../../../design/ui/screens/06-render-publish.html#f-channels-connected) · [`#f-channels-errors`](../../../../design/ui/screens/06-render-publish.html#f-channels-errors); disconnect [`#f-channels-disconnect`](../../../../design/ui/screens/06-render-publish.html#f-channels-disconnect) · [`#f-channels-disconnecting`](../../../../design/ui/screens/06-render-publish.html#f-channels-disconnecting) · [`#f-channels-disconnected`](../../../../design/ui/screens/06-render-publish.html#f-channels-disconnected) · [`#f-channels-disconnect-error`](../../../../design/ui/screens/06-render-publish.html#f-channels-disconnect-error); dialogs [`#f-channels-reauth`](../../../../design/ui/screens/06-render-publish.html#f-channels-reauth) · [`#f-channels-legal`](../../../../design/ui/screens/06-render-publish.html#f-channels-legal). Full state → frame matrix: `10.7.1-T0-ui-design.md` §Outputs. **Owner approval: approved by the owner 2026-10-02** (deck v2 "Phòng thu", commit `192cc73`). The task below lists `10.7.1-T0` as a hard `Depends on`.

## 7. Jobs and integrations

- **Timers: none.** No poll, no interval, no refresh timer on this page (D13/D17; `visible-poll` is not used — §3 table). Nothing needs `DestroyRef` cleanup; if a future revision adds one it must use it (D13).
- **Realtime: none** (F7). The page neither subscribes nor resyncs; a connect/disconnect done in a second tab is visible after the next navigation to `/channels` (fresh `GET /channels`), which is the same freshness the redirect flow provides.
- **External navigation.** Exactly one outbound navigation: `window.location.assign(authorizationUrl)` after the allow-list check (WD10 + WT-W13). No popup, no `iframe`, no `fetch` to Google, no third-party script or font on the page (D11, WD10). Consequences: the SPA's state is not preserved across the OAuth round trip — by design, the page reloads from `/channels?...`.
- **Server-side integration this page depends on but does not drive:** the callback's Google exchange (timeout 15 s, **no retry** — the code is single-use), `channels.list?part=snippet&mine=true` (10 s, 2 attempts on 5xx), the connect e-mail through `IEmailSender.QueueAsync` in the same transaction, and the disconnect's 3-attempt revoke loop (F2/F6). A disconnect whose Google revoke fails still wipes the tokens and answers `204` — the page shows success and the operator alert/metric (`youtube_channel_revoke_failed_total`) is the only signal (§11).

## 8. Security and privacy

| Threat / duty | Control (owner) |
|---|---|
| Open redirect / hostile `authorizationUrl` | Parse and allow-list before navigating: `https:`, hostname exactly `accounts.google.com`, pathname exactly `/o/oauth2/v2/auth`, no credentials, no explicit port, and `redirect_uri` equal to `${location.origin}/api/v1/channels/youtube/callback`; otherwise no navigation, `actionError` + `channels.error.badAuthorizationUrl`, dev console error (WD10 + WT-W13) — this task |
| `state`/PKCE/`code` leaking | The `authorizationUrl` is never logged, never stored, never rendered into an anchor: it exists only in the store's local variable until `location.assign`; the API logs the callback query redacted and the LB drops it (F4, 06 YR23) — this task + 06 |
| Connect started in another browser (login-CSRF) | The API's `__Host-nts_oauth` nonce (HttpOnly, Secure, SameSite=Lax, 10 min) is required by the callback and compared constant-time; the SPA never reads or writes it, and it must use a **top-level** navigation so the Lax cookie is sent on the callback GET (F3, 06 YR10) — this task's navigation choice |
| A stolen session using the connect | The API requires a re-auth within 5 min (`RequireReauth`, 401) and a current legal acceptance (409); the SPA opens the dialog rather than bypassing the gate, and re-checks on the retry (§10 [OPEN] 2) — 10.3.3-T3 + 13.2.1-T1 + this task |
| Reflected copy from the callback query | Only the closed four-value set maps to copy; an unknown value is ignored and the query is still replaced out of the URL — this task (F4/F5) |
| Result re-shown from history / shared link | `Location.replaceState('/channels')` immediately after reading the query once (WB5, WD5) — this task |
| Tokens in the DOM/logs | `ChannelConnectionDto` carries no token and no `oauth_client_id`; the page never sees an access/refresh token; no `console.log` of the response (F1, 06 D4) — this task |
| Credit or billing exposure | No cost/quote/balance copy on this page (D14) |
| CSP/Trusted Types | The page adds no inline script/style, no `innerHTML`, no `bypassSecurityTrust*`; one external link (`security.google.com/...`) is a plain anchor with `rel="noopener noreferrer"`; the D4 header needs no new source (`img-src`/`media-src` untouched) and `form-action 'self'` does not govern `location.assign` (F14) — asserted by the e2e CSP fixture |
| Revocation duty (policy) | The disconnect dialog states the immediate Google revoke, the 7-day deletion and links Google's permissions page; the API implements the revoke + `data_deletion_requests(scope = channel, reason = in_app_revoke, due_at = now + 7 d)` (F6, F15) |
| Storage | No `localStorage`/`sessionStorage`, no cookie written by the SPA; the only client persistence remains `core/prefs` (language) — D2/D6 |
| Cross-tenant | The API's tenant filter answers `404` for another owner's channel id; the page maps `404` to a reload + `errors.not_found` and never reveals an existence difference (F6) |

**Reuse ownership (D11/WR4) — produced vs consumed.** Produced by this task: `Channel`/`ChannelStatus`/`ChannelResult`, `ChannelRepository` + `ChannelHttpRepository`, `ChannelsStore`, `publishing.routes.ts`, `index.ts`, `fake-channel.repository.ts`, the `channels` i18n scope, the dynamic `/channels` route and its nav entry. Cross-folder split agreed with the `10.7.2-publish` folder (2026-09-25): 10.7.2-T1 defines no channel port of its own — its `PublishFormStore` injects this task's abstract `ChannelRepository` through `@features/publishing/index.ts` and filters `status === 'active'` — keeps only the `publishing` i18n scope, links here with `publication.create.connectChannel` when the owner has no active connection, and 10.7.4-T2 appends `/publications/cancel` + `/publications/approve` to the `publishing.routes.ts` this task creates. Consumed (never re-declared): `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `StatusChip`, `ConfirmDialog` (10.1.3-T1); `AppErrorMapper`, `AppError`, `Result`, `RetryCountdown`, `FormErrorAlert`/inline alert, `bindFieldErrors` (10.2.2-T1); `SessionStore.hasFeature` + `authGuard` (10.2.1-T1) and the shell (`10.2.6-T1`); `ReauthService`/`ReauthDialog` (10.3.3-T3, `shared/ui/dialog/reauth-dialog/` — `shared/`, per D11/WR4); `LegalAcceptanceStore` + `LegalAcceptanceDialog` (13.2.1-T1, `features/account/application/legal-acceptance.store.ts` + `features/account/ui/legal-acceptance-dialog/`, reached through `@features/account/index.ts` — D2's only legal cross-feature path, `13.1-legal-acceptance/plan.md:134-141`); Transloco + the `core`/`errors` scopes (10.2.5-T1); `@shared/api` (10.1.4-T1, `data-access` only).

## 9. Test plan

**Vitest (`@angular/build:unit-test` + jsdom, TestBed zoneless, hand-written fakes — D15; no mocking library).**

`data-access/channel.http-repository.spec.ts` — `list()` unwraps `{ items }` and maps every DTO field + all four statuses; an unforeseen status string maps to `'error'` without throwing; an empty body yields `[]`; a `404 feature_disabled` reaches the caller as the mapped `AppError` (`HttpTestingController` only for the HTTP shape — the port-level tests use the fake).
`application/channels.store.spec.ts` (with `FakeChannelRepository` + a fake `SessionStore` + a fake `ReauthService`): `load_SetsReadyAndRows` · `load_EmptyListStaysReady` · `load_ErrorSetsBlockingError` · `load_FeatureDisabledStopsRetrying` · `load_IsIssuedOncePerCall` (no double fetch) · `connect_HappyPathNavigatesToProvider` (asserts the assign target and that `connectState` went `starting → navigating`) · `connect_NonGoogleOriginIsRefusedAndNeverNavigates` · `connect_WrongAuthorizationPathIsRefused` · `connect_ForeignRedirectUriIsRefused` (absolute `redirect_uri` on another origin, and a relative/absent one) · `connect_ReauthRequired_OpensDialogThenRetriesOnce` (fake re-auth: one `ensureReauthenticated` call, exactly one retry) · `connect_ReauthCancelled_ReturnsErrorAndNoRetry` · `connect_LegalRequired_RetriesOnceAfterAccept` · `connect_LegalDeclined_KeepsErrorWithDocuments` · `connect_RateLimited_ExposesRetryAfter` · `connect_ChannelAlreadyConnected_MapsCopy` · `disconnect_ConfirmCallsApiThenReloads` · `disconnect_CancelIssuesNoRequest` · `disconnect_NotFoundReloadsList` · `disconnect_ErrorKeepsList` · `readCallbackResult_ConnectedTriggersLoadOnce` · `readCallbackResult_UnknownValueIgnored` · `readCallbackResult_IsClearedAfterDismiss`.
`feature/channels-page.spec.ts` (component): all five render states; the four banner copies render in `vi` **and** `en`; the unknown query value renders no banner; `StatusChip` text per status; the disable action is absent for `revoked` and present for `active`/`expired`/`error`; the confirm dialog copy contains the 7-day sentence and the Google permissions link (`rel="noopener noreferrer"`); the banner has `role="status"` + `aria-live="polite"` and receives focus on the callback landing; date formatting differs between `vi-VN` and `en-US`; no store is injected in `channel-row`/`channels-result-banner`.

**Playwright (`web/e2e/channels.spec.ts`, run by 11.5.1-T1's `e2e.yml` — WR3/WR14; owner login via the UI + TOTP helper, one login per file, `workers: 1`, axe + the D4 CSP fixture on every visited page).**
1. Empty state: with `youtube_publish` on and no connection, `/channels` shows `channels.empty.*` and an enabled Connect button (axe clean, both languages).
2. Connect navigation target: intercept/observe the navigation request (`page.route` on `https://accounts.google.com/**` or `page.waitForRequest`) after clicking Connect and assert the URL's path `/o/oauth2/v2/auth`, `access_type=offline`, `prompt=consent`, `include_granted_scopes=false`, `code_challenge_method=S256`, `redirect_uri` **equal to** `${location.origin}/api/v1/channels/youtube/callback` (same origin as the page — WT-W13), and the two scopes (F2) — the request is aborted so no real Google call happens (WR14 allows `page.route`).
3. Callback result × 4: direct navigation to `/channels?error=<code>` and `/channels?connected=youtube` renders the matching banner once, and the URL is `/channels` afterwards (`history` length unchanged, back does not re-render it) — the client-only half of UC3 (WB5).
4. Flag off: with `youtube_publish` disabled (`nts-worker flags set`, WT-W2's out-of-SPA seam), `/channels` shows `errors.feature_disabled` and no Connect action.
4b. Seeded connection (WT-W4): with the `active` row seeded by 10.10.2-T1's global setup (`web/e2e/seed/youtube-channel.sql`, token columns `NULL`) the list renders one `active` row with its dates and the Disconnect action, and a real `DELETE /channels/{id}` through the confirm dialog leaves the row `revoked` after the reload with the action gone — the real delete path, exercised in the browser. **Ordering:** this scenario revokes the seeded row, so it must either re-apply the seed in its own `beforeAll` (the fixture is idempotent only while the row is `active`: `ux_channel_connections_account` is partial — `CREATE UNIQUE INDEX … WHERE status <> 'revoked'`, `06.1.2-T2-channel-connection.md:58-59`, and a revoked row reconnects as a **new** row, `:192` — so a re-run inserts a fresh `active` row) or run after every spec that needs an `active` row (10.10.2-T1's J6) — raised with that folder 2026-09-25.
5. axe (WCAG 2.1 AA tags) + no `securitypolicyviolation` and no CSP/Trusted-Types console error on every page above; 360 px without horizontal scroll; keyboard-only path to the Connect button and through the disconnect confirm dialog.
The **connect exchange with Google** is still not exercised in the browser (the callback needs a real OAuth exchange; `FakeGoogleServer` fakes Google only in .NET tests — WC10): scenario 2 stops at the navigation target and scenario 3 feeds the callback outcomes directly. The **disconnect of a real row** *is* covered now (4b) because WT-W4 seeds one `active` connection outside the SPA; the full connect walkthrough (Google → callback → row) stays manual evidence for 06.5.1-T1's `06-connect` gate and 13.4.2-T1.

**Where each suite runs.** Vitest in `web.yml` (10.1.5-T1's pipeline); Playwright in `11.5.1-T1`'s `e2e.yml` — never in `web.yml` (WR3/D15). i18n parity for `channels`, `core` and `errors` is 10.2.5-T1's check and must be green with the new keys.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 10.7.1-T1 | Channels page: connect (re-auth + legal gate), callback result, disconnect | M (1.0) | 10.2.6-T1, 10.3.3-T3, 13.2.1-T1, 06.1.2-T1, 06.1.2-T2, 06.4.1-T1 | 10.7.2-T1, 06.5.1-T1, 10.7.3-T1 (10 CR-37) |
| 10.7.1-T0 | UI design (channels: connect, callback result, disconnect) | S (0.5) | 10.1.3-T1, 06.1.2-T1 (soft) | 10.7.1-T1 |

Sum 1.5 d (0.5 `10.7.1-T0` design, WD11/owner rule 2026-09-25, + 1.0) = `../README.md` §B9 row "10.7 P publishing" (+0.5 for this task versus the high-level S-sized "channels page", because the P contract adds the re-auth and legal gates and the callback-result handling). Order: this task first; 10.7.2-T1 (publish form + consent) then consumes its `Channel`/`ChannelRepository`/`ChannelsStore` and the route/`index.ts`; 10.7.4-T1/T2 and 10.10.2-T1 follow. Phase `P · M1`, flag `youtube_publish`.

**[OPEN] items raised by this folder** (for the TL at DoR; the fallback needs no file outside this folder):

1. **Nav-entry ownership and `visible()` signature.** F11 says the screen task appends its own `NAV_ITEMS` entry, but `10.4.1-T1:39` says the entry is 10.2.6-T1's, and `10.2.6-T1:27` lists Channels under "S tasks 10.7–10.9" although Part B moved P publishing into P. This task appends the entry (flag-gated) and adds `shell.nav.channels` to the `core` scope; if the shell actually ships the entry itself, the acceptance check asserts the existing entry instead of adding one. The `visible()` form depends on D14's `hasFeature(key)` exposure (function vs `SessionStore` method): fallback — read `session.me()?.features ?? []` and treat an absent array as flag **off** (the 10.5.8-T1 precedent). Separately, `10.2.6-T1:27`'s "S tasks 10.7–10.9 add Channels" predates Part B, which moved P publishing into P (§B8.1/WD1): recorded as a **CR request to 10.2.6-T1's Notes** (the entry arrives with 10.7.1-T1, phase P) — no behaviour change here, and the entry ships flag-gated either way (D14: a `404 feature_disabled` hides the block and hides the entry for the session).
2. **Who opens `LegalAcceptanceDialog` — the hand-off is unowned, so this page does it.** `13.1-legal-acceptance/plan.md:147-149` hands the 409 → dialog wiring to **10.2.2-T1**, but 10.2.2-T1's produced/consumed list has no legal branch at all (`10.2.2-T1-app-error-mapper.md:175-178`; a `grep legal` over `10.2.1-session-errors/` returns nothing), so a page that merely mapped the error would dead-end the owner. [DECISION] this task injects the account feature's `LegalAcceptanceStore` (through `@features/account/index.ts`, D2), opens the dialog itself and retries the `POST` once; if 10.2.2-T1 later adds the mapper branch, this page's explicit handling wins (it inspects `AppError.code` before delegating) and nothing double-opens — the CR request stands for the mapper/13 to record the real owner. Also: the re-auth window is re-evaluated by the server on the retry, so a re-auth that succeeded just before the 5-minute boundary can still answer `reauth_required` → the second failure is terminal (no third attempt) with `errors.reauth_required`.
3. **Forcing a `/me` refresh after `404 feature_disabled`.** D14 says the block hides itself and refetches `/me`, but 10.2.6-T1's `loadMe()` is guarded by `me === null` and WR8 lists no `refreshMe()`. Fallback: the page renders `errors.feature_disabled`, hides the actions and the nav entry for the session, and issues no further request; a `refreshMe()` on the shell store is a CR request to 10.2.6-T1, not a blocker.
4. **e2e seed for an `active` connection — closed by WT-W4 (README Part B §B10).** A row can only exist after a real OAuth exchange (WC10), so the connect-return cannot be driven in Playwright; **WT-W4** seeds one `active` connection with `web/e2e/seed/youtube-channel.sql` (token columns `NULL`), run by 10.10.2-T1's global setup through `docker compose exec postgres psql`, with no test-only Api/Worker code. This task therefore exercises the real `DELETE` path in the browser (scenario 4b) and leaves the Google exchange itself to 06.5.1-T1's `06-connect` manual walkthrough (`tools/ops/youtube-oauth-check.py`, 06.1.1-T1) and 13.4.2-T1; if the seed file's shape changes, only scenario 4b's fixture is affected — no product code.
5. **`errors.*` keys are another task's file.** This task appends only `errors.channel_already_connected` and `errors.oauth_state_invalid`; `errors.reauth_required`, `errors.legal_acceptance_required`, `errors.rate_limited`, `errors.feature_disabled`, `errors.network_error`, `errors.unknown_error` must exist (10.2.2-T1 / 13.2.1-T1). If one is missing at implementation time, the acceptance check fails loudly and the fix is a one-line append, not a new key family (WR15).

**Re-estimate: none for the folder.** 1.0 d is the §B9 figure; the §B8.1 rows and the §B9 total (wave 2 P 7.5, plan 10 50.0) need no change.

## 11. Rollout

- **Flag.** `youtube_publish` only (D14/O1/C…): read through `GET /me` → `features[]`. Off ⇒ the nav entry is hidden, a direct URL renders `errors.feature_disabled`, the Connect/Disconnect actions never render, and the API answers `404 feature_disabled` on all three routes (F1/F2/F6). Flipped off mid-session: the first 404 hides the block and stops retrying ([OPEN] 3), which is the same outcome as a fresh load.
- **Order.** 10.7.1-T1 (this) → 10.7.2-T1 → 10.7.4-T1 → 10.7.4-T2 → 10.10.2-T1. Backend prerequisites must be merged first (hard, per §B8.1): 06.1.2-T1 (connect + callback), 06.1.2-T2 (`GET /channels`, `ChannelConnectionDto`), 06.4.1-T1 (`DELETE`), plus the wave-1 gates 10.2.6-T1 (shell/nav), 10.3.3-T3 (re-auth) and 13.2.1-T1 (legal dialog). Component tests may start against `FakeChannelRepository`; the acceptance run needs the real API.
- **Monitoring.** The server-side signals an operator needs because the UI cannot show them: `youtube_channel_revoke_failed_total` (a disconnect that answered `204` but failed at Google — the user sees success and the data still disappears on schedule), the `oauth_states` claim failures behind the four callback codes (a spike in `oauth_state_invalid` means expiry/foreign-browser attempts), and the connect endpoint's `503 service_unavailable` (unreadable client secret). No new client-side telemetry, no client log of any URL.
- **Rollback.** Switch `youtube_publish` off: the SPA hides the entry + block, and the API stops answering the routes; no migration to revert, no client state to unwind (the SPA persists nothing about channels), and already-connected channels keep publishing because 06's jobs do not depend on the SPA. The `/channels` route can stay registered (it renders the feature-off state).
- **After-ship checks.** The 06.5.1-T1 `06-connect` gate evidence (manual OAuth walkthrough recorded), the 10-W1-session gate addition (owner connects + disconnects once in staging with the real Google client), and the S-phase items stay out (TikTok, admin client view, per-client counts).

## 12. Angular implementation amendment (2026-10-02)

### Design evidence and implementation reconciliation notes

- Static evidence is in the approved deck: channel frames in [`06-render-publish.html`](../../../../design/ui/screens/06-render-publish.html): `#f-channels-loading`, `#f-channels-empty`, `#f-channels-ready`, `#f-channels-connect-busy`, callback/error variants, `#f-channels-feature-off`, disconnect, re-auth and legal-gate variants. The state×breakpoint×theme×locale evidence matrix is recorded in the T0 §Outputs.
- Connect disabled, redirecting, rejected-URL, callback-focus/dismiss and disconnect-busy/failure states follow the locked contract; flag to the owner only if a new visual state is needed (does not reopen the approval or block the task). Static callback banners do not prove read-once query cleanup, focus movement or safe navigation. The legal consent dialog is evidenced by `06-render-publish.html#f-channels-legal` (checkbox, Cancel, Accept-and-continue) — not by screen 01, whose `f-legal-*` frames are document pages, not the acceptance dialog. The shared dialog itself is 13.2.1-T1's (`features/account`, WT-W5) and is opened by this page; its loading/failure/focus and re-acceptance states follow the locked contract; flag to the owner only if a new visual state is needed (does not reopen the approval or block the task).

### Angular boundaries and integration readiness

- `/channels` is the routed `feature/` container; `ChannelRow`/`ChannelResultBanner` stay presentational; `ChannelsStore` is route-provided and owns composition; `ChannelRepository` and mapper are the only generated-client boundary. Public signals are readonly, lists are replaced, and a page/session request generation prevents a late list/connect/disconnect result from mutating a remounted page.
- Full response/status/header concerns remain in data access. Explicit mapping handles `{items}`, nullable channel labels/dates, closed callback codes and unknown status fail-closed. Re-auth/legal are one-retry orchestration boundaries; URL allow-list validation occurs before top-level navigation; callback query is consumed once and removed before reuse. `DestroyRef` clears form/dialog/banner registrations and ignores obsolete async completions.
- Channel connect/list/disconnect are planned-only until their `06.*` owners land. No mockup authorizes another platform, a token in browser storage or an additional action. TikTok remains the separate R2 owner in `10.7.3`; this plan only preserves the previously approved additive callback/store surface.

No task ID, size, phase, dependency or estimate changes in this amendment.
