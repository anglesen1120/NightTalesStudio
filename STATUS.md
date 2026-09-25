# Implementation status

The single place to see what is **done**, **in progress**, **blocked** and **next**. Update it in the same commit as the work. Plans stay the contract (`plans/`); this file only tracks execution.

Legend: `done` (acceptance checks run and passed) · `doing` · `blocked (<who>)` · `todo` · `partial` (see note).

## Resume here

- **Last session:** 2026-09-25, day 1 of plan 01 (`plans/features/01-environment-setup/README.md` §5, "Suggested order"). Branch `feature/01-env-setup-day1` → PR into `develop`.
- **Next actions:**
  1. Owner: **01.2.4-T1** (OpenAI dashboard checklist below). This is an external wait, so start it first.
  2. Owner: pick how to prove **ownership / copyright** of the system's output: [OPEN], see Findings → Watermark. Perth cannot do it.
  3. Day 2 (after 01.2.4-T1): 01.2.2-T1…T5 (MPT), 01.2.3 (whisper), 01.2.4-T2/T3 (projects, keys, access checks), 01.3.1-T1, 01.3.2-T1.
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
| 01.2.2-T1…T6 MPT | todo | Day 2. The MPT clone is already at its pin (bootstrap) |
| 01.2.3-T1/T2 Whisper | todo | Day 2 |
| 01.2.4-T1 Org verification + billing guard | blocked (owner) | Owner-only dashboard work; checklist below |
| 01.2.4-T2/T3 Projects, keys, access checks | todo | Waits for 01.2.4-T1 |
| 01.3.1-T1…T4, 01.3.2-T1, 01.3.3-T1/T2 Measurement, carry-over | todo | Days 2–3 |

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
- **Watermark (01.2.1-T4 steps 2 and 4; DEC-7, CR-13)** — without the extra: class **C** (silent off). `import perth` → ModuleNotFoundError, and `src/vieneu/base.py:128-131` logs only at DEBUG. **With `--extra watermark` it is still off (class B):** `resemble-perth` 1.0.1 is locked with no dependencies (`uv.lock:3381-3387`), so there is no torch, and the server logs "Watermarker init failed" (`base.py:134-144`). Cost of the extra: RTF +11–25 %, idle RSS 512 → 662 MB. `uv sync --frozen` without the extra removes it again (Q5 answered). **Decision (owner):** keep the watermark; it is required in production, where the node syncs `--extra cuda --extra watermark` and fails closed unless `Audio watermarking initialized (Perth)` is logged (11.3.1-T1). The Mac M0 trial runs without it. **Caveat, verified in source:** Perth `apply_watermark(self, signal, sample_rate, **_)` takes no payload, so every Perth user gets the same mark. It marks audio as AI-made but **cannot prove NightTales ownership**. **[OPEN] ownership proof:** (a) a signed per-render manifest (sha256 of each published audio/video + time + signature, kept by the system): in-house, cheap; (b) a watermark with an owner payload: new dependency, needs a licence check [INFERENCE: e.g. Meta AudioSeal, not verified]; (c) a paid Resemble product, which conflicts with "OpenAI is the only paid provider".
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

## Session log

| Date | Session | Done |
|---|---|---|
| 2026-09-25 | Day 1, plan 01 | 01.1.1-T1/T2, 01.1.2-T1…T4, 01.2.1-T1…T4 done. DEC-5, DEC-6, DEC-7, CR-11, CR-12, CR-13. Code review: 4 findings fixed. QA pass: 3 defects fixed. Ownership proof [OPEN]. Branch `feature/01-env-setup-day1` → PR #1 into `develop` |
