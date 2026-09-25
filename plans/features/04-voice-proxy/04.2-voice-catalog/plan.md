# 04.2 — Voice catalog (curation, catalog file + backend rules, previews)

> Refines: `../../../04-voice-proxy.md` §4 epics **04.2.1** (curate voices), **04.2.2** (catalog in backend), **04.2.3** (preview samples), §6 task rows 04.2.1-T1/04.2.2-T1/04.2.3-T1, §9 exit "voice catalog of 3–4 voices per language with previews"
> Contract: `../README.md` — **D9** (`voices.v1` catalog + rules), **D11** (curation protocol), **D12** (previews), **D8** (health `catalog` object, consumer only), findings **F04**, **F06**, **F07**, **F08**, **F09**, **F14**, **F15**, **F20**; **CR-6**, **CR-9**, **CR-11**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs (file + line) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet. Depends on README §10 owner decisions O2 (English fallback) and O4 (exclude `Adam bựa`) — both carried here as already-decided per the TL recommendation; O2's actual scored outcome is produced by 04.2.1-T1 itself.

## 1. Scope

**Stories** (`04-voice-proxy.md` §4):
- **04.2.1** *Curate voices* — "As the owner I want a short list of good narrators so that users do not face 20+ options." Must · S (detailed 0.5 d, 04.2.1-T1).
- **04.2.2** *Catalog in backend* — "As a creator I want to pick a voice by name and hear it so that I choose well." Must · S (detailed 0.75 d, split content/backend: 04.2.2-T1 XS + 04.2.2-T2 S, F09).
- **04.2.3** *Preview samples* — "As a creator I want 10-second previews so that I do not spend credits to test voices." Should · S (detailed 0.5 d, 04.2.3-T1).

**In scope.**
- Blind listening test over every VieNeu preset in vi and en, selection of 3–4 voices per language against D11 thresholds (04.2.1-T1).
- The content file `seed/voices.v1.json` revision 1 and the shared cross-language fixtures `seed/fixtures/voices/*.json` used by both readers (04.2.2-T1, D9, D14).
- The backend rules and seeder wiring: `VoiceCatalogRules` (Domain), `VoiceCatalogEntry`, the `VoiceSeeder` file-source change (04.2.2-T2, CR-9).
- Preview generation through the running proxy, loudness normalisation, manifest, and upload of the resulting `previewStorageKey`s (04.2.3-T1, D12).

**Out of scope (non-goals).**
- The proxy code that reads the catalog and serves `/v1/audio/voices` — owned by `04.1.1-speech-endpoint/` and `04.1.3-voices-health-packaging/` (`catalog.py` is 04.1.2-T1; the voices route is 04.1.3-T1). This folder only produces and validates the **content**.
- The backend query endpoint `GET /voices` (public shape in D9) — owned by **09.4.2-T1**; referenced here only as a consumer of the seeded rows.
- The web voice picker UI — owned by plan **10** §4 10.4.x via **CR-11** (i18n tag keys, play-button gating). §6 below describes what it shows, not its design.
- VieNeu upstream code, the `voices_v3_turbo.json` source file, and any change to preset names — locked (00 DECISIONS §3, F08: a rename is a catalog migration, owner action).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| V8 | `voices_v3_turbo.json` has 25 presets, default `Hải Đăng`; every name/alias NFC; `gender` ∈ {`male`,`female`}; description = region + style (Bắc/Trung/Nam; tự nhiên/kể chuyện/tin tức/đọc truyện); **no language field** — every preset is a Vietnamese regional speaker; aliases exist (`Anh Khôi → Thiện Minh`, `Minh Quân`/`Minh Quân Pro → Hải Đăng`, `Mạnh Dũng → Quốc Tuấn`) ✅ | README V8 |
| V9 | `resolve_voice_name` matches the preset name or an alias by exact string, no normalisation, no case folding ✅ | README V9 |
| V10 | v3 Turbo README claims "10,000+ hours of bilingual training"; English quality with these presets is **not** stated ⚠️ | README V10 |
| V11 | CPU RTF: fp32 one stream 0.58, two streams 1.19; int8 two streams 0.35/0.67 (upstream measurement) ✅ | README V11 |
| M5 | MPT strips every `-Female`/`-Male` substring anywhere in the name and routes any `-V2`-suffixed name to Azure before the kokoro branch ✅ | README M5 |
| M6 | MPT's API path never reads the voice list (`get_kokoro_voices` is WebUI-only, unused on the node, 01 D14) ✅ | README M6 |
| P9 | `voice_name = "kokoro:<voices.id>"`; 05.5.2 needs a native speaker to rate voice + subtitles ≥ 4/5 for **both** vi and en ✅ — superseded 2026-09-24 by CR-13: the en rating follows O2 (accepted) | README P9 |
| P10 | 10.4.x series form step 3 shows a voice list filtered by language with play buttons for previews ✅ — refined 2026-09-24 by CR-11: tag i18n keys, no play button when `previewUrl` is null, no speed control | README P10 |
| F04 | All 25 presets are Vietnamese regional speakers with no stated English quality; there may be **no** acceptable English narrator | README F04, action: D11 protocol + owner O2 |
| F06 | MPT rewrites voice names broadly (`-Female`/`-Male` anywhere, `-V2` suffix routes to Azure) | README F06 → D9 id rules |
| F07 | VieNeu matches ids byte-exactly, no Unicode normalisation; ids must be canonical NFC preset names, never aliases | README F07 → D9 |
| F08 | `voices.id` is a text PK: one preset → one language only; a VieNeu rename is a catalog migration | README F08 → D9 |
| F09 | Ownership split: this folder owns the **content file**; 09.2.5-T1 owns the seeder plumbing, 09.4.2-T1 owns the query endpoint | README F09 |
| F14 | The proxy's allow-list is **all** catalog ids, active or not; `active=false` only hides a voice from pickers | README F14 → D9 |
| F20 | `Adam bựa` (slang name) is excluded (O4); display names = preset names; descriptions become tags (vi+en i18n) | README F20, O4 |

**Closed here.** Which presets are selected and why (D11 evidence). The exact `voices.v1` revision-1 content and its validation fixtures (D9). The backend-side validation of that content (`VoiceCatalogRules`). Preview file production and upload contract (D12). **Not closed here:** the proxy's own loader/allow-list logic (04.1.2-T1) and the public `GET /voices` route (09.4.2-T1) — both only consume this folder's output.

## 3. Domain and data

### 3.1 Content file `seed/voices.v1.json` (revision 1)

Schema `voices.v1` per README D9 exactly:

```json
{ "schema": "voices.v1", "revision": 1, "vieneuCommit": "<40-hex VIENEU_COMMIT>",
  "voices": [
    { "id": "Thiện Minh", "slug": "thien-minh", "displayName": "Thiện Minh", "language": "vi", "gender": "male",
      "tags": ["storyteller", "deep", "northern"], "active": true, "previewStorageKey": "voice-previews/thien-minh.v1.mp3" } ] }
```

3–4 active entries per language chosen by 04.2.1-T1 (§10); `previewStorageKey` starts `null` for every entry (04.2.2-T1) and is filled in per-entry only after 04.2.3-T1 verifies the upload (HEAD 200) in every env bucket. Revision 0 (the two provisional dev entries, `Hải Đăng`→vi, `Ngọc Huyền`→en) is owned by 04.1.2-T1; revision 1 replaces its content, and any revision-0 id not selected stays as `active: false` (§10 open items).

### 3.2 Shared fixtures (D14)

`seed/fixtures/voices/{valid-rev1.json, invalid-*.json}` — one small `voices.v1` document per D9 rule, read by both readers (proxy `catalog.py` tests, 04.1.2-T1; `VoiceCatalogRulesTests`, 04.2.2-T2). Full list in 04.2.2-T1.

### 3.3 Backend types (04.2.2-T2, Domain `Catalog/`)

| File | Type |
|---|---|
| `Domain/Catalog/VoiceCatalogEntry.cs` | `sealed record VoiceCatalogEntry(string Id, string Slug, string DisplayName, string Language, string Gender, IReadOnlyList<string> Tags, bool Active, string? PreviewStorageKey)` |
| `Domain/Catalog/VoiceCatalogFile.cs` | `sealed record VoiceCatalogFile(string Schema, int Revision, string VieneuCommit, IReadOnlyList<VoiceCatalogEntry> Voices)` |
| `Domain/Catalog/VoiceCatalogRules.cs` | `static ErrorOr<Success> ValidateEntry(VoiceCatalogEntry e)`, `static ErrorOr<Success> ValidateFile(VoiceCatalogFile f)`, `static string Slugify(string id)` |

`voices` table (09.2.2, unchanged here): `id text PK, display_name, language CHECK, gender CHECK female/male/neutral, tags text[], preview_storage_key, active`. `VoiceSeeder` (09.2.5-T1, changed by 04.2.2-T2) upserts by `id` from the file instead of `seed/catalog.dev.json` (CR-9).

### 3.4 Preview manifest (04.2.3-T1, D12)

`worker-node/proxy/previews.manifest.json` (tracked): one row per voice — `id, slug, key, sha256, bytes, duration_s, text_sha256, catalog_revision`.

### 3.5 Migration

None owned here: `voices` table and its columns already exist (09.2.2). This folder only changes file content and the seeder's data source (CR-9).

## 4. Use cases

Content-curation workflows, not application commands:

| Step | Actor / tool | Outcome |
|---|---|---|
| Generate audition samples, blind-rate, select | facilitator + raters, `worker-node/proxy/tools/audition.py` (04.2.1-T1) | `trial/voice-audition/results.csv` + README with the selected ids and scores |
| Write catalog revision 1 + fixtures | BA/Worker, hand-authored JSON validated against D9 by hand + `catalog.py` (04.1.2-T1's loader) at proxy restart (04.2.2-T1) | `seed/voices.v1.json` (rev 1), `seed/fixtures/voices/*` |
| Validate + seed in backend | `VoiceCatalogRules` + `VoiceSeeder` (04.2.2-T2) | `voices` rows match the file; invalid file → seeder refuses to start (Development) or migration fails (other envs) |
| Generate + upload previews | operator, `worker-node/proxy/tools/make_previews.py` (04.2.3-T1) | one static MP3 per voice in `nt-public-<env>`, `previewStorageKey` set, `revision` bumped |

## 5. API and events

No new routes owned by this folder.
- `GET /v1/audio/voices` (proxy, D2) and `GET /voices` (backend, 09.4.2-T1) both **read** the file/table this folder produces; their shapes are fixed by D9 and are not redefined here.
- `GET /health` `catalog` object (D8: `ok, revision, voices, missing_upstream`) is populated by 04.1.4-T1 from the same file; this folder is responsible only for the file being valid and every id existing upstream (checked in 04.2.2-T1's smoke step).
- No domain events.

## 6. UI

Owned by plan 10 §4 10.4.x (series form step 3, voice picker) via **CR-11**. What it shows, using this folder's data:
- One row per **active** voice returned by `GET /voices`, filtered by the series language; tags rendered from i18n keys `voices.tag.<tag>` (vi + en) — never the raw tag string.
- A play button only when `previewUrl` is non-null (i.e. 04.2.3-T1 has uploaded that voice's preview); no speed/rate control (D10, F18).
- No design work happens here; any UI change request goes through CR-11, not this plan.

## 7. Jobs and integrations

No background jobs are owned by this folder. Integration points (file/data handed to other owners):

| Consumer | Reads | Timing |
|---|---|---|
| Proxy `catalog.py` loader (04.1.2-T1) | `seed/voices.v1.json` via `VOICE_CATALOG_FILE` | at proxy startup; reload = restart (D9) |
| `VoiceSeeder` (09.2.5-T1, changed 04.2.2-T2) | same file | at migration/seed time, every environment (revision 0 refused outside Development) |
| Object storage (11.4.1, S3-compatible) | preview MP3s pushed by 04.2.3-T1 | one-off per revision bump, operator-run |
| Adapter/backend health (D8) | `catalog` object built from the same file by 04.1.4-T1 | polled per CATALOG_CHECK_INTERVAL_S (300 s) |

## 8. Security and privacy

| Threat | Control |
|---|---|
| VieNeu key or proxy key leaking through audition/preview tooling | `audition.py` and `make_previews.py` read keys via `secret nts.vieneu.api_key` / `nts.proxy.api_key` into environment/curl `-H @<(...)`, never argv, never logged |
| Preview audio identifiable to a real person | preview texts are fixed, mood-only sentences with no names of real people (D12); voices are synthetic VieNeu presets, not cloned humans |
| Rater PII in `results.csv` | rater sheet stores rater initials only, no contact info; raw audio stays under `$NTS_HOME/runs/` (not tracked), never committed |
| Public preview bucket serving unintended content | uploads go only to `voice-previews/<slug>.v<n>.mp3` in `nt-public-<env>` (11 §4 11.4.1), immutable filenames per revision (D12), verified by HEAD before the catalog is updated |
| Invalid/malicious catalog content reaching production | `VoiceCatalogRules.ValidateFile` rejects the whole file on any violation (D9); seeder refuses `revision=0` outside Development |

## 9. Test plan

- **Fixtures (04.2.2-T1):** one `invalid-<code>.json` per D9 rule + `valid-rev1.json`, consumed by both the proxy's existing catalog tests (04.1.2-T1, unchanged here) and `VoiceCatalogRulesTests` (04.2.2-T2).
- **Backend unit (04.2.2-T2):** `VoiceCatalogRulesTests` data-driven over every fixture; `Slugify_HaiDang_ReturnsHaiDang`; seeder integration tests with `PostgresFixture` (TestKit).
- **Smoke (04.2.2-T1):** proxy restart on revision 1 + `smoke-proxy.sh` + `GET /health` `catalog.ok = true`, `missing_upstream = []`.
- **Preview tests (04.2.3-T1):** `make_previews.py --generate` output ≤ 12 s / ≤ 200 KB per file; manifest schema; `--upload` HEAD-verify step (mocked transport in unit tests, real bucket in the operator run).
- No tests are owned for the audition selection itself (04.2.1-T1) beyond recording the evidence artefacts — it is a human rating process, not code.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 04.2.1-T1 | Audition samples + blind listening test + selection | BA/Worker | S (0.5) | 01.2.1-T3 (voice snapshot), 01.2.1-T4 (RTF) | 04.2.2-T1 |
| 04.2.2-T1 | Catalog revision 1 + shared fixtures | BA/Worker | XS (0.25) | 04.2.1-T1, 04.1.2-T1 (loader, revision 0) | 04.2.3-T1, 04.2.2-T2, 05.5.2; 01.2.2-T6 soft (CR-6) |
| 04.2.2-T2 | `VoiceCatalogRules` + `VoiceSeeder` file source | BE | S (0.5) | 04.2.2-T1, 09.2.2-T1 (`Voice` entity), 09.2.5-T1 (`VoiceSeeder`) | 09.4.2-T1; 09.4.1-T1 soft |
| 04.2.3-T1 | Preview generation, loudness, manifest, upload | Worker | S (0.5) | 04.2.2-T1, 04.1.5-T1 (proxy running), 11.4.1-T1 (upload only; 11 CR-13) | 09.4.2-T1 `previewUrl`, 10.4.2-T2 (soft) (10 CR-7) |

Total **1.75 d** (README §8/§9: matches "04.2 voice catalog: high-level 1.5 → detailed 1.75, backend rules + seeder change split out, F09; English rater, F04").

### Open items for the TL

- [OPEN] README O2 (English fallback path) is only resolved by 04.2.1-T1's actual scores; this plan cannot pre-select which of (a)/(b)/(c) applies. 04.2.1-T1's Steps record the decision inline once scores exist, per the TL's stated rule ("(a) if best en mean naturalness ≥ 3.0, otherwise (b)").
- [OPEN] D12's upload tool/CLI is not fixed (11.4.1 ⚠️ still open). 04.2.3-T1 describes the upload as a contract (PUT/HEAD, headers) rather than naming a CLI, per the assignment; the concrete command is filled in when 11.4.1 lands.
- [DECISION carried] O4 (exclude `Adam bựa`) and the D9 id/slug/tag rules are treated as already accepted for this plan's content, consistent with the TL's own recommendation in README §10 — flagged here in case the owner answers differently at DoR.
- [RULED — README §10 T6] O2 (b) does not conflict with D9: in phase P only the owner creates series, so "postpone the en channel" means the owner creates no en series. Revision 1 keeps the best-rated en voice active; the D9 rule stays.
- [RULED — README §10 T7] Python `str` semantics are normative for the id rule; C# checks characters explicitly with `Rune` (04.2.2-T2). README D9 now states the rule without relying on regex semantics.
- [RULED — README §10 T8] Unknown properties are rejected at file and entry level (`catalog_invalid`); fixture `invalid-unknown-property.json` added in 04.2.2-T1.
- [DECISION] One catalog file serves every environment (D9), so 04.2.3-T1 writes a `previewStorageKey` only after the object is verified in **every** `nt-public-<env>` bucket of the run (D12 says "the upload is verified", singular).
- [DECISION] Revision-0 ids not selected by 04.2.1-T1 stay in revision 1 with `active: false` (09.2.5-T1 never deletes rows; deactivation is file-driven), rather than disappearing as D9's "replaces it" could be read.
- [INFO] Fixture set extends D14's `{valid-rev1,invalid-*}` with `valid-boundaries.json` and `valid-rev0-no-active-en.json`, plus `expected.json` (machine-readable file → code map read by both suites). New proxy test files outside the pinned prefix list: `test_catalog_fixtures.py` (fits `test_catalog_*`), `test_audition_tool.py`, `test_previews_tool.py`.
- [INFO] 04.2.2-T1 steps 6–8 (proxy restart, `smoke-proxy.sh`, health `catalog.ok`) need 04.1.5-T1 and 04.1.4-T1, which are not in its `Depends on` cell (README §5 has only `SE → C1`); treated as soft dependencies — steps 1–5 run with 04.1.2-T1 alone.
- [INFO] 04.2.2-T2 widens the pinned 09.2.5 constructor to `VoiceSeeder(IAppDbContext, IClock, IOptions<SeedOptions>, IHostEnvironment)` and adds `Seed:VoiceCatalogFile`; covered by CR-9, to be tagged `(04 CR-9)` in 09.2.5-T1 when CRs are applied.

## 11. Rollout

- **Order:** 04.2.1-T1 (human time, parallel to M0 proxy work) → 04.2.2-T1 → 04.2.3-T1 generation (M0, local) → [09 wave 1 lands] → 04.2.2-T2 → 04.2.3-T1 upload (M1, needs 11.4.1) → 04-M1 gate.
- **Flag:** none — the catalog is static content; a bad revision is caught at load time (proxy exit 2, seeder refusal), not gated by a runtime flag.
- **Config:** `VOICE_CATALOG_FILE` (D13, already defined) points at `seed/voices.v1.json`; `Seed:VoiceCatalogFile` (backend config, 04.2.2-T2) points at the same path.
- **Monitoring:** none new; existing health `catalog` fields (D8) and the seeder's startup log are the only signals.
- **Rollback:** revert `seed/voices.v1.json` to the previous revision and restart the proxy / re-run the seeder; previews are additive (`v<n>` never overwrites), so no storage rollback is needed.
