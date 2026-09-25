# 12.7 — Packages, promotions, receipts and bank QR

**Folder:** `plans/features/12-billing-credits/12.7-packages-promos/` · **Epic:** 12.7 (plan `plans/12-billing-credits.md` §"Epic 12.7") · **Contract:** `../README.md` §0 folder brief (line 36), §3 D4/D8/D13/D14/D16/D17/D18, §3.1 (frozen routes/DTOs), §8.1 rows (lines 346–350), §10 O-7/O-8 + BR6/BR12/BR13/BR17 · **Legend:** ✅ verified on disk · ⚠️ unverified/external · [DECISION] owner-accepted · [OPEN] carried to DoR · [INFERENCE] derived here.

This folder carries **4 tasks — 3.0 d raw** (`12.7.1-T1` 0.5 + `12.7.2-T1` 1.0 + `12.7.3-T1` 0.5 + `12.7.4-T1` 1.0). Epic 12.7 is **4.0 d** (README §9): the remaining 1.0 d is `12.7.2-T2` (0.5, owned by `../12.5-admin-minting/`) and `12.7.5-T1` (0.5, owned by `../12.3.5-review-refunds/`).

## 1. Scope

**In scope.** The catalogue and the offers, and the two read/wire surfaces 10 S consumes:

- `12.7.1-T1` — `credit_packages` (00 §6:297 ✅) + admin CRUD `GET/POST /admin/credit-packages` (+ `/{id}`, PATCH) + the cost-floor guard `package_below_cost` (D4, BR13) + the `GET /billing/packages` read model.
- `12.7.2-T1` — `promo_codes` (00 §6:298 ✅) + `promo_redemptions` (00 §6:299 ✅), the **implementation of `IPromoValidator`** declared by `12.3.2-T1` (BR6), `POST /billing/promo/redeem`, the admin list/create/patch routes, and the promo half of the `payment_paid.v1` handler (`purchase`/`bonus` rows are 12.3.3-T1's; the promo lot is this task's).
- `12.7.3-T1` — `GET /billing/payments`, `GET /billing/payments/{id}` and `GET /billing/payments/{id}/receipt` (`text/html; charset=utf-8`, `Content-Disposition: inline`, D18/BR12/CR-2).
- `12.7.4-T1` — the bank-transfer QR order path (`method = bank_qr`) as the buyer sees it: QR details carrying the **exact** amount and the row's `order_code`, the provider capability wiring (`Capabilities.BankQr`; unsupported ⇒ 400 `payment_method_unavailable`, fail-closed — BR32), the expiry/countdown contract (BR11), the payload-handling rules, and the `needs_review` handoff QA for 10.8.4. Resolution-by-reference, the amount/currency gate and the unmatched-event outcome stay **`12.3.3-T1`'s** (DoR-12).

**Out of scope (owned elsewhere — never re-implemented here).** `IPaymentProvider` + `POST /billing/checkout` + `payments` row (`12.3.2-T1` — it does **not** mount `GET /billing/packages`, BR32); webhook verification and `ProcessPaymentWebhookCommand` (`12.3.3-T1`); the status-sync/anomaly jobs (`12.3.3-T2`); refund/chargeback voiding (`12.3.4-T1`); resolve queue + approval (`12.3.5-T1`/`12.3.5-T2`); refund requests (`12.7.5-T1`); owner-only promo hardening + the minting-exposure arithmetic (`12.7.2-T2`); ledger/lots (`12.2.1-T1`, `12.6.1-T1`); a server-side PDF library (**never** — F19/CR-2); VAT e-invoice issuing (⚠️ out of MVP build, plan §12.7.3).

**Hard stop conditions.** Every route name, DTO field, table, column, error code, config key and idempotency key below is copied from README §3/§3.1/§5 and 00 §6/§8 — BR15. A name that is not in the contract is not added here; gaps are listed in §11 DoR items, not invented.

## 2. Confirmed facts

| # | Fact | Evidence |
|---|---|---|
| C1 | Tables and columns are fixed by 00 §6: `credit_packages` (`:297`), `promo_codes` (`:298`), `promo_redemptions` (`:299`), `payments` (`:300`), `credit_lots` (`:296`) | README C1 ✅, 00 §6 quoted on disk |
| C2 | Money conventions: `numeric(12,6)` USD, credits `bigint`, `id uuid` UUIDv7, `timestamptz`, optimistic concurrency via `xmin`, soft delete only per retention | README C2 ✅ |
| C4 | Routes already fixed in 00 §8: Credits/Billing row L365 (`GET /billing/packages`, `POST /billing/promo/redeem`, `GET /billing/payments`, `GET /billing/payments/{id}`, `GET /billing/payments/{id}/receipt`) and Admin row L367 (`GET/POST /admin/credit-packages`, `PATCH /admin/credit-packages/{id}`, `GET/POST /admin/promo-codes`, `PATCH /admin/promo-codes/{id}`) | README C4 ✅ |
| C7 | `price_versions` P subset (`version`, `active_from`, `rates`, `limits`) exists from 02.2.4-T1 via `IPriceCatalog`; the credit columns (`credit_usd`, `margin`, `fee_rate`, `fx_buffer`, `gpu_s_per_video_min`, image/GPU rates, `floors`) arrive with 12.1.2 (M2) | README C7 ✅ |
| C13 | There is **one** alert pipeline (11.6.2-T1); no plan may invent a second | README C13 ✅ |
| C14 | 10 S owns the money UI: `10.8.x` owns credits/quote/buy/checkout and the `CreditBalanceChanged` realtime handling | README C14 ✅ |
| F16 | `max_redemptions` under the `promo_codes` row lock and `per_user_limit` under the user's `credit_accounts` lock are **both** required; a `fixed_credits` redemption has `payment_id = NULL`, so the partial unique does **not** stop a double redeem of a payment-less code | README F16 (D14) ✅ |
| F17 | The user-visible money surface is vi+en and must match the legal texts | README F17 (D18) ✅ |
| F19 | "HTML/PDF receipt" would imply a server-side PDF library: AGPL-ish/revenue-licensed options are excluded by the licence rule | README F19 (D18/CR-2) ✅ |
| F20 | Pinned indexes needed: `payments (status, created_at)`, `payments (order_code)` (unique), `credit_lots (payment_id)`, `promo_redemptions (promo_code_id, user_id)` | README F20 (D17) ✅ |
| F21 | Metric names: `billing_webhook_received_total{provider,result}`, `billing_webhook_signature_invalid_total`, `billing_payments_needs_review_total`, `billing_credits_minted_total{actor}`, `billing_credit_grant_pending`, `billing_openai_runway_days`, `billing_balance_liability_credits`, `billing_reservation_missing_total`, `billing_provider_loss_total` | README F21 (D17) ✅ |
| D4 | `credit_packages` prices are VND (int) + optional USD, validated against the cost floor of the **current** version and FX rate ⇒ 400 `package_below_cost` | README D4 ✅ |
| D8 | `IPaymentProvider` shapes verbatim; `CheckoutSession(string? CheckoutUrl, BankQrDetails? BankQr, DateTimeOffset ExpiresAt)`; `BankQrDetails(string QrPayload, string BankName, string AccountNumber, string AccountName, long AmountMinorUnits, string OrderCode)`; `Capabilities { Card, Wallet, BankQr, StatusApi, RefundApi }` | README D8 ✅ |
| D13/BR13 | Minting cap counts promo exposure `value × max_redemptions`; `UpsertPromoCodeCommand` (owner-only; admins may only set `active = false`) calls `MintingPolicy`; a package whose `bonus_credits` exceeds `credits` is rejected at save | README D13, BR13 ✅ |
| D14 | `promo_codes` as 00 §6:298; `kind ∈ bonus_percent \| fixed_credits`; validation order `promo_invalid`/`promo_expired`/`promo_exhausted`/`promo_already_used`; `bonus_percent` requires a paid purchase and is not minting exposure; redeem takes `credit_accounts` first, then `promo_codes`; at most one promo per payment; bonus credits granted **only** on `paid`, as a `source = promo` lot bound to that `payment_id` | README D14 ✅ |
| D16/BR7 | `package_below_cost` reads one `IFxRateProvider.CurrentAsync()` (latest `provider_topups.fx_rate`, fallback `Credit:FxUsdVndFallback`) | README D16, BR7 ✅ |
| D17 | Reuse: `UnitOfWorkBehavior`, `ISystemWorkQuery.PendingAsyncT`, `[OutboxEvent]`, `IEmailSender` template `payment_receipt`, `IRealtimeNotifier` (ids only), `xmin` ETags `"cp-<xmin>"` (credit packages), `"pc-<xmin>"` (promo codes), `"pm-<xmin>"` (payments) | README D17 ✅ |
| D18 | vi+en keys `billing.packages.*`, `billing.checkout.*`, `billing.payments.*`, `billing.history.*`, `billing.promo.*`, `billing.errors.*`, `billing.receipt.*`, `admin.billing.*`; receipt `text/html`, print-styled, `ui_locale`, `vi-VN`/`en-US` formats, business name/tax code/address from settings, no PII beyond the buyer's own | README D18 ✅ |
| O-7/BR17 | `billing_enabled` (default **false**) gates **order creation only**: `POST /billing/checkout`, `POST /billing/promo/redeem`, buy UI. Never gates the webhook, `GET /billing/payments*` + receipt, `POST /billing/payments/{id}/refund-request`, or the `PaymentStatusSyncJob`/`PaymentAnomalyJob`/`CreditReconciliationJob` registrations | README O-7, BR17 ✅ |
| O-8/BR12 | Receipts are HTML rendered for printing; no server-side PDF library | README O-8, BR12 ✅ |
| BR6 | Promo side effects ride `payment_paid.v1` in 12.7.2-T1's handler (checkout never depends on the promo task); checkout validates through `IPromoValidator`, declared by `12.3.2-T1` and implemented by `12.7.2-T1` | README BR6 ✅ |
| — | `IPromoValidator` is **frozen by BR32** (README §10 ✅): `ValidateAsync(string code, Guid userId, Guid packageId, long amountMinorUnits, ct) → ErrorOr<PromoValidation(Guid PromoCodeId, PromoKind Kind, long BonusCredits, long BonusPercent)>` — **declared by `12.3.2-T1`, implemented by `12.7.2-T1`** | ✅ README §10 BR32 (DoR-1 resolved) |
| C4.1 | `12.3.2-T1` "consumes" the `GET /billing/packages` read model "and never declares a second catalog"; its plan line 14/48 wording says "surface" — the owner of the read model is **this folder's 12.7.1-T1** — ruled by BR32 ("`GET /billing/packages` belongs to **12.7.1-T1** and 12.3 must drop its claim") | ✅ README §10 BR32 (DoR-4 resolved) |

## 3. Domain and data

**Owned tables (columns verbatim from 00 §6 ✅).**

- `credit_packages` (`:297`): `code` (unique), `name_vi`, `name_en`, `credits`, `bonus_credits`, `price_vnd`, `price_usd`, `active`, `sort_order` + C2 house columns (`id uuid` UUIDv7, `created_at`/`updated_at timestamptz`, `xmin` optimistic token ✅).
- `promo_codes` (`:298`): `code` (unique, **citext**), `kind` (`bonus_percent`/`fixed_credits`), `value`, `valid_from`, `valid_to`, `max_redemptions`, `per_user_limit`, `min_package_id`, `active`.
- `promo_redemptions` (`:299`): `promo_code_id`, `user_id`, `payment_id` (nullable for fixed-credit codes), `credits_granted`, `redeemed_at`; unique `(payment_id)` where not null ✅.
- Read-only here: `payments` (`:300`, snapshot columns `credits`, `bonus_credits`, `price_version`, `package_id`, `promo_code_id`, `order_code` unique, `invoice_info` jsonb, `status`, `expires_at`/`paid_at` from 12 CR-1), `credit_lots` (`:296`, `source = promo` lots created by 12.6.1-T1's `GrantAsync`), `credit_accounts` (`:319`).

**Domain types (this folder's; names are new, no contract name is duplicated).** C# PascalCase below mirrors the frozen snake_case columns / camelCase wire fields 1:1 (`NameVi` ↔ `name_vi` ↔ `nameVi`) — one name, not a second one.

```
CreditPackage(Id, Code, NameVi, NameEn, Credits, BonusCredits, PriceVnd, PriceUsd?, Active, SortOrder)
PromoCode(Id, Code, Kind: PromoKind, Value, ValidFrom, ValidTo, MaxRedemptions, PerUserLimit, MinPackageId?, Active)
PromoRedemption(Id, PromoCodeId, UserId, PaymentId?, CreditsGranted, RedeemedAt)
PromoKind ∈ { bonus_percent, fixed_credits }        // wire values, D14 verbatim
PromoValidation(PromoCodeId, Kind, BonusCredits, BonusPercent)   // BR32-frozen shape (DoR-1 resolved)
ReceiptModel(PaymentId, OrderCode, CreatedAt, PaidAt?, PackageName, Amount, Currency, Method, Status, Credits, BonusCredits, InvoiceInfo?, BusinessName, TaxCode, Address, UiLocale, PriceVersion)
```

**Invariants and guards (all inside the command transaction, DB-enforced where possible).**

1. `credit_packages.code` unique (citext-free, case-sensitive per 00 §6; lookups normalise `.Trim().ToLowerInvariant()` for `promo_codes` only — 00 §6 makes that column citext ✅).
2. `credits > 0`; `bonus_credits >= 0`; **BR13**: `bonus_credits <= credits` ⇒ else 400 `package_below_cost` is **not** used; the save rejection is a validation error on `bonusCredits` (`400` with the field-level `ValidationProblemDetails`) — BR13 says "rejected at save".
3. `price_vnd` integer > 0; `price_usd` optional (`numeric(12,6)`); the floor guard is `priceVnd ÷ (credits + bonusCredits) ≥ MinimumPricePerCreditVnd(priceVersion, fxRate)` — that value is declared by 12.1.1/12.1.2 (**BR32**) — otherwise **400 `package_below_cost`** (D4, D16).
4. `promo_codes.value`: `bonus_percent` ⇒ integer `1..100`; `fixed_credits` ⇒ integer `1..100000`.
5. `valid_from < valid_to`; `max_redemptions >= 1`; `per_user_limit >= 1` and `<= max_redemptions`; `min_package_id` nullable FK.
6. `promo_redemptions` unique `(payment_id) WHERE payment_id IS NOT NULL` — the DB is the at-most-one-promo-per-payment guard (D14, F16 part b).
7. Payment-less (`fixed_credits`) redemptions: the guard is the `promo_codes` row lock + an in-transaction `COUNT(*)` over `promo_redemptions (promo_code_id, user_id)` (F20 index) — never a pre-transaction check (F16).
8. Lots created by this folder always name their source: `source = promo` with `payment_id` for `bonus_percent`, `source = promo` with `payment_id = NULL` for `fixed_credits`; expiry per code (`promo_codes.valid_to`, D6 ✅).
9. Ledger idempotency keys used here (verbatim, D2 ✅): `grant:promo:{promoRedemptionId}`; deactivated packages/promos never affect already-created `payments` rows (the snapshot lives on `payments`).
10. Soft delete: **none** — `active = false` is the only deactivation (keeps `payments.package_id`/`promo_code_id` referential and the audit trail intact).

**Indexes (F20 ✅, created in this folder's migrations where the table is owned here).** `promo_redemptions (promo_code_id, user_id)`; `promo_codes (active, valid_to)`; `credit_packages (active, sort_order)`; plus the existing `payments (order_code)` unique used by 12.7.4-T1's matching, `payments (status, created_at)`, `credit_lots (payment_id)`.

**Transactions and locks (BR2 ✅, D2 ✅).** Lock order globally `credit_accounts` → `promo_codes` → `payments` → `credit_lots`; advisory locks are transaction-scoped only (`pg_try_advisory_xact_lock(hashtext('nts:credit_mint:' || @actorId))` is 12.5.1-T1's; the `IMintingPolicy` call sites are 12.5.1-T1, 12.3.5-T1/-T2 and 12.7.2-T2 only — **BR32** — so this folder calls no policy and takes no lock). `promo/redeem` = one transaction: `SELECT … FROM credit_accounts WHERE user_id = @u FOR UPDATE` → `SELECT … FROM promo_codes WHERE code = @code FOR UPDATE` → re-count `per_user_limit` → insert `promo_redemptions(payment_id = NULL)` → `GrantAsync(source = promo)` → the ledger row carries `grant:promo:{promoRedemptionId}` (D2 ✅) → one `SaveChangesAsync` via `UnitOfWorkBehavior`.

## 4. Use cases

- **UC-1 (owner/admin, 12.7.1-T1)** — *Given* a new offer, *When* `POST /admin/credit-packages` with `CreditPackageInput`, *Then* the row is created, `audit_logs` (`category = 'billing'` ✅ C1) is written, `ETag "cp-<xmin>"` is returned; *When* the effective price per credit is below the current floor, *Then* 400 `package_below_cost` and nothing is written.
- **UC-2 (admin, 12.7.1-T1)** — *Given* a live catalogue, *When* `PATCH /admin/credit-packages/{id}` with `If-Match: "cp-<xmin>"`, *Then* only changed fields are written and a stale token ⇒ 412; *When* `active = false`, *Then* `GET /billing/packages` stops listing it and existing `payments` are untouched.
- **UC-3 (creator, 12.7.1-T1 + 10.8.x)** — *Given* an authenticated user (no flag), *When* `GET /billing/packages`, *Then* `{ items: CreditPackageDto[] }` with VND integers, sorted by `sortOrder`.
- **UC-4 (creator, 12.7.2-T1)** — *Given* a `fixed_credits` code, *When* `POST /billing/promo/redeem { code }` with `Idempotency-Key` and `billing_enabled` on, *Then* one `promo_redemptions` row (`payment_id = NULL`), a `source = promo` lot, the response `{ creditsGranted, balance }`; *When* the same key replays, *Then* the same response and no second lot; *When* the per-user limit is already reached, *Then* 409 `promo_already_used`.
- **UC-5 (creator, 12.7.2-T1 via 12.3.2-T1)** — *Given* a `bonus_percent` code at checkout, *When* `POST /billing/checkout` carries `promoCode`, *Then* `IPromoValidator.ValidateAsync` returns the preview and **no** redemption is written; *When* the payment later goes `paid` (12.3.3-T1), *Then* this task's `payment_paid.v1` handler writes exactly one `promo_redemptions` row with that `payment_id`, grants the `source = promo` lot bound to the payment, and takes `bonus:{paymentId}`-style protection through `grant:promo:{promoRedemptionId}` (a duplicate delivery is a no-op).
- **UC-6 (buyer, 12.7.3-T1)** — *Given* purchases, *When* `GET /billing/payments?cursor=&limit=`, *Then* newest-first `PaymentDto` rows with `receiptAvailable`; *When* `GET /billing/payments/{id}` for **own** payment, *Then* `PaymentDetailDto`; another user's id ⇒ 404 (tenant filter, never 403).
- **UC-7 (buyer, 12.7.3-T1)** — *Given* a paid payment, *When* `GET /billing/payments/{id}/receipt`, *Then* `text/html; charset=utf-8`, `Content-Disposition: inline`, rendered in the user's `ui_locale` with VND/USD + locale dates, print-styled, business details from settings, and only the buyer's own data.
- **UC-8 (Vietnamese buyer, 12.7.4-T1)** — *Given* `Capabilities.BankQr` true, *When* `POST /billing/checkout` with `method = bank_qr`, *Then* the response carries `bankQr { qrPayload, bankName, accountNumber, accountName, amount, orderCode }` with the exact amount and the unique `order_code` in the transfer note, `expiresAt` = checkout + `Payment:CheckoutExpiryMinutes` (30 ✅).
- **UC-9 (platform, 12.7.4-T1)** — *Given* a bank transfer, *When* the provider webhook arrives with the order code's transfer, *Then* 12.3.3-T1 grants; *When* the amount is short/long, *Then* `needs_review` + alert and the admin resolves through 12.3.5-T1's queue (`GET /admin/payments?status=needs_review`); *When* no webhook arrives before `expiresAt`, *Then* `PaymentStatusSyncJob` (12.3.3-T2) applies the same status API path and the pending expiry (`payment_expired` 409 ✅ CR-7).
- **UC-10 (platform, 12.7.4-T1)** — *Given* no configured provider reports `Capabilities.BankQr` ⚠️, *Then* the story is dropped and the QR step is absent from 10.8.4 — **never** replaced by manual bank-statement matching (plan §12.7.4 ✅); *Given* a provider without `BankQr`, *Then* `method = bank_qr` answers **400 `payment_method_unavailable`** — fail-closed, never a silent card fallback (**BR32**, CR-7).

## 5. API and events

All routes are copied verbatim from README §3.1 ✅; bearer auth is the default (09), 403/404 follow 09's ProblemDetails shape (C4 ✅). `Idempotency-Key` is **required** on `POST /billing/promo/redeem` (00 §8 ✅).

| Route | Verb | Authz | Flag | Request / response (verbatim) | Codes |
|---|---|---|---|---|---|
| `/billing/packages` | GET | authenticated | **never** (BR17) | `{ items: CreditPackageDto[] }`; `CreditPackageDto { id, code, nameVi, nameEn, credits, bonusCredits, priceVnd, priceUsd \| null, sortOrder }` ✅ | 200 |
| `/admin/credit-packages` (+ `/{id}`) | GET/POST/PATCH | admin policy `admin.billing.manage` | n/a | `CreditPackageInput { code, nameVi, nameEn, credits, bonusCredits, priceVnd, priceUsd?, active, sortOrder }` ✅; PATCH needs `If-Match` (`"cp-<xmin>"`) | 400 `package_below_cost`, 412, 404 |
| `/billing/promo/redeem` | POST | authenticated | **gated** (BR17) | body `{ code }` ⇒ `{ creditsGranted, balance }` ✅ + `Idempotency-Key` | 400 `promo_invalid` \| `promo_expired`, 409 `promo_exhausted` \| `promo_already_used` |
| `/admin/promo-codes` (+ `/{id}`) | GET/POST/PATCH | read: admin · write: owner (12.7.2-T2 owns the guard, §11 DoR-3) | n/a | `PromoCodeInput { code, kind, value, validFrom, validTo, maxRedemptions, perUserLimit, minPackageId?, active }` ✅; PATCH `If-Match` (`"pc-<xmin>"`) | 403 `forbidden` (admin create/patch ✅), 409 `grant_in_progress` (policy, D13) |
| `/billing/payments` | GET | authenticated (own) | never | `{ items: PaymentDto[], nextCursor }`; `PaymentDto { id, createdAt, packageCode, amount, currency, method, status, credits, bonusCredits, receiptAvailable }` ✅; `?cursor=&limit=` (max 100) | 200 |
| `/billing/payments/{id}` | GET | authenticated (own) | never | `PaymentDetailDto` = `PaymentDto` + `{ orderCode, providerPaymentId \| null, invoiceInfo \| null, refundRequest: { id, credits, amount, status } \| null, refundEligible, refundableCredits }` ✅ | 200, 404 |
| `/billing/payments/{id}/receipt` | GET | authenticated (own) | never | `text/html; charset=utf-8`, `Content-Disposition: inline` ✅ | 200, 404 |
| `/billing/checkout` | POST | authenticated | **gated** (order creation) | `method = bank_qr` ⇒ `CheckoutSessionDto.bankQr { qrPayload, bankName, accountNumber, accountName, amount, orderCode }` ✅ (the enum wire values of `method` are 00 §6 `payments.method`: `card`/`wallet`/`bank_qr` ✅) | 400/409/502 as `12.3.2-T1`; 400 `payment_method_unavailable` when `method = bank_qr` and the active provider's `Capabilities.BankQr` is false — **fail-closed**, never a silent card fallback (**BR32**, CR-7) |

`refundRequest` in `PaymentDetailDto` is read from `refund_requests` (`:302` ✅) — the **writer** is 12.7.5-T1; this task only projects it (`null` when absent). `refundEligible`/`refundableCredits` are **computed by 12.7.5-T1** (`IRefundEligibility`) and **rendered** by 12.7.3-T1 through the new soft edge `12.7.3-T1 Depends 12.7.5-T1 (soft)` (**BR32**). `GET /billing/packages` is owned by **12.7.1-T1** and `12.3.2-T1` does not mount it (**BR32**); `POST /admin/refund-requests/{id}/confirm` (body `{ bankReference }`, CR-16) is **12.7.5-T1**'s route — this folder only links to it (**BR28**).

**Events.** Outbox `payment_paid.v1` (declared by 12.3.3-T1; this folder subscribes): `12.7.2-T1` subscribes with `[OutboxEvent("payment_paid.v1")]` and applies the promo side effect (`promo_redemptions` row + `source = promo` lot) and nothing else — the `purchase`/`bonus` rows are 12.3.3-T1's (BR6 ✅). Realtime `CreditBalanceChanged { userId }` (ids only, emitted by the ledger, consumed by 10.8.x ✅ C14). No new event name is introduced by this folder.

**ProblemDetails** — `type` = the error code, `title` from `billing.errors.*`, `status` per the table; `package_below_cost` 400 carries `extensions: { minimumPricePerCreditVnd, packagePricePerCreditVnd }` (renamed from `floorCredits`/`packageCreditsPerUsd` to match the BR32 VND-only floor formula); `promo_*` 400/409 carry `extensions: { code: <the submitted code> }` (never the row id).

## 6. UI

**Owner rule (2026-09-25, `plans/features/README.md` §8.1; BR22 ✅).** Every web/UI feature needs an approved design first. This folder is **backend only** (BR22 ✅ — 12 has **no** `-T0` task anywhere): no UI file, no component, no Angular route. The billing UI is `10.8.x` (12 CR-9 ✅) — `10.8.1` Buy credits and ledger, `10.8.2` Account settings, `10.8.3` Wallet, payments and refunds, `10.8.4` Bank QR and paused-processing states (`plans/10-web-ui.md:200-221` ✅) — and its gates are **`10.8.1-T0`** (user billing screens, 10 CR-24 ✅) **and `10.9.1-T0`** (admin screens — needed by `10.9.2-T1`, the admin billing/promo consumer of `12.7.1-T1`/`12.7.2-T1`; §7 `12-ui-gate`, README:306 ✅); no `10.8.x`/`10.9.x` implementation task may start before its own gate is owner-approved. Each task file below names its consumers; that is the whole UI contract this folder owns.

| Task | Consumed by (10 CR-26, `features/10-web-ui/README.md:697` ✅) | UI contract fixed by this folder |
|---|---|---|
| 12.7.1-T1 | `10.8.1-T1` (buy page: packages, credits, bonus, VND price, price per credit) + `10.9.2-T1` (admin billing screen) | `CreditPackageDto` shape; VND integers; `package_below_cost` copy key |
| 12.7.2-T1 | `10.8.1-T1` (optional promo field at checkout) + `10.9.2-T1` (promo list/editor) ; the redeem body/response is also consumed by the wallet surface `10.8.3-T1` when a fixed-credit code is redeemed | redeem body/response; the four `promo_*` codes mapped to `billing.promo.*` |
| 12.7.3-T1 | `10.8.3-T1` (wallet: payment history, receipt download, "Request refund" when eligible) | list/detail shapes; receipt opens in a new tab (`inline`); print CSS `@media print` |
| 12.7.4-T1 | `10.8.4-T1` (QR step: exact amount, order code to keep in the note, countdown, live status, `needs_review` copy) | `bankQr` fields; the page polls `GET /billing/payments/{id}` until a terminal status or `expiresAt` ✅ |

**10 CR-26 back-edges.** CR-26 asks this folder's task headers to name `10.8.x` ids in `Blocks` (`12.7.1-T1`/`12.7.2-T1` → `10.9.2-T1`, `12.7.3-T1` → `10.8.3-T1`, `12.7.4-T1` → `10.8.4-T1`). The canonical §8.1 rows say `10.8.x (12 CR-9)` and `—` and are copied verbatim (§8.1 line 319 ✅); the mismatch is recorded as **DoR-10**, not patched into a canonical cell.

i18n (D18 ✅): `billing.packages.*`, `billing.promo.*`, `billing.history.*`, `billing.payments.*`, `billing.receipt.*`, `billing.errors.*`, `admin.billing.*` in `i18n/vi.json` + `i18n/en.json` with English fallback; the receipt's own strings are server-side in `billing.receipt.*`. Accessibility per 10.1.3 (focus order, labels, no colour-only status ✅); money screens meet the D18 rule in both locales.

## 7. Jobs and integrations

- **No new job.** D7 ✅ fixes all 12 jobs; this folder adds **none** and registers no queue (BR9 ✅). `12.7.4-T1` relies on `PaymentStatusSyncJob` (every 10 min, retries 3, ids-only, `[JobPolicy(Queue = JobQueue.Default)]`, registered independently of `billing_enabled` ✅ BR17) and `PaymentAnomalyJob` for the QR path; both are owned by `12.3.3-T2`/D7. Bank-QR pending rows are expired by that job (`payments.expires_at` = checkout + 30 min ✅ BR11, `payment_expired` 409 ✅ CR-7).
- **Integrations.** (a) Provider `CreateCheckoutAsync(CheckoutRequest(..., ProviderMethod.BankQr, ...))` returning `CheckoutSession.CheckoutUrl` **or** `BankQr` ✅ D8 — the *only* provider call this folder makes; the port and adapter are 12.3.2-T1's, the capability flag is 12.3.1-T1's. (b) `FakePaymentProvider` in `NightTales.TestKit` drives all tests (hand-written, no mocking library ✅ 12.3.2-T1). (c) Bank apps scan `BankQrDetails.QrPayload` — no server integration. (d) E-mail: `IEmailSender` templates; the receipt **renderer** is this folder's, and the `payment_receipt` template send is **12.7.3-T1**'s through 09.2.6-T3 (**BR32**, DoR-5 resolved). (e) `[OutboxEvent]` `payment_paid.v1` subscribe (above). (f) `IFxRateProvider.CurrentAsync()` for `package_below_cost` (D16/BR7 ✅).

## 8. Security and privacy

- **Tenant isolation.** `credit_packages`/`promo_codes` are **global** admin data (not `IUserOwned`); `promo_redemptions` and `payments` reads are `IUserOwned` (09.2.3) so a foreign id is a **404**, never a 403. Any webhook/sync path is 12.3.3-T1's `ISystemContext.For(userId)` after `IPaymentLookupQuery` (F10 ✅); this folder's `payment_paid.v1` handler runs under the same per-user scope and re-reads the payment by id inside it.
- **Authorization.** Admin routes: `admin.billing.manage` policy (09.3.x). Promo create/patch is **owner-only** (`403 forbidden`, `active = false` allowed for admins ✅) — the guard + exposure arithmetic are `12.7.2-T2`'s (**BR32**; DoR-3 resolved), and `12.7.1-T1`'s package save path enforces `package_below_cost` + `bonus_credits <= credits` (BR13 ✅).
- **Audit.** Every package/promo write and every redemption writes `audit_logs` with `category = 'billing'` ✅ (C1), `actor`, target id, before/after diff of the changed columns only — never a whole row dump.
- **Secrets.** No new secret. Provider credentials stay in `Payment:Providers` (**12.3.2-T1/D10**); anything this folder must encrypt uses Data Protection with an explicit purpose string (e.g. `nts.billing.receipt`) — never `Credit:*` config into logs.
- **Input/OUPUT hygiene.** (a) Receipt HTML: **every** dynamic value is HTML-encoded, notably `payments.invoice_info` (`companyName`, `taxCode`, `address` — user-supplied) — this is the one XSS-relevant sink in the folder; the route also sets `X-Content-Type-Options: nosniff` and a restrictive `Content-Security-Policy` (`default-src 'none'; style-src 'unsafe-inline'; img-src data:`), plus `Cache-Control: private, no-store`. (b) `promo_codes.code` is citext and normalised; a failed lookup never echoes the row. (c) `Idempotency-Key` is stored as a hash, never the raw header. (d) Logs: ids only (no email, no QR payload, no invoice info), `billing_*` metrics carry no user labels except D13's `billing_credits_minted_total{actor}` ✅ F21.
- **Abuse.** Rate limits: `auth` policy on redeem/checkout ✅ (09); `per_user_limit` + `max_redemptions` under row locks (F16 ✅); the QR path cannot be self-approved (12.3.5-T1) and a package with bonus above `credits` is refused (BR13 ✅).
- **Privacy.** A receipt exposes only the buyer's own data (D18 ✅). `promo_redemptions` holds user ids only. Retention/soft-delete of money rows follows the retention rule (C2 ✅) — never hard-delete.

## 9. Test plan

Projects: `NightTales.Domain.UnitTests` (pure guards), `NightTales.Application.UnitTests` (handlers with `NightTales.TestKit` fakes — **no mocking library** ✅), `NightTales.Api.IntegrationTests` (Testcontainers PostgreSQL 17 + `FakeClock`), `NightTales.Architecture.Tests`. The names below are the exact ones in the four task files (`## Tests`); a rename must be applied in both places.

- **12.7.1-T1 — `NightTales.Domain.UnitTests`.** `CreditPackage_RejectsBonusAboveCredits`, `CreditPackage_RejectsZeroCredits`, `CreditPackage_RejectsInvalidCodeShape`.
- **12.7.1-T1 — `NightTales.Application.UnitTests`.** `UpsertPackage_BelowFloor_ReturnsPackageBelowCost`, `UpsertPackage_ExactlyAtFloor_Succeeds`, `UpsertPackage_BonusCreditsCountTowardFloor`, `UpsertPackage_PatchWithStaleRowVersion_Returns412`, `UpsertPackage_WritesAuditLogWithChangedColumnsOnly`, `GetPackages_ReturnsOnlyActive_SortedBySortOrder`, `GetPackages_FlagOff_StillSucceeds`.
- **12.7.1-T1 — `NightTales.Api.IntegrationTests`.** `AdminCreditPackages_PostThenPatch_RoundTripsETag`, `CreditPackages_CodeUnique_ConflictIsProblemDetails`, `PackageSave_BelowFloor_LeavesNoRow`.
- **12.7.2-T1 — `NightTales.Domain.UnitTests`.** `PromoCode_RejectsPercentAbove100`, `PromoCode_RejectsPerUserLimitAboveMaxRedemptions`, `PromoCode_RejectsValidFromAfterValidTo`, `PromoCode_RejectsInvalidKind`.
- **12.7.2-T1 — `NightTales.Application.UnitTests`.** `ValidatePromo_Precedence_InvalidExpiredExhaustedAlreadyUsed`, `ValidatePromo_InactiveCode_IsInvalid`, `ValidatePromo_MinPackageIdMismatch_IsInvalid`, `Redeem_KindBonusPercent_IsRejectedOnTheRedeemRoute`, `Redeem_ExhaustedAndAlreadyUsed_ReturnsExhausted`, `Redeem_WritesOneRedemptionWithNullPaymentId_AndOnePromoLot`, `Redeem_SameKeySameBody_ReturnsStoredResponse`, `Redeem_SameKeyDifferentBody_409`, `Redeem_EmitsCreditBalanceChanged_IdsOnly`, `PaymentPaid_Handler_GrantsBonusPercentRoundedDown`, `PaymentPaid_Handler_FixedCreditsCodeAtCheckout_GrantsValue`, `PaymentPaid_Handler_DuplicateDelivery_NoSecondLot`, `PaymentPaid_Handler_SecondPromoForSamePayment_IsNoOp`, `PaymentPaid_Handler_PaymentNotPaid_DoesNothing`, `UpsertPromoCode_PatchStaleETag_Returns412`, `UpsertPromoCode_NoLedgerMovement_NoCapArithmetic`.
- **12.7.2-T1 — `NightTales.Api.IntegrationTests`.** `PromoRedeem_ConcurrentSameUser_OneRowOnly`, `PromoRedeem_ConcurrentSameCode_RespectsMaxRedemptions`, `PromoRedemption_PartialUniqueOnPaymentId_IsEnforced`, `Redeem_DbFailure_RollsBackBothRedemptionAndLedger`, `PromoRoutes_AdminCannotCreateOrPatch_403Forbidden`.
- **12.7.3-T1 — `NightTales.Application.UnitTests`.** `GetPayments_NewestFirst_CursorStableUnderInsert`, `GetPayments_LimitClampedTo100`, `GetPayments_ExcludesOtherUsersRows`, `GetPaymentDetail_ProjectsLatestRefundRequest_NullWhenAbsent`, `GetPaymentDetail_RefundEligibleWindowBoundary`, `GetPaymentDetail_RefundableCredits_UsesRefundArithmetic`, `ReceiptAvailable_TrueOnlyAfterMoneyTaken`, `Receipt_EscapesInvoiceInfo`, `Receipt_UsesViAndEnFormats`, `Receipt_IncludesBusinessDetailsAndLegalFooter`, `Receipt_HasPrintMediaStyleBlock`, `ReceiptEmail_SentOnceThroughIEmailSender`.
- **12.7.3-T1 — `NightTales.Api.IntegrationTests`.** `Payments_CursorPaging_NoDuplicatesNoGaps`, `PaymentRoutes_CrossTenant_404`, `Receipt_ReturnsInlineHtmlWithNosniffAndCsp`, `PaymentDetail_ETag_IfNoneMatch_304`, `Receipt_NoForeignUserPii`.
- **12.7.4-T1 — `NightTales.Application.UnitTests`.** `BankQrCheckout_ReturnsQrDetailsMatchingRowAmountAndOrderCode`, `BankQrCheckout_CapabilityOff_ReturnsPaymentMethodUnavailable`, `BankQrCheckout_ProviderAmountMismatch_ReturnsPaymentProviderError`, `BankQrCheckout_ProviderOrderCodeMismatch_ReturnsPaymentProviderError`, `BankQrCheckout_NeitherField_ReturnsPaymentProviderError`, `BankQrCheckout_ExpiresAt_IsRowExpiresAt_ShorterProviderWindowAdopted`, `BankQrCheckout_FlagOff_NoOrderCreated`, `BankQrCheckout_UnsupportedProvider_StoryDropped_NoStatementMatching`, `BankQrCheckout_QrPayload_IsNeverLogged`.
- **12.7.4-T1 — `NightTales.Api.IntegrationTests`.** `Checkout_BankQr_ThenWebhookPaid_GrantsOnce`, `Checkout_BankQr_Underpaid_LeavesNeedsReview_NoGrant`, `Checkout_BankQr_PendingRowExpires_After30Minutes`, `NeedsReviewQueue_AdminPaymentDtoShape_CoversQrRows`, `Checkout_BankQr_DoesNotLogQrPayloadOrTransferNote`.
- **`NightTales.Architecture.Tests` (all four tasks).** `BankQrTask_DeclaresNoMatcherNoClassifierNoGrantPath`; exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command ✅ (09.3.6-T1); no session-scoped advisory lock in 12 ✅ (BR2); `12.7.2-T1`'s handler reachable only through the outbox (`payment_paid.v1`) or `IPromoValidator` ✅ (BR6); the three read/receipt routes never behind `billing_enabled` ✅ (BR17); no new job/queue registered (BR9 ✅); the checkout response has no `redirectUrl` member ✅ (12 CR-9); `IReceiptRenderer` has no PDF/native dependency ✅ (F19/BR12).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.7.1-T1 | S (0.5) | BE | S · M2 | 12.1.2-T1, 12.1.1-T1 (soft) | 12.3.2-T1, 12.7.2-T1, 10.9.2-T1 (10/08 CR-hand-off) |
| 12.7.2-T1 | M (1.0) | BE | S · M2 | 12.7.1-T1, 12.6.1-T1, 12.3.2-T1 (soft) | 12.7.2-T2, 10.9.2-T1 (10/08 CR-hand-off) |
| 12.7.3-T1 | S (0.5) | BE | S · M2 | 12.3.2-T1, 12.3.3-T1, 12.7.5-T1 (soft) | 10.8.3-T1 (10/08 CR-hand-off) |
| 12.7.4-T1 | M (1.0) | BE | S · M2 · flag `billing_enabled` | 12.3.1-T1, 12.3.2-T1, 12.3.3-T1 | 10.8.1-T1, 10.8.4-T1 (10/08 CR-hand-off) |

Sum = **3.0 d raw** for this folder; epic 12.7 = 4.0 d ✅ (README §9, "unchanged"): `12.7.2-T2` (0.5) is `../12.5-admin-minting/`'s and `12.7.5-T1` (0.5) is `../12.3.5-review-refunds/`'s. Task files: `12.7.1-T1-package-catalog.md`, `12.7.2-T1-promo-codes.md`, `12.7.3-T1-payment-history-and-receipt.md`, `12.7.4-T1-bank-qr.md`.

Critical path: `12.1.2-T1` → `12.7.1-T1` → `12.3.2-T1` → {`12.7.2-T1`, `12.7.3-T1`, `12.7.4-T1`} → `12.7.2-T2`.

Sequencing notes (README §5 diagram ✅): `PK1["12.7.1-T1 packages"] --> CO["12.3.2-T1 IPaymentProvider + checkout"]`, `PK1 --> PR1["12.7.2-T1 promo codes"] --> PR2`, `PK1 --> RE["12.7.3-T1 payments + receipt"]`, `CO --> QR["12.7.4-T1 bank QR"]`. So `12.7.1-T1` is the earliest task in the folder (only `12.1.2-T1` hard) and `12.7.4-T1` is last (12.3.1-T1's provider choice ⚠️ external lead time gates it, BR16 ✅).

## 11. Rollout

Order of landing: `12.7.1-T1` (migrations for `credit_packages` + the `GET /billing/packages` read model; seed three offers Starter/Creator/Studio ⚠️ prices pending provider) → `12.3.2-T1` mounts checkout → `12.7.2-T1` (promo tables + validator impl + redeem route + the subscribed handler) → `12.7.3-T1` (history/detail/receipt — pure read, no migration) → `12.7.4-T1` (bank QR; only if `Capabilities.BankQr` ✅, else the story is dropped per plan §12.7.4).

Flags and switches: `billing_enabled` = **false** at first deploy ✅ O-7; order creation (checkout, `POST /billing/promo/redeem`, buy UI) stays off, while `GET /billing/packages`, `GET /billing/payments*`, the receipt and the payment jobs stay on ✅ BR17. The QR method is additionally gated by 12.3.1-T1's capability flag.

Data/migration notes: additive only (new tables `credit_packages`, `promo_codes`, `promo_redemptions` in S, never in P ✅ C2/09 D9; the F20 indexes; no back-fill). **Migration ownership (BR20 ✅ — one text per table, written by the named task only):** `_12_CreditPackages` → **12.7.1-T1**, `_12_Promo` → **12.7.2-T1**, `_12_RefundRequests` → **12.7.5-T1** (`../12.3.5-review-refunds/`); **nothing in this folder creates `credit_grant_requests`** — `_12_CreditGrantRequests` is 12.5.1-T1's alone. Rollback: disable `billing_enabled`… — order creation is already off; the receipt and reads stay available; a bad catalogue row is fixed by `PATCH active = false` (never a delete, §3 invariant 10), so no down-migration is needed.

**Open items carried to DoR (not silent gaps):**

- **DoR-1 — resolved (BR32).** `IPromoValidator` is frozen as `ValidateAsync(string code, Guid userId, Guid packageId, long amountMinorUnits, ct) → ErrorOr<PromoValidation(Guid PromoCodeId, PromoKind Kind, long BonusCredits, long BonusPercent)>`; **declared by `12.3.2-T1`, implemented by `12.7.2-T1`** (README §10 BR32). The earlier `ValidateAsync(code, platform, packageId, userId, ct)` / `…Result` naming is superseded and no local synonym remains in this folder.
- **DoR-2 — resolved (BR32/CR-7).** Unsupported `bank_qr` (the active provider's `Capabilities.BankQr` false) ⇒ **400 `payment_method_unavailable`**, fail-closed; 12 CR-7 adds the code to 09 §5.4. `12.7.4-T1` implements that branch and no silent card fallback.
- **DoR-3 — resolved (BR32).** The owner-only guard, the exposure arithmetic and the ≥-cap request flow stay `12.7.2-T2`'s (`../12.5-admin-minting/`); the promo exposure sum lives inside `MintingPolicy` and the `IMintingPolicy` call sites are 12.5.1-T1, 12.3.5-T1/-T2 and 12.7.2-T2 only — `12.7.2-T1` ships routes + validation + redemption with **no** policy call and **no** lock.
- **DoR-4 — resolved (BR32).** `GET /billing/packages` belongs to **`12.7.1-T1`**; `12.3.2-T1` drops its "surface" claim (that folder's task applies it).
- **DoR-5 — resolved (BR32).** The `payment_receipt` e-mail is sent by **`12.7.3-T1`** through **09.2.6-T3**; this folder renders the receipt on demand and sends the template once per paid payment.
- **DoR-6 — [OPEN] (external, reason: provider facts unavailable).** Bank-QR availability, QR standard and transfer-note limits stay unverified until `12.3.1-T1` reads the chosen provider's official pages (BR16 ✅); if no provider supports it, `12.7.4-T1`'s QR step is dropped, not replaced.
- **DoR-7 — resolved (BR32).** `refundEligible`/`refundableCredits` are computed by **`12.7.5-T1`** (`IRefundEligibility`) and rendered here through the **new soft edge `12.7.3-T1` Depends `12.7.5-T1` (soft)** — applied to the task header and to §10 above, matching README §8.1 line 349.
- **DoR-8 — resolved (BR32).** `bankQr` is **never** persisted in `payments.raw`; it is derived from `order_code` + the provider account config and is never logged.
- **DoR-9 — resolved (BR32).** The floor formula is `priceVnd ÷ (credits + bonusCredits) ≥ MinimumPricePerCreditVnd(priceVersion, fxRate)`, that value declared by 12.1.1/12.1.2 — applied in §3 invariant 3 and in `12.7.1-T1`'s Design.
- **DoR-10 — [OPEN] (owner/TL, reason: canonical row is frozen).** 10 CR-26 (`features/10-web-ui/README.md:697` ✅) asks for `10.8.x` ids in this folder's `Blocks` cells while README §8.1 rows are canonical and copied verbatim (`10.8.x (12 CR-9)` / `—`); the ids are recorded in §6 and the canonical rows are not patched here.
- **DoR-11 — resolved (BR32).** `Capabilities.BankQr == false` ⇒ **400 `payment_method_unavailable`**, fail-closed, never a silent card fallback; `12.3.2-T1`'s acceptance-4 page fallback is superseded and is that folder's to change.
- **DoR-12 — resolved (BR32).** The duplicate matcher is **withdrawn**: `12.7.4-T1` declares no `OrderCodeMatcher` and no `BankQrSettlementClassifier`; its scope is the QR-session (capability wiring, the five `BankQrDetails` → `CheckoutSessionDto` invariants, expiry/countdown) plus the `needs_review` handoff QA, with the arch test guarding the withdrawal.
- **DoR-13 — resolved (BR32).** `12.7.2-T1` performs **no** cap arithmetic, takes **no** advisory lock and has **no** `IMintingPolicy` call site (call sites: 12.5.1-T1, 12.3.5-T1/-T2, 12.7.2-T2).
- **DoR-14 — resolved (BR32).** `12.7.4-T1` **stays `M (1.0)`** with QR-session + `needs_review` scope only (the duplicate matcher withdrawn); README §9's "unchanged" for epic 12.7 and this folder's 3.0 d arithmetic hold — no re-scope.
