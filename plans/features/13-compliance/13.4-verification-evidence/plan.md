# 13.4 — TikTok audit submission (P · R2)

> Contract: `../README.md` — Part A §9 (plan 13's P doors) and Part B §B0a (evidence-pack convention), §B8.1 (canonical rows), §B10 **T17** (single filing copy) / **T6** (content_url); the former §B1–§B11 contract, D12–D18 and the `13-S-*` gates are retired by DEC 10.14. Planning inputs: high-level `../../13-compliance.md` §4 epic 13.4 `13.4.3`, §6 row `13.4.3-T1` (BA, R2), §8 risk "audit delays", §9 exit criteria M3; 07's audit contract `../07-tiktok-posting/README.md` §1.3 U1–U13, §3 D4/D8/D10/D11, §4 CR-7/CR-8/CR-9/CR-10.
> This folder is **documents and filings only** and holds **one** task, `13.4.3-T1` (R2). DEC 10.14 dropped the other three: `13.4.1-T1` (Google OAuth verification + `saas_registration` gate), `13.5.1-T1` (reusable evidence pack as its own task) and `13.5.2-T1` (compliance calendar + monthly SLA report). Rows, steps and tests of those three are removed, not deferred; the pack convention they established survives as `../README.md` §B0a and is exercised by `07.4.1-T1`.
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope.** The one R2 task of plan 13 that produces a filing:

| Task | Deliverable | Why it exists |
|---|---|---|
| `13.4.3-T1` | TikTok Content Sharing audit filing (`docs/compliance/tiktok-audit-<yyyy-mm-dd>.md`) + its calendar row inline | 13 §4 `13.4.3`; without the audit the posting path may not publish publicly from the owner's own account (07 §7) |

Dropped by DEC 10.14, listed only so the folder total is auditable: `13.4.1-T1` Google OAuth sensitive-scope verification (no public sign-up exists, so there is no unverified-app screen and no `saas_registration` gate to flip), `13.5.1-T1` evidence pack as a task (the convention stays, §B0a), `13.5.2-T1` `docs/compliance-calendar.md` + monthly SLA/DSAR report (no SLA rows: a single owner, no third-party duty; the filing's own §6 carries the calendar values).

Folder total **0.5 d raw** (`13.4.3-T1`); the former 2.25 d (four S tasks) is dropped.

**Out of scope.** (a) The P slice of Part A — the legal drafts (`13.1.1-T1`, `13.1.2-T1`), the owner's acceptance (`13.2.1-T1`), the deletion engine (`13.3.1-T1`) and the YouTube audit filing (`13.4.2-T1`) — owned elsewhere; this folder cites them and waits on them. (b) Every former S artifact: lawyer reviews, content policy, cookie notice, PDPD memo, 18+ age gate, DSAR export, account deletion, report endpoints, `legal_documents` publishing — deleted with phase S and not cited as deliverables here. (c) All UI: the posting screen and the consent/revoke screens belong to 10 (see §6). (d) The evidence capture itself: `07.4.1-T1` owns the pack; this folder only *reads* it and links to it (T17).

## 2. Confirmed facts

| Id | Fact | Status / source |
|---|---|---|
| 07 §1.3 | The audit's required-UX list is U1–U13 in `../07-tiktok-posting/README.md` §1.3; it is copied **verbatim**, in its order, and never reworded | ✅ 07 README §1.3 |
| B0a | Pack convention: text index `docs/compliance/evidence-<yyyy-mm-dd>/index.md` in the repo, binaries in `~/.nts/compliance/evidence-<yyyy-mm-dd>/`, sha256 per artefact, synthetic tenant only, ≥ 24-month retention | ✅ `../README.md` §B0a |
| T17 | `docs/compliance/tiktok-audit-<yyyy-mm-dd>.md` is this task's **single** filing copy; `07.4.1-T1` links to it | ✅ `../README.md` §B10 |
| FR-13-7 | Posts carry `is_aigc = true`; the disclosure duty is on the poster, and the product claims only what it ships | ✅ `../../13-compliance.md` §3 |
| — | The TikTok developer portal's audit form, its required fields and its reply times are **external** and not published in the plans → captured verbatim at filing time, no reply deadline promised | ⚠️ [OPEN] |
| — | The product is personal-only (DEC 10.14): one owner account, one TikTok connection, no public sign-up, no other users, no charges — the filing must say exactly that | ✅ DECISIONS.md §10.14 |

## 3. Domain and data

No table, column, index, migration or DTO is created or changed by this folder (documents only). The "domain" is the set of artefacts and the values read from existing systems.

**Artifacts (repo, text only).**

```
docs/compliance/tiktok-audit-<yyyy-mm-dd>.md     # <yyyy-mm-dd> = filing date (ISO), this task's single copy (T17)
docs/compliance/evidence-<yyyy-mm-dd>/index.md   # 07.4.1-T1's pack index - read only, never rewritten here
```

**External (recorded, never committed).** Pack binaries stay in `~/.nts/compliance/evidence-<yyyy-mm-dd>/` on the FileVault disk — never the repo and never `/Volumes/Project` (§B0a). Submitted form answers, the portal ticket reference and support threads → recorded in the filing by location + sha256, not copied into the repo.

**Values read (read-only, no config key added).** `07.4.1-T1`'s required-UX list and pack index; the shipped screens' task ids from 10 (`10.7.3-T1` posting screen and its approved `10.7.3-T0`); the audit status tracked by 06/07 (public publishing stays off until approval).

**Identifier list used verbatim from the contract (no synonyms).** Filing path `docs/compliance/tiktok-audit-<yyyy-mm-dd>.md`; artifact id = pack index row id; verdicts `proven` / `provisional` / `gap`; field `is_aigc`; deletion path `13.3.1-T1` channel-scope request.

## 4. Use cases

- **UC-4 (13.4.3-T1, R2) — file the TikTok audit.** Given `07.4.1-T1`'s required-UX list and pack, When each item maps to a shipped screen with an artefact, Then the filing is submitted with no `gap` row and no unsupported claim.
- **UC-5 — answer a follow-up without rewriting history.** Given the portal asks for more information, When the owner answers, Then the exchange appends to §7 and the §6 status changes, while §0–§5 stay as filed.

## 5. API and events

**None new.** This folder adds no route, DTO, error code, outbox event, job or handler. What the filing documents:

| Interaction | Contract | Recorded for |
|---|---|---|
| TikTok connect / disconnect / token revoke | 07's OAuth and revoke flows (`07.4.1-T1` evidence) | §4 consent / revoke rows |
| Post creation with per-post consent and series authorisation; `is_aigc = true` written on every post | 07 README §1.3, FR-13-7 | §2/§3 rows |
| Channel-scope deletion request on disconnect | `13.3.1-T1` (deletion engine) | §5 data handling |
| Takedown of a published video | TikTok's own in-app report flow — the product adds **no** public report route (DEC 10.14) | §5 content rules |

## 6. UI

**No UI work is owned by this folder** — no screen, component, route, copy key, breakpoint, light/dark or focus order is designed or built here (owner rule 2026-09-25: every web/UI story needs an approved `-T0 — UI design` task; this folder has none). The screen the filing photographs belongs to 10 and to 07's UX contract:

| Recorded surface (evidence only) | Owner | What this folder does |
|---|---|---|
| Posting screen `/episodes/:id/publish/tiktok` | `10.7.3-T1` (design approved in `10.7.3-T0` against 07 README §1.3) | cites it as the owning task of every §2 row it covers; marks `provisional` if only staging evidence exists |
| Per-post consent, series authorisation, disconnect / token revoke | 07 + `10.7.3-T1` flows | photographs them for §4; never re-specifies them |
| Published-post state and the AI-disclosure position | 07 (`is_aigc`) | states what ships; claims no label the platform does not show |

i18n: the filing is English markdown (repo convention) with vi/en names quoted only where the evidence refers to a page. a11y: no owned surface — the a11y review gate applies to the screens above in 10.

## 7. Jobs and integrations

**No new job, no new schedule, no new integration endpoint.**

- **Existing jobs (read-only expectations).** None is touched: the filing is manual, run once and then per exchange.
- **Optional automated surface (evidence only).** The evidence pack for `07.4.1-T1` already uses the Application Insights/`usage_records` reads it needs; this folder adds no telemetry. Budget reminder quoted from the source doc: images $0.30 run, video $4.20 run, single video with subtitles $5.10, max $342/mo at 30 runs/mo, `opencode-go` free.
- **External integrations (outside the repo).** The TikTok developer portal (audit form, ticket thread). Reply times are ⚠️ unknown, so the filing's §6 records "expected reply" as a range/unknown and never a fabricated date.
- **Operator CLI.** None used; no verb is added.
- **Calendar values.** Recorded **inline in the filing's §6** (filing date, expected reply, re-file cadence, appeal path); there is no `docs/compliance-calendar.md` (that file belonged to a dropped task, DEC 10.14).

## 8. Security and privacy

- **Evidence content (§B0a).** Synthetic tenant only (`demo+<n>@invalid`), no real e-mail, IP, token, API key or story text; the redaction pattern is 06.5.1-T1's VP7. Failure to redact = recapture, never file.
- **Binaries stay out of the repo.** `~/.nts/compliance/evidence-<yyyy-mm-dd>/` (FileVault). Repo copies are markdown + sha256 + paths only (`git status --porcelain docs/compliance` shows no media). ⚠️ `/Volumes/Project` is an unencrypted APFS volume with Owners disabled — nothing sensitive may sit there.
- **No secrets in the filing.** The TikTok client key/secret are referenced by *name only* (`nts/<env>/tiktok/…`), never pasted; no token, no cookie, no portal password.
- **Personal scope is itself the security statement.** The filing says one owner account, no third party data, no public route — do not claim multi-tenant capability (DEC 10.14).
- **Retention.** The filing and its pack are kept at least **24 months** [DECISION] (§B0a) or until the next successful re-file.
- **Legal caution.** No sentence in the filing asserts PDPL/PDPD compliance: the VAT/legal instrument list stays ⚠️ and no memo is claimed, because the PDPD-memo task was dropped with phase S.

## 9. Test plan

This folder has no code path, so its "tests" are document checks that the owner or a reviewer runs; none needs new tooling.

| # | Check | How | Pass condition |
|---|---|---|---|
| TS-1 | Header/shape integrity of this folder | `python3 tools/verify_plan.py plans/features/13-compliance` + the header-vs-§B8.1 diff (`13 README` §B8.1) | 1 `plan.md` with sections 1–11; **1** task file with H1 + header + the 9 headings; header row byte-equal to §B8.1 |
| TS-2 | Redaction of the filing and the pack | VP7 pattern from `06.5.1-T1:117` over `docs/compliance/**` | no token / e-mail (except `*@invalid`) / IP / real video id; no `*.png\|*.mp4` tracked |
| TS-6 | Filing completeness | manual matrix read of §2: every row cites an owning task id + an artefact id; verdict ∈ {proven, provisional, gap}; **any `gap` blocks the filing** | no empty cell, no unsupported claim, 07's order preserved |
| TS-9 | Evidence reproducibility | a second person retakes one artefact using the index's reproduction column | same observable state, no real user data in the recapture |
| TS-10 | Calendar honesty | read §6: filing date, expected reply as range/unknown, re-file cadence, appeal path | no fabricated date, status matches §7's last exchange |

No permanent automated test is added: nothing executable is owned here. The checks that are automated belong to `07.4.1-T1` (pack) and `06.5.1-T1` (redaction) and are cited as such.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 13.4.3-T1 | S (0.5) | BA | R2 | 07.4.1-T1, 10.7.3-T1 | 07.4.1-T2 |

Row is byte-equal to `../README.md` §B8.1. Milestones per `00-overview.md` §4.1: R2 only (the former "all S · M2" reading no longer applies to this folder).

Re-estimate (§B9 — no change from the high-level plan):

| Task | High-level (§6) | Detailed | Why |
|---|---|---|---|
| 13.4.3-T1 | 0.5 | 0.5 | unchanged (R2) |
| **Folder** | **0.5** | **0.5** | the former 2.25 d of four S tasks is dropped by DEC 10.14 |

Sequencing: **R2** — after `07.4.1-T1`'s pack and required-UX list and after `10.7.3-T1`'s screen (and its `10.7.3-T0` approval) exist. Hand-off: the filing **gates `07.4.1-T2`** (public publishing stays off until the audit is approved).

[OPEN] carried into implementation (none blocks writing the task): the portal's field set and reply times (⚠️, captured verbatim at filing time); a possible difference between 07's U1–U13 and the portal's current list at filing time — recorded in both places, never silently reworded (§7 of the filing).

## 11. Rollout

1. **Pre-work (other folders).** `07.4.1-T1` builds the pack and the required-UX list; `10.7.3-T1` ships the posting screen with its approved `10.7.3-T0`; `13.3.1-T1` provides the channel-scope deletion request the filing cites.
2. **Matrix.** Copy U1–U13 verbatim into §2, resolve every row to a shipped task id + artefact id, mark `provisional` where evidence is staging-only, and stop if any row is a `gap`.
3. **File.** Capture the portal's questions verbatim, answer only from §2/§3/§4 evidence, file, then record `sent at` + ticket reference in §0/§7.
4. **After filing.** Every exchange appends to §7 and updates §6; a re-file appends a new revision section and reuses the same pack (T17 keeps one copy).
5. **Unblock.** Only an approved audit closes `07.4.1-T2`; the flag/step that keeps public posting off is 07's and is not flipped here.
6. **Steady state.** Re-file cadence recorded in §6; the filing is retained ≥ 24 months; no calendar file, no monthly report, no admin surface (all dropped with phase S).
