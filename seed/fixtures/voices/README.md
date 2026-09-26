# Voice catalog shared fixtures (D14)

One file per D9 rule, shared by the Python loader (`nts_vieneu_proxy.catalog`,
`worker-node/proxy/tests/test_catalog_fixtures.py`) and the C#
`VoiceCatalogRules` suite (04.2.2-T2). Neither suite hard-codes this table:
both read `expected.json` (`{ "<file>": { "code": <code|null>,
"voiceId": <id|null> } }`).

Base document: `revision 1`, `vieneuCommit
"c1390abbdb2eedcdf58eafb546966c06ce27af71"`, entries `Hải Đăng` (vi, male,
`["storyteller","northern"]`) and `Ngọc Huyền` (en, female, `["calm"]`), both
active, `previewStorageKey null`. Each `invalid-*` file is the base plus
exactly one deliberate violation. Where an invalid id also changes the derived
slug, the id error wins by the D9 check order (id → slug → other fields), so
both readers report `voice_id_invalid`.

| File | Violation | Expected code | `voice_id` |
|---|---|---|---|
| `valid-rev1.json` | none | — | — |
| `valid-boundaries.json` | none: 64-char id with `.` and `-`, id `"Giọng²"` (a Unicode `No` digit, Python `\w`), 40-char displayName, 4 tags, `gender neutral`, key `.v12.mp3` | — | — |
| `valid-rev0-no-active-en.json` | none: `revision 0`, en inactive (the ≥ 1 active rule applies from revision 1) | — | — |
| `invalid-voice-id-nfd.json` | id `"Ha\u0309i \u0110a\u0306ng"` (NFD, JSON escapes so editors cannot normalise it) | `voice_id_invalid` | that id |
| `invalid-voice-id-whitespace.json` | id `"Hải Đăng "` (slug still `hai-dang`) | `voice_id_invalid` | that id |
| `invalid-voice-id-pattern.json` | id `"_Hải"` (leading `_`) | `voice_id_invalid` | that id |
| `invalid-voice-id-too-long.json` | 65-char id | `voice_id_invalid` | that id |
| `invalid-voice-id-female-substring.json` | id `"Ngọc-Female Huyền"` | `voice_id_invalid` | that id |
| `invalid-voice-id-connector-punct.json` | id `"Hải‿Đăng"` (U+203F, `Pc`: .NET `\w` accepts it, Python `\w` does not — engine trap) | `voice_id_invalid` | that id |
| `invalid-voice-id-male-substring.json` | id `"Hải-Male Đăng"` | `voice_id_invalid` | that id |
| `invalid-voice-id-colon.json` | id `"Hải:Đăng"` | `voice_id_invalid` | that id |
| `invalid-voice-id-v2-suffix.json` | id `"Hải Đăng-V2"` | `voice_id_invalid` | that id |
| `invalid-slug-mismatch.json` | slug `"hai-dang-1"` | `voice_slug_mismatch` | `Hải Đăng` |
| `invalid-display-name-empty.json` | displayName `"   "` | `catalog_invalid` | `Hải Đăng` |
| `invalid-display-name-too-long.json` | 41 chars | `catalog_invalid` | `Hải Đăng` |
| `invalid-language.json` | language `"fr"` | `catalog_invalid` | `Hải Đăng` |
| `invalid-gender.json` | gender `"other"` | `catalog_invalid` | `Hải Đăng` |
| `invalid-tag-unknown.json` | tag `"spooky"` | `catalog_invalid` | `Hải Đăng` |
| `invalid-tag-duplicate.json` | `["calm","calm"]` | `catalog_invalid` | `Ngọc Huyền` |
| `invalid-tag-too-many.json` | 5 tags | `catalog_invalid` | `Hải Đăng` |
| `invalid-preview-key.json` | `"voice-previews/other-slug.v1.mp3"` | `catalog_invalid` | `Hải Đăng` |
| `invalid-preview-key-version-zero.json` | `"voice-previews/hai-dang.v0.mp3"` | `catalog_invalid` | `Hải Đăng` |
| `invalid-duplicate-id.json` | `Hải Đăng` twice (second `language en`; necessarily also a duplicate slug — same code either way) | `catalog_invalid` | `Hải Đăng` |
| `invalid-duplicate-slug.json` | extra id `"Hai Dang"` → slug `hai-dang` | `catalog_invalid` | `Hai Dang` |
| `invalid-no-active-en.json` | en entry `active false` at revision 1 | `catalog_invalid` | `null` |
| `invalid-schema.json` | `schema "voices.v2"` | `catalog_invalid` | `null` |
| `invalid-revision-negative.json` | `revision -1` | `catalog_invalid` | `null` |
| `invalid-vieneu-commit.json` | `vieneuCommit "c1390abb"` | `catalog_invalid` | `null` |
| `invalid-unknown-property.json` | extra entry key `"order": 1` (unknown properties are rejected at file and entry level) | `catalog_invalid` | `Hải Đăng` |
| `invalid-not-json.json` | truncated JSON | `catalog_invalid` | `null` |

Fixtures are synthetic and independent of the curated `seed/voices.v1.json`;
the seed file itself is covered by
`test_catalog_seed_file_is_valid_revision_ge_1`.
