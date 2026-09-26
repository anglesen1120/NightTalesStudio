# 02.1 — Prompt presets: detailed plan

> Refines: `../../../02-story-writing.md` §4 (stories **02.1.1**, **02.1.2**, **02.1.4**, **02.1.5**) and §6 (tasks 02.1.1-T1…T3, 02.1.2-T1, 02.1.4-T1, 02.1.5-T1).
> Contract: `../README.md` — **D3** (`story.v1`), **D4** (length/token rule), **D5** (prompt catalog), **D6** (untrusted data), **D16** (rubric); findings **F07**, **F08**, **F09**, **F22**, **F30**, **F37**; facts **O7**, **O9**, **V1**, **P5**; **FR-02-1**, **FR-02-11**, **NFR-02-2**.
> Template: `../../../00-overview.md` §10.1.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-23. Nothing implemented yet.

---

## 1. Scope

**In scope (M0, no code).** The complete authored prompt catalog for the writer path:

- `prompts/` skeleton, `manifest.json`, `REFERENCES.md`, PR provenance checkbox (02.1.4-T1).
- The **preset spec** every later prompt obeys: writer system prompt outline, the full strict `story.v1` JSON Schema text, the D4 length table, `structure-vocab.v1.json` starter lists (02.1.1-T0).
- Six presets `story/{ghost|spiritual|true_crime}.{vi|en}.v1.md` (02.1.1-T1…T3).
- `shared/keyword-rules.v1.md` + ≥ 10 good/bad keyword examples per topic (02.1.2-T1).
- `shared/ear-rules.{vi|en}.v1.md`, `shared/overused.{vi|en}.v1.txt` (~30 entries each) and the functional naturalness questions handed to 02.6.1 (02.1.5-T1).

**Out of scope (named, so nobody widens it).**

| Not here | Owner |
|---|---|
| Loading/validating the catalog in C# (`PromptCatalog`, manifest sha256 check, startup gate) | 02.2.3-T1 |
| Prompt assembly, the D6 data object, injection fixtures | 02.2.3-T2 |
| The `story.v1` **validator** implementation (`StoryDraftValidator`) and derived fields | 02.2.1-T1 |
| The M0 story harness that proves the presets | 02.1.3-T0 |
| A/B run, blind scoring, model decision, `presets v1` freeze | 02.1.3-T1, 02.1.3-T2 |
| Review/revise/facts/bible/ideas/finder prompts | 02.6.1-T1, 02.5.3-T1, 02.6.3-T2, 02.8.1-T1, 02.7 |
| `KeywordRules` implementation in Domain (`Stories/Keywords/`) | CR-5 → 03.2.2 |
| MPT integration, `video_script`/`video_terms` hand-off | 02.2.2 |
| Fixture files `fixtures/briefs.*` and the true-crime fixture fact sheet | **02.1.3-T0** (**T34**) — this feature only fixes the contract they must satisfy |
| `fixtures/rubric.md` | 02.1.3-T1 (**T35**) |
| `fixtures/injection/*` | 02.2.3-T2 (**T27**) |
| `fixtures/seeded-defects/*` | 02.6.1-T2 (**T27**) |
| `manifest.json` entries for `fixtures/**`, `tools/**`, `manifest.json`, `REFERENCES.md`, `**/.gitkeep`, dotfiles | excluded from the release manifest by **T27/T75** — nothing to do |
| Any C# file, migration, route, build or test project | — |

**Definition of done for the feature.** All 6 `topic × language` presets and the shared rule files (`structure-vocab.v1.json`, `preset-spec.v1.md`, `keyword-rules.v1.md`, `ear-rules.{vi|en}.v1.md`, `overused.{vi|en}.v1.txt`) plus `system/writer.v1.md` exist with valid front-matter, the manifest lists every loadable file with a sha256 (keys relative to `prompts/`, T27), and the M0 harness (02.1.3-T0) returns **schema-valid** `story.v1` output for each preset's **own 3 topic briefs** (6 presets × 3 briefs = 18 runs, T34; the 36-story A/B in 02.1.3-T1 is the follow-on measurement). Nothing in this feature calls a provider.

---

## 2. Confirmed facts

| # | Fact (id) | Consequence here |
|---|---|---|
| F07 | Length is unspecified per `length_tier × language`; for vi a "word" is a syllable | T0 fixes the D4 table verbatim in the spec; every preset states the tier band it targets by reference, never by prose numbers |
| F08 | `hook`/`ending` have no keywords, no beats, no `schema_version`; strict-mode schemas cannot express string length limits | T0 writes the schema **without** `maxLength`/`minLength` and states that the validator (02.2.1-T1) enforces lengths; `beat` and `structure` exist in the schema |
| F09 | Uncontrolled keyword totals blow the `ceil(D × 6)` cap (P5) | D4 segment counts derive from K = 6; `keyword-rules.v1.md` states the cap and `KeywordRules` (built by 02.2.1-T1, reused by 02.3.4 and 03.2.2) enforces it (**T85**) |
| F22 | `prompt_version` covers 3–5 files; disk loading drifts between nodes | T0 defines the composite `prompt_version` format and the per-file `version`; T1…T3 stamp each file's front-matter `version` |
| F30 | The prompt catalog was missing from the estimate (now in the 4.25 d) | §10 re-estimate below |
| F37 | Keyword language was open; 03 FR-03-3 fixes **English** | Closed: keyword rules and examples are English-only; presets say so |
| V1 | vi writes each syllable as its own token | All vi counts are syllables; the token rule is D4's NFC + Unicode-whitespace split |
| P5 | `ceil(D × 6)` keyword cap, K = 6 images/min | Segment counts in D4 are the keyword budget |
| O7 | `omni-moderation-latest` is free; category list | Presets must avoid generating the blocking categories, but moderation is not this feature's job — the tone rules are the *authoring* side of the same line |
| O9 | Prompt caching needs a static ≥ 1,024-token prefix, instructions first, data last | T0 fixes the file order `system → preset → shared rules → data object`; the ≥ 1,024 threshold applies to the whole cacheable head (strict schema + system prompt + preset + shared blocks), not to `writer.v1.md` alone (02 T67; measured in 02.1.3-T0) |
| D16 | 7-dimension rubric with anchors in `prompts/fixtures/rubric.md` | T0's preset spec lists the rubric dimensions the presets must be able to satisfy (hook, pacing, atmosphere without gore, natural language, originality, factual care, length fit); the rubric file itself is 02.1.3-T1 |
| FR-02-11 | Prompts are original work; external projects inspire ideas only, listed in `prompts/REFERENCES.md` | 02.1.4-T1 |

⚠️ No web research was done for this plan; every external claim is inherited from README §1 with its source and date.

---

## 3. Domain and data

**No database tables, no entities, no migrations.** The artifacts are repository files under the tracked `prompts/` folder (D5). Data contracts:

### 3.1 Preset / shared-file front-matter (YAML-ish header, one key per line, `---` fenced)

```yaml
---
id: ghost.vi
topic: ghost            # ghost | spiritual | true_crime
language: vi            # vi | en
version: v1
role: preset            # preset | system | shared | schema | fixture | review | revise | facts | bible | ideas | finder
target_tier: all        # all | short | standard | long
---
```

- `id` = `{topic}.{language}` — the only place the pair is written; `PromptCatalog` keys on it (02.2.3-T1).
- `version` participates in `prompt_version` (D5): `rel=2026.10.1;sys=writer.v1;preset=ghost.vi.v1;review=v1;revise=v1;schema=story.v1`.
- `role` lets the loader apply per-role rules without guessing from the path.

### 3.2 `manifest.json` (D5 shape, placeholder written by 02.1.4-T1, filled by each later task)

```json
{
  "release": "2026.10.1",
  "files": {
    "system/writer.v1.md": { "version": "v1", "sha256": "<64 hex>" },
    "story/ghost.vi.v1.md": { "version": "v1", "sha256": "<64 hex>" }
  }
}
```

- **T27:** keys are **relative to `prompts/`** (e.g. `system/writer.v1.md`), matching the embedded `LinkBase="Prompts"`. `fixtures/**`, `tools/**`, `manifest.json`, `REFERENCES.md`, `**/.gitkeep` and dotfiles are **excluded** from the release manifest (T27/T75) — the fixture files this feature's presets depend on are therefore never listed here.

### 3.3 `shared/structure-vocab.v1.json` (owned by 02.1.1-T0; consumed by the validator, D3)

```json
{
  "schema_version": "structure-vocab.v1",
  "topics": {
    "ghost": {
      "sub_genre":  { "vi": ["truyện ma tâm linh", "…"], "en": ["quiet haunting", "…"] },
      "setting":    { "vi": ["ngôi nhà cũ", "…"],      "en": ["old house", "…"] },
      "opening":    { "vi": ["một lời kể truyền miệng", "…"], "en": ["a whispered account", "…"] },
      "twist_type": { "vi": ["người kể là kẻ gây ra", "…"], "en": ["the narrator caused it", "…"] }
    }
  }
}
```

- Matching is exact after NFC + lowercase; no entry → the validator stores `other:<text>` (D3). This file is **data**, not prompt text; it is never substituted into a template (D6).

### 3.3b `prompts/shared/preset-spec.v1.md` (owned by 02.1.1-T0)

The authoring spec every preset task obeys: front-matter contract (§3.1), section order, beat rules (hook ≤ 20 s), tone rules, CTA rules, good/bad hook examples, the validator-rules list, the D4 table and the cache order. It lives inside `prompts/` so 02.2.3-T1's "every file is listed" rule stays true; it is **not** a template (nothing substitutes into it).

### 3.4 `story.v1` JSON Schema (full text lives in 02.1.1-T0, target `prompts/schemas/story.v1.json`)

Strict mode rules restated as an authoring constraint: every property `required`, `additionalProperties: false`, `enum` for `beat` / `narrator_mode` / `content_warnings`, `fact_refs` = `[]` when unused. Length limits, markdown/URL bans and keyword counts are **validator** rules (F08) and are listed in the preset spec, not in the schema.

### 3.5 Derived fields (D3, for reference only — computed by 02.2.1-T1)

`body` = hook + paragraphs + ending joined by `\n\n`; `keywords` = hook ++ paragraphs ++ ending; `word_count` = D4 token count (vi: syllables). The backend adds `schema_version` and `fact_sources`; **the model never outputs URLs or source ids**.

---

## 4. Use cases

| # | Actor | Use case | Realised by |
|---|---|---|---|
| UC1 | Owner/BA | Author a preset for a `topic × language` pair from the spec, in our own words | 02.1.1-T1…T3 (ghost/spiritual/true-crime), 02.1.1-T0 (spec) |
| UC2 | Owner/BA | Author a shared rule file (keyword rules, ear rules, overused list) referenced by every preset | 02.1.2-T1, 02.1.5-T1 |
| UC3 | TL / reviewer | Prove provenance: every idea taken from elsewhere is a one-line principle + source; no copied/translated text | 02.1.4-T1 (REFERENCES.md + PR checkbox) |
| UC4 | M0 harness (02.1.3-T0) | Load a preset + shared rules + schema, call OpenAI, get **schema-valid** `story.v1` for 3 fixture briefs | acceptance gate of T1…T3 |
| UC5 | 02.2.3-T1 | Load the catalog at startup and fail if a `topic × language` preset is missing, a sha256 differs, or an unknown `{{placeholder}}` is used | consumes D5 tree + §3.1/§3.2 contracts |
| UC6 | Owner/BA | Iterate a preset after the A/B result without breaking the schema or the length table | 02.1.3-T2 freezes `v1`; a change bumps the file `version` |

UC5/UC6 are *consumer* use cases: this feature defines the contract, 02.2.3-T1 implements the loading.

---

## 5. API and events

**No HTTP route, no domain event, no Hangfire job in this feature.** The surfaces are:

1. **File contracts** (§3) consumed by `PromptCatalog` (02.2.3-T1) — the only integration point.
2. **Placeholder contract** (D5, closed list, unchanged): `language_name`, `tier_minutes`, `token_range`, `segment_range`, `keyword_rules`, `ear_rules`, `overused_phrases`, `structure_vocab`. A preset may use only these; anything else fails startup in 02.2.3-T1. [DECISION] This feature adds **no** placeholder.
3. **Fixture contract** consumed by 02.1.3-T0 — the files are **owned by 02.1.3-T0** (T27/T34): `prompts/fixtures/briefs.vi.json` and `briefs.en.json`, each holding **9 briefs per language = 3 per topic**, one object `{ "id": "vi-ghost-1", "topic": "ghost", "tier": "standard", "brief": "…", "language": "vi" }`. **T34: a preset's acceptance runs the 3 briefs of its own topic**, not 3 shared briefs. **T24** fixes the tier at `standard` for all 36 A/B runs (pacing and length fit cannot be judged on 1-minute stories).

```json
[ { "id": "vi-ghost-1", "topic": "ghost", "tier": "standard", "language": "vi",
    "brief": "Một người trở về căn nhà cũ của gia đình và nghe thấy giọng nói quen thuộc." } ]
```

- **Ordering — resolved:** the briefs and the true-crime fixture fact sheet are owned by **02.1.3-T0** (T34), which is also the harness the preset tasks are accepted against, so the preset gate and its fixtures land together. No cycle remains (the former P2 → AB concern is closed).
- Briefs are **fixtures**, not prompts; they must be short enough that the tier's `max_output_tokens` is the only binding limit ⚠️.
4. **Fixture fact sheet** for the true-crime topic (**T34**): an invented case with stable ids `f1…f5` and aliases `s1…s2`, shape `{ id, statement, sources }`; **owned by 02.1.3-T0, content supplied by 02.1.1-T3**. It is **not** a D5 path (D5 lists only `briefs.{vi|en}.json`, `injection/*`, `seeded-defects/*`) — [DECISION] T34 adds it, so nothing in this feature authors or manifests it. No real person, place or incident.

---

## 6. UI

None. The only human-visible surface is the repository:

- `prompts/REFERENCES.md` (UC3) — one line per borrowed principle: `principle — source (e.g. "self-review → one revision → human" — InkOS README)`.
- The PR template provenance checkbox (02.1.4-T1): *"I confirm no prompt text was copied or translated from InkOS (AGPL-3.0) or any other project; any borrowed idea is recorded in `prompts/REFERENCES.md`."*
- No admin screen, no editor, no config key. Prompt edits ship through a PR (the provenance checkbox is the gate).

---

## 7. Jobs and integrations

| Integration | Status in this feature |
|---|---|
| OpenAI Responses API | Not called here. The preset spec fixes what the harness (02.1.3-T0) will send: system prompt first, preset, shared rules, then the D6 JSON data object |
| MPT | Not called here; the `video_script`/`video_terms` hand-off is 02.2.2 |
| Hangfire / Worker / Api | Untouched |
| `PromptCatalog` (02.2.3-T1) | Consumer; D5's startup validation is the enforcement of §3.1/§3.2/§5.2 |
| `StoryDraftValidator` (02.2.1-T1) | Consumer of §3.3 (structure vocab) and of the validator-rules list in the preset spec |
| `KeywordRules` (built by 02.2.1-T1, reused by 03.2.2 via CR-5) | Consumer of `keyword-rules.v1.md`; the rule text must match R1–R13 (**T76–T81**) and 03 FR-03-3 (English, 3–12 words, no person names, no gore or weapon/self-harm/nudity/minor imagery, no real place identifier, no instruction) |
| 02.6.1 review prompt | Consumer of the ear-rule questions and the overused lists (02.1.5-T1 hands them over as text, does not write the review prompt) |

---

## 8. Security and privacy

| Concern | Rule |
|---|---|
| Untrusted data (D6, F21) | Prompt files contain **no** untrusted text and no user data. Every preset must state the two-tier rule: control keys are followed, every material string is written about and never an instruction (02 T69). Injection fixtures are 02.2.3-T2, but the preset wording is authored here |
| Prompt injection via brief/bible/facts/sources | The preset must not instruct the model to follow any instruction found in data; the data object is JSON-encoded |
| Likeness / real persons (FR-02-3, 03 FR-03-3) | Keyword rules forbid person names and likeness; true-crime presets forbid naming living private individuals in scene prompts |
| Tone line (02.1.1 story text) | No gore, no sexual content, no minors in danger on screen (02 T73). Authored as a tone rule in every preset; enforced by moderation (D12) and the validator |
| Copyleft / copyright (I1, FR-02-11) | REFERENCES.md + PR checkbox; InkOS (AGPL-3.0) ideas only, never text or translation |
| Secrets | None. No key, no token, no `.env`; the harness reads its own key (01) |
| PII in fixtures | Fixture briefs are invented; no real names, no real addresses, no real incidents |
| Logging (D19) | Nothing logs prompt text; this feature adds no logging |

---

## 9. Test plan

Documentation-only feature: "tests" are deterministic checks over the files, runnable by a script or by hand. Each check is named and observable.

| # | Check (named) | Expected |
|---|---|---|
| T-1 | `PresetFrontMatter_AllSixPresets_HasIdTopicLanguageVersion` | 6 files, each with the §3.1 keys, `id` = `{topic}.{language}`, unique |
| T-2 | `Manifest_EveryPromptFile_HasSha256AndVersion` | every file under `prompts/` (except `manifest.json`) has an entry; sha256 matches the file |
| T-3 | `Preset_TopicLanguageMatrix_Complete` | the 3×2 matrix has no gap → 02.2.3-T1 startup gate cannot fail |
| T-4 | `Preset_UnknownPlaceholder_NoneUsed` | every `{{…}}` token ∈ the D5 closed list |
| T-5 | `Schema_StoryV1_StrictModeShape` | `required` covers all properties, `additionalProperties: false`, enums present, **no** `maxLength`/`minLength`/`pattern` |
| T-6 | `Preset_HookSegment_StatesTwentySecondLimit` | each preset's hook beat states the ≤ 20 s rule (and its tier token share) |
| T-7 | `Preset_ToneRules_ProhibitsGoreSexualMinors` | each preset contains all three prohibitions |
| T-8 | `Preset_CTA_CarriesChannelAction` | each preset defines the ending call to action |
| T-9 | `KeywordRules_GoodBadExamples_PerTopicAtLeastTen` | ≥ 10 good and ≥ 10 bad per topic; every bad one names the violated rule |
| T-10 | `OverusedLists_ViEn_AtLeastThirtyEntries` | ~30 lines each, no duplicates, one phrase per line |
| T-11 | `EarRules_NumbersDates_SpokenForm` | each language file states the spoken-form rule for numbers, dates, units, ordinals |
| T-12 | `TrueCrimePreset_FactRules_AliasOnlyNoUrls` | the true-crime presets state `fact_refs ⊆` approved sheet ids, aliases `s1…s10`, never URLs |
| T-13 | `StructureVocab_AllTopics_AllFourKeys` | 3 topics × 4 keys × 2 languages, non-empty starter lists |
| T-14 | `References_NoCopiedText_InkOsIdeaOnly` | REFERENCES.md lists principles with sources; a diff review confirms no verbatim external text (manual) |
| T-15 | `Fixtures_Briefs_ShapeContract` | the brief files (owned by 02.1.3-T0) hold 9 objects per language, 3 per topic, each `{id, topic, tier: "standard", brief, language}` — a **contract check** on 02.1.3-T0's file (T34), not a file this feature authors |
| T-16 | **acceptance gate** `Harness_SixPresets_ThreeBriefs_SchemaValid` | 02.1.3-T0 returns schema-valid `story.v1` for 18/18 runs (manual, M0) |

T-16 is the story-level acceptance (02.1.1/02.1.2/02.1.5); T-1…T-15 are static checks that run without a provider. T-15's fixture files and T-16's harness live in 02.1.3 (T27 ownership); this feature supplies the presets, the shared rules, the schema and the contract those fixtures must satisfy.

---

## 10. Tasks and re-estimate

| Task | Title | Size | Area | Depends on | Blocks |
|---|---|---|---|---|---|
| 02.1.4-T1 | `prompts/` skeleton + `REFERENCES.md` + PR provenance checkbox | XS (0.25) | TL | — | all (folder + manifest) |
| 02.1.1-T0 | Preset spec: writer system prompt outline, `story.v1` schema text, length table, structure vocab | M (1.0) | BA | 02.1.4-T1 | 02.1.1-T1…T3, 02.1.2-T1, 02.1.5-T1, 02.1.3-T0 |
| 02.1.1-T1 | Draft ghost vi/en presets | S (0.5) | BA | 02.1.1-T0 | 02.1.3-T1 |
| 02.1.1-T2 | Draft spiritual vi/en presets | S (0.5) | BA | 02.1.1-T0 | 02.1.3-T1 |
| 02.1.1-T3 | Draft true-crime vi/en presets incl. fact rules | M (1.0) | BA | 02.1.1-T0 | 02.1.3-T1, 02.5.3-T1 |
| 02.1.2-T1 | Keyword rules + ≥ 10 good/bad examples per topic | S (0.5) | BA | 02.1.1-T0 | 02.2.1-T1 (validator), CR-5/03.2.2 |
| 02.1.5-T1 | Ear rules + overused lists (vi, en) + naturalness questions handed to 02.6.1 | S (0.5) | BA | 02.1.1-T0 | 02.6.1-T1 |
| | **Total** | **4.25 d** | | | |

Matches README §9 (`02.1 presets 3.25 → 4.25`, "+preset spec + schema + length table as a shared base (F07, F08)").

**Sequencing (M0):** 02.1.4-T1 → 02.1.1-T0 → {02.1.1-T1, 02.1.1-T2, 02.1.1-T3, 02.1.2-T1, 02.1.5-T1 in any order} → 02.1.3-T0 → 02.1.3-T1 → 02.1.3-T2.

**Why the estimate moved from 3.25 to 4.25.** The high-level plan assumed presets could be written without a shared spec; F07/F08 make the length table, the token rule and the strict schema a prerequisite that three later features (02.2.1, 02.2.3, 02.1.3) all read. Authoring them once, here, is cheaper than three divergent copies (+1.0 d). The keyword example tables (02.1.2-T1) and the overused lists (02.1.5-T1) are content work, not code, and were underestimated by ~0.25 d each in the high-level plan; both are now explicit tasks.

### Open items for the TL

- **[RESOLVED by T34–T36 — recorded, no action left in this feature]** `structure-vocab.v1.json` is owned by 02.1.1-T0 (02.8.4 consumes it, does not edit it in P) · the runtime authority for `max_output_tokens` is `price_versions.limits` only (the preset spec's table is documentation) · `rubric.md` is authored once by 02.1.3-T1 · overused phrases feed the review prompt only, no local linter in P · true-crime `content_warnings` always include `crime` · **superseded by 02 T67:** the cacheable head (schema + system + preset + shared blocks) must reach ≥ 1,024 tokens — `writer.v1.md` alone (~765) is fine, and shared rules are **not** moved into the system prompt (D5 order stays system → preset → shared → data) · briefs are 9 per language = 3 per topic, each preset's acceptance uses its topic's 3 briefs · the briefs **and** the true-crime fixture fact sheet are owned by 02.1.3-T0, so the preset acceptance gate and its fixtures land together (the former P2 → AB cycle is closed).
- **[OPEN] vi/en speaking rate (D4) — T26 ruling.** vi 160 / en 145 tokens/min are ⚠️ defaults; 02.1.1-T0 writes the table with those numbers and marks them as config-overridable (`Story:Length:Rate:{lang}`). Per **T26** the D4 rates stay the defaults until 01.3.1-T4 lands (`words_per_minute_measured = null` is accepted for the M0 A/B); no rate measurement is in scope here.
- **[OPEN] Keyword cap inputs (02.1.2-T1).** 03.2.1's `ceil(D × 6)` and K = 6 (P5) should be re-confirmed against plan 03 at implementation time; the gore word list (rule R6) belongs to the Domain implementation (CR-5) and is linked, never duplicated here.
- **Size note.** `plan.md` is 21.2 KB (target 12–20) and the four content-heavy task files exceed 4–8 KB (02.1.1-T0 15.0, 02.1.1-T3 11.3, 02.1.2-T1 10.6, 02.1.5-T1 12.4) because the assignment requires the full strict `story.v1` schema text, 30 keyword good/bad pairs and 60 overused-phrase lines inline; no required content was trimmed to hit the byte target.

---

## 11. Rollout

| Step | Gate | Evidence |
|---|---|---|
| 1 | 02.1.4-T1 lands: `prompts/` tree + manifest placeholder + REFERENCES + PR checkbox | T-2 (partial), T-14 |
| 2 | 02.1.1-T0 lands: spec + schema + length table + structure vocab | T-5, T-13 |
| 3 | Six presets + shared rule files land | T-1, T-3, T-4, T-6…T-12, T-15 |
| 4 | 02.1.3-T0 harness consumes the catalog | T-16 (18/18 schema-valid) |
| 5 | 02.1.3-T1 measures; 02.1.3-T2 freezes `presets v1` and records D15 | README gate **02-M0** |

- **No feature flag.** The catalog is inert data until 02.2.3-T1 loads it; M0 presets are exercised only by the harness.
- **Rollback.** Prompt edits are plain file edits: revert the file and bump `version` (the manifest sha256 is recomputed in the same PR). A bad preset cannot corrupt data because nothing persists until M1.
- **After M0.** Any preset change bumps the file's `version` and the manifest sha256 in one PR with the provenance checkbox; `prompt_version` on new `story_versions` rows then differs, so quality changes stay traceable (NFR-02-2). 02.1.3-T2 records the frozen baseline.
- **Hand-offs.** 02.2.3-T1 (loading + startup validation), 02.2.1-T1 (validator rules + structure vocab), 03.2.2 via CR-5 (keyword linter), 02.6.1-T1 (ear questions + overused lists).
