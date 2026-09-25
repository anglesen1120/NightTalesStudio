# 08.2 — Alerts and runbooks (S slice)

> Part of plan 08 (S slice). Contract: `../README.md` Part B — §B1 (K10, K11, K16, K18, K23), §B3 (D8, D9, D11, D12), §B4 (CR-8, CR-9, CR-10, CR-12, CR-14), §B8.1. High-level: `../../../08-90-day-operations.md` §4 (08.2.1, 08.2.2), §5.2 (thresholds), §5.3 (runbook list), §7 (test plan).
> Phase: **S · M2**.

## 1. Scope

Make alerting operable for a SaaS: one alert vocabulary with an owner and a runbook per rule, a mechanism for the conditions that are not metric-expressible, and the 14 operational runbooks with an index that can be verified.

In: the rule-file schema (`owner`, `runbook`, `severity`, `dedup_key`, `window`) and the `db_checks:` section; `OpsAlertSweepJob` + `OpsAlertsOptions`; the reconciliation of 08 §5.2 with 11.6.2-T1/D17 (CR-9); the security-event routing and the alert drill; the 14 runbooks and `docs/runbooks/index.md`; the staging break-glass rehearsal and the sealed recovery-code procedure.

Out: 11.6.2-T1's collector, dashboards, metric rules and alert channel (consumed); 09.10.4-T3's `ISecurityAlerter` and the owner security e-mail (consumed — this slice adds the routing rule and the failure rule only); 12's thresholds (`runway`, `margin_drop`, `paid_volume_anomaly`, `spend_cap`) — only linked; 06's `platform_incidents` (referenced by rule 16, never extended); the P runbooks in `docs/runbooks/` (11.6.1-T1, 11.3.1-T3, 11.5.2-T1, 02.7.2-T1) and 08.4.4-T1's takedown runbook.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| K10 | 11.6.2-T1 owns the pipeline, the two boards, the 21 rules and the repository rule file; thresholds are owned by 08 §5.2, initial values by D17 (`5 min`, `30 s`, `80 %`, `20 %/24 h`, `360 min`, `80/85 %`, `85 °C`, `< 30 d`) | `11.6.2-T1:22-50`; `11 README:266,304` ✅ |
| K11 | 12 owns every money threshold and its alerts | `../../../12-billing-credits.md:184-192,338-361` ✅ |
| K14 | The platform's custom metric vocabulary is five names plus 11's node/relay names — no auth, moderation or token metric exists | `09:231-236` ✅ |
| K15 | `email_messages` records `template`, `status`, `attempts`, `last_error_code` | `../../../00-overview.md:270` ✅ |
| K16 | `platform_incidents` means a broken OAuth client hold, cleared by an operator — never a generic alert log | `../../../00-overview.md:293`; `06.2.3-T3` ✅ |
| K18 | P runbooks already exist and are referenced, not rewritten | `11 README:145`; `08.4.4-T1:158` ✅ |
| K23 | 09 S lead: adding a recurring job needs a CR to the P-signed 09.2.6-T1 `RecurringJobs.cs`; `OpsAlertSweepJob` does not collide with the five 09 S jobs; the `nts-admin` image owner is proposed as `11.6.4-T1` `[OPEN]` | 09 S lead 2026-09-25 ✅ |
| K13 | `audit_logs.category` includes `support`; admin mutations need a reason | `../../../00-overview.md:307`; `09:285` ✅ |

## 3. Domain and data

No table, no migration, no outbox event. Tables read by the DB checks: `refresh_tokens` (`revoked_reason`, `created_at`), `moderation_events` (`created_at`, `user_id`), `channel_connections` (`status`, `last_verified_at`), `publications` (`error_code`, `platform`), `payment_events` (`signature_valid`), `payments` (`status`, `created_at`), `email_messages` (`template`, `status`, `created_at`), `auth_failure_counters` (`failures`, `window_started_at`, `last_failure_at` — `login_failures_spike`), `platform_incidents` (`raised_at`, `cleared_at`) for the OAuth-client rule only.

Rule entry: `id`, `severity` (`page` | `warn`), `for`, `expression` (metric rules) **or** `db_check` (SQL over the tables above), `owner`, `runbook` (`docs/runbooks/<slug>.md`), `dedup_key`, `window`. `ops/alerts/rules.yml` [INFERENCE: exact path pinned by 11.6.2-T1's Outputs] is the single file; metric rules are loaded by 11's pipeline, `db_checks:` by `OpsAlertSweepJob`.

## 4. Use cases

| # | Use case | Story |
|---|---|---|
| UC1 | An operator sees a page, opens its `runbook` link, and follows a written fix with vi/en user communication | 08.2.2 |
| UC2 | A condition with no metric (a failed security e-mail, a moderation spike, a refresh-token reuse) still pages, within one sweep interval | 08.2.1 |
| UC3 | A rule that has no owner or names a runbook that does not exist fails CI before deploy | 08.2.1, 08.2.2 |
| UC4 | Every S rule is fired once on staging; the drill matrix records destination and detection latency | 08.2.1 |
| UC5 | The owner rehearses recovery on staging, sees the audit + alert + e-mail, and the sealed-code procedure is on paper | 08.2.2 |

## 5. API and events

No public API. `OpsAlertSweepJob` pushes `(id, severity, dedup_key, summary)` to 11.6.2-T1's alert endpoint (credential from 11.2.1-T1's secret store) with three attempts; on failure the owner e-mail fallback (09.2.6-T3's `SendEmailJob`) is used and `ops_alerts_fired_total{severity="send_failed"}` is incremented. `OpsAlertsOptions { Enabled, SweepMinutes = 5, PageCooldownMinutes = 60, RuleFile = "ops/alerts/rules.yml", EmailFallbackEnabled = true }`. Metrics `ops_alerts_fired_total{rule,severity}`, `ops_alert_sweep_duration_seconds` (CR-8). No HTTP route is added; the admin surface stays 09's.

## 6. UI

No screen (11 owns the operator surfaces; 08.1 owns the business KPI page). Human interfaces are files: 14 runbooks, `docs/runbooks/index.md`, the drill matrix `docs/ops/drills/2026-10-alert-drill.md`, and the rehearsal record inside `docs/runbooks/owner-recovery.md`.

## 7. Jobs and integrations

`OpsAlertSweepJob` — recurring, registered in 09.2.6-T1's `RecurringJobs.cs` (CR-8), single-instance through the house `[JobPolicy]`, interval `OpsAlerts:SweepMinutes` (5), loads the rule file once per run, evaluates the `db_checks:` in one statement each, logs rule id + severity + counts only (no user identifiers), and continues after a failing check. Integrations: 11.6.2-T1's alert channel and rule file, 09.10.4-T3's `ISecurityAlerter` (consumed), 09.2.6-T3's `SendEmailJob` (fallback), 11.5.1-T1's deploy pipeline (pushes `post_deploy_smoke_failed`), 09.10.7-T1's one-shot `nts-admin` job (the subject of the rehearsal, not called by this slice).

## 8. Security and privacy

Alert payloads and logs carry rule id, severity, counts and a runbook link — no story text, no e-mail, no user id beyond a count (11 D19 S7). The rule file holds thresholds, SQL and file paths, never a secret. The alert endpoint credential lives in the secret manager, not in the file or the repo. Support/security alerts about staff and account actions reach the owners only (K12, 09.10.4-T3). The sweep job runs as `system` and reads across tenants on purpose: every check returns a count, never a row, and it is added to no per-user scope. Runbooks must not contain credentials; the sealed recovery codes are referenced by storage location only. A page path that cannot deliver is itself alerted (`email_queue_stale`, `security_email_failed`).

## 9. Test plan

- Rule file: table-driven validation of every entry (id syntax, severity, owner, runbook file exists, `dedup_key`); negative test for a missing `owner` and for a runbook path that does not exist.
- `tools/ops/check_alert_contract.sh` in CI: exits 0 on the shipped file, 1 when one rule loses its `owner` or its runbook, and 1 when `docs/runbooks/index.md` omits a rule id.
- Job: with a recording alert-sender fake + TestKit, two consecutive runs of the same condition produce one page (dedup key + cooldown) and a second tick with the condition cleared produces nothing; a throwing check does not stop the others; the send-failure path increments the metric and sends the fallback e-mail once.
- SQL checks: each `db_check` against seeded rows at the boundary (exactly at the threshold, one over), with `FakeClock` for the windows; `payment_integrity` thresholds are asserted to equal 12's values, not re-declared.
- Drill (manual acceptance, 08.2.1-T2): every S rule fired once on staging with the recorded destination and detection latency, including the security e-mail with the pre-change configuration.
- Rehearsal (manual acceptance, 08.2.2-T2): the recorded command transcript, the audit row, the alert, the owner e-mail and the measured start-to-working-login time.

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 08.2.1-T1 | S (0.5) | BE/Ops | 11.6.2-T1, 09.9.3-T1 | 08.2.1-T2, 08.2.2-T1 | S · M2 |
| 08.2.1-T2 | XS (0.25) | Ops | 08.2.1-T1, 09.10.4-T3 (soft), 11.5.1-T1 (soft) | — | S · M2 |
| 08.2.2-T1 | M (1.0) | Ops/BA | 08.2.1-T1, 06.2.3-T3, 09.10.7-T1, 11.6.2-T1, 12.3.3-T2, 12.8.3-T1, 13.3.1-T1 | 08.2.2-T2, 08.3.1-T1 (soft) | S · M2 |
| 08.2.2-T2 | XS (0.25) | Ops | 08.2.2-T1, 09.10.7-T1, 11.6.4-T1 | — | S · M2 |

**Estimate:** 2.0 d (0.5 + 0.25 + 1.0 + 0.25) = `../README.md` §B8.1 = `../../../08-90-day-operations.md` §11 epic 08.2.

## 11. Rollout

Milestone **M2**; the runbooks must exist before public sign-up widens at day 30 (D13's checkpoint), and the drill before the first beta cohort. Order: 08.2.1-T1 (rule file + job) → 08.2.2-T1 (runbooks + index) → {08.2.1-T2 (routing + drill), 08.2.2-T2 (rehearsal)}. Preconditions: 11.6.2-T1 loaded and its channel chosen (if `11.2.1-T1` is still `[OPEN]`, the e-mail fallback is the drill path and this is recorded as a defect, not silently accepted); CR-9 applied so no rule has two owners of its threshold; `11.6.4-T1` delivered for the rehearsal. Rollback: `OpsAlerts:Enabled = false` silences the DB checks without touching 11's metric rules; a bad rule is reverted by a file diff. Success: every rule has an owner and a runbook, and the drill shows no unexplained page.
