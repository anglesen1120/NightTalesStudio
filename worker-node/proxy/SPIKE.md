# 04.1.1-T1 — SPIKE: PCM/WAV decode, streaming transcode, abort and disconnect at the pin

Run: 2026-09-25 on the implementation Mac. VieNeu `127.0.0.1:8000` started via
`worker-node/bin/start-vieneu.sh` (backend `onnx`, `max_streams=1`, `queue=1`,
`VIENEU_QUEUE_TIMEOUT=10` — `worker-node/vieneu/env.example` guards).

Machine: macOS 27.0 (26A428), Apple M4, 16 GB RAM, ffmpeg 9.0.2 (Homebrew,
`libmp3lame` present), scratch venv Python 3.12.14 with `httpx 0.28.1`,
`uvicorn 0.54.0`, `starlette 1.7.0`; MPT venv `$NTS_HOME/src/mpt/.venv`
(`requests 2.33.1`, `moviepy` dist 2.2.1 — see ⚠️ in M9).

Pins (from `worker-node/pins.env` via `load_pins`, verified live):

| Pin | pins.env | Live `git rev-parse HEAD` |
|---|---|---|
| `VIENEU_COMMIT` | `c1390abbdb2eedcdf58eafb546966c06ce27af71` | `c1390abbdb2eedcdf58eafb546966c06ce27af71` ✅ equal |
| `MPT_COMMIT` | `3d5f4e421927d61f3eac729cf4b711ac0b69d688` | `3d5f4e421927d61f3eac729cf4b711ac0b69d688` ✅ equal |

`apps/openai_speech.py` at the pin is byte-identical to
`/private/tmp/vieneu_speech_truth.py` (`diff -q` → identical).

## S0 — fact re-check at the pin

| Id | Result | Note |
|---|---|---|
| V1 | same | `SpeechRequest` defaults at L229–242: `model="vieneu-v3-turbo"`, `input` 1–20,000 chars, `response_format="wav"`, `stream_format="audio"`, `sample_rate=48000`, `temperature=0.8`, `top_k=25`, `top_p=0.95`, `repetition_penalty=1.2`, `max_chars=256` |
| V2 | same | only `pcm`/`wav`, `sample_rate ∈ {48000,24000,16000,8000}`, unknown voice → 400 (L300–308) |
| V3 | same | `eng.acquire()` before `StreamingResponse` (L310); 429 `Retry-After: 1` queue full / `2` after `VIENEU_QUEUE_TIMEOUT` (default 10 s) — **confirmed live**: a POST while the slot was held returned 429 `Retry-After: 2` after 10,009 ms |
| V4 | same | live responses carry `X-Request-Id: spk-…`, `X-Sample-Rate: 48000`, `Cache-Control: no-store`, `Content-Type: audio/pcm`; `X-VieNeu-Ignored` only when `speed`/`instructions` sent (code L312–315) |
| V5 | same | `finally: eng.release()` in the generator (L268–269) — but see experiment (d): release is **not** prompt after a client disconnect |
| V6 | same | WAV header carries `0xFFFFFFFF` RIFF/data sizes (L197–208); observed bytes `52494646 ffffffff 57415645` |
| V7 | same | `GET /v1/voices` list shape and `GET /health` `{status,backend,max_streams,active,waiting,sample_rate}` confirmed live |
| V8 | same | 25 presets, default `Hải Đăng`, genders `male`/`female`, aliases `Anh Khôi→Thiện Minh`, `Minh Quân`/`Minh Quân Pro→Hải Đăng`, `Mạnh Dũng→Quốc Tuấn`; all names NFC |
| V9 | same | `resolve_voice_name` = exact match then alias map, no normalisation/case folding (`v3turbo.py` L315–323) |
| V10 | same | README L13 "10,000+ hours of bilingual training"; L332 Nano "English words come out with a Vietnamese accent" |
| V11 | same | CPU fp32 RTF note at L103–106 (upstream figure); ours is better — see (b) |
| M1 | same | `_openai_compatible_tts` body `{model,input,voice,response_format:"mp3",speed:clamp(0.25,4.0)}` + Bearer when key set (`voice.py` L2144–2173) |
| M2 | same | 3 attempts, non-200 → log+`continue`, no sleep, `Retry-After` ignored (L2177–2228) |
| M3 | same | `requests.post(url, json=…, timeout=120)`, no `stream=True`, full body → temp `.mp3` → `AudioFileClip` duration finite and > 0 → `os.replace` (L2183–2211) |
| M4 | same | `kokoro_tts` rejects empty/no-alnum text; `model_id` default `"kokoro"` (L2299–2316) |
| M5 | same | `parse_voice_name` strips every `-Female`/`-Male` substring (L371–376); `-V2` routed to Azure before kokoro (L619, L692); kokoro branch splits on first `:` and strips (L692–704; additionally strips a trailing `-Female`/`-Male`, consistent with M5) |
| M6 | same | `get_kokoro_voices` only called by `webui/Main.py` L539, L552 and tests — not the API path |
| M7 | same | `tts()` sends the whole script in one request; pause tags removed first (L995–1040) |
| M8 | same | TTS failure → `failed_stage="audio"`, error "failed to synthesize audio; verify the selected voice and TTS connectivity" (`task.py` L545–551) |
| M9 | same | `uv.lock`: `requests 2.33.1`, `moviepy 2.2.1`; venv `requests.__version__` = 2.33.1 ✅; `importlib.metadata` reports moviepy 2.2.1, but `moviepy.__version__` reports **2.1.2** ⚠️ (dist version = lock; module attribute differs) |

## Experiments

All VieNeu requests used the D3 upstream body
`{model:"vieneu-v3-turbo", input, voice:"Hải Đăng", response_format, stream_format:"audio", sample_rate:48000}`
over httpx; the key reached VieNeu only via `Authorization: Bearer` from
`secret nts.vieneu.api_key` (env, never argv/logs). Scripts and artifacts are
throwaway in `$NTS_HOME/runs/spike-04/`.

### (a) PCM / WAV decode through the exact D4 command

Command (PCM):
`ffmpeg -hide_banner -loglevel error -f s16le -ar 48000 -ac 1 -i pipe:0 -vn -c:a libmp3lame -b:a 192k -write_xing 0 -id3v2_version 0 -flush_packets 1 -f mp3 pipe:1`
(WAV variant: `-f wav -i pipe:0`.)

- PCM fetch: 200, `Content-Type: audio/pcm`, `X-Sample-Rate: 48000`,
  `X-Request-Id: spk-254d6676`, 5,166,720 B (53.820 s) in 10.643 s.
- PCM→MP3: exit 0, **no stderr**, 1,292,544 B; `ffprobe` duration 53.856 s
  (Δ **+36 ms** vs expected, ≤ +40 ms; matches X1's ~+30 ms); first bytes `ff fb`.
- WAV fetch: 200, `audio/wav`, 5,167,724 B (53.830 s of PCM + 44 B header,
  `0xFFFFFFFF` sizes confirmed).
- WAV→MP3: exit 0, **no warnings at all** (X1 saw `Ignoring maximum wav data
  size`/`corrupt input packet` on the planning Mac — not reproduced here);
  duration 53.856 s (Δ +26 ms); first bytes `ff fb`.
- Empty input cases: empty PCM → exit 0, **0 bytes out**; WAV header-only
  (44 B) → exit 0, **0 bytes out**. X1 reported a 44-byte output for the
  header-only case on the planning Mac — different here (0 bytes). Either way
  D4's `upstream_empty_audio` rule (first-PCM-bytes gate, never trust exit
  code) is required: **ffmpeg exit 0 does not imply audio**.

### (b) Streaming (fixture `tts-vi-5min.txt`, voice `Hải Đăng`)

`spike_pipe.py` streamed the PCM into ffmpeg stdin and read stdout in 16 KiB
reads:

```json
{ "chars": 3764, "headers_ms": 31, "status": 200, "x_request_id": "spk-3b0a1a1f",
  "x_sample_rate": "48000", "content_type": "audio/pcm", "ffmpeg_start_ms": 33,
  "first_pcm_ms": 177, "first_mp3_ms": 272, "last_pcm_ms": 37300,
  "max_gap_ms": 441, "mp3_bytes": 4639104, "last_mp3_ms": 37304,
  "ffmpeg_exit": 0, "ffmpeg_exit_ms": 37306, "pcm_bytes": 18552960,
  "pcm_seconds": 193.26, "wall_s": 37.306, "rtf": 0.193 }
```

MP3 bytes flowed continuously while VieNeu was still synthesising
(first MP3 at 272 ms of a 37.3 s request; largest gap between 16 KiB MP3 reads
441 ms). VieNeu's own log: `ttfa=146ms total=37.27s audio=193.26s rtf=0.19`.
RTF 0.19 on M4 (better than the V11 upstream 0.58 on the 6-core reference).

### (c) Abort seen by MPT's `requests`

`abort_server.py` (uvicorn 0.54.0 + starlette 1.7.0) yields 64 KiB of the real
MP3 from (a), then raises. `mpt_client.py` was run with MPT's own interpreter
(`$NTS_HOME/src/mpt/.venv/bin/python`, `requests.__version__` = **2.33.1** =
M9 lock):

- `/abort`: `requests.post(url, json=payload, timeout=120)` →
  **`ChunkedEncodingError: Response ended prematurely`** (twice). uvicorn logs
  the ASGI exception; the socket closes without the terminating chunk. ✅ X2
  reproduced at the real client version.
- `/trickle` (1 KiB/s for 5 s, client `timeout=2`) → **200, 5,120 bytes
  complete, 5.009 s** ✅ — the 120 s is an idle timeout (R1), bytes keep the
  request alive.

### (d) Disconnect → stream-slot release (F17, T12)

`disconnect_probe.py`: stream `tts-vi-5min.txt` PCM, close after ~5 s of
audio, poll `GET /health` every 250 ms for `active → 0`; fire one re-request
**immediately** after each close. Five measurements across two probe batches
(all `max_streams=1`, `VIENEU_QUEUE_TIMEOUT=10`):

| # | Close type | `t_close → active==0` | Immediate re-request |
|---|---|---|---|
| 1 | httpx stream close after 5.53 s PCM | **81.79 s** | fired only after the release wait → 200 |
| 2 | SIGTERM on the client process | **> 90 s** (still `active:1` at +90 s; **0 established TCP connections**) | — |
| 3 | httpx stream close after 5.16 s PCM | released at **≈ +210 s** (its own 200 s poll window expired; the next run's poll saw `active:0` 10.27 s into a run that started ≈ +200 s) | POST immediately after close → **429 `Retry-After: 2`** after 10,009 ms (full queue timeout) |
| 4 | POST while run-3 slot still held | — | **429 `Retry-After: 2`** after 10,008 ms |
| 5 | httpx stream close after 5.00 s PCM | **still held at > ~400 s** (`active:1`, `lsof`: 0 established connections) | POST immediately after close → **429 `Retry-After: 2`** after 10,008 ms |

Interpretation (evidence only, cause not verified): `eng.release()` lives in
the generator's `finally` (V5); after a client disconnect the suspended sync
generator in Starlette's threadpool is not promptly closed, so the slot is
held for tens of seconds to minutes — sometimes past every observed window —
with no TCP connection left. Consequence on the Mac (`VIENEU_MAX_STREAMS=1`):
**while a slot is leaked, every new request burns the 10 s queue timeout and
gets 429** — and D5's `ADMISSION_WAIT_S` (default 60 s, allowed ≤ 80 s)
**cannot cover the observed releases (81.79 s – >400 s)**.

**T12 measurement: release delay observed at 81.79 s, ≈210 s, >400 s — all
> 30 s.** TL action per T12 is required before 04.1.2-T2; `ADMISSION_WAIT_S`
at its D13 maximum (80 s) still falls short of the observed releases, so this
is flagged beyond the T12 ruling: options are keeping the upstream request
alive server-side (drain/discard) instead of cancelling it on client
disconnect, or revisiting the VieNeu pin / an upstream fix. [OPEN] for the TL.

### (e) MoviePy decode of the streamed MP3

`$NTS_HOME/src/mpt/.venv/bin/python` + `moviepy.AudioFileClip("vi5min.mp3")`:
duration **193.300 s**, finite, > 0; PCM duration 193.260 s → **Δ +0.040 s**
(≤ 0.1 s ✅). Same check `tools/mpt_decode_check.py` will run (D14).
`moviepy.__version__` prints 2.1.2 while the installed dist is 2.2.1 (M9 ⚠️).

### (f) Sampling defaults the proxy inherits (D3 sends none)

From the pinned `apps/openai_speech.py` (L229–242, L113–114): `temperature
0.8`, `top_k 25`, `top_p 0.95`, `repetition_penalty 1.2`, `max_chars 256`,
`sample_rate 48000`; `VIENEU_QUEUE_TIMEOUT` unset in env → default 10 s,
`max_streams` = 1 (env `VIENEU_MAX_STREAMS=1`; the CPU fp32 code default is
also 1). `max_queue` defaults to `max_streams` (`VIENEU_QUEUE`).

## Results

| Exp | Measure | Value | Expected | OK |
|---|---|---|---|---|
| a | PCM→MP3 exit / duration Δ | 0 / +36 ms | 0 / ≤ +40 ms | ✅ |
| a | WAV→MP3 exit / warnings / duration Δ | 0 / none / +26 ms | 0 / warnings allowed (F12) | ✅ (⚠️ X1 warnings not reproduced) |
| a | empty input exit / bytes | 0 / **0 B** | 0 / 44 B (X1) | ⚠️ deviation: 0 B here; D4 empty-audio rule still required |
| b | first_mp3_ms / max_gap_ms / RTF | 272 / 441 / 0.193 | < 30,000 / < 60,000 | ✅ |
| c | exception on abort / trickle result | `ChunkedEncodingError` (`requests` **2.33.1** = M9) / 200 complete | `ChunkedEncodingError` / 200 | ✅ |
| d | slot release delay (5 obs.) | 81.79 s · >90 s · ≈210 s · >400 s; immediate re-requests → 429 `Retry-After: 2` after ~10 s | recorded, vs D5 60 s | ⚠️ all > 30 s, worst > ADMISSION_WAIT_S max 80 s → T12 + escalation |
| e | MoviePy duration Δ | +0.040 s | ≤ 0.1 s | ✅ |
| f | sampling defaults | t=0.8, k=25, p=0.95, rp=1.2, mc=256, sr=48000, qto=10, streams=1 | matches V1/V3 | ✅ |

## Go/No-go for D4

- ffmpeg command turns VieNeu PCM (and WAV) into a correct MP3: **PASS** (a).
- MP3 bytes flow while VieNeu synthesises: **PASS** (b): 272 ms to first MP3, 441 ms max gap.
- Post-commit abort surfaces in MPT's `requests` as `ChunkedEncodingError`: **PASS** (c) at the locked client version.
- Slot release after client disconnect is **not** prompt (d): **does not block D4** (per task failure-handling), but violates the T12 ≤30 s assumption and exceeds `ADMISSION_WAIT_S` ≤ 80 s — **flagged to the TL, [OPEN]**.

D4: GO
