# Night Tales Studio — Phase 1 foundation

## Pass 1: proposal grounded in the production desk

One owner needs a desk, not a control tower of statistics. The first read is work awaiting a decision; the second is production moving without intervention; the third is completed work waiting for distribution. Avoid horror artwork in app chrome: the stories are the content, while the desk must stay clear during long review sessions.

### Color: extracted base palette

Values below come from the existing `web/src/app/shared/ui/theme/_tokens.scss`. Keeping the current brand colors is deliberate: the screenshots show a hierarchy and component integration problem, not evidence that the brand needs replacing.

| Role | Dark | Light |
|---|---|---|
| Room / background | `#202523` | `#EEF2F0` |
| Sheet / surface | `#2A312E` | `#FFFFFF` |
| Ink / foreground | `#F4F5EF` | `#16211D` |
| Secondary text | `#BFC9C4` | `#4B5A54` |
| Signal / action | `#6ECAC0` | `#006C65` |
| Failure / playhead | `#FF786C` | `#B43B35` |

Six named hex colors per theme. Existing outline/soft/control-outline values are retained in the CSS compatibility layer; these are structural colors rather than extra brand accents. New information and warning pairs are derived with OKLch and remain restricted to semantic states. Teal is the sole action accent. Error, warning, success and info use text/symbols, not color alone. No decorative coral/teal brand stripes.

### Typography

- **Be Vietnam Pro**, OFL 1.1, Vietnamese/Latin/Latin Extended, self-hosted, 400 / 500 / 600. Use for display, headings, controls and body because this is a utilitarian production interface. Keep one family for consistent Vietnamese diacritics. The installed package is already imported by `_typography.scss`; reuse it rather than adding a font dependency.
- **System monospace** only for machine-readable setup secrets and recovery codes; not a chosen display face and not relied on for Vietnamese UI text. No downloadable second font is required.
- Local WOFF2 data embedded in `tokens.css`; OFL copyright and full license retained in `font-license.txt`. No network fetch or external asset. Production can extract data URLs into `/assets/fonts` without changing family/weight roles.

| Role | Size | Leading | Weight |
|---|---|---|---|
| Showcase display | 40px | 1.30 | 600 |
| Page title | 30px (26px mobile) | 1.35 | 600 |
| Section title | 22px | 1.40 | 600 |
| Component title | 18px | 1.45 | 600 |
| Body / input | 15px | 1.60 | 400 |
| Action / label | 14px | 1.50 | 500 |
| Caption | 13px | 1.55 | 400 |

No uppercase tracking, italic accent words, aggressive negative tracking or decorative metadata separators. Limit prose to 68ch. Titles wrap naturally with balanced line breaking where available; essential episode titles are never ellipsized.

### Layout candidates

**A — equal three-column board.** Easy category comparison but three cramped columns are poor for long Vietnamese titles, and review work is no more important than background work. Rejected in pass 2.

```
[ navigation ] [ top bar                           ]
[            ] [ needs review | running | ready    ]
[            ] [ card         | card    | card     ]
```

**B — prioritized production desk.** Left-aligned review queue occupies the larger lane; a quieter side lane holds running jobs and ready work. Shared vertical rhythm, different structures according to the job. Selected.

```
1280 viewport: 216 navigation + remaining workspace
+----------------+----------------------------------------------+
| Night Tales    | Bảng điều khiển       VI/EN   Theme   Account |
|                +----------------------------------------------+
| Bảng điều khiển| Tổng quan                         Làm mới    |
| Loạt bài       | Cần bạn xem (2)       | Đang thực hiện (2)   |
| Tập phim       | review story row      | progress / stage     |
|                | review video row      | progress / stage     |
|                |                       | Sẵn sàng đăng (1)    |
| Kết nối        |                       | destination + action |
+----------------+----------------------------------------------+
768 viewport: 176 navigation, single content lane
+-------------+-------------------------------------+
| full labels | top bar; controls may wrap          |
|             | needs review                        |
|             | running                             |
|             | ready                               |
+-------------+-------------------------------------+
360 viewport: 16px gutters, no persistent side rail
+--------------------------------+
| Menu          VI/EN Theme User |
| page title / context           |
| needs review, full title       |
| descriptive action             |
| running / filled progress      |
| ready / destination            |
+--------------------------------+
```

Auth has a restrained production-track introduction beside a 440px form at wide widths; at tablet/mobile, form first with no decorative intro. All auth steps are separate CSS target panels, not a long wall of simultaneous forms. A compact navigation above the panel permits review of the specimens.

### Principles

1. Waiting on the owner always precedes autonomous activity. Do not lead with totals or charts.
2. One production-track motif: connected segments encode actual ordered work. No soundwave, moon, film-grain or fake cover imagery.
3. Structure matches purpose: review rows, progress rails, publish summary. Do not put every datum into the same rounded card.
4. First run is a distinct state. Suppress empty operational queues and show one clear path to creating a series.
5. Preserve vi/en keys and all eleven backend status keys. Use honest labeled fixtures, never imply private production data was read.
6. Keyboard and mobile are first-class: 44px targets, visible focus, full labels, explicit error recovery and no automatic animation.

## Pass 2: review against generic defaults, before building

| Initial tendency | Why it fails this brief | Revision committed before implementation |
|---|---|---|
| Three equal rounded boards | Makes passive rendering compete with decisions; familiar task-board template | Review lane is larger; progress and distribution have different compact structures |
| Oversized dark hero with mint statistic | Adds marketing emphasis to a private daily tool | Operational heading at 30px; no hero or metric tiles in dashboard |
| Coral decorative playhead on every panel | Existing repeated marks are already visual noise | Remove decorative bars; coral only signals failure, connected track only communicates a real sequence |
| Three empty queues plus welcome card | Exactly the redundancy in screenshot 02 | First-run branch replaces the dashboard content, rather than appending to it |
| Icon-only tablet rail | Hides production terminology and makes a one-owner tool harder to scan | 176px labeled sidebar at 768; native disclosure menu at 360 |
| Theme and state controls everywhere | Designer controls do not belong in product workflow | Theme/language in account chrome; specimen state controls only on system/state documentation, auth examples in disclosures |
| New premium display font | Unnecessary download and visual voice for dense Vietnamese workflow | Keep the existing OFL Vietnamese family, improve scale and leading instead |

Distinctiveness comes from the review-first desk and visible production sequence, not unusual decoration. Do not copy another product's UI. No image asset is required for this foundation: no cover, place or person is depicted; demo episode titles are clearly marked content fixtures.

## Implementation and Angular mapping

- Deliverables: `review.md`, this plan, `tokens.css`, `index.html`, `shell.html`, `auth.html`, `dashboard.html`. `brand-spec.md` records extracted system; `font-license.txt` retains the bundled font license.
- Every page links `tokens.css`; shared specimen styles live inline in each HTML to keep each page portable with that single dependency. No JavaScript, CDN, remote image or library.
- `--nts-color-*`, `--nts-font-*`, `--nts-space-*`, `--nts-radius-*`, `--nts-type-*`, `--nts-control-*`, `--nts-motion-*`, `--nts-elevation-*` map directly to `_tokens.scss`. Existing room/sheet/ink/signal/playhead and Material semantic aliases remain available. Add `info`, `warning` and associated soft pairs, layout dimensions and focus tokens.
- Production uses `[data-theme='dark'|'light']`. Specimen theme selection applies the same variables with CSS `:has()`. Locale selection hides one language tree with CSS; `lang` on translated spans gives pronunciation. Static HTML cannot change document language, announce the locale change or persist choice between pages/reloads. Angular must update `html.lang`, use the existing locale service, and persist the chosen language. This is a known consequence of the explicit no-JavaScript scope, not a proposed behavior regression.
- Native details demonstrates account menu and mobile navigation. Production should retain shell inert/focus restoration, Escape dismissal and route focus behavior.
- Dialog is an inline specimen with dialog semantics, not a pretend modal. Angular Material must provide actual focus trap, initial focus, Escape/cancel and restoration to invoker. Toast specimen uses `role=status`; announce once after success, not continually.
- Auth forward actions link to the next visual specimen; no secrets are submitted. Demo QR encodes a public test secret; recovery codes are dummy values. No clipboard simulation. File download uses an inline data URL and explicitly labels demo codes.
- Progress percentages are explicitly demo render progress. `next-action-row` currently has no progress payload; add a view-model field from actual job progress, show indeterminate progress if unavailable. Never invent production percentages or ETA.

## State and accessibility contract

| Area | Coverage |
|---|---|
| System | Default/hover/focus/active/disabled/pending controls; input error and hint; select; stepper; all 11 statuses + unknown; empty/error/loading/success; inline dialog; toast |
| Shell | Connected, reconnecting, paused and profile-load error specimens; mobile nav and account menu; loading language save / failed save notes |
| Password | Default, invalid credentials, pending, expired session, rate limit; next visual step is TOTP |
| TOTP | Single numeric pasteable input, invalid/expired/pending; separate recovery-code branch |
| Forgot/reset | Neutral email sent confirmation, invalid/expired link, mismatch/short password, pending and success |
| MFA setup | Valid demo QR + manual key, QR failure, pending, invalid code, expired setup/reauth; ten dummy recovery codes and acknowledgement gate; completion |
| Dashboard | Populated, first run, loading, failed, stale-refresh warning, all-clear per-section text and success specimen |

Focus order: skip link → navigation → locale/theme → account → main heading/context → content/actions in visual order. In auth: utility controls → step links → heading → email/password or code → primary → recovery/back links. Disclosures remain keyboard reachable with native Enter/Space. No tabindex greater than zero. Errors associate through `aria-describedby` and `aria-invalid`; pending messages use polite status. Progress includes text plus native `<progress>` value. Core controls have 3px focus outlines with 3px offset. No content obstructs focus. No time-driven motion; reduced-motion tokens reduce duration to zero.

Testing targets: 1280, 768, 360 in both themes, both languages. Static review checks breakpoint math, min-width:0, text wrapping, tokens, contrast pairs, asset locality, IDs, no placeholders and state coverage. One host render is used for visual layout review when available. Full browser, keyboard, screen-reader and Angular integration testing remain required; a static HTML foundation is not proof of WCAG certification.

## Completed foundation verification

- All four HTML pages parsed with balanced tags, unique HTML/inspection IDs and complete local links, including fragment targets. Each links `tokens.css`. No JavaScript, remote assets, template variables or missing local dependencies.
- Font subsets: installed Be Vietnam Pro WOFF2, weights 400/500/600, Vietnamese + Latin Extended + Latin, embedded in CSS; full installed license retained. QR is a real encoded public test fixture, with a square viewBox and explicit 208×208 dimensions. Ten recovery codes are clearly labeled dummy values.
- CSS state review covered auth default and every fragment target, returning to password, recovery acknowledgement, dashboard populated/first-run/loading/error selection and first-run direct link. Native select/disclosure behavior is used; authenticated actions remain static specimens.
- Width budgets checked: 1280 = 216px navigation + 1064px workspace; 768 = 176px + 592px; 360 = 16px gutters + 328px content. Dashboard lanes collapse below 1000; auth centers one form below 1000; mobile setup stepper is vertical. No fixed-width content forces a mobile horizontal scroll.
- Exact source text pairs: dark ink/sheet 12.14:1, muted/sheet 7.84:1, primary button 8.05:1; light ink/sheet 16.54:1, muted/sheet 7.27:1, primary button 6.30:1. Control outlines: dark 3.15:1 and light 3.57:1 against sheet; at least 3.16:1 against room. Semantic pair checks include derived soft backgrounds. Error-soft tint was reduced from 9% to 6% for more AA contrast headroom.
- Primary hover improves contrast by lightening teal in dark mode and darkening it in light mode. Other hover fills use a darker surface in dark mode and white in light mode, retaining foregrounds. Reduced-motion override, skip link and focus-visible rings present.
- One dashboard render was visually inspected. The review led to removing the prominent specimen selector from the operational first view, constraining ready-status chip width and fixing bilingual wordmark specificity. The return-to-password selector was also corrected during state review. Final affected rules were rechecked statically.
- Not claimed: exhaustive rendered coverage of 24 viewport/theme/language combinations, screen-reader certification, browser keyboard execution, authentication, focus-trapped modal behavior or persistence. Those need browser/Angular integration checks. This is a verified static foundation, not a deployed application.
