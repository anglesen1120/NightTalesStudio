# 03.1 — Style system (templates, scene prompt builder, previews)

> Refines: `../../../03-image-generation.md` §4 epic 03.1 (stories **03.1.1**, **03.1.2**), FR-03-1, G4, G5, §5.2
> Contract: `../README.md` — **D1** (template contract, `NegativeClause`, `ScenePromptBuilder`), **D2** (catalogue v1, topic rule, labels, previews), **D13** (M0 global template), **F07**, **F11**, **F12**, **F13**, **F16**, **F21**, **CR-1**, **CR-2**, **CR-3**, **CR-7**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: task **03.1.1-T2 implemented 2026-10-01** (branch `feature/03.1.1-T2-09.4.1-T1`). Deviations from §3.1: `StyleTopicPolicy.IsAllowed(Topic, Topic?)` takes the typed hint (`StyleTemplate.TopicHint` is `Topic?`); `StyleTemplate.Create` keeps its 09.2.2-T1 signature (task file Design) and throws `ArgumentException` carrying the rule code, while the seeder pre-validates and aborts with `seed_style_template_invalid:<name>:<code>`. Other tasks in this folder: 03.1.1-T1 done; 03.1.2-T1 not implemented.

## 1. Scope

**Stories.**
- 03.1.1 *Style templates* — "As a creator I want to pick a visual style so that my channel looks consistent." Must · M (detailed 1.0 d).
- 03.1.2 *Style preview* — "As a creator I want a sample image per style so that I choose visually." Should · S (detailed 0.25 d).

**In scope.**
- The 7 template texts, their vi/en labels and hints, and the tracked seed file `seed/style-templates.v1.json` (03.1.1-T1).
- Domain rules for templates (`StyleTemplateRules`), the platform `NegativeClause`, `ScenePromptBuilder`, `StylePromptVersion`, and the topic rule `StyleTopicPolicy` (03.1.1-T2).
- One preview image per template, uploaded once by the operator; `preview_storage_key` set in the seed file (03.1.2-T1).

**Out of scope.**
- `GET /style-templates` and its DTO (09.4.2; only the topic filter is requested by CR-2).
- The series form and picker UI (10.4.2; copy keys handed over by CR-7).
- Recurring characters / Edits endpoint (DECISIONS §5.2), thumbnails, per-series custom templates (not in P).
- Rotating styles inside one series: 02.8.4's variety guard rotates **story** structure only; the scene style stays fixed per series (G5) [DECISION].

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| S1 | MPT applies `openai_image_prompt_template` globally; empty or no `{term}` → raw term (I3) | README §1 I3 ✅ |
| S2 | `style_templates`: `name` unique, `template` CHECK contains `{term}`, `preview_storage_key` null, `topic_hint` null or a topic, `active`; platform table (no tenant filter); `series.style_template_id` FK RESTRICT | 09.2.2 plan §3 ✅ (P6) |
| S3 | 09.2.5-T1 seeds 2 placeholder templates from `seed/catalog.dev.json` and upserts by `name` (updates `template`, `topic_hint`, `active`) | 09.2.5-T1 ✅ (P7) |
| S4 | 02.8.2-T2 builds scene prompts itself and strips the negative clause by suffix (P4) | 02.8.2-T2 ✅ |
| S5 | The prompt reaches OpenAI unchanged (MPT only replaces `{term}`), so the whole scene prompt is ours to design (I1, I3) | README §1 ✅ |
| S6 | OpenAI returns `moderation_blocked` for blocked prompts (O9); a negative instruction ("no gore") in a GPT Image prompt is followed as an instruction ⚠️ (not verified; checked on the 03.3.2 contact sheet) | DECISIONS §5.2 ✅ / ⚠️ |
| S7 | Bucket `nt-public-<env>` holds voice/style previews, public-read or CDN | 11 §4 11.4.1 ✅ |
| S8 | Web i18n is runtime Transloco, vi + en; every screen string is a key | 00 §5.1 ✅ |

**Closed here.** Where the negative clause lives (D1, builder appends it). Topic restriction for true crime (D2). Labels without new columns (D2). Preview format and storage (D2).

## 3. Domain and data

### 3.1 Types — `src/NightTales.Domain/Catalog/`

| File | Type / signature | Notes |
|---|---|---|
| `StyleTemplate.cs` | existing entity (09.2.2-T1): `Name`, `Template`, `PreviewStorageKey?`, `TopicHint?`, `Active` | unchanged; gets `static ErrorOr<StyleTemplate> Create(...)` calling the rules |
| `StyleTemplateRules.cs` | `static ErrorOr<Success> Validate(string name, string template)` | D1: slug regex, 3–40 chars; exactly one `{term}`; 60–600 chars trimmed; no `\r`/`\n`; no `"`; must **not** contain the negative clause text or the words `no gore` (case-insensitive) |
| `NegativeClause.cs` | `const string V1 = "No gore, no blood, no text, no letters, no logos, no watermark, no real person likeness."`, `const string Version = "v1"`, `static string Current => V1` | one platform constant; a change = a new `Vn` + release note |
| `ScenePrompt.cs` | `sealed record ScenePrompt(string Text, string ModerationText, string Sha256)` | D1 |
| `ScenePromptBuilder.cs` | `static ScenePrompt Build(StyleTemplate style, string keyword)` | `Replace("{term}", keyword.Trim(), Ordinal)`; `Text = replaced + " " + NegativeClause.Current`; SHA-256 over UTF-8 `Text`, lower-case hex |
| `StylePromptVersion.cs` | `static string For(StyleTemplate style)` | `style=<name>@<sha8(template)>;neg=<NegativeClause.Version>` |
| `StyleTopicPolicy.cs` | `static bool IsAllowed(Topic topic, string? topicHint)` | `true_crime` → hint must be `true_crime`; other topics → hint ≠ `true_crime` |

All pure, no packages (Domain rule, 00 §5.2). `System.Security.Cryptography.SHA256` is in the BCL.

### 3.2 Seed file — `seed/style-templates.v1.json` (tracked)

```json
{ "schema": "style-templates.v1",
  "negativeClauseVersion": "v1",
  "templates": [
    { "name": "dark-folklore-ink", "topicHint": "ghost", "active": true,
      "previewStorageKey": "style-previews/dark-folklore-ink.v1.webp",
      "template": "Dark folklore ink illustration of {term}. …" } ] }
```

The text of all 7 templates is fixed in 03.1.1-T1. `previewStorageKey` is `null` until 03.1.2-T1 uploads the file.

### 3.3 Migration

None. The table exists (09.2.2). The seeder change is CR-1 (09.2.5-T1 reads this file in every environment).

## 4. Use cases

No new command or query. Consumers of this feature:

| Consumer | Uses | Owner |
|---|---|---|
| Seeder | `StyleTemplateRules.Validate` before upsert; a failing row aborts the seed with its name | 09.2.5-T1 (CR-1) |
| `CreateSeriesCommand` / `UpdateSeriesCommand` | `StyleTopicPolicy.IsAllowed(topic, template.TopicHint)` → 400 `validation_failed` field `styleTemplateId` | 09.4.1-T1 (CR-2) |
| `GET /style-templates?topic=` | same policy as a filter | 09.4.2 (CR-2) |
| Render submit | `ScenePromptBuilder.Build` per term, `StylePromptVersion.For` | 03.2.1-T1 → 09.6.2 |
| Autopilot content gate | `ScenePromptBuilder.Build(...).ModerationText` | 02.8.2-T2 (CR-3) |

## 5. API and events

No new route or event. Existing route `GET /style-templates` (00 §8) returns `{ id, name, topicHint, previewUrl }`; labels come from i18n keys `series.style.<name>.label` / `.hint` (CR-7). `previewUrl` is built from `preview_storage_key` + the public bucket base URL (09.4.2).

## 6. UI

Owned by 10.4.2 (step 4 "visual style", grid of preview images). This feature delivers:
- vi/en label + one-line hint per template (03.1.1-T1 table), as i18n keys.
- A true-crime notice under the grid: "Chỉ các phong cách minh hoạ không có khuôn mặt người thật" / "Only illustration styles without real faces" (key `series.style.trueCrimeNotice`).
- Accessibility: every preview `<img>` has `alt` = the label; the grid is a radio group.

## 7. Jobs and integrations

- No job. The preview images are generated once with the 03.3.2 tool (`--mode previews`) through the M0 usage tap and the trial image project (01 D7). Cost ≤ 7 images × ≈ $0.2 ⚠️ upper guess.
- Upload with the storage CLI to `nt-public-<env>` (runbook step in 03.1.2-T1).

## 8. Security and privacy

| Threat | Control |
|---|---|
| A template without the negative clause reaches OpenAI | The clause is not in the template; the builder always appends it (D1); the relay rejects prompts without it (D5, `relay_prompt_unstyled`) |
| Photographic style used for a true-crime case (real-person likeness, G4) | `StyleTopicPolicy` (D2) + templates that say "no faces" / "silhouettes only" |
| Template edited in the DB by hand | Seed upsert is the only writer; `StylePromptVersion` in `render_jobs.params` makes a change traceable (F16) |
| Preview bucket is public | Previews are generic scenes made from our own fixed keywords, no user content |

Retention: templates and previews are platform data; not affected by user deletion.

## 9. Test plan

- **Unit (Domain):** every `StyleTemplateRules` rule (slug, one `{term}`, two `{term}`, none, 59/601 chars, newline, quote, contains negative clause); `ScenePromptBuilder` output ends with the clause, `ModerationText` has no clause, SHA-256 stable and matches a fixed vector; keyword trimmed; `StylePromptVersion` format; `StyleTopicPolicy` 3 × 4 table.
- **Seed file test:** `SeedFile_AllTemplatesPassRules`, `SeedFile_HasSevenTemplates_TwoTrueCrime`, `SeedFile_NamesUnique`, `SeedFile_NegativeClauseVersionMatchesDomain`.
- **Integration:** seeder upsert from the file (CR-1, owned by 09.2.5 tests; one extra case here: an invalid template aborts the seed).
- **Manual:** contact sheet review of the 7 styles (03.3.2-T1); preview grid check in 10.4.2.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 03.1.1-T1 | Style spec + 7 templates + labels + seed file | BA + Dev | **S (0.5)** | — | 03.1.1-T2, 03.3.2-T1, 03.1.2-T1, 02.8.2-T2 |
| 03.1.1-T2 | `StyleTemplateRules`, `NegativeClause`, `ScenePromptBuilder`, `StylePromptVersion`, `StyleTopicPolicy` + seed hook | BE | **S (0.5)** | 03.1.1-T1, 09.2.2-T1, 09.2.5-T1 | 03.2.1-T1, 09.4.1-T1 (CR-2), 02.8.2-T2 (CR-3), 03.4.3-T1 |
| 03.1.2-T1 | Preview images: generate, convert, upload, seed keys | Worker + Ops | **XS (0.25)** | 03.1.1-T1, 03.3.2-T1, 11.4.1-T1 (upload; 11 CR-13) | 10.4.2-T1 |

Total **1.25 d** (high-level 1.5 d; README §9).

### Open items for the TL

- [OPEN] Whether GPT Image honours "no …" instructions or sometimes draws the named thing (S6) — the contact sheet (03.3.2-T1) checks it on 80 images; if it fails, the clause is rewritten positively ("clean scene without any lettering") as `NegativeClause.V2`.
- [OPEN] A per-series custom template (creator-written) is not in P; the builder already treats templates as data, so it is a UI + validation feature later.

## 11. Rollout

- **Flag:** none; the catalogue is platform data.
- **Order:** 03.1.1-T1 (M0) → 03.3.2-T1 review → 03.1.1-T2 (M1) with CR-1 → 03.1.2-T1 → 10.4.2.
- **Migration:** none.
- **Monitoring:** `moderation_events{source=openai_image}` per `style_prompt_version` (03.4.2-T1) → retire a template with a high block rate (03 §8 risk).
- **Rollback:** set `active = false` in the seed file and re-seed; existing series keep rendering (D1).
