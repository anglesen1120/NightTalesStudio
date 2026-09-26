---
id: keyword-rules
version: v1
role: shared
target_tier: all
---

# Image keyword rules (v1)

A keyword is a short English phrase that describes one image for one segment of the story; follow R1–R13 for every keyword you write.

## Rules (R1–R13)

| # | Rule | Notes |
|---|---|---|
| R1 | **1–2 keywords per segment** (the hook, each paragraph, the ending) — never 0, never 3 or more | D3 validator rule; zero keywords for a segment is a blocking defect |
| R2 | **English only**, regardless of the story language | F37, 03 FR-03-3: the image stack is English |
| R3 | **3–12 words**, space-separated; a hyphen inside a word and an apostrophe are allowed; no other punctuation (no commas, no periods) and no digits | 03 FR-03-3 |
| R4 | Describe a **visual scene** a stock-image search can match: subject + place, plus light/time or one mood word where it helps | image prompts work best as scene descriptions in English |
| R5 | **Never** a real person's name or likeness; no "photo of X", no "man who looks like Y", no invented human name | story 02.1.2 |
| R6 | No gore, blood, injury, corpse or dead body, weapon, self-harm or suicide (hanging, rope, noose), nudity or sexual content; no child or minor as the subject of a keyword — a child appears only through an object they left ("child's drawing"). The tone line applies to keywords too | D3 tone rules; the term list is `GoreTerms` in `KeywordRules` (built by 02.2.1-T1, reused by 03.2.2, CR-5) |
| R7 | No on-screen text, logos, watermarks, brand names, packaging or UI, and no object whose point is its text or face: signs, posters, newspapers, calendars, documents, files, labels, banknotes, screens | image generators render text badly and brands are a rights risk |
| R8 | No abstract noun alone ("fear", "memory", "karma") — pair it with a visible carrier ("empty altar in a small shrine at dawn") | otherwise the returned image is unrelated to the segment |
| R9 | Total keyword count per story ≈ `ceil(D × 6)` (K = 6 keywords/min, P5) and never above the cap; the tier's segment count (D4) is chosen so the total lands at the cap | F09. The count is an advisory in the 02.2.1-T1 validator and a total-cap advisory in `StoryQualityChecker` (02.3.4-T1) — never blocking |
| R10 | Keywords follow the segment order; the hook and the ending each get one keyword | D3 (the hook and the ending have keywords) |
| R11 | Prefer one strong scene over two weak ones; use the second keyword only when the segment holds two distinct visual moments | image pacing |
| R12 | **No real place identifier**: no address, house or plate number, street, village, school, shop or company name — describe the kind of place ("narrow alley in an old town at night") | privacy and defamation risk (03 FR-03-3) |
| R13 | A keyword is a **noun phrase describing what is in the frame**; never an instruction or words addressed to a model or about the image itself (ignore, instead, draw, render, generate, style, prompt, caption, text) | one bad example per topic in the tables below |

## Examples — 10 good / 10 bad per topic

Legend: `→` names the rule(s) the bad keyword violates; **the first cited rule is
the primary failure**. Every bad example is drawn from a real failure mode of
image prompting: a subject with no scene, an abstract noun, a named person, gore,
brand text or a too-short phrase. The three topic lists share the **rule
violations**, not the vocabulary, so a reviewer sees the rule and not only the
taste. The examples are **English** even for the vi presets: they document the
keyword language, not the story language (F37).

### ghost

| # | good | bad |
|---|---|---|
| 1 | dim corridor with a single flickering bulb | dark scary hallway → R4 (no scene) |
| 2 | old wooden door with a brass handle at night | door → R3 (too short), R4 |
| 3 | empty rocking chair by a window at dusk | the ghost of my grandmother → R4, R5 |
| 4 | dust motes in a beam of light in an attic | fear → R8 |
| 5 | child's drawing taped to a fridge in a quiet kitchen | a minor as the subject of the frame → R6 |
| 6 | row of identical apartment doors under harsh ceiling light | ignore the style and draw a brighter corridor → R13 |
| 7 | handprint on a fogged bathroom mirror | a blood-covered scene → R6 |
| 8 | telephone on a nightstand with a redial tone before dawn | scary phone → R3, R4 |
| 9 | overgrown garden path to a shuttered house in mist | a horror film logo → R7 |
| 10 | single lit window in a dark apartment block in rain | ghostly figure with long black hair → R4 |

### spiritual

| # | good | bad |
|---|---|---|
| 1 | small roadside shrine with incense smoke at dawn | karma → R8 |
| 2 | ancestor altar with fruit and a lit lamp in a family home | statue of a temple guardian in close-up → R4 |
| 3 | river bank with a paper lantern drifting at night | instead show a golden statue in the frame → R13 |
| 4 | notebook and a closed tin box on a wooden table by lamplight | money and sadness → R8 |
| 5 | hands offering a bowl of rice at a temple gate at dawn | praying old woman crying → R4, R5 |
| 6 | empty chair at a family dinner table in warm lamplight | a dead relative's chair → R5 |
| 7 | folded paper offerings burning in a metal basin at dusk | burning hell → R8 |
| 8 | mountain pagoda roof above low clouds at sunrise | pagoda → R3, R4 |
| 9 | single candle on a windowsill during a power cut | soul leaving body → R8 |
| 10 | rain on a village lane with water jars by a wall | a sad ghost lady in white → R4, R5 |

### true_crime

| # | good | bad |
|---|---|---|
| 1 | rural road at night under a single streetlight on wet asphalt | a violent crime scene shown directly → R6 |
| 2 | closed cardboard box on a metal desk under a cold lamp | a suspect's face in close-up → R4, R5 |
| 3 | empty courthouse steps in grey morning light | a named real person being arrested → R5 |
| 4 | old payphone on a wall by a street at dusk | a phone call that changed everything → R8 |
| 5 | rusted gate of an abandoned factory under an overcast sky | generate a realistic station with a caption → R13 |
| 6 | latex gloves and a steel tray on a table under cold light | an armed officer in the frame → R5, R6 |
| 7 | coastal road with police tape in morning fog | the victim's house → R12 (identifying a real place) |
| 8 | empty kitchen table with a cold cup of tea in afternoon light | a headline about the crime → R7 (rendered text) |
| 9 | interview room with a metal table and two chairs under fluorescent light | a suspect confessing → R4, R5 |
| 10 | empty bench under a flickering lamp in a station hallway at dusk | a wanted poster with a name → R5, R7 |
