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

All configuration is environment variables (D13 table). Required:
`PROXY_API_KEY`, `VIENEU_API_KEY` (each ≥ 32 chars), `VOICE_CATALOG_FILE`.
Defaults: `PROXY_HOST=127.0.0.1` (refuses anything else, exit 2),
`PROXY_PORT=8001`, `VIENEU_BASE_URL=http://127.0.0.1:8000`,
`MP3_BITRATE=192k`. `FFMPEG_PATH` defaults to `ffmpeg` resolved via
`shutil.which`; a missing binary is **not** a startup error — speech then
answers 500 `transcode_failed`.

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
