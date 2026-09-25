# 04.1.3 — Voices Endpoint, Health, Packaging and Option B Note

> Refines: `../../../04-voice-proxy.md` §4 stories 04.1.3 (voices endpoint), 04.1.4 (health), 04.1.5 (packaging), 04.3.1 (option B note); FR-04-3, FR-04-6, NFR-04-1, NFR-04-4
> Contract: `../README.md` — **D1** (process/packaging), **D2** (routes/auth), **D6** (error codes), **D8** (`proxy-health.v1`), **D9** (voice catalog + voices route shape), **D13** (Mac vs node, env vars), **D14** (test kit); findings **F05**, **F07**, **F10**, **F15**, **F19**, **F22**; **CR-3**, **CR-4**, **CR-6**, **CR-7**, **CR-12**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs (README id cited) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.**
- 04.1.3 *Voices endpoint* — "As an operator I want `GET /v1/audio/voices` to return the active catalog voices that exist upstream, so I can verify parity between the catalog file and VieNeu without reading logs." Should · **XS (0.25)**.
- 04.1.4 *Health* — "As the node adapter I want `GET /health` to report `proxy-health.v1` with a specific reason for every failure mode, so dispatch can be gated on a working proxy." Must · **XS (0.25)**.
- 04.1.5 *Packaging* — "As an operator I want a licence-checked lock file, a Mac start/smoke script pair and a node systemd unit, so the proxy installs and runs the same way in both places." Must · **S (0.5)**.
- 04.3.1 *Option B (voxcpm) fallback note* — "As the Tech Lead I want a written fallback plan to the `voxcpm` MPT slot, so a VieNeu blocker does not stall the milestone while the decision to switch is made deliberately." Should · **XS (0.25)**.

**In scope.**
- `voice_cache.py` (`UpstreamVoiceCache`), the `GET /v1/audio/voices` route, and `VieneuClient.voices()`.
- `health.py` (`build_health`), the `GET /health` route, `VieneuClient.health()`, and the startup ffmpeg `-encoders` probe.
- `worker-node/proxy/` packaging: `uv lock` + licence table, `bootstrap.sh proxy` target, `worker-node/bin/start-proxy.sh`, `worker-node/bin/smoke-proxy.sh`, `smoke.sh` wiring, `worker-node/README.md` runbook section, `worker-node/proxy/README.md`, `worker-node/proxy/systemd/vieneu-proxy.service`, `worker-node/proxy/env.example`.
- `worker-node/proxy/FALLBACK-voxcpm.md` (04.3.1-T1): when to switch to `voxcpm`, what the proxy would change, consequences, what stays unverified.
- Route registration for the two GET routes into `app.py` (owned by 04.1.2-T1; this folder's tasks add their own router and register it, they do not create `app.py`).

**Out of scope.**
- The speech pipeline: `app.py` skeleton, `config.py`, `catalog.py`, `errors.py`, `upstream.py` `speech_stream`, `transcode.py`, `reqlog.py`, `__main__.py`, and the `POST /v1/audio/speech` route — all owned by `04.1.1-speech-endpoint/` (04.1.2-T1, 04.1.2-T2, 04.1.2-T3).
- Catalog curation and content: audition, selection, `seed/voices.v1.json` revision 1, `VoiceCatalogRules` (C#), `VoiceSeeder`, previews — all owned by `04.2-voice-catalog/`.
- The `mpt-adapter` itself (11.3.5-T1) and the node bundle/firewall (11.3.1) — this folder only hands over the exact fields the adapter must embed (D8) and blocks their tasks softly.
- Actually switching MPT to `voxcpm` — 04.3.1-T1 is a decision note only, not an implementation.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| L1 | VieNeu `GET /v1/voices` (auth) → `{ "object": "list", "data": [ { id, name, description, gender, featured, aliases } ] }`; `GET /health` (no auth) → `{ status, backend, max_streams, active, waiting, sample_rate }` ✅ | README V7 |
| L2 | `get_kokoro_voices` (MPT WebUI only, never the API render path) reads `{base_url}/audio/voices`, accepts a list or `{ "voices": [...] }` of strings or `{id}` objects ✅ | README M6 |
| L3 | The backend talks only to `mpt-adapter`; the adapter aggregates VieNeu, proxy and MPT health; the Worker never reaches the proxy directly ✅ | README P8 |
| L4 | MPT's API never reads the voice list (M6); "MPT fetches from the proxy" is not a render dependency, so 04.1.3-T1 stays XS and 01.2.2-T6's dependency on it becomes soft (CR-6) [finding] | README F15 |
| L5 | FastAPI serves `/docs`, `/redoc`, `/openapi.json` by default, breaking the 01 route allow-list; fixed once in `app.py` by `docs_url=None, redoc_url=None, openapi_url=None` (04.1.2-T1); this folder's routes must not reintroduce a doc route [finding] | README F05, D2 |
| L6 | Voice ids carry diacritics/spaces and VieNeu matches them byte-exactly (no NFC folding); an alias must never be treated as a catalog id [finding] | README F07, V9 |
| L7 | 09 §5.6 previously defined a backend `vieneu_proxy` integration with its own health call — withdrawn; the backend only ever sees `proxy-health.v1` embedded as `tts_proxy` inside the adapter's health (CR-8, CR-12) [finding] | README F10 |
| L8 | Plan 01 had no `PROXY_PYTHON` pin, no `proxy` bootstrap target and no proxy entry-point string for the node listener check — closed by D1 (`nts_vieneu_proxy`) and CR-3/CR-4/CR-6 [finding] | README F19 |
| L9 | Nothing off-host ever needs the proxy; loopback bind is required on the Mac **and** the node [finding] | README F22, D1 |
| L10 | `[voxcpm]` sends `{"response_format":"wav","stream":true}`, requires an `api_key`, parses SSE `speech.audio.delta` / `speech.audio.done`, and supports only **one voice per config** (`voice_id`) ✅ | `DECISIONS.md` §5.3 (`voice.py:2629-2713`, `voice.py:324-337`) |
| L11 | VieNeu's `stream_format` body field accepts `"sse"` (default `"audio"`); the `kokoro` proxy design (D3) never sends `stream_format: "sse"` — that is only relevant to the option B design ✅ | `DECISIONS.md` §5.3 |

**Closed here.** The exact `proxy-health.v1` reason set and its triggers (D8, this folder owns `health.py`). The voices route's shape and its "active ∩ upstream" filter (D9, this folder owns the route; the catalog domain object is 04.1.2-T1's). The Mac/node packaging split (D13) as runnable scripts and a unit file, not just an env-var table. The concrete option B switch-over note (D14/DECISIONS §5.3), left ⚠️ [OPEN] in the README itself.

## 3. Domain and data

No database, no new domain types. Two small in-process readers over state created by 04.1.2-T1 (`app.state.catalog`, a frozen `Catalog`; `app.state.settings`, a frozen `Settings`) plus two owned by this folder:

- `voice_cache.py`: `UpstreamVoiceCache(client: VieneuClient, interval_s: int)`. Internal state: `_names: frozenset[str]` (canonical VieNeu voice names only — `data[].id` (= `name`, README D9), never `data[].aliases`, per L6/D9), `_last_error: str | None` (D6 code of the latest failed refresh), `_ever_succeeded: bool`, `_last_refresh: float`. `async names()` returns the cached set (never blocks a request on a live upstream call); a background `asyncio.Task` started at app startup refreshes every `interval_s` (= `CATALOG_CHECK_INTERVAL_S`, D13). On a refresh failure, the previous `_names` is kept and `_last_error` is set (never an empty set from a transient failure; the speech allow-list is the catalog file, not this cache, so a refresh failure never rejects a speech request). Health reports the latest refresh failure by code (`vieneu_auth_failed` / `vieneu_unreachable`, README D8), even while stale names are cached.
- `health.py`: no stored state of its own; `build_health(state)` reads `app.state.settings`, `app.state.catalog`, `app.state.voice_cache`, `app.state.started_at`, `app.state.in_flight`, calls `VieneuClient.health()` live (2 s timeout) and reuses the ffmpeg probe result cached at startup (`app.state.ffmpeg_probe: FfmpegProbe` — a small frozen dataclass `(ok: bool, version: str | None, libmp3lame: bool)`, populated once by `04.1.4-T1`'s startup probe inside `__main__.py`, which is 04.1.2-T1's file; this folder specifies the probe's shape and `health.py` consumes it as a read-only attribute).

No migration, no seed file changes (`seed/voices.v1.json` is read-only from this folder's point of view).

## 4. Use cases

| # | Actor | Flow | Module |
|---|---|---|---|
| UC1 | Operator / smoke script | `smoke-proxy.sh` calls `GET /v1/audio/voices` with a valid key and asserts the response is non-empty and every `id` is a canonical VieNeu name | `worker-node/bin/smoke-proxy.sh`, `voices` route |
| UC2 | `mpt-adapter` (11.3.5, hand-over) | Polls `GET /health` before every dispatch decision; treats any non-`ok` `status` (503) as "do not send work to this node" | `health.py`, adapter (out of scope, consumes the contract) |
| UC3 | Operator (Mac) | Runs `worker-node/bin/start-proxy.sh`; script checks the port, loads keys from Keychain, execs the process in the foreground with a tee'd log | `start-proxy.sh` |
| UC4 | Operator (Mac) | Runs `worker-node/bin/start-proxy.sh --check` to validate config/catalog without binding the port (pre-flight before a long shift) | `start-proxy.sh` |
| UC5 | Ops (node, M1) | `systemctl enable --now vieneu-proxy` starts the same process under `vieneu-proxy.service`, hardened, `EnvironmentFile`-driven | `systemd/vieneu-proxy.service` |
| UC6 | Tech Lead | Consults `FALLBACK-voxcpm.md` when VieNeu is blocked, to decide whether/when to switch MPT to the `voxcpm` slot instead of `kokoro` | `FALLBACK-voxcpm.md` (04.3.1-T1) |

## 5. API and events

Both routes are already fixed by README **D2**; this folder implements them.

| Route | Method | Auth | Behaviour |
|---|---|---|---|
| `/v1/audio/voices` | GET | Bearer `PROXY_API_KEY` (D2) | 200 `{ "voices": [ { "id", "name", "language", "gender" } ] }` — catalog entries with `active = true` **and** `id ∈ UpstreamVoiceCache.names()` (D9); missing/wrong bearer → 401 `proxy_unauthorized` (D6); no VieNeu call on the request path (cache-only, L2/D9 "Reload = restart" for the catalog, but the upstream cache itself refreshes on its own interval) |
| `/health` | GET | none (loopback only, D2) | 200 when `status = "ok"`; else 503; body is exactly `proxy-health.v1` (D8, §7 below); never requires the bearer token, never echoes catalog voice ids beyond a `count` |

No domain events: the proxy is a private worker-node process (README P8); nothing here publishes to the backend. The adapter's own `/health` aggregation (11.3.5, CR-12) is out of scope — it is documented as a hand-over in `04.1.4-T1` §Notes with the exact embedding key `tts_proxy`.

## 6. UI

Not applicable. This folder has no user-facing surface: the only consumer of `GET /v1/audio/voices` is MPT's WebUI, which is not deployed (README M6, 01 D14); `GET /health` is read by the node adapter and by operators via `curl`/`smoke-proxy.sh`, not by any screen. The voice-picker UI (series form step 3, 10.4.x) is driven by the backend's `GET /voices` (09.4.2-T1) and previews, both owned by `04.2-voice-catalog/`.

## 7. Jobs and integrations

| Job / integration | Trigger | Detail |
|---|---|---|
| `UpstreamVoiceCache` refresh | background `asyncio` loop, every `CATALOG_CHECK_INTERVAL_S` (300 s, D13) | httpx call to VieNeu `GET /v1/voices`; failure keeps the previous set and records `last_error` (never used to fail a request; feeds health's `vieneu_auth_failed` / `vieneu_unreachable` by code and, after a successful refresh, `catalog_voice_missing`) |
| ffmpeg startup probe | once, at process start (`__main__.py`, hand-over to 04.1.2-T1's file; this folder specifies `ffmpeg -hide_banner -encoders` and the `libmp3lame` substring check) | populates `app.state.ffmpeg_probe`; failure does **not** stop startup (the process must still answer `/health` with `ffmpeg_missing` so the adapter can see the fault) |
| `bootstrap.sh proxy` target | operator/CI, once per machine (01.1.2-T3, CR-4) | `UV_PROJECT_ENVIRONMENT="$NTS_HOME/src/proxy-venv" uv sync --frozen --project worker-node/proxy --python "$PROXY_PYTHON"` |
| `start-proxy.sh` | operator, every Mac session (D13) | foreground child process (not `exec`'d), tee'd log, exit code = the proxy's, refuses to start if VieNeu is not listening on 8000 |
| `smoke-proxy.sh` | operator / `smoke.sh` aggregator (01.2.1-T3) | scripted HTTP checks against a running proxy; see 04.1.5-T1 |
| `vieneu-proxy.service` | systemd, node boot / `Restart=on-failure` (M1, D13) | same process, `EnvironmentFile=/etc/nts/vieneu-proxy.env` |
| Adapter health embedding | 11.3.5-T1 (hand-over, CR-12) | adapter's own health calls this proxy's `GET /health` and embeds the JSON verbatim under `tts_proxy`; dispatch is gated on `tts_proxy.status = "ok"` |

## 8. Security and privacy

| Threat | Control |
|---|---|
| `GET /health` has no auth (D2) but is reachable off-loopback | Bind check refuses any host but `127.0.0.1` (D1, `config.py`, hand-over); `start-proxy.sh`/systemd never publish 8001 beyond loopback |
| `GET /health` body leaking secrets | Body is exactly `proxy-health.v1` (D8): counts, booleans, versions — never a key, never `input` text, never a raw upstream error string |
| Keys in process args or logs during packaging/start | `start-proxy.sh` reads `nts.proxy.api_key` / `nts.vieneu.api_key` from Keychain into the **child environment only** (`env VAR=val exec …`, never `argv`, never `echo`); `smoke-proxy.sh` passes the key via `-H @<(printf 'Authorization: Bearer %s\n' "$KEY")` (never `curl -H "Authorization: Bearer $KEY"` on the command line); systemd's `EnvironmentFile` is `0600`, owned by `nts-proxy`, never logged |
| Supply-chain risk from new runtime/test dependencies | `uv lock`-derived licence table checked in 04.1.5-T1; any AGPL or non-commercial licence fails the task (reject, do not vendor) |
| Node process privilege | systemd hardening (D13): `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`, `IPAddressDeny=any` + `IPAddressAllow=127.0.0.1 ::1` (11 CR-6), dedicated `User=nts-proxy` |
| `GET /v1/audio/voices` leaking non-active or upstream-only voices | Route filters to `active = true` **and** present upstream (D9); ids never expose upstream-only aliases (L6) |
| Option B note recommending an unverified path | `FALLBACK-voxcpm.md` explicitly marks the WAV-decode and SSE-parsing details ⚠️/[OPEN] (DECISIONS §8 item 6); it is a decision aid, not an approved cutover |

Retention: proxy request logs (D7, owned by 04.1.2-T1) never log voices/health traffic bodies beyond the standard fields; `/health` responses are not persisted anywhere by this folder.

## 9. Test plan

- **Unit (`worker-node/proxy/tests/test_voices_*.py`):** cache staleness on upstream failure, alias exclusion, active∩upstream filter, auth.
- **Unit (`test_health_*.py`):** one test per D8 reason code and its trigger; 200 vs 503 boundary; no-auth-required.
- **Wire (`worker-node/proxy/tools/mpt_decode_check.py` not used here — N/A):** none needed; voices/health carry no audio.
- **Smoke (`smoke-proxy.sh`, shell, named `PASS`/`FAIL`/`WARN`/`INFO` rows):** health schema and 200/503, auth 401 (no/wrong key) on the speech and voices routes, 404 on `/docs`/`/openapi.json`, 405 on a wrong method, voices non-empty, listener command contains `nts_vieneu_proxy`, bind check 127.0.0.1:8001 only. Full row list in `04.1.5-T1`.
- **Manual:** `FALLBACK-voxcpm.md` is reviewed by the Tech Lead for accuracy against `DECISIONS.md` §5.3; no automated test (it is documentation).

Named tests are listed per task in their own `## Tests` sections; this section is the index, not a duplicate.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 04.1.3-T1 | Voices endpoint + upstream voice cache | Worker | **XS (0.25)** | 04.1.2-T1 | 04.1.4-T1, 04.1.5-T1, 01.2.2-T6 (soft, CR-6) |
| 04.1.4-T1 | Health endpoint `proxy-health.v1` | Worker | **XS (0.25)** | 04.1.2-T1, 04.1.3-T1 | 04.1.5-T1, 11.3.5-T4 (CR-12; 11 CR-6) |
| 04.1.5-T1 | Packaging: lock + licences, bootstrap target, start/smoke scripts, systemd unit | Worker/Ops | **S (0.5)** | 04.1.2-T2, 04.1.3-T1, 04.1.4-T1, 01.1.2-T3, 01.2.1-T3 | 04.1.2-T3, 04.2.3-T1, 01.2.2-T6, 11.3.1-T2 (11 CR-6); 01.2.2-T4 soft |
| 04.3.1-T1 | Option B (voxcpm) fallback note | BA/Worker | **XS (0.25)** | 04.1.1-T1 | — |

Total **1.25 d** — matches README §8 (feature index) and §9 (re-estimate row "04.1.3–04.1.5 voices, health, packaging | 1.0" + "04.3.1 option B note | 0.25").

### Open items for the TL

- [OPEN] README §9 lean option: "Drop 04.1.3-T1 (voices endpoint; nothing in the render path reads it, F15): −0.25 d." Not applied here — the task stays because the owner has not exercised this lean option (§10 O3 accepts the 5.5 d re-estimate as-is). If the owner later takes the −0.25 d cut, `04.1.4-T1`'s `catalog_voice_missing` check must then compare directly against `VieneuClient.voices()` instead of `UpstreamVoiceCache` (`04.1.4-T1` already depends on `04.1.3-T1` for exactly this reuse; dropping T1 would move the cache into `04.1.4-T1` at no net size change, per the README's own note "Health would then check the catalog against VieNeu directly (unchanged cost)").
- ✅ Resolved by 11 D8 (11 CR-6): node paths are fixed (`/opt/nts/current/venvs/proxy`). Previously: the README's D13 systemd path table was marked ⚠️ "until 11.3.1 fixes the node layout (CR-12)". `04.1.5-T1` writes the unit file with the paths given in D13 verbatim; if 11.3.1 changes the node layout, the unit file needs a follow-up edit owned by 11.3.1, not by this folder.
- [OPEN] Licence table (D1: "all expected MIT/BSD/Apache ⚠️") is unverified until `uv lock` is actually run in `04.1.5-T1`; this plan cannot pre-confirm licences of not-yet-resolved transitive dependencies.

## 11. Rollout

- **Order:** `04.1.2-T1` → `04.1.3-T1` → `04.1.4-T1` → `04.1.5-T1` (M0, Mac). `04.3.1-T1` runs in parallel any time after `04.1.1-T1`'s spike evidence exists. The node systemd unit (`04.1.5-T1`'s `systemd/vieneu-proxy.service`) is written at M0 but only *activated* at M1, gated by `11.3.1` (node bundle) and `11.3.5-T1` (adapter health embedding).
- **Flag:** none of this folder's routes are feature-flagged; `/v1/audio/voices` and `/health` are always registered (D2 is a fixed contract, not conditional).
- **Config:** `CATALOG_CHECK_INTERVAL_S` (D13, default 300 s) tunes the voice-cache/health refresh cost; `PROXY_MAX_CONCURRENT=2` is set by `start-proxy.sh` on the Mac specifically (D13) and left at the D13 default (4) in the systemd `EnvironmentFile` unless the node sizing rule (D5, CR-12) says otherwise.
- **Monitoring:** none new beyond the existing D7 log line (owned by 04.1.2-T1) and the D8 health body itself, which the adapter polls (11.3.5, out of scope). No metrics endpoint exists in P (README does not define one for the proxy).
- **Rollback:** `smoke-proxy.sh`/`start-proxy.sh` failing `--check` blocks the operator from starting a broken build; on the node, `systemctl stop vieneu-proxy` plus the adapter's existing "node unhealthy → stop dispatch" behaviour (11.3.3, out of scope) is the rollback path. No data migration exists to roll back (no database).
