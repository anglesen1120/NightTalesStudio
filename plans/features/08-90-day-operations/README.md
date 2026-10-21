# 08 — Operations, P slice (flagship channel + checkpoints): Tech Lead review, shared decisions and feature index

> Reviews: `../../08-90-day-operations.md` — **P slice only**: epics 08.4 (flagship channel) and 08.5 (checkpoints) for the **Channel phase** (00 §4: run the owner's channel on autopilot for ≥ 12 weeks after the YouTube audit allows public uploads, then Gate 2). 08.1–08.3 (KPI dashboard, alert rules, runbooks, support) and the SaaS 30/60/90 checkpoints are dropped with phase S (DEC 10.14). Reviewed 2026-09-25 against DECISIONS §6/§10.12, 00 §4, 02.8.x, 06 (YR rulings), 11.6.2-T1 and 13.4.2-T1.
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
| K9 | Pause triggers incl. gate-fail streaks, spend caps, provider funds, channel state, veto/quota/rejection; a takedown is an owner-initiated `owner_paused` pause (no report intake in P — DEC 10.14) and is handled by 08.4.4-T1 | 02.8.3-T4 ✅ |

## 2. Findings

| # | Sev | Finding | Resolution |
|---|---|---|---|
| F1 | H | 08.4.2 describes manual weekly production (3 briefs, ≥ 15 min human edit per script); in P fiction runs on autopilot (K2) | D2: the weekly routine operates the autopilot (veto review, weekly sample audit of ≥ 1 published video + its script, time log per touch) — manual editing only for true crime and when a gate fails |
| F2 | H | 08.5.1's day-30/60/90 criteria are SaaS metrics (sign-ups, activation, revenue); P has no users | D4: 08.5.1-T1 = Gate 2 criteria + week-4/8/12 channel checkpoints; the SaaS 30/60/90 checkpoints are dropped (DEC 10.14) |
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
| 08 CR-1 | `plans/08-90-day-operations.md` §4/§6/§11 | 08.4.2 wording → autopilot routine (F1); 08.5.1 becomes the P task (Gate 2 + channel checkpoints) and the SaaS 30/60/90 half is dropped (DEC 10.14, F2); 08.4.1 cadence/tier per D1 (F3) |
| 08 CR-2 | `DECISIONS.md` §10.12 | the `[OPEN]` Gate 2 criteria line points to `docs/ops/gate-2.md` drafted by 08.5.1-T1 and signed before the Channel phase |
| 08 CR-3 | `plans/00-overview.md` §4.1/§13 | 08: P 2.5 unchanged; phase S dropped (DEC 10.14), so no S row remains |
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
| 08.5.1-T1 | XS (0.25) | BA | P · Channel phase | — | 08.4.1-T1 |
| 08.4.1-T1 | S (0.5) | BA/Ops | P · Channel phase | 08.5.1-T1, 02.8.3-T2, 06.1.2-T2, 13.4.2-T1 (soft) | 08.4.2-T1 |
| 08.4.2-T1 | M (1.0) | BA/Ops | P · Channel phase (recurring; estimate covers setup) | 08.4.1-T1, 02.8.3-T4, 06.2.2-T3 | 08.4.3-T1 |
| 08.4.3-T1 | S (0.5) | BA | P · Channel phase | 08.4.2-T1, 11.6.2-T1 | — |
| 08.4.4-T1 | XS (0.25) | BA/Ops | P · Channel phase | 02.8.2-T2, 02.8.3-T4 (soft) | — |

## 9. Re-estimate

| Area | High-level P | Detailed | Why |
|---|---|---|---|
| 08.4 + 08.5 | 2.5 | 2.5 | unchanged (phase S dropped by DEC 10.14) |
| Plan 08 | 2.5 | **2.5** | P only: the 4.5 d S slice is dropped (DEC 10.14); buffered ×1.2 = 3.0 |

## 10. Owner decisions and TL rulings

| # | Decision | Outcome |
|---|---|---|
| O1 | Flagship defaults (D1) and the proposed Gate 2 criteria (D4) | Accepted as proposals under the standing approval; the owner signs the final criteria document in 08.5.1-T1 (DECISIONS §10.12 requires the owner to set them) |

## 11. Definition-of-Ready review (2026-09-25)

- Script: 2 `plan.md` files with sections 1–11 (headings normalised by the TL), 5 task files with header + 9 headings, headers equal §8.1, total 2.5 d = §9 = `08-90-day-operations.md` P rows; graph 0 cycles after 08 CR-4 (the 02.8.3-T4 ↔ 08.4.4-T1 edge is soft on both sides).
- Review pass (6 findings: two SQL column errors, `consent_text_version` vs authorization hash, takedown mechanics for disabled/paused/uploading rows) verified and fixed by the writer; checked on disk (Q7 uses `created_at`, Q1 uses `publish_at` with the 06 CR-1 note, runbook state table).
- CRs 08 CR-1…CR-5 applied (`08-90-day-operations.md`, DECISIONS §10.12, 00 §4.1/§13, 02.8.3-T4 soft edge, upstream back-edges).

**Verdict: ready for implementation with conditions** (accepted 2026-09-25). **Conditions:** (1) the owner signs `docs/ops/gate-2.md` (08.5.1-T1) with real numbers before public autopilot — the proposed defaults are not decisions; (2) the Channel phase's 12 counted weeks start only after the YouTube audit allows public uploads; (3) cost thresholds are filled once 11.6.3-T1 and 02's metering give a measured cost per video.
