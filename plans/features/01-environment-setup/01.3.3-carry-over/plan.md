# 01.3.3 — Carry-over to the GPU worker: feature plan

> Parent: `../README.md` (D1–D12, CR-4, CR-9, gate 01-closed) · high-level: `../../01-environment-setup.md` story 01.3.3 · consumers: `../../11-worker-deployment.md` 11.3.1, 11.5.3 (+ risk "drift between Mac trial and GPU node"), `../../04-voice-proxy.md` 04.1.5, `../../05-video-assembly-subtitles.md` (template 05.1.1, music 05.3.x, trial 05.5.2).
> Legend: ✅ verified (README §1 id) · ⚠️ to confirm · [DECISION] · [INFERENCE].

## 1. Scope

In: one tracked document `worker-node/CARRY-OVER.md` (D1) that lets the plan 11 owner write the 11.3.1 provisioning checklist and the 11.5.3 pin/verify step without asking questions; a walkthrough with that owner whose Q&A is recorded in the document's changelog.
Out: provisioning scripts, systemd units, the node secret manager (plan 11); the proxy unit (04.1.5); template upgrade diffs (05.1.1); any decision on CR-4 metering (03/11/12) — the document only records the current state.

| Item | Value |
|---|---|
| Deliverable | `worker-node/CARRY-OVER.md` (tracked, secret-free) |
| Tasks | 01.3.3-T1 (S), 01.3.3-T2 (XS) |
| Starts after | 05.5.2 trial rows (vi + en) and all other 01 tasks (values must be final) |
| Gate | contributes to **01-closed** (README §7: "`CARRY-OVER.md` reviewed by the plan 11 owner") |

## 2. Confirmed facts

| Id | Used for |
|---|---|
| ✅ M1, M7 | MPT pin, Python 3.11, `uv sync --frozen`, config keys |
| ✅ M10 (`agent://MptFacts`, not in README §1) | MPT licence MIT (`LICENSE`, pyproject `license = { text = "MIT" }`) |
| ✅ V1, V3, V5 | VieNeu env vars, Python 3.12, ONNX CPU on Mac, weight repos (backbone + codec), 25 voices |
| ✅ V7 (`agent://VieneuWhisperFacts`, not in README §1) | VieNeu code Apache-2.0; model weights, ONNX files and preset voices apache-2.0 (HF card) |
| ✅ extras (`agent://VieneuWhisperFacts` V3 install table) | VieNeu pyproject extras `cuda`, `watermark`, `pdf`, `finetune`; groups `dev`, `gpu`; backend auto-selects torch CUDA else ONNX |
| ✅ 01.1 contracts (BaseToolingPlan) | `worker-node/bin/bootstrap.sh [mpt\|vieneu\|all]` clones, detaches at pin, `uv sync --frozen`, no weights; `worker-node/bin/env-versions.sh` prints tool versions JSON (ffmpeg, uv, commits) |
| ✅ W1 | faster-whisper MIT, `mobiuslabsgmbh/faster-whisper-large-v3-turbo`, no Metal → CPU/int8 on Mac |
| ✅ E2 | ffmpeg 9.0.2 with `libmp3lame`, `aac` on the Mac |
| ✅ M6 | fonts in MPT `resource/fonts`; bundled music (stripped by 01.2.2-T3) |
| ✅ 11.5.3 text | node installs from `uv.lock` with hashes; weights pinned by revision + sha256, verified at provisioning |
| ✅ 11.3.1 text | node needs driver/CUDA, ffmpeg, `uv`, fonts, music sync, pinned MPT/VieNeu, proxy, adapter, whisper cache, systemd |
| ✅ 04.1.5 text | proxy ships a systemd unit/container spec for plan 11 |
| ⚠️ | CUDA/driver version for VieNeu PyTorch; `--extra cuda` still present at the pin; PyTorch weight file list on the node; whisper `cuda/float16` on the node GPU; node secret-manager names (resolved: 11 D11/D15, 11 CR-7) |

## 3. Domain and data

N/A — no data model; the document is a static inventory (outline in 01.3.3-T1).

## 4. Use cases

| # | Actor | Procedure | Result |
|---|---|---|---|
| U1 | Developer | Fill `CARRY-OVER.md` from `pins.env`, lock files, `trial/baselines/`, `trial/measurements.csv` (T1) | every §-entry has a source and a value or `⚠️ <owner>` |
| U2 | Plan 11 owner | Reads the document, drafts the 11.3.1 checklist during the walkthrough (T2) | no open question; each question became an edit + changelog line |
| U3 | Plan 11 owner (later) | Diffs a new `CARRY-OVER.md` revision against the node | drift detected by pin/sha256 mismatch |

## 5. API and events

N/A — documentation only.

## 6. UI

N/A — no UI.

## 7. Jobs and integrations

N/A — no runtime component; inputs are files listed in 01.3.3-T1 §Inputs.

## 8. Security and privacy

- `CARRY-OVER.md` contains **names only**: env var names, Keychain service names (D3), secret-manager placeholders. Never a key, token, hash of a key, Keychain dump, or `config.toml` excerpt with rendered values.
- Env var catalog column "secret" = yes → Mac value column says `Keychain: <service>`, node column says `secret manager: nts/<env>/<NAME>` with the names of 11 README D11/D15 (11 CR-7).
- The gitleaks hook (01.1.2-T4) runs on the commit; acceptance also greps the file (T1 checks).
- Rotation/revocation of the trial keys is part of gate 01-closed, not this document; the document only states that trial keys (D7) are **not** carried (node gets its own project, 11.3.2-T2).

## 9. Test plan

| Check | Command / observation | Expected |
|---|---|---|
| Completeness | checklist in 01.3.3-T1 §Acceptance (12 sections present, no empty cell) | all ticked |
| Pins match | `( . worker-node/pins.env; for v in MPT_COMMIT VIENEU_COMMIT VIENEU_MODEL_REVISION VIENEU_CODEC_REVISION WHISPER_MODEL_REVISION MPT_PYTHON VIENEU_PYTHON; do grep -qF -- "${!v}" worker-node/CARRY-OVER.md \|\| echo "missing $v"; done )` | no output |
| No secrets | `gitleaks detect --no-git --source worker-node/CARRY-OVER.md` ⚠️ (flag names at installed version) and `grep -nE 'sk-[A-Za-z0-9]' worker-node/CARRY-OVER.md` | no findings / no output |
| Usability | T2 walkthrough | owner drafts 11.3.1 checklist without asking; Q&A in changelog |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 01.3.3-T1 | Write `worker-node/CARRY-OVER.md` | S (0.5) | 05.5.2, all 01 tasks |
| 01.3.3-T2 | Walkthrough with plan 11 owner | XS (0.25) | 01.3.3-T1 |
| **Total** | | **0.75 d** (= README §9) | |

Size note: kept as README §9. T1 is S only because values are copied from existing artefacts; if sha256 of weights/locks were not recorded by 01.2.1-T1 / 01.2.3-T1 / 01.1.1-T2, computing them adds ≈ 0.1 d.

New files introduced: none beyond `worker-node/CARRY-OVER.md` (already in D1). No new scripts.

## 11. Rollout

1. T1 after the 05.5.2 rows exist; commit `worker-node/CARRY-OVER.md` (hook green).
2. T2 walkthrough; edits + changelog committed; owner's sign-off line in changelog → gate 01-closed item ticked.
3. Afterwards the document is updated whenever `pins.env`, a lock file, the template or a weight revision changes (the same commit), until 11.3.1-T1 lands; then `worker-node/pins.env` + 11 D8/D9 are the source of truth and the document is frozen with a pointer (11 CR-7).
