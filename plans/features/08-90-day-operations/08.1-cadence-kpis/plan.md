# 08.1 — Cadence and KPIs (S slice)

> Part of plan 08 (S slice). Contract, decisions, findings and the canonical task table: `../README.md` Part B — §B0 (scope), §B1 (facts), §B3 (D6–D8, D13), §B4 (CR-6, CR-7, CR-11, CR-12, CR-13), §B8.1. High-level story text: `../../../08-90-day-operations.md` §4 (08.1.1, 08.1.2), §5.1.
> Phase: **S · M2** (SaaS beta; `../../../00-overview.md` §4.1).

## 1. Scope

Give the owner one surface for the weekly numbers and one fixed review ritual, so the review takes 30 minutes and decisions are made on data.

In: the KPI read model (`GET /admin/kpis?window=7|30`, `KpiSnapshotDto` with the 15 pinned keys of §B3 D7), the Angular admin page that renders it, and the weekly review template with the agenda and the 15-minute money routine.

Out: any new table (00 §6's preamble already grants `created_at` to every table); any per-user row or personal data in the snapshot; the margin formula (12.5.2-T1) and the provider-money view (`GET /admin/funding`, 12.8.1-T1) — both consumed, never re-implemented; the flagship channel sheet (P, `08.4.3-T1`) which stays a separate manual sheet.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| K17 | `platform_quota_usage(platform, quota_day, units_used, units_limit)` is the quota ledger the capacity KPI reads | `../../../00-overview.md:292` ✅ |
| K20 | The registered domain/DNS come from 11.2.1-T1 / 11.2.2-T3; the KPI page needs no domain | `../../../features/11-worker-deployment/README.md:253` ✅ |
| K21 | Admin/owner screens and `roleGuard` are S; the admin shell is `10.9.1-T1` and designs come from `10.9.1-T0`; the nav registry is `ADMIN_NAV_ITEMS` in `features/admin/nav/admin-nav.ts` | `10 README:3,146`; Lead10S 2026-09-25 ✅ |
| K22 | 00 §6's preamble grants `id`, `created_at`, `updated_at` to every table; `users` still has no verified-at timestamp and no global `usage_records` index exists | `../../../00-overview.md:262,266`; Lead12 2026-09-25 ✅ |
| K23 | The 09 S lead owns the `/api/v1/admin` group, `AdminPolicy` and the audited-request attributes | 09 S lead 2026-09-25 ✅ |
| K10 | 11.6.2-T1 owns the metric boards; the KPI page links to them and never replaces them | `11.6.2-T1:31-34` ✅ |

## 3. Domain and data

Read-only. Tables read: `users` (`created_at`, `email_verified_at`, `status`), `episodes` (`created_at`, `status`), `render_jobs` (`status`, `started_at`, `finished_at`, `error_code`), `usage_records` (`operation`, `cost_usd`, `created_at`), `platform_quota_usage`, `data_deletion_requests` (`due_at`, `completed_at`), `payments` (`status`, `amount`, `paid_at`) and, for margin, 12.5.2-T1's query. No write, no migration, no event.

`KpiSnapshotDto (string Window, DateTimeOffset From, DateTimeOffset To, DateTimeOffset GeneratedAt, KpiBlockDto Acquisition, Activation, Retention, Reliability, Cost, Capacity, Compliance, Support, Flagship)`; `KpiBlockDto (KpiMetricDto[] Metrics)`; `KpiMetricDto (string Key, decimal? Value, string Unit, decimal? Target, string Status, string Source, string? DocsRef, string? Link)`; `Status ∈ {ok, warn, breach, unknown}`; `Source ∈ {db, metric, manual}`.

Pinned definitions (F17): `first_video_within_7d_rate` counts users with a non-null `email_verified_at` whose first `render_jobs` row with `status = succeeded` has `finished_at ≤ email_verified_at + 7 days` (00 §6 has no download event — `[DECISION]`, download tracking is out of scope); `second_episode_within_30d_rate` counts activated users with a second `episodes` row within 30 days of the first. **Never** count publications by `publications.publish_at`: it is a slot/consent timestamp (TikTok writes `publish_at = consent_at`; pre-audit private rows carry it too — K24, 07 CR-9) — a future publication count must filter `status = published` and group by `platform`.

Keys and sources ([DECISION] §B3 D7): `signups`, `verified_email_rate`, `first_video_within_7d_rate`, `second_episode_within_30d_rate`, `render_success_rate`, `render_p95_seconds`, `cost_per_episode_usd`, `margin_percent`, `youtube_quota_used_percent`, `deletion_on_time_rate` → `db`; `gpu_utilisation_percent` → `metric` (link to 11.6.2-T1's node board); `flagship_watch_hours_12mo`, `flagship_subscribers`, `support_first_response_median_hours`, `support_sla_breaches` → `manual` (`DocsRef` into `docs/ops/flagship/kpi.md` and `docs/support/sla/YYYY-WW.md`). Targets come from `../../../08-90-day-operations.md` §5.1 and live in one `KpiTargets` table with an explicit direction (`higher_is_better` / `lower_is_better`); `warn` is at 90 % of the target, `breach` beyond it.

## 4. Use cases

| # | Use case | Story |
|---|---|---|
| UC1 | Owner opens `/admin/kpis` in the admin shell, switches 7/30 days, reads each block's value against its target and status, and copies the numbers into the weekly review file | 08.1.1 |
| UC2 | The same snapshot is fetched directly (link or `curl`) for a week that was missed; the URL carries `?window=30` so it is shareable | 08.1.1 |
| UC3 | Owner runs the weekly ritual from the template: KPI snapshot → incidents → costs/margin → quota → support themes → policy news → top 3 backlog changes, then the 15-minute money routine | 08.1.2 |
| UC4 | A manual metric (`flagship_*`, `support_*`) shows "manual — see the doc" with the file link, and the owner fills it in the doc | 08.1.1, 08.1.2 |

## 5. API and events

`GET /admin/kpis?window=7|30`
- Registered on 09.9.1-T1's `/api/v1/admin` group (`Api/Endpoints/AdminEndpoints.cs`) with `AdminPolicy` (`RequireRole(Admin)`) and `[Audited("kpis_view", Category.Support, Visibility.Internal)]`; no second `/admin` group, no own policy (K23).
- `window ∉ {7, 30}` → `400 invalid_window`; not admin/owner → `404 not_found` (09's rule for every `/admin/*` route).
- Response `200 KpiSnapshotDto`. Read-only: no `If-Match`, no idempotency key, no state change.
- Handler `Features/Admin/Kpis/GetKpiSnapshotQuery` (Mediator, `ErrorOr<KpiSnapshotDto>`), `AsNoTracking`, one set-based statement per block, ≤ 10 statements total, `IgnoreQueryFilters` only because the file is under `Features/Admin` (09.2.3-T1 arch test).
No domain event, no outbox row, no SignalR message.

## 6. UI

`features/admin/kpis/` (route `/admin/kpis`), mounted in the 10.9.1-T1 admin shell; nav entry appended to `ADMIN_NAV_ITEMS`: `{ path: '/admin/kpis', labelKey: 'admin.nav.kpis', roles: ['admin', 'owner'] }`; label keys in the `admin` Transloco scope, vi + en.
The page renders the nine blocks as tables (`<caption>`, `scope="col"`), status through the shared `StatusChip` as text (never colour-only), `manual` metrics with their `DocsRef` link, `metric` keys with the link to 11.6.2-T1's board, a 7/30-day selector written into the query string, and the shell's `LoadingState` / `EmptyState` / `ErrorState`. WCAG 2.1 AA: verified by 10.10.1-T1's `@axe-core/playwright` pass; no chart-only data.

## 7. Jobs and integrations

No new job. The page calls the endpoint on demand; the endpoint memoises the snapshot for 60 s in-process (F15) and is otherwise uncached. Integrations: 09.9.1's admin group (route), 12.5.2-T1's margin query (`IMarginQuery`), `/admin/funding` (12.8.1-T1) linked from the cost block, 11.6.2-T1's board linked from the capacity block. The manual sources are files (`docs/ops/flagship/kpi.md`, `docs/support/sla/YYYY-WW.md`).

## 8. Security and privacy

Aggregate data only: counts and rates, never per-user or per-episode rows, no e-mail, no title, so a KPI leak cannot disclose tenant data (D7, 09.2.3-T2). The route is MFA-protected (`09:275`) and audited (`kpis_view`, category `support`). No secret, no token and no provider credential appears in the response or in the page. The manual blocks hold business numbers (watch hours, subscribers, support medians), not user data. Retention: the endpoint stores nothing.

## 9. Test plan

- Unit: `KpiTargets` direction and status mapping per key (ok/warn/breach/unknown) — table-driven, all 15 keys.
- Integration (TestKit, seeded fixtures, `FakeClock`): `window=7` and `window=30` boundaries (row exactly on `From`, exactly on `To`, outside both); empty database → zeros for `db` keys and `unknown` for `manual`; activation counts only rows with a non-null `email_verified_at` and reports the excluded count; a user with two episodes counts once in retention.
- Definitions: a user verified 7 days + 1 second before their first succeeded render is not activated, one exactly at the boundary is; a user with two successes counts once; a user with a failed render only is not activated; a `publications` row with `publish_at` set but `status ≠ published` changes no KPI (K24).
- Contract: DTO key set equals the §B3 D7 list (a test that fails when a key is renamed) — this is the FE/BE contract, not source-text testing.
- Route: `window=15` → `400 invalid_window`; a non-admin token → `404 not_found`; the audit row is written with action `kpis_view`.
- Arch test: the handler lives under `Features/Admin` (09.2.3-T1) and the endpoint is registered on the shared admin group.
- E2E (08.1.1-T2): one Playwright spec under the owner project loads `/admin/kpis?window=30`, asserts the nine block headings and one status chip per block, and runs `@axe-core/playwright` (10.10.1-T1).

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 08.1.1-T1 | S (0.5) | BE | 09.9.1-T1, 09.9.3-T1, 11.6.2-T1 (soft), 12.5.2-T1, 08.3.1-T1 (soft) | 08.1.1-T2, 08.1.2-T1 (soft), 08.5.1-T2 | S · M2 |
| 08.1.1-T2 | S (0.5) | FE | 08.1.1-T1, 10.9.1-T0, 10.9.1-T1, 10.10.1-T1 (soft) | — | S · M2 |
| 08.1.2-T1 | XS (0.25) | BA/Ops | 08.1.1-T1 (soft) | 08.3.1-T1 | S · M2 |

**Estimate:** 1.25 d (BE 0.5 + FE 0.5 + BA/Ops 0.25) = `../README.md` §B8.1 = `../../../08-90-day-operations.md` §11 epic 08.1. The rows above are byte-identical to §B8.1 (verifier-checked).

## 11. Rollout

Milestone **M2**, behind the existing admin roles (no feature flag: `/admin/*` is already owner/admin-only). Order: 08.1.1-T1 → 08.1.1-T2 and 08.1.2-T1 in parallel. Preconditions: 09.9.1-T1's admin group and 12.5.2-T1's margin query exist; CR-11 (`users.email_verified_at`, the global `usage_records` index) is migrated, otherwise the activation block is `unknown` and the cost window is unindexed. Rollback: remove the nav entry (10 CR-13) — the endpoint is read-only, so no data can be corrupted. Success: the weekly review is filled from the snapshot for four consecutive weeks without ad-hoc SQL.
