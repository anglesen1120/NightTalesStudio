# 05 — Video Assembly, Subtitles, Music and the Worker Contract (M0)

> Contracts: `00-overview.md` §8 (backend → MPT). Facts: `../DECISIONS.md` §5.4, §5.5, §10.2. This plan owns **MPT render configuration, subtitle and music quality, and the written MPT API contract** used by 09 (`IRenderClient`) and 11 (`mock-mpt`).
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-only, `00-overview.md` §4.1, DEC 10.14): all P; 05.4 is Gate 1.

## 1. Purpose and scope

**Goal:** MPT turns an approved script + keywords + voice into a watchable MP4 with accurate Vietnamese/English subtitles and licensed music, and we know MPT's API precisely enough to automate it.

**In scope:** MPT render config; faster-whisper subtitles; fonts and subtitle style; music catalog (CC0/YouTube Audio Library) and removal of bundled music; render quality checks; worker contract spike; the M0 trial run itself.

**Out of scope:** MPT source changes (configure only ✅ DECISIONS §3); publishing; `app/services/upload_post.py` (never used ✅).

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| F1 | MPT API has one shared key, lists all tasks, binds `0.0.0.0` by default ✅ (DECISIONS §10.2) | Worker is private; only the backend calls it |
| F2 | Per-job fields exist in `VideoParams` (`video_script`, `video_terms`, `video_source`, `video_aspect`, `voice_name`, `bgm_*`, subtitle options) ✅ `schema.py:106-160` | Backend sends everything per job; worker config keeps only secrets and defaults |
| F3 | Exact task-query/download routes and progress fields ✅ confirmed by C6, C7 and C12 | Spike 05.4 re-confirms them at the pinned commit; its output defines the `mpt-adapter` contract (05 CR-1) |
| F4 | Bundled music came from YouTube videos ("如有侵权，请删除") ✅ | Delete; own catalog with license records |
| F5 | kokoro slot returns no word timestamps ✅ → subtitles need whisper | `subtitle_provider = "whisper"` (default is `edge` ✅) |
| F6 | Default font `STHeitiMedium.ttc` (`schema.py:150`) ✅ is a Chinese font; Vietnamese diacritics render badly — 92 of 178 letters missing (X1) (05 CR-1) | Ship a Vietnamese-capable font on the worker and send `font_name` per job. First candidate (01 CR-6): MPT already ships `BeVietnamPro-Bold.ttf` / `BeVietnamPro-Medium.ttf` in `resource/fonts` ✅ (01 README §1 M6); licence ✅ OFL 1.1 (L1) |
| F7 | Whisper transcribing our own TTS may misspell names/terms | **Closed by F01**: in sentence mode `subtitle.correct()` replaces every cue's text with the script chunk, so the subtitle text equals the script and whisper supplies timing only (05 CR-1) |
| F8 | MPT does not pass `language` to faster-whisper (auto-detect) ✅ (01 README §1 M3, `subtitle.py`) → a Vietnamese clip can be misdetected (01 CR-5) | 05.2.1 records `detected language` + probability per video (01.2.3-T2 gives the Mac baseline); its effect on sync is measured in 05.2.1-T1 and `[whisper] initial_prompt` stays unset (F16); a misdetected video fails 05.5.1 (05 CR-1) |

## 3. Requirements

- **FR-05-1** Worker config: `app.api_key` set, private `listen_host`, `video_source = "openai_image"`, `subtitle_provider = "whisper"`, `[whisper] model_size = "large-v3-turbo"`, kokoro slot → proxy (04): `[kokoro] model_id = "vieneu-v3-turbo"` (04 CR-13), `max_concurrent_tasks` sized to the node (11). Image block (03 D4, 03 CR-5): `openai_image_base_url` = the loopback image relay in `mpt-adapter`, `openai_image_api_keys = []` (the relay holds the key), `openai_image_prompt_template = ""`, `openai_image_size = ""`, `twelvelabs_rerank_terms = false`, `material_directory = ""`; guards reject other values. Node profile per README D2: `max_queued_tasks = 0`, MPT binds loopback only, and the node holds no LLM key (05 CR-1).
- **FR-05-2** Bundled music removed; catalog tracks each have: title, artist, source URL, license (CC0 / YouTube Audio Library terms), attribution text if needed, mood tags, duration, loudness.
- **FR-05-3** Subtitles per README D6: Be Vietnam Pro Bold, size 84 (landscape) / 64 (portrait), stroke 3, portrait position `custom` at 62 %; long cues are an advisory hint only (O5) (05 CR-1).
- **FR-05-4** Render quality checks run on every output — README D9 (normalise loudness, then measure, on the node) with the D10 `render-quality.v1` report — and results are stored on the render job (05 CR-1).
- **FR-05-5** A written worker contract (§5.3) is the single source for 09 and 11.
- **NFR-05-1** A 10-minute landscape video renders in ≤ 20 min on the GPU worker [target], measured on the GPU node by plan 11; the Mac per-minute split is measured in 05.5.2-T1 (D15 P7) (05 CR-1).
- **NFR-05-2** Output: H.264 MP4, AAC 192 kbit/s, landscape 1920×1080 / portrait 1080×1920 ✅ C14 (05 CR-1).

## 4. Epics and user stories

### Epic 05.1 — Render configuration

**05.1.1 Worker config template** — *As the operator I want one reviewed config template so that every worker renders the same way.* Must · XS
- Given `worker-node/mpt/config.template.toml` and `render-mpt-config.py` (written and guarded in 01.2.2-T2, 01 CR-7), Then all keys from FR-05-1 are set, secrets are placeholders filled at render time, and an upgrade diff script compares the pinned `config.example.toml` against `worker-node/mpt/config.example.pinned.toml` (01.2.2-T1) on every MPT upgrade. Worker-only values (`max_concurrent_tasks`, whisper `cuda`/`float16`, codec) are set per node (11, 01 CR-9).

**05.1.2 Per-job parameter mapping** — *As the backend I want a documented mapping from our domain to `VideoParams` so that jobs are reproducible.* Must · S
- Given an episode + series, Then the mapping table in §5.2 is complete, and `render_jobs.params` stores the exact JSON sent.

### Epic 05.2 — Subtitles and fonts

**05.2.1 Whisper subtitles** — *As a viewer I want accurate subtitles so that I can follow without sound.* Must · M
- Given 3 vi and 3 en trial videos, When subtitles are compared with the script (F01: cue text = script), Then cue-text fidelity is 100 % and ≥ 95 % of sampled cues are on time (sync); failures are listed with examples (05 CR-1).

**05.2.2 Vietnamese-capable font** — *As a Vietnamese viewer I want correct diacritics so that the channel looks professional.* Must · S
- Given a font with full Vietnamese coverage and a licence that allows embedding in videos (Be Vietnam Pro Bold: OFL 1.1 ✅ L1), When installed in MPT's font folder and sent as `font_name`, Then a test string "Người đàn bà áo trắng đứng dưới gốc đa" renders with 0 missing glyphs (X1) (05 CR-1).

**05.2.3 Subtitle style presets** — *As a creator I want subtitles that fit my aspect so that Shorts UI does not cover them.* Should · S
- Given landscape and portrait, Then presets define `font_size`, `stroke_width`, `stroke_color`, `subtitle_position`/`custom_position` (`schema.py:139-157` ✅) and are applied by the backend per aspect.

### Epic 05.3 — Music

**05.3.1 Remove bundled music** — *As the platform I want no unlicensed audio so that users never get copyright strikes from us.* Must · XS
- Given the MPT checkout, When the worker image is built, Then MPT's bundled song folder is empty and a build check fails if audio files appear there. The check is `worker-node/bin/strip-bundled-music.sh --check` from 01.2.2-T3 (01 CR-7; no forked logic).

**05.3.2 Music catalog** — *As a creator I want fitting background music so that videos feel atmospheric.* Must · M
- Given 20–30 tracks (dark ambient, suspense, temple/meditation, noir), Then sources follow O2 (YouTube Audio Library first, CC0 second, CC-BY with credit), each has a license record (FR-05-2); files live in object storage and are synced to workers; the backend sends `bgm_type = "custom"` plus a per-track gain (`bgm_file`/`bgm_volume`, default 0.2 ✅ `schema.py:133`) (05 CR-1).
- ⚠️ YouTube Audio Library download terms for use outside YouTube (e.g. TikTok) must be checked; if unclear, use CC0 only for TikTok-bound videos.

### Epic 05.4 — Worker contract spike (hard gate before 09.6.2-T1 and 11.1.2-T1)

**05.4.1 Confirm MPT API and define the `mpt-adapter` contract** — *As the Tech Lead I want MPT's API behaviour written down so that the `mpt-adapter`, the backend and mock-mpt match reality.* Must · L (2–3 d)
Answer, with request/response samples saved under `worker-node/contract/`:
1. Base path `/api/v1` and auth header behaviour `x-api-key` ✅ (C1, C2).
2. `POST /videos` request/response (task id field name `data.task_id`) ✅ (C1, C3).
3. Task query route and response ✅ (C6): state values, progress field, output file paths, error text.
4. Download/stream route ✅ (C12): how the file path is referenced, content type, range support.
5. Delete task ✅ (C11): 409 while the task runs, no cancel route; what it removes (files? record?).
6. Behaviour on: TTS failure, image failure/moderation (answered by 03.4.1-T1 with an image stub: `worker-node/contract/images.md`; MPT skips a failed image and the task still succeeds, 03 CR-5), whisper failure, worker restart mid-task ✅ (C8, C9): stages `preflight|script|terms|audio|materials|video|pipeline`, and a restart loses the task.
7. Where outputs are written and when they are cleaned up ✅ (C13): MPT never deletes a task dir on its own.
8. Does `max_concurrent_tasks` queue or reject extra tasks? ✅ (C10): it queues invisibly in memory up to `max_queued_tasks`, then 429.
9. Redis vs memory task state (`enable_redis = false` ✅): state is lost on restart ✅ (C9).
- Given C1–C13, Then every item above is already ✅ (05 CR-1); 05.4.1-T1 re-confirms each one at the pinned commit and 11's mock-mpt implements the same shapes.
- Given the answers, Then the stable `mpt-adapter` contract (submit, status = state + percent, output download by task id, health + active task count, cleanup; 11.3.5) is written down with samples [DECISION]; the backend talks only to the adapter, never to MPT directly.
- Given MPT's progress data, Then the spike records which `RenderProgress.stage` values are real: `queued|running|ingesting` are guaranteed, and the finer `voice|images|subtitles|assembling` stages are derived from MPT's progress values ✅ (F09) (05 CR-1).
- Given the spike is not finished, Then 09.6.2-T1 (MptRenderClient) and 11.1.2-T1 (mock-mpt) do not start (hard gate) [DECISION].

### Epic 05.5 — Quality checks and trial

**05.5.1 Automated render checks** — *As the platform I want broken videos caught before review so that users do not waste time.* Must · M
- Given an output file, When `ffprobe` runs, Then checks: video and audio streams present; duration within ±5 % of TTS audio duration — the streamed MP3 is ≈ 30 ms longer than the source PCM (04 X1), well inside this tolerance (04 CR-13); resolution matches aspect; integrated loudness in target range (−16 to −14 LUFS [target] via `ffmpeg loudnorm` analysis); subtitles present if enabled. Checks follow README D9 (loudness normalised on the node, then measured) and write the D10 `render-quality.v1` report; failures mark the render `failed` with the outcome `render_quality_failed` (a job outcome, never HTTP 409) and details (05 CR-1).

**05.5.2 M0 trial run** — *As the owner I want one complete video per language so that we know the pipeline works.* Must · M
- Given plans 01–04, When two 5-minute landscape stories are rendered — L1 = vi, L2 = en if 04 O2 launches the en channel, otherwise a second vi story (T14) — Then both pass 05.5.1, a native speaker rates the vi voice + subtitles ≥ 4/5, and the measurement sheet rows are complete. The en voice rating follows 04 O2 (accepted 2026-09-24): after 04.2.1-T1, en launches with the best-rated voice if its mean naturalness ≥ 3.0, otherwise the en channel is postponed and L2 is a second vi story; en subtitles are still rated ≥ 4/5 when en launches (04 CR-13) (05 CR-1).

## 5. Technical design

### 5.1 Worker layout

Tracked in `worker-node/mpt/`: `config.template.toml`, `config.example.pinned.toml` (01.2.2), `contract/` (spike samples), `../checks/render_check.py` = `worker-node/checks/render_check.py` (ffprobe/loudnorm, D9) (05 CR-1). Not tracked: `fonts/` and `music/` content (synced from storage; audio is git-ignored, licence records in `worker-node/music/LICENSES.csv`). The pinned MPT checkout itself lives outside the repo (`$NTS_HOME/src/mpt` on the Mac, 01 D1; node path in 11).

### 5.2 Domain → `VideoParams` mapping (✅ field names from `schema.py:106-160`; submit fields per README D3) (05 CR-1)

| VideoParams field | Source |
|---|---|
| `video_subject` | story title |
| `video_script` | approved story body, read through `IStoryRenderInputProvider.GetAsync(approved_story_version_id)` (02 CR-18) |
| `video_terms` | JSON **array** (never a string: MPT splits strings on commas) of scene prompts: keywords from `StoryRenderInput.Segments` (02 CR-18, 03 CR-13), capped by `RenderImagePlanner` and styled by `ScenePromptBuilder` (03 D1/D3/D4, 03 CR-5) |
| `video_aspect` | series aspect: `"16:9"` \| `"9:16"` (D3, C14) (05 CR-1) |
| `video_source` | `"openai_image"` |
| `video_clip_duration` | `60 ÷ K` = **10** s (03 D3); ✅ MPT stops generating once `images × clip ≥ audio` (03 README I7) (03 CR-5) |
| `video_concat_mode` | `"sequential"` (default `random` shuffles scenes, 03 README I10) (03 CR-5) |
| `match_materials_to_script` | `true` (forces sequential, skips any term rerank) (03 CR-5) |
| `video_count` | `1` |
| adapter-only `image_plan` | `{ max_images, prompt_sha256[] }` for the image relay; stripped by the adapter before calling MPT (03 D4/D5, 03 CR-5) |
| `voice_name` | `"kokoro:<voices.id>"` |
| `voice_rate` | `1.0` (ignored by VieNeu ✅) |
| `bgm_type`, `bgm_file`, `bgm_volume` | music catalog choice |
| `subtitle_enabled`, `subtitle_position`, `font_name`, `font_size`, `stroke_color`, `stroke_width`, `text_fore_color` | style preset (05.2.3) |
| `subtitle_display_mode` | `"sentence"` (F01) (05 CR-1) |
| `font_name` | `"BeVietnamPro-Bold.ttf"` (D7) (05 CR-1) |
| `bgm_type` | `"custom"` with a `bgm_file` and per-track gain — or `""` for no music (O2/D8) (05 CR-1) |
| `n_threads` | **not sent by the backend**; the adapter injects it from node config (D3, F22) (05 CR-1) |
| `video_language` | series language |
| `paragraph_number`, `video_script_prompt` | unused (script is provided) |

### 5.3 Worker contract: `render-node.v1` (README D4; MPT's own API wrapped by `mpt-adapter`, 11.3.5) (05 CR-1)

| Operation | Route | Status |
|---|---|---|
| Create render | `POST /api/v1/videos` body `VideoParams`, header `x-api-key` | ✅ (C1/C3): prefix `/api/v1`, response `data.task_id` (05 CR-1) |
| Query task | `GET /api/v1/tasks/{task_id}` | ✅ (C6): `state`, `progress`, `failed_stage`, `error`, output paths (05 CR-1) |
| List tasks | `GET /api/v1/tasks` | ✅ exists, never exposed (C1) (05 CR-1) |
| Delete task | `DELETE /api/v1/tasks/{task_id}` | ✅ (C1/C11): 409 while running, no cancel route (05 CR-1) |
| Download output | `GET /api/v1/download/{file_path}` | ✅ (C1/C12): `realpath` + `commonpath` guard, `video/<ext>` (05 CR-1) |

Terminal adapter statuses also carry the `images.v1` report of the image relay (03 D6); the backend maps it with the 03 D7 table (03 CR-5). Backend states mapping: MPT pending → `submitted`; MPT processing → `running` (progress %); MPT complete → `ingesting`; MPT failed → `failed` with mapped code. The table above is MPT's internal API as seen by `mpt-adapter`; the backend and mock-mpt use only the adapter contract `render-node.v1` (README D4), served over HTTPS with a pinned node certificate (D15 S1) (05 CR-1). Guaranteed `stage` values: `queued|running|ingesting`; the finer `voice|images|subtitles|assembling` stages are derived from MPT's progress values (F09) (05 CR-1).

## 6. Task breakdown

> The rows below are replaced by `features/05-video-assembly-subtitles/README.md` §8 (15 tasks, 10.75 d) (05 CR-1). The three new task ids in that index are listed here for continuity.

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 05.1.1-T1 | Upgrade diff script (template already written in 01.2.2-T2, 01 CR-7) | Worker | XS | 01.2.2 |
| 05.1.1-T2 | Node config profile + guards (D2) (05 CR-1) | Worker | S | 01.2.2-T2, 03.4.1-T1, 05.4.1-T2, 05.2.2-T1 |
| 05.1.2-T1 | Mapping table + `render_jobs.params` spec | BE | S | 05.4.1 |
| 05.2.1-T1 | Whisper accuracy test (6 videos) | BA | M | 05.5.2 |
| 05.2.2-T1 | Pick + license-check Vietnamese font; install; glyph test | Worker | S | 01.2.2 |
| 05.2.3-T1 | Landscape/portrait subtitle presets | Worker | S | 05.2.2 |
| 05.3.1-T1 | Wrap `strip-bundled-music.sh --check` (01.2.2-T3) as the image build check | Worker | XS | 01.2.2 |
| 05.3.2-T1 | Curate 20–30 tracks + license records | BA | M | — |
| 05.3.2-T2 | Storage sync to workers | Worker | S | 11 storage |
| 05.3.2-T3 | `music_tracks` table, seeder, `MusicSelector` (owned by 05; pointer in 09.2.2) (05 CR-1) | BE | S | 05.3.2-T1, 09 wave 1 |
| 05.4.1-T1 | Spike: call every route, capture samples, failure scenarios, real `stage` values; hard gate for 09.6.2-T1 and 11.1.2-T1 | Worker | L | 01, 04 |
| 05.4.1-T2 | Update §5.3, write `mpt-adapter` contract (11.3.5) + hand samples to 09/11 | BA | XS | 05.4.1-T1 |
| 05.5.1-T1 | Render check script + backend job integration spec | Worker | M | 05.4.1 |
| 05.5.1-T2 | `RenderQualityPolicy`/`Evaluator`, `render-quality.v1` (05 CR-1) | BE | S | 05.5.1-T1, 05.4.1-T2 |
| 05.5.2-T1 | M0 trial vi + en; measurements | BA | M | 01–04 |

## 7. Test plan

- Contract samples replayed by mock-mpt tests (11) and `MptRenderClient` tests (09).
- Render check script tested on: good file, silent audio, wrong resolution, truncated file.
- Manual native-speaker review of subtitles and voice (vi + en).

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| MPT task state lost on restart (memory manager) | F06 decision (D4): `enable_redis = false`; the adapter keeps its own durable task store and maps an MPT 404 for a task it submitted to the outcome `render_lost` (transient, auto-retried); no Redis (05 CR-1) |
| Subtitle timing drift (F7 closed by F01: the cue text comes from the script, so the residual risk is sync, not misspelling) | Script-vs-subtitle diff in quality checks; 05.2.1-T1 measures sync on 6 videos; allow manual subtitle edit later (Could) (05 CR-1) |
| Music license ambiguity | CC0-first catalog; record every source |
| MPT upgrade breaks contract | Pin commit; replay contract samples before upgrade; `mpt-adapter` absorbs MPT changes so the backend contract stays stable |
| Spike overruns (L, 2–3 d) and blocks 09.6.2 / 11.1.2 | Start 05.4.1 first in M0; hard gate is explicit in both plans' task dependencies [DECISION] |

## 9. Exit criteria (M0)

- Worker contract confirmed (§5.3 all ✅); samples committed; `mpt-adapter` contract and real `stage` values documented.
- Two trial videos pass automated checks and native review; measurements complete. L2 is en if 04 O2 launches the en channel, otherwise a second vi story (T14); the en video is required only when the en channel launches (04 CR-13) (05 CR-1).
- Music catalog v1 with license records; bundled music removed.

## 10. Open questions and contract change requests

- ✅ Can MPT align subtitles to provided text rather than transcribe? Yes (C15): in sentence mode `correct()` replaces every cue with the script chunk, so F7 is closed by F01 (05 CR-1).
- **From plan 01 (applied 2026-09-24):** CR-5 (F8, whisper language), CR-6 (BeVietnamPro candidate, F6), CR-7 (05.1.1 and 05.3.1 reuse the 01 template, renderer and strip script; 05.1 re-estimated 1.0 → 0.75 d; superseded by 05 CR-1, which sets 05.1 = 1.5 d). The MPT checkout lives outside the repo (`$NTS_HOME/src/mpt` on the Mac, 01 D1).
- **Contract changes (applied to 00):** add `render_jobs.quality_report jsonb` to 00 §6; add `music_tracks` catalog (title, artist, source_url, license, attribution, moods, storage_key, active).
- **Hand-over from plan 03 (2026-09-24, read before detailing this plan).** Plan 03 is owner-signed and its task files already name the task ids below. Keep those ids, or update the `Depends on` / `Blocks` cells in plan 03 when they change. Contract: `features/03-image-generation/README.md` D1–D14.
  - [x] 05.4.1 (contract spike) (05 CR-1; README D4): take in the image part of the contract from 03.4.1-T1 (`worker-node/contract/images.md`, cases P0–P12). The adapter contract carries the D4 submit fields: `video_terms` as a JSON array, `video_clip_duration = 10`, `video_concat_mode = sequential`, `match_materials_to_script = true`, `video_count = 1`, and the adapter-only `image_plan { max_images, prompt_sha256[] }`. It also carries the `images.v1` status block (D6) and the cleanup rule of D12 (item 7). 09.6 re-estimates after this gate.
  - [x] 05.5.2 (trial renders) (05 CR-1; README T14): 03.3.1-T1 measures image tokens on two landscape trial renders, using the 01 usage tap and MPT's global template (D13). Each trial row records the skipped images (the 03-M0 exit criterion). 03.3.1-T2 then writes the measured numbers into DECISIONS §4 and the `price_versions.rates.image` seed.
  - [x] 05.5.1 (quality report) (05 CR-1; README D10): `render_jobs.quality_report.images` is owned by 03.4.2-T1 (D7 block). 05.5.1 owns every other key and must not write `images`.

## 11. Estimate summary

| Epic | Days |
|---|---|
| 05.1 Render configuration | 1.5 (05 CR-1) |
| 05.2 Subtitles + fonts | 2.0 (05 CR-1) |
| 05.3 Music | 2.25 (05 CR-1) |
| 05.4 Worker contract spike | 2.5 (05 CR-1) |
| 05.5 Quality + trial | 2.5 (05 CR-1) |
| **Total** | **10.75** → with 20 % buffer **12.9** person-days (05 CR-1) |

Re-estimate per README §9: 8.75 → 10.75 raw (+2.0 d) (05 CR-1).
