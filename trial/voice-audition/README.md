# Voice audition — blind listening test (04.2.1-T1, D11)

Machine-side audition set for the VieNeu voice curation protocol (README D11).
**Selection is not done here** — this directory collects the rater sheets and,
after `unblind`, the tracked scores that 04.2.1 uses to pick 3–4 voices per
language.

## Generation log (machine part, done 2026-09-26)

- VieNeu pin `VIENEU_COMMIT=c1390abbdb2eedcdf58eafb546966c06ce27af71`,
  model `pnnbao-ump/VieNeu-TTS-v3-Turbo@61b85e3`, backend onnx / fp32 /
  max_streams=1 on the implementation Mac.
- Candidates: **24** presets — every id of the pinned
  `worker-node/vieneu/voices.snapshot.json` (25 ids) minus `Adam bựa` (O4).
  Aliases never sampled (V8).
- Samples were POSTed directly to VieNeu `127.0.0.1:8000/v1/audio/speech`
  (`response_format: pcm`, `sample_rate: 48000`) and encoded with the D4
  ffmpeg command (`-f s16le -ar 48000 -ac 1` → `libmp3lame -b:a 192k
  -write_xing 0 -id3v2_version 0`). The proxy was not used (its allow-list
  only accepts catalog voices).
- Texts: `trial/fixtures/voice-audition.vi.txt` (87 syllables) and
  `voice-audition.en.txt` (75 words), both ghost-story register, UTF-8 NFC,
  single paragraph, no real names.
- Output (untracked): `$NTS_HOME/runs/voice-audition/` — `s-*.mp3`
  (48 files = 24 voices × 2 languages), `key.json` (blind map, facilitator
  only), `sheet.{vi,en}.csv`, `playlist.{vi,en}.m3u`, `generate.jsonl`
  (per-sample `pcm_bytes`, `audio_seconds`, `wall_ms`).
- Generation stats (from `generate.jsonl`, 2026-09-26): **48/48 samples, 0
  failures**, total wall **258 s** for **1,233.9 s** of audio → mean RTF
  **0.209** (measured, replaces V11's 0.58 estimate; single stream,
  onnx/fp32, M-series CPU). Per-sample wall 3.9–7.6 s; per-sample audio
  19.2–37.5 s (all decode; four fast vi voices land at 19.2–20.0 s — expected
  spread around the ≈30 s target, not a defect).

## How to run the listening test (owner)

Raters needed (D11): vi — the owner plus one other native speaker;
en — at least one fluent English listener (native preferred).

1. Facilitator (you, machine-side): the sheets and playlists are already in
   `$NTS_HOME/runs/voice-audition/` — `sheet.vi.csv`, `sheet.en.csv`,
   `playlist.vi.m3u`, `playlist.en.m3u`. The sheet order is a fresh random
   order per language; the playlist plays the samples in that order.
2. Give each rater the sheet and playlist of their language only. Do **not**
   share `key.json` — it is the blinding key.
3. Each rater plays the `.mp3` files in playlist order and fills one row per
   `sample_id`: `rater` = initials (e.g. `OW`), `naturalness`,
   `intelligibility`, `night_fit` = integer 1–5, `mispronunciations` = the
   mispronounced words only (never the whole text), `rater_tags` ⊆
   `deep;warm;calm;bright;whisper` (`;`-separated), `notes` free text.
4. Collect the filled sheets back into `$NTS_HOME/runs/voice-audition/`
   (e.g. `sheet.vi.filled.csv`, `sheet.en.filled.csv`), then:

   ```bash
   uv run --no-project --python 3.12 worker-node/proxy/tools/audition.py \
     unblind --key "$NTS_HOME/runs/voice-audition/key.json" \
     --sheets "$NTS_HOME/runs/voice-audition/sheet.vi.filled.csv" \
             "$NTS_HOME/runs/voice-audition/sheet.en.filled.csv" \
     --out trial/voice-audition/results.csv

   uv run --no-project --python 3.12 worker-node/proxy/tools/audition.py \
     summarize --results trial/voice-audition/results.csv
   ```

5. Paste the `summarize` tables below under "Results", apply the selection
   rules (task §Selection rules): per language take the 3–4 best with mean
   naturalness ≥ 3.5 and intelligibility ≥ 4.0, ranked by night_fit →
   naturalness → intelligibility with the listed tie-breaks; one language per
   voice (en wins ties of scarcity); ≥ 1 female + ≥ 1 male per language when
   possible. Record the selected set (id, language, gender, tags, means).
6. If no en candidate passes → write the "O2 escalation" section (top-3 en
   voices with means), send to the owner, record the answer and date.
7. Hand the selected set to 04.2.2-T1 (catalog revision 1).

## Results

_Pending: no rater sheets collected yet. `results.csv` is a header-only
placeholder until `unblind` runs._

## Selection

_Pending human rating — not yet performed._
