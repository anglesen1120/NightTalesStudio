# 10.8 — Billing and account (S · M2): packages, checkout, wallet, account, activity

Contract: `plans/features/10-web-ui/README.md` **Part C** (SC1–SC31, FC1–FC18, SD1–SD16, CR-22…CR-30, §C8.1, S-O1…S-O5, ST-1…ST-10) plus the signed Parts A/B it extends (D1–D18, WT-W1…WT-W15). Every route, DTO field, status, error code and component name below is copied from that contract or from `plans/features/12-billing-credits/README.md` §3.1; nothing is re-invented. Evidence legend: ✅ read on disk (path + line/scope) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In.** The six tasks of `10.8-billing-account/` (§C8.1): `10.8.1-T0` (design, M), `10.8.1-T1` (packages, checkout, provider return, ledger, M), `10.8.3-T1` (wallet, payment history, receipt, refund request, low-balance banner, M), `10.8.4-T1` (bank-QR checkout + paused-processing states, S), `10.8.2-T1` (account settings, export, deletion, M), `10.8.5-T1` (activity timeline, episode History tab, sessions, M). Total **5.5 d** (4.5 S rows + 1.0 design, §C9).

Money and identity screens only: credit packages → checkout → provider return → ledger; wallet/payments/receipts/refund/low balance; bank QR and the paused states; `/account` profile/locale/export/deletion; `/account/activity`, `/account/security`, the episode History tab. Implements **SD6**, **SD9**, **SD16** and consumes **D14** (`cost-line`/`header-end` slots, `CreditBalanceChanged` is S), **D17** (`visible-poll`), **SD1** routes, **SD13** i18n, **SD14** reuse inventory, **SD15** third-party rules.

**Out.** Admin billing and admin approvals (10.9.2-T1, 10.9.4-T2 — this folder only links a signed-in admin to them); the public pricing page and landing (10.3.1-T2, SD3); landing marketing copy; the TikTok screen (10.7.3-T1/T2); the DSAR/deletion **engine** (13.3.3-T1, 13.3.2-T1 — consumed); legal acceptance and its dialog (13.2.1-T1, consumed); login/MFA/reset (10.3.3-T1/T3); every server route, table, job and provider fact (12/09/13 own them; this folder owns no backend code).

**Non-goals.** No client-side credit arithmetic anywhere (FC7); no grant, no optimistic balance, no status trusted from a URL (FC3); no polling outside `core/state/visible-poll.ts` (SD16); no third-party script, font, analytics or CDN on these routes (D4, SD15); no new hub event (SD16); no new localStorage key outside `core/prefs` (D6); no API call during prerender (D12) — none of these routes is prerendered.

## 2. Confirmed facts

| # | Fact | Evidence |
|---|---|---|
| F1 | Route table for this folder, fixed rows: `/billing` → `/billing/wallet`, `/billing/payments`, `/billing/payments/:id`, `/billing/return`; `/account`, `/account/activity`, `/account/security`; the rest are [DECISION]. Nav: shell `NAV_ITEMS` gains `Billing` (10.8.1-T1) and `Account` (10.8.2-T1). | README §C3 SD1 (`README.md:645-655`) ✅ |
| F2 | Checkout wire is frozen and has **no** `redirectUrl`: `POST /billing/checkout` (+`Idempotency-Key`) body `{ packageId, promoCode?, method?, invoiceInfo?: { companyName, taxCode, address } }` → `CheckoutSessionDto { paymentId, provider, method, amount, currency, checkoutUrl \| null, bankQr: { qrPayload, bankName, accountNumber, accountName, amount, orderCode } \| null, expiresAt }`; 409 `payment_invalid_state` when the key already created a payment; provider return URL is `<PublicOrigin>/billing/return?paymentId={id}&orderCode={orderCode}`. | `12-billing-credits/README.md:229,231-233` ✅ |
| F3 | `GET /credits/ledger` → `{ items: CreditLedgerEntryDto[], nextCursor }`, `CreditLedgerEntryDto { id, createdAt, kind, amount, operation, episodeId, title, groupId, quoted, charged, released, usage: { inputTokens, cachedInputTokens, outputTokens, imageCount, gpuSeconds, estimated } }`, `?cursor=&limit=` (max 100) and **one UI line per `groupId`** (= reservation id). | README SC26 + `12 …:209` ✅ |
| F4 | `GET /credits/balance` → `CreditBalanceDto { total, free, purchased, nextExpiry: { amount, expiresAt } \| null }` (header widget, always on, not behind `billing_enabled`); `GET /billing/quote?operation=story_generate\|fact_sheet\|render&episodeId=` → `QuoteDto { operation, tier, maxCredits, typicalCredits \| null, balanceAfter, priceVersion }`, `story_review`/`story_revision` ⇒ 400; `GET /billing/packages` → `{ items: CreditPackageDto[] }`, `CreditPackageDto { id, code, nameVi, nameEn, credits, bonusCredits, priceVnd, priceUsd \| null, sortOrder }`, authenticated, no feature flag (VND prices are integers). | README SC26 + `12 …:207,208,211` ✅ |
| F5 | `GET /billing/payments` → `{ items: PaymentDto[], nextCursor }`, `PaymentDto { id, createdAt, packageCode, amount, currency, method, status, credits, bonusCredits, receiptAvailable }`; `GET /billing/payments/{id}` → `PaymentDetailDto` = `PaymentDto` + `{ orderCode, providerPaymentId \| null, invoiceInfo \| null, refundRequest: { id, credits, amount, status } \| null, refundEligible, refundableCredits }`; `GET …/receipt` → `text/html; charset=utf-8`, `Content-Disposition: inline`; `POST …/refund-request` body `{ reason }` → `RefundRequestDto { id, credits, amount, status }`, 409 `refund_not_eligible`, 403 `account_under_review`; `POST /billing/promo/redeem` body `{ code }` → `{ creditsGranted, balance }`, `Idempotency-Key` required. | `12 …:212,213,214,215,216,224-228` ✅ |
| F6 | Payment state machine: `pending → needs_review → pending_approval → paid`, else `failed`; `payments.status` gained `pending_approval` + `resolve_requested_by`; the return page stops polling at `expiresAt` (checkout + 30 min) or when the status is `paid`/`failed`/`needs_review`/`pending_approval`. | README SC17, SD6/ST-8 ✅ |
| F7 | `billing_enabled` (default **false**) gates **payment creation only** — `POST /billing/checkout`, `POST /billing/promo/redeem` and the buy UI — and must **never** gate `GET /billing/payments*`, `{id}/receipt`, `POST …/refund-request` or any webhook. | `12 …` 12 CR-3 ✅ |
| F8 | Account wire: `GET/PATCH /me`, `GET /me/legal`, `POST /me/legal/accept`; `GET /me/activity?category=&from=&to=&cursor=&limit=` → `{ items: ActivityItemDto[], nextCursor }`, `ActivityItemDto(occurredAt, category, messageKey, params, targetType, targetId, actorLabel, reason, ip, device)`; `GET /me/sessions` → `{ items: SessionDto[] }`, `SessionDto(id, createdAt, lastUsedAt, expiresAt, ip, userAgent, deviceLabel, isCurrent)` (≤ 20 rows, no cursor); `DELETE /me/sessions/{id}` → 204 (404 for another user's id); `POST /me/sessions/revoke-all` → 204 (keeps the current family); `GET /episodes/{id}/activity`. | README SC5 + `09-backend-api/README.md:1042,1044-1046,1024` ✅ |
| F9 | DSAR and deletion belong to 13 and are consumed: `POST /me/export` 202 + `Idempotency-Key`, max 1/24 h, `GET /me/exports/{id}` with fresh pre-signed links ≤ 15 min (13.3.3-T1); deletion status from `GET /me/deletion-requests` — a **new route**, 13 CR-8 (13.3.2-T1); credit/session routes are 09's and the billing routes 12's. | README SC24, SC27, SC5 ✅ |
| F10 | Errors/copy this folder renders: `insufficient_credits` 402, `spend_cap_reached` 429, `promo_invalid`/`promo_expired` 400, `promo_exhausted`/`promo_already_used` 409, `refund_not_eligible` 409, `payment_invalid_state` 409, `payment_expired` 409, `account_under_review`/`self_grant_forbidden`/`grant_target_forbidden`/`forbidden` 403, `package_not_found` 404, `package_below_cost` 400, `validation_failed` 400, `rate_limited` 429, `reauth_required` 401, plus the generic `unknown_error` (surfaces `traceId`). | README FC10, SC13 + 12 CR-7 ✅ |
| F11 | Realtime is ids-only: `CreditBalanceChanged { userId }` → **refetch balance + ledger only** (no toast, no arithmetic); polling only through `core/state/visible-poll.ts`, and only for `/billing/return` (capped at `expiresAt`) and `/me/exports/{id}` (capped 10 min). | README SD16, D14, D17 ✅ |
| F12 | Reuse, never rebuild (SD14): `IdempotencyKeys`/`IdempotencyIntent`/`withIdempotency` (10.2.3-T1), `cursor-page.ts`/`cursor-list.ts`/`visible-poll.ts` (10.2.6-T1), `withIfMatch`/`readEtag`, `PreferencesStore` (`core/prefs`, the only storage site, 10.1.1-T1), `AppErrorMapper`/`bindFieldErrors` (D9), `LegalAcceptanceDialog` (13.2.1-T1), `ConfirmDialog` (+`cost-line` slot), `ReauthDialog` (10.3.3-T3), `ErrorState`/`EmptyState`/`LoadingState`/`StatusChip`/`PageHeader`/`CharCounter`/`CopyableCode` (10.1.3-T1, D11), `header-end` slot (D14), CSP/Trusted Types fixture (10.1.6-T1), `check-gzip-budget.mjs` + Playwright config (10.1.5-T1). | README SD14, SC29 + `10.2.1-session-errors/plan.md:185`, `10.2.4-realtime-i18n-shell/plan.md:36` ✅ |
| F13 | Accessibility and budgets: WCAG 2.2 AA (CR-23), axe tags `wcag2a`/`wcag2aa`/`wcag21a`/`wcag21aa`/`wcag22aa` in every spec, per-route budget table added by 10.10.1-T2 (initial ≤ 250 kB gzip), heavy widgets behind `@defer`, `qrcode` = self-hosted MIT library, receipt printed from the API's `text/html` in a new tab with `window.print()` (no PDF library, no CDN). | README SD12, SD15, FC8 ✅ |
| F14 | i18n: new scopes `billing` and `account` (plus `errors` keys for every F10 code and `activity.<messageKey>` with `activity.unknown` fallback); vi default, every key in both languages, CI key-set equality (10.2.5-T1) and the `messageKey` catalogue check (09.3.6-T2); server-emitted text (receipt, consent, error `detail`) is never a key. | README SD13 ✅ |
| F15 | An `audit_logs` row carries a `User`/`Admin` visibility; account screens call only `GET /me/activity` and `GET /episodes/{id}/activity` and never ask for admin visibility; revoking a session removes that device's trust; "this wasn't me" = `POST /me/sessions/revoke-all` + a link to password reset. | README FC11, SD9 ✅ |
| F16 | Server-side (not owned here, only consumed): `payments`/`credit_lots`/`credit_ledger` state, the reservation ledger, `PaymentStatusSyncJob`/`PaymentAnomalyJob`/`CreditReconciliationJob`, and every admin billing route are 12's; the webhook is the only `[AllowAnonymousRequest]` route that writes money state. | `12 …` 12 CR-3, 12 CR-11 ✅ |

## 3. Domain and data

This folder is front-end only: it **mirrors** server DTOs one-for-one (no renames, no derived money fields) and owns no table. Model files live next to the screens that use them, are `readonly` with exact wire names, and are never summed client-side.

| Type (file) | Shape (wire-exact) | Rules |
|---|---|---|
| `CreditBalance` (`features/billing/model/credit-balance.ts`) | `{ total: number; free: number; purchased: number; nextExpiry: { amount: number; expiresAt: string } \| null }` | Rendered verbatim in the wallet card and the `header-end` chip. **No** `total = free + purchased` assertion, **no** formatting arithmetic (F4, FC7). |
| `CreditLedgerEntry` | `{ id: string; createdAt: string; kind: string; amount: number; operation: string; episodeId: string \| null; title: string \| null; groupId: string \| null; quoted: number \| null; charged: number \| null; released: number \| null; usage: UsageBreakdown \| null }` | One row per `groupId`; entries with no `groupId` render as their own row (F3). |
| `UsageBreakdown` | `{ inputTokens: number; cachedInputTokens: number; outputTokens: number; imageCount: number; gpuSeconds: number; estimated: boolean }` | Shown in the row's `@defer` detail panel; `estimated` renders the "estimated" chip. |
| `LedgerLine` (view model) | `{ groupId: string \| null; primary: CreditLedgerEntry; secondary: CreditLedgerEntry[]; occurredAt: string }` | Pure fold function `foldLedger(items)`; `secondary` only for rows sharing the `groupId`; the fold sorts by `createdAt` and keeps `quoted`/`charged`/`released` of the reservation primary (F3, FC7). |
| `Payment` / `PaymentDetail` | `PaymentDto` / `PaymentDetailDto` exactly as F5 | `status` is a string union of 00 §6 values including `pending_approval`; an unknown value renders as-is via `StatusChip` (forward-compatible, no `switch` default that hides it). |
| `CheckoutSession` | exact F2 shape | `checkoutUrl` and `bankQr` are both nullable and never both required; `bankQr` fields are rendered verbatim (F2, FC3). |
| `CreditPackage` | `{ id; code; nameVi; nameEn; credits; bonusCredits; priceVnd; priceUsd \| null; sortOrder }` | Ordered by `sortOrder`; the visible name follows the current Transloco language (`nameVi`/`nameEn` — the only language-dependent choice). |
| `Quote` | `{ operation; tier; maxCredits; typicalCredits \| null; balanceAfter; priceVersion }` | Rendered as "up to N, usually about M" when `typicalCredits != null`; `balanceAfter` is displayed from the server, never computed (F4). |
| `ActivityItem` | `ActivityItemDto` exactly as F8 | Label = `activity.<messageKey>` with `activity.unknown` fallback; `params` are interpolated, never templated into HTML (D4). |
| `SessionInfo` | `SessionDto` exactly as F8 | Rendered verbatim; `ip`/`userAgent` shown as the DTO gives them; `deviceLabel` + `isCurrent` drive the trusted-device badge (F15). |
| `AccountProfile` | `{ email; displayName; uiLocale; … }` as `GET /me` returns | `PATCH /me` sends only the changed fields; `uiLocale` also drives Transloco (D10). |

**Server-side expectations referenced, not implemented** (12/09 own them; named here so the client's pagination and states match): `GET /credits/ledger` and `GET /billing/payments` are cursor-paged (`?cursor=&limit=`, max 100) — the client sends `limit=20` [INFERENCE] and never aggregates; `payments.status` transitions in a single transaction per payment with `xmin` ETags (`"pm-<xmin>"`) for admin writes only; `credit_lots` enforces `remaining <= amount` [INFERENCE]; the reservation rows behind `groupId` are the ledger's own unit of display.

**Client state.** Signals only. One root `BalanceStore` (`features/billing/state/balance.store.ts`) holds `CreditBalance` + a `refresh()` that calls `GET /credits/balance`; it is the only writer for the `header-end` chip, is driven by `CreditBalanceChanged` (ids-only, F11) and by explicit `resync()` after a checkout/refund/promo mutation. No store caches a printed total independently: the ledger page and the wallet page both read the store. `PreferencesStore` remains the single storage site; the checkout hand-off key is the one addition (see §8).

## 4. Use cases

**UC-1 — Buy credits (10.8.1-T1, 10.8.4-T1).**
- *Given* a signed-in user with `billing_enabled` on, *when* `/billing/packages` loads, *then* cards render from `GET /billing/packages` in `sortOrder`, each showing `credits` + `bonusCredits` + `priceVnd` (and `priceUsd` when non-null).
- *When* the user opens the checkout dialog and submits, *then* exactly one `POST /billing/checkout` is issued per intent (`IdempotencyKeys`), the pending `paymentId` is written to `core/prefs` **before** any top-level navigation, and the response decides the branch: `checkoutUrl != null` → `window.location.assign(checkoutUrl)`; else `bankQr != null` → the QR card renders `qrPayload` verbatim (no navigation).
- *Given* a double click, *then* one payment exists (same `Idempotency-Key`), and the second response's 409 `payment_invalid_state` never shows a raw error (it re-reads the payment).
- *Given* 409 `payment_invalid_state`, 400 `promo_*`, 404 `package_not_found`, 400 `package_below_cost` or 429 `rate_limited`, *then* the mapped copy renders, the dialog stays open and the button re-enables.

**UC-2 — Provider return (10.8.1-T1).** *Given* the provider redirects to `/billing/return?paymentId=…&orderCode=…`, *when* the page initialises, *then* it takes `paymentId` from the query **or** `core/prefs`, ignores every other parameter, and starts `visible-poll` on `GET /billing/payments/{id}` with backoff 2/4/8/15 s **until** `paid`/`failed`/`needs_review`/`pending_approval` or the checkout `expiresAt` — never showing success before the server says `paid` and never granting anything client-side (FC3, ST-8). Terminal `paid` → success panel + `BalanceStore.refresh()`; `pending_approval`/`needs_review` → the paused panel with the explain-copy; `failed`/expired → the failure panel with a retry link.

**UC-3 — Wallet, history, receipt, refund, low balance (10.8.3-T1).**
- *Given* `/billing/wallet`, *then* the balance card renders `{ total, free, purchased, nextExpiry }` verbatim, the low-balance banner appears only on the server-signalled condition (12.6.3-T1's low-balance signal via the balance/threshold data the API returns, F4/F11) and the buy CTA links to `/billing/packages`.
- *Given* `/billing/payments`, *then* the cursor list pages through `cursor-page.ts` (empty state, error state, retry) and each row links to `/billing/payments/:id`.
- *Given* `receiptAvailable`, *when* the user clicks "Receipt", *then* the API's `text/html` response opens in a new tab (`rel="noopener"`) and printing uses `window.print()` — no PDF library, nothing rendered through `[innerHTML]` (SD15, D4).
- *Given* `refundEligible`, *when* the user submits the refund dialog, *then* one `POST …/refund-request` per intent (`Idempotency-Key`), 409 `refund_not_eligible` / 403 `account_under_review` map to copy, and success re-reads the payment detail.
- *Given* the ledger tab, *then* one line per reservation with `quoted`/`charged`/`released` + `operation` (F3, FC7).

**UC-4 — Paused processing (10.8.4-T1).** *Given* `pending_approval`, *then* the paused card shows the pending-approval copy, the amount and `expiresAt`, **no** balance change and **no** retry button (the decision is an operator's); *given* `needs_review`, the copy explains the mismatch/QR case and links to support; *given* `expired`, the copy offers a fresh checkout and the return page clears the prefs key.

**UC-5 — Account, export, deletion (10.8.2-T1).** *Given* `/account`, *then* the profile form loads `GET /me`, saves changed fields only via `PATCH /me` (`bindFieldErrors` for 400 `validation_failed`), and the language picker writes `uiLocale`; *when* the user requests an export, *then* `POST /me/export` is called once per intent (202, `Idempotency-Key`, max 1/24 h), the status route polls `GET /me/exports/{id}` via `visible-poll` (capped 10 min) and refreshes pre-signed links at expiry (≤ 15 min, 13.3.3-T1); *when* the user requests deletion, *then* 13.3.2-T1's flow runs and `GET /me/deletion-requests` drives the status page; legal re-acceptance opens `LegalAcceptanceDialog` (13.2.1-T1) when `GET /me/legal` says a version is stale.

**UC-6 — Activity, History, sessions (10.8.5-T1).** *Given* `/account/activity`, *then* `GET /me/activity` renders a `cursor-list` timeline with an icon per `category`, `activity.<messageKey>` labels, deep links from `targetType`/`targetId`, filters that produce a **filtered-empty** state distinct from the empty one; *given* an episode, the History tab renders `GET /episodes/{id}/activity` with the same component; *given* `/account/security`, *then* `GET /me/sessions` renders ≤ 20 rows verbatim with `isCurrent` + `deviceLabel`, `DELETE /me/sessions/{id}` (204, optimistic removal reverted on error) removes that device's trust, "This wasn't me" = `POST /me/sessions/revoke-all` + a password-reset link, and the page never asks for admin-visible rows (FC11).

## 5. API and events

Verbs, paths and parameters exactly as the sources; the client never calls anything not in this table.

| # | Call | Request | Response | Codes / notes |
|---|---|---|---|---|
| 1 | `GET /credits/balance` | — | `CreditBalanceDto` | 200; not gated by `billing_enabled`; `header-end` chip + wallet card (F4) |
| 2 | `GET /credits/ledger` | `?cursor=&limit=` | `{ items, nextCursor }` | 200; one UI line per `groupId` (F3) |
| 3 | `GET /billing/quote` | `?operation=story_generate\|fact_sheet\|render&episodeId=` | `QuoteDto` | 400 on `story_review`/`story_revision`; only where a pre-action cost is shown (F4) |
| 4 | `GET /billing/packages` | — | `{ items: CreditPackageDto[] }` | 200; authenticated, no flag (F4) |
| 5 | `POST /billing/checkout` | `{ packageId, promoCode?, method?, invoiceInfo?: { companyName, taxCode, address } }` + `Idempotency-Key` | `CheckoutSessionDto` | 409 `payment_invalid_state`, 400 `promo_*`, 400 `package_below_cost`, 404 `package_not_found`, 403 `forbidden`; gated by `billing_enabled` (F2, F7) |
| 6 | `POST /billing/promo/redeem` | `{ code }` + `Idempotency-Key` | `{ creditsGranted, balance }` | 400 `promo_invalid`/`promo_expired`, 409 `promo_exhausted`/`promo_already_used`; gated by `billing_enabled` (F5, F7) |
| 7 | `GET /billing/payments` | `?cursor=&limit=` | `{ items: PaymentDto[], nextCursor }` | 200; never gated by the flag (F5, F7) |
| 8 | `GET /billing/payments/{id}` | — | `PaymentDetailDto` | 404 `not_found`; polled by `/billing/return` and by 10.8.4-T1 (F5) |
| 9 | `GET /billing/payments/{id}/receipt` | — | `text/html; charset=utf-8`, `inline` | opened in a new tab + `window.print()` (F5, SD15) |
| 10 | `POST /billing/payments/{id}/refund-request` | `{ reason }` + `Idempotency-Key` | `RefundRequestDto` | 409 `refund_not_eligible`, 403 `account_under_review` (F5) |
| 11 | `GET/PATCH /me` | `PATCH` sends changed fields only | profile | 400 `validation_failed` → `bindFieldErrors` (F8) |
| 12 | `GET /me/legal`, `POST /me/legal/accept` | `{ tos, privacy }` | `LegalAcceptanceDto` | consumed via 13.2.1-T1's dialog (F9) |
| 13 | `GET /me/activity` | `?category=&from=&to=&cursor=&limit=` | `{ items: ActivityItemDto[], nextCursor }` | 200; user-visible projection only (F8, F15) |
| 14 | `GET /episodes/{id}/activity` | `?cursor=&limit=` | `{ items, nextCursor }` | 200; the History tab (F8) |
| 15 | `GET /me/sessions` | — | `{ items: SessionDto[] }` | ≤ 20 rows, no cursor (F8) |
| 16 | `DELETE /me/sessions/{id}` | — | 204 | 404 `not_found` for another user's id (F8) |
| 17 | `POST /me/sessions/revoke-all` | — | 204 | keeps the current family (F8) |
| 18 | `POST /me/export` | + `Idempotency-Key` | 202 | 429 when repeated < 24 h (13.3.3-T1, F9) |
| 19 | `GET /me/exports/{id}` | — | export status + pre-signed links | links refreshed at expiry (≤ 15 min) (F9) |
| 20 | `GET /me/deletion-requests` | — | deletion status | 13 CR-8 route (F9) |

**Events.** `CreditBalanceChanged { userId }` (existing hub, D8/SD16) → `BalanceStore.refresh()` only: it refetches #1 and, when the ledger or wallet page is open, that page's list. No toast, no arithmetic, no optimistic total (F11). No new hub event is added, and no other screen subscribes.

**Polling.** Only `core/state/visible-poll.ts`: `/billing/return` → #8 capped at `expiresAt`; export status → #19 capped 10 min. Everything else refetches on navigation, `resync()` or a user action (SD16).

**Error envelope.** Errors arrive as `ErrorOr` → `AppError { code, detail?, fieldErrors? }` mapped by `AppErrorMapper` (D9); `unknown_error` renders the `traceId` (FC15); no screen shows a raw HTTP status or a stack trace.

## 6. UI

Design authority: **`10.8.1-T0`** (SD11/S-O1) — wireframes + hi-fi mockups for every screen and state below at 360/768/1280, vi+en, dark+light, focus order, keyboard path and a11y notes, built from the D11 tokens/components.

**Design link and owner approval.** `[OPEN]` — `<design link to be recorded here by 10.8.1-T0; owner approval (name + date) recorded in this section before any implementation task starts>` (design gate, `README.md:718`). ✅ Approved design: none yet — this folder is not implementation-ready until the line above is filled (DoR gate, §C11).

| Screen (route) | Feature | States to render | Owner |
|---|---|---|---|
| `/billing/packages` [DECISION] | `billing` | loading (skeleton), empty (no packages), error + retry, success (cards), flag-off (buy UI hidden, read-only explanation) | 10.8.1-T1 |
| Checkout dialog (on the packages page) | `billing` | idle, submitting, promo applied, promo rejected (`promo_*`), invoice-info expanded, redirecting, bank-QR alternative, 409 `payment_invalid_state`, 429 `rate_limited` | 10.8.1-T1 (QR branch: 10.8.4-T1) |
| `/billing/return` (fixed) | `billing` | confirming (poll), paid, pending_approval, needs_review, failed, expired, missing/unknown `paymentId` | 10.8.1-T1 (paused states: 10.8.4-T1) |
| `/billing/ledger` [DECISION] | `billing` | loading, empty, error + retry, success (one line per reservation), row detail (`@defer`) | 10.8.1-T1 |
| `/billing/wallet` (fixed) | `billing` | loading, error, success (balance card verbatim), low-balance banner, nextExpiry present/absent | 10.8.3-T1 |
| `/billing/payments` (fixed) | `billing` | loading, empty, error, success (cursor list), filtered-empty | 10.8.3-T1 |
| `/billing/payments/:id` (fixed) | `billing` | loading, 404, success, receipt available/absent, refund eligible/not, refund dialog submit/success/`refund_not_eligible`/`account_under_review`, refund pending | 10.8.3-T1 |
| `/account` (fixed) | `account` | loading, error, success (form), saving, field errors, locale changed, export pending/ready/expired-link/`rate_limited`, deletion pending/requested, legal re-acceptance required | 10.8.2-T1 |
| `/account/export` + `/account/deletion` [DECISION] | `account` | pending (poll), ready (fresh links), expired link, failed, requested | 10.8.2-T1 (engine: 13.3.3-T1/13.3.2-T1) |
| `/account/activity` (fixed) | `account` | loading, empty, filtered-empty, error, success, row detail (`@defer`) | 10.8.5-T1 |
| Episode History tab (`/episodes/:id` tab) | `account` (component) | loading, empty, error, success — same component as the timeline | 10.8.5-T1 |
| `/account/security` (fixed) | `account` | loading, empty, error, success (≤ 20 rows), revoking row, revoke-all confirm, revoke-all done, `isCurrent` marked | 10.8.5-T1 |
| `header-end` chip (shell slot, D14) | `billing` | hidden when the balance is unknown, loading, value, error (silent — no shell error) | 10.8.3-T1 |
| `cost-line` slot (in `ConfirmDialog`, D14) | `billing` | hidden, quote loading, quote shown ("up to N, usually about M"), quote failed (silent) | 10.8.1-T1 |

Breakpoints: 360 (single column, chips wrap, dialogs full-screen; the QR card scales to ≥ 240 px so nothing is zoom-clipped — SD8/360 rule), 768 (two-column cards, side-by-side balance/banner), 1280 (three-column package grid, table-style ledger/payments/activity with sticky headers). Themes: dark default + light, all states contrasted in both (D11 tokens). Languages: vi default + en, all copy from Transloco scopes `billing`/`account`/`errors`/`activity` (no hard-coded string, no server text as a key). Focus order: page header → primary action → content; dialogs trap focus, restore it to the opener, and announce state changes via `aria-live` (`aria-live="polite"` for poll transitions, `role="alert"` for errors). Money is never colour-only (`StatusChip` carries a text label; FC10 "no colour-only status", SD12).

Components (all D11/reuse, no new visual primitives without a T0 design item): `PageHeader`, `LoadingState`/`EmptyState`/`ErrorState`, `StatusChip`, `ConfirmDialog` (+`cost-line`), `RetryCountdown` (poll backoff countdown), `CopyableCode` (ledger/payment ids, order code, export links), `CharCounter` (refund reason), `bindFieldErrors`/`FormErrorAlert`, `LegalAcceptanceDialog` (13.2.1-T1), `ReauthDialog` (10.3.3-T3, unused by this folder unless a route re-auths). New folder-local components: `CreditPackagesPage`, `CheckoutDialog`, `BankQrCard`, `BillingReturnPage`, `LedgerPage` + `LedgerRowDetail`, `WalletPage` + `BalanceCard` + `LowBalanceBanner`, `PaymentsPage`, `PaymentDetailPage` + `ReceiptButton` + `RefundRequestDialog`, `BalanceChip` (slot), `CostLine` (slot), `AccountPage` + `AccountExportPage` + `AccountDeletionPage`, `ActivityTimeline` + `EpisodeHistoryTab`, `SessionsPage` + `RevokeAllDialog`.

Nav: 10.8.1-T1 appends `Billing` to `NAV_ITEMS`; 10.8.2-T1 appends `Account` (F1, WT-W6 precedent). No admin entries here.

## 7. Jobs and integrations

- **No background job is created by this folder** (front-end only). The jobs behind the consumed states belong to 12: `PaymentStatusSyncJob`, `PaymentAnomalyJob`, `CreditReconciliationJob` (12 CR-3) — named here so a paused payment is understood as a server state, never a client timer.
- **Realtime hub (D8/SD16).** `CreditBalanceChanged { userId }` is the only subscription; it is ids-only and triggers refetch. Connection lifecycle, `ActivityTracker` gating and reconnect are 10.2.4-T1's (`RealtimeService`); this folder only calls `BalanceStore.refresh()` from a handler and unsubscribes on `DestroyRef`.
- **Third-party (SD15).** One new runtime dependency: `qrcode` (self-hosted MIT) loaded behind `@defer` inside `BankQrCard`, importing only the string-mode renderer; no CDN, no vendor script, no analytics, no font. The receipt is the API's `text/html` opened in a new tab and printed with `window.print()` — no PDF library.
- **Content/tooling.** `tools/web/check-gzip-budget.mjs` gains this folder's lazy chunks in the per-route budget table (10.10.1-T2 owns the script change; this folder only must stay inside it, FC8). `tools/web/legal-to-json.mjs` and `web/src/content/legal/*` are 10.3.1-T1's, consumed by the legal-acceptance entry point.
- **i18n pipeline.** Transloco scopes `billing`/`account` + `errors.*` keys for every F10 code + `activity.*` labels; the key-set equality job (10.2.5-T1) and the server `messageKey` catalogue check (09.3.6-T2, SD13) run in CI — this folder adds keys, not pipeline.
- **Environments.** Staging/prod must have 12's and 13's endpoints deployed (M2 entry gate, §C7); the sandbox checkout reaches `paid` through the return page with `billing_enabled` on, and the flag can be switched off without losing access to payments/receipts/refunds (F7).

## 8. Security and privacy

**Money integrity.** No credit value is ever computed, summed or displayed as an optimistic total in the client; every amount comes from a server DTO (FC7). The return page reads `paymentId` from the query **or** `core/prefs` and ignores every other parameter, never trusts a status in the URL, and shows success only when `GET /billing/payments/{id}` says `paid` (FC3, ST-8). `Idempotency-Key` (10.2.3-T1) is attached to `POST /billing/checkout`, `POST /billing/promo/redeem`, `POST /me/export` and `POST /billing/payments/{id}/refund-request` — one key per user intent, settled after a terminal outcome so a retry of the same intent reuses it (F12, FC7). `qrcode` renders the server's `qrPayload` verbatim; the payload is never logged.

**Storage.** `core/prefs` stays the only storage site (D6). The one addition is the checkout hand-off: `core/prefs/checkout-handoff.ts` writes `{ paymentId, expiresAt }` under `nts.checkout.v1` in `sessionStorage` **before** the top-level navigation and clears it on a terminal state (paid/failed/expired) — no PII, no amount, no token, no cross-user value; reads are `try/catch`-guarded like `PreferencesStore`. ⚠️ D6/10.1.1-T1's "only the UI language/theme" line predates FC3's prefs hand-off; the two must be reconciled by the owner (raised as a folder note, see `## 11`).

**Tokens and privacy.** Access/refresh tokens stay in memory via the existing interceptor/session store — never in `core/prefs`, never in a URL, never logged. No screen calls an admin-visibility route; activity comes only from `GET /me/activity` / `GET /episodes/{id}/activity`, and the sessions list renders the DTO fields and nothing more (no raw `audit_logs.data`, no extra IP/UA formatting) (F15, FC11). Revoking a session kills that device's trust; revoke-all keeps the current family; the "this wasn't me" path is revoke-all + password reset (no support impersonation). Receipts and export links are per-user pre-signed URLs: rendered via `safe-filename.ts` where a download name is offered, opened with `rel="noopener"`, never pasted into a log or an `[innerHTML]`.

**Client hardening.** CSP/Trusted Types from 10.1.6-T1 are unchanged and asserted: no `bypassSecurityTrust*`, no `[innerHTML]` for receipt or activity text, no inline script, no third-party asset on these routes (D4, SD15). `AppErrorMapper` maps codes to copy, and `unknown_error` surfaces `traceId` (FC15). Admin mutations and any money state change rely on the server's `[Audited]` rows — the client adds no audit of its own and never claims success before a refetch (FC15). `rate_limited` (429) always shows the retry copy, on checkout, promo, export and refund alike.

## 9. Test plan

Workspace: `web/` (Angular 22, zoneless, Vitest + Playwright per D15, 10.1.5-T1 config). No .NET project is modified by this folder; the consumed wire is verified server-side in `NightTales.Api.IntegrationTests` / `NightTales.Application.UnitTests` / `NightTales.Domain.UnitTests` (12/09/13) and this folder asserts only the client half — it consumes DTO fixtures, not the API. D15 fakes: hand-written `fake-billing-repository.ts` / `fake-account-repository.ts` (and the shared fakes of 10.1.5-T1) — no mocking library.

| Project/file | Kind | Cases |
|---|---|---|
| `features/billing/state/balance.store.spec.ts` | Vitest | `CreditBalanceChanged` → one refetch, no arithmetic; a failed refetch keeps the last value and flags the chip as stale; `resync()` after a promo/refund |
| `features/billing/ledger/ledger-fold.spec.ts` | Vitest | entries sharing a `groupId` fold to one line carrying `quoted`/`charged`/`released` + `operation`; entries without a `groupId` stay separate; ordering by `createdAt`; an unknown `kind`/`operation` renders from the catalogue rather than throwing |
| `features/billing/packages/*.page.spec.ts` | Vitest + axe (`wcag2a, wcag2aa, wcag21a, wcag21aa, wcag22aa`) | loading/empty/error/success; cards in `sortOrder`; flag-off hides the buy CTA; double-submit → one `POST /billing/checkout` (same key); `checkoutUrl` → `window.location.assign` spy; `bankQr` → no navigation |
| `features/billing/return/billing-return.page.spec.ts` | Vitest | `paymentId` from query and from `core/prefs`; a bogus `status=paid` param in the URL is ignored; poll stops at `paid`/`failed`/`needs_review`/`pending_approval`/`expiresAt`; `paid` triggers exactly one balance refresh |
| `features/billing/payments/payment-detail.page.spec.ts` | Vitest + axe | receipt opens a new tab with `noopener` and never via `innerHTML`; refund submit is idempotent per intent; `refund_not_eligible`/`account_under_review` copy; `refundEligible=false` hides the button |
| `features/account/settings.page.spec.ts` | Vitest + axe | `PATCH /me` sends changed fields only; `bindFieldErrors` on 400; locale change round-trips through `uiLocale`; export repeats < 24 h show the 429 copy |
| `features/account/security/sessions.page.spec.ts` | Vitest + axe | ≤ 20 rows rendered verbatim; `DELETE` removes the row and re-adds it on error; revoke-all confirm; "wasn't me" links to password reset; no admin-visibility call is made (the fake throws on one) |
| `features/account/activity/activity-timeline.spec.ts` | Vitest + axe | `activity.<messageKey>` label + `activity.unknown` fallback; deep link per `targetType`; filtered-empty ≠ empty; `@defer` detail panel |
| `web/e2e/billing.spec.ts` | Playwright + axe | packages → checkout → return → `paid` in the compose stack, with zero `securitypolicyviolation` events and no client grant; ledger one line per reservation; receipt prints; flag off hides only the buy UI |
| `web/e2e/billing-paused.spec.ts` | Playwright | `pending_approval` / `needs_review` / `expired` panels; no retry affordance on `pending_approval`; prefs key cleared after a terminal state |
| `web/e2e/account.spec.ts` | Playwright + axe | profile save, export pending→ready with link refresh, deletion status, activity + episode History tab, sessions revoke/revoke-all |
| `tools/web/check-gzip-budget.mjs` (owned by 10.10.1-T2) | CI | the `billing`/`account` lazy chunks stay inside their per-route budget and are absent from the initial bundle (FC8) — this folder's acceptance is "the check passes for these routes" |
| `10.10.1-T1` axe run | CI/manual | every route of §6 passes `wcag22aa`; the manual matrix covers keyboard-only, focus restore in dialogs, 200 % zoom, 360 px, `prefers-reduced-motion`, `aria-live` poll announcements |

No test is written against implementation text or incidental markup (no snapshot of a template); money assertions are made against DTO fixtures, never against a client-computed sum. A regression test accompanies any bug fixed in this folder.

## 10. Tasks and re-estimate

Rows equal `README.md` §C8.1 verbatim (Size/Depends on/Blocks/Phase as written there):

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 10.8.1-T0 | M (1.0) | UX | S · M2 | 10.1.3-T1, 12.2.2-T1, 12.3.2-T1 | 10.8.1-T1, 10.8.2-T1, 10.8.3-T1, 10.8.4-T1, 10.8.5-T1 |
| 10.8.1-T1 | M (1.0) | FE | S · M2 | 10.8.1-T0, 12.3.2-T1, 12.7.4-T1, 12.1.3-T1, 10.2.3-T1 | 10.8.3-T1, 10.8.4-T1, 10.10.1-T1, 10.10.2-T2 |
| 10.8.3-T1 | M (1.0) | FE | S · M2 | 10.8.1-T0, 10.8.1-T1, 12.2.2-T1, 12.7.3-T1, 12.6.1-T1, 12.6.3-T1 | 10.10.1-T1 |
| 10.8.4-T1 | S (0.5) | FE + Payments | S · M2 | 10.8.1-T0, 10.8.1-T1, 12.7.4-T1, 12.8.3-T1 (soft) | 10.10.1-T1 |
| 10.8.2-T1 | M (1.0) | FE | S · M2 | 10.8.1-T0, 13.3.3-T1, 13.3.2-T1, 13.2.1-T1 | 10.10.1-T1 |
| 10.8.5-T1 | M (1.0) | FE | S · M2 | 10.8.1-T0, 09.3.6-T2, 09.3.7-T1, 10.5.3-T1 (10 CR-34) | 10.10.1-T1 |

Folder total **5.5 d** raw (§C9: 4.5 §6 rows + 1.0 `10.8.1-T0`; area row `10.8 Billing + account: 4.5 → 5.5`, 10 CR-22). No task exceeds L; 6 task files. Implementation order per §C5 step 3: `10.8.1-T1` → `10.8.4-T1` → `10.8.3-T1` → `10.8.2-T1` → `10.8.5-T1`, all hard-blocked by `10.8.1-T0` + owner design approval (SD11).

## 11. Rollout

**Order.** (1) `10.8.1-T0` design approved by the owner (gate; nothing below starts). (2) `10.8.1-T1` — packages, checkout dialog, `/billing/return`, ledger, `cost-line`, `Billing` nav. (3) `10.8.4-T1` — QR card + paused panels (extends 10.8.1-T1's return page; soft dependency on 12.8.3-T1). (4) `10.8.3-T1` — wallet, `header-end` balance chip, payments list/detail, receipt, refund, low-balance banner. (5) `10.8.2-T1` — account settings, export, deletion. (6) `10.8.5-T1` — activity, History tab, sessions. (7) `10.10.1-T1` axe + manual matrix over all of the above.

**Flags.** `billing_enabled` (default false, 12 CR-3) gates only payment creation and the buy/promo UI; the read routes (balance, ledger, packages, payments, receipt, refund-request) and the account screens stay available with the flag off — the packages page must render a "credits are granted by the operator in closed beta" state, not an error (F7). `CreditBalanceChanged` handling is flag-independent. No other flag is introduced by this folder.

**Migration/back-compat.** None — no schema, no route rename. Forward-compatibility: an unknown `payments.status`, `ledger.kind`, `activity.category` or `operation` renders as-is via `StatusChip`/the label catalogue with an `unknown` fallback (never throws, never hides the row).

**Rollback.** Disable `billing_enabled` → new checkouts/promo redemptions stop, in-flight provider returns still resolve through `GET /billing/payments/{id}` (F7). A client rollback is a plain re-deploy of the previous SPA; nothing here writes server state that a client rollback would orphan except the cleared-in-terminal-state prefs key, which is inert.

**Verification at M2 exit (folder's share of §C7).** Compose stack + sandbox provider: registration→login with `billing_enabled` on, checkout → `paid` through the return page with **no** client grant, `expired`/`pending_approval`/`needs_review` each rendering their own state, ledger showing one line per reservation, receipt printing, export/deletion status pages driven by 13's engine, activity + History + sessions working with revoke semantics, zero CSP violations on every route, `wcag22aa` axe clean, per-route budgets green, i18n key sets equal in vi/en incl. `errors.*` and `activity.*`.

**Open items carried out of this folder** (owner/TL, not silently resolved): (a) the design link + owner approval line in §6 is unfilled until `10.8.1-T0` runs (design gate); (b) D6/10.1.1-T1 ("only UI language/theme may persist") needs a one-line addendum for the checkout hand-off key mandated by FC3; (c) the episode-page tab mount for `EpisodeHistoryTab` is not assigned in Part C — 10.8.5-T1 owns the component and asks 10.5.3-T1's page for the tab slot (CR-worthy if wave 1's page exposes no extension point); (d) `GET /me/deletion-requests` is a 13 CR-8 route and `GET /me/exports/{id}` fields are 13's, so 10.8.2-T1's status pages are pinned to 13's DTO names once 13's Part C is on disk.
