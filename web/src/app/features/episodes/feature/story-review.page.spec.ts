import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { MatSnackBar, type MatSnackBarDismiss } from '@angular/material/snack-bar';
import { BehaviorSubject, of, Subject, type Observable } from 'rxjs';
import { afterEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import { AppErrorMapper } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '@core/session/testing/fake-session.api';
import { ConfirmDialog, ConflictDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { EpisodeDetailStore } from '../application/episode-detail.store';
import { StoryEditorStore } from '../application/story-editor.store';
import type { EpisodeDetail } from '../domain/episode.model';
import type { StorySnapshot } from '../domain/story.model';
import { StoryReviewPage } from './story-review.page';
import { SegmentEditorComponent } from '../ui/segment-editor.component';
import { StoryEditorComponent } from '../ui/story-editor.component';
import { VersionsPanelHostComponent } from './versions-panel-host.component';
import { VersionsPanelComponent } from '../ui/versions-panel/versions-panel.component';

@Component({ selector: 'nts-versions-panel-host', imports: [VersionsPanelComponent], template: '<nts-versions-panel (closed)="closed.emit()" />', changeDetection: ChangeDetectionStrategy.OnPush })
class VersionsPanelProbe {
  readonly episodeId = input.required<string>();
  readonly initialLeft = input<string | null>(null);
  readonly initialRight = input<string | null>(null);
  readonly closed = output<void>();
  readonly selectionChange = output<{ left: string | null; right: string | null }>();
}

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

const snapshot: StorySnapshot = {
  versionId: 'version-1', version: 1, etag: '"sv-version-1"', source: 'edited', wordCount: 18, qualityHints: [],
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
    detail: value.asReadonly(), episode: value.asReadonly(), loading: signal(false), error: signal(null),
    nextActions: signal<readonly string[]>(['edit_story']).asReadonly(),
    load: vi.fn(() => Promise.resolve(detail)), refetch: vi.fn(() => Promise.resolve(detail)),
  };
}

function fakeStoryStore() {
  const paragraphs = signal(snapshot.content.paragraphs.map((paragraph, index) => ({
    segKey: `paragraph-${index}`, beat: paragraph.beat, text: signal(paragraph.text), keywords: signal(paragraph.keywords), factRefs: [],
  })));
  return {
    loading: signal(false), generating: signal(false), generationError: signal(null), canGenerate: signal(false), error: signal<{ readonly status: number } | null>(null), snapshot: signal(snapshot), title: signal(snapshot.content.title), hook: signal(snapshot.content.hook.text),
    ending: signal(snapshot.content.ending.text), meta: signal({ title: snapshot.content.title, description: snapshot.content.description, tags: snapshot.content.tags, contentWarnings: snapshot.content.content_warnings, structure: null }),
    paragraphs, hookKeywords: signal(snapshot.content.hook.keywords), endingKeywords: signal(snapshot.content.ending.keywords), hookKey: signal('hook-key'), endingKey: signal('ending-key'),
    segmentTokens: signal(new Map([['title', 3], ['hook', 2], ['p:0', 2], ['p:1', 2], ['p:2', 2], ['p:3', 2], ['ending', 1], ['meta', 5]])),
    metaTokens: signal(5), totalTokens: signal(19), isDirty: signal(false), saving: signal(false), savedAt: signal<number | null>(null), saveDisabled: signal(true), conflict: signal(null),
    fieldErrors: signal({}), formErrors: signal<readonly string[]>([]), keywordLimitHit: signal<string | null>(null), readOnly: signal(false), invalidState: signal(false),
    removed: signal<unknown>(null), dirty: signal(false), structureWarning: signal(false),
    moveTo: vi.fn(() => true), removeParagraph: vi.fn(() => true), undoRemove: vi.fn((): number | null => 1), discardDraft: vi.fn(),
    load: vi.fn(() => Promise.resolve()), setText: vi.fn(), setSegmentText: vi.fn(), addKeyword: vi.fn(), updateKeyword: vi.fn(), removeKeyword: vi.fn(), setMeta: vi.fn(),
    saveVersion: vi.fn(() => Promise.resolve()), resolveConflict: vi.fn(() => Promise.resolve()), hasUnsavedChanges: vi.fn(() => false),
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

async function createPage() {
  const detailStore = fakeDetailStore();
  const storyStore = fakeStoryStore();
  const params = convertToParamMap({ id: 'episode-1' });
  const query = convertToParamMap({});
  const snack = fakeSnackBar();
  await TestBed.configureTestingModule({
    imports: [StoryReviewPage, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'login', children: [] }]),
      { provide: ActivatedRoute, useValue: { paramMap: of(params), queryParamMap: of(query), snapshot: { paramMap: params, queryParamMap: query } } },
      { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: ConflictDialog, useValue: { open: vi.fn() } },
      { provide: ConfirmDialog, useValue: { confirm: vi.fn(() => ({ afterClosed: () => of(true) })) } },
      { provide: MatSnackBar, useValue: { open: snack.open } },
    ],
  }).overrideComponent(StoryReviewPage, {
    set: { providers: [
      { provide: EpisodeDetailStore, useValue: detailStore },
      { provide: StoryEditorStore, useValue: storyStore },
    ] },
  }).compileComponents();
  const fixture = TestBed.createComponent(StoryReviewPage);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, storyStore, snack, transloco: TestBed.inject(TranslocoService) };
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
    expect(root.querySelector('[role="status"]')?.textContent).toContain(errorsVi.story_invalid_content);
    // The store retains the 412 error after Cancel but clears its conflict marker.
    storyStore.error.set({ status: 412 });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain(episodesVi.editor.unsaved);
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
      set: { providers: [{ provide: EpisodeDetailStore, useValue: detailStore }, { provide: StoryEditorStore, useValue: storyStore }] },
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
      set: { providers: [{ provide: EpisodeDetailStore, useValue: detailStore }, { provide: StoryEditorStore, useValue: storyStore }] },
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

});
