import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { TRANSLOCO_LOADER, TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import type { TranslocoLoader, Translation } from '@jsverse/transloco';
import { MatSnackBar, type MatSnackBarDismiss } from '@angular/material/snack-bar';
import { BehaviorSubject, firstValueFrom, of, Subject, type Observable } from 'rxjs';
import { afterEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import { AppErrorMapper } from '@core/errors';
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
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { StoryEditorStore } from '../application/story-editor.store';
import { ApprovalStore } from '../application/approval.store';
import { VersionsStore } from '../application/versions.store';
import { fakeVersionsStore } from '../testing/fake-versions.store';
import { fakeResearchStore, fakeResearchStoreProviders } from '../testing/fake-research.store';
import { StoryRepository } from '../domain/story.repository';
import { FakeStoryRepository, attestation } from '../testing/fake-story.repository';
import { ApproveDialogComponent } from '../ui/approve-dialog/approve-dialog.component';
import { ReopenDialogComponent } from '../ui/reopen-dialog/reopen-dialog.component';
import type { SegmentAnnotation } from '../domain/annotation.model';
import type { EpisodeDetail } from '../domain/episode.model';
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
const TRANSLATIONS: Record<string, Translation> = { vi: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi, 'quality/vi': qualityVi };
/** Both languages, so a spec can switch the active language and prove which copy is frozen and which re-renders. */
const BILINGUAL: Record<string, Translation> = {
  ...TRANSLATIONS, en: coreEn, 'core/en': coreEn, 'episodes/en': episodesEn, 'errors/en': errorsEn, 'quality/en': qualityEn,
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
  hasCurrentStory: true, currentStory: snapshot, nextActions: ['edit_story'],
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
  return {
    loading: signal(false), generating: signal(false), generationError: signal(null), canGenerate: signal(false), error: signal<{ readonly status: number } | null>(null), snapshot: signal(snapshot), title: signal(snapshot.content.title), hook: signal(snapshot.content.hook.text),
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

/** `langs` overrides the loaded scopes, so a spec can leave one out and exercise its failed/absent load. */
async function createPage(options: { readonly langs?: Record<string, Translation>; readonly loader?: TranslocoLoader; readonly panel?: boolean } = {}) {
  const detailStore = fakeDetailStore();
  const storyStore = fakeStoryStore();
  const versions = fakeVersionsStore();
  const repo = new FakeStoryRepository();
  const params = new BehaviorSubject(convertToParamMap({ id: 'episode-1' }));
  const query = convertToParamMap(options.panel ? { panel: 'versions' } : {});
  const snack = fakeSnackBar();
  const dialogs = fakeConflictDialogs();
  // The single confirm the page owns: leaving a dirty draft and reloading after a conflict both go through it.
  const confirm = vi.fn(() => ({ afterClosed: () => of(true) }));
  const testbed = TestBed.configureTestingModule({
    imports: [StoryReviewPage, translationsModule(options.langs ?? TRANSLATIONS)],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'login', children: [] }]),
      { provide: ActivatedRoute, useValue: { paramMap: params, queryParamMap: of(query), snapshot: { paramMap: params.value, queryParamMap: query } } },
      { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: StoryRepository, useValue: repo },
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
      ...fakeResearchStoreProviders(fakeResearchStore()),
      ApprovalStore,
    ] },
  }).compileComponents();
  const fixture = TestBed.createComponent(StoryReviewPage);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, storyStore, detailStore, repo, params, approval: fixture.debugElement.injector.get(ApprovalStore), versions, snack, dialogs, confirm, transloco: TestBed.inject(TranslocoService) };
}

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
        { provide: ConflictDialog, useValue: { open: vi.fn() } },
        { provide: ConfirmDialog, useValue: { confirm: vi.fn() } },
      ],
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [
        { provide: EpisodeDetailStore, useValue: detailStore },
        { provide: StoryEditorStore, useValue: storyStore },
        { provide: VersionsStore, useValue: fakeVersionsStore() },
        { provide: ApprovalStore, useValue: fakeApprovalStore() },
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
        { provide: ConflictDialog, useValue: { open: vi.fn() } },
        { provide: ConfirmDialog, useValue: { confirm: vi.fn() } },
      ],
    }).overrideComponent(StoryReviewPage, {
      remove: { imports: [VersionsPanelHostComponent] },
      add: { imports: [VersionsPanelProbe] },
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [{ provide: EpisodeDetailStore, useValue: detailStore }, { provide: StoryEditorStore, useValue: storyStore }, { provide: VersionsStore, useValue: fakeVersionsStore() }, { provide: ApprovalStore, useValue: fakeApprovalStore() }, ...fakeResearchStoreProviders(fakeResearchStore())] },
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
        { provide: ConflictDialog, useValue: { open: vi.fn() } },
        { provide: ConfirmDialog, useValue: { confirm: vi.fn() } },
      ],
    }).overrideComponent(StoryReviewPage, {
      remove: { imports: [VersionsPanelHostComponent] }, add: { imports: [VersionsPanelProbe] },
    }).overrideComponent(StoryReviewPage, {
      set: { providers: [{ provide: EpisodeDetailStore, useValue: detailStore }, { provide: StoryEditorStore, useValue: storyStore }, { provide: VersionsStore, useValue: fakeVersionsStore() }, { provide: ApprovalStore, useValue: fakeApprovalStore() }, ...fakeResearchStoreProviders(fakeResearchStore())] },
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
