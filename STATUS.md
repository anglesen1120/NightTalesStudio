# Implementation status

The single place to see what is **done**, **in progress**, **blocked** and **next**. Update it in the same commit as the work. Plans stay the contract (`plans/`); this file only tracks execution.

Legend: `done` (acceptance checks run and passed) · `doing` · `blocked (<who>)` · `todo` · `partial` (see note).

## Resume here

- **Last session:** 2026-09-25, day 2 of plan 01 (branch `feature/01-env-setup-day2` → PR into `develop`). Day 1 merged in PR #1. `smoke.sh` exits 2 until MPT can start (OpenAI keys + tap).
- **Next actions:**
  1. Owner: store the two **relay** keys (DEC-9: `OPENAI_BASE_URL=https://api.aixingialaire.shop/v1` in `pins.env`): `worker-node/bin/secrets.sh set nts.openai.story`, then `… set nts.openai.image`. Set a spending cap in the relay account. The OpenAI-dashboard checklist below (01.2.4-T1) no longer applies (CR-15).
  2. ~~Owner: pick ownership proof~~ → decided 2026-09-25: signed per-render hash manifest (DEC-8, CR-14; designed in plan 05/06).
  3. After 01.2.4-T1: 01.2.4-T2/T3 (projects, keys, access checks), then 01.3.1-T2 (usage tap). That unblocks the live checks in 01.2.2-T4/T5 and the MPT offline proof in 01.2.3-T1.
  4. Day 3: 01.3.1-T3 collector. After plan 04 proxy: 01.2.2-T6, 01.3.1-T4.
- **Environment (DEC-5, DEC-6):**
  - Implementation machine = this Mac (M4, 16 GB, macOS 27.0).
  - `NTS_HOME=<repo>/.nts` (gitignored; `lib.sh` default).
  - D13 mode `accepted-risk`: no volume encryption. Keys must never be committed or pushed: `.gitignore` plus the gitleaks hooks.
- **Before any work in a fresh clone:** `git config core.hooksPath .githooks`, then `worker-node/bin/bootstrap.sh all`, then `worker-node/bin/smoke.sh`.

## Plan 01 — Environment setup (29 tasks)

| Task | Status | Evidence / note |
|---|---|---|
| 01.1.1-T1 Tools + version manifest | done | Installed uv 0.12.18, gitleaks 8.30.1, shellcheck 0.11.0. `env-versions.sh`: 16 keys; `mpt_commit` shows the clone SHA once cloned; exit 2 without tools; no user/host/paths in output. D13 recorded in `worker-node/README.md` |
| 01.1.1-T2 Pins | done | `worker-node/pins.env`: 13 keys (D2 incl. `PROXY_PYTHON`). MPT kept at candidate `3d5f4e42` (main is ahead by 2 commits: an LLM provider and subtitle-font validation). VieNeu `c1390abb` (3.8.3, `uv.lock` tracked). Codec re-pinned to `…-Nano-ONNX` (CR-12) |
| 01.1.1-T3 Runbook + clean-account rehearsal | todo | Runbook sections accumulate in `worker-node/README.md`. The rehearsal runs last, after 05.5.2 |
| 01.1.2-T1 Layout, `.gitignore`, `$NTS_HOME` | done | Check: 11 paths ignored, 7 kept. Added `.nts/` and `!worker-node/pins.env` (`*.env` hid `pins.env`, CR-11). `tmutil isexcluded` → `[Excluded]` (no Time Machine destination is configured) |
| 01.1.2-T2 `lib.sh` + `secrets.sh` | done | `init-local`: created ×3, then unchanged ×3. `check` shows the OpenAI keys missing. Value length 64. `secret` distinguishes a missing item from an unknown service. `security -i` store and verify pass. `check --local --acl` → 3 × `acl ok`. No value appears in any output |
| 01.1.2-T3 `bootstrap.sh` | done | Fresh run 42 s: `mpt … sync=frozen strip=skipped` (the strip script arrives with 01.2.2-T3), `vieneu … sync=frozen`. Second run 2 s with no clone or fetch. HEAD is detached. Python 3.11 / 3.12. Untracked files survive. Wrong pin → exit 2. Dirty tree → exit 1 |
| 01.1.2-T4 gitleaks hooks | done | Tree scan and history scan exit 0. One reviewed false positive is recorded in `.gitleaksignore` (`plans/00-overview.md:412`). Throwaway repo: commit with a secret blocked, clean commit ok, pre-push blocks a `--no-verify` leak. Both hooks fail closed |
| 01.2.1-T1 VieNeu install + weight pins | done | Facts V1/V3/V5 confirmed at the pin. Licences apache-2.0 (model and codec). First download + load took 104 s. `weights.sha256`: 15 files, 7 under `onnx_update/`, 0 under `onnx_int8/`. Offline start: ready in 1.4 s, 0 Hub requests. `--fetch` and `--verify` → `OK 15 files` |
| 01.2.1-T2 `start-vieneu.sh` | done | READY after 2–4 s. Listens only on `127.0.0.1:8000`. The key is in the server env and in no process argv: the child env is built with builtins after the review finding. Caller variables are dropped (canary and `HF_TOKEN` absent). Second start → exit 2. `HOST=0.0.0.0` from the caller is ignored. Env-file guards (HOST, int8, key in file) → exit 2. Ctrl-C → exit 0, port freed. The Wi-Fi-off check was replaced by `HF_HUB_OFFLINE=1` with 0 Hub requests in the log |
| 01.2.1-T3 Smoke scripts | done | `smoke-vieneu.sh`: every line PASS/INFO. Voices 25. `POST /v1/voices` unauthenticated → 401 ×2. `GET /v1/voices` without a key → 401. RIFF size `ffffffff` (04 F3). `pcm_s16le,48000,1`. Snapshot committed; the drift test FAILs as intended. `check-binds.sh` flags `*:8001` and 8505. `smoke.sh` → exit 0 |
| 01.2.1-T4 Mac baseline, 120 s gate, watermark | done | `trial/baselines/vieneu-mac-20260925.json`: RTF 0.176–0.186, **gate 120 s pass** (projected 55.7 s per 300 s audio; informational, 04 CR-2), cold start 4 s / 2 s, idle RSS 512 MB, peak RSS ≈ 1.44 GB, 9 runs all 200. Narration rate: vi 263 syllables/min, en 200 words/min (01.2.1-T4 step 1 assumed 170 / 150 [INFERENCE]). Four WAV samples in `$NTS_HOME/runs/samples/` (RIFF/data `ffffffff`, decode ok). `-2.json` = the `--extra watermark` run. Watermark: DEC-7 / CR-13 |
| 01.2.2-T1 MPT install at pin | done | HEAD `3d5f4e42` = pin, Python 3.11.16, `uv sync --frozen` idempotent, ffmpeg 9.0.2 via `IMAGEIO_FFMPEG_EXE`, MIT. Pinned example snapshot + sha256 in `worker-node/mpt/`. M1 line anchors shifted at the pin (log_level L9, listen_host L11, port L12, [app] L14, [whisper] L500, [kokoro] L576); keys and defaults match |
| 01.2.2-T2 Config template + renderer | done | `render-mpt-config.py --self-test` 7/7, `--check` all guards PASS, output 0600, drift diff redacted, unexpected-key shows `<set>`. SEC-09 with the live OpenAI values: partial (keys missing; proven with the real MPT/proxy keys + in-process test values). Added `--allow-direct-openai` for `start-mpt-api.sh --no-tap` |
| 01.2.2-T3 Strip music + CC0 placeholder | done | 29 bundled tracks removed, idempotent; plant/tamper/symlink tests removed; `bootstrap.sh mpt` prints `strip=ok`. Placeholder `nts-ambient-pad-cc0.mp3` generated in-house with ffmpeg (90 s), row in `LICENSES.csv`; header upgraded to the T3 12-column form. A BA-picked track can replace it |
| 01.2.2-T4 `start-mpt-api.sh` | partial (keys, tap) | All guards PASS: no tap → 2, port busy → 2, foreign 8002 listener → 2 before any Keychain read, edited `tap.json` → 2, missing `nts.openai.story` → 2 naming the service only, no WebUI script. Live start, bind and env checks wait for 01.2.4-T2 keys + 01.3.1-T2 tap. Fix vs spec: secrets are read as standalone assignments; inside `VAR="$(secret …)" cmd` a failing `secret` doesn't stop the script |
| 01.2.2-T5 `smoke-mpt.sh` | partial (keys, tap) | API down → `FAIL mpt.listening`, exit 2; `smoke.sh` propagates it, so **`smoke.sh` exits 2 until MPT can start**. Offline rows (binds, `--check`, WebUI absent, mode 600, self-test, music) pass one by one. Live rows 2–9 and 12 wait for MPT |
| 01.2.2-T6 Wiring render | todo | Needs plan 04 proxy (04.1.1/04.1.2) + tap |
| 01.2.3-T1 Whisper model fetch | partial (offline proof via MPT) | `fetch-whisper-model.sh`: 7 files, 1.5 GB, `--verify` OK, re-run 3 s, tamper → 1, no symlinks, ignored by upstream `.gitignore`, MIT, survives `bootstrap.sh mpt`. Offline load proven directly with faster-whisper + `HF_HUB_OFFLINE=1`; the MPT-API proof waits for T4. This Mac drops IPv6, so the script forces IPv4 when a v6 probe fails. `mobiuslabsgmbh/…` now redirects to `dropbox-dash/…`; the pin SHA is still HEAD |
| 01.2.3-T2 Whisper baseline | done | `trial/baselines/whisper-mac-20260925.json`: RTF vi 0.46–0.66, en 0.31–0.36; peak RSS 2.43 GB; language detection is correct for all 4 (p ≥ 0.999), so CR-5 isn't needed. Params = MPT `subtitle.py:62-68` |
| 01.2.4-T1 Org verification + billing guard | blocked (owner) | Owner-only dashboard work; checklist below |
| 01.2.4-T2/T3 Projects, keys, access checks | todo | Waits for 01.2.4-T1 |
| 01.3.1-T1 Schema v2, dictionary, prices | done | `trial/measurements.csv` 58 columns (first 31 = 01 §5.3), CRLF, no BOM; `trial/schema.md` 58 rows; `trial/prices.json` checked 2026-09-25 against the official OpenAI pricing pages |
| 01.3.2-T1 RSS sampler | done | `trial/tools/rss_sampler.sh --selftest 60` passes: 57 rows, p95 79 ms, CPU 1.3 %. `ps rss` is in KiB (checked against `top`). TERM stop 75 ms. Roots and pressure are sampled on staggered ticks to stay under the 100 ms p95 |
| 01.3.1-T2 usage tap, T3 collector, T4 calibration | todo | T2 waits for the OpenAI keys; T3 day 3; T4 after 01.2.2-T6 |
| 01.3.3-T1/T2 Carry-over | todo | After 05.5.2. Interim whisper notes: `.nts/runs/whisper-baseline/carryover-notes.md` |

Other plans: not started. Order after plan 01: 02 M0 → 03 M0 → 04 → 05 → Gate 1 (`plans/features/README.md`).

## Owner checklist — 01.2.4-T1 (OpenAI dashboard, start first: external wait)

Spec: `plans/features/01-environment-setup/01.2.4-openai-access/01.2.4-T1-org-verification-billing-guard.md`. Never paste a key, card or ID data anywhere; write back only the values in step 5.

1. **Verification.** https://platform.openai.com/settings/organization/general → "Verify Organization". Record `verified`, or `pending` + the UTC submit time, or `not_shown`.
2. **Prepaid credit.** Settings → Billing: confirm prepaid, buy **$30** (proposal), turn **auto-reload off** (it is on by default), reload the page and confirm it is off.
3. **Hard limit.** https://platform.openai.com/settings/organization/limits → Spend → monthly limit **$30**, **Enforce a hard limit = on** (not alert only). An optional alert at $15 may be added.
4. **Read** the usage tier (expect Tier 1), the `gpt-image-2.5-flare` rate limit (expect 5 images/min), the `gpt-6-luna` RPM/TPM, and the approved usage limit.
5. **Report back** for T3: `verification`, `verification_submitted_at`, `auto_reload=false`, `hard_limit_usd=30`, `credit_purchased_usd`, `usage_tier`, `image_rate_limit_ipm`, `story_rate_limit`, `usage_limit_usd`, and any dashboard labels that differ from the steps above.

## Findings during implementation (recorded as CRs / notes)

- **CR-11** — `NTS_HOME=<repo>/.nts` (DEC-5). `*.env` hid `worker-node/pins.env` → `.gitignore` adds `!worker-node/pins.env`.
- **CR-12** — The Mac ONNX path uses codec `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX`, not `…-Nano` (V5 was wrong for ONNX). The PyTorch path (GPU worker) uses `…-Nano`. Plan 11.3.1 must pin it: **[OPEN] for plan 11**.
- **DEC-6** — No volume encryption. The only hard rule: API keys are never published. D13 mode `accepted-risk`.
- **Bootstrap spec gap (01.1.2-T3)** — A fresh `git clone --no-checkout` shows every path as deleted. The cleanliness check therefore skips a clone made in the same run.
- **Watermark (01.2.1-T4 steps 2 and 4; DEC-7, CR-13)** — without the extra: class **C** (silent off). `import perth` → ModuleNotFoundError, and `src/vieneu/base.py:128-131` logs only at DEBUG. **With `--extra watermark` it is still off (class B):** `resemble-perth` 1.0.1 is locked with no dependencies (`uv.lock:3381-3387`), so there is no torch, and the server logs "Watermarker init failed" (`base.py:134-144`). Cost of the extra: RTF +11–25 %, idle RSS 512 → 662 MB. `uv sync --frozen` without the extra removes it again (Q5 answered). **Decision (owner):** keep the watermark; it is required in production, where the node syncs `--extra cuda --extra watermark` and fails closed unless `Audio watermarking initialized (Perth)` is logged (11.3.1-T1). The Mac M0 trial runs without it. **Caveat, verified in source:** Perth `apply_watermark(self, signal, sample_rate, **_)` takes no payload, so every Perth user gets the same mark. It marks audio as AI-made but **cannot prove NightTales ownership**. **Ownership proof decided (owner 2026-09-25, DEC-8, CR-14): option (a)**, a signed per-render hash manifest (sha256 of each rendered file + time + signature, kept by the system). Rejected: a payload watermark (new dependency) and paid Resemble (second paid provider). Design is [OPEN] for plan 05/06 detailing.
- **Cold start** — 1–4 s from the warm cache on this Mac (V2 expected ≈ 19 s).
- **Narration rate** — VieNeu ignores `speed` (04 F4). The measured vi 263 syllables/min and en 200 words/min are faster than the 170 / 150 assumed in 01.2.1-T4 step 1 [INFERENCE], so a 5-minute story needs ≈ 1,300 vi syllables. Follow-up: a CR to the plan 02 length tables (not raised yet).
- **Code review (Day1Review, 2026-09-25)** — 4 findings, all fixed and re-verified:
  - (1) `env -i … KEY=…` put the key in the argv of `/usr/bin/env`; the env is now built with builtins and `exec`.
  - (2) The bench SIGKILL fallback orphaned the listener; it now kills the listener and asserts the port is free (shellcheck clean; the fallback runs only when the server hangs, so it was not exercised).
  - (3) The `xargs tar` tree-scan could drop files; it now uses `tar --null -T -` (606/606 files).
  - (4) Stale "volume" wording in `lib.sh` and the D1 amendment was aligned to DEC-6.
- **bash 3.2** — macOS `/bin/bash` is 3.2. No `mapfile`, `declare -A` or `${x@Q}` in any script.
- **QA pass (QaDay1, independent test agent, 2026-09-25)** — re-ran the acceptance checks of all 10 day-1 tasks plus exploratory negatives. Nothing FAILs in the matrix (items overridden by CR-11/DEC-6 are marked AMENDED/SKIP). Defects:
  - F2 (M): `check --acl` was flaky, one dump per service at ≈ 70 s each. **Fixed:** one dump per run with one retry on a miss, ≈ 24 s.
  - F3 (L): the gate decision fields were missing in `-2.json`. **Fixed.**
  - F4 (L): `init-local` accepted extra args. **Fixed:** exit 2.
  - F7, F8: intended behaviour.
  - Spec-text nits, where the task file stays and the runbook form is authoritative:
    - F1: `gitleaks dir .` walks the ignored `.nts/`; use the runbook tree-scan recipe.
    - F5: the encoder grep counts `aac_at` too, giving 3.
    - F6: bare `shellcheck` prints SC1091; use `shellcheck -x`.
    - The SEC-07 negative test cannot fail: an item created by `/usr/bin/security` always trusts `/usr/bin/security`, even without `-T`.

- **QA pass (QaDay2) and code review (ReviewDay2), day 2, 2026-09-25.** QA reproduced every STATUS claim for the 8 tasks; the live-API rows are legitimately partial. Fixed and re-tested:
  - QA D1 (M): the sampler self-test sampled a foreign tree when 18099 was already taken. It now requires the port free (busy → exit 2) and only accepts its own listener.
  - QA D2 (M): the self-test printed criteria but always exited 0. It now prints `selftest: PASS|FAIL` and exits 1 on any failed criterion.
  - QA D3 (L): the load buffer was compressible. It now uses `os.urandom`.
  - Review (M): `start-mpt-api.sh --no-tap` also skipped the 8001 proxy-listener ownership check, while the proxy key still goes to 8001. Now only the 8002 check is skipped; a foreign 8001 listener → exit 2.
  - Review (L): `smoke-mpt.sh` `mpt.no_value_leak` passed after checking nothing. A missing redacted state file is now a FAIL.
  - Review (L): `--template`, `--mpt-root` and `--source` given with no value fell back to the default. Now exit 2.
  - Noted, not fixed:
    - Review (L): between root refreshes (≤ 10 s), a reused pid could be walked as the service tree.
    - QA D4: the M1 line anchors in the task files are stale; the pin-verified numbers are in the template comments.

## Session log

| Date | Session | Done |
|---|---|---|
| 2026-09-25 | Day 1, plan 01 | 01.1.1-T1/T2, 01.1.2-T1…T4, 01.2.1-T1…T4 done. DEC-5, DEC-6, DEC-7, CR-11, CR-12, CR-13. Code review: 4 findings fixed. QA pass: 3 defects fixed. Ownership proof = signed manifest (DEC-8, CR-14). Branch `feature/01-env-setup-day1` → PR #1 into `develop` |
| 2026-09-25 | Day 2, plan 01 | 01.2.2-T1…T3, 01.2.3-T2, 01.3.1-T1, 01.3.2-T1 done; 01.2.2-T4/T5 and 01.2.3-T1 partial (OpenAI keys + tap). QA: 3 defects fixed; review: 3 fixed. Branch `feature/01-env-setup-day2` → PR into `develop` |
