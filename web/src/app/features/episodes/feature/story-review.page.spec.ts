import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed, type ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { TRANSLOCO_LOADER, TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import type { TranslocoLoader, Translation } from '@jsverse/transloco';
import { MatSnackBar, type MatSnackBarDismiss } from '@angular/material/snack-bar';
import { BehaviorSubject, firstValueFrom, of, Subject, type Observable } from 'rxjs';
import { afterEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { RealtimeService } from '@core/realtime';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '@core/session/testing/fake-session.api';
import { ConfirmDialog, ConflictDialog } from '@shared/ui';
import type { ConflictDialogData, ConflictDialogOutcome } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import qualityVi from '../../../../assets/i18n/quality/vi.json';
import coreEn from '../../../../assets/i18n/core/en.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import qualityEn from '../../../../assets/i18n/quality/en.json';
import storyVi from '../../../../assets/i18n/story/vi.json';
import storyEn from '../../../../assets/i18n/story/en.json';
import { countBySeverity, highlightMap, whyIssues } from '../domain/audit.model';
import { ReviewPanelComponent } from '../ui/review-panel.component';
import { ResearchStore } from '../application/research.store';
import type { ResearchSource } from '../domain/research.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { RenderRepository } from '../domain/render.repository';
import { VideoReviewRepository } from '../domain/video-review.repository';
import { FakeVideoReviewRepository } from '../testing/fake-video-review.repository';
import { MakeVideoStore } from '../application/make-video.store';
import { VideoReviewStore } from '../application/video-review.store';
import { VideoDecisionDialogComponent } from '../ui/video-decisions/video-decision-dialog.component';
import { FakeRenderRepository } from '../testing/fake-render.repository';
import { FakeEpisodesRepository, EPISODE_ID, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { mergeAnnotations } from '../application/annotation-merge';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { RenderTimelineStore } from '../application/render-timeline.store';
import { StoryEditorStore } from '../application/story-editor.store';
import { ApprovalStore } from '../application/approval.store';
import { VersionsStore } from '../application/versions.store';
import { fakeVersionsStore } from '../testing/fake-versions.store';
import { fakeResearchStore, fakeResearchStoreProviders } from '../testing/fake-research.store';
import { StoryRepository } from '../domain/story.repository';
import { FakeStoryRepository, attestation, auditReport, reviewIssue, similaritySection } from '../testing/fake-story.repository';
import { ApproveDialogComponent } from '../ui/approve-dialog/approve-dialog.component';
import { ReopenDialogComponent } from '../ui/reopen-dialog/reopen-dialog.component';
import type { SegmentAnnotation } from '../domain/annotation.model';
import type { EpisodeDetail, FinalVideoAsset } from '../domain/episode.model';
import type { RenderJob } from '../domain/render.model';
import type { StorySnapshot } from '../domain/story.model';
import { StoryReviewPage } from './story-review.page';
import { SegmentEditorComponent } from '../ui/segment-editor.component';
import { StoryEditorComponent } from '../ui/story-editor.component';
import { VersionsPanelHostComponent } from './versions-panel-host.component';
import { VersionsPanelComponent } from '../ui/versions-panel/versions-panel.component';

@Component({ selector: 'nts-versions-panel-host', imports: [VersionsPanelComponent], template: '<nts-versions-panel [needsReopen]="true" (closed)="closed.emit()" (reopenRequested)="reopenRequested.emit()" />', changeDetection: ChangeDetectionStrategy.OnPush })
class VersionsPanelProbe {
  readonly episodeId = input.required<string>();
  readonly initialLeft = input<string | null>(null);
  readonly initialRight = input<string | null>(null);
  readonly closed = output<void>();
  readonly reopenRequested = output<void>();
  readonly selectionChange = output<{ left: string | null; right: string | null }>();
}

/** The scopes the default test module loads: omitting one (see `createPage`) is how a spec exercises its failed load. */
const TRANSLATIONS: Record<string, Translation> = { vi: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi, 'quality/vi': qualityVi, 'story/vi': storyVi };
/** Both languages, so a spec can switch the active language and prove which copy is frozen and which re-renders. */
const BILINGUAL: Record<string, Translation> = {
  ...TRANSLATIONS, en: coreEn, 'core/en': coreEn, 'episodes/en': episodesEn, 'errors/en': errorsEn, 'quality/en': qualityEn, 'story/en': storyEn,
};

/** Builds the testing module for a given set of loaded scopes; `langs` is exactly what the loader can resolve. */
function translationsModule(langs: Record<string, Translation>) {
  return TranslocoTestingModule.forRoot({
    langs,
    translocoConfig: { availableLangs: Object.keys(langs).filter((key) => !key.includes('/')), defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
    preloadLangs: true,
  });
}

/**
 * A loader that holds chosen scope files back until the spec releases them, so a language switch can be watched while the
 * new language's file is still in flight (the test loader otherwise resolves every path at once).
 */
class GatedScopeLoader implements TranslocoLoader {
  private readonly gates = new Map<string, Subject<Translation>>();
  constructor(private readonly langs: Record<string, Translation>) {}
  /** Registers the gate for one path; that file is served only once the returned subject emits. */
  gate(path: string): Subject<Translation> {
    const gate = new Subject<Translation>();
    this.gates.set(path, gate);
    return gate;
  }
  getTranslation(path: string): Observable<Translation> {
    return this.gates.get(path)?.asObservable() ?? of(this.langs[path] ?? {});
  }
}

const translations = translationsModule(TRANSLATIONS);

const snapshot: StorySnapshot = {
  versionId: 'version-1', version: 1, etag: '"sv-version-1"', source: 'edited', wordCount: 18, qualityHints: [], attestation: null,
  auditReport: null, similarityScore: null,
  content: {
    title: 'Chuyến xe cuối',
    hook: { text: 'Mở đầu', keywords: ['Mưa'], fact_refs: [] },
    paragraphs: Array.from({ length: 4 }, (_, index) => ({ beat: `Nhịp ${index + 1}`, text: `Đoạn ${index + 1}`, keywords: ['Ngõ tối'], fact_refs: [] })),
    ending: { text: 'Kết', keywords: ['Bình minh'], fact_refs: [] },
    description: 'Một câu chuyện đêm mưa', tags: ['ma'], content_warnings: [], structure: null,
  },
};

const detail: EpisodeDetail = {
  id: 'episode-1', seriesId: 'series-1', status: 'story_review', lengthTier: 'standard', etag: '"ep-1"', generation: null,
  hasCurrentStory: true, currentStory: snapshot, nextActions: ['edit_story'], renderJob: null,
};

function fakeDetailStore() {
  const value = signal<EpisodeDetail | null>(detail);
  return {
    value, detail: value.asReadonly(), episode: value.asReadonly(), loading: signal(false), error: signal(null),
    nextActions: signal<readonly string[]>(['edit_story']).asReadonly(),
    load: vi.fn(() => Promise.resolve(detail)), refetch: vi.fn(() => Promise.resolve(detail)),
  };
}

function fakeStoryStore() {
  const paragraphs = signal(snapshot.content.paragraphs.map((paragraph, index) => ({
    segKey: `paragraph-${index}`, beat: paragraph.beat, text: signal(paragraph.text), keywords: signal(paragraph.keywords), factRefs: [],
  })));
  // The choices the page asked the store to resolve, in order: the assertion surface for every conflict flow.
  const conflictChoices: string[] = [];
  const conflict = signal<true | null>(null);
  const current = signal<StorySnapshot>(snapshot);
  const hints = computed(() => current().qualityHints);
  const audit = computed(() => current().auditReport);
  const revision = computed(() => audit()?.revision ?? null);
  const reviewBlock = computed(() => audit()?.modelReview ?? null);
  const similarity = computed(() => audit()?.similarity ?? null);
  const sourcesExist = signal(false);
  return {
    loading: signal(false), generating: signal(false), generationError: signal(null), canGenerate: signal(false), error: signal<{ readonly status: number } | null>(null), snapshot: current, title: signal(snapshot.content.title), hook: signal(snapshot.content.hook.text),
    hints, audit, reviewBlock, revision, similarity, sourcesExist,
    setSourcesExist: vi.fn((exists: boolean) => sourcesExist.set(exists)),
    reviewCounts: computed(() => countBySeverity(hints())), whyIssues: computed(() => whyIssues(audit())),
    revisedBadge: computed(() => current().source === 'revised' || revision()?.applied === true),
    driftNote: computed(() => revision()?.drift === true || whyIssues(audit()).some((issue) => issue.type === 'revision_drift')),
    similarityHighlights: computed(() => highlightMap(similarity(), hints())),
    similarityUnavailable: computed(() => current().similarityScore === null && sourcesExist()),
    ending: signal(snapshot.content.ending.text), meta: signal({ title: snapshot.content.title, description: snapshot.content.description, tags: snapshot.content.tags, contentWarnings: snapshot.content.content_warnings, structure: null }),
    paragraphs, hookKeywords: signal(snapshot.content.hook.keywords), endingKeywords: signal(snapshot.content.ending.keywords), hookKey: signal('hook-key'), endingKey: signal('ending-key'),
    segmentTokens: signal(new Map([['title', 3], ['hook', 2], ['p:0', 2], ['p:1', 2], ['p:2', 2], ['p:3', 2], ['ending', 1], ['meta', 5]])),
    metaTokens: signal(5), totalTokens: signal(19), isDirty: signal(false), saving: signal(false), savedAt: signal<number | null>(null), saveDisabled: signal(true), conflict,
    conflictDiff: signal<readonly string[]>([]), conflictBusy: signal(false), conflictDiffKnown: signal(true),
    remoteChange: signal<{ readonly etag: string; readonly diff: readonly string[] } | null>(null),
    annotations: signal<readonly SegmentAnnotation[]>([]), keywordFlags: signal<readonly unknown[]>([]), videoBlocked: signal(false),
    annotationAnnouncement: signal<{ readonly count: number; readonly sequence: number } | null>(null),
    fieldErrors: signal({}), formErrors: signal<readonly string[]>([]), keywordLimitHit: signal<string | null>(null), readOnly: signal(false), invalidState: signal(false),
    removed: signal<unknown>(null), dirty: signal(false), structureWarning: signal(false),
    version: signal(1), applyKeywordsFlaggedProblem: vi.fn(),
    moveTo: vi.fn(() => true), removeParagraph: vi.fn(() => true), undoRemove: vi.fn((): number | null => 1), discardDraft: vi.fn(),
    load: vi.fn(() => Promise.resolve()), setText: vi.fn(), setSegmentText: vi.fn(), addKeyword: vi.fn(), updateKeyword: vi.fn(), removeKeyword: vi.fn(), setMeta: vi.fn(),
    saveVersion: vi.fn(() => Promise.resolve()), hasUnsavedChanges: vi.fn(() => false),
    // Cancel is the "leave it for now" answer, so the store drops its marker — the next conflict is a fresh one.
    resolveConflict: vi.fn((choice: string): Promise<void> => {
      conflictChoices.push(choice);
      if (choice === 'cancel') conflict.set(null);
      return Promise.resolve();
    }),
    conflictChoices,
  };
}

/** Non-approval specs replace the whole page provider list; keep their unrelated orchestration inert. */
function fakeApprovalStore() {
  return {
    status: signal('story_review'), canOpenApprove: signal(true), canApprove: signal(true), canReopen: signal(false),
    attestation: signal(null), attestationAccepted: signal(false), approving: signal(false), reopening: signal(false),
    error: signal(null), blocked: signal(null), checkboxInvalid: signal(false), retryAfterSec: signal(null),
    conflict: signal(false), outcome: signal(null), hintCount: signal(0),
    approve: vi.fn(), reopen: vi.fn(), reset: vi.fn(), clearBlocked: vi.fn(), clearOutcome: vi.fn(), setAccepted: vi.fn(),
  };
}

function dispatchBeforeUnload(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}

/**
 * Stand-in for `MatSnackBarRef` (node_modules/@angular/material/fesm2022/snack-bar.mjs): `dismissWithAction` (a click)
 * emits `onAction` synchronously and completes it, but `afterDismissed` only fires once the exit animation ends — which
 * is why the page must resolve on `onAction`. A non-click dismissal (`dismiss()` from the duration timeout or the page's
 * effect cleanup) completes `onAction` without emitting; `afterDismissed` then carries `dismissedByAction: false`.
 */
class FakeSnackBarRef {
  private readonly actionSource = new Subject<void>();
  private readonly dismissedSource = new Subject<MatSnackBarDismiss>();
  private clicked = false;
  readonly dismiss = vi.fn((): void => this.endExit());
  onAction(): Observable<void> { return this.actionSource.asObservable(); }
  afterDismissed(): Observable<MatSnackBarDismiss> { return this.dismissedSource.asObservable(); }
  /** The action click (`MatSnackBarRef.dismissWithAction`): `onAction` emits and completes at once, the exit follows. */
  click(): void {
    if (this.actionSource.closed) return;
    this.clicked = true;
    this.actionSource.next();
    this.actionSource.complete();
  }
  /** The exit animation ends (`_finishDismiss`): `afterDismissed` emits once with the click flag and each subject completes. */
  endExit(): void {
    if (this.dismissedSource.closed) return;
    if (!this.actionSource.closed) this.actionSource.complete();
    this.dismissedSource.next({ dismissedByAction: this.clicked });
    this.dismissedSource.complete();
  }
}

function fakeSnackBar() {
  const opened: FakeSnackBarRef[] = [];
  const open = vi.fn((): FakeSnackBarRef => {
    const ref = new FakeSnackBarRef();
    opened.push(ref);
    return ref;
  });
  return { open, opened };
}

/**
 * Stand-in for the `ConflictDialog` service: every `open` records the data it was handed and hands back an
 * `afterClosed` the test drives, so a flow can be stepped one user action at a time — including the second 412 after
 * Save as new and a declined reload confirm, neither of which a real dialog could be asked for in one go.
 */
function fakeConflictDialogs() {
  const opened: ConflictDialogData[] = [];
  const outcomes: Subject<ConflictDialogOutcome>[] = [];
  const open = vi.fn((data: ConflictDialogData) => {
    opened.push(data);
    const closed = new Subject<ConflictDialogOutcome>();
    outcomes.push(closed);
    return { afterClosed: () => closed.asObservable() };
  });
  const choose = (index: number, outcome: ConflictDialogOutcome): void => {
    const closed = outcomes[index];
    if (closed === undefined) throw new Error(`dialog ${index} was never opened`);
    closed.next(outcome);
    closed.complete();
  };
  return { open, opened, choose };
}

/** The page settles every dialog on a promise continuation, so a macrotask — not just a microtask — has to pass. */
// The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
const tick = (): Promise<void> => new Promise((resolve) => { setTimeout(resolve, 0); });

/**
 * `FakeEpisodeRealtime` (the shared stand-in) plus the live progress map the render store reads (10.2.4-T1's
 * `RealtimeService.progress`). The map is a signal, so `push` is exactly what the hub's coalesced flush writes: a stage
 * and a percent for one episode, with no HTTP anywhere near it.
 */
class PageRealtime extends FakeEpisodeRealtime {
  private readonly state = signal<ReadonlyMap<string, { readonly stage: string; readonly percent: number }>>(new Map());
  readonly progress = this.state.asReadonly();

  push(episodeId: string, entry: { readonly stage: string; readonly percent: number }): void {
    const next = new Map(this.state());
    next.set(episodeId, entry);
    this.state.set(next);
  }
}

/** `langs` overrides the loaded scopes, so a spec can leave one out and exercise its failed/absent load. */
async function createPage(options: { readonly langs?: Record<string, Translation>; readonly loader?: TranslocoLoader; readonly panel?: boolean } = {}) {
  const detailStore = fakeDetailStore();
  const storyStore = fakeStoryStore();
  const versions = fakeVersionsStore();
  const repo = new FakeStoryRepository();
  const research = fakeResearchStore();
  const realtime = new PageRealtime();
  const params = new BehaviorSubject(convertToParamMap({ id: 'episode-1' }));
  const query = convertToParamMap(options.panel ? { panel: 'versions' } : {});
  const snack = fakeSnackBar();
  const dialogs = fakeConflictDialogs();
  // The render command port (10.6.1-T2): the page's component-scoped MakeVideoStore and RenderTimelineStore read it.
  const renderRepo = new FakeRenderRepository();
  // The single confirm the page owns: leaving a dirty draft and reloading after a conflict both go through it.
  const confirm = vi.fn(() => ({ afterClosed: () => of(true) }));
  const testbed = TestBed.configureTestingModule({
    imports: [StoryReviewPage, translationsModule(options.langs ?? TRANSLATIONS)],
    deferBlockBehavior: DeferBlockBehavior.Manual,
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'login', children: [] }]),
      { provide: ActivatedRoute, useValue: { paramMap: params, queryParamMap: of(query), snapshot: { paramMap: params.value, queryParamMap: query } } },
      { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: StoryRepository, useValue: repo },
      { provide: RenderRepository, useValue: renderRepo },
      // The review port (10.6.2-T1): the page provides the component-scoped VideoReviewStore, which injects it.
      { provide: VideoReviewRepository, useValue: new FakeVideoReviewRepository() },
      // The page provides the render timeline store, which is the only thing here that reads the socket (D11/WR4).
      { provide: RealtimeService, useValue: realtime },
      { provide: ConflictDialog, useValue: dialogs },
      { provide: ConfirmDialog, useValue: { confirm } },
      { provide: MatSnackBar, useValue: { open: snack.open } },
    ],
  });
  // A spec that needs a scope file still in flight supplies its own loader, which then serves every other path itself.
  const withLoader = options.loader === undefined ? testbed : testbed.overrideProvider(TRANSLOCO_LOADER, { useValue: options.loader });
  await withLoader.overrideComponent(StoryReviewPage, {
    remove: { imports: [VersionsPanelHostComponent] }, add: { imports: [VersionsPanelProbe] },
  }).overrideComponent(StoryReviewPage, {
    set: { providers: [
      { provide: EpisodeDetailStore, useValue: detailStore },
      { provide: StoryEditorStore, useValue: storyStore },
      { provide: VersionsStore, useValue: versions },
      RenderTimelineStore,
      MakeVideoStore,
      VideoReviewStore,
      ...fakeResearchStoreProviders(research),
      ApprovalStore,
    ] },
  }).compileComponents();
  const fixture = TestBed.createComponent(StoryReviewPage);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, storyStore, detailStore, repo, params, research, realtime, approval: fixture.debugElement.injector.get(ApprovalStore), versions, snack, dialogs, confirm, renderRepo, transloco: TestBed.inject(TranslocoService) };
}

const reviewSource: ResearchSource = {
  id: 'source-1', kind: 'user_note', url: null, registrableDomain: null, noteText: 'Source note', status: 'fetched',
  errorCode: null, errorMessageKey: null, httpStatus: null, title: null, finalUrl: null, contentType: null,
  textChars: 11, isCited: true, createdAt: '2026-10-01T00:00:00Z', fetchedAt: null,
};

function reviewedSnapshot(overrides: Partial<StorySnapshot> = {}): StorySnapshot {
  const issue = reviewIssue({ id: 'M1', segment: 'p:3', evidence: 'Recorded draft', repairDirection: 'Use a concrete image.' });
  return {
    ...snapshot, source: 'revised', similarityScore: 0.21,
    qualityHints: [
      { ruleId: 'Q-HOOK-01', kind: 'objective', severity: 'blocking', segment: 'p:3', messageKey: 'quality.hook_short', evidence: issue.evidence, repairDirection: issue.repairDirection },
      { ruleId: 'Q-SIM-01', kind: 'objective', severity: 'advisory', segment: 'meta', messageKey: 'quality.similarity_warn', evidence: 'similarity', repairDirection: null },
    ],
    auditReport: auditReport({
      modelReview: { stale: true, issues: [issue], scores: { hook: 1, pacing: 2, atmosphere: 3, natural_language: 4, originality: 5, factual_care: 4, length_fit: 3 } },
      revision: { applied: true, drift: true, reason: null },
      similarity: similaritySection({ segments: [{ segment: 'p:3', share: 0.21 }] }),
    }),
    ...overrides,
  };
}

describe('StoryReviewPage self-review wiring', () => {
  it.each(['vi', 'en'] as const)('places the revised badge in header reading order and links eager drift/stale explanations in %s', async (lang) => {
    const { fixture, storyStore, transloco } = await createPage({ langs: BILINGUAL });
    transloco.setActiveLang(lang);
    storyStore.snapshot.set(reviewedSnapshot());
    fixture.detectChanges(); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector('nts-revised-badge span')!;
    expect(badge.textContent).toContain((lang === 'vi' ? storyVi : storyEn).generation.revised.badge);
    expect(root.querySelector('h1')!.compareDocumentPosition(badge) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
    expect(badge.compareDocumentPosition(root.querySelector('nts-review-panel')!) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
    expect(badge.getAttribute('aria-describedby')).toBe('story-review-audit-drift story-review-audit-stale');
    expect(root.querySelector('#story-review-audit-drift')).not.toBeNull();
    expect(root.querySelector('#story-review-audit-stale')).not.toBeNull();
    expect(root.querySelector('nts-revised-badge a')).toBeNull();
    expect(root.querySelector('nts-review-panel table')).toBeNull();
  });

  it.each(['generated', 'edited'])('omits the badge and drift note for %s without a revision', async (source) => {
    const { fixture, storyStore } = await createPage();
    storyStore.snapshot.set({ ...snapshot, source });
    fixture.detectChanges(); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('nts-revised-badge')).toBeNull();
    expect(root.querySelector('#story-review-audit-drift')).toBeNull();
  });

  it('removes dangling badge explanations when neither drift nor stale remains', async () => {
    const { fixture, storyStore } = await createPage();
    storyStore.snapshot.set(reviewedSnapshot({ auditReport: auditReport({ modelReview: { stale: false, scores: null, issues: [] } }) }));
    fixture.detectChanges(); await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('nts-revised-badge span')?.hasAttribute('aria-describedby')).toBe(false);
  });

  it('counts flattened hints, renders each inline once and keeps the raw audit only in the deferred why panel', async () => {
    const { fixture, storyStore } = await createPage();
    const current = reviewedSnapshot();
    storyStore.snapshot.set(current);
    storyStore.annotations.set(mergeAnnotations(current.qualityHints, []));
    fixture.detectChanges(); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const panel = fixture.debugElement.query(By.directive(ReviewPanelComponent)).componentInstance as ReviewPanelComponent;
    expect(panel.counts()).toEqual({ blocking: 1, advisory: 1 });
    expect(panel.partial()).toBe(false);
    expect(root.querySelector('.review-panel__counts')?.textContent).toContain(`${episodesVi.audit.blocking}: 1`);
    expect(root.querySelector('nts-review-panel section')?.getAttribute('aria-label')).toBe(episodesVi.audit.heading);
    expect(root.querySelector('textarea[data-segment="p:3"]')?.closest('nts-segment-editor')?.querySelectorAll('nts-inline-annotation')).toHaveLength(1);
    expect(root.querySelector('nts-review-panel nts-inline-annotation')).toBeNull();
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete); await fixture.whenStable();
    expect(root.querySelectorAll('nts-review-panel tbody th[scope="row"]')).toHaveLength(7);
    expect(root.querySelector('nts-review-panel tbody td')?.textContent).toBe('1');
    expect(root.querySelector('nts-review-panel table')?.getAttribute('aria-describedby')).toBe('story-review-audit-stale');
    root.querySelector<HTMLButtonElement>('.review-panel__disclosure')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('#story-review-audit-why')?.textContent).toContain('Use a concrete image.');
    expect(root.querySelector('nts-review-panel [aria-live]')).toBeNull();
  });

  it('shows partial only when a recorded issue is missing from the flattened hints and never synthesizes inline rows', async () => {
    const { fixture, storyStore } = await createPage();
    storyStore.snapshot.set(reviewedSnapshot({ qualityHints: [] }));
    fixture.detectChanges(); await fixture.whenStable();
    const panel = fixture.debugElement.query(By.directive(ReviewPanelComponent)).componentInstance as ReviewPanelComponent;
    expect(panel.partial()).toBe(true);
    expect(panel.counts()).toEqual({ blocking: 0, advisory: 0 });
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('nts-inline-annotation')).toHaveLength(0);
  });

  it('passes no current review as an empty panel rather than a zero-issue claim', async () => {
    const { fixture } = await createPage();
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.review-panel__counts')).toBeNull();
    expect(root.querySelector('.review-panel__empty')?.textContent).toBe(episodesVi.audit.noChecks);
    root.querySelector<HTMLButtonElement>('.review-panel__disclosure')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('#story-review-audit-why')?.textContent?.trim()).toBe(episodesVi.audit.noChecks);
  });

  it('uses the existing research list to show only the server-addressed similarity note and focuses its paragraph', async () => {
    const { fixture, storyStore, research } = await createPage();
    storyStore.snapshot.set(reviewedSnapshot());
    research.setSources([reviewSource]);
    fixture.detectChanges(); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const panel = fixture.debugElement.query(By.directive(ReviewPanelComponent)).componentInstance as ReviewPanelComponent;
    const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
    expect(storyStore.setSourcesExist).toHaveBeenLastCalledWith(true);
    expect(panel.similarityRows()).toEqual([{ address: 'p:3', share: 0.21, level: 'warn', addressLabelKey: 'episodes.editor.segment.paragraph', addressLabelIndex: 4, addressText: null }]);
    expect(root.querySelectorAll('.segment-editor--similarity-warn')).toHaveLength(1);
    const target = root.querySelector<HTMLTextAreaElement>('textarea[data-segment="p:3"]')!;
    expect(target.closest('nts-segment-editor')?.querySelector('.segment-editor__similarity')?.textContent).toBe(qualityVi.similarity_warn);
    const levels = editor.similarityHighlights();
    storyStore.paragraphs()[0]!.text.set('A keystroke elsewhere');
    fixture.detectChanges(); await fixture.whenStable();
    expect(editor.similarityHighlights()).toBe(levels);
    const focus = vi.spyOn(editor, 'focusSegment');
    panel.focusRequested.emit('p:3');
    expect(focus).toHaveBeenCalledWith('p:3');
    expect(document.activeElement).toBe(target);
    research.setSources([]);
    fixture.detectChanges(); await fixture.whenStable();
    expect(storyStore.setSourcesExist).toHaveBeenLastCalledWith(false);
    expect(panel.similarityRows()).toEqual([]);
    expect(panel.similarityScore()).toBeNull();
    expect(panel.similarityUnavailable()).toBe(false);
    expect(root.querySelector('.segment-editor__similarity')).toBeNull();
  });

  it('uses Q-SIM-02 rather than score thresholds and treats past-episode shares as informational', async () => {
    const { fixture, storyStore, research } = await createPage();
    const current = reviewedSnapshot();
    research.setSources([reviewSource]);
    storyStore.snapshot.set({ ...current, similarityScore: 0.001, qualityHints: [{ ...current.qualityHints[1]!, ruleId: 'Q-SIM-02', messageKey: 'quality.similarity_blocked' }] });
    fixture.detectChanges(); await fixture.whenStable();
    const panel = fixture.debugElement.query(By.directive(ReviewPanelComponent)).componentInstance as ReviewPanelComponent;
    expect(panel.similarityRows()[0]?.level).toBe('block');
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.segment-editor--similarity-block')).toHaveLength(1);
    storyStore.snapshot.set({ ...current, auditReport: auditReport({ similarity: similaritySection({ corpus: 'past_episodes', segments: [{ segment: 'p:3', share: 0.21 }] }) }) });
    fixture.detectChanges(); await fixture.whenStable();
    expect(panel.similarityRows()[0]?.level).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('.segment-editor__similarity')).toBeNull();
  });

  it('shows unavailable only with sources and never marks an unscorable snapshot', async () => {
    const { fixture, storyStore, research } = await createPage();
    storyStore.snapshot.set(reviewedSnapshot({ similarityScore: null }));
    research.setSources([reviewSource]);
    fixture.detectChanges(); await fixture.whenStable();
    const panel = fixture.debugElement.query(By.directive(ReviewPanelComponent)).componentInstance as ReviewPanelComponent;
    expect(panel.similarityUnavailable()).toBe(true);
    expect(panel.similarityRows()).toEqual([]);
    expect((fixture.nativeElement as HTMLElement).querySelector('.segment-editor__similarity')).toBeNull();
    const [block] = await fixture.getDeferBlocks();
    await block!.render(DeferBlockState.Complete); await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('.review-panel__similarity')?.textContent).toContain(storyVi.similarity.unavailable);
    research.setSources([]);
    fixture.detectChanges(); await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('.review-panel__similarity')).toBeNull();
  });

  it('withholds the previous research list during an episode gate load and clears the audit on a fresh version', async () => {
    const { fixture, storyStore, research } = await createPage();
    research.setSources([reviewSource]);
    storyStore.snapshot.set(reviewedSnapshot());
    fixture.detectChanges(); await fixture.whenStable();
    expect(storyStore.sourcesExist()).toBe(true);
    research.setLoaded(false);
    fixture.detectChanges(); await fixture.whenStable();
    expect(storyStore.sourcesExist()).toBe(false);
    expect((fixture.nativeElement as HTMLElement).querySelector('.segment-editor__similarity')).toBeNull();
    research.setLoaded(true);
    fixture.detectChanges(); await fixture.whenStable();
    expect(storyStore.sourcesExist()).toBe(true);
    storyStore.snapshot.set({ ...snapshot, versionId: 'version-fresh' });
    fixture.detectChanges(); await fixture.whenStable();
    const panel = fixture.debugElement.query(By.directive(ReviewPanelComponent)).componentInstance as ReviewPanelComponent;
    expect(panel.hasAudit()).toBe(false);
    expect(panel.scores()).toBeNull();
    expect(panel.similarityRows()).toEqual([]);
    expect((fixture.nativeElement as HTMLElement).querySelector('nts-revised-badge')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('#story-review-audit-stale')).toBeNull();
  });

  it('adds no repository request on load through the real editor/detail stores and reuses one gate load', async () => {
    const episodes = new FakeEpisodesRepository().scriptGet(() => Promise.resolve({ ...detail, currentStory: reviewedSnapshot() }));
    const stories = new FakeStoryRepository();
    const research = fakeResearchStore();
    const params = convertToParamMap({ id: detail.id }), query = convertToParamMap({});
    await TestBed.configureTestingModule({
      imports: [StoryReviewPage, translations], deferBlockBehavior: DeferBlockBehavior.Manual,
      providers: [
        ...ZONELESS_TEST_PROVIDERS, provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(params), queryParamMap: of(query), snapshot: { paramMap: params, queryParamMap: query } } },
        { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: stories },
        { provide: RenderRepository, useValue: new FakeRenderRepository() },
        { provide: VideoReviewRepository, useValue: new FakeVideoReviewRepository() },
        { provide: RealtimeService, useValue: new PageRealtime() },
        { provide: SessionApi, useValue: new FakeSessionApi() },
      ],
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [EpisodeDetailStore, RenderTimelineStore, MakeVideoStore, VideoReviewStore, StoryEditorStore, { provide: ApprovalStore, useValue: fakeApprovalStore() }, { provide: VersionsStore, useValue: fakeVersionsStore() }, ...fakeResearchStoreProviders(research)] },
    }).compileComponents();
    const fixture = TestBed.createComponent(StoryReviewPage);
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(episodes.getCalls).toEqual([{ episodeId: detail.id, userAction: true }]);
    expect(research.loadedEpisodeIds).toEqual([detail.id]);
    expect(stories.calls).toHaveLength(0);
    expect(stories.approveCalls).toHaveLength(0);
    expect(stories.reopenCalls).toHaveLength(0);
    expect(episodes.calls).toHaveLength(0);
    expect(fixture.debugElement.injector.get(ResearchStore)).toBe(research);
  });
});

afterEach(() => TestBed.resetTestingModule());

describe('StoryReviewPage', () => {
  it('loads and focuses the story while preserving other segment view models on a single keystroke', async () => {
    const detailStore = fakeDetailStore();
    const storyStore = fakeStoryStore();
    const params = convertToParamMap({ id: 'episode-1' });
    const query = convertToParamMap({ segment: 'p:3' });

    await TestBed.configureTestingModule({
      imports: [StoryReviewPage, translations],
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(params), queryParamMap: of(query), snapshot: { paramMap: params, queryParamMap: query } } },
        { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
        { provide: RealtimeService, useValue: new PageRealtime() },
        { provide: ConflictDialog, useValue: { open: vi.fn() } },
        { provide: ConfirmDialog, useValue: { confirm: vi.fn() } },
        { provide: RenderRepository, useValue: new FakeRenderRepository() },
        { provide: VideoReviewRepository, useValue: new FakeVideoReviewRepository() },
        // The page-scoped review store injects the root `SessionStore` (D6/WD10).
        { provide: SessionApi, useValue: new FakeSessionApi() },
      ],
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [
        { provide: EpisodeDetailStore, useValue: detailStore },
        { provide: StoryEditorStore, useValue: storyStore },
        { provide: VersionsStore, useValue: fakeVersionsStore() },
        { provide: ApprovalStore, useValue: fakeApprovalStore() },
        RenderTimelineStore,
        MakeVideoStore,
        VideoReviewStore,
        ...fakeResearchStoreProviders(fakeResearchStore()),
      ] },
    }).compileComponents();

    const fixture = TestBed.createComponent(StoryReviewPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(storyStore.load).toHaveBeenCalledWith('episode-1');
    expect(root.querySelector('h1')?.textContent).toContain('Chuyến xe cuối');
    expect(root.querySelectorAll('textarea[data-segment^="p:"]')).toHaveLength(4);
    expect(document.activeElement).toBe(root.querySelector('#seg-p-3'));
    const editors = fixture.debugElement.queryAll(By.directive(SegmentEditorComponent)).map((element) => element.componentInstance as SegmentEditorComponent);
    const before = editors.map((editor) => editor.viewModel());
    storyStore.paragraphs()[0]!.text.update((text) => text + '!');
    fixture.detectChanges();
    await fixture.whenStable();
    const after = editors.map((editor) => editor.viewModel());
    expect(after[2]).not.toBe(before[2]);
    expect(after[2]!.text).toBe('Đoạn 1!');
    for (const index of [0, 1, 3, 4, 5, 6]) expect(after[index]).toBe(before[index]);
    storyStore.isDirty.set(true);
    storyStore.error.set({ status: 400 });
    storyStore.formErrors.set(['errors.story_invalid_content']);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    // Several regions are live on this page (the alert, the editor's annotation region, the save bar), so the text is
    // looked for across all of them rather than in whichever one the DOM happens to put first.
    expect([...root.querySelectorAll('[role="status"]')].map((node) => node.textContent ?? '').join(' ')).toContain(errorsVi.story_invalid_content);
    // The store retains the 412 error after Cancel but clears its conflict marker.
    storyStore.error.set({ status: 412 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect([...root.querySelectorAll('[role="status"]')].map((node) => node.textContent ?? '').join(' ')).toContain(episodesVi.editor.unsaved);
  });

  it('beforeunload_IsRegisteredOnlyWhileDirty_AndRemovedWhenClean', async () => {
    const { fixture, storyStore } = await createPage();
    expect(dispatchBeforeUnload()).toBe(false);
    storyStore.dirty.set(true);
    storyStore.hasUnsavedChanges.mockReturnValue(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(dispatchBeforeUnload()).toBe(true);
    // Clean again, even before the debounced dirty indicator catches up: the listener is gone and cannot prompt.
    storyStore.hasUnsavedChanges.mockReturnValue(false);
    expect(dispatchBeforeUnload()).toBe(false);
    storyStore.dirty.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(dispatchBeforeUnload()).toBe(false);
  });

  it('deleteUndoSnackbar_ActionClick_RestoresTheBufferedParagraph', async () => {
    const { fixture, storyStore, snack, transloco } = await createPage();
    storyStore.removed.set({ paragraph: {}, index: 1 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(snack.open).toHaveBeenCalledWith(transloco.translate('editor.deleted', {}, 'episodes'), transloco.translate('editor.undo', {}, 'episodes'), { duration: 10_000 });
    snack.opened[0]!.click();
    await fixture.whenStable();
    expect(storyStore.undoRemove).toHaveBeenCalledTimes(1);
    storyStore.removed.set(null);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(snack.opened[0]!.dismiss).toHaveBeenCalled();
  });

  it('deleteUndoSnackbar_SecondDelete_DoesNotUndoTheNewerBuffer', async () => {
    const { fixture, storyStore, snack } = await createPage();
    storyStore.removed.set({ paragraph: {}, index: 1 });
    fixture.detectChanges();
    await fixture.whenStable();
    const second = { paragraph: {}, index: 2 };
    // The second delete replaces the buffer: the effect cleanup dismisses snackbar 1 without any action click, so
    // snackbar 1's dismissal must never undo the paragraph the later delete buffered.
    storyStore.removed.set(second);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(snack.opened).toHaveLength(2);
    expect(snack.opened[0]!.dismiss).toHaveBeenCalled();
    expect(storyStore.undoRemove).not.toHaveBeenCalled();
    expect(storyStore.removed()).toBe(second);
  });

  it('deleteUndoSnackbar_AutoDismissWithoutClick_LeavesTheParagraphRemoved', async () => {
    const { fixture, storyStore, snack } = await createPage();
    const buffered = { paragraph: {}, index: 1 };
    storyStore.removed.set(buffered);
    fixture.detectChanges();
    await fixture.whenStable();
    // The 10 s duration elapses (or the snackbar is dismissed by hand) with no action click.
    snack.opened[0]!.dismiss();
    await fixture.whenStable();
    expect(storyStore.undoRemove).not.toHaveBeenCalled();
    expect(storyStore.removed()).toBe(buffered);
  });

  it('deleteUndoSnackbar_ActionClickBeforeTheExitAnimation_UndoesImmediately', async () => {
    const { fixture, storyStore, snack } = await createPage();
    const buffered = { paragraph: {}, index: 1 };
    storyStore.removed.set(buffered);
    fixture.detectChanges();
    await fixture.whenStable();
    // The click lands while the snackbar is still on screen: the exit animation has not run, so `afterDismissed` has
    // not fired, yet the buffer is untouched. The page must resolve on the synchronous `onAction` to undo here.
    snack.opened[0]!.click();
    await fixture.whenStable();
    expect(storyStore.undoRemove).toHaveBeenCalledTimes(1);
    // `dismissWithAction` dismisses right after emitting: the later exit must not undo a second time.
    snack.opened[0]!.endExit();
    await fixture.whenStable();
    expect(storyStore.undoRemove).toHaveBeenCalledTimes(1);
  });

  it('moveAndDeleteIntents_ReachTheStore', async () => {
    const { fixture, storyStore } = await createPage();
    const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
    editor.intent.emit({ kind: 'moveParagraph', from: 0, to: 2 });
    expect(storyStore.moveTo).toHaveBeenCalledWith(0, 2);
    editor.intent.emit({ kind: 'removeParagraph', index: 1 });
    expect(storyStore.removeParagraph).toHaveBeenCalledWith(1);
  });

  it('terminalSessionEnd_WhileDirty_DiscardsTheDraft_AndOpensNoDialog', async () => {
    const { fixture, storyStore } = await createPage();
    // The stubbed `ConfirmDialog.confirm` is a vi.fn: reading it unbound is the point of the assertion below.
    // eslint-disable-next-line @typescript-eslint/unbound-method
    const confirm = TestBed.inject(ConfirmDialog).confirm as unknown as MockInstance;
    storyStore.hasUnsavedChanges.mockReturnValue(true);
    // A terminal `SessionEnded` discards through the registry the page registered with, before the guard runs, so the
    // leave prompt is never opened for it.
    TestBed.inject(SessionStore).signOut('revoked');
    expect(storyStore.discardDraft).toHaveBeenCalledTimes(1);
    storyStore.hasUnsavedChanges.mockReturnValue(false);
    await expect(fixture.componentInstance.confirmLeave()).resolves.toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('mounts versions only for the query flag, merges ids safely and preserves the segment query focus', async () => {
    const detailStore = fakeDetailStore(), storyStore = fakeStoryStore();
    const params = convertToParamMap({ id: 'episode-1' });
    const query = new BehaviorSubject(convertToParamMap({ segment: 'p:3' }));
    await TestBed.configureTestingModule({
      imports: [StoryReviewPage, translations],
      providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(params), queryParamMap: query, snapshot: { paramMap: params, queryParamMap: query.value } } },
        { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
        { provide: RealtimeService, useValue: new PageRealtime() },
        { provide: ConflictDialog, useValue: { open: vi.fn() } },
        { provide: ConfirmDialog, useValue: { confirm: vi.fn() } },
        { provide: RenderRepository, useValue: new FakeRenderRepository() },
        { provide: VideoReviewRepository, useValue: new FakeVideoReviewRepository() },
        // The page-scoped review store injects the root `SessionStore` (D6/WD10).
        { provide: SessionApi, useValue: new FakeSessionApi() },
      ],
    }).overrideComponent(StoryReviewPage, {
      remove: { imports: [VersionsPanelHostComponent] },
      add: { imports: [VersionsPanelProbe] },
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [{ provide: EpisodeDetailStore, useValue: detailStore }, { provide: StoryEditorStore, useValue: storyStore }, RenderTimelineStore, MakeVideoStore, VideoReviewStore, { provide: VersionsStore, useValue: fakeVersionsStore() }, { provide: ApprovalStore, useValue: fakeApprovalStore() }, ...fakeResearchStoreProviders(fakeResearchStore())] },
    }).compileComponents();
    const fixture = TestBed.createComponent(StoryReviewPage);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('nts-versions-panel-host')).toBeNull();
    root.querySelector<HTMLButtonElement>('.story-review-page__versions-toggle')!.click();
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { panel: 'versions' }, queryParamsHandling: 'merge', replaceUrl: true }));
    const left = '00000000-0000-7000-8000-000000000001';
    query.next(convertToParamMap({ segment: 'p:3', panel: 'versions', left, right: '<script>content</script>' }));
    fixture.detectChanges(); await fixture.whenStable();
    const probe = fixture.debugElement.query(By.directive(VersionsPanelProbe)).componentInstance as VersionsPanelProbe;
    expect(probe.episodeId()).toBe('episode-1');
    expect(probe.initialLeft()).toBe(left); expect(probe.initialRight()).toBeNull();
    const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
    const focus = vi.spyOn(editor, 'focusSegment');
    probe.selectionChange.emit({ left, right: left });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { left, right: left }, queryParamsHandling: 'merge', replaceUrl: true }));
    query.next(convertToParamMap({ segment: 'p:3', panel: 'versions', left, right: left }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(focus).not.toHaveBeenCalled();
    probe.closed.emit();
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { panel: null, left: null, right: null }, queryParamsHandling: 'merge', replaceUrl: true }));
    query.next(convertToParamMap({ segment: 'p:3' }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-versions-panel-host')).toBeNull();
    expect(storyStore.load).toHaveBeenCalledTimes(1);
  });
  it.each(['close button', 'Escape'] as const)('returns focus to Versions after %s removes the panel, and focuses the heading when reopened', async (gesture) => {
    const detailStore = fakeDetailStore(), storyStore = fakeStoryStore();
    const params = convertToParamMap({ id: 'episode-1' });
    const query = new BehaviorSubject(convertToParamMap({ segment: 'p:3' }));
    await TestBed.configureTestingModule({
      imports: [StoryReviewPage, translations],
      providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(params), queryParamMap: query, snapshot: { paramMap: params, queryParamMap: query.value } } },
        { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
        { provide: RealtimeService, useValue: new PageRealtime() },
        { provide: ConflictDialog, useValue: { open: vi.fn() } },
        { provide: ConfirmDialog, useValue: { confirm: vi.fn() } },
        { provide: RenderRepository, useValue: new FakeRenderRepository() },
        { provide: VideoReviewRepository, useValue: new FakeVideoReviewRepository() },
        // The page-scoped review store injects the root `SessionStore` (D6/WD10).
        { provide: SessionApi, useValue: new FakeSessionApi() },
      ],
    }).overrideComponent(StoryReviewPage, {
      remove: { imports: [VersionsPanelHostComponent] }, add: { imports: [VersionsPanelProbe] },
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [{ provide: EpisodeDetailStore, useValue: detailStore }, { provide: StoryEditorStore, useValue: storyStore }, RenderTimelineStore, MakeVideoStore, VideoReviewStore, { provide: VersionsStore, useValue: fakeVersionsStore() }, { provide: ApprovalStore, useValue: fakeApprovalStore() }, ...fakeResearchStoreProviders(fakeResearchStore())] },
    }).compileComponents();
    const fixture = TestBed.createComponent(StoryReviewPage);
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.detectChanges(); await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector<HTMLButtonElement>('.story-review-page__versions-toggle')!;
    toggle.click();
    query.next(convertToParamMap({ segment: 'p:3', panel: 'versions' }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#versions-heading'));
    if (gesture === 'close button') root.querySelector<HTMLButtonElement>('.versions__close')!.click();
    else root.querySelector<HTMLElement>('.versions')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(document.activeElement).not.toBe(toggle);
    query.next(convertToParamMap({ segment: 'p:3' }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-versions-panel-host')).toBeNull();
    expect(document.activeElement).toBe(toggle);
    toggle.click();
    query.next(convertToParamMap({ segment: 'p:3', panel: 'versions' }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#versions-heading'));
  });

  it('opensTheConflictDialogWithTheTranslatedDiff', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    storyStore.conflictDiff.set(['title', 'p:2']);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened).toHaveLength(1);
    expect(dialogs.opened[0]!.messageKey).toBe('episodes.conflict.body');
    expect(dialogs.opened[0]!.detailHeadingKey).toBe('episodes.conflict.diffHeading');
    // Segment labels read like the editor's own navigation; the server's addresses never reach the copy.
    expect(dialogs.opened[0]!.detailTexts).toEqual([
      episodesVi.editor.segment.title,
      `${episodesVi.editor.segment.paragraph} 3`,
    ]);
    dialogs.choose(0, 'cancel');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['cancel']);
  });

  it('waitsForTheConflictingWriteToSettleBeforeItShowsTheDiff', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    // The 412 lands while its own request is still running; the store reads the incoming story right after it.
    storyStore.saving.set(true);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened).toHaveLength(0);
    storyStore.conflictDiff.set(['hook']);
    storyStore.saving.set(false);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened).toHaveLength(1);
    expect(dialogs.opened[0]!.detailTexts).toEqual([episodesVi.editor.segment.hook]);
    dialogs.choose(0, 'cancel');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['cancel']);
  });

  it('namesWholeEpisodeChangesAndSaysSoWhenNothingDiffers', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    storyStore.conflictDiff.set(['meta']);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened[0]!.detailTexts).toEqual([episodesVi.annotations.meta]);
    dialogs.choose(0, 'cancel');
    await tick();
    storyStore.conflictDiff.set([]);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened[1]!.detailTexts).toEqual([episodesVi.conflict.diffNone]);
    dialogs.choose(1, 'cancel');
    await tick();
  });

  it('reopensTheDialogInTheBusyVariantWhenSaveAsNewLosesTheRaceAgain', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    // The second 412 is what the store records; the page must show it and stop, waiting for the user's next click.
    storyStore.resolveConflict.mockImplementation((choice: string) => {
      storyStore.conflictChoices.push(choice);
      if (choice === 'saveAsNew') storyStore.conflictBusy.set(true);
      return Promise.resolve();
    });
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened[0]!.messageKey).toBe('episodes.conflict.body');
    dialogs.choose(0, 'saveAsNew');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['saveAsNew']);
    expect(dialogs.opened).toHaveLength(2);
    expect(dialogs.opened[1]!.messageKey).toBe('episodes.conflict.busyBody');
    // No automatic retry: the second dialog waits for the user, so the store is asked nothing more.
    await tick();
    expect(storyStore.conflictChoices).toEqual(['saveAsNew']);
    dialogs.choose(1, 'cancel');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['saveAsNew', 'cancel']);
  });

  it('reloadRequiresTheConfirm_AndDecliningReturnsToTheDialogWithTheDraftIntact', async () => {
    const { fixture, storyStore, dialogs, confirm } = await createPage();
    storyStore.dirty.set(true);
    storyStore.hasUnsavedChanges.mockReturnValue(true);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    confirm.mockReturnValue({ afterClosed: () => of(false) });
    dialogs.choose(0, 'reload');
    await tick();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(storyStore.conflictChoices).toEqual([]);
    expect(storyStore.discardDraft).not.toHaveBeenCalled();
    expect(storyStore.dirty()).toBe(true);
    // Declining is not a decision about the conflict: the dialog is where the user came back to.
    expect(dialogs.opened).toHaveLength(2);
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('textarea[data-segment="p:0"]')?.value)
      .toBe('Đoạn 1');
    dialogs.choose(1, 'cancel');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['cancel']);
  });

  it('omitsTheDiffBlockWhileTheDiffReadHasNotSaidAnything', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    storyStore.conflictDiffKnown.set(false);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened).toHaveLength(1);
    // The read told us nothing about the segments: the dialog makes no claim at all, not even "no differing parts".
    expect(dialogs.opened[0]!.detailHeadingKey).toBeUndefined();
    expect(dialogs.opened[0]!.detailTexts).toBeUndefined();
    dialogs.choose(0, 'cancel');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['cancel']);
  });

  it('doesNotShowTheRetryCopyForAnErrorThatPredatesTheReloadAttempt', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    // An error left over from an earlier action is not a failed reload: each iteration derives its variant fresh.
    storyStore.error.set({ status: 400 });
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened[0]!.messageKey).toBe('episodes.conflict.body');
    dialogs.choose(0, 'reload');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['reload']);
    expect(dialogs.opened[1]!.messageKey).toBe('episodes.conflict.body');
    dialogs.choose(1, 'cancel');
    await tick();
  });

  it('reloadFailure_OffersTheSameActionsUnderTheRetryCopy', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    storyStore.resolveConflict.mockImplementation((choice: string) => {
      storyStore.conflictChoices.push(choice);
      // The reload was attempted and came back with an error while the conflict still stands.
      if (choice === 'reload') storyStore.error.set({ status: 500 });
      return Promise.resolve();
    });
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    dialogs.choose(0, 'reload');
    await tick();
    expect(storyStore.conflictChoices).toEqual(['reload']);
    expect(dialogs.opened[1]!.messageKey).toBe('episodes.conflict.reloadFailed');
    expect(dialogs.opened[1]!.saveAsNewLabel).toBe('errors.conflict.saveAsNew');
    dialogs.choose(1, 'cancel');
    await tick();
  });

  it('cancellingKeepsTheDraftDirty_AndHandsFocusBackToSave', async () => {
    const { fixture, storyStore, dialogs } = await createPage();
    storyStore.saveDisabled.set(false);
    storyStore.dirty.set(true);
    storyStore.hasUnsavedChanges.mockReturnValue(true);
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    dialogs.choose(0, 'cancel');
    await tick();
    const root = fixture.nativeElement as HTMLElement;
    expect(storyStore.conflictChoices).toEqual(['cancel']);
    expect(storyStore.discardDraft).not.toHaveBeenCalled();
    expect(storyStore.dirty()).toBe(true);
    const save = root.querySelector<HTMLButtonElement>('.story-save-bar button');
    expect(save).not.toBeNull();
    await vi.waitFor(() => expect(document.activeElement).toBe(save));
  });

  it('aConflictAfterDestroy_OpensNoDialog_AndALateChoiceResolvesNothing', async () => {
    const { fixture, storyStore, dialogs, confirm } = await createPage();
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened).toHaveLength(1);
    fixture.destroy();
    dialogs.choose(0, 'reload');
    await tick();
    expect(confirm).not.toHaveBeenCalled();
    expect(storyStore.conflictChoices).toEqual([]);
    // A conflict that arrives after the route died never reaches a dialog either.
    storyStore.conflict.set(true);
    await tick();
    expect(dialogs.opened).toHaveLength(1);
  });

  it('rendersTheChangedElsewhereNotice_WithoutTouchingTheDraft', async () => {
    const { fixture, storyStore, dialogs, confirm } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    const before = [...root.querySelectorAll<HTMLTextAreaElement>('textarea[data-segment]')].map((area) => area.value);
    storyStore.remoteChange.set({ etag: '"sv-2"', diff: ['p:1', 'meta'] });
    fixture.detectChanges();
    await fixture.whenStable();
    const notice = root.querySelector<HTMLElement>('.story-review-page__notice--remote');
    // The buttoned block is NOT a live region: only its one short copy line is, so a screen reader never reads the
    // controls out as an announcement.
    expect(notice?.getAttribute('role')).toBeNull();
    expect(notice?.querySelector('p[role="status"]')?.textContent).toContain(episodesVi.conflict.elsewhereBody);
    expect(notice?.textContent).toContain(episodesVi.conflict.elsewhereTitle);
    expect([...notice!.querySelectorAll('li')].map((item) => item.textContent))
      .toEqual([`${episodesVi.editor.segment.paragraph} 2`, episodesVi.annotations.meta]);
    // The notice is informational: it never rewrites or scrolls the draft, and it asks nothing of the store on render.
    expect([...root.querySelectorAll<HTMLTextAreaElement>('textarea[data-segment]')].map((area) => area.value)).toEqual(before);
    expect(storyStore.conflictChoices).toEqual([]);
    expect(notice!.querySelectorAll('button')).toHaveLength(3);
    notice!.querySelectorAll<HTMLButtonElement>('button')[1]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual(['saveAsNew']);
    storyStore.remoteChange.set({ etag: '"sv-3"', diff: [] });
    fixture.detectChanges();
    await fixture.whenStable();
    expect([...root.querySelectorAll('.story-review-page__notice--remote li')].map((item) => item.textContent))
      .toEqual([episodesVi.conflict.diffNone]);
    root.querySelectorAll<HTMLButtonElement>('.story-review-page__notice--remote button')[0]!.click();
    await tick();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(storyStore.conflictChoices).toEqual(['saveAsNew', 'reload']);
    root.querySelectorAll<HTMLButtonElement>('.story-review-page__notice--remote button')[2]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual(['saveAsNew', 'reload', 'cancel']);
    // The dialog owns the screen: while it is up the notice stands down.
    storyStore.conflict.set(true);
    fixture.detectChanges();
    await tick();
    expect(dialogs.opened).toHaveLength(1);
    fixture.detectChanges();
    expect(root.querySelector('.story-review-page__notice--remote')).toBeNull();
    dialogs.choose(0, 'cancel');
    await tick();
  });

  it('noticeActions_MoveFocusOnlyWhenTheyResolveTheNotice', async () => {
    const { fixture, storyStore, confirm } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    // A resolved notice leaves a clean draft, so Save is natively disabled and cannot take focus — the assertion below
    // runs on the real `saveDisabled` (nothing here sets it false).
    storyStore.remoteChange.set({ etag: '"sv-2"', diff: [] });
    fixture.detectChanges();
    await fixture.whenStable();
    // The store carries the notice out only when the action really resolves it (a reload can return early, a refetch can
    // fail, a cancel can arrive while a save is in flight): the flag below models both outcomes by hand.
    let resolvesNotice = false;
    storyStore.resolveConflict.mockImplementation((choice: string) => {
      storyStore.conflictChoices.push(choice);
      if (resolvesNotice) storyStore.remoteChange.set(null);
      return Promise.resolve();
    });
    const noticeButtons = () => root.querySelectorAll<HTMLButtonElement>('.story-review-page__notice--remote button');
    // Declining the reload confirm resolves nothing: the notice stays and the pressed button keeps the focus.
    confirm.mockReturnValue({ afterClosed: () => of(false) });
    noticeButtons()[0]!.focus();
    noticeButtons()[0]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual([]);
    expect(document.activeElement).toBe(noticeButtons()[0]);
    // An accepted reload that leaves the notice standing (it returned early, or the refetch failed) keeps the focus too.
    confirm.mockReturnValue({ afterClosed: () => of(true) });
    noticeButtons()[0]!.focus();
    noticeButtons()[0]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual(['reload']);
    expect(document.activeElement).toBe(noticeButtons()[0]);
    // Only a reload that takes the notice away hands focus on: the draft is clean, so the first editor field takes it
    // instead of the disabled Save control.
    resolvesNotice = true;
    const field = root.querySelector<HTMLTextAreaElement>('textarea[data-segment]');
    expect(field).not.toBeNull();
    expect(root.querySelector<HTMLButtonElement>('.story-save-bar button')?.disabled).toBe(true);
    noticeButtons()[0]!.focus();
    noticeButtons()[0]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual(['reload', 'reload']);
    await vi.waitFor(() => expect(document.activeElement).toBe(field));
    // Dismiss has the same rule: a cancel that returns early while a save is in flight leaves the notice up, and the
    // button the user pressed keeps the focus.
    resolvesNotice = false;
    storyStore.remoteChange.set({ etag: '"sv-4"', diff: [] });
    fixture.detectChanges();
    await fixture.whenStable();
    noticeButtons()[2]!.focus();
    noticeButtons()[2]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual(['reload', 'reload', 'cancel']);
    expect(document.activeElement).toBe(noticeButtons()[2]);
    // A dismiss that does resolve it leaves the draft in place and dirty (`saveDisabled` false, as the real store reports
    // it), so the Save control itself takes the focus back.
    resolvesNotice = true;
    storyStore.saveDisabled.set(false);
    noticeButtons()[2]!.focus();
    noticeButtons()[2]!.click();
    await tick();
    expect(storyStore.conflictChoices).toEqual(['reload', 'reload', 'cancel', 'cancel']);
    const save = root.querySelector<HTMLButtonElement>('.story-save-bar button');
    expect(save?.disabled).toBe(false);
    await vi.waitFor(() => expect(document.activeElement).toBe(save));
  });

  it('annotationViews_PassHintKeysThrough_AndFallBackOnUnknownKeys', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      const { fixture, storyStore } = await createPage();
      const annotations: readonly SegmentAnnotation[] = [
        { id: 'ann-p-0-hook_missing', source: 'hint', address: 'p:0', ruleId: 'hook_missing', severity: 'advisory', kind: 'objective', messageKey: 'quality.hook_missing', evidence: 'Hook is short', repairDirection: null, keywordIndex: null, placement: 'inline' },
        { id: 'ann-p-1-not_a_rule', source: 'hint', address: 'p:1', ruleId: 'not_a_rule', severity: 'blocking', kind: 'objective', messageKey: 'quality.not_a_rule', evidence: 'Unknown rule', repairDirection: null, keywordIndex: null, placement: 'inline' },
        { id: 'ann-meta-scenes_sparse', source: 'hint', address: 'meta', ruleId: 'scenes_sparse', severity: 'advisory', kind: 'taste', messageKey: 'quality.hook_too_long', evidence: '', repairDirection: null, keywordIndex: null, placement: 'panel' },
      ];
      storyStore.annotations.set(annotations);
      fixture.detectChanges();
      await fixture.whenStable();
      const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
      const views = editor.annotationViews();
      expect(views.inline.get('p:0')?.map((item) => item.messageKey)).toEqual(['quality.hook_missing']);
      expect(views.inline.get('p:1')?.map((item) => item.messageKey)).toEqual(['episodes.annotations.unknown']);
      expect(views.panel.count).toBe(3);
      expect(views.panel.groups.map((group) => group.kind)).toEqual(['objective', 'taste']);
      // D19: the key alone — never a hint's evidence or any story text.
      expect(warn.mock.calls).toContainEqual(['unknown annotation key', 'quality.not_a_rule']);
      expect(warn.mock.calls.filter((call) => call[0] === 'unknown annotation key').every((call) => call.length === 2)).toBe(true);
    } finally {
      warn.mockRestore();
    }
  });

  it('annotationViews_RebuildOnALanguageSwitch_BecauseHasKeyResolvesPerLanguage', async () => {
    // The hint key exists in `vi` but not in `en`, so only a rebuilt view can tell: the fallback key proves `hasKey`
    // ran again against the new language, which is what the tile's message needs.
    const enQuality: Record<string, string> = { ...qualityEn };
    delete enQuality['hook_missing'];
    const langs: Record<string, Translation> = { ...BILINGUAL, 'quality/en': enQuality };
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      const { fixture, storyStore, transloco } = await createPage({ langs });
      const annotations: readonly SegmentAnnotation[] = [
        { id: 'ann-p-0-hook_missing', source: 'hint', address: 'p:0', ruleId: 'hook_missing', severity: 'advisory', kind: 'objective', messageKey: 'quality.hook_missing', evidence: 'Hook is short', repairDirection: null, keywordIndex: null, placement: 'inline' },
      ];
      storyStore.annotations.set(annotations);
      fixture.detectChanges();
      await fixture.whenStable();
      const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
      expect(editor.annotationViews().inline.get('p:0')?.map((item) => item.messageKey)).toEqual(['quality.hook_missing']);
      transloco.setActiveLang('en');
      fixture.detectChanges();
      await fixture.whenStable();
      expect(editor.annotationViews().inline.get('p:0')?.map((item) => item.messageKey)).toEqual(['episodes.annotations.unknown']);
    } finally {
      warn.mockRestore();
    }
  });

  it('keepsKeywordFlagsAndTheirPanelEntriesWhileTheQualityScopeIsNotLoaded', async () => {
    // The quality scope file is absent, so no `quality.<ruleId>` key can resolve; the flags carry `errors.*`/`episodes.*`
    // keys and must stay visible instead of being hidden with the hints.
    const noQuality = { vi: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi };
    const { fixture, storyStore } = await createPage({ langs: noQuality });
    const annotations: readonly SegmentAnnotation[] = [
      { id: 'ann-p-0-hook_missing', source: 'hint', address: 'p:0', ruleId: 'hook_missing', severity: 'advisory', kind: 'objective', messageKey: 'quality.hook_missing', evidence: 'Hook is short', repairDirection: null, keywordIndex: null, placement: 'inline' },
      { id: 'ann-p-1-keyword_name_detected-k0', source: 'keyword', address: 'p:1', ruleId: 'keyword_name_detected', severity: 'blocking', kind: 'objective', messageKey: 'errors.keywordsFlaggedRule.keyword_name_detected', evidence: '', repairDirection: null, keywordIndex: 0, placement: 'inline' },
    ];
    storyStore.annotations.set(annotations);
    fixture.detectChanges();
    await fixture.whenStable();
    const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
    const views = editor.annotationViews();
    // The hint is withheld (its key cannot resolve), the flagged chip is not.
    expect(views.inline.get('p:0')).toBeUndefined();
    expect(views.flaggedChips.get('p:1')?.get(0)?.messageKey).toBe('errors.keywordsFlaggedRule.keyword_name_detected');
    expect(views.panel.count).toBe(1);
    expect(views.panel.groups.map((group) => group.kind)).toEqual(['objective']);
    expect(views.panel.groups[0]?.items.map((item) => item.id)).toEqual(['ann-p-1-keyword_name_detected-k0']);
    const root = fixture.nativeElement as HTMLElement;
    const flagged = root.querySelector<HTMLInputElement>('input[aria-invalid="true"]');
    expect(flagged).not.toBeNull();
    expect(root.querySelector('#ann-p-0-hook_missing')).toBeNull();
  });

  it('announcement_StaysSilentUntilTheEpisodesScopeLands_ThenFillsInWithoutARawKey', async () => {
    // The `episodes` scope is missing for the default language but present for en: the save resolves no copy (never a
    // raw key), and the scope that arrives with the language switch fills the same sequence in.
    const langs: Record<string, Translation> = { ...BILINGUAL };
    delete langs['episodes/vi'];
    const { fixture, storyStore, transloco } = await createPage({ langs });
    const root = fixture.nativeElement as HTMLElement;
    storyStore.annotationAnnouncement.set({ count: 2, sequence: 1 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')).toBeNull();
    transloco.setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent)
      .toBe(transloco.translate('annotations.announce', { count: 2 }, 'episodes'));
  });

  it('annotationViews_WithholdHintsUntilTheNewLanguagesScopeArrives', async () => {
    // The new language's quality file is held back, so for a while there is no scope to resolve a hint against: the tile
    // must wait for it instead of resolving against the previous language's file and reading as an unknown rule.
    const loader = new GatedScopeLoader(BILINGUAL);
    const gate = loader.gate('quality/en');
    const langs: Record<string, Translation> = { ...BILINGUAL };
    delete langs['quality/en'];
    const enQuality: Record<string, string> = { ...qualityEn, hook_missing: 'Hook is missing (gated en)' };
    const { fixture, storyStore, transloco } = await createPage({ langs, loader });
    const root = fixture.nativeElement as HTMLElement;
    const annotations: readonly SegmentAnnotation[] = [
      { id: 'ann-p-0-hook_missing', source: 'hint', address: 'p:0', ruleId: 'hook_missing', severity: 'advisory', kind: 'objective', messageKey: 'quality.hook_missing', evidence: 'Hook is short', repairDirection: null, keywordIndex: null, placement: 'inline' },
      { id: 'ann-p-1-keyword_name_detected-k0', source: 'keyword', address: 'p:1', ruleId: 'keyword_name_detected', severity: 'blocking', kind: 'objective', messageKey: 'errors.keywordsFlaggedRule.keyword_name_detected', evidence: '', repairDirection: null, keywordIndex: 0, placement: 'inline' },
    ];
    storyStore.annotations.set(annotations);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.inline-annotation__message')?.textContent).toBe(qualityVi.hook_missing);
    transloco.setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
    // Nothing resolves yet for `en`: the hint is withheld (never rendered as an unknown rule) while the flag stays visible.
    expect(editor.annotationViews().inline.get('p:0')).toBeUndefined();
    expect(editor.annotationViews().flaggedChips.get('p:1')?.get(0)?.messageKey).toBe('errors.keywordsFlaggedRule.keyword_name_detected');
    expect(root.querySelector('#ann-p-0-hook_missing')).toBeNull();
    gate.next(enQuality);
    gate.complete();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(editor.annotationViews().inline.get('p:0')?.map((item) => item.messageKey)).toEqual(['quality.hook_missing']);
    expect(root.querySelector('.inline-annotation__message')?.textContent).toBe(enQuality['hook_missing']);
  });

  it('announcement_FillsInWhenTheNewLanguagesEpisodesScopeArrives', async () => {
    // The same rule for the announcement: the `episodes` file of the new language is in flight, so a save made while it
    // is missing announces nothing (never a raw key) and the copy lands, frozen, once the file arrives.
    const loader = new GatedScopeLoader(BILINGUAL);
    const gate = loader.gate('episodes/en');
    const langs: Record<string, Translation> = { ...BILINGUAL };
    delete langs['episodes/en'];
    const { fixture, storyStore, transloco } = await createPage({ langs, loader });
    const root = fixture.nativeElement as HTMLElement;
    transloco.setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    storyStore.annotationAnnouncement.set({ count: 2, sequence: 1 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')).toBeNull();
    gate.next(episodesEn);
    gate.complete();
    fixture.detectChanges();
    await fixture.whenStable();
    const announcedEn = transloco.translate('annotations.announce', { count: 2 }, 'episodes');
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent).toBe(announcedEn);
    // Frozen: going back to `vi` cannot rewrite what was read out.
    transloco.setActiveLang('vi');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent).toBe(announcedEn);
  });

  it('announcement_FreezesTheCopyPerSaveAndReCreatesTheAnnouncedNode', async () => {
    const { fixture, storyStore, transloco } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    const editor = fixture.debugElement.query(By.directive(StoryEditorComponent)).componentInstance as StoryEditorComponent;
    expect(editor.announcement()).toBeNull();
    expect(root.querySelector('.story-editor__annotation-announcement span')).toBeNull();
    storyStore.annotationAnnouncement.set({ count: 2, sequence: 1 });
    fixture.detectChanges();
    await fixture.whenStable();
    const announced = transloco.translate('annotations.announce', { count: 2 }, 'episodes');
    const node = root.querySelector('.story-editor__annotation-announcement span');
    expect(node?.textContent).toBe(announced);
    expect(editor.announcement()).toEqual({ text: announced, sequence: 1 });
    // One save, one message: the count the server returned appears exactly once in the page.
    expect(root.textContent?.split(announced)).toHaveLength(2);
    // A second save with the same count keeps the copy but must still re-create the announced node, or the region would
    // stay mute (the assertion that used to pin a trailing space is gone).
    storyStore.annotationAnnouncement.set({ count: 2, sequence: 2 });
    fixture.detectChanges();
    await fixture.whenStable();
    const recreated = root.querySelector('.story-editor__annotation-announcement span');
    expect(recreated?.textContent).toBe(announced);
    expect(recreated).not.toBe(node);
    expect(editor.announcement()?.sequence).toBe(2);
    // A single suggestion reads as one, not "1 suggestions".
    storyStore.annotationAnnouncement.set({ count: 1, sequence: 3 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent)
      .toBe(transloco.translate('annotations.announceOne', {}, 'episodes'));
    storyStore.annotationAnnouncement.set({ count: 0, sequence: 4 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent)
      .toBe(transloco.translate('annotations.announceNone', {}, 'episodes'));
    storyStore.annotationAnnouncement.set(null);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(editor.announcement()).toBeNull();
    expect(root.querySelector('.story-editor__annotation-announcement span')).toBeNull();
  });

  it('announcement_KeepsTheCopyReadAtSaveTimeWhenTheLanguageChanges', async () => {
    const { fixture, storyStore, transloco } = await createPage({ langs: BILINGUAL });
    const root = fixture.nativeElement as HTMLElement;
    storyStore.annotationAnnouncement.set({ count: 2, sequence: 1 });
    fixture.detectChanges();
    await fixture.whenStable();
    const announcedVi = transloco.translate('annotations.announce', { count: 2 }, 'episodes');
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent).toBe(announcedVi);
    // The rest of the page follows the language; the announcement is frozen, so what a screen reader already read stands.
    transloco.setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')?.textContent).toBe(announcedVi);
    expect(root.querySelector('.story-save-bar button')?.textContent).toContain(episodesEn.editor.saveVersion);
  });

  it.each(['story_review', 'story_approved', 'draft', 'rendering'] as const)('shows only the decision appropriate to %s and never offers approve without a story', async (status) => {
    const { fixture, detailStore } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    detailStore.value.set({ ...detail, status });
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.story-review-page__approve') !== null).toBe(status === 'story_review');
    expect(root.querySelector('.story-review-page__reopen') !== null).toBe(status === 'story_approved');
    detailStore.value.set({ ...detail, status, currentStory: null, hasCurrentStory: false });
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.story-review-page__approve')).toBeNull();
    expect(root.querySelector('.story-review-page__reopen')).toBeNull();
  });

  it.each(['dirty', 'saving', 'keywords', 'moderation'] as const)('disables the launcher for the WR6 %s blocker but not for quality hints', async (condition) => {
    const { fixture, detailStore, storyStore, repo } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    detailStore.value.set({ ...detail, currentStory: { ...snapshot, qualityHints: [{ ruleId: 'hint', severity: 'blocking', kind: 'objective', segment: 'hook', messageKey: 'quality.hint', evidence: '', repairDirection: null }] } });
    fixture.detectChanges(); await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('.story-review-page__approve')!;
    expect(button.disabled).toBe(false);
    if (condition === 'dirty') storyStore.dirty.set(true);
    if (condition === 'saving') storyStore.saving.set(true);
    if (condition === 'keywords') storyStore.videoBlocked.set(true);
    if (condition === 'moderation') {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
      repo.scriptApprove(() => Promise.reject({ code: 'story_moderated', kind: 'conflict', status: 409, fieldErrors: {} }));
      button.click();
      fixture.detectChanges(); await fixture.whenStable();
      root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:last-child')!.click();
      await fixture.whenStable();
    }
    fixture.detectChanges(); await fixture.whenStable();
    expect(button.disabled).toBe(true);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    if (condition === 'dirty' || condition === 'saving') {
      expect(button.getAttribute('aria-describedby')).toBe('approval-save-hint');
      expect(root.querySelector('#approval-save-hint')).not.toBeNull();
    }
    if (condition === 'moderation') {
      storyStore.version.update((version) => version + 1);
      fixture.detectChanges(); await fixture.whenStable();
      expect(button.disabled).toBe(false);
    }
  });

  it('opens with hints, confirms once with the freshest ETag and no non-true-crime attestation, announces and focuses Reopen', async () => {
    const { fixture, detailStore, repo, snack, transloco, approval } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    root.querySelector<HTMLButtonElement>('.story-review-page__approve')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    const dialog = fixture.debugElement.query(By.directive(ApproveDialogComponent)).componentInstance as ApproveDialogComponent;
    expect(dialog.episodeId()).toBe(detail.id);
    expect(dialog.confirmDisabled()).toBe(false);
    detailStore.value.set({ ...detail, currentStory: { ...snapshot, etag: '"sv-freshest"' } });
    detailStore.refetch.mockImplementation(() => {
      const approved = { ...detail, status: 'story_approved', nextActions: ['reopen_story'] };
      detailStore.value.set(approved);
      return Promise.resolve(approved);
    });
    const confirm = root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:last-child')!;
    confirm.click(); confirm.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(repo.approveCalls).toEqual([{ episodeId: detail.id, etag: '"sv-freshest"', attestation: undefined }]);
    expect(root.querySelector('nts-approve-dialog')).toBeNull();
    expect(root.querySelector('.story-review-page__approve')).toBeNull();
    expect(approval.outcome()).toBeNull();
    const text = transloco.translate('approve.done', {}, 'episodes');
    expect(snack.open).toHaveBeenCalledWith(text, undefined, { duration: 5000, politeness: 'off' });
    expect(root.querySelector('.story-review-page__announcement')?.getAttribute('aria-live')).toBe('polite');
    expect(root.querySelector('.story-review-page__announcement')?.textContent).toContain(text);
    expect(document.activeElement).toBe(root.querySelector('.story-review-page__reopen'));
    fixture.destroy(); root.remove();
  });

  it('allows opening true-crime attestation but requires the tick before sending its current server text version', async () => {
    const { fixture, detailStore, repo, approval } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    const statement = attestation({ text: '<b>Server statement</b>\n\nRead this literally.', textVersion: 'server-v2' });
    detailStore.value.set({ ...detail, currentStory: { ...snapshot, attestation: statement } });
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('.story-review-page__approve')!.disabled).toBe(false);
    root.querySelector<HTMLButtonElement>('.story-review-page__approve')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    const confirm = root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:last-child')!;
    expect(confirm.disabled).toBe(true);
    confirm.click(); expect(repo.approveCalls).toEqual([]);
    expect(root.querySelector('.attestation__text')?.textContent).toBe('<b>Server statement</b>');
    expect(root.querySelector('.attestation b')).toBeNull();
    root.querySelector<HTMLInputElement>('mat-checkbox input[type="checkbox"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(approval.attestationAccepted()).toBe(true);
    expect(confirm.disabled).toBe(false);
    confirm.click(); await fixture.whenStable();
    expect(repo.approveCalls).toEqual([{ episodeId: detail.id, etag: snapshot.etag, attestation: { accepted: true, textVersion: 'server-v2' } }]);
  });

  it('consumes 412 once, uses the existing Reload flow and retries only on a new confirm with its fresh ETag', async () => {
    const { fixture, detailStore, storyStore, repo, approval, dialogs, confirm } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
    repo.scriptApprove(() => Promise.reject({ code: 'precondition_failed', kind: 'conflict', status: 412, fieldErrors: {} }));
    root.querySelector<HTMLButtonElement>('.story-review-page__approve')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:last-child')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(dialogs.open).toHaveBeenCalledTimes(1);
    expect(approval.conflict()).toBe(false);
    expect(root.querySelector('nts-approve-dialog')).toBeNull();
    expect(dialogs.opened[0]?.saveAsNewLabel).toBe('errors.conflict.saveAsNew');
    storyStore.resolveConflict.mockImplementation((choice) => {
      storyStore.conflictChoices.push(choice);
      if (choice === 'reload') detailStore.value.set({ ...detail, currentStory: { ...snapshot, etag: '"sv-after-reload"' } });
      storyStore.conflict.set(null);
      return Promise.resolve();
    });
    dialogs.choose(0, 'reload');
    await tick(); fixture.detectChanges(); await fixture.whenStable();
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ titleKey: 'episodes.conflict.reloadConfirmTitle' }), expect.anything());
    expect(storyStore.resolveConflict).toHaveBeenCalledExactlyOnceWith('reload');
    expect(root.querySelector('nts-approve-dialog')).not.toBeNull();
    expect(repo.approveCalls).toHaveLength(1);
    root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:last-child')!.click();
    await fixture.whenStable();
    expect(repo.approveCalls.map((call) => call.etag)).toEqual([snapshot.etag, '"sv-after-reload"']);
    expect(dialogs.open).toHaveBeenCalledTimes(1);
  });

  it('opens the same reopen dialog from the versions hand-over, then announces the success and restores trigger focus', async () => {
    const { fixture, detailStore, storyStore, repo, versions, transloco, snack } = await createPage({ panel: true });
    const root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    detailStore.value.set({ ...detail, status: 'story_approved', nextActions: ['reopen_story', 'render'] });
    storyStore.readOnly.set(true);
    versions.needsReopen.set(true);
    fixture.detectChanges(); await fixture.whenStable();
    const reopen = root.querySelector<HTMLButtonElement>('.story-review-page__reopen')!;
    expect(reopen.disabled).toBe(false);
    expect(root.querySelectorAll('.story-review-page__reopen')).toHaveLength(1);
    const panelReopen = root.querySelector<HTMLButtonElement>('.versions__reopen')!;
    panelReopen.focus();
    panelReopen.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.debugElement.query(By.directive(ReopenDialogComponent))).not.toBeNull();
    detailStore.refetch.mockImplementation(() => {
      detailStore.value.set(detail);
      storyStore.readOnly.set(false);
      return Promise.resolve(detail);
    });
    root.querySelector<HTMLButtonElement>('nts-reopen-dialog .confirm-dialog__actions button:last-child')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(repo.reopenCalls).toEqual([{ episodeId: detail.id, etag: snapshot.etag }]);
    expect(root.querySelector('nts-reopen-dialog')).toBeNull();
    expect(root.querySelector<HTMLTextAreaElement>('textarea[data-segment]')!.readOnly).toBe(false);
    const text = transloco.translate('reopen.done', {}, 'episodes');
    expect(snack.open).toHaveBeenCalledWith(text, undefined, { duration: 5000, politeness: 'off' });
    expect(root.querySelector('.story-review-page__announcement')?.textContent).toContain(text);
    // The reopen succeeded, so the versions panel's stale needs-reopen notice (and the action it offers) is gone.
    expect(versions.needsReopen()).toBe(false);
    // The panel's trigger survives in this probe, so focus returns there.
    expect(document.activeElement).toBe(root.querySelector('.versions__reopen'));
    fixture.destroy(); root.remove();
  });

  it('clears the versions needs-reopen notice on a reopen outcome only, and consumes the outcome', async () => {
    const { fixture, approval, versions } = await createPage();
    versions.needsReopen.set(true);
    approval.outcome.set('approved');
    fixture.detectChanges(); await fixture.whenStable();
    // Approving is not what the notice was waiting for: a stale notice keeps demanding a reopen it does not need.
    expect(versions.needsReopen()).toBe(true);
    approval.outcome.set('reopened');
    fixture.detectChanges(); await fixture.whenStable();
    expect(versions.needsReopen()).toBe(false);
    expect(approval.outcome()).toBeNull();
  });

  it('returns focus to the trigger on Cancel, clears refusal state and resets dialogs on episode change and destroy', async () => {
    const { fixture, detailStore, params, approval } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    const trigger = root.querySelector<HTMLButtonElement>('.story-review-page__approve')!;
    trigger.focus(); trigger.click();
    fixture.detectChanges(); await fixture.whenStable();
    approval.blocked.set({ code: 'story_sources_required', kind: 'conflict', status: 409, fieldErrors: {} });
    root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:first-child')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-approve-dialog')).toBeNull();
    expect(approval.blocked()).toBeNull();
    expect(document.activeElement).toBe(trigger);
    trigger.click(); fixture.detectChanges(); await fixture.whenStable();
    approval.setAccepted(true);
    detailStore.value.set({ ...detail, id: 'episode-2' });
    params.next(convertToParamMap({ id: 'episode-2' }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-approve-dialog')).toBeNull();
    expect(approval.attestationAccepted()).toBe(false);
    expect(approval.blocked()).toBeNull();
    const reset = vi.spyOn(approval, 'reset');
    fixture.destroy();
    expect(reset).toHaveBeenCalledTimes(1);
    root.remove();
  });

  it('keeps reopen open after a 429 with the server countdown and disables confirmation during the wait', async () => {
    const { fixture, detailStore, repo, approval } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    detailStore.value.set({ ...detail, status: 'story_approved', nextActions: ['reopen_story'] });
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
    repo.scriptReopen(() => Promise.reject({ code: 'rate_limited', kind: 'rate_limit', status: 429, fieldErrors: {}, retryAfterSec: 30 }));
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.story-review-page__reopen')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('nts-reopen-dialog .confirm-dialog__actions button:last-child')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(approval.retryAfterSec()).toBe(30);
    expect(approval.canReopen()).toBe(false);
    expect(root.querySelector('nts-reopen-dialog')).not.toBeNull();
    const dialog = fixture.debugElement.query(By.directive(ReopenDialogComponent)).componentInstance as ReopenDialogComponent;
    expect(dialog.retryAfterSec()).toBe(30);
    expect(root.querySelector('nts-reopen-dialog nts-retry-countdown')).not.toBeNull();
    const confirm = root.querySelector<HTMLButtonElement>('nts-reopen-dialog .confirm-dialog__actions button:last-child')!;
    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    confirm.click();
    expect(repo.reopenCalls).toHaveLength(1);
    fixture.destroy();
  });

  it('requires a fresh attestation tick after cancelling and reopening the approve dialog', async () => {
    const { fixture, detailStore, approval } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    detailStore.value.set({ ...detail, currentStory: { ...snapshot, attestation: attestation() } });
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.story-review-page__approve')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLInputElement>('mat-checkbox input[type="checkbox"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(approval.attestationAccepted()).toBe(true);
    root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:first-child')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(approval.attestationAccepted()).toBe(false);
    root.querySelector<HTMLButtonElement>('.story-review-page__approve')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector<HTMLInputElement>('mat-checkbox input[type="checkbox"]')!.checked).toBe(false);
    expect(root.querySelector<HTMLButtonElement>('nts-approve-dialog .confirm-dialog__actions button:last-child')!.disabled).toBe(true);
  });

  it('omits Save mine as new version for a reopen conflict on an approved read-only story', async () => {
    const { fixture, detailStore, storyStore, repo, dialogs } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    detailStore.value.set({ ...detail, status: 'story_approved', nextActions: ['reopen_story'] });
    storyStore.readOnly.set(true);
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
    repo.scriptReopen(() => Promise.reject({ code: 'story_version_conflict', kind: 'conflict', status: 412, fieldErrors: {} }));
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('.story-review-page__reopen')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('nts-reopen-dialog .confirm-dialog__actions button:last-child')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(dialogs.open).toHaveBeenCalledTimes(1);
    expect(dialogs.opened[0]).not.toHaveProperty('saveAsNewLabel');
    dialogs.choose(0, 'cancel');
    await tick(); fixture.detectChanges(); await fixture.whenStable();
    expect(storyStore.resolveConflict).toHaveBeenCalledExactlyOnceWith('cancel');
    expect(root.querySelector('nts-reopen-dialog')).not.toBeNull();
  });

  it('refetches detail once for a new save timestamp without looping when the detail answers', async () => {
    const { fixture, storyStore, detailStore } = await createPage();
    expect(detailStore.refetch).not.toHaveBeenCalled();
    storyStore.savedAt.set(1000);
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).toHaveBeenCalledTimes(1);
    detailStore.value.set({ ...detail, currentStory: { ...snapshot, etag: '"sv-after-save"' } });
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).toHaveBeenCalledTimes(1);
    storyStore.savedAt.set(1000);
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).toHaveBeenCalledTimes(1);
    storyStore.savedAt.set(2000);
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).toHaveBeenCalledTimes(2);
  });

  it('does not refetch for a savedAt change while an approval command is pending', async () => {
    const { fixture, storyStore, detailStore, approval } = await createPage();
    approval.approving.set(true);
    storyStore.savedAt.set(1000);
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).not.toHaveBeenCalled();
    approval.approving.set(false);
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).not.toHaveBeenCalled();
  });

  it.each(['approve', 'reopen'] as const)('awaits the post-save detail refetch before %s sends the current ETag', async (intent) => {
    const { fixture, detailStore, storyStore, repo } = await createPage();
    const root = fixture.nativeElement as HTMLElement;
    const current = intent === 'approve' ? detail : { ...detail, status: 'story_approved', nextActions: ['reopen_story'] };
    detailStore.value.set(current);
    fixture.detectChanges(); await fixture.whenStable();
    const refreshed = new Subject<EpisodeDetail>();
    detailStore.refetch.mockImplementationOnce(() => firstValueFrom(refreshed).then((fresh) => {
      detailStore.value.set(fresh);
      return fresh;
    }));
    storyStore.savedAt.set(1000);
    fixture.detectChanges(); await fixture.whenStable();
    expect(detailStore.refetch).toHaveBeenCalledTimes(1);
    root.querySelector<HTMLButtonElement>(intent === 'approve' ? '.story-review-page__approve' : '.story-review-page__reopen')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    root.querySelector<HTMLButtonElement>(`nts-${intent}-dialog .confirm-dialog__actions button:last-child`)!.click();
    await Promise.resolve();
    expect(repo.approveCalls).toHaveLength(0);
    expect(repo.reopenCalls).toHaveLength(0);
    const fresh = { ...current, currentStory: { ...snapshot, etag: '"sv-post-save-detail"' } };
    refreshed.next(fresh); refreshed.complete();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    const calls = intent === 'approve' ? repo.approveCalls : repo.reopenCalls;
    expect(calls).toHaveLength(1);
    expect(calls[0]?.etag).toBe('"sv-post-save-detail"');
  });

});

/** One job row with the render fixtures' defaults (F5): each spec overrides only what it is about. */
function renderJobFixture(overrides: Partial<RenderJob>): RenderJob {
  return {
    renderJobId: 'job-1', status: 'queued', stage: 'queued', percent: 0, attempt: 1, imagesNeeded: 3,
    submittedAt: null, finishedAt: null, errorCode: null, hints: [], images: null, ...overrides,
  };
}

const SECOND_EPISODE_ID = '33333333-3333-4333-8333-333333333333';

/**
 * The render-view harness (10.6.1-T1/WD1): the page with the real `EpisodeDetailStore` and the real `RenderTimelineStore`
 * over the scriptable episodes repository and the progress-pushing realtime fake, so a spec proves the served states and
 * the "a live event adds no HTTP" rule against the repository's own call counters. Every other store is a fake, exactly
 * as in `createPage`; `answers` queues the responses of the reads after the first one (the store's `refresh()`).
 */
async function createRenderPage(detail: EpisodeDetail, options: { readonly answers?: readonly EpisodeDetail[]; readonly canReopen?: boolean; readonly status?: string; readonly messageKey?: (error: AppError) => string } = {}) {
  const episodes = new FakeEpisodesRepository().scriptGet(() => Promise.resolve(detail));
  for (const answer of options.answers ?? []) episodes.scriptGet(() => Promise.resolve(answer));
  const realtime = new PageRealtime();
  const approval = fakeApprovalStore();
  if (options.canReopen === true) approval.canReopen.set(true);
  if (options.status !== undefined) approval.status.set(options.status);
  // The render command port (10.6.1-T2): both the timeline's cancel and the make-video command go through it.
  const renderRepo = new FakeRenderRepository();
  // The review port (10.6.2-T1): unused by these states, but the page's component-scoped store injects it.
  const reviewRepo = new FakeVideoReviewRepository();
  // The one confirm the make-video command opens; every other dialog in this view is a fake too.
  const confirm = vi.fn(() => ({ afterClosed: () => of(true) }));
  const params = new BehaviorSubject(convertToParamMap({ id: detail.id }));
  const query = convertToParamMap({});
  // The page's own load is what reads the detail (production: `StoryEditorStore.load` → `EpisodeDetailStore.load`), so the
  // fake delegates it to the page's own component-scoped store over the scriptable repository: the branch's data and the
  // call counters that prove "a live event adds no HTTP" are the real ones, not a harness shortcut.
  let detailStore: EpisodeDetailStore | null = null;
  const story = { ...fakeStoryStore(), load: (id: string) => detailStore?.load(id) ?? Promise.resolve(null) };
  await TestBed.configureTestingModule({
    imports: [StoryReviewPage, translations], deferBlockBehavior: DeferBlockBehavior.Manual,
    providers: [
      ...ZONELESS_TEST_PROVIDERS, provideRouter([]),
      { provide: ActivatedRoute, useValue: { paramMap: params, queryParamMap: of(query), snapshot: { paramMap: params.value, queryParamMap: query } } },
      { provide: AppErrorMapper, useValue: {
        messageKey: options.messageKey ?? (() => 'errors.story_invalid_content'),
        // The real footer shape (`errors.unknown.trace` + the id as a param), so the client-bug alert can be asserted.
        traceFooter: (error: AppError) => (error.traceId === undefined
          ? null
          : { key: 'errors.unknown.trace', params: { traceId: error.traceId } }),
      } },
      { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: new FakeStoryRepository() },
      { provide: RenderRepository, useValue: renderRepo },
      { provide: VideoReviewRepository, useValue: reviewRepo },
      { provide: RealtimeService, useValue: realtime },
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: ConflictDialog, useValue: fakeConflictDialogs() },
      { provide: ConfirmDialog, useValue: { confirm } },
      { provide: MatSnackBar, useValue: { open: vi.fn(() => ({ onAction: () => of(undefined), dismiss: vi.fn() })) } },
    ],
  }).overrideComponent(StoryReviewPage, {
    remove: { imports: [VersionsPanelHostComponent] }, add: { imports: [VersionsPanelProbe] },
  }).overrideComponent(StoryReviewPage, {
    set: { providers: [
      EpisodeDetailStore,
      RenderTimelineStore,
      MakeVideoStore,
      VideoReviewStore,
      { provide: StoryEditorStore, useValue: story },
      { provide: ApprovalStore, useValue: approval },
      { provide: VersionsStore, useValue: fakeVersionsStore() },
      ...fakeResearchStoreProviders(fakeResearchStore()),
    ] },
  }).compileComponents();
  const fixture = TestBed.createComponent(StoryReviewPage);
  // The component's own injector: `providers` (and the override above) live on this element, not on the TestBed root.
  detailStore = fixture.componentRef.injector.get(EpisodeDetailStore);
  fixture.detectChanges(); await fixture.whenStable();
  fixture.detectChanges(); await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, episodes, realtime, approval, params, renderRepo, reviewRepo, story, confirm };
}

describe('StoryReviewPage render view (10.6.1-T1)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders the three guaranteed stages, the queued one active and the 0 % bar with the notice for a queued job', async () => {
    const { root } = await createRenderPage(episodeDetail({
      status: 'rendering', nextActions: ['cancel'], renderJob: renderJobFixture({}),
    }));
    const timeline = root.querySelector('[data-testid="render-timeline"]')!;
    expect(timeline).not.toBeNull();
    const stages = [...timeline.querySelectorAll('[data-testid="render-stage"]')];
    expect(stages.map((stage) => stage.querySelector('strong')?.textContent)).toEqual([
      episodesVi.render.stage.queued, episodesVi.render.stage.running, episodesVi.render.stage.ingesting,
    ]);
    expect(stages.map((stage) => stage.getAttribute('aria-current'))).toEqual(['step', null, null]);
    expect(timeline.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('0%');
    expect(timeline.textContent).toContain(episodesVi.render.timeline.elapsed.replace('{{duration}}', '0:00'));
    expect(timeline.textContent).toContain(episodesVi.render.timeline.emailNotice);
    expect(timeline.textContent).toContain(episodesVi.render.timeline.leave);
    // The waiting notice is explicitly not an error: 0 s in, and no alert anywhere on the view.
    expect(timeline.querySelector('[data-testid="render-waiting"]')).toBeNull();
    expect(root.querySelector('nts-render-failure')).toBeNull();
  });

  it('takes the render branch for a rendering episode with no story on the detail and still heads it', async () => {
    const { root, episodes } = await createRenderPage(episodeDetail({
      status: 'rendering', hasCurrentStory: false, currentStory: null, nextActions: ['cancel'], renderJob: renderJobFixture({}),
    }));
    expect(root.querySelector('nts-empty-state')).toBeNull();
    expect(root.querySelector('h1')?.textContent).toContain(snapshot.content.title);
    expect(root.querySelector('nts-status-chip')?.getAttribute('aria-label')).toBe(coreVi.core.ui.statusChip.rendering);
    expect(episodes.getCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
  });

  it('shows the loading state and reads the detail exactly once more while the job row has not landed', async () => {
    const pending = episodeDetail({ status: 'rendering', nextActions: ['cancel'] });
    const { root, episodes, fixture } = await createRenderPage(pending, { answers: [pending] });
    expect(root.querySelector('nts-loading-state')).not.toBeNull();
    expect(root.querySelector('nts-render-timeline')).toBeNull();
    expect(episodes.getCalls).toEqual([
      { episodeId: EPISODE_ID, userAction: true },
      { episodeId: EPISODE_ID, userAction: false },
    ]);
    // The condition still holds after its own answer, and the effect is keyed on the value: no second read, no loop.
    fixture.detectChanges(); await fixture.whenStable();
    expect(episodes.getCalls).toHaveLength(2);
  });

  it('renders the failure panel from key copy only, without the bar, and offers the bound retry', async () => {
    const { root } = await createRenderPage(episodeDetail({
      status: 'failed', nextActions: ['retry_render', 'reopen_story'],
      renderJob: renderJobFixture({ status: 'failed', stage: 'ingesting', percent: 100, errorCode: 'render_quality_failed', finishedAt: '2026-10-18T00:00:00Z' }),
    }));
    const failure = root.querySelector('[data-testid="render-failure"]')!;
    expect(failure.getAttribute('role')).toBe('alert');
    expect(failure.textContent).toContain(errorsVi.render_quality_failed);
    expect(root.querySelector('nts-status-chip')?.getAttribute('aria-label')).toBe(coreVi.core.ui.statusChip.failed);
    // A completed-looking bar is never shown next to a failure, and the empty scene list is omitted.
    expect(root.querySelector('nts-render-timeline')).toBeNull();
    expect(root.querySelector('[data-testid="render-failure-scenes"]')).toBeNull();
    // 10.6.1-T2 binds the retry: `retry_render` is no longer filtered out of the actions, so the control is live.
    const retry = [...failure.querySelectorAll<HTMLButtonElement>('button')]
      .find((button) => button.textContent?.includes(episodesVi.render.failure.retry));
    expect(retry).toBeDefined();
    expect(retry!.disabled).toBe(false);
  });

  it('lists both skipped scenes with their reason keys, links the parsable one and keeps the empty edit action out', async () => {
    const { root } = await createRenderPage(episodeDetail({
      status: 'failed', nextActions: ['reopen_story'],
      renderJob: renderJobFixture({
        status: 'failed', stage: 'ingesting', errorCode: 'image_moderated',
        images: { needed: 3, succeeded: 1, skipped: [{ segment: 'p:3', reason: 'moderated' }, { segment: 'intro', reason: 'timeout' }] },
      }),
    }));
    expect(root.querySelector('[data-testid="render-failure"]')?.textContent).toContain(errorsVi.image_moderated);
    const rows = [...root.querySelectorAll('nts-skipped-scenes li')];
    expect(rows).toHaveLength(2);
    const link = rows[0]!.querySelector('a')!;
    expect(link.textContent).toBe('p:3');
    expect(link.getAttribute('href')).toContain(`/episodes/${EPISODE_ID}`);
    expect(link.getAttribute('href')).toContain('segment=p');
    expect(rows[0]!.textContent).toContain(episodesVi.render.images.reason.moderated);
    expect(rows[0]!.textContent).toContain(episodesVi.render.images.moderatedScene);
    // `intro` is not an address this client can parse: it renders as the unnamed-scene label, no link, no throw, and the
    // raw wire string never reaches the DOM.
    expect(rows[1]!.querySelector('a')).toBeNull();
    expect(rows[1]!.querySelector('.scene-address')?.textContent).toBe(episodesVi.render.images.sceneUnnamed.replace('{{index}}', '2'));
    expect(rows[1]!.textContent).not.toContain('intro');
    expect(rows[1]!.textContent).toContain(episodesVi.render.images.reason.timeout);
    // The reopen flow cannot run for the `failed` status, so the hint renders and the button does not.
    expect(root.querySelector('nts-render-failure button')).toBeNull();
    expect(root.querySelector('nts-render-failure .edit-hint')?.textContent).toContain(episodesVi.render.failure.editScenesHint);
  });

  it('edits the scenes through the existing reopen flow once it can run', async () => {
    const { root, fixture } = await createRenderPage(episodeDetail({
      status: 'failed', nextActions: ['reopen_story'],
      renderJob: renderJobFixture({ status: 'failed', errorCode: 'image_moderated', images: { needed: 3, succeeded: 1, skipped: [{ segment: 'p:3', reason: 'moderated' }] } }),
    }), { canReopen: true });
    const edit = [...root.querySelectorAll<HTMLButtonElement>('nts-render-failure button')]
      .find((button) => button.textContent?.includes(episodesVi.render.failure.editScenes))!;
    edit.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-reopen-dialog')).not.toBeNull();
  });

  it('moves the timeline from a live progress push with no extra read, and a later percent moves the bar up', async () => {
    const { root, fixture, episodes, realtime } = await createRenderPage(episodeDetail({
      status: 'rendering', nextActions: ['cancel'],
      renderJob: renderJobFixture({ status: 'running', stage: 'running', percent: 10, submittedAt: '2026-10-18T00:00:00Z' }),
    }));
    expect(episodes.getCalls).toHaveLength(1);
    const timeline = root.querySelector('[data-testid="render-timeline"]')!;
    expect(timeline.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('10%');
    realtime.push(EPISODE_ID, { stage: 'assembling', percent: 42 });
    fixture.detectChanges(); await fixture.whenStable();
    expect(timeline.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('42%');
    expect(timeline.textContent).toContain(episodesVi.render.subStep.assembling);
    expect([...timeline.querySelectorAll('[data-testid="render-stage"]')].map((stage) => stage.getAttribute('aria-current')))
      .toEqual([null, 'step', null]);
    realtime.push(EPISODE_ID, { stage: 'assembling', percent: 55 });
    fixture.detectChanges(); await fixture.whenStable();
    expect(timeline.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('55%');
    // D17/WR11: the live path is the socket's own signal — no repository read, not even a list call.
    expect(episodes.getCalls).toHaveLength(1);
    expect(episodes.calls).toHaveLength(0);
  });

  it('moves the active step and the percent when a live running event beats a detail still saying queued', async () => {
    const { root, fixture, episodes, realtime } = await createRenderPage(episodeDetail({
      status: 'rendering', nextActions: ['cancel'], renderJob: renderJobFixture({}),
    }));
    realtime.push(EPISODE_ID, { stage: 'running', percent: 42 });
    fixture.detectChanges(); await fixture.whenStable();
    const timeline = root.querySelector('[data-testid="render-timeline"]')!;
    expect(timeline.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('42%');
    // The live stage proves the job is running, so the step list moves with the bar (C1): a 42 % bar under an active
    // `queued` step would be the inconsistent state, and `running` is where the sub-step will appear.
    expect([...timeline.querySelectorAll('[data-testid="render-stage"]')].map((stage) => stage.getAttribute('aria-current')))
      .toEqual([null, 'step', null]);
    expect(episodes.getCalls).toHaveLength(1);
  });

  it('opens the failure panel with its generic copy for a failed job whose code the wire did not carry', async () => {
    const { root } = await createRenderPage(episodeDetail({
      status: 'failed', nextActions: ['reopen_story'],
      renderJob: renderJobFixture({
        status: 'failed', stage: 'ingesting', percent: 100, errorCode: null,
        images: { needed: 3, succeeded: 2, skipped: [{ segment: 'p:3', reason: 'timeout' }] },
      }),
    }));
    const failure = root.querySelector('[data-testid="render-failure"]')!;
    expect(failure.getAttribute('role')).toBe('alert');
    expect(failure.querySelector('h2')?.textContent).toContain(episodesVi.render.failure.title);
    expect(failure.textContent).toContain(episodesVi.render.failure.generic);
    // Not the timeline: a failed render is never shown as a progress bar, whatever the code says.
    expect(root.querySelector('nts-render-timeline')).toBeNull();
    // The scene the job did report is still listed, and the generic title offers neither action.
    expect(failure.querySelector('[data-testid="render-failure-scenes"]')).not.toBeNull();
    expect(failure.querySelector('button')).toBeNull();
  });

  it('restarts the subscription and the job when the same component is reused for another :id', async () => {
    const first = episodeDetail({ status: 'rendering', nextActions: ['cancel'], renderJob: renderJobFixture({}) });
    const second = episodeDetail({ id: SECOND_EPISODE_ID, status: 'rendering', nextActions: ['cancel'], renderJob: renderJobFixture({ renderJobId: 'job-2' }) });
    const { root, fixture, episodes, realtime, params } = await createRenderPage(first, { answers: [second] });
    expect(realtime.subscriptions.map((entry) => entry.id)).toEqual([first.id]);
    realtime.push(first.id, { stage: 'running', percent: 42 });
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('42%');
    params.next(convertToParamMap({ id: SECOND_EPISODE_ID }));
    fixture.detectChanges(); await fixture.whenStable();
    fixture.detectChanges(); await fixture.whenStable();
    expect(realtime.subscriptions.map((entry) => entry.id)).toEqual([first.id, SECOND_EPISODE_ID]);
    expect(realtime.subscriptions.filter((entry) => entry.unsubscribed)).toHaveLength(1);
    expect(episodes.getCalls).toEqual([
      { episodeId: first.id, userAction: true },
      { episodeId: SECOND_EPISODE_ID, userAction: true },
    ]);
    // The previous episode's progress does not carry over: the new job's own 0 % is what the view shows.
    expect(root.querySelector('[data-testid="render-percent"]')?.textContent?.trim()).toBe('0%');
  });

  it('leaves a story_review episode on the story editor branch with a single read', async () => {
    const { root, episodes } = await createRenderPage(episodeDetail({
      status: 'story_review', hasCurrentStory: true, currentStory: snapshot, nextActions: ['edit_story'],
    }));
    expect(root.querySelector('nts-render-timeline')).toBeNull();
    expect(root.querySelector('nts-render-failure')).toBeNull();
    expect(root.querySelector('textarea[data-segment="p:0"]')).not.toBeNull();
    expect(episodes.getCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
  });
});

describe('StoryReviewPage make video and cancel wiring (10.6.1-T2)', () => {
  afterEach(() => TestBed.resetTestingModule());

  /** An approved story with the given server actions: the one view that offers the render command. */
  const approvedDetail = (nextActions: readonly string[]): EpisodeDetail =>
    episodeDetail({ status: 'story_approved', hasCurrentStory: true, currentStory: snapshot, nextActions });

  it('renders one live Make video button in the approved header and no inert duplicate', async () => {
    const { root, fixture, episodes, story } = await createRenderPage(approvedDetail(['render', 'reopen_story']), { status: 'story_approved' });
    const buttons = [...root.querySelectorAll<HTMLButtonElement>('[data-testid="make-video"]')];
    expect(buttons).toHaveLength(1);
    // The control keeps its place in the tab order and advertises its state instead of dropping out of it (10.6.1-T2
    // focuses it back after the confirm), so enablement is `aria-disabled`, never the native attribute.
    expect(buttons[0]!.getAttribute('aria-disabled')).toBeNull();
    expect(buttons[0]!.textContent?.trim()).toBe(episodesVi.render.make.action);
    // The approved view's action bar is the page header (design 06 `#f-make-enabled`), where the primary action sits.
    // Both live controls arrive through the header's single `[actions]` slot: a second root node in the branch would
    // drop both of them from the projection (NG8011), so the slot is asserted, not just the presence in the page.
    const slot = root.querySelector('nts-page-header .page-header__actions')!;
    expect(slot).not.toBeNull();
    expect(slot.querySelector('[data-testid="make-video"]')).not.toBeNull();
    expect(slot.querySelector('.story-review-page__reopen')).not.toBeNull();
    // The read-only notice renders no second, dead control for the same command: `render` is not in its inert list.
    story.readOnly.set(true);
    fixture.detectChanges(); await fixture.whenStable();
    const notice = root.querySelector('.story-review-page__notice')!;
    expect(notice).not.toBeNull();
    expect(notice.querySelectorAll('button')).toHaveLength(0);
    expect(root.querySelectorAll('[data-testid="make-video"]')).toHaveLength(1);
    // One read only: rendering the button and its enablement never fetches the episode.
    expect(episodes.getCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
  });

  it('omits the button entirely when the server offers neither render action', async () => {
    const { root, episodes } = await createRenderPage(approvedDetail(['reopen_story']), { status: 'story_approved' });
    expect(root.querySelector('nts-make-video-button')).toBeNull();
    expect(root.querySelector('[data-testid="make-video"]')).toBeNull();
    expect(episodes.getCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
  });

  it('keeps the button present but disabled, with the keyword reason as visible linked text', async () => {
    const { root, fixture, story, renderRepo, confirm } = await createRenderPage(approvedDetail(['render']), { status: 'story_approved' });
    story.videoBlocked.set(true);
    fixture.detectChanges(); await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!;
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.hasAttribute('disabled')).toBe(false);
    const reasonId = button.getAttribute('aria-describedby');
    expect(reasonId).not.toBeNull();
    const reason = root.querySelector(`#${reasonId}`);
    expect(reason).not.toBeNull();
    expect(reason!.textContent?.trim()).toBe(episodesVi.render.make.blockedKeywords);
    // A blocked control is inert, not merely painted: no dialog, no command (the click is not trusted to a CSS state).
    button.click();
    await fixture.whenStable();
    expect(confirm).not.toHaveBeenCalled();
    expect(renderRepo.requestCalls).toHaveLength(0);
  });

  it('shows the mapped refusal once, in a single alert region', async () => {
    const { root, fixture, renderRepo } = await createRenderPage(approvedDetail(['render']), {
      status: 'story_approved', messageKey: (error) => `errors.${error.code}`, answers: [approvedDetail(['render'])],
    });
    renderRepo.willFailRender({ code: 'keywords_flagged', status: 409, kind: 'precondition', fieldErrors: {} });
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    const alerts = [...root.querySelectorAll('[role="alert"]')];
    expect(alerts).toHaveLength(1);
    expect(alerts[0]!.textContent).toContain(errorsVi.keywords_flagged);
    expect(renderRepo.requestCalls).toHaveLength(1);
  });

  it('asks once through the confirm dialog and issues exactly one render request', async () => {
    const { root, fixture, renderRepo, confirm } = await createRenderPage(approvedDetail(['render']), { status: 'story_approved', answers: [approvedDetail(['render'])] });
    renderRepo.willAcceptRender('job-9');
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(renderRepo.requestCalls).toHaveLength(1);
    // The command carries the loaded episode and its current ETag; the key itself is the store's own business.
    expect(renderRepo.requestCalls[0]!.episodeId).toBe(EPISODE_ID);
    expect(renderRepo.requestCalls[0]!.etag).toBe('"ep-1"');
  });

  it('renders the server wait after a 429 as the store countdown', async () => {
    const { root, fixture, renderRepo } = await createRenderPage(approvedDetail(['render']), {
      status: 'story_approved', messageKey: (error) => `errors.${error.code}`, answers: [approvedDetail(['render'])],
    });
    renderRepo.willFailRender({ code: 'rate_limited', status: 429, kind: 'rate_limit', fieldErrors: {}, retryAfterSec: 30 });
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    const alert = root.querySelector('.story-review-page__make-error')!;
    expect(alert.textContent).toContain(errorsVi.rate_limited);
    // One wait, announced once: the countdown lives in the single alert region and nowhere else (the plain alert is
    // not `FormErrorAlert`, which would render a second one from the same `retryAfterSec`).
    expect(alert.querySelectorAll('nts-retry-countdown')).toHaveLength(1);
    expect(root.querySelectorAll('nts-retry-countdown')).toHaveLength(1);
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
  });

  it('shows the generic copy and the trace id for a 428 the client caused', async () => {
    const { root, fixture, renderRepo } = await createRenderPage(approvedDetail(['render']), { status: 'story_approved' });
    renderRepo.willFailRender({ code: 'if_match_required', status: 428, kind: 'client_bug', fieldErrors: {}, traceId: 'trace-9f2' });
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    const alert = root.querySelector('.story-review-page__make-error')!;
    // A missing header is this client's own bug: the store answers `unknown_error` plus the correlation line, never the
    // code's own leaf (plan U6) and never the server's `detail`.
    expect(alert.textContent).toContain(errorsVi.unknown_error);
    expect(alert.textContent).not.toContain('if_match_required');
    expect(alert.textContent).toContain('trace-9f2');
    expect(alert.querySelectorAll('nts-retry-countdown')).toHaveLength(0);
  });

  it('binds the failure panel retry to the make-video command and issues exactly one render request per click', async () => {
    const { root, fixture, renderRepo, confirm } = await createRenderPage(episodeDetail({
      status: 'failed', hasCurrentStory: true, currentStory: snapshot, nextActions: ['retry_render', 'reopen_story'],
      renderJob: renderJobFixture({ status: 'failed', stage: 'ingesting', percent: 100, errorCode: 'render_quality_failed', finishedAt: '2026-10-18T00:00:00Z' }),
    }), { answers: [episodeDetail({ status: 'rendering', hasCurrentStory: true, currentStory: snapshot, nextActions: ['cancel'], renderJob: renderJobFixture({}) })] });
    const retry = [...root.querySelectorAll<HTMLButtonElement>('nts-render-failure button')]
      .find((button) => button.textContent?.includes(episodesVi.render.failure.retry))!;
    renderRepo.willAcceptRender('job-2');
    retry.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();

    // The panel's retry is the header button's own command, not a second path to the API: one confirm and one request
    // through the port, carrying the loaded episode's ETag.
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(renderRepo.requestCalls).toHaveLength(1);
    expect(renderRepo.requestCalls[0]!.episodeId).toBe(EPISODE_ID);
    expect(renderRepo.requestCalls[0]!.etag).toBe('"ep-1"');
  });

  it('drops a refusal the refetch answered with a state that offers no render at all', async () => {
    const { root, fixture, renderRepo } = await createRenderPage(approvedDetail(['render']), {
      status: 'story_approved', messageKey: (error) => `errors.${error.code}`,
      answers: [episodeDetail({ status: 'rendering', nextActions: ['cancel'], renderJob: renderJobFixture({}) })],
    });
    renderRepo.willFailRender({ code: 'concurrency_conflict', status: 409, kind: 'conflict', fieldErrors: {} });
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();

    // The refusal described the command of the approved view; the read it triggered brought the timeline in, so a banner
    // over that new state would describe something the user can no longer do (C4).
    expect(root.querySelector('nts-render-timeline')).not.toBeNull();
    expect(root.querySelector('.story-review-page__make-error')).toBeNull();
  });

  it('offers the cancel control while the job is cancellable and routes its click to one DELETE', async () => {
    const { root, fixture, renderRepo } = await createRenderPage(episodeDetail({
      status: 'rendering', hasCurrentStory: true, currentStory: snapshot, nextActions: ['cancel'], renderJob: renderJobFixture({}),
    }));
    const button = root.querySelector<HTMLButtonElement>('[data-testid="render-cancel"]');
    expect(button).not.toBeNull();
    // Same state discipline as the make-video control: an idle cancel advertises nothing and stays clickable.
    expect(button!.getAttribute('aria-disabled')).toBeNull();
    expect(button!.getAttribute('aria-busy')).toBeNull();
    button!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();

    // The control is the store's own command: the click reaches the port as the DELETE of the loaded job id.
    expect(renderRepo.cancelCalls).toEqual(['job-1']);
  });

  it('drops the previous episode make-video message when the page moves to another :id', async () => {
    const first = approvedDetail(['render']);
    const second = episodeDetail({ id: SECOND_EPISODE_ID, status: 'story_approved', hasCurrentStory: true, currentStory: snapshot, nextActions: ['render'] });
    const { root, fixture, renderRepo, episodes, params } = await createRenderPage(first, {
      status: 'story_approved', messageKey: (error) => `errors.${error.code}`, answers: [second],
    });
    renderRepo.willFailRender({ code: 'keywords_flagged', status: 409, kind: 'precondition', fieldErrors: {} });
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.story-review-page__make-error')).not.toBeNull();

    params.next(convertToParamMap({ id: SECOND_EPISODE_ID }));
    fixture.detectChanges(); await fixture.whenStable();
    fixture.detectChanges(); await fixture.whenStable();

    // The refusal described a command on the episode the user left: the new episode's view must not carry its banner.
    expect(root.querySelector('.story-review-page__make-error')).toBeNull();
    expect(root.querySelector('[data-testid="make-video"]')).not.toBeNull();
    expect(episodes.getCalls.map((call) => call.episodeId)).toEqual([first.id, SECOND_EPISODE_ID]);
  });

  it('marks the cancel control busy in flight, and a second click there emits nothing', async () => {
    const { root, fixture, renderRepo } = await createRenderPage(episodeDetail({
      status: 'rendering', hasCurrentStory: true, currentStory: snapshot, nextActions: ['cancel'], renderJob: renderJobFixture({}),
    }));
    renderRepo.deferCancel();
    const button = root.querySelector<HTMLButtonElement>('[data-testid="render-cancel"]')!;
    button.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(renderRepo.cancelCalls).toEqual(['job-1']);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(button.textContent?.trim()).toBe(episodesVi.render.cancel.busy);
    // Nothing is claimed twice: the control is inert while the store holds the request, and no status is painted.
    button.click();
    await fixture.whenStable();
    expect(renderRepo.cancelCalls).toHaveLength(1);
    expect(root.querySelector('.cancel-error')).toBeNull();
  });

  it('hides the cancel control once the job is ingesting, a status the server would refuse', async () => {
    const { root } = await createRenderPage(episodeDetail({
      status: 'rendering', hasCurrentStory: true, currentStory: snapshot, nextActions: ['cancel'],
      renderJob: renderJobFixture({ status: 'ingesting', stage: 'ingesting', percent: 100 }),
    }));
    expect(root.querySelector('nts-render-timeline')).not.toBeNull();
    expect(root.querySelector('[data-testid="render-cancel"]')).toBeNull();
  });
});

/** The final asset of a reviewed render (09.5.3-T1/WD4): metadata only, and never a media URL. */
const FINAL_VIDEO: FinalVideoAsset = {
  assetId: 'asset-1', contentType: 'video/mp4', sizeBytes: 12_345_678, durationSec: 184,
  retentionUntil: '2026-11-19T10:00:00+00:00', sha256: 'a'.repeat(64),
};

/** The status chip's copy is the core scope's (`core.ui.statusChip.<status>`), not the episodes feature's. */
const statusChipCopy: Readonly<Record<string, string>> = coreVi.core.ui.statusChip;

/**
 * The review fixture (WD4): the finished cut of an approved script, with the render-quality hints of 10 CR-12, one
 * scene the image pipeline dropped and the asset `ready` downloads. `nextActions` is the server's own permission, so
 * each spec states exactly which decision the episode offers.
 */
function reviewDetail(nextActions: readonly string[] = ['approve_video', 'reject_video', 'download'], overrides: Partial<EpisodeDetail> = {}): EpisodeDetail {
  return episodeDetail({
    status: 'video_review', hasCurrentStory: true, currentStory: snapshot, title: snapshot.content.title,
    nextActions, finalVideo: FINAL_VIDEO,
    renderJob: renderJobFixture({
      status: 'succeeded', stage: 'ingesting', percent: 100, finishedAt: '2026-10-19T00:00:00+00:00',
      hints: ['subtitle_sync_suspect', 'unknown_hint'],
      images: { needed: 3, succeeded: 2, skipped: [{ segment: 'seg-2', reason: 'moderated' }] },
    }),
    ...overrides,
  });
}

/**
 * The review-view harness (10.6.2-T1/WD4): the page with the real `EpisodeDetailStore` and the real
 * `VideoReviewStore` over the scriptable review port, progress-pushing realtime and episodes repository, so a spec
 * proves the served branch and the two counts the view promises against the fakes' own call records — one
 * `GET …/video-url` per review view open, one `GET /episodes/{id}` for the whole page. `answers` queues the reads
 * after the first one (the `EpisodeDetailStore.refetch()` a decision awaits; holding one open proves that the
 * *refetch*, not the answer, moves the branch), `urlFailure` fails the opening URL read, and `messageKey` is the leaf
 * the mapper answers a refusal with (the default ignores the code, so a spec that asserts copy names its own).
 */
async function createReviewPage(
  detail: EpisodeDetail,
  options: {
    readonly answers?: readonly (EpisodeDetail | Promise<EpisodeDetail>)[];
    readonly urlFailure?: unknown;
    readonly messageKey?: (error: AppError) => string;
  } = {},
) {
  const episodes = new FakeEpisodesRepository().scriptGet(() => Promise.resolve(detail));
  for (const answer of options.answers ?? []) episodes.scriptGet(() => Promise.resolve(answer));
  const reviewRepo = new FakeVideoReviewRepository();
  if (options.urlFailure !== undefined) reviewRepo.willFailUrl(options.urlFailure);
  const renderRepo = new FakeRenderRepository();
  const snack = fakeSnackBar();
  const realtime = new PageRealtime();
  // The real `ApprovalStore` derives its status from the episode the page owns; this fake mirrors that derivation, so a
  // refetched `story_approved` reaches the branch the template gates on `approval.status()`.
  const approval = { ...fakeApprovalStore(), status: computed(() => detailStore?.detail()?.status ?? 'story_review') };
  const confirm = vi.fn(() => ({ afterClosed: () => of(true) }));
  const params = new BehaviorSubject(convertToParamMap({ id: detail.id }));
  const query = convertToParamMap({});
  let detailStore: EpisodeDetailStore | null = null;
  const story = { ...fakeStoryStore(), load: (id: string) => detailStore?.load(id) ?? Promise.resolve(null) };
  await TestBed.configureTestingModule({
    imports: [StoryReviewPage, translations], deferBlockBehavior: DeferBlockBehavior.Manual,
    providers: [
      ...ZONELESS_TEST_PROVIDERS, provideRouter([]),
      { provide: ActivatedRoute, useValue: { paramMap: params, queryParamMap: of(query), snapshot: { paramMap: params.value, queryParamMap: query } } },
      { provide: AppErrorMapper, useValue: { messageKey: options.messageKey ?? (() => 'errors.service_unavailable'), traceFooter: () => null } },
      { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: new FakeStoryRepository() },
      { provide: RenderRepository, useValue: renderRepo },
      { provide: VideoReviewRepository, useValue: reviewRepo },
      { provide: RealtimeService, useValue: realtime },
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: ConflictDialog, useValue: fakeConflictDialogs() },
      { provide: ConfirmDialog, useValue: { confirm } },
      { provide: MatSnackBar, useValue: { open: snack.open } },
    ],
  }).overrideComponent(StoryReviewPage, {
    remove: { imports: [VersionsPanelHostComponent] }, add: { imports: [VersionsPanelProbe] },
  }).overrideComponent(StoryReviewPage, {
    set: { providers: [
      EpisodeDetailStore,
      RenderTimelineStore,
      MakeVideoStore,
      VideoReviewStore,
      { provide: StoryEditorStore, useValue: story },
      { provide: ApprovalStore, useValue: approval },
      { provide: VersionsStore, useValue: fakeVersionsStore() },
      ...fakeResearchStoreProviders(fakeResearchStore()),
    ] },
  }).compileComponents();
  const fixture = TestBed.createComponent(StoryReviewPage);
  detailStore = fixture.componentRef.injector.get(EpisodeDetailStore);
  fixture.detectChanges(); await fixture.whenStable();
  fixture.detectChanges(); await fixture.whenStable();
  // The view's signed-URL read is fired, not awaited (`loadUrl`), so its answer lands on a later microtask: flush it
  // and re-render before a spec reads the DOM it feeds (the loading skeleton, the player or the failed link).
  for (let turn = 0; turn < 3; turn++) await Promise.resolve();
  fixture.detectChanges(); await fixture.whenStable();
  return {
    fixture, root: fixture.nativeElement as HTMLElement, episodes, reviewRepo, renderRepo, realtime, confirm, snack,
    detailStore, params,
    store: fixture.componentRef.injector.get(VideoReviewStore),
    reviewDialog: (): VideoDecisionDialogComponent | undefined => fixture.debugElement
      .query(By.directive(VideoDecisionDialogComponent))?.componentInstance as VideoDecisionDialogComponent | undefined,
  };
}

/**
 * Renders every manual `@defer` block of the page (the page spec configures `DeferBlockBehavior.Manual`): the review
 * container defers the player to its viewport, and C9 keeps that the only defer — the element inside the player is
 * rendered with it.
 */
async function renderDeferBlocks(fixture: ComponentFixture<StoryReviewPage>): Promise<void> {
  for (let pass = 0; pass < 3; pass++) {
    for (const block of await fixture.getDeferBlocks()) await block.render(DeferBlockState.Complete);
    fixture.detectChanges(); await fixture.whenStable();
  }
}

describe('StoryReviewPage video review (10.6.2-T1)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('serves video_review off exactly one URL read and one episode read, with the hints as advice', async () => {
    const { root, fixture, episodes, reviewRepo } = await createReviewPage(reviewDetail());
    expect(root.querySelector('nts-video-review')).not.toBeNull();
    expect(root.querySelector('h1')?.textContent).toContain(snapshot.content.title);
    expect(root.querySelector('nts-status-chip')?.textContent).toContain(statusChipCopy['video_review']);
    // AC 1: one signed-URL read for the view open, and it is the automatic one (a background read, never USER_ACTION).
    expect(reviewRepo.urlCalls).toEqual([{ method: 'getVideoUrl', episodeId: EPISODE_ID, userAction: false }]);
    // AC 1: the review view adds no second `GET /episodes/{id}` — `EpisodeDetailStore` owns the episode, this view does not.
    expect(episodes.getCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
    // The quality hints are advice and the dropped scenes are named, and neither is a decision (C11 / 03.4.2-T1).
    expect(root.querySelector('nts-quality-notes')).not.toBeNull();
    expect(root.querySelector('[data-testid="skipped-scenes"]')).not.toBeNull();
    // AC 8: the container's decisions follow the server's own `nextActions`.
    expect(root.querySelector<HTMLButtonElement>('[data-testid="approve-video"]')?.getAttribute('aria-disabled')).toBeNull();
    expect(root.querySelector<HTMLButtonElement>('[data-testid="re-make-video"]')?.getAttribute('aria-disabled')).toBeNull();
    await renderDeferBlocks(fixture);
    expect(root.querySelector('nts-video-player')).not.toBeNull();
    // Rendering the view fetched nothing else: the player, the notes and the banner are all off the one read.
    expect(reviewRepo.urlCalls).toHaveLength(1);
    expect(episodes.getCalls).toHaveLength(1);
  });

  it('narrows the ready branch to the asset metadata and the download link, with no decision controls', async () => {
    const { root, fixture, episodes, reviewRepo } = await createReviewPage(
      reviewDetail(['download'], { status: 'ready' }),
    );
    expect(root.querySelector('nts-video-review')).not.toBeNull();
    expect(root.querySelector('nts-status-chip')?.textContent).toContain(statusChipCopy['ready']);
    // AC 11: `ready` offers the download and nothing to decide, and the little of the cut kept is metadata only.
    expect(root.querySelector('[data-testid="approve-video"]')).toBeNull();
    expect(root.querySelector('[data-testid="reject-video"]')).toBeNull();
    expect(root.querySelector('[data-testid="re-make-video"]')).toBeNull();
    expect(root.querySelector('nts-final-video-meta')).not.toBeNull();
    // The publication half of `ready` belongs to 10.7.4-T1 (plan §10 O-1): nothing of it is rendered here.
    expect(root.querySelector('nts-quality-notes')).toBeNull();
    const link = root.querySelector<HTMLAnchorElement>('[data-testid="download-mp4"]');
    expect(link?.getAttribute('href')).toBe(reviewRepo.url);
    expect(link?.getAttribute('download')).toBe(`${snapshot.content.title}.mp4`);
    expect(link?.getAttribute('rel')).toBe('noopener');
    // The link downloads the very URL this view opened with: `ready` costs no second read.
    expect(reviewRepo.urlCalls).toEqual([{ method: 'getVideoUrl', episodeId: EPISODE_ID, userAction: false }]);
    expect(episodes.getCalls).toHaveLength(1);
    await renderDeferBlocks(fixture);
  });

  it('leaves every other status to its own branch and never reads a video-url for them', async () => {
    const { root, reviewRepo } = await createReviewPage(episodeDetail({
      status: 'story_review', hasCurrentStory: true, currentStory: snapshot, nextActions: ['edit_story'],
    }));
    expect(root.querySelector('nts-video-review')).toBeNull();
    expect(root.querySelector('nts-story-editor')).not.toBeNull();
    expect(reviewRepo.urlCalls).toHaveLength(0);
  });

  it.each(['scheduled', 'published'])('keeps %s reserved: no review container and no URL read for it', async (status) => {
    const { root, reviewRepo, episodes } = await createReviewPage(reviewDetail(['download'], { status }));
    expect(root.querySelector('nts-video-review')).toBeNull();
    expect(root.querySelector('[data-testid="download-mp4"]')).toBeNull();
    expect(reviewRepo.urlCalls).toHaveLength(0);
    expect(episodes.getCalls).toHaveLength(1);
  });

  it('opens the one decision dialog from the container and routes its outputs to the store', async () => {
    const { root, fixture, store, reviewRepo, reviewDialog } = await createReviewPage(reviewDetail());
    root.querySelector<HTMLButtonElement>('[data-testid="approve-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.dialog()).toBe('approve');
    expect(reviewDialog()?.mode()).toBe('approve');
    // "Re-make video" opens that same dialog with the `re_render` target preselected (WD4/F12).
    root.querySelector<HTMLButtonElement>('[data-testid="re-make-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.dialog()).toBe('reject');
    const dialog = reviewDialog()!;
    expect(dialog.mode()).toBe('reject');
    expect(dialog.target()).toBe('re_render');
    // The radio writes the store's own target…
    dialog.targetChange.emit('story');
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.target()).toBe('story');
    expect(reviewDialog()?.target()).toBe('story');
    // …and the dismissal is the store's own close: the dialog goes and nothing was sent.
    dialog.dismissed.emit();
    fixture.detectChanges(); await fixture.whenStable();
    expect(store.dialog()).toBeNull();
    expect(reviewDialog()).toBeUndefined();
    expect(reviewRepo.approveCalls).toHaveLength(0);
    expect(reviewRepo.rejectCalls).toHaveLength(0);
  });

  it('approves through the dialog and hands the ready branch to the refetched episode, not to the answer', async () => {
    const { root, fixture, episodes, reviewRepo, store, reviewDialog } = await createReviewPage(
      reviewDetail(), { answers: [reviewDetail(['download'], { status: 'ready' })] },
    );
    root.querySelector<HTMLButtonElement>('[data-testid="approve-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    reviewDialog()!.confirmed.emit();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    // AC 11: one approve, with the episode's own ETag and no key (the route requires none) and no note.
    expect(reviewRepo.approveCalls).toEqual([
      { method: 'approveVideo', episodeId: EPISODE_ID, etag: '"ep-1"', note: undefined },
    ]);
    // AC 12: nothing is painted from the 200 — the awaited refetch is what moves the view, off the server's own status.
    expect(episodes.getCalls).toHaveLength(2);
    expect(store.dialog()).toBeNull();
    expect(root.querySelector('nts-video-review')).not.toBeNull();
    expect(root.querySelector('nts-status-chip')?.textContent).toContain(statusChipCopy['ready']);
    expect(root.querySelector('[data-testid="approve-video"]')).toBeNull();
    expect(root.querySelector('[data-testid="download-mp4"]')?.getAttribute('href')).toBe(reviewRepo.url);
    // The link minted for this open is the one the approved episode downloads: no second URL read.
    expect(store.url()?.url).toBe(reviewRepo.url);
    expect(reviewRepo.urlCalls).toHaveLength(1);
  });

  it('holds a re_render 202 until the authoritative refetch lands, and moves the branch only for it', async () => {
    let land!: (detail: EpisodeDetail) => void;
    const held = new Promise<EpisodeDetail>((resolve) => { land = resolve; });
    const { root, fixture, episodes, reviewRepo, store, reviewDialog } = await createReviewPage(
      reviewDetail(), { answers: [held] },
    );
    reviewRepo.willAcceptReject({ status: 'story_approved', etag: '"ep-3"', accepted: true });
    root.querySelector<HTMLButtonElement>('[data-testid="re-make-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    store.reason.setValue('  the last shot is unreadable  ');
    reviewDialog()!.confirmed.emit();
    await fixture.whenStable();
    // AC 12: one reject with the current ETag, the trimmed reason, the `re_render` target and a held key OBJECT.
    expect(reviewRepo.rejectCalls).toHaveLength(1);
    const call = reviewRepo.rejectCalls[0]!;
    expect(call.episodeId).toBe(EPISODE_ID);
    expect(call.etag).toBe('"ep-1"');
    expect(call.reason).toBe('the last shot is unreadable');
    expect(call.target).toBe('re_render');
    expect(call.intent.key).toMatch(/^[0-9a-f-]{36}$/);
    expect(call.intent.isSettled).toBe(true);
    // The 202 is accepted, its dialog is gone and the view still shows the cut: the refetch has not answered yet, so
    // no status, no render job id and no branch came out of the 202.
    expect(store.accepted()).toBe(true);
    expect(store.dialog()).toBeNull();
    expect(store.deciding()).toBe(true);
    expect(root.querySelector('nts-video-review')).not.toBeNull();
    expect(root.querySelector('[data-testid="make-video"]')).toBeNull();
    expect(episodes.getCalls).toHaveLength(2);
    land(reviewDetail(['render', 'reopen_story'], { status: 'story_approved' }));
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    // The server's own read is the only thing that switched the view, and it released the command.
    expect(store.deciding()).toBe(false);
    expect(root.querySelector('nts-video-review')).toBeNull();
    expect(root.querySelector('[data-testid="make-video"]')).not.toBeNull();
    expect(root.querySelector('nts-status-chip')?.textContent).toContain(statusChipCopy['story_approved']);
  });

  it('walks re_render → story_approved → make video → rendering with nothing painted ahead of the server', async () => {
    const approved = reviewDetail(['render', 'reopen_story'], { status: 'story_approved' });
    const rendering = reviewDetail(['cancel'], {
      status: 'rendering', renderJob: renderJobFixture({ status: 'running', stage: 'voice', percent: 40 }),
    });
    const { root, fixture, episodes, reviewRepo, renderRepo, store, reviewDialog, confirm } = await createReviewPage(
      reviewDetail(), { answers: [approved, rendering] },
    );
    reviewRepo.willAcceptReject({ status: 'story_approved', etag: '"ep-3"', accepted: true });
    root.querySelector<HTMLButtonElement>('[data-testid="re-make-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    store.reason.setValue('the last shot is unreadable');
    reviewDialog()!.confirmed.emit();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('nts-video-review')).toBeNull();
    expect(root.querySelector('[data-testid="make-video"]')).not.toBeNull();
    expect(root.querySelector('nts-status-chip')?.textContent).toContain(statusChipCopy['story_approved']);
    // The view that left the review branches released its own link (WD10): the signed URL dies with the branch.
    expect(store.url()).toBeNull();
    // The re-make is the approved view's own command: it opens the page's confirm, and the read after the 202 brings
    // the timeline in — again without a status written from the answer.
    renderRepo.willAcceptRender('job-9');
    root.querySelector<HTMLButtonElement>('[data-testid="make-video"]')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(renderRepo.requestCalls).toHaveLength(1);
    expect(episodes.getCalls).toHaveLength(3);
    expect(root.querySelector('nts-render-timeline')).not.toBeNull();
    expect(root.querySelector('nts-status-chip')?.textContent).toContain(statusChipCopy['rendering']);
    // One URL read for the whole walk: neither the approve nor the re-make asked for a second signed link.
    expect(reviewRepo.urlCalls).toHaveLength(1);
    expect(reviewRepo.rejectCalls).toHaveLength(1);
  });

  it('offers the retry after a failed opening read, and that retry is the only USER_ACTION URL read', async () => {
    const { root, fixture, episodes, reviewRepo } = await createReviewPage(reviewDetail(), {
      urlFailure: { code: 'service_unavailable', status: 503, kind: 'server', fieldErrors: {} },
    });
    // AC 2: the view says the link failed and offers the retry instead of a player claiming a healthy source.
    const error = root.querySelector('.url-error');
    expect(error).not.toBeNull();
    expect(root.querySelector('nts-video-player')).toBeNull();
    expect(error!.querySelector('button')?.textContent?.trim()).toBe(episodesVi.render.review.retry);
    await renderDeferBlocks(fixture);
    expect(root.querySelector('nts-video-player')).toBeNull();
    // The retry carries USER_ACTION (it is the owner's own click, so it may extend the session — WR11/D7)…
    error!.querySelector<HTMLButtonElement>('button')!.click();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(reviewRepo.urlCalls).toEqual([
      { method: 'getVideoUrl', episodeId: EPISODE_ID, userAction: false },
      { method: 'getVideoUrl', episodeId: EPISODE_ID, userAction: true },
    ]);
    // …and reads no episode: the episode is still the one `EpisodeDetailStore` already owns.
    expect(episodes.getCalls).toHaveLength(1);
    expect(root.querySelector('.url-error')).toBeNull();
    await renderDeferBlocks(fixture);
    expect(root.querySelector('nts-video-player')).not.toBeNull();
  });

  it('never reads a signed link for a ready episode with no download action, and reads one when the action appears', async () => {
    const { root, fixture, reviewRepo, detailStore, episodes } = await createReviewPage(
      reviewDetail([], { status: 'ready' }),
      { answers: [reviewDetail(['download'], { status: 'ready' })] },
    );
    // AC 11 / SP-8: `ready` without `download` shows the asset metadata and nothing that could use a link, so the
    // view reads none — no audited `GET …/video-url`, and nothing for the due check to poll.
    expect(root.querySelector('nts-video-review')).not.toBeNull();
    expect(root.querySelector('nts-final-video-meta')).not.toBeNull();
    expect(reviewRepo.urlCalls).toHaveLength(0);
    // The server starts offering the download (the read the owner's reload shows): the branch reads the link then, once.
    await detailStore.refetch();
    fixture.detectChanges(); await fixture.whenStable();
    for (let turn = 0; turn < 3; turn++) await Promise.resolve();
    fixture.detectChanges(); await fixture.whenStable();
    expect(reviewRepo.urlCalls).toEqual([{ method: 'getVideoUrl', episodeId: EPISODE_ID, userAction: false }]);
    expect(episodes.getCalls).toHaveLength(2);
    expect(root.querySelector<HTMLAnchorElement>('[data-testid="download-mp4"]')?.getAttribute('href')).toBe(reviewRepo.url);
  });

  it('announces a decision the moved-on episode refused, exactly once, as the branch it belonged to is left', async () => {
    const { root, fixture, reviewRepo, snack, reviewDialog } = await createReviewPage(
      reviewDetail(),
      { answers: [reviewDetail(['edit_story'], { status: 'story_review' })], messageKey: (error) => `errors.${error.code}` },
    );
    // The other tab decided first: this tab's approve is refused with the 409 the dialog would show…
    reviewRepo.willFailApprove({ code: 'episode_invalid_state', status: 409, kind: 'conflict', fieldErrors: {} });
    root.querySelector<HTMLButtonElement>('[data-testid="approve-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    reviewDialog()!.confirmed.emit();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    // …and the refetched status takes the branch, and the dialog carrying that key, away (F5).
    expect(root.querySelector('nts-video-review')).toBeNull();
    expect(reviewDialog()).toBeUndefined();
    const text = errorsVi.episode_invalid_state;
    expect(snack.open).toHaveBeenCalledTimes(1);
    expect(snack.open).toHaveBeenCalledWith(text, undefined, { duration: 5000, politeness: 'off' });
    expect(root.querySelector('.story-review-page__announcement')?.textContent).toContain(text);
    // Exactly once: the one region carries it once, and the refusal is retired with the branch.
    expect(root.textContent?.split(text)).toHaveLength(2);
  });

  it('does not surface a refusal that belongs to the episode the user navigated away from', async () => {
    let land!: (detail: EpisodeDetail) => void;
    const held = new Promise<EpisodeDetail>((resolve) => { land = resolve; });
    const second = episodeDetail({ id: SECOND_EPISODE_ID, status: 'story_review', hasCurrentStory: true, currentStory: snapshot, nextActions: ['edit_story'] });
    const { root, fixture, reviewRepo, snack, reviewDialog, params } = await createReviewPage(
      reviewDetail(),
      { answers: [held, second], messageKey: (error) => `errors.${error.code}` },
    );
    reviewRepo.willFailApprove({ code: 'episode_invalid_state', status: 409, kind: 'conflict', fieldErrors: {} });
    root.querySelector<HTMLButtonElement>('[data-testid="approve-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    reviewDialog()!.confirmed.emit();
    await fixture.whenStable();
    // The refused decision's read is still in flight (a `Promise` the spec holds) when the user opens another episode:
    // the branch being left is not the one the refusal belongs to, so its message is not carried onto the new page.
    params.next(convertToParamMap({ id: SECOND_EPISODE_ID }));
    fixture.detectChanges(); await fixture.whenStable();
    fixture.detectChanges(); await fixture.whenStable();
    land(reviewDetail());
    fixture.detectChanges(); await fixture.whenStable();
    expect(snack.open).not.toHaveBeenCalled();
    expect(root.querySelector('.story-review-page__announcement')?.textContent?.trim()).toBe('');
    expect(root.querySelector('nts-video-review')).toBeNull();
  });

  it('leaves the branch silently when the decision succeeded — a release without a refusal announces nothing', async () => {
    const { root, fixture, reviewRepo, snack, store, reviewDialog } = await createReviewPage(
      reviewDetail(), { answers: [reviewDetail(['edit_story'], { status: 'story_review' })] },
    );
    expect(root.querySelector('nts-video-review')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('[data-testid="reject-video"]')!.click();
    fixture.detectChanges(); await fixture.whenStable();
    store.reason.setValue('the last shot is unreadable');
    reviewDialog()!.confirmed.emit();
    await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    expect(reviewRepo.rejectCalls).toHaveLength(1);
    expect(root.querySelector('nts-video-review')).toBeNull();
    expect(snack.open).not.toHaveBeenCalled();
    expect(root.querySelector('.story-review-page__announcement')?.textContent?.trim()).toBe('');
  });
});
