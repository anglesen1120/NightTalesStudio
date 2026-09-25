# 01.1 — Base tooling: detailed feature plan (stories 01.1.1, 01.1.2)

> Refines `../../../01-environment-setup.md` §4 Epic 01.1, §6 (01.1.1-T1/T2, 01.1.2-T1), §8 (gitleaks row), NFR-01-1, NFR-01-2. Contract: `../README.md` D1–D12 (names used verbatim).
> Template: `../../../00-overview.md` §10.1. Legend: ✅ verified (README §1 id) · ⚠️ confirm at install / pinned commit · [DECISION] · [INFERENCE] · [OPEN].
> Status: Tech Lead review and owner sign-off done 2026-09-24 (README §12). Documentation only — no script exists yet; every script below is specified, not written.

## 1. Scope

| Story | Title | Size (01 §4) | Tasks here |
|---|---|---|---|
| 01.1.1 | Install base tools | S | 01.1.1-T1, -T2, -T3 |
| 01.1.2 | Project folders (+ secrets, bootstrap, hook) | XS | 01.1.2-T1, -T2, -T3, -T4 |

**In scope**
- Homebrew tools `uv`, `gitleaks` (git, ffmpeg already present ✅ E2); version manifest `worker-node/bin/env-versions.sh` (F20).
- Pins file `worker-node/pins.env` with every D2 key filled (F18: no system `python`).
- Repo layout and `.gitignore` (D1, F14, F16); runtime tree `$NTS_HOME` on the FileVault disk (F05).
- Common shell library `worker-node/bin/lib.sh` and Keychain helper `worker-node/bin/secrets.sh` (D3, D9).
- `worker-node/bin/bootstrap.sh`: clone + pin + verify + `uv sync` for MPT and VieNeu (used by 01.2.1-T1, 01.2.2-T1).
- gitleaks pre-commit hook `.githooks/pre-commit`, plus a scan of the working tree before the first commit (E2).
- Runbook `worker-node/README.md` and a timed rehearsal in a clean account (NFR-01-2, gate 01-closed).

**Out of scope** (owner)
- Starting services, smoke tests, and `check-binds.sh`: 01.2.1, 01.2.2.
- Config template and renderer (D5): 01.2.2-T2.
- `strip-bundled-music.sh`: 01.2.2-T3. Bootstrap only calls it.
- Model weight downloads: 01.2.1-T1 (VieNeu), 01.2.3-T1 (whisper). This feature only pins their revisions.
- OpenAI projects and keys: 01.2.4. This feature only provides `secrets.sh set`.
- Measurement tools (`start_run.sh`, `finish_run.py`): 01.3.1. This feature only provides `env-versions.sh` output.
- Proxy (`worker-node/proxy/`): plan 04. This feature only creates the placeholder folder.

**Acceptance criteria (refined from 01 §4)**
- 01.1.1: Given a clean Mac account, when the runbook is followed, then `git`, `uv`, `ffmpeg`, `gitleaks` are on PATH. `env-versions.sh` prints one valid JSON object with their versions and the uv-managed Pythons (MPT 3.11, VieNeu 3.12). `pins.env` holds full 40-char SHAs for 2 code repos and 3 model repos. The whole setup takes ≤ 2 h excluding model downloads.
- 01.1.2: Given the repo, when setup finishes, then the D1 tracked layout exists. `git status --ignored` shows no media, weights, logs or secrets as tracked or untracked-unignored. `$NTS_HOME` exists with mode 700, is excluded from Time Machine, and is on the internal disk. The five Keychain items of D3 exist; values are never printed. The pinned clones pass `git rev-parse HEAD == pin`. A commit containing a fake secret is blocked.

## 2. Confirmed facts

| Id | Fact used here | Where used |
|---|---|---|
| ✅ E1 | Internal disk: FileVault on, 66 GiB free. `/Volumes/Project`: APFS over USB, not encrypted, Owners: Disabled (modes not enforced) | D1 runtime split, `nts_home` guard, §8 |
| ✅ E2 | brew 7.0.6, git 2.49.0, ffmpeg 9.0.2 with `libmp3lame` + `aac`. `uv` and `gitleaks` missing. Ports 8000/8001/8002/8080/8501 free. Repo has no commits | T1, T4 (scan before first commit) |
| ✅ M1 | MPT `main` @ `3d5f4e421927d61f3eac729cf4b711ac0b69d688` (2026-09-21, v1.3.7, MIT). Full SHA from `agent://MptFacts` | T2 candidate `MPT_COMMIT` |
| ✅ M3 | MPT whisper lookup `<mpt_root>/models/whisper-<size>/model.bin` | layout (`$NTS_HOME/src/mpt/models/…`) |
| ✅ M6 | Bundled music in `resource/songs` | T3 strip interaction |
| ✅ M7 | MPT `>=3.11`, `.python-version` 3.11, `uv python install 3.11 && uv sync --frozen` | T2, T3 |
| ✅ M8 | `config.toml` read from the MPT root, auto-copied from the example if missing; WebUI rewrites it via `os.replace` | why `config.toml` lives in `$NTS_HOME` (F05) |
| ✅ V3 | VieNeu Mac install: plain `uv sync` (ONNX, CPU). `.python-version` 3.12 | T2, T3 |
| ✅ V4 | VieNeu `requires-python >=3.10`, uv uses `.python-version` 3.12 (`agent://VieneuWhisperFacts`) | T2 |
| ✅ V5 | Weights `pnnbao-ump/VieNeu-TTS-v3-Turbo` + codec `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano` via `hf_hub_download` (honours `HF_HOME`) | T2 model pins, `$NTS_HOME/hf` |
| ✅ W1 | `large-v3-turbo` → `mobiuslabsgmbh/faster-whisper-large-v3-turbo`; faster-whisper MIT | T2 model pin |
| ✅ F05 / F14 / F16 / F18 / F20 | Review findings this feature closes | — |

**⚠️ Open items (to confirm at install, recorded in the task files)**

| # | Item | Closed by |
|---|---|---|
| U1 | gitleaks licence (expected MIT): check `brew info gitleaks` and upstream `LICENSE` | 01.1.1-T1 |
| U2 | Installed gitleaks subcommand syntax: v8.19+ `gitleaks git --pre-commit --staged` / `gitleaks dir`, older `gitleaks protect --staged` / `gitleaks detect --no-git` | 01.1.2-T4 |
| U3 | Is `uv.lock` tracked at `VIENEU_COMMIT`? Decides `uv sync --frozen` vs plain `uv sync` | 01.1.1-T2, used by 01.1.2-T3 |
| U4 | Does the HF model API `https://huggingface.co/api/models/<repo>/revision/main` return the commit in field `sha`? | 01.1.1-T2 |
| U5 | Output format of `uv python list --only-installed` at the installed uv version | 01.1.1-T1 |
| U6 | `security add-generic-password … -w` as last argument prompts (twice?) for the value; `security -i` reads commands from stdin | 01.1.2-T2 |
| U7 | `tmutil addexclusion <path>` without `-p` needs no sudo (sticky exclusion) | 01.1.2-T1 |
| U8 | MPT's own `.gitignore` ignores `config.toml`, `models/`, `storage/` (untracked files survive `git checkout`) | 01.1.2-T3 |
| U9 | Standard (non-admin) account can use `/opt/homebrew/bin` tools but cannot `brew install` | 01.1.1-T3 |

## 3. Domain and data

No database. The "data" are files, split per D1.

**Tracked files created or changed by this feature**

| Path | Created by | Content |
|---|---|---|
| `.gitignore` | 01.1.2-T1 | Existing rules plus the F16 rules (exact lines in the T1 file) |
| `.githooks/pre-commit` | 01.1.2-T4 | gitleaks staged scan, fail closed |
| `worker-node/README.md` | 01.1.2-T1 (skeleton) → 01.1.1-T3 (runbook) | Runbook |
| `worker-node/pins.env` | 01.1.1-T2 | D2 keys (below) |
| `worker-node/bin/env-versions.sh` | 01.1.1-T1 | Version manifest (JSON on stdout) |
| `worker-node/bin/lib.sh` | 01.1.2-T2 | Sourced helpers |
| `worker-node/bin/secrets.sh` | 01.1.2-T2 | Keychain helper |
| `worker-node/bin/bootstrap.sh` | 01.1.2-T3 | Clone/pin/sync |
| `worker-node/mpt/.gitkeep`, `worker-node/vieneu/.gitkeep`, `worker-node/proxy/.gitkeep` | 01.1.2-T1 | Placeholders; removed once a real file lands |
| `worker-node/music/LICENSES.csv` | 01.1.2-T1 (header only) | FR-05-2 columns; rows added by 01.2.2-T3 / 05.3.2 |
| `trial/runs/.gitkeep`, `trial/tools/.gitkeep`, `trial/fixtures/.gitkeep`, `trial/baselines/.gitkeep` | 01.1.2-T1 | Placeholders |
| `trial/baselines/rehearsal-mac-<YYYYMMDD>.json` | 01.1.1-T3 | Rehearsal timing sheet (fits the D1 `<component>-mac-<YYYYMMDD>.json` pattern) |
| `.gitleaksignore` | 01.1.2-T4, **only if** a reviewed false positive appears | Finding fingerprints, one per line |

**`$NTS_HOME` (default `$HOME/.nts`, never tracked, mode 700)**: `src/`, `src/mpt/` (clone + `.venv` + `config.toml` 0600 + `models/whisper-large-v3-turbo/`), `src/vieneu/` (clone + `.venv`), `src/proxy-venv/` (plan 04), `hf/`, `logs/`, `runs/`, `current_run` (file). The layout is exactly D1.

**`worker-node/pins.env` keys (D2)**: `MPT_REPO`, `MPT_COMMIT`, `VIENEU_REPO`, `VIENEU_COMMIT`, `VIENEU_MODEL_REPO`, `VIENEU_MODEL_REVISION`, `VIENEU_CODEC_REPO`, `VIENEU_CODEC_REVISION`, `WHISPER_MODEL_REPO`, `WHISPER_MODEL_REVISION`, `MPT_PYTHON=3.11`, `VIENEU_PYTHON=3.12`. Invariants, enforced by `load_pins`:
- `*_COMMIT` and `*_REVISION` match `^[0-9a-f]{40}$`.
- `*_PYTHON` match `^3\.[0-9]+$`.
- No other assignments.
- The file is plain `KEY=value` with comments, so both bash and a Python parser can read it.

**`env-versions.sh` JSON (schema_version 1)**: consumed by `trial/tools/start_run.sh` (01.3.1) as `run.json` key `env`. The contract was broadcast to 01.3.1.

| Key | Source command | Example |
|---|---|---|
| `schema_version` | constant | `1` |
| `captured_at` | `date -u +%Y-%m-%dT%H:%M:%SZ` | `"2026-09-23T09:00:00Z"` |
| `macos_version` / `macos_build` | `sw_vers -productVersion` / `-buildVersion` | `"27.0"` |
| `hw_model` | `sysctl -n hw.model` | `"Mac16,10"` ⚠️ |
| `cpu_brand` | `sysctl -n machdep.cpu.brand_string` | `"Apple M4"` |
| `memory_bytes` | `sysctl -n hw.memsize` (number) | `17179869184` |
| `git` / `uv` / `ffmpeg` / `gitleaks` | `git --version`, `uv --version`, first line of `ffmpeg -version`, `gitleaks version` (version token only) | `"2.49.0"` |
| `ffmpeg_encoders` | `ffmpeg -hide_banner -encoders` filtered | `["aac","libmp3lame"]` |
| `uv_pythons` | `uv python list --only-installed` ⚠️ U5 (versions only, no paths) | `["3.11.x","3.12.x"]` |
| `mpt_commit` / `vieneu_commit` | `git -C "$NTS_HOME/src/<p>" rev-parse HEAD` | 40-hex or `"n/a:not-cloned"` |
| `nts_home_on_internal_disk` | `diskutil info "$(df -P "$NTS_HOME" \| awk 'NR==2{print $1}')"` → `Device Location: Internal` and `FileVault: Yes` (⚠️ label text at macOS 27) | `true` (JSON bool; `false` if `$NTS_HOME` is missing) |

Values that cannot be read become `"n/a:<reason>"` (D8 style). The JSON holds no paths, user names, serial numbers or hostnames (§8).

## 4. Use cases (operator procedures)

| # | Command | Precondition | Effect | Exit codes (D9) |
|---|---|---|---|---|
| P1 | `brew install uv gitleaks` | brew ✅ E2 | Tools on PATH | brew's |
| P2 | `worker-node/bin/env-versions.sh` | P1 | JSON on stdout | 0 ok · 2 a required tool is missing |
| P3 | Edit `worker-node/pins.env` by the 01.1.1-T2 procedure | network | Pins filled | — |
| P4 | Layout commands (01.1.2-T1 Steps) | repo | Tracked dirs, `.gitignore`, `$NTS_HOME` | — |
| P5 | `worker-node/bin/secrets.sh set nts.openai.story` (and `nts.openai.image`) | Keychain unlocked | Owner key stored via prompt | 0 · 2 unknown service / no TTY |
| P6 | `worker-node/bin/secrets.sh init-local` | — | Creates missing `nts.mpt.api_key`, `nts.vieneu.api_key`, `nts.proxy.api_key` | 0 · 2 |
| P7 | `worker-node/bin/secrets.sh check [--local]` | — | Lists `present`/`missing` per service | 0 all present · 1 any missing · 2 |
| P8 | `worker-node/bin/secrets.sh rotate-local [<service>]` | services stopped | New random values; restart and re-render needed | 0 · 2 |
| P9 | `worker-node/bin/bootstrap.sh [mpt\|vieneu\|all]` | P1, P3, P4 | Clones at pins, venvs synced, MPT music stripped | 0 · 1 pin/python/tree check failed · 2 env/network |
| P10 | `git config core.hooksPath .githooks` | P1 | Hook active (per clone, not versioned) | — |
| P11 | `gitleaks dir . --redact` (⚠️ U2) before the first commit | P1 | Report in `$NTS_HOME/logs/` | 0 clean · 1 findings |

Every script supports `--help`, prints to stderr only human messages (never values), and is idempotent. A second run with nothing to do exits 0.

## 5. API and events

N/A — no HTTP API or events. Contracts are files and CLIs (§3, §4).

## 6. UI

N/A — CLI only.

## 7. Jobs and integrations

| External call | Used by | Timeout / retry | Failure mapping |
|---|---|---|---|
| Homebrew install | 01.1.1-T1 | brew's | Manual retry; record the error in the runbook |
| `git ls-remote`, `git clone`, `git fetch` over HTTPS to GitHub | T2, bootstrap | git defaults; no auto-retry | exit 2 `network or remote error: <repo>`; rerun is safe (idempotent) |
| `raw.githubusercontent.com/<owner>/<repo>/<sha>/<file>` (`.python-version`, `pyproject.toml`, `uv.lock` HEAD) | 01.1.1-T2 (manual) | `curl -fsSL --max-time 30` | Non-200 → read the file from the clone after bootstrap instead |
| `https://huggingface.co/api/models/<repo>/revision/main` ⚠️ U4 | 01.1.1-T2 | `curl -fsSL --max-time 30` | Field missing → use `git ls-remote https://huggingface.co/<repo> refs/heads/main` [INFERENCE: HF model repos are git repos] |
| `uv python install`, `uv sync` (PyPI, python-build-standalone) | bootstrap | uv defaults | exit 2; the lock is unchanged, so rerun |

No background jobs. Services run in the foreground (D9) and are out of scope here.

## 8. Security and privacy

| Threat | Control | Task |
|---|---|---|
| Secret in git | `.gitignore` (`config.toml`, `*.env`, `.env*`); gitleaks hook blocks staged secrets; working tree scanned before the first commit (E2); runtime secrets live only in Keychain + `$NTS_HOME/src/mpt/config.toml` | 01.1.2-T1, -T4 |
| Secret in shell history | Owner keys are entered only at the `security … -w` prompt (value not on the command line). Scripts read keys inside command substitution, never echo them, never use `set -x`. The runbook forbids `export OPENAI_API_KEY=…` lines. `HISTCONTROL`/zsh `HIST_IGNORE_SPACE` is not relied on | 01.1.2-T2, -T3 |
| Secret in argv (visible to `ps` for same-user processes and root) | Owner keys never enter argv. Local random keys: `init-local` feeds `add-generic-password` through `security -i` on stdin (⚠️ U6). The `-w "$val"` argv fallback is allowed only if `security -i` itself exits non-zero, and only after the TL records it (SEC-06). A verify mismatch is a hard failure, never a switch to the fallback; the compare is like-for-like in-process, so a trailing newline cannot cause a false mismatch. Consumers pass secrets through the child environment (D3), not argv | 01.1.2-T2 |
| Planted code reads the Keychain: scripts sit on the ownership-disabled USB volume, and the `-T /usr/bin/security` ACL lets any same-user process read every `nts.*` item without a prompt (F26, SEC-01, SEC-07) | D13: the owner enables ownership + encryption on the volume (verified in 01.1.1-T1), or the `export` fallback runs secret-reading code only from `$NTS_HOME/exec/<sha>` (`git archive` of a clean commit). `secret` refuses outside the trusted location. `check --acl` verifies the ACL; repair = delete + re-add (`-U` does not change an ACL). Residual same-user risk accepted on a single-user Mac; OpenAI spend capped by D7 | 01.1.1-T1, 01.1.2-T2, -T3 |
| Secret in logs / `run.json` | `secrets.sh check` prints names and status only; `env-versions.sh` holds no secrets, paths or identifiers; `log` never takes values | 01.1.1-T1, 01.1.2-T2 |
| Plaintext secrets on the unencrypted USB volume (E1: no encryption, Owners disabled → `chmod` ineffective) | [DECISION D1] `$NTS_HOME` on the internal FileVault disk. `nts_home` refuses (exit 2) a path under `/Volumes/` or inside the repo. `config.toml` is 0600 inside a 700 dir | 01.1.2-T1, -T2 |
| Secrets/weights in Time Machine backups (backup disk may be unencrypted) | `tmutil addexclusion "$NTS_HOME"` (sticky); verified with `tmutil isexcluded`. Keychain items are backed up by macOS as part of the login keychain (encrypted with the login password) — accepted. The repo on the USB volume is not in Time Machine scope [INFERENCE: external volumes are excluded by default ⚠️] | 01.1.2-T1 |
| USB volume lost/stolen | Holds only tracked, secret-free files. The gitleaks hook plus the pre-first-commit scan keep it that way. `trial/runs/*` keeps only metadata (media is ignored) | 01.1.2-T1, -T4 |
| Hook bypass (`git commit --no-verify`, rebase/cherry-pick, fresh clone without `core.hooksPath`) | `.githooks/pre-push` scans every pushed range and fails closed (SEC-12). Full-history scan at 01-ready, 01-closed and after any rebase/cherry-pick/`--no-verify`. `smoke.sh` asserts `core.hooksPath = .githooks`; the rehearsal checks it too. CI scan is out of scope (no CI in M0) | 01.1.2-T4, 01.1.1-T3 |
| Supply chain: moving upstream `main` | Detached checkout of full SHAs + `git rev-parse HEAD` check; `uv sync --frozen` from the upstream lock; model revisions pinned (downloads by 01.2.x use `revision=`) | 01.1.1-T2, 01.1.2-T3 |
| Rehearsal account leaks keys or plants code (SEC-01, SEC-11) | The account is created only after D13 holds and clones from a remote or a bundle of committed history, never running code from `/Volumes/Project`. It uses its own `nts-trial-*-rehearsal-<YYYYMM>` keys, typed by the owner at the prompt (no chat, screen-share or clipboard sync). Those keys are revoked, and the account deleted, at teardown | 01.1.1-T3 |

Retention: `$NTS_HOME/logs/` is kept until M0 close, then deleted with `$NTS_HOME` (§11). Nothing personal is stored.

## 9. Test plan

Documentation-level checks. Each task's Acceptance checks are the executable form.

| Behaviour / boundary | Check | Task |
|---|---|---|
| Tools present, ffmpeg encoders | `command -v uv gitleaks git ffmpeg`; `ffmpeg -hide_banner -encoders \| grep -E 'libmp3lame\|aac '` → 2 lines | 01.1.1-T1 |
| Manifest is valid JSON with all keys | `env-versions.sh \| uv run --no-project --python 3.12 python -m json.tool` → exit 0; key count = 16 | 01.1.1-T1 |
| Manifest degrades, never fails, on a missing clone | Before bootstrap: `mpt_commit` = `"n/a:not-cloned"`, exit 0 | 01.1.1-T1 |
| Pins well-formed | `grep -cE '^[A-Z_]+_(COMMIT\|REVISION)=[0-9a-f]{40}$' worker-node/pins.env` → 5 | 01.1.1-T2 |
| Ignore rules | `git check-ignore -v` on sample paths (list in 01.1.2-T1) → ignored / not ignored as tabled | 01.1.2-T1 |
| `$NTS_HOME` properties | `stat -f %Lp` → `700`; `tmutil isexcluded` → `[Excluded]`; `df` → internal disk | 01.1.2-T1 |
| `nts_home` guard | `NTS_HOME=/Volumes/Project/x` → exit 2 | 01.1.2-T2 |
| Secrets never printed | Output of `secrets.sh check` contains no string of 64 hex chars | 01.1.2-T2 |
| `init-local` idempotent | Second run: `unchanged` ×3, values' sha256 unchanged (compared inside a subshell, hashes only) | 01.1.2-T2 |
| Pin enforcement | Bootstrap with a wrong-but-valid SHA → exit 1/2, HEAD unchanged | 01.1.2-T3 |
| Bootstrap idempotent + strip re-applied | Second run exit 0; `resource/songs` has no audio after each run | 01.1.2-T3 |
| Hook blocks a secret, passes clean | Temp repo + generated fake AWS-style key → commit exit ≠ 0; clean file → commit exit 0 | 01.1.2-T4 |
| NFR-01-2 | Clean-account rehearsal ≤ 120 min excluding downloads | 01.1.1-T3 |

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Days | Depends on | Blocks |
|---|---|---|---|---|---|---|
| 01.1.1-T1 | Install tools + version manifest | Ops | XS | 0.25 | — | all |
| 01.1.1-T2 | Upstream requirements → `pins.env` | Ops | XS | 0.25 | 01.1.1-T1 | 01.1.2-T3, 01.2.1-T1, 01.2.2-T1, 01.2.3-T1 |
| 01.1.1-T3 | Runbook + clean-account rehearsal | Ops | S | 0.5 | all 01.1.x, 01.2.x, 01.3.x tasks; 05.5.2 | gate 01-closed |
| 01.1.2-T1 | Layout + `.gitignore` + `$NTS_HOME` | Ops | XS | 0.25 | 01.1.1-T1 | 01.1.2-T2…T4 |
| 01.1.2-T2 | `lib.sh` + `secrets.sh` | Ops | XS | 0.25 | 01.1.2-T1 | 01.1.2-T3, 01.2.x start scripts, 01.2.4-T2 |
| 01.1.2-T3 | `bootstrap.sh` | Ops | S | 0.5 | 01.1.1-T2, 01.1.2-T2 | 01.2.1-T1, 01.2.2-T1 |
| 01.1.2-T4 | gitleaks pre-commit hook | Ops | XS | 0.25 | 01.1.1-T1, 01.1.2-T1 | first commit |
| **Total** | | | | **2.25** | | |

Stories: 01.1.1 = 1.0 d (high-level S 0.5), 01.1.2 = 1.25 d (high-level XS 0.25). Feature: 0.75 → 2.25 d (+1.5 d), matching README §9.

Size notes (sizes kept as README §8/§9):
- **01.1.1-T3 (S) is tight.** The rehearsal alone may take up to 2 h plus downloads, and it needs a fix-and-retry loop if it fails. It fits 0.5 d only if each 01.2.x/01.3.x task appends its runbook section when it lands (DoD, below). Otherwise it is M. Risk flagged; no change requested.
- **01.1.2-T2 (XS)** holds two scripts. That is realistic only because `lib.sh` is ~5 short functions. Flagged, kept.

DoD addition proposed for every 01.2.x/01.3.x task: "runbook section added to `worker-node/README.md`" (see report).

## 11. Rollout

**Order** (day 1, after 01.2.4-T1 has started the external wait):
1. 01.1.1-T1 tools
2. 01.1.2-T1 layout, `.gitignore`, `$NTS_HOME`
3. 01.1.2-T4 hook + pre-first-commit scan → **first commit allowed only after this**
4. 01.1.1-T2 pins
5. 01.1.2-T2 lib + secrets (`init-local`; OpenAI keys are set later, in 01.2.4-T2)
6. 01.1.2-T3 bootstrap → hand-off to 01.2.1-T1 / 01.2.2-T1
7. 01.1.1-T3 runbook sections accumulate; the rehearsal runs last, after 05.5.2 (gate 01-closed).

No feature flag. Pin changes are rolled out by editing `pins.env` in its own commit, then running `bootstrap.sh` (the previous pin stays available through git history for rollback).

**Undo / rollback** (all user-level, no sudo):

| What | Command |
|---|---|
| Runtime tree (clones, venvs, weights, config, logs) | Stop all services, then `tmutil removeexclusion "$HOME/.nts"` and `rm -rf -- "$HOME/.nts"`. If `NTS_HOME` was overridden, confirm the path first with `printf '%s\n' "$NTS_HOME"` |
| Keychain items | For each of `nts.openai.story nts.openai.image nts.mpt.api_key nts.vieneu.api_key nts.proxy.api_key`: `security delete-generic-password -a "$USER" -s <service> >/dev/null`. Revoke OpenAI keys in the dashboard as well (01.2.4-T3) |
| Hook | `git config --unset core.hooksPath` |
| uv-managed Pythons / cache | `uv python uninstall 3.11 3.12` ⚠️ syntax; `uv cache clean` |
| Tools | `brew uninstall uv gitleaks` (keep git/ffmpeg: they were pre-installed ✅ E2) |
| Pin rollback | `git revert` the pins commit → `worker-node/bin/bootstrap.sh all` |
| Rehearsal account | System Settings → Users & Groups → delete the user, including the home folder |

Monitoring: N/A — local trial. Health belongs to `smoke.sh` (01.2.x).
