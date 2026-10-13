import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
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
import { StoryReviewPage } from '../feature/story-review.page';
import { dirtyStoryGuard } from './dirty-story.guard';

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

function fakeStoryStore(dirty: boolean) {
  return {
    loading: signal(false), generating: signal(false), generationError: signal(null), canGenerate: signal(false), error: signal<{ readonly status: number } | null>(null),
    snapshot: signal(snapshot), title: signal(snapshot.content.title), hook: signal(snapshot.content.hook.text), ending: signal(snapshot.content.ending.text),
    meta: signal({ title: snapshot.content.title, description: snapshot.content.description, tags: snapshot.content.tags, contentWarnings: snapshot.content.content_warnings, structure: null }),
    paragraphs: signal(snapshot.content.paragraphs.map((paragraph, index) => ({ segKey: `paragraph-${index}`, beat: paragraph.beat, text: signal(paragraph.text), keywords: signal(paragraph.keywords), factRefs: [] }))),
    hookKeywords: signal(snapshot.content.hook.keywords), endingKeywords: signal(snapshot.content.ending.keywords), hookKey: signal('hook-key'), endingKey: signal('ending-key'),
    segmentTokens: signal(new Map([['title', 3], ['hook', 2], ['p:0', 2], ['p:1', 2], ['p:2', 2], ['p:3', 2], ['ending', 1], ['meta', 5]])),
    metaTokens: signal(5), totalTokens: signal(19), isDirty: signal(dirty), saving: signal(false), savedAt: signal<number | null>(null), saveDisabled: signal(!dirty), conflict: signal(null),
    fieldErrors: signal({}), formErrors: signal<readonly string[]>([]), keywordLimitHit: signal<string | null>(null), readOnly: signal(false), invalidState: signal(false),
    removed: signal(null), dirty: signal(dirty), structureWarning: signal(false),
    hasUnsavedChanges: vi.fn(() => dirty), discardDraft: vi.fn(), moveTo: vi.fn(() => true), removeParagraph: vi.fn(() => true), undoRemove: vi.fn(() => null),
    load: vi.fn(() => Promise.resolve()), setText: vi.fn(), setSegmentText: vi.fn(), addKeyword: vi.fn(), updateKeyword: vi.fn(), removeKeyword: vi.fn(), setMeta: vi.fn(),
    saveVersion: vi.fn(() => Promise.resolve()), resolveConflict: vi.fn(() => Promise.resolve()),
  };
}

const route = {} as ActivatedRouteSnapshot;
const state = {} as RouterStateSnapshot;

async function setup(dirty: boolean, answer: boolean | null) {
  const detailStore = fakeDetailStore();
  const storyStore = fakeStoryStore(dirty);
  const params = convertToParamMap({ id: 'episode-1' });
  const query = convertToParamMap({});
  const leave = vi.fn(() => ({ afterClosed: () => of(answer) }));
  await TestBed.configureTestingModule({
    imports: [StoryReviewPage, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { paramMap: of(params), queryParamMap: of(query), snapshot: { paramMap: params, queryParamMap: query } } },
      { provide: AppErrorMapper, useValue: { messageKey: () => 'errors.story_invalid_content' } },
      { provide: ConflictDialog, useValue: { open: vi.fn() } },
      { provide: ConfirmDialog, useValue: { confirm: leave } },
      { provide: MatSnackBar, useValue: { open: vi.fn(() => ({ onAction: () => of(undefined), dismiss: vi.fn() })) } },
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
  return { page: fixture.componentInstance, leave, storyStore, fixture };
}

afterEach(() => TestBed.resetTestingModule());

describe('dirtyStoryGuard', () => {
  it('cleanDraft_ReturnsTrue_WithoutOpeningTheDialog', async () => {
    const { page, leave, storyStore } = await setup(false, null);
    await expect(dirtyStoryGuard(page, route, state, state)).resolves.toBe(true);
    expect(leave).not.toHaveBeenCalled();
    expect(storyStore.discardDraft).not.toHaveBeenCalled();
  });

  it('dirtyDraft_Leave_ReturnsTrue_AndDiscardsTheDraft', async () => {
    const { page, leave, storyStore } = await setup(true, true);
    await expect(dirtyStoryGuard(page, route, state, state)).resolves.toBe(true);
    expect(leave).toHaveBeenCalledWith(expect.objectContaining({
      titleKey: 'episodes.dirty.title', bodyKey: 'episodes.dirty.body', confirmLabelKey: 'episodes.dirty.leave', cancelLabelKey: 'episodes.dirty.stay', destructive: true,
    }), expect.anything());
    expect(storyStore.discardDraft).toHaveBeenCalledTimes(1);
  });

  it('dirtyDraft_Stay_ReturnsFalse_AndKeepsTheDraft', async () => {
    const { page, storyStore } = await setup(true, false);
    await expect(dirtyStoryGuard(page, route, state, state)).resolves.toBe(false);
    expect(storyStore.discardDraft).not.toHaveBeenCalled();
    expect(storyStore.hasUnsavedChanges()).toBe(true);
  });

  it('stay_DoesNotMutateTheStore', async () => {
    const { page, storyStore } = await setup(true, false);
    await expect(dirtyStoryGuard(page, route, state, state)).resolves.toBe(false);
    expect(storyStore.discardDraft).not.toHaveBeenCalled();
    expect(storyStore.moveTo).not.toHaveBeenCalled();
    expect(storyStore.removeParagraph).not.toHaveBeenCalled();
    expect(storyStore.undoRemove).not.toHaveBeenCalled();
    expect(storyStore.saveVersion).not.toHaveBeenCalled();
    expect(storyStore.paragraphs()).toHaveLength(4);
    expect(storyStore.hasUnsavedChanges()).toBe(true);
  });

  it('concurrentCalls_ShareOnePrompt', async () => {
    const { page, leave } = await setup(true, true);
    const [first, second] = await Promise.all([dirtyStoryGuard(page, route, state, state), dirtyStoryGuard(page, route, state, state)]);
    expect(first).toBe(true);
    expect(second).toBe(true);
    expect(leave).toHaveBeenCalledTimes(1);
  });
});
