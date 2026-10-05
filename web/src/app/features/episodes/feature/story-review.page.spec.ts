import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
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
    loading: signal(false), error: signal(null), snapshot: signal(snapshot), title: signal(snapshot.content.title), hook: signal(snapshot.content.hook.text),
    ending: signal(snapshot.content.ending.text), meta: signal({ title: snapshot.content.title, description: snapshot.content.description, tags: snapshot.content.tags, contentWarnings: snapshot.content.content_warnings, structure: null }),
    paragraphs, hookKeywords: signal(snapshot.content.hook.keywords), endingKeywords: signal(snapshot.content.ending.keywords), hookKey: signal('hook-key'), endingKey: signal('ending-key'),
    segmentTokens: signal(new Map([['title', 3], ['hook', 2], ['p:0', 2], ['p:1', 2], ['p:2', 2], ['p:3', 2], ['ending', 1], ['meta', 5]])),
    totalTokens: signal(19), isDirty: signal(false), saving: signal(false), savedAt: signal<number | null>(null), saveDisabled: signal(true), conflict: signal(null),
    fieldErrors: signal({}), formErrors: signal<readonly string[]>([]), keywordLimitHit: signal<string | null>(null), readOnly: signal(false), invalidState: signal(false),
    load: vi.fn(() => Promise.resolve()), setText: vi.fn(), setSegmentText: vi.fn(), addKeyword: vi.fn(), updateKeyword: vi.fn(), removeKeyword: vi.fn(), setMeta: vi.fn(),
    saveVersion: vi.fn(() => Promise.resolve()), resolveConflict: vi.fn(() => Promise.resolve()), hasUnsavedChanges: vi.fn(() => false),
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('StoryReviewPage', () => {
  it('loads the route episode, renders its story and focuses the parsed segment deep link', async () => {
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
        { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.unknown_error' } },
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
  });
});
