# `trial/` — local trial tooling

Local, non-product tools used to calibrate plan 01 and the plan-02 M0 spike. Nothing here ships; nothing here is imported by product code.

- `trial/schema.md` — the 01 run-manifest schema (`run.json`, `measurements.csv`, `stages.json`).
- `trial/tools/` — the 01 measurement helpers and the M0 story harness.
- `trial/prices.json` — the D14 rate table the harness prices calls with (owner 01).
- `trial/ab/` — blind kits, scores and reports (tracked); `trial/stories/` — raw harness cells (gitignored, regenerable).

## Model A/B harness

`trial/tools/story_harness.py` (02.1.3-T0) turns one brief into one schema-valid `story.v1` story per call. It is the M0 A/B's measuring instrument: it uses our real presets and the real `prompts/schemas/story.v1.json`, so the run measures our prompts and our schema instead of MPT's output format (F01). It calls the OpenAI Responses API **only** through the 01 usage tap (`http://127.0.0.1:8002/v1`, 01 D6); the tap is pinned to `127.0.0.1`, so an HTTP proxy cannot carry the bearer key, and redirects are not followed, so the key is never re-sent to a `Location` host.

Run it with the 01 interpreter (stdlib only):

```
uv run --no-project --python 3.12 trial/tools/story_harness.py --help
uv run --no-project --python 3.12 trial/tools/story_harness.py \
  --preset ghost --lang vi --brief-id b1 --tier short --dry-run
```

The two-file matrix (`--briefs-file prompts/fixtures/briefs.vi.json --briefs-file prompts/fixtures/briefs.en.json`, no `--lang`) builds 36 cells — 9 briefs × 2 languages × 2 models — as distinct `vi-…`/`en-…` ids; each cell takes the language (and preset) of the brief it was built from, not a batch-wide flag. `--lang` restricts a batch to one language instead.

### Flags

| Flag | Meaning |
|---|---|
| `--preset ghost\|spiritual\|true_crime` | Topic preset for a single run (required unless `--matrix`) |
| `--brief "text"` \| `--brief-id b1` \| `--brief-ids b1,b4,b7` \| `--briefs-file <json>` | Brief selector; `--briefs-file` is repeatable and defaults to the selected language's file (`prompts/fixtures/briefs.<lang>.json`, i.e. `briefs.vi.json` when `--lang` is absent). Two files that repeat the same ids (`briefs.vi.json` + `briefs.en.json`) yield one cell per file — `vi-…` and `en-…` — each assembled from its own language's preset and shared blocks. With `--brief-id`/`--brief-ids` an id resolves per file and language (`b1` selects the `b1` of every file given), never last-file-wins |
| `--lang vi\|en` | Optional language **filter**. A cell's language comes from its brief's own `language` field (and names the `vi-…`/`en-…` run id), never from this flag; with `--lang` given, only briefs of that language run and a brief of the other language is skipped with a note — exit 2 when no cell remains |
| `--model gpt-6-luna\|gpt-6-sol` | Default `gpt-6-luna`; `--matrix` enumerates both unless `--model` restricts it |
| `--tier short\|standard\|long` | Length tier (default `short`) |
| `--out <dir>` | Output root (default `trial/stories`) |
| `--run-id <id>` | Override the derived cell id; rejected with exit 2 when the invocation builds more than one cell |
| `--dry-run` | Assemble and write `request.json`; make no call and never write the run lock (it only reads the current id into `tap_run_id`) |
| `--matrix` | All presets × briefs × models from the briefs file(s) |
| `--only-missing` | Skip finished cells (default for `--matrix`): a cell is finished when its `metrics.json` parses and records `ok`, or a failure and `--retry-failed` was not passed |
| `--retry-failed` | With `--only-missing`/`--matrix`, re-run cells whose `metrics.json` records a non-`ok` outcome (the earlier dir is kept; the retry lands in `<run_id>-r2`) |
| `--force` | Overwrite an existing run dir |
| `--timeout <sec>` | Client timeout for one call (default **240**; the D7 product writer timeout is 90 s) |
| `--max-usd <x>` | Hard stop before the next call once `spent` would exceed `x` (default 5.0) |
| `--sleep-ms <ms>` | Pause between calls (default 200) |
| `--key-service <name>` | Keychain service (default `nts.openai.story`) |
| `--tap-base-url <url>` | Must be `http://127.0.0.1:<port>/v1` (default the tap) |
| `--prompts-dir <dir>` | Prompts folder (default `prompts/`) |
| `--length <json>` | D4 length table (default `trial/tools/LENGTH.v1.json`) |
| `--prices <json>` | D14 rates (default `trial/prices.json`); a model row with `input`/`cached_input`/`output` is required before a call, plus `cache_write` whenever the response reports it |
| `--facts <json>` | With `--validate-only`: the fact sheet whose ids the story may cite (default: the brief's own facts from `--brief-id`) |
| `--validate-only <story.json>` | Run the validator on a story and exit |

### Exit codes (01 D9)

- `0` — ok.
- `1` — a check failed: validator issues on `--validate-only`, or the budget was exceeded.
- `2` — environment or usage error, with a one-line message and no traceback: missing Keychain item, tap not listening, missing preset/schema, unknown `{{placeholder}}`, unknown `--model`, a prompt file whose sha256 differs from `prompts/manifest.json`, an unsafe `--run-id`/brief id (must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` and stay under `--out`), `--run-id` with more than one cell, or `current_run` held by another run.

### The six-file contract

One cell writes `trial/stories/<run_id>/`:

| File | Content |
|---|---|
| `request.json` | The assembled request body, plus `run_id`, `tier`, `language`, `preset`, `prompt_version`, `prompt_files` (the sha256 of each loaded prompt file), `tap_run_id`. **No** `Authorization` and no key. |
| `response.json` | The raw provider response body (whatever the outcome). |
| `story.json` | The parsed `story.v1` object, verbatim. |
| `video_script.txt` | The D3 `body` derivation (MPT `video_script`). |
| `video_terms.json` | `hook` ++ paragraph ++ `ending` keywords, in order (MPT `video_terms`). |
| `metrics.json` | Outcome, `json_valid`, `http_status`, latency, per-attempt `attempts[]`, `usage` (`input_tokens`, `cached_tokens`, `cache_write_tokens`, `output_tokens`, `reasoning_tokens`), `tokens`, `cost_usd`, `validator_issues`. |

A **failed** cell (`provider_unavailable`, `provider_error`, `moderated`, `invalid_output`) writes **three** files — `request.json`, `response.json`, `metrics.json` — so the attempt is still evidence. `metrics.json` is written atomically (temp file + `os.replace`), so a torn file never looks like a finished cell.

### Run lock (`current_run`)

The tap attributes a call only while `$NTS_HOME/current_run` holds a matching id (`$NTS_HOME` defaults to `<repo>/.nts`). A live run takes that lock for the whole batch:

- it refuses to start when the file holds an id it did not write (exit 2, naming the holder) — a live MPT render keeps its attribution;
- the first acquisition is atomic (`os.link`), so a lock that appears between the read and the write is never clobbered;
- the lock is released in `finally`, and only while it still holds this process's ids;
- `--dry-run` never writes it (it only reads the current id for `tap_run_id`).

Until the tap resolves the run at request time (01.3.1-T2, 02 CR-25), a call that the client abandons can be logged against a later run, so do not assert cell-sum == tap-sum for a batch that contains a timeout.

### Cost

- Every **attempt** of a cell is charged: usage when the response carried it, otherwise a conservative bound built from the UTF-8 bytes of the request body (P6: input tokens ≤ UTF-8 bytes) — never `$0`.
- A transport timeout or HTTP status 0 is recorded once as `provider_unavailable` and is **not** retried: the upstream call is still in flight and is billed.
- `--max-usd` is a hard stop with no paging; the batch aborts before the next call, prints `spent=<x> cap=<y>` and exits 1, and resumes with `--only-missing`.
- Prices come from `trial/prices.json`; `--matrix` exits 2 before any call when the model has no rates row (a missing `gpt-6-sol` row blocks the batch).

The summary line printed after a batch:

```
summary: cells=N ok=N invalid=N moderated=N provider_error=N provider_unavailable=N \
  funds_exhausted=N tokens_in=N tokens_out=N cached=N reasoning=N p50_ms=N spend_usd=N estimated=false
```
