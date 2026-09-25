# 08.3 — Support and feedback (S slice)

> Part of plan 08 (S slice). Contract: `../README.md` Part B — §B1 (K12, K13), §B3 (D10), §B4 (CR-6, CR-12, CR-13), §B8.1. High-level: `../../../08-90-day-operations.md` §4 (08.3.1, 08.3.2), §5.4 (support targets).
> Phase: **S · M2**.

## 1. Scope

Support process and feedback capture for the beta: one channel with a tag taxonomy and SLA clocks, bilingual macros, a public FAQ page with an in-app Help link, and a monthly interview loop that turns feedback into backlog items with counts and evidence.

In: the `support@<domain>` alias configuration and process doc, the fixed tag set, SLA targets per tag, vi/en macros, the weekly SLA log, the public `/{locale}/help` FAQ page and the in-app Help link, the interview script, and `docs/ops/backlog.md`.

Out: **any impersonation or view-as-user** (prohibited by 09 §284 and 13 §115 — D10); any ticket table, ticket API or help-desk integration (a new table would need its own migration, retention and deletion plan for a beta); any content or attachment storage; any auto-reply; marketing pages (10 owns landing/pricing).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| K12 | No impersonation [DECISION]: content is visible to admins only from a moderation event or a report case, and each view writes `audit_logs` (`content_viewed`) | `../../../09-backend-api.md:284`; `../../../13-compliance.md:115` ✅ |
| K13 | `audit_logs.category` includes `support`; admin mutations require a reason and write a row | `../../../00-overview.md:307`; `09:285` ✅ |
| K21 | Public web surfaces are S; the public shell, i18n and the axe pass come from 10 (`10.3.1-T2`, `10.10.1-T1`); `/help` goes into the SD2 prerender list + sitemap | `10 README:3,146`; Lead10S 2026-09-25 ✅ |
| K23 | 09.10.5-T1 gives the audited staff actions (reason + `audit_logs` category `support`, `visibility = internal`) | 09 S lead 2026-09-25 ✅ |
| K20 | The registered domain comes from 11.2.1-T1; the alias is `support@<domain>` | `11 README:253` ✅ |
| K22 | Support medians are not derivable from the database, so the KPI `support_*` keys are `manual` and read from the SLA log | `../../README.md` §B3 D7 ✅ |

## 3. Domain and data

No table, no migration. All support state lives outside the domain: the provider mailbox, `docs/support/sla/YYYY-WW.md` (rows `ticket | tag | opened_at | first_response_at | first_response_minutes | sla_target | breach | theme`) and `docs/support/faq.vi.md` / `faq.en.md`. Feedback lives in `docs/ops/backlog.md` (`id | theme | source | count | evidence | first_seen | week_added | last_reviewed | decision | reason`) and interview records in `docs/ops/interviews/`. The only platform reads are 09.10.5-T1's admin user view (during a ticket) and the audit trail; nothing is stored back. Deliberately no personal data beyond what the user writes to support: no story text, no attachments, no token.

## 4. Use cases

| # | Use case | Story |
|---|---|---|
| UC1 | A user clicks Help, lands on `/{locale}/help`, reads the FAQ, and opens a prefilled `mailto:` carrying only ids/version/locale | 08.3.1 |
| UC2 | An admin triages a ticket, tags it, answers within the tag's target from a macro, and the first-response time lands in the weekly SLA log | 08.3.1 |
| UC3 | A user reports abuse; the tag becomes `content_report`, the 72 h target applies, and the flow hands off to the `reports`/moderation path | 08.3.1 |
| UC4 | A question that repeats becomes an FAQ entry and a macro in the same week | 08.3.1 |
| UC5 | Five beta users are interviewed per month; each theme becomes a backlog row with count and evidence, and the weekly review picks the top three | 08.3.2 |

## 5. API and events

**None.** No route, no DTO, no event is added by this folder; the Help page is static content and the mailto link is built client-side. The only cross-boundary contract is the link/query-key set (D10): `subject=[nts:<tag>] …` and `body` with `episode`, `renderJob`, `publication`, `appVersion`, `uiLocale` — ids, version and locale only; a unit test asserts that no other key (and no token-looking value) can be produced. Support interactions on the platform go through 09.10.5-T1's existing admin mutations with a reason.

## 6. UI

`/{locale}/help` — public, prerendered (10's SD2 list + sitemap, vi + en), sections: getting started, credits/billing, publishing, render/video, account and security, content reports; every section ends with the support button. In-app: a `help` nav entry and the `mailto:` builder in `shared/util/support-link.ts` (pure function, unit-tested), with the address also shown as copyable text for clients where mailto fails. Accessibility: 10.10.1-T1's axe pass (WCAG 2.1 AA), heading order, no colour-only meaning, no form. Content source of truth for review: `docs/support/faq.{vi,en}.md`, mirrored into the page's i18n content in the same pull request.

## 7. Jobs and integrations

No job. Integrations: the e-mail provider's shared alias (11.2.1-T1's provider; no forwarding, spam filter on, no auto-reply, 180-day retention, attachments rejected), 10's web shell for the page and the link, 09.10.5-T1 for staff actions, 13's report flow (`reports`, 72 h) for the `content_report` tag, and 08.1.2-T1's weekly review, which consumes the SLA log and the backlog.

## 8. Security and privacy

Support staff see account, billing and session data for support and abuse handling; story text, sources and videos only inside a moderation or report case, and every such view is audited (K12). **No impersonation exists** — no route, cookie, token or flag (D10, F7); the process doc and the macros state it, and the FAQ/macros never ask for a password, TOTP code or recovery code. The Help link carries no token and no content, so a leaked mailto cannot leak a session. Mailbox data minimisation: ids only, no attachments, 180-day retention, no forwarding to a personal mailbox, and interview notes are pseudonymous (ids, never names or story text). No new Data Protection purpose is introduced.

## 9. Test plan

- Unit (FE): `support-link.ts` emits exactly the pinned query keys, URL-encodes ids, never includes a token-shaped value, and falls back to the plain address.
- E2E (FE): `/vi/help` and `/en/help` render, are in the sitemap, and pass `@axe-core/playwright` (10.10.1-T1); the nav entry opens the page; the support button's `href` matches the builder's output.
- Process (manual acceptance): a test message to the alias is triaged, tagged and answered within the target, and the SLA log row is complete; a `content_report` ticket reaches the 13 flow; a credentials-request attempt from a user (someone sends their password) is handled by the documented macro + forced password reset via 09.10.5-T1.
- Review: the macros are read once for forbidden prompts; the FAQ headings and the macro list are checked against the tag set (reviewer, not CI — no source-text test).
- Feedback: after the first month, `docs/ops/backlog.md` holds ≥ 5 themes with counts and evidence and 5 interview records with consent lines.

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 08.3.1-T1 | XS (0.25) | Ops | 09.10.5-T1 (soft), 08.1.2-T1 (soft), 08.2.2-T1 | 08.3.1-T2, 08.3.2-T1, 08.1.1-T1 (soft) | S · M2 · flag `no-impersonation` |
| 08.3.1-T2 | XS (0.25) | FE | 08.3.1-T1, 10.3.1-T2 (soft) | — | S · M2 |
| 08.3.2-T1 | S (0.5) | BA | 08.3.1-T1 | — | S · M2 |

**Estimate:** 1.0 d (0.25 + 0.25 + 0.5) = `../README.md` §B8.1 = `../../../08-90-day-operations.md` §11 epic 08.3.

## 11. Rollout

Milestone **M2**, before the beta cohort grows: support must exist the day the first stranger signs up. Order: 08.3.1-T1 (process + alias) → {08.3.1-T2 (page), 08.3.2-T1 (feedback loop)}. Preconditions: the domain/alias is available from 11.2.1-T1 (else the process runs on a temporary alias and the change is recorded); 10's public shell ships the route (CR-13). Rollback: none needed (no platform state); the alias and the page are removed/redirected, and the SLA log stays as the historical record. Success: every beta ticket has a tag and a first-response time, and the weekly review shows a top-3 backlog list that came from real feedback.
