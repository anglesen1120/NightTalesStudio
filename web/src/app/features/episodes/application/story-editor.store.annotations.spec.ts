import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { KeywordFlag } from '../domain/annotation.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import type { QualityHint, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeStoryRepository, storyContent, storySnapshot } from '../testing/fake-story.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

// 10.5.10-T1 slice B: the annotation merge, the keyword-flag lifecycle (WR6), the annotation live-region announcement,
// and the 412/remote-change conflict flow. Deterministic, fake timers only where the 150 ms dirty debounce or the
// 250 ms invalidation coalescing is under test.

interface EditorState {
  readonly store: StoryEditorStore; readonly detail: EpisodeDetailStore;
  readonly episodes: FakeEpisodesRepository; readonly stories: FakeStoryRepository;
  readonly realtime: FakeEpisodeRealtime; readonly sessionGeneration: WritableSignal<number>;
}

function appError(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  return { code, status, kind: status === 400 ? 'validation' : status === 412 ? 'conflict' : 'precondition', fieldErrors: {}, ...extra };
}
function rejected<T>(error: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- structured domain-port failure
  return Promise.reject(error);
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}
async function settle(): Promise<void> { for (let i = 0; i < 12; i++) await Promise.resolve(); }
function hint(overrides: Partial<QualityHint> = {}): QualityHint {
  return { ruleId: 'rule', severity: 'advisory', kind: 'objective', segment: 'hook', messageKey: 'quality.rule', evidence: '', repairDirection: null, ...overrides };
}
function flag(overrides: Partial<KeywordFlag> = {}): KeywordFlag {
  return { address: 'hook', index: 0, rule: 'keyword_invalid', ...overrides };
}
function setup(snapshot: StorySnapshot | null = storySnapshot(), actions: readonly string[] = ['edit_story']): EditorState {
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: snapshot, hasCurrentStory: snapshot !== null, nextActions: actions, status: 'story_review' })));
  const stories = new FakeStoryRepository();
  const realtime = new FakeEpisodeRealtime();
  const sessionGeneration = signal(0);
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, EpisodeDetailStore, StoryEditorStore,
    { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: stories },
    { provide: RealtimeService, useValue: realtime }, { provide: SessionStore, useValue: { generation: sessionGeneration } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  const store = TestBed.inject(StoryEditorStore), detail = TestBed.inject(EpisodeDetailStore);
  return { store, detail, episodes, stories, realtime, sessionGeneration };
}
async function loaded(snapshot: StorySnapshot | null = storySnapshot(), actions: readonly string[] = ['edit_story']): Promise<EditorState> {
  const state = setup(snapshot, actions); await state.store.load(EPISODE_ID); return state;
}
/** A background read that answers every call with the same fresh detail. */
function scriptFresh(state: EditorState, snapshot: StorySnapshot): void {
  state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: snapshot })));
}
async function refetched(state: EditorState): Promise<void> {
  state.realtime.emit(EPISODE_ID); vi.advanceTimersByTime(250); await settle();
}
/** A server copy identical to `storyContent()` except `index`'s paragraph text. */
function contentWithParagraph(index: number, text: string): StorySnapshot {
  const base = storyContent();
  return storySnapshot({ etag: '"sv-other"', content: storyContent({ paragraphs: base.paragraphs.map((row, at) => at === index ? { ...row, text } : row) }) });
}
async function conflict412(state: EditorState): Promise<void> {
  state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
  await state.store.saveVersion();
}

describe('StoryEditorStore annotations and conflict', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  it('annotations_merge_sorted_and_deduped', async () => {
    const duplicate = hint({ ruleId: 'block_rule', severity: 'blocking', kind: 'objective', segment: 'p:0' });
    const { store } = await loaded(storySnapshot({ qualityHints: [
      hint({ ruleId: 'taste_rule', kind: 'taste', segment: 'hook', messageKey: 'quality.taste_rule' }),
      duplicate, duplicate,
    ] }));
    expect(store.annotations().map((item) => [item.ruleId, item.kind, item.severity]))
      .toEqual([['block_rule', 'objective', 'blocking'], ['taste_rule', 'taste', 'advisory']]);
    expect(store.annotationCount()).toBe(2);
    // A keyword flag is merged on top of the saved hints, and the merge is stable across an edit.
    store.setKeywordFlags([flag({ address: 'p:1', index: 1, rule: 'keyword_name_detected' })]);
    expect(store.annotationCount()).toBe(3);
    expect(store.annotations().find((item) => item.source === 'keyword'))
      .toMatchObject({ source: 'keyword', address: 'p:1', keywordIndex: 1 });
    store.setSegmentText('title', 'edited'); vi.advanceTimersByTime(150);
    expect(store.annotationCount()).toBe(3);
    expect(store.annotations().map((item) => item.ruleId)).toContain('taste_rule');
  });

  it('setKeywordFlags_disables_videoBlocked_and_edit_does_not_clear', async () => {
    const { store } = await loaded();
    expect(store.videoBlocked()).toBe(false); expect(store.keywordFlags()).toEqual([]);
    store.setKeywordFlags([flag()]);
    expect(store.videoBlocked()).toBe(true); expect(store.keywordFlags()).toHaveLength(1);
    // The set replaces and caps at 100 (WR6).
    store.setKeywordFlags(Array.from({ length: 150 }, (_, index): KeywordFlag => ({ address: 'p:0', index: index % 10, rule: 'no_scenes' })));
    expect(store.keywordFlags()).toHaveLength(100);
    store.setKeywordFlags([flag()]);
    store.setSegmentText('title', 'edited while flagged'); vi.advanceTimersByTime(150);
    expect(store.isDirty()).toBe(true); expect(store.videoBlocked()).toBe(true); expect(store.keywordFlags()).toHaveLength(1);
  });

  it('setKeywordFlags_ignored_without_a_snapshot', async () => {
    const { store } = await loaded(null, ['generate_story']);
    store.setKeywordFlags([flag()]);
    expect(store.keywordFlags()).toEqual([]); expect(store.videoBlocked()).toBe(false);
  });

  it('flags_cleared_by_next_successful_save', async () => {
    const { store, stories } = await loaded();
    store.setKeywordFlags([flag()]);
    expect(store.videoBlocked()).toBe(true);
    store.setSegmentText('title', 'fixed');
    stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-2"' })));
    await store.saveVersion();
    expect(store.keywordFlags()).toEqual([]); expect(store.videoBlocked()).toBe(false);
    expect(store.snapshot()?.etag).toBe('"sv-2"'); expect(store.hasUnsavedChanges()).toBe(false);
  });

  it('keywords_flagged_409_maps_the_flagged_chips_and_keeps_the_draft', async () => {
    const state = await loaded();
    state.store.setSegmentText('title', 'mine');
    state.stories.scriptSave(() => rejected(appError('keywords_flagged', 409, { problem: { errors: [
      { segment: 'p:1', index: 1, rule: 'keyword_name_detected' },
      { segment: 7, index: 'nope', rule: '' },
    ] } })));
    await state.store.saveVersion();
    // The malformed second row is dropped, so one chip carries the refusal; the draft and the ETag are untouched.
    expect(state.store.keywordFlags()).toEqual([{ address: 'p:1', index: 1, rule: 'keyword_name_detected' }]);
    expect(state.store.videoBlocked()).toBe(true);
    expect(state.store.title()).toBe('mine'); expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.snapshot()?.etag).toBe(storySnapshot().etag);
    expect(state.store.conflict()).toBeNull(); expect(state.store.saving()).toBe(false);
    expect(state.store.formErrors()).toEqual(['errors.keywords_flagged']);
    expect(state.episodes.getCalls).toHaveLength(1);
    // The shared entry point (10.5.6-T1) reports whether it owned the error.
    expect(state.store.applyKeywordsFlaggedProblem(appError('episode_invalid_state', 409))).toBe(false);
    expect(state.store.applyKeywordsFlaggedProblem(appError('keywords_flagged', 409, { problem: { errors: [{ segment: 'p:1', index: 1, rule: 'keyword_name_detected' }] } }))).toBe(true);
    expect(state.store.keywordFlags()).toHaveLength(1);
  });

  it('annotationAnnouncement_once_per_save', async () => {
    const state = await loaded();
    expect(state.store.annotationAnnouncement()).toBeNull();
    state.store.setSegmentText('title', 'one');
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-2"', qualityHints: [hint({ ruleId: 'r1' }), hint({ ruleId: 'r2', segment: 'p:1' })] })));
    await state.store.saveVersion();
    expect(state.store.annotationAnnouncement()).toEqual({ count: 2, sequence: 1 });
    state.store.setSegmentText('title', 'two');
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-3"', qualityHints: [hint({ ruleId: 'r1' })] })));
    await state.store.saveVersion();
    expect(state.store.annotationAnnouncement()).toEqual({ count: 1, sequence: 2 });
    expect(state.store.annotations().map((item) => item.ruleId)).toEqual(['r1']);
    // A load (fresh episode state) clears the announcement.
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'], status: 'story_review' })));
    await state.store.load(EPISODE_ID);
    expect(state.store.annotationAnnouncement()).toBeNull();
  });

  it('conflict_412_keeps_draft_and_computes_diff', async () => {
    const state = await loaded();
    const paragraph = state.store.paragraphs()[1]; if (!paragraph) throw new Error('Missing fixture paragraph');
    state.store.setText(paragraph.segKey, 'My own second paragraph');
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    scriptFresh(state, contentWithParagraph(1, 'Their second paragraph'));
    await state.store.saveVersion();
    expect(state.store.conflict()).toBe(true); expect(state.store.conflictBusy()).toBe(false);
    expect(state.store.title()).toBe('The lantern');
    expect(state.store.paragraphs()[1]?.text()).toBe('My own second paragraph');
    expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.conflictDiff()).toEqual(['p:1']);
    expect(state.episodes.getCalls).toHaveLength(2);
    expect(state.episodes.getCalls[1]).toEqual({ episodeId: EPISODE_ID, userAction: true });
    expect(state.stories.calls).toHaveLength(1);
  });

  it('conflict_RefetchWithTheSameEtag_IsANoOp', async () => {
    const state = await loaded();
    state.store.setSegmentText('title', 'mine');
    scriptFresh(state, storySnapshot());
    await refetched(state);
    expect(state.episodes.getCalls).toHaveLength(2);
    expect(state.store.remoteChange()).toBeNull(); expect(state.store.conflict()).toBeNull();
    expect(state.store.title()).toBe('mine'); expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.snapshot()?.etag).toBe(storySnapshot().etag);
  });

  it('conflict_RefetchWithADifferentEtag_RaisesTheNoticeWithoutTouchingTheDraft', async () => {
    const state = await loaded();
    const paragraph = state.store.paragraphs()[0]; if (!paragraph) throw new Error('Missing fixture paragraph');
    state.store.setText(paragraph.segKey, 'Mine');
    scriptFresh(state, contentWithParagraph(0, 'Theirs'));
    await refetched(state);
    const first = state.store.remoteChange();
    expect(first).toEqual({ etag: '"sv-other"', diff: ['p:0'] });
    // The typed segment is byte-identical and the adopted baseline is untouched: no draft change, no adopt.
    expect(state.store.paragraphs()[0]?.text()).toBe('Mine');
    expect(state.store.title()).toBe('The lantern');
    expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.snapshot()?.etag).toBe(storySnapshot().etag);
    // Same ETag again: complete no-op, the same notice object is not re-created.
    scriptFresh(state, contentWithParagraph(0, 'Theirs'));
    await refetched(state);
    expect(state.store.remoteChange()).toBe(first);
    // Cancel dismisses it; the dismissed ETag stays dismissed on the next read.
    await state.store.resolveConflict('cancel');
    expect(state.store.remoteChange()).toBeNull();
    scriptFresh(state, contentWithParagraph(0, 'Theirs'));
    await refetched(state);
    expect(state.store.remoteChange()).toBeNull();
    expect(state.store.paragraphs()[0]?.text()).toBe('Mine');
  });

  it('saveAsNew_uses_fresh_etag_once_and_second_412_sets_conflictBusy_without_looping', async () => {
    const state = await loaded();
    state.store.setSegmentText('title', 'mine');
    await conflict412(state);
    expect(state.store.conflict()).toBe(true); expect(state.store.conflictBusy()).toBe(false);
    const reads = state.episodes.getCalls.length, puts = state.stories.calls.length;
    scriptFresh(state, storySnapshot({ etag: '"sv-fresh"' }));
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.resolveConflict('saveAsNew');
    // Exactly one fresh-etag read and one re-attempt, then it stops: no loop.
    expect(state.episodes.getCalls.length - reads).toBe(1);
    expect(state.stories.calls.length - puts).toBe(1);
    expect(state.stories.calls[1]).toMatchObject({ etag: '"sv-fresh"', userAction: true });
    expect(state.stories.calls[1]?.content.title).toBe('mine');
    expect(state.store.conflict()).toBe(true); expect(state.store.conflictBusy()).toBe(true);
    expect(state.store.title()).toBe('mine'); expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.saving()).toBe(false);
    // The busy variant clears on the user's decision, leaving the draft alone.
    await state.store.resolveConflict('cancel');
    expect(state.store.conflict()).toBeNull(); expect(state.store.conflictBusy()).toBe(false);
    expect(state.store.hasUnsavedChanges()).toBe(true);
  });

  it('saveAsNew_from_a_remote_change_notice_refetches_and_puts_the_fresh_etag', async () => {
    const state = await loaded();
    state.store.setSegmentText('title', 'mine');
    scriptFresh(state, contentWithParagraph(0, 'Theirs'));
    await refetched(state);
    expect(state.store.remoteChange()).not.toBeNull(); expect(state.store.conflict()).toBeNull();
    scriptFresh(state, storySnapshot({ etag: '"sv-fresh"' }));
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-new"' })));
    await state.store.resolveConflict('saveAsNew');
    expect(state.stories.calls).toHaveLength(1);
    expect(state.stories.calls[0]).toMatchObject({ etag: '"sv-fresh"', userAction: true });
    expect(state.stories.calls[0]?.content.title).toBe('mine');
    expect(state.store.remoteChange()).toBeNull(); expect(state.store.conflict()).toBeNull();
    expect(state.store.hasUnsavedChanges()).toBe(false); expect(state.store.snapshot()?.etag).toBe('"sv-new"');
  });

  it('late_412_after_destroy_sets_nothing', async () => {
    const state = await loaded();
    const late = deferred<StorySnapshot>();
    state.stories.scriptSave(() => late.promise);
    state.store.setSegmentText('title', 'mine');
    const save = state.store.saveVersion();
    const reads = state.episodes.getCalls.length;
    TestBed.resetTestingModule();
    late.reject(appError('story_version_conflict', 412)); await save; await settle();
    expect(state.store.conflict()).toBeNull(); expect(state.store.conflictDiff()).toEqual([]);
    expect(state.store.conflictBusy()).toBe(false); expect(state.store.remoteChange()).toBeNull();
    expect(state.episodes.getCalls).toHaveLength(reads);
    expect(state.store.snapshot()).toBeNull();
  });

  it('reload_failure_keeps_draft_and_conflict', async () => {
    const state = await loaded();
    state.store.setSegmentText('title', 'mine');
    await conflict412(state);
    expect(state.store.conflict()).toBe(true);
    state.episodes.scriptGet(() => rejected(appError('network_error', 0, { kind: 'network' })));
    await state.store.resolveConflict('reload');
    expect(state.store.conflict()).toBe(true);
    expect(state.store.title()).toBe('mine'); expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.error()?.status).toBe(0);
    expect(state.store.saving()).toBe(false);
  });
});
