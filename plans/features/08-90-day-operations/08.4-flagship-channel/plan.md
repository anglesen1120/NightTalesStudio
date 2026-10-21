# 08.4 — Flagship channel on autopilot (feature plan)

> Refines: `../../08-90-day-operations.md` §4 Epic 08.4 (08.4.1–08.4.3) + `../../00-overview.md` §4.1 (personal phase) / §4 "Channel phase" (K1). Contract: `../README.md` — facts §1 K1–K9, findings F1–F6, decisions **D1–D5**, CRs §4 (08 CR-1, 08 CR-4, 08 CR-5), sequencing §5, gate §7, canonical dependencies §8.1; task template `../../02-story-writing/README.md` §6 (BA tasks). Section template: `../../00-overview.md` §10.1, with the BA mapping of §3–§7 declared in `../README.md` §0/§5.
> Sibling folder: `../08.5-checkpoints/` (08.5.1-T1, XS) — it drafts `docs/ops/gate-2.md` and is a **hard dependency** of 08.4.1-T1.
> Upstream contracts read here: 02.8.3 `plan.md` §3.1 (`autopilot_settings` jsonb) / §3.5–§3.6, `02.8.3-T2` (routes + authorization), `02.8.3-T4` (pause triggers), 02.8.2 `plan.md` §3.1–§3.3 (gate checks + thresholds), 06 README D11/D12/YR2/YR20 + 06.1.2-T2 (channel connection), 11.6.2-T1 (alert rules), `../../00-overview.md` §6 (`usage_records`, `render_jobs`, `publications`, `platform_incidents`).
> Legend: ✅ verified (source) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-25. Nothing implemented yet. **Deliverables of this folder are documents, templates, SQL snippets and routines — no application code, no migration, no route, no config key, no job.**

## 1. Scope

| | |
|---|---|
| Slice | Plan 08 **P** — epic 08.4 only (the flagship channel during the **Channel phase**, 00 §4: autopilot on the owner's channel for ≥ 12 weeks after the YouTube audit allows public uploads, then Gate 2) |
| Tasks | 08.4.1-T1 flagship series setup + 12-week calendar (S 0.5) · 08.4.2-T1 weekly autopilot operations routine (M 1.0) · 08.4.3-T1 weekly channel + production KPI sheet (S 0.5) · 08.4.4-T1 takedown runbook (XS 0.25) = **2.25 d**; with 08.5.1-T1 (XS 0.25) the P slice is **2.5 d**, unchanged (`../README.md` §9) |
| In scope | (a) the configuration decisions for one autopilot series and their in-product acceptance (re-auth + versioned authorization, 02.8.3-T2); (b) the 12-week content calendar as a document; (c) the recurring weekly operator routine and its template; (d) the weekly KPI sheet and the SQL snippets that read **existing** tables; (e) the takedown runbook for a flagged or struck autopilot video; (f) the measurement of owner touch time per episode — a Gate 2 input (K1/F5) |
| Out of scope | The autopilot machinery itself — slot planner, run state machine, pause/resume, spend caps, gates, consent, upload, go-live, quota (02.8.3-T1/T2/T3/T4, 02.8.2-T1/T2/T3, 06.2.1–06.2.6, 06.3.1); the Autopilot tab and every Studio screen (10.4.3-T1, 10.5.2 — this folder only **uses** them); the Gate 2 criteria document and the week-4/8/12 checkpoint questions (08.5.1-T1, sibling folder); the SaaS KPI dashboard, alert-rule ownership, runbooks other than the takedown one and support (08.1–08.3, dropped with phase S by DEC 10.14 — D5); monetization/YPP application (13, not required in P, K4); any new metric, log line, alert rule, config key, table or endpoint |
| Acceptance (restated) | Given a signed `docs/ops/gate-2.md`, a connected channel in `active` state and an accepted autopilot authorization, when the Channel phase starts, then one `ghost`/`vi` series runs on autopilot at the calendar's cadence, the owner spends ≤ 45 min/week on the routine, every published video of the week is represented by one KPI row with a recorded source, and any takedown is handled by the runbook inside 24 h. Videos are `public` only while `YouTube:AuditStatus = approved` (K7) |

Two boundary rules this folder must not violate:

- **No production work.** F1/D2: the weekly routine does **not** produce episodes. Manual editing happens only for `true_crime` (always owner-reviewed, 02.8.2 `plan.md` §3.1 check `true_crime_hold`) or after a gate failure that leaves the episode in `story_review` / `video_review`.
- **No YouTube analytics through the API.** K5/D3: every channel KPI comes from a **manual** Studio export; the platform half comes from our own tables. Storing YouTube analytics data via the API is not allowed under the API's stored-data duties.

## 2. Confirmed facts

| # | Fact used here | Evidence |
|---|---|---|
| K1 | Channel phase = autopilot on the owner's channel ≥ 12 weeks after the audit allows public uploads; Gate 2 decides on channel results, cost per video and time per episode | 00 §4 "Channel phase" ✅ |
| K2 | Fiction is auto-approved by the gates, true crime always waits for the owner; variety guard; per-channel daily cap default 1; veto window default 12 h | DECISIONS §10.12 ✅ (`../README.md` K2) |
| K3 | Gate 2 criteria are `[OPEN]` and must be set by the owner **before** the Channel phase starts | DECISIONS §10.12 ✅ (K3, D4, 08 CR-2) |
| K4 | YPP: 1,000 subscribers + 4,000 watch hours (12 months) or 10M Shorts views (90 days); mass-produced content is not monetizable → the monetization thresholds are **tracked, not required** in P | DECISIONS §6 ✅ (K4, D4) |
| K5 | No YouTube analytics is stored via the API; channel KPIs come from a manual Studio export | 08 §4 08.4.3 [DECISION] ✅ (K5, D3) |
| K6 | `autopilot_settings` jsonb keys and ranges: `cadence {days_of_week, publish_time, time_zone}`, `daily_cap` 1..5, `buffer_days` 1..7, `privacy` ∈ public\|unlisted\|private, `veto_window_hours` 2..48, `pre_audit_mode` ∈ `keep_cadence_private`\|`pause_after_n_private`, `pre_audit_pause_after` 1..20, `gate_thresholds_ref`, `tone` ≤ 500, `banned_themes` ≤ 20×100, `render_lead_minutes`, `story_lead_minutes` | 02.8.3 `plan.md` §3.1 ✅ |
| K7 | Enabling/changing autopilot needs a **current versioned authorization** + `If-Match` + **re-auth**; `POST /series/{id}/autopilot/authorization` (201 `{version, acceptedAt}`) and `DELETE …/authorization` (204) are the accept/revoke routes; the rendered text carries `textVersion` + `textHash`, placeholders include `{{days_of_week}} {{publish_time}} {{time_zone}} {{daily_cap}} {{privacy_label}} {{veto_window_hours}} {{pre_audit_note}}`; changing a covered field revokes until re-accepted | 02.8.3-T2 §Routes/§Authorization text/§Accept-revoke ✅ |
| K8 | Pre-audit: `YouTubeOptions.AuditStatus` ∈ `pending`\|`approved`\|`rejected` (`IOptionsMonitor`); while ≠ `approved` the upload omits `publishAt` and the row stays `private` (`platform_payload.preAudit = true`); a `PATCH` to non-private on a pre-audit row → 409 `publication_privacy_locked` | 06 README D11/D12, YR2, YR20 ✅ |
| K9 | Alerts already exist and are the P operator's only paging channel: rule 10 quota > 80 % (warn), 11 gate fail > 50 %/24 h (warn), 12 any `autopilot_pause_total` or `autopilot_run_stuck_total` > 60 min (**page**), 15 render queued > 360 min (**page**), 16 `platform_incident_open` > 0 for 5 min (**page**), 17 deletion due < 24 h (**page**), 5 render failure share > 20 %/24 h (**page**), 9 OpenAI cost/day over the budget line (**page**) | 11.6.2-T1 §Alert rules 1–17 ✅ (K8/F6: 11.6.2-T1's values are authoritative, **not** 08 §5.2) |
| K10 | Pause triggers that end the autopilot without the owner: 1 gate-failed streak, 2/3 spend caps, 5 provider funds, 6 channel not `active`, 7 OAuth client incident, 8/9/10 quota/upload/rejected, 11 veto notice undelivered, 12 repeated run failures, 13 slot missed twice, 14 pre-audit private limit (15 owner pause is explicit, not automatic); the pause writes `autopilot_paused_at` + `autopilot_pause_reason`, stops new runs and blocks `awaiting_consent` rows (`AutopilotConsentJob` skips a paused series — 02 CR landed in 06.2.2-T3:64/155) while leaving already-consented rows untouched — a `scheduled` row still uploads and must be cancelled explicitly, and `uploading`/`processing`/`published` cannot be cancelled through the product (`Publication.Cancel` accepts `awaiting_consent|scheduled` only, 06.2.1-T1:74) — and e-mails the owner with the already-scheduled videos (except `owner_paused`) | 02.8.3-T4 §Trigger wiring ✅ (K9); 06.2.2-T3:64/155; 06.2.1-T1:74 ✅ |
| K11 | Takedown is owner-initiated, not event-driven: there is **no** report intake in P (the abuse-report form and its `reports` table were dropped with 13.3.4, DEC 10.14), so a takedown starts when the owner sees a Studio notice or strike and pauses the series through `POST /series/{id}/autopilot/pause` (`owner_paused`) | 02.8.3 `plan.md` §3.6 row 15, 02.8.3-T4 §Takedown hook ✅ |
| K12 | Gate evidence available for the weekly sample audit: `story_versions.audit_report` = `audit.v1` (local issues, moderation, `model_review.scores` over the 7 D16 dimensions, revision, similarity) and `autopilot_runs.gate_results` = `gates.v1` (story + video `checks[]` with `id/value/threshold/pass/detail`, `failures[]`) | 02.8.2 `plan.md` §3.4, D16, D22 ✅ |
| K13 | The Studio deep link used by the pause e-mail is `https://studio.youtube.com/video/{external_post_id}/edit` ⚠️ (shape not verified against Google docs; fallback `https://studio.youtube.com/channel/{external_account_id}/videos/upload`) | 02.8.3 `plan.md` §1 ✅⚠️ |
| K14 | Column names available to the SQL snippets (00 §6): `usage_records` (`user_id, episode_id, render_job_id, provider, operation, model, input_tokens, cached_input_tokens, output_tokens, image_count, gpu_seconds, estimated, cost_usd, price_version, provider_request_id`); `render_jobs` (`user_id, episode_id, status, attempt, error_code, started_at, finished_at, stage, percent, submitted_at, next_poll_at, quality_report`); `publications` (`status, consent_source, autopilot_authorization_id, veto_notice_sent_at, contains_synthetic_media`, plus `publish_at`/`external_post_id`); `platform_incidents` (`platform, oauth_client_id, error_code, raised_at, cleared_at`) | 00 §6 ✅ |
| K15 | The owner is the **only** user in P; the series is owned by the owner (`SystemContext(ownerId)`), so every snippet must filter by the owner's `user_id`/`series_id` rather than assume one row | 00 §5.2, 02.8.3-T1 ✅ |
| K16 | The takedown runbook depends on pause mechanics owned by 02.8.3-T4 (the `owner_paused` route); the reverse dependency was removed (README 08 CR-4) | `../README.md` §4 CR-4 ✅ |

## 3. Domain and data

Documents and data sources.

### 3.1 Deliverable files (all under `docs/ops/`, none in `src/` or `plans/`)

| File | Owner | Content | Consumed by |
|---|---|---|---|
| `docs/ops/flagship/setup.md` | 08.4.1-T1 | the chosen series configuration, the 3-candidate test result, the accepted authorization (version + `textHash` + `acceptedAt`), the audit-status snapshot, the enable/disable order | 08.4.2-T1 (context), 08.5.1-T1 (Gate 2 record) |
| `docs/ops/flagship/calendar.md` | 08.4.1-T1 | 12 weekly rows: `week #`, `dates`, `planned slots` (dates at the cadence), `theme notes`, `risk notes` | 08.4.2-T1 (weekly plan), 08.4.3-T1 (denominator for "slots published") |
| `docs/ops/flagship/weekly/_template.md` | 08.4.2-T1 | the routine template (D2) | 08.4.2-T1 (every week) |
| `docs/ops/flagship/weekly/YYYY-WW.md` | 08.4.2-T1 | one file per ISO week, filled once | 08.4.3-T1 (touch time, pauses, incidents), Gate 2 (08.5.1-T1) |
| `docs/ops/flagship/kpi.md` | 08.4.3-T1 | one row per week, fixed column order, `source` column per metric family | Gate 2, week-4/8/12 checkpoints |
| `docs/ops/flagship/queries.sql` | 08.4.3-T1 | the read-only SQL snippets (§5.2), with the exact `user_id`/`series_id` bind parameters | 08.4.2-T1 (context counts), 08.4.3-T1 |
| `docs/ops/flagship/takedown.md` | 08.4.4-T1 | the runbook (trigger → pause → private/remove ≤ 24 h → record cause) | the owner during the Channel phase |
| `docs/ops/gate-2.md` | **08.5.1-T1** (sibling folder) | owner-signed Gate 2 criteria + week-4/8/12 questions | 08.4.1-T1 (entry gate), 08.4.2-T1 (weekly review), Gate 2 |

### 3.2 Data sources

| Source | Kind | Read how | Used for |
|---|---|---|---|
| YouTube Studio → Analytics export (CSV) | manual, human | owner downloads/transcribes into `kpi.md` | views, CTR, average view duration, subscribers, watch hours (K5) |
| `usage_records` | our DB | `queries.sql` snippet Q2 (read-only) | cost per episode/video, cost breakdown by `operation`, `estimated` share |
| `render_jobs` | our DB | Q3 | render success share, p95 render duration, `render_quality_failed` count |
| `autopilot_runs` | our DB | Q4 | slots planned/run, `state`, `outcome`, `pause_reason`, `gate_results` (`gates.v1`) |
| `publications` | our DB | Q5 | videos published per week, consent source (`user` vs `autopilot`), pre-audit private count |
| `platform_incidents` | our DB | Q6 | incident days (OAuth client), pause correlation |
| `audit_logs` | our DB | Q7 (actions `autopilot.paused`, `autopilot.resumed`, `autopilot.enabled/disabled`, `publishing.*`) | a check against the weekly narrative |
| 11.6.2-T1 dashboards/alerts | telemetry backend | human read of the board | alert counts and the paging events of the week (K9) |

**No new table, no new view, no new endpoint, no new metric.** Every snippet runs against tables created by 09.2.2-T2 / 02 migrations and listed in 00 §6 (K14).

## 4. Use cases

Routines and use cases.

| Id | Routine | Actor | Preconditions | Steps (summary) | Success / evidence |
|---|---|---|---|---|---|
| UC-1 | **Series setup and authorization** (08.4.1-T1) | owner | `docs/ops/gate-2.md` signed (K3); channel `active`; 06.1.2-T2 live | pick topic/voice/style/tier by a 3-candidate test on the M0 outputs (D1) → set `autopilot_settings` per K6/D1 → `GET /series/{id}/autopilot` to read the rendered authorization → accept it (re-auth + legal) → `PUT` with `If-Match` → write the 12-week calendar → record the audit status | `setup.md` records `version`, `textHash`, `acceptedAt`; `GET` shows `RequiredAuthorization = false` and 3 `NextSlots`; `calendar.md` has 12 rows |
| UC-2 | **Weekly autopilot routine** (08.4.2-T1, D2) | owner | series enabled, week's runs exist | open the week's veto notices → edit/cancel only what is wrong → watch **one** sampled published video in full and read its script against the 02 rubric (scores recorded, K12) → record true-crime approvals and gate failures/pauses with causes → incidents → touch minutes per episode → next-week decisions | one `weekly/YYYY-WW.md` file, ≤ 45 min of owner time, every field from the template either filled or explicitly `none` |
| UC-3 | **Weekly KPI row** (08.4.3-T1, D3) | owner | UC-2 done for the week; Studio export available | export the Studio CSV → transcribe the 5 channel columns → run Q2–Q7 → compute the platform columns → one row appended to `kpi.md` with the week's denominator from `calendar.md` | one row/week, every cell traced to a source label, no YouTube-API-derived value anywhere |
| UC-4 | **Takedown** (08.4.4-T1) | owner | runbook rehearsed once before public autopilot | detect (Studio notice, e-mail, or alert rule 12/15/16) → pause the series through the product (`POST /series/{id}/autopilot/pause`, `owner_paused`, K10/K11 — nothing pauses it automatically for a strike) → within **24 h** make the video `private` or remove it in Studio → walk the run log for other publications of the series and act by state (a `scheduled` row still uploads and is cancelled, an `awaiting_consent` row is blocked by the pause, `uploading`/`processing`/`published` are Studio-only — 06.2.1-T1:74) → record cause, timestamp, evidence and whether the source was a Studio notice, a strike or a proactive decision → decide: resume, adjust settings, or stop | `takedown.md` §Record filled for the event; weekly file references it; the runbook's timed table has a real row |
| UC-5 | **Checkpoint review** (08.5.1-T1 consumes this folder's data) | owner | ≥ 4 / 8 / 12 completed public weeks | re-read `kpi.md` + the weekly files → answer the week-4/8/12 questions in `docs/ops/gate-2.md` | one dated entry per checkpoint in `gate-2.md` |

Ordering inside the folder is strict: **08.4.1-T1 → 08.4.2-T1 → 08.4.3-T1**; 08.4.4-T1 is independent of that chain and only needs 02.8.3-T4 (soft) + 02.8.2-T2, but it must be rehearsed **before** public autopilot starts (gate §7).

## 5. API and events

Inputs read.

### 5.1 Product surface read (read-only — this folder adds no route)

| Route / screen element | Method | Who owns it | Read here for |
|---|---|---|---|
| `GET /series/{id}/autopilot` → `AutopilotDto` | GET | 02.8.3-T2 | `Enabled`, `Settings`, `PausedAt/PauseReason`, `Authorization {version, textVersion, textHash, acceptedAt}`, `AuthorizationText`, `RequiredAuthorization`, `Channel`, `GatesDryRun`, `GatesDryRunRemaining`, `NextSlots[]` |
| `PUT /series/{id}/autopilot` | PUT | 02.8.3-T2 | writing the D1 settings (needs `If-Match` + `If-Match` ETag from the GET, 428 otherwise) |
| `POST`/`DELETE /series/{id}/autopilot/authorization` | POST/DELETE | 02.8.3-T2 | accept (re-auth) / revoke, replayed **through the product** (D1) |
| `POST /series/{id}/autopilot/pause` \| `/resume` | POST | 02.8.3-T2 | the takedown runbook's first step; `resume` re-checks the triggers (02.8.3-T4) |
| `GET /series/{id}/autopilot/runs` | GET | 02.8.3-T2 | run log for the weekly file: `state`, `outcome`, `pauseReason`, `waitingFor`, `gates` (`gates.v1`), `publicationId` |
| `GET /channels` | GET | 06.1.2-T2 | the connection `status` (`active`) and the channel title used in `setup.md` |
| `platform_incidents` / `nts-worker incident clear <id>` | CLI | 06.2.3-T3 | only when alert rule 16 fires (K9); the runbook may name the verb, never run it as a routine step |

### 5.2 SQL snippets (read-only, existing tables only — K14)

`docs/ops/flagship/queries.sql` holds seven labelled snippets Q1–Q7 (Q2 carries a second statement, Q2b); each binds `:'owner_id'::uuid` (plus `:'series_id'::uuid`, `:'week_start'::timestamptz`, `:'week_end'::timestamptz` where relevant) and runs with `psql -v owner_id=<uuid> -v week_start=<date> -v week_end=<date> -f queries.sql` or pastes into DBeaver with the same bind form — the `:'var'` quoting is required because psql splices a bare `:var` in raw. Shapes (final text in 08.4.3-T1):

- **Q1 — episodes published per ISO week** from `publications` (`status = 'published'`), grouped by `date_trunc('week', publish_at)` — `publications` has no `published_at` in 00 §6 (that column is 06 CR-1's proposal); the week key is therefore an approximation (a retry/pre-audit hold can publish later than its slot) and switches to `published_at` once it lands; columns `week, published_count, private_only_count`.
- **Q2 — cost per episode**: `SUM(cost_usd)` from `usage_records` grouped by `episode_id`, with `SUM(...) FILTER (WHERE operation = 'render')` as `render_cost_usd`, `SUM(...) FILTER (WHERE estimated)` as `estimated_cost_usd`, `SUM(gpu_seconds)` and `bool_or(estimated)` as `estimated`; a second statement (Q2b) aggregates the same rows by `operation`, provider and model for the week.
- **Q3 — render reliability**: from `render_jobs`, per week: `count(*) FILTER (WHERE status = 'succeeded') / count(*)` as success share, `percentile_cont(0.95) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM (finished_at - started_at)))` as p95 seconds, and `count(*) FILTER (WHERE status = 'failed' AND error_code = 'render_quality_failed')`.
- **Q4 — autopilot outcome**: from `autopilot_runs` per week: runs started, `count(*) FILTER (WHERE outcome IS NULL)` as still producing, `count(*) FILTER (WHERE pause_reason IS NOT NULL)` by reason, and `count(*) FILTER (WHERE (gate_results -> 'story' ->> 'pass') = 'true')` / `count(*) FILTER (WHERE gate_results -> 'story' IS NOT NULL)` as the story-gate pass share.
- **Q5 — consent source**: `publications` per week: `count(*) FILTER (WHERE consent_source = 'autopilot')` vs `'user'`, plus the veto-window usage (`consent_at` vs `veto_notice_sent_at`) as the "veto acted on" share.
- **Q6 — incidents**: `platform_incidents` rows open or raised in the week (`raised_at`, `cleared_at`), grouped by `error_code`.
- **Q7 — audit cross-check**: `audit_logs` rows for the week where `action IN ('autopilot.paused','autopilot.resumed','autopilot.enabled','autopilot.disabled','autopilot.authorization_accepted','autopilot.authorization_revoked')` (keys per 02.8.3 D13), returning `action, occurred_at, data`.

Two snippet rules: (1) **read-only** — no `INSERT/UPDATE/DELETE`, no `CREATE`; run against a replica or staging copy when possible; (2) column names above are taken from 00 §6 (K14) and must be re-checked against the generated migrations at implementation — a drifted name is fixed in `queries.sql`, never by inventing a column.

### 5.3 Studio export columns (manual, K5)

`views`, `impressions` + `CTR`, `average view duration`, `subscribers gained` (+ total), `watch hours` — the same five columns every week so the trend is comparable; `source` cell records `studio-export <date>`. Nothing here is fetched by code.

## 6. UI

Product screens used.

| Screen (owner) | Story | Used for |
|---|---|---|
| Autopilot tab of the series (10.4.3-T1) | 10.4.3 | read/put settings, read the rendered authorization, accept/revoke, pause/resume, run log (`AutopilotRunDto` list) |
| Series editor (10.4.1/10.4.2) | 10.4 | `tone`, `banned_themes`, `length_tier`, aspect, topic label (D1) |
| Episode detail + script/video review (10.5.1/10.5.2) | 10.5 | the weekly sample audit: script, rubric scores from `audit_report`, video playback, true-crime approve/reject |
| Channels page (10.7.1) | 10.7 | connection status; reconnect after `channel_not_active` |
| Alert notifications (11.6.2-T1 channel) | 11.6 | the paging events of the week (K9) |
| YouTube Studio (Google) | — | manual analytics export; `private`/remove; comment/report handling |

No screen is built or changed by this folder; no copy key is handed over (10 owns every screen).

## 7. Jobs and integrations

Cadence and timers.

| Cadence | Value (D1 unless stated) | Timer source / owner |
|---|---|---|
| Publish cadence | 3 days/week at one fixed local time (`days_of_week` 3 entries, `publish_time` 19:00, `time_zone` `Asia/Ho_Chi_Minh` in D1) | `autopilot_settings.cadence`, planned by `AutopilotPlanJob` (15 min tick, 02.8.3 D17) |
| Daily cap | 1 video per channel per local day | `autopilot_settings.daily_cap` (K6) |
| Ready buffer | `buffer_days` 2 | `autopilot_settings.buffer_days` |
| Veto window | 12 h (range 2..48) | `autopilot_settings.veto_window_hours`; enforced by `AutopilotConsentJob` (06.2.2-T3) |
| Pre-audit behaviour | `keep_cadence_private` while `AuditStatus ≠ approved` (K8) | `autopilot_settings.pre_audit_mode` + `YouTubeOptions.AuditStatus` |
| Upload lead time | 120 min (`PublishLeadMinutes`), minimum veto 2 h (`MinVetoWindowHours`) | 06 README D11 (one owner key) |
| Weekly routine | once per ISO week, **time box 45 min** (D2) | manual; measured in the weekly file |
| Weekly KPI row | once per ISO week, after the routine | manual; `kpi.md` |
| Takedown SLA | **≤ 24 h** from detection to `private`/remove (08.4.4-T1) | manual; runbook records `detected_at` / `action_at` |
| Checkpoints | week 4, 8, 12 | 08.5.1-T1 (`gate-2.md`) |
| Channel phase length | ≥ 12 public weeks (K1) | measured in `kpi.md`; weeks with pre-audit private output do **not** count (F4) |

## 8. Security and privacy

| Requirement | How this folder satisfies it |
|---|---|
| No secret or token value in any document | `setup.md`/`calendar.md`/`weekly/*`/`kpi.md`/`takedown.md` never contain a client secret, access/refresh token, upload session URI, OAuth `code`/`state`, or a `pc.v1` cancel token; only public identifiers (channel title, series id, publication/episode ids when needed) — the same forbidden list 13.4.2-T1 applies to the audit pack |
| No YouTube data stored outside the manual export | K5: the only channel metrics in the repo are the numbers the owner transcribes from the Studio CSV; no API call, no scraper, no scheduled job |
| No third-party personal data | a takedown record carries the platform notice reference (or `none` when the owner acted proactively) and the reason — never a reporter's e-mail; P has no report intake (K11, DEC 10.14) |
| Least privilege for the snippets | Read-only SQL, run by the owner/operator role, no DDL/DML; run on a replica or staging copy where available |
| Honest provenance | every KPI cell carries `source` (`studio-export <date> \| queries.sql Q<n> \| alert board`); an unmeasured cell is `n/a` with the reason, never a guess |
| Retention of ops docs | `docs/ops/flagship/**` is repository documentation, kept in git with the plan (no runtime state, no secrets); deleted/altered content follows the normal review flow |
| The authorization is accepted through the product | D1/K7: re-auth + legal acceptance, versioned text with `textHash` recorded in `setup.md` — not a hand-written note that could drift from the stored authorization |

## 9. Test plan

No code exists, so no xunit/Shouldly test, no `PostgresFixture`, no `FakeGoogleServer` is written by this folder (the behaviour these documents describe is proven by 02/06/11 tests — 02.8.3 §9, 06 README §3). Verification = **dry runs, rehearsals and document checks**, each with a recorded output:

| # | Check | Method | Pass condition |
|---|---|---|---|
| V1 | The `autopilot_settings` payload from `setup.md` is accepted by the product | paste it into the Autopilot tab `PUT` (staging) | 200 + ETag; no 400 `autopilot_settings_invalid`; the GET echoes every key with the intended value |
| V2 | The authorization round-trip works | `GET` → accept (re-auth) → `GET` | `RequiredAuthorization = false`; `version`/`textHash` in `setup.md` byte-equal to the GET's values |
| V3 | Pre-audit behaviour | while `AuditStatus = pending`, observe the first run | publication ends `published` with `privacy = private` and `platform_payload.preAudit = true`; no public video; the run outcome matches `keep_cadence_private` |
| V4 | Every SQL snippet runs | run `queries.sql` against staging data, one statement at a time | no error; row counts and `EXPLAIN` shapes recorded; each snippet's purpose satisfied by its columns |
| V5 | The weekly template is fillable in the time box | fill `_template.md` once for a past week using only the run log + one Studio export | every field has a value or `none`; owner time ≤ 45 min, recorded |
| V6 | The KPI sheet reconciles | fill one `kpi.md` row; re-derive the platform columns by hand from the run log | every platform cell equals the Q2–Q7 result; the channel cells equal the CSV |
| V7 | The takedown runbook is rehearsed once | run UC-4 end to end on a **staging** publication chosen deliberately (e.g. the current run's video): pause → `private` in Studio → record → resume | the timed table has `detected_at`, `action_at` and an elapsed time ≤ 24 h; the pause shows `autopilot_pause_reason` = the exercised reason; resume re-checks triggers |
| V8 | The calendar matches the planner | compare `calendar.md`'s 12 weeks/36 slots with the Autopilot tab's `NextSlots` | the first three `NextSlots` fall on the calendar's planned weekdays/time; no day holds more than `daily_cap` slots |
| V9 | Gate-2 input completeness | for a completed week, ask: can week-4 questions be answered from `kpi.md` + the weekly file alone? | cost/video, touch time/episode, slots published vs planned and the channel trend are all present |

V3 and V7 need a real channel and are run **after** the audit-filing task 13.4.2-T1 (soft dependency of 08.4.1-T1); V1, V2, V4, V5, V6, V8, V9 run on staging before the Channel phase starts.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 08.4.1-T1 | Flagship series setup: topic line, style, voice, length tier, autopilot settings + authorization, 12-week calendar | S (0.5) | 08.5.1-T1, 02.8.3-T2, 06.1.2-T2, 13.4.2-T1 (soft) | 08.4.2-T1 |
| 08.4.2-T1 | Weekly autopilot operations routine: veto review, sample audit, time log, incident notes | M (1.0) | 08.4.1-T1, 02.8.3-T4, 06.2.2-T3 | 08.4.3-T1 |
| 08.4.3-T1 | Weekly channel + production KPI sheet (manual Studio export + platform metrics) | S (0.5) | 08.4.2-T1, 11.6.2-T1 | — |
| 08.4.4-T1 | Takedown runbook: flagged or struck autopilot video → pause, private/remove in Studio ≤ 24 h, record cause | XS (0.25) | 02.8.2-T2, 02.8.3-T4 (soft) | — |

Folder total **2.25 d**; with `../08.5-checkpoints/` 08.5.1-T1 (XS 0.25) the plan-08 P slice is **2.5 d**, matching `../README.md` §9 ("08.4 + 08.5 = 2.5 unchanged"). These four rows are the canonical §8.1 rows and are repeated **verbatim** in each task file's header.

## 11. Rollout

1. **Entry gate `Channel-phase-start`** (`../README.md` §7): `docs/ops/gate-2.md` signed by the owner (K3/D4) · the series exists with the accepted authorization (`setup.md`) · the takedown runbook exists and was rehearsed once (V7) · `kpi.md` template + `queries.sql` ready (V4/V6). Any missing item blocks the phase.
2. **Order.** 08.5.1-T1 (signed criteria) → 08.4.1-T1 (D1 series + calendar + authorization) → 08.4.2-T1 (weekly routine template, first filled week) → 08.4.3-T1 (first KPI row) → weekly from then on. 08.4.4-T1 lands before public autopilot and is independent of that chain.
3. **Rehearsal (private, not counted).** With `AuditStatus = pending` and `pre_audit_mode = keep_cadence_private`, turn the series on and let it run: verify V3, a real veto notice + window expiry, the weekly file (V5) and at least one KPI row (V6). These weeks are **not** part of the 12 public weeks (F4/D1).
4. **Public autopilot.** Only after the audit is `approved` (K8) and the criteria are signed: confirm the privacy setting is `public`. Note the automatic side effect of the audit flip: the rendered authorization text changes (the pre-audit note disappears), so the covered-field/text check **revokes** the authorization (`revoked_reason = text_changed`, 02.8.3-T2:123/170) — the owner re-accepts (re-auth + legal) before the first public slot, and the re-acceptance is appended to `setup.md` §audit snapshot. Then run V8/V9 on the first public week, then let the routine repeat. Watch alert rules 5, 10–12, 15–16 as the paging surface (K9) — no new alert is configured in P (D5).
5. **During the phase (steady state).** Weekly UC-2 + UC-3; D5's rule: the operator relies on 11.6.2-T1's alerts and the takedown runbook only. A pause from any K10 trigger is read in the weekly file with its reason code; resuming after a covered-field change needs re-acceptance (K7).
6. **Weekly checkpoints 4/8/12** (08.5.1-T1): answer the questions from `kpi.md` + the weekly files; record the answer in `gate-2.md`; adjust D1 settings only through the product (each covered-field change revokes the authorization → re-accept, K7).
7. **Exit / Gate 2.** After ≥ 12 completed public weeks, 08.5.1-T1 evaluates the owner's criteria against this folder's data; this folder's job ends there (a decision to continue, adjust or stop belongs to 08.5.1-T1, not here).
