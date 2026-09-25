# 13 — Compliance: Legal Pages, Data Rights, Verification and Audits (M1–M3)

> Contracts: `00-overview.md` §6 (`user_consents`, `data_deletion_requests`, `audit_logs`), §11 (privacy NFR). Platform duties: 06 (YouTube), 07 (TikTok). Facts: `../DECISIONS.md` §6, §10.6, §10.7 and the official pages cited (✅).
> This plan is **not legal advice**. Every legal text must be reviewed by a qualified lawyer before launch.
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-first, `00-overview.md` §4.1): S (after Gate 2) except the P slice needed for the YouTube audit: 13.1.1-T1 / 13.1.2-T1 (privacy policy and terms drafts, Developer Policies III.A; personal version covers the owner's tool, lawyer review before any other user at M2), 13.2.1-T1 (versioned acceptance, shown before the owner first connects YouTube or enables autopilot, III.A.2), 13.3.1-T1 (deletion engine for the 7/30-day duties, III.D.2) and 13.4.2 YouTube audit submission, filed in the personal phase as soon as 06 upload works and the pages are live. Where this plan says M1 for an S item, read M2.

Sources: [YouTube API Services Developer Policies](https://developers.google.com/youtube/terms/developer-policies) · [YouTube API audit form](https://support.google.com/youtube/contact/yt_api_form) · [TikTok Content Sharing Guidelines](https://developers.tiktok.com/doc/content-sharing-guidelines).

## 1. Purpose and scope

**Goal:** the platform can legally process users' data, publish to their channels, and pass Google and TikTok reviews — with deadlines enforced by code, not memory.

**In scope:** privacy policy, terms of service, acceptable-use/content policy, cookie notice; consent capture; data export; deletion workflows with SLA timers; Google OAuth verification; YouTube API audit + quota extension; TikTok audit (R2); AI-content labelling; evidence packs; compliance calendar; Vietnamese personal-data law review ⚠️.

**Out of scope:** tax/e-invoice (12, accountant); trademark registration (Could).

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| C1 | YouTube policies require a privacy policy describing access/storage/sharing/deletion, the revoke link, and a contact ✅ | Checklist §5.1 |
| C2 | Deletion deadlines differ by trigger (7 days in-app revoke/user request/account delete; 30 days Google-page revoke; 30-day max for stored Authorized Data) ✅ | `data_deletion_requests.due_at` per reason; alerting |
| C3 | Uploads need the user's prior specific and express consent ✅ | Per-video consent (06.2.2) — documented in the policy |
| C4 | Users' channels risk demonetisation under the mass-produced content policy ✅ (DECISIONS §6) | ToS: no monetisation guarantee; product enforces review checkpoints |
| C5 | True crime → defamation/privacy of real people | Content policy + attestation (02.3.3) + takedown process |
| C6 | Vietnam's Law 91/2025/QH15 (Personal Data Protection Law) + Decree 356/2025/ND-CP apply to us (13 CR-9); exact obligations under them not yet mapped ⚠️ | Lawyer review task 13.6.1 (memo + backlog); do not cite an obligation until confirmed |
| C7 | Google OAuth verification timeline for `youtube.upload` unknown ⚠️ | SaaS only: file at mid-M2 once multi-user connect runs and the lawyer-reviewed pages are live (00 §4); the owner's app runs unverified in production |
| C8 | Audits need screencasts of flows that must already exist | Build 10.7/06 flows first, then record |

## 3. Requirements

- **FR-13-1** Public, versioned Privacy Policy, Terms of Service, Content Policy in vi and en; each version has an id and effective date.
- **FR-13-2** Registration records acceptance of current ToS + Privacy versions (`user_consents`); material changes require re-acceptance at next login.
- **FR-13-3** Data export: user receives a machine-readable archive (JSON + their media links) within 7 days [DECISION: same as deletion window].
- **FR-13-4** Deletion workflows for account, channel disconnect and user requests, with `due_at` per C2 and completion evidence.
- **FR-13-5** Takedown/report channel (e-mail + form) with response target 72 h [DECISION].
- **FR-13-6** Audit/verification submissions filed with evidence packs and tracked in a compliance calendar.
- **FR-13-7** AI disclosure: YouTube `containsSyntheticMedia = true` ✅ (06); TikTok `is_aigc = true` ✅ (07); ToS requires users to keep disclosures on.
- **NFR-13-1** 100 % of deletion requests completed before `due_at`; alert at `due_at − 24 h`.
- **NFR-13-2** Legal pages live before any external beta user (M2).

## 4. Epics and user stories

### Epic 13.1 — Legal documents

**13.1.1 Privacy Policy** — *As a user I want to know what happens to my data so that I can trust the platform.* Must · M
- Given the checklist §5.1, When the draft is written and lawyer-reviewed, Then every item is covered in both languages and published at `/privacy` with version and date.

**13.1.2 Terms of Service** — *As the owner I want clear terms so that liability and rules are defined.* Must · M
- Given §5.2, Then the ToS covers account rules, credits/refunds (link to 12), AI-generated content ownership and disclosure, no monetisation guarantee, user responsibility for published content and platform rules, limitation of liability, termination, governing law ⚠️ (lawyer).

**13.1.3 Content Policy** — *As the platform I want explicit content rules so that moderation decisions are consistent.* Must · S
- Given §5.3, Then prohibited and restricted content is listed with examples (e.g. realistic depiction of real victims, accusations beyond sources, sexual content, content involving minors in harm, hate, copyrighted material).

**13.1.4 Cookie notice** — *As a visitor I want to know about cookies so that I can choose.* Should · S
- Given only essential cookies (auth refresh, CSRF, language) in MVP, Then a notice (not a consent wall) lists them; if analytics is added later, a consent banner becomes Must.

### Epic 13.2 — Consent and versioning

**13.2.1 Policy versions and re-acceptance** — *As the owner I want proof of what each user accepted so that disputes are resolvable.* Must · S
- Given a new material version, When a user logs in, Then a blocking dialog shows the change summary and requires acceptance; `user_consents` stores version, time, IP.

### Epic 13.3 — Data rights workflows

**13.3.1 Deletion requests engine** — *As the platform I want deletions tracked with deadlines so that we never miss a legal window.* Must · M
- Given a trigger (account delete, in-app channel revoke, Google-page revoke detected, user request), Then a `data_deletion_requests` row with `reason` and `due_at` (7 or 30 days ✅) is created; `DataDeletionJob` (hourly) executes scoped deletions; completion writes `completed_at` and an audit entry; alert at `due_at − 24 h`. 02 CR-22: an episode-scope deletion also deletes the extracted-text objects under `research/{userId}/{episodeId}/` (DB rows of `story_sources`, `fetch_tasks`, `source_candidates`, `generation_runs`, `fact_sheets`, `story_versions` cascade with the episode; storage objects do not).

| 13.3.1-T1 | Deletion requests engine | BE | M | 09.2 |

|**13.3.2 Account deletion** — *As a user I want to delete my account so that my data is gone.* Must · M
- Given `POST /me/delete` (with password re-auth), Then sessions revoked, channels disconnected (tokens revoked), publications cancelled, media and extracted source texts (every object under `research/{userId}/`, 02 CR-22) deleted from storage, personal fields anonymised; ledger/payments kept only as legally required ⚠️ (accountant) with the user reference pseudonymised.

Given database and storage backups, Then deleted data is not removed from existing backups but ages out within the backup retention window ([DECISION] 30 days, 11.6.1); a restore from backup re-runs every `data_deletion_requests` row completed after the backup date before the service reopens, and the confirmation e-mail states the 30-day backup window.

**13.3.4 Takedown/report handling** — *As a third party I want to report harmful content so that it is removed.* Must · S
- Given `/report` form or e-mail, Then an admin ticket is created; admin can suspend an episode/user; outcome logged.

### Epic 13.4 — Platform verification and audits

**13.4.1 Google OAuth verification** — *As the owner I want the OAuth app verified so that any user can connect YouTube.* Must · M
- Given the production consent screen (06.1.1), When verification is requested ⚠️ (requirements per Google's current process), Then the submission includes domain ownership, privacy policy URL, scope justification per scope, demo video; status tracked in the calendar.

**13.4.2 YouTube API Compliance Audit + quota extension** — *As the owner I want the audit passed so that uploads can be public and quota can grow.* Must · M
- Given the 06.5.1 evidence pack, When filed via the audit form ✅, Then answers match actual behaviour (consent, retention, deletion windows ✅); requested quota is justified from the owner's autopilot cadence and daily cap at the M1 filing, and from beta data (uploads/user/day × users) at the M2 re-filing.

**13.4.3 TikTok audit (R2)** — *As the owner I want TikTok's audit passed so that posts can be public.* Must (R2) · S
- Given 07.4.1, Then the submission argues intended use (public creators, not an internal tool ✅) and shows every required UX item ✅.

### Epic 13.5 — Evidence and calendar

**13.5.1 Evidence pack** — *As the owner I want reusable evidence so that re-audits are quick.* Must · S
- Given staging, Then screenshots/screencasts of: registration consent, connect, publish form, per-video consent, disconnect + revoke explanation, deletion request, export; stored with dates and app version.

**13.5.2 Compliance calendar** — *As the owner I want deadlines tracked so that nothing lapses.* Must · XS
- Given a calendar (table in `docs/compliance-calendar.md`), Then entries: audit submissions/renewals (YouTube re-audit cadence ⚠️), policy review every 6 months, restore drill monthly (11), key rotation, deletion SLA monthly report.

### Epic 13.6 — Local law

**13.6.1 Vietnamese data-protection review** — *As the owner I want local obligations confirmed so that we operate lawfully in Vietnam.* Must · S (+ external lawyer time)
- Given a lawyer consultation on Law 91/2025/QH15 + Decree 356/2025/ND-CP (13 CR-9), Then a memo lists applicable obligations (e.g. consent form, notices, data-processing records, cross-border transfer rules, breach notification), and required product changes; items become backlog stories (memo + backlog, no direct code task here). Until then all such claims stay ⚠️.

## 5. Technical design and content checklists

### 5.1 Privacy Policy checklist (✅ items are from YouTube developer policies)

- Who we are, contact for privacy questions/complaints ✅.
- Data collected: account data, content created, channel connection data (YouTube/TikTok ids, names, tokens), usage/billing data, logs.
- Data from YouTube API Services: what is accessed, stored, shared, and how it is used ✅; statement that we use YouTube API Services and link to YouTube Terms and Google Privacy Policy ⚠️ (confirm exact required links in the policy text).
- How users revoke access: in-app disconnect and https://security.google.com/settings/security/permissions ✅.
- Retention and deletion: 7-day and 30-day windows by trigger; 30-day refresh/delete of stored Authorized Data ✅; backups are kept [DECISION] 30 days, so deleted data (including encrypted channel tokens) disappears from backups within 30 days of deletion and is never restored into the live service.
- Upload consent: every upload has its own consent record — given by the user, or for autopilot series derived from the owner's versioned series authorization after a veto window in which the owner is notified and can edit or cancel (06.2.2).
- We never ask for YouTube passwords ✅.
- Sub-processors: OpenAI (story/image generation), hosting, storage, payment provider, e-mail provider.
- Staff access: admins see account, billing and session data for support and abuse handling; story text, sources and videos only inside a moderation or report case; every such view is logged (09.10.5); no impersonation; security alerts about staff and account actions (role changes, MFA resets, settings changes, 08 §5.2) go only to internal owners/on-call and are not shared with third parties.
- International transfers, security measures, user rights (access, correction, deletion, export), children (service not for under-18 [DECISION] ⚠️ lawyer), changes to the policy.

### 5.2 Terms of Service checklist

Eligibility (18+), account security, credits (non-cash, expiry per 12.6.2 [DECISION] 12 months ⚠️ lawyer, refunds per 12.7.5 incl. the spent-bonus deduction), acceptable use (link Content Policy), ownership (user owns outputs subject to provider terms ⚠️ check OpenAI terms on output ownership), AI disclosure duty, no monetisation guarantee, user is the publisher of record on their channels, platform rules compliance (YouTube/TikTok), takedown and suspension, service availability, liability limits, governing law/disputes ⚠️.

### 5.3 Content Policy checklist

Prohibited: realistic images of identifiable real people; accusations not supported by cited sources; content sexualising anyone; harm to minors; glorifying violence or self-harm; hate/harassment; copyrighted text/music/footage; impersonation. Restricted (allowed with care): real historical cases with sources and respectful tone; religious/spiritual content without targeting groups.

### 5.4 Deletion scopes

| Scope | Deleted | Kept (pseudonymised) |
|---|---|---|
| Channel (in-app revoke / Google revoke) | tokens, channel metadata, platform payloads, external ids | publication rows without external ids |
| Account | profile, series, episodes, story versions, assets, channels, sessions, user-visible activity rows | ledger/payments ⚠️ (legal retention), security and staff-action audit rows with user id hashed, `user_consents` (13 CR-9: retained as proof of what was accepted, user id hashed) |
| Episode (user delete) | story versions, assets | usage_records (cost accounting) |
| Backups (all scopes above) | ageing out: deleted rows and objects leave database and storage backups within the [DECISION] 30-day backup retention; no backup is kept longer | nothing beyond the Kept column of the live scope |

## 6. Task breakdown

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 13.1.1-T1 | Draft privacy policy (vi/en) from checklist | Legal/BA | M | — |
| 13.1.1-T2 | Lawyer review + publish (privacy) — half of the shared 0.5 d engagement with 13.1.2-T2 (13 CR-9) | Legal | XS (+ external) | 13.1.1-T1 |
| 13.1.2-T1 | Draft ToS (vi/en), incl. the YouTube Terms clause (III.A.1) | Legal/BA | M | — (credit/refund sections from 12 added before M2) |
| 13.1.2-T2 | Lawyer review + publish (terms) — half of the shared 0.5 d engagement with 13.1.1-T2 (13 CR-9) | Legal | XS (+ external) | 13.1.2-T1 |
| 13.1.3-T1 | Draft Content Policy | BA | S | 02, 03 rules |
| 13.1.4-T1 | Cookie notice | FE | S | 10.3.1 |
| 13.2.1-T1 | Policy versions + acceptance endpoint/gate + owner acceptance dialog (P: owner accepts before first YouTube connect / autopilot; 13 CR-4) | BE/FE | M | 09.3.1-T1, 13.1.1-T1, 13.1.2-T1 |
| 13.2.1-T2 | All-user blocking re-acceptance at login after a material change (S; 13 CR-4) | BE/FE | XS | 13.2.1-T1 |
| 13.2.2-T1 | `legal_documents` registry + `nts-worker legal publish` verb + startup/CI equality check (13 CR-9, new id) | BE/Ops | M | 13.1.1-T2 (soft), 13.1.2-T2 (soft), 13.6.1-T1 (soft), 09.2.2 |
| 13.2.3-T1 | 18+ age gate: attestation record, `age_attestation_required` 400, `LegalDocument.Age18` enum/config key, copy (13 CR-9, new id) | BE/FE | XS | 10.3.1-T0, 13.2.2-T1 (soft), 09.3.1-T1 |
| 13.3.1-T1 | Deletion engine + job + alerts | BE | M | 09.2 |
| 13.3.2-T1 | Account deletion flow | BE | M | 13.3.1, 06.4.1 |
| 13.3.2-T2 | Backup-window check: retention config = 30 days, restore runbook re-applies completed deletions, e-mail and policy text match | BE/Ops | XS | 13.3.2-T1, 11.6.1 |
| 13.3.3-T1 | Export job | BE | M | 09.7 |
| 13.3.4-T1 | Report form + admin ticket | BE/FE | S | 09.9.1 |
| 13.4.1-T1 | OAuth verification submission | BA | M | 06.1.1, 13.1 |
| 13.4.2-T1 | YouTube audit submission | BA | M | 06.5.1-T1 |
| 13.4.3-T1 | TikTok audit submission (R2) | BA | S | 07.4.1 |
| 13.5.1-T1 | Evidence pack capture | BA | S | 10.7, 13.3 |
| 13.5.2-T1 | Compliance calendar | BA | XS | — |
| 13.6.1-T1 | Lawyer consultation + memo → backlog | Legal | S (+ external) | 13.1 drafts |

## 7. Test plan

- **Integration:** each deletion trigger creates the right `due_at`; job deletes the right scope; alert fires at T−24 h (clock fake); account deletion leaves no personal data (query checklist).
- **Manual:** full deletion on staging, then verify storage objects gone and Google permissions page no longer lists the app; list backup snapshots and confirm none is older than 30 days; restore an older staging backup and confirm deletions completed after it are re-applied before reopening.
- **Review:** policy text vs actual behaviour walkthrough before each audit submission.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Policy says one thing, code does another | Behaviour walkthrough before filing; tests for deletion windows; backup retention checked against the stated 30 days (13.3.2-T2) |
| Verification/audit delays | YouTube audit filed in M1 as soon as upload works (13.4.2), pre-audit mode meanwhile, appeal/re-file on rejection; OAuth verification mid-M2 (SaaS only) |
| Local-law gaps | Lawyer memo in M1; backlog items before M3 |
| Defamation claim from true-crime content | Sources + attestation + takedown within 72 h + suspension tools |

## 9. Exit criteria

- **M1:** drafts ready; YouTube connect + private publish + consent flows on staging (evidence can be recorded); deletion engine live on staging.
- **M2:** legal pages lawyer-reviewed; OAuth verification filed (mid-M2) and the YouTube audit re-filed/extended for the SaaS use case; re-acceptance, export, account deletion working; evidence pack complete.
- **M3:** YouTube audit approved; lawyer memo items closed.

## 10. Open questions and contract change requests

- ⚠️ Law 91/2025/QH15 (Personal Data Protection Law) + Decree 356/2025/ND-CP apply (13 CR-9); exact obligations under them still unconfirmed (lawyer).
- ⚠️ OpenAI terms on output ownership and on depicting real people.
- ⚠️ Credit expiry (12 months, 12.6.2) and the 14-day refund window — lawyer check (13.6).
- **Contract changes (applied to 00):** add `legal_documents(id, kind, version, locale, effective_at, content_url)` and `reports(id, reporter_email, target_type, target_id, reason, status, resolved_at)` to 00 §6; add `POST /reports` to 00 §8.
- **Contract changes (applied to 00):** 00 §11 Data NFR states 30-day backup retention and that deleted data ages out of backups within that window; the restore procedure re-applies deletions completed after the backup date (11.6.1).

## 11. Estimate summary

| Epic | Days |
|---|---|
| 13.1 Legal documents | 3.5 |
| 13.2 Consent/versioning (13 CR-9: +1.0 `13.2.2-T1` +0.25 `13.2.3-T1`) | 2.5 |
| 13.3 Data rights | 3.75 |
| 13.4 Verification + audits | 2.5 |
| 13.5 Evidence + calendar | 0.75 |
| 13.6 Local law | 0.5 |
| **Total** | **13.5** → with 20 % buffer **16.2** person-days (13 CR-4: was 11.5 / 13.8; 13 CR-9: S 7.25 → 8.5, total 12.25 → 13.5; P 5.0, S 8.5) (+ external lawyer time; of which R2: 0.5 raw / 0.6 with buffer) |
