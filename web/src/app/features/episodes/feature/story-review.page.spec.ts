import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { BehaviorSubject, of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppErrorMapper } from '@core/errors';
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

@Component({ selector: 'nts-versions-panel-host', template: '', changeDetection: ChangeDetectionStrategy.OnPush })
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
  id: 'episode-1', seriesId: 'series-1', status: 'story_review', etag: '"ep-1"', generation: null,
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
    load: vi.fn(() => Promise.resolve()), setText: vi.fn(), setSegmentText: vi.fn(), addKeyword: vi.fn(), updateKeyword: vi.fn(), removeKeyword: vi.fn(), setMeta: vi.fn(),
    saveVersion: vi.fn(() => Promise.resolve()), resolveConflict: vi.fn(() => Promise.resolve()), hasUnsavedChanges: vi.fn(() => false),
  };
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

});
