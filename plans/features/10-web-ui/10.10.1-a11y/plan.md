# 10.10.1 — Accessibility pass (WCAG 2.2 AA audit + per-lazy-route budgets)

> Refines: `../../../10-web-ui.md` §4/§5 story **10.10.1** ("Accessibility pass", Must · M, `../../../10-web-ui.md:253`, split row `:396`) and `../README.md` **Part C** — §C0 (`:574`), facts SC1–SC31 (`:576-611`), findings **FC8/FC9/FC16** (`:624,625,632`), decisions **SD11** (`:675`), **SD12** (`:677`), **SD13** (`:679`) and **ST-3** (`:802`).
> Wave-1/2 contract still binds: `../README.md` §3 **D1–D18** — hardest here **D15** (`:182`, Vitest/Playwright + `expectA11yClean`), **D16** (`:188`, a11y baseline), **D13** (`:172`, `tools/web/check-gzip-budget.mjs`, initial ≤ 250 kB gzip), **D4** (`:117-132` CSP + Trusted Types), **D11** (`:168`, the component inventory), **D10** (`:166`, Transloco vi/en), **D17** (`:190`, polling).
> Consumes, never forks: `../10.1.1-workspace-ci/10.1.5-T1-vitest-playwright-web-yml.md` (harness: `web/playwright.config.ts`, `web/e2e/fixtures/{axe,auth}.ts`, `web/tools/web/check-gzip-budget.mjs`), `../10.1.3-design-api-csp/10.1.6-T1-csp-trusted-types.md` (`web/e2e/fixtures/csp.fixture.ts`, `web/e2e/security/routes.ts` → `CSP_ROUTES`), `../10.1.1-workspace-ci/10.1.2-T1-lint-boundaries-ci.md` (template a11y rule set), `../10.1.3-design-api-csp/10.1.3-T1-material-theme-ui-kit.md` (M3 tokens, dark default + light).

**In scope (2 tasks, 1.0 d raw — unchanged by CR-24, §C9:779).** `10.10.1-T1` (S) — the audit harness (axe tag upgrade + route matrix + keyboard suite + manual script) and the audit of **public/auth/billing/account** screens. `10.10.1-T2` (S) — the audit of **admin/publishing** screens and the **per-lazy-route budget table** in `web/tools/web/check-gzip-budget.mjs`.

**No new UI.** This folder writes no page, component, style or copy key; it verifies the screens other folders wrote and changes only test/CI tooling plus the two files it must extend (`web/e2e/fixtures/axe.ts`, `web/tools/web/check-gzip-budget.mjs`). It is therefore **exempt from the `-T0` UI-design rule** by TL ruling **ST-3** (`../README.md:802`) — the owner rule (2026-09-25) still governs every folder that produces UI, and those designs are an **input** here, not an output (§6).

**Out.** New features, new components, visual redesign, the CAPTCHA vendor decision (ST-2/SD15, `:797`), the wave-3 folders' own screens, the `-T0` design tasks, and any edit to a sibling folder. Findings are reported back to the owning task; a fix that changes a screen is that screen's owner's change (T1/T2 open a `[OPEN]` row instead of editing).

---

## 1. Scope

Two stories, one release gate.

| Story | Size | Deliverable |
|---|---|---|
| `10.10.1-T1` | S (0.5) | Audit harness + WCAG 2.2 AA audit of the public, auth, billing and account surfaces |
| `10.10.1-T2` | S (0.5) | WCAG 2.2 AA audit of the admin and publishing surfaces + per-lazy-route gzip budgets and the "no admin/billing chunk in the initial bundle" assertion |

**Goal.** Every route listed in §6 of this plan passes, in CI and in a signed manual pass: axe with the five WCAG 2.2 tag sets, zero CSP/Trusted-Types violations (D4), a keyboard-only path, a correct focus order, both themes and both languages, 200 % zoom and 360 px without horizontal scrolling, ≥ 24 px targets, and no information carried by colour alone. Additionally the initial bundle stays ≤ 250 kB gzip and the wave-3 lazy chunks are budgeted per route and provably absent from the initial bundle (FC8).

**Non-goals.** Conformance claims for the API docs page (09.9.4-T1 owns its CSP and its own axe run, `09.9.4-T1:96,147`), for the design tool, for the receipt HTML that the API serves as `text/html` (it renders in a new tab; its own audit is 12's), or for stock TikTok/YouTube surfaces (third-party, unreachable from the client).

**Why now / last.** §C5:583 puts both tasks after every wave-3 screen: T1 after public/auth/billing/account, T2 after the admin screens; both are **M2 exit gates** together with the per-route budget table (§C7:722 "10.10.1-T1/T2 complete (axe `wcag22aa` on every route, manual matrix signed, per-route budgets enforced)"). TikTok (R2, 10.7.3) is audited by T2 immediately after 10.7.3-T2 lands; if `tiktok_publish` is off in M2, the TikTok rows are audited with the flag on in the R2 window and marked as such in the matrix (§11).

---

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| A1 | Wave-3 screens target **WCAG 2.2 AA**; axe runs with `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`; T1 owns **harness + manual script** (keyboard-only, focus visible/trapped/restored, focus not obscured, `aria-live`, contrast in both themes, 200 % zoom, 360 px, `prefers-reduced-motion`, dragging alternatives, redundant entry) and audits public/auth/billing/account; T2 audits admin/publishing and adds the **per-lazy-route budget table** to `tools/web/check-gzip-budget.mjs` (initial ≤ 250 kB gzip unchanged; admin/billing/TikTok chunks budgeted separately and asserted absent from the initial bundle). | SD12 `../README.md:677` ✅ |
| A2 | D13 budgets today: `angular.json` initial raw warning 700 kB / error 900 kB, component style ≤ 8 kB, and `web/tools/web/check-gzip-budget.mjs` fails `web.yml` when the gzip sum of **initial** chunks > 250 kB (NFR-10-2). | Part A D13 `../README.md:172-177` ✅ |
| A3 | D15 harness that exists and must be reused: `web/e2e/fixtures/axe.ts` → `expectA11yClean(page, { include?, exclude? })` using `withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa'])`; `web/e2e/fixtures/auth.ts` → `totp()` + `loginAsOwner(page)`; `web/playwright.config.ts` (`testDir: './e2e'`, Chromium only, `trace/video = retain-on-failure`); `web.yml` has **no** compose e2e — the suite runs in `11.5.1-T1`'s `e2e.yml` job via `npm run e2e`. | `10.1.5-T1-vitest-playwright-web-yml.md:30-37,87` ✅ |
| A4 | `web/tools/web/check-gzip-budget.mjs` is invoked as `--dir dist/web/browser --budget 256000`, parses the initial HTML for `<script src>`, `<link rel="modulepreload">` and `<link rel="stylesheet">`, gzips each (`zlib.gzipSync(buf, { level: 9 })`), prints `file raw gzip` rows and exits non-zero over budget; `web/tools/web/check-gzip-budget.test.mjs` (Node `--test`) asserts exit codes with temp fixtures and that a missing directory **fails loudly**. | `10.1.5-T1-vitest-playwright-web-yml.md:37-38` ✅ |
| A5 | CSP is emitted at build time by `tools/web/gen-csp.mjs`; the e2e auto-fixture `web/e2e/fixtures/csp.fixture.ts` **fails any spec that installs it** on a `securitypolicyviolation` or a Trusted-Types console error, and `web/e2e/security/routes.ts` exports `CSP_ROUTES` — "the shared route list each later feature appends to". | `10.1.6-T1-csp-trusted-types.md:70-80` ✅ |
| A6 | D16 baseline already binds every task: labels on every control, visible focus ≥ 3:1, keyboard reachable, no trap except modal dialogs (trapped and restored), `aria-live="polite"` for progress/status/"saved", errors linked with `aria-describedby`, contrast ≥ 4.5:1 in both themes, no colour-only information, target ≥ 24 px, 360 px layout without horizontal scroll. | Part A D16 `../README.md:188` ✅ |
| A7 | FC9: axe **cannot** cover the two 2.2 criteria that bite (dragging alternatives, focus not obscured) or 24 px target size under the old tag set, and it cannot inspect inside a cross-origin iframe — hence a manual script, and the CAPTCHA widget's iframe a11y is manual-only (FC16). | `../README.md:625,632` ✅ |
| A8 | FC8: wave 3 adds the biggest chunks (`/admin/**` incl. the owner-only settings subtree, `/billing`, the QR library, the Turnstile script on every public page); D13's script guards only the initial chunk today. | `../README.md:624` ✅ |
| A9 | Sibling wave-3 folders already state their side of this contract: `10.8-billing-account/plan.md:148,179` — "`tools/web/check-gzip-budget.mjs` gains this folder's lazy chunks in the per-route budget table (**10.10.1-T2 owns the script change**)" and their axe run is "10.10.1-T1 axe run"; `10.9-admin-core/plan.md:30,138,144` — the per-lazy-route table plus "no admin/billing chunk referenced by the initial bundle", admin e2e under `web/e2e/admin/**`; `10.3-public-signup/10.3.1-T2-landing-pricing.md:60` and `10.3.3-T2-captcha-widget.md:62` run axe with the five tags. | those files ✅ |
| A10 | The audited route inventory is fixed by SD1's table, SD2's prerender list and SD10 (TikTok), with the shell (nav, `header-end`, cookie notice 13.1.4-T1, `LegalAcceptanceDialog` 13.2.1-T2) audited once for every authenticated page. | SD1/SD2/SD10 `../README.md:645-673` ✅ |
| A11 | The component inventory the audit may demand (and must name, never reinvent): `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState` (skeleton), `StatusChip`, `ConfirmDialog` (+ `cost-line` slot), `RetryCountdown`, `CharCounter`, `ReauthDialog`, `LegalAcceptanceDialog`, `captcha-widget`, `cursor-page.ts`/`cursor-list.ts`/`visible-poll.ts`. | Part A D11 `../README.md:168`; SD14 `:681` ✅ |
| A12 | Copy: keys are `scope.area.item`, vi default, every key in both languages, `errors.*` carries a key for every server code; **dynamic server text (consent texts, receipts, `detail`) is never a key**; CI key equality from 10.2.5-T1 covers the new scopes. | SD13 `../README.md:679` ✅ |
| A13 | New locales/`lang` attribute: `<html lang>` follows the locale (10.2.6-T1) and the wave-2 journey suite asserts it in `en`; the audit asserts it per route instead of a journey. | `10.10.2-journeys/plan.md:124` ✅ |
| A14 | The four approved designs are inputs: `10.3.1-T0`, `10.7.3-T0`, `10.8.1-T0`, `10.9.1-T0` each carry focus order + a11y annotations, three breakpoints, vi+en, dark+light, and every state list — the manual matrix is checked **against those annotations**, not against taste. | SD11 `../README.md:675`; §C7 design gate `:719` ✅ |
| A15 | Estimate: both tasks stay `S (0.5)`; the accessibility area stays **1.0** d (M split into two S, no design, §C9:779); the folder is `none (ST-3)` in the design-owner column (§C8:737). | §C8.1 `:757-758`; §C9 `:779` ✅ |
| A16 | `@axe-core/playwright` 4.13.0 (MPL-2.0, accepted) supports `withTags`/`exclude`/`disableRules`; `page.clock` and `page.route` are stable in `@playwright/test` 1.63.0. Vendor docs are not re-read here; the versions are the pinned ones. | `10.10.2-journeys/plan.md:48` ✅ ⚠️ version pin is a sibling's fact, not re-verified here |

**Open / unverified.** `[OPEN]` **A10-1**: the exact chunk→route attribution mechanism must be validated against the installed `@angular/build` esbuild output at implementation (§7, T2 step 4) — if sourcemap-based attribution is unavailable in the pinned builder, T2 falls back to the committed `web/tools/web/route-chunk-prefixes.json` and records the deviation. `[OPEN]` **A10-2**: whether Turnstile's widget is keyboard-usable/MIT-licence-compatible is owned by ST-2/SD15 before 10.3.3-T2; if it is not, T1's matrix records the documented fallback path instead of a violation (FC16).

---

## 3. Domain and data

No production data is created. The folder owns **three artifacts** and one CI gate.

| Artifact | Path | Owner task | Contents |
|---|---|---|---|
| Route matrix | `web/e2e/a11y/route-matrix.ts` | T1 | `export const A11Y_ROUTES: readonly A11yRoute[]` — `interface A11yRoute { path: string; name: string; scope: 'public'\|'auth'\|'billing'\|'account'\|'admin'\|'publishing'; lang: 'vi'\|'en'; auth: 'none'\|'owner'\|'admin'\|'owner+reauth'; states: readonly string[]; manualOnly?: readonly string[] }`; T2 **appends** the admin/publishing rows (same file, one writer at a time — T2 depends on T1); `CSP_ROUTES_EXTRA` holds the routes appended to 10.1.6-T1's `CSP_ROUTES`. |
| Axe tag sets | `web/e2e/fixtures/axe.ts` (extended) | T1 | `export const WCAG22_TAGS = ['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa'] as const` and `expectA11yClean(page, { include?, exclude?, tags = WCAG21_TAGS, disableRules? } = {})`; the wave-1 default (`WCAG21_TAGS`) is unchanged so no P spec changes behaviour, and every wave-2/3 spec passes `tags: WCAG22_TAGS`. |
| Manual matrix | `plans/features/10-web-ui/10.10.1-a11y/a11y-manual-matrix.md` | T1 (public/auth/billing/account rows), T2 (admin/publishing rows) | One row per route × criterion, columns: `Route · Screen/state · Keyboard-only · Focus order · Focus not obscured · Zoom 200 % · 360 px · prefers-reduced-motion · Contrast (dark/light) · SR name/role/value · Dragging alternative · Redundant entry · 24 px · SR tool + version · Verified by/date`. Blank cells are missing evidence, and the file is the signed M2-exit artifact (§C7:722). |
| Budget table | `web/tools/web/check-gzip-budget.mjs` + `web/tools/web/check-gzip-budget.test.mjs` | T2 | A checked-in table `ROUTE_BUDGETS: readonly { route: string; match: readonly string[]; budget: number }[]` plus the existing `--budget 256000` initial gate, a `--routes` mode that walks the emitted chunk graph, and the `INITIAL_MUST_NOT_REFERENCE` list. |

**Budget numbers** `[DECISION]`: `initial 256000` (250 kB, unchanged), `admin /admin/** ≤ 300000`, `admin-settings (owner-only subtree) ≤ 120000`, `billing /billing/** ≤ 260000`, `account /account/** ≤ 180000`, `publishing /channels + /episodes/:id/publish/** ≤ 240000`, `tiktok (flag `tiktok_publish`) ≤ 140000`. These are **ceilings, not targets**: T2 measures the real chunks at implementation, records the measured value in the table, and sets each ceiling at measured + 15 % so the gate catches regressions without blocking unrelated features; every number is listed for owner approval in §10 of this plan (§11 step 1).

**Relationships/derivations.** Route → feature scope → audited states come from SD1's "States to design/implement" column (`../README.md:645-666`); a route with states the design listed but the screen does not implement is a finding **against that screen's owner**, recorded as `[OPEN]` in the matrix, never fixed here.

---

## 4. Use cases

**UC-1 — CI axe gate (T1).** *Given* the compose stack from `e2e.yml`, *when* `npm run e2e -- a11y`, *then* for every `A11Y_ROUTES` row whose `auth` the fixture can satisfy, the page is opened in vi and en, axe runs with `WCAG22_TAGS`, `expectA11yClean` reports zero violations, and the CSP fixture reports no violation. *Failure* prints `id/impact/help/nodes` (the fixture's existing message) and the route row name, so the failing route is identifiable without reading the trace.

**UC-2 — keyboard-only pass (T1).** *Given* a route, *when* a Playwright spec presses only `Tab`/`Shift+Tab`/`Enter`/`Space`/`Escape`/arrow keys, *then* every interactive control in SD1's state list is reachable and operable; the first `Tab` on an authenticated page focuses the skip link; inside a dialog focus is trapped and `Escape` closes it and returns focus to the invoking control; removing the last focusable element in a modal never moves focus to the document body. *Failure* names the element (accessible name + role) that could not be reached.

**UC-3 — live-region announcement audit (T1/T2).** *Given* a state transition (loading → data, save → "saved", validation error, poll progress, pending-approval banner), *when* it happens, *then* the changed text sits in an element with `aria-live="polite"` (or `role="status"`) and **focus is not moved**; toasts never steal focus. The spec asserts the live region exists and holds the expected `errors.<code>`/`billing.*`/`admin.*` key text.

**UC-4 — theme and language matrix (T1/T2).** *Given* each route, *when* run with `dark` and `light` and with `vi` and `en`, *then* axe stays clean, contrast stays ≥ 4.5:1 (text) / 3:1 (UI + focus ring), `<html lang>` matches the locale, no string overflows its control at 1280 px in either language (longest vi/en pair), and no layout scrolls horizontally at 360 px.

**UC-5 — reduced motion and zoom (T1/T2).** *Given* `page.emulateMedia({ reducedMotion: 'reduce' })`, *when* the route renders, *then* no non-essential animation runs (skeletons may still fade but must not translate/scale, no auto-rotating carousel, the CAPTCHA countdown still announces via `aria-live`); *given* 200 % zoom (viewport 640×512 CSS px with `deviceScaleFactor: 2`), *then* no content is clipped and no control is obscured by a sticky header/footer (the "focus not obscured" 2.2 criterion).

**UC-6 — per-route budget gate (T2).** *Given* a production build, *when* `node web/tools/web/check-gzip-budget.mjs --dir dist/web/browser --budget 256000 --routes`, *then* it prints `route raw gzip budget` rows for every entry, fails on any exceeded ceiling, and fails if any chunk matched by the `admin`/`admin-settings`/`billing`/`tiktok` matchers is reachable from the initial HTML or from the initial bundle's static import graph (FC8's assertion).

**UC-7 — evidence and re-run (both).** *Given* a fixed screen, *when* the audit is re-run after the fix, *then* the previously failing row is green, the matrix row records `Verified by/date`, and the fix itself lives in the owning folder's task — this folder only re-runs the audit.

**UC-8 — escalation, not scope creep (both).** *Given* a violation caused by a product screen, *when* it cannot be fixed by test tooling, *then* T1/T2 add an `[OPEN]` row to the matrix with the axe rule id, the route, the node and the owning task id, report it in the final hand-off, and leave the screen untouched.

---

## 5. API and events

No API call is added, changed or removed by this folder. The audits read whatever the screens read, through the screens.

- **Backend routes exercised by the matrix stubs:** failure copies come from the server codes the screens already render, quoted from §C1/§C3 — `captcha_required` 400, `breached_password` 400, `forbidden` 403, `reauth_required` 401 (step-up), `tiktok_reauth_required` as a publication `error_code` (ST-6 `:805`), `legal_acceptance_required` 409, `setting_change_not_pending`/`owner_action_not_pending` 409, 202 bodies `setting_change_pending`/`owner_action_pending`/`owner_action_disputed`/`grant_requires_approval`, `rate_limited` 429, `feature_disabled` 404 on `/admin/quota` (ST-9 `:808`). Each of those states is an axe row in the matrix, with the copy read from the `errors.*` key named by SD13 (`:679`).
- **Stubbing.** States CI cannot produce are produced with `page.route` (the wave-2 pattern, `10.10.2-journeys/plan.md:65`) — never by loosening a fixture and never by mocking the API client.
- **Events.** The hub is unchanged (SD16 `:684`): `CreditBalanceChanged { userId }` and `PublicationUpdated` are only observed — the audit asserts `aria-live` on the region the refetch updates and that focus is not moved.
- **ProblemDetails.** The `traceId` on `unknown_error` is rendered by the screens (FC15 `:631`); the audit asserts the error region contains it and that no token/QR payload/receipt body appears in the DOM. No new ProblemDetails code is introduced here.

**Error-code → i18n key check (T1).** A Vitest spec (`web/src/app/core/errors/a11y-error-copy.spec.ts`) asserts that every code listed above resolves to a key present in **both** `web/src/assets/i18n/errors/vi.json` and `en.json`, so an axe row for a failure state can never fail for a missing translation; a missing key is a finding against SD13's owner.

---

## 6. UI

**No UI is produced** (ST-3, `../README.md:802`). This section is the **audited screen inventory**, built from SD1/SD2/SD10 (`../README.md:645-673`) and split by task; the manual matrix (T1/T2) carries one row block per line below.

**Design inputs (not outputs).** The audit checks implementations against the approved designs of the folders that own them — `10.3.1-T0` (public marketing + auth), `10.7.3-T0` (TikTok), `10.8.1-T0` (billing + account), `10.9.1-T0` (admin) — each of which must carry focus order, a11y annotations, the state list, 360/768/1280, vi+en and dark+light (SD11 `:675`, §C7 design gate `:719`). Those design links live in the owning `plan.md` §6; **this folder links them as inputs and holds no design approval record of its own** (the `-T0` exemption IS the ruling). If a folder ships without an approved design shown in its §6, the audit stops for that folder and reports it — the gate is theirs.

**T1 — public / auth / billing / account rows (SD1/SD2/SD10).**

| Group | Routes | States audited |
|---|---|---|
| Public (prerendered) | `/` (redirect), `/vi/`, `/en/`, `/<lang>/pricing`, `/<lang>/privacy`, `/<lang>/terms`, `/<lang>/report`, `/<lang>/help` | static, hydrated CTAs, form, submitting, submitted, rate-limited (`rate_limited` 429), captcha states |
| Public auth | `/register`, `/verify-email`, `/login`, `/login/mfa`, `/forgot-password`, `/reset-password`, `/cancel-request` | form, captcha revealed (`captcha_required`), submitting, neutral "check your e-mail", verified/expired/resent, confirm, done, unknown/expired/used, step-up dialog, 401 `reauth_required` |
| Billing | `/billing` → `/billing/wallet`, `/billing/payments`, `/billing/payments/:id`; `/billing/return` | packages, promo, redirecting, confirming (poll), paid, `pending_approval`, `needs_review`, failed/expired, ledger grouped by `groupId`, wallet, history, receipt, refund, low balance, paused (QR) |
| Account | `/account`, `/account/activity`, `/account/security` | form, export pending/done, deletion pending, timeline empty/filtered, sessions (trusted-device badge), `ReauthDialog`, MFA enrol/verify, recovery codes shown once |
| Shell (once) | nav + `header-end` + cookie notice (13.1.4-T1) + `LegalAcceptanceDialog` (13.2.1-T2) | skip link, focus restoration, `aria-live`, both themes |

**T2 — admin / publishing rows.**

| Group | Routes | States audited |
|---|---|---|
| Admin core | `/admin` → `/admin/users`, `/admin/users/:id`, `/admin/render-jobs`, `/admin/usage`, `/admin/moderation`, `/admin/quota`, `/admin/reports`, `/admin/deletion-requests`, `/admin/flags`, `/admin/kpis` (08.1.1-T2) | tables (filter/paging/empty/error/**`aria-sort`**), detail tabs, dialogs, `feature_disabled` (flag off), 403/202 banners |
| Admin ops | `/admin/billing/{packages,promos,payments,refunds,funding}`, `/admin/settings`, `/admin/settings/nodes`, `/admin/approvals` | tables, resolve dialogs, funding snapshots, cards, test/versions/rollback, pending-change banner, disputed, approve/cancel |
| Publishing | `/channels`, `/channels/:platform/callback`, `/episodes/:id/publish/youtube`, `/episodes/:id/publish/tiktok` (R2), `/publications/cancel`, `/publications/approve` | list, connecting, callback result, form, consent, submitted, quota wait, creator blocked, status; public cancel/approve pages fully keyboard-operable sessionless |

`768 px` is asserted for the shell + the two table-heavy groups (admin tables, ledger) as the tablet breakpoint; the other routes are asserted at 360 and 1280 only, and no route is allowed a horizontal scrollbar at either (D16 `:188`).

---

## 7. Jobs and integrations

No job, queue message or scheduled task is added. This folder adds **one CI gate** and extends two existing ones.

| Integration | Contract |
|---|---|
| `e2e.yml` job `e2e` (11.5.1-T1) | The new specs live in `web/e2e/a11y/**` and are picked up by the existing `npm run e2e` (`testDir: './e2e'`, A3). No new workflow, **no `playwright.config.ts` change**; each spec sets `test.setTimeout` where it walks long flows, and authenticated specs set `test.use({ trace: 'off', video: 'off' })` (WR14). |
| `web.yml` (10.1.5-T1) | T2's budget gate runs in the existing step order (`build` → `gzip budget`); **no new step name** and no `paths:` change. |
| `web/e2e/security/routes.ts` (`CSP_ROUTES`) | T1 appends the new wave-3 routes it navigates, so the CSP spec covers them; the file is *appended to*, never rewritten (A5). |
| Playwright fixtures | Reused verbatim: `loginAsOwner`/`totp()` (A3), the CSP auto-fixture (A5). `expectA11yClean` is **extended with an optional `tags`** (T1) — the only edit to a wave-1 file; the default keeps wave-1 behaviour. |
| `page.clock` | Used only to fast-forward app-side timers (poll countdowns, OTP windows); server-side transitions are awaited with `expect.poll` (the wave-2 rule, `10.10.2-journeys/plan.md:132`). |

**Commands (the exact gate runs).**

```bash
# T1 — audits (against the compose stack, same env as e2e.yml)
npm run e2e -- a11y                                        # whole a11y folder
npx playwright test a11y/public-auth-billing-account.spec.ts --project=chromium
# T2 — budgets (no stack needed, pure build artifact)
node web/tools/web/check-gzip-budget.mjs --dir dist/web/browser --budget 256000 --routes
node --test web/tools/web/check-gzip-budget.test.mjs
```

**Jobs / `[JobPolicy]`.** None — no Hangfire job, worker node or queue is touched; the budget gate is a CI step, not a hosted job.

---

## 8. Security and privacy

| Rule | Application here |
|---|---|
| No fixture weakens a header (D4, `:117-132`) | T1/T2 never relax CSP or Trusted Types to make a page scan clean; a violation is a finding against the page's owner. The only CSP-adjacent change is `frame-src https://challenges.cloudflare.com` + the CAPTCHA origins, which is **CR-28's** territory (10.1.6-T1), not this folder's. |
| No `bypassSecurityTrust*`, no `[innerHTML]` (10.1.2-T1) | The audit adds no template and no DOM sink; a spec that needs a canary uses `page.setContent` on the *test* page only (the existing `expectA11yClean` self-proof pattern, A3). |
| Token hygiene in tests | `loginAsOwner` writes `web/e2e/.auth/`, deleted by `global-teardown.ts` (WR14); new specs set `trace/video: 'off'` when authenticated so no token or QR payload lands in a trace; the manual matrix stores no token, no QR payload and no receipt body — screenshots are redacted or omitted. |
| Secrets | None introduced. The audit needs only `NTS_E2E_OWNER_EMAIL`/`NTS_E2E_OWNER_PASSWORD` and, for admin rows, an admin account created by the existing seed path (10.9.1-T1's fixtures) — read from env, never committed; `NTS_CAPTCHA_SITE_KEY` is public by design (ST-7 `:807`). |
| Tenant / authorization | The audit deliberately re-runs the negative cases server-side-verified by the owning tasks: a non-owner token must not render the owner-only subtree, and 403/404 must be the screen's copy — the audit asserts the rendered state, and never weakens a guard to reach a screen. Any admin row that cannot be reached with the correct role is a finding against 10.9.1-T1. |
| Audit trail | Admin mutations exercised while auditing go through the real endpoints and are therefore audited server-side (`[Audited]`, 09.3.6-T1); the audit adds no client telemetry and logs nothing but axe output (no PII). |
| Data Protection / secrets | Not touched: no setting, no secret re-entry, no worker-node key is rendered or read; the matrix asserts write-only secrets stay masked on `/admin/settings`. |

---

## 9. Test plan

**Projects.** `web/e2e/**` (Playwright, `@playwright/test` 1.63.0, `@axe-core/playwright` 4.13.0) for the audits; `web/src/**/*.spec.ts` (Vitest via `@angular/build:unit-test` + jsdom, `ZONELESS_TEST_PROVIDERS`) for the two small unit checks; Node `--test` for the budget script. **Hand-written fakes only** from the existing `features/*/testing/fake-*.repository.ts` (D15 `:182-187`); no mocking library, no `NightTales.TestKit` (that is the backend's). No backend test project is touched.

| # | Test | Task | Asserts |
|---|---|---|---|
| AE-1 | `web/e2e/a11y/axe-matrix.spec.ts` | T1 | For every `A11Y_ROUTES` row with `auth: 'none'\|'owner'`: `expectA11yClean(page, { tags: WCAG22_TAGS })` zero violations in vi+en, dark+light; the CSP fixture reports nothing; `<html lang>` matches. Prints `name` on failure. |
| AE-2 | `web/e2e/a11y/keyboard.spec.ts` | T1 | UC-2: skip link first, full tab path per route reaches every control in SD1's state list, dialog trap + `Escape` restore, `aria-sort` toggling on any table, `aria-live` on loading→data and save feedback, focus never moves to `body`. |
| AE-3 | `web/e2e/a11y/states.spec.ts` | T1 | The failure/empty/loading states via `page.route`: `captcha_required` reveals the widget, `breached_password` binds to the password control, `rate_limited`, `legal_acceptance_required`, `unknown_error` shows `traceId`; each renders its `errors.<code>` copy. |
| AE-4 | `web/e2e/a11y/zoom-motion.spec.ts` | T1 | UC-5: 200 % zoom and `prefers-reduced-motion: reduce`; no clipping, no obscured focused control, no non-essential animation; contrast tokens unchanged. |
| AE-5 | `web/e2e/a11y/admin-publishing.spec.ts` | T2 | Same as AE-1/AE-2 for the admin/publishing rows with `loginAsOwner`/admin fixture, incl. `feature_disabled` on `/admin/quota`, the 202 pending banners, and the public `/publications/cancel` + `/cancel-request` pages logged out. |
| AE-6 | `web/e2e/a11y/iframe-manual.spec.ts` | T2 | Partial automation of the CAPTCHA frame: the widget's container has an accessible name, the error/expiry state is `aria-live`, the fallback path (e-mail/OTP) is keyboard reachable; the inside of the third-party frame is **explicitly skipped** and marked manual in the matrix (FC16). |
| AU-1 | `web/src/app/core/errors/a11y-error-copy.spec.ts` | T1 | Every §5 code has key parity vi/en (`errors.<code>`), using the existing i18n JSON files. |
| AU-2 | `web/tools/web/check-gzip-budget.test.mjs` (extended) | T2 | Temp-fixture builds: under/over the **route** ceiling exits 0/1 and names the route; a chunk matching the admin/billing matcher reachable from the initial HTML exits 1 with the "initial bundle references <chunk>" message; a missing `route-budgets` entry for a discovered route exits 1 (never silently passes, mirroring A4). |
| AM-1 | `a11y-manual-matrix.md` | T1+T2 | Signed rows for screen reader (name/role/value), dragging alternatives, redundant entry, 24 px target size, both themes, 360/768/1280, vi+en — the M2-exit evidence (§C7:722). |

**Evidence legend** for the matrix and this plan: ✅ read on disk, ⚠️ unverified/peer-reported, `[DECISION]`, `[OPEN]`, `[INFERENCE]`. No axe rule is disabled anywhere; a page that cannot pass is a finding, not a `disableRules` entry.

---

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 10.10.1-T1 | S (0.5) | FE + QA | S · M2 | 10.3.1-T2, 10.3.2-T1, 10.3.3-T2, 10.8.1-T1, 10.8.2-T1, 10.8.3-T1, 10.8.4-T1, 10.8.5-T1, 10.1.5-T1 | 10.10.1-T2, 08.1.1-T2 (soft), 10.10.2-T2 (10 CR-40) |
| 10.10.1-T2 | S (0.5) | FE + QA | S · M2 | 10.10.1-T1, 10.9.1-T1, 10.9.1-T2, 10.9.2-T1, 10.9.3-T1, 10.9.3-T2, 10.9.4-T1, 10.9.4-T2, 10.9.5-T1 | — |

Rows are verbatim §C8.1 (`../README.md:757-758`).

- **Estimate.** 1.0 d raw / 1.2 buffered (0.5 + 0.5); the accessibility area stays **1.0** in §C9 (`:779`) — the L→two-S split changes nothing, and **no `-T0`** is added (ST-3). The `+3.5 d` design delta in CR-22 does not touch this folder.
- **Task files.** `10.10.1-T1-wcag22-harness-and-public-audit.md`, `10.10.1-T2-admin-publishing-audit-and-route-budgets.md`.
- **Dependency notes.** T1 depends on the wave-3 screens it audits (they must exist in the compose stack) and on the harness (10.1.5-T1); T2 depends on T1 for the extended fixture and the shared route matrix, and on every admin task for the screens. T2's TikTok rows additionally need 10.7.3-T2 (R2) — if R2 slips, T2 runs its M2 rows and the TikTok rows move to the R2 window with the flag on (§11). No task in this folder blocks anything outside it.
- **Owner decisions requested in this plan.** (a) the budget ceilings in §3; (b) the manual-matrix reviewer (one named person who signs, since the audit cannot sign itself — `[OPEN]`, non-blocking for the task files); (c) approval that the CAPTCHA iframe interior stays manual-only (FC16) rather than being treated as a violation.

---

## 11. Rollout

1. **Pre-flight (with the first task).** Confirm the pins (A3: `@playwright/test` 1.63.0, `@axe-core/playwright` 4.13.0, `@angular/build` builder line) and record the measured initial gzip size, so the per-route ceilings in §3 are set against a real baseline (owner approval, §10). If sourcemap-based chunk attribution is unavailable in the pinned builder, T2 switches to the committed `web/tools/web/route-chunk-prefixes.json` and records the deviation in the matrix and in its task file `## Notes` (`[OPEN]` A10-1).
2. **T1 lands first** (extended fixture + route matrix + public/auth/billing/account specs + the error-copy unit spec + the matrix's first rows). Green means: AE-1…AE-4 and AU-1 pass on the compose stack, and every finding is recorded as an `[OPEN]` row with an owning task id.
3. **T2 lands** (admin/publishing specs + budget table + its unit tests + the matrix's remaining rows). Green means: AE-5/AE-6 and the extended AU-2 pass, and `web.yml`'s existing `gzip budget` step fails on a deliberately oversized admin chunk (self-proof, reverted).
4. **M2 exit.** §C7:722 — axe `wcag22aa` clean on every route, the manual matrix signed by the reviewer, per-route budgets enforced, and the "no admin/billing chunk in the initial bundle" assertion active. Both tasks are release gates; a red row blocks the M2 exit, not the phase.
5. **R2 window.** The TikTok rows (`/episodes/:id/publish/tiktok`, the flag-gated nav entry, the private-only notice) are audited with `tiktok_publish` on and marked `R2` in the matrix; ST-2/ST-6's reconnect affordance is asserted for both the 401 `reauth_required` path and the publication `tiktok_reauth_required` copy key.
6. **Rollback.** Nothing to roll back in production — both tasks only add specs, one matrix file and one CI gate. A false-positive rule in a new axe version is handled by pinning (`package-lock.json`), never by `disableRules`; a false `[OPEN]` finding is struck through in the matrix with the date and the reason, keeping the evidence trail.
7. **DoD for the folder.** Both task files' acceptance checkboxes green on the real stack; `npm run e2e -- a11y` green in `e2e.yml`; `node web/tools/web/check-gzip-budget.mjs … --routes` green in `web.yml`; the matrix signed; every `[OPEN]` row either closed by its owner or carried into the M2-exit report with an owner and a date.
