# 10.2.4 · 10.2.5 · 10.2.6 — Core realtime, i18n and shell (web UI wave 1)

Parent: ../README.md · Area FE · Phase P · M1 · 4 tasks · **2.5 d raw / 3.0 buffered** (incl. `10.2.4-T0` design, D19/owner rule 2026-09-25)

Contract: `README.md` §3 **D1–D18** (decisions used with their exact names: `SessionStore`, `RealtimeService`, `AppErrorMapper`, `ConflictDialog`, `InlineAnnotation`, `visible-poll`, `segment-address`, `cursor-list`, …). Evidence legend: ✅ read from a file/URL (path + line/scope, 2026-09-24) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

Sources read for this plan: `plans/features/10-web-ui/README.md:1-347` (§1 C1–C15, §2 F01–F25, §3 D1–D18, §4 CR-1–CR-10, §5, §6, §7, §8.1, §9 re-estimate, §10 O1–O4) ✅ · `plans/10-web-ui.md` §4 epics 10.2.4–10.2.6 and §5.2 ✅ · `plans/features/09-backend-api/09.8.1-realtime/plan.md:69-143` ✅ · `plans/features/09-backend-api/09.8.1-realtime/09.8.1-T2-hub-ticket.md` ✅ · `plans/features/09-backend-api/09.3.4-me-limits-audit/09.3.4-T1-me-endpoints.md:1-63` ✅.

## 1. Scope

**In scope (P · M1).** The three core services the whole wave-1 UI stands on:

| Story | What it delivers |
|---|---|
| 10.2.5 i18n | Transloco `vi`/`en` scopes, lazy per route, `LocaleService` with `Intl` formats + explicit time zones, key-parity + used-key CI gate |
| 10.2.4 realtime | `RealtimeService`: hub ticket → WebSockets-only connection, one uniform close path (backoff → fresh token → ticket → connect), `resync` + coalesced refetch, idle pause |
| 10.2.6 shell | Shell layout (top bar + side nav), navigation, language switch, user menu, realtime status bar, document titles |

**Out of scope.** Login/MFA/reset screens (10.3.3-T1/T3), public/legal pages (10.3.1-T1), every studio screen (10.4.x, 10.5.x). Wave 2/S: render/video review consumes `RealtimeService.progress` (10.6.1-T1) but adds no key here; credit/balance texts and the `header-end`/`cost-line` slots stay empty in P (D14); admin chunk and Channel/Billing/Account navigation entries belong to 10.7–10.9 (S). Toast/snackbar host and `LoadingState`/`EmptyState` visuals come from 10.1.3-T1/D11 — the shell only composes them.

**Tasks (see §10).** `10.2.5-T1` (S 0.5) → `10.2.4-T1` (M 1.0) → `10.2.6-T1` (S 0.5). Order is forced: the shell imports `LocaleService` (10.2.5-T1) and `RealtimeService` (10.2.4-T1); both consume `SessionStore`/`ActivityTracker` from 10.2.1-T1 and `AppErrorMapper` from 10.2.2-T1 (both in `../10.2.1-session-errors/`, same wave).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| RF1 | Hub is `/hubs/episodes`, WebSockets only + `skipNegotiation`; `POST /api/v1/hubs/ticket` → `{ ticket, expiresAt }`, 60 s, single use, policy `hub` 10/min/user; `401 hub_ticket_invalid`; a connection is **aborted** after `Hubs:MaxConnectionMinutes` = the role's access-token lifetime (owner 5, user 15) and on every family revocation; handshake `Origin` must equal `App:PublicOrigin` | README §1 C6 ✅; 09.8.1-T2 Design/§5 ✅ |
| RF2 | Ticket route: `200 { "ticket": "<43-char base64url>", "expiresAt": "…Z" }`; errors `401 unauthorized`, `429 rate_limited` + `Retry-After`, `503 service_unavailable`; the plaintext ticket is never logged and stored only as a SHA-256 hash | 09.8.1-T2 §5 + Steps 2/7 ✅ |
| RF3 | Wire payload `{ userId, event, ids { seriesId, episodeId, renderJobId, publicationId, storyVersionId }, data }`; `data` present **only** for `RenderProgress` and carries only `stage` + `percent` (0–100); ids only; body ≤ 8 KB | 09.8.1 `plan.md` §3, §5 ✅ |
| RF4 | Event names: `EpisodeUpdated`, `RenderProgress`, `PublicationUpdated`, `CreditBalanceChanged`; no `AutopilotRunUpdated`, no `SeriesBibleUpdated` in P; clients refetch `GET /episodes/{id}` on events | README §1 C7 ✅; 09.8.1 `plan.md` §5 ✅ |
| RF5 | Render stages the client may show: guaranteed `queued \| running \| ingesting`, sub-steps `voice → subtitles → images → assembling` + `checking`; unknown stage values are ignored, never shown raw | `plans/10-web-ui.md` 10.6.1 ✅; README §2 F07/D8 ✅ |
| RF6 | D8 realtime contract: one `HubConnection`, `withUrl('/hubs/episodes?ticket=<t>', { transport: WebSockets, skipNegotiation: true })`, **no** `accessTokenFactory`, no built-in automatic reconnect (ticket is single-use); every `onclose` follows one path after backoff 1, 2, 4, 8, 16, 30 s + 0–20 % jitter, gated by D7; `401 hub_ticket_invalid` → one new ticket then backoff; refresh failure ends the session; `resync` on every (re)connect and on `visibilitychange → visible`; only stores with a live subscription refetch, coalesced per store within **250 ms**; `RenderProgress` updates the progress signal directly (no refetch); token never in the hub URL; ticket never logged | README §3 D8 ✅ |
| RF7 | D7 activity gate: `core/session/activity.ts` tracks `pointerdown/keydown/wheel/touchstart` throttled 1/10 s + `document.visibilityState`; `isActive()` = input within `idleWindow` (owner/admin 25 min, user 13 d); a refresh not caused by a user action happens only when `isActive()`, else realtime pauses and a `realtime.paused` bar offers Continue; polling/visibility are gated the same way | README §3 D7 ✅ |
| RF8 | D6 session: boot `POST /api/v1/auth/refresh` in `provideAppInitializer`; interceptor single-flight refresh **on demand only** (expiry < 30 s or 401), **no timer-driven refresh**; `SessionEnded` (logout, refresh failure, `refresh_token_reused`) resets every `SESSION_SCOPED` store, **stops realtime**, routes to `/login`; `returnUrl` only `^/(?![/\\])[^\s]*$` | README §3 D6, F21/F22 ✅ |
| RF9 | D10 i18n: Transloco; `vi` default + `en`; one scope per feature (`auth`, `series`, `episodes`, `dashboard`, `public`) + `core` + `errors` + **`quality`** (`web/src/assets/i18n/quality/<lang>.json`, 02 hint copy — WR15); files `web/src/assets/i18n/<scope>/<lang>.json`, lazy per route; key style `scope.area.item` camelCase leaves; handed-over keys used verbatim (`series.style.<name>.label/.hint`, `series.style.trueCrimeNotice`, `voices.tag.<tag>`, `quality.*`, `render.*`); language = profile `uiLocale` → saved pre-login preference → browser → `vi`; `Intl` for `vi-VN`/`en-US`; time zones explicit where the value is a schedule; CI: `vi`/`en` key sets equal per scope **and** every key used in a template exists (keys-manager `find` output compared against the flattened key sets by the same script) | README §3 D10 (line 166) ✅, §10 WR15 (line 358) ✅ |
| RF10 | D11 shared UI: Material M3 dark default + light, tokens in `shared/ui/theme/`, Be Vietnam Pro self-hosted, **no third-party font/CDN request**; components with inputs/outputs only, OnPush, component test. Inventory with owners per **WR4**: `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `StatusChip`, `ConfirmDialog` (+`cost-line` slot), `Stepper`, `CharCounter`, `CopyableCode`, **inline-`<svg>` icon components** → 10.1.3-T1; `ConflictDialog`, `RetryCountdown`, `bindFieldErrors`, `FormErrorAlert` → 10.2.2-T1; `IdempotencyKeys` → 10.2.3-T1; `core/state/cursor-list.ts`, `shared/util/cursor-page.ts`, `core/state/visible-poll.ts` → **10.2.6-T1**; `InlineAnnotation`/`SegmentChips` → 10.5.10-T1, `DiffView` (with `@defer`) → 10.5.4-T1, `ReauthDialog` → 10.3.3-T3, `AudioPreviewButton` → 10.4.2-T2 | README §3 D11 (line 168) ✅, §10 WR4 (line 347) ✅ |
| RF11 | D14: `core/session` exposes `hasFeature(key)`; allow-list keys `source_finder`, `youtube_publish`; `GET /me` gains `features: string[]` (10 CR-10); shell header exposes an optional `header-end` slot filled by S tasks 10.8.1-T1/10.8.3-T1; no credit text in P | README §3 D14, §4 CR-10 ✅ |
| RF12 | `MeDto(Guid Id, string DisplayName, string Email, Language UiLocale, PlatformRole Role, bool MfaEnabled, DateTimeOffset? LastLoginAt)`; `PATCH /api/v1/me` changes `displayName` and `uiLocale` only (strict binding filter → unknown member = `400 validation_failed`, camelCase `uiLocale`), `401 unauthenticated`, no ETag | 09.3.4-T1 Design ✅ |
| RF13 | D15: Vitest via `@angular/build:unit-test` + jsdom, TestBed zoneless, **hand-written fakes only** (no mocking library); Playwright e2e in `web/e2e/`, one UI login per spec file + TOTP helper, `workers: 1` for owner specs, never reuse `storageState`; `web.yml` steps: `npm ci → lint → i18n check → api:gen drift → unit → build → gzip budget → gen-csp`; the compose e2e runs in 11.5.1-T1's `e2e.yml` job `e2e` | README §3 D15 (line ~174) ✅ |
| RF14 | D16 a11y baseline: labelled controls, visible focus ≥ 3:1, no keyboard traps (except trapped modals), `aria-live="polite"` for progress/status, contrast ≥ 4.5:1 both themes, no colour-only information, targets ≥ 24 px, 360 px without horizontal scroll, `prefers-reduced-motion` honoured, axe in each e2e spec | README §3 D16 ✅ |
| RF15 | D2 lint: `domain` imports nothing Angular/rxjs; `ui` injects no store/repository/`HttpClient`; only `data-access` imports `@shared/api`; no `any`, no non-`OnPush`, no `subscribe()` in components, no `bypassSecurityTrust*`/`[innerHTML]`/`document.write`/`eval`, **no `localStorage`/`sessionStorage` outside `core/prefs`** | README §3 D2 ✅ |
| RF16 | D3 dev: `ng serve` on `127.0.0.1:4200` with `proxy.conf.json` → `/api`, `/health` to the local API and `/hubs` with `ws: true`; one origin per environment (`https://app.<domain>`), SPA uses relative URLs, `connect-src 'self'` covers `wss://` in current browsers ⚠️ (confirmed by the 10.1.6-T1 e2e over `wss://`, else add `wss://<host>`); `script-src 'self' <hashes>`, `style-src 'self' 'unsafe-inline'`, `font-src 'self'`, `frame-ancestors 'none'` | README §3 D3, D4 ✅ |
| RF17 | D13 performance: every component `OnPush`; `@for` always with a stable `track`; `@defer (on viewport \| on interaction)` for heavy parts; every feature lazy; initial raw warning 700 kB / error 900 kB and gzip sum of initial chunks ≤ 250 kB (`tools/web/check-gzip-budget.mjs`); no polling except D17; no timers without `DestroyRef` cleanup | README §3 D13 ✅ |
| RF18 | FE10: `@jsverse/transloco` **8.4.0** MIT, `@jsverse/transloco-keys-manager` **8.1.1** MIT; FE9: `@microsoft/signalr` **10.0.11** MIT, `skipNegotiation` only when WebSockets is the only transport | README §1 FE9/FE10 ✅ |
| RF19 | 09.8.1-T2 `Blocks: 09.9.3-T2, 10.2.4-T1 (10 CR-8)` — this plan's realtime task is the named consumer | 09.8.1-T2 header ✅ |
| RF20 | Gate **10-W1-session** requires: owner login with password + TOTP, reload restores the session, sign-out after a family revocation, **hub reconnect after a backstop close without a refetch storm**, and realtime paused when idle | README §7 ✅ |
| RF21 | **WR7** — canonical realtime consumer API: `on(name, ids?)` and `subscribeEpisode(episodeId, handler)`; consumers use `subscribeEpisode`; D8's wording follows this | README §10 WR7 (line 350) ✅ |
| RF22 | **WR8** — canonical `SessionStore` extras: `mode: 'full' \| 'enrolment'`, `beginEnrolment(token)`, **`logout()`** (calls `POST /auth/logout`, then ends the session without `returnUrl`) and `anonymousGuard`; in enrolment mode `status` is never `'authenticated'`, so **realtime and `GET /me` never start**; no client-side `markReauthenticated` | README §10 WR8 (line 351) ✅ |
| RF23 | **WR11** — requests carry an `HttpContext` flag `USER_ACTION` (set by store methods called from UI events); a 401 on a request **without** the flag refreshes only when `isActive()` (D7), otherwise realtime/polling pause. Resync- and event-driven refetches plus polling are background requests (no flag) | README §10 WR11 (line 354) ✅ |
| RF24 | **WR12** — no `MatIconRegistry` SVG sets (they would need `bypassSecurityTrustResourceUrl` and the `angular#components` policy); icons are inline-`<svg>` components owned by 10.1.3-T1; the Trusted Types header allows exactly `angular`, `angular#bundler`, `nts-sanitizer` | README §10 WR12 (line 355) ✅ |
| RF25 | **WR15** — D10 gains the `quality` scope (02 hint copy, path above); the invented `story_conflict` code is removed (`series_conflict` is the 412 code) | README §10 WR15 (line 358) ✅ |
| RF26 | **WR14** — e2e auth helper: retries on `invalid_mfa_code`, the enrolled TOTP secret lives only in the git-ignored run file `web/e2e/.auth/owner-totp.json` deleted in global teardown, traces/videos off for auth specs, **no in-app test hooks** (`page.route`/`page.clock` only) | README §10 WR14 (line 357) ✅ |

## 3. Domain and data

Client domain models, ports, stores and signals only — no DB, no server model.

### 3.1 Models (`core/realtime/realtime.model.ts`, `core/session/session.model.ts`, `core/i18n/locale.model.ts`)

```ts
// no Angular/rxjs imports in *.model.ts (D2)
export type AppLocale = 'vi' | 'en';
export type FeatureKey = 'source_finder' | 'youtube_publish';           // D14 allow-list (CR-10)
export interface MeSummary { id: string; displayName: string; email: string; uiLocale: AppLocale;
                             role: 'user' | 'admin' | 'owner'; mfaEnabled: boolean; features: FeatureKey[]; }
export type RealtimeState = 'idle' | 'connecting' | 'connected' | 'backoff' | 'paused' | 'stopped';
export type RealtimeEventName = 'EpisodeUpdated' | 'RenderProgress' | 'PublicationUpdated' | 'CreditBalanceChanged'; // canonical SignalR client names (00 §8); snake_case NOTIFY values are tolerated as aliases
export interface RealtimeIds { seriesId?: string; episodeId?: string; renderJobId?: string;
                               publicationId?: string; storyVersionId?: string; }
export interface RenderProgress { episodeId: string; renderJobId: string; stage: string; percent: number; receivedAt: number; }
export interface PublicationUpdated { episodeId: string; publicationId: string; }
export interface HubTicket { ticket: string; expiresAt: string; }
```

```ts
export const WIRE_TO_NAME: Readonly<Record<string, RealtimeEventName>> = {
  EpisodeUpdated: 'EpisodeUpdated',            // canonical: the SignalR client method name (00 §8; 09.8.1 plan.md:170)
  RenderProgress: 'RenderProgress',
  PublicationUpdated: 'PublicationUpdated',
  CreditBalanceChanged: 'CreditBalanceChanged',
  episode_updated: 'EpisodeUpdated',           // tolerant aliases: the internal NOTIFY `event` values the Api listener maps before SendAsync (09.8.1 plan.md:82)
  render_progress: 'RenderProgress',
  publication_updated: 'PublicationUpdated',
  credit_balance_changed: 'CreditBalanceChanged',
};
export const NAME_TO_WIRE: Readonly<Record<RealtimeEventName, string>> = /* canonical PascalCase names */;
```

The four **canonical** names are the SignalR client method names of 00 §8 and `09.8.1-realtime/plan.md:170`; the snake_case members are tolerant aliases for the internal NOTIFY `event` value (`render_progress` at `plan.md:82`) that the Api listener normally maps before `SendAsync` — accepting both costs one map entry and keeps the client correct if a raw payload ever reaches the socket (WR17a). The socket handler looks the incoming name up in `WIRE_TO_NAME`; a name that is not a key is dropped **before** any payload is parsed, never logged with ids, never re-emitted. `FakeHubConnection` emits the **canonical** names in the specs (one alias spec covers the tolerant entry), so the map is what the tests exercise.

An event whose name is not a key of `WIRE_TO_NAME` is dropped at the boundary — never logged with its payload, never re-emitted (RF3/RF4). `percent` is clamped to `0…100`; an unknown `stage` string is dropped (RF5).

### 3.2 Ports (D5: only `data-access` imports `@shared/api`)

```ts
// core/realtime/hub-connection.port.ts — abstraction so the reconnect logic is testable with a hand-written fake (D15)
export abstract class HubConnectionPort {
  abstract start(url: string): Promise<void>;
  abstract stop(): Promise<void>;
  abstract state(): 'disconnected' | 'connecting' | 'connected';
  abstract on(name: string, handler: (payload: unknown) => void): void;
  abstract onClose(handler: (error?: unknown) => void): void;
}
// core/realtime/hub-ticket.repository.ts
export abstract class HubTicketRepository { abstract issue(): Promise<HubTicket>; }
// core/session/me.repository.ts — GET /me feeds the shell; the DTO→MeSummary mapping lives in its data-access
export abstract class MeRepository {
  abstract get(): Promise<MeSummary>;                                   // 09.3.4-T1 MeDto (incl. features[], CR-10)
  abstract updateUiLocale(locale: AppLocale): Promise<MeSummary>;       // PATCH /api/v1/me { uiLocale }
}
```

Implementations: `core/realtime/data-access/signalr-hub-connection.ts` (the only file importing `@microsoft/signalr`), `core/realtime/data-access/hub-ticket.http-repository.ts` (generated client, `POST /api/v1/hubs/ticket`), `core/session/data-access/me.http-repository.ts` (generated client, `GET`/`PATCH /api/v1/me` + the explicit `MeDto → MeSummary` mapping; `MeDto`'s identity fields are grounded — `MeDto(Guid Id, string DisplayName, string Email, Language UiLocale, PlatformRole Role, bool MfaEnabled, DateTimeOffset? LastLoginAt)` at `09.3.4-T1-me-endpoints.md:22-24` ✅, `Language` serialises `"vi"`/`"en"`). The shell calls `SessionStore.setMe(mapped)` after each call; the store itself never talks HTTP (10.2.1-T1). Fakes: `core/realtime/testing/fake-hub-connection.ts`, `fake-hub-ticket.repository.ts`, `core/session/testing/fake-me.repository.ts` (hand-written, D15).

### 3.3 Stores / services and their signals

| Symbol | File | Signals / API (inputs → outputs) |
|---|---|---|
| `RealtimeService` (root) | `core/realtime/realtime.service.ts` | `state: Signal<RealtimeState>`, `connected: Signal<boolean>`, `paused: Signal<boolean>`, `resync: Signal<number>` (monotonic), `progress: Signal<ReadonlyMap<string, RenderProgress>>` keyed by `episodeId`; methods `start()`, `stop()`, `pause()`, `resume()`, `subscribeEpisode(episodeId, handler): RealtimeSubscription`, `on<E>(name, ids?): Observable<Evt<E>>` |
| `RealtimeSubscriptions` | `core/realtime/realtime-subscriptions.ts` | ref-counted registry: remembers `{episodeId, seriesId}` of live subscribers, provides `liveKeys()`, `refetchAll()`, coalescing window **250 ms** per key (D8) |
| `RealtimeBackoff` (pure) | `core/realtime/backoff.ts` | `nextDelayMs(attempt: number, random: () => number): number` → `[1000, 2000, 4000, 8000, 16000, 30000]` capped at 30 s, `+ 0…20 %` jitter; deterministic with an injected RNG |
| `LocaleService` (root) | `core/i18n/locale.service.ts` | `locale: Signal<AppLocale>`; `resolveInitial(profile, pref, browserLangs): AppLocale` (profile → pref → browser → `vi`); **`activate(locale, { persist }): void`** (the only public entry point — the `PATCH /me` call lives in the shell's `ShellStore.changeLocale`, because `MeRepository` is route-scoped); `formatDate/formatDateTime/formatNumber/formatRelative(value, opts?)`; `formatInZone(instant, timeZoneId): string` (schedule values show the zone explicitly — D10) |
| Shell state | `core/shell/shell.store.ts` (`@Injectable()`, provided by the shell component) | `navItems: Signal<NavItem[]>` = `NAV_ITEMS.filter(i => i.visible())`; `titleKey: Signal<string>` from the active route's `data.titleKey`; `menuOpen: Signal<boolean>`; `realtimeLabelKey: computed` from `RealtimeService.state` |
| `MeRepository` (port) | `core/session/me.repository.ts` | `get(): Promise<MeSummary>`, `updateUiLocale(locale): Promise<MeSummary>`; the shell maps and pushes with `SessionStore.setMe` |
| Shared helpers produced here (**WR4**) | `shared/util/cursor-page.ts`, `core/state/cursor-list.ts`, `core/state/visible-poll.ts` | `CursorPage<T> = { items: T[]; nextCursor: string \| null }` + `cursorPage()`; `cursorList({ fetchPage })` → `{ items, nextCursor, loading, hasMore, loadMore() }` with a **single in-flight guard** and `limit ≤ 100` (consumed by the cursor lists of 10.4.1-T1/10.5.1-T1); `visiblePoll({ intervalMs, run })` → `{ start(), stop() }` — runs only while `document.visibilityState === 'visible'` **and** `ActivityTracker.isActive()` (D7), immediate run on return to visible, single in-flight, `DestroyRef` cleanup (consumed by the autopilot run log 10.4.3-T2) |
| Consumed (not produced here) | `core/session/*` (10.2.1-T1) | `SessionStore.me: Signal<MeSummary \| null>` + `setMe(summary)`, `SessionStore.hasFeature(key)` (the shell **does not** define it), `SessionStore.ensureFreshAccessToken()`, **`SessionStore.logout()`** (calls `POST /auth/logout`, ends without `returnUrl` — WR8), `SessionStore.mode: Signal<'full' \| 'enrolment'>`, `SessionStore.beginEnrolment(token)`, `SessionStore.anonymousGuard`, `SessionStore.status: Signal<'anonymous' \| 'restoring' \| 'authenticated' \| 'refreshing' \| 'ended'>` + `SessionStore.endReason()` (stop realtime only on `anonymous`/`ended`, ignore the transient `restoring`/`refreshing`; realtime and `GET /me` run **only** when `mode === 'full'` and `status === 'authenticated'`), `ActivityTracker.isActive()` in `core/session/activity.ts`; `AppErrorMapper` (10.2.2-T1) |

`NavItem = { id: string; labelKey: string; icon: string; route: string; visible: () => boolean }`. `NAV_ITEMS` starts **empty** and the shell route ships with `children: []` (WR bootstrapping rule): each screen task registers **its own** child route under the shell path **and** its own `NAV_ITEMS` entry in the same PR (10.4.1-T1 Dashboard, 10.4.2-T1 Series, 10.5.1-T1 Episodes; the S tasks 10.7–10.9 add Channels/Billing/Account/Admin with their own `visible()`). No stub route, no stub component and no dead link ever exists — a nav entry appears only when its screen is merged.

### 3.4 Persistence rules

Nothing about the session, the user or content is persisted client-side. The single allowed `core/prefs` write is the **UI language** before login (`nts.prefs.uiLocale`, D6/D10); no token, name, e-mail, ticket, event payload or story text ever reaches `localStorage`/`sessionStorage` (RF15, F25). The hub ticket lives in memory for one `start()` call and is never written anywhere (RF2/RF6).

## 4. Use cases

| # | Actor → goal | Flow | States | Errors |
|---|---|---|---|---|
| UC1 | Visitor/user → read the UI in his language | boot: `LocaleService.resolveInitial(profile?.uiLocale, prefs, navigator.languages)` → `TranslocoService.setActiveLang` → `document.documentElement.lang`; route lazy-loads its scope JSON | resolving → active | scope JSON 404 → `missingHandler` (dev: log + key; prod: fallback `en`) |
| UC2 | User → switch language | `LanguageSwitch` → `ShellStore.changeLocale` → `LocaleService.activate(l, {persist:true})` → Transloco re-render + `<html lang>` + `Title` re-translate; if authenticated → `PATCH /api/v1/me { uiLocale }` → `SessionStore.setMe` | switching → applied | `400 validation_failed` / `401` → snackbar `shell.language.saveFailed`, local value kept (server value wins on the next `/me`) |
| UC3 | Signed-in user → live updates | shell mounts → `RealtimeService.start()` → `SessionStore.ensureFreshAccessToken()` → `POST /hubs/ticket` → `start('/hubs/episodes?ticket=…')` → `state='connected'`, `resync` +1 | idle → connecting → connected | ticket `401/429/503` → backoff (honours `Retry-After`); refresh failure → `stop()` + session end |
| UC4 | Server aborts the socket (backstop 5 min / family revocation) | `onClose` → one path: `isActive()`? → refresh → new ticket → connect → `resync`; not active → `paused` | backoff → connecting → connected \| paused | never reconnect without a fresh token (RF6) |
| UC5 | Idle user → realtime off | after the idle window every close leaves `state='paused'`; the `realtime.paused` bar offers **Continue** (a user action) → refresh → ticket → connect → refetch | connected → paused → connected | pause is not an error; no error UI |
| UC6 | Store → subscribe to one episode | `realtime.subscribeEpisode(episodeId, h)`; on `EpisodeUpdated` (matching id) → coalesced (250 ms) `handler()` → the store refetches `GET /episodes/{id}`; unsubscribe removes the live subscription | subscribed → refetching | refetch errors belong to the store (`AppErrorMapper`), realtime stays connected |
| UC7 | Render in progress (wave 2 consumer) | `RenderProgress` → `progress` map updated **without** any HTTP call; a stage change resets `percent` | live | unknown stage → dropped |
| UC8 | Tab returns to the foreground | `visibilitychange → visible` → `resync` +1 → only live subscriptions refetch; D7 gate still applies to any refresh | hidden → visible | — |
| UC9 | User signs out / family revoked | `SessionStore.logout()` (or a server-driven end) moves `SessionStore.status` to `'ended'` (reason via `endReason()`) or `'anonymous'`; `RealtimeService` watches that signal with an `effect()` → `reset()` (hub `stop()`, clear subscriptions, clear the per-user `progress` map, `state='idle'`; a bare `stop()` without a session end only reaches `'stopped'`); it starts only when `mode === 'full'` **and** `status === 'authenticated'` and ignores the transient `'restoring'`/`'refreshing'`; the router goes to `/login` (10.2.1-T1) | connected → stopped → idle | — |
| UC10 | User → navigate | shell nav (`aria-current="page"`) → lazy `loadChildren` → title from `shell.title.*`; the realtime connection is **not** recreated on navigation | — | unknown route → `EmptyState`-less 404 route owned by 10.1.1-T1 |

## 5. API and events

Routes consumed (relative URLs — D3; the generated client from 10.1.4-T1 is the only caller):

| Method | Route | Success | Errors the task handles |
|---|---|---|---|
| POST | `/api/v1/hubs/ticket` | `200 { ticket, expiresAt }` | `401` (refresh once → else session end), `429 rate_limited` + `Retry-After` (backoff = max(backoff, retryAfter)), `503 service_unavailable` (backoff) |
| WS | `/hubs/episodes?ticket=<t>` | `101` upgrade, group `user:{id}` | `401 hub_ticket_invalid` → **one** new ticket then backoff (never a loop); `400` non-WebSocket transport (client bug) |
| GET | `/api/v1/me` | `200 MeDto` (incl. `features[]`, RF11/RF12) | called by the shell (`MeRepository.get()`) on mount **only when `SessionStore.mode === 'full'` and `status === 'authenticated'`** (never in enrolment mode — WR8); result mapped → `SessionStore.setMe` (memory only, never persisted); `401` → session flow of 10.2.1-T1 |
| PATCH | `/api/v1/me` `{ uiLocale }` | `200 MeDto` | `400 validation_failed`, `401 unauthenticated`, `503 service_unavailable` |

Events (client behaviour; payload per RF3 — ids only, `data` only for `RenderProgress`):

| Event | Id used | Client action | Refetch? |
|---|---|---|---|
| `EpisodeUpdated` | `episodeId` (also `seriesId`) | notify live subscriptions for that id | yes — the store refetches the episode detail, coalesced 250 ms (D8) |
| `RenderProgress` | `episodeId` + `renderJobId` | update `RealtimeService.progress` | **no** |
| `PublicationUpdated` | `episodeId` + `publicationId` | notify live subscriptions of the episode | via the subscriber's own policy (wave 2 / 10.4.3-T2) |
| `CreditBalanceChanged` | — | parsed and exposed; **no wave-1 UI** (D14: no credit text in P) | no |
| any other name / unknown `stage` | — | ignored, not logged with payload | no |

Error codes referenced by copy: `hub_ticket_invalid` is never shown to the user (it is silently retried); `errors.rate_limited`, `errors.network_error`, `errors.unknown_error`, `errors.session_revoked` come from the `errors` scope owned by 10.2.2-T1. `feature_disabled` (404) is handled by the owning block, not by the shell.

## 6. UI

### 6.1 Screens / components

| Component | File | Inputs / outputs | Notes |
|---|---|---|---|
| `ShellComponent` | `core/shell/shell.component.ts` | projects `header-end` (D14) and the router outlet | landmarks `<header>`, `<nav>`, `<main id="content">`; skip link first in DOM |
| `TopBarComponent` | `core/shell/top-bar.component.ts` | `@Input() titleKey`, `@Input() menuOpen`, `@Output() menuToggle` | product name (not translated, brand), `[header-end]` slot, `LanguageSwitch`, `UserMenu` |
| `SideNavComponent` | `core/shell/side-nav.component.ts` | `@Input() items: NavItem[]`, `@Output() navigate` | `mat-nav-list`; `aria-current="page"`; `mode="over"` below 600 px with focus restore |
| `LanguageSwitchComponent` | `core/shell/language-switch.component.ts` | `@Input() locale`, `@Output() localeChange` | endonyms `Tiếng Việt` / `English` (never translated); `mat-select` with a label (`shell.language.label`) |
| `UserMenuComponent` | `core/shell/user-menu.component.ts` | `@Input() me: MeSummary \| null`, `@Output() logOut` | `mat-menu`; shows display name + e-mail (memory only); e-mail is a text node (no `[innerHTML]`) |
| `RealtimeStatusComponent` | `core/shell/realtime-status.component.ts` | `@Input() state: RealtimeState`, `@Output() resume` | `paused` → bar + **Continue**; `backoff` → icon + text (never colour only); `aria-live="polite"` |
| `ShellStore` | `core/shell/shell.store.ts` | see §3.3 | `@Injectable()` provided by the shell route |
| `NavItem[]` | `core/shell/nav-items.ts` | const array, **starts empty** | each screen PR adds its own entry + child route (WR bootstrapping rule) |

Icons: **no `MatIconRegistry` SVG sets** (WR12 — they would require `bypassSecurityTrustResourceUrl` and the `angular#components` policy, which the Trusted Types header does not allow). Icons are inline-`<svg>` components owned by 10.1.3-T1 (`IconDashboard`, `IconSeries`, `IconEpisodes`, `IconMenu`, `IconLanguage`, `IconAccount` — names final in that task), rendered with `aria-hidden="true"` next to a visible text label. The default Material icon font and any `uses-material-icons`-style CDN link must not appear in `index.html` (checked by 10.1.6-T1's CSP scan and D4's `font-src 'self'`).

### 6.2 Copy keys (authored here; `vi` + `en` in `scope core`, files `web/src/assets/i18n/core/{vi,en}.json`)

| Key | vi | en |
|---|---|---|
| `shell.nav.dashboard` | Bảng điều khiển | Dashboard |
| `shell.nav.series` | Loạt bài | Series |
| `shell.nav.episodes` | Tập phim | Episodes |
| `shell.nav.label` | Điều hướng chính | Main navigation |
| `shell.menu.label` | Tài khoản của {name} | Account of {name} |
| `shell.menu.logOut` | Đăng xuất | Sign out |
| `shell.language.label` | Ngôn ngữ | Language |
| `shell.language.vi` / `.en` | Tiếng Việt / English | Tiếng Việt / English |
| `shell.language.saveFailed` | Không lưu được ngôn ngữ. Vui lòng thử lại. | Could not save your language. Please try again. |
| `shell.realtime.connected` | Đã kết nối trực tiếp | Live updates on |
| `shell.realtime.reconnecting` | Đang kết nối lại… | Reconnecting… |
| `shell.realtime.paused` | Cập nhật trực tiếp đang tạm dừng | Live updates paused |
| `shell.realtime.continue` | Tiếp tục | Continue |
| `shell.skipToContent` | Chuyển tới nội dung chính | Skip to main content |
| `shell.title.dashboard` / `.series` / `.episodes` (document titles) | Bảng điều khiển… | Dashboard… |
| `common.close` / `common.retry` / `common.cancel` / `common.back` / `common.loading` | Đóng / Thử lại / Huỷ / Quay lại / Đang tải… | Close / Retry / Cancel / Back / Loading… |

Key style `scope.area.item`, camelCase leaves (RF9). `session.*` in the same core scope and the whole `errors` scope belong to 10.2.2-T1/10.2.1-T1 (agreed with `W10Session`, 2026-09-24): 10.2.5-T1 creates the scaffold with `common.*` only, so both writers edit disjoint sub-objects. `transloco-keys-manager` with marker `t` keeps used-key extraction honest; the module-level `{ provideTranslocoScope }` imports in feature route files are the only place a scope is declared.

### 6.3 Accessibility (D16, screen-specific)

* Landmarks: one `<header>`, one `<nav aria-label>` (`shell.nav.label`), one `<main id="content">`; skip link is the first focusable element and moves focus to `#content` (`tabindex="-1"`).
* Active nav item: `aria-current="page"` **and** a visible marker (never colour only, ≤ 4.5:1 in both themes).
* `RealtimeStatusComponent` is an `aria-live="polite"` region whose text changes only on state change (no per-event chatter); the Continue button is keyboard reachable and ≥ 24 px.
* Language switch and user menu: labelled triggers, `aria-expanded` on the menu trigger, focus trapped in the overlay menu and restored on close, Escape closes.
* Responsive: at 360 px the side nav becomes an overlay (`mode="over"`) opened by a labelled hamburger with `aria-expanded`; no horizontal scroll (verified in e2e).
* `prefers-reduced-motion: reduce` disables the reconnect indicator's animation.
* Titles/labels translated (`Title.setTitle` via `shell.title.*`) so a screen reader announces the page in the current language; `<html lang>` follows `LocaleService`.

**Design (owner rule 2026-09-25).** `10.2.4-T0` is the folder's design gate (D19): wireframes + hi-fi mockups for every shell surface above at 360/768/1280, dark + light, vi + en, with focus order/keyboard path/`aria-live` annotations, built from the D11 tokens/components. Design link + owner approval record: ⚠️ [OPEN] (`10.2.4-T0-ui-design.md` §Outputs). Every task below lists `10.2.4-T0` as a hard `Depends on`.

## 7. Jobs and integrations

No backend job is added. Client-side timers, all created through `DestroyRef`-scoped helpers (RF17) and all silent while `document.visibilityState !== 'visible'`:

| Timer / loop | Owner | Interval | Gate |
|---|---|---|---|
| Reconnect backoff | 10.2.4-T1 | 1→30 s + jitter | D7 `isActive()`; a paused service schedules nothing |
| Refetch coalescing | 10.2.4-T1 | 250 ms per key | live subscriptions only |
| Ticket `Retry-After` delay (429) | 10.2.4-T1 | value of the header | — |
| Input throttle (activity) | 10.2.1-T1 (`activity.ts`) | 1/10 s | — |
| Activity-toast / progress timers | other tasks | — | — |
| `visible-poll` (produced by 10.2.6-T1, WR4) | 10.4.3-T2 | caller's `intervalMs` (30 s) | visible **and** `isActive()` (D7); immediate run on return; single in-flight |

Integrations: `@microsoft/signalr` 10.0.11 (RF18) — wrapped in exactly one file so nothing else imports it; `@jsverse/transloco` 8.4.0 + `@jsverse/transloco-keys-manager` 8.1.1 (dev); Angular `HttpClient` from 10.1.4-T1; `Intl` + `timeZone` from the platform. `visible-poll` (`core/state/visible-poll.ts`, D17) is **produced here** (WR4) and its only wave-1 consumer is 10.4.3-T2; neither the shell nor the realtime service polls.

Background vs user requests (WR11): requests that originate from a user event carry the `HttpContext` flag `USER_ACTION`; resync-driven and event-driven refetches (`subscribeEpisode`) and any polling run **without** it, so a 401 on them refreshes only when `isActive()` — otherwise realtime and polling pause instead of reviving the session from an idle tab.

## 8. Security and privacy

* **Tokens**: the access token only ever travels in the `Authorization` header added by the 10.2.1-T1 interceptor. The hub URL carries a 60-s single-use ticket, never the token (RF1/RF6); `HubConnectionPort.start(url)` receives the URL and `SignalrHubConnection` keeps no copy of it after `start()`; the ticket is never logged (no `console.*`, no `logger` call with the URL) and never persisted (§3.4).
* **Reconnect cannot stack**: single-flight `connect()`; a close during a connect is ignored until the attempt settles; the 429 `Retry-After` is always respected, so a revoked/misconfigured session cannot generate a ticket flood (policy `hub` = 10/min, RF2).
* **XSS**: no `[innerHTML]`, no `bypassSecurityTrust*` (D2). All copy is rendered through interpolation; translation interpValues are inserted as text. Event payloads are never rendered raw — only mapped fields (`stage` allow-list, `percent` numeric) reach the DOM.
* **No content in storage/logs**: no user name, e-mail, story text, event payload or ticket is written to `localStorage`/`sessionStorage` (`core/prefs` exception: UI language only), and no payload is passed to console/telemetry [DECISION: wave 1 has no client telemetry].
* **Session end**: `SessionStore.logout()` or a server-driven end clears the subscription registry, clears the per-user `progress` map, stops the socket and ends in `state='idle'` (a bare `stop()` without a session end ends in `'stopped'`); `RealtimeService` registers with `SESSION_SCOPED` (10.2.1-T1) so the reset fan-out reaches every root-level store, and a second user on the same device cannot be served stale data, a live channel or leftover progress (F22).
* **Background requests cannot revive an idle session** (WR11): resync/event refetches and polling carry no `USER_ACTION` flag, so a 401 on them refreshes only while `isActive()` — an abandoned tab cannot hold a 30-min-idle owner session open by itself (D7).
* **Open redirect**: the shell never builds a `returnUrl`; §6.2 language switch and nav use static relative routes (F21 applies to 10.2.1-T1/10.3.3-T1).
* **CSP fit**: no inline script/style in the shell beyond what D4 allows; no third-party origin is contacted (icons are inline-`<svg>` components from 10.1.3-T1, fonts are self-hosted, no icon-or-font CDN and no `MatIconRegistry` SVG set — WR12; `connect-src 'self'` covers the hub ⚠️ → the 10.1.6-T1 e2e over `wss://` confirms it, and a `securitypolicyviolation` fails that spec).
* **Idempotency/ETag**: not applicable — the only writes are `PATCH /me { uiLocale }` (last-write-wins, no ETag per RF12) and the idempotent ticket POST; the realtime task performs no content write.

## 9. Test plan

Vitest + TestBed (zoneless) + jsdom with hand-written fakes (RF13); `vi.useFakeTimers()` for backoff/coalescing; a seeded RNG injected into `nextDelayMs` so jitter is asserted exactly. Playwright for the three e2e specs (login per file through the UI, `workers: 1`, axe on each visited page per D16, CSP fixture from 10.1.6-T1).

| Task | Unit / component (Vitest) | E2E (Playwright) |
|---|---|---|
| 10.2.5-T1 | `locale.service.spec.ts`: resolution order (profile > pref > browser > vi), unsupported browser tag → `vi`, `Intl` output for `vi-VN`/`en-US` with a fixed instant, `formatInZone` prints the zone for a schedule value, `activate` persists once and writes no other key, a persistence failure keeps the local locale. `i18n-keys.spec.ts`: parity computed by the shared `flattenKeys` for every scope on disk (both directions, including the `quality` scope once its files land), duplicate leaf detection, a deliberately missing key fails, `matchesPattern` wildcard semantics (`*` matches exactly one segment, a concrete key matches, a two-segment skip does not), and the **used-key** diff (a key present in `i18n:find` output but absent from every flattened set is reported). `transloco-loader.spec.ts`: URL shape, per-scope+lang caching, HTTP error → `missingHandler`/fallback | `web/e2e/i18n.spec.ts`: switch vi↔en, assert two visible strings, `document.documentElement.lang`, no raw `\w+\.\w+` key rendered, axe in both languages |
| 10.2.4-T1 | `realtime.service.spec.ts` with `FakeHubConnection` + `FakeHubTicketRepository`: `start()` issues exactly one ticket and connects with `skipNegotiation`/WebSockets (asserted on the fake's recorded options object), no `accessTokenFactory`; close → backoff `1,2,4,8,16,30 s` + jitter and a **new** ticket per attempt; 401 `hub_ticket_invalid` → one extra ticket then backoff; ticket 429 → delay = `Retry-After`; refresh failure → `stop()`, `state='stopped'`, no further ticket; `isActive()===false` at close → `state='paused'` and zero ticket calls; `resume()` → refresh → ticket → connect → `resync` +1; `resync` on `visibilitychange → visible`; coalescing: three events inside 250 ms → one handler call; `RenderProgress` → map updated, zero HTTP calls; unknown name/stage dropped; `stop()` clears subscriptions and rejects later events; `subscribeEpisode` unsubscribe removes the live key; the **canonical PascalCase** names drive `WIRE_TO_NAME` (fakes emit them) and the snake_case NOTIFY aliases (`render_progress`) resolve to the same names; `stop()` → `state='stopped'`, `reset()` clears `progress`/subscriptions → `'idle'`; a resync-driven refetch carries **no** `USER_ACTION` flag | `web/e2e/realtime.spec.ts`: owner login through the WR14 helper, open an episode, force-close via `context.setOffline(true/false)`, assert exactly one reconnect and **one** refetch per live subscription (count `/api/v1/episodes/` requests), assert no ticket/`t=` appears in any captured console line or in a later request URL, axe |
| (WR4 helpers, 10.2.6-T1) | `cursor-page.spec.ts` (items/nextCursor shapes, `null` cursor = last page, `limit > 100` rejected), `cursor-list.spec.ts` (append, single in-flight guard, `hasMore=false` stops, error keeps the loaded items and exposes the `AppError`, no request without an explicit `loadMore()`), `visible-poll.spec.ts` (runs while visible+active, does not run while hidden or after the idle window, immediate run on return to visible, single in-flight, `stop()` clears the timer via `DestroyRef`, `intervalMs` respected with fake timers) | — (exercise via 10.4.3-T2's run-log spec with `page.clock`) |
| 10.2.6-T1 | `shell.component.spec.ts` / `side-nav.component.spec.ts` / `language-switch.component.spec.ts` / `user-menu.component.spec.ts` / `realtime-status.component.spec.ts`: shell mount calls `MeRepository.get()` exactly once (only when `mode === 'full'` and `status === 'authenticated'`; never in enrolment mode) and `SessionStore.setMe` with the mapped summary (a second mount does not double-fetch while `me()` is set); `MeDto → MeSummary` maps `features[]` into the typed `FeatureKey` allow-list and drops unknown keys; the nav renders exactly the entries present in `NAV_ITEMS` (empty at merge time — the spec injects its own entries) and no other item; `aria-current="page"` follows the active route; language switch calls `LocaleService.activate` + `MeRepository.updateUiLocale` once (no duplicate call on a second click within the same tick); `PATCH` failure → snackbar, locale kept; user menu → `logOut` output → `SessionStore.logout()` called once; `realtime.paused` bar appears only for `paused` and the Continue button calls `resume()`; titles come from `shell.title.*`; icons are the 10.1.3-T1 inline-`<svg>` components with `aria-hidden` | `web/e2e/shell.spec.ts`: skip link focus, landmarks, `aria-current`, 360 px viewport with no horizontal scroll, overlay nav keyboard round-trip with focus restore, axe |

Coverage discipline (RF13): no mocking library; fakes implement the ports; no test asserts a source string or wiring.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 10.2.5-T1 | Transloco scopes, locale formats, missing-key CI | S (0.5) | 10.1.1-T1 |
| 10.2.4-T0 | UI design (app shell, nav, language switch, user menu, realtime status) | S (0.5) | 10.1.3-T1, 09.8.1-T2 (soft) |
| 10.2.4-T1 | Realtime service: hub ticket, WebSockets only, reconnect + refetch | M (1.0) | 10.2.1-T1, 09.8.1-T2 |
| 10.2.6-T1 | Shell layout, navigation, language switch, user menu | S (0.5) | 10.1.3-T1, 10.2.1-T1, 10.2.4-T1, 10.2.5-T1, 09.3.4-T1 |

Story total **2.5 d raw / 3.0 buffered** (2.0 d implementation + 0.5 d `10.2.4-T0` design, D19/owner rule 2026-09-25), matching README §9 ("10.2 core 3.75 unchanged" = 10.2.2-T1 0.5 + 10.2.1-T1 1.0 + 10.2.3-T1 0.25 + this folder 2.0 + 0.5 design). Blocks (README §8.1): 10.2.5-T1 → 10.2.2-T1, 10.2.6-T1, 10.3.1-T1; 10.2.4-T1 → 10.2.6-T1, 10.4.3-T2, 10.5.2-T1; 10.2.6-T1 → 10.4.2-T1, 10.4.1-T1, 10.4.3-T1, 10.5.1-T1, 10.9.1-T1 (10 CR-37).

**Cross-folder seams — [OPEN] / notes (no peer file is edited from here)**

1. **Event names** (RF21/§3.1, WR17a): the canonical client method names are `EpisodeUpdated`, `RenderProgress`, `PublicationUpdated`, `CreditBalanceChanged` (00 §8; `09.8.1-realtime/plan.md:170`); `render_progress` (`plan.md:82`) is the internal NOTIFY `event` value that the Api listener maps before `SendAsync`, kept as a tolerant alias. Both forms live **only** in `WIRE_TO_NAME`, so an upstream rename is a one-line change; the integration smoke must assert at least one `EpisodeUpdated` reaches a subscribed store, and `snakeCaseAliases_ResolveToTheSameNames` proves the alias entry.
2. [OPEN] **`Hubs:MaxConnectionMinutes`** — RF1 (owner 5 / user 15) is the connection cap; the client must not hard-code it (it reconnects on close, whatever the cause). If the value ever exceeds the token lifetime, UC4 still holds because a reconnect always refreshes first.
3. Note for **10.1.5-T1** (`web.yml`): the `i18n check` step runs `npm run i18n:check`, produced by 10.2.5-T1 — parity **and** used-key extraction in one command (README §3 D10, D15 already list the step). No compose/e2e runs in `web.yml` (WR3).
4. Note for **10.3.1-T1** (prerendered public pages): 10.2.5-T1 provides `PRERENDER_SAFE_SCOPES` + the bundled-scope mechanism, because a prerender cannot fetch `/assets/i18n/**`; the public feature must import its own scope JSONs into that map (hand-over, one line per language).
5. Note for **10.2.1-T1** (`W10Session` + **WR8**): this folder consumes `ActivityTracker.isActive()`, `SessionStore.ensureFreshAccessToken()`, **`SessionStore.logout()`** (the shell's sign-out action; it calls `POST /auth/logout` and ends without `returnUrl`), `SessionStore.me`/`setMe` (`core/session/me-summary.ts`), `SessionStore.hasFeature(key)` (**consumed, never defined here**), `SessionStore.mode`/`beginEnrolment`/`anonymousGuard`, and `SessionStore.status` + `endReason()`. `RealtimeService` starts only when `mode === 'full' && status === 'authenticated'` (so it never opens a socket in enrolment mode) and stops+resets on `anonymous`/`ended`, ignoring the transient `restoring`/`refreshing`; it also registers its `reset()` with the `SESSION_SCOPED` fan-out so the per-user `progress` map is cleared. The shell's `core/session/data-access/me.http-repository.ts` owns `GET`/`PATCH /me` and the `MeDto → MeSummary` mapping (`MeDto` fields per `09.3.4-T1-me-endpoints.md:22-24` ✅).
6. Note for **10.6.1-T1** (wave 2): `RealtimeService.progress` is the single source for the progress bar; the stage allow-list of RF5 lives in `core/realtime/realtime.model.ts` and must be extended there, not in the screen.
7. Note for **10.2.6-T1**: `GET /me` returns `features[]` — 10 CR-10 **is** applied (`MeDto.Features`, `09.3.4-T1:22-24`) — carrying the enabled keys of the allow-list `source_finder`, `youtube_publish` (empty until 09.9.2-T2 lands), and D14/O1 (`hasFeature`) reads it. The client still maps `features` **defensively** (`me.features ?? []`, unknown keys dropped), so a deploy skew or an older API build degrades to "flag hidden" instead of a crash — not a substitute for the field.
8. Note for **10.1.3-T1**: the shell consumes its inline-`<svg>` icon components (WR12) and `CharCounter`/`CopyableCode`; no `MatIconRegistry` SVG set and no icon-font CDN link may be introduced (D4 `font-src 'self'`, D11). Note for **10.2.5-T1** (WR15): the `quality` scope files are authored by the hints task (10.5.10-T1 / 02 copy), this folder only guarantees the parity+used-key check covers them once the directory exists.

## 11. Rollout

All three tasks are **P · M1**, unconditional — no feature flag guards i18n, realtime or the shell; only later navigation entries (Channels, Billing, Account, Admin) arrive with their own S tasks (D14). Merge order `10.2.5-T1 → 10.2.4-T1 → 10.2.6-T1` inside one PR series (the shell is the first consumer of both). Rollback: the shell is the app's only layout, so a revert reverts the shell and the i18n/realtime wiring together; the realtime service degrades safely on its own (a hub that answers 503 leaves the UI fully functional with no live updates — screens fall back to their own load/refetch, `state='backoff'` is shown as a passive indicator, never a blocking error). No migration, no config key, no server change. Observability: none beyond the browser console in dev (no client telemetry in wave 1, §8). Verification before "done": the three e2e specs + axe green in `e2e.yml`, `web.yml` green (including `i18n:check` and the gzip budget with the core scope added), and the gate **10-W1-session** (RF20) passes on the compose stack.
