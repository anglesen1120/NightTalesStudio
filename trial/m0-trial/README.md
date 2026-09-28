# M0 trial renders — vi + en measurement (05.5.2-T1)

State: **preparation — no render has run yet.** Everything below that a run needs
is frozen (story, voice, music, spec) so the two landscape trial renders can be
started without re-deciding anything. The run-result table is empty until L1/L2
have run; Gate 1 and 03.3.1-T1 read this page.

## O2 result — `en` launched, L2 is the en ghost story

04 README §11 condition 2 / 04 CR-13: en launches when the best en voice's mean
naturalness is **≥ 3.0**. From `trial/voice-audition/results.csv` (24 en
candidates × 1 rater `NMT`, 04.2.1-T1):

- **best en mean naturalness = 5.00** (every one of the 24 en rows scored 5.00;
  count ≥ 3.0 = 24/24) → **≥ 3.0 → en LAUNCHED → L2 = en ghost story**.
- best-rated en voice id per the D11 ranking (`night_fit` → `naturalness` →
  `intelligibility`) and the §Selection order, and the first active en voice of
  the catalog: **`Adam`** — mean 5.00 naturalness / 5.00 intelligibility / 5.00
  night_fit (male, tags `natural, deep`), `active` in `seed/voices.v1.json`
  rev 1 (04.2.2-T1). No `en-skipped` note applies; the en subtitle rating does
  apply.

## Run plan (frozen before any spend)

| Run | Lang | Aspect | Target | Story cell (02.1.3-T0, untracked) | Est. min | Voice (05.4.1-T1 spec) | Music (D8) | Preset |
|---|---|---|---|---|---|---|---|---|
| L1 | vi | 16:9 landscape | 5 min | `trial/stories/vi-ghost-b3-sol-standard` | 3.87 | `Mai Anh` (vi, 5.00/5.00/5.00) | `apprehensive-at-best`, `bgmVolume` 0.057 | landscape@1 |
| L2 | en | 16:9 landscape | 5 min | `trial/stories/en-ghost-b1-luna-standard` | 4.60 | `Adam` (en, 5.00/5.00/5.00) | `apprehensive-at-best`, `bgmVolume` 0.057 | landscape@1 |

> **[DECISION] owner 2026-09-29:** keep the D15-approved-model cells (L1 vi **3.87 min**,
> 1007 tokens, **−22.6 %** vs 5 min, D4 objective shortfall; L2 en 4.60 min). The
> shortfall is noted on the L1 row of the run-results table. The rejected alternative
> was `vi-ghost-b1-luna-long` (5.25 min), written by `gpt-6-luna`, which is not the vi
> D15 default (`DECISIONS.md` §3).

Both specs: `trial/m0-trial/L1-vi.spec.json`, `trial/m0-trial/L2-en.spec.json`
(`aspect 16:9`, `language`, `voice_id`, `preset_file: null` = the D6 inline
landscape preset, `music_track`). `preset_file` null is safe: the inline D6
landscape preset equals `seed/subtitle-presets.v1.json` `landscape@1` on all 8
D3 fields (`font_size 84`, `bottom`, `custom_position 70.0`, `#000000`,
`#FFFFFF`, `false`, `false`, `BeVietnamPro-Bold.ttf`). A preset revision bump
before the run ⇒ materialise with `"preset_file": "seed/subtitle-presets.v1.json"`.

### Voice rule trace

Vi voice = first row of `trial/voice-audition/README.md` §Selection for `vi`
(the D11 ranking + the vi region tie-break): **`Mai Anh`** mean
naturalness/intelligibility/night_fit 5.00/5.00/5.00 (female, `news, northern,
deep`); the other vi picks in rev 1 are `Đức Trí`, `Thiền Tâm Đức`, `Thiện
Minh`. En voice = the same rule for `en`: **`Adam`** (5.00/5.00/5.00). Both are
`active: true` in `seed/voices.v1.json` revision 1 and are sent as
`voice_name = "kokoro:<id>"` (D3/04 D10).

### Story rule trace

Filter: topic `ghost`; writer model = the D15-approved default per `DECISIONS.md`
§3 / 02 T109 (**vi `gpt-6-sol`**, **en `gpt-6-luna`**); tier ∈ {`standard`,
`long`}; then minimize `|est. min − 5.0|`, tie → smaller block id. Length =
whitespace tokens ÷ the speaking rate from 02 D4/T103 (vi 260 syllables/min, en
200 words/min — the rates the `trial/tools/LENGTH.v1.json` tier bands are built
from; one token = one syllable in vi, one word in en).

| Language | Candidate (approved model) | Tokens | Est. min | Δ vs 5 min |
|---|---|---|---|---|
| vi | `vi-ghost-b3-sol-standard` | 1007 | 3.87 | 1.13 ← chosen |
| vi | `vi-ghost-b1-sol-standard` | 899 | 3.46 | 1.54 |
| vi | `vi-ghost-b2-sol-standard` | 790 | 3.04 | 1.96 |
| en | `en-ghost-b1-luna-standard` | 919 | 4.60 | 0.41 ← chosen |
| en | `en-ghost-b2-luna-standard` | 865 | 4.33 | 0.67 |
| en | `en-ghost-b3-luna-standard` | 759 | 3.79 | 1.21 |
| en | `en-ghost-b1-luna-long` | 1831 | 9.15 | 4.15 |

Rejected alternative: `vi-ghost-b1-luna-long` = 1365 tokens = **5.25 min** is the
closest to 5 min overall, but it was written by `gpt-6-luna`, which is not the vi
default (`DECISIONS.md` §3) — the trial render must exercise the shipped vi path,
so the approved-model filter wins over the last 1.4 min of length. The owner ruled
2026-09-29 (above): the approved-model cells stand. All three vi/en `standard`
drafts are below the D4 5–8 min band (02 T103 already records the band/observed
gap); the render length therefore measures the real narration, not a 5-minute
ceiling.

### Music rule trace (D8, by hand)

Topic `ghost` → moods `{dark_ambient, suspense}`. Candidates = `active` ∧
platformScope ∈ {`youtube`, `any`} ∧ `moods ∩ {dark_ambient, suspense} ≠ ∅` →
**24 of the catalog's 30 tracks** in `seed/music-tracks.v1.json`. D8 orders candidates by `id`
(ordinal) and picks an index from a hash of the episode id; M0 has no canonical
episode id and the track must be frozen in the tracked spec, so the index is
fixed at **0** = first by id:

- **`track_id = apprehensive-at-best`** — moods `[suspense, noir]`, file
  `apprehensive-at-best.mp3`, **`bgmVolume` 0.057** (catalog value =
  `round(clamp(10^((−34 − −9.1)/20), 0.03, 0.40), 3)` at `MusicBedLufs` −34),
  loudness −9.1 LUFS, 102.8 s, license YouTube-Audio-Library (`youtube` scope,
  allowed), attribution null.
- Next by id: `aztec-empire`, `blood-kiss`. Body fields ⇒ `bgm_type = "custom"`,
  `bgm_file = "apprehensive-at-best.mp3"`, `bgm_volume = 0.057` (verified in the
  dry build below).
- File check: `$NTS_HOME/music/apprehensive-at-best.mp3` exists with the catalog
  `sha256` `533167ff…2d0e` (D8 M0 = files copied by hand).
- If the owner wants a track that carries **both** topic moods, the same rule
  with a "both moods" preference picks `blood-kiss` (`dark_ambient, suspense`);
  swapping it is a one-line spec edit plus a TL note, and the by-ear
  `MusicBedLufs` tuning this task owns applies to either.

## Materialise the spec, then run (L1; L2 identical with `L2-en`)

`mpt_client.py` does **not** expand environment variables, so the tracked specs
keep `<NTS_HOME>` / `<RUN_ID>` placeholders (a tracked file never carries a host
path, and the story text lives only under `$NTS_HOME`, T11):

```bash
NTS_HOME="${NTS_HOME:-$PWD/.nts}"
RUN_ID="$(trial/tools/start_run.sh vi landscape "ghost: <story title>" 5 \
            --status trial --voice "Mai Anh" --story-source plan02)"     # tap must be up (01 D6)
uv run --no-project --python 3.12 .nts/runs/m0-trial-inputs/materialise_spec.py \
    --spec trial/m0-trial/L1-vi.spec.json --run-id "$RUN_ID"
# then: mpt_client.py submit → poll --timeout 3600 --trace … → download; 05.5.2-T1 §Design steps 2-6
```

## Run results (to fill after L1/L2)

D10's nine blocking checks in order, then `render-quality.json.pass`, then the
native ratings (means per axis, min and count of rows in `ratings.csv`):

| run_id | lang | story cell | cost_usd | wall_s (col 23) | container_valid | streams | resolution | duration | loudness | true_peak | silence | subtitles_present | subtitle_text | pass | hints (advisory) | ratings voice / subtitles |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| _(L1)_ | vi | `vi-ghost-b3-sol-standard` (3.87 min, D4 shortfall −22.6 % — owner decision 2026-09-29) | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| _(L2)_ | en | `en-ghost-b1-luna-standard` | — | — | — | — | — | — | — | — | — | — | — | — | — | — |

Record per run: `render-quality.json` `pass` (or the failing check + re-render
note, counted), the advisory hints (`subtitle_sync_suspect`, `subtitle_cues_long`,
`loudnorm_dynamic`, `music_none`), the `render_check.py` and `--normalize` wall
times (NFR-05-1 / 11.3.5-T1 input) in `trial/runs/<run_id>/metadata.json`, and
loudness before/after. `trial/measurements.csv` gains one row per run via
`finish_run.py` (`run_status = trial`, `subtitle_detected_language` ≠ `n/a`).
Media, submit body, check/quality JSON and metadata stay in
`$NTS_HOME/runs/<run_id>/` (T11).

## Dry build (no network, no render)

`mpt_client.build_body` (`trial/tools/mpt_client.py:318`) run on the materialised
specs, then `encode_body`:

| Run | D3 key set | `voice_name` | `bgm_type/file/volume` | `video_subject` | terms | script chars | wire bytes |
|---|---|---|---|---|---|---|---|
| L1 | exact, 38/38 | `kokoro:Mai Anh` | `custom` / `apprehensive-at-best.mp3` / `0.057` | 1st line of the staged story (≤ 200 chars, matches the cell title) | 20 | 4538 | 8015 |
| L2 | exact, 38/38 | `kokoro:Adam` | `custom` / `apprehensive-at-best.mp3` / `0.057` | 1st line of the staged story (≤ 200 chars, matches the cell title) | 21 | 4932 | 6881 |

Both bodies match `D3_KEYS` exactly (no missing, no extra; `n_threads` absent),
`video_aspect 16:9`, `clip_duration 10`, `concat sequential`,
`match_materials_to_script true`, `video_source openai_image`, `fit_mode cover`,
subtitles on with the landscape preset values, `video_script` ≤ 20,000 code
points and body ≤ 2 MiB (D15 S2). The dry run used materialised copies under
`$NTS_HOME/runs/m0-trial-inputs/`; nothing was submitted and no key was read.

## Ratings (owner action)

Raters and the gate: **L1 (vi)** — the owner **and** one other native speaker,
each watching L1 once on a phone and once on a desktop, rating voice naturalness
and subtitle readability 1–5 independently; **≥ 4/5 on both axes, both raters,
both devices**. **L2 (en)** — one fluent listener rates en subtitles ≥ 4/5 (the
en voice rating does not gate). Rows go in `ratings.csv`:

```
run_id,rater,rater_role,device,axis,score,notes
```

`rater` is a pseudonymous id (`R1`, `R2`, …; the name mapping stays in
`$NTS_HOME/m0-trial/raters.csv`, D15 S8), `rater_role` ∈ `owner|native|fluent`
(the gate names a role per row), `device` ∈ `phone|desktop`, `axis` ∈
`voice|subtitles`, `score` = integer 1–5, `notes` free text. The task's inline
column list (`…, note`) is superseded by this header: the gate needs the role to
prove "owner + 1 native" for vi and "1 fluent listener" for en.

**Blocked:** the second native vi rater and the fluent en rater are the only
missing pieces (04.2.1-T1 shipped with the owner-only D11 exception). The render
and its measured numbers are not blocked by them; the rating-dependent
acceptance rows are.

## Hand-over to 03.3.1-T1 (TBD)

Not yet — no run id exists. When L1/L2 have run: post both run ids on `hub` and
confirm the submit body used D3's fields (38-key set, verified above), so
03.3.1-T1 can read `$NTS_HOME/runs/<run_id>/openai_usage.jsonl` for token rows
and skipped-image counts. The runs stay tagged `trial`.

## Files

- Tracked here: `README.md`, `ratings.csv` (header only), `L1-vi.spec.json`,
  `L2-en.spec.json`. This task's other tracked artefact is
  `trial/tools/apply_quality_policy.py` (the D10 stdlib mirror used in the
  post-render pipeline, written by the sibling slice).
- Untracked (`$NTS_HOME/runs/m0-trial-inputs/`): `L1-vi.story.txt`,
  `L1-vi.terms.json`, `L2-en.story.txt`, `L2-en.terms.json` (harness copies) and
  the two staging helpers `make_inputs.py` (adds the title line, checks
  `video_script.txt` = derived body, prints the length estimate) and
  `materialise_spec.py` (placeholder substitution + `--self-test`); both
  `--self-test` exit 0. The materialised `*.spec.json` copies sit beside them.
