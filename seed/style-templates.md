# Style templates v1 — authoring reference (03.1.1-T1)

Machine source of truth: `seed/style-templates.v1.json` — read by the seeder (CR-1, every
environment) and by `trial/tools/check_style_templates.py` in M0; from M1 the same rules are
enforced in C# by `StyleTemplateRules.Validate` + the seed test `SeedFile_AllTemplatesPassRules`
(03.1.1-T2). This file is the human reference: slot order, people rule, limits, change process.
Labels and hints: `seed/i18n-style-labels.v1.json` (web keys, CR-7 → 10.4.2).

## 1. Slot order (every template)

    <medium> of {term}[, framing]. <palette>, <texture>, <light>, <composition>, <people rule>, <mood>.

- Sentence 1 = `medium` (+ optional `framing`), the same opener for every scene of a series, so
  medium/palette/light/people are described the same way while the keyword changes (G5).
- Sentence 2 = the remaining slots, in the order above, ending with `mood`.
- `people rule` is mandatory in every template and always states what *is shown*
  ("silhouettes", "distant shapes", "places and objects only") — never a safety negative (§4).
- One `{term}`, one line, no `"` (§3).

### Per-template slot map (v1 texts are fixed; the one revision is recorded in §6)

| `name` | topicHint | medium | framing | palette | texture | light | composition | people rule | mood |
|---|---|---|---|---|---|---|---|---|---|
| `dark-folklore-ink` | `ghost` | Dark folklore ink illustration of `{term}` | — | muted indigo and grey tones | Black ink wash on aged rice paper | pale moonlight | deep shadows, wide composition | people shown only as small distant silhouettes | quiet and eerie |
| `foggy-cinematic` | `ghost` | Cinematic film still of `{term}` | wrapped in thick fog | Cold teal and grey palette | soft 35mm film grain | low-key lighting | shallow depth of field, wide establishing shot | people shown only as blurred silhouettes | tense and quiet |
| `vintage-photo-grain` | `ghost` | Faded vintage photograph of `{term}` | — | Washed-out sepia colours | heavy film grain, light leaks, soft vignette, 1970s snapshot look | (carried by "light leaks" / "soft vignette") | soft vignette | empty scene or one distant blurred figure | unsettling stillness |
| `watercolor-night` | `spiritual` | Watercolor painting of `{term}` | "at night" (inside sentence 1) | Deep blue and violet washes | soft wet edges, loose brushwork | a small warm lantern glow | loose brushwork | figures only as simple distant shapes | calm and mysterious |
| `temple-candlelight` | `spiritual` | Painterly illustration of `{term}` | "lit by candlelight" (inside sentence 1) | Warm amber and deep red tones | drifting incense haze | soft glow and long shadows | close and quiet framing | hands or silhouettes only and faces never shown | reverent |
| `noir-sketch` | `true_crime` | Noir charcoal sketch of `{term}` | — | Monochrome | rough hatching on grey paper | one hard light source, deep black shadows | empty places and objects only | empty places and objects only, no faces, no identifiable people | sober documentary |
| `case-file-illustration` | `true_crime` | Flat 2D editorial illustration of `{term}` | printed like a page in an old police case file | Muted grey-beige palette | visible paper grain | cool neutral light, no warm glow, no lamps lit | thin ink outlines and flat colour fills, no 3D rendering, no photographic detail | places and objects only, no faces, no identifiable people | calm and serious |

Deviations accepted in the v1 texts (fixed by 03.1.1-T1; normalise only when a text is next
revised, §6): `dark-folklore-ink` writes texture before palette; `vintage-photo-grain` has no
standalone light phrase; `watercolor-night` and `temple-candlelight` put the time/light marker
inside the `{term}` sentence; `case-file-illustration` writes light ("cool neutral light") before
texture ("visible paper grain") (from the 2026-09-28 revision, §6; the pre-revision text wrote
composition before light). Those five rows are the only deviations — every other
slot matches the order above, and all 7 templates carry a people rule and end on the mood.

## 2. People rule

- Always present. For `true_crime` templates (G4) it must contain **both** `no faces` and
  `no identifiable people` — the checker fails the row otherwise. Neither phrase is a safety
  negative: it states what the frame shows.
- Fiction templates may only show people as silhouettes, blurred figures or simple distant
  shapes (`vintage-photo-grain` is ghost-only for this reason, README F11/D2).
- Topic rule (D2): a `true_crime` series may use only templates with
  `topicHint = "true_crime"`; `ghost`/`spiritual` series may use any non-true-crime template.

## 3. Limits (README D1)

| Rule | Value |
|---|---|
| `name` | slug `^[a-z0-9]+(-[a-z0-9]+)*$`, 3–40 chars, unique in the file |
| `{term}` | exactly one placeholder |
| length | 60–600 chars after trim |
| line breaks | none (no `\n`, no `\r`) |
| `"` | not allowed (the value is embedded in a JSON string consumed by MPT) |
| `topicHint` | one of `ghost`, `spiritual`, `true_crime` |
| `previewStorageKey` | `null` until 03.1.2-T1 uploads the preview, then `style-previews/<name>.v1.webp` (the only non-null value the checker accepts) |

Commas are safe: MPT receives the keyword as an array element (`video_terms`, plan D4), not as a
comma-joined string.

## 4. Never write safety negatives

The template holds the **style only**. The platform clause is appended by
`ScenePromptBuilder.Build` (D1):

    NegativeClause.V1 = No gore, no blood, no text, no letters, no logos, no watermark, no real person likeness.
    sha256("No gore, no blood, no text, no letters, no logos, no watermark, no real person likeness.")
      = e1ba7fa9b85b7a46fc53e2a3110f1a88439a665df1553ee4e7f4238ddf1979a7   (relay startup check, 03.4.3-T1)

- Do not write `no gore`, `no text` or `watermark`, any other invented negative, or the clause
  itself into a template. The checker fails a row on those three literal fragments
  (`forbidden`); invented negatives that contain none of them pass it and rely on review. A
  duplicated clause also breaks the relay's suffix check (`relay_prompt_unstyled`).
- `Text` = template with `{term}` replaced + `" "` + `NegativeClause.V1` (what OpenAI receives);
  `ModerationText` = the same string without the clause (02.8.2-T2, CR-3).

## 5. How to add a template

1. Pick a slug `name` (3–40 chars) with the topic in mind.
2. Append a row to `seed/style-templates.v1.json`: `name`, `topicHint`, `active: true`,
   `previewStorageKey: null`, `template` per §1.
3. Add `series.style.<name>.label` and `.hint` to `seed/i18n-style-labels.v1.json` in **both**
   `en` and `vi` (label ≤ 32 chars).
4. Run the checker (§8); fix by rule name.
5. 03.1.2-T1 generates and uploads `style-previews/<name>.v1.webp`, then fills
   `previewStorageKey`.
6. 03.3.2-T1 reviews the new style on the contact sheet before any series uses it.
7. Retiring a style: set `active: false` (do not delete the row — existing series keep
   rendering, and the 6-style fallback in 03.1.1-T1 keeps `case-file-illustration` with
   `active: false`).

## 6. How a text change is versioned (F16)

Editing a `template` string changes its SHA-256, therefore
`StylePromptVersion.For(style)` = `style=<name>@<first 8 hex of sha256(template)>;neg=v1` (stored
in `render_jobs.params.style_prompt_version`). So: change the seed file, re-run the checker,
record the change (release note / plan row with the old and new text) and expect new image spend
for that style. Never edit `style_templates.template` by hand in a database.

### Change record

| Date | Template | Old → new | `StylePromptVersion` | Evidence |
|---|---|---|---|---|
| 2026-09-28 | `case-file-illustration` | `Muted editorial illustration of {term}, like a page from an old case file. Flat desaturated colours, paper texture, simple clean shapes, even daylight, places and objects only, no faces, no identifiable people, calm and serious mood.` → `Flat 2D editorial illustration of {term}, printed like a page in an old police case file. Muted grey-beige palette, cool neutral light, no warm glow, no lamps lit, visible paper grain, thin ink outlines and flat colour fills, no 3D rendering, no photographic detail, places and objects only, no faces, no identifiable people, calm and serious mood.` | `style=case-file-illustration@74c968c5` → `style=case-file-illustration@42ca2380` (233 → 348 chars) | Checker `7 templates ok` after the edit; the contact sheet scored the old text 3.31 vs 4.38–4.72 for the other six (`trial/images/REPORT.md`); both AI raters reported realistic 3D detail and warm lamp light. Owner-approved 2026-09-28. The 8 `k09` (`case-file-illustration`) cells in `trial/images/contact-sheet-results.csv` were rendered from the old text; the 2026-09-28 re-run on the new text scores 3.88 vs 3.31 (`trial/images/REPORT.md` §Re-run) |

The `stylePromptVersion` values above are derived (`sha256` of the template, §6), they are not
stored anywhere in the repo.

## 7. M0 handover — global template strings (D13)

For the M0 runs, `openai_image_prompt_template` = the template text (with `{term}` still in
place — MPT replaces it, I3) + `" "` + `NegativeClause.V1`. Handed to 01.2.2-T2 / 05.5.2, which
replace their temporary neutral template:

- vi ghost run — `dark-folklore-ink`:
  `Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted indigo and grey tones, pale moonlight, deep shadows, wide composition, people shown only as small distant silhouettes, quiet and eerie mood. No gore, no blood, no text, no letters, no logos, no watermark, no real person likeness.`
- en ghost run — `foggy-cinematic`:
  `Cinematic film still of {term}, wrapped in thick fog. Cold teal and grey palette, soft 35mm film grain, low-key lighting, shallow depth of field, wide establishing shot, people shown only as blurred silhouettes, tense and quiet mood. No gore, no blood, no text, no letters, no logos, no watermark, no real person likeness.`

## 8. Checks

```sh
uv run --no-project --python 3.12 trial/tools/check_style_templates.py seed/style-templates.v1.json  # 7 templates ok
uv run --no-project --python 3.12 trial/tools/check_style_templates.py --self-test
```

Exit codes: 0 ok / 1 check failed (the rule name is printed) / 2 usage error.

## 9. Owner review

Owner review of the texts (task step 6, 15 min, before 03.3.2-T1 spends money): **approved
2026-09-28** by the owner — all 7 v1 texts unchanged (opened 2026-09-26). The M0 runs use the
strings in §7; the rollback is the temporary neutral template of 01.

**Revision 2026-09-28:** the contact sheet scored `case-file-illustration` 3.31 against 4.38–4.72
for the other six, and both AI raters said the model renders realistic 3D detail with warm lamp
light. The owner approved the rewritten text the same day; the old → new text and both derived
`StylePromptVersion` values are in the §6 change record. The other six texts are unchanged.
