# 10.3 — Public signup (S slice of 10.3.1, 10.3.2, 10.3.3-T2): landing, pricing, registration, verify-e-mail and the CAPTCHA widget

> Detailed feature plan. Contract: `../README.md` Part C (§C0 folder brief, §C1 facts SC1–SC31, §C3 SD1–SD16, §C6 template, `### C8.1` canonical rows, §C7 gates, §C9 re-estimate, §C10 rulings ST-1…ST-10); Part A D1–D18 and Part B WT-W1…WT-W15 stay in force. Template: `../../00-overview.md` §10.1.
> Stories: 10.3.1-T2 (landing + pricing, prerendered), 10.3.2-T1 (register + verify-e-mail), 10.3.3-T2 (CAPTCHA widget + progressive-delay countdown). Design gate: **10.3.1-T0** (SD11, owner rule 2026-09-25). All four are **S · M2**.
> Legend: ✅ read from a file (`path:line`, 2026-09-25) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

In scope (wave 3, S · M2 — the only **unauthenticated** surfaces of the product):

| Task | Delivers |
|---|---|
| 10.3.1-T0 (M) | UI design for every screen/state in §6 — wireframes + hi-fi mockups, 360/768/1280, vi+en, dark+light, focus order, keyboard path, a11y notes, built from the D11 tokens/components; owner approval recorded in §6. **Hard dependency of the three implementation tasks.** |
| 10.3.1-T2 (S) | Prerendered `/<lang>/` landing, `/<lang>/pricing`; `/` prerendered language redirect; the prerender list + sitemap + `robots.txt`; SEO head per language; pricing tiers from checked-in content (ST-1). |
| 10.3.2-T1 (S) | `/register` (the SC30 body, three consents, `inviteCode?`, `uiLocale?`, `captchaToken?`) and `/verify-email` (fragment token, verified/expired/invalid/resent). |
| 10.3.3-T2 (XS) | `CaptchaProvider` port + `turnstile.provider.ts` + `CaptchaWidget`; progressive-delay countdown on `captcha_required`/`rate_limited`; `breached_password` field error; CSP delta (CR-28) and the `securitypolicyviolation` spec. |

Out of scope, named owner: the P part of 10.3.1 and all of 10.3.3 (login/forgot/reset/MFA) → `10.3-auth-legal/` (10.3.1-T1, 10.3.3-T1, 10.3.3-T3); the legal-page components and `legal-to-json.mjs` → 10.3.1-T1 (WR1: this folder only owns the prerender list and links the pages); `/help` → 08.3.1-T2, `/report` → 13.3.4-T1 (both consume this folder's prerender list and mount the widget, ST-10); `/billing` packages/promo/checkout → 10.8.1-T1; cookie notice → 13.1.4-T1; the accessibility audit harness → 10.10.1-T1.

Gates this folder feeds: **M2 exit** (`../README.md:773`) — "Registration → verify → login works in the compose stack with Turnstile mounted and zero CSP violations". This folder owns the registration half.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| F1 | Folder = `10.3.1-T0 (M), 10.3.1-T2 (S), 10.3.2-T1 (S), 10.3.3-T2 (XS)`; design owner = the folder's `-T0` | README:556,732 ✅ |
| F2 | Routes: `/` → `/<lang>/` redirect; `/<lang>/{,pricing,report,help,privacy,terms}` static; `/register`, `/verify-email` (`auth`) | SD1:642-644 ✅ |
| F3 | Prerender exactly `/<lang>/{,pricing,privacy,terms,report,help}`, `lang` ∈ `vi`,`en`; `/` prerendered client redirect (`Intl`, `vi` default); **no API call during prerender** | SD2:657 ✅ |
| F4 | Public pages ship `title`/`description`/`canonical`/`hreflang`+`x-default`/`og:`; no third-party asset; `web/public/sitemap.xml` six per lang; `robots.txt` disallows `/admin`,`/account`,`/billing`,`/cancel-request` | SD2:657 ✅ |
| F5 | `GET /billing/packages` is **authenticated** → pricing from `web/src/content/pricing/<lang>.json` (structure only, **no prices of record invented**); CTAs `/register` and `/billing` (signed in); a published price must exist in `GET /billing/packages` (e2e) | SD3/ST-1:659,800 ✅ |
| F6 | `CaptchaProvider { readonly siteKey; render(el,opts): Promise<{token}>; reset() }`; impl `turnstile.provider.ts`; vendor = Cloudflare Turnstile (09 W3-O2) | SD4:661 ✅ |
| F7 | `shared/ui/captcha-widget.ts`: OnPush, inputs `siteKey`/`visible`/`resetKey`, outputs `token`/`expired`/`error`/`heightChange`, from `@shared/ui`; **no store, no `HttpClient`** | SD4:661 ✅ |
| F8 | `turnstile-loader.ts` loads `api.js` **on demand** (idempotent, Trusted-Types-scoped to the fixed URL) from `CaptchaWidget`'s first mount — never a static tag in `index.html`, so `/cancel-request` never requests it (09 W3 CR-34); provider renders on demand | SD4:661, ST-2:801 ✅ |
| F9 | `gen-csp.mjs` + `CAPTCHA_ORIGINS`: `script-src`/`style-src` += `https://challenges.cloudflare.com`, add `frame-src` same; pre-clearance off; TT header unchanged (else ST-2 re-opens vendor) | SD4:661, FC1:617, CR-28:701 ✅ |
| F10 | Entrypoint writes `assets/runtime-config.json` (`{ captchaSiteKey }`, `no-store`) from `NTS_CAPTCHA_SITE_KEY`; read once by `core/config/runtime-config.ts`; public value only | SD4:661, ST-7:806 ✅ |
| F11 | Register body exactly `{ email, password, consents: [{ document: "tos"\|"privacy"\|"age_18", version }], inviteCode?, uiLocale?, captchaToken? }`; `uiLocale` = Transloco language | SD5:663, SC30:609 ✅ |
| F12 | Fields: email, password (`shared/util/password-rules.ts`), confirm, 3 consents (18+ from 13.2.3-T1; versions from content, SC16), `inviteCode` when `registration.mode = invite`, `CaptchaWidget` (present when `open`, else revealed after 400 `captcha_required`; never pre-loaded) | SD5:663 ✅ |
| F13 | `registration_closed`/invite errors → neutral copy; `breached_password` binds to password; `rate_limited` → `RetryCountdown`; existing e-mail = same neutral page; **no auto-login**; `/verify-email` reads `#token=` + `history.replaceState` → verified/expired/invalid/resent | SD5:663, FC6:622 ✅ |
| F14 | `captcha_required` → widget; `captcha_invalid` → reset + clear field; `captcha_unavailable` on register → **fail-closed** (retry/`RetryCountdown`, no token-less submit — 09 W3 CR-34; fail-open only on login/forgot/reauth); tokens single-use ≤ 300 s; field `captchaToken`; reused by 10.3.3-T1/T3 and 13.3.4-T1 | SC14:595, SD5:663 ✅ |
| F15 | `captcha_required` after 3 failures per account+IP/device, progressive delay each further; with `rate_limited` drives the countdown | SC13:594, 10-web-ui.md:358 ✅ |
| F16 | Codes rendered: `captcha_required` 400, `captcha_invalid` 400, `captcha_unavailable` 503, `breached_password` 400, `registration_closed` 403, `forbidden` 403, `validation_failed` 400, `rate_limited` 429, `age_attestation_required` | SC13:594, FC10:626 ✅ |
| F17 | Register indistinguishable for an existing e-mail; modes `open`/`invite`/`closed`; `inviteCode` required when `invite`; 403 `registration_closed` when `closed` | FC6:622, SC30:609 ✅ |
| F18 | Legal versions are build-time content (`web/src/content/legal/<prefix>.<version>.<lang>.md` (manifest key -> file prefix: `tos -> terms`, `privacy -> privacy`; 13 CR-34) via `tools/web/legal-to-json.mjs`); version shown = version the API records | SC15/SC16:596-597 ✅ |
| F19 | WCAG 2.2 AA (CR-23): axe tags `wcag2a,wcag2aa,wcag21a,wcag21aa,wcag22aa`; manual matrix owned by 10.10.1-T1/T2 | SD12:677, CR-23:694 ✅ |
| F20 | Transloco scopes `public`,`auth` exist; `errors` gains every FC10 code; style `scope.area.item`; vi default, both languages; marketing copy in `content/<area>/<lang>.json` | SD13:679 ✅ |
| F21 | Reuse: `shared/ui/*` (D11), `RetryCountdown`, `AppErrorMapper`+`bindFieldErrors`, `gen-csp.mjs`, CSP fixture (10.1.6-T1), i18n key check (10.2.5-T1), compose + Playwright (10.1.5-T1), `legal-to-json.mjs` | SD14:681 ✅ |
| F22 | CAPTCHA vendor free tier only, licence + privacy documented, owner-approved before 10.3.3-T2 starts (ST-2, `[OPEN]` 09:602); no analytics/ads/fonts/CDN on public pages | SD15:683, S-O3:792 ✅ |
| F23 | Widget a11y: iframe `title`, error state, keyboard path, `aria-live` countdown, documented fallback if not keyboard-usable | FC16:632 ✅ |
| F24 | No new hub events; polling only via `core/state/visible-poll.ts` (not needed by these screens) | SD16:685 ✅ |
| F25 | CSP fixture gains register/login specs failing on any `securitypolicyviolation` with the widget mounted | CR-28:701 ✅ |
| F26 | 10.3.1-T2 owns prerender list, `/` redirect, sitemap, `/help` + `/report` paths; 08/13 depend on it instead of editing `app.routes.server.ts` | ST-10:809 ✅ |
| F27 | 09 back-edges (CR-25): 09.3.1-T2 `Blocks` += `10.3.2-T1`; 09.3.3-T2 `Blocks` += `10.3.3-T2` | CR-25:696 ✅ |
| F28 | Prereqs already detailed (depend, do not reopen): 10.1.3-T1, 10.1.5-T1, 10.1.6-T1, 10.2.1-T1, 10.2.2-T1, 10.2.5-T1, 10.3.1-T1, 10.3.3-T1 | SC29:611 ✅ |
| F29 | **No client-visible `registration.mode`**: 00 §8:359 has no such route; 09's `registration.mode` `system_settings` row + `IRegistrationPolicyProvider` are **server-side only** (09 README:1027,1096) and a wrong `inviteCode` is indistinguishable from closed mode (403 `registration_closed`; 09 README:1040). SD5's "widget always when `open`" therefore has no public source → **[OPEN]**; proposed CR: extend ST-7's public `assets/runtime-config.json` with `registrationMode` (or add a public policy route); documented fallback meanwhile: reveal the widget on 400 `captcha_required`, and key invite/closed copy to the single code 09 names (`registration_closed`) — no second code is invented | 09 README:1027,1040,1096; README:806 (ST-7) ⚠️ |

## 3. Domain and data

No database, no persistence of secrets, no API call during prerender. Client domain models live in `features/*/domain` and import nothing but `domain` + `@shared/util` (D2); routes/params/types come from `@shared/api` only inside `data-access` (D5).

### 3.1 `features/public/domain`

```ts
// public.model.ts
export type UiLang = 'vi' | 'en';
export interface PricingTier {
  readonly id: string;                 // stable key, e.g. 'starter' | 'creator' | 'studio'
  readonly nameKey: string;            // i18n key — no price text here
  readonly featureKeys: readonly string[];
  readonly ctaHref: '/register' | '/billing';
  readonly pricePlaceholder: string;   // '—' until the owner supplies figures (ST-1)
}
export interface PricingContent { readonly tiers: readonly PricingTier[]; readonly note: string; }
export interface PublicPageMeta {
  readonly title: string; readonly description: string;
  readonly canonicalPath: string; readonly alternates: Readonly<Record<UiLang | 'x-default', string>>;
  readonly og: Readonly<Record<string, string>>;
}
export interface SitemapEntry { readonly path: string; readonly alternates: Readonly<Record<UiLang, string>>; }
```

### 3.2 `features/auth/domain` (register slice; login slice belongs to 10.3.3-T1)

```ts
// register.model.ts
export type ConsentDocument = 'tos' | 'privacy' | 'age_18';
export interface ConsentAcceptance { readonly document: ConsentDocument; readonly version: string; }
export type RegistrationMode = 'open' | 'invite' | 'closed';
export interface RegisterRequest {
  readonly email: string; readonly password: string;
  readonly consents: readonly ConsentAcceptance[];
  readonly inviteCode?: string; readonly uiLocale: UiLang; readonly captchaToken?: string;
}
export type RegisterOutcome =
  | { kind: 'accepted' }                                   // 202 → neutral "check your e-mail" (09 W3 CR-32)
  | { kind: 'captcha_required' }                           // 400
  | { kind: 'captcha_invalid' }                            // 400 → reset widget, clear field
  | { kind: 'captcha_unavailable' }                        // 503 on register → fail-closed, retry (no token-less submit, 09 W3 CR-34)
  | { kind: 'breached_password'; field: 'password' }        // 400
  | { kind: 'registration_closed' }                        // 403 (also a wrong invite code, 09 README:1040)
  | { kind: 'rate_limited'; retryAfterSeconds: number };   // 429
export type VerifyEmailOutcome = 'verified' | 'expired' | 'invalid' | 'resent';
```

### 3.3 Content and config

- `web/src/content/pricing/<lang>.json` — marketing tiers only (F5). Schema mirrors `PricingContent`; validated by a build-time shape check.
- `web/src/content/legal/<prefix>.<version>.<lang>.md` (manifest key -> file prefix: `tos -> terms`, `privacy -> privacy`; 13 CR-34) — owned by 13.1.1-T1 / 13.1.2-T1; converted by `tools/web/legal-to-json.mjs` (10.3.1-T1). This folder **reads only the version strings** for the consent checkboxes (F18).
- `assets/runtime-config.json` — `{ "captchaSiteKey": "…" }`, `Cache-Control: no-store`, written at container start from `NTS_CAPTCHA_SITE_KEY`, read once by `core/config/runtime-config.ts` (F10).

### 3.4 Tables, columns, indexes

None. This folder owns no schema, no migration, no SQL (00 §6 untouched).

## 4. Use cases

- **UC-1** `/` — prerendered redirect resolves language (`Intl` → browser, `vi` default) → `history.replaceState` to `/<lang>/`; stored preference wins.
- **UC-2** `/<lang>/` — prerendered landing with hero + CTAs; hydration fetches nothing; JS-off keeps static HTML + links.
- **UC-3** `/<lang>/pricing` — tiers from `content/pricing/<lang>.json`, placeholder price until the owner supplies figures, CTA `/register` (or `/billing` when signed in); no live package call during prerender (F5).
- **UC-4** `/register` submit — build the SC30 body (consents with versions, `uiLocale`, `captchaToken?`) → **202** → neutral "check your e-mail", no auto-login (09 W3 CR-32). Alternates: 400 `captcha_required` reveals the widget; 400 `captcha_invalid` resets it; 503 `captcha_unavailable` is **fail-closed** — `RetryCountdown`, no token-less submit (09 W3 CR-34); 400 `breached_password` binds to password; 403 `registration_closed`; 429 `rate_limited` → `RetryCountdown`.
- **UC-5** `/verify-email#token=…` — read fragment, `history.replaceState` (D6), POST `/auth/verify-email` → verified / expired / invalid / resent.
- **UC-6** 3 failures — widget appears (or `captcha_required` arrives) → solve → token attached; progressive-delay countdown (`aria-live`) on each further failure.
- **UC-7** repeated attempts — server enforces indistinguishability + rate limit; client shows identical neutral copy, never reveals existence.

## 5. API and events

| Route | Verb | Body / params | Responses used here | Source |
|---|---|---|---|---|
| `/auth/register` | POST | `{ email, password, consents: [{ document: "tos"\|"privacy"\|"age_18", version }], inviteCode?, uiLocale?, captchaToken? }` | 202 (identical body for a new and an existing e-mail, 09 W3 CR-32); 400 `validation_failed`/`breached_password`/`captcha_required`/`captcha_invalid`; 403 `registration_closed`; 429 `rate_limited` (+`Retry-After`); 503 `captcha_unavailable` | F11, F16, F17, 09 README:1040 ✅ |
| `/auth/verify-email` | POST | `{ token }` (from the URL fragment) | 204 verified; 400 `validation_failed` (expired/invalid); 429 `rate_limited` | F13 |
| `GET /billing/packages` | GET | — | **not called by this folder** (authenticated; pricing is content-driven, F5) | F5 |
| `assets/runtime-config.json` | GET (static) | — | `{ captchaSiteKey }`, `no-store` | F10 |

Route authority: `POST /auth/register` + `/auth/verify-email` are owned by 09.3.1-T3 (09 README §C1:1040; `RegisterCommand(Email, Password, Consents, InviteCode, UiLocale, CaptchaToken)`); `GET /billing/packages` is owned by 12.7.1-T1 and returns `{ items: CreditPackageDto[] }` with `CreditPackageDto { id, code, nameVi, nameEn, credits, bonusCredits, priceVnd, priceUsd | null, sortOrder }` (12 README:219) — **not called here** (F5). **F29 `[OPEN]`**: no client-visible `registration.mode` exists (00 §8:359 has no route; 09's provider is server-side), so the mode-conditional widget/invite field has no source yet; fallback = reveal the widget on 400 `captcha_required` (see §11).

No SignalR events are consumed (SD16, F24). No outbox events, no jobs, no `[JobPolicy]` — this is a frontend-only slice.

## 6. UI

Screens and states (all owned by this folder; the design link below covers every one at 360/768/1280, vi+en, dark+light, focus order + keyboard path, a11y notes, D11 tokens/components):

| Screen / route | Feature | States to design and implement |
|---|---|---|
| `/` → `/<lang>/` | `public` | prerendered redirect only (no visible screen) |
| `/<lang>/` | `public` | static hero + CTAs (no loading/error — no fetch) |
| `/<lang>/pricing` | `public` | static tiers; placeholder price; signed-in CTA variant |
| `/register` | `auth` | form, captcha visible/hidden, submitting, neutral "check your e-mail", closed, invite-required, rate-limited |
| `/verify-email` | `auth` | verified / expired / invalid / resent |
| `CaptchaWidget` (shared) | `shared` | idle, loading, ready, expired, error, unavailable |

**Design deliverable (SD11, hard gate). Link (recorded 2026-10-02):** review deck [`design/ui/index.html`](../../../../design/ui/index.html) → screen 02 [`screens/02-public.html`](../../../../design/ui/screens/02-public.html): landing [`#f-landing-hero`](../../../../design/ui/screens/02-public.html#f-landing-hero) · [`#f-landing-how-topics`](../../../../design/ui/screens/02-public.html#f-landing-how-topics) · [`#f-landing-faq-footer`](../../../../design/ui/screens/02-public.html#f-landing-faq-footer) · [`#f-cookie-notice`](../../../../design/ui/screens/02-public.html#f-cookie-notice); pricing [`#f-pricing`](../../../../design/ui/screens/02-public.html#f-pricing) · [`#f-pricing-signed-in`](../../../../design/ui/screens/02-public.html#f-pricing-signed-in); `/register` [`#f-register-idle`](../../../../design/ui/screens/02-public.html#f-register-idle) · [`#f-register-age-gate`](../../../../design/ui/screens/02-public.html#f-register-age-gate) · [`#f-register-errors`](../../../../design/ui/screens/02-public.html#f-register-errors) · [`#f-register-breached`](../../../../design/ui/screens/02-public.html#f-register-breached) · [`#f-register-captcha-required`](../../../../design/ui/screens/02-public.html#f-register-captcha-required) · [`#f-register-captcha-invalid`](../../../../design/ui/screens/02-public.html#f-register-captcha-invalid) · [`#f-register-captcha-unavailable`](../../../../design/ui/screens/02-public.html#f-register-captcha-unavailable) · [`#f-register-invite`](../../../../design/ui/screens/02-public.html#f-register-invite) · [`#f-register-closed`](../../../../design/ui/screens/02-public.html#f-register-closed) · [`#f-register-rate-limited`](../../../../design/ui/screens/02-public.html#f-register-rate-limited) · [`#f-register-submitting`](../../../../design/ui/screens/02-public.html#f-register-submitting) · [`#f-register-success`](../../../../design/ui/screens/02-public.html#f-register-success); `CaptchaWidget` [`#f-captcha-widget`](../../../../design/ui/screens/02-public.html#f-captcha-widget); `/verify-email` [`#f-verify-verifying`](../../../../design/ui/screens/02-public.html#f-verify-verifying) · [`#f-verify-verified`](../../../../design/ui/screens/02-public.html#f-verify-verified) · [`#f-verify-expired`](../../../../design/ui/screens/02-public.html#f-verify-expired) · [`#f-verify-invalid`](../../../../design/ui/screens/02-public.html#f-verify-invalid) · [`#f-verify-resent`](../../../../design/ui/screens/02-public.html#f-verify-resent). `/` has no frame by design (prerendered redirect, no visible screen). Full state → frame matrix: `10.3.1-T0-ui-design.md` §Outputs.

**Owner approval record (2026-10-02).** Approved by the owner 2026-10-02 — the full review deck v2 "Phòng thu", commit `192cc73` (see `10.3.1-T0-ui-design.md` §Outputs). The design gate is satisfied for this folder; the only carried items are content dependencies (13's legal/consent copy, owner-supplied pricing figures).

Shared components used: `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState` (10.1.3-T1); `RetryCountdown` (10.2.2-T1); `CaptchaWidget` (this folder, `@shared/ui`); theme tokens from `shared/ui/theme/`.

## 7. Jobs and integrations

| Integration | Detail |
|---|---|
| Prerender | `app.routes.server.ts` prerenders the six paths per language (F3); `/` prerendered redirect (F26) |
| Build-time content | `tools/web/legal-to-json.mjs` (10.3.1-T1) for legal versions (read-only here); pricing JSON validated at build |
| CSP generation | `tools/web/gen-csp.mjs` gains `CAPTCHA_ORIGINS` → `script-src`/`frame-src`/`style-src` += `https://challenges.cloudflare.com`; pre-clearance off (F9) |
| Turnstile script | `turnstile-loader.ts` on-demand injection (idempotent, Trusted-Types-scoped), invoked from `CaptchaWidget`'s first mount only — never a static tag in `index.html` (F8, 09 W3 CR-34) |
| Runtime config | container entrypoint writes `assets/runtime-config.json` from `NTS_CAPTCHA_SITE_KEY` (F10) |
| Sitemap / robots | static `web/public/sitemap.xml` (six per language) + `robots.txt` disallow list (F4) |
| CI | `web.yml`: lint, i18n key equality (10.2.5-T1), api:gen drift, unit, build, gzip budget, CSP generation; e2e in 11.5.1-T1's `e2e.yml` |

## 8. Security and privacy

- **CSP**: `default-src 'none'`; the only third-party origin is `https://challenges.cloudflare.com` in `script-src`/`frame-src`/`style-src` (F9, CR-28). The Turnstile script is injected **on demand** by `turnstile-loader.ts` through a Trusted-Types policy scoped to the fixed URL — never a static tag, never `bypassSecurityTrust*` (ST-2, F8, 09 W3 CR-34). Any `securitypolicyviolation` with the widget mounted fails the CSP fixture spec (F25).
- **Trusted Types**: `require-trusted-types-for 'script'` and the `trusted-types` allow-list stay unchanged; if Turnstile cannot run under them, ST-2 re-opens the vendor — the header is never relaxed (F9).
- **Token handling**: the verify-e-mail token comes from the URL **fragment** and is removed with `history.replaceState` before the POST (D6, F13); never logged, echoed, or placed in a query string.
- **No account enumeration**: register always ends on the same neutral page (F17); `registration_closed` and invite errors use neutral form-level copy (F13).
- **Data / audit**: no tenant filter or `Authorization` header here (no data access); no client telemetry; the server audits registration/verification; `traceId` surfaced on `unknown_error` only (FC15).
- **Secrets**: only public values reach the browser (`captchaSiteKey`); `NTS_CAPTCHA_SITE_KEY` is public by design (F10, ST-7).
- **Third-party licence/privacy**: Turnstile free tier, documented licence + privacy terms + data leaving the browser, owner-approved before 10.3.3-T2 starts (F22, S-O3); no analytics/ads/fonts/CDN on any public page.
- **i18n**: vi default; every new key in both languages (F20).

## 9. Test plan

- **Unit** (Vitest, 10.1.5-T1): `password-rules` (single implementation), register body builder (consents/`uiLocale`/`inviteCode?`/`captchaToken?`), outcome mapper for every code in F16, pricing-content loader, meta builder, `lang-redirect`, `runtime-config`, `turnstile.provider` (fake `window.turnstile`).
- **Component** (Vitest + hand-written fakes from `NightTales.TestKit`, no mocking library): `RegisterPage`, `VerifyEmailPage`, `CaptchaWidget` (fake `CaptchaProvider`), `landing.page`, `pricing.page`.
- **Axe** (Vitest specs): `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa` on every screen + widget state, vi + en (F19).
- **CSP fixture** (10.1.6-T1 harness): register + login specs assert zero `securitypolicyviolation` with the widget mounted (F25).
- **e2e** (Playwright in `e2e.yml`, 11.5.1-T1): register → verify → login against the real API; any published price exists in `GET /billing/packages` (F5); language redirect; both languages.
- **Budget** (`tools/web/check-gzip-budget.mjs`): public/auth chunks within budget; no admin/billing chunk in the initial bundle (10.10.1-T2).

Given/When/Then acceptance checks live in each task file.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 10.3.1-T0 | M (1.0) | UX | S · M2 | 10.1.3-T1, 13.1.1-T1, 13.1.2-T1 | 10.3.1-T2, 10.3.2-T1, 10.3.3-T2, 13.1.4-T1, 13.2.3-T1, 13.3.4-T1 |
| 10.3.1-T2 | S (0.5) | FE + SEO | S · M2 | 10.3.1-T0, 10.3.1-T1, 10.1.6-T1, 13.1.3-T1 (13 CR-27) | 10.3.2-T1 (soft), 10.10.1-T1, 08.3.1-T2 |
| 10.3.2-T1 | S (0.5) | FE | S · M2 | 10.3.1-T0, 10.3.1-T2 (soft), 10.2.1-T1, 09.3.1-T2, 13.2.3-T1, 13.2.1-T2 | 10.3.3-T2, 10.10.1-T1, 10.10.2-T2 |
| 10.3.3-T2 | XS (0.25) | FE + Security | S · M2 | 10.3.1-T0, 10.3.2-T1, 10.3.3-T1, 09.3.3-T2, 10.1.6-T1 | 10.10.1-T1, 13.3.4-T1 (soft) |

Re-estimate (area row `../README.md:775`): 10.3 Public + auth (S part) §6 S rows 1.25 → detailed **2.25** (+1.0 `10.3.1-T0` design). Slice total per §C9: 18.75 raw / 22.5 buffered (CR-22). Largest task here is M (1.0).

## 11. Rollout

1. **Design gate** — 10.3.1-T0 completes, owner approves, link + record land in §6. No implementation task starts before this.
2. **M2 entry** — Gate 2 passed; 09's register/verify/CAPTCHA endpoints deployed; 13's legal texts + 18+ attestation (13.2.3-T1) available; staging serves the SPA from the `web` image.
3. **10.3.1-T2 → 10.3.2-T1 → 10.3.3-T2** — prerender/SEO, then register/verify, then widget + CSP delta + runtime config; each verified by its e2e/SEO/CSP checks.
4. **M2 exit** — registration half green: zero CSP violations with Turnstile mounted, i18n key sets equal.
5. **Follow-ups** — 10.10.1-T1 audits these screens (axe `wcag22aa` + manual matrix); any design/contract gap is reported to the TL, not patched outside this folder.
6. **[OPEN] F29 — `registration.mode` source.** Before 10.3.2-T1 ships the mode-conditional widget/invite field, the wave-3 TL must close F29 (proposed CR: extend ST-7's public `assets/runtime-config.json` with `registrationMode`, or add a public policy route). Until then the task implements the documented fallback (reveal on 400 `captcha_required`; closed/invite copy keyed off `registration_closed`).
