# 03.3 — Cost and quality measurement (M0)

> Refines: `../../../03-image-generation.md` §4 epic 03.3 (stories **03.3.1**, **03.3.2**), G1, A1, G6, §5.3, §10 CR-3/CR-4
> Contract: `../README.md` — **D3** (clip 10 s, K = 6), **D8** (metering, `price_versions.rates.image`), **D9** (quality via relay), **D13** (M0 path), **F05**, **F19**, **CR-4**, **CR-12**; `../../01-environment-setup/README.md` **D3**, **D6**, **D7**, **D8**, **D10**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.**
- 03.3.1 *Measure real image tokens* — "As the Tech Lead I want actual tokens per image per aspect so that credit prices are correct." Must · S (detailed 0.75 d incl. the decision task).
- 03.3.2 *Quality comparison* — "As the owner I want to see low/medium/high side by side so that I choose the cheapest acceptable quality." Should · S (detailed 0.5 d).

**In scope.**
- A one-off stdlib tool that renders a blind contact sheet (10 keywords × 4 qualities × 2 aspects) and records tokens per call; the same tool makes the style previews (`--mode previews`, 03.1.2-T1) (03.3.2-T1).
- Six trial renders (3 per aspect) on the Mac through the usage tap, with MPT's `auto` quality and one confirmation render per aspect with the chosen quality injected by the tap; a per-image token report (03.3.1-T1).
- The recorded decisions: quality (CR-3 → D9 value), production metering (CR-4 → D8), measured `tokens_per_image` per `aspect:quality` seeded into `price_versions.rates.image`, and the DECISIONS §4 update (03.3.1-T2).

**Out of scope.**
- The relay itself (03.4.3), GPU cost (11.6.3), credit prices (12.1.1 consumes these numbers).
- Style evaluation beyond "does the template work at this quality" (the owner rates each cell once; no A/B of styles).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| Q1 | Quality `auto` applies because MPT sends none (I1, O6); the tap records `request.quality` (sent) and `response.quality` (echoed) per call, and can inject with `TAP_IMAGE_QUALITY` | README §1 I1, O6, P1 ✅ |
| Q2 | Rates: text in $5, image in $8, image out $30 per 1M; Tier 1 = 5 images/min | O8 ✅ |
| Q3 | Sheet schema v2 columns: `image_count`, `moderation_blocked_count`, `image_http_429_count`, `image_failed_count`, `image_tokens_per_image_avg`, `image_cost_per_image_usd`; one aspect per run | 01.3.1-T1 ✅ (P2) |
| Q4 | Run ids `YYYYMMDD-HHMM-<vi|en>-<landscape|portrait>-<nn>`; the tap takes the run id from `$NTS_HOME/current_run` | 01 README D10, D6 ✅ |
| Q5 | Image project `nts-trial-image`: allow-list `gpt-image-2` (`gpt-image-2.5-flare` superseded, 01 DEC-11), hard limit **$25**; key in Keychain `nts.openai.image` | 01 README D7, D3 ✅ |
| Q6 | DECISIONS §4 floor prices at 1024²: low ≈ $0.006, medium ≈ $0.013, high ≈ $0.053 → ≈ 200 / 433 / 1,767 output tokens [INFERENCE from $30/1M] | DECISIONS §4 ✅ / [INFERENCE] |
| Q7 | 05.5.2 renders one 5-min vi and one 5-min en ghost video (landscape) | 05 §4 05.5.2 ✅ |

**Closed here.** How quality is compared (blind contact sheet + rule, 03.3.2-T1). Which renders give the per-aspect numbers (03.3.1-T1). Where the decision is written (03.3.1-T2).

## 3. Domain and data

No production table. Tracked artefacts (all under `trial/images/`):

| File | Written by | Content |
|---|---|---|
| `contact-sheet-results.csv` | 03.3.2-T1 tool | one row per call: `cell_id, keyword_id, template, aspect, quality_requested, quality_echoed, size, http_status, error_code, text_tokens, image_tokens, output_tokens, cost_usd, latency_ms, size_actual, note` (16 columns; `size_actual` = the measured pixel size of the saved image, `note` = `;`-joined markers such as `portrait_hint`, `usage_absent`, `format_<fmt>`) |
| `contact-sheet-ratings.csv` | owner | `cell_id, letter, rating_1_5, text_visible, face_realistic, gore, off_prompt, note` |
| `contact-sheet-key.json` | tool | letter ↔ quality per keyword (opened only after rating) |
| `tokens-per-image.csv` | 03.3.1-T1 report script | `aspect, quality_echoed, source (render|contact), n, mean, p50, p95, max, cost_mean_usd` |
| `REPORT.md` | 03.3.1-T1 / T2 | runs, numbers, decision, spend |

Images themselves stay in `$NTS_HOME/runs/…` (untracked, 01 D1; `.gitignore` covers `*.png *.webp`).

## 4. Use cases

None (M0 tooling). Consumers of the outputs:
- 03.3.1-T2 → `price_versions.rates.image` seed (D8), `ImageRelay:Quality` / `Images:Quality` default (D9), DECISIONS §4 (CR-12).
- 12.1.1 cost per tier; 02 D1 budget defaults re-checked with the real image cost.

## 5. API and events

None.

## 6. UI

None. The contact sheet is a local static HTML page (`index.html`) opened from `$NTS_HOME`.

## 7. Jobs and integrations

| Call | Path | Guard |
|---|---|---|
| Contact sheet (80 images) | tool → tap `127.0.0.1:8002` → OpenAI Images | `--max-usd 8` hard stop (computed from each response's `usage`, Q2 rates) |
| Previews (7–21 images) | same tool, `--mode previews` | `--max-usd 2` |
| Trial renders (≈ 96 images) | MPT → tap → OpenAI | run plan in 03.3.1-T1; stop when the tap total for the plan exceeds $12 |

Budget table (worst case with every image at `high`, [INFERENCE] from Q6 × 1.5 for 1536×1024 ≈ $0.08/image): contact sheet ≈ $4, previews ≈ $1, renders ≈ $8, plus 01's wiring render ≤ $4.5 → ≤ $17.5 < $25 hard limit (Q5). If `auto` turns out above `high` pricing, the owner raises the image project limit to $40 before 03.3.1-T1 (O-M1 below).

## 8. Security and privacy

| Threat | Control |
|---|---|
| Key exposure in the tool | the wrapper reads `nts.openai.image` from the Keychain into the child environment only (01 D3); the tool never prints or logs it; argv never contains it |
| Content in tracked files | keywords are our own fixtures (02.1.2-T1 examples); no user content exists in M0 |
| Spend runaway | per-tool `--max-usd`, the project hard limit (01 D7), no retries in the tool |

## 9. Test plan

- **Tool self-tests** (`--self-test` against the 01 tap stub or a local stub): cost computation from a canned `usage`; `--max-usd` stop; blind letter shuffle is a permutation per keyword and `key.json` round-trips; `index.html` has no quality words.
- **Report script tests:** p95 on a fixture JSONL; `quality_echoed` null → bucket `unknown`; `usage` absent → excluded from token stats but counted in `n_no_usage`.
- **Manual:** owner rating session (≈ 30 min); 6 renders watched once.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 03.3.2-T1 | Quality contact sheet + per-quality tokens (+ `--mode previews`) | Worker | **S (0.5)** | 03.1.1-T1, 01.3.1-T2 (tap), 01.2.4-T2 (key) | 03.3.1-T2, 03.1.2-T1 |
| 03.3.1-T1 | Trial renders token measurement (3 per aspect) + token report | Worker | **S (0.5)** | 03.4.1-T1, 05.5.2, 01.3.1-T3 (collector) | 03.3.1-T2 |
| 03.3.1-T2 | CR-3/CR-4 decision + `price_versions` image seed + DECISIONS §4 | TL | **XS (0.25)** | 03.3.2-T1, 03.3.1-T1 | 03.4.2-T1, 03.4.3-T1 (quality value), 12.1.1 |

Total **1.25 d** (high-level 1.0 d; README §9: +0.25 d for the decision/seed task).

### Open items for the owner

- [OPEN] **O-M1** Raise `nts-trial-image` hard limit $25 → $40 if the contact sheet shows `auto` ≥ `high` token counts (decided after 03.3.2-T1, before 03.3.1-T1).
- [OPEN] Owner time: ≈ 30 min rating + ≈ 15 min watching the 6 renders.

## 11. Rollout

- **Order:** 03.3.2-T1 → (03.4.1-T1, 05.5.2) → 03.3.1-T1 → 03.3.1-T2.
- **Flag:** none; `TAP_IMAGE_QUALITY` is set only for the two confirmation renders and unset afterwards (the tap's `tap.json` shows the value).
- **Monitoring:** tap JSONL totals per run; `REPORT.md` spend line.
- **Rollback:** none needed (read-only measurements); a bad decision is changed by a new `price_versions` row and a DECISIONS entry.
