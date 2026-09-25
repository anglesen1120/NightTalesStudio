# 08 — 90-Day Operations and Dogfooding (M3 onward)

> Contracts: `00-overview.md` §3 (success metrics), §11 (NFRs). Monitoring built in 09.9.3 and 11.6.2. Facts: `../DECISIONS.md` §6 (YPP rules), §10.4 (quota ceiling).
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-first, `00-overview.md` §4.1): P: 08.4–08.5 (flagship channel = the owner's channel in the Channel phase); S: 08.1–08.3. Where this plan says M1 for an S item, read M2.

## 1. Purpose and scope

**Goal:** run the platform as a service for 90 days after launch with clear cadence, KPIs, alerts and runbooks — and prove quality by running one flagship channel on it ("dogfooding").

**In scope:** operating cadence; KPIs and alert thresholds; runbooks; support process; feedback loop; flagship channel plan; 30/60/90-day checkpoints; policy watch.

**Out of scope:** paid marketing campaigns (separate plan); hiring.

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| O1 | A solo operator can be overwhelmed by alerts | Few, actionable alerts; weekly review instead of constant watching |
| O2 | The flagship channel is the best proof the product works — and the best early-warning for policy problems | 3 videos/week on one topic line; all through the product's own flow |
| O3 | YPP needs 1,000 subscribers + 4,000 watch hours (12 months) or 10M Shorts views (90 days) ✅ | Track progress weekly; no promises to users |
| O4 | Mass-produced/inauthentic content policy ✅ applies to users and to the flagship | Review sample of user output weekly; content-quality guidance in the product |
| O5 | Global YouTube quota 100/day ✅ | KPI + alert at 70 % daily usage; request extension early |

## 3. Requirements

- **FR-08-1** Weekly operating review with a fixed agenda and KPI snapshot.
- **FR-08-2** Runbooks for every alert in §5.2, stored in `docs/runbooks/`.
- **FR-08-3** Support inbox with response targets (§5.4).
- **FR-08-4** Feedback captured as backlog items with source and frequency.
- **FR-08-5** Flagship channel: 3 videos/week produced and published through the platform.
- **NFR-08-1** Each alert has an owner, a threshold, and a runbook link.

## 4. Epics and user stories

### Epic 08.1 — Cadence and KPIs

**08.1.1 KPI dashboard** — *As the owner I want one dashboard so that the weekly review takes 30 minutes.* Must · M
- Given data from 09/11/12, Then the dashboard shows the KPIs in §5.1 for the last 7 and 30 days with targets.
- Split into 08.1.1-T1 (BE read model + `GET /admin/kpis`) and 08.1.1-T2 (FE admin page); see `features/08-90-day-operations/README.md` Part B D7 (08 CR-6).

**08.1.2 Weekly review ritual** — *As the owner I want a fixed weekly agenda so that decisions are made on data.* Must · XS
- Agenda: KPIs → incidents → costs/margin → quota → support themes → policy news → top 3 backlog changes. Notes stored in `docs/ops/weekly/YYYY-WW.md`.

### Epic 08.2 — Alerts and runbooks

**08.2.1 Alert rules** — *As the operator I want alerts only for real problems so that I act on each one.* Must · S
- Given thresholds §5.2, Then alerts route to e-mail + phone push ⚠️ (tool TBD in 11.6.2); each links to its runbook.
- Given a security event (§5.2 "Security events"), Then it goes to the 11.6.2 alert pipeline **and** to every owner by e-mail sent with the e-mail configuration active before the change; a failed security e-mail is itself a P1 alert.

**08.2.2 Runbooks** — *As the operator I want step-by-step fixes so that incidents are handled calmly.* Must · M
- Given each scenario in §5.3, Then a runbook exists with: symptoms, checks, fix steps, user communication template (vi/en), and follow-up.
- Given the owner-recovery runbook, When it is rehearsed on staging, Then the one-shot `nts-admin` job (09.10.7) runs `nts owner recover --email` (sole owner: resets MFA, forces a password reset; with other owners: files a time-locked request) `nts owner restore --email`, and `nts owner resolve --request|--change`; the platform job audit shows the operator, the alert pipeline fires on the job start, all owners are e-mailed, and the time from start to a working owner login is recorded.

### Epic 08.3 — Support and feedback

**08.3.1 Support process** — *As a user I want help when stuck so that I keep using the product.* Must · S
- Given support e-mail + in-app "Help" link, Then tickets are tagged (billing, render, publish, account, content report) and answered within targets (§5.4); common answers become FAQ entries.
- Split into 08.3.1-T1 (Ops intake: alias, tags, SLA, macros) and 08.3.1-T2 (FE Help/FAQ page); see `features/08-90-day-operations/README.md` Part B D10 (08 CR-6).

**08.3.2 Feedback loop** — *As the owner I want user feedback in the backlog so that we build what matters.* Should · S
- Given tickets, interviews (5 beta users per month) and usage data, Then each theme becomes a backlog item with count and evidence; top items reviewed weekly.

### Epic 08.4 — Flagship channel (dogfooding)

**08.4.1 Channel setup** — *As the owner I want my own channel on the platform so that I experience what users experience.* Must · S
- Given one topic line first ([DECISION] Vietnamese ghost stories, landscape 8–12 min + 1 Short cut-down per episode ⚠️ Shorts from long video is a later feature), Then a series with a fixed style/voice exists and a 12-week content calendar is written.

**08.4.2 Production cadence** — *As the owner I want 3 videos/week so that the channel grows and the product is exercised.* Must · M (recurring, estimate covers setup)
- Given the calendar, Then each week: 3 briefs → generate → real human editing (≥ 15 minutes per script, logged) → render → review → schedule; time spent per step recorded to find UX pain.
- (08 CR-1) **Personal phase:** the flagship runs on autopilot (DECISIONS §10.12); the weekly routine is veto review + one sampled video/script audit + owner touch-time log; manual editing only for true crime or after a gate failure (`features/08-90-day-operations/README.md` D1–D3).

**08.4.3 Channel KPIs** — *As the owner I want channel metrics so that I learn what works.* Should · S
- Given YouTube Studio (manual export; we do not store YouTube analytics via API [DECISION] to avoid stored-data duties), Then weekly: views, CTR, average view duration, subscribers, watch hours vs YPP thresholds ✅.

### Epic 08.5 — Checkpoints

**08.5.1 30/60/90-day go/no-go** — *As the owner I want explicit checkpoints so that I stop or double down deliberately.* Must · XS
- (08 CR-1) **P (08.5.1-T1):** Gate 2 criteria + week-4/8/12 Channel-phase checkpoints, signed before public autopilot (`features/08-90-day-operations/README.md` D4). **S (08.5.1-T2):** the SaaS checkpoints below.
- Day 30: render success ≥ 95 %, no data-duty misses, margin ≥ 60 % → open sign-ups wider. Day 60: ≥ 30 % of activated users make a 2nd episode → start paid acquisition tests. Day 90: revenue ≥ infrastructure + tooling cost → continue; else revisit pricing/segment.

## 5. Technical design and reference tables

### 5.1 KPIs

| Area | KPI | Target (90 days) |
|---|---|---|
| Acquisition | Sign-ups/week; verified e-mail rate | growth; ≥ 70 % |
| Activation | % of verified users who download or publish 1 video within 7 days | ≥ 40 % |
| Retention | % making a 2nd episode within 30 days | ≥ 30 % |
| Reliability | Render success rate; p95 render time | ≥ 95 %; ≤ 30 min |
| Cost | Cost/episode (OpenAI + GPU); margin | ≥ 60 % margin |
| Capacity | YouTube quota used/day vs limit; GPU utilisation | < 70 % alert line |
| Compliance | Deletion requests completed before `due_at` | 100 % |
| Support | First response time | within targets §5.4 |
| Flagship | Watch hours (12 mo), subscribers | trend toward 4,000 h / 1,000 ✅ |

### 5.2 Alert thresholds (08 CR-9: `Source` column added)

| Alert | Threshold | Source |
|---|---|---|
| API 5xx rate | > 2 % for 10 min | 11.6.2-T1 |
| Render failure rate | > 10 % over last 20 jobs | 11.6.2-T1 |
| Render queue wait | > 30 min for oldest job | 11.6.2-T1 |
| Outbox lag | > 60 s | 11.6.2-T1 |
| OpenAI cost today | > 80 % of daily cap | 12.8.x |
| YouTube quota today | > 70 % used | 11.6.2-T1 |
| Deletion request | `due_at` < 24 h and not completed | 11.6.2-T1 |
| Token refresh failures | > 5 in 1 h | here (08.2.1-T1) |
| Payment webhook failures | any signature failure; > 3 processing errors/h; any `needs_review` payment older than 4 h | here (08.2.1-T1) |
| OpenAI runway (12.8.1) | < 7 days warning; < 3 days critical (trial grants pause) | 12.8.x |
| OpenAI `credit_balance_exhausted` ✅ | any occurrence → P1 (processing paused, 12.8.3) | 12.8.x |
| Margin per package (12.8.4) | < 50 % for 7 days | 12.8.x |
| Disk on render node | > 80 % | 11.6.2-T1 |
| Moderation events | > 20 per hour, or one user > 5 per day | here (08.2.1-T1) |
| Failed logins | > 100 per hour platform-wide, or refresh-token reuse detected | here (08.2.1-T1) |
| Post-deploy smoke test | any failure | here (08.2.1-T1) |
| Security events (D9) | any occurrence of: settings change, role change, MFA enrol/reset/disable, recovery-code use, owner login from a new IP, credit grant above the per-admin cap, env-lock change, settings rollback → notify owners; failed security e-mail → P1 | here (08.2.1-T2) |
| OAuth client rejected | any `invalid_client`/`unauthorized_client` in TokenHealthJob → P1 platform incident (never a user revoke) | here (08.2.1-T2) |
| Paid-volume anomaly | paid payments or amount in the 24 h after any `payment.*` setting change > 2× the same weekday average [DECISION] | here (08.2.1-T2) |
| Security e-mail failed | any failure to send a security event e-mail → P1 | here (08.2.1-T2) |

Shared rules: values owned by 11.6.2-T1/D17.

### 5.3 Runbook list

Worker down / no capacity · API error spike (5xx) · outbox lag · OpenAI outage or rate limiting · **OpenAI balance low or exhausted (top up, resume processing, 12.8)** · moderation spike or abuse · YouTube quota exhausted · YouTube/TikTok token refresh failures (incl. OAuth client rejected) · payment webhook failures, `needs_review` payments, paid-volume anomaly or chargeback · data deletion request near deadline · security incident (suspected key leak — incl. rotating the node-managed OpenAI image key in its separate OpenAI project and each node's `mpt-adapter` x-api-key, 09 §5.6 / 11.3 — account takeover, token reuse, unexpected security event) · **owner account recovery / break-glass** (keep ≥ 2 owners, or sealed offline recovery codes for a single owner; audited one-shot `nts-admin` job `nts owner recover|restore|resolve`, operator identity from the platform job audit, 09.10.7; owner disputes between two owners are settled only by the host operator; [DECISION] the operator acknowledges a `disputed` owner action, an objection or a held critical setting change within 24 h and decides within 72 h) · storage full or egress spike · bad deploy rollback. Every alert in §5.2 links to one of these 14 runbooks.

**Weekly money routine (owner, 15 min, part of 08.1.2):** open `/admin/funding` → check runway ≥ 14 days → top up OpenAI by the set-aside gap (12.8.2) → record the top-up and the new balance snapshot → check margin per package.

### 5.4 Support targets

| Type | First response |
|---|---|
| Account locked / payment taken but no credits | 8 business hours |
| Render or publish failure | 1 business day |
| Content report (takedown) | 72 h (13) |
| General question | 2 business days |

## 6. Task breakdown

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 08.1.1-T1 | KPI read model + `GET /admin/kpis` (08 CR-6) | BE | S | 09.9.1, 09.9.3, 12.5.2 |
| 08.1.1-T2 | Admin KPI dashboard page (08 CR-6) | FE | S | 08.1.1-T1, 10.9.1-T0, 10.9.1-T1 |
| 08.1.2-T1 | Weekly review template | BA | XS | — |
| 08.2.1-T1 | Configure alert rules | Ops | S | 11.6.2 |
| 08.2.1-T2 | Security-event alerts + owner e-mail with pre-change config, failed-e-mail P1, OAuth-client and paid-volume alerts | Ops | XS | 08.2.1-T1, 09.10 |
| 08.2.2-T1 | Write 14 runbooks | Ops | M | 09, 11, 12, 13 |
| 08.2.2-T2 | Break-glass rehearsal on staging + sealed recovery-code procedure | Ops | XS | 08.2.2-T1, 09.10.7 |
| 08.3.1-T1 | Support intake: alias, tags, SLA times, macros (08 CR-6) | Ops | XS | 09.10.5 |
| 08.3.1-T2 | Help/FAQ page + in-app Help link (08 CR-6) | FE | XS | 08.3.1-T1, 10.3.1-T2 |
| 08.4.1-T1 | Flagship series + 12-week calendar | BA | S | 02.8.3, 13.4.2 (approved) |
| 08.4.2-T1 | Weekly production (recurring) | BA | M | 08.4.1 |
| 08.4.3-T1 | Weekly channel KPI sheet | BA | S | 08.4.2 |
| 08.4.4-T1 | Takedown runbook: flagged/reported autopilot video → pause series, private/remove in YouTube Studio within 24 h, record cause | BA | XS | 02.8.2-T2 |
| 08.5.1-T1 | Gate 2 criteria + week-4/8/12 Channel-phase checkpoints (P; 08 CR-1) | BA | XS | — |
| 08.5.1-T2 | SaaS 30/60/90-day checkpoint criteria + calendar reminders (S; 08 CR-1) | BA | XS | 08.5.1-T1 |

## 7. Test plan

- Fire each alert on staging (synthetic condition) and follow its runbook end-to-end once; for security events, also confirm the owner e-mail arrives after changing the e-mail settings themselves.
- Game day before launch: worker down + OpenAI 429 + quota exhausted simultaneously; measure time to stable.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Operator burnout | Weekly cadence, few alerts, runbooks, automation of routine checks |
| Sole owner locked out (lost phone, MFA) | ≥ 2 owners or sealed offline recovery codes; break-glass CLI rehearsed (08.2.2-T2) |
| Flagship channel underperforms | Treat as product research; iterate style/voice; do not use it as a marketing promise |
| Policy change on AI content | Monthly check of YouTube/TikTok policy pages; adjust product guidance |

## 9. Exit criteria

- All runbooks written and tested once; dashboard live; 12 weeks of reviews recorded; 36 flagship videos published; 30/60/90 decisions documented.

## 10. Open questions and contract change requests

- ⚠️ YouTube ad-suitability for horror/true crime (DECISIONS §8) — track flagship ad status as evidence.
- ⚠️ Alerting/on-call tool choice (11.6.2).
- [OPEN] Back-office automation with jev-ultrafast (DECISIONS §10.10) for provider portals **without an API** (e.g. a payment provider's daily settlement report), on our own account only and only where the provider's terms allow automated access; decide after the payment provider is chosen (12). Never for Google, YouTube or TikTok.
- **Contract changes requested (not yet in 00):** none for 00; depends on 09.10.7 (break-glass CLI) and the 11.6.2 alert pipeline accepting security events.

## 11. Estimate summary

| Epic | Days |
|---|---|
| 08.1 Cadence + KPIs | 1.25 |
| 08.2 Alerts + runbooks | 2.0 |
| 08.3 Support + feedback | 1.0 |
| 08.4 Flagship setup | 2.25 |
| 08.5 Checkpoints | 0.5 |
| **Total** | **7.0** → with 20 % buffer **8.4** person-days (08 CR-1: was 6.75 / 8.1; P 2.5, S 4.5) (setup; weekly operation time excluded) |
