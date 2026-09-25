# 01 — Environment Setup (M0 Trial)

> Contracts: `00-overview.md`. Facts: `../DECISIONS.md`. Legend: ✅ verified · ⚠️ unverified · [DECISION] · [OPEN].
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-first, `00-overview.md` §4.1): all P (personal studio). Where this plan says M1 for an S item, read M2.
> **Reviewed 2026-09-23:** Tech Lead review, shared decisions D1–D12, detailed feature plans and one file per task are in `features/01-environment-setup/` (README §2 findings, §8 task mapping that replaces §6 below, §9 re-estimate 4.5 → 10.0 raw days pending TL acceptance).

## 1. Purpose and scope

**Goal:** a reproducible trial environment on the Mac M4 (16 GB) that runs the full pipeline once, and measures time, cost and memory, so every later plan works from real numbers.

**In scope (M0):** MPT install and config skeleton, VieNeu v3 Turbo (ONNX/CPU), VieNeu proxy placeholder (built in 04), ffmpeg, faster-whisper `large-v3-turbo`, OpenAI key handling, measurement sheet, a "carry-over list" for the GPU worker image (plan 11).

**Out of scope:** backend, Angular, GPU server, publishing, billing.

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| R1 | 16 GB RAM: VieNeu + whisper + MPT's MoviePy render can overlap during a run | Run one heavy step at a time; record peak RSS per step (01.3) |
| R2 | Secrets risk: MPT keeps keys in `config.toml` | `config.toml` is git-ignored ✅ (`.gitignore`); set an OpenAI project spending limit before the first call |
| R3 | Trial must mirror the future worker, or numbers are useless for plan 11 | Pin versions (git commit SHA of MPT and VieNeu, model IDs) in the sheet |
| R4 | Apple Silicon ≠ Linux GPU: speed numbers do not transfer | Record them as "Mac baseline"; plan 11 re-measures on GPU |
| A1 | Assumption: MPT and VieNeu each run in their own Python env (`uv`) | Avoids dependency conflicts |

## 3. Requirements

- **FR-01-1** One command per service starts MPT, VieNeu, and the proxy locally, bound to `127.0.0.1` only.
- **FR-01-2** MPT is configured with `llm_provider = "openai"`, `video_source = "openai_image"`, `subtitle_provider = "whisper"`, `[whisper] model_size = "large-v3-turbo"`, kokoro slot `base_url` → proxy.
- **FR-01-3** A measurement sheet records, per run: stage durations, tokens, image count, cost, RAM peak, failures.
- **FR-01-4** A carry-over list names every file/config/version reused by the worker image.
- **NFR-01-1** No secret in git, shell history or chat. Keys come from a local file or environment variable.
- **NFR-01-2** Setup reproducible from this document in ≤ 2 hours on a clean Mac.

## 4. Epics and user stories

### Epic 01.1 — Base tooling

**01.1.1 Install base tools** — *As the developer I want the base toolchain installed so that every service can run.* Must · S
- Given a clean Mac, When I follow the checklist, Then `git`, `uv`, `ffmpeg`, `python` (version required by MPT/VieNeu ⚠️ read from their READMEs at install time) are on PATH and their versions are written in the sheet.

**01.1.2 Project folders** — *As the developer I want a fixed layout so that paths in configs never change.* Must · XS
- Given the repo, When setup finishes, Then `worker-node/mpt/`, `worker-node/vieneu/`, `worker-node/proxy/`, `worker-node/music/`, `trial/runs/` exist and only config templates (no secrets, no media) are tracked by git.

### Epic 01.2 — Services

**01.2.1 VieNeu v3 Turbo on CPU** — *As the developer I want VieNeu running locally so that MPT can get Vietnamese and English voices.* Must · M
- Given VieNeu cloned at a pinned commit, When I run `uv sync` and start `apps/openai_speech.py` with `HOST=127.0.0.1`, `VIENEU_API_KEY=<local>`, Then `GET /health` returns 200 and `GET /v1/voices` lists presets ✅ (routes from DECISIONS §5.3).
- Given the server, When I request `response_format: "mp3"`, Then it returns 400 ✅ (confirms the proxy is needed).
- Given the server, When I call it from another machine on the LAN, Then the connection is refused.

**01.2.2 MPT installed** — *As the developer I want MPT running so that I can render the trial video.* Must · M
- Given MPT cloned at a pinned commit, When I install and start the WebUI/API, Then it listens on `127.0.0.1` (not the default `0.0.0.0` ✅) and `app.api_key` is set.
- Given bundled music, When setup finishes, Then the bundled music folder is emptied (DECISIONS §5.5) and a CC0 placeholder track is present.

**01.2.3 faster-whisper model** — *As the developer I want the subtitle model cached so that the first render does not stall.* Must · S
- Given MPT config `subtitle_provider = "whisper"` ✅, When the first subtitle job runs, Then `large-v3-turbo` is downloaded once to a known cache dir and reused.

**01.2.4 OpenAI access** — *As the owner I want API access with a hard budget so that a bug cannot overspend.* Must · S
- Given an OpenAI project for the trial, When I set a monthly budget/limit ⚠️ (dashboard feature name to confirm), Then the key is stored only in local `config.toml`/env.
- Given MPT config, When a render runs, Then story uses `openai_model_name = "gpt-6-luna"` and images use `openai_image_model = "gpt-image-2.5-flare"`, `openai_image_size = ""` ✅.

### Epic 01.3 — Measurement

**01.3.1 Measurement sheet** — *As the Tech Lead I want the same numbers for every trial run so that pricing and capacity use data, not guesses.* Must · S
- Given `trial/measurements.csv`, When a run finishes, Then one row holds all columns in §5.3 and nothing is blank (use `n/a` with reason).

**01.3.2 Memory profiling** — *As the developer I want peak memory per step so that I know what can run together.* Should · S
- Given a run, When each stage runs, Then peak RSS of VieNeu, whisper and MPT render is recorded (Activity Monitor or `/usr/bin/time -l`).

**01.3.3 Carry-over list** — *As the Tech Lead I want a list of what moves to the GPU worker so that plan 11 is not rediscovery.* Must · XS
- Given the trial passed, When I close M0, Then `worker-node/CARRY-OVER.md` lists pinned commits, config templates, voice list, music catalog, proxy code, known differences Mac vs Linux GPU.

## 5. Technical design

### 5.1 Local ports [DECISION]

| Service | Bind | Port |
|---|---|---|
| VieNeu `openai_speech.py` | 127.0.0.1 | 8000 (VieNeu default) |
| VieNeu proxy (plan 04) | 127.0.0.1 | 8001 |
| MPT API/WebUI | 127.0.0.1 | 8080 (MPT default `listen_port` ✅) |

### 5.2 MPT `config.toml` skeleton (keys verified in DECISIONS ✅)

```toml
[app]
listen_host = "127.0.0.1"
api_key = "<local random>"
video_source = "openai_image"
llm_provider = "openai"
openai_api_key = "<from local secret>"
openai_model_name = "gpt-6-luna"
openai_image_base_url = "https://api.openai.com/v1"
openai_image_api_keys = ["<from local secret>"]
openai_image_model = "gpt-image-2.5-flare"
openai_image_size = ""
openai_image_prompt_template = "<style template from plan 03> {term}"
subtitle_provider = "whisper"
max_concurrent_tasks = 1

[whisper]
model_size = "large-v3-turbo"

[kokoro]
base_url = "http://127.0.0.1:8001/v1"
api_key = "<proxy key>"
```

⚠️ Section placement of each key (`[app]` vs top level) must be copied from the pinned `config.example.toml`, not from this sketch.

### 5.3 Measurement sheet columns

`run_id, date, mpt_commit, vieneu_commit, language, topic, target_minutes, story_model, story_input_tokens, story_output_tokens, story_cost_usd, image_model, image_quality, image_size, image_count, image_output_tokens_total, image_cost_usd, moderation_blocked_count, tts_seconds_audio, tts_wall_seconds, whisper_wall_seconds, render_wall_seconds, total_wall_seconds, ram_peak_vieneu_mb, ram_peak_whisper_mb, ram_peak_render_mb, output_duration_sec, output_size_mb, words_in_story, words_per_minute_measured, issues`

`words_per_minute_measured` feeds story length targets in plan 02.

## 6. Task breakdown

> Superseded: the task list, sizes and dependencies are now the 29 task files in `features/01-environment-setup/` (README §8 mapping, §10 dependency graph). The table below is the original high-level breakdown, kept for history.

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 01.1.1-T1 | Install git, uv, ffmpeg; record versions | Ops | XS | — |
| 01.1.1-T2 | Read MPT and VieNeu READMEs; record required Python versions | Ops | XS | — |
| 01.1.2-T1 | Create `worker-node/*` and `trial/` layout; update `.gitignore` for media | Ops | XS | — |
| 01.2.1-T1 | Clone VieNeu at pinned commit; `uv sync` | Worker | S | 01.1.1 |
| 01.2.1-T2 | Start script with `HOST=127.0.0.1`, `VIENEU_API_KEY`, `VIENEU_WATERMARK=1` | Worker | XS | 01.2.1-T1 |
| 01.2.1-T3 | Smoke: health, voices, mp3→400, wav OK, LAN refused | Worker | XS | 01.2.1-T2 |
| 01.2.2-T1 | Clone MPT at pinned commit; install | Worker | S | 01.1.1 |
| 01.2.2-T2 | Write `config.toml` from template §5.2; empty bundled music | Worker | S | 01.2.2-T1 |
| 01.2.2-T3 | Smoke: API rejects requests without `x-api-key` | Worker | XS | 01.2.2-T2 |
| 01.2.3-T1 | Pre-download whisper model; record cache path | Worker | XS | 01.2.2 |
| 01.2.4-T1 | Create OpenAI project + budget limit; store key locally | Ops | XS | — |
| 01.3.1-T1 | Create `trial/measurements.csv` with §5.3 header | BA | XS | — |
| 01.3.2-T1 | Measure RAM per stage during trial run | Worker | S | 05 trial |
| 01.3.3-T1 | Write `worker-node/CARRY-OVER.md` | BA | XS | all M0 |

## 7. Test plan

- Smoke checks in 01.2.x acceptance criteria (health, auth, bind address).
- Negative: MPT API without key → 401/403; VieNeu mp3 → 400; LAN access refused.
- Manual: one full trial run (plan 05) produces a complete sheet row.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Python dependency conflict between MPT and VieNeu | Separate `uv` environments |
| Model downloads fail/slow | Pre-download, record cache paths |
| Accidental key commit | `.gitignore` + pre-commit secret scan (e.g. gitleaks ⚠️ license check) |
| Mac OOM during render | `max_concurrent_tasks = 1`; stop VieNeu before render if needed |

## 9. Exit criteria (M0)

- All services start from the documented commands; smoke checks pass.
- At least two sheet rows (one `vi`, one `en`) are complete.
- `CARRY-OVER.md` written.

## 10. Open questions and contract change requests

- ✅ Python versions and install commands: MPT 3.11 `uv sync --frozen`, VieNeu 3.12 `uv sync` (feature README §1 M7, V3, V4); re-check at the pinned commits.
- ✅ OpenAI budget control: a project/org **hard spend limit** exists and returns 429 (feature README §1 O1); amounts decided by the owner (D7, DEC-3).
- Contract change requests CR-1…CR-10: see `features/01-environment-setup/README.md` §4 (applied to 00, 03, 04, 05, 11 and DECISIONS on 2026-09-24).

## 11. Estimate summary

| Epic | Days |
|---|---|
| 01.1 Base tooling | 2.25 |
| 01.2 Services | 4.75 |
| 01.3 Measurement | 3.0 |
| **Total** | **10.0** → with 20 % buffer **12.0** person-days |

Re-estimated from the 29 task files of the detailed feature plans (`features/01-environment-setup/` §9, previously 4.5 / 5.4); accepted by the owner 2026-09-24 (DEC-1). The §6 task table above is superseded by the feature task files (mapping in the feature README §8).
