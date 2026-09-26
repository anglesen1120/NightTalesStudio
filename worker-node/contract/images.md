# `worker-node/contract/images.md` — MPT image-failure contract (spike 03.4.1-T1)

MPT commit: `3d5f4e421927d61f3eac729cf4b711ac0b69d688` (`worker-node/pins.env` `MPT_COMMIT`; 01 D1).
Date: 2026-09-26. Machine: owner Mac (macOS 27, arm64), MPT 1.3.7 at the pin.
Consumers: 03.4.3-T1 (image relay), 05.4.1 (adapter report), 11.1.2-T1 (mock shapes), 03.2.1/03.4.2 (planning + mapping).

Method: the pinned MPT was run **against a scripted fake Images API** (`trial/tools/image_stub.py`,
`127.0.0.1:18003`) on its own profile and port, so **no request left this machine and no OpenAI call was
made** (03 README D14). The spike is a second MPT instance on `127.0.0.1:18080` with a swapped config
(`render-mpt-config.py --profile spike`, `start-mpt-api.sh --profile spike`); the live 8080 instance was
**never stopped or restarted** (MPT reads `config.toml` only at process start and keeps it in memory).
`config.toml` itself **was** temporarily replaced by the spike render and is restored on every exit path
(`config.toml.pre-spike` → back to `config.toml`) ⚠️.

Legend: ✅ verified by execution at the pin · ⚠️ unverified / partial · ❌ corrected · [INFERENCE] · [OPEN].

## 1. P0 — pin check: every README §1 `I` fact re-read at `MPT_COMMIT`

Read at the pin on 2026-09-26: `app/services/material.py`, `app/services/task.py`, `app/services/video.py`,
`app/models/schema.py` (plus `app/services/twelvelabs.py`, `app/config/config.py`, `app/models/const.py`,
`app/controllers/v1/video.py`). `git -C $NTS_HOME/src/mpt rev-parse HEAD` = the pin above ✅.

| # | Fact (03 README §1) | Evidence at the pin | Verdict |
|---|---|---|---|
| I1 | Image request body is exactly `{model, prompt, n: 1, size}` — no `quality`, `moderation`, `response_format`, `user` | `material.py` L1453-1458 | ✅ source identical to the review snapshot (same line range) |
| I2 | `openai_image_size = ""` → `1024x1536` portrait, `1536x1024` landscape, `1024x1024` square | `material.py` L1102-1106 (dict), L1158-1170 (`_openai_image_size`) | ✅ same line range |
| I3 | Final prompt = `template.replace("{term}", term)` when the template is non-empty and holds `{term}`; otherwise the raw term; a replace error also falls back to the raw term | `material.py` L1172-1193 (`_openai_image_prompt`) | ✅ same line range |
| I4 | Retry on 429/500/502/503/504, `OPENAI_IMAGE_MAX_ATTEMPTS = 3`, backoff `(5, 15, 30)`; 401/403 retried only with >1 key; every other status ≥ 400 (incl. 400 `moderation_blocked`) returns at once | `material.py` L1108-1119, L1304-1390 | ✅ same line range; measured waits: §P5 |
| I5 | A read timeout / dropped connection is **not** retried (double-billing) — only a connect timeout is; timeouts `(connect 30 s, read 300 s)` | `material.py` L1116 (`OPENAI_IMAGE_REQUEST_TIMEOUT = (30, 300)`), L1342-1357 | ✅ same line range; measured: §P8 |
| I6 | On failure MPT logs `openai image generation failed: term=<repr>, detail=HTTP <status>: <error.message ≤ 300 chars>` and skips the keyword (`return []`); the OpenAI `error.code` is not logged | `material.py` L1196-1215 (message extraction), L1464-1468 (log + `return []`) | ✅ same line range; measured: §P2-§P4 |
| I7 | Images are generated **on demand** in list order; after each success `video_clip_duration` is added and generation stops as soon as `successes × video_clip_duration ≥ audio_duration × video_count`; a skipped term is replaced by the next one | `material.py` L1530-1596 (`_download_videos_openai_image_on_demand`), `task.py` L734-746 (`audio_duration * video_count`) | ✅ (snapshot said `task.py` L733-746) measured: §P1-§P3 |
| I8 | When the clips are shorter than the audio, `combine_videos` loops them until the audio is covered | `video.py` L935-949 (`itertools.cycle`) | ✅ (snapshot said L934-949) measured: §P3 |
| I9 | Zero clips → task failed, stage `materials`, message `failed to download video materials from openai_image` | `task.py` L787-791 (`_mark_task_failed(..., "materials", f"failed to download video materials from {params.video_source}")`), error text at L810-812 | ✅ moved ~7 lines from the snapshot (L803-809); measured: §P4 |
| I10 | `video_concat_mode` defaults to `random` (`random.shuffle(primary_items)`); order is kept with `sequential` or `match_materials_to_script = true` (which forces sequential) | `video.py` L203-224 (`concat_mode != random` → order kept), L256 (`random.shuffle`), L826-830; `task.py` L739-743, L906-907; `schema.py` L111, L115 | ✅ (shuffle at L256 vs snapshot L245-257) measured: §P1 order kept, §P11 shuffled to 3,1,2,4 |
| I11 | `video_terms` as a **string** is split on `,` and `，`; as a **list** each item is only `strip()`ed; no length limit; an empty result fails at stage `terms` | `task.py` L313-352; `schema.py` L108 (`Optional[str \| list]`) | ✅ same line range; measured: §P10 |
| I12 | Terms are re-ranked by TwelveLabs only when TwelveLabs is enabled **and** `twelvelabs_rerank_terms = true` (default `false`) | `twelvelabs.py` L97-125 (early return when disabled or the flag is falsy); `config.example.pinned.toml` L213 (`twelvelabs_api_keys = []`), L216 (`twelvelabs_rerank_terms = false`); the rendered config has **no** `twelvelabs_*` key at all → `config.app.get(...)` is `None` → falsy | ✅ |
| I13 | `VideoParams.video_clip_duration` defaults to **5** (`ge=1`) | `schema.py` L113 (`Field(default=5, ge=1)`) | ✅ same line range |
| I14 | With `material_directory = ""` images land in the task dir as `openai-image-<uuid12>.png` (names never come from the term); the task dir also receives `material_sources` (with `search_term`) and `script.json` (script, search terms, all params) | `material.py` L1409 (`openai-image-{uuid4().hex[:12]}.png`), L87-155 (`_material_source_record`, `_persist_material_sources`); `task.py` L355-361 (`save_script_data`) | ✅ observed: §P1 (task dir listing) |
| I15 | The image key is optional: an empty `openai_image_api_keys` → **no** `Authorization` header; the source counts as enabled when `openai_image_base_url` and `openai_image_model` are set | `material.py` L1122-1135 (`is_openai_image_enabled`), L1320-1336 (header only when a key resolves) | ✅ measured: `has_auth_header = false` in every `P<n>-stub-calls.jsonl` |
| I16 | A `url` response is downloaded up to 3× from the same URL (never re-generated); `b64_json` is decoded; an undecodable image skips the keyword; a disk write error raises | `material.py` L1230-1305 (download retries + `_parse_openai_image_response`), L1393-1431 (`_save_openai_image_file`), L1469-1480 (decode error → skip) | ✅ same line range; measured: §P9 — `garbage` skipped (`UnidentifiedImageError`, L1476-1479), `url` fetched exactly once |
| I17 | MPT logs the full final prompt at INFO on generation (`generating image via openai-compatible endpoint: model=…, term=…, size=…`) and again on failure | `material.py` L1459-1461, L1464-1468 | ✅ same line range — the reason every sample here is prompt-redacted (D12) |

Verdict: **I1-I17 all confirmed at the pin; no `I` fact is wrong.** The only drift against the review
snapshot is line movement in `task.py` (I7, I9) and `video.py` (I8, I10); two snapshot line numbers were
already exact. `task.py` at the pin also gains a second empty-materials guard
(`task.py` L1599-1602, `failed to prepare video materials`) which is unreachable while
`get_video_materials` already marks the task failed — `_mark_task_failed` keeps the **first** error
(`task.py` L243-278), so the API shows the specific `failed to download video materials from openai_image`
(measured in §P4) ✅. No README §1 edit is needed for plan 03.

**Evidence legend for the case references:** every `I` verdict in the table above is a *read* of the pin
(✅ by itself). Where a row also says `measured: §P<n>`, §3/§4 hold the committed sample that produced the
behavioural verdict.

## 2. What plan 03/05 can rely on (conclusions)

### 2.1 Relay (D5) and the request the relay will see

* MPT sends exactly `{model, prompt, n: 1, size}` (I1 ✅). `quality`, `moderation`, `response_format` and
  `user` are **not** sent, so the relay's injected `quality`/`moderation` are pure additions and there is no
  MPT field to overwrite.
* `prompt` is the raw styled term when `openai_image_prompt_template = ""` (I3 ✅, the spike profile) and
  `template.replace("{term}", term)` otherwise (the M0 production stand-in). `n` is always `1`.
* `size` follows the aspect when `openai_image_size = ""`: the spike (16:9) requested `1536x1024` on every
  call ✅ (I2), i.e. the relay's `size` field is one of the three documented values.
* Authentication: with an empty key list MPT sends **no** `Authorization` header ✅ (I15) — measured
  `has_auth_header = false` on every stub call in every case. The relay is therefore the only component
  that needs the image key, exactly as D5/F09 assume.
* Retry semantics the relay must not fight: MPT retries 429/5xx itself (`5 s`, `15 s`, max 3 attempts,
  I4) and gives up after ≈ 20 s, while a 400 (incl. `moderation_blocked`) and any other ≥ 400 returns
  immediately (I4/I6); a read timeout is **never** retried — measured in §P8: the term is abandoned at
  exactly **300 s** (zero `attempt=/3, next_retry_in` lines) and the whole task pays that 300 s. The relay's own retry
  (`ImageRelay` one relay-side retry, D5/F06) therefore sits *inside* MPT's attempt budget: a relay 429 or
  a relay hold longer than the read timeout costs the term, not just one attempt.
* `usage` in the response body: MPT ignores it (`_parse_openai_image_response` reads only
  `data[0].b64_json` / `data[0].url`) — so an image billed through the relay is only visible to the
  platform through the **relay's own report** (D6), never through MPT. That is the core reason 03.4.3
  exists ✅.
* A `url` response is downloaded by MPT itself (up to 3× from the same URL) and re-generated never (I16,
  measured in §P9: exactly **one** fetch on success — one `imagedownload slot=2` for the whole run; the
  3-attempt loop at `material.py` L1241 only re-fetches after a *failed* download) — a relay that returns
  200 with a `url` would move the image bytes outside the relay's control and add a second, unauthenticated
  fetch; `b64_json` is the shape to emit (the relay's `relay_image_missing` → 502 path already assumes a
  body without an image).

### 2.2 Report (D6) and what MPT cannot tell us

* MPT's task API carries **no per-image data**: the task object holds `state`, `progress`, `failed_stage`,
  `error`, `videos`, `combined_videos` (and, for the terms/script stop-ats, `terms`/`script`). Per-image
  status, `error.code`, usage and prompt attribution exist **only** in the relay's JSONL ✅ — so
  `images.v1` (D6) cannot be reconstructed from MPT and must come from the relay.
* The image count is a *stop condition*, not a plan: MPT calls terms in list order and stops after
  `ceil(audio ÷ clip)` successes (§P1: 4 calls for 4 needed images, `t05`-`t08` never called) ✅ I7. A
  skipped term is replaced by the next one (§P2). The relay's cap (`max_images`) therefore has to be
  enforced on *claimed slots*, as D5 specifies.
* The **rendered** order is not the generation order: `video_concat_mode` defaults to `random`
  (`random.shuffle`, `video.py` L256) and `match_materials_to_script = true` is what forces `sequential`
  (`task.py` L906-907). Measured in §P11: the calls still ran `t01`-`t04` in list order, but the scenes came
  out **3, 1, 2, 4**. A mock or a report must therefore never assume "scene *n* = term *n*" (F02 ✅).
* `needed` in `images.v1` is unknowable to the relay for the same reason and must be filled from
  `render_jobs.params.image_plan.needed` (D6) — confirmed: the relay sees calls, not audio ✅.
* Prompt attribution by hash works: MPT sends the term **unchanged** (spike profile) and the same prompt
  is byte-identical across a retry, so `sha256(prompt)` is stable per slot ✅ (the stub's `prompt_sha256`
  matched the fixture prompts exactly in §P1).
* `moderation_blocked` never reaches the task API: it is a 400 on one call, the keyword is skipped and the
  task continues (§P2/P3/P4). At the pin MPT does not even log the `error.code`, only `error.message`
  (I6 ✅), so **D5's relay is the only place the platform can learn that an image was blocked**.

### 2.3 Classifier (D7)

* "MPT succeeded" is **not** "no image was blocked" — confirmed by construction: §P2 reaches
  `state = 1` while one image was moderated. Rows 1/2/3 of D7 therefore need the relay report, and row 6
  (MPT failed at `materials` with moderated slots) is only reachable when *every* called term failed (§P4).
* Stage text for the failed case: `failed_stage = "materials"`, `progress = 40`, `error = "failed to
  download video materials from openai_image"` (§P4) — this is the string 03.4.2-T1 must expect when no
  report is available (D7 row 8).
* MPT's own retry budget is short (≈ 20 s, I4) and a read timeout skips the term without retry after 300 s
  (I5, §P8) — the longest a single lost slot can hold the whole task, so
  "how many slots were skipped" is dominated by relay behaviour (pacing/429) rather than by MPT — the
  relay's `relay_rate_limited` counter, not MPT's state, is what makes row 4/5 vs row 3 decidable.

### 2.4 Node logging / data (D12) — a correction worth acting on

MPT logs the **entire task body** at INFO when a task is created:
`app/controllers/v1/video.py` `create_task` → `logger.success(f"Task created: {utils.to_json(task)}")`
where `task["params"] = body.model_dump()` — i.e. `video_script` (the whole narration) **and** every
`video_terms` entry appear verbatim in `mpt-*.log` on every run, in addition to the per-image
`term=<repr>` lines of I17 (both observed in `P1-log.txt`, before redaction) ⚠️. Consequences:

* D12's "MPT logs stay on the node, rotate after 7 days" is not enough on its own if any support bundle,
  CI artefact or bug report ever picks up `$NTS_HOME/logs` — the file holds the story text and all prompts.
* `trial/tools/redact_prompts.py` now strips **four** shapes: `term='…'`, a `"video_script": "…"` line, the
  items of an array under any prompt-bearing key (`terms`, `video_terms`, `search_terms`, `prompt`,
  `script`, `video_script`), and MPT's `subtitle.py` correction lines `… - Script: <narration>, Subtitle:
  <corrected>` (both operands hashed) ✅ (its
  `--self-test` covers each), which is what makes the committed samples here safe. It does **not** hash a
  narration fragment written under any other key or free-form shape (e.g. a `correct - Subtitle:` line with
  no `Script:` operand, or a `"video_script"` value nested deeper than the top-level key), so the safety
  proof is the re-grep, not the regex list: `grep -rE "Dark folklore|moonlight" worker-node/contract/samples/`
  → 0 lines, and none of the narration fixture's own phrases appear anywhere in `samples/` (e.g.
  `grep -rF "listening for the bell that only rang when fog rolled in" worker-node/contract/samples/` → 0).
* `script.json` in the task dir is prompt-bearing in **two** keys (`search_terms` and
  `material_sources[].search_term`) ⚠️ I14 wording correction — the adapter cleanup (05.4.1 item 7) must
  delete the whole task dir, not a list of files.

### 2.5 Open points handed to other tasks

| # | For | Point |
|---|---|---|
| 1 | 05.4.1 | The task-query route is `GET /api/v1/tasks/{task_id}` with the `x-api-key` header and returns the `{"status","message","data"}` envelope; the fields 03.4.2-T1 needs live under `data` (`state` 1/-1/4, `progress`, `failed_stage`, `error`, `videos`, `combined_videos`) ✅ sampled. MPT's task store is **in-process** (`enable_redis = false`), so a task is only queryable on the instance that created it — a restarted adapter loses terminal states. ⚠️ |
| 2 | 03.4.2-T1 | `needed` from `image_plan` vs MPT's own audio duration drifts by ≈4 % (33.02 s vs 34.46 s for the same text) ⚠️; and the combined video is `audio + 0.1 s`, so the last scene can be shorter than `clip_seconds` (3.03 s of 10 s in §P1). **Useful for D6:** the task JSON *does* carry `data.audio_duration` (e.g. `36` in §P2; the `combine_videos - audio duration` line in `P2-log.txt` reads 35.28 s) alongside `data.audio_file` and `data.materials`, so the adapter (05.4.1) can fill `images.v1.needed` as `ceil(data.audio_duration ÷ video_clip_duration)` from MPT itself rather than trusting `params.image_plan.needed` — neither field comes from the relay ✅ |
| 3 | 03.4.3-T1 | The relay must emit `b64_json` (a `url` would be fetched by MPT itself — measured once in §P9); it must never return 200 without an image for a prompt it claimed (MPT skips that term and carries on, §P9); and its holds must stay well under MPT's read timeout (300 s) because MPT will not retry a read timeout — measured in §P8: exceeding it drops the term, costs ≈ +300 s of wall time (390.3 s vs ≈ 90 s for the same body), and MPT never re-asks, so a relay-side retry cannot recover it. |
| 4 | 11.1.2-T1 | Mock shapes: a moderated call is `400 {"error":{"code":"moderation_blocked","message":"Your request was rejected by the safety system.","type":"image_generation_user_error"}}`; the success body carries `data[0].b64_json`, `created`, `quality`, `size`, `usage{input_tokens,input_tokens_details{text_tokens,image_tokens},output_tokens,total_tokens}` — MPT reads none of it ✅ (so mocks only need `b64_json`). |
| 5 | 03.2.1-T1 / 03.3.1 | `needed = ceil(audio_s ÷ clip_s)` is confirmed as the driver, but MPT stops at `successes × clip_s ≥ audio_s` — extra terms are free (never called) ✅, which supports D3's `MaxTerms` with spares. |
| 6 | 01 README F24 (via 03 CR-8) | F24's ≈ 50 s wait is wrong: the measured cost of exhausting a term is ≈ **20 s** (5 s + 15 s, §P5), because attempt 3 is terminal (`material.py` L1377-1386). The 30 s backoff entry is unreachable at `MAX_ATTEMPTS = 3` ⚠️ |
| 7 | 03.4.2-T1 / 03.4.3-T1 | P2 is the D7 row 2 shape end-to-end: one moderated slot, MPT still `complete`, and the rendered video simply **omits** that scene (slots 1, 3, 4, 5 = `t01`, `t03`, `t04`, `t05`) with no looping and no visible break — so a 1-in-8 moderated scene is invisible to MPT and only the relay report can drive row 2's `moderation_events` + quality hint ✅ |
| 8 | owner / 03 README | P12 (one real 1-image run through the tap, to confirm `request.quality = null` for MPT calls) was **skipped**: it needs a live paid image call, and the spike's acceptance is provable without it. It stays [OPEN] for 03.4.3-T1/03.3.2. |

## 3. Cases

Every case uses the same submit body (03 README D4) unless its row says otherwise:
`video_source = "openai_image"`, `video_aspect = "16:9"`, `video_clip_duration = 10`,
`video_concat_mode = "sequential"`, `match_materials_to_script = true`, `video_count = 1`,
`voice_name = "kokoro:Ngọc Huyền"` (the en voice of the local proxy on 127.0.0.1:8001),
`video_script` = `trial/fixtures/spike-images-en.txt` (34.5 s of narration →
`ceil(34.5 ÷ 10) = 4` images, I7), `video_terms` = the 8 styled prompts of
`trial/fixtures/spike-images-terms.json` (320-336 chars each, `≈330`), `bgm_type = ""` (no licensed music)
and, for P1 only, `subtitle_enabled = true` (the production path); every other case runs with
`subtitle_enabled = false` to fit the spike budget ⚠️ — that skips the whisper stage only
(`task.py` L589-590 returns early), and every measurement below (image calls, term order, task state,
`failed_stage`) happens at or before the `materials` stage, so it cannot affect a P2/P4/P5/P7/P8/P9/P10/P11 verdict.
P1's own wall time (150.1 s) does include whisper; the other cases' wall times do not. Submitted with
`POST /api/v1/videos` + `x-api-key`, polled with `GET /api/v1/tasks/{id}`; the task-query response is the
`{"status", "message", "data"}` envelope from `app/utils/utils.py` L18-25 ⚠️ 05.4.1 must read
`data.state` / `data.progress` / `data.failed_stage` / `data.error` (see the samples).

Samples: `worker-node/contract/samples/images/P<n>-task.json` (full task-query response, terms hashed),
`P<n>-stub-calls.jsonl` (one row per stub call: `n, ts, prompt_sha256, prompt_len, size, has_auth_header,
outcome`) and `P<n>-log.txt` (MPT log slice with every `term='…'` replaced by
`term=<prompt sha256=…>` by `trial/tools/redact_prompts.py`); P9 additionally commits
`P9-img-downloads.txt` (the prompt-free stub-stdout slice proving the `url` response was fetched exactly
once). The runner (`.nts/spike-0341/`, gitignored,
not part of this contract) also samples the combined video 4×/s and maps the dominant colour back to the
stub's per-call colour, which is how scene order and scene length below are measured.

Path fields in `P<n>-task.json` (`data.audio_file`, `data.subtitle_path`, `data.materials[]`) are
**normalised to MPT's own `/tasks/<task_id>/…` form** — the form `data.videos`/`data.combined_videos`
already use: at the pin the API returns them as absolute `$NTS_HOME/src/mpt/storage/tasks/<task_id>/…`
paths, and the committed samples drop that host prefix so no volume name or checkout path is published
(SEC-NTS-M0-07). The task UUID and every file name are unchanged, so the suffix is byte-identical to what
MPT returned; the live API still returns the absolute form.

⚠️ Two hashes appear in the samples and they are **different things**. `P<n>-stub-calls.jsonl` carries
`prompt_sha256`: the real sha256 of the prompt, and the only source for call→term attribution (verified:
stub row *n* equals fixture term *n* in `trial/fixtures/spike-images-terms.json` by both sha256 and length,
so calls 1-4 are `t01`-`t04` and call 5 is `t05`). `P<n>-task.json` carries the redactor's marker
`<prompt sha256=<first 16 hex of the value's sha256>>`; the three cases added here were **normalised to a
single pass**, so `data.terms[i]` is that marker of fixture term *i* and `data.script` is that marker of the
fixture story. The older `P1`-`P10` samples were **not** normalised — they were redacted repeatedly before
the guard described below existed, so their markers are hashes of hashes (verified: `P1-task.json`
`terms[0]` = `00e133ab841824e0` is pass 4 of the fixture term, its `script` = `5e2792fc772b8bf3` is pass 3)
and must not be used for attribution. ⚠️ [OPEN] for the owner: re-normalise those files or leave them as-is
(the stub rows already carry the same information).

### Case execution status

| Case | Status |
|---|---|
| P0 (pin check, §1) | ✅ done — the four MPT files re-read at the pin, a verdict for every `I` fact |
| P1 (baseline) | ✅ done — 4 calls `t01`-`t04`, `state = 1`, clips 10.000 s, scenes slots 1→2→3→4 |
| P2 (one moderated image) | ✅ done — 5 calls `ok, moderated, ok, ok, ok`, `t02` blocked and replaced by `t05`, `state = 1`, scenes slots 1, 3, 4, 5 |
| P3 (six moderated) | ✅ done — 8 calls, calls 1-6 `moderated`, 2 successes, `state = 1`, video **loops the 2 clips** (slots 7, 8, 7, 8) |
| P4 (all images fail) | ✅ done — `state = -1`, `progress = 40`, `failed_stage = materials`, `error = failed to download video materials from openai_image`, 8 calls, 15 s |
| P5 (+P6) | ✅ done — 3 identical-prompt `rate` attempts (**+5.0 s**, **+15.0 s**), term abandoned after ≈ 20 s, then `t02`-`t05` `ok`, `state = 1`, 7 calls |
| P7 (quota 429) | ✅ done — 7 calls `quota, quota, quota, ok, ok, ok, ok`: quota behaves exactly like `rate` (D7 row 7), same waits, `state = 1` |
| P8 (read timeout) | ✅ done — 5 calls `slow, ok, ok, ok, ok`, `state = 1`; the term was skipped after exactly **300 s** with **no retry** (0 `next_retry_in` lines), total wall time 390.3 s, scenes slots 2→3→4→5 |
| P9 (garbage + url) | ✅ done — 5 calls `garbage, url, ok, ok, ok`, `state = 1`, scenes slots 2→3→4→5; `garbage` skipped as undecodable, `url` fetched **exactly once** from `/img/2.png` (evidence: `P9-img-downloads.txt`) |
| P10 (string `video_terms`) | ✅ done — **120** calls, 8 terms × 15 comma fragments, every prompt 7-88 chars |
| P11 (`random` concat) | ✅ done — 4 calls, `state = 1`, scene order slots **3, 1, 2, 4** ≠ term order 1, 2, 3, 4 (measured `random.shuffle`, `video.py` L256) |
| P12 (tap, real paid call) | ⚠️ **skipped by design** (§2.5 item 6) |

⚠️ History note (tag protocol): the first P2 execution was **discarded** — the stub resets its call counter
only when its `case` tag changes, so a re-run with the same tag continues counting and `by_call` never
fires. The runner now writes a unique tag per run (`P<n>-<timestamp>`) and filters the sample rows on it;
P2 was re-run under tag `P2-20260926-195216` and every `P2-*` sample cited here is from that clean run.
The same applies to the two aborted P11 submissions of 12:56/12:58Z: the first was cut off when the runner
was interrupted (4 calls served, no meta written) and the second died when the spike instance was torn down
mid-task at 12:58:37Z. Their tags (`P11-20260926-195647`, `P11-20260926-195734`) differ from the committed
run `P11-20260926-200252` and produced no samples.

To re-run any case (the runner is `.nts/spike-0341/run_case.py`, gitignored; `NTS_MPT_KEY` comes from the
Keychain via `worker-node/bin/lib.sh`; the spike must be listening on 18080 and the stub on 18003):

```
source worker-node/bin/lib.sh
export NTS_MPT_KEY="$(secret nts.mpt.api_key)"
LOG=$(ls -t .nts/logs/mpt-spike-api-*.log | head -1)
uv run --no-project --python 3.12 .nts/spike-0341/run_case.py <CASE> --log-file "$LOG" --poll 15 --timeout 1200
uv run --no-project --python 3.12 trial/tools/redact_prompts.py --json --in "worker-node/contract/samples/images/<CASE>-task.json" --out "worker-node/contract/samples/images/<CASE>-task.json"
uv run --no-project --python 3.12 trial/tools/redact_prompts.py --in "worker-node/contract/samples/images/<CASE>-log.txt" --out "worker-node/contract/samples/images/<CASE>-log.txt"
uv run --no-project --python 3.12 .nts/spike-0341/append_sections.py  # appends only cases that have a meta and no section yet
grep -rE "Dark folklore|moonlight" worker-node/contract/samples/     # must print nothing
```
The redaction pass is now idempotent — `redact_prompts.py` refuses to hash its own markers (`MARKER_RE`), so
re-running it is a no-op; that guard was added after repeated passes were found to have corrupted the
earlier samples' markers (see the samples note above). It is still needed even though `run_case.py` redacts
at write time: a late case's log slice includes MPT's task-body dump (§2.4). Stopping the spike must be **port-scoped** —
the live 8080 MPT runs the same `main.py` command line, so a name-based kill would take down the service
these guards protect:

```
lsof -t -iTCP:18080 -sTCP:LISTEN | xargs kill     # spike MPT only
lsof -t -iTCP:18003 -sTCP:LISTEN | xargs kill     # image stub only
```

`config.toml` restoration and the post-spike `smoke.sh` run are recorded in §5.


### P1 — baseline (all `ok`)

Submit: the D4 body above, `video_terms` as a **list** of the 8 styled prompts.

| Observation | Value | Fact |
|---|---|---|
| Stub calls | **4** (`ok` ×4) | I7 ✅ |
| Call → term | call 1 = `t01` (`prompt_len` 320), 2 = `t02` (328), 3 = `t03` (325), 4 = `t04` (336) — MPT stops after 4 successes and never calls `t05`-`t08` | I7 ✅ |
| `has_auth_header` | `false` on every row (empty key list → no header) | I15 ✅ |
| Task state | `state = 1` (complete), `progress = 100`, `failed_stage`/`error` `null`, wall **150.1 s** | — |
| MPT audio duration | `audio duration: 33.02 seconds` → `ceil(33.02 ÷ 10) = 4` images needed | I7 ✅ |
| Rendered clips | 4 × `openai-image-<uuid12>.png.mp4`, each **10.000000 s** | 10 s ± 0.5 ✅ |
| Combined video | `combined-1.mp4` **33.033 s**; `final-1.mp4` 33.02 s | — |
| Scene order (colour sampled 4×/s) | `0.00-10.00 slot 1`, `10.00-20.00 slot 2`, `20.00-30.00 slot 3`, `30.00-33.03 slot 4` → **exactly `t01…t04`, in term order** | I10 ✅ |
| PNG → slot | `openai-image-f71ce9014059.png` = call 1, `…2a70ed56be3a.png` = 2, `…03ffa201e01f.png` = 3, `…8888ff253318.png` = 4 (the stub draws the call number and a per-call colour) | — |
| Task dir (I14) | `audio.mp3`, `subtitle.srt`, 4 × `openai-image-<uuid12>.png`, 4 × `openai-image-<uuid12>.png.mp4`, `combined-1.mp4`, `final-1.mp4`, `script.json` | I14 ⚠️ see below |
| Prompt in the log (I17) | yes — `generating image via openai-compatible endpoint: model=gpt-image-2, term='…', size=1536x1024` and the failure line; samples hold `term=<prompt sha256=…>` | I17 ✅ |

⚠️ **I14 wording (P0 correction):** at the pin `material_sources` is **not** a separate file in the task dir — it is a key inside `script.json` (`task_artifacts.patch_script_data(task_id, material_sources=…)`, `material.py` L128-155). It does carry `search_term` (= the prompt) per successful image, in call order, so the adapter can still map images to terms — but it means `script.json` is prompt-bearing in **two** places (`search_terms`, `material_sources[].search_term`), which matters for D12/05.4.1 cleanup and for any sample taken from a task dir.

Two consequences for plan 03, from the numbers above:
* The combined video is **audio + 0.1 s** (`_VIDEO_DURATION_SAFETY_MARGIN = 0.1`, `video.py` L79), not `4 × 10 s`: with 4 clips and 33.02 s of audio the **last scene is truncated to 3.03 s**, and `combine_videos` only loops clips when the material is *shorter* than the audio (I8). So the `needed` images of D3 are a lower bound for coverage, and a scene in the video is not necessarily 10 s long.
* MPT's own audio duration (33.02 s) differs from a direct proxy render of the same text (34.46 s, measured on 127.0.0.1:8001): ⚠️ ±1.4 s (4 %). `needed = ceil(audio ÷ 10)` therefore wobbles at a clip boundary; the D3 planner computes `NeededImages` from the script tokens (not from audio), and the relay's `image_plan.needed` is [INFERENCE] comparable only within that tolerance. [OPEN] for 03.3.1/03.2.1 if a hard equality is ever asserted.

## 4. Per-case evidence (generated from the run artefacts)

Generated by `.nts/spike-0341/append_sections.py` straight from the samples and the meta files; the case status table in §3 says which cases had finished.

### P4 — all images fail

every call moderated: task failed at materials (I9)

| Observation | Value |
|---|---|
| Stub calls | 8 — outcomes `['moderated']` (all `moderated` ×8) |
| Prompt lengths | n=8, min=320, median=328.0, max=336 (chars); the full list is in `P4-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `-1` (failed), `progress = 40`, `failed_stage = materials`, `error = failed to download video materials from openai_image` |
| Wall time | 15.0 s |
| MPT audio duration | n/a — the task failed before the video stage (the `audio duration:` line is logged by `combine_videos`, `video.py` L766) |
| Failure detail lines | ['HTTP 400: Your request was rejected by the safety system.\x1b[0m'] |
| `moderation_blocked` occurrences in the log | 0 |

Measured gap between consecutive stub calls (s): n=7, min=0.0, median=0.0, max=0.0; the 8 rows (ts included) are in `P4-stub-calls.jsonl`.

Notes: `needed = n/a` and the absent scene timeline are **expected** — the task failed at `materials` before any clip was rendered; there is no `combined-1.mp4`/`final-1.mp4` and no task-dir video to sample. The 8 calls arrive with 0 s gaps, which is MPT returning at once on a 400 and **not** retrying it (I4, I6): `moderation_blocked` never appears in the log either — MPT logs only `error.message` (`HTTP 400: Your request was rejected by the safety system.`), so the `error.code` in D5/D7 exists only in the relay's own JSONL. This case is the D7 row 6/7/8 terminal shape with `image_generation_failed` unless the report shows moderated slots.

Samples: `worker-node/contract/samples/images/P4-task.json`, `P4-stub-calls.jsonl`, `P4-log.txt`.

### P10 — `video_terms` as one comma-joined string

video_terms as one comma-joined string: MPT splits it, many short prompts (I11, F04)

| Observation | Value |
|---|---|
| Stub calls | 120 — outcomes `['moderated']` (all `moderated` ×120) |
| Prompt lengths | n=120, min=7, median=12.0, max=88 (chars); the full list is in `P10-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | string |
| Task state | `-1` (failed), `progress = 40`, `failed_stage = materials`, `error = failed to download video materials from openai_image` |
| Wall time | 15.0 s |
| MPT audio duration | n/a — the task failed before the video stage (the `audio duration:` line is logged by `combine_videos`, `video.py` L766) |
| Failure detail lines | ['HTTP 400: Your request was rejected by the safety system.\x1b[0m'] |
| `moderation_blocked` occurrences in the log | 0 |

Measured gap between consecutive stub calls (s): n=119, min=0.0, median=0.0, max=0.0; the 120 rows (ts included) are in `P10-stub-calls.jsonl`.

Structure: `prompt_len` is a 15-value cycle repeated 8× — the first fragment of each term varies (72-88 chars, it carries the keyword) and the other 14 are identical across terms — so 120 = 8 terms × 15 comma fragments, deterministically, not randomly. 112 of the 120 fragments lack the platform negative clause and would be rejected by D5's `relay_prompt_unstyled` check; the other 8 (n = 15, 30, 45, 60, 75, 90, 105, 120 — the last fragment of each 15-cycle) are byte-identical to the clause itself (`no real person likeness.`), so they **pass** the clause test and must be caught by the slot/sha256 check — `relay_prompt_unstyled` alone is not sufficient. Together the checks turn this 15×-amplified submit into a hard error instead of 120 paid images (03.4.3-T1), and 88 chars vs 320-336 for a styled term is the concrete threshold to test.

Samples: `worker-node/contract/samples/images/P10-task.json`, `P10-stub-calls.jsonl`, `P10-log.txt`.


### P5 — calls 1-3 rate (t01 retried 3x then skipped): waits 5 s + 15 s (I4); same run answers P6

Stub script: `.nts/spike-0341/cases.json` case `P5` (run tag `P5-20260926-194900`).

| Observation | Value |
|---|---|
| Stub calls | 7 — outcomes `['ok', 'rate']` as `['rate', 'rate', 'rate', 'ok', 'ok', 'ok', 'ok']` |
| Prompt lengths (chars) | n=7, min=320, median=325, max=336 — per-call rows in `P5-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 105.1 s (subtitles off, §3 preamble) |
| MPT audio duration | 34.27 s → `needed = 4` |
| Logged retries | `attempt/next_retry_in` = [('1', '5'), ('2', '15')] |

Measured gap between consecutive stub calls (s): n=6, min=0.0, median=11.0, max=15.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 4 | 10.0 |
| 10.0 | 20.0 | 5 | 10.0 |
| 20.0 | 30.0 | 6 | 10.0 |
| 30.0 | 34.25 | 7 | 4.25 |

The drawn slot number is the **stub call** number, so these four scenes are `t02`-`t05` (calls 1-3 produced no image: they were the three `rate` attempts of `t01`). The increasing 4→7 sequence is therefore *not* an order violation — it is the order of the successful calls, i.e. term order with the abandoned term removed.

Samples: `worker-node/contract/samples/images/P5-task.json`, `P5-stub-calls.jsonl`, `P5-log.txt`.

**P6 (same run, no separate case needed):** the 3 rate responses are the 3 attempts of `t01` — identical
`prompt_len` 320 and identical `prompt_sha256` on calls 1-3, then call 4 is `t02` (328) with no wait. So a
429 that exhausts the budget **skips the term** after 3 attempts and ≈ **20 s** of waiting (5 s + 15 s; the
30 s entry of the backoff table is never reached because `attempt < MAX_ATTEMPTS` fails first), and the task
continues with the next term — the D5/F06 reason the relay must own pacing rather than leaning on MPT.
Mechanism (pin `material.py` L1377-1386): the wait only happens `if retryable and attempt < OPENAI_IMAGE_MAX_ATTEMPTS`, so attempt 3 is always terminal and the `30` entry of the (5, 15, 30) table is **unreachable at MAX_ATTEMPTS = 3**; multiple keys change only whether a 401/403 attempt is retried, never the budget. Measured: call 1 `rate` 12:49:06, call 2 same prompt (`sha 2590a214…`) 12:49:11 (**+5.0 s**), call 3 same prompt 12:49:26 (**+15.0 s**), then call 4 is `t02` (`sha 80662f88…`) in the same second — 3 attempts, ≈ **20 s** per term, then the term is abandoned. That is the behavioural confirmation of I4 and it corrects 01 F24's ≈ 50 s estimate ⚠️ (cross-plan note in §2.5). The retried prompt hash is identical across all three attempts, which is what makes relay-side attribution by `prompt_sha256` stable across retries (D5).

### P2 — call 2 moderated: t02 skipped, t05 replaces, 5 calls, task still succeeds (F01)

Stub script: `.nts/spike-0341/cases.json` case `P2` (run tag `P2-20260926-195216`).

| Observation | Value |
|---|---|
| Stub calls | 5 — outcomes `['moderated', 'ok']` as `['ok', 'moderated', 'ok', 'ok', 'ok']` |
| Prompt lengths (chars) | n=5, min=320, median=328, max=336 — per-call rows in `P2-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 105.1 s (subtitles off, §3 preamble) |
| MPT audio duration | 35.28 s → `needed = 4` |

Measured gap between consecutive stub calls (s): n=4, min=0.0, median=10.0, max=11.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 1 | 10.0 |
| 10.0 | 20.0 | 3 | 10.0 |
| 20.0 | 30.0 | 4 | 10.0 |
| 30.0 | 35.25 | 5 | 5.25 |

Samples: `worker-node/contract/samples/images/P2-task.json`, `P2-stub-calls.jsonl`, `P2-log.txt`.

### P3 — calls 1-6 moderated: 8 calls, 2 clips, looped video, task state

Stub script: `.nts/spike-0341/cases.json` case `P3` (run tag `P3-20260926-195547`).

| Observation | Value |
|---|---|
| Stub calls | 8 — outcomes `['moderated', 'ok']` as `['moderated', 'moderated', 'moderated', 'moderated', 'moderated', 'moderated', 'ok', 'ok']` |
| Prompt lengths (chars) | n=8, min=320, median=328.0, max=336 — per-call rows in `P3-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 60.1 s (subtitles off, §3 preamble) |
| MPT audio duration | 34.01 s → `needed = 4` |
| Clip looping | `looped 2 clips` |

Measured gap between consecutive stub calls (s): n=7, min=0.0, median=0.0, max=10.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 7 | 10.0 |
| 10.0 | 20.0 | 8 | 10.0 |
| 20.0 | 30.0 | 7 | 10.0 |
| 30.0 | 34.0 | 8 | 4.0 |

Samples: `worker-node/contract/samples/images/P3-task.json`, `P3-stub-calls.jsonl`, `P3-log.txt`.

Reading for D7: MPT reports `state = 1` (complete) while **6 of the 8 called terms were blocked** and only
2 images exist, so `skipped = 6`, `needed = 4`, `skippedShare = 1.5 > 0.10` → this is **D7 row 4/5** (all
skipped are `moderation_blocked` → row 4, job `failed` with `image_moderated`), *not* row 2 — i.e. the
backend must overrule MPT's "success" using the relay report alone. The scene timeline is the owner-facing
half of rows 4/5: the two surviving scenes alternate for the whole 34 s (slots 7, 8, 7, 8) because
`combine_videos` loops them (I8 ✅, `looped 2 clips`), so the video is visibly broken — the argument behind
"fails a video that was rendered" in D7's note and §10 O2.

### P7 — call 1 quota: treated like a rate limit, retried 3x (D7 row 7)

Stub script: `.nts/spike-0341/cases.json` case `P7` (run tag `P7-20260926-195401`).

| Observation | Value |
|---|---|
| Stub calls | 7 — outcomes `['ok', 'quota']` as `['quota', 'quota', 'quota', 'ok', 'ok', 'ok', 'ok']` |
| Prompt lengths (chars) | n=7, min=320, median=325, max=336 — per-call rows in `P7-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 105.1 s (subtitles off, §3 preamble) |
| MPT audio duration | 35.16 s → `needed = 4` |
| Logged retries | `attempt/next_retry_in` = [('1', '5'), ('2', '15')] |

Measured gap between consecutive stub calls (s): n=6, min=0.0, median=10.5, max=15.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 4 | 10.0 |
| 10.0 | 20.0 | 5 | 10.0 |
| 20.0 | 30.0 | 6 | 10.0 |
| 30.0 | 35.25 | 7 | 5.25 |

Samples: `worker-node/contract/samples/images/P7-task.json`, `P7-stub-calls.jsonl`, `P7-log.txt`.

Reading for D7 row 7: the call pattern and the waits are **identical to P5's** (3 × 429, +5.0 s then +15.0 s, term abandoned, then `t02`-`t05` `ok`, `state = 1`), i.e. MPT keys retries on the HTTP status only (`OPENAI_IMAGE_RETRYABLE_STATUS_CODES = frozenset({429, …})`) and never reads `error.code`. The quota signal is invisible to MPT's retry logic yet *is* visible in its log text: the detail reads `HTTP 429: You exceeded your current quota, please check your plan and billing details.` (P5 shows `…Rate limit reached for images per min.`), because `_openai_image_http_failure` prints `error.message`. So D7 row 7 must be decided from the relay's structured `error_code`, while a node operator can still tell the two apart by the log message ✅.

### P9 — call 1 garbage + call 2 url: garbage term skipped (I16), url downloaded once

Stub script: `.nts/spike-0341/cases.json` case `P9` (run tag `P9-20260926-200439`).

| Observation | Value |
|---|---|
| Stub calls | 5 — outcomes `['garbage', 'ok', 'url']` as `['garbage', 'url', 'ok', 'ok', 'ok']` |
| Prompt lengths (chars) | n=5, min=320, median=328, max=336 — per-call rows in `P9-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 90.1 s (subtitles off, §3 preamble) |
| MPT audio duration | 34.25 s → `needed = 4` |

Measured gap between consecutive stub calls (s): n=4, min=1.0, median=10.0, max=11.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 2 | 10.0 |
| 10.0 | 20.0 | 3 | 10.0 |
| 20.0 | 30.0 | 4 | 10.0 |
| 30.0 | 34.25 | 5 | 4.25 |

Samples: `worker-node/contract/samples/images/P9-task.json`, `P9-stub-calls.jsonl`, `P9-log.txt`, `P9-img-downloads.txt` (stub stdout slice, prompt-free).

Reading for I16 (pin `material.py`): call 1 is a **200 whose body is not an image** — the b64 payload decodes but `Image.open` fails, so `_save_openai_image_file` raises `_OpenAIImageDecodeError` (L1418, `UnidentifiedImageError`) and `generate_images_openai` logs `openai image response is not a decodable image, skipping term: …, error=_OpenAIImageDecodeError, detail=UnidentifiedImageError: cannot identify image file …` (L1476-1479) and returns `[]` → the term is skipped, the task continues, and the run needs 5 calls instead of 4 to reach 4 successes (I7). Call 2 is a **200 with `data[0].url`** pointing at `http://127.0.0.1:18003/img/2.png`: MPT downloads it **once** — exactly one `imagedownload slot=2 bytes=8601` and one `GET /img/2.png` in the stub stdout for the whole run (only one `imagedownload` line exists in `stub.log` in total), i.e. the 3-attempt re-download loop (L1241, `OPENAI_IMAGE_MAX_DOWNLOAD_ATTEMPTS = 3`, L1118) only fires on a *failed* download and a success costs exactly one fetch. The scene timeline confirms the call→scene mapping: call 1 produced no clip, so the four scenes are slots 2, 3, 4, 5, in that order (no shuffle: `match_materials_to_script = true`).

### P11 — video_concat_mode=random, match_materials_to_script=false: scene order differs from term order (I10, F02)

Stub script: `.nts/spike-0341/cases.json` case `P11` (run tag `P11-20260926-200252`).

| Observation | Value |
|---|---|
| Stub calls | 4 — outcomes `['ok']` as `['ok', 'ok', 'ok', 'ok']` |
| Prompt lengths (chars) | n=4, min=320, median=326.5, max=336 — per-call rows in `P11-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 90.1 s (subtitles off, §3 preamble) |
| MPT audio duration | 34.46 s → `needed = 4` |

Measured gap between consecutive stub calls (s): n=3, min=11.0, median=11.0, max=11.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 3 | 10.0 |
| 10.0 | 20.0 | 1 | 10.0 |
| 20.0 | 30.0 | 2 | 10.0 |
| 30.0 | 34.5 | 4 | 4.5 |

Samples: `worker-node/contract/samples/images/P11-task.json`, `P11-stub-calls.jsonl`, `P11-log.txt`.

Reading for I10/F02 (pin `video.py`): the four calls still ran in term order `t01`-`t04` (the on-demand loop, I7, is not affected by concat mode — `prompt_len` 320, 328, 325, 336 = the first four styled terms, in list order), but the rendered video's scene order is **3, 1, 2, 4**, not `1, 2, 3, 4` — the measured `random.shuffle(primary_items)` (L256; each generated clip is its own source, so all four land in `primary_items`) versus the order-kept run §P1 (slots 1→2→3→4). The submit used `video_concat_mode = "random"` with `match_materials_to_script = false`; at the pin that flag is what would otherwise force sequential (`task.py` L906-907). So F02's shuffle is now **measured**, not merely source-proven. A genuine shuffle lands term order with probability 1/24 — the single permutation 3,1,2,4 observed here is the expected reading; the contract only needs "order is not guaranteed to be term order".

### P8 — call 1 slow (320 s > the 300 s read timeout): not retried, t01 skipped (I5)

Stub script: `.nts/spike-0341/cases.json` case `P8` (run tag `P8-20260926-200622`).

| Observation | Value |
|---|---|
| Stub calls | 5 — outcomes `['ok', 'slow']` as `['slow', 'ok', 'ok', 'ok', 'ok']` |
| Prompt lengths (chars) | n=5, min=320, median=328, max=336 — per-call rows in `P8-stub-calls.jsonl` |
| `has_auth_header` | `[False]` |
| Submit `video_terms` form | list |
| Task state | `1` (complete), `progress = 100`, `failed_stage = None`, `error = None` |
| Wall time | 390.3 s (subtitles off, §3 preamble) |
| MPT audio duration | 33.58 s → `needed = 4` |

Measured gap between consecutive stub calls (s): n=4, min=10.0, median=10.5, max=300.0.

Scene timeline of the combined video (dominant colour per 0.25 s):

| t_start | t_end | stub call (slot) | length s |
|---|---|---|---|
| 0.0 | 10.0 | 2 | 10.0 |
| 10.0 | 20.0 | 3 | 10.0 |
| 20.0 | 30.0 | 4 | 10.0 |
| 30.0 | 33.5 | 5 | 3.5 |

Samples: `worker-node/contract/samples/images/P8-task.json`, `P8-stub-calls.jsonl`, `P8-log.txt`.

Reading for I5 (pin `material.py`): call 1 is the `slow` outcome (the stub writes its call row, then sleeps
320 s before answering). MPT gives up first: the gap between call 1 and call 2 is **exactly 300.0 s**, the
read timeout of `OPENAI_IMAGE_REQUEST_TIMEOUT = (30, 300)` (L1116), and the log holds
`openai image generation failed: …, detail=unconfirmed request error (no retry to avoid double billing):
ReadTimeout, detail=HTTPConnectionPool(host='127.0.0.1', port=18003): Read timed out. (read timeout=300)`
(`material.py` L1464-1467). That wording comes from the generic `except Exception` branch (L1351-1357),
which deliberately leaves `retryable = False` (only `requests.exceptions.ConnectTimeout`, L1344-1350, sets
it) — so the retry gate at L1377 is never consulted and the term is skipped at once: the run contains **zero**
`attempt=/3, next_retry_in` lines (contrast §P5/§P7, where the identical line appears twice for a 429). MPT
then logs the next term in the same second (call 2, `t02`). Consequences: (a) a relay hold longer than 300 s
loses the term *and* the image the relay already billed, and no relay-side retry can help because MPT never
re-asks; (b) the total task wall time jumps from ≈ 90 s (§P9/§P11, same body) to **390.3 s** — ≈ +300 s of
dead wait for one skipped term; (c) the clip loss is silent in the task state (`state = 1`) and visible only
in the scene timeline, where call 1 is absent and the four scenes are slots 2, 3, 4, 5.

## 5. Verification after the spike

* Teardown was **port-scoped** (`lsof -t -iTCP:18080 -sTCP:LISTEN | xargs kill`, then 18003); the live 8080
  MPT and the 8001 proxy were never stopped (all four D4 listeners still up afterwards). The spike wrapper
  exits non-zero when its port holder is killed (`wait "$SRV"` → rc 1), which is expected; the `EXIT` trap
  still ran `restore_config`.
* `config.toml` restored byte-identically: sha256
  `c5ead73b3b0f9bbcfdf5b5396f88ddb315dfa740b047aa2d3351425cee85ad9d` equals the sha256 of the copy taken
  before the swap; `config.toml.pre-spike` and `config.spike.toml` are both absent and the restored file is
  back to `openai_image_base_url = "http://127.0.0.1:8002/v1"` (line 21) ✅.
* Both profile guards hold, measured in both directions: (a) with the swap in place, `start-mpt-api.sh
  --profile normal` exits **2** ("refusing to start the normal profile: … config.toml.pre-spike exists");
  (b) on the restored normal file, `render-mpt-config.py --check --profile spike` exits **1** with
  `FAIL listen_port`, `FAIL openai_base_url`, `FAIL openai_image_base_url`, `FAIL openai_image_api_keys`,
  while `--profile normal` on the same file exits **0**.
* `worker-node/bin/smoke.sh` → **exit 0**, run *after* the stop and the restore: every child smoke green,
  `check-binds.sh` PASS (`check-binds.sh` only walks the D4 ports 8000/8001/8002/8080 and their process
  trees, so the spike ports 18003/18080 are invisible to it by design), `git.hooks` PASS.
* Self-tests re-run this session, all three exit 0: `trial/tools/image_stub.py --self-test` **40 PASS /
  0 FAIL**; `trial/tools/redact_prompts.py --self-test` **27 PASS / 0 FAIL** (four of them new idempotency
  checks); `worker-node/bin/render-mpt-config.py --self-test` exits 0 while printing intermediate `FAIL …`
  lines, because its negative cases run the guards on deliberately bad configs (`missing variable
  NTS_PROXY_API_KEY`, `listen_host: expected "127.0.0.1"`, `openai_image_api_keys: expected exactly 1
  non-empty`, …) — expected output, not test failures. The counts previously recorded here (48/24/15) were
  not reproduced and are superseded.
* Tool fix (genuine bug, see the samples note in §3): `trial/tools/redact_prompts.py` re-hashed its own
  markers, so a repeated `--json`/text pass published `sha256(marker)` instead of `sha256(value)` and the
  file was not idempotent. `marker()` now returns an existing marker unchanged (`MARKER_RE`,
  `trial/tools/redact_prompts.py:57-62`); both modes are idempotent (verified: two further passes over the
  committed P8/P9/P11 samples leave their sha256 unchanged) and the P8/P9/P11 `task.json` markers were reset
  to the single-pass value (`data.terms[i]` = marker of fixture term *i*, `data.script` = marker of the
  fixture story).
* Live services: the 8080 MPT instance and the 8001/8002 listeners were never stopped (the spike ran beside
  them on 18080/18003); at the end of the session 18080 and 18003 are gone as intended, and the normal
  `smoke.sh` above exercised 8000/8001/8002/8080.
* Cost: **no real OpenAI call** — every image request went to the local stub: all 173 rows across the 10
  `samples/images/P*-stub-calls.jsonl` files carry `has_auth_header = false` and a locally computed
  `prompt_sha256` (verified row-by-row this session), and the spike profile pointed both `openai_base_url`
  and `openai_image_base_url` at the stub, so even a stray LLM call would have 404ed locally. The usage tap
  was **not** usable as corroboration here: sibling agents were running their own harnesses concurrently and
  `trial/runs/` gained run directories in the window (`20260926-1301-vi-landscape-01`,
  `20260926-1307-en-landscape-01`) that this spike did not create — they prove nothing either way, so the
  stub-side evidence above is the whole argument. P12 stays [OPEN].
