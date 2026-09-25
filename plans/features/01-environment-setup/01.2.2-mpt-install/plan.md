# 01.2.2 — MPT installed (feature plan)

> Refines: `../../../01-environment-setup.md` §4 story 01.2.2, §5.1, §5.2, §6 (01.2.2-T1…T3), §7. Contract: `../README.md` §1 facts, F06–F08, F13, F15, D1–D12, CR-7, CR-9. Related: `../../../05-video-assembly-subtitles.md` §5.1–§5.3 and 05.1.1, 05.3.1, 05.4; `../../../04-voice-proxy.md` §5.2; `../../../DECISIONS.md` §5.2 and §5.5.
> Legend: ✅ read from source (README §1 id) · ⚠️ confirm at the pinned commit or install · [DECISION] · [INFERENCE] · [OPEN].
> Status: detailed plan written 2026-09-23 (step 3 of 00 §10.1). Tech Lead review and owner sign-off done 2026-09-24 (README §12). Nothing implemented yet.

## 1. Scope

**Story 01.2.2 (Must).** *As the developer I want MPT running so that I can render the trial video.*

Acceptance criteria (rewritten to match D1–D5; these replace the text in 01 §4):
- Given MPT checked out at `MPT_COMMIT` in `$NTS_HOME/src/mpt` and installed with `uv sync --frozen`, When `worker-node/bin/start-mpt-api.sh` runs, Then the API listens only on `127.0.0.1:8080`, `app.api_key` is set, and a request without the key gets 401.
- Given the config template, When the renderer runs, Then `$NTS_HOME/src/mpt/config.toml` has mode 0600, is written atomically, and passes every guard (D5 plus §8 of this plan). A missing variable, an empty key or `listen_host = "0.0.0.0"` makes it exit 1 without writing anything.
- Given bundled music, When setup or a checkout finishes, Then `resource/songs` and `storage/bgm` hold no audio except tracks listed in `worker-node/music/LICENSES.csv` (the CC0 placeholder), each with a matching sha256 (DECISIONS §5.5).
- Given VieNeu, the proxy (04.1.2), the usage tap, the whisper model and OpenAI keys, When the wiring render (T6) runs, Then one MP4 with video and audio streams exists, OpenAI usage for chat and images is logged by the tap, and `finish_run.py` appends a `calibration` row.

| In scope | Out of scope |
|---|---|
| Checkout and install at the pin (through `bootstrap.sh`, 01.1.2-T3) | Clone/sync logic itself (01.1.2-T3 owns `bootstrap.sh`) |
| Pinned `config.example.toml` copy + sha256 (the baseline for the 05.1.1 upgrade diff, CR-7) | The upgrade diff script (05.1.1-T1) |
| `config.template.toml`, `render-mpt-config.py`, guards, config-drift diff | Worker (GPU node) config values (05.1.1, 11; CR-9) |
| Stripping bundled music, CC0 placeholder licence record, `install-music.sh` | Music catalog of 20–30 tracks and storage sync (05.3.2) |
| API start script (no WebUI in M0, D14), MPT smoke test | Proxy (04), usage tap and collector (01.3.1), whisper weights (01.2.3) |
| Wiring render: the cheapest end-to-end run, `run_status=calibration` | The vi/en trial rows (05.5.2), the task-query route (05.4), fonts (05.2.2, CR-6) |

## 2. Confirmed facts

| Id | Fact used here | Where it matters |
|---|---|---|
| ✅ M1 | Top-level keys `log_level` (default `"DEBUG"`, L15), `listen_host` (`"0.0.0.0"`, L17), `listen_port` (8080, L18). The `[app]` table starts at L20 and holds `api_key` L32, `video_source` L84, `pexels_api_keys` L91, `pixabay_api_keys` L94, `openai_image_base_url` L198, `openai_image_api_keys` L201, `openai_image_model` L204, `openai_image_size` L210, `openai_image_prompt_template` L214, `llm_provider` L246, `openai_api_key/base_url/model_name` L276–278, `subtitle_provider` L435, `enable_redis` L463, `max_concurrent_tasks` L470 (default 5), `upload_post_*` L480–500. `[whisper]` L506: `model_size` L509, `device` L512, `compute_type` L513. `[kokoro]` L582: `base_url` L585, `api_key` L586, `voices` L590, `model_id="kokoro"`. `tls_verify=true` L65. The example has no quality or moderation key (line numbers are from `3d5f4e42`) | T1, T2 |
| ✅ M2 | API: `uv run python main.py` (uvicorn with host and port from config, uvicorn `log_level="warning"`). WebUI: `sh webui.sh` → Streamlit with `--server.address=$MPT_WEBUI_HOST` (default 127.0.0.1) and `--server.port=$MPT_WEBUI_PORT` (default 8501); if the port is busy it moves to 8502–8599 | T4 |
| ✅ M3 | Whisper model path `<mpt_root>/models/whisper-<model_size>/model.bin`. `language` is not passed. Logs `loading model: …` and `detected language: '<xx>', probability: …` | T6 |
| ✅ M4 | `openai_image` body is `{model, prompt, n:1, size}`. Retries 429/5xx 3× (5/15/30 s). On failure it logs `openai image generation failed: term=…` and skips the keyword without raising. Empty `openai_image_base_url` or `openai_image_model` raises `ValueError` | T2, T6 |
| ✅ M5 | LLM: `chat.completions.create`, not streaming. Empty base URL → api.openai.com; empty model → `gpt-5.5`. MPT retries 5× | T2 (guard: model non-empty) |
| ✅ M6 | Music dirs `resource/songs` + `storage/bgm`. Random pick by extension `.mp3 .m4a .aac .wav .flac .ogg .opus .wma`. An empty list logs `no background music files found` and renders without music. `bgm_type` default `random`, `bgm_volume` 0.2 | T3, T6 |
| ✅ M7 | Python `>=3.11`, `.python-version` 3.11. Install: `uv python install 3.11 && uv sync --frozen`. ffmpeg lookup: `IMAGEIO_FFMPEG_EXE` → PATH → imageio-ffmpeg. Pins: `faster-whisper==1.1.0`, `moviepy==2.2.1`, `streamlit==1.59.1`, `openai==2.24.0` | T1, T4 |
| ✅ M8 | `config.toml` is read from the MPT root; if it is missing it is copied from the example. The WebUI rewrites it on reruns (temp file + `os.replace`; top-level, `[whisper]` and `[proxy]` are kept as loaded). No general env override | T2, T4, §8 |
| ✅ M9 | loguru line format; stage markers `## generating …`; `task <id> finished, generated N videos.` (logged with `logger.success`); `task failed, task_id: <id>, stage: …`. ⚠️ the level used for `## …` markers (assumed INFO) | T2 (log_level), T6 |
| ✅ M10 | MIT licence | T1 |
| ✅ M11 | Empty `api_key` → auth off, and startup logs `API key authentication is disabled; keep the API on a trusted network`. Missing, wrong or duplicate `x-api-key` → 401 `invalid API key`. A browser `Origin` that is not allowed → 403 `cross-origin browser request is not allowed`. The key also protects the static `/tasks` files | T5 |
| ✅ E1/E2 | `$NTS_HOME` is on the FileVault disk; `/Volumes/Project` is unencrypted with ownership disabled. Homebrew ffmpeg 9.0.2 is installed; ports 8080/8501 are free | T1, T2 (guard against `/Volumes/`) |
| ✅ O5/O6/O8 | Org verification may be needed for GPT Image. Image `quality` defaults to `auto`. Prices: flare text-in $5, image-in $8, image-out $30 per 1M tokens; luna $0.10 in / $0.50 out per 1M. Tier 1 = 5 images/min | T6 cost ceiling, 429 risk |
| ✅ D6 | Tap at `127.0.0.1:8002` writes `trial/runs/<run_id>/openai_usage.jsonl` | T2 base URLs, T6 |
| ⚠️ | Task query route and output file names (05.4); `VideoAspect` enum values (05 §5.2); `video_language` value format; the effect of `video_terms` omitted (schema default applies) with a given `video_script` (assumed: script LLM skipped, terms LLM runs); where the WebUI is allowed to upload into `storage/bgm` | T6 |

Closed here: story text "WebUI/API listen on 127.0.0.1" (the API needs config, the WebUI needs an env var: M2); "CC0 placeholder present" (now a licence-recorded, hash-verified install, T3).

## 3. Domain and data

N/A for database. The feature owns these files:

| File | Tracked | Owner task | Content |
|---|---|---|---|
| `worker-node/mpt/config.example.pinned.toml` | yes | T1 | Verbatim copy of `config.example.toml` at `MPT_COMMIT` |
| `worker-node/mpt/config.example.pinned.toml.sha256` | yes | T1 | `shasum -a 256` line + `MPT_COMMIT` comment |
| `worker-node/mpt/config.template.toml` | yes | T2 | Template with 4 placeholders (D5) |
| `worker-node/bin/render-mpt-config.py` | yes | T2 | Renderer + guards + diff + `--check` |
| `worker-node/bin/strip-bundled-music.sh` | yes | T3 | Removes unlicensed audio; `--check` mode |
| `worker-node/bin/install-music.sh` | yes | T3 | Copies hash-verified tracks into `resource/songs` |
| `worker-node/music/LICENSES.csv` | yes | T3 | One row per track (schema in T3) |
| `worker-node/bin/start-mpt-api.sh` | yes | T4 | Start script (API only, D14) |
| `worker-node/bin/smoke-mpt.sh` | yes | T5 | MPT smoke test |
| `trial/fixtures/wiring-en.txt` | yes | T6 | ~90-word neutral English script |
| `$NTS_HOME/src/mpt/config.toml` | no (0600) | T2 | The only file with secrets (D3) |
| `$NTS_HOME/mpt-config.last.redacted.toml` | no | T2 | Canonical redacted copy of the last render, used for the config-drift diff |
| `$NTS_HOME/music/<file>` | no | T3 | Source audio (BA pick) |
| `$NTS_HOME/logs/mpt-api-<YYYYmmdd-HHMMSS>.log` | no | T4 | Service logs (the 01.3.1 collector reads `mpt-api-*`) |

New `$NTS_HOME` entries not in the D1 diagram: `mpt-config.last.redacted.toml`, `music/` (README change request).

## 4. Use cases (operator procedures)

| # | Procedure | Command | Result |
|---|---|---|---|
| UC1 | Install / re-install at the pin | `worker-node/bin/bootstrap.sh mpt`, then the T1 checks | HEAD == `MPT_COMMIT`, venv synced, songs stripped |
| UC2 | Render config (normally done by the start scripts) | `uv run --no-project --python 3.12 worker-node/bin/render-mpt-config.py` with the 4 `NTS_*` vars in the environment (T4 wraps this) | config.toml 0600; exit 0/1/2 |
| UC3 | Validate the existing config without writing | `… render-mpt-config.py --check` | exit 0 or a list of failed guard names |
| UC4 | Install music | `worker-node/bin/install-music.sh` | Placeholder in `resource/songs`, hash verified |
| UC5 | Start API | `worker-node/bin/start-mpt-api.sh` (own terminal) | Listening on `127.0.0.1:8080`; log in `$NTS_HOME/logs/` |
| UC6 | (removed) WebUI | not used in M0 (D14, owner DEC-4) | nothing listens on 8501–8599 |
| UC7 | Smoke | `worker-node/bin/smoke-mpt.sh` | exit 0 = 401/401/200/403, binds, renderer negatives |
| UC8 | Wiring render | T6 steps (`start_run.sh` → `POST /api/v1/videos` → wait for the log line → `finish_run.py`) | MP4 + calibration row |
| UC9 | After any MPT checkout or upgrade | `bootstrap.sh mpt` (calls strip) → `install-music.sh` → start script (re-render) → `smoke-mpt.sh`; 05.1.1 diffs the new example against `config.example.pinned.toml` | Same state as before |

Exit codes follow D9 for every script: 0 ok · 1 check failed · 2 usage/environment error.

## 5. API and events

MPT routes used (MPT's own API, not a platform contract; 05 §5.3 owns the contract):

| Route | Used by | Expected |
|---|---|---|
| `GET /api/v1/tasks` | T5 | no key → 401 `invalid API key` ✅ M11 · wrong key → 401 ✅ · right key → 200 ✅ (05 §5.3 "exists") · right key + `Origin: https://example.invalid` → 403 ✅ M11 |
| `POST /api/v1/videos` body `VideoParams`, header `x-api-key` | T6 | 200 with a task id (field name ⚠️ 05.4; the prefix ⚠️ 05 §5.3) |
| `GET /api/v1/tasks/{task_id}` | not used | ⚠️ 05.4; T6 uses the log line `task <id> finished` (M9) instead |

No events.

## 6. UI

N/A — no UI. The MPT WebUI (Streamlit) is not used in M0 (README D14, owner DEC-4, 2026-09-24): measured runs are submitted through the API (T6), and nothing may listen on 8501–8599.

## 7. Jobs and integrations

| Integration | Address | Failure → effect in MPT | Handling |
|---|---|---|---|
| Usage tap (D6) → OpenAI chat | `openai_base_url = http://127.0.0.1:8002/v1` | Tap down → connection refused → MPT retries 5× (M5) → task failed at the terms stage | T6 pre-flight: tap port listening; the task failure is visible in the log |
| Usage tap → OpenAI images | `openai_image_base_url = http://127.0.0.1:8002/v1` | Tap down, 429 (Tier 1 = 5/min, O8), 400/403 (verification O5, allow-list O3) → **keyword skipped silently** (M4); the video gets fewer images or none | T6 fails if `openai image generation failed` appears; count recorded by the collector (F11) |
| Proxy (04) → VieNeu | `[kokoro] base_url = http://127.0.0.1:8001/v1`, `api_key = PROXY_API_KEY` | 401 (key mismatch), 400 (unknown voice), 429 busy → MPT retries 3× (04 F6); 120 s idle timeout (F01, 04 R1) (04 CR-2) | T6 picks the en voice from `seed/voices.v1.json` (04 CR-6); 30–45 s script stays far below the 120 s limit |
| Whisper (in process) | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` with `HF_HUB_OFFLINE=1` | Model missing + offline → load fails → subtitles empty ⚠️ (subtitle.py logs an error and returns None) | T6 checks `loading model:` path and that a subtitle file exists |
| ffmpeg | `IMAGEIO_FFMPEG_EXE=/opt/homebrew/bin/ffmpeg` | Missing → MPT's ffmpeg check stops the pipeline (M7) | T4 checks `-x` before start |
| Timeouts | T6 waits ≤ 30 min for the finish/fail log line | No line → stop, inspect log | — |

## 8. Security and privacy

| Threat | Control |
|---|---|
| **Open API.** Missing or failed render → MPT copies the example → `api_key=""` (auth off) + `0.0.0.0` on the LAN (F07, M8, M11) | `start-mpt-api.sh` renders on every start and aborts on exit ≠ 0. It refuses to start if `config.toml` is missing, is a symlink, or fails `--check`. Post-start it asserts the `127.0.0.1:8080` bind (`check-binds.sh`, D4) and that the log does not contain `API key authentication is disabled`; otherwise it kills MPT and exits 1. `smoke-mpt.sh` asserts 401/401/200/403 |
| **Config drift** (M8: the WebUI would rewrite `config.toml`) | No WebUI in M0 (D14). The API config is re-rendered from the template on every start; any drift since the last render is printed as a redacted diff and treated as a finding; guards run before `os.replace` |
| **WebUI** is an unauthenticated second server that would hold the real keys (F08, F27, SEC-02) | Removed from M0 (D14, owner DEC-4): no start script; `check-binds.sh` and smoke row 13 fail on any listener in 8501–8599; never on the worker (CR-9) |
| **Rogue upstream listener** on `127.0.0.1:8002` / `8001` receives the OpenAI / proxy keys that MPT sends (SEC-04) | `start-mpt-api.sh` step 3a runs before any Keychain read: exactly one listener per port, owned by this uid, whose command is `usage_tap.py` / the proxy; `tap.json` upstream = `https://api.openai.com` and its pid matches the listener. Any mismatch → exit 2 |
| **upload-post** (`app/services/upload_post.py`) could publish to TikTok/Instagram | `upload_post_enabled=false`, `upload_post_auto_upload=false`, `upload_post_api_key=""` in the template, all three guarded; never configured |
| **Keys on disk** (F05): E1 USB volume is unencrypted with ownership disabled | Keys are only in the Keychain (D3: `nts.mpt.api_key`, `nts.openai.story`, `nts.openai.image`, `nts.proxy.api_key`). The only file is `$NTS_HOME/src/mpt/config.toml` (FileVault disk, 0600, verified with `stat -f %Lp`). The renderer exits 2 if the output path resolves under `/Volumes/`. Values pass only through the child environment and are never printed; no `set -x` |
| Secret leak through the diff or the logs | The redactor replaces every value whose key matches `key|keys|token|secret|password`, **and** any value equal to one of the 4 mapping values or starting with `sk-` (SEC-09). Value-based leak check with `grep -cFf <(…)` over the state file, the renderer stderr and `mpt-*.log` (smoke row 12). Guard messages print key names only. `log_level = "INFO"` (the DEBUG default may log prompts, M1) |
| TOML injection through a key value (quote or newline in a secret) | Each `NTS_*` value must match `^[A-Za-z0-9._-]{16,512}$`, else exit 1 (name only) |
| Stock-footage keys leaking into use | `pexels_api_keys = []`, `pixabay_api_keys = []` (guarded) |
| Unlicensed music (DECISIONS §5.5) | `strip-bundled-music.sh` removes all audio not in `LICENSES.csv` by sha256; runs from `bootstrap.sh` and after every checkout |
| Retention | Task outputs in `$NTS_HOME/src/mpt/storage/tasks/<id>` (internal disk). Media copies go to `$NTS_HOME/runs/<run_id>/`. Delete both at M0 close together with the key revocation (01.2.4) |

## 9. Test plan

| Behaviour / boundary | How | Task |
|---|---|---|
| Install matches the pin and the lock | `git rev-parse HEAD` == `MPT_COMMIT`; `uv sync --frozen --check` ⚠️ flag, or re-run `uv sync --frozen` → no changes | T1 |
| Renderer happy path | Exit 0, mode 600, regular file, `--check` exit 0 | T2, T5 |
| Renderer missing variable / empty key / `listen_host = "0.0.0.0"` / bad characters in a value / output under `/Volumes/` | Exit 1 (last: 2); the target file is unchanged (sha256 before = after) | T2, T5 (self-test) |
| Config drift detected | Edit a non-secret value in the rendered file → next render prints a diff line with the key name and no secret value | T2 |
| Unlicensed audio removed; licensed placeholder kept; idempotent | Plant a dummy `.mp3` → strip → gone; placeholder hash kept; second run exit 0 with no changes | T3 |
| API auth | 401 without key, 401 with wrong key, 200 with right key, 403 with a foreign Origin | T5 |
| Loopback only | `check-binds.sh` exit 0; `curl http://<LAN-IP>:8080/` → exit 7 (connection refused) (F19) | T5 |
| Port busy → refuse | `nc -l 127.0.0.1 8080 &` → `start-mpt-api.sh` exit 2; a listener on 8501 → `check-binds.sh` exit 1 | T4, T5 |
| Missing config → refuse | Remove `config.toml` and have the render fail (unset one var) → script exit 1 and no MPT process | T4 |
| End-to-end | MP4 with video + audio streams, chat + images usage lines, no skipped keyword, subtitle file present, `detected language: 'en'`, calibration row | T6 |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 01.2.2-T1 | Install at pin, ffmpeg binding, pinned example + sha256 | XS 0.25 | 01.1.1-T2, 01.1.2-T3 | T2, T3, T4, 01.2.3-T1 |
| 01.2.2-T2 | Config template + renderer + guards | S 0.5 | T1, 01.1.2-T2 | T4, T5, 05.1.1-T1 |
| 01.2.2-T3 | Strip bundled music + CC0 placeholder + `install-music.sh` | XS 0.25 | T1, 01.1.2-T1 | T4, T6, 05.3.1-T1 |
| 01.2.2-T4 | Start scripts: API and WebUI | XS 0.25 | T2, T3, 01.1.2-T2 | T5, T6, 01.2.3-T1 |
| 01.2.2-T5 | MPT smoke test | XS 0.25 | T4 | T6, gate 01-ready |
| 01.2.2-T6 | Wiring render (calibration run) | S 0.5 | T3, T5, 01.2.1-T2, 04.1.2, 04.1.3 (soft, 04 CR-6), 01.2.3-T1, 01.2.4-T2, 01.3.1-T2, 01.3.1-T3 | 01.3.1-T4, gate 01-ready |
| **Total** | | **2.0 d** (= README §9) | | |

High-level: 1.25 d (01 §6, story size M) → detailed 2.0 d (+0.75 d: renderer with guards, WebUI script, wiring render; F06–F08, F13). Size notes (sizes kept): T2 is at the upper edge of S because of the diff and the self-test. T6 is S only if 04.1.2, the tap and the collector work on the first try; debugging them is not counted here.

New names introduced (they fit D1/D9): `worker-node/bin/install-music.sh`, `worker-node/mpt/config.example.pinned.toml.sha256`, `trial/fixtures/wiring-en.txt`, `$NTS_HOME/mpt-config.last.redacted.toml`, `$NTS_HOME/music/`. Renderer flags: `--check`, `--template`, `--out`, `--state`, `--self-test`; start scripts pass `--template` through (test hook). `worker-node/bin/smoke.sh` (shared aggregator, D4/§7 gate) is created by T5 only if 01.2.1-T3 has not created it.

## 11. Rollout

1. Order (README §5, day 2): T1 → T2 → T3 → T4 → T5. T6 runs after 04.1.2, 01.2.3-T1, 01.2.4-T2 and 01.3.1-T2/T3, then 01.3.1-T4 → gate **01-ready**.
2. No feature flag. Trial runtime only; the worker gets the template through 05.1.1 (CR-7) with node values (CR-9).
3. Monitoring: every start prints the guard summary and any WebUI diff; `smoke-mpt.sh` is part of `smoke.sh` (gate 01-ready; request to the 01.1 owner).
4. After every MPT checkout or upgrade: UC9.
5. **Rollback:**
   - Stop services: Ctrl-C in the API and WebUI terminals. `lsof -nP -iTCP:8080 -sTCP:LISTEN` and `lsof -nP -iTCP:8501 -sTCP:LISTEN` must print nothing.
   - Delete the rendered config: `rm -f "$NTS_HOME/src/mpt/config.toml" "$NTS_HOME/mpt-config.last.redacted.toml"`. MPT must not be started by hand afterwards (it would copy the example, F07).
   - Full reset: `rm -rf "$NTS_HOME/src/mpt"` → `bootstrap.sh mpt` → T1…T5 again (the whisper model must be fetched again, 01.2.3-T1).
   - Suspected key exposure: rotate `nts.mpt.api_key` (`secrets.sh`, 01.1.2-T2 ⚠️ subcommand) and the OpenAI keys (01.2.4-T3), then re-render.
6. Carry-over (01.3.3): template, pinned example + sha256, renderer, strip script, `LICENSES.csv`. The start scripts and the WebUI are Mac-only.
