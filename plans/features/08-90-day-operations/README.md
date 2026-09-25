# 08 — Operations, P slice (flagship channel + checkpoints): Tech Lead review, shared decisions and feature index

> Reviews: `../../08-90-day-operations.md` — **P slice only**: epics 08.4 (flagship channel) and 08.5 (checkpoints) for the **Channel phase** (00 §4: run the owner's channel on autopilot for ≥ 12 weeks after the YouTube audit allows public uploads, then Gate 2). 08.1–08.3 (KPI dashboard for a SaaS, alert rules, runbooks, support) are S. Reviewed 2026-09-25 against DECISIONS §6/§10.12, 00 §4, 02.8.x, 06 (YR rulings), 11.6.2-T1 and 13.4.2-T1.
> Verdict: **approve with changes** — the high-level 08.4/08.5 was written for a manually produced flagship and a SaaS launch; the P reality is an autopilot channel whose output is gated automatically (DECISIONS §10.12) and whose purpose is to feed **Gate 2**.
> Status: **ready for implementation with conditions** (DoR 2026-09-25) — see §11.
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 0. Folders

```
plans/features/08-90-day-operations/
  README.md
  08.4-flagship-channel/   ← 08.4.1-T1 setup, 08.4.2-T1 weekly routine, 08.4.3-T1 KPI sheet, 08.4.4-T1 takedown runbook
  08.5-checkpoints/        ← 08.5.1-T1 Gate 2 criteria + week-4/8/12 checkpoints
```

## 1. Facts

| Id | Fact | Source |
|---|---|---|
| K1 | Channel phase: autopilot on the owner's channel for ≥ 12 weeks after the audit allows public uploads; Gate 2 decided on channel results, measured cost per video and time per episode | 00 §4 row "Channel phase" ✅ |
| K2 | Fiction auto-approved by gates; true crime waits for the owner; variety guard; per-channel daily cap default 1; veto window default 12 h | DECISIONS §10.12 ✅ |
| K3 | Gate 2 criteria are `[OPEN]` and must be set by the owner **before** the Channel phase starts | DECISIONS §10.12 ✅ |
| K4 | YPP: 1,000 subscribers + 4,000 watch hours (12 months) or 10M Shorts views (90 days); mass-produced content is not monetizable | DECISIONS §6 ✅ |
| K5 | No YouTube analytics stored via the API (stored-data duties); channel KPIs come from a manual Studio export | 08 §4 08.4.3 [DECISION] ✅ |
| K6 | Autopilot settings (cadence days/time/zone, daily cap 1..5, buffer, privacy, veto 2..48 h, pre-audit mode) and the versioned authorization with re-auth + legal acceptance | 02.8.3 `plan.md` §3.1; 02.8.3-T2 (+13 CR-2) ✅ |
| K7 | Pre-audit mode keeps uploads private until `YouTube:AuditStatus = approved` | 06 README D11, YR2, YR20 ✅ |
| K8 | Alerts already specified: YouTube quota > 80 %, autopilot pause/stuck (page), gate fail share, render failure share, platform incident, deletion due (rules 1–17) | 11.6.2-T1:32-50 ✅ |
| K9 | Pause triggers incl. reports, gate-fail streaks, spend caps; takedown handled by 08.4.4-T1 | 02.8.3-T4 ✅ |

## 2. Findings

| # | Sev | Finding | Resolution |
|---|---|---|---|
| F1 | H | 08.4.2 describes manual weekly production (3 briefs, ≥ 15 min human edit per script); in P fiction runs on autopilot (K2) | D2: the weekly routine operates the autopilot (veto review, weekly sample audit of ≥ 1 published video + its script, time log per touch) — manual editing only for true crime and when a gate fails |
| F2 | H | 08.5.1's day-30/60/90 criteria are SaaS metrics (sign-ups, activation, revenue); P has no users | D4: 08.5.1-T1 = Gate 2 criteria + week-4/8/12 channel checkpoints; the SaaS 30/60/90 checkpoints become 08.5.1-T2 (S, XS) |
| F3 | M | "3 videos/week, 8–12 min + 1 Short cut-down" conflicts with the daily cap default 1 (K2), the 02 length tiers (standard 5–8, long 10–15 min) and the absence of a Shorts cut-down feature | D1: cadence and tier are autopilot settings chosen at setup (default 3 days/week, `standard` until the 05 render-time and 02 cost measurements justify `long`); no Shorts cut-down in P |
| F4 | M | The Channel phase must not start before Gate 2 criteria exist (K3) and before the audit allows public uploads | D1/D4: 08.4.1-T1's exit requires the signed criteria document (08.5.1-T1 drafts it first) and records the audit status; private pre-audit runs are allowed as a rehearsal, not counted in the 12 weeks |
| F5 | M | Time per episode (a Gate 2 input, K1) has no measurement source in an autopilot flow | D3: the KPI sheet records owner touch time per episode (veto review, edits, true-crime approvals, incidents) from the weekly log, plus platform cost/episode from `usage_records` and render duration from metrics |
| F6 | L | Alert thresholds in 08 §5.2 differ from 11.6.2-T1's loaded values (e.g. quota 70 % vs 80 %) | D5: 11.6.2-T1's D17 values are authoritative in P; 08 §5.2 is revisited with 08.2 (S) |

## 3. Decisions

**D1 Flagship setup [DECISION].** One series: topic `ghost`, language `vi`, aspect `landscape`, voice and style from the 03/04 catalogs chosen by a 3-candidate listening/looking test on the M0 outputs; length tier `standard` (upgrade to `long` only after one week of cost/render data); autopilot cadence 3 days/week at a fixed local time, daily cap 1, veto window 12 h, privacy `public` (effective only after audit approval), pre-audit mode `keep_cadence_private` until approved. Calendar = 12 weekly rows (week, planned slots, theme notes, risk notes), stored in `docs/ops/flagship/calendar.md`. The authorization is accepted through the product (re-auth + legal acceptance).

**D2 Weekly routine [DECISION].** `docs/ops/flagship/weekly/YYYY-WW.md` from a template: veto notices seen/edited/cancelled; one sampled published video watched in full + its script read against the 02 rubric (scores recorded); true-crime approvals; gate failures and pauses with causes; incidents; owner touch time per episode (minutes); decisions for next week. Time box 45 min/week.

**D3 KPI sheet [DECISION].** `docs/ops/flagship/kpi.md` (one row per week): Studio export (views, CTR, average view duration, subscribers, watch hours — manual, K5), platform (videos published, render success share, p95 render time, cost/episode from `usage_records` via an SQL snippet stored in `docs/ops/flagship/queries.sql`, gate pass share, pauses), owner touch time/episode (D2). Nothing is fetched from YouTube Analytics.

**D4 Checkpoints [DECISION].** 08.5.1-T1 writes `docs/ops/gate-2.md`: the owner's Gate 2 criteria (proposed defaults for the owner to accept or change: after 12 public weeks — cost/video ≤ the 12 price model's target, owner touch time ≤ 20 min/episode, ≥ 90 % of slots published without manual rescue, no policy strike, channel trend (subscribers/watch hours) recorded; the monetization thresholds (K4) are tracked, not required), plus week-4/8/12 checkpoint questions (continue / adjust settings / stop). Signed by the owner before 08.4.1-T1 turns public autopilot on.

**D5 Alerts and runbooks in P.** No new alert: the P operator relies on 11.6.2-T1's rules (K8); the only P runbook here is the takedown runbook (08.4.4-T1) — the rest of the 08.2 runbooks are S.

## 4. Change requests

| CR | Target | Change |
|---|---|---|
| 08 CR-1 | `plans/08-90-day-operations.md` §4/§6/§11 | 08.4.2 wording → autopilot routine (F1); 08.5.1 split into T1 (P, Gate 2 + channel checkpoints) and T2 (S, SaaS 30/60/90) (F2); 08.4.1 cadence/tier per D1 (F3) |
| 08 CR-2 | `DECISIONS.md` §10.12 | the `[OPEN]` Gate 2 criteria line points to `docs/ops/gate-2.md` drafted by 08.5.1-T1 and signed before the Channel phase |
| 08 CR-3 | `plans/00-overview.md` §4.1/§13 | 08: P 2.5 unchanged; S +0.25 (08.5.1-T2) |
| 08 CR-5 | upstream task headers (02, 06, 11) | Blocks back-edges for §8.1 hard dependencies |
| 08 CR-4 | `features/02-story-writing/02.8.3-cadence-publishing/02.8.3-T4-…` + plan §10 row | the dependency on 08.4.4-T1 (runbook text) becomes soft — the runbook depends on 02.8.3-T4's pause mechanics, not the reverse (breaks a cycle) |

## 5–7. Sequencing, template, gates

08.5.1-T1 (criteria, signed by the owner) comes first; 08.4.1-T1 after it, 02.8.3-T2 and a connected channel, public autopilot only after the audit (13.4.2-T1 soft: rehearsal runs private) and the signed criteria; 08.4.2-T1 weekly; 08.4.3-T1 weekly after the first week; 08.4.4-T1 before public autopilot. Template: `../02-story-writing/README.md` §6 (BA tasks: deliverable files, steps, checks). Gate **Channel-phase-start**: criteria signed, series + authorization live, takedown runbook rehearsed once, KPI sheet template ready.

## 8. Feature index

| Folder | Tasks (size) |
|---|---|
| `08.4-flagship-channel/` | 08.4.1-T1 (S), 08.4.2-T1 (M), 08.4.3-T1 (S), 08.4.4-T1 (XS) |
| `08.5-checkpoints/` | 08.5.1-T1 (XS) |

### 8.1 Canonical task dependencies

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 08.5.1-T1 | XS (0.25) | BA | P · Channel phase | — | 08.4.1-T1, 08.5.1-T2 (08 CR-15) |
| 08.4.1-T1 | S (0.5) | BA/Ops | P · Channel phase | 08.5.1-T1, 02.8.3-T2, 06.1.2-T2, 13.4.2-T1 (soft) | 08.4.2-T1 |
| 08.4.2-T1 | M (1.0) | BA/Ops | P · Channel phase (recurring; estimate covers setup) | 08.4.1-T1, 02.8.3-T4, 06.2.2-T3 | 08.4.3-T1 |
| 08.4.3-T1 | S (0.5) | BA | P · Channel phase | 08.4.2-T1, 11.6.2-T1 | — |
| 08.4.4-T1 | XS (0.25) | BA/Ops | P · Channel phase | 02.8.2-T2, 02.8.3-T4 (soft) | — |

## 9. Re-estimate

| Area | High-level P | Detailed | Why |
|---|---|---|---|
| 08.4 + 08.5 | 2.5 | 2.5 | unchanged; 08.5.1-T2 (S, XS 0.25) added for the SaaS checkpoints |
| Plan 08 | 6.75 | **7.0** (P 2.5, S 4.5) | buffered 8.4 |

## 10. Owner decisions and TL rulings

| # | Decision | Outcome |
|---|---|---|
| O1 | Flagship defaults (D1) and the proposed Gate 2 criteria (D4) | Accepted as proposals under the standing approval; the owner signs the final criteria document in 08.5.1-T1 (DECISIONS §10.12 requires the owner to set them) |

## 11. Definition-of-Ready review (2026-09-25)

- Script: 2 `plan.md` files with sections 1–11 (headings normalised by the TL), 5 task files with header + 9 headings, headers equal §8.1, total 2.5 d = §9 = `08-90-day-operations.md` P rows; graph 0 cycles after 08 CR-4 (the 02.8.3-T4 ↔ 08.4.4-T1 edge is soft on both sides).
- Review pass (6 findings: two SQL column errors, `consent_text_version` vs authorization hash, takedown mechanics for disabled/paused/uploading rows) verified and fixed by the writer; checked on disk (Q7 uses `created_at`, Q1 uses `publish_at` with the 06 CR-1 note, runbook state table).
- CRs 08 CR-1…CR-5 applied (`08-90-day-operations.md`, DECISIONS §10.12, 00 §4.1/§13, 02.8.3-T4 soft edge, upstream back-edges).

**Verdict: ready for implementation with conditions** (accepted 2026-09-25). **Conditions:** (1) the owner signs `docs/ops/gate-2.md` (08.5.1-T1) with real numbers before public autopilot — the proposed defaults are not decisions; (2) the Channel phase's 12 counted weeks start only after the YouTube audit allows public uploads; (3) cost thresholds are filled once 11.6.3-T1 and 02's metering give a measured cost per video.

# Part B — S slice: platform KPIs, alerts/runbooks, support, SaaS checkpoints

> Reviews: `../../08-90-day-operations.md` §4 (08.1–08.3, 08.5.1), §5 (KPI/alert/runbook/support tables), §6, §7, §9, §11; **S slice only** — the SaaS phase (M2 → M3, after Gate 2, `../../00-overview.md` §4.1). The P part above (08.4, 08.5.1-T1) is untouched. Reviewed 2026-09-25 against 00 §4.1/§6/§8, DECISIONS §6/§10.12, 09 §5.4/§231-236/§282-284, 11 README D17/D19/D20 + 11.6.2-T1, 12 §4 (12.3.3/12.4.1/12.5.2/12.8.x), 06.2.3-T3, 10 README D14/`:146`, 13 `:115`.
> Verdict: **approve with changes** — 08.1–08.3 were written before 11.6.2-T1 existed (two threshold sets), before 09.10.4's `ISecurityAlerter`, and before the S web/API slices were scoped; the KPI windows are not computable from 00 §6 as written.
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## B0. Folders and slice scope

| Folder | Tasks (size) |
|---|---|
| `08.1-cadence-kpis/` | 08.1.1-T1 (S), 08.1.1-T2 (S, split of 08.1.1-T1), 08.1.2-T1 (XS) |
| `08.2-alerts-runbooks/` | 08.2.1-T1 (S), 08.2.1-T2 (XS), 08.2.2-T1 (M), 08.2.2-T2 (XS) |
| `08.3-support-feedback/` | 08.3.1-T1 (XS, split of 08.3.1-T1), 08.3.1-T2 (XS), 08.3.2-T1 (S) |
| `08.5.1-saas-checkpoints/` | 08.5.1-T2 (XS) — the P plan expects it **outside** `08.5-checkpoints/` (`08.5-checkpoints/plan.md:211`) |

Slice = 11 tasks, **4.5 raw d** (1.25 + 2.0 + 1.0 + 0.25), identical to 00 §4.1 S = 4.5 and to `08-90-day-operations.md` §11 per epic. Two high-level tasks are split (08.1.1-T1 → T1+T2, 08.3.1-T1 → T1+T2) so the BE/ops and FE halves can be written in parallel; no id in `08-90-day-operations.md` §6 disappears and no epic total changes (08 CR-6). 08.5.1-T2 keeps the high-level size and moves to a sibling folder.

**Consumed, never duplicated:** 11.6.2-T1's 21 alert rules, rule file and alert channel (K10); 12's alerts — `12.3.3-T2`, `12.4.1-T1`, `12.5.2-T1`, `12.8.3-T1`, `12.8.4-T1` (K11); 06's `platform_incidents` + `06.2.3-T3` clear verb (K16); 09.9.3's metrics/OTel wiring (K14); 09.10.4's `ISecurityAlerter` (security events + pre-change-config owner e-mail); 09.10.5's user-management actions; 09.10.7's break-glass CLI; 10's web shell, `roleGuard` and public pages; 11's P runbooks (`restore-drill`, `node-deploy`, `secrets-rotation`, key-leak) and 08.4.4-T1's takedown runbook (K18).

**Not in this slice:** any new PostgreSQL table, and no new column except CR-11's `users.email_verified_at` + one global index (00 §6 is otherwise unchanged; `created_at` on every table comes from the preamble `:262`); any impersonation or view-as-user (K12); any new paid component (the mailbox is the existing e-mail provider's, the telemetry backend is 11.2.1-T1's); any promotion/marketing spend; 08.4/08.5.1-T1 (P).

**Folder briefs.**
- `08.1-cadence-kpis/` — the owner's single weekly-number surface. 08.1.1-T1 defines the read model once: `GET /admin/kpis?window=7|30` returning `KpiSnapshotDto` (D7), its SQL over existing tables, and the manual metrics that stay manual; 08.1.1-T2 is the Angular page in 10's admin shell; 08.1.2-T1 is the weekly agenda/template plus the 15-minute money routine (`/admin/funding`). Trap: every table already has `created_at` (00 §6 preamble `:262`) — never re-add it and never fake dates from `audit_logs`; the one genuine gap is `users.email_verified_at` (CR-11), which the ≤ 7-day activation window and 12.4.2-T1's trial grant both need.
- `08.2-alerts-runbooks/` — one alerting vocabulary, two mechanisms (D8): metric rules stay in 11.6.2-T1's backend, DB-derivable rules go through `OpsAlertSweepJob`, both reading the same repository rule file, every rule carrying `owner` + `runbook` (NFR-08-1). 08.2.2-T1 writes the 14 S runbooks + `docs/runbooks/index.md`; 08.2.2-T2 rehearses break-glass on staging. Trap: 08 §5.2 currently disagrees with 11's D17 values (F8) and lists thresholds no metric can express (F9).
- `08.3-support-feedback/` — support process, not support tooling: `support@<domain>` alias + tag taxonomy + SLA timers + vi/en macros (08.3.1-T1), the public `/help` FAQ page the in-app Help link opens (08.3.1-T2), and the feedback loop with 5 interviews/month into `docs/ops/backlog.md` (08.3.2-T1). Trap: no impersonation, no content in tickets/alerts (F7/F13).
- `08.5.1-saas-checkpoints/` — `docs/ops/saas-checkpoints.md`: the day-30/60/90 go/no-go rows, each `metric key | window | target | source | decision rule`, reusing D7's KPI keys and `/admin/funding` instead of new numbers (F12).

## B1. Facts verified for this slice

| Id | Fact | Source |
|---|---|---|
| K10 | 11.6.2-T1 owns the alerting pipeline, the two dashboards, the 21 rules and the **rule file in the repository**; thresholds are owned by 08 §5.2 and initial values by D17 (`API 5xx > 2 %/5 min`, `outbox lag > 30 s`, quota `> 80 %`, render failure `> 20 %/24 h`, queued `> 360 min`, disk 80/85 %, GPU temp 85 °C, cert < 30 d, `platform_incident_open > 0`, deletion due < 24 h) | `11.6.2-T1:22-50`, `11 README:266,304,436` ✅ |
| K11 | 12 owns every money threshold: runway `< 7 d` warn / `< 3 d` critical + trial pause + `FundingRunwayJob`; margin-drop alert + FX; `PaymentAnomalyJob` (24 h after a `payment.*` change); spend-cap alerts | `../../12-billing-credits.md:184-192,338,342,361` ✅ |
| K12 | **No impersonation** [DECISION]: story text, sources and videos are visible to admins only from a moderation event or a report case, and each view writes `audit_logs` (`content_viewed`); staff/security alerts never leave the owners | `../../09-backend-api.md:284`, `../../13-compliance.md:115` ✅ |
| K13 | `audit_logs.category` already has `support` and `visibility` `internal`; admin mutations require a reason and write a row | `../../00-overview.md:307`, `09:225,285` ✅ |
| K14 | The platform's custom metrics are exactly `render_jobs_active`, `render_duration_seconds`, `openai_cost_usd_total`, `youtube_quota_used`, `outbox_lag_seconds` (+ 11's node and relay names) | `09:231-236`, `11.6.2-T1:22` ✅ |
| K15 | `email_messages` records `template`, `locale`, `status` (`queued`/`sent`/`failed`), `attempts`, `last_error_code` — a failed security e-mail is DB-derivable | `../../00-overview.md:270` ✅ |
| K16 | `platform_incidents` is per `(platform, oauth_client_id)` with a partial unique while open, cleared by an operator; it is not a generic alert log | `../../00-overview.md:293`, `06.2.3-T3` ✅ |
| K17 | `platform_quota_usage(platform, quota_day, units_used, units_limit)` is the quota ledger the capacity KPI reads | `../../00-overview.md:292` ✅ |
| K18 | P runbooks already exist in `docs/runbooks/`: `restore-drill.md` (11.6.1-T1), `node-deploy.md` (11.3.1-T3), `secrets-rotation.md` + key-leak (11.5.2-T1/11 CR-3), `source-finder-allow-list.md` (02.7.2-T1); 08.4.4-T1's takedown runbook stays where it is | `11 README:145`, `11.3.1-T3:31`, `02.7.2-T1:76`, `08.4.4-T1:158` ✅ |
| K19 | The SaaS checkpoints are expected **outside** `08.5-checkpoints/` and outside its estimate | `08.5-checkpoints/plan.md:9,211,217` ✅ |
| K20 | The registered domain and DNS (`app.<domain>`, `staging.<domain>`) come from 11.2.1-T1/11.2.2-T3; the exact domain is not yet fixed | `11 README:253`, `09 §8` ✅ |
| K21 | Admin/owner screens and `roleGuard` are S; wave 1 builds P screens only; `cost-line`/`header-end` slots exist for S tasks to fill | `10 README:3,146,180` ✅ |
| K24 | `publications.publish_at` is a **slot/consent** timestamp, not a publish instant: TikTok writes `publish_at = consent_at` (Direct Post has no scheduling parameter) and pre-audit private rows carry it too, so grouping "published" by it over-counts; count by the `status` transition (`published`) plus `platform` | 07 slice lead IRC 2026-09-25 (07 CR-9) ✅ |
| K25 | `security_events_total{kind}` is a counter metric (`kind = SecurityEvent.Kind`) registered through 09.9.3's meter, so the 9 security-event rules can be real metric rules instead of documentation-only mappings. Frozen `kind` values (09 W3 ruling, never the `account.*` audit action names): `security.settings_changed`, `security.role_changed`, `security.mfa_changed`, `security.recovery_code_used`, `security.owner_login_new_ip`, `security.grant_above_cap`, `security.settings_rolled_back`, `security.owner_action_pending`, `security.break_glass_recovery` | Lead09S IRC 2026-09-25 ✅ |
| K23 | 09 S contract (Lead09S, 2026-09-25): the `/admin/kpis` handler registers on **09.9.1-T1**'s `/api/v1/admin` group (`Api/Endpoints/AdminEndpoints.cs`) with `AdminPolicy` + `[Audited(…, Category.Support, Visibility.Internal)]` (no second admin group); the security alerter is **09.10.4-T3**, payload `ISecurityAlerter.AlertAsync(SecurityEvent evt, ct)` / `SecurityEvent(string Kind, Guid? SubjectUserId, string? Detail)` (09.3-auth/09.10.4-T1:53-54; severity/template routing is 09.10.4-T3's `SecurityEventCatalog` lookup, not a payload field), and its template set is pinned (`security_alert`, `owner_action_pending`, `setting_change_pending`, `approval_cancel` [renamed from `cancel_link`, 2026-09-25: 09.10.8-T1 reuses the signed `approval_cancel` template with a new `requestId` param instead of a new one], `mfa_failed_notice`, `session_new_device`, `mfa_reset_forced`); 09.9.3 also exports `security_events_total{kind}` (`kind = SecurityAlert.Event`) for metric-backed security rules (K25); adding `OpsAlertSweepJob` needs a CR to 09.2.6-T1's `RecurringJobs.cs` (P-signed); the one-shot `nts-admin` image owner is proposed as `11.6.4-T1` (S, `[OPEN]`) | 09 S lead IRC 2026-09-25 ✅ (sibling contract, not an official doc) |
| K22 | 00 §6's preamble gives every table `id`, `created_at` and `updated_at` where mutable; `users` nevertheless has **no verified-at timestamp**, and no global `usage_records` index exists for a whole-platform window (12's is tenant-scoped) | `../../00-overview.md:262,266`; Lead12 2026-09-25 ✅ |

## B2. Findings

| # | Sev | Finding | Fix |
|---|---|---|---|
| F7 | H | The support story is read as "admin impersonation"; both signed contracts forbid it (K12). Impersonation would also break the audit trail (`audit_logs.actor_type` has no `impersonator`) | D10: **no impersonation, ever**; support works through 09.10.5's audited actions (reason + `audit_logs` category `support`) and content is visible only inside a moderation/report case; the task is flagged `no-impersonation` |
| F8 | H | Two alert-threshold sets disagree on the shared rules: 08 §5.2 `API 5xx > 2 % for 10 min`, `quota > 70 %`, `render failure > 10 % over last 20 jobs`, `queue wait > 30 min`, `outbox lag > 60 s` vs 11.6.2-T1/D17 `5 min`, `80 %`, `20 %/24 h`, `360 min`, `30 s`. P F6 deferred the merge to 08.2; nothing enforces either set | D8: 11.6.2-T1's loaded values win for shared rules; 08 §5.2 keeps only the rules 11 does not have and gains a `Source` column (CR-9); 08.2.1-T1 proves every value against the rule file |
| F9 | H | 08 §5.2 asks for alerts no metric can express — failed logins, refresh-token reuse, moderation spike, token-refresh failures, payment-webhook failures, failed security e-mail — while the metric vocabulary is fixed (K14) and 11's 21 rules are metric-only | D8: `OpsAlertSweepJob` (5 min) evaluates `db_checks` from the same rule file against existing tables and pushes to 11's pipeline; names/options/metrics pinned; CR-8 (09.2.6 + 09.9.3) and CR-10 (11.6.2-T1 rule schema) |
| F10 | H | The KPI set is only **partly** computable from 00 §6: the preamble (`:262`) grants `created_at` to every table, but `users` has no verified-at timestamp, so the ≤ 7-day activation window (verified e-mail → first video) has no start instant and 12.4.2-T1's trial grant has nothing to fire on (Lead12 2026-09-25); no index supports a whole-platform `usage_records` window (12's is `(user_id, operation, created_at)`) | CR-11: add `users.email_verified_at` + the global index `usage_records (created_at)` (12 installs its tenant-scoped one via 12 CR-12; both land once in 09.2.2), and require 09.2.2's `InitialCore` to realise the preamble's `created_at` on `users`/`episodes`/`usage_records`; M2 |
| F11 | M | "Build KPI dashboard" (M) has no route, DTO or source contract; 00 §8 has no KPI route and `GET /admin/funding` (12.8.1-T1) already owns provider money | D7: `GET /admin/kpis?window=7|30` + `KpiSnapshotDto`/`KpiMetricDto` with pinned keys and `source` (`db`/`metric`/`manual`); margin reuses 12.5.2-T1's query; GPU utilisation and the flagship numbers stay links to 11's board and the P KPI sheet; CR-7 to 00 §8 |
| F12 | M | "Write 14 runbooks" has no slug/format/owner contract, and NFR-08-1's "each alert has an owner, a threshold and a runbook link" cannot be verified: 11's rule entries carry no `owner`/`runbook` fields | D8/D11: 14 slugs + `docs/runbooks/index.md` (alert id → rule → severity → owner → runbook → last rehearsed), the rule schema gains `owner`/`runbook`, and `tools/ops/check_alert_contract.sh` fails CI when a rule or the index is incomplete |
| F13 | M | 08.5.1-T2's day-30/60/90 criteria repeat 08 §5.1 targets without sources or an owner, and "calendar reminders" implies new platform machinery for a once-a-month need | D13: `docs/ops/saas-checkpoints.md` rows name a D7 KPI key + window + target + decision rule; day 90's revenue/cost comes from `/admin/funding` (12.8.1-T1) and a manual infra line; reminders = a standing item in 08.1.2's weekly agenda + the owner's own calendar — no new job, no new e-mail template |
| F14 | M | The support channel is an open surface with no data-minimisation or anti-spam rule: tickets and Help links can leak story text, tokens or PII, and `support@` invites spam | D10: provider-hosted alias (no forwarding to a personal mailbox, no attachments stored), Help link prefills ids/version/locale only, macros and FAQ never ask for password/TOTP/recovery codes, the 180-day mailbox retention is stated in the runbook, no auto-reply |
| F15 | M | `GET /admin/kpis?window=30` would aggregate whole-platform reads of `render_jobs`/`usage_records`/`episodes` with no supporting index (00 §6 lists none for them) and with the tenant filter bypassed | D7/B6: single set-based aggregate per block, the CR-11 index `usage_records (created_at)` plus 00 §6's existing indexes, handler inside `Features/Admin` (the only place `IgnoreQueryFilters` is allowed, 09.2.3-T1), counts/rates only — never per-user rows — plus `GeneratedAt` cached for 60 s in-process |
| F17 | M | Two KPI definitions in 08 §5.1 are not measurable as written: "downloads **or** publishes 1 video within 7 days" has no download event in 00 §6 (activation would need a new event), and any publication-based count grouped on `publish_at` over-counts (K24 — it is a slot/consent timestamp) | D7's pinned definitions: activation = the first `render_jobs.status = succeeded` (`finished_at`) within 7 days of `users.email_verified_at` (a succeeded render is the observable "video exists"; download tracking stays out of scope `[DECISION]`); no key in this slice reads `publish_at`, and any future publication count must filter `status = published` and group by `platform` |
| F16 | L | Alert noise is unmanaged: the same condition would page on every 5-minute sweep, and a page path that cannot deliver (no channel chosen yet, `11.2.1-T1` `[OPEN]` ⚠️) fails silently | D8: every rule carries `dedup_key = <rule>:<window bucket>` and the backend groups on it with `repeat_interval = OpsAlerts:PageCooldownMinutes` (60); the pipeline send is retried by the job and a failed send raises `ops_alerts_fired_total{severity="send_failed"}` + a DB check that pages via the e-mail fallback (02.8.3-T4's path) |

## B3. Shared decisions for the S slice (contract)

**D6 Slice boundaries and reuse [DECISION].** 08 S adds no PostgreSQL table and no new paid component; it extends 11.6.2-T1's rule file, reuses 09.9.3's metrics/meter and 09.10.4's `ISecurityAlerter`, reads existing tables only (`users`, `episodes`, `render_jobs`, `usage_records`, `platform_quota_usage`, `data_deletion_requests`, `payments`, `email_messages`, `channel_connections`, `publications`, `moderation_events`), and keeps every money threshold in 12. Cross-plan ids used by this slice: `09.9.1-T1`, `09.9.3-T1`, `09.10.4-T3` (the security alerter; 09.10.4-T2 is the role hierarchy), `09.10.5-T1`, `09.10.7-T1`, `11.6.4-T1` (`nts-admin` job image, `[OPEN]`), `10.9.1-T0`, `10.9.1-T1` (admin shell + `ADMIN_NAV_ITEMS`), `10.10.1-T1`, `12.3.3-T2`, `12.4.1-T1`, `12.5.2-T1`, `12.8.3-T1`, `12.8.4-T1`, `11.5.1-T1`, `11.6.2-T1`, `13.3.1-T1`, `06.2.3-T3` (CR-12/CR-13/CR-14 pin them).

**D7 KPI read model [DECISION].** Route `GET /admin/kpis?window=7|30` (role `admin`/`owner`; anything else `404 not_found` like every `/admin/*` route; `window ∉ {7,30}` → `400 invalid_window`). Response `KpiSnapshotDto (string Window, DateTimeOffset From, DateTimeOffset To, DateTimeOffset GeneratedAt, KpiBlockDto Acquisition, Activation, Retention, Reliability, Cost, Capacity, Compliance, Support, Flagship)`; `KpiBlockDto` = `KpiMetricDto[] Metrics`; `KpiMetricDto (string Key, decimal? Value, string Unit, decimal? Target, string Status, string Source, string? DocsRef, string? Link)` with `Status ∈ {ok, warn, breach, unknown}` and `Source ∈ {db, metric, manual}`. Pinned keys: `signups`, `verified_email_rate`, `first_video_within_7d_rate`, `second_episode_within_30d_rate`, `render_success_rate`, `render_p95_seconds`, `cost_per_episode_usd`, `margin_percent`, `youtube_quota_used_percent`, `gpu_utilisation_percent`, `deletion_on_time_rate`, `support_first_response_median_hours`, `support_sla_breaches`, `flagship_watch_hours_12mo`, `flagship_subscribers`; targets are 08 §5.1's, held in one options/const table in the handler (`KpiTargets`), never inline per branch. Two definitions are pinned because 08 §5.1 is ambiguous (F17): `first_video_within_7d_rate` = share of users with a non-null `email_verified_at` whose first `render_jobs` row with `status = succeeded` has `finished_at ≤ email_verified_at + 7 days` (a download event does not exist in 00 §6 — `[DECISION]`); `second_episode_within_30d_rate` = share of activated users with a second `episodes` row created within 30 days of the first. No key in this slice reads `publications.publish_at` (K24); a future publication count must filter `status = published` and group by `platform`. `db` keys come from D7 SQL; `metric` keys (`gpu_utilisation_percent`, `render_p95_seconds` if the metric is preferred) carry `Link` to the 11.6.2-T1 board; `manual` keys (`flagship_*`, `support_*`) return `Value = null`, `Status = unknown` and `DocsRef = docs/ops/flagship/kpi.md` / `docs/support/sla/YYYY-WW.md`. No per-user row ever appears in the DTO. The page mounts through 10's registry: append `{ path: '/admin/kpis', labelKey: 'admin.nav.kpis', roles: ['admin', 'owner'] }` to `ADMIN_NAV_ITEMS` in `features/admin/nav/admin-nav.ts`, label keys in the `admin` Transloco scope (vi + en) — agreed with Lead10S 2026-09-25. The handler itself registers on 09.9.1-T1's `/api/v1/admin` group (`Api/Endpoints/AdminEndpoints.cs`) with `AdminPolicy` (`RequireRole(Admin)`, 404 for everyone else) and `[Audited("kpis_view", Category.Support, Visibility.Internal)]` — no second `/admin` group and no own policy; the route reaches 00 §8 through 09 W3 CR-5, so 08 files no route CR (Lead09S 2026-09-25).

**D8 One alert vocabulary, two mechanisms [DECISION].** Thresholds stay owned by 08 §5.2 (CR-9 makes 11.6.2-T1's values authoritative for shared rules). All rules live in 11.6.2-T1's repository rule file; each entry is `id, severity (page|warn), for, expression | db_check, owner (role or alias), runbook (docs/runbooks/<slug>.md), dedup_key, window`. Metric-backed rules are loaded by 11's pipeline unchanged. DB-backed rules are a `db_checks:` section evaluated by `OpsAlertSweepJob` (recurring, `OpsAlerts:SweepMinutes` = 5, single-instance via the house `[JobPolicy]`, no content/user ids beyond counts in logs or payloads) which pushes `(id, severity, dedup_key, summary)` to 11's alert channel; `OpsAlertsOptions { Enabled, SweepMinutes = 5, PageCooldownMinutes = 60, RuleFile = "ops/alerts/rules.yml", EmailFallbackEnabled = true }`. The DB checks this slice owns: `login_failures_spike` (failed logins > 100/h), `refresh_token_reuse` (any reuse-detected revocation), `moderation_spike` (> 20/h or one user > 5/day), `token_refresh_failures` (> 5/h), `payment_integrity` (any invalid signature, > 3 processing errors/h, any `needs_review` older than 4 h — severity and copy reference 12's runbook, the threshold stays 12's), `security_email_failed` (any `email_messages` row with `status = 'failed'` whose `template` is in the pinned set `security_alert`, `owner_action_pending`, `setting_change_pending`, `approval_cancel`, `mfa_failed_notice`, `session_new_device`, `mfa_reset_forced` → page), `post_deploy_smoke_failed` (pushed by the 11.5.1-T1 pipeline step), `email_queue_stale` (oldest `email_messages` row still `queued` > 15 min → page: the e-mail path itself is down, so an alert could not be delivered). `paid_volume_anomaly`, `runway`, `margin_drop` and `spend_cap` stay 12's (K11) and are only linked: 12 declares no alert rule of its own — it emits `billing_*` metrics routed through 11.6.2-T1 (Lead12 2026-09-25, 12 CR-8), so the rule ids in the shared file are 08's and the thresholds remain 12's. Metrics `ops_alerts_fired_total{rule,severity}` and `ops_alert_sweep_duration_seconds` are registered through 09.9.3's meter (CR-8).

**D9 Security-event routing [DECISION].** The nine D9 events (settings change, role change, MFA enrol/reset/disable, recovery-code use, owner login from a new IP, grant above cap, settings rollback, owner-action pending [08 O-4, accepted], break-glass) are produced and e-mailed by **09.10.4-T3**'s `ISecurityAlerter` (payload `ISecurityAlerter.AlertAsync(SecurityEvent evt, ct)` / `SecurityEvent(string Kind, Guid? SubjectUserId, string? Detail)` (09.3-auth/09.10.4-T1:53-54; severity/template routing is 09.10.4-T3's `SecurityEventCatalog` lookup, not a payload field)) with the configuration active **before** the change; this slice only (a) turns each into a real **metric** rule — `expression: security_events_total{kind="<event>"} > 0`, `severity: page`, one rule id per event, each with `owner` + `runbook` (D11) and loaded by 11.6.2-T1's pipeline exactly like its other 21 rules (K25, 08 CR-17; no longer a documentation-only mapping) — (b) adds `security_email_failed` (D8) as the P1 backstop, (c) asserts both, plus the counter's increment, in the drill. The security e-mail template set is already pinned by 09 (K23) and **none of it is ours** — this slice consumes the alert pipeline, not the owner e-mail; alerts carry no user content and are never sent to third parties (K12).

**D10 Support contract — no impersonation [DECISION].** Channel: the platform's shared mailbox alias `support@<domain>` on 11.2.1-T1's e-mail provider (no forwarding, no per-user mailbox); in-app Help link = nav item `help` → public prerendered route `/{locale}/help` (in 10's SD2 prerender list + sitemap, 08 CR-13; Lead10S 2026-09-25) plus a `mailto:support@<domain>` button prefilled with `subject=[nts:<tag>] …` and `body` containing ids/version/locale only (`episode`, `renderJob`, `publication`, `appVersion`, `uiLocale`); never tokens, never story text. Tags (fixed): `billing`, `render`, `publish`, `account`, `content_report`. SLA targets = 08 §5.4 mapped to tags (`account` 8 business hours; `render`/`publish` 1 business day; `content_report` 72 h; general `billing` 2 business days) with the first-response clock recorded per ticket in `docs/support/sla/YYYY-WW.md`. Macros: one canned vi/en reply per tag plus one per error code the user reports (codes as named in 09 §5.4). Access rules for staff: 09.10.5 actions only (reason required, `audit_logs` category `support`); account/billing data is visible, story text/sources/videos only inside a moderation or report case (K12); macros and FAQ must never ask for a password, TOTP code or recovery code; mailbox retention 180 days, attachments not stored. **Impersonation is prohibited** — no view-as-user route, cookie or token exists in this slice or any other.

**D11 Runbooks [DECISION].** Path `docs/runbooks/<slug>.md`; fixed sections `Symptoms | Checks | Fix steps | User communication (vi/en) | Follow-up | Owner`; P files (K18) are referenced, not rewritten. The 14 S slugs: `worker-down`, `api-error-spike`, `outbox-lag`, `render-queue`, `openai-outage`, `openai-balance`, `moderation-abuse`, `youtube-quota`, `token-refresh`, `payments-integrity`, `deletion-deadline`, `security-incident`, `owner-recovery`, `support-sla`. `docs/runbooks/index.md` is the single mapping `alert id | rule file id | severity | owner | runbook | last rehearsed` and is the artifact NFR-08-1 is verified against; `tools/ops/check_alert_contract.sh` ([INFERENCE] path, same family as 11's `check_metrics_contract.sh`) fails when a rule has no `owner`/`runbook` or names a file that does not exist.

**D12 Break-glass rehearsal [DECISION].** 08.2.2-T2 runs 09.10.7's one-shot `nts-admin` job on **staging**: `nts owner recover --email` (sole owner: MFA reset + forced password reset), `nts owner restore --email`, `nts owner resolve --request|--change`; it records operator identity in the job audit, the alert firing on job start, the owner e-mail, and the start-to-working-login time; the sealed offline recovery-code procedure (paper, two copies, one off-site; ≥ 2 owners recommended) is written into `docs/runbooks/owner-recovery.md`; no production rehearsal, no new CLI. Verbs are exactly `recover|restore|resolve` with exit codes 0/2/3, and the one-shot `nts-admin` image still has no owner in plan 11: 09 W3 CR-6 proposes `11.6.4-T1` (S 0.5) `[OPEN]`, which this task depends on (K23).

**D13 SaaS checkpoints [DECISION].** `docs/ops/saas-checkpoints.md`: §0 header + owner sign-off, §1 three rows day 30/60/90 (`metric key | window | target | source | decision rule | status`), §2 the D7 key list it reuses, §3 the reminder rule, §4 revision log. Day-30 targets: `render_success_rate ≥ 95 %`, `deletion_on_time_rate = 100 %`, `margin_percent ≥ 60 %` → open sign-ups wider; day 60: `second_episode_within_30d_rate ≥ 30 %` → start paid acquisition tests; day 90: revenue (7/30-day, `/admin/funding`, 12.8.1-T1) ≥ `infra_cost_30d_usd` (manual: node + storage + telemetry invoice lines, recorded in §1) + provider cost → continue, else revisit pricing/segment. Reminders are an agenda item in 08.1.2's weekly review (countdown to the next checkpoint) plus the owner's own calendar — no job, no template.

## B4. Change requests

| CR | Target | Change (exact old → new) |
|---|---|---|
| 08 CR-6 | `plans/08-90-day-operations.md` §6, §4 (08.1.1, 08.3.1) | §6 replaces `| 08.1.1-T1 | Build KPI dashboard | Ops | M | 09.9.3, 11.6.2, 12.5.2 |` with `| 08.1.1-T1 | KPI read model + \`GET /admin/kpis\` | BE | S | 09.9.1, 09.9.3, 12.5.2 |` + `| 08.1.1-T2 | Admin KPI dashboard page | FE | S | 08.1.1-T1, 10.9.1-T0, 10.9.1-T1 |`; and `| 08.3.1-T1 | Support inbox, tags, FAQ page | Ops/FE | S | 10 |` with `| 08.3.1-T1 | Support intake: alias, tags, SLA times, macros | Ops | XS | 09.10.5 |` + `| 08.3.1-T2 | Help/FAQ page + in-app Help link | FE | XS | 08.3.1-T1, 10.3.1-T2 |`; §4's 08.1.1/08.3.1 bullets gain one line each pointing at Part B D7/D10. Sizes and §11 unchanged (1.25 / 2.0 / 1.0) |
| 08 CR-7 | `plans/00-overview.md` §8 | **withdrawn** — Lead09S adds `GET /admin/kpis?window=7\|30` to the Admin row in **09 W3 CR-5** (2026-09-25); 08 files no duplicate route CR |
| 08 CR-8 | `plans/features/09-backend-api/09.2.6-worker-hangfire/09.2.6-T1-*.md` (`RecurringJobs.cs`, P-signed) + `09.9-platform/09.9.3-T1-*.md` | add `OpsAlertSweepJob` (`OpsAlertsOptions`, 5 min, single-instance) to the recurring list and `ops_alerts_fired_total{rule,severity}` + `ops_alert_sweep_duration_seconds` to 09.9.3-T1's Outputs (Lead09S 2026-09-25: no collision with `AuthFailureSweepJob`, `SettingChangeApplyJob`, `OwnerActionApplyJob`, `SettingSecretPurgeJob`, `TokenHealthJob`; the CR targets the P-signed 09.2.6-T1, not the S wave) |
| 08 CR-9 | `plans/08-90-day-operations.md` §5.2 | table gains a `Source` column: `§5.2` keeps only `security events (D9)`, `OAuth client rejected`, `paid-volume anomaly`, `post-deploy smoke test`, `moderation events`, `failed logins`, `token refresh failures`, `payment webhook failures` + the new `security e-mail failed` row and a note "shared rules: values owned by 11.6.2-T1/D17"; the rows `API 5xx`, `quota`, `render failure`, `queue wait`, `outbox lag`, `disk`, `OpenAI cost`, `runway`, `margin`, `deletion due` point to `11.6.2-T1`/`12.8.x` |
| 08 CR-10 | `plans/features/11-worker-deployment/11.6-operations/11.6.2-T1-telemetry-dashboards-and-alerts.md` | §Design rule entry becomes `id, severity, for, expression | db_check, owner, runbook, dedup_key, window`; add a `db_checks:` section evaluated by `OpsAlertSweepJob` (08.2.1-T1) and the `group_by: [dedup_key]` / `repeat_interval` note; §Outputs adds `tools/ops/check_alert_contract.sh` as a consumer of the rule file |
| 08 CR-11 | `plans/00-overview.md` §6 (`:266`) + 09.2.2 migrations | `users` gains `email_verified_at timestamptz` (no `created_at` is added: the preamble `:262` already grants it to every table — corrected after Lead12's reply, 2026-09-25); add the global index `usage_records (created_at)` for the whole-platform cost window (12 CR-12 adds the tenant-scoped `usage_records (user_id, operation, created_at)`; both land once, in 09.2.2); note "M2, required by 08.1.1-T1 (F10) and by 12.4.2-T1's trial grant" |
| 08 CR-12 | `plans/features/09-backend-api/README.md` S wave + the 09.5/09.10 task headers | pin the ids this slice depends on: `09.9.1-T1` (the `/api/v1/admin` group + `AdminPolicy` own the `/admin/kpis` handler and its `[Audited("kpis_view", Category.Support, Visibility.Internal)]`), `09.10.4-T3` (`ISecurityAlerter` + `SecurityEvent` payload; **T2 is the role hierarchy**), `09.10.5-T1` (support actions, reason + `audit_logs` category `support`), `09.10.7-T1` (verbs/exit codes), and `11.6.4-T1` (`nts-admin` job image, 09 W3 CR-6, `[OPEN]`) |
| 08 CR-13 | `plans/features/10-web-ui/README.md` (S wave) | reserve `/admin/kpis` (08.1.1-T2, inside the 10.9.x admin shell with `roleGuard`) and the public `/{locale}/help` (08.3.1-T2, added to the SD2 prerender list + sitemap, axe-clean via 10.10.1-T1); agent with Lead10S 2026-09-25: 08 appends `{ path: '/admin/kpis', labelKey: 'admin.nav.kpis', roles: ['admin','owner'] }` to `ADMIN_NAV_ITEMS` in `features/admin/nav/admin-nav.ts` (`admin` Transloco scope, vi+en) |
| 08 CR-14 | `plans/features/12-billing-credits/README.md` (S wave) | state that 08 consumes `12.3.3-T2`, `12.4.1-T1`, `12.5.2-T1`, `12.8.3-T1`, `12.8.4-T1` by id and never re-declares their thresholds; 12's alert ids must match the `id`s used in `ops/alerts/rules.yml` |
| 08 CR-16 | P task headers (back-edges so the whole graph is symmetric) | `06.2.3-T3` Blocks: `06.4.2-T2` → `06.4.2-T2, 08.2.2-T1`; `13.3.1-T1` Blocks: `06.4.1-T1, 06.4.3-T1, 11.6.1-T1 (soft)` → `06.4.1-T1, 06.4.3-T1, 11.6.1-T1 (soft), 08.2.2-T1`; `09.9.3-T1` Blocks: `09.9.3-T2, 11.6.2-T1` → `09.9.3-T2, 11.6.2-T1, 08.1.1-T1, 08.2.1-T1`; `11.6.2-T1` Blocks: `08.4.3-T1 (08 CR-5)` → `08.4.3-T1 (08 CR-5), 08.2.1-T1, 08.2.2-T1` (08 CR-10 already extends 11.6.2-T1's rule schema and Outputs) |
| 08 CR-15 | `plans/features/08-90-day-operations/08.5-checkpoints/08.5.1-T1-gate2-checkpoints.md` header | `Blocks` cell `08.4.1-T1` → `08.4.1-T1, 08.5.1-T2` (08.5.1-T2 lands in the sibling folder `08.5.1-saas-checkpoints/`, per `08.5-checkpoints/plan.md:211`) |
| 08 CR-17 | `08.2-alerts-runbooks/08.2.1-T1-…md`, `08.2-alerts-runbooks/plan.md`, this file (K10, F9, §B0) | 11.6.2-T1 now ships **21** rules, not 17 (18–20 billing via 12 CR-8, 21 TikTok via 07 CR-10): the task extends all 21 with `owner`/`runbook` (18–20 `owner = billing`, runbooks `payments-integrity`/`openai-balance`; 21 `owner = ops`, runbook `youtube-quota` — the existing platform-cap runbook, no 15th file), and `auth_failure_counters` joins the read-only table list (`login_failures_spike` reads it) |

## B5. Security, privacy and abuse review (OWASP ASVS L2, 2026-09-25)

| Area | Rule in this slice | Where |
|---|---|---|
| Access control | Every route here is `/admin/*` (role `admin`/`owner`, MFA mandatory, non-admin → 404) or a public static page; no user-facing mutation is added | D7, D10; `09:225,275` |
| Tenant isolation | The only cross-tenant reads are the KPI aggregates, and the DTO returns counts/rates/metric links — never per-user or per-episode rows; the handler lives in `Features/Admin`, the only place `IgnoreQueryFilters` is architecturally allowed | D7, F15; `09:225-226`, `09.2.3-T1/T2` |
| Authentication / impersonation | **No impersonation, view-as-user, token-minting or cookie-swap exists** in this slice; support acts through 09.10.5's audited admin actions with a mandatory reason; content is reachable only inside a moderation/report case | D10, F7; `09:284`, `13:115` |
| Secrets | The rule file holds thresholds and paths only; the alert-channel credential comes from the secret manager (11.2.1-T1); `docs/runbooks/owner-recovery.md` records where the sealed recovery codes are kept, never the codes themselves, and no runbook contains a key, token or password | D8, D11, D12 |
| Input validation | `window` is an allow-list (`7`, `30`) → `400 invalid_window`; the rule file is validated at start-up (unknown severity, missing `owner`, or a missing runbook file is a configuration error that pages `ops_alerts_fired_total{severity="invalid_config"}`); `/help` query parameters are ignored server-side | D7, D8 |
| Logging / audit | Sweep-job logs and alert payloads carry rule id, severity, counts and runbook link only — no story text, no e-mail, no user id (matches 11's D19 S7); every support action writes `audit_logs` with `category = support` and a reason | D8, D10, K13 |
| Privacy / data protection | Tickets and alerts are content-free by construction; the mailbox is provider-hosted with a 180-day retention and no attachments; no new Data Protection purpose or key is introduced; DP purposes and the key ring are unchanged | D10, F13/F14 |
| Abuse / stability | No auto-reply, provider spam filtering on the alias, a static `/help` page (no form/upload), and alert dedup + cooldown so a stuck condition cannot flood the owner's phone or the e-mail path | D8, D10, F16 |
| Residual risk | If 11.2.1-T1 leaves the alert channel unchosen (⚠️ `[OPEN]`), pages have no destination: mitigated by the e-mail fallback rule (`EmailFallbackEnabled`) and by `email_queue_stale`; delivery latency is measured in the 08.2.1-T2 drill, not assumed | F16, `11 README:436` |

## B6. Performance, reliability and money-effect safety

| Concern | Rule |
|---|---|
| KPI query cost | One set-based aggregate per block (`AsNoTracking`), ≤ 10 statements for the whole snapshot, 7/30-day windows bounded by `usage_records (created_at)` (CR-11) and the existing per-table indexes; no N+1, no per-user loop; the only cache is the 60 s in-process memo of F15 — the endpoint is otherwise read-only and idempotent |
| Row locks / concurrency | No lock is taken: the snapshot is read-only and the sweep job is single-instance via the house `[JobPolicy]`. No advisory lock is used (contrast the 09.7.3-T1 `pg_try_advisory_lock` house pattern, which is for writers) |
| Idempotency | The sweep recomputes state from committed rows each run, so a repeated or missed tick is harmless; pages are deduplicated by `dedup_key = <rule>:<window bucket>` with `repeat_interval = OpsAlerts:PageCooldownMinutes` (60) on the backend side — no new dedup table or state |
| Money-effect safety | This slice **never writes** `credit_ledger`, `payments`, `credit_lots`, `publications` or `render_jobs`; the payment-integrity check reads `payment_events.signature_valid`, `payments.status` and `email_messages` only, and the circuit breaker (12.8.3-T1/T2) is untouched — alerts inform, the money paths stay 12's |
| Job fan-out | Exactly one new recurring job, one bounded batch of DB checks (≤ ~20 rules) per tick, 3 send attempts, then the e-mail fallback; a failing check logs and continues, it never aborts the sweep or another job |
| Outbox | Not used: this slice adds no domain event, no subscriber and no `outbox_messages` row, so it cannot add outbox lag (the very thing it alerts on) |
| Escalation path | Page → owner's phone/e-mail via 11's channel; send failure → `ops_alerts_fired_total{severity="send_failed"}` + `security_email_failed`/`email_queue_stale` fallback rows; the fallback is the e-mail configuration active before the change (D9, `09:278`) |
| Test data | All timing-sensitive tests use `FakeClock` (house rule); the sweep's SQL is exercised against seeded rows, never the wall clock |

## B7. Cross-cutting: observability, i18n, accessibility, licences, cost

| Dimension | Position |
|---|---|
| Observability | Two new metrics only (`ops_alerts_fired_total{rule,severity}`, `ops_alert_sweep_duration_seconds`, CR-8), registered through 09.9.3's meter; no new dashboard — 11.6.2-T1 owns the metric boards and the `/admin/kpis` page is the **business** board, never a substitute for them; `docs/runbooks/index.md` is the human interface and the artifact NFR-08-1 is checked against |
| Measurable exit for this slice | Every rule in the rule file resolves to exactly one owner and one existing runbook file, and each S rule has been fired once on staging with the detection latency recorded (`tools/ops/check_alert_contract.sh` + the 08.2.1-T2 drill) |
| i18n (vi + en) | Runbook "user communication" templates in both languages; FAQ entries and support macros in both; `/help` and `/admin/kpis` labels through 10's i18n (vi default); owner-facing e-mails in vi; plan/runbook bodies stay English |
| Accessibility | `/admin/kpis` and `/help` pass 10.10.1-T1's `@axe-core/playwright` WCAG 2.1 AA pass; KPI status is text/token, never colour-only; every value has a table equivalent (no chart-only data); tables have captions and `scope` headers |
| Licences | **Nothing added**: the mailbox is the provider service chosen in 11.2.1-T1, the telemetry backend and rules engine are 11's, the FAQ page is the existing web app, and no AGPL/RPL/non-commercial component enters (00 §5.1, 11 E11/E12) ✅ |
| Cost | No new paid component; the alias is on the existing e-mail plan ⚠️ (confirm alias count/price with the provider at M2 — `[OPEN]`, no price invented); alert volume stays ~30 rules on the backend already costed in 11.6.2-T1; the KPI page adds no infrastructure |
| Reuse (code quality) | Reuses 11's rule file + alert channel, 09.9.3's meter, 09.10.4's `ISecurityAlerter`, 09.10.5's actions, 12.5.2-T1's margin query, 12.8.1-T1's funding overview, 06's `platform_incidents` (referenced, never extended), 00 §6 tables only, and the house `[JobPolicy]`/`TestKit`/ErrorOr conventions; no parallel convention is introduced |

## B8. Feature index and task fan-out

| Folder | Tasks (size) | Writer brief |
|---|---|---|
| `08.1-cadence-kpis/` | 08.1.1-T1 (S), 08.1.1-T2 (S), 08.1.2-T1 (XS) | D7 read model + the Angular page in 10's admin shell + the weekly agenda/money routine; traps: `users.email_verified_at` (CR-11), `ADMIN_NAV_ITEMS`, manual vs `db` metric sources |
| `08.2-alerts-runbooks/` | 08.2.1-T1 (S), 08.2.1-T2 (XS), 08.2.2-T1 (M), 08.2.2-T2 (XS) | D8/D9/D11/D12: rule-file schema + `OpsAlertSweepJob`, security-event routing + drill, the 14 runbooks + index, break-glass rehearsal; traps: threshold ownership (CR-9) and the `11.6.4-T1` `[OPEN]` image |
| `08.3-support-feedback/` | 08.3.1-T1 (XS), 08.3.1-T2 (XS), 08.3.2-T1 (S) | D10: alias/tags/SLA/macros (flagged `no-impersonation`), the `{locale}/help` FAQ page, the feedback loop; traps: no content in tickets, no form on `/help` |
| `08.5.1-saas-checkpoints/` | 08.5.1-T2 (XS) | D13: `docs/ops/saas-checkpoints.md` reusing D7 keys, `/admin/funding` and one manual infra line; trap: no new job or e-mail template for reminders |

One writer agent per folder; every folder's `plan.md` §10 row set must equal the canonical table below (the verifier compares them), and each task file's header row is copied from it verbatim.

### B8.1 Canonical task dependencies

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 08.1.1-T1 | S (0.5) | BE | S · M2 | 09.9.1-T1, 09.9.3-T1, 11.6.2-T1 (soft), 12.5.2-T1, 08.3.1-T1 (soft) | 08.1.1-T2, 08.1.2-T1 (soft), 08.5.1-T2 |
| 08.1.1-T2 | S (0.5) | FE | S · M2 | 08.1.1-T1, 10.9.1-T0, 10.9.1-T1, 10.10.1-T1 (soft) | — |
| 08.1.2-T1 | XS (0.25) | BA/Ops | S · M2 | 08.1.1-T1 (soft) | 08.3.1-T1 |
| 08.2.1-T1 | S (0.5) | BE/Ops | S · M2 | 11.6.2-T1, 09.9.3-T1 | 08.2.1-T2, 08.2.2-T1 |
| 08.2.1-T2 | XS (0.25) | Ops | S · M2 | 08.2.1-T1, 09.10.4-T3 (soft), 11.5.1-T1 (soft) | — |
| 08.2.2-T1 | M (1.0) | Ops/BA | S · M2 | 08.2.1-T1, 06.2.3-T3, 09.10.7-T1, 11.6.2-T1, 12.3.3-T2, 12.8.3-T1, 13.3.1-T1 | 08.2.2-T2, 08.3.1-T1 (soft) |
| 08.2.2-T2 | XS (0.25) | Ops | S · M2 | 08.2.2-T1, 09.10.7-T1, 11.6.4-T1 | — |
| 08.3.1-T1 | XS (0.25) | Ops | S · M2 · flag `no-impersonation` | 09.10.5-T1 (soft), 08.1.2-T1 (soft), 08.2.2-T1 | 08.3.1-T2, 08.3.2-T1, 08.1.1-T1 (soft) |
| 08.3.1-T2 | XS (0.25) | FE | S · M2 | 08.3.1-T1, 10.3.1-T2 (soft) | — |
| 08.3.2-T1 | S (0.5) | BA | S · M2 | 08.3.1-T1 | — |
| 08.5.1-T2 | XS (0.25) | BA | S · M3 | 08.5.1-T1, 08.1.1-T1 (soft) | — |

Cross-plan edges appear only in `Depends on`; the back-edges this slice wants on other plans' headers are 08 CR-8…CR-15 (B4). In-slice hard edges form no cycle: 08.1.1-T1→08.1.1-T2, 08.2.1-T1→{08.2.1-T2, 08.2.2-T1}, 08.2.2-T1→08.2.2-T2, 08.3.1-T1→{08.3.1-T2, 08.3.2-T1}, plus 08.5.1-T1→08.5.1-T2 (P header via CR-15). `(soft)` marks an edge that may start against a contract-true fake.

## B9. Re-estimate

| Epic | High-level S (`08-90-day-operations.md` §11) | Detailed here | Why |
|---|---|---|---|
| 08.1 Cadence + KPIs | 1.25 | 1.25 | 08.1.1-T1 split into BE (0.5) + FE (0.5), 08.1.2-T1 0.25 — sum unchanged |
| 08.2 Alerts + runbooks | 2.0 | 2.0 | 08.2.1-T1/T2 0.75, 08.2.2-T1/T2 1.25 — sum unchanged |
| 08.3 Support + feedback | 1.0 | 1.0 | 08.3.1-T1 split into Ops (0.25) + FE (0.25), 08.3.2-T1 0.5 — sum unchanged |
| 08.5 Checkpoints (S share) | 0.25 | 0.25 | 08.5.1-T2 only; P 08.5.1-T1 stays 0.25 in `08.5-checkpoints/` |
| **Total** | **4.5** (00 §4.1 S, buffered 5.4) | **4.5** (buffered 5.4) | no change → **no CR to 00 §4.1/§13 and no plan-08 total change** (7.0 d, buffered 8.4). 08 CR-3 already moved S from 4.25 to 4.5 for 08.5.1-T2 |

## B10. Owner decisions and TL rulings

| # | Decision | Outcome |
|---|---|---|
| O2 | The two task splits (08.1.1-T1→T1/T2, 08.3.1-T1→T1/T2), the `/admin/kpis` route + DTO (D7), the **no-impersonation** ruling (D10), the alert-mechanism merge (D8/D9) and "calendar reminders = weekly agenda item" (D13) | Accepted **under the owner's standing approval (2026-09-25)**; sizes and the 4.5 d total are unchanged, so nothing needs a new estimate decision |
| O3 | CR-11's schema addition (`users.email_verified_at`, global index `usage_records (created_at)`) | Accepted under the same standing approval; applies at M2 with 09.2.2's migration; corrected after Lead12's reply (00 §6 preamble `:262` already grants `created_at` everywhere) |
| R1 | Threshold ownership | 11.6.2-T1's loaded values are authoritative for shared rules; 08 §5.2 keeps only its own rules and gains a `Source` column (CR-9) |
| R2 | Alert mechanism for DB-derivable conditions | `OpsAlertSweepJob` + a `db_checks:` section in 11's rule file; no new table, no new event, no change to 12's thresholds (D8) |
| R3 | Support tooling | No impersonation, no view-as-user, no second mailbox store; 09.10.5's audited actions are the only staff path (D10, F7) |
| R4 | Sibling contracts | Agreed 2026-09-25 with Lead09S (`/api/v1/admin` group, `09.10.4-T3`, template set, `RecurringJobs.cs` CR, `11.6.4-T1` `[OPEN]`), Lead10S (`10.9.1-T0` + `10.9.1-T1`, `ADMIN_NAV_ITEMS`, `{locale}/help` in the SD2 prerender list) and Lead12 (`usage_records` index, no rule duplication, `billing_*` metrics) |
| O-4 | `SecurityEvent.Kind` frozen values (09 W3 ruling): `security.settings_changed`, `security.role_changed`, `security.mfa_changed`, `security.recovery_code_used`, `security.owner_login_new_ip`, `security.grant_above_cap`, `security.settings_rolled_back`, `security.owner_action_pending` (ninth kind, new), `security.break_glass_recovery` — never the `account.*` audit action names; nine metric rules (`sec_*` ids, K25) | Accepted under the owner's standing approval (2026-09-25) |
| R5 | `08.2.1-T2`'s security-alert payload citation corrected: signed `ISecurityAlerter.AlertAsync(SecurityEvent evt, ct)` / `SecurityEvent(string Kind, Guid? SubjectUserId, string? Detail)` (09.3-auth/09.10.4-T1:53-54) — never the stale `SecurityAlert(Event, Severity, OccurredAt, UserId, DataJson)` shape; severity/template routing is 09.10.4-T3's `SecurityEventCatalog` lookup, not a payload field | boundary fix, 2026-09-25 |

## B11. Definition-of-Ready review

- Script (`verify_plan.py … 08-90-day-operations "### B8.1"`, 2026-09-25): 11 S tasks, 4.5 raw d = §B9 = `00-overview.md` §4.1's 08 S column; headers equal §B8.1; 0 ERR, 0 cycles.
- Review (RevS07, verified on disk): 08.2.1-T1 undercounted 11.6.2-T1's rules (17 assumed vs 21 actual — billing rules 18–20 via 12 CR-8, TikTok rule 21 via 07 CR-10) and `login_failures_spike` had no declared table — both fixed (08 CR-17: all 21 rules carry owner/runbook; `auth_failure_counters` added to the read-only Inputs).

| DoR item | Status |
|---|---|
| User value, acceptance, estimate | ✅ |
| Dependencies known | ✅ §B8.1 (KPIs consume 11.6.2-T1, 12's alerts, 06's `platform_incidents`) |
| Contract linked | ✅ Part B D-n, K-facts |
| Reviewed | ✅ TL + review pass |

**Verdict: ready for implementation with conditions** (accepted 2026-09-25 under the owner's standing approval). **Conditions:** (1) alert rule owners/runbooks for rules 18–21 are the assignments recorded in 08 CR-17 — the billing owner confirms them at M2; (2) the KPI queries are re-validated against the real 00 §6 columns at implementation (the P-slice review already corrected `audit_logs.occurred_at` → `created_at`).
