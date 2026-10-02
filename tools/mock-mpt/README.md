# `tools/mock-mpt` — the fake render node

`NightTales.MockMpt` serves the **real** `render-node.v1` surface (the `NightTales.RenderNode.Server` core, 11
README D5) over an in-memory backend: the seven routes of 05 §2 plus the core's catch-all, a task that walks
`queued → running → checking → succeeded` over `MOCK_RENDER_SECONDS`, canned per-aspect media, an SRT derived
from the submitted `video_script`, valid `render-status.v1`/`render-node-health.v1` bodies and the 05 D14
fixtures. **No MPT, GPU, OpenAI or egress** (11 README D2/D3/D14; 05 D4/D14).

It is a **dev/staging-only host**: never in the node bundle (D12), never a production render node (11 CR-16).

## Run it

**Native (the daily path, 11 CR-16).** `tools/dev/up.sh` starts it for you on `127.0.0.1:8090` with
`ASPNETCORE_URLS=http://127.0.0.1:8090`, `MOCK_API_KEY` and the `MOCK_*` rows from `.env`. By hand:

```bash
MOCK_API_KEY=dev-mock-api-key-local-0123456789 \
ASPNETCORE_URLS=http://127.0.0.1:8090 \
  dotnet run --project tools/mock-mpt
```

The loopback bind is the point: the mock must not be reachable from a café LAN. `lsof -nP -iTCP:8090 -sTCP:LISTEN`
must show `127.0.0.1:8090` only.

```bash
printf 'x-api-key: %s\n' "$MOCK_API_KEY" | curl -fsS -H @- http://127.0.0.1:8090/v1/health
```

**Compose (optional; e2e / image smoke, 11 CR-16).** `docker compose up -d --wait mock-mpt` builds
`tools/mock-mpt/Dockerfile` and serves the mock as `http://mock-mpt:8090` **on the compose network only**
(`expose`, never `ports`; `docker compose ps --format '{{.Ports}}'` shows no host port for it). The fixtures are
a read-only bind mount, so a fixture edit needs no rebuild (README §10 T33). The daily path stays native; never
run both at once. The service's healthcheck runs the mock's own `--healthcheck` mode
(`dotnet NightTales.MockMpt.dll --healthcheck`): one authenticated `GET /v1/health` with the same key
resolution the server uses, so the runtime image needs no `curl` and a custom `MOCK_API_KEY` is probed with the
real value. The key travels as a request header, never on the command line (01 D9).

**Staging.** The same image can be a real node to the Worker: run it with
`ASPNETCORE_ENVIRONMENT=Staging` (never Development) and HTTPS on 8090 with its own self-signed certificate,
pinned in the staging `RenderNodes:Nodes` config like a real node [RULED — 11 README §10 T44] (D20 H2).
`http://127.0.0.1:8090` and `http://mock-mpt:8090` are **Development-only** values: outside Development
`RenderNodeConnectionFactory` refuses an `http://` node or a missing `TlsCertSha256` (D13/F26).

Kestrel inside a container binds all interfaces because a container cannot bind the host's `127.0.0.1`; the
compose service publishes nothing, so the adapter's `ListenAddress` guard (D6) does not apply to it. **Do not
copy that onto a real node.**

## Environment

| Variable | Default | Meaning |
|---|---|---|
| `MOCK_API_KEY` | `dev-mock-api-key-local-0123456789` | The one `x-api-key` the core compares, constant-time (≥ 32 bytes, 05 D15 S3). `RenderNodeServer__ApiKey` is accepted as the second spelling. |
| `MOCK_RENDER_SECONDS` | `20` | Wall-clock seconds one task takes to reach a terminal state (≥ 1, §10 T32). |
| `MOCK_VIDEO_SECONDS` | `10` | The **reported** `narration_seconds`. The served file stays the canned 10 s one — see below. |
| `MOCK_MAX_CONCURRENT` | `2` | The node's `capacity.max_concurrent_tasks` (`RenderNodeServer__MaxConcurrentTasks` is the second spelling). |
| `MOCK_FIXTURE` | *(blank)* | A D14 fixture id every task replays (`render-status.v1.*`) or the health body replays (`render-node-health.v1.*`). An unknown id is exit 2. |
| `MOCK_FIXTURES_DIR` | `worker-node/contract/fixtures` in the working tree | Where the D14 fixtures are read from (`/fixtures` in the image). A missing or empty directory is exit 2. |
| `MOCK_MEDIA_DIR` | `${NTS_HOME:-<repo>/.nts}/mock-mpt/media` | Where the canned media lives and each task's SRT is written (`/media` in the image). |
| `NTS_HOME` | `<repo>/.nts` | The runtime-state root (01 CR-11). When `MOCK_MEDIA_DIR` is unset the media/SRT directory resolves under it, so a custom `NTS_HOME` never writes into the working tree. `tools/dev/up.sh` passes it to the native mock. |
| `MOCK_HEALTH_URL` | `http://127.0.0.1:8090/v1/health` | The URL the `--healthcheck` mode probes (the container healthcheck); the app otherwise ignores it. |
| `ASPNETCORE_URLS` | `http://127.0.0.1:8090` | The binding; the image sets `http://+:8090`. |

`MOCK_FAIL_RATE`, `MOCK_FAIL_MODE` and `MOCK_IMAGE_REPORT` are 11.1.2-T2's (failure injection); the compose
service passes them so one `.env` drives both, and this task's mock ignores them.

**Every invalid value is a named message and exit code 2** — a listener never opens on a configuration nobody
can use. `MOCK_API_KEY` shorter than 32 bytes, `MOCK_RENDER_SECONDS`/`MOCK_VIDEO_SECONDS` < 1,
`MOCK_MAX_CONCURRENT` < 1, an unknown `MOCK_FIXTURE`, a missing fixtures directory.

## Fixtures

Loaded once at start from `MOCK_FIXTURES_DIR` with `RenderNodeJson.Strict` (unknown members and duplicates
rejected, 11 README D4/E18). A submit whose free text carries `__fixture_<id>__` replays that status fixture,
and `MOCK_FIXTURE=<id>` makes every task (or the health body) do the same; the keyword wins over the
environment. The served body is the fixture **verbatim except the fields the contract generates for the
replaying task** — `task_id`, `render_job_id` and the timestamps (the per-fixture ignore list lives in
`tests/NightTales.MockMpt.Tests/TestHost/JsonComparison.cs`).

```bash
curl -s -H "x-api-key: $MOCK_API_KEY" -H 'content-type: application/json' \
  --data-binary @worker-node/contract/fixtures/render-submit.v1.landscape-vi.json \
  http://127.0.0.1:8090/v1/renders
```

## Canned media and subtitles

The image build (and the first native start) generates `landscape.mp4` (10 s, h264/aac, 1920×1080) and
`portrait.mp4` (10 s, h264/aac, 1080×1920) with the pinned recipe:

```bash
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30 -f lavfi -i sine=frequency=440 \
  -t 10 -c:v libx264 -c:a aac -movflags +faststart landscape.mp4
```

**Nothing binary is committed** (`*.mp4` is git-ignored; the native run writes into `.nts/`). The native run
needs `ffmpeg` on the PATH once, to generate the two files; the image generates them in its media stage and
carries no ffmpeg at runtime.

**The canned file is always 10 s while `MOCK_VIDEO_SECONDS` only changes the reported duration.** The status
reports `narration_seconds = MOCK_VIDEO_SECONDS` and `output.video`/`Content-Length`/`X-Content-SHA256` are the
canned file's real measured facts, so `check.output.sha256 = output.video.sha256` still holds (05 §2). The
`check` block is the D14 `render-check.v1.ok` (or `.no-subtitles`) fixture with those served-file facts and the
cue count substituted — the mock cannot run `render_check.py`. The fixture's `video.width`/`video.height` are
replaced with the served file's own coded dimensions too, so a portrait task's check reports 1080×1920 and a
landscape one 1920×1080 (the fixture itself is landscape).

The SRT splits the submitted `video_script` at sentence terminators (`.`, `!`, `?`, `…`, `。`, `！`, `？`) and
newlines, one cue per sentence, spread evenly over the **canned 10 s duration** — never over
`MOCK_VIDEO_SECONDS`, which only changes the reported `narration_seconds` (05 C15's "one cue per script chunk",
timing invented because the mock has no whisper). A task with `subtitle_enabled = false` gets no subtitles, so
`GET …/subtitles` answers `404 adapter_output_absent`.

A replayed `render-status.v1.succeeded` fixture advertises a non-null `output.subtitles`, so the mock writes the
same generated SRT for it and `GET …/subtitles` answers `200`. Exactly like `output.video` against the canned
MP4, the fixture's advertised `bytes`/`sha256`/`cues` stay verbatim in the status body (the served body is the
fixture modulo the contract-generated fields), while the download's `Content-Length`/`X-Content-SHA256` are the
served file's own measured facts.

## Lifecycle and retention

`DELETE /v1/renders/{task_id}` marks `cleaned_at` and keeps the row for the contract's 7-day window, exactly
like a real node, so re-reads stay idempotent. A background **retention sweep** then calls the core's own
`ITaskStore.PurgeCleanedAsync` after that window (10-minute interval), so the in-memory store of a long-running
dev session stays bounded instead of accumulating cleaned tombstones forever. The mock also **drops the
submitted `video_script`** the moment a task becomes terminal: after the SRT is written, a served read only
needs the chosen aspect and the task's SRT path.

## What the mock does not do

No MPT, no GPU, no `ffmpeg` at runtime, no OpenAI, no egress, no `nvidia-smi` — health is an in-process body
built from the `render-node-health.v1.ok` fixture with a live capacity block (D19 Q1). Tasks live in the core's
in-memory store, so a mock restart loses them: polls then answer `404 adapter_task_unknown`, which is the
Worker's `render_lost` path (05 D4, intended).

## Tests

`tests/NightTales.MockMpt.Tests` (11 README D3/D18) hosts the same `MockHost.Create` the native run uses, in a
real Kestrel on `http://127.0.0.1:0`: routes and the catch-all, auth on every route, the accepted envelope,
idempotency, the capacity gate, the progress mapping, fixture replay (status and health), the served video and
subtitles, cancel/delete and the restart loss. 03.4.3-T2 and 11.1.2-T2 add their files there.
