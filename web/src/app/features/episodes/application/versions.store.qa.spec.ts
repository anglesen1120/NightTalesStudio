import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { StorySnapshot } from '../domain/story.model';
import type { StoryVersionDetail } from '../domain/story-version.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { StoryRepository } from '../domain/story.repository';
import { StoryVersionRepository } from '../domain/story-version.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeStoryRepository, storyContent, storySnapshot } from '../testing/fake-story.repository';
import { FakeStoryVersionRepository, versionDetail, versionSummary } from '../testing/fake-story-version.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';
import { VersionsStore } from './versions.store';

function appError(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  const kind = status === 0 ? 'network' : status === 412 ? 'conflict' : status === 409 ? 'precondition' : status === 404 ? 'not_found' : 'unknown';
  return { code, status, kind, fieldErrors: {}, ...extra };
}
function rejected<T>(error: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- structured domain-port failure
  return Promise.reject(error);
}

interface StoreState {
  readonly store: VersionsStore;
  readonly editor: StoryEditorStore;
  readonly episodes: FakeEpisodesRepository;
  readonly stories: FakeStoryRepository;
  readonly versions: FakeStoryVersionRepository;
  readonly realtime: FakeEpisodeRealtime;
  readonly sessionGeneration: WritableSignal<number>;
}

function setup(actions: readonly string[] = ['edit_story'], snapshot: StorySnapshot | null = storySnapshot()): StoreState {
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: snapshot, hasCurrentStory: snapshot !== null, nextActions: actions, status: 'story_review' })));
  const stories = new FakeStoryRepository();
  const versions = new FakeStoryVersionRepository();
  const realtime = new FakeEpisodeRealtime();
  const sessionGeneration = signal(0);
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, EpisodeDetailStore, StoryEditorStore, VersionsStore,
    { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: stories },
    { provide: StoryVersionRepository, useValue: versions }, { provide: RealtimeService, useValue: realtime },
    { provide: SessionStore, useValue: { generation: sessionGeneration } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  const store = TestBed.inject(VersionsStore);
  const editor = TestBed.inject(StoryEditorStore);
  return { store, editor, episodes, stories, versions, realtime, sessionGeneration };
}

async function editorLoaded(snapshot?: StorySnapshot, actions: readonly string[] = ['edit_story']): Promise<StoreState> {
  const state = setup(actions, snapshot ?? storySnapshot());
  await state.editor.load(EPISODE_ID);
  return state;
}

function seedPair(state: StoreState, left: Partial<StoryVersionDetail> = {}, right: Partial<StoryVersionDetail> = {}): void {
  state.versions.seed(
    [versionSummary(2, { isCurrent: true }), versionSummary(1)],
    [versionDetail(1, left), versionDetail(2, { isCurrent: true, ...right })],
  );
}

/**
 * Exploratory/boundary coverage for `VersionsStore` (QA of 10.5.4-T1, not the author's spec).
 *
 * The author's `versions.store.spec.ts` covers the happy paths, the 412/409/428 matrix and the per-side
 * generation fence. These tests probe the concurrency and eligibility edges the UI can realistically reach:
 * a double restore, restoring from a current row, a content-less history row, a broken editor ETag, a second
 * 412 after `saveAsNew`, retrying a failed list, and switching the panel to another episode.
 */
describe('VersionsStore (QA boundaries)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  it('select_BeforeThePanelIsOpened_IsANoOp', async () => {
    const state = await editorLoaded();
    await state.store.select('v1', 'left');
    expect(state.versions.detailCalls).toHaveLength(0);
    expect(state.store.leftId()).toBeNull();
    expect(state.store.loaded()).toBe(false);
  });

  it('open_AfterAListFailure_RetriesOnTheNextOpen', async () => {
    const state = await editorLoaded();
    state.versions.willFailList(appError('network_error', 0));
    await state.store.open(EPISODE_ID);
    expect(state.store.error()?.kind).toBe('network');
    expect(state.store.loaded()).toBe(false);

    state.versions.seed([], [versionDetail(1), versionDetail(2, { isCurrent: true })]);
    state.versions.scriptList(() => Promise.resolve([versionSummary(2, { isCurrent: true }), versionSummary(1)]));
    await state.store.open(EPISODE_ID);
    expect(state.store.loaded()).toBe(true);
    expect(state.store.error()).toBeNull();
    expect(state.versions.detailCalls).toHaveLength(2);
  });

  it('retryList_AfterAFailure_ReloadsAndAppliesTheDefaultSelection', async () => {
    const state = await editorLoaded();
    state.versions.willFailList(appError('not_found', 404));
    await state.store.open(EPISODE_ID);
    expect(state.store.error()?.status).toBe(404);

    state.versions.seed([], [versionDetail(1), versionDetail(2, { isCurrent: true })]);
    state.versions.scriptList(() => Promise.resolve([versionSummary(2, { isCurrent: true }), versionSummary(1)]));
    await state.store.retryList();
    expect(state.store.loaded()).toBe(true);
    expect(state.store.leftId()).toBe('v1');
    expect(state.store.rightId()).toBe('v2');
  });

  it('open_AnotherEpisode_ReplacesTheSelection', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(1, { isCurrent: true })], [versionDetail(1, { isCurrent: true })]);
    await state.store.open(EPISODE_ID);
    expect(state.store.leftId()).toBeNull();
    expect(state.store.rightId()).toBe('v1');

    state.versions.seed([versionSummary(2, { isCurrent: true }), versionSummary(1)], [versionDetail(1), versionDetail(2, { isCurrent: true })]);
    await state.store.open('episode-2');
    expect(state.versions.listCalls.map((call) => call.episodeId)).toEqual([EPISODE_ID, 'episode-2']);
    expect(state.store.leftId()).toBe('v1');
    expect(state.store.rightId()).toBe('v2');
  });

  it('restore_SecondCallWhileInFlight_IsUnavailableAndWritesOnce', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('internal_error', 500)));

    const first = state.store.restoreAsNewVersion();
    const second = await state.store.restoreAsNewVersion();

    expect(second).toBe('unavailable');
    expect(await first).toBe('failed');
    expect(state.stories.calls).toHaveLength(1);
  });

  it('restore_LeftIsTheCurrentVersion_IsUnavailableWithoutAWrite', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID, { left: 'v2', right: 'v1' });
    expect(state.store.left()?.isCurrent).toBe(true);
    expect(state.store.canRestore()).toBe(false);
    expect(await state.store.restoreAsNewVersion()).toBe('unavailable');
    expect(state.stories.calls).toHaveLength(0);
  });

  it('restore_AHistoryRowWithoutStoryContent_IsUnavailableWithoutAWrite', async () => {
    const state = await editorLoaded();
    seedPair(state, { content: null });
    await state.store.open(EPISODE_ID);
    expect(state.store.left()?.content).toBeNull();
    expect(state.store.canRestore()).toBe(false);
    expect(await state.store.restoreAsNewVersion()).toBe('unavailable');
    expect(state.stories.calls).toHaveLength(0);
  });

  it('restore_AReadOnlyEditor_IsUnavailableWithoutAWrite', async () => {
    const state = await editorLoaded(undefined, []);
    seedPair(state);
    await state.store.open(EPISODE_ID);
    expect(state.editor.readOnly()).toBe(true);
    expect(state.store.canRestore()).toBe(false);
    expect(await state.store.restoreAsNewVersion()).toBe('unavailable');
    expect(state.stories.calls).toHaveLength(0);
  });

  it('restore_AMalformedEditorEtag_IsUnavailableAndWarnsInDev', async () => {
    const state = await editorLoaded(storySnapshot({ etag: 'not-an-etag' }));
    seedPair(state);
    await state.store.open(EPISODE_ID);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      expect(state.store.canRestore()).toBe(true);
      expect(await state.store.restoreAsNewVersion()).toBe('unavailable');
      expect(state.stories.calls).toHaveLength(0);
      expect(warn).toHaveBeenCalledWith('if_match_required', '-');
    } finally {
      warn.mockRestore();
    }
  });

  it('resolveConflict_SaveAsNew_WhenTheRetryConflictsAgain_KeepsTheFlagAndStops', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    expect(await state.store.restoreAsNewVersion()).toBe('conflict');
    expect(state.store.pendingConflict()).toBe(true);

    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"' }), nextActions: ['edit_story'], status: 'story_review' })));
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.resolveConflict('saveAsNew');
    expect(state.store.pendingConflict()).toBe(true);
    expect(state.store.restoreError()).toBeNull();
    expect(state.stories.calls).toHaveLength(2);
  });

  it('diff_IdenticalBodiesInDifferentVersions_ReportsNoDifferences', async () => {
    const state = await editorLoaded();
    seedPair(state, { body: 'the very same words' }, { body: 'the very same words' });
    await state.store.open(EPISODE_ID);
    const diff = state.store.diff();
    expect(diff).not.toBeNull();
    expect(diff?.added).toBe(0);
    expect(diff?.removed).toBe(0);
    expect(diff?.truncated).toBe(false);
  });

  it('restore_SendsTheRestoredStoryAsTheNewContent_NotTheDraft', async () => {
    const state = await editorLoaded();
    state.editor.setSegmentText('title', 'Draft that must not be written');
    vi.advanceTimersByTime(150);
    const restored = storyContent({ title: 'Restored title' });
    seedPair(state, { content: restored });
    await state.store.open(EPISODE_ID);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-new"', version: 3, content: restored }), hasCurrentStory: true, nextActions: ['edit_story'], status: 'story_review' })));
    state.versions.scriptList(() => Promise.resolve([versionSummary(3, { isCurrent: true, source: 'edited' }), versionSummary(2), versionSummary(1)]));
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(3, { isCurrent: true, source: 'edited', content: restored })));
    expect(await state.store.restoreAsNewVersion()).toBe('restored');
    expect(state.stories.calls[0]?.content).toEqual(restored);
    expect(state.stories.calls[0]?.etag).toBe(storySnapshot().etag);
    expect(state.editor.isDirty()).toBe(false);
    expect(state.editor.title()).toBe('Restored title');
  });
});
