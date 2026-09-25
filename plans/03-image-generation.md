# 03 — Image Generation (M0)

> Contracts: `00-overview.md`. Facts: `../DECISIONS.md` §4, §5.2. Images are generated **inside MPT** (`video_source = "openai_image"` ✅); the backend only chooses style, keywords and measures cost.
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-first, `00-overview.md` §4.1): all P. Where this plan says M1 for an S item, read M2.
> **Detailed plans:** `features/03-image-generation/` (reviewed 2026-09-24; owner decisions O1–O6 accepted). Its README D1–D14 supersede this file where they differ; the changes below are tagged `(03 CR-10)`.

## 1. Purpose and scope

**Goal:** consistent, on-brand, policy-safe scene images at a known cost per video.

**In scope:** style templates per series, keyword rules, MPT image config, moderation failure handling, cost measurement, spike on how MPT reports image failures, and the **image relay** inside `mpt-adapter` (new story 03.4.3: key, quality, pacing, per-task metering) (03 CR-10).

**Out of scope:** consistent recurring characters via the Edits endpoint (MPT has no Edits path — DECISIONS §5.2), thumbnails (Could, later), local image models.

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| G1 | Cost table in DECISIONS is a **floor** (1024×1024); MPT sends 1536×1024 / 1024×1536 by default ✅ | Measure real `usage` per aspect (03.3.1) before pricing (12) |
| G2 | Unknown how MPT surfaces `moderation_blocked` through its task API | Spike 03.4.1 before designing UI messages |
| G3 | One image per keyword ✅ → keyword count drives cost | Cap keywords per minute of video (§5.3) |
| G4 | True crime: realistic images of real people are a legal and policy risk | Style templates force illustration/silhouette/scenery; keyword linter blocks names (03.2.2) |
| G5 | Scenes can look inconsistent | One template per series; describe place/time/light the same way |
| A1 | `medium` quality is good enough | ⚠️ Compare low/medium/high side-by-side in M0 (03.3.2) |
| G6 | MPT cannot choose image quality: its `openai_image` body is only `{model, prompt, n: 1, size}` (no `quality`, no `moderation`) ✅ (01 README §1 M4, MPT `material.py` @ `3d5f4e42`), so the API default `quality = auto` applies ✅ (O6). The DECISIONS §4 price "medium ~$0.013" is therefore not what M0 pays | [OPEN] CR-3 decision in §10 before 03.3.1 numbers feed pricing (12) |
| G8 | MPT skips a blocked or failed image, uses the next term, loops clips and reports success; image count = `ceil(audio ÷ video_clip_duration)` (default clip 5 s); clip order is shuffled by default (`video_concat_mode = random`) ✅ (feature README I4–I13) | Relay report `images.v1` + outcome table D7; clip 10 s; sequential order (03 CR-10) |
| G7 | Tier 1 allows **5 images/min** for `gpt-image-2.5-flare` ✅ (O8 — superseded by `gpt-image-2`, 01 DEC-11; rate re-check [OPEN]); MPT retries a 429 only 3× (waits 5 s and 15 s, ≈ 20 s in total; 03 CR-8) and then **skips the keyword silently** ✅ (M4) → a video can render with missing images and still "succeed" | Count skipped keywords per run (01.3.1 `image_failed_count`, `image_http_429_count`); a run with any skipped keyword is invalid; pacing by the relay token bucket (D5) and a tier increase request before M1 (owner O4) (01 F24, 03 CR-10) |

## 3. Requirements

- **FR-03-1** Every series references one style template (`series.style_template_id` → `style_templates`, 00 §6) whose text contains `{term}` and **no** negative clause; the **backend** `ScenePromptBuilder` expands it and appends the versioned platform `NegativeClause` to each `video_terms` entry (feature D1). MPT's global `openai_image_prompt_template` is set to `""` on the node (03 CR-10).
- **FR-03-2** The backend sends per-job `video_source = "openai_image"` ✅ (`schema.py:118`), `video_terms` from the approved story as a JSON **array**, `video_clip_duration = 10`, `video_concat_mode = "sequential"` and `match_materials_to_script = true` (feature D4, 03 CR-10).
- **FR-03-3** Keywords are linted: no personal names, no gore words, English, 3–12 words each.
- **FR-03-4** Image cost per job is recorded in `usage_records` (`operation = image`), one row per billed image from the relay report, on every terminal render state (feature D8, 03 CR-10).
- **FR-03-5** Superseded by feature D7 (03 CR-10): a video with moderated scenes is kept and sent to review with the scenes listed when ≤ 10 % of the needed scenes are missing; above 10 % the render fails with `image_moderated` (all skips moderated, never auto-retried) or `image_generation_failed` (≤ 2 retries). Scenes are named from the relay report.
- **NFR-03-1** Image spend per job never exceeds the reserved worst case: at most `MaxTerms` images per render (relay cap), checked by `IBudgetGuard` before dispatch (feature D3, D8, 03 CR-10).

## 4. Epics and user stories

### Epic 03.1 — Style system

**03.1.1 Style templates** — *As a creator I want to pick a visual style so that my channel looks consistent.* Must · M
- Given the style library, When I create a series, Then I can choose one of the 7 templates of catalogue v1 (feature D2; owner O5): "Dark folklore ink", "Foggy cinematic", "Vintage photo grain", "Watercolor night", "Temple candlelight", "Noir sketch", "Case-file illustration"; a `true_crime` series may only pick the two `true_crime` templates (03 CR-10).
- Given a template, When a series is saved, Then `series.style_template_id` points to a `style_templates` row whose `template` text contains `{term}`; the negative clause ("no gore, no text, no logos, no real person likeness") is appended by the builder, not stored in the template (03 CR-10).

**03.1.2 Style preview** — *As a creator I want a sample image per style so that I choose visually.* Should · S
- Given the style picker, When shown, Then each style has a static preview image produced once by the operator (not generated per user).

### Epic 03.2 — Keyword quality

**03.2.1 Keyword cap** — *As the platform I want a cap on keywords so that cost stays predictable.* Must · S
- Given a story with N keywords and target duration D minutes, When submitting a render, Then `video_clip_duration = 60 ÷ K` (K = 6 → 10 s) and the terms are capped at `MaxTerms` = needed images + 15 % spares (never above the tier maximum), trimming second keywords from the end first; no merging (feature D3, 03 CR-10).

**03.2.2 Keyword linter** — *As the platform I want risky keywords blocked so that fewer images are moderated and no real person is depicted.* Must · M
- Given keywords, When linted, Then names (capitalised person-name heuristics + names found in `fact_sources` titles), gore terms and non-English text are flagged; the user must edit flagged keywords before render. 02 CR-5: the base rules (English, 3–12 words, person-name heuristic, gore list) are Domain `Stories/Keywords/KeywordRules`, built by 02.2.1-T1 and already applied when the story is written; 03.2.2 reuses them and adds only the check against `fact_sources` titles at render.

### Epic 03.3 — Cost and quality measurement (M0)

**03.3.1 Measure real image tokens** — *As the Tech Lead I want actual tokens per image per aspect so that credit prices are correct.* Must · S
- Given 3 trial renders (landscape and portrait), When they finish, Then average `output_tokens` per image and cost per image are written to the measurement sheet (01) for each aspect/quality.
- MPT does not log `usage` ✅ (01 M4/M5). M0 reads it from the local usage tap (01 D6, `trial/runs/<run_id>/openai_usage.jsonl`), which records `usage` and the `quality` the API echoes per call; the dashboard is only a daily cross-check (01.3.1-T4).

**03.3.2 Quality comparison** — *As the owner I want to see low/medium/high side by side so that I choose the cheapest acceptable quality.* Should · S
- Given 10 keywords, When generated at low/medium/high (outside MPT, one-off script), Then a contact sheet is reviewed and the chosen default quality is recorded in DECISIONS.md.
- MPT does not pass `quality` at all ✅ (G6). The contact sheet compares low/medium/high **and** `auto` (what MPT gets today), so the CR-3 decision (§10) has evidence.

### Epic 03.4 — Failures

**03.4.3 Image relay (new, 03 CR-10)** — *As the platform I want every image call measured, paced and attributable to a render so that cost, rate limits and blocked scenes are visible without forking MPT.* Must · M
- Given MPT's `openai_image_base_url` points at the relay inside `mpt-adapter`, When MPT generates an image, Then the relay adds the node key and the configured `quality`, paces to `ImagesPerMinute`, refuses prompts not planned by the backend, and records status, `error.code` and `usage` per call; the adapter status returns `images.v1` for terminal tasks (feature D5, D6).

**03.4.1 Spike: MPT image failure reporting** — *As the Tech Lead I want to know what the task API returns when one image is blocked so that the backend can react correctly.* Must · S
- Given a keyword likely to be blocked, When a render runs, Then we record: does the whole task fail or skip the image; task state and error text; whether the failing keyword is identifiable (MPT logs `openai image generation failed: term=…` ✅ M4); a 429 burst at Tier 1 (G7). `quality`/`moderation` cannot be set through MPT ✅ (G6). Output: `worker-node/contract/images.md` + samples, handed to 05.4.1 (03.4.1-T1). Done with a scripted image stub; no real policy-violating prompt is sent (feature D14, 03 CR-10).

**03.4.2 Moderation handling** — *As a creator I want a clear message when an image is blocked so that I can fix it.* Must · S
- Given MPT reports an image moderation failure, When the backend polls, Then the backend applies the feature D7 table to the relay's `images.v1` report: moderated scenes get `moderation_events` rows (excerpt = keyword), the video is kept or the render fails as D7 says, credits are released on failure, and the UI highlights the scene with guidance (03 CR-10). No automatic retry with the same keywords ✅ (DECISIONS §5.2).

## 5. Technical design

### 5.1 Config (MPT, per worker — ✅ keys)

`video_source = "openai_image"`, `openai_image_base_url` = the trial tap / node relay `/v1` (01 DEC-9 — not `api.openai.com`), `openai_image_api_keys = [...]`, `openai_image_model = "gpt-image-2"` (01 DEC-11; advisory on the relay, which serves `gpt-image-2-codex`), `openai_image_size = ""`, `openai_image_prompt_template` = **default** template (the per-series template is applied by the backend, see 5.2).

`openai_image_api_keys` and `openai_image_model` are **node-managed** deploy config [DECISION]: the key comes from a separate OpenAI project limited to image models with its own hard budget (not the backend's text project), is not editable in the admin settings (listed there read-only as "node-managed, rotate via runbook"), and is rotated through the "suspected key leak" runbook one node at a time (11.3.2).

### 5.2 Applying the per-series style [DECISION]

MPT's template is a **global** config key ✅, but `video_terms` is per request ✅. So the backend expands the series template itself and sends already-styled terms:

`video_terms[i] = style_templates[series.style_template_id].template.replace("{term}", keyword[i]) + " " + NegativeClause.V1` and the worker's global template is set to `""` (feature D1, D4; 03 CR-10).

✅ MPT does not truncate list terms, never uses them in file names, and re-ranks them only when TwelveLabs is enabled (feature README I11, I12, I14); a comma-joined **string** would be split, so terms are always sent as an array (03 CR-10).

### 5.3 Cost guard

Superseded by feature D8 (03 CR-10): worst case = `MaxTerms × (tokens_per_image_p95[aspect:quality] × $30/1M + 200 × $5/1M)` ⚠️ rates priced on `gpt-image-2.5-flare`, superseded — relay price for `gpt-image-2` [OPEN] (01 DEC-11); from `price_versions.rates.image`; actual cost per billed image = text/image-input/output tokens × their rates from the relay report. Credits (plan 12, S) reserve the worst case.

### 5.4 Error codes

`image_moderated`, `image_generation_failed`, `keywords_flagged` (409 with `errors[]`); advisory hints `scenes_trimmed`, `scenes_sparse`, `images_skipped`. `keyword_limit_exceeded` is withdrawn (feature D10, 03 CR-10).

## 6. Task breakdown

Replaced by the 13 task files of `features/03-image-generation/` (README §8 mapping; 03 CR-10).

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 03.1.1-T1 | Style spec + 7 templates + labels + seed file | BA | S | — |
| 03.1.1-T2 | `StyleTemplateRules`, `NegativeClause`, `ScenePromptBuilder`, topic policy + seeder hook | BE | S | 03.1.1-T1, 09.2.2-T1, 09.2.5-T1 |
| 03.1.2-T1 | Preview images: generate, convert, upload, seed keys | Worker | XS | 03.1.1-T1, 03.3.2-T1 |
| 03.2.1-T1 | `RenderImagePlanner` + `IImageCostEstimator` | BE | S | 03.1.1-T2, 02.2.1-T1, 02.2.4-T1 |
| 03.2.2-T1 | `RenderKeywordLinter` + `NameCorpus` | BE | S | 02.2.1-T1, 02.5.3-T2 |
| 03.2.2-T2 | Linter wiring: checker, approve, render | BE | XS | 03.2.1-T1, 03.2.2-T1, 02.3.2-T1, 02.3.4-T1, 09.6.1-T1 |
| 03.3.2-T1 | Quality contact sheet + per-quality tokens | Worker | S | 03.1.1-T1, 01.3.1-T2 |
| 03.3.1-T1 | Trial renders token measurement | Worker | S | 03.4.1-T1, 05.5.2 |
| 03.3.1-T2 | CR-3/CR-4 decision + price seed + DECISIONS §4 | TL | XS | 03.3.2-T1, 03.3.1-T1 |
| 03.4.1-T1 | Spike: MPT image failure behaviour with an image stub | Worker | M | 01.2.2-T5 |
| 03.4.3-T1 | Image relay in `mpt-adapter` + `images.v1` | Worker/BE | M | 03.4.1-T1, 11.3.5-T1 |
| 03.4.3-T2 | mock-mpt `images.v1` report | BE | XS | 03.4.3-T1, 11.1.2-T1 |
| 03.4.2-T1 | Outcome mapping, moderation events, metering, retries | BE | M | 03.2.1-T1, 03.4.3-T2, 09.6.3-T1 |

## 7. Test plan

- **Unit:** template expansion; keyword cap maths; linter cases (Vietnamese text, names, gore words, empty).
- **Integration:** mock-mpt `MOCK_IMAGE_REPORT` values (`ok`, `moderated:<slots>`, `skipped:<share>`, `moderated_all`, `funds`, `none`) → every row of the feature D7 table: job/episode state, `moderation_events`, `usage_records`, retries (03.4.2-T1, 03 CR-10).
- **Manual:** contact sheet review; 3 trial renders per aspect.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Real cost per image 2–3× floor (`quality = auto` may resolve above `medium`, G6) | Measure before pricing (usage tap, 01 D6); margin buffer in 12; CR-3 decision |
| Images silently missing at Tier 1 (G7, G8) | Relay pacing + `images.v1` report; D7 fails a render with > 10 % missing scenes; tier increase (03 CR-10) |
| Horror imagery often moderated | Atmospheric templates; linter; track block rate per template and retire bad ones |
| MPT uses terms beyond image prompts | ✅ Not the case for list terms (I11–I14); spike 03.4.1-T1 re-checks at the pin (03 CR-10) |

## 9. Exit criteria (M0)

- Tokens/cost per image known for landscape and portrait.
- Default quality chosen; 7 templates written; spike note `worker-node/contract/images.md` handed to 05.4.1 (gate 03-M0, feature README §7; 03 CR-10).

## 10. Open questions and contract change requests

- ✅ MPT cannot pass `quality` or `moderation` (G6; answered by the 01 review, 2026-09-23).
- ✅ **CR-3 (from 01) — closed 2026-09-24 (03 CR-10): option (b).** The image relay (03.4.3) injects `quality`; the value is chosen by 03.3.2 and recorded by 03.3.1-T2 (feature D9). Original options: Options: (a) accept `auto` and price from measured tokens; (b) inject `quality` at a forwarder between MPT and OpenAI (the M0 usage tap supports `TAP_IMAGE_QUALITY`, off by default; in production the `mpt-adapter`/node forwarder, 11.3.5); (c) upstream PR to MPT adding a quality key (no fork, DECISIONS §3). Decide after 03.3.2 (contact sheet incl. `auto`) and 03.3.1 (tokens per aspect); record in DECISIONS §3/§4.
- ✅ **CR-4 (from 01) — closed 2026-09-24 (03 CR-10): relay metering.** The relay records `usage` per call per render task; the backend writes one `usage_records` row per billed image (feature D8). Original question: MPT exposes no `usage`, so FR-03-4 (`usage_records`, `operation = image`) needs a source. Options: a metering forwarder on the GPU node like the M0 tap (writes per-call usage keyed by task id, 11) or per-image estimates from measured `tokens_per_image` (§5.3) reconciled daily with the OpenAI Costs API (needs an Admin key). Owner plans: 03 (source), 11 (node component), 12 (`usage_records`).
- **Contract changes (applied to 00):** `style_templates` catalog (name, template, preview_storage_key, topic_hint, active) in 00 §6, referenced by `series.style_template_id`.

## 11. Estimate summary

Re-estimated in `features/03-image-generation/` §9, accepted by the owner 2026-09-24 (03 CR-11).

| Epic | Days |
|---|---|
| 03.1 Style system | 1.25 |
| 03.2 Keyword quality | 1.25 |
| 03.3 Measurement | 1.25 |
| 03.4 Failures (incl. 03.4.3 image relay) | 3.25 |
| **Total** | **7.0** → with 20 % buffer **8.4** person-days |
