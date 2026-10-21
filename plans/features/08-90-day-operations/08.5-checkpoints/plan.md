# 08.5 — Checkpoints (Channel phase, P slice): Gate 2 criteria + week-4/8/12 checkpoints — detailed feature plan

> Refines: `../../08-90-day-operations.md` §4 (08.5.1, :76-78), §6, §11; `../../00-overview.md` §4 row "Channel phase" + Gate 2 (`plans/00-overview.md:68`), §10.1; `../../DECISIONS.md` §10.12 (`:330-338`, the `[OPEN]` Gate 2 criteria line `:338`); contract `../README.md` — facts K1–K9 (§1), finding F2 (§2), decisions D1/D2/D3/D4/D5 (§3), CR-2 (§4), gate Channel-phase-start (§5–7), canonical dependencies §8.1 (:60), re-estimate §9, owner decision O1 (§10).
> Level: detailed feature plan → review (DoR) → task. **Boundary: this folder produces documents, templates, routines and SQL snippets only — no application code, no route, no DTO, no table/column/index, no migration, no config key, no CLI command, no `tools/` script.**
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**Covered story:** **08.5.1-T1** (XS, 0.25 d, BA) — the **P slice** of epic 08.5. The high-level 08.5.1 ("30/60/90-day go/no-go", `plans/08-90-day-operations.md:76-78`) was written for the SaaS (sign-ups, activation, revenue); in P there are no users, so per F2 the SaaS checkpoints became **08.5.1-T2** (XS, phase S) — **deleted outright by DEC 10.14** — and 08.5.1-T1 became *Gate 2 criteria + week-4/8/12 Channel-phase checkpoints* (`../README.md:14` F2, `:19` D4).

**In scope.**
- Drafting `docs/ops/gate-2.md` — the owner's **Gate 2 criteria** (proposed defaults for the owner to accept or change) plus the **week-4 / week-8 / week-12 checkpoint questions** (continue / adjust settings / stop) — and taking it to the owner's sign-off before public autopilot is switched on (K3, F4, D4).
- The checkpoint *questions and the fields they read*, the review routine (who fills what, from which existing source), and the dry run that proves the routine is executable.

**Out of scope.**
- The **SaaS** 30/60/90 checkpoints (sign-ups, activation ≥ 40 %, 2nd-episode ≥ 30 %, margin ≥ 60 %, revenue ≥ infra+tooling) were 08.5.1-T2, which is **deleted by DEC 10.14**: the product is free and personal, so there are no sign-ups and no revenue to check.
- The flagship **setup**, **calendar**, **weekly routine**, **KPI sheet**, `queries.sql` and **takedown runbook** — owned by 08.4.1-T1…08.4.4-T1 (`../README.md:18`, D1–D3). This task *reads* their outputs and *names the fields* it needs; it never creates or edits them.
- Any new alert, dashboard or metric: P uses 11.6.2-T1's rules (K8, D5).
- Any product surface change: the only P product surfaces this plan touches are existing screens (§6) — the criteria document is a repository file plus a sign-off act, not a feature.
- Gate 2 **execution** as a go/no-go gate on a business (the former SaaS decision, M2 planning): deleted by DEC 10.14 — this task still delivers the document and the channel checkpoints; the continue/adjust/stop call is the owner's (§11).

**Refines:** `plans/08-90-day-operations.md` §4 (08.5.1), §6 (KPI table's Flagship row), §11 epic row for 08.5; `plans/00-overview.md` §4 (Channel phase, Gate 2 [OPEN]); `DECISIONS.md` §10.12 (personal-first, Gate 2 inputs).

## 2. Confirmed facts

| Id | Fact | Source (verified) |
|---|---|---|
| K1 | Channel phase = run the flagship channel on autopilot **≥ 12 weeks after the YouTube audit allows public uploads**; **Gate 2** is decided on channel results, **measured cost per video** and **time per episode** (the SaaS build go/no-go that 00 §4 once attached to it was dropped with phase S, DEC 10.14) | ✅ `plans/00-overview.md:68` (row "Channel phase"), `plans/00-overview.md:469` (critical path: … public autopilot → Channel phase (≥ 12 weeks) → Gate 2); `plans/08-90-day-operations.md:76-78` |
| K2 | In P, fiction is **auto-approved by gates**; true crime waits for the owner; variety guard; **per-channel daily cap default 1**; **veto window default 12 h** (2..48 h allowed) | ✅ `DECISIONS.md:332`; `../README.md:9` K2; settings schema `02.8.3-cadence-publishing/plan.md:55-63` |
| K3 | **Gate 2 criteria are `[OPEN]`** and must be **set by the owner before the Channel phase starts**; drafted with proposed defaults in `docs/ops/gate-2.md` by 08.5.1-T1, signed before public autopilot | ✅ `DECISIONS.md:338` (the `[OPEN]` line); `../README.md:10` K3, `:19` D4 |
| K4 | YPP thresholds: 1,000 subscribers + 4,000 watch hours (12 months) or 10M Shorts views (90 days); **mass-produced content is not monetizable** | ✅ `DECISIONS.md:145` (§6 platform rules); `plans/08-90-day-operations.md:72` (08.4.3 bullet, "vs YPP thresholds ✅") |
| K5 | **No YouTube analytics stored via the API** (stored-data duties); channel KPIs come from a **manual Studio export** | ✅ `plans/08-90-day-operations.md:71-72` (08.4.3 [DECISION]); `../README.md:11` K5 |
| K6 | Autopilot settings (cadence days/time/zone, daily cap 1..5, buffer, privacy, veto 2..48 h, pre-audit mode) + the **versioned series authorization** with re-auth and legal acceptance | ✅ `02.8.3-cadence-publishing/plan.md:51-71` (§3.1 schema), `:28` (D17); routes `plans/00-overview.md:361`; authorization rules `10.4-series-autopilot/plan.md:59-60` (B11/B12) |
| K7 | **Pre-audit mode** keeps uploads private until `YouTube:AuditStatus = approved`; outcome `published_private_locked`; the channel cannot grow while private | ✅ `../../06-youtube-posting/README.md` D11/YR2/YR20; `plans/00-overview.md:95`; run outcome enum `plans/00-overview.md:278` |
| K8 | P alert rules already exist: quota > 80 % (warn), autopilot pause / run stuck > 60 min (page), gate-fail share > 50 %/24 h (warn), render-failure share > 20 %/24 h (page), open platform incident (page), deletion due < 24 h (page) — rules 5, 10, 11, 12, 16, 17 | ✅ `plans/features/11-worker-deployment/11.6-operations/11.6.2-T1-telemetry-dashboards-and-alerts.md:34-50` |
| K9 | Pause triggers: reports, gate-fail streaks, spend caps (`Budget:MaxPlatformCostUsdPerDay = $3`, `…PerMonth = $40`); owner is e-mailed with the already-scheduled videos and Studio links; resume is an owner action | ✅ `02.8.3-cadence-publishing/plan.md:29` (D1), `../README.md:12` K9 |
| B1 | **Cost/video has no single published figure.** Plan 12 computes cost from measurements (`usage_records`), not a target number; the worked example (Standard, 8 min ≈ **$0.708** measured) is explicitly *illustrative* ⚠️ and its parameters are marked ⚠️ | ✅ DECISIONS §10.14 (plan 12 deleted, git history keeps it); live metering is 02.2.4-T1's `usage_records`, `plans/00-overview.md:294` (`usage_records` columns incl. `cost_usd`, `gpu_seconds`, `estimated`, `operation`) |
| B2 | The former price invariant (**≥ 60 % gross margin per episode**) needed revenue and is **deleted with plan 12** (DEC 10.14); the owner's channel earns nothing, so only **measured cost per episode** is a criterion | ✅ DECISIONS §10.14; the criterion is C1 cost, never margin |
| B3 | Render time is measurable: `render_duration_seconds` by stage/outcome and render success share are on the 11 board; the machine metric is `usage_records.gpu_seconds` | ✅ `11.6.2-T1-telemetry-dashboards-and-alerts.md:30` (boards), `plans/00-overview.md:294` |
| B4 | Gate verdicts per run are persisted: `autopilot_runs.gate_results` jsonb, `outcome ∈ {published, published_private_locked, waiting_owner, gate_failed, cancelled_by_owner, awaiting_authorization, paused, failed}`, `pause_reason` | ✅ `plans/00-overview.md:278`; counters `autopilot_gate_total{result="fail"}` / `autopilot_pause_total` (`11.6.2-T1:44-45`) |

**Open questions (escalated, not invented here).**
- **[OPEN]** The **numeric** Gate 2 criteria (a cost ceiling, a touch-time ceiling, the publish-success share) are the owner's to set (K3). This plan supplies *proposed defaults and the measurement recipe*; the signed numbers live in `docs/ops/gate-2.md` (§4 UC-1) and O1 (`../README.md:70`).
- **[OPEN]** Whether the Channel phase counts weeks in which **no** slot was published (a pause) — proposed default: **at least 12 weeks with ≥ 90 % of planned slots published**; the extra weeks a pause costs are *not* counted (rationale: K1 measures channel results, and a paused channel produces none).
- **[OPEN]** YPP eligibility in P is unverified ⚠️ for an autopilot channel (K4 "mass-produced content is not monetizable"); the YPP thresholds are therefore **tracked, not required** (D4).
- **[OPEN]** Google's YouTube audit wait is unknown ⚠️ (`plans/00-overview.md:480`); the public-autopilot countdown cannot start until `AuditStatus = approved`, so the calendar is relative to approval, not to the document's sign-off date.

## 3. Domain and data

Documents and data sources.

All paths are repository files owned as marked; this task **creates only** `docs/ops/gate-2.md`.

| Path | Owner | Role for the checkpoints |
|---|---|---|
| `docs/ops/gate-2.md` | **this task (08.5.1-T1)** | The deliverable: §0 header + sign-off, §1 Gate 2 criteria (owner-accepted), §2 week-4/8/12 checkpoint questions and their decision rule, §3 the fields each checkpoint reads + where they come from, §4 the audit-approval precondition and the 12-week clock rule, §5 revision log. |
| `docs/ops/flagship/calendar.md` | 08.4.1-T1 | The 12 weekly rows (week, planned slots, theme notes, risk notes) — the denominator for "planned slots" (`../README.md:17` D1). |
| `docs/ops/flagship/weekly/YYYY-WW.md` | 08.4.2-T1 | Veto notices seen/edited/cancelled; the one sampled video + its script with 02-rubric scores; true-crime approvals; gate failures and pauses with causes; incidents; **owner touch time per episode (minutes)**; decisions (D2). Checkpoint §3 reads this log, never re-derives it. |
| `docs/ops/flagship/kpi.md` | 08.4.3-T1 | One row per week: Studio export (views, CTR, average view duration, subscribers, watch hours — manual, K5) + platform (videos published, render success share, p95 render time, cost/episode, gate pass share, pauses) + owner touch time/episode (D3). |
| `docs/ops/flagship/queries.sql` | 08.4.3-T1 | The read-only snippets that produce cost/episode and render figures; **this task does not write it** — §3 records which of its outputs each checkpoint reads (D3). |
| `DECISIONS.md` §10.12 | project | Authority for Gate 2's existence and its three inputs (results, cost/video, time/episode) — `DECISIONS.md:330-338`. |

**Data sources behind the fields** (no new storage, no new route, no API fetch from YouTube):

| Field | Source | Exactness |
|---|---|---|
| Views, CTR, average view duration, subscribers, watch hours | Manual YouTube Studio export, transcribed into `kpi.md: ` | K5 — manual by [DECISION], never via the YouTube Analytics API |
| Videos published (public / private-locked), planned slots published | `publications.status` + `autopilot_runs.outcome` (`plans/00-overview.md:278`, `:291`), transcribed weekly | ✅ |
| Gate pass share, pause reasons | `autopilot_runs.gate_results` / `pause_reason`; board counters `autopilot_gate_total{result="fail"}`, `autopilot_pause_total` (`11.6.2-T1:44-45`) | ✅ |
| Cost/episode (OpenAI + GPU) | `usage_records` — `episode_id`, `operation`, `cost_usd`, `gpu_seconds`, `estimated`, `price_version` (`plans/00-overview.md:294`) via 08.4.3-T1's `queries.sql` | ✅ tables; the *ceiling* is the owner's number [OPEN] |
| Render duration p95 / render success share | `render_duration_seconds`, `render_jobs` (11.6.2-T1:30) | ✅ |
| Owner touch time/episode | `weekly/YYYY-WW.md` minutes column, divided by that week's episodes (D2/D3) | ✅ |
| Policy strikes / channel restrictions | YouTube Studio (manual) + any platform incident row | K5; manual |
| Audit status | `YouTube:AuditStatus` (`pending|approved|rejected`) | ✅ (`../README.md:11` K7) |

## 4. Use cases

Routines and use cases.

BA deliverable → "use cases" are **document routines**, each with a trigger, the actor, the fields consulted and the observable result. No command, no DTO, no endpoint.

### UC-1 — Draft and sign the Gate 2 criteria (before public autopilot) — the task's core
**Actor:** owner (author: this task). **Trigger:** 08.4.1-T1's exit needs the signed document (F4: "08.4.1-T1's exit requires the signed criteria document (08.5.1-T1 drafts it first)").
**Routine:** author the document from the proposed-default table below → owner reviews each row, changes any number, and signs §0 → the signed path/date is recorded in 08.4.1-T1's setup notes and in `DECISIONS.md` §10.12's `[OPEN]` line is closed by 08 CR-2.
**Observable:** `docs/ops/gate-2.md` §0 carries the owner's sign-off (name + date) and every criterion row has an owner-accepted value; no row is left as "proposed".

**Proposed defaults (owner may accept or change; every value has a measurement source, none is invented).**

| # | Criterion (after 12 counted public weeks) | Proposed default | Measurement source | Why this default |
|---|---|---|---|---|
| C1 | **Measured cost per episode** (OpenAI + GPU) | Owner sets a **ceiling**; proposed method: ceiling = the mean measured cost/episode of the first 4 public weeks × 1.25, expressed in VND and in USD at the top-up FX rate | `usage_records` (`cost_usd`, `gpu_seconds`, `operation`, `estimated`) via `queries.sql` (D3) | B1: there is **no** published cost target: the deleted plan 12 computed cost from measurements (`usage_records`) and its $0.708 worked example was illustrative ⚠️ only, so the owner's ceiling is derived from the Channel phase's own first-month data (DEC 10.14) |
| C2 | **Owner touch time per episode** | ≤ **20 min** average over the phase | `weekly/YYYY-WW.md` minutes ÷ episodes that week (D2/D3) | D4's proposed default; matches the 45 min/week time box at the default cadence of 3 slots/week (D1, `../README.md:17`) |
| C3 | **Planned slots published without manual rescue** | ≥ **90 %** | `autopilot_runs.outcome` + `publications.status` vs `calendar.md` planned slots | D4's proposed default; the complement (≤ 10 %) is the measured "how often did I have to intervene" |
| C4 | **Policy standing** | **Zero** YouTube policy strikes; no channel restriction | Studio (manual) + platform incident rows | K4 (mass-produced risk); a strike is a stop-signal, not a tunable |
| C5 | **Quality of the sampled output** | Every week's sampled video (D2) has a 02-rubric score ≥ the 02 gate threshold in force, and no gate-fail streak occurred | `weekly/YYYY-WW.md` rubric scores; `autopilot_runs.gate_results` | Ties Gate 2 to substance, not just uptime; the threshold is 02's, not invented here |
| C6 | **Channel trend** (recorded, not pass/fail) | Subscribers + watch hours + views + CTR + AVD recorded weekly; distance to K4's YPP thresholds stated | Studio export in `kpi.md` | K1 says "channel results" inform Gate 2; K4's thresholds are tracked, not required (D4); YPP eligibility for an autopilot channel is unverified ⚠️ |
| C7 | **Automation health** | Render success share ≥ 95 %; no unresolved platform incident; quota never exhausted into a skipped slot | `render_duration_seconds` / render outcomes; incident rows; `youtube_quota_used` | Mirrors 11.6.2-T1's alert lines (K8) so a checkpoint never asserts something the alerts would have paged |
| — | **Preconditions (not criteria)** | `YouTube:AuditStatus = approved`; series authorization accepted; takedown runbook rehearsed once | K7; 13.4.2-T1; 08.4.4-T1 | These say *whether the phase may run*, not *whether it succeeded* (gate Channel-phase-start, `../README.md:22`) |

### UC-2 — Week-4 checkpoint (adjust-early)
**Actor:** owner, 30 min. **Trigger:** 4 counted public weeks completed. **Routine:** read `kpi.md`'s four rows + `weekly/*.md`; answer the six questions of §2 of the document (below); pick exactly one outcome — **continue** / **adjust settings** / **stop**. **Adjust** names which autopilot settings change (cadence days-of-week, tier `standard`→`long` only with cost data, veto window within 2..48 h, tone/banned themes) and records the new authorization obligation (a covered-field change revokes the authorization — `10.4-series-autopilot/plan.md:59` B11, so re-accept is required). **Observable:** a dated entry under §2 of `docs/ops/gate-2.md` with the outcome and, if "adjust", the settings diff.

### UC-3 — Week-8 checkpoint (mid-phase)
Same routine as UC-2, plus: recompute C1's ceiling from 8 weeks of data and state whether the measured cost is trending toward or away from it; state the touch-time average and its worst week; list every pause with its cause (from `pause_reason`, K9).

### UC-4 — Week-12 checkpoint → **Gate 2 recommendation**
Same routine, and: evaluate **every** criterion C1–C7 against the owner's signed numbers, produce the **Gate 2 recommendation** (continue the Channel phase for another 4-week block with named changes / adjust settings / stop), and hand it to the owner for the actual decision. **Observable:** §2's week-12 entry with a per-criterion verdict table (`met` / `missed` / `not measurable`) and the recommendation; the owner's decision is recorded there. There is no "build the SaaS" outcome — phase S and its plans were deleted by DEC 10.14.

### UC-5 — Late or paused phase
If the audit approval, an autopilot pause or a policy event interrupts the phase, the checkpoint moves to the next counted week (§4 of the document defines "counted week": ≥ 90 % of planned slots published, no open pause at week end). No checkpoint is ever evaluated on a partial week. **Observable:** §4 of the document states the clock rule; §2's entries always name the counted-week index, not a calendar date.

## 5. API and events

Inputs read.

The document and the checkpoint routine read **only** existing sources; no route is added and no query is invented outside 08.4.3-T1's file.

**Product routes consulted when *recording* an outcome (read-only, all exist):**

| Need | Route / screen | Source |
|---|---|---|
| Autopilot settings + authorization state | `GET /series/{id}/autopilot` (200 `AutopilotDto`), `PUT /series/{id}/autopilot` (If-Match), `POST/DELETE /series/{id}/autopilot/authorization`, `POST …/pause`, `POST …/resume` | `plans/00-overview.md:361`; DTO/enable-rule detail `10.4-series-autopilot/plan.md:57-60` |
| Run log, gate results, pause reason | `GET /series/{id}/autopilot/runs` (cursor `(slot_at DESC, id DESC)`, `limit ≤ 100`) | `plans/00-overview.md:361`; `10.4-series-autopilot/plan.md:57` |
| Publications per episode (public vs private-locked, consent, veto) | `GET /episodes/{id}/publications` | `plans/00-overview.md:364` |
| Legal acceptance in force (owner, before first public upload) | `GET /me/legal` / `POST /me/legal/accept` | `plans/00-overview.md:360` |

**SQL (read-only, on existing tables — the snippet lives in 08.4.3-T1's `queries.sql`, D3):**

- **Cost per episode** — group `usage_records` by `episode_id` over `operation ∈ {story, story_review, story_revision, fact_sheet, image, tts, render}`; sum `cost_usd`; sum `gpu_seconds`; flag `bool_or(estimated)` so an estimate is visible; keep `price_version` for the run (columns ✅ `plans/00-overview.md:294`). Ceiling comparison uses the owner's C1 number (UC-1).
- **Per-episode cost for one episode** — the same predicate with `episode_id = :id` (used by a weekly sample audit, D2).
- **Render figures** — `render_duration_seconds` p95 and render outcome share (11.6.2-T1:30); `usage_records.gpu_seconds` per episode as the machine-level cross-check.
- **Autopilot effectiveness** — count of `autopilot_runs` by `outcome`/`pause_reason` over a window (columns ✅ `plans/00-overview.md:278`); each run is one planned slot.
- No `UPDATE`, no `INSERT`, no DDL, no new table; `usage_records` is not modified by this task.

**Manual inputs:** the YouTube Studio export (views, CTR, average view duration, subscribers, watch hours) — K5 forbids storing YouTube analytics via the API; the export is transcribed into `kpi.md` by 08.4.3-T1.

## 6. UI

Product screens used.

The checkpoint routine uses **existing** screens; the criteria document is not a UI. Screens named from plan 10's P wave (10.4-series-autopilot, 10.7.2-publish):

| Screen | Used for | Task |
|---|---|---|
| **Autopilot tab** (series → Autopilot): settings form, on/off, versioned authorization dialog with re-auth, pause/resume, topic lock, next-slots preview, pause banner | UC-2/3 "adjust settings"; confirming the authorization is current before a counted week | `10.4-series-autopilot/plan.md:15` (10.4.3-T1) |
| **Run log + veto list** (autopilot run log, calibration, veto rows; 30 s visibility-gated poll, 1 min veto countdown) | reading outcomes, gate verdicts, veto decisions for the weekly log and the checkpoint evidence | `10.4-series-autopilot/plan.md:57` (B9), `10.10.2-journeys/plan.md:35` C14 (10.4.3-T2) |
| **Episodes editor** — render/approve/reject, video status | checking a `long` tier upgrade and the sampled video's state | `10.5.1-episodes-editor` |
| **Publish screen** (10.7.2) — publication status, privacy, upload state, quota wait | public vs private-locked counting (C3/K7) | `10.7.2-publish` |
| **Channels page** (10.7.1) — connection status, re-auth prompts | precondition: an active channel connection | `10.7.1-channels` |
| **Legal acceptance** (`/me/legal`, `POST /me/legal/accept`) | precondition: acceptance before the first public upload | `plans/00-overview.md:360`; 13.2.1-T1 |

**Not used:** the YouTube Analytics API (forbidden, K5); Hangfire dashboard, log viewers and Grafana/OTel boards beyond reading an alert's firing state (11.6.2-T1's boards are 11's; the checkpoint cites the alert, not the board).

**Accessibility/copy:** the only written artifact is the repository document; no user-facing copy, no new copy key, no i18n change. Documents are written in English (project convention); the owner's sign-off note may be in Vietnamese. No a11y surface is introduced.

## 7. Jobs and integrations

Cadence and timers.

| Event | When | Duration | Evidence produced |
|---|---|---|---|
| **Gate 2 criteria drafted and signed** | **Before** public autopilot; required by 08.4.1-T1's exit | ≤ 60 min (draft + owner review) | `docs/ops/gate-2.md` §0 signed; sign-off note in 08.4.1-T1's setup notes |
| **Weekly routine** | weekly, owner-chosen local day (D2) | 45 min/week time box | `docs/ops/flagship/weekly/YYYY-WW.md`; a `kpi.md` row (08.4.2-T1 / 08.4.3-T1) |
| **Week-4 checkpoint** | after the 4th counted public week | 30 min | §2 entry (UC-2) |
| **Week-8 checkpoint** | after the 8th counted public week | 30 min | §2 entry (UC-3) |
| **Week-12 checkpoint + Gate 2 recommendation** | after the 12th counted public week | 60 min | §2 entry with the per-criterion verdict table + recommendation (UC-4) |
| **Clock start** | the week `YouTube:AuditStatus = approved` **and** the first public upload lands | — | §4 of the document states the rule; the start week is recorded once |
| **Counted week** | ≥ 90 % of that week's planned slots published, no open pause at week end | — | the counter in §2's heading; a partial/paused week is skipped, not deleted |

Timers are **human** timers recorded in documents; **no** background job, recurring job or timer is added by this task. (The only product timers in play are 02.8.3's `AutopilotPlanJob` tick and the veto window, both owned upstream — `02.8.3-cadence-publishing/plan.md:28` D17.)

## 8. Security and privacy

| Concern | Handling |
|---|---|
| Tenant / authorization | Not applicable — no endpoint and no state change. The document names the owner's series by id/slug only; the signed criterion values are the owner's business figures and stay in `docs/ops/gate-2.md`. |
| Secrets | None read or written. `YouTube:AuditStatus` is read as a status word (`pending|approved|rejected`), never the API client secret or OAuth tokens. Studio exports contain channel data only (no personal data of third parties); the export file itself is **not** committed — only the transcribed figures into `kpi.md` (D3). |
| Personal data | The document and the weekly logs contain no user personal data (the owner is the only actor). They are therefore **outside** 13.3.1-T1's deletion scope — 13's deletion duties cover product users' data (`13.3.1-T1`). |
| Redaction | Cost figures, subscriber counts and watch hours are commercially sensitive but not secret; still, the document records the **secret's name only** where a settings reference is needed (`YouTube:AuditStatus`), never a value of a credential. |
| Audit trail | The document's §5 revision log is the change history: every criteria change after sign-off appends a revision (date, changed criterion, reason, owner initials) rather than editing the signed text — so the signed baseline stays provable. |
| Failure blast radius | A wrong criterion costs one wasted 4-week block at most, because week-4 already offers "adjust" (UC-2); a wrong *diagnosis* cannot affect the running channel (the document is not read by any program). |
| Gate discipline | Gate 2 criteria are the owner's to set (K3) — the document never hard-codes a number the owner has not accepted; the author's proposals are marked "proposed" until §0 is signed. |

## 9. Test plan

Verification plan.

Because this is a BA deliverable, "tests" are **dry runs and document checks** (no unit/integration/e2e suites, no fixtures).

| Name | Kind | Check | Asserts |
|---|---|---|---|
| V1_Structure | scripted (grep) | `sed -n '1,60p' docs/ops/gate-2.md` shows §0–§5 non-empty and §0 carries a sign-off name + date | the document is complete and signed |
| V2_CriteriaMeasurable | manual (walk each row) | every criterion row of §1 names a value **and** a measurement source in the §3 field table | no criterion without a source (no invented metric) |
| V3_FieldsExist | scripted (read-only) | the cost/render/count queries in §3 are run once against **staging** data (08.4.3-T1's `queries.sql`) and return the expected columns (`episode_id`, `cost_usd`, `gpu_seconds`, `estimated`; run counts by `outcome`) | each field the checkpoints read is obtainable from an existing table; no field needs a new table or route |
| V4_CheckpointDryRun | rehearsal | the author fills a **dry-run** week-4 entry from an existing/temporary week of data (or from the private pre-audit rehearsal week — F4 allows a private rehearsal run) and the owner reads it; the entry is then deleted or marked `dry-run` | the routine is executable in ≤ 30 min and the questions are answerable from the named sources |
| V5_NoApiFetch | manual (grep) | the document and this plan mention the YouTube Analytics API only as **forbidden**; `grep -rn 'analytics' docs/ops/gate-2.md` shows no fetch instruction | K5 respected — Studio export only |
| V6_ClockRule | manual | §4 states the audit-approval precondition and the counted-week rule; the week-12 section cannot be evaluated on a paused partial week | K1/F4 respected; the 12 weeks cannot be gamed |
| V7_NoCodeBoundary | scripted (git) | after the task, `git status` shows changes under `docs/ops/` only (plus the plan/task files) | no code, route, migration, config key or `tools/` script added |
| V8_SignoffEvidence | manual | 08.4.1-T1's setup notes cite the signed `docs/ops/gate-2.md` path/date, and the gate Channel-phase-start (`../README.md:22`) can be stated as satisfied | F4's exit condition is objectively checkable |

**Dry-run values.** V4 may use the **private pre-audit rehearsal** week (F4: "private pre-audit runs are allowed as a rehearsal, not counted in the 12 weeks") so the routine is rehearsed before the clock starts; such a row is labelled `rehearsal — not counted` and never enters the week count.

## 10. Tasks and re-estimate

Canonical dependency table (row verbatim from `../README.md` §8.1):

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 08.5.1-T1 | Gate 2 criteria + week-4/8/12 Channel-phase checkpoints document (signed before public autopilot) | XS (0.25) | — | 08.4.1-T1 (the former second consumer, 08.5.1-T2, was deleted by DEC 10.14) |

**Estimate:** 08.5.1-T1 = 0.25 d (XS) — unchanged from the high-level 08.5 (`plans/08-90-day-operations.md` §11; `../README.md` §9: 08.5 stays 2.5 d for the P slice; the SaaS 08.5.1-T2 (XS) that once sat outside this folder is **deleted**, DEC 10.14). Plan 08 total is now **2.5 d** (P only), buffered 3.0 (`../README.md` §9).

**Downstream consumers of this task's document:** 08.4.1-T1's exit (sign-off evidence), 08.4.2-T1/08.4.3-T1 (they fill the weekly log and KPI row the checkpoints read), and the Gate 2 decision itself at week 12.

## 11. Rollout

- **Deliverable location:** `docs/ops/gate-2.md` (new file, created by 08.5.1-T1). No feature flag, no migration, no deployment step — a repository document plus the owner's sign-off.
- **Order of work:** author §1 criteria drafts → §2 checkpoint questions + decision rule → §3 field table → §4 clock rule → §0 header → V1/V2/V3 checks → V4 dry run → owner signs §0 → hand the path/date to 08.4.1-T1. Only then may public autopilot be switched on (F4, gate Channel-phase-start `../README.md:22`).
- **Gate / DoR:** Task ready when (a) the criteria rows cite only the sources in §3, (b) the checkpoint questions are answerable from `kpi.md` + `weekly/*.md` + `queries.sql`, (c) V4's dry run is recorded, (d) the sign-off placeholder in §0 is the only remaining unfilled item (the owner's act, O1).
- **Monitoring while the phase runs:** no new alert (D5) — the phase is watched by 11.6.2-T1's rules (K8: quota, pause/stuck, gate-fail share, render failure, platform incident) and by 08.4.2-T1's weekly log. A checkpoint only reads what those already surfaced.
- **Change control after sign-off:** criteria changes append a §5 revision with the owner's initials; a closed criterion (C4 policy strike) is re-opened only by a new revision. If the audit is rejected, the clock never starts and the document is unchanged (§11 records it as a precondition failure, not a criteria failure).
- **Rollback:** none needed (a document); a wrongly specified criterion is corrected by a revision plus an owner note — the signed baseline is preserved in §5.
- **Gate 2 outcome routing:** "continue" → a new 4-week block with named adjustments; "adjust" → the named settings change plus its re-accept obligation; "stop" → the reason is recorded in §2. The former "build" branch (unblock the SaaS plans 06 S / 10 S / 12 / 13 S) no longer exists — those plans were deleted by DEC 10.14.
