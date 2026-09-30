# Implementation status

The single place to see what is **done**, **in progress**, **blocked** and **next**. Update it in the same commit as the work. Plans stay the contract (`plans/`); this file only tracks execution. History (old Resume-here blocks, per-task evidence tables, findings, full session log) lives in [`docs/status-archive.md`](docs/status-archive.md); keep this file short.

Legend: `done` (acceptance checks run and passed) · `doing` · `blocked (<who>)` · `todo` · `partial` (see note).

## Resume here

Keep this block ≤ 15 lines: current state and next step only. Move the previous block to the archive when you replace it.

- **Current (2026-09-30 (7)).** `develop` @ `0e3f306`: 09 wave 4 merged — 09.2.2-T2 (#51, InitialCore + composite same-owner FKs + `migrations` CI job) and 09.2.3-T1 (#52, tenant filter, ICurrentUser, ISystemContext, SystemWorkQuery). Merged tree: Release build 0 warnings, 321/321 tests incl. integration on PG17.
- **Next wave, in order:** `09.1.3-T2 ∥ 09.2.5-T1` → `09.2.4-T1` (09 README §5).
- **Owner decisions open:** none blocking phase 1.
- **Security debt live on `develop`:** see §Deferred security gates — `AuthorizationBehavior` still a stub (09.1.3-T2); production Api has no authentication registered yet.
- **Gates:** per task by risk tier (`plans/00-overview.md` §10 Definition of Done; full rule in `~/.omp/agent/RULES.md`).
- **Environment (DEC-5, DEC-6):** implementation machine = this Mac (M4, 16 GB, macOS 27.0); `NTS_HOME=<repo>/.nts` (gitignored; `lib.sh` default); D13 mode `accepted-risk`, keys never committed or pushed (`.gitignore` + gitleaks hooks).
- **Fresh clone:** `git config core.hooksPath .githooks`, then `worker-node/bin/bootstrap.sh all`, then `worker-node/bin/smoke.sh`. Native dev stack: `docs/dev-setup.md`.

## Progress by plan

Per-task evidence for `done` rows: `docs/status-archive.md` §Per-plan evidence tables.

| Plan | Done | Open (see §Open tasks) |
|---|---|---|
| 01 Environment (29 tasks) | 25 + 01.2.4-T1 n/a (DEC-9: no OpenAI org, calls go through the relay) | 01.1.1-T3, 01.3.3-T1, 01.3.3-T2 |
| 02 Story writing (M0 slice) | 11 tasks — M0 slice complete (presets, harness, A/B, D15 freeze, 02.5.2-T0 spike) | M1 tasks not started |
| 03 Images (M0 slice) | 3 (03.1.1-T1, 03.4.1-T1, 03.3.2-T1) | 03.1.2-T1, 03.2.x, 03.4.2/03.4.3 (M1) |
| 04 Voice proxy (12 tasks) | 10 | 04.2.2-T2, 04.2.3-T1 |
| 05 Video/subtitles | 9 | 05.2.1-T1, 05.5.2 |
| 09 Backend | 12 tasks merged (wave 4: 09.2.2-T2 #51, 09.2.3-T1 #52) | next wave in §Resume here |
| 11 Worker deployment | 11.2.1-T1 (proposal only; sign-off deferred to phase 2, owner 2026-09-30) | phase 1: 11.1.x, 11.3.3, 11.3.5; the rest is phase 2 (11 CR-18) |
| 13 Compliance | 13.1.1-T1, 13.1.2-T1 (PR #35) | 13.2.1-T1 → 13.2.2-T1, 10.3.1-T1 pages; 13.1.1-T2 / 13.1.2-T2 lawyer rows (O1) |

Plans 06, 07, 08, 10, 12: not started. Order: `plans/features/README.md`.

## Open tasks

| Task | Status | Note |
|---|---|---|
| 01.1.1-T3 Runbook + clean-account rehearsal | todo | Runbook sections accumulate in `worker-node/README.md`. The rehearsal runs last, after 05.5.2 |
| 01.3.3-T1 Carry-over | partial (§11 `[OPEN]`) | `worker-node/CARRY-OVER.md` §1–12 written; §11 waits for the 05.5.2 trial rows |
| 01.3.3-T2 Carry-over walkthrough | todo | Owner walkthrough, after 05.5.2. Interim whisper notes: `.nts/runs/whisper-baseline/carryover-notes.md` |
| 03.1.2-T1, 03.2.x, 03.4.2/03.4.3 | todo (M1) | Need 09 and 11.3.5 |
| 04.2.2-T2 Catalog rules + seeder (C#) | todo | Needs 09.2.2-T1 (merged) and 09.2.5-T1 |
| 04.2.3-T1 Preview generation + upload | partial | M0 generation, owner listening and T19 option A done (PR #26); local SeaweedFS upload rehearsal passed; M1 upload waits for 11.4.1-T2 → 11.4.1-T1. SEC-0423T19-1 (info) open. A 2nd vi rater + an en rater → catalog revision 2 (04 T18) |
| 05.2.1-T1 Subtitle fidelity + sync evaluation | partial (machine leg) | L1/L2 rows + owner labels blocked on 05.5.2-T1 (05 T84); `RenderQuality:*` values await calibration |
| 05.5.2 | todo — prep on branch `feature/05.5.2-m0-trial` (3 commits, not on `develop`) | Branch: `apply_quality_policy.py` (D10 mirror) + `mpt_client` spec-path containment, gates passed; L1 vi / L2 en renders blocked on the D11 memory gate (swap). Needs 04.2.2-T1 raters and 03.3.2-T1 (done). Portrait renders use 9:16 option A (05 O9, 03 CR-16) with the hinted prompt; judge the cropped frames of the first portrait render (`trial/images/REPORT.md` §Crop probe) |
| 13.2.1-T1 Legal versions + acceptance | todo | Versions config + `GET /me/legal` + `POST /me/legal/accept` + gate + owner dialog; then 13.2.2-T1 `tools/ci/check-legal-versions.py` and 10.3.1-T1 pages / `legal-to-json.mjs` |
| 13.1.1-T2 / 13.1.2-T2 Legal review rows | todo (O1) | The ⚠️ rows (children/transfers/retention; credit expiry and refunds; OpenAI output terms; governing law; liability) close with a lawyer; OI-1 (operator legal name + contact) deferred to phase 2 (owner 2026-09-30), before any user other than the operator |

## Deferred security gates

**Deferred security gates (security review 2026-09-29, must close before any feature endpoint/command/query lands):** `AuthorizationBehavior` is a pass-through scaffold owned by 09.1.3-T2 (no unauthenticated/deny coverage yet). Closed 2026-09-30: the tenant `HasQueryFilter` (09.2.3-T1, #52) now covers all 8 `IUserOwned` entities on both contexts; anonymous sees no rows. Still open (RevSec0923 deferred note): production Api registers no authentication yet, so `HttpCurrentUser` is proven only through `TestAuthHandler` and unit tests until the auth task lands. No feature endpoint may merge on top of these stubs.

## Open findings

Closed findings and their full context: `docs/status-archive.md` §Findings during implementation.

- **CR-12** — the GPU worker's PyTorch path uses codec `…-Nano`, the Mac ONNX path `…-Nano-ONNX`; plan 11.3.1 must pin it: **[OPEN] for plan 11**.
- **Egress allow-list** — `plans/11-worker-deployment.md:75` still names `api.openai.com`; it needs a CR for the relay host (DEC-9, phase P).
- **Relay image output** — the relay backend is `gpt-image-2-codex` with output not configurable: CR-3 quality cannot be enforced on this relay [OPEN]; an OpenAI API key, or a relay backed by an API account, is needed for deterministic output.
- **Tap hardening (01 CR-25)** — `[OPEN]` **NTS-TAP-SEC-01** (a `usage` schema allowlist in place of "numeric leaves, keep every key") and **NTS-TAP-SEC-02** (constrain `endpoint`/`model`/scalar/`error_code` strings, record size cap). Other id sinks, gitignored today: `trial/tools/story_harness.py` `redact_response` keeps `msg_`/`rs_` ids in each cell `response.json`; `worker-node/bin/openai-access-check.sh` stores raw upstream bodies under `.nts/runs/access-check-*`. The gitleaks rule `openai-upstream-response-id` blocks either if staged.
- **02 D4 `standard` length band** — the measured M0 lengths sat below the old D4 band (en median 732, vi median 749 units); 02 T103 replaced the rates per language. [INFERENCE] the band question may be closed by T103; confirm before the 02 M1 length work (history: archive §Resume-here history, Next action 7).
- **02 D4 `long` vi headroom (T103)** — `[OPEN]`: estimated output ≈ 10.1K tokens sits at the 10,000 writer/revision cap; decide whether to raise those caps or accept occasional truncation before the long-tier work (02 README:192).
- **Ownership proof (01 DEC-8 / CR-14, 05 O8)** — `[OPEN]`: signing key type/rotation, manifest storage and dispute export for the signed per-render manifest, decided at 05 detailing; plan 06 appends the platform video id.
- **05-M0 music gate** — `check-music.sh` gate row stays `[OPEN]`: the catalog is legally empty (`tracks: []`), so the gate passes vacuously until the owner curates 20–30 tracks.
- **Relay behaviour (02.1.3 open items, DEC-9)** — latency bimodal (17–58 s vs 102–216 s) with 502s at 154–181 s; abandoned calls are billed; the relay rewrites `safety_identifier`/`prompt_cache_key`, drops `max_output_tokens` and injects ~21 k chars of its own instructions (billed input ≈ 2.5× our prompt). Relevant to 05.5.2 and M1 budgets (full note: archive §Per-plan evidence tables, Plan 01 leftovers).

## Session log

Recent rows only; the full log is `docs/status-archive.md` §Session log.

| Date | Session | Done |
|---|---|---|
| 2026-09-29 | Plan 13 content, `feature/13.1-legal-drafts` | 13.1.1-T1 + 13.1.2-T1 finished and verified (two locales each; `manifest.json` `privacy` + `tos` at `1.0.0`; `docs/compliance/privacy-checklist-1.0.0.md` and `terms-checklist-1.0.0.md`; the repeatable check `docs/compliance/legal-parity.py`, stdlib, exit 0 → `OK privacy 1.0.0 10/10`, `OK tos 1.0.0 12/12`). Operator name/contact are placeholders (owner open item); the ⚠️ lawyer rows go to 13.1.1-T2/13.1.2-T2. Uncommitted |
| 2026-09-29 | 11.2.1-T1 (`feature/11.2.1-provider-scoring`) | Hosting decision from dated official sources: `infra/scoring/` harness + 3 captures (aws/azure/gcp 2026-09-29) + `latency.md` ⚠️; **azure 0.8400 eligible > aws 0.7540 (`owner_locked_eligible` + `block_volume_two_zones` unknown ⚠️) > gcp 0.4646 (`pg17_pitr` unknown ⚠️)**. Proposed block in `DECISIONS.md` §10.4.1 names Azure `southeastasia` + `Standard_NC8as_T4_v3`, the Azure resource-name table, the Azure-Firewall FQDN answer and the revisit triggers; provisional on the 11.2.2-T1 latency check. **11 CR-17** logged against D15: Key Vault cannot hold `nts/<env>/…`, physical name `nts-<env>-<scope>-<name>` + logical path tag. Harness hardened after review: exact id/unit/type schema, duplicate-key refusal, O_NOFOLLOW descriptor reads with parent-inode containment and per-file + aggregate read-byte caps, band-aware tie-break that skips an unmeasured dimension for the whole band, derived monthly value, and full `--check-criteria` validation of every executable field (params ranges, gate thresholds, enum scale scores, derived grammar) — 55 tests OK, `--check-criteria` exit 0, two runs byte-identical, clean under `-W error::ResourceWarning`. No spend (no instance rented), no OpenAI call |
| 2026-09-29 | 09.1.1-T1/T2 solution skeleton (`feature/09.1.1-solution-skeleton`) | Owner: local develop first. .NET skeleton (12 projects, CPM, lock files, MTP runner) + native PostgreSQL 17 dev DB (`tools/dev/db-init.sh`). Build 0/0, tests exit 0, health 200, DB checks pass. 09 W3 CR-36 records 5 implementation findings. Gates: QA (1 M + 3 L fixed: `postgres`/`pg_*` refused, CA1852 probe wording, password grep scope, inline comments) -> perf NO DEFECTS (build 1.45 s incremental, Api first 200 at 0.70 s) -> code review NTS-09-01..08 + security SEC-0911-01..03 (all fixed, CR-36 item 8; `dotnet list --vulnerable --include-transitive` clean) -> re-review: 4 L (post-restart identity, malformed `.env.example`, user-secrets semantics, exit-code normalisation) fixed + probed |
| 2026-09-29 | 09 wave-2 batch (#39 `feature/09.2.1-testkit`, #40 `feature/09.1.4-error-mapping`, #41 `feature/09.1.5-ci-pipeline`) | Three tasks implemented in parallel worktrees on one shared contract (the TestKit harness), each with the full gate set (QA → code + perf + security → fixes → delta re-review) plus a final union review. 09.2.1-T2 harness (`PostgresFixture`/`TestDatabase`/`IntegrationTest`/`ApiFactory`/`TestAuthHandler`/`MapEndpoints`/`FakeClock`/`UserBuilder`, 48 cases), 09.1.4-T1 error mapping (`ErrorCodes` 162 + `ErrorCatalogue` + `ProblemDetailsWriter` + `ResultExtensions` + filters + `ETags`, 78 cases), 09.1.5-T1 CI (two jobs, zero-test guard, build-time `openapi.json` 3.1.1, trait split). Integrated trial: unit 98/98 without a DB, integration 56/56, format clean, `--locked-mode` restore clean. Live CI on #41 green (1m18s + 1m07s); scratch PRs #42/#43 proved both red paths and 14-day artifacts, then were closed and deleted. CR-37 + CR-38 recorded; `WorkerHost`/builders deferred with owners |
| 2026-09-30 | 09 wave-3 batch (`feature/09.2.2-core-entities` `ec99274`, `feature/09.2.6-worker-hangfire` `15f10b6`) | Two tasks in parallel worktrees off `develop` @ `f433126`, each with QA → code + perf + security → fixes → delta re-review, then a final union review (approve, 0 findings). 09.2.2-T1: core entity shells + 13 configurations + audit timestamps, plan §3 live-catalog tests; CR-44 (jsonb columns stay string), CR-45 (`AutopilotRunOutcome`, paths, NTS-922-SEC-001 `[OPEN]`). 09.2.6-T1: Worker host, Hangfire server/storage, `IJobScheduler`, `[JobPolicy]`, recurring registration, health on 8082; O2 `[OPEN]`. Integrated tree `c45bed2`: unit 108/108, integration 163/163, format 0, `openapi.json` 3.1.1 |
| 2026-09-30 | Tiered review gates (`docs/tiered-review-gates`, PR #47 → `30dd82e`) | Owner rule: per-task gates tiered by risk T0–T3 (T3 over size; T0 docs-only = one doc review, no QA agent). One home for the rule: `~/.omp/agent/RULES.md`; `plans/00-overview.md` §10, `WORKFLOW.md` §8.1, `AGENTS.md` point to it; 01 CR-26. Gate: T0 doc review, 2 findings fixed (`e44dc90`), closed on re-check |
| 2026-09-30 | STATUS split (`docs/status-archive`) | History moved verbatim to `docs/status-archive.md`; this file keeps Resume here, progress by plan, open tasks, security gates, open findings and recent log rows. Docs only (T0) |
| 2026-09-30 | Owner decisions (`docs/owner-decisions-0930`) | NTS-922-SEC-001 → (a) composite same-owner FKs (09.2.2-T2/09.2.3-T1 Notes, 09 README W3-O7); 09.2.6 O2 → separate deploy/migration role (09.2.6 plan.md, W3-O8); phase 1 local only, hosting sign-off and 13 OI-1 deferred to phase 2 (`DECISIONS.md` §10.4.1, §10.13). Docs only (T0) |
| 2026-09-30 | Phase-split residuals (`docs/phase-split-residuals`) | TL follow-up to #49: 00 §6 states composite `(parent_id, user_id)` FKs (09 W3 CR-46); 00 §4 M1 row split phase 1 (Mac, private publishing) / phase 2 (cloud, public legal pages, audit filed); 11.5.3-T3 and 13.4.2-T1 → phase 2; 03.1.2-T1, 04.2.3-T1, 05.3.2-T2 use local 11.1.1-T2 buckets in phase 1; 10.10.2-T1 → 11.5.1-T1 soft. Docs only (T0); T0 review fixes: 04.2.3-T1/03.1.2-T1 upload steps + acceptance dev-only in phase 1 (04.2.3-T1 [OPEN]: tool must upload already-recorded keys to new envs before phase 2), 10.10.2-T1 phase-1 run on the optional Compose stack, YouTube audit filing timed to phase 2 in 00 §4.1/§13, DECISIONS §10.12, 06, 13 |
| 2026-09-30 | 09 wave 4 (`feature/09.2.2-T2-initial-migration` `2323956` PR #51, `feature/09.2.3-T1-tenant-filter` `694e912` + STATUS `9b01a48` PR #52; both merged → `develop` `0e3f306`) | Parallel worktrees off `ec75a22`, T3 gates. 09.2.2-T2: QA D1 (bundle needed env var → placeholder fallback), code review 3 harness fixes (N-1 column projection, N-1 seed tables, unbounded→bounded narrowing); perf/security clean. 09.2.3-T1: QA Guid.Empty sub + role-without-sub fixed, code review For-after-dispose re-arm fixed; perf/security clean. Each branch 296/296, integrated 321/321 on PG17, format 0; post-merge `develop` re-run 321/321 |
