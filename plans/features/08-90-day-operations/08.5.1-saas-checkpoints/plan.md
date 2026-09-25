# 08.5.1 — SaaS 30/60/90-day checkpoints (S slice)

> Part of plan 08 (S slice). Contract: `../README.md` Part B — §B1 (K19), §B3 (D7, D13), §B4 (CR-15), §B8.1. High-level: `../../../08-90-day-operations.md` §4 (08.5.1 day-30/60/90 text), §11. P sibling: `../08.5-checkpoints/` (08.5.1-T1, Gate 2 + Channel-phase checkpoints).
> Phase: **S · M3** (the SaaS checkpoints are only reachable after Gate 2 = go).

## 1. Scope

Decide deliberately at day 30, 60 and 90 of the SaaS operation: continue, widen sign-ups, start paid acquisition tests, or revisit pricing/segment — each on numbers that already have a name.

In: `docs/ops/saas-checkpoints.md` — the three checkpoint rows with `metric key | window | target | source | decision rule`, the sign-off block, the reminder rule and the revision log.

Out: any new platform code, job, e-mail template or table (D13); the P Gate 2 criteria and the week-4/8/12 Channel-phase checkpoints (08.5.1-T1, `docs/ops/gate-2.md`); the definition of any metric (08.1.1-T1 owns the keys; 12.8.1-T1 owns the money view; 12.5.2-T1 owns margin).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| K19 | 08.5.1-T2 is expected **outside** `08.5-checkpoints/` and outside its estimate | `../08.5-checkpoints/plan.md:9,211` ✅ |
| K11 | Revenue 7/30 days, provider cost and runway come from `GET /admin/funding` (12.8.1-T1); margin from 12.5.2-T1 | `../../../12-billing-credits.md:176-177,346` ✅ |
| K1 | The S phase starts only after Gate 2 (owner decision on channel results, cost per video and time per episode) | `../../../00-overview.md:68`; `../../../DECISIONS.md:338` ✅ |
| K22 | The KPI keys and their targets are named once in 08.1.1-T1 (`KpiTargets`) — this document must not invent a second name | `../../README.md` §B3 D7 ✅ |
| K20 | Infra cost is not in any plan's data model: node price is recorded by 11.6.3-T1, storage/telemetry prices by 11.2.1-T1, so the day-90 comparison needs one manual line | `11 README:266`; `11.6.3-T1` ✅ |

## 3. Domain and data

No table, no migration, no event, no API. The document is the artifact; its values come from existing sources by name: `KpiSnapshotDto` keys (D7) for `render_success_rate`, `deletion_on_time_rate`, `margin_percent`, `second_episode_within_30d_rate`; `GET /admin/funding` for 7/30-day revenue and provider cost; one manual line `infra_cost_30d_usd` (node + storage + telemetry invoices) defined in the document itself. A row is `day | metric key | window | target | source | decision rule | status | decision`.

**Source map for the checkpoint rows (the checklist §9 reads):**

| Row | Value | Window | Target | Source surface | Filled by |
|---|---|---|---|---|---|
| Day 30 | `render_success_rate` | last 30 d | ≥ 95 % | `GET /admin/kpis?window=30` (`db`) | owner |
| Day 30 | `deletion_on_time_rate` | last 30 d | 100 % | `GET /admin/kpis?window=30` (`db`) | owner |
| Day 30 | `margin_percent` | last 30 d | ≥ 60 % | `GET /admin/kpis?window=30` → 12.5.2-T1 | owner |
| Day 30 | sign-up gate ("no data-duty misses") | last 30 d | none | the deletion row above + 13's request log | owner |
| Day 60 | `second_episode_within_30d_rate` | last 30 d of activated users | ≥ 30 % | `GET /admin/kpis?window=30` (`db`) | owner |
| Day 90 | revenue | 7 d / 30 d | ≥ the two rows below | `GET /admin/funding` (12.8.1-T1) | owner |
| Day 90 | provider cost | 7 d / 30 d | — | `GET /admin/funding` (12.8.1-T1) | owner |
| Day 90 | `infra_cost_30d_usd` | last full month | — | manual: node (11.6.3-T1's price), storage, telemetry | owner |

The decision rules, sign-off block, revision rule and reminder rule live in §1/§4 of this plan and in the document itself; this table exists so the DoR can check every row against a named surface without reading prose. `infra_cost_30d_usd` is the only value with no platform source, and it is itemised from invoices so a later reader can rebuild it.

## 4. Use cases

| # | Use case | Story |
|---|---|---|
| UC1 | At day 30 the owner opens the document, fills the three day-30 numbers from `/admin/kpis?window=30` and `/admin/funding`, and applies the decision rule (widen sign-ups / hold / fix first) | 08.5.1-T2 |
| UC2 | The weekly review (08.1.2) shows "next checkpoint in N days" and the checkpoint dates, so nobody has to remember | 08.5.1-T2 |
| UC3 | A target has to change after signing: a dated revision row with the owner's initials is required, and the change is announced in the weekly notes | 08.5.1-T2 |
| UC4 | Gate 2 = no-go: §0 records the decision, the checkpoint rows are marked `not scheduled` and the S plans stop | 08.5.1-T2 |

## 5. API and events

None added. Consumes `GET /admin/kpis?window=7|30` (08.1.1-T1) and `GET /admin/funding` (12.8.1-T1) read-only. No new error code, no new DTO, no event, no webhook.

## 6. UI

No screen. Human interfaces: `docs/ops/saas-checkpoints.md` (owner-facing, vi summary at the top of each checkpoint section with the English row table) and one line in the weekly review file (`docs/ops/weekly/YYYY-WW.md`, 08.1.2-T1). The `/admin/kpis` page is the live view; this document is the decision record.

## 7. Jobs and integrations

No job and no reminder e-mail: the reminder is the standing agenda item in 08.1.2-T1 plus the owner's own calendar entry (D13 — deliberately no recurring job and no new template for a once-a-month need). Integrations are links only: 08.1.1-T1's snapshot, 12.8.1-T1's funding overview, 12.5.2-T1's margin, 11.6.3-T1's cost-per-video measurement and 08.4.3-T1's channel sheet for context.

**Why there is no automation (ruling).** Three candidate automations were rejected so the document stays a decision record instead of a feature:

1. **A reminder job or an e-mail template** — a recurring job plus a template for a need that occurs once per 30 days, and both would need scheduling, tests and an owner. Rejected: the countdown row in 08.1.2-T1's weekly review plus the owner's own calendar cover it (D13).
2. **A snapshot table** storing each checkpoint's numbers — a new table means a migration, a retention rule and a deletion path for business data. Rejected: the numbers are reproducible from `/admin/kpis` and `/admin/funding`, so the document stores the decision, not the evidence.
3. **Auto-evaluating the decision rules** ("open sign-ups wider") — widening sign-ups is a registration/product decision with its own controls (09.10.6). Rejected: the document states the rule and the owner applies it through that existing path.

## 8. Security and privacy

The document contains business aggregates and no personal data; it is a repository file, so it must never include credentials, provider account ids or user-identifying numbers (counts only). Removing or changing it needs no data-protection action because nothing is stored in the platform. Access follows the repository's rules; the sign-off line names the owner, not a credential.

## 9. Test plan

- Review (DoR/acceptance): every metric key in the document appears in 08.1.1-T1's `KpiTargets` (or is the explicit `infra_cost_30d_usd` manual line), each checkpoint row has all six columns, and the day-90 row points at `/admin/funding` for revenue and cost.
- Manual acceptance: the day-30 checkpoint is actually performed at day 30 of the S operation (or the first weekly review after it) with real numbers and a recorded decision; the countdown appears in `docs/ops/weekly/YYYY-WW.md`.
- Negative: if a key is missing (`unknown`), the row records it as a data gap with an owner and a date — never an estimate presented as a measurement.
- No automated test is added (document-only task), consistent with the P precedent `docs/ops/gate-2.md`.

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 08.5.1-T2 | XS (0.25) | BA | 08.5.1-T1, 08.1.1-T1 (soft) | — | S · M3 |

**Estimate:** 0.25 d (XS) — `../README.md` §B8.1 and §B9; the P sibling 08.5.1-T1 stays 0.25 d in `../08.5-checkpoints/`; plan-08 total unchanged (P 2.5 / S 4.5).

## 11. Rollout

Milestone **M3** (after Gate 2 = go and the S launch). Order: 08.5.1-T1 (P, signs Gate 2) → 08.5.1-T2 (this document, before day 30 of the S operation) → 08.1.1-T1's keys exist so the rows can be filled. Preconditions: Gate 2 decision recorded in `docs/ops/gate-2.md`; `users.email_verified_at` migrated (CR-11) so the activation number is real. Rollback: none needed (a document); if the checkpoints are abandoned, §0 records why. Success: three dated checkpoint decisions exist with real numbers, and the day-90 decision (continue or revisit pricing) is traceable to them.
