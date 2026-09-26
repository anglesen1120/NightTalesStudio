# 01.3.1 + 01.3.2 — Measurement sheet, usage tap, run collector, RAM sampler (feature plan)

> Refines `../../../01-environment-setup.md` §3 FR-01-3, §4 Epic 01.3 (stories 01.3.1, 01.3.2), §5.3. Contract: `../README.md` (facts §1, findings F02/F03/F04/F11/F20/F22, decisions D1–D12, CR-1/CR-3/CR-4). Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs (README §1 id) · ⚠️ unverified, confirm at the pinned commit/install · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Documentation only; no script exists yet.

## 1. Scope

| Story | Title | In scope | Out of scope |
|---|---|---|---|
| 01.3.1 | Measurement sheet | Schema v2 (`trial/measurements.csv`, `trial/schema.md`, `trial/prices.json`); usage tap (D6); run start/finish collector that turns one MPT task into one complete CSV row; calibration on the wiring render (01.2.2-T6) | The trial renders themselves (05.5.2); image quality decision (CR-3 → 03); production metering (CR-4 → 03/11/12); VieNeu proxy code and its log (04, CR-1) |
| 01.3.2 | Memory profiling | 1 s process-tree RSS sampler for VieNeu (8000), proxy (8001), MPT API (8080), MPT WebUI (8501); swap; memory pressure; attribution to MPT stage windows | Per-allocation profiling; `footprint`-based private-memory accounting (⚠️ noted as alternative only); GPU node measurement (plan 11) |

Consumers of the numbers:

| Consumer | Needs | Column(s) |
|---|---|---|
| 02 (G2) | narration speed for length targets | `words_per_minute_measured`, `words_in_story`, `language` |
| 03.3.1 | output tokens and cost per image per aspect/quality | `image_tokens_per_image_avg`, `image_cost_per_image_usd`, `video_aspect`, `image_quality`, `image_quality_returned`, `image_size` |
| 12.1.1 | cost per operation; local compute seconds per finished minute | `story_cost_usd`, `image_cost_usd`, `usd_per_output_minute`, `local_compute_seconds_per_output_minute`, `output_duration_sec` |
| 11 / 01.3.3 | what can run together on 16 GB; Mac baseline (R4) | `ram_peak_*_mb`, `ram_peak_total_mb`, `swap_peak_mb`, `memory_pressure_max`, `trial/runs/<id>/rss.csv` |

## 2. Confirmed facts

| Id | Fact used here | Effect on this feature |
|---|---|---|
| ✅ M4 | `openai_image` sends `{model, prompt, n: 1, size}` only; timeout (30, 300) s; retries 429/500/502/503/504 3× (5/15/30 s); read timeouts not retried; failure logs `openai image generation failed` and skips the keyword | Tap timeout MUST be ≥ 300 s (else the tap turns a slow success into 502 → MPT retries → double billing); collector counts skip lines; `image_quality_requested = omitted` by default |
| ✅ M5 | LLM `openai` = `chat.completions.create`, not streaming; MPT retries 5× | Tap may buffer responses; story usage summed over all chat calls incl. retries |
| ✅ M9 | loguru line `YYYY-MM-DD HH:MM:SS \| LEVEL \| "file:line": func - msg`; markers `## generating video script`, `## generating video terms`, `## generating audio`, `## generating subtitle`, `## downloading videos from <source>`, `## combining video`, `## generating video`, `task <id> finished` / `task failed … stage:`; **end-of-audio marker ⚠️** | Stage windows = marker to next marker; markers start with `\n\n` so they sit on **continuation lines** without a timestamp (MptFacts: message `"\n\n## generating video script"`) → parser attaches continuation lines to the preceding header; audio window ends at `## generating subtitle` until T4 confirms an end-of-audio line |
| ✅ M3 | whisper runs inside the MPT process; logs `detected language: '<l>', probability: <p>` and `complete, elapsed: <s> s` | `subtitle_detected_language`; RAM attribution by window, not by process |
| ✅ O6 | image `quality` default `auto`; response echoes resolved quality; `usage` = `input_tokens`, `input_tokens_details{text_tokens,image_tokens}`, `output_tokens`, `total_tokens`, optional `output_tokens_details`; **may be absent** | Tap copies `usage` as-is (numbers only); collector marks cost `n/a:usage-absent` if any successful image call lacks it |
| ✅ O7 | Usage/Costs APIs need an Admin key; costs only 1-day buckets | Per-run numbers come from the tap (F02); dashboard used only for a next-day cross-check (`dashboard_cost_usd_check`) |
| ✅ O8 | `gpt-image-2.5-flare`: text in $5, image in $8, image out $30 per 1M; Tier 1 = 5 images/min. ⚠️ priced on `gpt-image-2.5-flare` (superseded by `gpt-image-2`, 01 DEC-11); relay price/rate [OPEN]. `gpt-6-luna`: $0.10 in / $0.50 out per 1M; cached $0.01 (OpenAiControlsFacts O8) | `trial/prices.json`; 429s expected on image bursts → `image_http_429_count` |
| ✅ M1 | `openai_base_url`, `openai_image_base_url` are `[app]` keys | MPT points both at `http://127.0.0.1:8002/v1` (D6, 01.2.2-T2; confirmed with MptPlan) |
| ✅ M8/M11 | config at MPT root; empty `api_key` = auth off | Collector hashes the rendered config with secret values redacted; never copies it |
| ✅ E1 | `/Volumes/Project` unencrypted, ownership disabled | Media, script text and raw samples stay in `$NTS_HOME/runs/<id>/` (D1); tracked run files hold numbers only |
| Peer contract (BaseToolingPlan) | `worker-node/bin/env-versions.sh` prints one JSON object (schema_version 1: `captured_at, macos_version, macos_build, hw_model, cpu_brand, memory_bytes, git, uv, ffmpeg, gitleaks, ffmpeg_encoders[], uv_pythons[], mpt_commit, vieneu_commit, nts_home_on_internal_disk`), no paths/user/secrets; exit 2 only if git/uv/ffmpeg missing | Embedded as `run.json.env`; `machine` column built from it |
| Peer contract (MptPlan) | MPT logs tee'd to `$NTS_HOME/logs/mpt-api-<YYYYmmdd-HHMMSS>.log`; `log_level = "INFO"`; wiring render submits via API with `video_count = 1` | Default log discovery; INFO keeps all markers (`info`/`success` levels) [INFERENCE] |

Open ⚠️ (closed in T4 unless stated):

| # | Item | Default until confirmed | T4 outcome (2026-09-26, run `20260926-0818-en-landscape-01`) |
|---|---|---|---|
| Q1 | End-of-audio log line (in `app/services/voice.py`, not read) | Audio window ends at `## generating subtitle` | **changed / closed**: no provider-independent line exists (`voice.py:2212`, `voice.py:1357` are provider-specific and absent on `custom_audio_file`); `AUDIO_END_RE` stays `None`; window `## generating audio` → `## generating subtitle` = 6.0 s (01 CR-18) |
| Q2 | ✅ Closed (01 CR-17): `final-{index}.mp4` (`task.py:968`), `audio.mp3` (`task.py:532`), `script.json` key `script` (`task.py:357`) at the pin | Defaults; overridable with `--output`, `--script` | ✅ **confirmed**: `final-1.mp4`, `audio.mp3`, `script.json`, `subtitle.srt`; the defaults worked with no `--output`/`--script` |
| Q3 | Chat Completions usage field names for cached tokens (`usage.prompt_tokens_details.cached_tokens`) | Missing → `story_cached_input_tokens = 0` + issue `cached-tokens-field-absent` | ✅ **confirmed (field absent)**: chat usage = `{prompt_tokens 319, completion_tokens 35, total_tokens 354}` with no `prompt_tokens_details` anywhere → `story_cached_input_tokens = 0` + issue; no code change |
| Q4 | Images response top-level `quality`/`size` echo present for flare | Missing → `image_quality_returned = n/a:not-echoed` | **changed**: echoed, but the relay forces quality `auto` **per call** (`low:3\|medium:2` in one run, `image_quality = low`), and one call echoed `1370x1148` ≠ requested `1536x1024` → `image_size = 1370x1148+1536x1024`; `n/a:not-echoed` does not apply (01 CR-19 → 03 CR-3) |
| Q5 | Non-200 image calls are not billed | [INFERENCE]; verified by the dashboard cross-check | ⚠️ **still open** (owner-blocked): 0 non-200 lines (0×429, 0×5xx, 0 moderation) — needs the DEC-9 dashboard |
| Q6 | `memory_pressure` output line `System-wide memory free percentage: NN%` and `sysctl -n kern.memorystatus_vm_pressure_level` (1/2/4) exist on macOS 27 without sudo | 01.3.2-T1 step 1 checks; absent → `n/a:pressure-unavailable` | n/a here (01.3.2-T1): the sampler's sources worked — `pressure_level`/`free_pct` parsed, `memory_pressure_max = warn` |
| Q7 | `ps -o rss` unit is KiB on macOS | 01.3.2-T1 checks against Activity Monitor once | n/a here (01.3.2-T1). ⚠️ **Numbering note:** `notes.md` / `.nts/wiring-calib.json` label the **SDK timeout** as Q7 instead — that one is **confirmed** (openai 2.24.0 read 600 s ≤ `TAP_UPSTREAM_TIMEOUT_S` 610, `llm.py:618`; T4 step 7) |
| Q8 | ✅ Resolved by 04: path `$NTS_HOME/logs/proxy-<YYYYmmdd-HHMMSS>.log`, `ts` = request arrival, `status` = logical outcome (04 D7); no code change (04 CR-7) | Closed; fallback (ffprobe of the MPT task audio file) kept for a missing log | ✅ **confirmed**: proxy line `01:19:00.398Z` (voice `Ngọc Huyền`, chars 557, audio_seconds 31.8, vieneu_ms 6001, ffmpeg_ms 4, status 200) inside the audio window → `sources.tts_seconds_audio = proxy-log`; no ffprobe fallback (01 CR-20) |
| Q9 | With `story_source = plan02` (script supplied as `video_script`) MPT skips the script LLM call and possibly the `## generating video script` marker; with `video_terms` omitted the terms call still runs (01.2.2-T6, agreed with MptPlan) | Story columns hold the terms call(s) only; 0 chat lines → `n/a:story-source-plan02`; every plan02 row gets issue `story-cost-terms-only`; missing script marker tolerated | ✅ **confirmed**: `## generating video script` **is** logged (`task.py:293`) while its LLM call is skipped; the terms call runs (5 terms, `ast.literal_eval`) → 1 chat call, `story_wall_seconds = 3.0`, issue `story-cost-terms-only`; no missing-marker tolerance needed (01 CR-18) |
| Q10 | Dashboard cost day boundary is UTC | [INFERENCE from O7 1-day buckets]; noted in `notes.md` | ⚠️ **still open** (owner-blocked): the relay has no OpenAI dashboard (DEC-9) → `n/a:pending-dashboard`; the UTC boundary stays [INFERENCE] |

## 3. Domain and data

No database. Tracked files under `trial/` (D1), untracked under `$NTS_HOME` (D1).

| File | Tracked | Writer | Content / schema |
|---|---|---|---|
| `trial/measurements.csv` | ✅ | `finish_run.py` only (D8) | Header = 58 columns (31 from 01 §5.3 in order + 27 appended, schema v2); one row per run; RFC 4180, UTF-8, CRLF, `.` decimals, no blank cells (`n/a:<reason>`) |
| `trial/schema.md` | ✅ | 01.3.1-T1 | Column dictionary (name, type, unit, source, formula, n/a rules) = the table in `01.3.1-T1-schema-v2-dictionary-prices.md`; word-count rule; price formulas |
| `trial/prices.json` | ✅ | 01.3.1-T1, re-checked before each trial | `checked_at`, `currency`, `unit`, per-model rates, source URLs |
| `trial/runs/<run_id>/run.json` | ✅ | `run_manifest.py` (start), `finish_run.py` (finish fields) | Pins, heads, `env`, redacted config digest, run parameters, tap settings, sources used |
| `trial/runs/<run_id>/openai_usage.jsonl` | ✅ | `usage_tap.py` | One line per upstream call, fields exactly D6 |
| `trial/runs/<run_id>/stages.json` | ✅ | `finish_run.py` | Stage windows (UTC), counts parsed from the MPT log, marker set version |
| `trial/runs/<run_id>/rss.csv` | ✅ | `rss_sampler.sh` (row `all`), `finish_run.py` (per-stage rows) | Peaks per stage and root |
| `trial/runs/<run_id>/ffprobe.json` | ✅ | `finish_run.py` | `ffprobe` JSON of the final video, `format.filename` reduced to basename |
| `trial/runs/<run_id>/notes.md` | ✅ | operator | D11 conditions, anomalies, dashboard comparison |
| `trial/runs/no-run/openai_usage.jsonl` | ❌ (git-ignored, agreed with 01.1.2-T1) | `usage_tap.py` | Calls made while no run is active |
| `$NTS_HOME/current_run` | ❌ | `start_run.sh` writes, `finish_run.py` clears | One line: active `run_id` |
| `$NTS_HOME/tap.json` | ❌ | `start-tap.sh` | `{"started_at", "port": 8002, "upstream": "<origin of OPENAI_BASE_URL>" (01 CR-15), "image_quality_injection": null\|"<q>", "image_api": "responses" (01 DEC-11), "timeout_s": 610}` — copied into `run.json.tap` |
| `$NTS_HOME/runs/<run_id>/rss_raw.csv`, `rss_sampler.pid`, `rss_sampler.log` | ❌ | `rss_sampler.sh` | 1 s samples |
| `$NTS_HOME/runs/<run_id>/final-1.mp4`, `script.txt` | ❌ | `finish_run.py` (copies) | Media and story text (content, not tracked) |
| `$NTS_HOME/logs/tap-<YYYYmmdd-HHMMSS>.log` | ❌ | `start-tap.sh` (tee) | One line per request: ts, method, endpoint, status, latency; no bodies/headers |

`run.json` shape (schema_version 1):

```json
{
  "schema_version": 1,
  "run_id": "20260925-1030-vi-landscape-01",
  "run_status": "calibration|trial|failed",
  "started_at": "2026-09-25T03:30:12Z", "finished_at": null,
  "lang": "vi", "aspect": "landscape", "topic": "…", "target_minutes": 5,
  "voice_name": "kokoro:<voice>|n/a:not-given", "story_source": "mpt|plan02",
  "pins": { "MPT_COMMIT": "…40 hex…", "VIENEU_COMMIT": "…", "VIENEU_MODEL_REVISION": "…", "VIENEU_CODEC_REVISION": "…", "WHISPER_MODEL_REVISION": "…", "MPT_PYTHON": "3.11", "VIENEU_PYTHON": "3.12" },
  "heads": { "mpt": "…", "vieneu": "…" },
  "env": { "…": "worker-node/bin/env-versions.sh output verbatim" },
  "config": {
    "path": "$NTS_HOME/src/mpt/config.toml",
    "sha256_redacted": "…", "redacted_keys": ["app.api_key", "app.openai_api_key", "app.openai_image_api_keys", "kokoro.api_key"],
    "template_sha256": "…",
    "settings": { "openai_base_url": "http://127.0.0.1:8002/v1", "openai_image_base_url": "http://127.0.0.1:8002/v1", "openai_model_name": "gpt-6-luna", "openai_image_model": "gpt-image-2", "openai_image_size": "", "whisper.model_size": "large-v3-turbo", "whisper.device": "cpu", "whisper.compute_type": "int8", "max_concurrent_tasks": 1, "log_level": "INFO" }
  },
  "tap": { "port": 8002, "image_quality_injection": null, "image_api": "responses", "timeout_s": 610 },
  "vieneu": { "VIENEU_PRECISION": "fp32", "VIENEU_MAX_STREAMS": "1" },
  "sampler": { "interval_s": 1, "roots": { "vieneu": 8000, "proxy": 8001, "mpt": 8080, "webui": 8501 } },
  "mpt_task_id": null,
  "sources": {}
}
```

Paths are stored with a literal `$NTS_HOME` prefix (no user name in tracked files). `sources` (filled at finish) records which source produced each derived number, e.g. `{"tts_seconds_audio": "proxy-log|mpt-audio-ffprobe|n/a", "mpt_log": "$NTS_HOME/logs/mpt-api-….log"}`.

Invariants:
- I1 `measurements.csv` header equals `COLUMNS` in `finish_run.py`; first 31 names equal 01 §5.3 in order.
- I2 One row per `run_id`; appended once; only `dashboard_cost_usd_check` may be rewritten later, and only from `n/a:pending-dashboard`.
- I3 At most one active run (`current_run`), matching D11 one heavy job.
- I4 No tracked file contains a secret, a header, a prompt, story text, image bytes, a user name or an absolute home path.
- I5 Every n/a reason matches `^n/a:[a-z0-9-]+$`.

## 4. Use cases (operator procedures)

| # | Procedure | Command | Result | Errors |
|---|---|---|---|---|
| U1 | Start the tap (terminal of its own, D9) | `trial/tools/start-tap.sh` (env `TAP_IMAGE_QUALITY` unset) | Listens `127.0.0.1:8002`; writes `$NTS_HOME/tap.json` | exit 2 port busy / bad env |
| U2 | Start a run | `trial/tools/start_run.sh <lang> <aspect> "<topic>" <target_minutes> [--status calibration\|trial] [--voice <name>] [--story-source mpt\|plan02] [--no-tap]` | Prints `run_id` on stdout; `run.json`; `current_run`; sampler running | exit 2 run active, service down, bad args; exit 1 head ≠ pin, or config base URLs not the tap (default) / still the tap (`--no-tap`) |
| U3 | Render | 01.2.2-T6 / 05.5.2 submit to MPT API (`video_count = 1`) | MPT task id | — |
| U4 | Finish a run | `uv run --no-project --python 3.12 trial/tools/finish_run.py <run_id> --task-id <mpt task id> [--mpt-log P] [--proxy-log P] [--script P] [--output P] [--issue "<text>"]… [--status failed] [--dry-run]` | `stages.json`, `ffprobe.json`, `rss.csv` stage rows, one CSV row, `current_run` cleared | exit 1 validation (no row written); exit 2 usage/env |
| U5 | Abort a run (no row) | `… finish_run.py <run_id> --abort` | Sampler stopped, `current_run` cleared, `run.json.run_status = "failed"`, `notes.md` gets "aborted" | — |
| U6 | Dashboard cross-check (next day) | `… finish_run.py <run_id> --dashboard-cost <usd> --dashboard-day <YYYY-MM-DD>` | Rewrites that one cell; prints tap-day total vs dashboard and delta % | exit 1 cell not pending / delta > 5 % (value still written, issue printed) |
| U7 | Sampler self-test | `trial/tools/rss_sampler.sh --selftest 60` | Overhead report; raw file under `$NTS_HOME/runs/sampler-selftest-<ts>/` | — |
| U8 | Parser tests | `uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests -v` | All green | — |

Authorization: local operator only; all listeners on `127.0.0.1` (D4). Idempotency: U2 refuses while a run is active; U4 refuses a `run_id` already in the CSV; U6 only replaces a pending cell.

## 5. API and events

Usage tap HTTP surface (`127.0.0.1:8002`, D4/D6; 01 DEC-11/CR-16: with `OPENAI_IMAGE_API=responses` the tap translates `/v1/images/generations` into `POST /v1/responses` with an `image_generation` tool (host `OPENAI_IMAGE_HOST_MODEL`, `reasoning.effort = OPENAI_EFFORT_IMAGE_HOST`), maps the completed `image_generation_call.result` back to `data[0].b64_json`, answers 502 `tap_image_missing` when a 200 has no image, rejects `n > 1` with 400 `tap_image_n_unsupported`, and injects `OPENAI_EFFORT_CHAT` into chat/responses bodies lacking an effort field; requested model/size/quality advisory on the relay — echo `gpt-image-2-codex`/`auto`, codex#28723 ✅):

| Request | Behaviour | Status |
|---|---|---|
| `POST /v1/*`, `GET /v1/*` | Forward to `$OPENAI_BASE_URL/*` (01 CR-15) with body and headers (minus hop-by-hop), `Authorization` passed through, `Accept-Encoding: identity` | Upstream status/body unchanged |
| Other methods | Not forwarded | 405 `{"error":{"type":"tap_error","code":"tap_method_not_allowed"}}` |
| Path not under `/v1/` | Not forwarded | 404 `…"code":"tap_path_not_allowed"` |
| Image call with `n > 1` (`responses` mode) | Not forwarded | 400 `tap_image_n_unsupported` |
| `Content-Length` > `TAP_MAX_BODY_BYTES` (1 MiB) or chunked request | Not forwarded | 413 `tap_body_too_large` / 411 `tap_length_required` |
| Upstream connect/TLS/timeout error | Logged | 502 `tap_upstream_connect` / `tap_upstream_tls` / `tap_upstream_timeout` |
| Upstream response > `TAP_MAX_RESPONSE_BYTES` (64 MiB) | Logged | 502 `tap_upstream_too_large` |

JSONL line (D6 fields exactly; example, synthetic numbers):

```json
{"ts":"2026-09-25T03:31:02.114Z","run_id":"20260925-1030-vi-landscape-01","endpoint":"/images/generations","model":"gpt-image-2","http_status":200,"error_code":null,"latency_ms":41230,"request":{"size":"1536x1024","quality":null,"moderation":null,"n":1,"reasoning_effort":"low"},"via":"responses","response":{"quality":"medium","size":"1536x1024"},"usage":{"input_tokens":61,"input_tokens_details":{"text_tokens":61,"image_tokens":0},"output_tokens":1056,"total_tokens":1117}}
```

CLI contracts (D9 exit codes 0/1/2; `--help` on all): see §4 and task files. `start_run.sh` args agreed with MptPlan (01.2.2-T6). No events/outbox.

## 6. UI

N/A — CLI and files only; no WebUI changes.

## 7. Jobs and integrations

| Integration | Direction | Timeouts / retries | Failure mapping |
|---|---|---|---|
| OpenAI via tap | MPT → tap → `${OPENAI_BASE_URL%/v1}` from `pins.env` (today `https://api.aixingialaire.shop`, 01 CR-15) | Tap: connect 30 s, total read `TAP_UPSTREAM_TIMEOUT_S = 610` (≥ MPT image 300 s ✅ M4 and ≥ OpenAI SDK default ⚠️); **no retries in the tap** (MPT retries, M4/M5) | Upstream status returned unchanged; tap-side errors → 502 `tap_upstream_*` (MPT retries 502 for images, M4 — a timeout after the request reached OpenAI could double-bill; logged, visible in JSONL) |
| MPT log | file → collector | — | Missing marker → stage `n/a:marker-missing`; missing end marker → exit 1 unless `--status failed` |
| Proxy log (CR-1, 04) | file → collector | — | Missing/no `ts` → fallback ffprobe of the task audio; else `n/a:no-proxy-log` |
| RSS sampler | background process started by `start_run.sh` | 1 s loop; lsof root refresh every 10 s; pressure every 5 s | Service down → zeros + `*_pid` empty; stopped by SIGTERM, by `current_run` change, or `--duration` |
| ffprobe | collector → local binary | — | Missing output → exit 1 unless `--status failed` |
| OpenAI dashboard | manual | next day | Delta > 5 % → issue in `notes.md`, investigate before 05.5.2 prices are used |

## 8. Security and privacy

| Threat | Control |
|---|---|
| Tap sees API keys in transit | Keys pass through in the `Authorization` header only; the tap holds no key, never stores or logs headers; exceptions logged as type name only (no `repr` of request); no `set -x` in `start-tap.sh` |
| Tap as an open proxy / SSRF | Bound to `127.0.0.1` only; upstream fixed to `${OPENAI_BASE_URL%/v1}` from `pins.env` (today `https://api.aixingialaire.shop`, 01 CR-15); `TAP_UPSTREAM` override accepted only for `http://127.0.0.1:<port>` (tests); only `/v1/` paths; no `CONNECT`; TLS verified with `ssl.create_default_context()` and **no** option to disable it |
| Local process uses the tap | It must bring its own key; the tap adds no privilege [INFERENCE]; every call is logged to the active run |
| Prompts/content leak into git | JSONL keeps only D6 fields; request parsing extracts `model, size, quality, moderation, n, reasoning_effort`; response parsing extracts `quality, size, usage` (numeric leaves only) and `error.code`; no prompt, messages, b64, URLs |
| Secrets in run manifest | `run_manifest.py` redacts values of keys matching `(^|_)(api_key|api_keys|key|secret|token|password)$` before hashing; stores key names only; config itself is never copied |
| Story text / media on the unencrypted USB volume (E1) | Script text and video copies go to `$NTS_HOME/runs/<id>/` (FileVault disk); CSV holds the owner-entered `topic` only — owner keeps topics free of personal data (true-crime names) |
| User name / paths in tracked files | Paths written with literal `$NTS_HOME`; `ffprobe.json` filename reduced to basename; `env` block contains no paths (BaseTooling contract) |
| Accidental commit of secrets | gitleaks hook (01.1.2-T4) runs on every commit of `trial/runs/**` |
| Retention | Tracked run files kept as the M0 record; `$NTS_HOME/runs/<id>/` deleted after 01-closed (`rm -rf` by the owner, noted in `CARRY-OVER.md`); `no-run` log ignored by git; trial keys revoked at M0 close (D7) |

## 9. Test plan

| # | Level | Behaviour / boundary | How |
|---|---|---|---|
| T-P1 | unit (permanent) | MPT log parser: header + continuation lines, ANSI stripped, local → UTC, all 7 markers + `task <id> finished`, windows contiguous, total = end − first marker | `trial/tools/tests/test_finish_run.py` with synthetic `fixtures/mpt-api-sample.log` |
| T-P2 | unit | Failure path: `task failed, task_id: X, stage: subtitle` → `run_status = failed`, later-stage columns `n/a:run-failed-at-subtitle` | fixture `mpt-api-failed.log` |
| T-P3 | unit | Other task's lines before `started_at` and a second task after end are ignored; wrong task id → exit 1 | fixture |
| T-P4 | unit | Image counts: 200s, 429s, `moderation_blocked`, `openai image generation failed` lines; usage absent on 1 of N → `image_cost_usd = n/a:usage-absent`, `image_count` still numeric | `fixtures/openai_usage-sample.jsonl` |
| T-P5 | unit | Cost formulas vs hand-computed values (6 decimals), snapshot model name `gpt-image-2-2026-09-08` matched by prefix (the flare-era snapshot name superseded, 01 DEC-11); unknown model → `n/a:no-price` | `fixtures/prices-test.json` |
| T-P6 | unit | Word count: vi `"Đêm ấy, trời mưa… rất to!"` → 6; en `"It's late—very late."` → 4 (`It's` one token, em dash splits); `"— 1990 —"` → 1 (digits count, dash-only tokens dropped); WPM = words / (seconds/60) | inline cases |
| T-P7 | unit | Stage RAM attribution: window boundaries inclusive of start, exclusive of end; window with 0 samples → `n/a:no-samples-in-window` | `fixtures/rss_raw-sample.csv` |
| T-P8 | unit | Row validation: blank cell, bad n/a reason, header drift → exit 1, CSV unchanged (byte-compare) | temp dir |
| T-P9 | unit | Dashboard update rewrites only the target cell; refuses non-pending cell | temp CSV |
| T-P10 | unit | Story-side branches: chat-only JSONL + images, `story_source = plan02` → story cols numeric + issue `story-cost-terms-only`; image-only JSONL + plan02 → cols 8–11 `n/a:story-source-plan02`, col 55 numeric (story = 0) **and** issue `story-cost-terms-only`; image-only JSONL + `mpt` → `n/a:no-llm-calls`, col 55 `n/a:component-na`; `--no-tap` → cols 8–18 and 39–48 `n/a:no-tap`; unknown collector issue code → validation exit 1 | `fixtures/openai_usage-chat-only.jsonl`, `fixtures/openai_usage-image-only.jsonl` |
| T-T1 | acceptance (throwaway) | Tap against a local stub upstream: passthrough of 200/429/500, one JSONL line per call, no `Authorization`/key/prompt/b64 in log, `run_id` from `current_run` else `no-run`, 413/404/405, timeout → 502, injection off by default and on with `TAP_IMAGE_QUALITY` | 01.3.1-T2 |
| T-S1 | acceptance | Sampler: roots found by port, tree sum includes a child process (`sleep` child test), swap parsed, overhead < 2 % of one core | 01.3.2-T1 |
| T-E1 | end-to-end | Wiring render → complete calibration row, windows cover timeline, dashboard within 5 % | 01.3.1-T4 |

Tests follow the collector's constants (marker table, `COLUMNS`); fixtures contain no real prompts or keys.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Days | Depends on | File |
|---|---|---|---|---|---|---|
| 01.3.1-T1 | Schema v2, column dictionary, prices | BA | XS | 0.25 | 01.1.2-T1 | `01.3.1-T1-schema-v2-dictionary-prices.md` |
| 01.3.1-T2 | Usage tap + start script | Worker | S | 0.5 | 01.1.2-T2; 01.2.4-T2 (real call only) | `01.3.1-T2-usage-tap.md` |
| 01.3.1-T3 | Run collector (`start_run.sh`, `run_manifest.py`, `finish_run.py`, tests) | Worker | M | 1.0 | 01.3.1-T1, 01.3.1-T2 (JSONL format), 01.3.2-T1, 01.1.1-T1 (`env-versions.sh`), 01.1.2-T2 (`lib.sh`) | `01.3.1-T3-run-collector.md` |
| 01.3.1-T4 | Calibration on the wiring render | BA | XS | 0.25 | 01.2.2-T6, 01.3.1-T3, 01.2.3-T2 (plausibility) | `01.3.1-T4-calibration.md` |
| 01.3.2-T1 | Process-tree RAM sampler | Worker | XS | 0.25 | 01.1.2-T2 | `01.3.2-T1-rss-sampler.md` |
| | **Total** | | | **2.25** | | = README §9 |

Size notes (sizes kept as README §9): 01.3.1-T3 is at the top of M — the parser, calculations, validation and the permanent unit tests (T-P1…T-P9) fit one day only because the dictionary (T1) fixes every formula up front; risk +0.5 d if Q2/Q9 force extra paths. 01.3.1-T1 XS holds because the dictionary is written in the task file already. High-level estimate for 01.3 was 1.0 d (01 §11) → 2.25 d.

New files introduced (fit D1/D9): `trial/tools/usage_tap.py`, `trial/tools/start-tap.sh`, `trial/tools/start_run.sh`, `trial/tools/run_manifest.py`, `trial/tools/finish_run.py`, `trial/tools/rss_sampler.sh`, `trial/tools/tests/test_finish_run.py`, `trial/tools/tests/fixtures/*`, `$NTS_HOME/tap.json`, `$NTS_HOME/logs/tap-<YYYYmmdd-HHMMSS>.log`.

## 11. Rollout

1. Day 2 (README §5): 01.3.1-T1, 01.3.2-T1 (self-test only).
2. Day 3: 01.3.1-T2 (stub acceptance, then one real `GET /v1/models` through the tap); 01.3.1-T3 with fixtures green.
3. 01.2.2-T2 template already points both base URLs at the tap; the tap must be running before any MPT render (otherwise MPT calls fail with connection refused — visible, not silent).
4. 01.2.2-T6 wiring render inside `start_run.sh … --status calibration` → 01.3.1-T4 → gate **01-ready** (a complete calibration row; `dashboard_cost_usd_check` may still be `n/a:pending-dashboard`, but MUST be filled before 05.5.2 prices are used and before **01-closed**).
5. 05.5.2 trial rows (`--status trial`), one vi, one en → **01-closed** (D12).
6. Monitoring during runs: `tail -f trial/runs/$(cat "$NTS_HOME/current_run")/openai_usage.jsonl`, tap terminal log, `$NTS_HOME/runs/<id>/rss_sampler.log`.
7. Rollback: if the tap misbehaves, stop it, re-render the MPT config with both base URLs = `OPENAI_BASE_URL` (`start-mpt-api.sh --no-tap`, 01 CR-15), and fall back to the README §9 lean option (dashboard daily totals, one run per day per project); the collector then writes `n/a:no-tap` for token/cost columns — `start_run.sh` requires `--no-tap` to allow this [DECISION].
8. Close: tap stopped and not carried to the worker (D6, CR-4); `CARRY-OVER.md` (01.3.3) records the measured numbers and that production metering is open.
