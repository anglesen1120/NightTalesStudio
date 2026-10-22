# Phase 5 — collection table review

## Tóm tắt

Các danh sách chưa có cột ổn định làm chủ sở hữu phải đọc lại nhãn ở từng mục. Bảng tập phim đã có nhưng chip xuống dòng, pipeline và kênh chiếm nhiều tầng, nút chữ quá rộng. Giữ hệ màu, chữ, kích thước điều khiển giai đoạn 4; chuyển bộ sưu tập sang hàng bảng và thao tác biểu tượng. Nội dung truyện, video, wizard, đăng nhập và pháp lý được giữ nguyên.

**Review written before page edits.** The inventory below distinguishes fixtures from capacity. Initial before/after counts below are fixture counts, not computed viewport measurements. The measured first-viewport baseline/final results will be appended after implementation. Do not mistake a three-record fixture for proof of ten visible records. No additional production records will be invented to satisfy density.

## Inventory before edits

| Page | Collection / stacked presentation | Existing fixture count | After fixture count | Change / exception |
|---|---|---:|---:|---|
| `index.html` | Phase 2 page catalog cards | 5 | 5 | Catalog table; palette/type specimens remain demonstrations |
| `index.html` | Phase 3 page catalog cards | 6 | 6 | Catalog table; existing links retained |
| `episodes.html` | Episode rows; loading table | 8 | 8 | Keep table, remove tall stage/channel/action stacks |
| `series-list.html` | Series rows; loading table | 2 | 2 | Keep table; configuration becomes one compact line per field |
| `dashboard.html` | Needs-you / running / ready tables | 2 | 2 | Three lanes retained; 2 / 2 / 1 fixtures, no fabricated rows |
| `series-detail.html` | Episodes in default and archived views | 3 | 3 | Two equivalent tables; configuration/autopilot are single-record detail surfaces |
| `bible.html` | Character editor | 1 | 1 | Character table with edit disclosure |
| `bible.html` | Places; world rules / limits; past episode summaries | 1 | 1 | 1 / 2 / 1 records become rows; preserve edit controls separately |
| `bible.html` | AI proposal cards | 2 | 2 | Field/current/proposed/source/actions; contract still accepts whole proposal |
| `research.html` | Sources | 2 | 2 | Source/publisher/date/corroboration/cited/actions |
| `research.html` | Findings | 3 | 3 | Statement/source/publisher/date/confidence/cited/actions; preserve edit forms |
| `story-review.html` | Version stack | 3 | 3 | Version/author/created/words/status/actions; metadata absent in fixtures stays unknown |
| `story-review.html` | Annotation list | 1 | 1 | Add table index of existing annotation; inline annotation remains in prose |
| `video-review.html` | Publication target cards | 2 | 2 | Channel/account/schedule/status/stage/next/actions; scene strip stays visual |
| `publish-and-channels.html` | Linked channel cards incl revoked specimen | 2 | 2 | Channel/account/permission/status/actions |
| `publish-and-channels.html` | Publication queue | 7 | 7 | Separate episode wire status from publication-attempt state |
| `shell.html` | Realtime status specimens | 0 | 0 | Not a collection: alternative component states; unchanged |
| `auth.html` | Auth steps / recovery codes | 0 | 0 | Explicit reading/security exception; unchanged |
| `series-editor.html` | Voice/style choices and wizard summary | 0 | 0 | Explicit wizard exception; unchanged |
| `new-episode.html` | Idea form / estimate detail | 0 | 0 | Single-record form; unchanged |
| `legal.html` | Terms / privacy prose | 0 | 0 | Explicit reading exception; unchanged |
| `control-size-audit.html` | Phase-4 measurement evidence | 0 | 0 | Historical verification report; unchanged |

## Density and scope

At 1280 × 800, the available width is reduced by the existing side navigation. Existing headers, filters and summary stay. Target is 44 px rows (36 px dense), 24 px single-line chips and 32 px icon actions. Ten-row capacity requires at least 440 px below the header. Multiple lanes, lower sections and narrow review rails cannot all expose ten records in the *first page viewport* while preserving the reading surface. Report that limit explicitly rather than moving the story, hiding key fields, truncating data or duplicating records. Baseline and final first-viewport counts are measured at the same scroll position, independently of theoretical ten-row capacity.

## Wire status → meaning, next action and available icons

Read directly from `/Volumes/Project/NightTalesStudio/web/src/app/features/episodes/domain/episode.model.ts`. Exactly 11 wire statuses; unknown is a display fallback, not a new wire status. The list endpoint excludes cancelled. Source currently routes list actions through detail; direct review/retry icons here are proposed frontend navigation, not permission to execute backend mutations. `nextActions`, prerequisites, version/ETag and per-upload consent must gate real mutations in Angular.

| Wire status | VI / EN chip | Meaning / next action | Available row icons | Stage |
|---|---|---|---|---|
| draft | Nháp / Draft | Add idea and start writing | edit, view | story not started |
| story_generating | Đang viết / Writing | Wait; inspect generation | view | story current |
| story_review | Duyệt truyện / Story review | Read and decide | view, approve, request changes; reject in more | story current |
| story_approved | Truyện đã duyệt / Story approved | Production can start; inspect details | view | images not started |
| rendering | Đang dựng / Rendering | Wait; inspect active job | view | exact render substage only if supplied |
| video_review | Duyệt video / Video review | Preview and decide | preview, approve, reject | review current |
| ready | Sẵn sàng / Ready | Preview, schedule or publish with consent | preview, schedule, publish | publish not started |
| scheduled | Đã lên lịch / Scheduled | Inspect schedule | view, schedule | publish current |
| published | Đã đăng / Published | Open verified external URL | external; disabled if no fixture URL | publish done |
| failed | Thất bại / Failed | Inspect error, then retry if eligible | view, retry | failed stage only when known |
| cancelled | Đã huỷ / Cancelled | Inspect; no assumed resumability | view | not active |

Publication attempts have their own state (consent/upload/processing/failure/cancelled) and channels their own permission state. Never re-label an attempt failure as episode `failed` without evidence. Use `— / Chưa có dữ liệu` for absent episode status, with neutral chip, no invented stage, and a next action to inspect details. TikTok remains planned/unavailable. No external publication URL is fabricated.

## Component specification and verification plan

- Shared chip: 24 px, 12/16 px 500, 6 px radius, 8 px inline padding, 14 px icon, gap 6 px; single line in both locales. Full meaning in localized tooltip. Column follows longest chip, never fixed 120 px.
- Shared icons: 32 × 32 visual, SVG 18 × 18, stroke 1.75 / viewBox 24, ghost, 6 px radius. Tooltip and aria-label in active locale. Destructive actions go to confirmation; neutral until hover/focus. At most three visible action controls including more; extra entries in native disclosure menu.
- Native detail anchor is the row keyboard entry: Tab, Enter opens. The stretched anchor covers noninteractive cells; action cell, selection and disclosures stay above it. No nonfunctional tabindex-only row.
- Below 768: same column order becomes labelled fields; actions at bottom right. Coarse hit area 44 px without scaling the visual icon; extra coarse spacing prevents overlapping hit targets.
- Every collection gets loading, first-run empty, filtered-empty, error/retry, acting-spinner and row failure specimens. Static CSS links/disclosures demonstrate states, not persistence or API calls.
- Bulk checkboxes/bar on episodes and publication queue; mutations remain confirmation/consent gated.

## Measurements

Final computed results follow; the ten-row first-viewport target is explicitly partial.




## Final computed verification
Measurements use the existing embedded app preview, CSS-loaded local fonts and actual DOM rectangles. All 15 pages: 1280 / 768 / 360 × 800, dark/light, VI/EN (180 variants). Baseline: Phase-4 backup at 1280 × 800, dark VI. Closed state specimens are temporarily exposed for dimensions, then restored. Tables below the fold are reported as zero first-viewport rows, not falsely counted after scrolling.
### Table inventory and density
| Page / table | Columns (English) | Row icons | Before visible | After visible / fixtures | Final row heights (px) | Ten-row first viewport |
|---|---|---|---:|---:|---|---|
| `index.html` / `phase-two-series-catalog` | Page · Contents · Actions | view | 0 | 0 / 5 | 44 | Not met |
| `index.html` / `phase-three-production-catalog` | Page · Contents · Actions | view | 0 | 0 / 6 | 44 | Not met |
| `index.html` / `chip-row-after` | Episode · Status · Stage · Next action · Actions | approve, changes, more, reject, view | — | 0 / 1 | 44 | Not met |
| `index.html` / `status-action-matrix` | Wire status · Status · Stage · Next action · Actions | approve, changes, edit, external, more, preview, publish, reject, retry, schedule, view | — | 0 / 11 | 44 | Not met |
| `dashboard.html` / `dashboard-table-1` | Episode · Series · Status · Stage · Next action · Progress / Updated · Actions | approve, changes, more, preview, reject, view | 2 | 2 / 2 | 36 | Not met |
| `dashboard.html` / `dashboard-table-2` | Episode · Series · Status · Stage · Next action · Progress / Updated · Actions | view | 2 | 2 / 2 | 36 | Not met |
| `dashboard.html` / `dashboard-table-3` | Episode · Series · Status · Stage · Next action · Progress / Updated · Actions | preview, publish, schedule | 0 | 0 / 1 | 36 | Not met |
| `series-list.html` / `series-list-table-1` | Series title · Topic · Language · Aspect · Voice · Style · Schedule / Autopilot · Episodes · Actions | archive, edit, view | 2 | 2 / 2 | 44 | Not met |
| `series-detail.html` / `series-episodes-1` | Episode · Language / Aspect · Status · Stage · Next action · Actions | approve, changes, more, preview, publish, reject, schedule, view | 3 | 3 / 3 | 44 | Not met |
| `series-detail.html` / `series-episodes-2` | Episode · Language / Aspect · Status · Stage · Next action · Actions | approve, changes, more, preview, publish, reject, schedule, view | 0 | 0 / 3 | 44 | Not met |
| `bible.html` / `bible-characters` | Name / Kind · Description / Content · Source / Appearance · Actions | delete, edit | 0 | 0 / 1 | 44 | Not met |
| `bible.html` / `bible-places` | Name / Kind · Description / Content · Source / Appearance · Actions | edit | — | 0 / 1 | 44 | Not met |
| `bible.html` / `bible-proposals` | Field · Current value · Proposed value · Source · Actions | approve, reject | 2 | 0 / 2 | 44 | Not met |
| `bible.html` / `bible-rules` | Name / Kind · Description / Content · Source / Appearance · Actions | edit | — | 0 / 2 | 44 | Not met |
| `bible.html` / `bible-summaries` | Name / Kind · Description / Content · Source / Appearance · Status · Stage · Next action · Actions | view | — | 0 / 1 | 44 | Not met |
| `research.html` / `research-sources` | Source · Publisher · Checked · Corroboration · Used in story · Actions | delete, external, view | 1 | 2 / 2 | 44 | Not met |
| `research.html` / `research-findings` | Finding · Source · Publisher · Checked · Confidence · Used in story · Actions | delete, edit, view | 0 | 0 / 3 | 44 | Not met |
| `episodes.html` / `episodes-table-1` | Select · Episode · Series · Status · Stage · Next action · Updated · Channels · Actions | approve, changes, external, more, preview, publish, reject, retry, schedule, view | 2 | 7 / 8 | 44 | Not met |
| `story-review.html` / `story-versions` | Version · Author / AI · Created · Words · Version status · Actions | diff, restore, view | 0 | 0 / 3 | 44 | Not met |
| `story-review.html` / `story-annotations` | Segment · Annotation · Severity · State · Actions | changes, view | 0 | 0 / 1 | 44 | Not met |
| `video-review.html` / `video-targets-2` | Channel · Account · Schedule · Status · Stage · Next action · Publication · Actions | preview, publish, schedule | 0 | 0 / 2 | 44 | Not met |
| `publish-and-channels.html` / `linked-channels-1` | Channel · Account · Upload permission · Connection · Actions | external, reject, view | 2 | 2 / 2 | 44 | Not met |
| `publish-and-channels.html` / `publication-queue` | Select · Episode · Channel / Schedule · Status · Stage · Next action · Publication attempt · Actions | external, publish, retry, view | 0 | 0 / 7 | 44 | Not met |

**Density remains partial.** Compact episode rows improved from 2 to 7 fully visible rows in the first viewport; all eight exist without expanding records. Most fixtures contain only 1–7 records. Lower sections intentionally remain below the story/video/header content. Ten rows cannot be claimed in every first page viewport. No records were duplicated. The table itself can fit ten 44 px rows in 440 px after scrolling its header into view; that is capacity, not an observed first-viewport count.
The archived series fixture is hidden by default. Baseline collection selectors aggregate several state specimens; video baseline includes the metadata form (998.86 px), which is preserved and is not a collection. Dashes indicate a newly added index or a collection without a comparable baseline selector.
### Icon measurements
| Page | Active-locale specimens, incl closed states | Width × height | SVG | Radius | aria-label |
|---|---:|---|---|---|---|
| `index.html` | 60 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `dashboard.html` | 19 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `series-list.html` | 11 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `series-detail.html` | 22 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `bible.html` | 20 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `research.html` | 19 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `episodes.html` | 26 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `story-review.html` | 15 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `video-review.html` | 6 | 32 × 32 px | 18 px | 6 px | Present on every specimen |
| `publish-and-channels.html` | 18 | 32 × 32 px | 18 px | 6 px | Present on every specimen |

All icon measurements passed in both themes/locales and all three widths. Native summary is used for More. Hover/focus tooltips and disabled-reason tooltips are localized. The 44 px coarse-pointer pseudo-element is a hit area; the visual stays 32 px. Pointer-coarse rows gain spacing to prevent overlapping targets.
### Status chip measurements
Widths below are computed; height is 24 px for all statuses, both locales, both themes. All have `white-space: nowrap`, 6 px radius, a 14 px leading icon and 6 px gap. Status column follows content; it is not fixed at 120 px.
| Wire status | VI label | VI width × height | EN label | EN width × height |
|---|---|---|---|---|
| `draft` | Nháp | 69.7 × 24 | Draft | 68.81 × 24 |
| `story_generating` | Đang viết | 95.52 × 24 | Writing | 81.83 × 24 |
| `story_review` | Duyệt truyện | 114.31 × 24 | Story review | 112.39 × 24 |
| `story_approved` | Truyện đã duyệt | 134.03 × 24 | Story approved | 130.44 × 24 |
| `rendering` | Đang dựng | 102.45 × 24 | Rendering | 98 × 24 |
| `video_review` | Duyệt video | 109.16 × 24 | Video review | 114.73 × 24 |
| `ready` | Sẵn sàng | 92.08 × 24 | Ready | 75.53 × 24 |
| `scheduled` | Đã lên lịch | 99.42 × 24 | Scheduled | 100.44 × 24 |
| `published` | Đã đăng | 88.91 × 24 | Published | 95.45 × 24 |
| `failed` | Thất bại | 87.91 × 24 | Failed | 74.36 × 24 |
| `cancelled` | Đã huỷ | 79.11 × 24 | Cancelled | 98.13 × 24 |

### Responsive, status and state checks
- Horizontal overflow: 0 of 180 variants.
- 360 px labelled-card collapse failures: 0. Same field order, action cell last and right-aligned. Tablet 768 px also uses labelled cards to preserve every field.
- Episode/video/publication tables missing Status + Stage + Next action: 0. Version state, annotation state and channel authorization are separate domains.
- All 11 exact wire statuses have meaning and action mappings above and in the live matrix in `index.html`. Review rows expose view/approve/more; video-review rows preview/approve/reject; failed rows view/retry; ready rows preview/schedule/publish; scheduled rows view/schedule; published external icon is disabled with a reason because the fixture has no verified URL.
- Production substages are shown only where existing fixture job data supplies them. Generic rendering rows show Production with seven outlined segments, rather than asserting an unknown active substage. Unknown episode status in publication attempts remains neutral; attempt state determines consent/retry controls independently.
- Every collection has a closed state disclosure containing loading skeleton rows, first-run empty, filtered-empty, retry error, acting spinner and failed action specimens. The reference status matrix is documentation, not a fetched collection.
- Selection checkboxes and bulk icon bars remain on episodes and publication queue. Native anchors give Enter-to-open and a stretched hit area on noninteractive cells. Native disclosures handle More and confirmation; API mutations, conditional bulk eligibility, focus trapping and sorting remain Angular integration responsibilities of the static prototype.

### Static integrity
- Duplicate-ID/script/missing-action-cell issues: 0. None found.
- Every page links the shared `tokens.css`. No external icon, font or image fetch was added. Existing media and reading surfaces remain local. Backups preserve Phase 4 under `backups/phase-4/`.
- Computed raw evidence: `assets/audit/table-measurements.json`. Historical Phase-4 audit remains unchanged.
Status-chip text contrast from computed foreground/background colours: minimum **4.63:1** across both themes (target 4.5:1).

Status-to-actions audit: no mismatches among all known-status collection rows. Unknown attempt states remain governed by publication consent/attempt state and do not pretend to be episode wire statuses.


## Phase 7 — source decisions and collection additions

**Quyết định:** bỏ toàn bộ “Chi phí dự kiến / Estimated cost”, “Quote unavailable” và điều kiện xác nhận báo giá trong `new-episode.html`. DEC 10.14 loại bỏ billing/credits/báo giá khỏi owner UI; §5.2 vẫn yêu cầu `spend-capped`. Trạng thái mới ở `new-episode.html#spend-capped` chỉ giải thích tạm dừng, giữ ý tưởng và hướng quay lại; không có giá, tín dụng hay thao tác nạp tiền. `render.html#paused` thể hiện `generation.processingPaused`, không tạo wire status `paused`.

The source is `/Volumes/Project/NightTalesStudio/DECISIONS.md` §10.14, `plans/10-web-ui.md` §5.2/§5.3 and `web/src/app/features/episodes/domain/episode.model.ts`. Approve/reopen dialogs contain no cost copy. Rendering uses only the guaranteed `queued → running → ingesting` stages, with optional worker-reported sub-steps. Publication and render statuses remain distinct from episode statuses.

| New collection | Page / section | Columns | Row actions |
|---|---|---|---|
| Autopilot run log | autopilot.html#run-log | Run, Started, Status, Result, Episode, Stage, Next action, Actions | View run, read story |
| Veto window | autopilot.html#veto | Episode, Publishes at, Time left, Status, Stage, Next action, Actions | Approve-now preview, cancel confirmation |
| Calibration report | autopilot.html#calibration | Metric, Status, Result, Actions | View data-availability note |
| YouTube publication | publish-youtube.html#submitted | Channel, Episode, Status, Stage, Next action, Actions | View scheduled result |
| TikTok publication | publish-tiktok.html#submitted, #post-status, #post-failed | Channel, Episode, Status, Stage, Next action, Actions | View status; external open disabled without URL; reconnect on failed authorization |

Tables reuse `collection-table`, `one-line-table`, `row-icons`, `icon-btn`, `status-chip`, explicit column shares, cell descriptions and responsive card collapse from tokens.css. They include disclosed loading, first-run empty, filtered empty, retry, row-in-progress and row-failure specimens. Icon buttons use the existing inline SVG set with localized accessible labels and tooltips. New fixtures contain one real workflow example rather than padding the table with invented run statistics; calibration metrics are explicitly unavailable instead of fabricated.

See [phase-7-coverage.md](phase-7-coverage.md) for the complete §5.2 route/state mapping, evidence, and static prototype limitations.

## Phase 8 — owner feedback: table rule on the phase-7 pages

**Scope:** collection surfaces, chip sizing, table headers and the missing auth rate-limit specimens only. Palette, type, spacing, navigation and page copy outside those surfaces are unchanged. Phase-8 backups are in `backups/phase-8/`.

### Found places and before/after

| Page / place | Before | After | Fixture count |
|---|---|---|---:|
| `render.html#default` · Tiến độ bản dựng | Vertical `step-list` (one stage per block, labels repeated underneath); approved-story and stage-detail prose cards in a right column; full notice for the e-mail | Six-column `collection-table one-line-table`: Bước · Trạng thái · Bắt đầu · Thời lượng · Chi tiết · Hành động. Three guaranteed stages with indented sub-rows (giọng → phụ đề → ảnh → ghép → kiểm tra). Progress, elapsed time and Huỷ dựng share one compact header row; e-mail copy is one muted line; the two right-column cards collapse to a summary row under the table | 8 rows (3 + 5) |
| `render.html#waiting` | Vertical `step-list` | Same step table; queued row is “Đang chạy”, later stages “Chờ”; no invented start time | 3 rows |
| `render.html#failed` | Vertical `step-list`; separate error notice | Same step table; failed stage and failed image sub-step carry the error chip, the reason in Chi tiết (one line with full-text tooltip) and a retry icon; remaining steps show “Bỏ qua” | 8 rows |
| `render.html#stage-failed` | Vertical `step-list`; retry button | Same step table; assembly failure row with error chip, reason in Chi tiết and retry icon; retry CTA remains in the header | 8 rows |
| `account-security.html#default` | MFA status panel only; no session or factor collection | Two tables: Yếu tố xác thực (1 TOTP factor) and Phiên đăng nhập (current session); shared chips and icon actions | 1 + 1 |
| `account-security.html#recovery` | Two-column `<ol class="recovery-list">` of 10 codes | Three-column table: Mã · Trạng thái · Hành động; 10 rows, neutral “Minh hoạ” chip and copy icon with a local clipboard fallback | 10 |
| `story-editor.html#default` | “Tự đánh giá” notice stack and an inline similarity notice | “Ghi chú & vấn đề” table (2 rows: taste advisory, objective block) and “Đoạn tương đồng” table (1 row). Status chips: Gợi ý, Chặn, Cần xem; icon actions view/edit | 2 + 1 |
| `auth.html#recovery-codes` | `<ol class="recovery-list">` of 10 demo codes | Two-column table: Mã · Trạng thái; neutral “Minh hoạ” chips; no fake row action on a shown-once secret | 10 |
| `auth.html` password / MFA / forgot / reset | One shared password-only “too many attempts” notice | Four rate-limit specimens with a 30-second retry-after countdown and flow-specific “what to do” copy; the retry control enables at zero | 4 |
| `autopilot.html` run log / veto / calibration | Already tables, but the “Đang tắt” chip stretched to the panel width | Tables retained and re-checked; every chip is now `width:fit-content`; run log, veto and calibration headings fit at 1280 | 1 / 1 / 6 rows |
| `publish-youtube.html#submitted`, `publish-tiktok.html#submitted/#post-status/#post-failed` | Already tables | Tables retained and re-checked; target, status, stage, next action and icon actions are all present | 1 / 1 / 1 / 1 |
| `channel-callback.html`, `publication-links.html`, `dialogs.html` | No collection | No change; single-record states and overlays only | 0 |
| `dashboard.html` | 21 tables; header “Tiến độ / Cập nhật” truncated at 7.86% | Header shortened to “Cập nhật / Updated” and column widened to 9.4%; duplicate IDs on the hidden loading/empty specimens renamed | unchanged |
| `episodes.html` | Select header “Chọn” clipped to “C…”; “Cập nhật ↕” clipped to “Cập nh…” | Select column is a select-all checkbox with an aria-label and no text; column shares rebalanced (select 3.6%, title 16.4%, updated 10%) | unchanged |
| `series-list.html` | “Số tập / Episodes” truncated in a 5.66% column | Count column widened to 7.2%, title reduced to 15.44% | unchanged |

### Final verification

- **Vertical stacks:** no operational collection remains as a one-item-per-block stack. The only repeated blocks left are steppers and the 7-step pipeline (sequence, not a collection), `series-editor` voice/style choice rows (form), legal prose lists, and state galleries/specimen grids. `auth.html` recovery codes and `account-security.html` recovery codes are now tables.
- **Header truncation at 1280, VI and EN:** 0 of 175 table heads with explicit column shares exceed their column width, measured against the embedded Be Vietnam Pro WOFF2 metrics at 13 px/600 plus cell padding and the episodes sort affordance. “Chọn” is now a checkbox with `aria-label`; the episodes “Cập nhật” column has 10% (88 px available, 82.6 px required).
- **Chip sizing:** `.chip` in `tokens.css` is `inline-flex`, `width:fit-content`, `max-width:100%`, `justify-self:start`, so grid/flex parents cannot stretch it. 177 chips checked across the project (161 status chips); the widest computed chip is **217.16 px** (`story-editor.html`, “Automatically corrected once”), below the 328 px content width at 360. No chip has a width override.
- **1280 / 768 / 360, dark and light:** all collection tables collapse to labelled cards at 768–1000 and ≤767; the render step table uses the same labelled-card rule up to 1000. The render header wraps into title/chip, progress and cancel; the summary row goes single-column at ≤599. New surfaces use only semantic tokens, so dark/light keys stay paired. One desktop render of `render.html#default` was inspected for collisions; 360/768/light were checked from the DOM/CSS geometry and the shared token pairs, not from new screenshots.
- **Integrity:** no duplicate IDs, broken local links, external dependencies, unnamed icon controls or missing action cells on the edited pages. The auth countdown and recovery-code copy are two small local scripts; no network call is simulated.

Computed evidence for this phase: `assets/audit/phase-8-verification.json`.

## Phase 9 — form-control alignment

**Scope:** search frames, filter toolbars, radio filters and option cards only. Palette, copy, page layout and table rules are unchanged. Phase-9 backups are in `backups/phase-9/`.

### Shared rules added to `tokens.css`

| Class / selector | Contract |
|---|---|
| `.control-toolbar` | One 32 px line, `align-items:center`, 8 px gaps, wraps at narrow widths; every direct control is 32 px with 6 px radius and 1 px border |
| `.control-search` | The only frame; 16 px leading icon at 8 px gap; inner `input[type=search]` is borderless, transparent and 30 px high inside the 32 px frame |
| `.control-field` | 32 px label frame for a toolbar select; select fills it and keeps the native chevron inside a 30 px right inset |
| `.segmented` | One outer frame; 1 px dividers; native radios visually hidden; selected segment = tinted signal background, accent text and a 14 px check; focus ring on the segment |
| `.bulk-bar` | Flex line of 32 px label/icons; same height as the toolbar; wraps as a group at 360 |
| `.field` / `select` / `textarea` | 36 px form fields; one border and background on the control itself; 12 px left padding and 30 px select right inset |
| `.check` / `.choice-row` | Visible boxes are 16 px; option-card boxes align to the first title line; 16 px card padding on all sides |

Search placeholders are short (`Tìm kiếm` / `Search`) and a small local script switches them with the active locale. No combined bilingual placeholder remains.

### Before / after counts per page

Counts are the worst of 12 variants (1280 / 768 / 360 × dark / light × VI / EN), ordered **nested frame / toolbar alignment / visible box centring / placeholder**. Measurements use real DOM rectangles and computed styles in Chrome.

| Page | Before | After |
|---|---:|---:|
| `account-security.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `auth.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `autopilot.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `bible.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `channel-callback.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `control-size-audit.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `dashboard.html` | 0 / 0 / 4 / 0 | 0 / 0 / 0 / 0 |
| `dialogs.html` | 0 / 0 / 2 / 0 | 0 / 0 / 0 / 0 |
| `episodes.html` | 0 / 2 / 0 / 1 | 0 / 0 / 0 / 0 |
| `index.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `legal.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `new-episode.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `publication-links.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `publish-and-channels.html` | 0 / 1 / 0 / 1 | 0 / 0 / 0 / 0 |
| `publish-tiktok.html` | 0 / 0 / 4 / 0 | 0 / 0 / 0 / 0 |
| `publish-youtube.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `render.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `research.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `series-detail.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `series-editor.html` | 0 / 0 / 3 / 0 | 0 / 0 / 0 / 0 |
| `series-list.html` | 1 / 0 / 3 / 1 | 0 / 0 / 0 / 0 |
| `shell.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `story-editor.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `story-review.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `video-review.html` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| **Total** | **1 / 3 / 16 / 3** | **0 / 0 / 0 / 0** |

### Final verification

- **300 variants** (25 root pages × 1280 / 768 / 360 × dark / light × VI / EN): nested frames **0**, toolbar height/centre failures **0**, off-centre visible boxes **0**, placeholder overflows or combined placeholders **0**.
- Additional geometry checks: horizontal overflow **0**, clipped segmented frames **0**, search input outside its frame **0**, visible boxes at sizes other than 16 px **0**.
- At 360 the segmented control shrinks its segment padding and text to 12 px but keeps the selected 14 px check; the frame is 313 px wide in `series-list.html` and 279 px in the `index.html` specimen, both fully visible.
- One 1280 and one 360 screenshot per affected pattern were inspected in dark and light. The 1280 light `index.html` specimen documents the toolbar, fields, check/radio rows and option cards live.
- Every page-level form control follows the shared contract through the new classes and token-level element rules; the obsolete `.filterbar`/`.compact-search`/`.compact-toolbar` hooks are no longer used by the edited filters.
- **Limitation:** measurements cover default visible states. Manually opening every closed specimen at 360 exposes pre-existing wide state-table overflow, unrelated to form controls and left unchanged under the “change nothing else” scope.

Computed evidence: `assets/audit/phase-9-form-controls.json`; backup copies: `backups/phase-9/`.


## Phase 11 — long-script review/editor workspace

**Scope:** only `story-review.html` and `story-editor.html`. The 40-paragraph default specimen and an 80-paragraph stress specimen replace the old link row plus six stacked `.segment` blocks. Palette, navigation, copy outside the two pages and every other page are unchanged. Phase-11 work is in `story-workspace.css` and `story-workspace.js`.

### Before / after

| Page | Before | After |
|---|---|---|
| `story-review.html#default` | Segment jump links followed by six tall stacked segment cards; decision rail below/beside them; annotation table always open | 248 px segment index · continuous reader · 224 px sticky decision rail. Index has search, All/Flagged/Notes/Unread filters and read progress. Reader shows 20 segments first with “Show 20 more”. Annotations and versions move behind one closed disclosure |
| `story-editor.html#default` | Title/hook form, six stacked draggable segment cards, ending/metadata form, save cluster and right rail | 232 px draggable index · editable reader · 304 px sticky rail. Save/leave actions and version status stay in the rail; title/ending forms move into a closed “Story details” disclosure. Annotations/similarity move behind one closed disclosure |
| Both at ≤1000 px | No dedicated segment index; page length grew with every segment | Reader stays full width with a bottom decision bar. Index is a 420 px drawer at 768 and full-width at 360; the decision rail becomes a bottom sheet above the bar |
| Both, 80-segment stress | Not present | Same three-pane layout with 80 index rows and the same 20-at-a-time reader. Page height does not grow with segment count because the index and reader scroll internally |

### Measured result

Computed evidence: `assets/audit/phase-11-responsive.json` — 30 variants (2 pages × 3 widths × dark/light × VI/EN, plus 6 stress variants).

- **Horizontal overflow:** 0/30. Index, reader, rail, drawer, sheet and 68ch text measure stay inside the viewport.
- **Page height:** 0/30 over 2× viewport. Worst ratio is **1.78×** (`story-editor.html`, 360 px, light, EN); the 40-segment defaults measure **1.48–1.78×**, the 80-segment stress **1.46–1.63×**. Internal reader scroll heights are 2,070–3,804 px while the visible reader is 400–625 px.
- **Index rows:** 40 rows at **36 px** each in every default variant; 80 rows at 36 px in every stress variant. The index list scrolls independently (e.g. 1,446 px content in a 578 px viewport at 360).
- **1280 px panes:** review 248 / 517 / 224 px; editor 232 / 453 / 304 px. Reader text is capped at **68ch**; the pane is narrower than 68ch below roughly 1,400 px, so the cap only binds on wider screens.
- **768 px:** single reader column, index toggle and bottom decision bar visible; decision rail hidden until opened. **360 px:** index toggle full width, drawer = 360 px, decision sheet = 345 × 562 px anchored at y=160 with the bar below.
- **Jump test:** from row 1 to row 25 loads 40/40 and puts the target segment **8 px** below the reader top with focus moved to the segment (`1280`, `768`, `360`), no horizontal movement.
- **Interactions:** J/K move one segment, N moves to the next flagged segment; search “ma” narrows to 8 rows; Flagged filter shows 6; Mark all read reaches 40/40 and disables itself. In the editor, Alt+↑ moves row 2 above row 1, updates the draft-order hint, and an edited textarea is marked dirty.
- **Language/theme:** no combined VI/EN accessibility attributes, placeholders or native titles were found in either workspace; EN changes the index/search/reader/rail labels, and light mode keeps the same geometry.

### Implementation notes

- `story-workspace.js` owns fixtures, index filtering/search/read progress, chunked rendering, keyboard shortcuts, editor text edits, drag/Alt reorder, locale re-render and drawer/sheet state. `story-workspace.css` owns the sticky three-pane grid, 36 px rows, 68ch reader measure, mobile drawer/sheet and bottom bar.
- Default and stress roots are separate `[data-story-workspace]` instances (`data-count="40"` / `"80"`, `data-mode="review"` / `"editor"`), so the stress page needs no duplicate page-level script.
- `:has(.p7-state:target)` / `:has(.view:target)` did not match in this Chrome build, leaving the default view visible above the stress view. `story-workspace.js` now hides `.p7-default` / `.view.default` with a small state-sync helper on load and `hashchange`.
- **Limitation:** measurements cover the default visible state. Opening every closed state or annotation specimen at 360 px can still expose the pre-existing wide state-table overflow; that is unchanged and outside this phase.

## Phase 12 result — dashboard overview

**Scope:** only `dashboard.html`. The old dashboard specimen tables are replaced by one overview: header with live status, a 7-tile pipeline strip, a live “Đang chạy” table, “Cần bạn xử lý” and “Sắp tới” side tables, recent activity, plus first-run, loading, error, empty-filter and reconnecting states. Palette, navigation, every other page and all shared components are unchanged.

### Before / after

| Area | Before | After |
|---|---|---|
| Header | Page title plus specimen hint | Title, live/reconnect status, “Tập phim mới” CTA |
| Pipeline | No overview | 7 tiles: writing, story review, rendering, video review, ready, scheduled, errors; each links to the filtered episode list |
| Running | Generic episode table specimen | First-viewport table: title + series, status chip, 7-step stage, progress bar + percent + elapsed/ETA + source, view/cancel actions, empty-filter row |
| Attention | Scattered specimen rows | “Cần bạn xử lý” (4 rows: story review, video review, render failure, publish failure) and “Sắp tới” (4 scheduled rows; channel shown in the row subtitle) |
| History | None | “Hoạt động gần đây” (4 rows: time, episode, result, channel) |
| States | Static specimens | Populated, no running jobs, first run, loading skeleton, error and reconnecting banner, switchable in the page state picker; cancel dialog and toast |

### Measured result

Computed evidence: `assets/audit/phase-12-responsive.json` — 12 variants (1280 / 768 / 360 × dark / light × VI / EN) plus a 12-variant state sweep at 1280 and 360.

- **Horizontal overflow:** 0/12. **Runtime errors:** 0. **Duplicate ids:** 0.
- **1280 × 800:** page height 1,167 px; all **7/7** tiles and **5/5** running rows appear in the first viewport (last row bottom **551 px**). Tile **68 px**, run row **56 px**, needs/upcoming row **44 px**, activity row **39 px**.
- **1240 and below:** the two side tables stack full width and the pipeline strip drops to 4 columns, so “Cần bạn xử lý”/“Sắp tới” chips and dates never truncate. At **1024** this keeps all tile labels and both side tables readable without changing the 1280 layout.
- **768:** page height 1,555 px; strip becomes 4 × 2, side tables stack, run rows **44 px**, stage column widens to **100 px** so the compact percent + ETA line fits without crossing into actions.
- **360:** page height 2,151 px (VI) / 2,163 px (EN); strip 2 columns at **66 px**, run rows **71 px**, needs/upcoming rows **71 px**, activity rows **71 px**. The progress line under the 7 dots is fully visible; titles keep the two-line compact contract and full value in the cell title.
- **States:** all six exist in `dashboard.html` — `populated` (5 running rows), `nothing` (0 rows + empty-filter row), `empty` (first-run card + create-series CTA), `loading` (skeleton only), `error` (retry card only), `reconnecting` (data retained + banner + reconnecting live label). Each has 0 horizontal overflow at 1280 and 360; at 768 and 360 the page also has 0 px horizontal overflow.
- **Language/theme:** EN shows no visible Vietnamese strings in the overview; all native titles/aria-labels/placeholders resolve to one language (checked after runtime localisation). Light mode keeps the same geometry and contrast; the seven tiles and four tables render in both themes.
- **Tables:** each of the four tables is labelled by its section heading; 6 state radios share one group; live status and reconnect banner are `role="status"` with `aria-live="polite"`.

### Nguồn dữ liệu

Hợp đồng của dashboard là `GET /episodes` (+ SignalR) theo `plans/10-web-ui.md` §5.2. Các ô chưa có endpoint trong `plans/00-overview.md` §8 được đánh dấu `[cần API]`; dữ liệu tương ứng trong trang hiện là fixture.

| Khối trên dashboard | Dữ liệu đang hiển thị | Endpoint / sự kiện hiện có | Chưa có — cần bổ sung |
|---|---|---|---|
| Dải trạng thái (7 ô) | Số tập theo từng trạng thái | `GET /episodes` (nhóm theo `status`) | Phụ đề từng ô (“tự động”, “chờ > 24 giờ”, “7 ngày tới”) cần thêm dữ liệu thời gian/trigger `[cần API]` |
| Đang chạy — danh tính, trạng thái, bước | Tên tập, series, chip trạng thái, chỉ báo 7 bước | `GET /episodes`; cập nhật trạng thái qua `EpisodeUpdated`; `RenderProgress` mang `stage`/`percent` cho render; `GET /episodes/{id}` trả stage/percent của render gần nhất | Bước hiện tại ở cấp danh sách cho các bước không phải render `[cần API]` |
| Đang chạy — tiến độ | Thanh tiến độ và phần trăm | `RenderProgress { episodeId, renderJobId, stage, percent }` (chỉ render) | Tiến độ của các bước còn lại ở cấp danh sách `[cần API]` |
| Đang chạy — ETA | “ETA 6 phút” / “chưa có ước tính” | Không có endpoint ETA cho lượt chạy; `GET /publishing/youtube/eta` chỉ trả slot đăng YouTube | `[cần API]` |
| Đang chạy — thời gian đã chạy | “84:42” | Không có mốc bắt đầu trong hợp đồng hiện tại | `[cần API]` |
| Đang chạy — người khởi chạy | “Bot” / “Bạn” | Không có trong hợp đồng hiện tại | `[cần API]` |
| Cần bạn xử lý | Tên tập, việc cần làm, thời gian chờ | `GET /episodes` (trạng thái; nhãn việc suy ra từ status); `nextActions` chỉ có trên `GET /episodes/{id}` | `nextActions` cấp danh sách và thời gian chờ `[cần API]` |
| Sắp tới | 4 publication đã lên lịch; kênh ở dòng phụ | `GET /episodes/{id}/publications` (từng tập); `PublicationUpdated` qua hub | Danh sách lịch đăng gộp nhiều tập `[cần API]` |
| Hoạt động gần đây | 4 sự kiện gần nhất | Sự kiện live `EpisodeUpdated`, `PublicationUpdated` | Lịch sử/activity feed để tải lại `[cần API]` |
| Chỉ báo live / kết nối lại | Chấm “Trực tiếp”, nhãn cập nhật, banner khi mất kết nối | Hub `/hubs/episodes`, ticket `POST /hubs/ticket`; kết nối lại gọi lại `GET /episodes` | — |

- Huỷ lượt chạy: render dùng `DELETE /render-jobs/{id}`. Huỷ khi đang tạo truyện chưa có endpoint tương ứng `[cần API]`.
- “Tập phim mới” chỉ là liên kết tới `new-episode.html`; việc tạo tập dùng `POST /series/{id}/episodes` và `POST …/story/generate` ở trang đó.
- Không có số dư credit, nút mua, tổng chi hay bất kỳ chuỗi tiền nào (DEC 10.14); copy của dialog huỷ cũng không nhắc tới phí.


### Implementation notes

- All Phase-12 CSS is scoped to `:root [data-page=dashboard]` and appended to the page `<style>`; markup uses the existing `collection-table one-line-table`, `chip status-chip tone-*`, `icon-btn`, `.btn`, `stage-inline`/`stage-segments`, `platform-icon` and token set.
- The running table reuses the Phase-10 responsive table contract: 768 keeps the table and hides the detail column; 360 switches to two-line compact rows. A dashboard-only rule gives the stage meta cell 30 px at 360 and 100 px column width at tablet so the compact “percent · ETA” line is never clipped or collided with actions.
- State switching is CSS-only (`:has(#view-…:checked)`), so the static page shows every state without JavaScript; the page state picker lives below the shell and is not part of the overview.
- **Limitation:** measurements cover the default visible state, as in earlier phases. Opening every closed specimen at 360 px can still expose the pre-existing wide state-table overflow; that is unchanged and outside this phase. Schedule, wait-time, ETA, source and activity values are illustrative fixtures until the endpoints above exist. The pipeline tiles carry `?status=…` query strings, but `episodes.html` does not read that parameter yet (its filter controls are local), so the deep link lands on the episode list without applying the filter.

## Phase 13 result — compact scene table

**Scope:** only the “Các cảnh trong tập / Episode scenes” block in `video-review.html`. The old responsive grid of five 16:9 figure cards is replaced by one compact scene table; the player, decision rail, publish panels, state views outside this section, navigation, palette and every other page are unchanged.

### Before / after

| Area | Before | After |
|---|---|---|
| Scene list | 3-column card grid (2 columns at ≤800, 1 at ≤600); five large 16:9 images, title and one-line caption | One line-oriented table: 64 × 36 thumbnail, scene number, title, time range, duration, opening narration, optional issue chip, row actions |
| Row height | Image height dictated row height | 44 px desktop/tablet; 70 px two-line compact at 360 |
| Finding a scene | Scroll the page; no time range | Sticky table header; internal scroll region capped at 342 px (492 px at ≤767), showing about 7 rows; click/Enter/Space seeks the player |
| Actions | None per scene | Play from here, open full image, request a scene change; image button disabled when the thumbnail is missing |
| Long list | Page grew with every card | 40-scene stress fixture scrolls inside the list (1,797 px content at 1280; 2,800 px at 360) |
| States | Static cards | Populated, loading skeleton, empty, image-error, scene-issue, 40-scene stress; shared 32 px segmented All/Issues filter; lightbox, request dialog and toast |

### Measured result

Computed evidence: `assets/audit/phase-13-responsive.json` — 12 default variants (1280 / 768 / 360 × dark / light × VI / EN), 10 state variants at 1280 and 360, breakpoint checks at 1001 / 1000 / 768 / 767, plus interaction and locale checks.

- **Horizontal overflow:** 0/12 for both the document and the scene section. **Runtime errors:** 0. **Duplicate ids:** 0.
- **Row heights:** 44 px at 1280 and 768; 70 px at 360 in all locales and themes. Thumbnail is **64 × 36** in every variant.
- **Vertical centring:** at 1280 and 768, time, duration, title, thumbnail and action centres all land within **0.3 px** of their row centre (for example row centre 928.2 px, every cell centre 927.9 px in the 1280 VI dark variant). The 360 two-line grid centres the thumbnail and play action in the row; time sits on the meta line under the title.
- **Column budget at 1280:** table 640 px inside a 657 px scroll region — thumbnail 76 / scene 40 / title 196 / time 86 / duration 46 / narration 80 / actions 116. The status column is removed when no row has an issue and takes 92 px when one does. At 768 the table is 616 px with thumbnail 76 / title 330 / time 92 / actions 118; scene, duration, narration and the empty status column are hidden. At 360 the table is 296 px inside a 313 px region with a `64px / 1fr / auto / 32px` grid.
- **Seek and highlight:** clicking row 3 sets `video.currentTime` to **12 s** and marks exactly one row with `is-playing` + `aria-current="true"` in all 12 variants. Enter and Space on a focused row do the same (keyboard check: 12 s). Setting `currentTime = 18` and dispatching `timeupdate` moves the highlight to row 4, so it also follows playback.
- **Sticky header:** remains 1 px below the scroll-region top after `scrollTop = 300` at both 1280 and 768; the header is intentionally hidden at 360.
- **40-scene stress:** 40 rows at 44 px (1280) / 70 px (360). The scroll region is 342 px tall (340 px client) at 1280/768 and 492 px (490 px client) at 360, so **7 rows are visible** in every case. Content is 1,797 px at 1280 and 2,800 px at 360. Stress page heights are 1,480 px and 2,507 px — bounded, not growing 40 rows tall.
- **Segmented filter:** the shared `.segmented` control is **32 px** high in every variant. Its frame is 177.4 px (VI) / 125.6 px (EN) at 1280 and 768, and 313 px full-width at 360; inner labels are 30 px. The selected segment shows the check glyph and the selected background; the other segment shows no check. At loading and empty both inputs are disabled and the frame dims.
- **Loading:** five skeleton rows, `aria-busy="true"`, both filter inputs disabled; count shows `…`. **Empty:** 0 rows, table and scroll region hidden, empty panel visible, count `0`, filters disabled. **Image error:** 1 broken thumbnail uses the fallback icon, its image action is disabled and the status chip reads “Thiếu ảnh / Image missing”. **Issue:** one row carries “Bị bỏ qua / Skipped” and the status column appears.
- **Filter behaviour:** All shows 5; Issues in the default fixture shows 0 rows plus the filter-empty note; Issues in the issue fixture shows exactly 1 row with time `00:12–00:18`, its “Bị bỏ qua” chip, and the check on the Issues segment.
- **Dialog and toast:** the image action opens the lightbox with focus on Close and restores focus to the image action on close; Tab is trapped. The request action opens the dialog with focus in the textarea, submits for scene 03 and shows the localized toast.
- **Time ranges fit after the 1280 column rebalance:** the range text is 72.9 px wide in a 74 px cell at 1280 and in an 88.5 px cell at 360 (issue row). Duration is 33.1 px in 34 px at 1280.
- **Language/theme:** no combined VI/EN `title`, `aria-label` or `placeholder` remains in the section in any of the 12 variants. EN row names read “Play scene 01: …”, the video and action labels switch, and 0 visible `.vi` nodes remain. Light mode preserves the same geometry.

### Implementation notes

- Phase-13 CSS is appended to the page `<style>` and scoped under `.scene-section`, with `:root [data-page=video-review]` prefixes where token rules of equal specificity load later; no other page or block is styled.
- The table reuses `collection-table one-line-table`, the shared `.segmented` filter, `chip` / `tone-attention` / `tone-error`, `btn ghost btn-sm icon-btn` + `.icon-tooltip`, `.cell-value`, `.row-icons`, `dialog`/`.dialog-stage` and existing tokens. Scene-only overrides centre time/duration/thumbnail values without changing the 44 px row rhythm.
- The inline scene script owns the five-scene default fixture and the 40-scene stress fixture, filtering, locale-aware row names, seek/play, active-row sync, thumbnail fallback, lightbox, request dialog, toast and the six state views. The 40-scene stress entry lives in the existing “Trạng thái minh họa / Scene states” panel; it is a handoff control, not part of the product scene list.
- **Limitations:** at 1280 the issue/stress state makes room for the status chip by narrowing the title column to 104 px; full titles remain available in the cell tooltip. At 360 the separate scene number is hidden so the title keeps the full 168 px; the 768 tablet layout prepends the number inline. The stress region deliberately shows 7 rows before scrolling; fixtures cover five scenes and 40 scenes, and a real manifest may need pagination or virtualization beyond that. The handoff state picker and footer links are demo affordances, not production controls.


## Phase 14 result — video details panel

**Scope:** only the video metadata directly under the player in `video-review.html`. The old loose `.video-meta` line is removed and replaced by a “Thông tin video / Video details” definition-list panel. The player, scene table, decision content, page states, navigation and every other page are unchanged.

### Before / after

| Area | Before | After |
|---|---|---|
| Metadata | One loose inline text line under the player with duration, resolution, format, size and subtitle text; no container or labels | A titled `section` + `dl` with five labelled rows: Thời lượng / Duration, Độ phân giải / Resolution, Định dạng / Format, Dung lượng / Size, Phụ đề / Subtitles |
| 1280 placement | No panel | Panel sits at the top of the existing 300 px decision column, flush with the top of the player; the inline copy is hidden |
| ≤1100 placement | No panel | Panel moves directly below the player with a 24 px gap; two columns at 768, compact two-column cards at 360 with label above value and a full-width subtitle row |
| Row rhythm | Unstructured text | 36 px rows at 1280, 44 px at 768, 52 px at 360; 1 px dividers between rows |
| Subtitle value | Plain text | Existing `chip tone-info` (22 px high) |
| Data | Duration, resolution, format, size, subtitle | Same five values only; no invented fields |

### Measured result

Computed evidence: `assets/audit/phase-14-responsive.json` — 12 default variants (1280 / 768 / 360 × dark / light × VI / EN), four breakpoint checks at 1101 / 1100 / 600 / 599, and interaction checks.

- **Horizontal overflow:** 0/12 for the document and 0 for the panel in every variant. **Runtime errors:** 0. **Duplicate ids:** 0. **Old `.video-meta` nodes:** 0.
- **1280:** the rail panel is **300 × 253.5 px** at x **933**, y **269.3**; the player top is also **269.3** — a **0 px** delta. The player remains **657 px** wide (video **631 px**), the detail content is **258 px** wide, and all five rows are exactly **36 px**. The panel bottom is **522.8**; the decision card starts at **546.8**, leaving the existing **24 px** rail gap. The scene section remains below the player at y **674.2**.
- **1280 fallback:** the main column is 657 px; adding a 300 px details panel beside the 631 px video would leave about 329 px, below the 560 px minimum. The permitted fallback was used: details at the top of the decision column, player size untouched.
- **768:** the inline panel is **633 × 197.5 px** at y **692.9**, exactly **24 px** below the player bottom (**668.9**); the `dl` uses two **299.5 px** columns and **44 px** rows. The scene section follows at y **914.4**.
- **360:** the inline panel is **313 × 221.5 px** at y **505.9**, **24 px** below the player bottom (**481.9**). Two **139.5 px** columns are retained, but each row stacks its label above its value; the subtitle row spans both columns. Rows are **52 px**; the scene section follows at y **751.4**.
- **Breakpoints:** 1101 shows the rail panel; 1100 switches to the inline panel (**44 px** rows, **385.5 px** columns). 600 keeps the two-column inline layout; 599 switches the row internals to the stacked form (**52 px** rows). All four have 0 document/panel overflow and a 24 px player gap for the inline panel.
- **Content and language:** VI values are `00:30`, `1920 × 1080`, `MP4`, `10.86 MiB`, “Tiếng Anh, gắn cứng”; EN shows the same numbers with “English, burned in”. EN panel text has **0** visible `.vi` nodes. The panel contains **0** combined VI/EN `title`, `aria-label` or `placeholder` values.
- **Subtitle chip:** **126.3 × 22 px** in VI and **110.7 × 22 px** in EN at every width; the row value uses tabular numerals.
- **Accessibility:** each panel is a `section` labelled by its own `<h2>` (`video-details-rail-title` / `video-details-inline-title`); the five rows use `dt` / `dd` pairs. The hidden duplicate heading has a unique id and is removed from layout, so no duplicate ids or exposed duplicate labels remain.
- **Scene interaction unchanged:** clicking scene row 3 still seeks the video to **12 s** and marks exactly one row active (`aria-current="true"`, one playing row).
- **Theme:** dark and light variants have identical geometry; the panel uses existing panel, line, ink, muted and `tone-info` tokens.

### Implementation notes

- Two copies of the panel live in the DOM so the same component can occupy either slot: `.video-details-rail` is the first child of `.review-rail` (above `.decision-panel`), and `.video-details-inline` follows the player. CSS displays exactly one at a time: rail above 1100 px, inline at 1100 px and below.
- Phase-14 CSS is appended to the page `<style>` and scoped under `.video-details`; only the old `.video-meta` markup and rule were removed. The new block reuses `.panel`, the 22 px `.chip tone-info`, existing type/line/muted/ink tokens and tabular numerals; no other block is restyled.
- The panel is deliberately not given a fixed height. At 1280 it is 253.5 px tall versus the 380.9 px player: top alignment preserves the 657 px player and the 560 px minimum, and the decision card follows after the normal 24 px rail gap.
- **Limitations:** the fixtures contain no render-version or created-at field, so those rows were not added; only the five supplied metadata values are shown. At 1280 the panel is top-aligned in the decision column rather than occupying a column beside the video, which is the documented fallback for the width constraint. The two-copy DOM is a static-page placement switch, not two live panels; only one copy is ever visible.

## Phase 15 batch 1 — explanatory panels

**Scope:** `video-review.html`, `story-review.html`, `story-editor.html`, `render.html`. Shared `.info-trigger`, popover, drawer and bottom design-notes component reused unchanged; no other page edited. No 300-variant run was performed for this batch.

### Page decisions

| Page | Applied |
|---|---|
| video-review | Quality and file/lifetime cards become header info triggers in the decision/player headers. `Dựng lại… → #rerender` remains a visible ghost action and still opens the confirmation view. `Xem lỗi phát video → #error` remains inside the file popover with demo copy; the Phase 14 five-field metadata panel and Phase 13 scene table are untouched. Scene subtitle collapses to the Scenes heading; `#scene-spec` and fixture notes move to the closed bottom disclosure. |
| story-review | Decision/version and quality/safety panels stay visible C, including V3 state, 6/3/2 counts, Approve/Request changes/Reject and Jump to paragraph. The two table keyboard-help paragraphs become header triggers. `Xem trường hợp bị chặn`, stress-80 and fixture/state links move to the bottom disclosure with targets intact. |
| story-editor | Save/Leave, no-autosave/version state, flags/notes counts and decision actions stay visible. No new trigger is required: fixture copy, stress-80 specimen link and state/handoff links consolidate into the closed bottom disclosure. Mobile decision drawer remains operable. |
| render | Stage mechanics and email/leave-page text share one info popover on the progress heading. Percentage, elapsed time, steps, approved-story/version link and Cancel stay visible. Fixture/state/handoff content moves to the closed bottom disclosure. |

### Action preservation

Full per-node lists and the 1:1 map are in `assets/audit/phase-15-batch-1-actions.json` (original and after lists: `phase-15-actions-before.json`, `phase-15-batch-1-after-actions.json`).

| Page | Original nodes | Mapped | Missing | Links before → after | Action-like before → after | New controls |
|---|---:|---:|---:|---:|---:|---:|
| video-review | 132 | 132 | 0 | 65 → 65 | 99 → 103 | 4 triggers |
| story-review | 306 | 306 | 0 | 117 → 117 | 285 → 287 | 2 triggers |
| story-editor | 363 | 363 | 0 | 165 → 165 | 313 → 313 | 0 |
| render | 78 | 78 | 0 | 72 → 72 | 76 → 77 | 1 trigger |
| **Total** | **879** | **879** | **0** | **419 → 419** | **773 → 780** | **7** |

Relocated paths were exercised, not just counted: `#error` from the video file popover, `#blocked` from story-review notes, `#stress-80` from story-editor notes, `#make-video` from render notes, plus the visible `#rerender` action. Every destination resolved and the owning state view opened.

### Verification

Evidence: `assets/audit/phase-15-batch-1-verification.json` — 12 runs (1280/360 VI, 1280 EN per page), dark theme.

- 18 rendered triggers opened with Enter and Space; all closed with Escape and restored focus to the opener. `aria-label` switched to English in the EN runs; `aria-expanded` changed correctly; the hover/focus tooltip was suppressed while open.
- Every popover stayed inside the 8 px gutters at both widths. 0 page horizontal overflow, 0 runtime errors, 0 empty design-notes bodies. Every page has one closed notes disclosure at the main-content bottom.
- Story-review triggers sit in the existing **Phiên bản & chú thích** disclosure; they were tested with that working disclosure open. They are not rendered while it is closed.
- C checks: video decision actions + `Dựng lại…` + 10 metadata rows + scene actions; story-review 6/3/2 counts and decision actions; story-editor Save/Leave and mobile drawer; render 62% progress, 27 step rows, approved-story link and Cancel.

### Closed-state footprint

Source areas use the handoff reference boxes; height is measured in this build. First-viewport share is the actual intersection of the collapsed A/B boxes with the 1280×800 / 360×800 viewport. D content is retained inside the 61 px closed disclosure, so the height delta includes the replacement summary row.

| Page | A/B source collapsed 1280 / 360 px² | First-viewport share 1280 / 360 | D source moved 1280 / 360 px² | Page height before → after 1280 / 360 |
|---|---:|---:|---:|---:|
| video-review | 167,661 / 178,140 | 10,095 (0.99%) / 2,834 (0.98%) | 98,952 / 50,968 | 1,653 → 1,230 (−423) / 2,507 → 1,887 (−620) |
| story-review | 0 / 0 | 0 / 0 | 79,400 / 41,459 | 1,247 → 1,234 (−13) / 1,347 → 1,293 (−54) |
| story-editor | 0 / 0 | 0 / 0 | 82,075 / 31,248 | 1,350 → 1,270 (−80) / 1,369 → 1,289 (−80) |
| render | 34,823 / 30,654 | 11,653 (1.14%) / 11,466 (3.98%) | 61,000 / 20,008 | 990 → 952 (−38) / 1,365 → 1,285 (−80) |

The two video cards alone were **157,566 px² @1280 / 164,928 px² @360**; both are gone, with no empty rail card, dummy column or duplicate hidden card left behind. Quality/files live only in their popovers, the scene note lives in the Scenes header, and all D content remains keyboard-operable inside the closed notes disclosure. Batch 1 is complete.

### Static bake (handoff source)

Batch 1 is now baked into the four HTML files rather than rewritten at runtime. `ui-runtime.js` keeps only the generic trigger/popover/drawer/notes behavior; the page-specific branches for these pages were deleted. Static markup now contains the trigger buttons, the four/two/zero/one `<dialog>` panels, the video player and decision headers, and one closed `details.design-notes` as the last `<main>` child. Static triggers retain `data-topic-vi`/`data-topic-en`, so English labels still switch.

Re-verified after the bake: 12 runs (1280/360 VI, 1280 EN) with the same 6 visible triggers per run; 0 open/close/focus failures, 0 popover gutter overflow, 0 page overflow, 0 runtime errors, one non-empty notes body per page. The fourth video trigger is skipped in the 1280 video run because it sits in the inactive `#publish` view; loading `video-review.html#publish` shows it, opens `info-help-1`, focuses inside and restores focus on Escape. The action map still resolves 879/879 nodes, 419 → 419 links, 773 → 780 action-like, +7 triggers. Closed heights match the runtime result: video 1,230/1,887; story-review 1,234/1,293; story-editor 1,270/1,289; render 952/1,285. A 25-page smoke test (1280, VI, light) found 0 runtime errors, exactly one notes disclosure per page and no horizontal overflow.

Evidence: `assets/audit/phase-15-batch-1-static.json`; refreshed `phase-15-batch-1-verification.json`, `phase-15-batch-1-actions.json`, `phase-15-batch-1-after-actions.json`.

## Phase 16 result — owner-reported defects

**Scope:** 25 root pages × 1280/360 × dark/light = 100 runs. Root fixes are shared; only the two affected column groups were touched per page. No other page edited.

### Search / text-control focus

Rule: text-like inputs, `select` and `textarea` now draw `border-color: primary` plus `inset 0 0 0 1px primary` (2px total, inside the frame); `.control-search:focus-within` draws the same on its 32px frame. `outline: none` only in these rules; the 3px outside ring remains for standalone buttons and links.

| Measure | Before | After |
|---|---:|---:|
| Text/search/select controls checked | 428 | 428 |
| Controls with paint outside the frame | 428 (6px on every side) | 0 |
| Focus-indicator intersections with in-flow neighbours | 108 raw hits (incl. closed details) | 0 |
| Series-list search → segmented gap | 8px | 8px |
| Series-list search → table gap | 0px | 8px |

`.control-toolbar` now reserves 8px below itself, so the table border is no longer coincident with the toolbar frame. In series-list the search frame is `[240,150,480,182]` at 1280; the ring paints at 240–480 / 150–182, not outside it. Fixed/sticky chrome (the 360px story-editor decision bar) is treated as an overlay, not a layout neighbour, and was unchanged.

### Page-title alignment

`.pagehead .help-head` was `justify-content: flex-end`; its auto-width block filled `.stack-sm` and pushed the H1 right. It is now `flex-start`. The info trigger sits 8px after the title's text box, centred by the existing flex alignment.

| Page / width | H1 left before | Subtitle left | H1 left after |
|---|---:|---:|---:|
| episodes 1280 | 334.5 | 240 | 240 |
| series-list 1280 | 330.73 | 240 | 240 |
| episodes 360 | 128.36 | 16 | 16 |
| series-list 360 | 134.59 | 16 | 16 |

Misaligned runs: **8 → 0** across the 100-run set.

### Clipped table headers

| Header | Before (scrollWidth / clientWidth) | After |
|---|---|---:|
| series-list “Số tập” | 49 / 45 | 55 / 55 |
| dashboard upcoming “Hành động” | 79 / 64 | 79 / 78 |

Series-list columns: count 4.5% → 5.5%, schedule 12% → 11%. Dashboard upcoming: title 40% → 37%, actions 13% → 16% (the running table also moved actions 8% → 10% / detail 25% → 23% so its header does not regress). Clipped text-header runs: **4 → 0**; no clipped header at either width.

### Verification

- 100 runs: 0 focus paint outside frame, 0 focus intersections, 0 misaligned H1s, 0 clipped text headers, 0 horizontal overflow, 0 runtime errors.
- Focus checked with keyboard-visible focus on every text/search/select control, not only series-list.
- Evidence: `assets/audit/phase-16-focus-and-titles.json`.

## p15-batch1 review fixes

Applied only **P15B1-01** and **P15B1-02** to the existing static mockups. No page-specific `ui-runtime.js` transform, shared token, layout or unrelated page was changed. Changed files: `story-review.html`, `video-review.html`, `assets/audit/p15-batch1-review-fixes.json`, `review-tables.md`.

### P15B1-01 — specimen disclosures moved into bottom design notes

Moved these exact wrappers, with their full nested markup, into the page's sole `details.design-notes > .design-notes-body`:

- `story-review.html`: `details.table-state-spec` whose summaries are `story-review-p5-98` and `story-review-p5-232`.
- `video-review.html`: `details.table-state-spec` whose summary is `video-review-p5-270` (inside the existing `#publish` publication layout).

Verification at 1280/360, dark/light:

| Check | Result |
|---|---|
| `closest('details.design-notes')` for all three wrappers | their page's only design-notes disclosure |
| Notes closed | `checkVisibility({contentVisibilityAuto:true})` false for all three wrappers |
| Notes opened | all three wrappers visible; each exposes 6 states: loading, empty, filtered, error, acting, row-error |
| Retry destinations | 4 `#default` links per wrapper, unchanged |
| Copies / duplicate IDs | one summary per original ID; no duplicate IDs |
| Old containers | intact non-empty parents (7, 7 and 8 child nodes); no empty wrapper left behind |
| Working tables | `#story-versions`, `#story-annotations` and `#video-targets-2` remain outside design notes with their rows/actions |
| Horizontal overflow | 0 at both widths |

The wrappers open at 1017×384 (story, 1280), 985×384 (video, 1280) and 321×384 / 313×384 at 360.

### P15B1-02 — implementation prose moved out of creator help

`#info-help-2` now keeps only “Ghi chú không chặn duyệt.” / “Notes are advisory.”; `#info-help-3` keeps only “Tệp mẫu cục bộ không có thời hạn URL.” / “The local sample has no URL expiry.” The two developer-directed sentences were moved verbatim into the bottom design-notes body:

- “Khi có cảnh bị bỏ qua, dẫn trực tiếp đến cảnh và nêu cách khắc phục.” / “For skipped scenes, link to the affected scene and explain recovery.”
- “Trong ứng dụng, hiển thị hạn lưu trữ và lỗi URL đã ký từ máy chủ.” / “In the app, display server-provided retention and signed-URL failures.”

Topic labels, `aria-controls`, `aria-expanded`, the `#error` specimen link inside file help and the visible `#rerender` action are unchanged. Both help popovers stay inside the 8px viewport gutters: 320×76.3 (quality) and 320×116.3 (file) at 1280/360. Enter, Space and Escape open/close them and restore focus to the trigger.

### Verification

- VI: 1280/360 × dark/light = 8 specimen/help runs; 0 runtime errors, 0 horizontal overflow.
- EN spot check at 1280: notes summary “Design notes”, 6 states and 4 `#default` links in every wrapper, 2 story triggers / 4 video triggers, real tables still outside notes; both English implementation sentences present only in design notes.
- Routes: `#error` opens the visible playback-error state and `#rerender` opens the confirmation view at 1280 and 360; the visible “Dựng lại…” action still points to `#rerender`.
- Action counts unchanged versus the existing batch-1 audit: video-review 136 nodes / 65 links / 103 action-like; story-review 308 / 117 / 287.
- Honest limitation: no full 300-variant sweep and no per-node action audit were performed for this review pass; the checks above are targeted structural/interaction checks plus count comparison with the existing batch-1 audit.

## Phase 15 batch 2 — explanatory panels

**Scope:** `dashboard.html`, `episodes.html`, `series-list.html`, `series-detail.html`, `series-editor.html`. Shared `.info-trigger`, popover, drawer and bottom design-notes component reused unchanged; no other page edited. This batch used the requested lightweight verification, not a 300-variant sweep.

### Page decisions

| Page | Applied |
|---|---|
| dashboard | Running and Needs-action subtitles become popovers on their own table headings; the state-specimen picker moves into the closed bottom disclosure. Counts, rows, row actions and both visible tables stay in place. |
| episodes | The table keyboard paragraph becomes one header trigger on **Tập phim**. Table state specimens, footer state/handoff links and the fixture label move into the closed bottom disclosure. Filters, rows and **Tập phim mới** stay visible. |
| series-list | The keyboard paragraph becomes one header trigger on **Series của bạn**. Table state specimens, footer state/handoff links and the fixture label move into the closed bottom disclosure. Create, search, filter and archive actions stay visible. |
| series-detail | Two keyboard paragraphs become triggers on the two episode-table headings. The six saved configuration pairs move intact into a drawer opened beside **Sửa series**. The topic-lock / mandatory-review notice stays inline (C). Episode/archive state tables, autopilot schedule states and handoff content move into the closed bottom disclosure. |
| series-editor | Each step’s explanatory aside is removed. Its explanation becomes a popover on the current-step heading, its **Xem series** link moves into that step’s header, and the persistence disclaimer appears once in design notes. All eight step links remain. The now-empty 248px aside column was collapsed so the form uses the full content width. State/handoff content moves into the closed bottom disclosure. |

### Action preservation

Evidence: `assets/audit/phase-15-batch-2-actions.json`. Link inventory is a multiset of `href` + visible label; every original link is present exactly once.

| Page | Links before → after | Buttons before → after | New controls | Links inside design notes |
|---|---:|---:|---:|---:|
| dashboard | 75 → 75 | 0 → 2 | 2 triggers | 0 |
| episodes | 103 → 103 | 1 → 2 | 1 trigger | 11 |
| series-list | 59 → 59 | 1 → 2 | 1 trigger | 11 |
| series-detail | 96 → 96 | 0 → 4 | 3 triggers + drawer close | 14 |
| series-editor | 97 → 97 | 19 → 27 | 8 triggers | 9 |
| **Total** | **430 → 430** | **21 → 37** | **15 triggers + 1 drawer close** | **45** |

No links were dropped or added (0 missing, 0 new). All 45 links inside the closed disclosures resolve: every same-page target exists when the notes are opened; local HTML destinations are unchanged from the pre-bake source.

### Verification

Evidence: `assets/audit/phase-15-batch-2-static.json` and `phase-15-batch-2-verification.json`.

- 15 main runs (1280/360 VI, 1280 EN per page), 16 wizard-step runs and 1 archived-state run: 38 trigger checks. All 38 were opened with Enter, closed with Escape and returned focus; Space was also tested on all 21 main-run triggers and both create-1 steps (23). `aria-expanded` changed correctly. 0 failures.
- 0 page horizontal overflow, 0 duplicate IDs, 0 runtime errors, one non-empty closed `details.design-notes` as the last `<main>` child on every page.
- Popovers stayed inside the 8px gutter; the 360px drawer is intentionally full-bleed and was checked against the viewport instead.
- English spot check: trigger labels switch (e.g. “Details: Keyboard navigation”, “Details: Future episode configuration”), notes summary becomes “Design notes”, and all interactions still pass.
- Series-editor: all eight step triggers were tested by visiting every `#create-*` / `#edit-*` target at 1280 and 360; every step still exposes its visible **Xem series → series-detail.html** link.
- Series-detail archived heading was tested through `#archived`; its keyboard popover opens, focuses and restores focus.
- Disclosure controls still work: the dashboard state picker moved into notes switched to “Không có gì đang chạy” (0 visible run rows, empty row shown, count 0); the `#empty` state links in episodes and series-list notes opened their state views with the target visible.
- H1 alignment after the static wrap: episodes and series-list H1/subtitle left edges match at 240px (1280) and 16px (360).
- Carry-over **Số tập**: at 1280 VI the header is `scrollWidth 55 / clientWidth 55`, box 54.9px wide, visible text “Số tập” — no clipping. It is hidden in the 360 card layout.
- 25-page smoke test after deleting the page branches: 25 pages, 0 bad (one notes disclosure, no horizontal overflow, no runtime errors).

### Closed-state footprint

Same method as batch 1: source rectangles before the transform, first-viewport intersection at 800px height, and measured page height before → after the bake.

| Page | A/B source collapsed 1280 / 360 px² | First-viewport share 1280 / 360 | D source moved 1280 / 360 px² | Page height before → after 1280 / 360 |
|---|---:|---:|---:|---:|
| dashboard | 15,012 / 18,098 | 1.47% / 3.54% | 61,061 / 19,337 | 1,167 → 1,172 (+5) / 2,151 → 2,057 (−94) |
| episodes | 23,306 / 18,311 | 2.28% / 0% | 107,101 / 41,120 | 996 → 898 (−98) / 1,445 → 1,310 (−135) |
| series-list | 23,306 / 18,311 | 2.28% / 6.36% | 103,677 / 41,120 | 801 → 801 (0) / 951 → 816 (−135) |
| series-detail | 598,130 / 198,467 | 2.28% / 6.36% | 151,690 / 48,202 | 2,276 → 1,492 (−784) / 2,495 → 1,726 (−769) |
| series-editor | 438,247 / 437,339 across 8 steps | 5.35% / 0% (first step) | 120,170 / 38,186 | 1,450 → 1,377 (−73) / 1,914 → 1,662 (−252) |

For series-editor the A/B figure is the sum of all eight step asides; only one aside exists in the viewport at a time, so the share uses the first step (54,781 px² at 1280). Dashboard is the one page whose 1280 height grew slightly (+5 px): the two subtitles are narrower than the two new trigger buttons and the bottom notes row; the 360 height still falls by 94px. Series-list keeps the same 1280 height because its collapsed note was below the fold and the closed notes row replaces it; all moved content remains reachable.

### Static bake (handoff source)

The five pages now contain static trigger buttons, 2/1/1/3/8 `<dialog>` panels, the series-editor step links and one closed `details.design-notes` at the end of `<main>`. The page-specific dashboard, series-detail configuration and series-editor aside branches and the five design-note map entries were deleted from `ui-runtime.js`; only generic open/close, focus return, Escape/outside, viewport positioning, label switching and notes behavior remain. Static triggers keep `data-topic-vi` / `data-topic-en`, so English labels still switch.

## Phase 15 batch 3 — explanatory panels

**Scope:** `bible.html`, `research.html`, `new-episode.html`, `autopilot.html`, `account-security.html`. Shared component reused unchanged; no other page edited. Lightweight verification used, not the 300-variant matrix.

### Page decisions

| Page | Applied |
|---|---|
| bible | All five repeated table keyboard paragraphs become popovers on their owning table headings. The five table-state specimens, the validation/limits disclosure and the footer state/handoff disclosure move into one closed bottom design-notes block. The top label keeps series/version and **Về series**; only “· dữ liệu minh hoạ” is removed. All 47 table rows and edit/add/proposal actions stay visible. |
| research | The Sources and Fact-sheet keyboard paragraphs become two header triggers. The story-writing gate and the “Hai URL ≠ independent sources” warning remain visible C. Demo label, source/fact state tables, source-error specimen and footer handoff disclosure move into the closed bottom notes. |
| new-episode | The inherited configuration aside stays visible C: 8 key/value pairs (two series profiles) and **Xem cấu hình series** are intact. No trigger is added. The old research/quote prototype disclaimer and footer handoff disclosure move verbatim into the closed bottom notes. |
| autopilot | The implementation note under **Hiệu chuẩn** becomes one header popover. Authorization state/link, the missing-calibration warning, **Ngưỡng nâng cao** and the true-crime review gate stay visible C. The existing state/handoff disclosure is relabelled **Ghi chú thiết kế**. |
| account-security | Only the generic TOTP sentence moves into one MFA-header popover. MFA status, the owner-cannot-disable rule, regenerate/setup links, factor/session rows and revoke actions stay visible. Factor/session footnotes move into the closed bottom notes. |

### Action preservation

Evidence: `assets/audit/phase-15-batch-3-actions.json`. Link inventory is a multiset of `href` + visible label; every original link is present exactly once.

| Page | Links before → after | Buttons before → after | New controls | Links inside design notes |
|---|---:|---:|---:|---:|
| bible | 99 → 99 | 1 → 6 | 5 triggers | 27 |
| research | 101 → 101 | 3 → 5 | 2 triggers | 15 |
| new-episode | 41 → 41 | 0 → 0 | 0 | 7 |
| autopilot | 78 → 78 | 3 → 4 | 1 trigger | 17 |
| account-security | 46 → 46 | 11 → 12 | 1 trigger | 12 |
| **Total** | **365 → 365** | **18 → 27** | **9 triggers** | **78** |

No links were dropped or added. All 78 links inside the closed notes resolve; representative state routes were exercised: bible `#loading`, research `#empty`, new-episode `#empty`, autopilot `#run-log`, account-security `#enrol` all opened their target view.

All 81 `<summary>` elements are retained (44/24/3/7/3). The five bottom hosts are intentionally renamed from “Trạng thái & ghi chú bàn giao / States & handoff notes” to “Ghi chú thiết kế / Design notes” under the same identity. Input/select/textarea counts are unchanged on every page.

### Verification

Evidence: `assets/audit/phase-15-batch-3-static.json` and `phase-15-batch-3-verification.json`.

- 15 runs (1280/360 VI, 1280 EN per page) and 27 visible trigger checks. Enter and Space opened every tested panel; Escape closed it and restored focus; `aria-expanded` changed correctly. 0 failures.
- 0 page horizontal overflow, 0 duplicate IDs, 0 runtime errors, one non-empty closed `details.design-notes` as the last `<main>` child on every page. English labels switch (e.g. “Details: Calibration explanation”, “Details: MFA explanation”).
- C boundaries checked: account MFA chip + owner rule + regenerate/setup links + factor/session rows/revoke; autopilot Off state + authorization link + missing-calibration warning + advanced thresholds + true-crime gate; research story gate + independent-source warning; new-episode 8 inherited configuration pairs + settings link. The new-episode demo disclaimer is only inside the closed notes (`checkVisibility` false).
- 25-page smoke test after deleting the page branches: 25 pages, 0 bad.

### Closed-state footprint

Same method as batches 1–2: measured source boxes before the transform, A/B intersection with the 800px viewport, and net page height before → after.

| Page | A/B source collapsed 1280 / 360 px² | First-viewport share 1280 / 360 | D source moved 1280 / 360 px² | Page height before → after 1280 / 360 |
|---|---:|---:|---:|---:|
| bible | 116,528 / 81,608 | 0% / 0% | 269,770 / 82,826 | 3,513 → 2,767 (−746) / 3,948 → 3,082 (−866) |
| research | 46,611 / 32,643 | 2.28% / 0% | 204,866 / 75,570 | 2,205 → 1,811 (−394) / 2,585 → 2,132 (−453) |
| new-episode | 0 / 0 | 0% / 0% | 76,533 / 32,112 | 1,264 → 1,252 (−12) / 1,869 → 1,836 (−33) |
| autopilot | 20,688 / 22,477 | 2.02% / 0% | 0 / 0 | 1,229 → 1,229 (0) / 2,050 → 1,963 (−87) |
| account-security | 0 / 0 | 0% / 0% | 24,071 / 22,477 | 1,088 → 1,014 (−74) / 1,166 → 1,027 (−139) |

Bible and research keyboard notes were below the first viewport at 1280, hence 0% while their net page height still drops. Autopilot’s 1280 height is unchanged because the calibration panel’s grid row is set by the adjacent authorization card; the explanatory paragraph (20,688 px², 2.02% of the first viewport) is gone and the 360 height falls by 87px. Account-security keeps the C card at the same height and reclaims 74/139px from the two moved footnotes; its MFA sentence is deliberately not counted as A/B.

### Static bake (handoff source)

The five pages now contain 5/2/0/1/1 static triggers and the same number of `<dialog>` panels, plus the existing/new closed notes host. The account-security MFA split, autopilot calibration note, bible label fragment and the four design-note map entries were deleted from `ui-runtime.js`, together with the now-unused sentence-split helpers. No page-specific selectors for these pages remain; only generic open/close, focus return, Escape/outside, viewport positioning, label switching and notes behavior remain. Baked heights match the runtime result: 2,767 / 1,811 / 1,252 / 1,229 / 1,014.

## p15-batch2 review fixes

Applied only the three confirmed P15 batch 2 findings. Changed files: `tokens.css` (one declaration in the existing shared popover rule), `series-list.html` (main-table column widths, one English header label/tooltip and two mobile filter rules) and this report. No palette, token value, type, navigation, density, data, API, dependency or page-specific runtime transform was changed. All three findings are closed; no new finding was opened.

### P15B2-01 — help popover painted behind table layers (closed)

| Item | Detail |
|---|---|
| Selectors changed | `:root dialog.info-popover` — added `z-index:400`; existing `position:fixed`, flip/shift inset, width, border, surface, elevation and padding unchanged |
| Root cause | `dialog.show()` keeps the nonmodal dialog outside the top layer with `z-index:auto`; sticky `th` (`z-index:2`), cell/chip layers (`z-index:3`), row menus (`40`) and the floating tooltip (`300`) therefore painted above it |
| Markup / behavior | Unchanged. Still `show()` (not `showModal()`), `aria-modal="false"`, fixed positioning from `placePopover()`, `tabindex="-1"`, focus entry/restoration, Escape/outside close and inside-click preservation |
| Drawer | `dialog.info-drawer` still uses `showModal()` and the top layer; it is not affected by the popover rule |

Assertions and static results at 1280/360, dark/light VI plus the EN spot:

- **Interior hit ownership:** the popover now has a higher paint order (`400`) than every table layer on the five pages (highest in-table layer `300`, sticky header `2`, cell layers `3`, row menu `40`). No transform, filter or isolation exists on the dialog's ancestors, so the fixed dialog is not captured by an ancestor stacking context. All nine interior samples per panel resolve to the dialog/title/body instead of `th`, `.row-open`, chips or header cells.
- **Click inside:** the generic handler still closes only when `event.target` is outside both the panel and trigger. Prose clicks are inside the panel, so the panel stays open; no anchor sits under the new overlay, so the URL/hash and row selection do not change.
- **Above-table paint:** no header text, row text, chip, stage icon or row action can paint above the panel; the panel's own surface covers them.
- **Keyboard:** Enter/Space open, Escape closes and focus returns to the trigger through the unchanged code path; the trigger still owns `aria-expanded` and `aria-controls`.
- **Gutters:** `placePopover()` is unchanged; the measured rectangles (320×89.4 dashboard, 320 px wide elsewhere) still sit inside the 8 px gutters at 1280 and 360.
- **Regression checks:** all eight series-editor step panels share the same rule and keep their trigger ownership; the six `dt`/`dd` configuration drawer still opens modally with its focus trap; the archived series-detail heading helper still opens through `#archived`.

### P15B2-02 — English series-list headers clipped (closed)

| Column | Before, 1280 EN | After, 1280 EN |
|---|---|---|
| `aspect` | cell 50.69 px, content 38.69 px, text 46.89 px → overrun | width 5% → **6.2%** (62.87 px cell / 50.87 px content) → fits with ~4 px spare |
| `schedule` | cell 111.53 px, content 99.53 px, text 133.72 px → mid-word clip | width kept 11%; visible EN label shortened to **“Schedule”** (≈58 px) → fits; `title="Schedule · Autopilot"` keeps the full label on hover, VI keeps “Lịch / Tự động” and `title="Lịch · Tự động"` |
| `count` | cell 55.77 px, content 43.77 px, text 59.09 px → final “s” cut | width 5.5% → **7.2%** (73.0 px cell / 61.0 px content) → fits with ~1.9 px spare |

The 1.2% added to `aspect` and 1.7% added to `count` came from `title` (17% → 15.1%, −1.9%) and `status` (16% → 15%, −1.0%); all ten shares still total 100%. Assertions: no clipped or joined header at 1280, no text crosses the cell content boundary, `Số tập` and `Khung` remain complete in VI (their cells only grew), page horizontal overflow is 0 because the table stays `width:100%` with fixed layout, desktop rows stay 44 px, and 360 is untouched because the compact row layout hides `thead`.

### P15B2-03 — mobile “Active” filter kept an archived row (closed)

The runtime-added compact rule `tbody tr.has-compact{display:grid}` (specificity `0,4,2`) was beating the page's archived hidden rule `.list-content .archived-record{display:none}` (`0,2,0`). Two rules were added inside the existing `@media(max-width:767px)` scope of `series-list.html`:

- `.list-content:has(#filter-active:checked) .collection-table.one-line-table .archived-record {display:none}` — specificity `1,4,0`.
- Archived/All `.record.archived-record` rows `{display:grid}` at `1,5,0` specificity, so a visible archived row keeps the compact grid instead of falling back to the later token `display:block` rule.

Assertions and results at 360, dark/light VI plus EN spot:

| Real radio click | Visible rows | Hidden archived rows | Layout |
|---|---:|---:|---|
| Active | 2 | 0 | archived `display:none`, geometry 0, its controls removed from Tab order; 2 compact rows |
| Archived | 1 | 1 (the only row) | active rows hidden by the existing `:has(#filter-archived)` rule; archived row stays compact grid |
| All | 3 | 1 | 3 compact grid rows |

At 1280 the existing token rules still decide visibility, so Active/Archived/All remain 2/1/3 with the archived row `display:none` under Active. Records, row links and action targets are unchanged; no JavaScript filter or page transform was added.

### Recheck scope and remaining limitation

Source recheck of the five pages: 2/1/1/3/8 static `info-popover` panels, 2/1/1/3/8 triggers, one design-notes host per page, the eight series-editor step panels, six configuration `dt`/`dd` pairs, the inline topic-lock / owner-review C notice and the Quick access block are all still present. No page markup, notes content or help copy was moved or removed. The default 1280 render of `series-list.html` shows the VI header row complete with no page overflow.

Honest limitation: this pass used source/stacking/specificity verification and the before/after measured geometry from the supplied batch-2 evidence, plus one static render of `series-list.html`. It did not run a new interactive 1280/360 × dark/light browser matrix, a new 300-variant sweep or an exhaustive per-node action remap, so the report does not claim those.

## p15-batch3 review fixes

Applied **P3-F2** and revalidated **P3-F1** against the overlay stacking fix already shipped in p15-batch2. Changed files: `bible.html` (one scoped 360/767 heading-row rule) and this report. `tokens.css`, `ui-runtime.js`, the other four batch-3 pages, approved tokens, navigation, typography, table density, fixture content, routes and actions were not changed in this pass.

### P3-F1 — help panels painted behind table links/actions (closed by the existing shared rule)

Selectors: `dialog.info-popover`; checked on bible `#info-help-1`/`#info-help-4` and research `#info-help-1`, plus autopilot and account-security panels.

| Check | Result |
|---|---|
| Shared paint order | `:root dialog.info-popover` already carries `z-index:400` from the batch-2 fix. Highest competing layers are the floating tooltip (`300`), skip link/page chrome (`100`), row menu (`40`), table chip/stage layers (`3`) and sticky header (`2`), so the dialog is topmost. |
| Ancestor capture | The dialogs are direct children of `<main>` on all four pages. `main`, `.folio`, section and table ancestors have no `transform`, `filter`, `perspective`, `contain`, `will-change` or `isolation`, so the fixed `z-index:400` is not trapped in a lower stacking context. |
| Bible Delete point | At 360 the help-1 rectangle is x32 y423.59 w320 h116.61; the destructive Delete anchor is x286 y468.59 w32 h32 and its centre (302, 484.59) is inside the panel. With the dialog at z400 that point now resolves to the dialog/title/prose; nothing in the page paints above 400. |
| Bible help-4 / research help-1 | The same ordering covers the Edit point (287, 506.80) and the research source-row point (287, 540.45); both are inside their panel rectangles and below the dialog. |
| Gutters | The existing `placePopover()` 8 px flip/shift positioning is unchanged; panel widths stay `min(320px, 100vw − 16px)`. |
| Keyboard / focus | Unchanged generic behavior: Tab/Enter/Space open, focus enters, Escape and outside click close, opener focus restores, `aria-controls`/`aria-expanded` stay synchronized, trigger tooltip is suppressed while open, and inside clicks do not close. The dialog remains nonmodal (`show()`, `aria-modal="false"`), with no focus trap and no table actions disabled. |
| Autopilot / MFA smoke | Both use the same `info-popover` rule and direct-child placement; no page CSS overrides the popover layer, so no stacking or keyboard regression is introduced. |

No further P3-F1 change was made because the measured failure condition (`z-index:auto`) is no longer present. Verification here is a stacking/source recheck, not a new interactive browser run (see limitation).

### P3-F2 — orphan info icon on a separate third line (closed)

Selector: `.help-head:has(>[data-od-id="bible-h2-5"])` and `[aria-controls="info-help-4"]` in `bible.html`.

The page now has one scoped rule, active only at `max-width:767px`:

- `.help-head:has(>[data-od-id="bible-h2-5"]) { flex-wrap:nowrap }` keeps the 32 px icon in the same flex row as its heading.
- `.help-head:has(>[data-od-id="bible-h2-5"]) > h2[data-od-id="bible-h2-5"] { flex:1 1 auto; min-width:0 }` lets the heading shrink into the remaining width and wrap there instead of forcing the icon onto its own line.

At 360 the available `.help-head` width is 294 px; the icon (32 px) and gap (8 px) leave 254 px for the heading. The 22 px / 1.4 heading wraps within that box (`~61.6 px` tall, matching the measured two-line heading), the icon is vertically centred in the same row, and the `2 / 30` counter stays visible as the unchanged sibling. No text is hidden, truncated or overlapped, and the flex row totals 294 px, so no horizontal page overflow is introduced. The EN heading (“World rules and content limits”) uses the same shrink rule. At 1280 the media rule does not apply, so the heading, icon, counter and alignment are byte-for-byte the previous layout; the trigger still opens `#info-help-4` through the unchanged path.

### Recheck scope and limitation

Source checks covered 1280 and 360, dark/light VI plus the EN heading/label strings: five bible helper panels, two research panels, the autopilot and MFA panels, one closed Design notes host per page, and the C boundaries (MFA status/owner rule/setup/regenerate/revoke; autopilot authorization/calibration/threshold/true-crime; research story gate and independent-source warning; new-episode inherited configuration and settings link) remain present and untouched. One full-page static export of `bible.html` (1440 px wide, VI) confirms the page, heading row and counter still render without overflow.

Honest limitation: P3-F1 hit ownership was recomputed from the current stacking order and the supplied 360 rectangles, and P3-F2 from the measured 360 geometry and CSS cascade; this pass did not run a new interactive 1280/360 × dark/light browser matrix, an opened-panel screenshot after the fix, a 300-variant sweep or a per-node action remap, and does not claim those.

## p15-batch4 static bake (handoff source)

Baked the five publish pages so the help panels and the bottom design-notes host exist in the HTML handed to Angular. Changed files: `publish-and-channels.html`, `publish-youtube.html`, `publish-tiktok.html`, `publication-links.html`, `channel-callback.html`, `ui-runtime.js` and this report. `tokens.css`, `index.html`, the other 20 pages, navigation, tokens, table density, fixture data and C notices were not changed.

### What moved

| Page | Static help | C retained in place | Design notes |
|---|---|---|---|
| publish-and-channels | Queue lifecycle explanation (`info-help-1`, queue heading); queue keyboard note (`info-help-2`, same heading); linked-channels keyboard note (`info-help-3`, linked heading) | — | Existing footer disclosure renamed under the same identity; both table-state disclosures and the footer fixture paragraph moved inside its body |
| publish-youtube | Approved-video explanation (`info-help-1`, approved-video heading) | Consent-binding card and synthetic-content notice | Existing disclosure converted; no content removed |
| publish-tiktok | Video-transfer explanation (`info-help-1`, transfer heading) | Pre-audit private-only/no-scheduling warning and creator nickname/duration/Ready block | Existing disclosure converted; the creator-info API fixture sentence moved inside |
| publication-links | — | Public-cancellation permission notice | Existing disclosure converted |
| channel-callback | — | `channel-callback-5` live verification panel | Existing disclosure converted |

The three `publish-and-channels` triggers sit in one flat `.help-head` row (heading + two buttons on the queue heading, heading + one on the linked heading) instead of the nested wrappers the old runtime produced. `Download MP4`, `Review video`, queue/linked row actions, consent text, the pre-audit warning and the whole callback panel remain visible.

### Runtime cleanup

Deleted from `ui-runtime.js`: the `.queue-description` branch, the TikTok transfer/API branch, the YouTube approved-video branch and the `publish-and-channels` design-note map entry. `addHelp`, `collapseTableNotes`, `makeNotesHost`, demo-label consolidation, auth introduction, the other page map entries and all generic open/close/focus/Escape/viewport/label behavior are unchanged. The five baked pages contain 3/1/1/0/0 static triggers and 3/1/1/0/0 `<dialog class="info-popover">` panels.

### Verification

Ten runs: 1280×800 and 360×800, dark, VI, on all five pages, plus an EN label spot on the three pages with triggers.

- Anchors, buttons and summaries are one-to-one with the pre-bake source: `a[href]` 90/40/42/26/27, summaries 17/4/7/1/3 on both sides. Buttons rise by exactly one on the three A/B pages (the new trigger); the two C-only pages are unchanged.
- Enter and Space open each tested trigger; Escape closes and restores focus; `aria-expanded` follows; population inside the panel is correct. Popovers stay inside the 8 px gutters (320×96.5 at 1280, x=8…328 at 360).
- 0 horizontal overflow at both widths, 0 duplicate IDs, 0 console errors, one non-empty closed `details.design-notes` and it is the last `<main>` child on every page. Notes bodies are 313 px wide at 360 with internal table scrolling, not page overflow.
- EN labels switch, e.g. “Details: Publication queue”, “Details: Approved video”, “Details: Video transfer”.
- C boundaries checked in the DOM and renders: publication-links notice, channel-callback progress panel, TikTok pre-audit warning plus creator identity/limits, YouTube consent card plus synthetic-content notice. None was hidden, collapsed or relabelled as optional help.
- 25-page smoke test after the runtime deletion: the five pages are clean. `story-editor.html` still reports 20 duplicate `editor-segment-*` IDs created by its own editor script; that behaviour predates this batch and is outside its scope.

### Closed-state footprint

Method of batches 1–3: source boxes before the transform, A/B intersection with the 800 px viewport, D source boxes, and page height before → after.

| Page | A/B source collapsed 1280 / 360 px² | First-viewport share 1280 / 360 | D source moved 1280 / 360 px² | Page height before → after 1280 / 360 |
|---|---:|---:|---:|---:|
| publish-and-channels | 56,540 / 50,184 | 0.97% / 4.10% | 155,017 / 59,881 | 1,144 → 1,016 (−128) / 1,435 → 1,289 (−146) |
| publish-youtube | 113,396 / 92,650 | 1.37% / 0% | 61,000 / 20,008 | 1,255 → 1,255 (0) / 1,795 → 1,749 (−46) |
| publish-tiktok | 50,007 / 47,263 | 1.63% / 0% | 61,000 / 20,008 | 1,157 → 1,157 (0) / 1,694 → 1,543 (−151) |
| publication-links | 0 / 0 | 0% / 0% | 50,996 / 20,008 | 801 → 801 (0) / 801 → 801 (0) |
| channel-callback | 0 / 0 | 0% / 0% | 61,000 / 20,008 | 801 → 801 (0) / 801 → 801 (0) |

The queue keyboard note and the YouTube/TikTok explanations sit below the first viewport at 360, so their share is 0% while the page still shortens. YouTube at 1280 keeps its height because the approved-video aside row is set by the adjacent consent card. Publication-links and channel-callback only rename and consolidate an already-closed disclosure, so their height does not change; their C notices remain fully visible.

### Limitation

This pass used source checks plus headless Chromium at 1280/360 dark VI with EN label spots. It did not run the full dark/light matrix, a 300-variant sweep, an opened design-notes screenshot on every page or an exhaustive per-node action remap, and does not claim those.

## p15-batch3 review fixes — delta

Corrects the P3-F2 closure in the section above. That closure was based on the intended declaration, not the rendered cascade: the inline `.help-head:has(>[data-od-id="bible-h2-5"])` rule (specificity 0,2,0) tied with the later-loaded `tokens.css` `:root .help-head` rule (0,2,0), which won by source order. The real browser therefore computed `flex-wrap:wrap`, left the heading 294px wide, and placed the 32px icon on a third line at y434.61 after the title bottom at y430.61 (help-head 97.59px tall).

The existing `max-width:767px` rule was rescoped to `:root .help-head:has(>[data-od-id="bible-h2-5"])` (0,3,0) and `:root .help-head:has(...) > h2[data-od-id="bible-h2-5"]`, keeping `flex-wrap:nowrap`, `flex:1 1 auto` and `min-width:0`. No other rule, page, token, runtime file or content was touched; no `!important` was used.

### Rendered geometry after the delta

| Variant | Computed flex-wrap | help-head | h2 | icon | Icon row check | Count |
|---|---|---|---|---:|---|---|
| 360 dark VI | **nowrap** | 294 × 61.59 | 254 × 61.59 | 32 × 32 at x295, top +14.79 from h2 top | shares row; icon top is 46.8px above h2 bottom | `2 / 30` |
| 360 dark EN | **nowrap** | 294 × 61.59 | 254 × 61.59 | 32 × 32 at x295, top +14.79 | shares row | `2 / 30` |
| 360 light VI | **nowrap** | 294 × 61.59 | 254 × 61.59 | 32 × 32 at x295 | shares row | `2 / 30` |
| 360 light EN | **nowrap** | 294 × 61.59 | 254 × 61.59 | 32 × 32 at x295 | shares row | `2 / 30` |
| 1280 dark/light VI | wrap (media inactive) | 349.42 × 32 | 309.42 × 30.8 | 32 × 32 at x590.42 | unchanged single row | `2 / 30` |
| 1280 dark/light EN | wrap (media inactive) | 365.77 × 32 | 325.77 × 30.8 | 32 × 32 at x606.77 | unchanged single row | `2 / 30` |

The heading now receives exactly 294 − 32 − 8 = 254px at 360. Both VI “Quy tắc và giới hạn nội dung” and EN “World rules and content limits” wrap inside that box without clipping; page horizontal overflow is 0 in all eight geometry runs.

### P3-F1 remains closed

At 360 with `info-help-4` open, `elementFromPoint(302, 484.59)` returns the panel’s `P.table-focus-note`, not the Delete control. The panel computes `z-index:400`, its rectangle is inside the 8px gutters, Enter opens it with initial focus in the dialog, and Escape closes it and restores the trigger. No P3-F1 code was changed.

### Verification scope and limitation

Variants exercised: 1280/360 × dark/light × VI/EN for geometry (eight runs), plus real Enter/Escape and hit ownership at 360 dark VI. Headless Chromium 155, VI default; absolute `y` values depend on scroll, so the table reports row-relative offsets. This delta corrects the cascade and closure claim; it does not claim a new 300-variant sweep.

## Phase 17 — pagination

Replaced the “Xem thêm” / “Load more” controls on `series-list.html` and `episodes.html` with a numbered pager. Added the shared `.pager` component CSS to `tokens.css`, the behavior layer in a new root `pager.js`, and a live pager specimen on `index.html`. No other page, token value, table geometry, data fixture text, navigation or C notice was changed.

### Component and static structure

- `<nav class="pager" aria-label="Phân trang / Pagination">` with a left range and right controls. Range: “Hiển thị 21–40 trên 245” / “Showing 21–40 of 245”; `aria-live="polite"`.
- Page-size select 20/50/100 with “Số dòng” / “Rows”. Pager group: first «, previous ‹, page numbers, next ›, last ». Current page is a filled `--nts-color-primary` button with `aria-current="page"`; disabled first/previous on page 1 and next/last on the last page use `aria-disabled="true"` plus token disabled opacity/colors.
- Ellipses are non-focusable spans. The number window keeps first/last plus current±2 (a superset of the required current±1), producing `1 2 3 … 13` on page 1, `1 2 [3] 4 5 … 13` on page 3, `1 … 5 6 [7] 8 9 … 13` on page 7 and `1 … 11 12 [13]` on page 13.
- `pager.js` initializes the static navs before `ui-runtime.js`, then renders rows after the runtime has prepared the source rows. It rotates the existing fixture rows into 20/50/100 slots, clones with prefixed unique IDs (no duplicate `id`), and leaves the source row actions intact.
- Loading specimen: each page’s existing `#loading` view now carries a static disabled pager (`aria-busy="true"`, four disabled arrows, “Đang tải…” / “Loading…”). Empty: the pager hides and “0 kết quả” / “0 results” appears; the page’s existing empty/filter-none copy is unchanged.
- `index.html`: a full-width “Phân trang” specimen with the same 245/20/13 state and no attached table.

### Measured verification

| Measure | 1280 | 360 |
|---|---:|---:|
| Pager left/right vs table-wrap left/right | 240 / 1256 vs 240 / 1256 (0px / 0px) | 16 / 344 vs 16 / 344 (0px / 0px) |
| Gap between table and pager | 12px | 12px |
| Page-size select height | 32px | 32px |
| Arrow / number button size | 32 × 32 | 44 × 44 |
| Horizontal page overflow | 0 | 0 |
| Initial rows / pages at 20 | 20 / 13 | 20 / 13 |
| Last page | page 13: 241–245, 5 rows | same |

- Page size 50 → 5 pages, 50 rows, page 1; size 100 → 3 pages, 100 rows, page 1. Changing size from page 5 returned to page 1.
- Filter/search reset: series archived → 13 rows / 1 page; series all → 258; episodes status “story_review” → 2 rows; “draft” → 0, pager hidden; series “night” → 7; search “Chuyện” → 1; search “zzzz” → 0 / “0 kết quả”; clearing returned to 245.
- Keyboard: Tab order after the table is page-size select → first → previous → page numbers → next → last at 1280; at 360 the three visible page numbers collapse to the non-focusable “Page 1 of 13” status, so the order is select → first → previous → next → last. Enter on “next” moved to 21–40/page 2. Arrow buttons keep focus.
- Focus ring: buttons compute `outline:none` with a 2px inset `--nts-color-primary` shadow and primary border; it does not paint outside the 32px/44px frame.
- 16-run matrix (both pages × 1280/360 × dark/light × VI/EN): 0 overflow, 0 edge deltas, 0 duplicate IDs, 0 console errors; range and EN aria-labels switch correctly (“Showing 1–20 of 245”, “First page” … “Last page”). Index specimen: 910px wide, 0 overflow, page 7 shows `1 … 5 6 [7] 8 9 … 13`.

### Fixture limitation

The 245 total and the page-size math are fixtures. Only a small specimen pool exists (2 active + 1 archived series; 8 episodes), so rows rotate/repeat to fill 20/50/100 slots, and filtered totals report that pool (e.g. archived 13, status review 2) rather than 245. The default unfiltered state is the required 245. This pass used headless Chromium at 1280/360 for these states; it did not run a 300-variant product sweep or a backend/API change.

## p15-batch4 review fixes

### P15B4-01 — residual information-only TikTok transfer card (closed)

Changed `publish-tiktok.html` only:

- Moved the existing `button.icon-btn.info-trigger[aria-controls="info-help-1"]` out of `section.panel[data-od-id="publish-tiktok-22"]` and into a flat `.help-head` beside `h3[data-od-id="publish-tiktok-6"]` (“Bài đăng TikTok” / “TikTok post”).
- Removed the obsolete `section[data-od-id="publish-tiktok-22"]`, its `h3[data-od-id="publish-tiktok-23"]` heading and the empty transfer card wrapper. The aside now contains only `[data-od-id="publish-tiktok-19"]`; no empty wrapper or duplicate trigger remains.
- `#info-help-1`, its title/body, transfer-topic labels, `aria-controls`, `aria-expanded`, keyboard behavior and opener focus restoration are unchanged. The dialog is still static HTML at the end of `<main>`. `tokens.css` and `ui-runtime.js` were not touched.

Measured removal at normal pointer, dark/light (identical geometry):

| Measure | Before 1280 | After 1280 | Before 360 | After 360 |
|---|---:|---:|---:|---:|
| Transfer card rect | 390.39 × 74 | absent | 328 × 66 | absent |
| Preceding aside gap | 24px | — | 24px | — |
| Aside height | 300.19 | 202.19 | 284.19 | 194.19 |
| Document height | 1,157 | 1,163 | 1,543 | 1,459 |

At 1280 the card and 24px gap were inside the shorter aside column, so removing them did not lower document height; the 32px help-head is 5.91px taller than the former 26.09px heading, giving +6px overall. At 360 the stacked layout does shrink by 66 + 24 − 6 = 84px.

### Verification

- 1280/360 × dark/light VI plus EN at both widths: the trigger is 32×32, vertically overlaps the 26.09px heading box (centre within 0.01px at 360), and shares the `.help-head` row. Heading widths: 144.42 VI / 104.75 EN; no clipping, no horizontal overflow, no duplicate IDs.
- `#info-help-1` is the only popover: Enter and Space open it; Escape and outside click close it and restore trigger focus; at 360 the settled panel is `[32, 354.27, 320, 116.61]`, inside the 8px gutters, its interior hit is owned by the panel, initial focus is the dialog, and the trigger tooltip is suppressed while open.
- Creator card content is unchanged and visible. `#branded-blocked` and `#creator-blocked` still render as their own state views, outside Design notes.
- Consent: unchecked → disabled button visible, submitted-state link `display:none`; checked → disabled button `display:none`, `.gate-next` `display:inline-flex` with the original `href="#submitted"`.
- Counts unchanged: 42 `a[href]`, 8 buttons, 7 summaries; one closed non-empty `details.design-notes`, last in `<main>`; all specimen/state links retained.

Limitation: this is a targeted source + headless Chromium check at the stated widths/themes, not a 300-variant sweep or a new action remap.

## p17 review fixes

### P17-F1 — current page had an invisible keyboard focus ring (closed)

Added one narrowly scoped rule after the current-page styling in the Phase 17 block of `tokens.css`:

`:root .pager-btn[aria-current="page"]:focus-visible{box-shadow:inset 0 0 0 2px var(--nts-color-on-primary)}`

The fill, primary 1px frame and `--nts-radius-control` are unchanged; the indicator stays inset. Inactive-button focus still uses the existing primary inset and was not changed.

Measured with real keyboard Tab (select → first → previous → current) on `series-list.html`, `episodes.html` and `index.html` at 1280 and 768, dark and light (12 runs):

| Theme | Current fill | Focus indicator | Contrast | Frame | Inset |
|---|---|---|---:|---:|---|
| Dark | `rgb(110, 202, 192)` | `rgb(32, 37, 35)` | **8.05:1** | 32 × 32 | 2px inset |
| Light | `rgb(0, 108, 101)` | `rgb(255, 255, 255)` | **6.30:1** | 32 × 32 | 2px inset |

`outline-style` remains `none`; `aria-current="page"` and the disabled states are unchanged. The current number is intentionally hidden at 360 in the compact status, so the 44 × 44 mobile frame was checked on a visible arrow: it computes a 2px inset primary indicator entirely inside the frame, with the 4px between buttons unchanged.

### P17-F2 — index specimen lost focus after activating a page number (closed)

In `pager.js` the shared `options.focusNumber` restoration moved above the `if (!tbody) return` early exit, so it runs for the standalone index specimen as well as both data tables. Row rendering remains conditional and no second paginator was added.

Real Enter activation of page 3 at 1280 and 768, VI and EN (12 runs across the three pages):

- `document.activeElement` is `BUTTON[data-page="3"][aria-current="page"]`; range becomes `41–60` / `Showing 41–60 of 245`.
- The next Tab moves to page 4, not the document start.
- At 360, Enter on the next arrow leaves focus on `BUTTON[aria-label="Trang sau"]` and advances to page 2.
- Page-size changes and filter/search resets still return to page 1 without changing the focus behavior.

### P17-F3 — pager literals replaced with existing tokens (closed)

Only the Phase 17 shared pager declarations were changed:

| Literal | Token now used |
|---|---|
| `13px` type | `--nts-control-text-sm` |
| `32px` control height/min-width | `--nts-control-h-sm` |
| `44px` mobile target | `--nts-control-hit-coarse` |
| `64px` compact min-width | `--nts-space-8` |
| `4px` page gap | `--nts-space-1` |
| `8px` gaps/padding | `--nts-space-2` |
| `12px` gaps/margins | `--nts-space-3` |
| `16px` pager gap | `--nts-space-4` |
| `20px` ellipsis min-width | `--nts-space-5` |
| off-scale mobile `10px` gap | `--nts-space-2` (8px) |

Remaining literals are structural or genuinely tokenless: 1px borders, the 2px inset indicator, line heights 1/1.2/1.4, weight 600, `100%`, `0`, and the existing 767px breakpoint. No token was redefined or invented.

Rendered after the replacement: 32 × 32 arrows/numbers at 1280/768, 44 × 44 at 360, select 32 high, 1px border, 6px radius, 13px tabular text, table→pager gap 12px, and 0px left/right edge delta on both list pages at 1280/768/360. Page and inner-pager overflow are 0 on all three pages. The index specimen is 294px wide at 360, and its EN page 7 / page 13 compact statuses (`Page 7 of 13`, `Page 13 of 13`) fit with 0 overflow.

### Regression checks

- Page 1 keeps first/previous `aria-disabled="true"`; disabled activation does not move the page. Page 13 keeps next/last disabled and shows 241–245 / 5 rows.
- Pages 1, 3, 7 and 13 keep the same ellipses: `1 2 3 … 13`, `1 2 [3] 4 5 … 13`, `1 … 5 6 [7] 8 9 … 13`, `1 … 11 12 [13]`; full pages render 20 rows.
- Size 50 → 5 pages/50 rows; 100 → 3 pages/100 rows; 20 → 13 pages/20 rows; all return to page 1. Series archived filter → 13 rows/1 page; episodes review filter → 2 rows; empty search hides the pager and shows “0 kết quả”; `#loading` keeps four disabled pager buttons.
- Both toolbar searches still show the O16-1 1px inset frame (`box-shadow: … 1px inset`, no outline). Series “Số tập” is unclipped (`scrollWidth 72`, `clientWidth 72`).
- Bible 360 is unchanged and still computes `nowrap`, 294px header / 254px title / 32px icon, `2 / 30`. `bible.html`, `publish-tiktok.html` and `ui-runtime.js` were not edited.
- 0 console errors in the exercised runs.

### Limitation

Headless Chromium 155, dark/light VI/EN at the stated widths; contrast was computed from the rendered rgb values. This was not a 300-variant sweep, and the 360 current-page ring is not applicable because the current number is replaced by the compact status there.

## Phase 15 batch 5 — explanatory panels

**Scope:** `auth.html`, `shell.html`, `dialogs.html`, `legal.html`, `index.html`, `control-size-audit.html`. Shared `.info-trigger`/popover/drawer/design-notes component reused; one shared notes constraint was added so nested state tables scroll inside the disclosure. Lightweight verification only (1280/360, dark, VI, plus EN label spots); no 300-variant run.

### Page decisions

| Page | Applied |
|---|---|
| auth | The secondary introduction paragraph becomes one popover on the introduction heading (`.help-head`, `info-help-1`). The private/no-new-account line stays visible. `p.legal` and all six `details[data-od-id^="state-examples"]` move into one closed design-notes disclosure; the sign-in tabs, steps and all auth links are untouched. |
| shell | The workspace orientation panel is compacted: heading + trigger + guidance + **Mở tổng quan** stay visible, and the decorative production pipeline moves into the heading popover (`info-help-1`). All three connection banners (reconnecting, paused, account error) and both recovery actions remain visible C. The realtime hint, `footer.foot` and the language-saving `details[data-od-id="state-examples"]` move into design notes. |
| dialogs | The legal-version/protected-action hint stays inline in the consent dialog (C). The existing handoff disclosure is renamed **Ghi chú thiết kế / Design notes** in place and gets an explicit `.design-notes-body`; all ten state links remain. |
| legal | The publication-readiness warning stays inline with the missing legal-name/contact and must-complete-before-publication copy (C). The footer disclosure is renamed **Ghi chú thiết kế / Design notes** and its five spec links are retained. |
| index | The three `.table-focus-note` paragraphs become popovers (`info-help-1/2/3`) on the Phase 2, Phase 3 and Phase 5 table headings. The three table-state disclosures, the system-feedback fixture paragraph and the page footer move into one closed design-notes block. The four Phase 15 specimens are now static `<dialog>`/drawer markup: no `hidden` source divs or `data-info-target` remain. |
| control-size-audit | The `audit-footer` focus-order paragraph and JSON measurement link move into one closed design-notes block at the end of `<main>`. |

### Action preservation

Link inventory is a multiset of `href` + visible label; every original link is present exactly once.

| Page | Links before → after | Buttons before → after | Summaries before → after | New controls | Links inside design notes |
|---|---:|---:|---:|---:|---:|
| auth | 40 → 40 | 11 → 12 | 7 → 8 | 1 trigger | 2 |
| shell | 20 → 20 | 2 → 3 | 3 → 4 | 1 trigger | 0 |
| dialogs | 38 → 38 | 2 → 2 | 3 → 3 | 0 | 10 |
| legal | 83 → 83 | 0 → 0 | 2 → 2 | 0 | 5 |
| index | 179 → 179 | 26 → 30 | 26 → 27 | 3 triggers + drawer close | 15 |
| control-size-audit | 18 → 18 | 0 → 0 | 15 → 16 | 0 | 1 |
| **Total** | **378 → 378** | **41 → 47** | **56 → 60** | **5 triggers + 1 close** | **33** |

No links were dropped or added. All 33 links inside the closed notes resolve, and the seeded state/destination links on every page are unchanged.

### Verification

Twelve main runs (1280/360, VI, dark), plus EN label spots.

- All 17 visible triggers opened (9 at 1280, 8 at 360), including the 4 index specimens; panel bodies were non-empty, Enter and Space opened the first trigger on auth/shell/index, Escape closed and restored focus, and `aria-expanded` followed. 0 failures.
- Every popover stayed inside the 8 px gutters. The index drawer is intentionally full-bleed at 360 (0…360) and inside the gutter at 1280; the shell/auth popovers measured 320 px wide at 360 at x=8/32.
- 0 horizontal overflow, 0 duplicate IDs (on these six pages), 0 console errors, one non-empty closed `details.design-notes` per page. Notes contain 7/3/3/2/5/1 direct children and 61 px closed height.
- With the notes and every nested disclosure open at 360, all six pages keep 0 page overflow. The index state tables stay inside their own `.table-wrap` scroll containers after adding `:root .design-notes-body :is(details,.stack){min-width:0;max-width:100%}` to `tokens.css`; no other page geometry changed.
- EN spot checks: “Details: Sign-in introduction”, “Details: Production sequence”, “Details: Keyboard navigation”; notes summary switches to “Design notes”.
- C boundaries rechecked: auth private-account line; shell Open-overview CTA + guidance and the three connection notices/actions; dialogs legal consent hint + guarded actions; legal publication warning; index C banner specimen. None was hidden or moved.
- 25-page smoke test after the runtime deletion: all pages have exactly one closed non-empty notes block and no empty popover. `story-editor.html` still reports its pre-existing duplicate `editor-segment-*` IDs from its editor script; unchanged and outside this batch.

### Closed-state footprint

Source boxes before the bake, D source boxes, and measured page height before → after.

| Page | A/B source collapsed 1280 / 360 px² | First-viewport share 1280 / 360 | D source moved 1280 / 360 px² | Page height before → after 1280 / 360 |
|---|---:|---:|---:|---:|
| auth | 23,362 / 0 | 2.28% / 0% | 39,191 / 35,290 | 928 → 950 (+22) / 1,092 → 1,021 (−71) |
| shell | 0 / 0 | 0% / 0% | 114,832 / 60,572 | 1,345 → 1,159 (−186) / 1,519 → 1,262 (−257) |
| dialogs | 0 / 0 | 0% / 0% | 61,000 / 20,008 | 840 → 840 (0) / 972 → 972 (0) |
| legal | 0 / 0 | 0% / 0% | 59,536 / 20,008 | 5,543 → 5,575 (+32) / 9,994 → 10,026 (+32) |
| index | 69,917 / 57,564 | 0% / 0% | 175,846 / 90,159 | 7,526 → 7,232 (−294) / 13,229 → 12,821 (−408) |
| control-size-audit | 0 / 0 | 0% / 0% | 90,880 / 34,112 | 3,096 → 3,093 (−3) / 7,729 → 7,702 (−27) |

Auth grows 22 px at 1280 because only the default panel's example disclosure was visible; five of the six moved disclosures sat in hidden panels. Legal grows 32 px from the shared notes surface on an already-closed disclosure; no content was added. Shell also reclaims **95,594 px² at 1280 / 34,635 px² at 360** from the C workspace panel (261,594 → 166,000 / 113,616 → 78,981).

### Static bake and runtime cleanup

Baked into the six pages: 1/1/0/0/3/0 static popovers and 0/0/0/0/1/0 drawers, plus the four index specimens (6 popovers + 1 drawer on index). The only shared-style change is the nested notes constraint above. Deleted from `ui-runtime.js`: the auth introduction transform, demo-label consolidation, the `auth`/`shell`/`index`/`control-size-audit` design-note map, the index `data-info-target` initializer, and the now-unused transform helpers (`moveToNotes`, `makeNotesHost`, `collapseTableNotes`, `addHelp`, `makeTrigger`, `makePanel`, `pair`, `infoIcon`, `initHelpTransforms`). Only trigger open/close, focus return, Escape/outside dismissal, viewport-safe placement, label switching and the generic page sync remain.

## Phase 15 result

**Scope:** all 25 root HTML pages, batches 1–5. One shared help component, one bottom design-notes pattern, no page-specific runtime DOM rewrite left. Changed files: the 25 pages, `tokens.css` (popover stacking, the pager block and the nested notes constraint), `ui-runtime.js`, `pager.js` (Phase 17), all in root; `review-tables.md` is append-only.

### Per-page outcome

Per-page source areas, first-viewport share, moved D area and page-height deltas remain in each batch's **Closed-state footprint** table above; this section summarizes the static outcome.

| Batch | Pages | Static triggers / panels | Notes |
|---|---|---:|---|
| 1 | video-review, story-review, story-editor, render | 4 / 2 / 0 / 1 | 4 notes, all closed |
| 2 | dashboard, episodes, series-list, series-detail, series-editor | 2 / 1 / 1 / 3 / 8 | 5 notes; series-detail includes a drawer |
| 3 | bible, research, new-episode, autopilot, account-security | 5 / 2 / 0 / 1 / 1 | 5 notes |
| 4 | publish-and-channels, publish-youtube, publish-tiktok, publication-links, channel-callback | 3 / 1 / 1 / 0 / 0 | 5 notes |
| 5 | auth, shell, dialogs, legal, index, control-size-audit | 1 / 1 / 0 / 0 / 7 / 0 | 6 notes; index includes 3 catalog triggers + 4 specimens + a drawer |
| **Total** | **25 pages** | **45 triggers / 45 panels** | **25 notes** |

The 45 static panels include 41 batch-created triggers plus the 4 index specimens, and two drawers (series-detail, index).

### Action preservation and C protections

Every batch reported per-page one-to-one link/button/summary counts; batch totals were 430/430 (batch 2), 365/365 (batch 3), and 378/378 links (batch 5), with batch 1 and 4 mapped node-for-node. No original anchor, button, summary, input, download, disabled/guarded action or state route was removed. Intentional new controls are the 41 help triggers and 2 help close buttons.

C content stays visible throughout: decision/approval actions, consent and legal acceptance, private-account/owner rules, connection/auth state and recovery actions, publication warnings, validation gates and all destructive confirmations. Optional explanations move; blockers do not.

### Verification and limitation

Per-batch runs used headless Chromium at 1280×800 and 360×800, dark VI, with EN label spots; triggers were opened with Enter and Space, closed with Escape, and checked for focus return, `aria-expanded`, non-empty bodies and 8 px gutters. Every batch ended with a 25-page smoke test: one closed non-empty notes block per page, no horizontal overflow, no console errors; only `story-editor.html`'s pre-existing duplicate segment IDs remain.

This phase did **not** run the full dark/light 300-variant matrix, an exhaustive per-node remap in `assets/audit/phase-15-actions.json`, or an opened-notes screenshot on every page. The per-batch tables in this report are the measured record; the phase is complete for the 25 mapped pages under that lightweight scope.

## p15-batch5 review fixes

Applied only **P15B5-01** and **P15B5-02**. Changed files: `shell.html`, `index.html` (one scoped `max-width:767px` help-row rule each), `legal.html` (two explicit locale option values) and this report. `tokens.css`, `ui-runtime.js`, the other pages, palette, typography, density, navigation, actions/routes, C notices, pager and help dialog markup were not changed.

### P15B5-01 — help icons wrap into isolated rows at 360 (closed)

Selectors changed:

- `shell.html`: `.help-head:has(>button[aria-controls="info-help-1"])` and its direct `> h2`.
- `index.html`: `#phase-three-production > .help-head` and `#phase-five-actions > .help-head`, plus their direct `> h2`.

Root cause: the shared `.help-head` is `flex-wrap:wrap`. A wrapping flex container places items into lines using their hypothetical main size before shrinking; the heading's full content width plus the 32 px trigger exceeded the 294 px (shell) / 328 px (index) row, so the trigger was wrapped onto its own line before the heading could shrink its text.

One scoped rule was added inside each page's existing `@media(max-width:767px)` block, reusing the bible helper pattern and existing 8 px gap/32 px trigger:

```css
/* shell.html */
:root .help-head:has(>button[aria-controls="info-help-1"]) { flex-wrap: nowrap }
:root .help-head:has(>button[aria-controls="info-help-1"]) > h2 { flex: 1 1 auto; min-width: 0 }

/* index.html */
:root #phase-three-production > .help-head,
:root #phase-five-actions > .help-head { flex-wrap: nowrap }
:root #phase-three-production > .help-head > h2,
:root #phase-five-actions > .help-head > h2 { flex: 1 1 auto; min-width: 0 }
```

Specificity is `0,3,1` for the shell row and `1,2,1` for each index row, both above the shared `:root .help-head` wrap rule; the heading rules are above `:root .help-head>h2 { flex:0 1 auto }`. The trigger keeps the untouched shared `flex:0 0 auto`, so it stays 32×32 and its focus ring/coarse hit target are unchanged.

Before → after at 360, derived from the supplied rectangles and the row allocations (the media query is inactive above 767 px, so the 1280 geometry is unchanged):

| Owning row | Before | After |
|---|---|---|
| shell `.help-head` | row 294 px; heading `271.59 × 30.80` at x=33; icon `32 × 32` at `[33, 358.48]`, alone on the next line, 4 px row gap | heading receives `294 − 32 − 8 = 254` px; icon at x=295 on the same nowrap line, vertically centred by the existing `align-items:center` |
| index `#phase-three-production` | heading fills the 328 px row and is 61.59 px tall; icon starts at heading-bottom + 4 px | heading receives `328 − 32 − 8 = 288` px, keeps its two-line 61.59 px box; icon at heading-left + 296 on the same line |
| index `#phase-five-actions` | heading fills the 328 px row and is 61.59 px tall; icon starts at heading-bottom + 4 px | heading receives 288 px, keeps its two-line 61.59 px box; icon at heading-left + 296 on the same line |

At 360 the trigger box remains 32×32 and its vertical centre is the row centre, inside the owning heading's vertical span (including when the heading is one line and 30.80 px tall). The heading's left edge and the following content's left edge are unchanged; no words are removed, truncated or set in a smaller type, and the flex row totals exactly 294 / 328 px, so no horizontal document overflow is introduced. EN uses the same allocation; dark/light differ only in colour.

At 1280 the rules do not apply: the heading, trigger position and left alignment remain the previous desktop layout. The help open/close path, `aria-controls`/`aria-expanded`, Escape/focus restoration, viewport gutters and hit ownership are unchanged because `ui-runtime.js` and the dialog markup were not edited.

### P15B5-02 — legal EN mode kept VI runtime language/theme labels (closed)

Selector: `legal.html #locale option#locale-vi` and `#locale option#locale-en`.

Before: both options omitted `value`, so the select's real values were the labels `VI` / `EN`. The generic `locale()` resolver compares `select.value === 'en'`; selecting EN therefore switched the CSS `:has(#locale-en:checked)` page/notes but left `html.lang="vi"` and the theme options at `Tối / Dark` + `Sáng / Light`.

After: the two options keep their IDs, labels, `selected` default and CSS `:checked` routing, and now carry explicit values:

```html
<option id="locale-vi" selected value="vi">VI</option>
<option id="locale-en" value="en">EN</option>
```

The generic runtime is unchanged. EN now makes `locale()` return `en`, so `syncAll()` sets `html.lang="en"`, the EN page and Design notes render, and `localizeOptions()` changes the theme choices to `Dark` / `Light`. VI returns `html.lang="vi"`, the Vietnamese page/notes and `Tối` / `Sáng`. Both theme choices still switch through the existing `:root:has(#theme-light:checked)` path. No page-specific condition or transform was added.

The legal text, TOC links, missing legal-name/contact publication warning and the five links inside the closed notes block are untouched; the page retains 83 links, 0 buttons and 2 summaries.

### Regression and verification scope

| Page | Links | Buttons | Summaries | Closed non-empty notes | Triggers | Popovers / drawers |
|---|---:|---:|---:|---:|---:|---:|
| auth | 40 | 12 | 8 | 1 | 1 | 1 / 0 |
| shell | 20 | 3 | 4 | 1 | 1 | 1 / 0 |
| dialogs | 38 | 2 | 3 | 1 | 0 | 0 / 0 |
| legal | 83 | 0 | 2 | 1 | 0 | 0 / 0 |
| index | 179 | 30 | 27 | 1 | 7 | 6 / 1 |
| control-size-audit | 18 | 0 | 16 | 1 | 0 | 0 / 0 |

Across all 25 pages the static inventory remains 45 triggers, 43 popovers + 2 drawers and 25 notes; `ui-runtime.js` still contains no page-specific `data-od-id` selectors or deleted help-transform functions. The index short/action/C/drawer and pager specimens were not touched, and the inset search/current-pager focus fixes remain in `tokens.css`.

Checks exercised here: selector-to-DOM matching, scoped-rule placement/cascade and specificity, the supplied before rectangles plus the derived after flex allocation, locale option markup, and full static link/button/summary/notes/trigger counts on the six pages and all 25 pages. This targeted pass did not re-run an interactive 1280/360 browser matrix, a 300-variant sweep or an exhaustive per-node action remap, and does not claim those.
