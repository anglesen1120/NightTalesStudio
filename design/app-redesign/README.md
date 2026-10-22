# App UI redesign mockups (approved 2026-10-22)

Static HTML mockups that redesign the whole owner-facing app (plan 10 web UI): shell, dashboard, series, story, episodes, review, publishing, account and system pages. Design artifacts only — **no Angular code is changed by this folder**, and the earlier deck in [`design/ui/`](../ui/) is untouched.

- Source: the OpenDesign project `nts-app-ui-redesign-20261021` (generated 2026-10-21/22 in numbered phases; the review documents below follow that order).
- Owner approval: **2026-10-22**, in the redesign session, for the whole redesign as a design direction. Approval is for the mockups, not for any API, copy key or Angular implementation detail.
- Scope: 25 pages, `vi` + `en` copy in every page, dark theme by default plus a light theme, responsive down to 360 px (shared media queries at 599 / 767 / 768 / 1000 / 1100 px plus page-local ones at 650 / 800 / 1240 px; measured at 360 / 768 / 1280), WCAG 2.2 AA states (default, hover, focus-visible, active, disabled, loading, empty, error, success).
- The light theme is a **CSS-only hook** in this deck, not a working preference: `tokens.css` keys it on `[data-theme='light'], :root:has(#theme-light:checked)`, no script sets `data-theme`, and nothing persists the choice — selecting Light in the page header resets on the next page load. A real implementation has to persist it (the Angular app already owns that through the theme-preference bridge, 10.1.3-T1).

## How to view

Open `index.html` through a static server rather than `file://`; every page loads `tokens.css` and `ui-runtime.js` by relative path, so serve the folder as the site root:

```bash
cd design/app-redesign
python3 -m http.server 8000
# then open http://127.0.0.1:8000/index.html
```

`index.html` is the page index and style guide: design direction, token table, component gallery, and links to every page. Production fonts are embedded as WOFF2 data URLs inside `tokens.css`, so the deck renders with no network access; `font-license.txt` carries the OFL copyright and licence for Be Vietnam Pro.

## Pages

| Page | What it shows |
|---|---|
| `index.html` | Page index, design direction, token and component gallery |
| `shell.html` | App shell: sidebar, top bar, user menu, language switch, realtime status |
| `auth.html` | Sign in, TOTP verification, forgot/reset password, MFA setup with QR and recovery codes |
| `dashboard.html` | Production desk: needs-you / running / ready-to-publish strips, first-run empty state |
| `series-list.html` | Series collection: filters, one-line rows, numbered pager |
| `series-editor.html` | Series create/edit wizard: topic and language, frame and aspect, voice picker, visual style |
| `series-detail.html` | Series overview: sections, create-episode entry, autopilot pause/resume |
| `bible.html` | Story bible: memory entries, per-entry provenance, edit and history |
| `research.html` | Sources and facts: source rows, find sources, approve, citations |
| `episodes.html` | Episode collection: status and series filters, next-action buttons, pager |
| `new-episode.html` | New episode: idea, options, generation start, blocked and loading states |
| `story-review.html` | Story review: versions panel, diff, inline annotations, approve/reject |
| `video-review.html` | Video review: player, render-quality notes, skipped-scenes banner, approve/reject/re-make |
| `render.html` | Episode production: render job state, stages, worker progress, cost |
| `story-editor.html` | Story editor: segment editing, character counter, conflict handling |
| `publish-and-channels.html` | Publishing and channels: queue, consent, channel connections |
| `publish-youtube.html` | Publish to YouTube: metadata, playlist, visibility, upload progress |
| `publish-tiktok.html` | Publish to TikTok: metadata, cover, upload progress |
| `channel-callback.html` | Channel connect result (OAuth callback) success, error and re-auth states |
| `publication-links.html` | Publication consent/veto links: consent text, cancel-by-link, anonymous cancel result |
| `account-security.html` | Account security: sessions table, sign-out other sessions, legal acceptance |
| `legal.html` | Legal documents: terms and privacy, acceptance dialog |
| `autopilot.html` | Series autopilot: settings, gates, run history, pause reasons |
| `dialogs.html` | Shared overlays and system feedback: confirm, conflict, session ended, connection lost |
| `control-size-audit.html` | Measurement deck for control sizes across the other pages |

## Supporting files

| File | Purpose |
|---|---|
| `tokens.css` | Design tokens plus the embedded Be Vietnam Pro WOFF2 data URLs; single source of truth for colour, type, spacing, control and chip sizes |
| `ui-runtime.js` | Language switch and shared page behaviour: rewrites `vi`/`en` pairs, sets `<html lang>`, localises option labels, table cell titles and description text, enhances/lays out tables, and drives the tooltip, help-panel and popover widgets. It contains **no theme code** |
| `pager.js` | Numbered-pagination behaviour used by the list pages (10 CR-57 proposal) |
| `story-workspace.css`, `story-workspace.js` | Story review/editor workspace: diff, version rail, annotation layer |
| `font-license.txt` | SIL OFL 1.1 licence for Be Vietnam Pro |
| `assets/production/source-notes.txt` | Provenance of the media fixtures used by the review pages |
| `assets/production/midnight-town.en.vtt` | English subtitle track used by the video and publication pages |

## Review history

The design work ran in phases; these documents are the evidence trail, in the order they were produced.

| File | Content |
|---|---|
| `BRIEF.md` | The brief: the owner's verdict on the current UI, the supplied screenshots and Angular source map |
| `review.md` | Honest review of the current app (screens, hierarchy, typography, colour, states) |
| `brand-spec.md` | Brand system extracted from the existing `_tokens.scss` / `_typography.scss` and app screenshots |
| `design-plan.md` | Foundation pass: direction, token decisions, shell, plus the log of review-driven fixes |
| `review-tables.md` | Phase 5 collection-table review, one-line rows and column shares |
| `phase-6-verification.md` | Phase 6 table geometry and navigation verification across 15 pages |
| `phase-7-coverage.md` | Phase 7 route and state coverage against `plans/10-web-ui.md` §5.2 |
| `info-panels-review.md` | Phase 15 read-only review of the explanatory/help panels on all 25 pages |
| `phase-15-spec.md` | Phase 15 specification: collapse help panels without hiding working state or losing an action |
| `design-review-qa.md` | Final design QA: per-page verdicts, measurements and the residual list |

## Known gaps (recorded, not implemented)

- **DASH-1…DASH-10** (plan-10 record **10 CR-56**, a number that exists only in PR #280 — `docs/dashboard-api-gaps`, still **open**, so the highest CR on `develop` is 10 CR-55) — the approved dashboard renders ten data points the API cannot supply yet (tile trigger/timing, list-level step and percent, run ETA/elapsed/started-by, list-level `nextActions` and status-entry time, aggregated publication schedule, reloadable activity history, cancel during story generation). Cite them by name until #280 merges. No route is invented here.
- **DASH-9 needs a DEC 10.14 amendment.** Reloadable activity history is not a revival of the user-facing activity feed that DEC 10.14 dropped (09.3.6-T2; `plans/09-backend-api.md` §09.3.6: "There is no HTTP feed over this table"), so any read route for it needs an explicit amendment before a task exists.
- **Numbered pagination** (plan-10 record **10 CR-57**, again only in PR #280) — owner list screens (`/series`, `/episodes`, any other pager) use numbered pages with a total ("Showing 21–40 of 245", pages `1 2 3 … 13`, 20/50/100 rows) instead of "load more". The wire shape is still `[OPEN]` because the cursor-only contract carries no total.
- **No Angular implementation yet.** Mockup CSS is not application CSS: `tokens.css` here is a superset/rework of [`design/ui/assets/tokens.css`](../ui/assets/tokens.css) (adds `--control-height`, `--control-pad`, `--control-text`, `--nts-chip-*`, hover/on-error roles; drops `--nts-color-hairline` and `--nts-color-playhead-soft`), so adopting it is a design-token decision, not a copy.

## Media fixtures not committed

Committed: the subtitle track above. **Not committed** (repository policy `.gitignore`: `*.png`, `*.mp4`): `assets/production/scene-1.png` … `scene-5.png` (1.0–2.6 MB each) and `assets/production/midnight-town.mp4` (11.4 MB). They remain in the OpenDesign project; copy them there to see those elements rendered.

Pages that reference an uncommitted fixture (they render without it, with an empty frame or the poster area missing):

| Referenced but not committed | Referenced by |
|---|---|
| `assets/production/midnight-town.mp4` | `video-review.html`, `publish-youtube.html`, `publication-links.html` |
| `assets/production/scene-1.png` … `scene-5.png` | `video-review.html` |
| `assets/audit/*.json` (18 MB of raw measurement output, excluded on purpose) | `control-size-audit.html`; cited by `design-review-qa.md`, `phase-15-spec.md`, `phase-6-verification.md`, `phase-7-coverage.md`, `review-tables.md` |

`backups/`, `.file-versions/`, `*.artifact.json` and `assets/audit/` are excluded from this copy as working state, not design.

## Next step

Angular implementation is **not started**. Before UI code lands, run the implementation plan through the `design-ui` agent with the usual T-tier gates (design task first, then per-task QA and reviews), and settle the design-token question in a DEC if `tokens.css` here is to replace `design/ui/assets/tokens.css`.
