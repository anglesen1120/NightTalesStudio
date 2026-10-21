# 08 — 90-Day Operations and Dogfooding (M3 onward)

> Contracts: `00-overview.md` §3 (success metrics), §11 (NFRs). Monitoring built in 11.6.2. Facts: `../DECISIONS.md` §6 (YPP rules), §10.4 (quota ceiling).
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-only, `00-overview.md` §4.1, DEC 10.14): all P — 08.4 (flagship channel = the owner's channel) and 08.5 (checkpoints). The former S epics 08.1–08.3 (KPI admin dashboard, alert-rule/admin alert tasks, support and feedback) are dropped; the technical alert thresholds and runbook list stay as reference for 11.6.2.

## 1. Purpose and scope

**Goal:** run the owner's own channel on the product for 90 days with a clear cadence and explicit checkpoints — and prove quality by running one flagship channel on it ("dogfooding", DEC 10.14: personal product, no service operation).

**In scope:** flagship channel plan and production cadence; week-4/8/12 checkpoints; policy watch; the technical alert thresholds (§5.2) and runbook list (§5.3) that 11.6.2-T1 monitors.

**Out of scope:** paid marketing campaigns; hiring; multi-user support operations (dropped with 08.1–08.3, DEC 10.14).

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| O1 | A solo operator can be overwhelmed by alerts | Few, actionable alerts; the owner checks the pipeline weekly instead of watching it |
| O2 | The flagship channel is the best proof the product works — and the best early-warning for policy problems | 3 videos/week on one topic line; all through the product's own flow |
| O3 | YPP needs 1,000 subscribers + 4,000 watch hours (12 months) or 10M Shorts views (90 days) ✅ | Track progress weekly; no monetisation promise |
| O4 | Mass-produced/inauthentic content policy ✅ applies to the flagship channel too | Varied structure + originality rubric (02) and a sampled review of the owner's own output weekly |
| O5 | Global YouTube quota 100/day ✅ | KPI + alert at 70 % daily usage; request extension early |

## 3. Requirements

- **FR-08-1** Flagship channel: 3 videos/week produced and published through the platform.
- **FR-08-2** A 12-week content calendar and a per-episode production log (time per step) so UX pain is visible.
- **FR-08-3** Explicit week-4/8/12 go/no-go checkpoints, decided and recorded by the owner (the former 30/60/90-day SaaS criteria, `08.5.1-T2`, are dropped with phase S, DEC 10.14).
- **FR-08-4** Monthly policy watch of the YouTube/TikTok developer and content policies; a change updates the prompts and the content rules.
- **NFR-08-1** Each alert in §5.2 has a threshold, a runbook link and a named owner (the owner of the product).

## 4. Epics and user stories

### Epic 08.1 — Cadence and KPIs (dropped)

Epic 08.1 (08.1.1 KPI read model + `/admin/kpis` dashboard, 08.1.2 weekly review ritual) is dropped with the admin surface (DEC 10.14). The owner's own cadence is Epic 08.4.

### Epic 08.2 — Alerts and runbooks (dropped as tasks)

The 08.2 alert-rule tasks (08.2.1-T1/T2) and the runbook tasks (08.2.2-T1/T2) are dropped (DEC 10.14: no admin alerts surface, no owners, no break-glass CLI). The technical alert thresholds and the runbook list stay in §5.2/§5.3 as the source for 11.6.2-T1, and the owner keeps those runbooks as operating notes.

### Epic 08.3 — Support and feedback (dropped)

Epic 08.3 (support inbox, help/FAQ page, feedback loop) is dropped: there are no users to support (DEC 10.14).

### Epic 08.4 — Flagship channel (dogfooding)

**08.4.1 Channel setup** — *As the owner I want my own channel on the platform so that I experience what users experience.* Must · S
- Given one topic line first ([DECISION] Vietnamese ghost stories, landscape 8–12 min + 1 Short cut-down per episode ⚠️ Shorts from long video is a later feature), Then a series with a fixed style/voice exists and a 12-week content calendar is written.

**08.4.2 Production cadence** — *As the owner I want 3 videos/week so that the channel grows and the product is exercised.* Must · M (recurring, estimate covers setup)
- Given the calendar, Then each week: 3 briefs → generate → real human editing (≥ 15 minutes per script, logged) → render → review → schedule; time spent per step recorded to find UX pain.
- (08 CR-1) **Personal phase:** the flagship runs on autopilot (DECISIONS §10.12); the weekly routine is veto review + one sampled video/script audit + owner touch-time log; manual editing only for true crime or after a gate failure (`features/08-90-day-operations/README.md` D1–D3).

**08.4.3 Channel KPIs** — *As the owner I want channel metrics so that I learn what works.* Should · S
- Given YouTube Studio (manual export; we do not store YouTube analytics via API [DECISION] to avoid stored-data duties), Then weekly: views, CTR, average view duration, subscribers, watch hours vs YPP thresholds ✅.

### Epic 08.5 — Checkpoints

**08.5.1 Week-4/8/12 go/no-go** — *As the owner I want explicit checkpoints so that I stop or double down deliberately.* Must · XS
- (08 CR-1) **08.5.1-T1:** Gate 2 criteria + week-4/8/12 Channel-phase checkpoints, signed before public autopilot (`features/08-90-day-operations/README.md` D4). The former S task 08.5.1-T2 (SaaS 30/60/90-day criteria: sign-ups, activated users, revenue) is dropped (DEC 10.14).
- Week 4: render success ≥ 95 %, no data-duty misses, cost per episode inside the spend cap → keep the cadence. Week 8: the owner's touch time per episode is falling and the veto rate is low → keep autopilot. Week 12: the channel's watch hours and subs trend toward 4,000 h / 1,000 ✅ and ad status is acceptable → continue; else revisit topic/style/voice.

## 5. Technical design and reference tables

### 5.1 KPIs

| Area | KPI | Target (90 days) |
|---|---|---|
| Reliability | Render success rate; p95 render time | ≥ 95 %; ≤ 30 min |
| Cost | Cost/episode (OpenAI + GPU) vs the owner's spend cap | inside the cap, no surprise overrun |
| Capacity | YouTube quota used/day vs limit; GPU utilisation | < 70 % alert line |
| Compliance | Deletion requests completed before `due_at` | 100 % |
| Owner effort | Owner touch time per episode (veto review + sampled audit) | falling month over month |
| Flagship | Watch hours (12 mo), subscribers | trend toward 4,000 h / 1,000 ✅ |

### 5.2 Alert thresholds (08 CR-9: `Source` column added)

| Alert | Threshold | Source |
|---|---|---|
| API 5xx rate | > 2 % for 10 min | 11.6.2-T1 |
| Render failure rate | > 10 % over last 20 jobs | 11.6.2-T1 |
| Render queue wait | > 30 min for oldest job | 11.6.2-T1 |
| Outbox lag | > 60 s | 11.6.2-T1 |
| OpenAI spend today | > 80 % of the owner's daily spend cap | 11.6.2-T1 |
| YouTube quota today | > 70 % used | 11.6.2-T1 |
| Deletion request | `due_at` < 24 h and not completed | 11.6.2-T1 |
| Token refresh failures | > 5 in 1 h | 11.6.2-T1 |
| OpenAI `credit_balance_exhausted` ✅ | any occurrence → P1 (paid dispatch paused, 02.8.3 pause rule) | 11.6.2-T1 |
| Disk on render node | > 80 % | 11.6.2-T1 |
| Moderation events | > 20 per hour | 11.6.2-T1 |
| Failed logins | > 100 per hour, or refresh-token reuse detected | 11.6.2-T1 |
| Post-deploy smoke test | any failure | 11.6.2-T1 |
| Security e-mail failed | any failure to send a security event e-mail → P1 | 11.6.2-T1 |
| OAuth client rejected | any `invalid_client`/`unauthorized_client` in TokenHealthJob → P1 platform incident (never a user revoke) | 11.6.2-T1 |

Shared rules: values owned by 11.6.2-T1/D17.

### 5.3 Runbook list

Worker down / no capacity · API error spike (5xx) · outbox lag · OpenAI outage or rate limiting · **OpenAI balance low or exhausted (top up, resume processing)** · moderation spike or abuse · YouTube quota exhausted · YouTube/TikTok token refresh failures (incl. OAuth client rejected) · data deletion request near deadline · security incident (suspected key leak — incl. rotating the node-managed OpenAI image key in its separate OpenAI project and each node's `mpt-adapter` x-api-key, 09 §5.6 / 11.3 — account takeover, token reuse, unexpected security event) · **owner account recovery** (single owner: sealed offline recovery codes plus a documented MFA reset on the host; no break-glass CLI, the former 09.10.7 task is dropped by DEC 10.14) · storage full or egress spike · bad deploy rollback. Every alert in §5.2 links to one of these 13 runbooks. The owner keeps them as operating notes under `docs/runbooks/`; the former 08.2.2 tasks that produced them are dropped (DEC 10.14).

**Monthly spend routine (owner, 15 min):** check the OpenAI project balance and the daily/monthly spend cap on `usage_records` → top up the OpenAI project directly → adjust the cap if the cadence changed → confirm no pause was caused by `credit_balance_exhausted` (02.8.3).

## 6. Task breakdown

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 08.4.1-T1 | Flagship series + 12-week calendar | BA | S | 02.8.3, 13.4.2 (approved) |
| 08.4.2-T1 | Weekly production (recurring) | BA | M | 08.4.1 |
| 08.4.3-T1 | Weekly channel KPI sheet | BA | S | 08.4.2 |
| 08.4.4-T1 | Takedown runbook: owner-flagged autopilot video (no public report intake, DEC 10.14) → pause series, private/remove in YouTube Studio within 24 h, record cause | BA | XS | 02.8.2-T2 |
| 08.5.1-T1 | Gate 2 criteria + week-4/8/12 Channel-phase checkpoints (08 CR-1) | BA | XS | — |

## 7. Test plan

- Fire each alert in §5.2 on staging (synthetic condition) and follow its runbook end-to-end once as the 11.6.2 alert pipeline is stood up; for security e-mail, confirm the owner e-mail arrives after changing the e-mail configuration itself.
- Game day before launch: worker down + OpenAI 429 + quota exhausted simultaneously; measure time to stable.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Operator burnout | Weekly cadence, few alerts, runbooks, automation of routine checks |
| Sole owner locked out (lost phone, MFA) | Sealed offline recovery codes and a documented MFA reset on the host (single owner, DEC 10.14) |
| Flagship channel underperforms | Treat as product research; iterate style/voice; no marketing promise |
| Policy change on AI content | Monthly check of YouTube/TikTok policy pages; adjust prompts and content rules |

## 9. Exit criteria

- 12 weeks of the flagship cadence recorded; 36 flagship videos published; week-4/8/12 decisions documented; the owner-held runbooks tested once.

## 10. Open questions and contract change requests

- ⚠️ YouTube ad-suitability for horror/true crime (DECISIONS §8) — track flagship ad status as evidence.
- ⚠️ Alerting tool choice (11.6.2).
- **Contract changes requested (not yet in 00):** none for 00; the order of §5.2 thresholds is accepted by the 11.6.2 alert pipeline.

## 11. Estimate summary

| Epic | Days |
|---|---|
| 08.4 Flagship setup | 2.25 |
| 08.5 Checkpoints | 0.25 |
| **Total** | **2.5** → with 20 % buffer **3.0** person-days (all P; 08 CR-1 was 6.75 / 8.1 and 7.0 / 8.4 with the dropped S epics 08.1–08.3 = 4.25 and 08.5.1-T2 = 0.25) (setup; weekly operation time excluded) |
