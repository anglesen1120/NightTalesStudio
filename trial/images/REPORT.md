# Image trial report (plan 03)

## Contact sheet (03.3.2-T1, 2026-09-28)

- Matrix: 10 keywords × 2 aspects × 4 qualities = 80 cells, 7 templates (`seed/style-templates.v1.json`).
- Tool: `trial/tools/image_contact_sheet.py` via `trial/tools/run-contact-sheet.sh`; relay path DEC-11 `OPENAI_IMAGE_API=responses` (the tool posts to `/v1/responses` itself, so the tap's `via` marker is `null` on every sheet and probe line), host model `gpt-6-luna`, effort `low` (pin), request body `"store": false`.
- Tool flags: `--tracked-dir` (own results CSV + key; a probe or `--keywords` subset run MUST use it — the tracked CSV is append-only and the `--max-usd` guard sums it), `--keywords k01,k08` (matrix-id subset), `--effort` (host-model `reasoning.effort`), `--portrait-hint` (appends `PORTRAIT_HINT`, the 05 option A sentence, between the style text and `NegativeClause.V1`, portrait cells only; the row `note` records `portrait_hint`), `--allow-remote` (required for a non-loopback `--base-url`, else usage error exit 2). Documented in `plans/features/03-image-generation/03.3-measurement/03.3.2-T1-quality-contact-sheet.md` §Design.
- Tap runs: `20260928-1618-en-landscape-90` (40 lines), `20260928-1618-en-portrait-91` (23), `20260928-1718-en-portrait-91` (18, resumed batch) = **81 calls for 80 cells**. The wrapper's run-id suffixes 90/91 are fixed, so the effort probe (`20260928-1743-en-landscape-90` 2 lines, `…-1743-en-portrait-91` 2 lines) is inside the same 90/91 count: the five directories total 85 lines = 81 sheet calls + 4 probe calls (any later subset re-run with the wrapper adds yet another `…-90`/`…-91` directory to that count — count the new directories too). Call 81 is the `k06-portrait auto` call of the interrupted batch: the relay completed and billed it, but the image was dropped; the sheet uses the resumed-batch image.
- Results: `contact-sheet-results.csv` 80 rows, all HTTP 200, every row has `output_tokens`; 0 `moderation_blocked`.
- Blind page: `grep -Eic '\b(low|medium|high|auto)\b' index.html` → 0; 80 images, 20 groups in `contact-sheet-key.json`.

### Raters (owner deviation, 2026-09-28, 03 O7)

Ratings are **AI-rated**, not owner-rated: 2 independent passes on the same blind A-D page (`trial/tools/image_rater.py`, one call per group, images downscaled to 768 px JPEG via macOS `sips` — a second retry at 640 px q60 when a group's body passes 900 kB — and sent with `detail: low`; no key and no quality word in the request, same 1-5 scale and 4 flags, `note` capped at 240 chars). `ai-1` = `gpt-6-luna`, `ai-2` = `gpt-6-sol`, both effort `medium` (tool default; tap `reasoning_effort`), tap run `20260928-1750-en-landscape-93` (40 lines). Per-pass files `contact-sheet-ratings.ai-1.csv` / `.ai-2.csv`; `contact-sheet-ratings.csv` = mean rating, flag = any rater, notes joined.

| Pass | n | mean | 3 | 4 | 5 |
|---|---|---|---|---|---|
| ai-1 (luna) | 80 | 4.31 | 10 | 35 | 35 |
| ai-2 (sol) | 80 | 4.54 | 4 | 29 | 47 |

Agreement: 48/80 exact, 78/80 within 1 point. No rating below 3.

### Per aspect × requested quality

Mean rating = mean of the 2 passes over 10 keywords; flag rate = share of cells with `text_visible`, `face_realistic` or `gore`; tokens = host-model output tokens (not image-output tokens, 01 CR-21); cost = the tool's **fixed O8 rates** (text input $5 / image input $8 / image output $30 per 1M — the `gpt-image-2.5-flare` row in `trial/prices.json`; the relay really bills `gpt-image-2` flat, see §Spend).

| Aspect | Quality | Mean | Flag rate | Out tokens mean | Out tokens max | Cost/image USD |
|---|---|---|---|---|---|---|
| landscape | low | 4.50 | 0.00 | 123.9 | 137 | 0.03745 |
| landscape | medium | 4.50 | 0.00 | 127.9 | 154 | 0.03756 |
| landscape | high | 4.25 | 0.00 | 133.6 | 149 | 0.03782 |
| landscape | auto | 4.45 | 0.00 | 131.2 | 162 | 0.03767 |
| portrait | low | 4.30 | 0.00 | 129.8 | 156 | 0.03766 |
| portrait | medium | 4.40 | 0.00 | 125.9 | 151 | 0.03747 |
| portrait | high | 4.50 | 0.00 | 126.9 | 147 | 0.03752 |
| portrait | auto | 4.50 | 0.00 | 132.6 | 155 | 0.03770 |

### Findings

- ⚠️ **The relay ignores the requested quality.** Echoed quality is `low` for 57 cells and `medium` for 23, regardless of the request; no cell echoed `high`. The `auto` cells echo the same mix. The requested-quality axis above is therefore noise, not a measurement. By echoed quality: landscape low 4.38 (n 30) / medium 4.55 (n 10); portrait low 4.31 (n 27) / medium 4.65 (n 13).
- ⚠️ **Effort does not change it.** Probe 2026-09-28: `image_contact_sheet.py --qualities high --effort high --keywords k01,k08 --tracked-dir .nts/runs/contact-sheet-effort-high/tracked` (4 calls, tap dirs `20260928-1743-en-landscape-90` / `-en-portrait-91`). It had to pass its own `--tracked-dir`: the matrix key holds only `k01`/`k08`, and a subset run appends to the append-only results CSV that also feeds the `--max-usd` guard. The tap recorded `reasoning_effort: high`; echoed quality was 1 × `medium`, 3 × `low`.
- ⚠️ **The relay ignores the aspect.** All 40 portrait cells came back landscape (1278×1230 … 1774×887; mostly 1536×1024). 9:16 through this relay needs a crop from landscape (~576×1024 → upscale to 1080×1920, softer image). **[DECISION 2026-09-28, owner — plan 05 O9, 03 CR-16] 9:16 uses option A:** MPT `video_fit_mode` stays `cover` (a 1536×1024 image on a 1080×1920 canvas keeps ≈ 37 % of its width) and a portrait-only prompt hint is appended between the style text and `NegativeClause.V1` ("Main subject centred in the middle third of the frame, empty margins on the left and right.", `PORTRAIT_HINT` / `--portrait-hint`). B (cover, no hint), C (`contain`, black bars under the 62 % portrait subtitle line) and D (blurred background, needs an MPT patch) were rejected. The hint's effect on the 9:16 crop is not yet rated — see §Open.
- **Negative clause held:** 0 `text_visible`, 0 `face_realistic`, 0 `gore`, 0 `off_prompt` in either pass.
- **Templates:** watercolor-night 4.72, noir-sketch 4.66, temple-candlelight 4.62, vintage-photo-grain 4.44, dark-folklore-ink 4.38, foggy-cinematic 4.38, **case-file-illustration 3.31**. Both raters say the case-file images come out realistic with warm, dramatic lamp light instead of flat, evenly lit editorial shapes. **Revised 2026-09-28 (owner-approved):** the template is now `Flat 2D editorial illustration of {term}, printed like a page in an old police case file. Muted grey-beige palette, cool neutral light, no warm glow, no lamps lit, …` — `style=case-file-illustration@74c968c5` → `@42ca2380` (checker `7 templates ok`; old → new text in `seed/style-templates.md` §6, 03 CR-16). The 3.31 above is the score of the **old** text: its 8 `k09` cells were rendered from it. The re-run on the new text scores **3.88** (§Re-run).

### Decision rule (03.3.2-T1 §Decision rule)

Applied literally, `low` qualifies on both aspects (landscape 4.50, portrait 4.30; ≥ 4.0, within 0.5 of the max, flag rate 0). The input is not valid for that choice: the relay picks `low`/`medium` itself, so no requested quality is reachable. [DECISION] for 03.3.1-T2: keep O3 = unset (`auto`) on this relay; revisit CR-3 only on an upstream that honours `quality`.

### Spend

- `--max-usd` accounting (host-model tokens at `trial/prices.json`): **$3.01 / $8** for the 80 sheet cells.
- Relay billing (184 VND/image): 81 sheet calls + 4 effort-probe calls = 85 × 184 VND ≈ 15,640 VND ≈ **$0.61**.
- Rater passes, recomputed from `trial/runs/20260928-1750-en-landscape-93/openai_usage.jsonl` (20 calls per rater) at the `trial/prices.json` **D14** rates: luna 114,099 input (30,720 cached) + 5,106 output → **$0.0112**; sol 114,099 input (26,880 cached) + 6,867 output → **$0.2485**. Rates used: input/output luna $0.10 / $0.50, sol $2.00 / $10.00, cached input luna $0.01, sol $0.20 per 1M (charging all input at the uncached rate would give $0.0140 / $0.2969). Total ≈ **$0.26**.

### Re-run (2026-09-28, new case-file text + portrait hint)

- Command: `trial/tools/run-contact-sheet.sh --mode sheet --keywords k09,k05,k10 --portrait-hint --out .nts/runs/contact-sheet-casefile-20260928 --tracked-dir .nts/runs/contact-sheet-casefile-20260928/tracked --max-usd 2` — k09 = `case-file-illustration`; k05 (`watercolor-night`) and k10 (`noir-sketch`) = portrait-hint probe. 24 calls, 0 failed, 12 rows with `note = portrait_hint`. Tracked copies: `trial/images/contact-sheet-rerun-20260928-{results,ratings,ratings.ai-1,ratings.ai-2}.csv`, `-key.json`.
- Tap dirs: `trial/runs/20260928-1903-en-landscape-90` / `-en-portrait-91` hold **this re-run's 24 calls** (12 + 12), not the original 81; the rater passes are `20260928-1924-en-landscape-94` (12) and `20260928-1926-en-landscape-94` (1 ai-2 retry after one group returned bad letters) — the rater stamp says `landscape` but covers both aspects. `msg_*`/`rs_*` ids sanitised to `item_n` as for the first run.
- Relay echo unchanged: quality 17 `low` / 7 `medium`, all images landscape (1370×1148 … 1942×809).
- Ratings (mean of ai-1 luna 4.00 and ai-2 sol 4.50 pass means; 0 flags):

| Cell | Old sheet | Re-run |
|---|---|---|
| k09-landscape (case-file) | 3.12 | **4.12** |
| k09-portrait (case-file, + hint) | 3.50 | 3.62 |
| k05-landscape | 4.50 | 4.25 |
| k05-portrait (+ hint) | 4.88 | 4.25 |
| k10-landscape | 4.62 | 4.75 |
| k10-portrait (+ hint) | 4.62 | 4.50 |

- **Case-file:** template mean 3.31 → **3.88**. Raters now report unlit lamps and flat ink outlines in most images; 3 of 8 still show a lit lamp with warm glow (k09 is "... reading glasses on a desk, **lamp light**" — the keyword itself asks for the lamp). Portrait stays below 4.0. **Owner decision 2026-09-28:** accept 3.88 and keep the k09 keyword ("lamp light" stays as the worst-case probe: production keywords come from the Writer, so the lit-lamp case is real input, not a fixture bug).
- **Portrait hint:** no gain in AI scores (k05 −0.63, k10 −0.12; within the ±1 rater spread seen in the first run). The raters score the full landscape frame, not the 9:16 cover crop, so this does not measure the hint's purpose; the crop probe below does.
- Spend: relay 24 × 184 VND ≈ 4,416 VND ≈ **$0.17**; tool accounting $0.92; rater passes at D14 rates luna **$0.0027**, sol **$0.0809**.

### Crop probe (2026-09-28, 9:16 cover crop, portrait hint)

- Question: does the portrait hint keep the subject inside MPT's 9:16 `cover` crop (05 O9 option A)? Offline, no new images: `trial/tools/make_crop_groups.py` builds one blind group per cell from the `auto` image of the first sheet (no hint) and of the re-run (hint), crop = scale to fill 1080×1920 then centre-crop (as MPT `cover`). A = no-hint crop, B = hint crop, C = no-hint full frame, D = hint full frame. Rated by the same 2 AI raters (`image_rater.py`); CSVs `trial/images/contact-sheet-crop-20260928-ratings{,.ai-1,.ai-2}.csv`; tap run `trial/runs/20260928-1930-en-landscape-96` (4 calls, ids sanitised).

| Cell | A no-hint crop | B hint crop | C no-hint full | D hint full |
|---|---|---|---|---|
| k05-portrait (watercolor-night) | 4.0 | 4.5 | 4.5 | 4.0 |
| k10-portrait (noir-sketch) | 4.0 | **3.5** | **5.0** | 4.0 |
| Mean | 4.00 | 4.00 | 4.75 | 4.00 |

- **Hint vs no hint inside the crop: 0.00** (B − A). The raters disagree in sign (ai-1 +0.5, ai-2 −0.5), so the probe cannot separate the hint from rater noise.
- **Confound:** the relay ignores `size`, so the two sides came back with different frames (k05 1370×1148 vs 1774×887; k10 1374×1145 vs 1536×1024). The crop keeps 47 % of the no-hint width but only 28 % / 37.5 % of the hint width, so "hint" is mixed with "harder crop".
- **What both raters agree on:** the crop itself costs the no-hint k10 frame 1 point (C 5.0 → A 4.0, notes: clipped chair), and the k10 hint crop is the worst variant (3.5; both raters: both chairs clipped). 0 `off_prompt` flags in all 8 images, so the subject never fully leaves the frame.
- **Result:** no evidence that the hint helps; on the geometry-heavy cell the evidence points the other way. Option A stays as decided (cover + hint, owner 2026-09-28) — the hint costs nothing and does not break the style — but its benefit is **unproven**. [OPEN] 05.5.2: judge the cropped frames of the first real portrait render; a controlled retest needs the same frame aspect on both sides and more than 2 cells.
- Spend: 4 rater calls (luna 2, sol 2); no image calls.

### Open

- [DONE 2026-09-28] Manual spot check, 5 images vs the flags (03.3.2-T1 Tests) — owner, 2026-09-28: `k02-landscape-B`, `k05-portrait-C`, `k06-landscape-A`, `k09-landscape-A`, `k10-portrait-D` **OK, no issue** found against the AI flags (03 README §10 O7).
- [DONE 2026-09-28] Aspect: 9:16 = option A (cover unchanged + portrait hint) — plan 05 O9 / 03 CR-16.
- [DONE 2026-09-28] case-file-illustration text: revised and owner-approved (`@74c968c5` → `@42ca2380`, `seed/style-templates.md` §6).
- [DONE 2026-09-28] Re-run of the `case-file-illustration` cells on the new text plus the portrait-hint probe, 2 AI raters (§Re-run). Case-file result accepted by the owner 2026-09-28 (3.88; k09 keeps "lamp light" as the worst-case probe). Portrait 3.62 stays below 4.0 — accepted as the known weak spot, recorded here.
- [OPEN] 01.3.1-T2 tap defect: `usage_tap.py`'s `numeric_leaves` keeps upstream `msg_*`/`rs_*` ids as keys under `usage.attribution.items` (found 2026-09-28; the six `trial/runs/20260928-1*-9[0-3]/openai_usage.jsonl` files were sanitised to `item_1..n` before commit; the 16 tracked `/responses` files under `trial/runs/20260927-*` still carry the raw ids). Recorded in STATUS.md §Findings during implementation.
