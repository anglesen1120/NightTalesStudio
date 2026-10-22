# Night Tales Studio mockup design QA

## Result

**12 deduplicated findings: 1 High, 7 Med, 4 Low.** Read-only browser review of the current root HTML/CSS files; no project file was modified.

The reproduced **“Tập phim” hover defect is active-state masking by the CSS cascade**: the later `.navitem:hover` rule replaces the current-page teal background. It is **not evidence of an overlapping element or overflow clipping** in the current files. Literal physical occlusion of this nav item was not reproduced; do not commission a speculative z-index fix for it.

Project: `/Users/mapmap/Library/Application Support/Open Design/namespaces/release-stable/data/projects/nts-app-ui-redesign-20261021`.
Evidence directory: `/tmp/nts-design-review/`. Screenshot paths below are relative to that directory.

## Coverage and method

- All **25 root `*.html` files**, including the additional historical `control-size-audit.html`, checked at **1280×800, 768×800 and 360×800**, **dark/light**, **VI/EN**: **300 page variants**. `backups/` and `.file-versions/` were excluded.
- Used an independent headless Chromium tab, real mouse movement to element centers, keyboard-modality/focus checks, computed styles, bounding rectangles, hit testing and screenshots. Default nav, links, buttons, icon actions, rows, form controls and native disclosure summaries were swept. Mobile navigation was opened and its five links separately hovered/focused in all four theme/locale combinations on every page that has a drawer.
- Main sweep recorded 11,830 successful `:hover` checks and 11,134 successful focus checks; these are interaction observations, not unique controls. Hidden/disabled targets were not counted as successful. Corrected legal-page option-value handling and additional drawer/targeted checks are separately recorded.
- Also checked **4,224 fragment-state/layout combinations** for target visibility and viewport overflow. This additional pass checks layout, not every interaction inside every alternate specimen.
- Each page has full-viewport screenshots for all 12 variants. Flagged tooltip states have additional hover screenshots. Raw measurements: `<page>.html.json`, `deep-<page>.html.json`, `extra-<page>.html.json`, `states-<page>.html.json`; corrected legal interactions: `legal-corrected-interactions.json`.
- Checked visible text contrast against composited ancestor backgrounds across the default 300 variants: no measured failure below 4.5:1 for ordinary text or 3:1 for large text. This is not a complete WCAG certification: opacity effects, native popups, video/image content and every alternative-state color pair require separate assessment. No general text-color redesign is justified by this review.
- No page-level horizontal overflow in the default matrix or additional fragment-state sweep. Local hrefs and fragment IDs resolve; no duplicate IDs found. Measured visible status chips are 24px high with 6px radius; visible icon buttons are 32×32. Desktop main collection rows are 44px, or 36px for dense dashboard rows.
- **Codebase-memory graph rules do not apply**: these are generated HTML files outside the indexed repository, as specified in the assignment. No repository code was investigated or changed.
- Consulted `frontend-design`, `audit-static-html-design-output`, `design-plan.md`, `review-tables.md`, actual page styles and `tokens.css`. Later owner compact-table requirements take precedence where earlier phase documentation differs; those discrepancies are explicitly identified below.

### Page matrix

Every row below has all 12 viewport/theme/locale variants, hover/focus sweeps and screenshots. “No page-specific finding” does not exempt shared navigation/interaction rules.

| Page | Result / applicable findings |
|---|---|
| account-security | F01–F03; clipped table chip feedback |
| auth | Shared F01, F07, F08; no additional auth-specific visual defect confirmed |
| autopilot | Shared navigation/localization findings; no additional page-specific defect confirmed |
| bible | F02, F03, F06, F07; shared navigation findings |
| channel-callback | Shared navigation/localization findings; no additional page-specific defect confirmed |
| control-size-audit | Historical evidence page checked; no new confirmed finding |
| dashboard | F01–F03, F05–F09, F12 |
| dialogs | Shared navigation/localization findings; no additional page-specific defect confirmed |
| episodes | F01–F03, F05–F09, F12 |
| index | F01–F08, F10, F12 |
| legal | Shared navigation/localization findings; both theme/locale toggles work with their actual option values |
| new-episode | F07 plus shared navigation findings |
| publication-links | Public layout; no app drawer by design; shared locale/theme option text issue |
| publish-and-channels | F01–F03, F05–F09 |
| publish-tiktok | F04, F07 plus shared navigation findings |
| publish-youtube | F04, F07 plus shared navigation findings |
| render | F02, F03, F06, F07 plus shared navigation findings |
| research | F05–F07 plus shared navigation findings |
| series-detail | F01–F03, F05–F08, F12 |
| series-editor | F07 plus shared navigation findings |
| series-list | F01, F05–F08, F13 |
| shell | Shared navigation/localization findings; no additional page-specific defect confirmed |
| story-editor | F02, F03, F06, F07 plus shared navigation findings |
| story-review | F05–F07 plus shared navigation findings |
| video-review | F07 plus shared navigation findings; media fixture itself not contrast-certified |

## Findings

| ID | Severity | Page(s) | Breakpoint / theme / locale | Element / selector | Observed, measured evidence | Root cause | Concrete fix rule |
|---|---|---|---|---|---|---|---|
| F01 | Med | Every app page using shared current-page nav; explicitly reproduced on episodes | 1280, 768, 360; both themes/locales | `.primary-navigation .navitem[aria-current=page]:hover` | Episodes before hover: background `oklch(0.381162 0.0236057 186.399)`; hover: `oklch(0.22534 0.0110398 167.9)`. Its 183×44 desktop rectangle is unchanged. Nine sampled points still hit the link/descendants; ancestor overflow is visible. Evidence: `F01-nav-active-before.png`, `F01-nav-active-hover.png`; `nav-episodes-768-dark.png` shows the rail tooltip visible, not clipped. | `tokens.css:488` sets current-page primary-soft; equally specific later `:root .primary-navigation .navitem:hover` at **490** overwrites it. Current state is visually covered by the darker hover fill, not another layer. | Give current+hover/current+focus an explicit retained active treatment, or scope ordinary hover to `:not([aria-current=page])`. Do not introduce arbitrary z-index/overflow changes. |
| F02 | Med | episodes, dashboard, index, series-detail, bible summaries, publish-and-channels, render, account-security, story-editor | Desktop; both themes/locales; inner-wrapper clipping also persists responsively | `.one-line-table .cell-value .icon-tooltip`, status/stage/platform tooltip hosts | Episodes status tooltip measures 229.4×32 above the chip, but its center hit-tests to a table header, and screenshot shows no visible tooltip. Both `.cell-value` and the `td` clip its rectangle. Evidence: `episodes-1280-dark-vi-hover-26.png`, `F02-tooltip-F03-focus.png`; raw JSON records clippers. | `tokens.css:466` clips cells; **470** clips `.cell-value`. Tooltip at **356** uses absolute positioning and z-index80, which cannot escape ancestor clipping. | Clip only the inner text/ellipsis span. Keep tooltip host ancestors overflow-visible, or render tooltip in a dedicated popover/overlay layer outside the table clipping hierarchy. Keep true table scroll containment separate. |
| F03 | Med | Table chip/stage controls on the F02 pages | 1280 and responsive tables; both themes/locales | Focusable `.chip`, `.stage-inline` inside `.cell-value` | A 24px chip in a 32px wrapper has 4px vertical clearance; 3px outline + 3px offset needs 6px. Top/bottom lose at least 2px and a flush left edge loses the outside ring. Evidence: `F02-tooltip-F03-focus.png`. The row-title full-row focus ring is correct and is **not** this finding (`F03-title-focus.png`). | `tokens.css:470` overflow-hidden intersects the shared outer focus ring. | Use a visible host-level focus indicator or an inset ring with sufficient contrast; do not remove focus styling. Keep ellipsis on a text child instead of clipping the focused component. |
| F04 | Med | index status specimens; publish-youtube and publish-tiktok narrow status specimens | 360, some 768; both themes/locales | Non-table `.chip > .icon-tooltip` | On index/360 VI, rendering tooltip is **x=-153.55, width=288, height=50**: more than half lies left of the viewport. Failed tooltip at 768 is x=-48.38. Evidence: `F04-tooltip-offscreen.png`; per-variant hover screenshots/JSON. | `tokens.css:356` right-aligns every tooltip to its small trigger, with a max-width but no viewport collision/placement handling. | Prefer an overlay with flip/shift placement and viewport gutter. If keeping CSS-only specimens, give edge triggers an explicitly safe inline-start placement and width constraint; test every status in both locales. |
| F05 | High | Shared one-line collection row actions: episodes, dashboard, series-list/detail, research, bible, publishing and review tables | Coarse pointer; reproduced at 360; both-theme rule | `.one-line-table .row-icons .icon-btn::before` | Visual buttons32×32; centers36px apart; coarse hit boxes44×44 overlap by **8px**. First View rect x227–259; Approve rect x263–295. **Point (258,400), inside the visible View button, hit-tests to Approve.** Evidence: `F06-coarse-targets.png`. No mutation was invoked. | Coarse spacing at `tokens.css:413–415` is overridden by later one-line row rule **479**, resetting gap to4 and padding to0 while retaining44px pseudo hit areas. | Put a coarse override after one-line rules: minimum12px horizontal gap, sufficient action-column width, and at least44px row/hit spacing, including dense rows. Verify hit-test ownership across the entire visible button and expanded target; never overlap View/Approve/Reject hit regions. |
| F06 | Med | All responsive collection tables, especially episodes, dashboard and series-list | 768 and 360; both themes/locales | `.one-line-table`, `tbody tr`, labelled `td` grids | Episodes rows grow **44→367px**, series rows44→363px, dashboard36→275px. At768, eight episode fixtures consume2936px. Evidence: `episodes-768-dark-vi.png`, `series-list-360-light-vi.png`, `dashboard-768-dark-vi.png`. | `tokens.css:444–457, 568–580` deliberately hide headers and convert tables into vertical label/value stacks. This conflicts with the owner's latest table-only/compact-row direction; older `review-tables.md:65` explicitly permitted phone stacking. This is a **contract discrepancy**, not an automatic WCAG failure. | Commission one explicit current responsive table rule: keep comparison columns/one-line rows at tablet, with contained horizontal scrolling and pinned identifying/action columns as needed; apply the owner's chosen table treatment at360 too. Update earlier phone-stack wording so it cannot regenerate this layout. Do not fix by shrinking text further. |
| F07 | Med | Shared shell selects/nav; episodes, new-episode, index, research, series-detail and video-review form options | All breakpoints/themes; VI and EN | `#theme option`, `#status-filter option`, `#series-filter`, related native selects; `.navitem[title][aria-label]` | English episodes still displays **“Tất cả / All”**; series choices and several field options similarly combine languages. Nav native title/accessible name remains **“Tập phim / Episodes”** regardless of active locale. Evidence: `F07-bilingual-controls.png`; default/EN page screenshots. | Strings are literal bilingual option text and title/aria-label values outside the locale-specific trees. Options cannot be localized by nested hidden spans. | Render a locale-specific option list and localized accessible names/native titles while retaining stable option values. Only the language chooser may intentionally expose both language names; operational controls must show the active language alone. |
| F08 | Low | Shared 72px tablet rail on app pages | 768; both themes/locales | `.sidebar > .wordmark` | Wordmark is only55px wide at10px text; **Studio splits “Studi” / “o”** into a stranded final letter. Evidence: `nav-episodes-768-dark.png`. | `tokens.css:522–526` introduces72px rail and `font-size:10px; overflow-wrap:anywhere`. | Use an approved compact wordmark/monogram for the rail, retaining a full accessible name; do not break brand words arbitrarily. |
| F09 | Low | episodes, dashboard and any responsive updated/created/count column | 768 and 360; both themes/locales | `td[data-col=updated] .cell-label` and analogous numeric cells | At768, normal labels start x109; Updated has a120px label box atx109 but right-aligned text at its far edge, while its value jumps to x731. Evidence: `nav-episodes-768-dark.png`; measured computed `text-align:right`. | Later generic numeric alignment `tokens.css:478` beats earlier responsive resets425/453; later grid rules do not reset descendant label alignment. | In responsive labelled treatment, explicitly left-align `.cell-label`; right-align numeric values only if the selected mobile/table design calls for it. If F06 removes labelled stacks, remove the obsolete rule instead. |
| F10 | Low | index only | All breakpoints/themes/locales | Two visible `button.btn.ghost.btn-sm.icon-btn` specimens: View and Request changes | Both have aria-label but **neither title nor tooltip child**; real hover produces no explanatory tooltip, unlike production row icons. Evidence: `F10-missing-tooltip.png`. | Component specimens manually omit `.icon-tooltip`, so the source-of-truth page demonstrates a different interaction contract. | Use the same localized tooltip markup/behavior as the row-action component, including keyboard focus. Keep the accessible name; do not replace it with the tooltip alone. |
| F12 | Low | episodes, dashboard, series-detail, index More actions | All breakpoints/themes/locales | `.row-more > summary.icon-btn::after` | A second `⌄` remains beside the ellipsis. At1280, summary x1217,width32; its18px SVG begins **x1215.78**, 1.22px outside the button, displaced by the inherited8px gap and extra chevron. Evidence: `F12-more-chevron.png`. | Generic inline page rule `details>summary::after` (e.g. `episodes.html:12`) appends a caret to the icon-only More summary. Shared icon rule hides the native marker but not this pseudo-element. | Scope generic disclosure carets to text disclosures, or set `.row-more > summary::after { content:none }`; center the single ellipsis and align its wrapper with sibling actions. |
| F13 | Med | series-list | All breakpoints/themes/locales | `#series-list-table-1` | Nine headers include title/topic/language/aspect/voice/style/schedule/episode count/actions, but **no dedicated Status column/chip**; autopilot state is buried in the long schedule text, truncated at desktop. Evidence: `series-list-1280-dark-vi.png`. | Series table retains the earlier phase field inventory instead of the owner's latest explicit collection-status pattern. | Add a compact, explicit series-state column using actual existing Active/Archived state; keep autopilot on/off distinct from lifecycle state. Do not invent episode wire statuses or a pipeline stage for a series. Preserve icon actions and one-line geometry. |

## Cross-page rules to commission in `tokens.css`

1. **State precedence:** explicit current+hover/current+focus nav rules; normal hover cannot erase the current-page marker.
2. **Separate clipping from interaction:** text child owns ellipsis; focusable/tooltip hosts do not own `overflow:hidden`. Preserve the existing successful full-row keyboard outline.
3. **One tooltip system:** overlay/viewport-aware placement, safe gutters, one localized label, visible on hover and keyboard focus; no z-index-only fixes for ancestor clipping.
4. **Coarse rules last:** 44px nonoverlapping hit regions, minimum12px icon spacing, sufficient column width and minimum44px dense row spacing; verify hit ownership, not dimensions alone.
5. **One responsive collection contract:** remove contradictory phase overrides; keep compact comparison structure per current owner instruction. Horizontal scrolling must stay inside a named table region, never on the whole page.
6. **Disclosure variants:** text summaries may have a caret; icon-only More summaries contain one centered icon and no inherited pseudo-caret.
7. **Rail branding:** use an approved compact mark; no `overflow-wrap:anywhere` on the full brand name.
8. **Locale-sensitive content is markup/data, not a token:** separately fix native option text, accessible names and tooltip strings. Add series lifecycle data to its column without manufacturing backend states.

## Exclusions and evidence integrity

- No broken local links, duplicate IDs, general default text-contrast failures, page-level overflow, double-framed toolbar inputs, or wrong measured default chip/icon dimensions were confirmed. Do not add speculative findings for these.
- The search input intentionally has no inner border/outline because its single outer `.control-search` owns border and `:focus-within`; it is not a missing-focus defect.
- `.row-open:focus-visible { outline:0 }` is paired with the visible full-row outline at `tokens.css:376`; it is not a keyboard-focus defect.
- Hidden historical table headers can retain layout rectangles while being clipped for accessibility. An initial automated word-break flag on `control-size-audit.html` was visually rejected: the actual mobile data uses readable labelled rows. Do not commission a fix for that false positive.
- Legal selects use label-valued options rather than the other pages' `dark/light` and `vi/en` values. Both themes/locales were rechecked with their actual option values; no legal-toggle failure is reported.
- Static-prototype limitations already documented in `design-plan.md:112–115` (document-language persistence, production modal trap/Escape restoration, fixture-only transitions) were not relabeled as newly discovered UI defects.
- Bounded evidence triage used `judge_batch`: **typesafe/jev-1.13.0, 13/13 completed, 0 failed, cost $0.000213822**. Low-confidence items F06/F08/F12 were grounded again in source/rectangles/screenshots. F06 remains explicitly a contract conflict. Thirteen candidate findings became twelve confirmed findings after visually rejecting the hidden-header false positive.
- Initial/final modification timestamps for root HTML/CSS/Markdown inputs are unchanged. All new artifacts are confined to `/tmp/nts-design-review/` and this `local://` report. No production build, repository test or source edit was performed.

## Remaining acceptance caveat

The active-nav hover masking is reproduced and pinned to a selector with before/after screenshots. **A distinct physical cover/clipping defect on the Episodes nav cannot truthfully be claimed from the current root files**: desktop/rail/mobile hit tests and screenshots contradict that mechanism. If the owner means another transient visual, preserve their screenshot/context before commissioning an unrelated layer fix. This caveat does not invalidate the twelve measured findings above.

## Phase 10 result — all QA findings implemented

Scope: all 25 root HTML pages at 1280 / 768 / 360, dark/light and VI/EN — 300 variants. Raw evidence: `assets/audit/phase-10-responsive.json`; renders: `/tmp/nts/phase10/`. No files under `backups/` were changed.

### Verification summary

| Check | Before | After |
|---|---|---|
| Variant geometry, 4 theme/locale combos | 12 findings | **300/300 pass** (75 per combo) |
| Page-level horizontal overflow | F06 stacks/overflow | **0** |
| Header/body column mismatch at 768/1280 | n/a | **0** |
| Row height at 768 | 367px episodes, 363px series, 275px dashboard | **44px max** |
| Row height at 360 | stacked labels, 275–367px | **72px max** |
| Coarse action hit boxes | 36px centres, 8px overlap | **44px centres, 0 overlap** |
| Tooltip viewport placement | x=-153.55 on index/360 | **200/200 inside 8px gutter** |
| Visible combined `vi / en` strings | options, titles, descriptions | **0** |
| Language/theme option text | “Tất cả / All”, “Tối / Dark” | **active language only** |
| Nav current+hover / current+focus | active fill replaced by hover | **same active fill** |
| Rail wordmark at 768 | “Studi / o” split | **“NT” monogram** |
| More-actions caret | inherited `⌄` | **none** |

### Finding closure

| ID | Result |
|---|---|
| F01 | `navitem[aria-current=page]` retains primary-soft on hover/focus-visible. |
| F02 | Tooltips use fixed overlay placement and viewport gutters; they escape table clipping. 200 edge/action/stage/chip checks pass at 360 and 768. |
| F03 | Text-only cells clip with ellipsis; row-open hosts stay overflow-visible for the full-row target. Focusable chips/stage/platform icons use a 3px inset ring (`outline-offset:-3px`). |
| F04 | Index, YouTube and TikTok edge specimens flip/shift inside the viewport. |
| F05 | Coarse action spacing is 12px around 32px buttons, giving 44px centres; hit ownership verified at the visible-button edge. Dense rows are 44px on coarse/mobile. |
| F06 | 768 keeps a real table with 44px rows and hides low-priority columns (`series`, `channels`, `next`, `updated`, `count`, format/account/etc.; `stage` when a channel column is present). Full values remain in the row `title`. 360 uses a compact two-line grid row: title + status, then stage/schedule + icon actions, capped at 72px. |
| F07 | Options, `title`/`aria-label`, native cell titles, `.sr` control labels and `aria-describedby` table descriptions are localized at runtime. No operational control shows a combined language string; the language chooser is the only intentional bilingual exception. |
| F08 | The 768–1000 rail uses an `NT` monogram with `font-size:0`, retaining `aria-label="Night Tales Studio"`. |
| F09 | Numeric/label alignment is reset to the left in the responsive table treatment; the redundant `updated` column is omitted at 768. |
| F10 | Index View and Request changes specimens now use the same localized `.icon-tooltip` contract, including keyboard focus. |
| F12 | `.row-more > summary::after { content:none }`; the single ellipsis is centred. |
| F13 | `series-list` adds a dedicated status column with Active / Active / Archived chips and tooltips. Desktop shares rebalanced to 17/16/8/8/5/8/8.5/12/4.5/13 so titles and status remain readable. |

### Implementation notes

- `ui-runtime.js` localizes options, attributes, native cell titles and `aria-describedby` descriptions; adds compact-table classes; assigns tablet column widths; writes active-language row titles; and places tooltips as viewport-aware fixed overlays.
- At 768 the table is a real table with a `table-row` header; previous header rows were `display:block`, which caused header/body column drift. Column shares are written as pixels only for the tablet range and restored from the original percentages when leaving it.
- `cell-value` clipping is intentionally skipped for cells containing `.row-open`, so the full-row click/focus target still spans the row.
- 360 compact rows use `grid-template-areas: "primary status" "meta actions"`; hidden fields remain available through the row `title` and the desktop/tablet layout.

### Remaining limitation

Opening every closed `<details>` specimen at 360 can still reveal the pre-existing wide state-table overflow documented in Phase 9. That is outside the default rendered state and was not counted in this phase’s pass/fail matrix.

## Owner-reported 2026-10-22 (queued as Phase 16)

| ID | Page | Observed (1280 dark VI) | Fix rule |
|---|---|---|---|
| O16-1 | series-list (shared `.control-search`) | Focused search frame draws `outline 3px` + `offset 3px` → ring 6px outside the 32px frame, colliding with the segmented filter (8px gap) and the table border below; reads as input overflowing its frame | Focus indicator inside/on the frame (border + inset 1px shadow) for toolbar/dense controls; no ring may touch neighbours |
| O16-2 | series-list (+ any page with a phase-15 title info-trigger) | H1 starts at x=331, subtitle/toolbar at x=240 | Title row left-aligned with content; info icon 8px after text |
| O16-3 | series-list | Header "Số tập" clipped to "Số tậ" | Wider column or short label + tooltip |
