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
// The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
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

/** Editor page mounted with a current story; the versions panel has NOT been opened yet (no version request). */
async function editorLoaded(): Promise<StoreState> {
  const state = setup();
  await state.editor.load(EPISODE_ID);
  return state;
}

function seedPair(state: StoreState, left: Partial<StoryVersionDetail> = {}, right: Partial<StoryVersionDetail> = {}): void {
  state.versions.seed(
    [versionSummary(2, { isCurrent: true }), versionSummary(1)],
    [versionDetail(1, left), versionDetail(2, { isCurrent: true, ...right })],
  );
}

describe('VersionsStore', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  it('open_MakesNoRequestUntilOpened_ThenDefaultsToPreviousAndCurrent', async () => {
    const state = await editorLoaded();
    expect(state.versions.listCalls).toHaveLength(0);
    seedPair(state);
    await state.store.open(EPISODE_ID);
    expect(state.store.loaded()).toBe(true);
    expect(state.store.versions()).toHaveLength(2);
    expect(state.store.leftId()).toBe('v1');
    expect(state.store.rightId()).toBe('v2');
    expect(state.store.diff()).not.toBeNull();
    expect(state.versions.listCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
    expect(state.versions.detailCalls).toEqual([
      { episodeId: EPISODE_ID, versionId: 'v1', userAction: true },
      { episodeId: EPISODE_ID, versionId: 'v2', userAction: true },
    ]);
  });

  it('open_BothIdsInTheQuery_MakesExactlyTwoDetailCalls', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(1), versionSummary(2)], [versionDetail(1), versionDetail(2)]);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    expect(state.versions.listCalls).toHaveLength(1);
    expect(state.versions.detailCalls).toHaveLength(2);
    expect(state.versions.detailCalls.map((call) => call.versionId)).toEqual(['v1', 'v2']);
  });

  it('open_OneNullId_FetchesOnlyThePresentSide', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(1), versionSummary(2)], [versionDetail(1), versionDetail(2)]);
    await state.store.open(EPISODE_ID, { left: null, right: 'v2' });
    expect(state.versions.detailCalls).toEqual([{ episodeId: EPISODE_ID, versionId: 'v2', userAction: true }]);
    expect(state.store.leftId()).toBeNull();
    expect(state.store.right()?.versionId).toBe('v2');
  });

  it('open_SameEpisodeTwice_RefetchesOnlyTheList', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    await state.store.open(EPISODE_ID);
    expect(state.versions.listCalls).toHaveLength(2);
    expect(state.versions.detailCalls).toHaveLength(2);
  });

  it('open_AfterThePanelReopens_KeepsTheSelectionAndRefreshesTheFlags', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    expect(state.versions.detailCalls).toHaveLength(2);
    state.versions.scriptList(() => Promise.resolve([versionSummary(2, { isCurrent: true, approved: true }), versionSummary(1)]));
    await state.store.open(EPISODE_ID);
    expect(state.versions.listCalls).toHaveLength(2);
    expect(state.versions.detailCalls).toHaveLength(2);
    expect(state.store.leftId()).toBe('v1');
    expect(state.store.rightId()).toBe('v2');
    expect(state.store.right()?.approved).toBe(true);
    expect(state.store.right()?.isCurrent).toBe(true);
    expect(state.store.left()?.isCurrent).toBe(false);
  });

  it('open_WhileRestoring_DoesNotRefetchAnything', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.restoreAsNewVersion();
    state.stories.scriptSave(() => rejected(appError('internal_error', 500)));
    const restoring = state.store.restoreAsNewVersion();
    await state.store.open(EPISODE_ID);
    expect(state.versions.listCalls).toHaveLength(1);
    expect(await restoring).toBe('failed');
  });

  it('open_NoVersions_ShowsEmptyNotAnError', async () => {
    const state = await editorLoaded();
    state.versions.seed([]);
    await state.store.open(EPISODE_ID);
    expect(state.store.empty()).toBe(true);
    expect(state.store.loaded()).toBe(true);
    expect(state.store.error()).toBeNull();
    expect(state.versions.detailCalls).toHaveLength(0);
  });

  it('open_404_SetsTheListErrorWithoutEmptyState', async () => {
    const state = await editorLoaded();
    state.versions.willFailList(appError('not_found', 404));
    await state.store.open(EPISODE_ID);
    expect(state.store.error()?.status).toBe(404);
    expect(state.store.loaded()).toBe(false);
    expect(state.store.empty()).toBe(false);
  });

  it('open_Network_MapsToNetworkError', async () => {
    const state = await editorLoaded();
    state.versions.willFailList(appError('network_error', 0));
    await state.store.open(EPISODE_ID);
    expect(state.store.error()?.code).toBe('network_error');
    expect(state.store.error()?.kind).toBe('network');
  });

  it('select_FetchesOnlyTheChangedSideAndKeepsTheOther', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(3), versionSummary(2), versionSummary(1)], [versionDetail(1), versionDetail(2), versionDetail(3)]);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    expect(state.versions.detailCalls).toHaveLength(2);
    await state.store.select('v3', 'left');
    expect(state.versions.detailCalls).toHaveLength(3);
    expect(state.versions.detailCalls[2]).toMatchObject({ versionId: 'v3' });
    expect(state.store.leftId()).toBe('v3');
    expect(state.store.right()?.versionId).toBe('v2');
  });

  it('select_WithoutDefaults_MakesOneDetailCallPerSelection', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(1), versionSummary(2)], [versionDetail(1), versionDetail(2)]);
    await state.store.open(EPISODE_ID, { left: null, right: null });
    expect(state.versions.detailCalls).toHaveLength(0);
    await state.store.select('v1', 'left');
    expect(state.versions.detailCalls).toHaveLength(1);
    await state.store.select('v2', 'right');
    expect(state.versions.detailCalls).toHaveLength(2);
  });

  it('select_RapidChanges_LastWriteWinsPerSide', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(4), versionSummary(3), versionSummary(2), versionSummary(1)], [versionDetail(1), versionDetail(2)]);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    const slow = deferred<StoryVersionDetail>();
    state.versions.scriptDetail(() => slow.promise);
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(4)));
    const first = state.store.select('v3', 'left');
    await state.store.select('v4', 'left');
    expect(state.store.leftId()).toBe('v4');
    expect(state.store.left()?.versionId).toBe('v4');
    slow.resolve(versionDetail(3));
    await first;
    expect(state.store.leftId()).toBe('v4');
    expect(state.store.left()?.versionId).toBe('v4');
  });

  it('select_SameVersionOnBothSides_IsFlaggedAndDisablesTheDiff', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(1)], [versionDetail(1)]);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v1' });
    expect(state.store.sameSelected()).toBe(true);
    expect(state.store.diff()).toBeNull();
    expect(state.store.canRestore()).toBe(false);
  });

  it('retrySide_ReRunsOnlyTheFailedSide', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(2, { isCurrent: true }), versionSummary(1)], [versionDetail(1)]);
    state.versions.willFailDetail('v2', appError('network_error', 0));
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    expect(state.store.sideError()?.side).toBe('right');
    expect(state.store.left()?.versionId).toBe('v1');
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(2, { isCurrent: true })));
    await state.store.retrySide('right');
    expect(state.store.sideError()).toBeNull();
    expect(state.store.right()?.versionId).toBe('v2');
    expect(state.versions.detailCalls).toHaveLength(3);
  });

  it('select_404_RemovesTheRowAndReloadsTheList', async () => {
    const state = await editorLoaded();
    state.versions.scriptList(() => Promise.resolve([versionSummary(2, { isCurrent: true }), versionSummary(1)]));
    state.versions.scriptList(() => Promise.resolve([versionSummary(2, { isCurrent: true })]));
    state.versions.details.set('v2', versionDetail(2, { isCurrent: true }));
    state.versions.willFailDetail('v1', appError('version_not_found', 404));
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    expect(state.store.versions().map((version) => version.versionId)).toEqual(['v2']);
    expect(state.store.leftId()).toBeNull();
    expect(state.store.left()).toBeNull();
    expect(state.store.sideError()).toBeNull();
    expect(state.store.rightId()).toBe('v2');
    expect(state.store.right()?.versionId).toBe('v2');
    expect(state.versions.listCalls).toHaveLength(2);
    expect(state.versions.detailCalls).toHaveLength(2);
  });

  it('select_404_WhenTheEpisodeIsGone_LeavesTheListErrorState', async () => {
    const state = await editorLoaded();
    state.versions.scriptList(() => Promise.resolve([versionSummary(2, { isCurrent: true }), versionSummary(1)]));
    state.versions.willFailList(appError('not_found', 404));
    state.versions.willFailDetail('v1', appError('not_found', 404));
    state.versions.details.set('v2', versionDetail(2, { isCurrent: true }));
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    expect(state.store.error()?.status).toBe(404);
    expect(state.store.versions().map((version) => version.versionId)).toEqual(['v2']);
    expect(state.store.sideError()).toBeNull();
  });

  it('restore_400_LogsTheMappedCodeAndTraceIdInDev', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      state.stories.scriptSave(() => rejected(appError('story_invalid_content', 400, { traceId: 'trace-42' })));
      expect(await state.store.restoreAsNewVersion()).toBe('failed');
      expect(state.store.restoreError()?.code).toBe('story_invalid_content');
      expect(warn).toHaveBeenCalledWith('story_restore_failed', 'story_invalid_content', 'trace-42');
      state.stories.scriptSave(() => rejected(appError('internal_error', 500)));
      expect(await state.store.restoreAsNewVersion()).toBe('failed');
      expect(warn).toHaveBeenCalledWith('story_restore_failed', 'internal_error');
    } finally {
      warn.mockRestore();
    }
  });

  it('restore_SendsTheOldContentWithTheCurrentEtag_ReloadsAndClearsDirty', async () => {
    const state = await editorLoaded();
    state.editor.setSegmentText('title', 'Dirty title');
    vi.advanceTimersByTime(150);
    expect(state.editor.isDirty()).toBe(true);
    const restored = storyContent({ title: 'Old title' });
    state.versions.seed(
      [versionSummary(2, { isCurrent: true }), versionSummary(1)],
      [versionDetail(1, { content: restored }), versionDetail(2, { isCurrent: true })],
    );
    await state.store.open(EPISODE_ID);
    expect(state.store.canRestore()).toBe(true);
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-new"', version: 3, content: restored, source: 'edited' })));
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-new"', version: 3, content: restored, source: 'edited' }), hasCurrentStory: true, nextActions: ['edit_story'], status: 'story_review' })));
    state.versions.scriptList(() => Promise.resolve([versionSummary(3, { isCurrent: true, source: 'edited' }), versionSummary(2), versionSummary(1)]));
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(3, { isCurrent: true, source: 'edited', content: restored })));
    const outcome = await state.store.restoreAsNewVersion();
    expect(outcome).toBe('restored');
    expect(state.stories.calls[0]).toMatchObject({ episodeId: EPISODE_ID, etag: storySnapshot().etag, userAction: true });
    expect(state.stories.calls[0]?.content).toEqual(restored);
    expect(state.editor.isDirty()).toBe(false);
    expect(state.editor.title()).toBe('Old title');
    expect(state.store.versions()[0]?.versionId).toBe('v3');
    expect(state.store.rightId()).toBe('v3');
    expect(state.store.restoring()).toBe(false);
  });

  it('restore_EditorReloadAndSameEpisodeReopen_PreserveTheOperationAndCurrentSelection', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    const reloaded = deferred<ReturnType<typeof episodeDetail>>();
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ version: 3, etag: '"sv-new"' })));
    state.episodes.scriptGet(() => reloaded.promise);
    state.versions.scriptList(() => Promise.resolve([versionSummary(3, { isCurrent: true }), versionSummary(2), versionSummary(1)]));
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(3, { isCurrent: true })));
    const restoring = state.store.restoreAsNewVersion();
    await Promise.resolve();
    expect(state.editor.loading()).toBe(true);
    // A transient host remount calls open again, but must not replace the ongoing restore or refetch the pair.
    await state.store.open(EPISODE_ID);
    expect(state.versions.listCalls).toHaveLength(1);
    expect(state.versions.detailCalls).toHaveLength(2);
    reloaded.resolve(episodeDetail({ currentStory: storySnapshot({ version: 3, etag: '"sv-new"' }), nextActions: ['edit_story'], status: 'story_review' }));
    expect(await restoring).toBe('restored');
    expect(state.store.restoredVersion()).toBe(3);
    expect(state.store.leftId()).toBe('v1');
    expect(state.store.rightId()).toBe('v3');
    state.versions.scriptList(() => Promise.resolve([versionSummary(3, { isCurrent: true }), versionSummary(2), versionSummary(1)]));
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    // An idle reopen re-reads the LIST only: the restore's selection and its already-loaded details survive.
    expect(state.versions.listCalls).toHaveLength(3);
    expect(state.versions.detailCalls).toHaveLength(3);
    expect(state.store.rightId()).toBe('v3');
    expect(state.store.versions()).toHaveLength(3);
    expect(state.store.consumeRestored()).toBe(3);
    expect(state.store.consumeRestored()).toBeNull();
    expect(state.store.restoredVersion()).toBeNull();
  });

  it('restore_412_KeepsTheDraftAndOpensTheConflictFlag', async () => {
    const state = await editorLoaded();
    state.editor.setSegmentText('title', 'Dirty title');
    vi.advanceTimersByTime(150);
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    const outcome = await state.store.restoreAsNewVersion();
    expect(outcome).toBe('conflict');
    expect(state.store.pendingConflict()).toBe(true);
    expect(state.store.restoreError()).toBeNull();
    expect(state.editor.title()).toBe('Dirty title');
    expect(state.editor.isDirty()).toBe(true);
    expect(state.stories.calls).toHaveLength(1);
  });

  it('restore_409EpisodeInvalidState_SetsNeedsReopenAndRefetchesTheEpisode', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('episode_invalid_state', 409)));
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['reopen_story'], status: 'story_approved' })));
    const outcome = await state.store.restoreAsNewVersion();
    expect(outcome).toBe('needs_reopen');
    expect(state.store.needsReopen()).toBe(true);
    expect(state.store.restoreError()).toBeNull();
    expect(state.episodes.getCalls).toHaveLength(2);
  });

  it('restore_428_SetsNoUserVisibleErrorAndWarnsInDev', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    state.stories.scriptSave(() => rejected(appError('if_match_required', 428)));
    try {
      const outcome = await state.store.restoreAsNewVersion();
      expect(outcome).toBe('failed');
      expect(state.store.restoreError()).toBeNull();
      expect(state.store.pendingConflict()).toBe(false);
      expect(warn).toHaveBeenCalled();
    } finally {
      warn.mockRestore();
    }
  });

  it('restore_5xx_SetsTheRestoreError', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('internal_error', 500)));
    const outcome = await state.store.restoreAsNewVersion();
    expect(outcome).toBe('failed');
    expect(state.store.restoreError()?.status).toBe(500);
  });

  it('restore_NotEligible_IsUnavailableWithoutAWrite', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(1, { isCurrent: true })], [versionDetail(1, { isCurrent: true })]);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v1' });
    expect(await state.store.restoreAsNewVersion()).toBe('unavailable');
    expect(state.stories.calls).toHaveLength(0);
  });

  it('resolveConflict_Reload_RefreshesTheEditorAndTheListWithoutAWrite', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.restoreAsNewVersion();
    expect(state.store.pendingConflict()).toBe(true);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-new"', version: 3 }), hasCurrentStory: true, nextActions: ['edit_story'], status: 'story_review' })));
    state.versions.scriptList(() => Promise.resolve([versionSummary(3, { isCurrent: true }), versionSummary(2), versionSummary(1)]));
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(3, { isCurrent: true })));
    await state.store.resolveConflict('reload');
    expect(state.store.pendingConflict()).toBe(false);
    expect(state.store.restoreError()).toBeNull();
    expect(state.store.versions()).toHaveLength(3);
    expect(state.store.leftId()).toBe('v1');
    expect(state.store.rightId()).toBe('v3');
    expect(state.stories.calls).toHaveLength(1);
  });

  it('resolveConflict_SaveAsNew_RefetchesTheEtagThenRetriesOnce', async () => {
    const state = await editorLoaded();
    const restored = storyContent({ title: 'Old title' });
    state.versions.seed(
      [versionSummary(2, { isCurrent: true }), versionSummary(1)],
      [versionDetail(1, { content: restored }), versionDetail(2, { isCurrent: true })],
    );
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.restoreAsNewVersion();
    expect(state.store.pendingConflict()).toBe(true);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"', content: storyContent({ title: 'Other writer' }) }), nextActions: ['edit_story'], status: 'story_review' })));
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-new"', version: 3, content: restored, source: 'edited' })));
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-new"', version: 3, content: restored }), hasCurrentStory: true, nextActions: ['edit_story'], status: 'story_review' })));
    state.versions.scriptList(() => Promise.resolve([versionSummary(3, { isCurrent: true, source: 'edited' }), versionSummary(2), versionSummary(1)]));
    state.versions.scriptDetail(() => Promise.resolve(versionDetail(3, { isCurrent: true })));
    await state.store.resolveConflict('saveAsNew');
    expect(state.stories.calls[1]).toMatchObject({ etag: '"sv-fresh"', userAction: true });
    expect(state.stories.calls[1]?.content).toEqual(restored);
    expect(state.store.pendingConflict()).toBe(false);
    expect(state.stories.calls).toHaveLength(2);
  });

  it('resolveConflict_Cancel_ClearsTheConflictFlagOnly', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.restoreAsNewVersion();
    await state.store.resolveConflict('cancel');
    expect(state.store.pendingConflict()).toBe(false);
    expect(state.stories.calls).toHaveLength(1);
  });

  it('reset_ClearsEverySignal', async () => {
    const state = await editorLoaded();
    seedPair(state);
    await state.store.open(EPISODE_ID);
    state.store.reset();
    expect(state.store.versions()).toEqual([]);
    expect(state.store.loaded()).toBe(false);
    expect(state.store.empty()).toBe(false);
    expect(state.store.leftId()).toBeNull();
    expect(state.store.rightId()).toBeNull();
    expect(state.store.left()).toBeNull();
    expect(state.store.right()).toBeNull();
    expect(state.store.diff()).toBeNull();
    expect(state.store.loading()).toBe(false);
    expect(state.store.loadingSide()).toBeNull();
    expect(state.store.sideError()).toBeNull();
    expect(state.store.error()).toBeNull();
    expect(state.store.restoring()).toBe(false);
    expect(state.store.pendingConflict()).toBe(false);
    expect(state.store.needsReopen()).toBe(false);
    expect(state.store.restoreError()).toBeNull();
  });

  it('reset_MakesALateDetailResponseInert', async () => {
    const state = await editorLoaded();
    state.versions.seed([versionSummary(3), versionSummary(2, { isCurrent: true }), versionSummary(1)], [versionDetail(1), versionDetail(2)]);
    await state.store.open(EPISODE_ID, { left: 'v1', right: 'v2' });
    const slow = deferred<StoryVersionDetail>();
    state.versions.scriptDetail(() => slow.promise);
    const pending = state.store.select('v3', 'left');
    state.store.reset();
    slow.resolve(versionDetail(3));
    await pending;
    expect(state.store.left()).toBeNull();
    expect(state.store.leftId()).toBeNull();
  });
});
