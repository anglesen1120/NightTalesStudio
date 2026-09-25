# 01.2.1 — VieNeu v3 Turbo on CPU (Mac M4): detailed feature plan

> Refines `../../../01-environment-setup.md` §4 (story 01.2.1), §5.1, §6 (01.2.1-T1…T3), §7. Contract: `../README.md` (facts §1, findings F01/F09/F10/F19, decisions D1–D12, template §6, gates §7). Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs (README §1 id) · ⚠️ to confirm at the pinned commit/install · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**Story:** 01.2.1 VieNeu v3 Turbo on CPU — Must · 1.5 d (4 tasks, §10).

**In scope**
- VieNeu clone at `VIENEU_COMMIT` in `$NTS_HOME/src/vieneu` via `worker-node/bin/bootstrap.sh vieneu` (01.1.2-T3: `uv sync --frozen`, no extras; falls back to plain `uv sync` + WARN + lock sha256 if `uv.lock` is not tracked at the pin ⚠️), weights in `HF_HOME=$NTS_HOME/hf`, weight revisions (set by 01.1.1-T2, D2) verified against the cache and sha256-listed.
- One-command start `worker-node/bin/start-vieneu.sh` bound to `127.0.0.1:8000`, key from Keychain `nts.vieneu.api_key`, offline after first download.
- Smoke `worker-node/bin/smoke-vieneu.sh` (called by `worker-node/bin/smoke.sh`): health, auth 401s, format/voice 400s, 48 kHz WAV, voice snapshot, loopback-only bind.
- Mac baseline: RTF, RSS, cold start on vi/en fixtures → `trial/baselines/vieneu-mac-<YYYYMMDD>.json`; **120 s gate** result (F01; informational, 04 CR-2); watermark behaviour (F10); WAV samples handed to 04.1.1.

**Out of scope**
- The proxy (plan 04: 04.1.1 spike, 04.1.2 endpoint), voice curation (04.2.1), MPT wiring (01.2.2-T6), GPU/CUDA install (plan 11, CR-9), int8 precision (V1: needs x86 VNNI), voice cloning (04 §1 out of scope).

**Acceptance (story, replaces 01 §4 wording)**
- Given VieNeu at the pin, When `start-vieneu.sh` runs, Then `GET /health` = 200 within 60 s and `GET /v1/voices` lists the presets (count recorded).
- Given the server, When `response_format: "mp3"` is requested, Then 400; When no/wrong Bearer, Then 401.
- Given the server, When `curl http://<en0 IP>:8000/health` runs from the Mac itself, Then the connection is refused (F19 replaces "another machine on the LAN").
- Given the baseline, When the 5-min fixtures are synthesised, Then the 120 s gate result is recorded before 04.1.2/05.5.2 as an informational capacity input; the streaming pass/fail check is 04.1.2-T3 (04 CR-2).

## 2. Confirmed facts

| Id | Fact used here | Status |
|---|---|---|
| V1 | `HOST` default `0.0.0.0`, `PORT` 8000, `VIENEU_API_KEY` unset = open, `VIENEU_WATERMARK` default on (only `0` disables; needs `[watermark]` extra = torch), `VIENEU_PRECISION` fp32 default (int8 needs x86 VNNI → fp32 on M4 [INFERENCE]), `VIENEU_MAX_STREAMS` 1 on CPU fp32, `VIENEU_BACKEND` auto/onnx/pytorch, `VIENEU_DEVICE` auto/cuda/cpu, `VIENEU_ONNX_DIR` local export, `VIENEU_QUEUE` = max_streams, `VIENEU_QUEUE_TIMEOUT` 10 s → 429 + `Retry-After` | ✅ |
| V2 | Routes `POST /v1/audio/speech` (Bearer), `GET /v1/voices`, `POST /v1/voices` (multipart voice-clone upload, ≤ 20 MB), `GET /v1/models`, `GET /health` (never auth; returns status, backend, max_streams, active, waiting, sample_rate; ready only after model load ≈ 19 s CPU). 401 body `{"error":{"message":"invalid api key",…,"code":401}}`. 400 for format ∉ {pcm, wav}, bad `stream_format`, bad `sample_rate` (allowed 48000/24000/16000/8000), unknown voice. Default format `wav`, native 48 kHz | ✅ |
| V3 | Mac install `uv sync` (torch-free ONNX, CPUExecutionProvider only, threads = min(cpu/2, 8) = 5 on M4, not configurable); start `uv run python -m apps.openai_speech`; `.python-version` 3.12 | ✅ |
| V5 | Weights `pnnbao-ump/VieNeu-TTS-v3-Turbo` subfolder `onnx_update` (fp32) + codec `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano`, downloaded by `hf_hub_download` **without `revision` and without `cache_dir`** → honours `HF_HOME`, always resolves `main`. Server loads `mode="v3turbo"`, `MODEL_ID = "vieneu-v3-turbo"`. Voices: 25 in SDK 3.8.3 (default "Hải Đăng"; "Minh Quân" alias), 23 on the HF card | ✅ |
| [new fact, not in README §1] | VieNeu codec licence and bundled-voice count disagreement, read from the GitHub/HF sources directly (not re-verified by the TL review): code Apache-2.0 (also stated by V1); weights/voices apache-2.0 per the HF model card, card allows monetised use; v4 proprietary (never used); GitHub README/bundled JSON say 25 voices (SDK 3.8.3), HF card says 23 (SDK 3.7.1) | agent://VieneuWhisperFacts (own scout run, 2026-09-24) — re-verify at the pin in T1 |
| 04 F3 | WAV header uses unknown length `0xFFFFFFFF` → `ffprobe` duration of a raw VieNeu WAV may be wrong | ✅ (plan 04) |
| 04 NFR-04-2 / 04 R1 | MPT kokoro request timeout 120 s is an **idle** timeout (no bytes for 120 s), not a total (04 CR-2); body ≤ 20,000 chars | ✅ (plan 04) |
| E1/E2 | FileVault internal disk for `$NTS_HOME`; ffmpeg 9.0.2 installed; port 8000 free on 2026-09-23 | ✅ |

**Open (⚠️, closed by the named task)**

| # | Question | Closed by |
|---|---|---|
| Q1 | Behaviour with `VIENEU_WATERMARK=1` and no `[watermark]` extra: startup failure, warning + off, or silent off (F10) | T4 |
| Q2 | Exact set of files VieNeu downloads from both repos (names under `onnx_update/`, codec files) | T1 (read from cache after first start) |
| Q3 | Whether `GET /v1/voices` and `POST /v1/voices` require the Bearer key (V2 lists auth only on speech) | T3 (observed status recorded) |
| Q4 | `/health` field values (`backend` string for ONNX) and `Content-Type` of WAV responses | T3 |
| Q5 | Whether `uv run` without `--extra watermark` removes an installed extra (exact vs inexact sync) | T4, only if the extra is installed |
| Q6 | Whether MPT sends a whole script as one TTS request (F01; `voice.py` not read) | plan 04 / 05 spike; T4 gate assumes worst case (one request) |
| Q7 | Whether the codec path is configurable (needed for a fully local `VIENEU_ONNX_DIR` alternative) | not pursued unless the HF-offline route fails (T1) |
| Q8 | Whether server logs contain request text or headers | T3 (grep log for the key, count only) |

## 3. Domain and data

No database. Artifacts and where they live (D1):

| Artifact | Tracked? | Path | Written by | Content |
|---|---|---|---|---|
| Pins | yes | `worker-node/pins.env` | 01.1.1-T2 (all D2 keys incl. `VIENEU_MODEL_REVISION`, `VIENEU_CODEC_REVISION`, resolved from the HF `revision/main` API before any download). **T1 only verifies**; a mismatch re-runs the 01.1.1-T2 procedure (no hand edit) | 40-char SHAs (D2) |
| Weight checksums | yes | `worker-node/vieneu/weights.sha256` [DECISION, new name] | T1 (`fetch-vieneu-weights.sh --record`) | one line per file: `<sha256>  <repo>@<revision>/<path-in-repo>`; copied/linked into `CARRY-OVER.md` by 01.3.3-T1 |
| Env template | yes | `worker-node/vieneu/env.example` | T2 | non-secret env vars with values (§7) |
| Voice snapshot | yes | `worker-node/vieneu/voices.snapshot.json` | T3 | raw `GET /v1/voices` body, pretty-printed with sorted keys |
| Fixtures | yes | `trial/fixtures/tts-vi-1000.txt`, `tts-en-1000.txt`, `tts-vi-5min.txt`, `tts-en-5min.txt` | T4 | original text, UTF-8 NFC, LF, no markup |
| Baseline | yes | `trial/baselines/vieneu-mac-<YYYYMMDD>.json` | T4 (`trial/tools/bench-vieneu.sh`) | schema below |
| Clone + venv | no | `$NTS_HOME/src/vieneu/` (+ `.venv`) | 01.1.2-T3 `bootstrap.sh` | VieNeu @ `VIENEU_COMMIT` |
| Weights | no | `$NTS_HOME/hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/`, `…/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano/` | first start (T1) | HF cache layout (`refs/main`, `snapshots/<sha>/`, `blobs/`) |
| Logs | no | `$NTS_HOME/logs/vieneu-<YYYYmmdd-HHMMSS>.log` | T2 | server stdout/stderr + readiness line |
| WAV samples | no | `$NTS_HOME/runs/samples/vieneu-<lang>-<fixture>.wav` | T4 | inputs for 04.1.1 |
| Bench raw data | no | `$NTS_HOME/runs/samples/bench-<YYYYMMDD-HHMM>/` | T4 | per-request WAV, `rss.csv`, `curl.tsv` |

**Baseline JSON (`vieneu-mac-<YYYYMMDD>.json`, schema_version 1)** [DECISION]

```json
{
  "schema_version": 1,
  "component": "vieneu",
  "tag": "plain|watermark-extra",
  "date": "2026-09-24T09:30:00Z",
  "host": {"model": "Mac M4", "ram_gb": 16, "macos": "<sw_vers -productVersion>", "cpu_count": 10},
  "pins": {"vieneu_commit": "<40>", "model_repo": "pnnbao-ump/VieNeu-TTS-v3-Turbo", "model_revision": "<40>",
           "codec_repo": "OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano", "codec_revision": "<40>"},
  "server": {"backend": "onnx", "precision": "fp32", "max_streams": 1, "health": {"…": "raw /health body"}},
  "watermark": {"env": "1", "extra_installed": false, "effective": "on|off|unknown", "evidence": "<log line or observation>", "decision": "<text>"},
  "cold_start_s": [19.4, 18.9],
  "idle_rss_mb": 0,
  "runs": [
    {"fixture": "tts-vi-5min.txt", "lang": "vi", "chars": 0, "units": 0, "unit_kind": "syllables|words",
     "voice": "<id>", "repeat": 1, "http_status": 200, "bytes": 0,
     "voice": "<id>", "repeat": 1, "http_status": 200, "bytes": 0,
     "wall_s": 0.0, "ttfb_s": 0.0, "audio_s": 0.0, "audio_s_source": "decoded|byte-count", "ffprobe_duration_s": 0.0, "rtf": 0.0, "units_per_min": 0.0, "peak_rss_mb": 0}
  ],
  "summary": {"<fixture>": {"wall_s_median": 0.0, "audio_s": 0.0, "rtf_median": 0.0, "peak_rss_mb_max": 0}},
  "gate_120s": {"rule": "rtf_5min_max * 300 < 100", "rtf_5min_max": 0.0, "projected_wall_s": 0.0,
                "threshold_s": 100, "result": "pass|fail", "decision": "<text>", "decided_by": "<name>", "decided_on": "YYYY-MM-DD"},
  "samples": [{"path": "$NTS_HOME/runs/samples/vieneu-vi-1000.wav", "sha256": "<64>", "riff_size_hex": "ffffffff", "data_size_hex": "ffffffff"}],
  "notes": "no other heavy app running (D11); …"
}
```

Definitions: `wall_s` = curl `time_total`; `audio_s` = decoded sample count ÷ sample rate (not the WAV header, 04 F3); `rtf = wall_s / audio_s`; `units` = whitespace-separated tokens (vi = syllables, en = words; F22); RSS in MiB from `ps -o rss=` (KiB ÷ 1024) of the process listening on 8000.

## 4. Use cases (operator procedures)

| # | Procedure | Command | Result | Task |
|---|---|---|---|---|
| P1 | Install at pin | `worker-node/bin/bootstrap.sh vieneu` (01.1.2-T3; T1 does not re-implement clone/sync) | clone @ `VIENEU_COMMIT` (detached, HEAD verified), `.venv` from `uv.lock` | T1 |
| P2 | First (online) start + verify/record weights | one-off online start (T1 step 4), then `worker-node/bin/fetch-vieneu-weights.sh --record` | weights in `$NTS_HOME/hf`; `refs/main` = pins verified; `weights.sha256` written | T1 |
| P3 | Re-fetch at pin (rehearsal 01.1.1-T3 runs it right after `bootstrap.sh vieneu`; worker) | `worker-node/bin/fetch-vieneu-weights.sh` (default mode = `--fetch`) | listed files downloaded with `revision=<pin>`, sha256 verified, `refs/main` = pin | T1 |
| P4 | Start | `worker-node/bin/start-vieneu.sh` (foreground terminal) | `READY` line in log; `/health` 200 | T2 |
| P5 | Smoke | `worker-node/bin/smoke-vieneu.sh` or `worker-node/bin/smoke.sh` | `PASS`/`FAIL` lines, exit 0/1/2 | T3 |
| P6 | Baseline + gate | `trial/tools/bench-vieneu.sh` | baseline JSON, samples, gate result | T4 |
| P7 | Watermark decision | T4 steps 2–4 | recorded in baseline `watermark` | T4 |
| P8 | Stop | Ctrl-C in the service terminal | port 8000 free (`lsof -nP -iTCP:8000 -sTCP:LISTEN` empty) | T2 |

Exit codes for every script: `0` ok · `1` check failed · `2` usage/environment error (D9).

## 5. API and events

Upstream API as consumed (no new API is built here):

| Route | Auth | Expected on the Mac (smoke, T3) | Consumer |
|---|---|---|---|
| `GET /health` | none (V2) | 200 after model load; 000/connection refused before | `start-vieneu.sh` readiness, proxy FR-04-3 |
| `POST /v1/audio/speech` | Bearer `nts.vieneu.api_key` | 401 without/wrong key; 400 `mp3`; 400 unknown voice; 200 WAV 48 kHz | proxy (04.1.2), bench (T4) |
| `GET /v1/voices` | ⚠️ Q3 | 200 with key; body saved as snapshot | proxy (04.1.3), curation (04.2.1) |
| `POST /v1/voices` | ⚠️ Q3 | never called with a valid key; the smoke probe (T3 `voices-post-auth-probe`) sends a complete multipart request with a 0.5 s silent WAV without a key and with a wrong key → both must be `401` (SEC-03) | nobody (CR-2: proxy never forwards it) |
| `GET /v1/models` | ⚠️ | 200, lists `vieneu-v3-turbo` ⚠️ | none |

Request body used everywhere: `{"model":"vieneu-v3-turbo","input":<text>,"voice":<id>,"response_format":"wav"}`. No events.

## 6. UI

N/A — headless local service; no screens.

## 7. Jobs and integrations

**Runtime env (`worker-node/vieneu/env.example`, set by `start-vieneu.sh`)** [DECISION]

| Var | Value | Why |
|---|---|---|
| `HOST` | `127.0.0.1` | V1 default `0.0.0.0` (F09, D4) |
| `PORT` | `8000` | D4 |
| `VIENEU_API_KEY` | from Keychain `nts.vieneu.api_key` (not in the file) | V1 unset = open (D3) |
| `VIENEU_WATERMARK` | `1` | DECISIONS §5.3; effect decided in T4 (F10) |
| `VIENEU_BACKEND` | `onnx` | V1/V3, no torch on Mac |
| `VIENEU_DEVICE` | `cpu` | explicit; no MPS branch (V5 facts) |
| `VIENEU_PRECISION` | `fp32` | V1 int8 needs x86 VNNI (D11) |
| `VIENEU_MAX_STREAMS` | `1` | D11 |
| `HF_HOME` | `$NTS_HOME/hf` | V5, D1 |
| `HF_HUB_OFFLINE` | `1` | pin enforcement after T1 [DECISION] |
| `HF_HUB_DISABLE_TELEMETRY` | `1` | no outbound calls at run time |

`VIENEU_QUEUE`, `VIENEU_QUEUE_TIMEOUT` stay at upstream defaults (1, 10 s → 429); `VIENEU_ONNX_DIR` stays unset.

**Integrations**
- Hugging Face Hub: online only during T1 first start and `fetch-vieneu-weights.sh`; afterwards `HF_HUB_OFFLINE=1`. A missing file offline → server fails at startup (visible in log, no `READY`).
- Plan 04 proxy: calls `http://127.0.0.1:8000/v1` with `Bearer <VIENEU_API_KEY>`; one stream → a second concurrent request queues ≤ 10 s then 429 (V1). Proxy maps 429 (04 FR-04-5).
- MPT → proxy → VieNeu: MPT's 120 s is an idle timeout (04 R1); the proxy streams PCM → ffmpeg → MP3, so the budget is time to first byte and the largest gap between bytes, not total wall time (04 D4) (04 CR-2).
- **120 s gate (F01) — informational (04 CR-2):** worst case assumed = whole 5-min story in one request (Q6). `result = pass` if `max(rtf_vi_5min, rtf_en_5min) × 300 s < 100 s` (i.e. RTF < 0.333), else `fail`; the result stays recorded as a capacity input for plan 04 and does not block anything. The "fail → chunking" branch is withdrawn (owner-approved 2026-09-24, 04 O1); the pass/fail check moves to 04.1.2-T3: `ttfb_ms` < 30,000 and `max_gap_ms` < 60,000 on the 5-min fixtures through the proxy. Recording the result is part of **01-ready** (README §7) (04 CR-2).

## 8. Security and privacy

| Threat | Control | Verified by |
|---|---|---|
| Server reachable from LAN (upstream default `0.0.0.0`, V1) | `HOST=127.0.0.1` from tracked `env.example`, guarded in `start-vieneu.sh` (exit 2 on any other value); child started with `env -i` + explicit list, so a caller's `HOST` is ignored | T2 guard checks; T3 `bind-loopback` (`lsof` = `127.0.0.1:8000` only) and `lan-refused` (curl exit 7); `check-binds.sh` (D4) |
| Open API without key (V1) | Start script exits 2 if `secret nts.vieneu.api_key` is empty or shorter than 32 chars (created by `secrets.sh init-local`, 32 random bytes hex) | T3 `speech-noauth`, `speech-wrongkey` → 401 |
| Voice-clone upload `POST /v1/voices` (F09) — anyone who can reach it can store arbitrary voices and later synthesise with them | loopback bind + key; never called with a valid key; proxy never forwards it (CR-2). A decisive probe (complete multipart, no key / wrong key → `401`) replaces the old "not 2xx" check, which a `422` would pass without proving auth (SEC-03). If the pinned handler has no auth, a local filter in front of :8000 is required before 01-ready | T3 `voices-post-auth-probe` |
| Non-loopback or IPv6 listener slips past a denylist (SEC-05) | `check-binds.sh` (owned by T3) is an allowlist over every D4 port and over all LISTEN sockets of the service process trees; IPv6 LAN probe `lan6-refused` | T3 `binds-all`, `lan6-refused` |
| `/health` unauthenticated (V2) | accepted: exposes only status/backend/queue counts, loopback only | — |
| Key leak via argv/history/logs | key read with `security find-generic-password … -w` inside scripts only, passed via child env (`VIENEU_API_KEY`) and to curl via `-H @<(printf 'Authorization: Bearer %s\n' "$KEY")`; no `set -x`; scripts never echo it | T3 `log-no-key`: `grep -cFf <(printf '%s\n' "$KEY")` (pattern from an fd, never in argv, SEC-08) = 0; T3 lint forbids `grep -cF -- "$…"` |
| Key visible in process environment | accepted on a single-user Mac [INFERENCE: `ps -E` shows env of own processes to the same user only] | — |
| Supply chain (unpinned weights: `hf_hub_download` resolves `main`, V5) | commit pin + `uv sync --frozen`; weight revisions pinned + sha256 (`weights.sha256`); offline at run time | T1 acceptance, `start-vieneu.sh` `refs/main` guard |
| Request text in logs (privacy, Q8) | fixtures are original, non-personal text; logs stay in `$NTS_HOME/logs` (FileVault disk, not tracked) | T3 observation recorded |
| AI-voice disclosure | `VIENEU_WATERMARK=1`; actual effect determined in T4 (F10) | T4 |

Retention: samples and logs in `$NTS_HOME` kept until 04.1.1 and 05.5.2 are done, then deleted at M0 close with the rest of `$NTS_HOME/runs` (01.3.3). Licences: Apache-2.0 code (V1) and weights (DECISIONS.md:292 §10.9); codec licence is a new fact (row above), re-read `LICENSE` and both HF cards at the pin in T1.

**F26/D13 — code that reads secrets lives on the untrusted volume.** `worker-node/bin/*`, `trial/tools/*` and `.githooks/` are tracked in this repo on `/Volumes/Project` (E1: ownership disabled, unencrypted). Until D13's ownership/encryption step or its `git archive` fallback lands (01.1.1-T1/01.1.2-T3, outside this folder), `start-vieneu.sh`, `smoke-vieneu.sh`, `fetch-vieneu-weights.sh` and `bench-vieneu.sh` inherit whatever execution-location guarantee 01.1.2-T2's `secret()` helper enforces (`lib.sh` per D13 refuses to read a secret off an untrusted location); this feature adds no separate control — it depends on D13's `lib.sh` behaviour and does not duplicate it.

## 9. Test plan

| Kind | Behaviour / boundary | Where |
|---|---|---|
| Install | HEAD = `VIENEU_COMMIT`; `.venv` Python 3.12; no torch in venv without the extra | T1 checks |
| Pin | `refs/main` of both repos = pins; sha256 of every cached file matches `weights.sha256`; server starts with `HF_HUB_OFFLINE=1` | T1, T2 |
| Readiness | `/health` 200 only after load; `READY after <n>s` logged; timeout 120 s → exit 1 | T2 |
| Guard | start refuses: port busy (exit 2), key missing (exit 2), clone at wrong commit (exit 2), revision empty (exit 2), `refs/main` ≠ pin (exit 2) | T2 |
| Auth | no Bearer → 401; wrong Bearer → 401; `/health` without auth → 200 | T3 |
| Validation | `mp3` → 400; unknown voice → 400; `wav` → 200 `RIFF…WAVE`, 48000 Hz | T3 |
| Exposure | loopback-only bind; LAN IP refused; clone endpoint not open | T3 |
| Drift | voice count recorded (expect 25); snapshot diff vs tracked file → FAIL | T3 |
| Performance | RTF, wall, RSS, cold start vi/en 1,000-char and 5-min; 120 s gate | T4 |
| Watermark | extra absent/present behaviour, RAM delta if needed | T4 |
| Handoff | WAV samples readable by `ffmpeg -f null` (exit 0) — full decode proof is 04.1.1 | T4 |

No automated test suite: these are operator scripts; `smoke-vieneu.sh` is the regression check re-run by `smoke.sh` before every trial run.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | File |
|---|---|---|---|---|
| 01.2.1-T1 | Install at pin + weight pins + licence check | XS 0.25 | 01.1.1-T2, 01.1.2-T2, 01.1.2-T3 | `01.2.1-T1-install-and-weight-pins.md` |
| 01.2.1-T2 | Start script + `env.example` | XS 0.25 | T1 | `01.2.1-T2-start-script.md` |
| 01.2.1-T3 | Smoke script + voice snapshot + bind check | S 0.5 | T2 | `01.2.1-T3-smoke-script.md` |
| 01.2.1-T4 | Baseline RTF/RAM, 120 s gate (informational, 04 CR-2), watermark, WAV handoff | S 0.5 | T2, T3 | `01.2.1-T4-baseline-gates-handoff.md` |
| **Total** | | **1.5 d** | | = README §9 detailed |

High-level 1.0 d (01 §6: S + XS + XS; story M) → 1.5 d (+0.5: RTF gate F01, watermark F10). Size risk: T1 now includes `fetch-vieneu-weights.sh` (pin enforcement for a library that downloads without `revision`, V5); kept at XS as README §9, but it may overrun to S if the HF offline lookup needs the `refs/main` write (T1 ⚠️). T4 at S assumes the watermark extra is not needed; installing it and re-measuring adds ≈ 0.25 d.

New tracked names introduced (D1/D9 compliant): `worker-node/bin/start-vieneu.sh`, `worker-node/bin/smoke-vieneu.sh`, `worker-node/bin/fetch-vieneu-weights.sh`, `worker-node/vieneu/weights.sha256`, `trial/tools/bench-vieneu.sh`, `trial/fixtures/tts-{vi,en}-{1000,5min}.txt`. Untracked: `$NTS_HOME/runs/samples/`.

## 11. Rollout

- **Order:** T1 → T2 → T3 → T4 on day 1 (README §5); T4 gate decided the same day. Outputs unblock 04.1.1 (samples), 04.1.2/04.1.3 (running server, snapshot), 01.2.2-T6 (wiring render).
- **No feature flag.** Switches that exist: `HF_HUB_OFFLINE` (only `fetch-vieneu-weights.sh` runs online), watermark extra (T4 decision).
- **Monitoring:** service log in `$NTS_HOME/logs/vieneu-*.log`; `smoke.sh` before each trial run; `/health` `active`/`waiting` during runs.
- **Rollback:**
  - Bad commit pin → restore previous `VIENEU_COMMIT` in `pins.env` (01.1.1-T2 procedure), re-run `bootstrap.sh vieneu` (checkout + `uv sync --frozen`).
  - Corrupt venv → `rm -rf "$NTS_HOME/src/vieneu/.venv"` then `bootstrap.sh vieneu`.
  - Bad weights → `rm -rf "$NTS_HOME/hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo" "$NTS_HOME/hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano"` then `fetch-vieneu-weights.sh` (pinned). Re-pinning = 01.1.1-T2 procedure, then T1 steps 4–6.
  - Watermark extra removed → `uv sync --frozen` in `$NTS_HOME/src/vieneu` and revert the `--extra watermark` flag in `start-vieneu.sh`.
- **Carry-over (01.3.3):** pins, `weights.sha256`, `env.example`, `voices.snapshot.json`, baseline JSON, gate decision, watermark decision; Mac → node differences (ONNX-CPU → PyTorch CUDA `--extra cuda`, `VIENEU_MAX_STREAMS`, bind to private interface; CR-9).
