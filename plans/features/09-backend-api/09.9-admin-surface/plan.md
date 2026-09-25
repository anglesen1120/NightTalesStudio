# 09.9 / 09.10.5 / 09.10.6 / 09.10.7 — Admin surface

> Source of truth: `../README.md` Part C (§C0 folder brief 2, §C1 facts W3-6…W3-16, §C3 D5/D13/D14/D15/D16, §C3.1 routes, §C8.1 canonical rows, §C10 W3-O4/W3-TL1/W3-TL6). Extends Part A (D1–D17, packages, pipeline, outbox, ETags, tenant filter) and Part B (§B3, esp. BD13 "Admin endpoints stay S", BD15 audit, `IReauthGuard`). Never contradicts either.

## 1. Scope

This folder writes the **operator surface**: the one `/api/v1/admin` route group, its policy split, cursor pagination, audit wiring and the platform ops that are *not* billing (09.9.1-T1); the feature-flag write side (09.9.2-T1); admin user management (09.10.5-T1); the `registration` settings row (09.10.6-T1); and the `nts-admin` break-glass CLI (09.10.7-T1). Five tasks, 5.0 raw days (L 2.0 + S 0.5 + M 1.0 + S 0.5 + M 1.0), matching `../README.md` §C8 folder index.

**In scope:** `/admin/*` group + `AdminPolicy`/`OwnerPolicy`, `AdminPageDto<T>` cursor pagination, admin audit attributes, the tenant-bypass allow-list entry, render-job admin ops (retry/cancel), usage/moderation/quota/reports/deletion-requests reads, `IFeatureFlagWriter` + `PATCH /admin/feature-flags/{key}`, `/admin/users` list+detail+actions, `registration.*` settings rows behind `IRegistrationPolicyProvider`, `nts-admin` CLI verbs (`owner recover|restore|resolve`).

**Out of scope (D13, D20):** billing admin routes (`/admin/users/{id}/credits`, `/admin/credit-packages*`, `/admin/promo-codes*`, `/admin/payments*`, `/admin/refund-requests*`, `/admin/funding*`, `/admin/credit-grant-requests*`) — plan 12's; `GET /admin/kpis` handler body — 08.1's (registered on this group only); `system_settings` store/secrets/critical-change machinery — `09.10-settings/`; owner governance requests — `09.10.4-owner-governance/`; no impersonation; no second `/admin` group; no second feature-flag store.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| W3-6 | `registration` is an existing settings-catalog integration: non-secret `mode`, `disposable_domains`, `trial.credits`; **no `captcha` integration** exists in §5.6 | `09-backend-api.md`:478–480 ✅ |
| W3-9 | Columns this wave needs already exist: `users.security_stamp/role/status`, `refresh_tokens`, `system_settings(+versions,+changes)`, `owner_action_requests`, `feature_flags`, `worker_nodes`, `data_deletion_requests`, `reports`, `moderation_events`, `usage_records`, `render_jobs` | `00-overview.md` §6 ✅ |
| W3-11 | No `nts-admin` (or one-shot job image) reference exists in plan 11 → the 09.10.7-T1 artefact has no owner in plan 11 yet | grep 2026-09-25 ✅ `[OPEN]`, W3 CR-6 |
| W3-12 | Task-file header format: H1 `# <id> — <title>` + one row `| Story | Size | Area | Depends on | Blocks | Phase / flag |`, then the 9 headings | `09.3.1-T3`:1–6 ✅ |
| F03 | 09.9.1-T1 and 09.10.5-T1 both claimed the admin user list before D13 split them | README §C2 |
| F04 | 00 §8 Admin row mixed platform and billing ops before W3 CR-5 split it | README §C2, `12` §8.1 |
| F05 | `09.9-platform/plan.md`:174 names the flag-write path `09.9.1-T1`; corrected to `09.9.2-T1` by W3 CR-11 | README §C2/§C3.7 |
| F15 | Five missing indexes (admin user keyset, sessions, trusted devices, two apply-job scans) — only the `users` keyset index applies to this folder | README §C2, W3 CR-1 |
| F16 | `nts-admin` has no plan-11 owner; W3 CR-6 proposes `11.6.4-T1` `[OPEN]` | README §C2 |

## 3. Domain and data

No new aggregate. This folder reads/writes existing tables only, per §C3.2:

- `users` — gains index `(status, role, created_at DESC, id)` (W3 CR-1, owned by `09.3.3-T2`'s migration per D10's table but consumed here for `/admin/users` keyset paging).
- `feature_flags` (`key`, `enabled`, `rules jsonb`) — 09.9.2-T1 adds the write path over the existing table (09.9.2-T2 owns the read path and the table's migration, `09.2.2-T2`).
- `system_settings`/`_versions` rows of the existing integration `registration` (W3-6) — 09.10.6-T1 adds three keys (`registration.mode`, `registration.disposable_domains`, `trial.credits`); no new table.
- `render_jobs`, `usage_records`, `moderation_events`, `platform_quota_usage`, `reports`, `data_deletion_requests`, `owner_action_requests` (read-only here) — read-only admin projections, `IgnoreQueryFilters` under `Application/Features/Admin/**` only (D5).
- `audit_logs` — every mutation in this folder appends a row via `IAuditWriter` (BD15), `category = support`, `visibility = internal`, except `nts-admin`'s `category = account_security` (D16).
- No new table, no new migration file owned by this folder.

## 4. Use cases

| # | Actor | Flow |
|---|---|---|
| UC1 | Admin | Lists render jobs, retries a `failed` one, cancels a `submitted` one — 09.9.1-T1 |
| UC2 | Admin | Reviews usage/moderation/quota/reports, resolves a report, views (read-only) a deletion request — 09.9.1-T1 |
| UC3 | Non-admin caller | Hits any `/admin/*` route → 404 `not_found`, never 403 — 09.9.1-T1 (D5) |
| UC4 | Admin/Owner | Flips a feature flag with `If-Match`, gets 412 `feature_flag_conflict` on a stale ETag (09 W3 CR-31), 404 on an unknown key — 09.9.2-T1 |
| UC5 | Admin | Lists/searches users (keyset), opens a detail bundle (balance via 12's `GetBalanceQuery`, sessions, channels, payments read-only) — 09.10.5-T1 |
| UC6 | Admin/Owner | Suspends a user (revokes sessions, rotates stamp, cancels queued jobs with refund, pauses publications); `role`/`delete` require `OwnerPolicy` + re-auth and may time-lock (D8, via `09.10.4-T2`) — 09.10.5-T1 |
| UC7 | Owner | Edits `registration.mode`/`disposable_domains`/`trial.credits` through the settings API; `POST /auth/register` honours the new mode set — 09.10.6-T1 |
| UC8 | Operator (break-glass) | Runs `nts owner recover --email` when the platform is unreachable via the web, or `resolve`s a stuck request/change from a terminal — 09.10.7-T1 |

## 5. API and events

All routes copied verbatim from `../README.md` §C3.1; policies from D5.

| Route | Policy | Codes | Task |
|---|---|---|---|
| `GET /admin/render-jobs?status=&cursor=` | `AdminPolicy` | 200, 404 | 09.9.1-T1 |
| `POST /admin/render-jobs/{id}/retry` \| `/cancel` | `AdminPolicy` | 202/204, 404, 409 `episode_invalid_state` | 09.9.1-T1 |
| `GET /admin/usage?from=&to=&groupBy=user\|operation\|model`, `GET /admin/moderation?cursor=`, `GET /admin/quota`, `GET /admin/reports?status=&cursor=`, `PATCH /admin/reports/{id}`, `GET /admin/deletion-requests?status=&cursor=` | `AdminPolicy` | 200, 400 `validation_failed`, 404 | 09.9.1-T1 |
| `GET /admin/kpis?window=7\|30` | `AdminPolicy` | 200 | 08.1 (08 CR-8), registered on this group |
| `PATCH /admin/feature-flags/{key}` | `AdminPolicy` + `IReauthGuard` | 200, 400 `validation_failed`, 404 `not_found`, 412 `feature_flag_conflict` (09 W3 CR-31), 401 `reauth_required` | 09.9.2-T1 |
| `GET /admin/users?query=&status=&role=&verified=&hasPayments=&cursor=` | `AdminPolicy` | 200, 404 | 09.10.5-T1 |
| `GET /admin/users/{id}` | `AdminPolicy` + role-above-target (else 404) | 200, 404 | 09.10.5-T1 |
| `POST /admin/users/{id}/suspend`\|`/unsuspend`\|`/sessions/revoke`\|`/resend-verification`\|`/force-password-reset`\|`/mfa/reset`\|`/delete`, `PATCH /admin/users/{id}/role` | `AdminPolicy` (`mfa/reset`,`role`,`delete` → `OwnerPolicy`+`IReauthGuard`) | 200/202/204, 404, 401 `reauth_required`, 409 `last_owner`, 403 `grant_target_forbidden` | 09.10.5-T1 |

**No route** is added for `registration.*` (it is read/written through the shared `09.10.2-T1` settings API, `PUT /admin/settings/registration`) — 09.10.6-T1 supplies the `IRegistrationPolicyProvider` implementation and validator only. **No HTTP route** for 09.10.7-T1 (D16: CLI only).

Events: no new event type (D20). `feature_flags_changed.v1 { Key, Enabled, OccurredAt }` (09.9.2-T1, D14). `UserSecurityChanged` is raised by `ISecurityEventWriter` (09.3.2-T2) on every suspend/role-change path this folder triggers — 09.10.5-T1 calls it, never re-implements it.

## 6. UI

No UI work in this folder (backend-only, `Area: BE`/`Ops`). Consumers, per README §C3.7/W3 CR-14: plan 10's admin web app (`Lead10S`) consumes every route above and `PATCH /admin/feature-flags/{key}`; `10.7.1`/`10.7.2` (channels/publish, via 06) read `GET /admin/quota` for their own display. Per the 2026-09-25 owner rule, any admin-web UI task in plan 10 that renders these routes needs its own `<story>-T0 — UI design` approval before implementation; none of that design work is owned by this folder.

## 7. Jobs and integrations

No recurring job is owned by this folder (`AuthFailureSweepJob` = `09.3.3-T2`, `SettingChangeApplyJob`/`OwnerActionApplyJob` = `09.10.2-T2`/`09.10.4-T2`, `SettingSecretPurgeJob` = `09.10.1-T2`). `09.10.7-T1`'s CLI is a one-shot job image invoked manually, never a Hangfire recurring job (D16); its container is plan 11's, tracked `[OPEN]` via W3 CR-6 → `11.6.4-T1`. Integration: `09.10.5-T1` calls plan 12's `GetBalanceQuery { UserId }` (12.2.2-T1, same read port, no second query) for the credit balance shown in the user-detail bundle.

## 8. Security and privacy

- `AdminPolicy` = `RequireRole(Role.Admin, Role.Owner)` (an owner outranks admin and must pass the admin subset too, 09 W3 CR-33); `OwnerPolicy` = `RequireRole(Role.Owner)` + `IReauthGuard.RequireRecentAsync`. Any other role or anonymous caller → **404** `not_found`, never 403 (D5).
- `IgnoreQueryFilters` only under `Application/Features/Admin/**`; every new admin route is added to `09.2.3-T2`'s cross-tenant table, never to a separate bypass list (D5, F trap).
- Every mutation carries exactly one of `[Audited("<area>.<action>", Category.Support, Visibility.Internal)]` / `[NotAudited]` / `[AllowAnonymousRequest]` — the `09.3.6-T1` arch test is the gate; `09.10.7-T1`'s CLI commands use `Category.AccountSecurity` per D16.
- Admin reads reveal masked reporter e-mails only (`a***@domain`); the raw address is owner-only (D5, C5).
- Role hierarchy: actor role must be strictly higher than target's, else 404 (D8, consumed by `09.10.5-T1`'s `role`/`delete`/`mfa/reset`).
- `09.10.7-T1` never references `NightTales.Api`; operator identity comes from the platform job/exec audit log, never a CLI-supplied string (D16).
- No endpoint in this folder returns a secret value or raw `audit_logs.data`.

## 9. Test plan

- `NightTales.Domain.UnitTests`: none new (no new aggregate in this folder).
- `NightTales.Application.UnitTests`: `AdminListRenderJobsQueryHandlerTests`, `AdminRetryRenderJobCommandHandlerTests`, `SetFeatureFlagAdminCommandHandlerTests`, `AdminListUsersQueryHandlerTests`, `AdminSuspendUserCommandHandlerTests`, `RegistrationSettingsValidatorTests` — hand-written fakes from `NightTales.TestKit` (`FakeClock`, `InMemoryAuditWriter`, `FakeCreditBalanceReader`), no mocking library.
- `NightTales.Api.IntegrationTests` (Testcontainers Postgres): one 404-for-non-admin test per new route; `AdminUsers_Keyset_NoNPlusOne` (asserts query count); `FeatureFlag_StaleIfMatch_Returns409`; `AdminSuspend_RevokesSessions_RotatesStamp_CancelsQueuedJobs`.
- `NightTales.Architecture.Tests`: extends the existing "every `ICommand` has exactly one of `[Audited]/[NotAudited]/[AllowAnonymousRequest]`" test to cover this folder's new commands; a new test asserting every `/admin/*` route appears in `09.2.3-T2`'s cross-tenant table.
- `nts-admin` CLI: a `NightTales.Worker.Cli.Tests`-style project (or `NightTales.Application.UnitTests` for the command handlers, since the CLI has no route) exercising the three verbs against a Testcontainers Postgres with a seeded owner/dispute fixture; asserts exit codes 0/2/3 and the `audit_logs`/security-alert side effects.
- i18n: `error.*`/`activity.*` startup-fails-if-missing test (09.2.6-T3:94) covers the reused codes; no new code is introduced by this folder (`captcha_*`/`setting_test_failed` belong to other folders).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.9.1-T1 | L (2.0) | BE | S · M2 | 09.3.6-T1, 09.10.4-T1, 09.2.3-T2; 09.6.1-T1 soft (render-job ops) | 09.10.5-T1, 08.1.1-T1 soft (`/admin/kpis` handler), 12.8.3-T1 soft, 12.5.1-T1 soft, 10.9.1-T0 soft, 10.9.1-T1 soft, 10.9.1-T2 soft, 06.5.2-T1, 06.3.2-T1, 06.3.3-T1 |
| 09.9.2-T1 | S (0.5) | BE | S · M2 | 09.9.2-T2 | 06.5.2-T1 (W3 CR-15), 10.9.1-T1 (09 W3 CR-hand-off) |
| 09.10.5-T1 | M (1.0) | BE | S · M2 | 09.9.1-T1, 09.10.4-T2, 09.3.2-T2; 09.3.6-T2 soft, 09.3.7-T1 soft, 12.5.1-T2 soft, 12.2.2-T1 soft (`GetBalanceQuery`), 13.3.2-T1 soft | 10.9.4-T1 (09 W3 CR-hand-off), 08.3.1-T1 |
| 09.10.6-T1 | S (0.5) | BE | S · M2 | 09.10.2-T1, 09.3.1-T3; 09.3.1-T2 soft | 12.4.2-T1, 12.8.3-T1 (W3 CR-13) |
| 09.10.7-T1 | M (1.0) | Ops | S · M2 | 09.10.4-T3, 09.3.3-T1, 11.6.4-T1 `[OPEN]` (W3 CR-6) | 08.2.2-T1, 08.2.2-T2 (09 W3 CR-hand-off) |

Total: **5.0 raw days**, matching `../README.md` §C8's folder index row for `09.9-admin-surface/`. No re-estimate delta against the high level (`09-backend-api.md` §6): 09.9.1 L(2.0)+09.9.2 S(0.5)+09.10.5 M(1.0)+09.10.6 S(0.5)+09.10.7 M(1.0) = 5.0, unchanged by this wave's CRs (W3-O1).

⚠️ Note (not a change made by this folder — flagged for the TL): C8.1's 09.9.1-T1 dependency list includes `12.5.1-T2 soft (credit grant in the detail bundle)`, but D13 assigns the *user detail bundle* to **09.10.5-T1**, not 09.9.1-T1. The edge is copied verbatim from §C8.1 above; this folder does not resolve the apparent mismatch (see final report).

## 11. Rollout

Gated by `09-W3-admin` (README §C8 Gates): a non-admin gets 404 on any `/admin/*` route; an admin sees `/admin/users` (keyset) and gets 404 on a billing route; every mutation leaves an `audit_logs` row (`category = support`, `visibility = internal`); `/admin/render-jobs/{id}/retry` reuses the user command's domain method. Phase `S · M2` for all five tasks — no feature flag gates this folder (09.9.2-T1 is the flag mechanism's writer, not a consumer, per §C8 template note (e)). `nts-admin` (09.10.7-T1) ships only once `11.6.4-T1` lands its container image; until then the task is blocked `[OPEN]` per W3 CR-6 and the DoR cannot close. No M3/R2 item in this folder.
