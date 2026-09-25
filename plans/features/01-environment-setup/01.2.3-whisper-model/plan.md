# 01.2.3 — faster-whisper model: feature plan

> Story: `../../../01-environment-setup.md` §4 **01.2.3** (Must · S). Contract: `../README.md` (facts §1, decisions D1–D12, template §6, gates §7).
> Legend: ✅ verified (README §1 id) · ⚠️ confirm at the pinned commit/install · [DECISION] · [INFERENCE].

## 1. Scope

**In:** place the pinned `large-v3-turbo` CTranslate2 model into MPT's local lookup dir so no hub call happens at run time (F17); record revision + sha256 per file for `CARRY-OVER.md`; prove offline load through MPT; measure a Mac baseline (wall, RTF, peak RSS, detected language) on vi + en samples.

**Out:** WER / subtitle accuracy (05.2.1); forcing `language` or `initial_prompt` in MPT (CR-5 → 05); subtitle font (CR-6); per-stage RSS inside a real run (01.3.2-T1); GPU numbers (plan 11, CR-9).

Story acceptance rewritten (supersedes "downloaded once to a known cache dir" — the model is **pre-placed**, not downloaded by the first job):
- Given `[whisper] model_size = "large-v3-turbo"` (D5 guard) and the model at `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/`, When the first subtitle job runs with `HF_HUB_OFFLINE=1`, Then MPT logs `loading model: <that path>` and makes no hub download.

## 2. Confirmed facts

| Id | Fact used here |
|---|---|
| ✅ M3 | MPT loads `<mpt_root>/models/whisper-<model_size>/model.bin` if present, else downloads by name; `language` not passed (auto-detect); `beam_size=5`, `word_timestamps=True`, `vad_filter=True` with `min_silence_duration_ms=500`; logs `loading model: …` |
| ✅ M1 | `[whisper]` defaults `model_size = "large-v3"`, `device = "cpu"`, `compute_type = "int8"`; template (01.2.2-T2) sets `large-v3-turbo` |
| ✅ M7 | MPT pins `faster-whisper==1.1.0`; Python 3.11 via uv |
| ✅ W1 | faster-whisper MIT; `large-v3-turbo` → `mobiuslabsgmbh/faster-whisper-large-v3-turbo`; CTranslate2 no Metal/MPS → CPU int8 |
| ✅ E1 | `$NTS_HOME` on FileVault internal disk, 66 GiB free |
| ⚠️ | Licence of the converted weights repo: Whisper weights MIT [INFERENCE] — read the model card at the pinned revision |
| ⚠️ | Whether upstream MPT `.gitignore` ignores `models/` — check at `MPT_COMMIT` |
| ⚠️ | huggingface_hub CLI name at the resolved version: `hf download` (newer) vs `huggingface-cli download` (older) |
| ⚠️ | Exact file list of the repo (expected `config.json`, `model.bin`, `tokenizer.json`, `vocabulary.json`, `preprocessor_config.json` [INFERENCE]) and model size (~1.6 GB [INFERENCE]) |
| ⚠️ | Exact MPT log text around `loading model:` (path vs name) at `MPT_COMMIT` |

## 3. Domain and data

| Item | Location | Tracked | Written by |
|---|---|---|---|
| `WHISPER_MODEL_REPO=mobiuslabsgmbh/faster-whisper-large-v3-turbo`, `WHISPER_MODEL_REVISION=<40-char sha>` | `worker-node/pins.env` (D2) | yes | 01.1.1-T2 (value confirmed in T1) |
| Model files | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` (D1) | no | `fetch-whisper-model.sh` |
| Checksum list `whisper-large-v3-turbo.sha256` (`<sha256>  <file>`, sorted) | `worker-node/mpt/whisper-large-v3-turbo.sha256` [DECISION, new file] | yes | T1 |
| Baseline | `trial/baselines/whisper-mac-<YYYYMMDD>.json` (D1) | yes | T2 |
| Throwaway script | `$NTS_HOME/runs/whisper-baseline/bench.py` | no | T2 (not committed) |

Baseline JSON shape [DECISION]:
```json
{"component":"faster-whisper","date":"YYYY-MM-DD","host":"Mac M4 16GB macOS 27.0",
 "faster_whisper":"1.1.0","ctranslate2":"<ver>","model_repo":"…","model_revision":"<sha>",
 "device":"cpu","compute_type":"int8","params":{"beam_size":5,"word_timestamps":true,"vad_filter":true,"min_silence_duration_ms":500,"language":null},
 "omp_num_threads":"unset",
 "samples":[{"file":"vieneu-vi-1000.wav","lang_expected":"vi","audio_s":0.0,"load_s":0.0,"transcribe_s":0.0,"wall_s":0.0,"rtf":0.0,"peak_rss_mb":0,"lang_detected":"<code>","lang_prob":0.0,"segments":0}],
 "notes":"…"}
```
`rtf = transcribe_s / audio_s` (load excluded; `wall_s` includes load).

## 4. Use cases

| # | Procedure | Command |
|---|---|---|
| UC1 | Fetch/verify model (idempotent) | `worker-node/bin/fetch-whisper-model.sh` |
| UC2 | Verify only, no network | `worker-node/bin/fetch-whisper-model.sh --verify` |
| UC3 | Offline proof | start MPT API with `HF_HUB_OFFLINE=1` (01.2.2-T4 script), submit subtitle job, grep log |
| UC4 | Baseline | `/usr/bin/time -l uv run --project "$NTS_HOME/src/mpt" python bench.py <wav>` |

## 5. API and events

N/A — no service API; only MPT's existing log line `loading model:` (M3) is consumed.

## 6. UI

N/A — CLI only.

## 7. Jobs and integrations

- Hugging Face Hub download at a pinned revision (T1 only, once). Run time: none (`HF_HUB_OFFLINE=1`).
- `bootstrap.sh mpt` (01.1.2-T3) does **not** download weights (01.1 contract); runbook order is `bootstrap.sh mpt` → `fetch-whisper-model.sh` [request to 01.1 owner for `worker-node/README.md`]. `strip-bundled-music.sh` (01.2.2-T3, called by bootstrap) and any `git clean` MUST NOT touch `models/` (never `git clean -fdX`; T1 checks by re-running bootstrap then `--verify`).
- Samples from 01.2.1-T4 (VieNeu WAV handoff) feed T2.

## 8. Security and privacy

Supply chain only: model fetched by pinned 40-char revision (D2), every file sha256-recorded and re-verified on every run of `fetch-whisper-model.sh` (and `--verify`); mismatch → exit 1, no load. No secrets involved (public repo; no HF token). Samples are synthetic TTS audio — no personal data.

## 9. Test plan

| Check | Task | Expected |
|---|---|---|
| `model.bin` present + all sha256 match | T1 | `fetch-whisper-model.sh --verify` exit 0 |
| Corrupt a copy → verify fails | T1 | exit 1 on a temp copy (never the real file) |
| Offline MPT load | T1 | log `loading model: …/models/whisper-large-v3-turbo`; no `huggingface.co` request; job finishes |
| Re-run fetch | T1 | no download, exit 0 |
| Baseline recorded | T2 | JSON valid (`python -m json.tool`), ≥ 2 samples (vi, en) |
| Language detection | T2 | vi sample → `vi`; misdetection flagged in `notes` + CR-5 |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 01.2.3-T1 | Fetch pinned model into MPT lookup dir + checksums + offline proof | XS 0.25 | 01.1.1-T2, 01.1.2-T3, 01.2.2-T1, 01.2.2-T4 | 01.2.3-T2, 01.2.2-T6, 01.3.3-T1 |
| 01.2.3-T2 | Speed / RAM / language baseline | XS 0.25 | T1, 01.2.1-T4 | 01.3.1-T4, 05.2.1 |
| **Total** | | **0.5 d** (= README §9) | | |

Size note: T1 is tight at XS if the offline proof waits on 01.2.2-T4 start scripts; if they slip, the offline proof may run with a plain `uv run python main.py` plus `HF_HUB_OFFLINE=1` (still valid). Sizes kept.

New files introduced: `worker-node/bin/fetch-whisper-model.sh` (D9), `worker-node/mpt/whisper-large-v3-turbo.sha256`.

## 11. Rollout

1. Day 2 after 01.2.2-T1: T1 → T2.
2. Contributes to gate **01-ready** (wiring render 01.2.2-T6 runs subtitles offline).
3. Carry-over (01.3.3): repo + revision + sha256 list + `fetch-whisper-model.sh`; node uses `device = "cuda"`, `compute_type = "float16"` (CR-9) — same weights, re-baseline on GPU.
4. Rollback: delete `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` and re-run the script.
