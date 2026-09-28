# Run notes — 20260928-1022-en-landscape-01

- Task: 01.3.1-T4 quiesced re-run of the 01.2.2-T6 wiring render (01 CR-21); second
  calibration row. The first row (`20260926-0818-en-landscape-01`) stays unchanged
  as structural calibration only (D8 append-only).
- MPT task id: `61331a5a-fc18-4319-8c5b-b5e0042bcd85` · `run_status = calibration`
- Story: `story_source = plan02` (script = `trial/fixtures/wiring-en.txt`, 102 words),
  `video_terms` omitted, `video_count = 1`, `video_aspect = 16:9`,
  `video_language = en`, `voice_name = kokoro:Adam` (revision 1 first active `en` id,
  04 CR-6), `bgm_type = random`, `subtitle_enabled = true`.
- Output: `$NTS_HOME/src/mpt/storage/tasks/61331a5a-fc18-4319-8c5b-b5e0042bcd85/final-1.mp4`
  (copy at `$NTS_HOME/runs/<run_id>/final-1.mp4`) — 1920x1080 h264 + aac, 30.03 s,
  10.86 MB, `ffmpeg -v error -i … -f null -` exit 0, no error lines.
- Logs: `$NTS_HOME/logs/mpt-api-20260928-102122.log`,
  `$NTS_HOME/logs/proxy-20260928-102114.log`, `$NTS_HOME/logs/vieneu-20260928-102103.log`.

## D11 conditions and the step-2 gate — PASS

- Mac rebooted by the owner ~10:11 local; no other heavy app open (no VM, no
  Chrome, no `VBCSCompiler`, no peer agent sessions rendering). Services started
  in D11 order: VieNeu → proxy → tap → MPT API; `smoke.sh` exit 0.
- Idle reading (all four services down, 03:21:03Z): `vm.swapusage` used
  **0.00 MB**, `kern.memorystatus_vm_pressure_level` **1 (normal)**, free 85 %.
- Pre-run (03:22:02Z, services up): pressure level 1, swap used 0.00 MB → within
  128 MB of idle ✓.
- Post-run (03:28:18Z): pressure level 1, swap used 0.00 MB.
- Sampler over the task window: `swap_peak_mb = 0.0`, `memory_pressure_max = normal`,
  free % min 66 → no `swap-used` / `pressure-warn` issue.
- Caveat: the machine had been up only ~10 min; `uptime` load averages were
  5.18 / 17.59 / 10.32 at 10:21 local (post-boot indexing), although `top` showed
  no process above 0.3 % CPU just before the submit. See the whisper note below.

→ This row's RAM/wall columns **may** serve as the 05.5.2 / 03.3.1 baseline under
the step-2 gate, with the whisper-wall caveat.

## Acceptance checks (01.3.1-T4)

| Check | Result |
|---|---|
| Last row: 58 fields, none blank, `run_status = calibration`, `schema_version = 2` | PASS (58/58, 0 blank) |
| Only allowed n/a | PASS: `dashboard_cost_usd_check = n/a:pending-dashboard`; `image_input_text_tokens_total` / `image_input_image_tokens_total = n/a:usage-split-absent` (relay usage has no O6 split, see first run's notes) |
| `stages.json` `gaps_seconds = 0`; Σ windows ≈ `total_seconds` | PASS (0.0; 355.0 = 355.0) |
| First marker ≥ `started_at`; end ≤ `finished_at` | PASS (03:22:18Z ≥ 03:22:03Z; 03:28:13Z ≤ 03:28:36Z) |
| `total_wall_seconds` ≈ log span | PASS (10:22:18 → 10:28:13 = 355 s) |
| `image_count` = `image material rendered:` lines | PASS (5 = 5), `openai image generation failed` 0 |
| `output_duration_sec` = ffprobe | PASS (30.030 = 30.030000) |
| `words_per_minute_measured` by hand | PASS (102 / 30.0 × 60 = 204.0) |
| Unit tests | PASS (137 run, OK, 2 skipped; no code changed) |
| Secret grep (T4 key-prefix / bearer patterns, T6 `x-api-key` hex pattern) on run dir + MPT log | PASS (0) |
| Sampler coverage (step 10: `rss_raw` rows in window / `total_seconds` ≥ 95 %) | **FAIL (borderline): 336 / 355 = 94.6 %** — 21 of 371 sample intervals were 2 s (sampler tick drift, not a stall); every non-zero stage window has samples. First run: 95.3 % / 94.9 % by counting rule |
| `ram_peak_whisper_mb` within ± 25 % of 01.2.3-T2 | PASS (2277.1 vs en-1000 2134.4 = +6.7 %) |
| Next-day dashboard cross-check | OPEN (owner-blocked, DEC-9: relay has no dashboard) |

T6 checks also passed: `task … finished, generated 1 videos.`, no `task failed`;
tap 6 lines (1 chat, 5 images), all `run_id` = this run and `http_status 200`,
`usage` present on all; `subtitle.srt` 912 bytes; `detected language: 'en',
probability: 1.00`; `loading model: ./models/whisper-large-v3-turbo` (offline);
`no background music files found` 0. Manual playback not done by the agent.

## Measurements vs the first (non-quiesced) row

| Column | 2026-09-26 (not D11-clean) | 2026-09-28 (quiesced) |
|---|---|---|
| `total_wall_seconds` | 276.0 | 355.0 |
| `image_wall_seconds` | 178.0 | 226.0 |
| `whisper_wall_seconds` | 19.0 | 38.0 |
| `tts_wall_seconds` / `tts_seconds_audio` | 6.0 / 31.8 | 5.5 / 30.0 |
| `render_wall_seconds` | 70.0 | 68.0 |
| `ram_peak_vieneu_mb` / `whisper` / `render` | 1350.0 / 1984.6 / 1726.0 | 1585.1 / 2277.1 / 2427.1 |
| `ram_peak_total_mb` | 2464.7 | 3840.2 |
| `swap_peak_mb` / `memory_pressure_max` | 1800.0 / warn | 0.0 / normal |

- The first row's RAM peaks are **lower**, likely because it ran under memory
  pressure with 1.8 GB in swap (pages evicted) and used a different voice
  (`Ngọc Huyền`), so it probably understated the real footprint [INFERENCE: no
  per-process swapped-page data was sampled]; the quiesced total peak is 3.84 GB
  (fits the 16 GB budget, R1).
- Images: 5 calls, 41–45 s apart in the log (226 s window) vs 178 s on 09-26 —
  relay latency, not local compute [INFERENCE].
- Whisper: `complete, elapsed: 29.82 s` for 30.0 s audio → RTF 0.99, vs 0.357 on
  09-26 and 0.362 in the 01.2.3-T2 baseline. The first guess (cold cache after the
  reboot) is **refuted** by the warm re-measure on 2026-09-28 03:42Z (pressure 1,
  swap 0.00 MB, services idle; 01.2.3-T2 `bench.py`, MPT's parameters, 3 runs each;
  raw files in `$NTS_HOME/runs/whisper-warm-20260928/`):

  | Input | transcribe s | RTF | wall s | peak MB |
  |---|---|---|---|---|
  | `vieneu-en-1000.wav` (baseline sample, 52.63 s) | 18.06 / 18.33 / 18.50 | 0.343–0.352 | 19.4–20.4 | 2242–2244 |
  | this run's `audio.mp3` (30.02 s) | 36.92 / 35.06 / 37.52 | 1.168–1.250 | 38.1–40.1 | 2160–2270 |

  - The machine reproduces the 01.2.3-T2 baseline (0.362), so the Mac and the
    model are fine; the slowness belongs to **this audio**.
  - Same process, warm model: row-1 audio (`Ngọc Huyền`, 31.82 s) RTF 0.50–0.52,
    this run's audio (`Adam`) RTF 1.11–1.21. VAD on/off and word timestamps
    on/off change it by ≤ 8 %, so neither is the cause. Every segment decodes at
    temperature 0 (no fallback). Both files are mp3 48 kHz mono 192 kbps.
  - Difference observed: whisper splits the `Adam` audio into 9 segments,
    including three 2-word fragments (8.74–8.98, 18.48–19.12, 24.28–25.00 s),
    vs 6 clean sentence segments for `Ngọc Huyền`. [INFERENCE] the voice's
    shorter pauses make whisper re-seek and decode more windows; not traced
    inside faster-whisper.
  - Consequence: `whisper_wall_seconds` 38.0 is **representative** for this
    voice/audio, not a cold-cache outlier, and the row stays valid as a
    baseline. Whisper cost depends on the voice: 05.5.2 must measure it per
    voice, not scale it from the 01.2.3-T2 RTF.
  - Side observation: row-1 audio takes 15.5–16.5 s standalone vs
    `complete, elapsed: 11.35 s` inside the 09-26 MPT run; the cause is unknown.

## Cost

- Chat (gpt-6-luna), 1 call: 319 in / 66 out → `story_cost_usd` 0.000065.
- Images (gpt-image-2, relay): 5 calls, `image_cost_usd` 0.035880
  (0.007176 / image) — now priced from the `gpt-image-2` entry in
  `trial/prices.json` (184 VND/image, owner decision 2026-09-28), so the first
  run's `n/a:no-price` does not recur. `image_quality_returned = low:3|medium:2`,
  sizes `1536x1024+1370x1148` — same relay drift as Q4 on 09-26.
- `usd_per_output_minute` 0.071818. Ceiling: 5 images ≤ 15 (abort 20 not reached);
  dollar total $0.036 ≪ $4.50 ceiling.

## Q1–Q10

No change from the first run's notes (`trial/runs/20260926-0818-en-landscape-01/notes.md`):
same 7 markers, parser and defaults worked unchanged, `AUDIO_END_RE` stays `None`,
Q3 field still absent, Q4 drift reproduced, Q8 proxy line `ts 03:22:36.046Z`
(voice `Adam`, chars 557, audio 29.98 s, vieneu 5472 ms) inside the audio window
03:22:36–03:22:41 → `sources.tts_seconds_audio = proxy-log`. Q5/Q10 stay
owner-blocked. No regex change → `MARKER_SET` stays 1.

## Open items

- Sampler coverage 94.6 % is 0.4 pp under the step-10 threshold: 01.3.2-T1 owner to
  decide whether 2 s tick drift is acceptable or the interval needs drift
  correction.
- Whisper wall: re-measured warm (above); RTF depends on the voice. 05.5.2 must
  measure whisper per voice (05 owner).
- `dashboard_cost_usd_check`: owner reads the relay's cost for 2026-09-28 UTC.
