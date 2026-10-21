# `worker-node/contract/mpt-api.md` — MPT API observations at the pin (spike 05.4.1-T1)

MPT commit: `3d5f4e421927d61f3eac729cf4b711ac0b69d688` (`worker-node/pins.env` `MPT_COMMIT`; 01 D1),
`pyproject.toml` `version = "1.3.7"`. Date: 2026-09-26. Machine: owner Mac (macOS 27, arm64).
Consumers: **05.4.1-T2** (freezes `render-node.v1`, D4), then 09.6.2-T1 (`MptRenderClient`),
11.1.2-T1 (mock-mpt), 11.3.5-T1 (adapter) — this task is their hard gate.

Legend: ✅ measured/verified by execution at the pin · ⚠️ unverified / partial · [INFERENCE] · [OPEN].

Method: the pinned MPT runs on **127.0.0.1:18080** (the `spike` profile, rendered by
`render-mpt-config.py --profile spike`, started by `start-mpt-api.sh --profile spike`) **beside** the
live `mac`-profile instance on 127.0.0.1:8080, which was never stopped. Two loopback fakes replace every
paid upstream: `trial/tools/image_stub.py` on `127.0.0.1:18003` (fake `images/generations`, 03.4.1-T1)
and `trial/tools/tts_stub.py` on `127.0.0.1:18004` (fake `/audio/speech`, this task). The `spike` profile
points `[kokoro] base_url` at the TTS stub with `api_key = ""` and points the OpenAI base URLs at the
image stub, so **no request left this machine and no OpenAI call was billed** (03 D14).

The reference client is `trial/tools/mpt_client.py` (D13). Every `S<n>-<slug>.json` is a task-API /
observed record redacted by `trial/tools/redact_prompts.py` (`script`, `terms`, `video_script`,
`video_terms`, `video_subject`, `search_term`/`search_terms` → `<text sha256=…16-hex…>`; absolute paths
→ `<task_dir>/…`), every `S<n>-<slug>.txt` a redacted MPT log slice. `.json` (a JSON array) and `.txt`
log excerpts are the sample convention from D12, so the stub call logs are committed as `.json` arrays,
not `.jsonl`; the two `.srt` files — `S7-whisper-fail-subtitle.srt` and `S8-sine-subtitle.srt` — are
the named exception, the zero-cue subtitle evidence for S7/S8. Secrets are read only from the Keychain through the D5 helper and never printed:

```bash
source worker-node/bin/lib.sh && NTS_MPT_API_KEY="$(secret nts.mpt.api_key)"
```

`worker-node/bin/secret` is not a file — `secret()` is the bash function `worker-node/bin/lib.sh`
defines for `security find-generic-password`; the value lives only in that command's environment, never
on argv, never in a sample. Stub dispatch (CR-E): the TTS stub matches its script's
`by_input_contains` map against the TTS `input`, the image stub its `by_prompt_contains` map against
the image prompt — each stub's script file uses its own key.

### S0 — pin re-check: every README §1 `C` fact at `MPT_COMMIT`

`git -C $NTS_HOME/src/mpt rev-parse HEAD` = `3d5f4e421927d61f3eac729cf4b711ac0b69d688` = `MPT_COMMIT` ✅.
All 22 facts re-read against the pinned tree. **Verdict: all `same` — no `C` fact is wrong at the pin**,
so no README §1 row needs editing; the only drift is minor line-range movement, recorded inside the
evidence column (app/models/schema.py C3/C5 off by ~3–7 lines, app/services/video.py C14 codec set ends at L100, inside cited
L86–99). Per-fact evidence (file + line + what was re-read) is in `samples/mpt/S0-pin-verdicts.json` —
the table below records the pin's authoritative line for each fact.

| # | Verdict | Pin evidence (`file:line`, path relative to the MPT root `$NTS_HOME/src/mpt`; every path carries its `app/…` prefix) |
|---|---|---|
| C1 | same | `app/controllers/v1/base.py` L7 prefix `/api/v1`; `app/controllers/v1/video.py` L41 router deps `verify_token`, L181 POST `/videos`, L240 GET `/tasks`, L258 GET `/tasks/{id}`, L290 DELETE `/tasks/{id}`, L457 GET `/stream/…`, L490 GET `/download/…`, L188 POST `/subtitle`, L195 POST `/audio`, L325/345 `/musics`, L388/419 `/video_materials`; `app/controllers/v1/llm.py` L21-55 (3 routes); `app/controllers/ping.py` L11-12 `pong`; `app/asgi.py` L285/288 mounts `/tasks`+`/`, L242 FastAPI (docs on) |
| C2 | same | `app/controllers/base.py` L58-60 empty `api_key`→None (auth off), L89-90 `secrets.compare_digest` on utf-8, L80-82 401 `invalid API key`; `app/asgi.py` L258-273 middleware guards `/tasks`; startup warning L27-30 |
| C3 | same | `app/controllers/v1/video.py` L202-237 `create_task` (state-4 write L214, `delete_task` rollback L223-224, 429 map L233); `app/controllers/base.py` L13-26 `normalize_task_id` (x-task-id ≤128 printable); `app/models/schema.py` L285-292 `TaskStatusData`, L357 `TaskResponse` (README L278-287 off ~7) |
| C4 | same | `app/controllers/v1/video.py` L225 `logger.success(f"Task created: {utils.to_json(task)}")` with `params = body.model_dump()` L212-214 — the full script + terms verbatim |
| C5 | same | `app/asgi.py` L57-63 `validation_exception_handler` → 400 `{status:400,data:e.errors(),message:"field required"}`; `app/models/schema.py` L88-161 `VideoParams` (no `extra="forbid"`) |
| C6 | same | `app/controllers/v1/video.py` L258-287 GET `/tasks/{id}`; `_task_file_to_uri` L107-127 (`/tasks/<id>/final-1.mp4`, `endpoint` prefix); `app/models/const.py` L25-27 (−1/1/4); `app/models/schema.py` L292 `extra="allow"`; 404 `task not found` L285-287 |
| C7 | same | `app/services/task.py` progress writes: L1388→5, L1510→10, L1537→20, L1562→30, L1587→40, L1613→50, L965-966+L1023-1024 `+=50/count/2` (=75 when `video_count=1`), L1674→100; 5→20 needs no LLM (L294-310, L315-352) |
| C8 | same | `app/services/task.py` L243-290 `_mark_task_failed` (first error kept L257-263); stages `preflight/script/terms/audio/materials/video`; uncaught → `pipeline` `f"{type(exc).__name__}: {exc}"` L1732-1739 (CR-B: `pipeline` is what an uncaught exception reports, so the adapter maps on `failed_stage` alone, never on `error` text) |
| C9 | same | `app/services/state.py` L44-95 `MemoryState` whole-record replace L69-74; L153-172 `RedisState` per-field `hset`; L238-243 `MemoryState` when `enable_redis=false`; `app/asgi.py` L43 recovers cross-posts only |
| C10 | same | `app/controllers/manager/base_manager.py` L48-85 (slot vs `queue_size() >= max_queued_tasks` L69-75); `app/controllers/manager/memory_manager.py` L7-19 `Queue(maxsize=…)`; 429 text at `app/controllers/v1/video.py` L230-233; `config.example.toml` L464-465 (5/100) |
| C11 | same | `app/controllers/v1/video.py` L290-322 (409 `task is still running` L306-308; `rmtree`+`delete_task` L313-316; 404 L321); `app/services/task.py` L106-123 `is_task_busy` (state 4 or active cross-post); **no cancel route** |
| C12 | same | `app/utils/file_security.py` L15-32 `realpath`+`commonpath`+`isfile` inside `storage/tasks`; `app/controllers/v1/video.py` `/stream` L457-486 always 206 `Content-Range`, `/download` L490-508 `FileResponse` `media_type=video/<ext>`; no per-task scoping (any file in the tasks tree readable) |
| C13 | same | `app/services/task.py` L532 `audio.mp3`, L592 `subtitle.srt`, L917-918/L968 `combined-N`/`final-N`; `app/services/task_artifacts.py` L16-18 `script.json`; `app/services/material.py` L1409 `openai-image-<12hex>.png`, L1530-1532 into task dir when `material_directory=""`; `app/services/video.py` L1553 `<png>.mp4`; MPT never deletes task dirs itself |
| C14 | same | `app/models/schema.py` L34-46 (`16:9`→1920×1080, `9:16`→1080×1920, `1:1`→1080×1080); `app/services/video.py` L71-75 aac 192k fps 30; L86-100 codec set incl `libx264` default + `h264_videotoolbox` etc. (set ends L100); L181-189 `audio_duration+0.1`; `app/services/task.py` L559-562 `audio_duration=ceil` |
| C15 | same | `app/services/task.py` L590-593 `subtitle_enabled`/`provider`; `app/services/subtitle.py` L62-69 `transcribe(beam_size=5, word_timestamps, vad_filter, min_silence 500, initial_prompt if set)` — no `language`; L19 `initial_prompt` is the global `[whisper]` key; L47-55 load failure → `None`, no file; L300-318 leftover chunks appended at next cue or `00:00:00,000`; `app/services/task.py` L637-638 sentence mode runs `subtitle.correct` |
| C16 | same | `app/services/video.py` L1446-1457 `SubtitlesClip`→`TextClip` composite (burned in); L1240-1242 font `resource/fonts/<font_name>`, default `STHeitiMedium.ttc`; L1260 `max_width=0.9W`; L1404-1423 positions `bottom/top/center/two_thirds_bottom/custom` (`(H-h)*p/100`); `webui/Main.py` L7774 the only `subtitle_font_supports_text` caller |
| C17 | same | `app/services/video.py` L1433-1437 narration ×`voice_volume`; L1487-1498 music ×`bgm_volume` + `AudioFadeOut(3)` + `AudioLoop` to video length; L1499-1506 mix failure → video without music; no loudness normalisation anywhere in `app/` |
| C18 | same | `app/services/video.py` L656-681 `get_bgm_file` (`""`→none, `"random"`→`choice` over both dirs); `app/services/bgm.py` L73-88 `should_use_bgm` (`bgm_volume>0`), L338-340+385-386 resolve `storage/bgm` then `resource/songs`; `app/services/task.py` L75-90 `sonilo`/`elevenlabs` paid generators |
| C19 | same | `app/services/task.py` L292-310 non-empty `video_script` skips `generate_script`; L313-334 non-empty `video_terms` skips `generate_terms` (string comma/，-split); L347 `rerank_terms_by_subject` bypassed only when `match_materials_to_script` |
| C20 | same | `config.example.toml` L11-12 `listen_host 0.0.0.0`/`listen_port 8080`; L26 `api_key`; L429 `subtitle_provider="edge"`; L440 commented `video_codec`; L448 `endpoint`; L453 `material_directory`; L457 `enable_redis`; L464-465 `max_concurrent_tasks=5`/`max_queued_tasks=100`; L500-511 `[whisper]` (`initial_prompt` commented) |
| C21 | same | `uv.lock` L685-686 ctranslate2 4.7.1; L768-769 fastapi 0.136.3; L784-785 faster-whisper 1.1.0; L1574-1575 moviepy 2.2.1; L2822-2823 starlette 1.2.1; L3082-3083 uvicorn 0.32.1 |
| C22 | same | `app/models/schema.py` L113-116 `video_clip_duration ge=1` default 5, `video_count ge=1`; L56-57 `Literal["sentence","word_by_word"]` + `["none","pop_spring"]`; L152-158 `text_background_color bool|str`, `font_size=60`, `stroke_width=1.5`, `n_threads=2`; L140-149 `subtitle_position`/`custom_position` defaults from `config.ui` |

→ **No README §1 C-row needs an edit.** A `moved`/`changed` drift against D3/D4 would have stopped the
spike (task file §Failure handling); none occurred.

### S1 — route and `VideoParams` surface

- `GET /openapi.json` is **unauthenticated** and on (FastAPI default, `app/asgi.py` L242). Snapshot:
  `worker-node/contract/mpt-routes.pinned.json` (the `paths`+methods map, schema `mpt-routes.v1`).
  Observed paths: `POST /api/v1/{videos,subtitle,audio,scripts,terms,social-metadata,musics}`,
  `GET /api/v1/{tasks,tasks/{task_id},musics,video_materials,stream/{file_path},download/{file_path}}`,
  `DELETE /api/v1/tasks/{task_id}`, `GET /ping` ✅ C1.
- `VideoParams` JSON schema dump at the pin (`$MPT_ROOT/.venv/bin/python -c
  '…VideoParams.model_json_schema()'`): `worker-node/contract/mpt-videoparams.pinned.json`.
  **39 fields.** The D3 body is a strict subset: `model minus D3 = {n_threads}` (the only field the
  spike never sends, F22). No field the D3 body needs is missing ✅.
- Samples: `samples/mpt/S1-missing-key.json` (the 401 body `verify_token` returns on the routed surface
  when `x-api-key` is absent), `worker-node/contract/mpt-routes.pinned.json`,
  `worker-node/contract/mpt-videoparams.pinned.json`.

### S2 — auth matrix (measured, live `mac` profile on 8080)

Key read once per the recipe in the method paragraph above (`source worker-node/bin/lib.sh &&
NTS_MPT_API_KEY="$(secret nts.mpt.api_key)"`), sent as `x-api-key`, never recorded. Samples:
`samples/mpt/S2-auth-matrix.json` (every cell below), `samples/mpt/S2-wrong-key.json` (the wrong-key
401 body — MPT makes no distinction between a missing and a wrong key; see also `S1-missing-key.json`).

| Variant | `/api/v1/tasks` | `/tasks/<id>/final-1.mp4` | `/api/v1/download/<id>/…` | `/docs` | `/ping` |
|---|---|---|---|---|---|
| no key | 401 | 401 | 401 | 200 | 200 |
| wrong key | 401 | 401 | 401 | 200 | 200 |
| two `x-api-key` headers | 401 | 401 | 401 | 200 | 200 |
| right key | 200 | 404 | 404 | 200 | 200 |

- Bad/absent/duplicate key → 401 `{"status":401,"message":"invalid API key"}` on every guarded route,
  **including the `/tasks` static mount** (C2 ✅, middleware `app/asgi.py` L258-273). `/docs`, `/ping`,
  `/openapi.json` are open (C1/F12 ✅).
- A valid key on a non-existent task → 404 `{"status":404,"message":"<request_id>: task not found"}` ✅.
  ⚠️ the echoed id is the **`x-task-id` `request_id`, not the path `{task_id}`** — when `x-task-id`
  is absent `normalize_task_id` substitutes a random UUID (`app/controllers/base.py` L13-26). Pollers must send
  `x-task-id` if they want the log to carry their id.

### S3 — happy path (measured on the spike profile)

Spec `s3-happy-16x9`: en story (8 styled terms supplied → the LLM is never called, C19 ✅),
`video_duration_per_clip=10` on a 37 s TTS-stub track → 4 images. Samples:
`samples/mpt/S3-happy-task.json` (full `GET` body, redacted), `S3-progress-trace.json` (1 s polls),
`S3-happy-log.txt` (redacted log slice). The stub call rows of this run are **not** committed: each stub
writes one calls file per process (`$NTS_HOME/runs/spike-05/{image,tts}-stub-calls.jsonl`, D12 — a
runtime artefact under `$NTS_HOME`), tagged per run by its script's `case`, and the committed evidence
for S3 stays the task-API body, the progress trace and the log slice.

- Progress → stage observed: `0 (queued) → 20 (voice) → 30 (subtitles) → 40 (images) → 50 (assembling)
  → 75 (assembling/final encode) → 100 (done)`. The 5/10 writes (script/terms) complete instantly when
  `video_script`+`video_terms` are supplied, so 1 s polling sees `0→20` directly (C7/F09 ✅).
- Task dir after success (C13 ✅): `audio.mp3`, `subtitle.srt`, `combined-1.mp4`, `final-1.mp4`,
  `openai-image-<12hex>.png` + `.png.mp4` ×4, `script.json`.
- Download: `GET /api/v1/download/<id>/final-1.mp4` → 200 `video/mp4`,
  `Content-Disposition: attachment`, honours `Range` (206). `GET /api/v1/stream/<id>/final-1.mp4`
  → always 206 `Content-Range` even without `Range` (C12 ✅). `GET /tasks/<id>/final-1.mp4` (static
  mount) → 200, same bytes. Path traversal (`/api/v1/download/../config.toml`) → 404 (file_security ✅).

### S4 — validation / unknown fields

`POST /api/v1/videos` with `video_subject` missing → HTTP 400
`{"status":400,"data":[{"type":"missing","loc":["body","video_subject"],…}],"message":"Field required"}`
(FastAPI `RequestValidationError`, C5 ✅). `video_aspect:"landscape"` → 400 `Field required` enum error.
Sample: `samples/mpt/S4-validation.json`.

### S5 — TTS 5xx / empty body (two variants)

Both variants run the S3 spec against the spike profile with the TTS stub scripted, and both end in the
same terminal shape — `state -1`, `progress 20`, `failed_stage: "audio"`, `error: "failed to synthesize
audio; verify the selected voice and TTS connectivity"` (C8 ✅ → adapter `tts_failed`, D5/P4). What
differs is the stub answer and the log line that records it:

| Variant | Stub mode | Stub answers | MPT log line |
|---|---|---|---|
| 5xx | `e500` | 3 × HTTP 500 `{"error":{"code":"server_error",…}}` | `app/services/voice.py:2185` `kokoro tts failed with status 500: {…}` |
| empty | `empty` | 3 × HTTP 200, `Content-Length: 0` | `app/services/voice.py:2219` `kokoro tts failed: kokoro returned empty audio` |

- `empty` (measured 2026-09-26 on the spike profile, task `76f962d4-7636-4e4a-9ec7-70eb1d18c1e9`):
  the stub served **three 200s of 0 bytes** (`S5-tts-empty-stub-calls.json`), MPT logged
  `kokoro returned empty audio` after each and then failed the task at `audio`. An empty body is a
  **hard** audio failure, not a silent zero-length success — the `Content-Length: 0` answer never
  reaches the subtitle stage. Samples: `samples/mpt/S5-tts-empty-task.json` (the redacted `GET` body),
  `samples/mpt/S5-tts-empty-stub-calls.json` (the three rows, `bytes_written: 0`),
  `samples/mpt/S5-tts-empty-log.txt` (the redacted log slice).
- `e500`: `samples/mpt/S5-tts-500-task.json`, `samples/mpt/S5-tts-500-stub-calls.json`,
  `samples/mpt/S5-tts-500-log.txt`.
- In both variants MPT retried **3 times inside the same second** (`try: 1/2/3`,
  `app/services/voice.py` L2180) — **no backoff, sub-second**; the node adapter owns retrying,
  MPT does not.

### S6 — TTS abort mid-stream

`abort` mode answers 200 with the fixture's full `Content-Length` and drops the socket after
4096 bytes (04 X2). Same terminal shape as S5 (`state -1`, `progress 20`, `failed_stage: "audio"`,
the same `error` string) — a truncated body is a hard audio failure too. Samples:
`samples/mpt/S6-tts-abort-task.json`, `samples/mpt/S6-tts-abort-stub-calls.json` (three rows, each
`aborted: true`, `bytes_written: 4096`).

### S7 — whisper failure → zero-cue success (F02)

✅ whisper is a **process-lifetime singleton** (`app/services/subtitle.py` loads the model once, lazily — measured:
a mid-process `model.bin` corruption left the running process producing normal subtitles because the
model was already resident). The failure must be staged **at process start** (the task file's recipe is
correct as written). With a corrupt `model.bin` + `HF_HUB_OFFLINE=1` at launch,
`subtitle.create` logged `failed to load model: Unsupported model binary version …` and returned `None`;
the task then wrote `subtitle.srt` with **15 cues, all `00:00:00,000 --> 00:00:00,000`** carrying the
script text and **succeeded** (`state 1`, `progress 100`, `failed_stage null`). F02 confirmed —
a whisper failure is invisible to `failed_stage`; detect it via `subtitle.srt` zero-cues (D9
`zero_duration`), never via `state`. Samples: `samples/mpt/S7-whisper-fail-{task.json,subtitle.srt,log.txt}`.

### S8 — zero-speech audio

✅ `custom_audio_file` must resolve inside `storage/tasks/<task_id>` (`app/services/task.py` L364-419,
`allow_server_file_input=false` for HTTP). The `task_id` is server-issued at submit, so the only
API-safe supply is to queue the task behind a running occupant and drop the file into its dir before
it dequeues — done (sine-20s.mp3 → task `d34ab243`). The TTS stub's own `sine` mode
(`by_input_contains: {"__tone__": "sine"}` → 440 Hz fixture, no speech) produces the identical
zero-cue result through the normal TTS path and needs no file staging. Result: task **succeeded** (`state 1`);
`subtitle.srt` exists with 15 cues, **all `00:00:00,000 --> 00:00:00,000`** carrying the script text;
`final-1.mp4` duration = 20.0 s (the sine, not the script). This is the same fail-open as S7 — the D9
`zero_duration`/`coverage_share=0` input. Samples: `samples/mpt/S8-sine-task.json`, `S8-sine-subtitle.srt`.

### S9 — all images fail (`failed_stage = materials`)

The task file's S9 is the every-image-call-fails case, already measured in the 03.4.1-T1 image spike
(`worker-node/contract/images.md` §P4 — the summary table row at `images.md:213`; "re-run only if 03
lacks the task API sample" never fired). At the pin: **`state = -1`, `progress = 40`,
`failed_stage = "materials"`, `error = "failed to download video materials from openai_image"`** —
`app/services/task.py` L787-791 `_mark_task_failed(…, "materials", …)` (I9), after 8 stub calls in
15 s. A `400 moderation_blocked` answer is **not** retried — the 8 calls arrive with 0 s gaps (I4/I6);
and a task that fails here has no `combined-1.mp4`/`final-1.mp4` (the video stage never runs).
Adapter mapping: `failed_stage = materials` → `image_generation_failed` (D7 rows 6/7/8).

Samples (the 03 spike's own dir `worker-node/contract/samples/images/`): `P4-task.json` (the
`GET /api/v1/tasks/{id}` body — `failed_stage = "materials"` verbatim), `P4-stub-calls.jsonl` (the 8
`moderated` rows), `P4-log.txt` (the redacted log slice with the `HTTP 400: …safety system…` detail).

*Extra note* (the first-pass probe, kept because it answers a different still-useful question): there
is **no voice-listing route at the pin** (`v1/audio.py` does not exist; the controllers are
`app/controllers/v1/video.py` + `app/controllers/v1/llm.py` only). `GET /api/v1/audio/voices` → 404 for
every auth state; `voices = []` in config is a WebUI catalogue only, and the API accepts any
`voice_name`, failing at synthesis (S5). Sample: `samples/mpt/S9-voices-endpoint.json`.

### S10 — assembly failure (missing font)

✅ `font_name: "missing-font-xyz.ttf"` with subtitles on (raw body — the D6 preset otherwise overrides
`font_name` to `BeVietnamPro-Bold.ttf`, so S10's first run `55f27607` rendered fine). moviepy
`TextClip` raises `ValueError: Invalid font <mpt_root>/resource/fonts/missing-font-xyz.ttf` at the video
stage; because it is uncaught, `failed_stage` surfaces as **`pipeline`** (not `video`) and `error` as
`ValueError: …` (`_mark_task_failed` fallback, `app/services/task.py` L1732-1739). C8 correction: the adapter MUST map
on `failed_stage`, never on `error` text; `pipeline` → `render_assembly_failed`/`render_node_error`.
Samples: `samples/mpt/S10-assembly-missing-font.json`, `S10-assembly-log.txt`.

### S11 — queue vs capacity (C10)

With `max_concurrent_tasks=1` (spike template) a second submit returns 200 and sits at
`state 4, progress 0` — **indistinguishable from a running task** (C10). Samples:
`samples/mpt/S11-queue.json` (default `max_queued_tasks=100` → silent queue), `samples/mpt/S11-429.json`
(`--template` render with `max_queued_tasks=0` → second submit returns **HTTP 429
`task queue is full, please try again later`**). C10 ✅: 429 = no task created (retry later);
queued-200 = silently `state 4, progress 0`.

### S12 — restart mid-task (C9, F06)

✅ SIGKILL mid-`images` (progress 40) on task `02c90883`; the wrapper freed :18080. After relaunch:
`GET /tasks/{id}` → **404** (memory state — the record is gone, not interrupted; F06 `render_lost`),
the **orphan dir `storage/tasks/02c90883-*/` persists** with `audio.mp3`/`subtitle.srt`/4 image clips/
`script.json` (MPT never GCs a task dir, C13), and **no ffmpeg leftover** (`pgrep -f 'ffmpeg.*<id>'`
empty — the in-flight encoder was a child of the killed process). The 404 echoed a *random* request_id
because the poll sent no `x-task-id` (C6/`normalize_task_id`). Sample: `samples/mpt/S12-restart.json`.

### S13 — delete matrix (C11, F08)

| case | DELETE | result |
|---|---|---|
| unknown id | 404 | `{"status":404,"message":"<x-task-id>: task not found"}` |
| running (state 4) | 409 | `{"status":409,"message":"<x-task-id>: task is still running"}` |
| complete (state 1) | 200 | `{"status":200,"data":null}` + task dir removed (`ls` 404) |

There is no cancel route; DELETE-on-running is refused, not honoured. The echoed id is `x-task-id`.
Sample: `samples/mpt/S13-delete-matrix.json` (every row of the table above, plus the post-delete `GET`
404 and the gone task dir).

### S14 — output variants

✅ Portrait `9:16` → `ffprobe` 1080×1920 h264 High yuv420p 30fps, aac 44100 stereo (C14 ✅; the client's
`spec.aspect` maps to `body.video_aspect` — an earlier run sent the field under the wrong spec key and
stayed 1920×1080). `subtitle_enabled=false` → no `subtitle.srt` burned. `bgm_type random/custom` was **not run** and stays
`open` in the sample: `random` needs ≥2 files in `storage/bgm`; `custom` needs a `bgm_file` upload via
`POST /api/v1/musics` (returns an immutable UUID filename). Both are WebUI-catalogue features the
adapter never uses — C17 documents the mixing code path, not a run. Sample:
`samples/mpt/S14-output-variants.json` (variants run: portrait-9:16, landscape-16:9,
subtitle_enabled=false).

### S15 — log content (C4, F18)

MPT logs the **entire submit body** — the full narration `video_script`, `video_subject` and every
`video_terms` entry verbatim — at `SUCCESS` on task creation (`app/controllers/v1/video.py` L225-226:
`logger.success(f"Task created: {utils.to_json(task)}")`, C4). The API never returns it; it lives in
the node's log and every task dir (`script.json`, C13) until the adapter deletes the dir (F18).

Sample: `samples/mpt/S15-log-content.txt` — the `Task created: {…}` dump from `S3-happy-log.txt`,
in the committed redacted form: every content value is `<text sha256=…>` (10 markers: the subject, the
script, and the 8 terms), the rest of the body intact. That same dump shape is what MPT writes for
every S3/S5 run — script + prompts at SUCCESS confirmed (C4 ✅, F18).

*Extra note* (the first-pass probe, kept because it answers a different still-useful question): a body
without `video_terms` forces `generate_terms` → the spike `openai_base_url` is the image stub →
`failed_stage: "terms"`, `state -1`, `progress 10`, `error: "failed to generate video search terms"`.
MPT log: `llm provider: openai … Error code: 404 - route_not_found` — the stray call hit a 404, never
a paid upstream (C19 inverse, F22). Samples: `samples/mpt/S15-llm-404-task.json`, `S15-llm-404-log.txt`.

### S16 — real-chain (optional)

[OPEN deferred — 04.1.2-T1 proxy path not gated for this wave; the D2 spike profile is the contract's
evidence surface. Real upstream timing is a separate plan item.] Marker sample:
`samples/mpt/S16-deferred.json` (records the deferral, its reason, and that no measurement was taken).

