# 13 — Compliance, P slice: Tech Lead review, shared decisions and feature index

> Reviews: `../../13-compliance.md` — **P slice only** (00 §4.1): 13.1.1-T1 (privacy draft), 13.1.2-T1 (terms draft), 13.2.1-T1 (versioned acceptance before the owner's first YouTube connect / autopilot), 13.3.1-T1 (deletion engine for the 7/30-day duties), 13.4.2-T1 (YouTube audit filing in M1). Everything else in plan 13 was phase S and is dropped by DEC 10.14 (Part B keeps only the R2 TikTok audit). Detailed together with `../06-youtube-posting/README.md`.
> Not legal advice: the drafts are written by the owner/BA from the checklists. The former S lawyer-review tasks were dropped by DEC 10.14; a review stays the owner's own call before the product leaves the personal use case.
> Reviewed: 2026-09-25 by the Tech Lead against Developer Policies III.A/III.D/III.E (06 README G13), 09 wave 2 (`IConsentWriter`, `IBlobStore`, audit), 10 wave 1 (D12 legal pages) and 11.6.1-T1 (restore re-apply).
> Verdict: **approve with changes** (F-list below). Status: **ready for implementation with conditions** (DoR 2026-09-25) — see §11.
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 0. Folders

```
plans/features/13-compliance/
  README.md
  13.1-legal-acceptance/         ← 13.1.1-T1, 13.1.2-T1, 13.2.1-T1
  13.3.1-deletion-engine/        ← 13.3.1-T1
  13.4-verification-evidence/    ← 13.4.3-T1 (R2)
  13.4.2-audit-filing/           ← 13.4.2-T1
```

## 1. Facts

| Id | Fact | Source |
|---|---|---|
| K1 | Legal texts are build-time repo files `web/src/content/legal/<prefix>.<version>.<lang>.md` + `manifest.json` `{ "<doc>": { "version", "effectiveDate" } }`, with the manifest key mapped to the file prefix `tos -> terms`, `privacy -> privacy` (the files stay `terms.*`, the key stays `tos`), converted at build (no raw HTML), rendered by 10.3.1-T1; the page version must equal the version recorded in `user_consents` | 10 README D12; 10.3.1-T1:57, 70 ✅ (file-prefix map added by 13 CR-34) |
| K2 | `IConsentWriter.RecordAsync(Guid userId, LegalDocument document, string version, IPAddress? ip, ct)`, `LegalDocument { Tos, Privacy }`, config `LegalDocuments:Current:Tos` / `:Privacy` (1–32 chars); rows commit in the caller's transaction; history kept (no unique index) | 09.3.1-T1:29, 47-50, 92, 146, 186 ✅ |
| K3 | III.A.1 terms link + binding clause; III.A.2 privacy policy before features, "uses YouTube API Services", Google Privacy Policy link, revoke link; III.D.2 7/30 days; III.E.4 30-day refresh; user deletion ≤ 7 days | 06 README G13 ✅ |
| K4 | `data_deletion_requests(user_id, scope account/channel/episode, reason user_request/in_app_revoke/google_revoke/account_delete, requested_at, due_at, completed_at)`; `legal_documents(kind, version, locale, effective_at, content_url)` | 00 §6 ✅ |
| K5 | Deletion engine consumers: 02 CR-22 (episode scope deletes `research/{userId}/{episodeId}/`), 06.4.1-T1/06.4.3-T1 (channel scope), 11.6.1-T1 (a restore re-applies requests with `completed_at` after the backup's PITR point before reopening), 09.7.2-T1 (`IBlobStore.DeleteAsync`, prefix deletes, no new bucket), 09.7.3-T1 ("13.3.1-T1 owns the hard delete") | 02 README:675; 11.6.1-T1:15, 81; 09.7.2-T1:161; 09.7.3-T1:86, 166 ✅ |
| K6 | Audit filing needs the 06.5.1-T1 pack; quota asked from the owner's cadence and daily cap at the M1 filing | 13 §4 13.4.2; 06 §4 06.5.1 ✅ |

## 2. Findings

| # | Sev | Finding | Resolution |
|---|---|---|---|
| F1 | H | `legal_documents` has no P writer or reader: texts are repo files (K1) and versions are config (K2) | D1: `legal_documents` is **not** created in P (S admin publishing); the single source of a version is `manifest.json`, and `LegalDocuments:Current:*` must equal it (a CI check in 13.2.1-T1) (13 CR-1 to 00 §6) |
| F2 | H | 10 classifies the consent dialog as S, 13 says P | D3: P = the owner's acceptance before the first YouTube connect and before enabling autopilot (13.2.1-T1); the S "blocking re-acceptance at every login after a material change for all users" had become 13.2.1-T2 (S, XS) and is deleted with phase S by DEC 10.14 — the owner is the only account, so nothing re-accepts |
| F3 | M | The deletion engine has four independent scope owners; a single job that knows every table would couple 13 to 02/06 internals | D4: `IDeletionScopeHandler` per scope, registered by the owning plan; the engine owns rows, scheduling, alerts, audit and the restore re-apply |
| F4 | M | Episode deletion vs `DELETE /episodes/{id}` (soft cancel, 09.5.1) | D4: episode scope runs only for user-requested hard deletes and account deletion (S); in P the engine has handlers for `channel` (06.4.3-T1) and `episode` (02 CR-22 research prefix + 09.7.3-T1 assets) and is idle for `account` until 13.3.2 (S) |
| F5 | M | Policy text vs behaviour drift before filing | D5: 13.4.2-T1 starts with a behaviour walkthrough checklist mapping each III.x clause and each policy sentence to a test or screenshot |

## 3. Shared decisions

**D1 Legal texts and versions [DECISION] (F1).** Files per K1, authored by 13.1.1-T1 / 13.1.2-T1; versions semver-like `1.0.0`; `manifest.json` is the **page** source; the API's source of truth for P is `LegalDocuments:Current:*`, kept equal to `manifest.json` at API startup and in CI (`tools/ci/check-legal-versions.py`) (13 CR-12, CR-30); the `legal_documents` registry that the never-shipped 13.2.2-T1 designed is deleted with that task (DEC 10.14). Required clauses: privacy — every §5.1 checklist item + III.A.2 items (YouTube API Services statement, Google Privacy Policy link, revoke link, contact) + the retention/deletion table (7/30 days, 30-day backups) + the consent model (per-video, autopilot veto window); terms — III.A.1 link to https://www.youtube.com/t/terms and the binding sentence, AI-disclosure duty, no monetisation guarantee, user is publisher of record. No credit, package, payment or refund clause exists (DEC 10.14: nothing is sold and the product is free). Personal version: the owner is the only user; …

**D2 Consent text for uploads.** The publication consent text `pc.v1` is owned by 06 (06 README D7); the privacy policy describes it; 13.1.1-T1 cites the file.

**D3 Acceptance [DECISION] (F2).** `ILegalAcceptance.EnsureCurrentAsync(userId, ct) → ErrorOr<Success>` (409 `legal_acceptance_required` with `errors.documents = ["privacy","tos"]`, sorted; 06 YR15); called by 06.1.2-T1 (connect) and by 02.8.3-T2 when enabling autopilot (13 CR-2); `GET /me/legal` → `{ documents: [{ document, currentVersion, acceptedVersion, acceptedAt }] }`; `POST /me/legal/accept { tos, privacy }` (versions must equal current, else 409 `legal_version_outdated`) → `IConsentWriter.RecordAsync` per document with the request IP; audit `account.legal_accepted`. FE: `LegalAcceptanceDialog` in `web/src/app/features/account/` (reuses 10.1.3-T1 `ConfirmDialog`, links to `/vi|en/privacy|terms` in a new tab), opened by the 409 handler (10.2.2-T1 maps `legal_acceptance_required` to an action, not a plain error).

**D4 Deletion engine [DECISION] (F3, F4).** `IDeletionRequests.CreateAsync(userId, DeletionScope scope, Guid? targetId, DeletionReason reason, ct)` computes `due_at` (7 d for `in_app_revoke`, `user_request`, `account_delete`; 30 d for `google_revoke`) and is idempotent per `(scope, target_id, reason)` while open (partial unique index; `target_id uuid` column added, 13 CR-1). `IDeletionScopeHandler { DeletionScope Scope; Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest r, ct) }` registered by owners (06.4.3-T1 `channel`, 13.3.1-T1 ships the `episode` handler composing 09.7.3-T1's asset delete + the 02 CR-22 research prefix delete). `DataDeletionJob` hourly (allow-listed system job, batch ≤ 20, `FOR UPDATE SKIP LOCKED`, per-row `SystemContext(user)`) executes due-first, idempotently; success → `completed_at`, audit `account.data_deleted` (`support`, `user`), metric `deletion_requests_open`, `deletion_request_overdue_total`; alert when any open request has `due_at − now < 24 h` (11.6.2-T1 rule). Restore re-apply: `nts-worker deletions reapply --since <PITR timestamp>` re-executes every request completed after that point (idempotent handlers), used by 11.6.1-T1's runbook before reopening.

**D5 Audit filing [DECISION] (F5).** 13.4.2-T1 = walkthrough checklist → form answers drafted in `docs/compliance/youtube-audit-<date>.md` (answers, quota justification from the owner's cadence and daily cap, links, pack index) → filing via the form → calendar entry (re-audit cadence ⚠️) → appeal path documented. No secrets or personal data in the repo copy.

## 4. Change requests

| CR | Target | Change |
|---|---|---|
| 13 CR-1 | `plans/00-overview.md` §6, `features/09-backend-api/README.md` table-ownership row, `09.2.2-core-schema-migrations/plan.md` §1 | `legal_documents` not created in P (the S half is dropped by DEC 10.14); `data_deletion_requests.target_id uuid` + partial unique `(scope, target_id, reason) WHERE completed_at IS NULL` |
| 13 CR-2 | `features/02-story-writing/02.8.3-cadence-publishing/02.8.3-T2-…` | enabling autopilot also calls `ILegalAcceptance.EnsureCurrentAsync` (409 `legal_acceptance_required`) |
| 13 CR-3 | `plans/09-backend-api.md` §5.4 | codes `legal_acceptance_required` 409, `legal_version_outdated` 409 |
| 13 CR-4 | `plans/13-compliance.md` §6/§11, 00 §4.1 | 13.2.1-T1 S → M in P; new 13.2.1-T2 for re-acceptance (later deleted by DEC 10.14: a single owner never re-accepts) |
| 13 CR-5 | `features/*` task headers | Blocks back-edges (§8.1) |
| 13 CR-6 | `features/09-backend-api/09.7-video-review-storage/09.7.2-T1-…` | `IBlobStore.ListKeysAsync(bucket, prefix, ct)` for prefix sweeps |

## 5–7. Sequencing, template, gates

13.1.1-T1 / 13.1.2-T1 start immediately (no dependency) and unblock 10.3.1-T1; 13.2.1-T1 follows; 13.3.1-T1 is independent (after 09.7.2-T1); 13.4.2-T1 is last. Template: `../02-story-writing/README.md` §6. Gate **13-P-ready**: pages live with the manifest versions, owner acceptance recorded before connect, deletion job live on staging with a channel request completed before `due_at`, filing done.

## 8. Feature index

| Folder | Tasks (size) |
|---|---|
| `13.1-legal-acceptance/` | 13.1.1-T1 (M), 13.1.2-T1 (M), 13.2.1-T1 (M) |
| `13.3.1-deletion-engine/` | 13.3.1-T1 (M) |
| `13.4.2-audit-filing/` | 13.4.2-T1 (M) |

### 8.1 Canonical task dependencies

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 13.1.1-T1 | M (1.0) | Legal/BA | P · M1 | — | 13.2.1-T1, 06.5.1-T1, 10.3.1-T1, 06.1.1-T1 (soft), 07.4.1-T1 (13 CR-17), 07.1.1-T1 |
| 13.1.2-T1 | M (1.0) | Legal/BA | P · M1 | — | 13.2.1-T1, 06.5.1-T1, 10.3.1-T1, 06.1.1-T1 (soft), 07.4.1-T1 (13 CR-17), 07.1.1-T1 |
| 13.2.1-T1 | M (1.0) | BE/FE | P · M1 | 13.1.1-T1, 13.1.2-T1, 09.3.1-T1, 10.2.6-T1 | 06.1.2-T1, 06.5.1-T1, 10.7.1-T1 (10 CR-15), 07.1.2-T1 (13 CR-17) |
| 13.3.1-T1 | M (1.0) | BE | P · M1 | 09.2.2-T1, 09.2.6-T1, 09.7.2-T1 | 06.4.1-T1, 06.4.3-T1, 11.6.1-T1 (soft) |
| 13.4.2-T1 | M (1.0) | BA | P · M1 | 06.5.1-T1 | — |

## 9. Re-estimate

| Task | High-level | Detailed | Why |
|---|---|---|---|
| 13.1.1-T1, 13.1.2-T1, 13.3.1-T1, 13.4.2-T1 | 4.0 | 4.0 | unchanged |
| 13.2.1-T1 | 0.5 | 1.0 | BE gate + endpoints + FE dialog + version CI check (F2) |
| **P slice** | **4.5** | **5.0** | +0.5 |
| Kept after DEC 10.14 (Part B) | — | 0.5 | 13.4.3-T1 0.5 R2; the former 1.25 for 13.2.2-T1 + 13.2.1-T2 is deleted with those never-shipped tasks |
| Plan 13 total | 11.5 | **5.5** | P slice 5.0 + kept 0.5; the 7.25 S slice is dropped (DEC 10.14) |

## 10. Owner decisions and TL rulings

| # | Decision | Outcome |
|---|---|---|
| O1 | Personal-version legal drafts published without lawyer review while the owner is the only user (the S lawyer-review tasks were dropped by DEC 10.14; the owner reviews before any other user) | Accepted |
| O2 | Re-estimate P 4.5 → 5.0 | Accepted |

**TL rulings.**

| # | Topic | Ruling |
|---|---|---|

## 11. Definition-of-Ready review (2026-09-25)

- Script (`verify_plan.py … 13-compliance`): 3 `plan.md` files with sections 1–11; 5 task files with the H1, header, 9 headings; headers equal §8.1; total 5.0 d = §9 = `13-compliance.md` P rows = `00-overview.md` §4.1 after 13 CR-4; graph 0 cycles.
- Reviewed in the same three passes as 06 (security: deletion uniqueness, restore re-apply of open requests, foreign-target handling, document order, audit-pack redaction; contract: `DeletionResult` shape); resolved by 06 YR7, YR12–YR15 and the writers' fix rounds; checked on disk (four-column partial unique, `reapply` covers open rows, sorted `["privacy","tos"]`, extended redaction grep).
- CRs 13 CR-1…CR-6 applied (00 §6/§8, 02.8.3-T2, 09 §5.4, 09 README/09.2.2 table ownership, 09.7.2-T1 `ListKeysAsync`, 11.6.1-T1 drill, 11.6.2-T1 rule 17, `13-compliance.md`).

| DoR item | Status |
|---|---|
| User value, acceptance, estimate | ✅ |
| Dependencies known | ✅ §8.1 (13.4.2-T1 after 06.5.1-T1) |
| Contract linked | ✅ D1–D5, K1–K6 |
| Reviewed | ✅ TL + three review passes |

**Verdict: ready for implementation with conditions** (accepted 2026-09-25 under the owner's standing approval).

**Conditions.** (1) The drafts are the personal version (O1); the owner's own legal review happens before any other user (the S lawyer-review tasks are dropped by DEC 10.14). (2) 13.4.2-T1 files only after 06.5.1-T1's pack and the gate `06-audit-ready`. (3) Second human reader for 13.3.1-T1 (deletion engine) before merge.

---
# Part B — the former S slice (retired by DEC 10.14)

> Phase S is dropped (DEC 10.14: personal-only product). Nothing in the former S slice is built: the lawyer review and legal publishing, content policy, cookie notice, PDPD memo, the 18+ age gate, DSAR export, account deletion, the report endpoints, Google OAuth verification, the evidence pack and the compliance calendar are all gone. Section numbers §B0–§B11, decisions D6–D18, CRs 13 CR-7…CR-34 and the `13-S-*` gates no longer exist.
> One folder survives with phase-P work: `13.4-verification-evidence/` (13.4.3-T1, the TikTok audit submission, lane **R2**, kept in P). `13.2-legal-versioning-consent/` (13.2.2-T1, 13.2.1-T2) was kept by the first draft of this note as "delivered", but **neither has a commit on `origin/develop`** (verified 2026-10-21), so both task files and the folder plan are deleted. `13.1-legal-publication/`, `13.3-data-rights/` and `13.2-legal-versioning-consent/` are gone with the phase.

## B0a. Evidence-pack convention (still in force)

The audit evidence pack convention was written for the dropped phase S and is kept (DEC 10.14 dropped the phase, not the convention); it is shared by `07.4.1-T1` and `13.4.3-T1`: the index is `docs/compliance/evidence-<yyyy-mm-dd>/index.md` (**text only**, in the repo) and the binaries live in `~/.nts/compliance/evidence-<yyyy-mm-dd>/` on the FileVault disk — never the repo, never `/Volumes/Project`; artefacts are captured with the synthetic tenant `demo+<n>@invalid`, redaction is proven by the `VP7_RedactionClean` grep, every artefact carries a sha256 in the index, and retention is at least 24 months. No S task is implied by this convention.

## B0. Folders (what is left)

```
plans/features/13-compliance/
  13.1-legal-acceptance/         ← 13.1.1-T1, 13.1.2-T1, 13.2.1-T1 (Part A)
  13.3.1-deletion-engine/        ← 13.3.1-T1 (Part A)
  13.4-verification-evidence/    ← 13.4.3-T1 (R2)
  13.4.2-audit-filing/           ← 13.4.2-T1 (Part A)
```

## B8. Feature index and task mapping (kept rows)

| Folder | Tasks (size) |
|---|---|
| `13.4-verification-evidence/` | 13.4.3-T1 (S, R2) |

### B8.1 Canonical task dependencies

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 13.4.3-T1 | S (0.5) | BA | R2 | 07.4.1-T1, 10.7.3-T1 | 07.4.1-T2 |

Sizes: 1 task, raw **0.5 d** (13.4.3-T1, R2). The former S total (8.5 raw / 10.2 buffered) is dropped with phase S, and the 1.75 d this table used to carry for 13.2.2-T1 + 13.2.1-T2 is deleted with those never-shipped task files.

## B9. Re-estimate (kept rows)

| Task | High-level (§6) | Detailed | Why |
|---|---|---|---|
| 13.4.3-T1 | 0.5 | 0.5 | unchanged (R2) |
| **kept total** | | **0.5** | R2 only |

The former S slice's 8.5 raw d (including the lawyer review, publishing, data rights and verification tasks) is dropped by DEC 10.14 and is no longer part of plan 13's total.

## B10. Owner decisions and TL rulings (kept)

| # | Decision | Outcome |
|---|---|---|
| O10 | Patch-level legal bumps that change nothing material are published without re-acceptance (publish verb `--material` defaults to `false`) | **Retired with 13.2.2-T1** (deleted, DEC 10.14) — there is no publish verb in the personal studio |

**TL rulings.**

| # | Topic | Ruling |
|---|---|---|
| T17 | TikTok filing artifact | `docs/compliance/tiktok-audit-<yyyy-mm-dd>.md` is **13.4.3-T1's** single filing copy; `07.4.1-T1` links to it instead of keeping its own |
| T23 | Login/refresh `pendingAcceptance` failure mode | **Retired with 13.2.1-T2** (deleted, DEC 10.14): no `pendingAcceptance` field is planned |

All other owner decisions (O3–O9) and TL rulings (T1–T5, T7–T16, T18–T22) belonged to dropped S work and are retired with it.
