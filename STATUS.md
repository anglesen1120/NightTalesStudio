# Implementation status

The single place to see what is **done**, **in progress**, **blocked** and **next**. Update it in the same commit as the work. Plans stay the contract (`plans/`); this file only tracks execution. History (old Resume-here blocks, per-task evidence tables, findings, full session log) lives in [`docs/status-archive.md`](docs/status-archive.md); keep this file short.

Legend: `done` (acceptance checks run and passed) · `doing` · `blocked (<who>)` · `todo` · `partial` (see note).

## Resume here

Keep this block ≤ 15 lines: current state and next step only. Move the previous block to the archive when you replace it.

- **Current (2026-10-03).** `develop` @ `6de35c6`. Owner merged every Session 2 next-lane PR: #84 11.1.1-T2, #85 test-fixture fix, #86 09.3.2-T3, #87 11.3.3-T1, #88 11.1.2-T1, #89 11.3.5-T2, #90 09.2.6-T2, #92 09.9.2-T2, #94 09.7.2-T1, plus docs #91, #93, #95, #96. No open PR.
- **Merge notes:** migrations on `develop` run `…083148_09_EmailMessages` → `20261002101251_09_AuthChallenges` (#86) → `20261002162527_11_WorkerNodes` (#87). #89 merged develop (6 additive conflicts, `2b0a3c3`) plus post-merge fix `911740e`: `MptHttpClient` allow-listed for 11.3.3-T1's HttpClient rule, and `AdapterOptions.ParseLoopbackMptBaseUrl` (loopback IP-literal http only); QA + T3 reviews, full local gate, CI 4/4.
- **Not on `develop`:** 09.3.3-T1 password reset, local `7e0ece8` on `feature/09.3.3-T1-s2` (gated); merge develop, regenerate its migration after `20261002162527`, re-gate, then PR.
- **11.3.5-T4 must:** wire `ParseLoopbackMptBaseUrl` into `ValidateOnStart`; build the MPT client with `MptHttpClient.Create` (redirects off). Known low gap QA89-LB-01: a non-root `MptBaseUrl` path is accepted and every MPT call then 404s.
- **Next:** not planned yet (owner 2026-10-03). Unblocked: password reset delivery, `13.3.1-T1` (storage merged), `09.3.4-T1` /me (flags merged), `09.9.3-T1` OTLP (storage + registry merged); migration fillers `05.3.2-T3`, `02.2.2-T1`, `02.2.4-T1` wait for slots (rule 3). Web UI stays in its separate session.
- **SEC-01:** owner chose range API + real top-N seed fallback (09 W3-O3); current bundled seed is synthetic. `09.3.1-T2` replaces it before registration opens.
- **Gates/environment:** risk-tier gates per 00 §10; native Mac stack, runtime under `NTS_HOME`, keys stay out of git. `gate.sh` provides `Email__AddressHashPepper`.
- **Setup:** `docs/dev-setup.md`; `git config core.hooksPath .githooks`; worker bootstrap + smoke per `worker-node/README.md`.

## Progress by plan

Per-task evidence for `done` rows: `docs/status-archive.md` §Per-plan evidence tables.

| Plan | Done | Open (see §Open tasks) |
|---|---|---|
| 01 Environment (29 tasks) | 25 + 01.2.4-T1 n/a (DEC-9: no OpenAI org, calls go through the relay) | 01.1.1-T3, 01.3.3-T1, 01.3.3-T2 |
| 02 Story writing (M0 slice) | 11 tasks — M0 slice complete (presets, harness, A/B, D15 freeze, 02.5.2-T0 spike); M1: 02.2.1-T1 #75, 02.2.3-T1 #76 | the other M1 tasks (02.2.2-T1, 02.2.4-T1 next) |
| 03 Images (M0 slice) | 4 (03.1.1-T1, 03.4.1-T1, 03.3.2-T1, 03.1.1-T2 #61) | 03.1.2-T1, 03.2.x, 03.4.2/03.4.3 (M1) |
| 04 Voice proxy (12 tasks) | 11 (04.2.2-T2 #73) | 04.2.3-T1 |
| 05 Video/subtitles | 10 (05.5.1-T2 #78) | 05.3.2-T3, 05.2.1-T1, 05.5.2 |
| 09 Backend | 33 tasks merged (Session 2: #72 09.9.5-T1, #74 09.10.4-T1, #77 09.3.5-T1 + 09.8.1-T2, #82 09.2.6-T3, #86 09.3.2-T3, #90 09.2.6-T2, #92 09.9.2-T2, #94 09.7.2-T1) | 09.3.3-T1 password reset gated locally, not pushed (migration regen); 09.3.4-T1 /me, 09.9.3-T1 OTLP unblocked |
| 11 Worker deployment | 11.1.1-T1 #81, 11.3.5-T1 #79, 11.1.1-T2 #84, 11.3.3-T1 #87, 11.1.2-T1 #88, 11.3.5-T2 #89; 11.2.1-T1 (proposal only; sign-off deferred to phase 2, owner 2026-09-30) | The rest is phase 2 (11 CR-18) |
| 10 Web UI | HTML design deck approved by the owner 2026-10-02 (`design/ui/index.html`, v2 “Phòng thu”, compact controls `192cc73`); linked from 12 T0 tasks and 13 feature plans. Angular implementation/integration plan refinement merged (#95 -> `185f6fd`): [implementation roadmap](plans/features/10-web-ui/implementation-roadmap.md), [integration readiness](plans/features/10-web-ui/integration-readiness.md), `Backend / upstream tasks to run first` column in the README task tables | Angular implementation not started; start with 10.1.x foundation per the roadmap; backend prerequisites in integration readiness remain OPEN |
| 13 Compliance | 13.1.1-T1, 13.1.2-T1 (PR #35) | 13.2.1-T1 → 13.2.2-T1, 10.3.1-T1 pages; 13.1.1-T2 / 13.1.2-T2 lawyer rows (O1) |

Plans 06, 07, 08, 12: not started. Plan 10: approved HTML design handoff and refined implementation plan (#95); implementation not started. Order: `plans/features/README.md`.

## Session 2 plan

**Approved by the owner 2026-10-01.** Current lanes below; the original kickoff matrix and completed D/G rows are archived. Independent code/gates run in parallel worktrees; delivery and next-lane tasks retain prerequisite merge order. Web UI stays out of this session.

| Lane | Current | Next, after prerequisite merges | Tier | Notes |
|---|---|---|---|---|
| B Storage follow-on | 13.3.1-T1 not started; storage (#84, #94) merged | Owner plans the next wave | T3 | Migration |
| E E-mail | 09.3.3-T1 local @7e0ece8, QA + T3 + full gate closed | Merge develop, regenerate migration after `20261002162527`, re-gate, then PR | T3 | Not pushed; rule 3 |
| F Seeders/checkers | 05.3.2-T3 not started (04.2.2-T2 / 05.5.1-T2 merged) | Free migration slot, then AddMusicTracks task | T3 | Migration |
| Fill a free slot | #92 feature flags merged | /me (flags merged); OTLP (storage + registry merged); 02.2.2-T1 / 02.2.4-T1 await migration slots | T3 | Money tasks have migrations |

**Progress (2026-10-03):** start-now set (#72–#83) and next-lane PRs (#84–#94) merged; `develop` @ `6de35c6`. Remaining Session 2 work: password reset delivery, 13.3.1-T1, /me, OTLP and the migration fillers. Finished lane rows and the Open-task rows of merged PRs are archived.

**Not in Session 2:** `09.6.2-T1` needs `05.1.2-T1`, which needs `02.3.2-T1` and `03.2.1-T1` (not built) → session 3 or later.

**Rules for every agent in this plan:**
1. One worktree + branch per task: `../nts-wt/<task-id>`, branch `feature/<task-id>-s2`, off current `develop`. No stacked branches on unmerged work.
2. At most 8 subagents at a time. Code in parallel; **gates run one at a time** (`/Volumes/Project/nts-wt/gate.sh <worktree>`), because all worktrees share the local Postgres template `nts_template` (parallel runs fail with 23505).
3. Migrations: at most 2 tasks with a migration waiting for gate/merge at once. Merge them in order; the later task **regenerates** its migration on the merged `develop` snapshot (`dotnet ef migrations add`), never hand-edits `AppDbContextModelSnapshot`. Check migration timestamp order after each merge.
4. Conflicts in `Program.cs` (`Map*` lines) and `AppDbContext.cs` (`DbSet` lines) are additive: keep both sides; each `DbSet` keeps its own `///` summary.
5. Each task closes its tier gates (QA agent first, then reviews per `~/.omp/agent/RULES.md`) before its PR. After any merge, check `git merge-base --is-ancestor <branch-tip> origin/develop`.
6. Local services: start `weed` and `mailpit` only through `tools/dev/up.sh` with the loopback flags in 11.1.1-T2 (never `brew services`, which binds all interfaces and puts the SeaweedFS volume server on MPT's 8080). Until 11.1.1-T2 lands, nothing else may depend on them.
7. Update this table's progress in the task's own PR (STATUS row + Session log); move finished lane rows to the archive at session end.

## Open tasks

| Task | Status | Note |
|---|---|---|
| 09.3.3-T1 Password reset | todo (migration slot free) | Local `7e0ece8` gated; merge develop/regenerate migration before delivery |
| 05.3.2-T3 Music metadata | blocked (migration queue) | Not started; at most 2 migration tasks waiting |
| 02.2.2-T1 M1 domain migration | doing (lane A, branch `feature/02.2.2-T1-s2`) | Implemented; gates pending |
| 02.2.4-T1 M1 cost persistence | blocked (migration queue) | Not started |
| 13.3.1-T1 Deletion engine | todo (storage merged) | Lane B next |
| 09.3.4-T1 /me | todo (flags merged) | Free-slot next |
| 09.9.3-T1 OTLP | todo (storage + registry merged) | Free-slot next |
| 01.1.1-T3 Runbook + clean-account rehearsal | todo | Runbook sections accumulate in `worker-node/README.md`. The rehearsal runs last, after 05.5.2 |
| 01.3.3-T1 Carry-over | partial (§11 `[OPEN]`) | `worker-node/CARRY-OVER.md` §1–12 written; §11 waits for the 05.5.2 trial rows |
| 01.3.3-T2 Carry-over walkthrough | todo | Owner walkthrough, after 05.5.2. Interim whisper notes: `.nts/runs/whisper-baseline/carryover-notes.md` |
| 03.1.2-T1, 03.2.x, 03.4.2/03.4.3 | todo (M1) | Need 09 and 11.3.5 |
| 04.2.3-T1 Preview generation + upload | partial | M0 generation, owner listening and T19 option A done (PR #26); local SeaweedFS upload rehearsal passed; M1 upload waits for 11.4.1-T2 → 11.4.1-T1. SEC-0423T19-1 (info) open. A 2nd vi rater + an en rater → catalog revision 2 (04 T18) |
| 05.2.1-T1 Subtitle fidelity + sync evaluation | partial (machine leg) | L1/L2 rows + owner labels blocked on 05.5.2-T1 (05 T84); `RenderQuality:*` values await calibration |
| 05.5.2 | todo — prep on branch `feature/05.5.2-m0-trial` (3 commits, not on `develop`) | Branch: `apply_quality_policy.py` (D10 mirror) + `mpt_client` spec-path containment, gates passed; L1 vi / L2 en renders blocked on the D11 memory gate (swap). Needs 04.2.2-T1 raters and 03.3.2-T1 (done). Portrait renders use 9:16 option A (05 O9, 03 CR-16) with the hinted prompt; judge the cropped frames of the first portrait render (`trial/images/REPORT.md` §Crop probe) |
| 13.2.1-T1 Legal versions + acceptance | todo | Versions config + `GET /me/legal` + `POST /me/legal/accept` + gate + owner dialog; then 13.2.2-T1 `tools/ci/check-legal-versions.py` and 10.3.1-T1 pages / `legal-to-json.mjs` |
| 13.1.1-T2 / 13.1.2-T2 Legal review rows | todo (O1) | The ⚠️ rows (children/transfers/retention; credit expiry and refunds; OpenAI output terms; governing law; liability) close with a lawyer; OI-1 (operator legal name + contact) deferred to phase 2 (owner 2026-09-30), before any user other than the operator |

## Deferred security gates

**Deferred security gates (security review 2026-09-29, must close before any feature endpoint/command/query lands):** Closed 2026-09-30: `AuthorizationBehavior` (09.1.3-T2, #54) is deny-by-default (anonymous → `unauthorized`, insufficient role → `not_found`, system actor passes only `User`); the tenant `HasQueryFilter` (09.2.3-T1, #52) covers all 8 `IUserOwned` entities on both contexts; anonymous sees no rows. Closed 2026-10-01: production `Program.cs` registers ES256 JwtBearer (09.3.2-T1, #60), so feature endpoints may merge; the series routes (09.4.1-T1) are its first consumer.

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
| 2026-10-02 | Session 2 lanes A–G (worktrees `../nts-wt/<task-id>`, branches `feature/<task-id>-s2`) | 11 PRs opened, none merged: #72 09.9.5-T1, #73 04.2.2-T2, #74 09.10.4-T1, #75 02.2.1-T1, #76 02.2.3-T1, #77 09.3.5-T1 + 09.8.1-T2, #78 05.5.1-T2, #79 11.3.5-T1, #80 RefreshTokenTests ordering fix, #81 11.1.1-T1, #82 09.2.6-T3. Each through its tier gates (QA agent first, then reviews, one fix agent, delta). 11.1.1-T1 raised T1 → T3 (process handling + secrets): 8 review + 3 security findings fixed, deltas closed; `docker build` not run (no Docker daemon). 09.2.6-T3: two delta rounds (send-lock key collision + silent drop, failure state after a failed `sent` commit, guarded compensating write, pepper required outside Development), last round verified by the orchestrator; full gate green. CI: `integration` on #73/#74/#76/#81 fails only on the flake #80 fixes. Held by rule 3 (max 2 migration tasks waiting): 05.3.2-T3, 02.2.2-T1, 02.2.4-T1, 09.9.2-T2 |
| 2026-10-02 | Session 2 merged (#72–#83 → `develop` `7f91446`) | Owner merged all 12 PRs. #79 conflicted with `develop` after #74 (both sides added `.gitleaksignore` lines; kept both, `27b2364`). #82 conflicted after #75–#81 (additive hunks in `docs/dev-setup.md`, Infrastructure csproj, `InitialCoreMigrationTests`, `SeedPrevious`; kept both sides, `73c3891`) and its migration was regenerated on #77's snapshot (`20261002083148`, `Up`/`Down` unchanged); merge reviewed (0 findings), full gate + CI 4/4 green. Follow-up on `develop` (owner request, pushed directly): `SeedPrevious` comments no longer say "skipped today" (an insert runs once its migration is no longer the last). Docs + comment only |
| 2026-10-02 | Session 2 next lanes (base `00e7d5d`) | Open #84–#90 plus flags #92; owner has not merged them. #84 canonical Storage exports (`50e131d`) and #88 CI ffmpeg (`29d45bf`) fixed and CI 4/4 green. Flags `488bd10`: QA/T3/delta/full local gate closed, CI run `37019151851` 4/4 green. Storage `08943b6`: live QA 26/26 + real suffix/HEAD/GET smoke, T3/delta/full gate closed; later merged with develop, CI-guard fix `13dca85`, pushed as draft PR #94 (waits for #84). Password reset `7e0ece8`: QA/T3/delta/full gate closed, local until migration queue frees. Full local gates trial-integrated test-only #85 then explicitly aborted; scratch merge was removed from storage commit. Native services explicitly stopped (SeaweedFS required SIGKILL after 15 s); user main-checkout WIP untouched. |
| 2026-10-02 | Angular implementation/integration planning (docs only, T0) | Refined existing web task bodies and design/backend hand-offs; added [implementation roadmap](plans/features/10-web-ui/implementation-roadmap.md) and [integration readiness](plans/features/10-web-ui/integration-readiness.md). Preserved 73 tasks/18 feature plans and header metadata; estimates remain P41.0 + S20.0 =61.0 raw /73.2 buffered days. Links/frame references and scoped diff-check pass. Independent reviews/delta closed eight findings; the two residual guard/lifetime contradictions were corrected and verified afterward. Named backend prerequisites remain OPEN; on merging `develop` the design gate follows develop's owner approval record (deck v2, `192cc73`) and stale approval-OPEN/pinned-artifact wording was reconciled; source audit is not runtime readiness. No Angular/backend implementation, commit or push; backend Resume/live state unchanged |
| 2026-10-02 | Web backend scheduling annotation (docs only, T0) | Added `Backend / upstream tasks to run first` to all 73 rows across the three canonical web README tables only, not task-file headers/templates. Direct non-10.x entries are projected verbatim from existing `Depends on`, including soft and CR tags; restored missing `(13 CR-27)` on 10.3.1-T2 during concern review. Inherited FE prerequisites, non-backend upstream owners and P/S/R2 gates remain explicit. Verified 73 seven-column rows and original six columns/order unchanged; task headers and estimates unchanged. Concern review PASS with one P3 wording nit (bare `None` undefined), closed by defining `None` vs `None directly` in the footnote; §6/C6 now say task headers equal the first six table cells. This is a scheduling aid, not merged/runtime-ready status. No code |
| 2026-10-03 | Web planning merged (#95 -> `develop` `185f6fd`) | Owner merged #95 (`40e6187` + merge `68be291`). Merge with develop adopted develop's owner design approval and local `design/ui/` links; mockup/contract mismatches kept as implementation reconciliation notes. 73 task headers equal develop, README tables 73×7, 1013 links resolve. Docs only |
| 2026-10-03 | Session 2 next lanes merged (#84–#94 → `develop` `6de35c6`) | Owner merged #84–#90, #92, #94 (plus docs #91, #93, #96). #89 conflicted after #86/#87 (6 additive hunks: `.gitleaksignore`, `NightTales.sln`, Architecture.Tests `AllowLists`/`ArchitectureModel`/csproj, regenerated `packages.lock.json`; `2b0a3c3`, merge review 0 findings). The merged tree then failed 11.3.3-T1's `HttpClient_IsOnlyConstructedByTheApprovedProducers` on `MptHttpClient`: allow-listed, and the allow-list review found `Adapter:MptBaseUrl` unvalidated, so a loopback IP-literal guard was added (`911740e`). QA 4/4, code/perf/security reviews (2 low nits fixed, 2 refuted with `dotnet fsi`), full local gate `TEST rc=0`, CI 4/4. STATUS refreshed; finished rows archived |
| 2026-10-03 | 02.2.2-T1 `generation_runs` + `story_versions` provenance (lane A, `feature/02.2.2-T1-s2`) | Implemented, gates pending: `GenerationRun`, `Episode.Story.cs` (T1–T9, four events), `StoryVersion` factory + D3 derivation, EF configs, `LockEpisodeForUpdateAsync`, migration `20261002191730_02_GenerationRuns`; the Down check of the last migration moved from `WorkerNodesMigrationTests` to `GenerationRunsMigrationTests`. `[OPEN]` `fact_sheets` does not exist yet, so the three fact-sheet id columns are plain `uuid` until 02.5.3-T2 adds the FKs |
