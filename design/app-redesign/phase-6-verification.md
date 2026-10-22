# Phase 6 — table geometry and navigation verification

Đã áp dụng ba thay đổi trên 15 trang: ô bảng một dòng, đầu trang gọn cho các trang danh sách, và bộ biểu tượng điều hướng SVG thống nhất. Hàng dữ liệu desktop đồng đều 44px; ba bảng dashboard đồng đều 36px. Không thêm bản ghi mẫu.

## Method and limits

- Computed layout measurements: 15 pages × 3 widths (1280, 768, 360) × 2 languages (vi/en) × 2 themes (dark/light) = **180 variants**. Viewport height: 800 CSS px; fine pointer. Fonts awaited before measurement.
- Tables below the fold stay below the fold on reading/detail screens. Hidden alternate-state tables were temporarily exposed to measure row geometry; they count as zero in the initial viewport.
- “Visible” means an entire existing row lies inside the first 800px. “Capacity” is the number of fixed-height rows that fit below the existing table header, before content following the table moves down. Capacity is not a claim that additional records exist.
- Desktop means 1280 here. At 768 and 360, tables use labelled cards; their total card height intentionally exceeds 44px, while each field remains one line. There is no horizontal document overflow in any measured variant.
- Fixture timestamps: episode records provide only relative times. These are shortened, and tooltip/accessibility descriptions explicitly say that the absolute timestamp was not supplied. Existing version dates/times are retained verbatim. A real absolute timestamp cannot be proven or manufactured from these fixtures.
- Static prototype: added search fields are visual controls; search execution, live relative-time updates and data fetching remain application integration work. Existing CSS-driven status/series selection and state anchors remain.

## Density targets

| Page / table | Table top (VI / EN) | Actual visible fixture rows (VI / EN) | Row capacity (VI / EN) | Result |
|---|---:|---:|---:|---|
| episodes / `episodes-table-1` | 201px / 201px | 8 / 8 | 12 / 12 | ≥10 capacity; fixture count remains 8 |
| series-list / `series-list-table-1` | 196px / 196px | 2 / 2 | 12 / 12 | ≥10 capacity; fixture count remains 2 |
| publish-and-channels / `publication-queue` | 253px / 253px | 7 / 7 | 11 / 11 | ≥10 capacity; fixture count remains 7 |

The series fixture has two active rows and one archived row. The default filter shows two. Episodes contain eight records; the publication queue contains seven. The literal goal of ten populated rows cannot be demonstrated without additional real fixtures.

### Dashboard five-row geometry test

A temporary measurement-only clone repeated existing rows until each lane had five rows. It was removed after measurement and was never saved into dashboard.html. All fifteen test rows fit simultaneously in the initial viewport in both languages and themes.

| Lane | Default fixture rows | Bottom of fifth test row | Row height |
|---|---:|---:|---:|
| `dashboard-table-1` | 2 | 319px | 36px |
| `dashboard-table-2` | 2 | 557px | 36px |
| `dashboard-table-3` | 1 | 795px | 36px |

## Per-page measurements

Each table below aggregates the four 1280 variants (vi/en × dark/light). A single row-height number means every measured row in those variants had exactly that height. Visible-row values are VI / EN; themes produced the same counts. All fifteen pages have the same five navigation destinations, five inline SVG icons in the sidebar and five in the mobile drawer (20px, stroke 1.75).

### index.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `index-table-1` | 44px | 2 / 2 | Initial view | 5/5 |
| `phase-two-series-catalog` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `phase-three-production-catalog` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `chip-row-after` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `status-action-matrix` | 44px | 0 / 0 | Below first viewport | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### shell.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| No table on this surface | — | — | Reading/form/shell content retained | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### auth.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| No table on this surface | — | — | Reading/form/shell content retained | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### dashboard.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `dashboard-table-1` | 36px | 2 / 2 | Initial view | 5/5 |
| `dashboard-table-2` | 36px | 2 / 2 | Initial view | 5/5 |
| `dashboard-table-3` | 36px | 1 / 1 | Initial view | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### series-list.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `series-list-table-1` | 44px | 2 / 2 | Initial view | 5/5 |
| `series-list-table-8` | 44px | 0 / 0 | Hidden alternate state | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### series-editor.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| No table on this surface | — | — | Reading/form/shell content retained | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### series-detail.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `series-episodes-1` | 44px | 3 / 3 | Initial view | 5/5 |
| `series-episodes-2` | 44px | 0 / 0 | Hidden alternate state | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### bible.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `bible-characters` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `bible-places` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `bible-proposals` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `bible-rules` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `bible-summaries` | 44px | 0 / 0 | Below first viewport | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### research.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `research-sources` | 44px | 2 / 2 | Initial view | 5/5 |
| `research-findings` | 44px | 0 / 0 | Below first viewport | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### episodes.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `episodes-table-1` | 44px | 8 / 8 | Initial view | 5/5 |
| `episodes-table-8` | 44px | 0 / 0 | Hidden alternate state | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### new-episode.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| No table on this surface | — | — | Reading/form/shell content retained | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### story-review.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `story-versions` | 44px | 0 / 0 | Below first viewport | 5/5 |
| `story-annotations` | 44px | 0 / 0 | Below first viewport | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### video-review.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `video-targets-2` | 44px | 0 / 0 | Hidden alternate state | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### publish-and-channels.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| `publication-queue` | 44px | 7 / 7 | Initial view | 5/5 |
| `linked-channels-1` | 44px | 0 / 0 | Initial view | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

### legal.html

| Table | Row height | Initial visible rows VI / EN | Initial placement | Navigation icons |
|---|---:|---:|---|---|
| No table on this surface | — | — | Reading/form/shell content retained | 5/5 |

1280: fixed table columns. 768: icon-only navigation rail with tooltips; labelled table cards. 360: drawer with five icons and labels; labelled table cards. Both languages and themes checked; no horizontal overflow.

## Shared implementation checks

| Check | Result |
|---|---|
| Fixed rows | All record tables: 44px; dashboard: 36px, vi/en and dark/light |
| One-line fields | nowrap + ellipsis + fixed column shares; full text retained in native titles and referenced accessible descriptions |
| Long text | Title, series, next action and schedule retain full source text in descriptions; no record text was deleted to force row height |
| Relative time | Short localized strings; right aligned, tabular figures; unavailable absolute timestamps explicitly disclosed |
| Navigation | Five icons, 20px, 12px gap, 24 viewBox, currentColor, stroke 1.75, active tint + accent icon |
| Row actions | All measured visual buttons remain 32×32px; all have aria-label; existing status/action mappings retained |
| Phase-4 sizing | sm32 / md36 / lg44 definitions retained; auth primary remains lg |
| Focus | Existing token focus ring retained; row-open links and row icon buttons remain in native keyboard order; rail tooltips on hover/focus |
| Preservation | Story prose, video player/scene strip, wizard steps, authentication and legal copy retained; previous version backed up in backups/phase-5 |

Raw measurements: [assets/audit/phase-6-measurements.json](assets/audit/phase-6-measurements.json). Historical review-tables.md and earlier audit files are unchanged.
