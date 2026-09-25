# nts-vieneu-proxy

Loopback-only OpenAI-compatible `POST /v1/audio/speech` proxy in front of
VieNeu-TTS (`127.0.0.1:8000`). Streams upstream PCM through ffmpeg and returns
MP3 to MPT. Contract: `plans/features/04-voice-proxy/README.md` (D1–D14).

## Layout

- `src/nts_vieneu_proxy/` — package; entry point `python -m nts_vieneu_proxy`.
- `tests/` — pytest suite (httpx.MockTransport fake VieNeu, real ffmpeg, wire
  tests over real uvicorn + requests).
- `../../seed/voices.v1.json` — voice catalog the proxy allow-lists.

## Environment

All configuration is environment variables (04 README D13):

| Env var | Default | Rule |
|---|---|---|
| `PROXY_HOST` / `PROXY_PORT` | `127.0.0.1` / `8001` | host must be `127.0.0.1` |
| `PROXY_API_KEY` | — | required, ≥ 32 chars |
| `VIENEU_BASE_URL` | `http://127.0.0.1:8000` | must be `http://127.0.0.1:<port>` (no path) |
| `VIENEU_API_KEY` | — | required, ≥ 32 chars |
| `VOICE_CATALOG_FILE` | — | required; valid `voices.v1` |
| `MP3_BITRATE` | `192k` | ∈ {`128k`, `160k`, `192k`} |
| `UPSTREAM_CONNECT_TIMEOUT_S` | `5` | 1–30 |
| `UPSTREAM_READ_GAP_S` | `60` | 10–100 |
| `FIRST_AUDIO_DEADLINE_S` | `90` | 10–100, > `ADMISSION_WAIT_S` |
| `ADMISSION_WAIT_S` | `80` | 0–80 |
| `REQUEST_MAX_S` | `1800` | 60–3600 |
| `PROXY_MAX_CONCURRENT` | `4` | 1–32 (Mac start script sets 2) |
| `CATALOG_CHECK_INTERVAL_S` | `300` | 30–3600 |
| `FFMPEG_PATH` | `ffmpeg` | resolved via `shutil.which` at startup; missing → health `ffmpeg_missing`, speech 500 `transcode_failed` (D8) |

Invalid config → exit 2 naming the variable (never its value). `env.example` lists every
key with its default; secrets stay blank — they come from the Keychain
(`secrets.sh init-local`, Mac) or the node secret store (`/etc/nts/vieneu-proxy.env`, M1).

## Operations (Mac)

- Start: `worker-node/bin/start-proxy.sh` (foreground, tee'd to
  `$NTS_HOME/logs/proxy-<YYYYmmdd-HHMMSS>.log`; `--check` validates config + catalog
  without binding the port). Needs VieNeu on `127.0.0.1:8000` first.
- Smoke: `worker-node/bin/smoke-proxy.sh`, also picked up by `worker-node/bin/smoke.sh`.
- Install/update the venv: `worker-node/bin/bootstrap.sh proxy` (CR-4). Python pin
  `PROXY_PYTHON` lives in `worker-node/pins.env` (CR-3).
- Node (M1): `systemd/vieneu-proxy.service`, installed by 11.3.1-T2.

## Licences

`LICENSES.md` lists every resolved package (name, version, licence), generated from the
lock after `uv sync` (04.1.5-T1). AGPL-family and non-commercial licences are rejected —
swap the dependency, never vendor around it. Regenerate after any dependency change:

```sh
worker-node/bin/bootstrap.sh proxy   # refresh $NTS_HOME/src/proxy-venv
"$NTS_HOME/src/proxy-venv/bin/python" - <<'PY' > worker-node/proxy/LICENSES.md
import importlib.metadata as m
print("# nts-vieneu-proxy resolved-package licences (04.1.5-T1, from uv.lock via the synced venv)")
print()
print("Regenerate: see README.md, Licences section. A row whose licence is in the strong")
print("copyleft family or restricts commercial use fails the task — swap the dependency,")
print("never vendor around it.")
print()
print("| Package | Version | Licence |")
print("|---|---|---|")
for d in sorted(m.distributions(), key=lambda d: d.metadata["Name"].lower()):
    name = d.metadata["Name"]
    lic = (d.metadata.get("License-Expression") or d.metadata.get("License") or "").strip()
    if not lic:
        lic = next((c.split("::")[-1].strip() for c in d.metadata.get_all("Classifier", [])
                    if c.startswith("License")), "?")
    if name == "nts-vieneu-proxy":
        lic = "(this package — internal, not distributed)"
    print(f"| {name} | {d.version} | {lic} |")
PY
```

Then verify `grep -icE 'AGPL|non-?commercial' worker-node/proxy/LICENSES.md` → `0`.

## Run

```sh
UV_PROJECT_ENVIRONMENT="$NTS_HOME/src/proxy-venv" \
    uv sync --frozen --project worker-node/proxy --python 3.12
UV_PROJECT_ENVIRONMENT="$NTS_HOME/src/proxy-venv" \
    uv run --frozen --project worker-node/proxy python -m nts_vieneu_proxy
```

## Test

```sh
UV_PROJECT_ENVIRONMENT="$NTS_HOME/src/proxy-venv" \
    uv run --frozen --project worker-node/proxy pytest -q
```

Never create a `.venv` inside the repo; `UV_PROJECT_ENVIRONMENT` is mandatory
for every `uv` invocation here (D14).
