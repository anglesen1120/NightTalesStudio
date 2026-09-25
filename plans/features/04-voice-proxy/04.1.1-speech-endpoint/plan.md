# 04.1.1 — Spike and speech endpoint (`/v1/audio/speech`)

> Refines: `../../../04-voice-proxy.md` §4 epic 04.1 (stories **04.1.1**, **04.1.2**), FR-04-1, FR-04-4, FR-04-5, FR-04-6, NFR-04-1..4, §5.1, §6, §7, §10
> Contract: `../README.md` — **D1** (process/packaging), **D2** (routes/auth), **D3** (request contract), **D4** (streaming pipeline), **D5** (admission), **D6** (error codes), **D7** (log line), **D9** (voice catalog, rev 0 only), **D13** (env vars), **D14** (test kit), **F01**, **F02**, **F03**, **F05**, **F07**, **F11**, **F12**, **F13**, **F17**, **F18**, **V1–V11**, **M1–M9**, **R1**, **X1**, **X2**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs (cites README id) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet. Owner decisions O1–O4 pending (README §10); this folder assumes them accepted (streaming design D4, re-estimate O3) since the README recommends "Yes" for both and the design has no viable alternative (F01 is a blocker without it).

## 1. Scope

**Stories.**
- 04.1.1 *Spike: decode VieNeu WAV* (04-voice-proxy.md epic 04.1, Must · XS in the high-level plan) — re-scoped by README F01/F12/F17/X1/X2: not just WAV decode but streaming PCM, streaming abort semantics seen by MPT's `requests`, and client-disconnect slot release, all re-checked at the pinned commit. Detailed **S (0.5 d)**.
- 04.1.2 *Speech endpoint* (04-voice-proxy.md epic 04.1, part of the Must · M "Proxy" work) — the production `/v1/audio/speech` pipeline, its error/admission layer, and an MPT-side acceptance run. Detailed across three tasks, **2.0 d** (T1 1.0 + T2 0.5 + T3 0.5).

**In scope.**
- 04.1.1-T1: spike experiments on the implementation machine, producing `worker-node/proxy/SPIKE.md` and a go/no-go for D4.
- 04.1.2-T1: the proxy project skeleton, `config.py`, `catalog.py` (+ `seed/voices.v1.json` revision 0), `errors.py`, `upstream.py` (`speech_stream` only), `transcode.py`, `reqlog.py`, `app.py` with the `/v1/audio/speech` route, D3 request checks, D4 streaming pipeline, D7 logging.
- 04.1.2-T2: the D6 error-code table end to end, D5 admission/retry, D3 body-size/length limits, D2 route allow-list (404/405/docs disabled), wire tests against a real uvicorn with `requests`.
- 04.1.2-T3: MPT-side acceptance using MPT's own `voice.tts()` and `moviepy`, a performance baseline artifact, and the CR-2 pass/fail check that replaces the 01.2.1-T4 gate.

**Out of scope (other folders/stories).**
- `GET /v1/audio/voices`, upstream voice cache, `GET /health`, packaging (systemd, bootstrap, start/smoke scripts), option B note — all `04.1.3-voices-health-packaging/`.
- Voice curation (rev 1), `VoiceCatalogRules` + `VoiceSeeder`, previews — `04.2-voice-catalog/`.
- Backend integration (09), node deployment (11), MPT config rendering (01.2.2-T2) — those plans' own tasks; only referenced here as dependents.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| V1–V11 | `SpeechRequest` shape, admission/queue behaviour, response headers, WAV header, preset list, voice-name matching, RTF numbers | README §1 ✅ |
| M1–M9 | MPT's `_openai_compatible_tts` body/retry/decode behaviour, `kokoro_tts`/`parse_voice_name`, MPT's `requests`/`moviepy` pins | README §1 ✅ |
| R1 | `requests` timeout is an idle timeout, not a whole-response limit | README §1 ✅ |
| X1 | ffmpeg 9.0.2 decodes both VieNeu's unknown-length WAV and raw PCM correctly; a zero-sample WAV exits 0 with a 44-byte output; `-write_xing 0 -id3v2_version 0` gives a clean MP3 frame start | README §1 ✅ (planning-Mac experiment; 04.1.1-T1 re-runs on the implementation machine) |
| X2 | A `StreamingResponse` generator that raises after first bytes → client sees `ChunkedEncodingError`; a slow trickle under a longer timeout completes normally | README §1 ✅ (requests 2.32.5; re-run at MPT's pinned 2.33.1 in 04.1.2-T3, M9) |
| F01/F02/F03/F11/F12 | Buffered design fails MPT's idle timeout on long stories; streaming with a hard abort-on-failure is required; WAV header warnings are not failure; empty audio must 502, not 200 | README §2 [finding] |
| F17 | Whether VieNeu frees its stream slot on client abort immediately or only after generation ends is unknown ⚠️ — 04.1.1-T1 measures it | README §2 |

**Closed here.** Whether option A (proxy) can meet MPT's timeout for a 5-min story (yes, by streaming, D4). Whether ffmpeg can read VieNeu's WAV/PCM at all (X1, re-confirmed by 04.1.1-T1). What MPT's HTTP client actually does on a broken stream (X2, re-confirmed by 04.1.2-T3 at MPT's pinned `requests` version).

## 3. Domain and data

No new persisted domain entities in this folder (no database, no ORM). Data artifacts:
- `seed/voices.v1.json` — written as **revision 0** by 04.1.2-T1 per README D9: `{ "schema": "voices.v1", "revision": 0, "vieneuCommit": "<pin>", "voices": [ { "id": "Hải Đăng", "slug": "hai-dang", "displayName": "Hải Đăng", "language": "vi", "gender": "male", "tags": [], "active": true, "previewStorageKey": null }, { "id": "Ngọc Huyền", "slug": "ngoc-huyen", "displayName": "Ngọc Huyền", "language": "en", "gender": "female", "tags": [], "active": true, "previewStorageKey": null } ] }`. Genders per V8 ✅ (`Hải Đăng` = upstream default, male; `Ngọc Huyền` female). `slug` computed by `catalog.slugify` (D9 rule: NFD → drop combining marks → `đ/Đ → d` → lowercase → non-`[a-z0-9]` runs → `-` → trim).
- `worker-node/proxy/SPIKE.md` — 04.1.1-T1 output, a findings table plus a go/no-go, not a data contract (no other task reads it programmatically).
- `trial/baselines/proxy-mac-<YYYYMMDD>.json` — 04.1.2-T3 output, schema defined in that task file; consumed by the README §7 04-M0 gate and by CR-2 (01.2.1-T4's re-estimate).
- In-process only: `CatalogVoice`/`Catalog` (from `catalog.py`, D9 pinned interface) and `Settings` (`config.py`, D13 env table) — dataclasses, not stored.

## 4. Use cases

- **UC1 — MPT synthesizes a short line.** MPT's kokoro branch posts `voice_name = "kokoro:<id>"` derived text to the proxy; proxy streams MP3 back inside a few seconds; MPT decodes with MoviePy, writes `audio.mp3`. (04.1.2-T1, D4.)
- **UC2 — MPT synthesizes a 5-minute story.** Same call, ≈ 174 s of VieNeu inference (V11 RTF 0.58 × 300 s). Without streaming this exceeds MPT's 120 s idle timeout (F01); with streaming, MP3 bytes flow continuously (`UPSTREAM_READ_GAP_S` = 60 s ceiling) so MPT never idles out. (D4 timing budget.)
- **UC3 — Unknown or malformed voice/body.** Proxy rejects before any upstream call, per D3 order; MPT logs the D6 code and fails after 3 immediate attempts (M2, M8). (04.1.2-T2.)
- **UC4 — VieNeu momentarily busy (429).** Proxy retries upstream itself inside `ADMISSION_WAIT_S` so MPT's first attempt usually succeeds; if the budget runs out, 429 `upstream_busy`/`proxy_busy` is returned once. (D5, 04.1.2-T2.)
- **UC5 — Upstream or ffmpeg dies mid-stream (post-commit).** Connection is aborted without a final chunk; MPT's `requests` raises `ChunkedEncodingError` and retries (X2, F02); proxy logs `status: 502` with the specific `error_code`. (D4, 04.1.2-T1/T2.)
- **UC6 — MPT gives up and disconnects.** Proxy cancels the writer and stderr tasks, kills ffmpeg, and **drains** the upstream stream to its end while holding its slot (README §10 T15: VieNeu leaks its slot for 82–>400 s after a disconnect, `SPIKE.md` (d)); logs `status: 499` `client_disconnected`. (04.1.1-T1, D4.)
- **UC7 — Zero speakable audio.** VieNeu returns 200 with no PCM samples (e.g. whitespace-only text slips through, or all content stripped upstream); proxy must not commit a 200 with an empty body — 502 `upstream_empty_audio` (F11). (D4, 04.1.2-T1.)
- **UC8 — Baseline / regression proof.** 04.1.2-T3 exercises UC1/UC2 through MPT's actual client code (not a synthetic `requests` script) and publishes the CR-2 pass/fail numbers that supersede the informational 01.2.1-T4 gate.

## 5. API and events

Routes (D2; only `/v1/audio/speech` is in scope here, the other two rows belong to `04.1.3-voices-health-packaging/` and are listed for context):

| Route | Method | Auth | Upstream call | Owner |
|---|---|---|---|---|
| `/v1/audio/speech` | POST | Bearer `PROXY_API_KEY` | VieNeu `POST /v1/audio/speech` | 04.1.2-T1 (this folder) |
| `/v1/audio/voices` | GET | Bearer `PROXY_API_KEY` | VieNeu `GET /v1/voices` | 04.1.3-T1 (other folder) |
| `/health` | GET | none | VieNeu `GET /health` | 04.1.4-T1 (other folder) |

Request/response contract for `/v1/audio/speech` is D3 (checks, in order) + D4 (pipeline phases, headers, error semantics) + D6 (error-code → HTTP mapping) + D7 (one JSON log line per request, D7 field list, `reqlog.emit`). No other events (no websockets, no queue, no webhook) in this endpoint. Upstream body is always the fixed D3 shape: `{ "model": "vieneu-v3-turbo", "input": <input>, "voice": <v>, "response_format": "pcm", "stream_format": "audio", "sample_rate": 48000 }` — `model`/`speed`/`instructions` from MPT are read only to populate `ignored_fields` (D7) and are never forwarded (F18).

## 6. UI

Not applicable — this endpoint has no UI. It is called only by MPT's own HTTP client (M1); the WebUI path (M6) never reaches this route because the WebUI is not used on the node (01 D14, README F15).

## 7. Jobs and integrations

- No background jobs. `speech_stream` (`upstream.py`) is a per-request coroutine, not a scheduled job.
- Integration: VieNeu `POST /v1/audio/speech` via `VieneuClient.speech_stream` (`upstream.py`, D1 pinned interface) over `httpx`, base URL `VIENEU_BASE_URL` (D13).
- Integration: `ffmpeg` subprocess via `asyncio.create_subprocess_exec` (`transcode.py`), exact command in D4.
- Downstream consumer: MPT's `[kokoro]` provider (M1–M5) — not modified by this folder; 01.2.2-T6 (soft dependency, CR-6) wires MPT's config once this endpoint exists.
- Log sink: stdout, one JSON line per request (D7); no external log shipper in scope here (01.3.1-T3 reads the file later, out of scope).

## 8. Security and privacy

- Auth is D2: `hmac.compare_digest` on the literal header value `Bearer <PROXY_API_KEY>`, checked before body parsing; `PROXY_API_KEY` and `VIENEU_API_KEY` each ≥ 32 chars, enforced at `Settings.from_env` (exit 2 otherwise, message never contains the value — pinned interface `ConfigError(var_name)`).
- No input text, request/response bodies, headers, keys, or ffmpeg argv values (other than the bitrate) are ever logged (D7 "Never logged" list) — 04.1.2-T1's tests assert this directly on the emitted JSON.
- Bind is `127.0.0.1` only (D1/D13); `PROXY_HOST` other than `127.0.0.1` is a startup config error, not a runtime guard, so this folder's tests exercise it at `Settings.from_env`, not over the network.
- `/docs`, `/redoc`, `/openapi.json` are disabled (F05, D2) — prevents accidental schema/route disclosure; 04.1.2-T2 asserts 404 on all three.
- Errors sent to MPT are always the generic D6 shape (`{"error":{"message","type":"proxy_error","code"}}`); VieNeu error messages are truncated to 200 chars before relay (D6 `upstream_rejected`) so no oversized or malformed upstream text reaches MPT's logs unbounded.
- `ffmpeg_stderr_tail` (bounded 8 KiB) is logged only on failure and never contains input text (it is ffmpeg's own diagnostic stream, D4) — 04.1.2-T1 verifies via a forced ffmpeg failure fixture.

## 9. Test plan

Per D14 (shared test kit): pytest in `worker-node/proxy/tests/`, `httpx.MockTransport` for the fake VieNeu, real ffmpeg, PCM fixtures generated in-test (sine wave), no voice audio committed. Wire tests use a real uvicorn + `requests` (MPT's own client library) against an ephemeral loopback port. Full named test list lives in the task files (04.1.1-T1 §Tests, 04.1.2-T1 §Tests, 04.1.2-T2 §Tests, 04.1.2-T3 §Tests); summary by layer:
- **Spike (04.1.1-T1):** not pytest — a throwaway script producing `SPIKE.md`; its "tests" are recorded experiment outcomes (go/no-go), re-run at the pin.
- **Unit/component (04.1.2-T1):** `config.py` (env validation), `catalog.py` (load/validate/slugify), `transcode.py` (`Mp3Encoder` against sine PCM), `reqlog.py` (field presence/absence), `app.py` pipeline state machine with `MockTransport`-driven upstream scenarios (happy path, empty audio, mid-stream break, ffmpeg failure).
- **Wire (04.1.2-T2):** real uvicorn + `requests`, proving abort semantics (X2), gaps, timeouts, auth-before-body, route allow-list.
- **MPT-side acceptance (04.1.2-T3):** MPT's own `voice.tts()` in MPT's venv against the running proxy, with `tools/mpt_decode_check.py` for duration proof; a failure-injection run (VieNeu killed mid-request).
- Run command (D14): `uv run --project worker-node/proxy pytest -q`.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 04.1.1-T1 | Spike: PCM/WAV decode, streaming transcode, abort and disconnect behaviour at the pin | Worker | S (0.5) | 01.2.1-T4, 01.2.1-T2, 01.2.2-T1 | 04.1.2-T1, 04.3.1-T1 |
| 04.1.2-T1 | Speech endpoint: config, auth, catalog loader, streaming pipeline, log line, catalog revision 0 | Worker | M (1.0) | 04.1.1-T1, 01.1.2-T2 | 04.1.2-T2, 04.1.3-T1, 04.1.4-T1, 04.2.2-T1, 01.2.2-T6, 03.4.1-T1 (soft) |
| 04.1.2-T2 | Error mapping, admission wait, limits, route allow-list, wire tests | Worker | S (0.5) | 04.1.2-T1 | 04.1.2-T3, 04.1.5-T1, 01.2.2-T6 |
| 04.1.2-T3 | MPT-side acceptance and performance baseline | Worker | S (0.5) | 04.1.2-T2, 04.1.5-T1, 01.2.2-T5, 01.2.1-T4 | gate 04-M0, 05.5.2, CR-2 decision evidence |
| **Total** | | | **2.5 d** | | |

Equals README §9 row "04.1.1 + 04.1.2 speech endpoint" = 2.5 d (high-level 1.75). Titles and sizes are the README §8 index verbatim.

### Open items for the TL

- README §10 O1 (streaming design) and O3 (re-estimate) are treated as accepted in this plan because F01 leaves no working alternative and both carry a TL "Yes" recommendation; if the owner answers otherwise, 04.1.2-T1/T2/T3 need a design change, not just a re-estimate. [OPEN — owner sign-off, not this folder's call.]
- 04.1.2-T3's dependency on 04.1.5-T1 (packaging/start-script) is a **forward** reference to the sibling folder `04.1.3-voices-health-packaging/`; sequencing (README §5) already shows `PK["04.1.5-T1"] --> AC["04.1.2-T3"]`, so this is confirmed by the contract, not a new coupling this folder introduces.
- Rev 0 maps `Ngọc Huyền` (a Vietnamese regional speaker, V8) to `en` only so the en wiring render (01.2.2-T6) has a catalog id; its English quality is unknown (F04). It is replaced by 04.2.2-T1 revision 1. [DECISION per D9]
- [RULED — README §10 T1] Module name `reqlog.py`; README D1 updated.
- [RULED — README §10 T2] `ffmpeg_stderr_tail` and `warnings: ["pcm_odd_tail"]` are failure-only D7 keys; README D7 updated.
- `UpstreamSpeech.error_message()` (on the object yielded by `speech_stream`, not a new client method) is needed for `upstream_rejected` message relay (D6). [DECISION]
- F17 outcome (slot release delay): measured 82–>400 s (`SPIKE.md` (d)) → drain instead of disconnect, `ADMISSION_WAIT_S` = 80. [RULED — README §10 T12, T15]

## 11. Rollout

- **Flag:** none — this is a new, unused-until-wired service; MPT is not pointed at it until 01.2.2-T6 (soft dependency) runs, so there is no user-facing cutover inside this folder.
- **Sequencing:** 04.1.1-T1 (spike) must land first and produce a go/no-go in `SPIKE.md` before 04.1.2-T1 starts (hard dependency) — a no-go blocks D4 and escalates to the TL; chunking or buffering is never used as a silent fallback (F01, CR-2).
- **Rollback:** before 01.2.2-T6 wires MPT's `[kokoro]` block to the proxy, rollback is simply "do not point MPT at port 8001" — no data migration, no stateful cutover. After 04.1.5-T1 packaging and 01.2.2-T6 wiring, rollback is reverting the `[kokoro]` config render (out of this folder's scope).
- **Gate:** 04-M0 (README §7) requires `SPIKE.md` recorded, the proxy test suite green, `smoke-proxy.sh` PASS, and the 04.1.2-T3 baseline meeting `ttfb_ms < 30,000` / `max_gap_ms < 60,000` / overhead ≤ 5 % / MoviePy duration within 0.1 s — all four owned by tasks in this folder.
- **Monitoring:** the D7 log line is the only telemetry in scope here; 01.3.1-T3 (another plan) aggregates it later — this folder only guarantees the line is emitted correctly and never leaks input text.
