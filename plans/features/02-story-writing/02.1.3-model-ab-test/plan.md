# 02.1.3 — Model A/B test (M0): luna vs sol, harness, blind scoring, decision

> Refines: `../../02-story-writing.md` §1 02.1.3 (M0) · §5.2 · §5.4 · §6
> Contract: `../README.md` (D3, D4, D5, D6, D7, D14, D15, D16, D21, D22, M1, M3, O2–O5, O9, F01, F10, F30, CR-10, CR-17, TL rulings T23–T27)
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

## 1. Scope

Story 02.1.3 is an **M0 evidence spike**, not product code. It answers one question with numbers: is `gpt-6-luna` good enough for the six presets, or does a preset/language need `gpt-6-sol`? (story `A1`, D15.)

In scope:

- `trial/tools/story_harness.py` — stdlib-only harness that calls the Responses API with our real presets and the real `story.v1` schema through the 01 usage tap (F01 removes MPT from this loop).
- `trial/tools/story_validator.py` — throwaway validator mirroring the D3 validator rules.
- `prompts/fixtures/briefs.{vi,en}.json` (9 brief ideas per language, 3 per topic) + `prompts/fixtures/fact-sheet.true_crime.v1.json` — authored by **T0** (T34) so preset acceptance can run before the A/B.
- `prompts/fixtures/rubric.md` — D16 rubric with written 1/3/5 anchors for all seven dimensions (T1).
- The 36-story matrix run, a blind kit, 2 raters per language (r1: 2 AI rater passes, 02 T106), `scores.csv` + `aggregate.csv` + `REPORT.md`.
- The D15 verdict written into `DECISIONS.md` §3 (CR-10), the frozen `prompts/manifest.json` release `2026.10.1`, and the token/latency/cost numbers handed to D4 and 12.1.1.
- The MPT hand-off artifacts (`video_script.txt`, `video_terms.json`, M3), so the presets can be exercised through MPT in 03 without MPT's own script generation.

Out of scope (owned elsewhere, do not create here):

| Not here | Owner |
|---|---|
| `prompts/system/writer.v1.md`, six presets, `story.v1.json`, length table, `structure-vocab.v1.json` | 02.1.1-T0/T1–T3, 02.1.2-T1, 02.1.5-T1 |
| `prompts/` skeleton, `REFERENCES.md`, provenance checkbox | 02.1.4-T1 |
| `OpenAiTextClient`, `ITextClient`, `TextCallRequest`, `TextStep` | 02.2.1-T2, 02.2.3-T1/§D21 |
| `StoryDraftValidator` (Domain) — the harness validator is a throwaway copy | 02.2.1-T1 |
| `PromptCatalog` startup validation + embedded-resource hashing | 02.2.3-T1 |
| `price_versions`, `usage_records`, `TextCostCalculator`, `IUsageMeter` | 02.2.4-T1 (D14) |
| DB, EF, migrations, `generation_runs` — the harness writes **no** row (D2 excludes it) | 02.2.2-T1 |
| MPT `pipeline` execution, images, TTS, render | 03–06, 01.3.x |

Boundary with 02.1.1/02.1.2: they **author** the prompts and the schema; 02.1.3-T0 **consumes** them read-only. If a preset is missing or the schema has no `additionalProperties: false`, T0 fails loudly and the fix belongs to the preset task. 02.1.3-T2's freeze is the input 02.2.3-T1 hashes at start-up.

## 2. Confirmed facts

Only what this story's design depends on; every id is defined in `../README.md` / `../../01-environment-setup.md` and is not restated here.

| # | Why it matters here |
|---|---|
| F01 | MPT cannot validate our presets (plain text, ≤ 10 paragraphs, own Initialization block) → a **direct** harness. |
| F10 | 12 stories = 1 sample/cell → **36 stories**, 2 blind raters per language (r1: AI passes, 02 T106). |
| M3 ✅ | `video_script` / `video_terms` sent directly skip MPT's LLM — this is the hand-off contract. |
| O2 ✅ | Responses + `text.format = json_schema`, `strict: true`. |
| O3 ✅ | luna/sol rates and the > 272K-input surcharge; drives §7's budget arithmetic. |
| O4 ✅ | `max_output_tokens` includes reasoning; a cap cut is `incomplete_details.reason = max_output_tokens`. |
| O5 ✅ | `reasoning.effort` values; **`store` defaults to `true`** → must send `false`. |
| O9 ✅ | ≥ 1,024-token static prefix first, dynamic data last (the D5 prompt order exists for this). |
| D3 | `story.v1` shape, the validator rule list, the `body`/`keywords` derivations the hand-off files mirror. |
| D4 | Tier × language bands, per-tier `max_output_tokens` (short writer 2,500 — 02 T66), vi = syllables, ±15 % band. |
| D5 | Catalog paths, closed `{{placeholder}}` list, prompt order, `manifest.json` shape. |
| D6 | The user message is **one** JSON object with a fixed key set; untrusted data never enters a template. |
| D7 | Call shape, SDK retries off + one bounded retry, the outcome mapping the harness reproduces. |
| D14 | `usage` fields and the cost formula the harness reimplements. |
| D15 | luna stays default unless its blinded mean is > 10 % below sol's, or its vi *natural language* mean < 3.5 → sol for that language. |
| D16 | Seven dimensions, 1–5, anchors in `prompts/fixtures/rubric.md`; the same keys are `review.v1`'s `scores`. |
| V1 ⚠️ | vi tokenises per syllable → "words" = syllables. |
| 01 D3 | Keychain service **`nts.openai.story`**, account `$USER`; key never in repo/logs. |
| 01 D6 | Usage tap on `127.0.0.1:8002` forwards `/v1/*`, holds no key, logs usage only (`trial/runs/<run_id>/openai_usage.jsonl`). |
| 01 D9 | Stdlib-only Python via `uv run --no-project --python 3.12`; exit codes 0/1/2. |
| 01 D7 ✅ T23/CR-17 | `nts-trial-story` allow-lists luna; **`gpt-6-sol` is added for the 02.1.3-T1 window only**, then removed, and the $5 hard limit stays. 01.2.4-T3 check C7 uses `gpt-6-astra` as the not-allowed model. |

## 3. Domain and data

No DB, no EF, no migration, no API, no job. The whole story is a filesystem contract under `trial/`, `prompts/fixtures/` and `prompts/manifest.json`.

### 3.1 Run identity

```
run_id = "<lang>-<preset>-<briefId>-<modelSlug>-<tier>"      e.g. vi-ghost-b1-luna-standard
modelSlug = luna | sol                                       (gpt-6-luna | gpt-6-sol)
tier      = standard for the 36 rated cells (T24); short|long for the 12-cell unrated sweep (T1 §1b)
briefId   = b1 … b9                                          (global: b1–b3 ghost, b4–b6 spiritual, b7–b9 true crime — T0 §fixtures)
raterId   = r-vi-1 | r-vi-2 | r-en-1 | r-en-2
storyId   = S01 … S36                                        (blind kit id, shuffled per language)
```

`--run-id` overrides the derived id; a rerun of the same cell appends `-r2`, `-r3` (never overwrites a previous run directory). Deterministic ids exist so the matrix is enumerable, resumable and diffable. `<lang>` is the **brief's own `language` field**, not the `--lang` flag: two briefs files that repeat the ids (`briefs.vi.json` + `briefs.en.json`) therefore enumerate distinct `vi-…` and `en-…` cells, each assembled from its own language's preset and shared blocks, and `--lang` acts only as a filter — with it given, a brief of the other language is skipped with a note (exit 2 when no cell remains). In `--brief-ids` mode an id resolves per file and language, never last-file-wins.

### 3.2 Files produced by one harness call

`trial/stories/<run_id>/` — **six files when the call succeeded (`outcome=ok`), three when it failed**:

| File | Content |
|---|---|
| `request.json` | `{ "tap_base_url": "http://127.0.0.1:8002/v1", "endpoint": "/responses", "model": "gpt-6-luna", "run_id": "…", "tier": "standard", "language": "vi", "preset": "ghost", "prompt_version": "rel=…;sys=writer.v1;preset=ghost.vi.v1;schema=story.v1", "body": { … } }`. **No `Authorization`, no API key, no header block.** The key is added in memory only when the request is sent. |
| `response.json` | The raw provider response body (includes `id`, `status`, `incomplete_details`, `usage`, `output[]`). |
| `story.json` | The parsed `story.v1` object (the model's JSON, verbatim — the backend-added `schema_version`/`fact_sources` are **not** present here, D3). |
| `video_script.txt` | `hook.text` + `\n\n` + paragraph texts joined by `\n\n` + `\n\n` + `ending.text` (D3 `body` derivation) → MPT `video_script` (M3). |
| `video_terms.json` | JSON array of strings: `hook.keywords ++ paragraphs[].keywords ++ ending.keywords`, in order (D3 `keywords` derivation) → MPT `video_terms` (M3). |
| `metrics.json` | See 3.3. |

A **failed** cell (`provider_unavailable` / `provider_error` / `moderated` / `invalid_output` with no parsed story) writes **three** files — `metrics.json`, `request.json`, `response.json` — so the attempt is still evidence (QA-02D-11). `story.json`, `video_script.txt` and `video_terms.json` exist only when a `story.v1` object was parsed.

### 3.3 `metrics.json`

```json
{
  "run_id": "vi-ghost-b1-luna-standard",
  "model": "gpt-6-luna", "tier": "standard", "language": "vi", "preset": "ghost", "brief_id": "b1",
  "provider_request_id": "req_…", "http_status": 200, "attempt": 1,
  "latency_ms": 11840, "started_at": "2026-09-24T03:11:02Z", "finished_at": "2026-09-24T03:11:14Z",
  "outcome": "ok",
  "json_valid": true,
  "incomplete_reason": null,
  "usage": { "input_tokens": 4020, "cached_tokens": 0, "cache_write_tokens": 0,
             "output_tokens": 1940, "reasoning_tokens": 620 },
  "tokens":  { "body_tokens": 1012, "segments": 22, "keywords_total": 41 },
  "cost_usd": 0.001372,
  "validator_issues": [ { "rule": "length_out_of_range", "severity": "advisory", "segment": "p:14", "evidence": "…" } ],
  "notes": ""
}
```

`outcome ∈ {ok, invalid_output, moderated, provider_error, provider_unavailable, funds_exhausted}` (D7 mapping). `cost_usd` uses the D14 formula and the O3 rates; a run with no `usage` gets `cost_usd: null` and `notes: "usage absent"` — it is never silently recorded as 0.

### 3.4 Blind-kit and score files

`trial/ab/<round>/` (`<round>` = `r1` for the first pass, `r2` after a preset change):

| File | Columns / shape |
|---|---|
| `key.csv` | `story_id,run_id,model,preset,language,brief_id,tier` — the only file that unblinds. Kept out of the raters' kit. |
| `stories/<story_id>.md` | The story text only: title, body. No model, no preset, no language hint beyond the language itself, no `run_id`, no metrics. |
| `scores.csv` | `story_id,rater_id,rater_language,rater_native,dimension,score,notes,scored_at` — long format, one row per rater × story × dimension. |
| `aggregate.csv` | `model,preset,language,dimension,n,mean_rating,json_valid_rate,tokens_mean,latency_p50_ms,latency_p95_ms,cost_usd_mean` |
| `REPORT.md` | The evidence: matrix summary, per-dimension means, the D15 test applied with the numbers, hand-off numbers. |

Rubric dimension keys (D16, exact): `hook`, `pacing`, `atmosphere`, `natural_language`, `originality`, `factual_care`, `length_fit`. `factual_care` is `n/a` outside true-crime presets and is excluded from those means (never scored 0).

### 3.5 Prompt files read (read-only, owned by 02.1.x)

```
prompts/system/writer.v1.md
prompts/story/{ghost|spiritual|true_crime}.{vi|en}.v1.md
prompts/shared/ear-rules.{vi|en}.v1.md
prompts/shared/overused.{vi|en}.v1.txt
prompts/shared/keyword-rules.v1.md
prompts/shared/structure-vocab.v1.json
prompts/schemas/story.v1.json
```
Written by 02.1.3: `prompts/fixtures/briefs.{vi,en}.json` + `prompts/fixtures/fact-sheet.true_crime.v1.json` (**T0**, T34 — so preset acceptance can run first) · `prompts/fixtures/rubric.md` (T1) · `prompts/manifest.json` (T2).

`prompts/fixtures/briefs.<lang>.json` — array of 9, global ids `b1..b9`:
```json
[ { "id": "b1", "topic": "ghost", "title": "The lighthouse keeper's last log",
    "brief": "Two sentences of premise and one line of desired atmosphere.",
    "tier": "standard", "language": "vi", "synthetic": true } ]
```
Three briefs per topic: `b1–b3` = ghost, `b4–b6` = spiritual, `b7–b9` = true_crime; the same nine ids in both languages — the `language` field (not `--lang`) is what names a cell's language and its `vi-…`/`en-…` run-id prefix, so the same id in the two files is two cells. The `tier` field is the fixture's intent; the run tier is the CLI flag (T24: `standard`). ⚠️ The true-crime briefs and their fixture fact sheet are **fiction framed as true crime, no real persons, no real incidents** — D11/D13 privacy and 02.8.2's real-person problem; they must be obviously synthetic so the A/B never tests a real person.

### 3.6 Git tracking boundary [DECISION]

Tracked: `trial/tools/**`, `trial/ab/**` (key, scores, aggregate, report, blind stories), `prompts/fixtures/**`, `prompts/manifest.json`.
Untracked (`.gitignore` `trial/stories/`): the 36 raw response directories — they embed provider output and are regenerable; keeping them out avoids bloat and keeps raw model text out of the repo.
⚠️ The 01 trial already writes `trial/runs/<run_id>/openai_usage.jsonl` (01 D6) and `trial/schema.md` (01 D8) — confirm which `trial/` subpaths 01 already ignores before adding the entry, so the ignore file is not clobbered.

## 4. Use cases

| # | Use case | Precondition | Result |
|---|---|---|---|
| UC1 | Single harness run (T0) | tap listening on `127.0.0.1:8002`, Keychain item present, preset + schema present | `trial/stories/<run_id>/` with six files; exit 0 |
| UC2 | Matrix batch (T1) | 36 cells enumerable; `$NTS_HOME/current_run` set to a 01 D10 batch id per language (for example `20261001-1030-vi-landscape-01`; the cell ids do not match the tap regex, T0 Notes) | 36 run directories, each with `tap_run_id`; `--only-missing` resumes; token sums match the tap JSONL |
| UC3 | Validate a story (T0) | a `story.json` + the tier/language | issue list; exit 0 clean / 1 issues |
| UC4 | Blind kit (T1) | 36 completed runs | `trial/ab/r1/{stories/*.md,key.csv}` with no identity leak |
| UC5 | Aggregate + decide (T1) | `scores.csv` complete, `key.csv` available | `aggregate.csv`, `REPORT.md`, the D15 verdict printed |
| UC6 | Freeze + record (T2) | presets final, verdict computed | `prompts/manifest.json` `2026.10.1`, `DECISIONS.md` §3 row, `trial/ab/handoff.json` |

## 5. API and events

None. This story adds no HTTP route, no domain event, no `ProblemDetails` code, no outbox row. The D8 catalogue does not apply. The D2 `generation_runs` / partial-unique-index rules do not apply — the harness is not the application and must never write to the app's DB.

External contracts consumed only:

| Contract | Shape |
|---|---|
| Usage tap (01 D6) | `POST http://127.0.0.1:8002/v1/responses`, forwarded to `$OPENAI_BASE_URL/responses` (pinned relay `https://api.aixingialaire.shop/v1`, 01 CR-15). TLS verified by the tap; `Authorization: Bearer <key>` added by the harness in memory. |
| Responses API | `{ model, input: [ {role, content}, … ], text: {format:{type:"json_schema", name:"story", schema:<story.v1.json>, strict:true}}, reasoning:{effort:"medium"} (Writer, owner 2026-09-25), max_output_tokens, store:false, safety_identifier, prompt_cache_key }` + header `X-Client-Request-Id: <run_id>:writer:1`. |
| MPT hand-off (M3) | `video_script` (from `video_script.txt`), `video_terms` (from `video_terms.json`) sent in `VideoParams` — skips MPT's LLM entirely. |

`prompt_version` string written into `metrics.json`/`request.json` uses the D5 composite form, minus the steps this spike does not run: `rel=<manifest release>;sys=writer.v1;preset=<topic>.<lang>.v1;schema=story.v1`.

## 6. UI

None. The operator surface is the CLI (`--help`) plus `REPORT.md`/`aggregate.csv`. A "checkpoint #1 story review" screen (10.5.x) is unrelated; this spike has no screen and no endpoint, so a smoke test on a UI is impossible by design — the observable proof is the artifacts plus the printed verdict.

## 7. Jobs and integrations

No scheduler, no Hangfire, no container. Runtime prerequisites and their failure modes:

| Prerequisite | Check | Failure behaviour |
|---|---|---|
| Usage tap listening | `lsof -nP -iTCP:8002 -sTCP:LISTEN` before the batch | exit 2 with "usage tap not listening on 8002 (01 D6)" — the harness must **not** call the upstream (`OPENAI_BASE_URL`, 01 CR-15) directly, or the trial's spend/usage evidence is lost |
| Keychain item `nts.openai.story` | `security find-generic-password -a "$USER" -s nts.openai.story -w` | exit 2 naming the service; never echo the value |
| `uv` + Python 3.12 | `uv run --no-project --python 3.12 -c "import sys;print(sys.version)"` | exit 2 |
| `$NTS_HOME/current_run` | read from env/`$NTS_HOME` | not fatal: the tap files usage under `no-run`; `metrics.json` still carries the real numbers, so the batch continues with a warning |
| Budget | `--max-usd` accumulated from `cost_usd` | stop before the next call, print the spend and exit 1 |

Sequencing and rate: calls are **serial** with `--sleep-ms` (default 200) to stay under the per-project RPM and to keep latency samples clean. On 429/5xx/timeout the harness retries **once** after `Retry-After` (or jittered backoff), matching D7's "SDK retries off, one resilience layer".

USD budget for the A/B — from O3 rates and the D4 `max_output_tokens` [INFERENCE, arithmetic shown so the TL can check it]. Rated tier is **`standard`** (T24); the 12-cell unrated sweep (T1 §1b) supplies the other two tiers:

| Cells | Input ≈ | Output ≤ | luna | sol |
|---|---|---|---|---|
| 1 story, `short` | 4,000 tok | 2,500 tok (02 T66) | 4,000×0.10/1e6 + 1,000×0.50/1e6 ≈ **$0.0009** | 4,000×2/1e6 + 1,000×10/1e6 ≈ **$0.018** |
| 1 story, `standard` | 4,000 tok | 6,000 tok | 4,000×0.10/1e6 + 2,000×0.50/1e6 ≈ **$0.0014** | 4,000×2/1e6 + 2,000×10/1e6 ≈ **$0.028** |
| 1 story, `long` | 4,000 tok | 10,000 tok | ≈ **$0.0019** | ≈ **$0.05** |
| 18 sol `standard` | — | — | — | ≈ **$0.5** |
| **36 rated `standard` (18/18)** | — | — | ≈ $0.03 | ≈ $0.5 |
| **+ 12-cell sweep (6 `short` + 6 `long`, luna)** | — | — | ≈ **$0.02** | — |

Total ≈ **$0.6–1.5** [INFERENCE]; worst case with 6,000-token outputs and no caching ≈ **$2.2**. Everything fits the 01 D7 `$5` project limit. `--max-usd` defaults to **5.0** with a hard stop and **no paging in M0** (T25): the batch aborts cleanly before the next call and resumes with `--only-missing`.

## 8. Security and privacy

- Key only in memory: read from the Keychain at process start into a local variable; never written to `request.json`, `response.json`, `metrics.json`, `REPORT.md`, stdout or a log; never passed on the command line.
- `store: false` on every call (O5 default is `true`) so no prompt/response is retained by the provider beyond the request.
- `safety_identifier = sha256("nts-m0-harness")` (hex) — the harness acts as one platform identity; it is not a user id and carries no PII.
- `X-Client-Request-Id` = `<run_id>:writer:<attempt>`; no user id, no e-mail.
- Fixtures are synthetic. No real person, no real incident, no real URL. True-crime briefs are labelled `synthetic: true` in the fixture and checked by T1 before the run; the A/B must not create material that 02.8.2's real-person detector has to catch.
- Blind kit strips model, preset, brief id and `run_id` from what a rater sees; `key.csv` is not opened by a rater. Raters are paid helpers, not anonymised data subjects; `scores.csv` holds only `rater_id` (no name, no contact).
- Raw model text is kept out of git (§3.6).
- Key rotation: the 01 D7 project keys (`nts-trial-story-mac-<YYYYMM>`) are revoked at M0 close; this spike must be finished before that, or the harness re-read fails cleanly (exit 2).
- The CR-17 window is time-boxed: `gpt-6-sol` is allow-listed on `nts-trial-story` **only** for the 02.1.3-T1 run and removed afterwards (T23), and the `$5` project hard limit is unchanged. If the window has closed, the batch fails with `provider_error` rather than being rerun on a stale key.

## 9. Test plan

The story's verification is empirical: the artifacts and the printed verdict are the evidence, and the checks below are run by hand or by the harness's own `--check` paths (no test project is added — this is a spike, and D20's `TestKit` is for the M1 product code).

T0 checks (throwaway validator + harness):
- `story_harness.py --help` exits 0 and documents `--preset --brief --brief-id --brief-ids --briefs-file --lang --model --tier --out --run-id --dry-run --matrix --force --max-usd --only-missing --sleep-ms --timeout --key-service --tap-base-url --validate-only`.
- `--dry-run` writes `request.json` and makes **no** network call; `grep -c Authorization trial/stories/<run_id>/request.json` = 0 and the key is absent from the whole directory.
- one live `short` run on luna → six files; `metrics.json.outcome == "ok"`, `json_valid == true`, `provider_request_id` non-empty, `cost_usd > 0`.
- validator: `validate_wellformed_story_returns_no_issues`, `validate_title_over_100_returns_issue`, `validate_keyword_with_person_name_returns_issue`, `validate_too_few_segments_returns_issue`, `validate_markdown_bracket_returns_issue`, `validate_unknown_fact_ref_returns_issue`, `body_join_matches_d3_derivation`, `keywords_order_is_hook_paragraphs_ending`, `tag_total_over_400_returns_issue`.
- missing preset / missing schema / bad `--model` → exit 2 with a named message (not a traceback).

T1 checks:
- `ls trial/stories | wc -l` = 48 (36 rated `standard` + 12 unrated sweep cells); every `outcome=ok` directory has exactly 6 files and every failed cell has exactly 3 (`metrics.json`, `request.json`, `response.json`) — the plan §3.2 contract for a failure, not a defect (QA-02D-11); `grep -L '"json_valid": true' */metrics.json` lists the invalid ones (expected 0; anything listed is recorded in the REPORT, not hidden).
- blind leak: `grep -rwiE 'luna|sol|ghost|spiritual|true_crime|run_id' trial/ab/r1/stories/` (whole words, 02 T108) returns **nothing**.
- `key.csv` row count = 36 (rated cells only — the sweep never enters the kit), one per story id; story ids unique.
- `scores.csv`: for each (story, language) the rater count is 2 (native, or `ai-` passes under 02 T106); `factual_care` rows exist only for `true_crime`.
- `aggregate.csv` reproduces a hand-computed mean for at least one cell (the test asserts the arithmetic, not just a row count).
- D15 applied mechanically: the printed verdict is derived from `aggregate.csv` means (luna vs sol per language; the vi `natural_language` floor 3.5), not asserted from a memory of the run.
- `sum(cost_usd) ≤ 5.0` (expected ≈ 0.6–1.5); p50/p95 latency and mean cost printed **per tier** as well as per model (the sweep supplies `short` and `long` for D4/12.1.1).

T2 checks:
- `freeze_manifest.py --check` exits 0 over `prompts/**`; after `touch prompts/story/ghost.vi.v1.md` it exits 1 (mutation check proves the hash path is real).
- `DECISIONS.md` §3 "Chosen stack" story-writing row names the chosen model with the margin and the evidence path; if nothing changed, the row is left as-is and §3 gains one line "02.1.3: no change, evidence `trial/ab/r1/REPORT.md`".
- `trial/ab/handoff.json` parses and contains `model_choice`, `words_per_minute_measured` (or `null` + a note if 01.3.1-T4 has not landed), `tokens_per_story`, `cost_per_story_usd`, `p50_ms`, `p95_ms`.
- `OpenAi:Models:Writer` default is **unchanged** unless the D15 condition fired.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 02.1.3-T0 | M0 story harness | S (0.50 d) | 02.1.1-T0, 02.1.2-T1, 01.2.4-T3 (tap + key) | 02.1.3-T1 |
| 02.1.3-T1 | 36-story run + blind scoring | M (1.00 d) | 02.1.3-T0, 02.1.2-T1, 02.1.5-T1 | 02.1.3-T2 |
| 02.1.3-T2 | Decision in `DECISIONS.md` + presets v1 freeze | XS (0.25 d) | 02.1.3-T1 | 02.2 (writer impl), 12.1.1 |

**Total 1.75 d** (sizes unchanged by T23–T27: T1's M absorbs the `standard` tier and the 12-cell sweep). README §9 records this story moving from the high-level `0.75` to `1.75` (`+1.00`): the harness replaces MPT (F01), and the matrix grows from 12 to 36 stories with 2 raters per language (F10). The README §8 mapping `T0 (S) · T1 (M) · T2 (XS)` is the authority for the sizes; T0 did not exist in the high-level table at all (F30).

Critical path: `02.1.1-T0` (schema + presets skeleton) → `T0` → `T1` → `T2` → M0 exit for 02. `T1` cannot start before the six presets and all shared rule files exist, because a partial preset set silently measures the wrong thing. `T2` cannot start before `T1`'s aggregate exists.

Rater effort sits inside `T1`'s day but needs 4 people for ≈ 1 h each at `standard`; the schedule risk is availability, not compute.

### Open items for the TL

Decided in README §10 and applied here: sol allow-listed for the T1 window only (T23/CR-17) · rated tier `standard` (T24) · `trial/stories/**` untracked / `trial/ab/**` tracked, `n=1` blocks the freeze, synthetic true-crime briefs with `factual_care` excluded from D15, `--max-usd 5` hard stop with no paging (T25) · `words_per_minute_measured = null` accepted, D4 rates stay defaults until 01.3.1-T4 (T26) · manifest keys relative to `prompts/`, with `fixtures/**`+`tools/**`+`manifest.json`+`REFERENCES.md`+`**/.gitkeep`+dotfiles excluded (T27, 02 T75) · **briefs fixtures + the true-crime fixture fact sheet authored by T0** so preset acceptance runs first, `rubric.md` stays in T1 (T27 as amended by T34) · **the 12-cell unrated per-tier sweep is accepted** (T37).

Still open:

1. **[OPEN] Rater workload and the blocking rule.** At `standard` each rater reads 18 stories of ~1,000 tokens (≈ 1 h per rater, ≈ 4 h across 4). T25's rule that an `n=1` decisive cell blocks the freeze is implemented as `verdict_ready=false` → `DECISION NOT READY` with no freeze, which makes rater availability the story's real schedule risk — confirm no relaxation.
2. **[OPEN] `$NTS_HOME/current_run` may not fit an A/B campaign id** (01 D10's shape is `YYYYMMDD-HHMM-<lang>-<aspect>-<nn>`). The plan accepts the tap writing to `no-run` and relies on `metrics.json` for attribution; confirm that is acceptable trial evidence, or define the campaign id shape with 01.
3. **[OPEN] `prompts/tools/**` and the other non-runtime paths are excluded from the manifest by T27/T75** but no `prompts/tools/` exists and there is **no** helper there (02 T50) — the manifest is regenerated with `sha256sum` or `trial/tools/freeze_manifest.py`. Keep the exclusion so an accidentally created directory stays untracked.
4. **[OPEN] The fixture fact sheet makes `factual_care` scorable in M0, while T25 keeps it out of the D15 comparison.** That is coherent (M0 has no sourced sheet, so the fixture cannot test source fidelity), but if the TL wants `factual_care` to weigh on the model choice, the D4/T25 exclusion must be revisited before T2 freezes.
5. **[OPEN] Relay behaviour is an environment risk for T1 (owner: the relay, not T0/T1 code).** From the phase-D QA run (`.nts/qa-02d/report.json` `environment_findings`, QA-02D-7, PERF-02D-2):
   - **Latency is bimodal** — fast cells 17–58 s, slow cells 102–181 s, with 3×502 at 154–181 s; 8 of 24 paid calls exceeded the 90 s writer timeout. The relay **billed every abandoned and 502 call anyway** (~$0.010 of the $0.0377 run; 12 of 28 `/responses` lines were billed after the client abort, ≈ 40 % of spend). T1 must not start the batch while the relay is in this state and must set `--timeout` from a measured tail (README T91), and the report must name the timeout/attribution differences rather than assert cell-sum == tap-sum (CR-25).
   - **The relay rewrites request fields.** Every `response.json` echoes a relay-side `safety_identifier` and `prompt_cache_key` and `max_output_tokens: null`, although `request.json` sent `sha256("nts-m0-harness")`, `write_story:<preset>` and 2,500/6,000/10,000. So the D4/T66 caps are request-side only: `incomplete_details.reason = max_output_tokens` can never fire live, and cap-cut is unit-tested only (`classify_maps_cap_cut_refusal_and_429_to_d7_outcomes`). T1's REPORT states this.
   - **The relay injects an `instructions` block.** Each response carries an `instructions` field of ~43,728 chars: our ~22,640-char head plus **~21 k chars of agent operating instructions the harness never sent** (PerfRevD). 43,728 chars ≈ the billed `input_tokens` (10,518–11,979), i.e. roughly half of every billed prompt is injected text charged at our rate — billed input ≈ 2.5× our prompt. Consequence: the D4 token bands and the 12.1.1 price bound are computed on *our* prompt, while the bill carries the relay's; the harness's unpriced-cell bound uses the full request-body **bytes** (P6 rule, PERF-02D-2) so the guard is not understated by the injection, and T2's hand-off numbers must record the observed billed input (not the estimate).
   - **`trial/prices.json` is incomplete** — no `gpt-6-sol` row and no `gpt-6-luna` `cache_write` rate (QA-02D-18). `--matrix` exits 2 at the rates pre-flight before any call, and a sol cell cannot be priced. Owner 01.3.1-T1; requested as CR-27, with the official price source still [OPEN].
6. **[OPEN] Tap attribution needs the 01.3.1-T2 change (CR-25).** The tap attributes a `/responses` call to whichever `current_run` holds the lock when the response *completes*, so an abandoned call lands on a later run and any concurrent probe to `:8002` is logged into the held run (QA-02D-8, QA-02D-17). Until the tap resolves the run at request time (or honours `X-NTS-Run-Id`), UC2's "token sums match the tap JSONL" cannot be asserted for a batch containing a timeout; T1 prints the per-run difference. The lock itself is made noclobber-atomic in the harness and requested for `start_run.sh` (SEC-02D-4, CR-26); the `prices.json` sol + luna `cache_write` rows are requested as CR-27 (QA-02D-18).

## 11. Rollout

No deployment, no flag, no migration. M0 execution order: 02.1.4-T1 (`prompts/` skeleton) → 02.1.1-T0 (schema + length table) → **T0** (smoke-testable as soon as one preset exists) → presets 02.1.1-T1/T2/T3 + 02.1.2-T1 + 02.1.5-T1 → **T1** full matrix → **T2** decision + freeze → M0 exit for plan 02.

**Gate 02-M0** (README §7) is met only when: 6 `topic × language` presets pass the harness validator, 36 stories are scored with 2 raters per language (AI passes allowed, 02 T106), `DECISIONS.md` §3 carries the D15 numbers, and `prompts/manifest.json` is frozen at `2026.10.1`.

Rollback: the story is additive. Delete `trial/stories/**` (untracked) or checkout `trial/ab/**`; revert `prompts/manifest.json` and the single `DECISIONS.md` §3 row if the A/B is rerun. Preset files belong to 02.1.x and are never edited here, so a rerun cannot damage the prompts. If both models fail the rubric on a preset, the fallback is **not** a silent model change: the verdict is "presets need work", the `REPORT.md` evidence goes to 02.1.1/02.1.2, and T1 reruns as `r2`.
