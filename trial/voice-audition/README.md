# Voice audition — blind listening test (04.2.1-T1, D11)

Audition set, scores and selection for the VieNeu voice curation protocol
(README D11): generation log, rater procedure, the unblinded scores in
`results.csv`, and the selected 3–4 voices per language (§Selection).

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

Collected 2026-09-27. Sheets: `sheet.vi.NMT.csv`, `sheet.en.NMT.csv` (untracked,
`$NTS_HOME/runs/voice-audition/`), unblinded into `results.csv` (48 rows = 1 rater
× 24 candidates × 2 languages, rejected 0).

**Raters: one (owner, `NMT`) for both languages. D11 is not met**: vi needs the
owner plus one other native speaker, en needs a fluent English listener. The
owner accepted this for catalog revision 1 on 2026-09-27 (04 README T18); a
second vi rater and an en rater are added later and the selection re-run
(revision 2). While filling the sheets the owner wrote a per-sample label in the
`rater` column; it was moved to `notes` as `label: <text>` before unblinding.

Score scale (MOS-style, per column): 5 excellent, no flaw · 4 good, minor
flaws · 3 acceptable, flaws audible · 2 poor, disturbing · 1 unusable.
`night_fit` 5 = calm, slow, atmospheric; 2 = too cheerful, fast, news/ad tone.

Caveat: 47/48 naturalness and 45/48 intelligibility scores are 5, so the
thresholds filter only 3 vi voices and the ranking rests on `night_fit` and the
tie-breaks.

`audition.py summarize` output:

### en (24 voices)

| voice | raters | nat | int | fit | mis | pass | tags |
|---|---|---|---|---|---|---|---|
| Mai Anh | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | news, bright |
| Thiền Tâm Đức | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, deep |
| Thiện Minh | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, deep |
| Adam | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | natural, deep |
| Phạm Tuyên | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | natural, calm |
| Thái Sơn | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, warm |
| Thục Đoan | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, warm |
| Xuân Vĩnh | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | natural, warm |
| Đức Trí | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, deep |
| Hải Đăng | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, bright |
| Ngọc Huyền | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, warm |
| Trúc Ly | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, bright |
| Minh Đức | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | news, calm |
| Ngọc Linh | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | storyteller, calm |
| Quốc Tuấn | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, bright |
| Ngọc Trân | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | natural, calm |
| Quang Sơn | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | natural, calm |
| Thùy Dung | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | news, bright |
| Minh Triết | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | news, bright |
| Mỹ Duyên | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | storyteller, bright |
| Quỳnh Anh | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | storyteller, bright |
| Thanh Bình | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | storyteller, bright |
| Đoan Trang | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | natural, bright |
| Kim Thanh | 1 | 5.00 | 5.00 | 2.00 | 0 | ✅ | storyteller, calm |

### vi (24 voices)

| voice | raters | nat | int | fit | mis | pass | tags |
|---|---|---|---|---|---|---|---|
| Mai Anh | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | news, northern, deep |
| Thiền Tâm Đức | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, northern, deep |
| Thiện Minh | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, northern, deep |
| Minh Đức | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | news, northern, warm |
| Mỹ Duyên | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, southern, deep |
| Phạm Tuyên | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | natural, northern, deep |
| Thái Sơn | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, southern, deep |
| Thục Đoan | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, southern, calm |
| Đức Trí | 1 | 5.00 | 5.00 | 5.00 | 0 | ✅ | storyteller, southern, warm |
| Ngọc Huyền | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, northern, bright |
| Thùy Dung | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | news, southern, bright |
| Minh Triết | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | news, southern, warm |
| Ngọc Linh | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | storyteller, northern, calm |
| Quốc Tuấn | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, northern, calm |
| Quỳnh Anh | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | storyteller, northern, bright |
| Đoan Trang | 1 | 5.00 | 5.00 | 4.00 | 0 | ✅ | natural, northern, bright |
| Trúc Ly | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | natural, northern, bright |
| Kim Thanh | 1 | 5.00 | 5.00 | 3.00 | 0 | ✅ | storyteller, southern, bright |
| Xuân Vĩnh | 1 | 5.00 | 5.00 | 2.00 | 0 | ✅ | natural, northern, whisper |
| Quang Sơn | 1 | 2.00 | 2.00 | 2.00 | 0 | ❌ | natural, central, warm |
| Hải Đăng | 1 | 5.00 | 5.00 | 1.00 | 0 | ✅ | natural, northern, bright |
| Thanh Bình | 1 | 5.00 | 5.00 | 1.00 | 0 | ✅ | storyteller, northern, bright |
| Ngọc Trân | 1 | 5.00 | 2.00 | 1.00 | 0 | ❌ | natural, central, bright |
| Adam | 1 | 5.00 | 2.00 | 1.00 | 0 | ❌ | natural, southern, bright |

## Selection

Rules: task §Selection rules. Pass: vi 21/24, en 24/24. Rule 5 (one language
per voice) uses `summarize`'s assignment: en has ≥ 3 passing voices, so each
bilingual pass stays in the language where it ranks higher; equal ranks keep vi
(T18). Rule 3's vi region tie-break, which `summarize` does not apply, puts
`Đức Trí` (southern) in place of `Minh Đức` (northern) among the 5/5/5 vi voices.

| language | voice | gender | nat | int | fit | tags |
|---|---|---|---|---|---|---|
| vi | Mai Anh | female | 5.00 | 5.00 | 5.00 | news, northern, deep |
| vi | Đức Trí | male | 5.00 | 5.00 | 5.00 | storyteller, southern, warm |
| vi | Thiền Tâm Đức | male | 5.00 | 5.00 | 5.00 | storyteller, northern, deep |
| vi | Thiện Minh | male | 5.00 | 5.00 | 5.00 | storyteller, northern, deep |
| en | Adam | male | 5.00 | 5.00 | 5.00 | natural, deep |
| en | Thục Đoan | female | 5.00 | 5.00 | 5.00 | storyteller, warm |
| en | Phạm Tuyên | male | 5.00 | 5.00 | 5.00 | natural, calm |
| en | Thái Sơn | male | 5.00 | 5.00 | 5.00 | storyteller, warm |

- Gender balance: ≥ 1 female and ≥ 1 male per language.
- No voice in two languages. Tags ⊆ D9, ≤ 4 each; en carries no region tag.
- No O2 escalation: every en candidate passed.
- Handed to 04.2.2-T1 (catalog revision 1).
