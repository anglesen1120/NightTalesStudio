# 05.3 — Music (build check, licensed catalog, storage sync, music_tracks and selector)

> Refines: `../../../05-video-assembly-subtitles.md` §4 epic 05.3 (stories **05.3.1**, **05.3.2**), FR-05-2, F4
> Contract: `../README.md` — **C17**, **C18**, **L2**, **P1**, **P5**, **D1**, **D3** (bgm fields), **D8**, **D11** (music), **F13**, **F14**, **O2**, **CR-2**, **CR-8**, **CR-12**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.** 05.3.1 *Remove bundled music* — "As the Tech Lead I want a build check that no unlicensed audio can reach a render so that a published video never carries music we cannot licence." Must · XS (**0.25 d**). 05.3.2 *Licensed music catalog* — "As the platform I want a small licensed music catalog with per-track gain, stored on every node and picked automatically per episode so that every video has music we can legally publish and that is recorded per render." Must · M → **M (2.0 d)** over three tasks.

**In scope.** `worker-node/bin/check-music.sh` (05.3.1-T1); 20–30 curated tracks with licence evidence, loudness and gain, `seed/music-tracks.v1.json` revision 1, `LICENSES.csv` rows, `worker-node/music/tools/check_catalog.py` (05.3.2-T1); `upload_music.py` + `worker-node/bin/sync-music.sh` (05.3.2-T2); `music_tracks` + `AddMusicTracks`, Domain `MusicTrack`/`MusicTrackRules`/`MusicSelector`, `MusicTrackSeeder`, `IRecentMusicQuery` (05.3.2-T3); the `bgm_type`/`bgm_file`/`bgm_volume` values of `mpt-submit.v1` (D3) and the `music` object of `render-params.v1` (D11) as consumed by 05.1.2-T1.

**Out of scope.** Forking MPT; music generation (`sonilo`, `elevenlabs`, C18 ✅); MPT's mix code (C17); music UI and per-series mood override (§6, §10); platform-scope enforcement (07) and the description attribution writer (06.2.1-T1, CR-8); the adapter, mock-mpt and provisioning code (11.3.1-T1, 11.3.5-T1, 11.1.2-T1) — this folder writes their contract and input files only; `render_jobs` itself (09.6.1) and `assets` storage (09.6.3).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| M1 | Music bed × `bgm_volume`, 3 s fade-out, looped to the video length; **no loudness normalisation**; a music mix failure keeps the video without music (warning) | C17 ✅ |
| M2 | `bgm_type = ""` or `bgm_volume ≤ 0` → no music; `bgm_file` resolves in `storage/bgm` then `resource/songs`; `bgm_type = "random"` (the schema default) picks any file in both dirs | C18 ✅ |
| M3 | YouTube Audio Library music is copyright-safe **on YouTube**, not claimed through Content ID, monetisable in YPP; CC tracks need description credit; "Attribution not required" = standard licence; off-platform use not covered [INFERENCE] | L2 ✅ |
| M4 | 01.2.2-T3 `strip-bundled-music.sh [--check] [--mpt-root DIR]` keeps only files whose sha256 is in `worker-node/music/LICENSES.csv`; `install-music.sh [--mpt-root DIR] [--source DIR]` copies CSV rows into `resource/songs`; "05.3.2 extends the file without renaming columns" | P1 ✅, 01.2.2-T3 ✅ |
| M5 | `music_tracks` is owned by plan **05**; `render_jobs` by 09.6.1; `worker_nodes` by `WorkerNodeSeeder`/11 (the former 09.10.1 admin platform was dropped by DEC 10.14) | P5 ✅ |
| M6 | D8 rules: id pattern (= file stem), licence enum, attribution iff `CC-BY-4.0`, scope↔licence, six-mood vocabulary (1–3), `durationSec ≥ 60`, extension, gain formula, topic → moods, `MusicBedLufs = −34` [target] | D8 ✅ |
| M7 | Selector: active ∧ moods ∩ topic moods ≠ ∅ ∧ scope allowed; drop the series' last `Music:AvoidRecent` (3) ids, keep them if that empties the set; order by `id` ordinal; stable index from `SHA256(episodeId)`; none → `null` → no music, hint `music_none` | D8 ✅ |
| M8 | D3: `bgm_type = "custom"` + `bgm_file = <file>` + `bgm_volume = <track bgmVolume>`, or `""` / `""` / `0`; **never `random`** | D3 ✅, F13 ✅ |
| M9 | `render-params.v1` `music` object = `{ "track_id": …, "bgm_volume": … }`, `null` when the selector returns `null` | D11 ✅ |
| M10 | Health `assets = { ok, font, music_revision, music_missing: [] }`, `reasons` ⊇ `music_missing`; `music_none` is an advisory `render-quality.v1` hint, never a failure. D5 has **no** music outcome code: music can never fail a render (M1) | D4 ✅, D10 ✅, D5 ✅ |
| M11 | Object key `music/<file>` in the private assets bucket of 11.4.1 (name ⚠️); sync uses a read-only credential scoped to `music/` (11.3.2 ⚠️), checks sha256, then install + strip check; M0 copies by hand to `$NTS_HOME/music/` | D8 ✅ |
| M12 | Audio never enters the repo (`.gitignore` audio rules, 01.1.2-T1 F16); a CC0 placeholder row exists for M0; 05.3.1-T1 wraps `strip-bundled-music.sh --check` and must not fork the logic | 01.2.2-T3 ✅, CR-7 ✅ |
| M13 | CR-2 columns for `music_tracks` (§3.3); CR-8: the description gets the `CC-BY-4.0` attribution of the render's track, a `youtube`-scope track blocks publication to any other platform (07); CR-12: 09.2.2 §1 gets a pointer that `music_tracks` is created by 05.3.2-T3 | CR-2 ✅, CR-8 ✅, CR-12 ✅ |
| M14 | 05-M0 requires `check-music.sh` exit 0, catalog revision 1 with 20–30 tracks and licence evidence, `check_catalog.py` exit 0; 05-M1 requires `music_tracks` seeded + `MusicSelector` used and `sync-music.sh` installing the catalog on the node | README §7 ✅ |

Closed here: which music a video used (F13 ✅ → recorded per render); why tracks of different loudness played at different levels (F14 ✅ → per-track gain); which files a node may keep (M4/M12); where the catalog lives for the backend (M5/M9). ⚠️ Not verified here: bucket name and credential scope (11.4.1/11.3.2); Content ID claims on CC0 (O2); `MusicBedLufs = −34` (target, tuned in 05.5.2-T1); whether `music_tracks` needs an explicit exemption from the 09.2.3 tenant filter.

## 3. Domain and data

### 3.1 `seed/music-tracks.v1.json` (tracked; owner 05.3.2-T1)

```json
{ "schema": "music-tracks.v1", "revision": 1,
  "tracks": [ { "id": "dark-ambient-01", "file": "dark-ambient-01.mp3", "title": "…", "artist": "…",
                "sourceUrl": "https://…", "license": "YouTube-Audio-Library", "licenseUrl": "https://…",
                "attribution": null, "platformScope": "youtube", "moods": ["dark_ambient", "suspense"],
                "durationSec": 184.2, "loudnessLufs": -17.3, "bgmVolume": 0.146,
                "sha256": "<64 hex>", "active": true } ] }
```

Rules (D8 ✅, M6): `id` = `file` stem, `^[a-z0-9]+(-[a-z0-9]+)*$`, ≤ 40 chars; `license` ∈ {`CC0-1.0`, `CC-BY-4.0`, `YouTube-Audio-Library`}; `attribution` non-empty iff `CC-BY-4.0`; `platformScope` `youtube` for `YouTube-Audio-Library`, `any` otherwise; `moods` ⊆ {`dark_ambient`, `suspense`, `temple`, `meditation`, `noir`, `calm`}, 1–3 entries; `durationSec` ≥ 60; extension ∈ {`.mp3`, `.m4a`}; `bgmVolume = round(clamp(10^((MusicBedLufs − loudnessLufs) ÷ 20), 0.03, 0.40), 3)`, `MusicBedLufs = −34` [target]; topic → moods: `ghost` → {`dark_ambient`, `suspense`}, `spiritual` → {`temple`, `meditation`, `calm`}, `true_crime` → {`noir`, `suspense`}. `revision` only increases; a node reports it as `assets.music_revision` (D4) and it lands in `music_tracks.catalog_revision`.

### 3.2 `worker-node/music/LICENSES.csv` (tracked; 01.2.2-T3 header, extended by 05.3.2-T1)

Header byte-identical to 01.2.2-T3 (M4 ✅): `file,title,artist,source_url,license,license_url,attribution,mood_tags,sha256,duration_s,loudness_lufs,added_at`; four columns **appended** (CR-5 ✅): `track_id,platform_scope,evidence_url,retrieved_at`. `track_id` empty only for the placeholder row; `evidence_url` = the page the licence was verified on; `retrieved_at` ISO-8601 UTC; `mood_tags` (`;`-separated) must equal the JSON `moods` of the same `track_id`. Audio is never committed (M12 ✅); evidence files live in `$NTS_HOME/music-evidence/<track_id>/`.

### 3.3 Table `music_tracks` (owner 05.3.2-T3; global catalog, **not** tenant-owned)

| Column | Type | Rule |
|---|---|---|
| `id` | `uuid` PK | UUIDv7 (`Guid.CreateVersion7()`) |
| `track_id` | `text` UNIQUE | D8 id rule (= `file_name` stem, regex, ≤ 40) |
| `file_name` | `text` | `.mp3` / `.m4a` |
| `title`, `artist`, `source_url` | `text` | non-null |
| `license` | `text` | ∈ {`CC0-1.0`, `CC-BY-4.0`, `YouTube-Audio-Library`} |
| `attribution` | `text` NULL | non-empty iff `CC-BY-4.0` |
| `platform_scope` | `text` | ∈ {`any`, `youtube`}; `youtube` iff `YouTube-Audio-Library` |
| `moods` | `text[]` | ⊆ the six moods, 1–3 entries |
| `duration_sec` | `numeric(7,1)` | ≥ 60 |
| `loudness_lufs` | `numeric(5,2)` | measured integrated, two decimals (§10 T85) |
| `bgm_volume` | `numeric(4,3)` | 0.03–0.40, equals the D8 formula at 3 dp |
| `sha256` | `char(64)` | lowercase hex |
| `storage_key` | `text` | `music/<file>` |
| `catalog_revision` | `int` | the file's revision |
| `active` | `bool` | default true |
| `created_at`, `updated_at` | `timestamptz` | default `now()` |

Each rule is a CHECK constraint; the full DDL is in 05.3.2-T3. No `user_id`, no tenant filter — the catalog is global and identical for every tenant; ⚠️ verify against the 09.2.3 tenant-filter conventions (§10). EF configuration in Infrastructure (`MusicTrackConfiguration`), not Domain (Domain has no EF reference). Migration **`AddMusicTracks`**, in the 09.2.2 bundle so the CI migration check (09.2.2-T2) covers it.

### 3.4 Domain types (owner 05.3.2-T3; file-scoped namespaces, sealed records, no EF)

`MusicTrack(Id, TrackId, FileName, Title, Artist, SourceUrl, License, Attribution?, PlatformScope, Moods, DurationSec, LoudnessLufs, BgmVolume, Sha256, StorageKey, CatalogRevision, Active)` · `enum MusicLicense { Cc0, CcBy, YouTubeAudioLibrary }` · `enum MusicPlatformScope { Any, YouTube }` · `MusicTrackRules.Validate(MusicTrackCandidate) → ErrorOr<Success>` (the D8 rules, same codes as `check_catalog.py`) · `MusicTrackRules.ComputeBgmVolume(decimal loudnessLufs, decimal musicBedLufs = -34m) → ErrorOr<decimal>` · `MusicSelectionInput(SeriesTopic Topic, IReadOnlyList<string> RecentTrackIds, Guid EpisodeId)` · `MusicChoice(string TrackId, string File, decimal BgmVolume)` · `MusicSelector.Pick(MusicSelectionInput, IReadOnlyList<MusicTrack> active, int avoidRecent = 3) → MusicChoice?`. Full signatures in 05.3.2-T3.

`Pick` is pure: one `SHA256.HashData` over the 16 bytes of `EpisodeId`, `BinaryPrimitives.ReadUInt32BigEndian(hash[..4])`, candidates ordered by `TrackId` with `StringComparer.Ordinal`, modulo index (M7 ✅); the topic comes from the series (`SeriesTopic`, 02). The recent ids come through the port `IRecentMusicQuery.GetRecentTrackIdsAsync(Guid seriesId, int take, CancellationToken ct)` (`Application`), implemented once 09.6.1's `render_jobs` exists (last 3 `params->'music'->>'track_id'` of the series by `created_at desc`); until then `NullRecentMusicQuery` returns an empty list and `MusicSelector` is unit-tested with an explicit list.

### 3.5 `seed/fixtures/music/`

`valid-catalog.json` plus one `invalid-*.json` per broken D8 rule (id, licence, attribution, scope, moods, duration, sha256, duplicate file, revision) and `expected.json` mapping each fixture to its error code. Read by `check_catalog.py --self-test` and by `MusicTrackRulesTests` (xUnit theory over the directory) — one source of truth, no re-typed expectations; C# reads them with `JsonUnmappedMemberHandling.Disallow` like the D14 fixtures.

## 4. Use cases

| # | Actor | Flow |
|---|---|---|
| UC1 | Provisioning (11.3.1-T1) | `check-music.sh --mpt-root $MPT_ROOT` → 0: no unlicensed audio, every active catalog file present with the right hash, catalog self-consistent; non-zero blocks provisioning |
| UC2 | Curator (BA, 05.3.2-T1) | Source 20–30 tracks per O2, verify each licence on its page, save evidence, measure sha256/duration/loudness, compute gain, write JSON + CSV rows, run `check_catalog.py`, install on the Mac, run `check-music.sh` |
| UC3 | Operator + node (05.3.2-T2) | Upload `$NTS_HOME/music/<file>` → `music/<file>`; node `sync-music.sh` downloads missing/changed, verifies sha256, installs, strip check; idempotent, at provisioning and on a revision change |
| UC4 | Backend (05.3.2-T3 + 05.1.2-T1) | On submit: load active tracks, read recent ids via `IRecentMusicQuery`, `MusicSelector.Pick`, store the `music` object or `null` in `render-params.v1`; 05.1.2-T1 maps it to the D3 bgm keys |
| UC5 | Publication, ops, catalog change | A `CC-BY-4.0` `attribution` is appended to the description, a `youtube`-scope track blocks publication to any other platform (07); a revision bump re-seeds and re-syncs, and `assets.music_revision` must equal the JSON revision |

## 5. API and events

No HTTP route is owned here. The music surface is three contracts: (1) **`mpt-submit.v1`** (D3 ✅, M8) — `bgm_type` ∈ {`"custom"`, `""`}, `bgm_file` = catalog basename or `""`, `bgm_volume` = track `bgmVolume` or `0`; `random` is never sent (F13 ✅) and only the basename goes to MPT (C18 ✅); (2) **`render-params.v1`** `music` (D11 ✅, M9) — `{ track_id, bgm_volume, attribution }` or `null`; per README §10 T9 the catalog `attribution` is frozen into the params at render time and 06.2.1-T1 reads it from there, never from the live catalog (06 CR-5); (3) **`render-node-health.v1.assets`** (D4 ✅, M10) — `music_revision` = installed revision, `music_missing` = ids absent or with a wrong sha256; non-empty → `assets.ok = false`, `reasons += music_missing` → 503 and the dispatcher skips the node. Storage (D8 ✅, M11): key `music/<file>`, private bucket of 11.4.1 (⚠️), `sha256` metadata on upload.

## 6. UI

None in P. Selection is automatic per episode (D8/M7): no picker, no playlist editor, no per-series override. The only user-visible surfaces are downstream — the `CC-BY-4.0` attribution in the publication description (06.2.1-T1, CR-8) and the advisory `music_none` hint in the video review (10.6.2, CR-9). A series-level mood override would need a new field plus UI in 10: recorded in §10, not designed here.

## 7. Jobs and integrations

| Consumer | Uses | Note |
|---|---|---|
| 11.3.1-T1 provisioning | `check-music.sh`, `sync-music.sh` | before MPT starts; non-zero blocks provisioning |
| 11.3.5-T1 adapter health | `assets.music_revision`, `assets.music_missing` (D4) | computed by running `check-music.sh --catalog …` in check mode |
| `start-mpt-api.sh` (01.2.2-T4) | `strip-bundled-music.sh --check` | unchanged; `check-music.sh` is the provisioning superset (M12 ✅) |
| 05.1.2-T1 `RenderSubmitMapper` | `MusicSelector`, `MusicChoice` → `render-params.v1.music` + the D3 bgm keys | 05.3.2-T3 blocks it (README §8.1 ✅) |
| 09.6.1-T1 · 09.2.2-T2 · 09.2.5-T1 | `IRecentMusicQuery` implementation; `AddMusicTracks` in the bundle + CI check; seeder pattern | the port is implemented once 09.6.1 exists |
| 06.2.1-T1 · 07 · 10.6.2 · 05.5.2-T1 · 11.4.1-T1 | `attribution`, `platform_scope`, `music_none` hint; the trial needs ≥ 1 track per topic mood set it uses; bucket + prefix `music/` | CR-8 ✅, CR-9; soft trial edge; bucket name ⚠️ |

## 8. Security and privacy

| Threat | Control |
|---|---|
| Unlicensed audio reaching a published video | `check-music.sh` at provisioning + `strip-bundled-music.sh --check` at every start (M12 ✅); only CSV rows with a matching sha256 may sit in `resource/songs` |
| Tampered/truncated track on a node | sha256 verified at sync, at install and in the build check; mismatch → `music_missing <id>`, node not accepting (M10) |
| Bucket credential abuse | node credential is **read-only**, prefix-scoped to `music/` (11.3.2 ⚠️), never printed, never in argv, never in repo or logs; keys in the macOS Keychain (Mac) / node env file (node) |
| Third-party content or secrets in the repo | audio and evidence never committed (M12 ✅); only metadata is tracked, and only where the licence permits it; no key, token or presigned URL in any plan, task file or log line |
| Wrong music on another platform; state on the wrong disk | `platform_scope` stored and enforced downstream (CR-8 ✅); `UV_PROJECT_ENVIRONMENT`, `$NTS_HOME/music/`, `$NTS_HOME/music-evidence/` live under `$NTS_HOME` (`~/.nts`, FileVault), never under `/Volumes/Project` (D14 ✅) |

## 9. Test plan

**Python.** `check_catalog.py --self-test` (fixtures vs `expected.json`, D8 id/gain boundaries, a synthetic JSON↔CSV mismatch pair, the `--json` shape; exit 0/1/2) and `check-music.sh` (01.2.2-T3 plant/tamper style: planted `storage/bgm` file, tampered catalog file, deleted catalog file, healthy tree, missing `resource/`, absent `--catalog`).

**C#.** `MusicTrackRulesTests` (fixture theory, parity with Python), `MusicSelectorTests` (the seven D8 cases named in 05.3.2-T3), `MusicTrackSeederTests` (upsert/no-delete, deactivate-absent, revision guard), `MusicTrackConfigurationTests`, `AddMusicTracksMigrationTests` (09.2.2-T2 bundle check).

**Shell / integration.** `sync-music.sh` idempotency against a local HTTP file-server fixture (second run 0 downloads; a corrupt body keeps the old file) and `upload_music.py` HEAD/sha256 verification. Mac M0 end to end: `check_catalog.py` → `install-music.sh` → `check-music.sh` all exit 0 at revision 1.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 05.3.1-T1 | `check-music.sh` build check | Worker | XS (0.25) | 01.2.2-T3 | 05.3.2-T2, 11.3.1-T2, 11.3.5-T4 (11 CR-4), 05-M0 gate |
| 05.3.2-T1 | Curation of 20–30 tracks, licence records, gain, catalog file | BA | M (1.0) | 01.2.2-T3 | 05.3.2-T2, 05.3.2-T3, 05.5.2-T1 soft |
| 05.3.2-T2 | Storage sync to nodes | Worker | S (0.5) | 05.3.2-T1, 05.3.1-T1, 11.4.1-T1 | 11.3.1-T2 (11 CR-4) |
| 05.3.2-T3 | `music_tracks` table, seeder, `MusicSelector` | BE | S (0.5) | 05.3.2-T1, 09.2.2-T2, 09.2.5-T1 | 05.1.2-T1, 06.2.1-T1 soft |

Total **2.25 d** (high-level 1.75 d; README §9).

### Open items for the TL

- [OWNER — README §10 O2] **O2 source policy.** Assumed (a): YouTube Audio Library first (standard "attribution not required", then CC-BY with credit), CC0 second. Option (b) CC0 only shrinks the catalog per mood but keeps the `license` enum — content only, no contract change.
- [OWNER — README §10 O2] **Content ID risk for CC0** ⚠️ (O2 note): a CC0 file can still be registered by a third party; prefer `YouTube-Audio-Library` scope for the channel, treat a claim as an ops incident. No mitigation designed here.
- [RULED — README §10 T24] **Licence evidence is not committable:** `evidence_url` + `retrieved_at` in the CSV, the PDF/screenshot in `$NTS_HOME/music-evidence/<track_id>/`. Repo evidence needs a licence permitting redistribution — decide before 05.3.2-T1 finishes.
- [RULED — README §10 T23] **`music_tracks` has no `license_url`/`evidence_url` column** (M13 ✅ list). If 06.2.1-T1 needs the licence URL, add the column (a 05.3.2-T3 change → new CR to 00 §6) or have 06.2.1-T1 read `LICENSES.csv`.
- [RULED — README §10 T22] **`music_tracks` is global (no `user_id`)** ⚠️: verify against the 09.2.3 tenant-filter conventions so the table is explicitly exempt from the tenant filter and any row-level security; otherwise selection silently returns `null`.
- [RULED — README §10 T9] `render-params.v1.music` carries `track_id`, `bgm_volume` and the frozen `attribution` (D11 updated by T9); 06.2.1-T1 reads the attribution from the params (06 CR-5).
- [RULED — README §10 T36] **Bucket name, credential scope, S3 client** are ⚠️ (11.4.1-T1, 11.3.2). 05.3.2-T2 lists both client options and recommends the node read-only credential; the final choice belongs to 11.
- [OWNER — README §10 O7] **No per-series mood override** — a series-level mood or a pinned track is a new field plus UI in 10, outside this folder.
- [RULED — README §10 T35] **`MusicBedLufs = −34` is a [target]** (D8 ✅), tuned by ear in 05.5.2-T1; a change re-computes every `bgmVolume` and bumps the revision. **`Music:AvoidRecent = 3` is a [target] config default**, changeable without a revision.

## 11. Rollout

- **M0 (Mac, no adapter).** 05.3.1-T1 (`check-music.sh` wrapping 01.2.2-T3) → 05.3.2-T1 (curation, revision 1, `check_catalog.py`, evidence, owner listening session) → `install-music.sh` + `check-music.sh` exit 0 on the Mac. Files are copied by hand to `$NTS_HOME/music/`; no storage, no sync (M11 ✅). 05.5.2-T1 may use the catalog (soft edge) and records the picked `track_id` per render.
- **M1 (node).** 05.3.2-T3 (after the 09 wave-1 tasks 09.2.2-T2 and 09.2.5-T1) → 05.3.2-T2 (`upload_music.py` + `sync-music.sh`, after 11.4.1-T1) → 05.1.2-T1 consumes `MusicSelector`. 11.3.1-T1 provisioning runs `sync-music.sh` then `check-music.sh`.
- **Flags.** None. Only content-level switches: `active = false` on a track (out of selection, row kept) and `bgm_type = ""` when `MusicSelector` returns `null` (M8 ✅). Config: `Music:AvoidRecent` (3), `Music:MusicBedLufs` (−34) in the backend; `NTS_MUSIC_BUCKET`, `NTS_MUSIC_PREFIX` (`music/`), `NTS_MUSIC_READ_KEY`/`NTS_MUSIC_READ_SECRET` (read-only, `music/` only, D15 S7) in the node env file (11.3.2 ⚠️); operator write key `nts.music.write` in the Keychain; `NTS_HOME/music` as the Mac source dir.
- **Monitoring.** Per node: `assets.music_revision` = the JSON revision, `assets.music_missing` empty; a `music_missing` reason pages as a provisioning defect. The share of renders with `music = null` is watched in M1 — a high share means a catalog gap, not a bug (hint `music_none`, M10 ✅).
- **Rollback.** Set every track `active = false` (or empty the catalog) → selection returns `null` → renders go out without music and succeed (M1 ✅); `strip-bundled-music.sh --check` stays green because the allow-list comes from the unchanged CSV. No MPT fork, no code path that can fail a render because of music.
