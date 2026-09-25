# 10.5.1 — Episodes: list, new + generate, story editor (wave 1, P)

> Refines: `../../../10-web-ui.md` §4 (epic 10.5, stories **10.5.1**, **10.5.2**, **10.5.3**, **10.5.9**, **10.5.10**), §5.1, §5.2, §6, §7
> Contract: `../README.md` — §3 **D2/D5/D6/D8/D9/D10/D11/D13/D14/D16/D17/D18**, C8–C11, C14, §6 template, §7 gate `10-W1-studio`, §8 index
> Template: `../../../00-overview.md` §10.1 · Legend: ✅ read from a file · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet (planning repo, no code).

## 1. Scope

**In scope.** The wave-1 Angular 22 screens of the episodes feature (phase **P**, milestone **M1**), built as one lazy feature `web/src/app/features/episodes/` (D2):

- `10.5.1` — `/episodes`: cursor-paginated list, status + series filters, status chips, continue buttons.
- `10.5.2` — `/series/:id/episodes/new`: brief form (≤ 1,000 chars), length tier, create + generate, the live generating state driven by `EpisodeUpdated`, then hand-off to the editor.
- `10.5.3` — `/episodes/:id`: the story editor (segments, scene chips, read-only `beat`, explicit **Save version** with `If-Match`), the single owner of the `StoryDraft` model (D18).
- `10.5.9` — structure: drag + keyboard reorder, delete with undo snackbar, scene chips travel with their paragraph, dirty guard (`canDeactivate` + `beforeunload`).
- `10.5.10` — inline annotations (quality hints from `qualityHints[]`, blocking vs advisory), the `409 keywords_flagged` mapping onto chips, and the `412 story_version_conflict` conflict flow.
- `10.5.3-T2` — self-review (`audit_report.model_review`) issues in the same inline component, the "Automatically corrected once" badge for a `source = revised` version, similarity `warn`/`block` highlights by segment address.

**Out of scope.** Version history and diff (10.5.4-T1), sources/fact sheet (10.5.5-T1), approve/reopen and attestation (10.5.6-T1), bible panel (10.5.7-T1), source finder (10.5.8-T1), render progress and video review (10.6.x), publishing (10.7.x), credits/billing/checkout (10.8.x — S), admin (10.9.x). No server-side code, no migration, no new API route: this folder consumes the frozen contracts of 09 and 02.

**Personal phase (D14/02 D1/F03).** No credit amount, quote, balance or buy button appears in P. The "Write story" action renders the plain label; the reserved cost-line slots (`ConfirmDialog` `cost-line`, shell `header-end`) stay empty and are filled by S tasks 10.8.1-T1/10.8.3-T1.

## 2. Confirmed facts

| id | Fact | Source |
|---|---|---|
| F1 | Routes `POST /api/v1/series/{seriesId}/episodes` (201 + `Location` + `ETag`), `GET /api/v1/episodes?seriesId=&status=&cursor=&limit=` (200 page), `GET /api/v1/episodes/{id}` (200 + `ETag`), `DELETE /api/v1/episodes/{id}`; `ListEpisodesQuery(SeriesId?, Status?, Cursor?, Limit = 20)`; `limit` 0 or 101 → 400 field `limit`, invalid cursor → 400 field `cursor`; `Cancelled` episodes are 404/hidden | ✅ `features/09-backend-api/09.5.1-episodes/09.5.1-T1-episode-commands-queries-pagination.md` §Design (routes table, List rules) |
| F2 | `CreateEpisodeCommand(Guid SeriesId, string? Title, string Brief, LengthTier LengthTier)`; validator: brief 1–1,000 chars after trim, title ≤ 120, control characters other than `\n`/`\t` rejected; **no** `Idempotency-Key` on create (no credits, no platform call) | ✅ same file §Design + Notes |
| F3 | `GET /episodes/{id}` returns `EpisodeDetailDto` = `id, seriesId, title, brief, lengthTier, status, nextActions[], etag ("ep-<xmin>"), renderJob?, finalVideo?, publications?` + `generation { kind, status, stage, errorCode, startedAt, processingPaused }` + `currentStory { versionId, version, source, etag, content, qualityHints[], auditReport, similarityScore, factSheetId, attestation }`, where `qualityHints` is the flattened objective-first `auditReport.local.issues ++ auditReport.model_review.issues` and `auditReport` is the verbatim `audit.v1` document; `If-None-Match` → 304 | ✅ `…/09.4.2-5.3-queries/09.5.3-T1-episode-detail.md:29-33,48-50,86-88`; `…/09.4.2-5.3-queries/plan.md:161-166,194` |
| F4 | `EpisodeNextAction` = `GenerateStory, EditStory, ApproveStory, ReopenStory, Render, RetryRender, Cancel, ApproveVideo, RejectVideo, Publish, Download`, serialised as snake_case strings; the DTO is a hint, never authorization (member list is [INFERENCE] per that file) | ✅ `09.5.3-T1-episode-detail.md:49-50,140`; ⚠️ `:57,71,179` |
| F5 | `EpisodeListItemDto(Id, SeriesId, Title, Status, LengthTier, CreatedAt, UpdatedAt, Etag)` — **no** `nextActions` on a list item | ✅ `09.5.1-T1-episode-commands-queries-pagination.md` §Design (List projection) |
| F6 | `PUT /api/v1/episodes/{id}/story`, body `{ content: <story.v1> }` + **required** `If-Match` (`"sv-<currentStoryVersionId>"`) → 200 `StoryVersionDto` + `ETag`; 400 `story_invalid_content` with `errors[] {field,rule,message}`, 409 `episode_invalid_state`, 412 `story_version_conflict`, 428 `if_match_required`; byte-identical content → 200, **unchanged** ETag, no new version; editing is allowed in `draft` or `story_review` only; each save recomputes derived fields, `audit_report.local` (checker), `audit_report.similarity` (free) and sets `model_review.stale = true` | ✅ `02.3-checkpoint-1/plan.md:104-107,113-116,144-148`; `02.3.1-T1-edit-story-command.md:9,41-45,63` |
| F7 | `POST /api/v1/episodes/{id}/story/generate` requires an `Idempotency-Key` header (≤ 128 chars; missing → 400 `idempotency_key_required`) → 202 + `Location` + `{ generationRunId, status, stage }`; 409 `episode_invalid_state` / `story_generation_in_progress` / `fact_sheet_required`; 422 `idempotency_key_reused`; 429 `story_generation_limit` / `spend_cap_reached`; generate does **not** need `If-Match` | ✅ `02.2.2-generate-story-job/02.2.2-T2-generate-story-command-endpoint.md:9` + §Design/§5.1 + acceptance |
| F8 | Story stages `queued\|writing\|checking\|reviewing\|revising\|scoring\|saving\|done`; `EpisodeUpdated` is emitted **on transition** (≤ 7 + terminal per run) and carries ids + the `generation` block only, never story text; copy keys `story.generation.stage.*`, `story.generation.error.*`, `story.generation.paused.provider_funds_exhausted`, `story.generation.moderation.*`, `story.generation.revised.badge` (= "Automatically corrected once" / "Đã tự sửa một lần"), `story.generation.action.retry`, `story.generation.action.generate` (= "Write story" / "Viết kịch bản") | ✅ `02.2.2-generate-story-job/plan.md:455-490` (§6 tables) |
| F9 | `QualityHintDto { ruleId, severity (advisory\|blocking), kind (objective\|taste), segment (D22 address), messageKey, evidence (≤ 200 chars of our draft), repairDirection? }`; 20 rule ids `Q-LEN-01…Q-TIER-01`; the message keys live at **`web/src/assets/i18n/quality/{vi,en}.json`** (scope `quality`, D10 as corrected by **WR15** / 10 CR-11), bound verbatim via `messageKey`; `Q-SIM-01` advisory → `quality.similarity_warn`, `Q-SIM-02` blocking → `quality.similarity_blocked` (sourced episodes only); the checker is advisory — hints **never** disable approve (WR6) | ✅ `02.3-checkpoint-1/plan.md` §5/§6; `02.3.4-T1-story-quality-checker.md:41-90,101-123`; `../README.md` §10 WR15 |
| F10 | Segment addressing is `title \| hook \| p:<0-based> \| ending \| meta` in every backend payload (`errors[].segment`, `QualityHintDto.segment`, `audit.v1.similarity.segments[].segment`, review issues) | ✅ `../README.md:62` (C9); `02.3.4-T1:41-90`; `02.5.6-T1-similarity-scorer-corpus-and-guard.md:27-35,49` |
| F11 | `409 keywords_flagged` carries `errors[] { segment, index, rule }`; raised by approve (02.3.2-T1) and render (09.6.1-T1) with rules such as `keyword_name_detected`, `keyword_invalid`, `no_scenes`; advisory hint codes `scenes_trimmed`, `scenes_sparse` (copy `quality.scenes_sparse`, `errors.keywords_flagged`) | ✅ `features/03-image-generation/03.2-keyword-quality/plan.md:80-82,89,100-101`; `features/03-image-generation/README.md:202,205` |
| F12 | `audit_report.model_review.issues[] { id, severity (blocking\|advisory), kind (objective\|taste), segment, evidence (≤ 200), impact, repair_direction }` merged from `review.v1`; the closed objective type list includes `fact_ref_invalid`, `missing_setup_before_twist`, `tone_violation`, `length_out_of_range`, `bible_contradiction`, `ear_rule_violation`, `keyword_rule_violation`, `hook_missing`, `ending_missing`, `beat_missing`, `markdown_or_url_in_text`, `content_warning_missing`, plus taste types `weak_hook`, `flat_ending`, `pacing_issue`, `repetitive_phrasing`; `kind` is derived server-side; a taste type is downgraded to advisory | ✅ `02.6.1-self-review/02.6.1-T1-…md:22-33,58,108` |
| F13 | A `source = revised` version and `audit_report.revision { applied, fixed_issue_ids, parent_version_id, review_after }` mark the single automatic correction; `revision_drift` is advisory only; the review surfaces reach the client through `currentStory.auditReport` (`model_review.scores`, `model_review.stale`, `model_review.issues[]`, `revision`) with the objective-first `qualityHints[]` flattening `local.issues ++ model_review.issues` (`02.3.4-T2:34,42`). 02.6.1-T1:67 describes a `currentStory.review { scores, issueCounts, hasDrift, stale }` slice; the payload owner 02.3.4-T2 does **not** define one, so this folder reads `auditReport` and derives the counts from `qualityHints` — the discrepancy is recorded and settled in favour of 02.3.4-T2 (see O9) | ✅ `02.6.2-T1-single-revision-pass.md:9,43,76-79`; `02.6.1-T1-…md:67` |
| F14 | `audit.v1.similarity` = `{ score, n, corpus, segments: [{ segment, share }] }`, segment shares emitted only when `> 0`, top 10, desc; `SimilarityVerdict` = `Pass \| Warn \| Block`, `null → Pass`; `corpus` ∈ `sources`/`past_episodes`; when the story is not scorable the score is `null` and the UI shows `story.similarity.unavailable` only when sources exist | ✅ `02.5.6-T1-…md:34-35,41-45,53,111` |
| F15 | `EpisodeUpdated` is raised by the commands through `IRealtimeNotifier` and the episode page refreshes on it. **The payload shape is inconsistent** — 02.3.1-T1:46 sends ids only, 09.7.1-T1:136 sends `(EpisodeId, Status, NextActions)`, 02.2.2 §5.2 adds a `generation` block — so clients treat it as an invalidation only (plan §10 O11); `RenderProgress` is owned by 10.6.x | ✅ `09.7-video-review-storage/09.7.1-T1-approve-reject-video.md:136`; `…/09.4.2-5.3-queries/plan.md:207` |
| F16 | Wave-1 FE conventions: Angular 22.2.x, standalone, zoneless, `strictTemplates`; signal stores; typed Reactive Forms; OnPush; `@for` with `track`; lazy routes; Vitest + jsdom + hand-written fakes; Playwright + `@axe-core/playwright`; one origin, relative URLs, dev proxy `127.0.0.1:4200` | ✅ `../README.md:111-260` (D1–D18) |
| F17 | `shared/util/cursor-page.ts` (`{ items, nextCursor }`, limit ≤ 100) and `core/state/cursor-list.ts` (signal "load more" with an in-flight guard) are the shared list primitives **owned by 10.2.6-T1** (WR4) and reused by every list; `core/http/withIfMatch(etag)`, `readEtag(response)`, `IdempotencyKeys` (10.2.3-T1) | ✅ `../README.md:135-140` (D5), §10 WR4 |
| F18 | The story `content` object is `story.v1`: `title`, `hook { text, keywords[], fact_refs[] }`, `paragraphs[] { beat, text, keywords[], fact_refs[] }`, `ending { … }`, `description`, `tags[]`, `content_warnings[]`, `structure { … }`; 1–2 keywords per segment; title ≤ 100, description ≤ 1,000, tags ≤ 15 × ≤ 30 chars (≤ 400 total); segment counts short 4–5 / standard 20–32 / long 40–60; `beat` is a read-only per-paragraph label | ✅ `../README.md:62` (C9); `02.3-checkpoint-1/plan.md:144-148` (PUT example) |
| F19 | `EpisodeStatus` = `draft, story_generating, story_review, story_approved, rendering, video_review, ready, scheduled, published, failed, cancelled` | ✅ `../../../10-web-ui.md:263-265` |
| F20 | `structure` has no field-level schema in any signed document I read | ⚠️ → [OPEN] O4 |

## 3. Domain and data

Client-side models, ports, stores and signals only — **no database** (this folder owns no persistence; the server owns the tables).

All paths are under `web/src/app/`. Nothing in `domain/` imports `@angular/*`, `rxjs` or anything outside `domain/` + `@shared/util` (D2).

### 3.1 Models — `features/episodes/domain/`

```ts
// episode.model.ts   (pure; mirrors the wire shapes, no extra fields on the wire — D18)
export type EpisodeStatus = 'draft' | 'story_generating' | 'story_review' | 'story_approved'
  | 'rendering' | 'video_review' | 'ready' | 'scheduled' | 'published' | 'failed' | 'cancelled';   // F19
export type EpisodeAction = 'generate_story' | 'edit_story' | 'approve_story' | 'reopen_story'
  | 'render' | 'retry_render' | 'cancel' | 'approve_video' | 'reject_video' | 'publish' | 'download'; // F4 (snake_case on the wire)
export type LengthTier = 'short' | 'standard' | 'long';
export type GenerationStage = 'queued' | 'writing' | 'checking' | 'reviewing' | 'revising' | 'scoring' | 'saving' | 'done'; // F8

export interface EpisodeListItem { id: string; seriesId: string; title: string | null; status: EpisodeStatus;
  lengthTier: LengthTier; createdAt: string; updatedAt: string; etag: string; }        // F5
export interface EpisodeGeneration { kind: string; status: string; stage: GenerationStage;
  errorCode: string | null; startedAt: string | null; processingPaused: string | null; } // F3
export interface EpisodeDetail extends EpisodeListItem { brief: string; nextActions: EpisodeAction[];
  generation: EpisodeGeneration | null; currentStory: StorySnapshot | null; }
export const canEditStory = (e: EpisodeDetail): boolean =>
  e.nextActions.includes('edit_story');           // hint only — the server re-checks (F4)
export const LIST_STATUSES: readonly EpisodeStatus[] = [...];   // the filter dropdown set
```

```ts
// story.model.ts
export type SegmentKind = 'title' | 'hook' | 'paragraph' | 'ending';
export type SegmentAddress = 'title' | 'hook' | `p:${number}` | 'ending' | 'meta';        // F10

export interface StorySegment { segKey: string;            // client-only, crypto.randomUUID() (D18)
  kind: SegmentKind; beat: string | null; text: string; keywords: string[]; factRefs: string[]; }
export interface StoryMetadata { title: string; description: string; tags: string[];
  contentWarnings: string[]; structure: unknown; }         // `structure` round-trips untouched — [OPEN] O4
export interface StoryDraft { title: StorySegment; hook: StorySegment; paragraphs: StorySegment[];
  ending: StorySegment; meta: StoryMetadata; }             // mirrors story.v1 exactly (F18, D18)

export interface QualityHint { ruleId: string; severity: 'advisory' | 'blocking';
  kind: 'objective' | 'taste'; segment: SegmentAddress; messageKey: string;
  evidence: string; repairDirection: string | null; }      // F9
export interface AuditIssue { id: string; severity: 'advisory' | 'blocking'; kind: 'objective' | 'taste';
  segment: SegmentAddress; evidence: string; impact: string; repairDirection: string | null; }   // F12
export interface SimilaritySection { score: number; n: number; corpus: 'sources' | 'past_episodes';
  segments: { segment: SegmentAddress; share: number }[]; }                                     // F14
export interface StorySnapshot { versionId: string; version: number;      // `currentStory` (C8, CR-7)
  source: 'generated' | 'edited' | 'revised'; etag: string;                  // "sv-<versionId>" (F6)
  content: StoryDraft; wordCount: number;
  qualityHints: QualityHint[];                  // flattened local.issues ++ model_review.issues (02.3.4-T2:42)
  auditReport: AuditReport; similarityScore: number | null; factSheetId: string | null; }
export interface AuditReport { schemaVersion: string;                        // audit.v1 verbatim (02.3.4-T2:34)
  local: { issues: QualityHint[] };
  modelReview: { model: string; promptVersion: string; stale: boolean;
                 scores: Record<string, number | null>; issues: AuditIssue[] } | null;
  revision: RevisionInfo | null; similarity: SimilaritySection | null; moderation: unknown; }
export interface ReviewCounts { blocking: number; advisory: number; }        // derived from qualityHints, not a DTO field
export interface SegmentAnnotation { address: SegmentAddress; severity: 'advisory' | 'blocking';
  kind: 'objective' | 'taste' | 'keyword'; messageKey: string; evidence: string | null;
  repairDirection: string | null; source: 'hint' | 'keyword' | 'similarity'; }   // NO 'review': model_review issues arrive as hints (02.3.4-T2:42)
export interface KeywordFlag { address: SegmentAddress; index: number; rule: string; }          // F11
export interface RevisionInfo { applied: boolean; fixedIssueIds: string[]; parentVersionId: string | null; }  // F13
// editor-internal view of a paragraph; `text`/`keywords` are the per-segment signals D18 requires
export interface ParagraphState { segKey: string; beat: string | null;
  text: WritableSignal<string>; keywords: WritableSignal<string[]>; factRefs: string[]; }
```

### 3.2 Ports (abstract classes) and their HTTP adapters

```ts
// domain/episodes.repository.ts
export abstract class EpisodesRepository {
  abstract list(q: { status?: EpisodeStatus; seriesId?: string; cursor?: string; limit?: number }):
    Promise<CursorPage<EpisodeListItem>>;                          // F1, F17
  abstract get(id: string): Promise<EpisodeDetail>;                 // F3
  abstract create(seriesId: string, body: { title: string | null; brief: string; lengthTier: LengthTier }):
    Promise<{ id: string; etag: string }>;                          // F2
  abstract generateStory(id: string, idempotencyKey: string): Promise<void>;   // F7
}
// domain/series-options.repository.ts
export abstract class SeriesOptionsRepository {
  abstract list(): Promise<readonly { id: string; title: string }[]>;      // GET /api/v1/series?limit=100
  abstract get(id: string): Promise<{ id: string; title: string; topic: string }>;  // GET /api/v1/series/{id}
}
// domain/story.repository.ts
export abstract class StoryRepository {
  abstract get(id: string): Promise<StorySnapshot>;                                  // currentStory (F3/F6)
  abstract save(id: string, content: StoryContentV1, etag: string): Promise<StorySnapshot>; // F6
}
```
Adapters: `data-access/episodes-http.repository.ts`, `data-access/story-http.repository.ts` — the **only** files that import `@shared/api` (D2/D5). Mapping lives in `data-access/episode.mapper.ts` / `story.mapper.ts` (pure functions, unit-tested against fixed DTO literals). `save` uses `withIfMatch(etag)` and `readEtag(response)`; `generateStory` uses `IdempotencyKeys.new()` (F17). Repositories return `Promise<T>`; stores never see `HttpResponse` (D5).

### 3.3 Stores (`application/`) — signals only, no NgRx (D13)

| Store | Scope | Signals / methods |
|---|---|---|
| `EpisodeListStore` | provided by the list page | `filters = signal<{status?: EpisodeStatus; seriesId?: string}>`, `page = cursorList(...)` from `core/state/cursor-list.ts` (`items`, `loading`, `loadingMore`, `hasMore`, `error`), `items = computed(...)`, `load(reset = true)`, `loadMore()`, `setStatus(s)`, `setSeries(id)` |
| `NewEpisodeStore` | provided by the new-episode page | `form` (typed Reactive Form: `title`, `brief`, `lengthTier`), `briefLength = toSignal(...)`, `submitting`, `generation = signal<EpisodeGeneration \| null>(null)`, `stage = computed(() => generation()?.stage ?? null)`, `error = signal<AppError \| null>(null)`, `create()` (topic-aware: fiction → generate, true crime → `/episodes/:id/research`), `subscribe(id)` via `RealtimeService.subscribeEpisode` (WR7, invalidation only) |
| `EpisodeDetailStore` | provided by the episode page | `episode = signal<EpisodeDetail \| null>(null)`, `loading`, `error`, `load(id)`, `refetch()` (called on an invalidation/`resync`; **background**, without `USER_ACTION` — WR11), `nextActions = computed(...)` |
| `StoryEditorStore` | provided by the episode page (single writer) | `draft = signal<StoryDraft \| null>(null)`, one `WritableSignal<string>`/`WritableSignal<string[]>` per segment text/keywords (D18), `segmentTokens = computed(...)`, `totalTokens = computed(...)`, `baselineJson`, `isDirty = computed(...)` (150 ms debounce, **indicator only**), `hasUnsavedChanges()` (**synchronous** `serialize(buildDraft()) !== baselineJson()` — used by Save and both guards, so a Ctrl+S inside the debounce window is never dropped), `hints`, `annotations = computed(...)` (merge of hints + review + keyword flags + similarity, sorted objective-first then by segment index), `saveVersion()`, `addKeyword/updateKeyword/removeKeyword(addr, …)`, `moveUp/moveDown/moveTo`, `removeSegment(addr)`, `undoRemove()`, `resolveConflict(choice)` |

`StoryEditorStore` is deliberately the **single** editor store: hints (10.5.10-T1), structure edits (10.5.9-T1) and self-review/similarity (10.5.3-T2) all add members to it instead of introducing a second writer — two stores over one draft is the failure mode D13/D18 forbid.

### 3.4 Shared helpers produced here

- `shared/util/segment-address.ts` — **the** single implementation: `parse(address) → {kind, index}`, `format({kind, index})`, `compare(a, b)`, `remap(afterReorder: SegmentAddress[], address)`; used by the editor, annotations, the conflict flow, the keyword flags and the `?segment=` deep link (D18). **Produced by 10.5.3-T1.**
- `shared/ui/segment-chips/` (`SegmentChips`) — **produced by 10.5.3-T1** (D11 inventory).
- `shared/ui/conflict-dialog/` (`ConflictDialog`) — **owned by 10.2.2-T1 (WR4)**, consumed here and by 10.5.5-T1's fact-sheet conflict; the 412 *flow* (kept draft, three actions, repeat-412 safety) is owned by 10.5.10-T1. This folder neither produces nor forks it.
- `shared/ui/inline-annotation/` (`InlineAnnotation`) — **produced by 10.5.10-T1**.
- Reused, not re-implemented (owners per WR4): `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `StatusChip`, `CharCounter`, `ConfirmDialog`, `Stepper` and the inline-SVG icon components → 10.1.3-T1; `ConflictDialog`, `RetryCountdown` → 10.2.2-T1; `DiffView` → 10.5.4-T1 (`@defer` only).

## 4. Use cases

| # | Use case | API call | States | Errors |
|---|---|---|---|---|
| U1 | Browse episodes | `GET /episodes?status=&seriesId=&cursor=&limit=` (F1) | loading (skeleton) → empty (`EmptyState` + "create episode") → list; "load more" with an in-flight guard; filter change resets the cursor | 400 field `cursor`/`limit` → `errors.validation_failed` on the filter controls; 401 → session flow (D6); network → `ErrorState` + retry |
| U2 | Open an episode | `GET /episodes/{id}` (F3) with `If-None-Match` (the HTTP cache honours the 304) | loading → detail; live refresh on `EpisodeUpdated` (F15) | 404 → `errors.not_found` + back to the list; 403 never (tenant filter returns 404) |
| U3 | Create an episode (topic-aware first step) | `GET /api/v1/series/{id}` (once) then `POST /series/{seriesId}/episodes` (F2) | submitting (button busy) → **fiction**: continue to U4; **true crime**: no generate call, navigate to `/episodes/:id/research` (the fact sheet and ≥ 2 cited sources must exist first, 02.2.2-T2 step 4) | 400 `errors.validation_failed` bound to `brief`/`title`; 404 `errors.not_found` (unknown/deleted series); 409 `series_archived`; 413 `payload_too_large` |
| U4 | Generate the story | `POST /episodes/{id}/story/generate` with a fresh `Idempotency-Key` (F7) | `story_generating` with the live stage line; retry keeps the **same** key for the same attempt (D9-adjacent) | 409 `episode_invalid_state` / `story_generation_in_progress` / `fact_sheet_required`; 422 `idempotency_key_reused`; 429 `story_generation_limit` / `spend_cap_reached` → `RetryCountdown`; `story_moderated` / `story_invalid_output` / `story_generation_failed` surface as the run's `errorCode` with `errors.<code>` |
| U5 | Edit segments and keywords | none (local) | one signal per segment; token counters; `isDirty` | — |
| U6 | Save a version | `PUT /episodes/{id}/story` + `If-Match` (F6) | saving → saved (aria-live "đã lưu") → baseline/etag/`qualityHints` replaced by the response | 400 `story_invalid_content` → `bindFieldErrors` onto segment controls (unmapped keys → form-level alert); 409 `episode_invalid_state` → refetch + `errors.episode_invalid_state` with `nextActions`; 412 `story_version_conflict` → `ConflictDialog`; 428 → dev console + generic message; 429 → countdown; unchanged body → same ETag, no new version (informational toast) |
| U7 | Reorder a paragraph | none (local) | buttons (keyboard) + CDK drop (pointer); scene chips and `beat` travel with the paragraph; live announcement | — |
| U8 | Delete a paragraph | none (local) | undo snackbar 10 s; the paragraph is out of the draft; durable only on save (D18) | — |
| U9 | Leave with unsaved work | — | `canDeactivate` confirm dialog (stay/leave) + `beforeunload`; logout discards drafts only after the guard asked (D6) | — |
| U10 | See and act on hints/annotations | none beyond U2/U6 (the payload carries them) | inline annotation per segment, advisory vs blocking by icon + text; group by kind (objective first) | — |
| U11 | Fix flagged keywords | `PUT` (U6) | red chips + "Make video" disabled while a `keywords_flagged` rule is unresolved; each chip toggles back to normal after a recompute | 409 `keywords_flagged` `errors[] {segment,index,rule}` (F11) mapped to chips |
| U12 | Resolve a 412 conflict | `GET /episodes/{id}` (reload) or `PUT` with the **fresh** ETag ("save mine as new version") (F6) | dialog with three actions while the draft is kept in memory | 412 again → the dialog stays open with the same draft |
| U13 | See the self-review and the revision badge | none (payload) | issues inline; `source = revised` → badge + diff link (`@defer`, 10.5.4-T1); similarity `Warn`/`Block` highlights by address | — |

## 5. API and events

Routes consumed (relative URLs, one origin — D3):

| Method | Route | Request | Response | Notes |
|---|---|---|---|---|
| GET | `/api/v1/episodes` | `status`, `seriesId`, `cursor`, `limit` | `{ items: EpisodeListItem[], nextCursor }` | F1/F5 · `limit` ≤ 100 (D5) |
| POST | `/api/v1/series/{seriesId}/episodes` | `{ title?, brief, lengthTier }` | 201 + `Location` + `ETag` | F2 · no idempotency key |
| GET | `/api/v1/episodes/{id}` | `If-None-Match` optional | `EpisodeDetailDto` + `ETag` (`"ep-<xmin>"`) | F3 · 304 honoured |
| POST | `/api/v1/episodes/{id}/story/generate` | `Idempotency-Key` header | 202 `{ generationRunId, status, stage }` | F7 |
| GET | `/api/v1/episodes/{id}` (story slice) | — | `currentStory` + `generation` | F3/F6 · one call per page load, no extra round trip |
| PUT | `/api/v1/episodes/{id}/story` | `{ content: story.v1 }` + `If-Match` | `StoryVersionDto` + `ETag` | F6 |

No new route, no new event, no new error code is introduced by this folder.

Events (D8):

| Event | Handling here |
|---|---|
| `EpisodeUpdated` (F15, **payload shape varies — O11**) | treated as an **invalidation only**: the store subscribes via `RealtimeService.subscribeEpisode(episodeId, handler)` (**WR7**), coalesces within 250 ms and refetches `GET /episodes/{id}` once (background, no `USER_ACTION` — **WR11**).
| `resync` (every reconnect + `visibilitychange → visible`) | only stores holding a live subscription refetch, coalesced per store within 250 ms (D8) |
| `RenderProgress` | not consumed here (10.6.x); an unknown `stage` value is ignored, never rendered raw |
| unknown event names / unknown `stage` | ignored (D8) |

Errors: every code is mapped by the shared `AppErrorMapper` (10.2.2-T1) and rendered through `errors.<code>`; `detail` is never shown raw (D9). Codes this folder can receive: `validation_failed`, `not_found`, `series_archived`, `episode_invalid_state`, `story_generation_in_progress`, `fact_sheet_required`, `idempotency_key_reused`, `story_generation_limit`, `spend_cap_reached`, `story_invalid_content`, `story_version_conflict`, `if_match_required`, `keywords_flagged`, `payload_too_large`, `rate_limited`, `network_error`, plus the run `errorCode`s of F8 (`story_moderated`, `story_invalid_output`, `story_generation_failed`, `provider_funds_exhausted`).

## 6. UI

Routes (`features/episodes/episodes.routes.ts`, all lazy, D13):

| Route | Page | Guards / providers |
|---|---|---|
| `/episodes` | `feature/episodes-list.page.ts` | `authGuard`; provides `EpisodeListStore`; `EpisodesRepository` from the route `providers` |
| `/series/:id/episodes/new` | `feature/new-episode.page.ts` | `authGuard`; provides `NewEpisodeStore` |
| `/episodes/:id` (query `?segment=p:3`) | `feature/story-review.page.ts` | `authGuard` + `canDeactivate` dirty guard; provides `EpisodeDetailStore`, `StoryEditorStore`; `?segment=` is parsed and applied by `segment-address.parse` (D18) |

Screens and copy keys (scope `episodes`, files `web/src/assets/i18n/episodes/{vi,en}.json`, key style `scope.area.item` — D10). Keys handed over by other plans are used **verbatim**: `quality.*` (F9), `story.generation.*` (F8), `errors.<code>`, `voices.tag.<tag>`, `series.style.*`.

| Screen | Keys owned here (vi / en) |
|---|---|
| List | `episodes.list.title` (Tập phim / Episodes), `episodes.list.filterStatus` (Trạng thái / Status), `episodes.list.filterSeries` (Loạt bài / Series), `episodes.list.all` (Tất cả / All), `episodes.list.empty` (Chưa có tập phim nào / No episodes yet), `episodes.list.loadMore` (Tải thêm / Load more), `episodes.list.continue` (Tiếp tục / Continue), `episodes.status.<status>` × 11 (Bản nháp / Draft, Đang viết / Generating, Chờ bạn duyệt / Needs your review, Đã duyệt kịch bản / Story approved, Đang dựng video / Rendering, Chờ duyệt video / Video review, Sẵn sàng đăng / Ready, Đã lên lịch / Scheduled, Đã đăng / Published, Lỗi / Failed, Đã huỷ / Cancelled) |
| New + generate | `episodes.new.title` (Tập phim mới / New episode), `episodes.new.briefLabel` (Ý tưởng / Your idea), `episodes.new.briefHint` (Tối đa 1.000 ký tự / Up to 1,000 characters), `episodes.new.titleOptional` (Tiêu đề (không bắt buộc) / Title (optional)), `episodes.new.lengthTier` (Độ dài / Length), `episodes.new.tier.short\|standard\|long` (Ngắn\|Tiêu chuẩn\|Dài / Short\|Standard\|Long), `episodes.new.submit` (= `story.generation.action.generate`), `episodes.generation.heading` (Đang tạo kịch bản / Generating your story), `episodes.generation.stage.queued` (Đang xếp hàng / Queued), `episodes.generation.stage.done` (Xong / Done) |
| Editor | `episodes.editor.saveVersion` (Lưu phiên bản / Save version), `episodes.editor.saving` (Đang lưu… / Saving…), `episodes.editor.saved` (Đã lưu / Saved), `episodes.editor.unsaved` (Có thay đổi chưa lưu / Unsaved changes), `episodes.editor.segment.title\|hook\|paragraph\|ending` (Tiêu đề / Mở đầu / Đoạn / Kết), `episodes.editor.beat` (Nhịp / Beat), `episodes.editor.keywords` (Cảnh / Scenes), `episodes.editor.addKeyword` (Thêm cảnh / Add scene), `episodes.editor.removeKeyword` (Xoá cảnh / Remove scene), `episodes.editor.tokens` ({{count}} từ / {{count}} words), `episodes.editor.description` (Mô tả / Description), `episodes.editor.tags` (Thẻ / Tags), `episodes.editor.moveUp` (Lên / Move up), `episodes.editor.moveDown` (Xuống / Move down), `episodes.editor.delete` (Xoá đoạn / Delete paragraph), `episodes.editor.undo` (Hoàn tác / Undo), `episodes.editor.deleted` (Đã xoá một đoạn / Paragraph deleted), `episodes.editor.reorderAnnounce` (Đã chuyển đoạn {{n}} / Moved paragraph {{n}}) |
| Conflict / guard | `episodes.conflict.title` (Kịch bản đã thay đổi / The story changed), `episodes.conflict.body` (…ở tab khác hoặc người khác vừa lưu / …in another tab or someone else saved), `episodes.conflict.reload` (Tải bản mới nhất / Reload latest), `episodes.conflict.saveMine` (Lưu bản của tôi thành phiên bản mới / Save mine as new version), `episodes.conflict.cancel` (Huỷ / Cancel), `episodes.dirty.title` (Bạn có thay đổi chưa lưu / You have unsaved changes), `episodes.dirty.body` (Rời trang sẽ mất các thay đổi này / Leaving will discard them), `episodes.dirty.stay` (Ở lại / Stay), `episodes.dirty.leave` (Rời trang / Leave) |
| Annotations | `episodes.annotations.heading` (Gợi ý cải thiện / Suggestions), `episodes.annotations.objective` (Khách quan / Objective), `episodes.annotations.taste` (Cảm nhận / Taste), `episodes.annotations.evidence` (Bằng chứng / Evidence), `episodes.annotations.repair` (Cách sửa / How to fix), `episodes.annotations.none` (Không có gợi ý / No suggestions), `episodes.editor.keywordFlagged` (Cảnh cần sửa / Scene needs a change) |
| Self-review | `episodes.audit.heading` (Tự đánh giá / Self-review), `episodes.audit.blocking` (Cần sửa / Must fix), `episodes.audit.advisory` (Nên xem / Worth a look), `episodes.audit.revisedBadge` → uses **`story.generation.revised.badge`** verbatim, `episodes.audit.diffLink` (Xem thay đổi / See what changed), `episodes.similarity.heading` (Giống nguồn / Similar to a source) → `quality.similarity_warn` / `quality.similarity_blocked` verbatim |

The per-task files enumerate the **complete** key sets they own (the table above is the screen-level index): 10.5.1-T1 adds `episodes.list.untitled`, `episodes.list.seriesFilter`, `episodes.list.filterActive`, `episodes.list.seriesFilterUnavailable`; 10.5.2-T1 adds `episodes.generation.*` and `episodes.new.trueCrimeNext` (Tạo tập phim và thêm nguồn / Create episode and add sources) + `episodes.new.trueCrimeNotice` (Tập tội phạm thật cần nguồn và bảng dữ kiện trước khi viết / A true-crime episode needs sources and an approved fact sheet before it is written); 10.5.9-T1 adds `episodes.editor.moveHandle`, `episodes.editor.structureWarning` and the `episodes.dirty.*` set; 10.5.10-T1 adds `episodes.annotations.*` and `episodes.conflict.*`; 10.5.3-T2 adds `episodes.audit.*` and `episodes.similarity.*`. The `vi` and `en` bundles must always carry identical key sets (checked by 10.2.5-T1), and every `messageKey` a server payload can carry (`quality.*`) exists before the UI binds it.

Accessibility (D16, screen-specific): every textarea has a `<label for>` and an `aria-describedby` chain to its counter + annotations; the annotation region is `role="note"` with `aria-live="polite"` when the set changes after a save; "saved" feedback is an `aria-live="polite"` region; the generation stage line and the failure message are `aria-live="polite"` / `role="alert"` respectively, and focus is never stolen (F8); reorder buttons are real `<button>`s with accessible names ("Lên: đoạn 3"), ≥ 24 px, and the CDK handle has a keyboard alternative (10.5.9-T1); flagged chips carry an icon **and** text (`episodes.editor.keywordFlagged`), never colour alone; dialogs trap focus and restore it on close; the layout is usable at 360 px without horizontal scroll; `prefers-reduced-motion` disables the snackbar/annotation transitions.

**Design (owner rule 2026-09-25).** `10.5.1-T0` is the folder's design gate (D19): wireframes + hi-fi mockups for `/episodes`, `/series/:id/episodes/new`, the `/episodes/:id` story editor (segments, scene chips, flagged chips, annotations, conflict dialog, dirty guard), at 360/768/1280, dark + light, vi + en, with focus order/keyboard path/`aria-live` annotations. Design link + owner approval record: ⚠️ [OPEN] (`10.5.1-T0-ui-design.md` §Outputs). Every task below lists `10.5.1-T0` as a hard `Depends on`.

## 7. Jobs and integrations

**Refetch while the draft is dirty.** The store's own `Save` also emits `EpisodeUpdated`, so the invalidation path must not clobber typing (D18): on a refetch the draft is **never** overwritten; if `currentStory.etag` equals the baseline etag the refetch is a no-op; if it differs, the editor shows a non-modal "changed elsewhere" notice (reuse the conflict actions: reload / save mine as a new version / cancel) **immediately**, instead of waiting for the next Save to return 412.


- **Realtime only** (D8/WR7): `RealtimeService.subscribeEpisode(episodeId, handler)` (canonical 10.2.4-T1 API; `on(name, ids?)` remains for other event names). The handler ignores its payload and only schedules the 250 ms-coalesced refetch. No polling — D17 restricts wave-1 polling to the autopilot run log, so this folder uses **no** `visible-poll` and no `setInterval`; if events are lost the `resync` on `visibilitychange → visible` refetches.
- **`USER_ACTION` (WR11).** Only store methods called from a UI event set the `HttpContext` flag: create/generate (10.5.2-T1), save/reload-latest (10.5.3-T1), filter change / load-more / retry (10.5.1-T1). Every event- or `resync`-driven refetch runs **without** the flag, so a 401 there refreshes only while `isActive()` (D7) and otherwise pauses rather than waking a rotating cookie in a forgotten tab (WR9/WR10 live in 10.2.1-T1).
- Timers introduced here: only the 150 ms dirty-debounce and the 10 s undo snackbar; both are cleared through `DestroyRef` (D13).
- The hub ticket, reconnect and backoff policy are entirely owned by `RealtimeService` (10.2.4-T1) — this folder never touches the connection.
- No job, no schedule, no outbox: all writes are explicit user commands.

## 8. Security and privacy

- **Tokens.** The access token lives only in `SessionStore` memory; this folder never reads or stores it, never writes story content to `localStorage`/`sessionStorage`, and persists no draft (D6/D18, NFR-10-5).
- **XSS.** Story text is rendered through interpolation only — no `[innerHTML]`, no Markdown, no `bypassSecurityTrust*` (D2/D4/D18). `InlineAnnotation` evidence strings (≤ 200 chars of our own draft, F9/F12) are interpolation as well. The lint rule (10.1.2-T1) fails the build otherwise.
- **CSP / Trusted Types.** No inline event handlers, no `javascript:` URLs, no dynamically built script; the D4 policy (`require-trusted-types-for 'script'`) stays satisfied, and every Playwright spec fails on a `securitypolicyviolation` event.
- **Lost updates.** Every story write is `If-Match`-guarded (F6); a 412 is never resolved silently — the dialog asks, and "save mine as new version" re-reads the current ETag before writing, so a colleague's version stays in history (immutable versions, F6).
- **Double-submit.** Generate carries a fresh `Idempotency-Key` per attempt; the create call needs none (F2) and is protected by the disabled submit button + a single in-flight promise.
- **No content in logs or telemetry.** Story text, briefs, titles, evidence and similarities are never logged; only ids, statuses and error codes (D19/F8). The dev-only 428 console line prints the code and `traceId`, never the payload.
- **Background vs user requests (WR11).** The `USER_ACTION` flag is the only signal the interceptor uses to decide whether a 401 may refresh: this folder sets it exclusively on calls the user just triggered, never on the invalidation refetches — so an unattended editor tab cannot keep a session alive or rotate a cookie it does not have a user for (D7).
- **Open redirect.** `returnUrl` handling belongs to the auth layer (D6)
- **Tenant isolation** is the API's job (F3: another user's episode is 404); the UI keeps the same behaviour by rendering `errors.not_found` instead of probing.
- **`409 keywords_flagged`** disables "Make video" locally, but the server re-checks; the UI never treats its own state as authorization (F4).

## 9. Test plan

Vitest through `@angular/build:unit-test` + jsdom, TestBed zoneless; **hand-written fakes**, no mocking library (D15).

- **Pure/domain:** `segment-address.spec.ts` (parse/format/compare/remap for `title|hook|p:<i>|ending|meta`, remap after a reorder sequence), token counting (NFC + Unicode whitespace split, D4 rule), `episode.mapper.spec.ts` / `story.mapper.spec.ts` against fixed DTO literals (snake_case actions, `"ep-"`/`"sv-"` ETag pass-through, `structure` round-trip), `annotations.spec.ts` (merge order: objective before taste, blocking before advisory, then segment order).
- **Store tests** (`application/*.store.spec.ts`) — the mandatory set from D15: success; `AppError` mapping (409/412/422/428/429 × the codes of §5); **ETag conflict** (`saveVersion` with 412 keeps `draft` byte-identical and opens the conflict state); **rollback of optimistic state** (reorder then failed save → the draft stays as the user made it, and the baseline is unchanged); **realtime merge** (one `EpisodeUpdated` burst → exactly one refetch within 250 ms); list paging (double `loadMore()` → one request; filter change resets the cursor); delete + undo; generation state machine per stage including an unknown stage being ignored.
- **Component tests** (logic-heavy UI only, D15): `story-editor.component` (segment inputs/outputs, keyboard reorder path), `scene-chips.component` (add/edit/remove, flagged state, a11y labels), `conflict-dialog.component` (three actions, focus trap contract), `inline-annotation.component` (severity icon + text, not colour alone), `generation-progress.component` (stage copy lookup, unknown stage ignored).
- **E2E (Playwright, `web/e2e/episodes-*.spec.ts`)**, against the compose stack and executed by **11.5.1-T1's `e2e.yml`** (WR3), `@axe-core/playwright` on every visited page plus the D4 CSP fixture (D15), one login per file (`describe.serial`), vi **and** en runs:
  1. list → filter by status → "load more" → open an episode (10.5.1-T1);
  2. `/series/:id/episodes/new` → create → generate → live stages → editor appears (10.5.2-T1);
  3. editor → edit title/hook/paragraph/keywords → "Save version" → reload shows the new version + fresh ETag (10.5.3-T1);
  4. keyboard-only reorder + delete + undo + dirty-guard dialog + `beforeunload` (10.5.9-T1);
  5. two tabs: save in tab A, save in tab B → 412 dialog → both actions exercised (10.5.10-T1);
  6. an episode generated with a blocking objective issue and a `revised` source → badge, inline issues, similarity highlight (10.5.3-T2).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on |
|---|---|---|---|
| 10.5.1-T1 | Episode list (cursor paging, filters) | S (0.5) | 10.2.6-T1, 09.5.1-T1 |
| 10.5.1-T0 | UI design (episode list, new + generate, story editor, conflict, dirty guard) | M (1.0) | 10.1.3-T1, 09.5.3-T1 (soft) |
| 10.5.2-T1 | New episode + generate + live generating state | S (0.5) | 10.5.1-T1, 10.2.3-T1, 10.2.4-T1, 02.2.2-T2, 02.3.4-T2, 09.5.3-T1 |
| 10.5.3-T1 | Story editor: segments, scene chips, Save version with If-Match | L (2.0) | 10.5.2-T1, 02.3.1-T1, 02.3.4-T2 |
| 10.5.9-T1 | Paragraph reorder (drag + buttons), delete with undo, dirty guard | L (2.0) | 10.5.3-T1 |
| 10.5.10-T1 | Inline annotations + quality hints, 412 conflict dialog | L (2.0) | 10.5.3-T1, 02.3.4-T1 |
| 10.5.3-T2 | Self-review issues, "corrected once" badge, similarity highlights | XS (0.25) | 10.5.10-T1, 02.6.1-T1, 02.6.2-T1 (soft), 02.5.6-T1 |

Total **8.25 d raw** (1.0 `10.5.1-T0` design, D19, + S 1.25, L 6.0, XS 0.25). `Depends on` cells above equal `../README.md` §8.1 verbatim; per **WR5** the canonical table now also records that **10.5.3-T1 blocks 10.4.3-T2** (the autopilot run-log deep link consumes `shared/util/segment-address.ts`) and, per **10 CR-34**, **10.5.3-T1 blocks 10.8.5-T1** (the episode History tab mount point), both of which appear in 10.5.3-T1's own header row. This note is the §10 sync point — no cell here changes.

Open items (`[OPEN]`, recorded, not resolved here — no file outside this folder is edited):

- **O1** *(resolved 2026-09-24 by WR15 / 10 CR-11)* The hint bundles live at **`web/src/assets/i18n/quality/{vi,en}.json`** (scope `quality`, D10) — the earlier `apps/web/...` path in 02.3.4-T1 is corrected by that CR, so this folder binds `qualityHints[].messageKey` verbatim against the D10 path with no mirror and no local copy. The 02 §6 wording difference (a "changed in another tab" banner vs the D9 `ConflictDialog`) remains recorded in 10.5.3-T1's Notes; the component's owner is 10.2.2-T1 (WR4).
- **O2** The high-level 10.5.2 mentions `GET /billing/quote` (`maxCredits`, `typicalCredits`, 12.1.3) in the same sentence as "no credit texts in P" (D14, 02 F03). P renders the plain action key and keeps the reserved cost-line slot empty; the quote call arrives with S (10.8.x). No P task depends on it.
- **O3** Stage keys `queued` and `done` are **not** in 02's copy-key table (F8 lists `writing…saving` only). 10.5.2-T1 adds `episodes.generation.stage.queued` / `.done` in the `episodes` scope instead of inventing entries inside the `story.generation.*` namespace owned by 02. Confirm the namespace split with the TL.
- **O4** `structure` inside `story.v1` has no documented field schema in any plan read (F20). The editor treats it as an opaque value it round-trips untouched and never renders; a schema would be needed before any structure editing.
- **O5** `EpisodeListItemDto` carries no `nextActions` (F5), so the list's "Continue" affordance derives its target client-side from `status` (`nextActionFor(status)`, [INFERENCE] — mirrors `EpisodeNextActions.For`). The detail page uses the server's `nextActions` as the single authority.
- **O6** D8's event contract says `EpisodeUpdated` "triggers a detail refetch (09.8.1 `plan.md:176`)"; this folder relies on that and adds no polling. If 09.8.1 ever omits `nextActions` from the payload, U2 falls back to a refetch — already the implemented behaviour.
- **O7** The series filter's option list comes from `GET /api/v1/series?cursor=&limit=&includeArchived=` → `200 { items, nextCursor }` (`09.4.1-crud/plan.md:62,73`; `ListSeriesQuery(string? Cursor, int Limit, bool IncludeArchived = false)`), consumed through a small `SeriesOptionsRepository` inside this feature — so no dependency on `@features/series` and no change to the canonical cells. Neither `SeriesListItemDto` (`09.4.1-T1:26`, list) nor `SeriesDto` (`09.4.1-crud/plan.md:74`, single) has a field list pinned in the plans read: the mappers read `id` + `title` and, for the single read, `topic` (proved a series property by `CreateSeriesCommand(string Title, Topic Topic, …)`), and the exact field spellings must be verified against the generated `@shared/api` client at implementation. A one-line CR to 09.4.1-T1 to pin both records is requested.
- **O8** D9 describes the `validation_failed`/`story_invalid_content` `errors` keys as "camelCase paths, D22 segment addresses for story", while 02.3.1-T1 returns `errors[] { field, rule, message }`. The editor's `bindFieldErrors` accepts `p:3` and `paragraphs[3].keywords` and falls back to a form-level alert; the exact wire format should be pinned by a CR before implementation.
- **O9** *(resolved 2026-09-24)* The review payload is confirmed: `currentStory.auditReport` is the verbatim `audit.v1` document and carries `model_review.issues[]`, `revision` and `similarity.segments[]` (`02.3.4-T2:13,34,42,134`), while `qualityHints` is the flattened `local.issues ++ model_review.issues`. Consequences: the client reads scores/revision/similarity from `auditReport`, renders `auditReport` in the expanded "why" panel only, and **never** merges `model_review.issues` into the inline annotations a second time (they already arrive as `qualityHints`). There is **no** `currentStory.review` field in CR-7 — the earlier citation of one is withdrawn.
- **O10** The "Automatically corrected once" badge's **diff link** belongs to 10.5.4-T1, which owns the versions route/panel; 10.5.3-T2 ships the badge as text and must not link to a route it does not depend on. W10Research (2026-09-24) fixed the target: `?panel=versions&left=<predecessorVersionId>&right=<revisedVersionId>`, owned by 10.5.4-T1 — 10.5.3-T2 points the link at it when that task lands.
- **O12** 02.3.4 contradicts itself on approve gating: `02.3.4-T1:85` says "the UI disables the approve button while a blocking hint is visible", while `02.3.4/plan.md:177` and `../README.md:64` (C11) say quality hints never disable approve. This folder follows the plan/C11 reading (only server-enforced state disables: a `keywords_flagged` 409, `story_moderated`, the true-crime guards) and asks the TL for a CR to settle the 02.3.4 wording.
- **O11** `EpisodeUpdated` does **not** have one payload shape: 02.3.1-T1:46 publishes `new EpisodeUpdated(episodeId)` (ids only), 09.7.1-T1:136 publishes `(EpisodeId, Status, NextActions)` and 02.2.2 `plan.md` §5.2 adds a `generation` block. The client therefore treats every event as an **invalidation only**: coalesce (250 ms) and refetch `GET /episodes/{id}`. No screen reads `stage`, `status` or `errorCode` from an event payload (10.5.2-T1 keeps its live generation state from the detail refetch).

## 11. Rollout

- **Order.** `10.5.1-T1 → 10.5.2-T1 → 10.5.3-T1 → {10.5.9-T1, 10.5.10-T1} → 10.5.3-T2`; the sibling folder `10.5.4-versions-research-bible/` consumes 10.5.3-T1.
- **Gates entered.** `10-W1-foundation` must be green before the first task: `web.yml` (lint, i18n parity, `api:gen` drift, unit, build, gzip budget, CSP generation) plus, per **WR3**, the Playwright suite (`web/e2e`, `npm run e2e`, axe + the D4 CSP fixture) in **11.5.1-T1's `e2e.yml`** — `web.yml` carries no compose/e2e stages.
- **Backend readiness.** Each task needs its backend task merged before the e2e acceptance runs: `GET /api/v1/series/{id}` (09.4.1-T1, already merged for 10.4.x) is a hard input for 10.5.2-T1's true-crime branch; 09.5.1-T1 (list/detail base), 09.5.3-T1 (`nextActions`, `currentStory` slice), 02.2.2-T2 (generate), 02.3.4-T1/T2 (hints + `currentStory`), 02.3.1-T1 (PUT story), 02.6.1-T1/02.6.2-T1 (review + revision), 02.5.6-T1 (similarity). Component/store tests may run against the contract-true fakes before that, but the acceptance e2e runs against the real API.
- **Flags.** None: the whole folder is P, `M1`, no feature flag. `source_finder`/`youtube_publish` (D14) do not gate any screen here.
- **Rollback.** Frontend-only, no migration: reverting the folder's commits removes the three routes; the API keeps working unchanged.
