# `worker-node/checks/` — the render check (`render-check.v1`)

One stdlib Python 3.12 tool measures a finished MPT task dir and emits
`render-check.v1` (plan 05 README **D9**, §3.1; task **05.5.1-T1**). It **measures
only** — every threshold lives in the C# `RenderQualityPolicy` (D10, 05.5.1-T2).

| File | Role |
|---|---|
| `render_check.py` | the tool: probe, loudness pass 1, optional normalisation, silence, SRT parse, script text match, report |
| `tools/gen_chunk_fixtures.py` | freezes MPT's own chunking output as drift fixtures (runs under **MPT's** interpreter) |
| `fixtures/*.srt` | SRT text fixtures: `good`, `zero-cues`, `overlapping`, `out-of-range`, `empty` (D14: text only, no media) |
| `fixtures/chunking/*.json` | one MPT chunking fixture per input text (port drift guard) |

## Usage

```
uv run --no-project --python 3.12 worker-node/checks/render_check.py run \
    --task-dir DIR --aspect landscape|portrait --subtitles on|off \
    [--normalize] [--presets FILE] [--out FILE]
uv run --no-project --python 3.12 worker-node/checks/render_check.py --self-test [--keep]
```

* `run` writes the report to `--out` (default `<task-dir>/render-check.json`, atomic
  `.tmp` + `os.replace`) **and** prints the same one JSON object on **stdout**
  (`trial/tools/mpt_client.py` `run` does `json.loads(proc.stdout)`); all
  diagnostics go to stderr. The `.tmp` is created `O_CREAT|O_EXCL|O_NOFOLLOW`
  mode 0600 (a planted symlink there is refused); an `--out` that **is** an
  existing symlink is exit 2.
* `--presets` defaults to `<repo>/seed/subtitle-presets.v1.json` (05.2.3-T1). While
  that file is absent the tool falls back to the D6 inline values (landscape
  42 × 2, portrait 28 × 2) and adds `warnings: ["presets_missing_d6_inline"]`, the
  same `d6-inline` fallback `mpt_client.py load_preset_file` uses. An **explicit**
  `--presets` path that does not exist is exit 2.
* `--aspect` disagreeing with the frame size is not a tool error (the policy owns
  the `resolution` check); spaces and non-ASCII task dirs are safe.

### Inputs (MPT's own task dir, C13)

`final-1.mp4` (required), `audio.mp3` (optional: `audio_missing`),
`subtitle.srt` (optional: `srt_missing`/`srt_empty`/`srt_unparsable`),
`script.json` (optional: `script_missing` warning, `text_matches_script = null`).
`--normalize` writes `final-1.norm.mp4` **into the task dir** (D15 P4: removed with
it), never touching `final-1.mp4` — its sha256 is re-asserted at the end
(`input_changed` if it moved). The normalised file is first written to a fresh
`mkstemp` name in the task dir and moved on with `os.replace`, so a planted
`final-1.norm.mp4` symlink is replaced instead of being written through.

Every fixed name is opened through one helper: `O_NOFOLLOW`, `S_ISREG`, and the
real path must stay inside `realpath(task_dir)`. A symlink or another non-regular
file is treated as a missing input (container → `container_invalid` +
`input_unreadable`; SRT → `srt_missing`; script → `script_missing`), and an input
that exists but cannot be read adds `input_unreadable` (`script.json` is optional
and only warns, so for it the `script_missing` warning is the whole signal). The
SRT is capped at 16 MiB (over it → `srt_unparsable`) and `script.json` at 4 MiB
(over it → `script_missing`), both by `fstat` before any read.

### Exit codes (01 D9)

| Code | Meaning |
|---|---|
| 0 | report written; `errors` empty (pass/fail is **not** decided here) |
| 1 | input unreadable — report still written, `errors` non-empty |
| 2 | usage/environment: bad arguments, `ffmpeg`/`ffprobe` missing from `PATH`, an explicit `--presets` file that does not exist, a task dir that is not a directory, an `--out` that is a symlink or cannot be written, no partial report |

Errors (in step order): `container_invalid`, `input_unreadable`, `probe_timeout`,
`no_video_stream`, `no_audio_stream`, `audio_missing`, `audio_silent`,
`ffmpeg_timeout`, `loudness_failed`, `normalize_failed`, `silence_failed`,
`srt_missing`, `srt_empty`, `srt_unparsable`, `input_changed`, `internal_error`.
Warnings: `loudnorm_dynamic`, `script_missing`, `presets_missing_d6_inline`,
`srt_time_invalid:<n>`.
Both lists are de-duplicated, first occurrence first. `internal_error` means
`run_check` raised although it should not: the traceback goes to stderr and a
minimal report (every block `null`) is still written, exit 1.
An unreadable container nulls `container`/`video`/`audio`/`narration`/`loudness`/
`silence`/`subtitles`/`output` (T65); non-finite measures are `null`, never
`-Infinity`.

## What it measures (steps 1–7, D9)

1. `ffprobe -v error -show_format -show_streams -of json` on `final-1.mp4` →
   `container {format, duration_s, faststart}`, `video {codec, pix_fmt, width, height, fps}`,
   `audio {codec, sample_rate, channels}`. `faststart` = the top-level `moov` box
   precedes `mdat`. Timeout 60 s. A non-finite duration or one over 4 h (14 400 s)
   is `container_invalid` and **no** ffmpeg step runs.
2. `audio.mp3` duration (60 s) → `narration.duration_s` (compared with the MP4 by
   the policy, F17).
3. Loudness pass 1: `loudnorm=I=-15:TP=-1.5:LRA=11:print_format=json` over the MP4
   audio → `loudness.before`; `input_i = -inf` → `audio_silent` + `silence.longest_s
   = container.duration_s` (X4). The JSON must directly follow ffmpeg's
   `[Parsed_loudnorm_…]` line and every measured value (`input_i`, `input_tp`,
   `input_lra`, `input_thresh`, `target_offset`) must be finite and in range
   (I ∈ [−99,0], TP ∈ [−99,99], LRA ∈ [0,99], thresh ∈ [−99,0], offset ∈ [−99,99]),
   else `loudness_failed`.
4. With `--normalize`: pass 2 `linear=true` with the measured values (formatted
   `%.2f`, never the raw strings), `-c:v copy -c:a aac -b:a 192k -ar <input rate>
   -movflags +faststart` → a fresh file that `os.replace`s onto
   `final-1.norm.mp4`, re-measured with `ebur128=peak=true` → `loudness.after`
   (the Summary must follow `[Parsed_ebur128_…]`);
   `normalization_type != "linear"` → warning `loudnorm_dynamic`. From here
   `container`/`video`/`audio`/`silence` and `faststart` describe the **served** file
   (D15 P2), while `input` keeps only the original's `file`/`bytes`/`sha256`.
5. `silencedetect=noise=-50dB:d=2` on the served file → `silence.longest_s`/`total_s`
   (`0.0` when none). Only lines carrying the filter's own `[Parsed_silencedetect_…]`
   prefix count, so a `silence_duration` inside the container's metadata is ignored.
6. `on`: parse `subtitle.srt` (UTF-8, BOM and CRLF tolerated) → `cues`,
   `zero_duration`, `overlapping` (start < previous end), `non_monotonic`
   (start < previous start), `out_of_range` (end > container duration + 0.5 s),
   `coverage_share` (**union** of the positive cue intervals ÷ narration, container
   fallback, clamped to [0,1]), `max_cue_chars`, `long_cues`
   (`> maxCharsPerLine × maxLines` of the `--aspect` preset), `cps_p95` (nearest
   rank of `chars ÷ max(duration, 0.05)`), `sync_suspect` (`cps > 25` **or**
   duration > 12 s, the module constants). A cue time line is matched whole:
   `HH:MM:SS,mmm --> HH:MM:SS,mmm` with at most two hour digits and minutes/seconds
   ≤ 59. A negative, malformed or reversed (`end < start`) time line is counted in
   `warnings: ["srt_time_invalid:<n>"]` and excluded from every metric — never
   sign-flipped, never silently dropped. `off`: `present = false`, other fields
   `0`/`null`, the SRT is not read.
7. `script.json` body (`script`, C13) chunked with the **ported**
   `normalize_script_for_subtitle_matching` + `split_string_by_punctuations` over
   MPT's `PUNCTUATIONS`; cue *i* (NFC, whitespace collapsed, trailing punctuation
   stripped) vs chunk *i* → `text_mismatch_cues`, `text_matches_script`.

Every subprocess is `subprocess.run([...])` with list arguments, an explicit
timeout and a minimal allow-listed environment (`PATH`, `HOME`, `LANG`, `LC_ALL`,
`TMPDIR`): never a shell, and no `*KEY*`/`*TOKEN*` variable is inherited. One
run-wide budget (`RUN_BUDGET_S` = 840 s, under the adapter's 900 s
`Adapter:CheckTimeoutSeconds`) caps each ffmpeg step at `min(its own formula, the
budget left)`; once it is spent the step records `ffmpeg_timeout` and the later
ffmpeg steps are skipped. ffmpeg stderr is streamed to a file in a private
`tempfile.mkdtemp()` (0700, removed in a `finally`) instead of being buffered, and
only the last 256 KiB of it is read (the loudnorm/ebur128 JSON tail). Files are
hashed in 1 MiB chunks through an already-open fd (D15 P3). The report carries
counts, durations, hashes and booleans only — never script text, never a host
path. `ffmpeg`/`ffprobe` are resolved from `PATH` (never hard-coded), and
`ffmpeg_version` is `"unknown"` when the banner does not match.

## Fixtures and drift guards

```
# SRT fixtures (text): rebuild with the same builder the self-test uses, then re-run
uv run --no-project --python 3.12 python - <<'PY'
import importlib.util, os
spec = importlib.util.spec_from_file_location("rc", "worker-node/checks/render_check.py")
rc = importlib.util.module_from_spec(spec); spec.loader.exec_module(rc)
base = "worker-node/checks/fixtures"
lines = ["The lighthouse keeper counted seven lamps",
         "Each one belonged to a family that had left",
         "yet the fires still answered the sea",
         "and the village slept through the storm"]
open(os.path.join(base, "good.srt"), "w", encoding="utf-8").write(rc.srt_text(lines, [
    ("00:00:00,000", "00:00:02,000"), ("00:00:02,200", "00:00:04,500"),
    ("00:00:04,700", "00:00:07,000"), ("00:00:07,200", "00:00:09,400")]))
open(os.path.join(base, "zero-cues.srt"), "w", encoding="utf-8").write(rc.srt_text(
    ["Every cue carries zero duration here", "so the policy can see them",
     "and the tool still reports the text"], [("00:00:00,000", "00:00:00,000")] * 3))
open(os.path.join(base, "overlapping.srt"), "w", encoding="utf-8").write(rc.srt_text(
    ["The first cue runs to three seconds", "the second one starts too early"],
    [("00:00:00,000", "00:00:03,000"), ("00:00:02,500", "00:00:05,000")]))
open(os.path.join(base, "out-of-range.srt"), "w", encoding="utf-8").write(rc.srt_text(
    ["A cue inside the video", "a cue that ends past the container duration"],
    [("00:00:00,000", "00:00:02,000"), ("00:00:11,000", "00:00:20,000")]))
open(os.path.join(base, "empty.srt"), "wb").close()
PY
# then: --self-test must still print ok for fixtures/good.srt and every SRT case

# chunking fixtures: MPT's interpreter, then the drift guard
.nts/src/mpt/.venv/bin/python worker-node/checks/tools/gen_chunk_fixtures.py
.nts/src/mpt/.venv/bin/python worker-node/checks/tools/gen_chunk_fixtures.py --check   # 0 = no drift
```

`gen_chunk_fixtures.py` imports MPT's own `split_string_by_punctuations`,
`normalize_script_for_subtitle_matching` and `const.PUNCTUATIONS` from
`--mpt-root` (default `$NTS_HOME/src/mpt`) and writes one file per input
(`{input, input_sha256, input_text, normalized, punctuations, chunks}`); inputs are
the tracked `trial/fixtures/*.txt` texts plus synthetic edge texts (decimal `2.5` /
`1,000`, markdown separator lines, underscores, pause tags, CJK punctuation, empty).
`input_text` is committed so the port can be checked from the other side: **never**
commit a fixture derived from `.nts` `script.json` content (D14/T10). `--check`
regenerates into a temp dir and diffs byte-for-byte (missing, stale and differing
files all fail) → exit 1.

`render_check.py --self-test` (lavfi media in a temp dir, generated with
`-preset ultrafast -threads 2`, no media in the repo) prints one line per case and
exits 1 on any failure: good landscape (incl. `faststart false`), portrait, silent,
wrong resolution, truncated (exit 1, `container_invalid`), no audio stream, loud
≈ −8 LUFS normalised into the band, the five SRT fixtures, the F02 single zero-cue
case, non-monotonic cues, `--subtitles off`, missing/unparsable SRT, the cue cap,
the presets fallback, the chunking fixtures reproduced by the port, plus the
regression guards added after the QA/review round: text mismatch and trailing
punctuation, `srt_time_invalid` accounting, `normalize-off` (`output`/`after`/
`normalization_type` all `null`), a planted `final-1.norm.mp4` symlink, container
symlinks, an unreadable container and a container that vanishes before the probe,
a bogus (non-finite / > 4 h) duration with no ffmpeg step, the
union/`cps_p95`/`sync_suspect`/`non_monotonic` metrics, the anchored
loudnorm/ebur128/silencedetect parsers against a poisoned log, the budget clamp and
its wiring, `--out` symlink and unwritable `--out` (exit 2), the fail-closed
`internal_error` report, the SRT/script size caps, and a wrong-shaped ffprobe
payload (a list, a bare string, a non-object `format`, a non-list `streams`),
which stays a measurement (`container_invalid` / `no_video_stream`) instead of an
unhandled crash. **42 cases, ~20 s.**

## Measured wall times (step 6)

Sample = the S3 happy path copied to `$NTS_HOME/runs/spike-05/checks/s1/`
(1920×1080, `final-1.mp4` 1.68 MB, 37.17 s container, 37.15 s narration, 15 cues),
ffmpeg 9.0.2 on the planning Mac:

| Invocation | real (3 runs) |
|---|---|
| `run --aspect landscape --subtitles on` | 0.95 / 0.95 / 0.94 s |
| `run … --subtitles on --normalize` | 1.53 / 1.55 / 1.53 s |

So the loudness step costs ≈ 0.6 s on a 37 s render. `--normalize` is **four audio
decodes** (pass 1 and pass 2 on `final-1.mp4`, then the `ebur128` re-measure and
`silencedetect` on `final-1.norm.mp4`) plus **one video stream copy** and **zero
video decodes** — X3's "two decodes plus a stream copy" counted only the pass-2
side of that. Projection for a 10-minute episode at that rate (Apple M4,
218 MB / 600 s / 2.92 Mbps synthetic sample, measured by the 05.5.1-T1 performance
review):

| Invocation | 10-min episode | share of the adapter's 900 s budget |
|---|---|---|
| `run --aspect landscape --subtitles on` | 7.2 – 8.3 s | 0.8 – 0.9 % |
| `run … --subtitles on --normalize` | 22.3 – 23.5 s | 2.5 – 2.6 % |

Hand-run reports live in `$NTS_HOME/runs/spike-05/checks/<sample>.json`
(never committed): `s1` (normalised), `s1-check-only`, `s1-off`, `s7`
(whisper failure: `zero_duration == cues`), `s8-nosrt`, `s8-empty`,
`s1-truncated`.

`--self-test` peak RSS (`/usr/bin/time -l`, the largest ffmpeg child included):
1 706 885 120 B (1.63 GiB) with libx264's default preset, 1 062 141 952 B
(1.01 GiB) with `-preset ultrafast -threads 2`.

## Decisions taken while implementing (task file vs D9)

* `ffmpeg_version` is the **version token** of the first line of `ffmpeg -version`
  (`"9.0.2"`), because D9's worked example and the frozen fixture
  `render-check.v1.ok.json` carry `"9.0.2"` rather than the whole banner line.
* No intermediate `render-check.pass1.json` is written: pass 1's JSON block is kept
  in memory (the task file listed it as a possible temp file; it is not needed).
* Extra error codes (`loudness_failed`, `normalize_failed`, `silence_failed`,
  `input_changed`) cover a failed pass that the task file named only as timeouts;
  they are fail-closed (`container_valid` fails on any `errors`).
* `loudness.before`/`after` are `null` and `normalization_type` stays `null` when
  the audio is digital silence (matches `render-check.v1.silent.json`), and steps
  4–5 are skipped for it — the whole file is silence by X4, and pass 2 cannot be
  built from `measured_I = -inf`.
* `cps` uses the spec's `max(duration, 0.05)` floor, so all-zero-duration cues
  (S7/F02) report a large `cps_p95` and `sync_suspect == cues`. The frozen example
  `render-check.v1.zero-cues.json` now carries the values that formula gives for
  its own cue set (4 zero-duration cues, longest text 60 chars → `cps_p95: 1200.0`,
  `sync_suspect: 4`); `render-check.v1.silent.json` likewise carries
  `silence.longest_s = total_s = 302.1`, its **container** duration (X4 measures the
  whole file), not its 301.37 s narration. `render-check.v1.loud.json` is unchanged:
  a loud render whose normalisation did not reach the band is a legitimate failure
  scenario pairing with `render-quality.v1.fail-loudness`.
* `input_changed` is an **error**, not a warning: `--normalize` must not modify
  `final-1.mp4`, and a moved hash invalidates the measurement. It now compares the
  `(st_ino, st_size, st_mtime_ns)` of a fresh `O_NOFOLLOW` open plus a re-hash
  through that fd, so a swap-back between the first hash and the end of the run is
  caught too.
* **Residual TOCTOU gap [KNOWN]:** the container is validated and hashed through
  one fd, but `ffprobe`/`ffmpeg` are then pointed at the *path*, so a writer that
  can replace the task dir's `final-1.mp4` after that check and restore it before
  the end-of-run re-stat is not detected (probing through the fd is not required).
* `RUN_BUDGET_S` (840 s) is the run-wide ffmpeg budget, deliberately under the
  adapter's 900 s `Adapter:CheckTimeoutSeconds` (11.3.5-T3:36) so the tool returns
  `ffmpeg_timeout` itself instead of being killed. A duration over 4 h, or a
  non-finite one, is `container_invalid` before any ffmpeg step — the old
  `max(120, 2 × duration)` formula would otherwise hand a hostile container an
  effectively infinite timeout.
* SEC-0551-7: an ffprobe payload that is not a JSON object (`[]`, `"x"`) is an
  unreadable container (`container_invalid`); one that is an object but carries a
  wrong-shaped `format`/`streams` stays a *measurement* (`no_video_stream` /
  `no_audio_stream`), never an unhandled crash. `script.json` is read only when it
  is an object with a non-empty string `script`.
* `srt_time_invalid:<n>` lives in `warnings` because D9's frozen `subtitles` key set
  has no per-cue counter field; the task file asks for per-cue accounting that is
  "not fatal", and `warnings` is additive (a list) while the fixture key sets are
  pinned.
* The ffmpeg/ffprobe parsers are anchored on ffmpeg's own log prefix. `-af` makes
  ffmpeg print `[Parsed_<filter>_<n> @ 0x…]` (not `[silencedetect @ 0x…]`), so the
  prefix pattern accepts the `Parsed_`/`_<n>` part as optional: that is what the
  pinned ffmpeg 9.0.2 actually writes, and it is still the filter's own line.
* Reading the container, `subtitle.srt` and `script.json` through an `O_NOFOLLOW`
  fd that must be a regular file inside `realpath(task_dir)` is a deliberate
  behaviour change: a task dir can no longer make the tool hash, probe or parse a
  file outside itself. A symlinked SRT therefore reports `srt_missing` and a
  symlinked `script.json` the `script_missing` warning, exactly as if the name were
  absent; only the required container adds `input_unreadable`.
