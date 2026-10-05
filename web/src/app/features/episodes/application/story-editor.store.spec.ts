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
import type { EpisodeDetail } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { StoryRepository } from '../domain/story.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeStoryRepository, storyContent, storySnapshot } from '../testing/fake-story.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

interface EditorTestState {
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
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
async function settle(): Promise<void> { for (let i = 0; i < 10; i++) await Promise.resolve(); }
function setup(snapshot: StorySnapshot | null = storySnapshot(), actions: readonly string[] = ['edit_story']) {
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
async function loaded(snapshot: StorySnapshot | null = storySnapshot(), actions: readonly string[] = ['edit_story']) {
  const state = setup(snapshot, actions); await state.store.load(EPISODE_ID); return state;
}
function edit(store: StoryEditorStore, text = 'Changed title'): void { store.setSegmentText('title', text); }
async function conflict(state: EditorTestState): Promise<void> {
  edit(state.store);
  state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
  await state.store.saveVersion();
}

describe('StoryEditorStore', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  it('load_MapsCurrentStoryIntoSignals', async () => {
    const { store, episodes, realtime } = await loaded();
    expect(store.title()).toBe('The lantern'); expect(store.hook()).toBe('A light appeared.'); expect(store.ending()).toBe('The light vanished.');
    expect(store.paragraphs()).toHaveLength(4); expect(store.paragraphs()[0]?.beat).toBe('beat-0');
    expect(store.paragraphs()[0]?.text()).toBe('Paragraph 1 waits.'); expect(store.meta().description).toBe('A midnight story');
    expect(store.snapshot()?.etag).toBe(storySnapshot().etag); expect(store.hasUnsavedChanges()).toBe(false);
    expect(episodes.getCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]); expect(realtime.subscriptions).toHaveLength(1);
  });
  it('edit_MarksDirtyAfterDebounce', async () => {
    const { store } = await loaded(); edit(store); vi.advanceTimersByTime(150); expect(store.isDirty()).toBe(true);
  });
  it('edit_BeforeDebounce_IsNotDirty', async () => {
    const { store } = await loaded(); edit(store); vi.advanceTimersByTime(149);
    expect(store.isDirty()).toBe(false); expect(store.hasUnsavedChanges()).toBe(true); expect(store.saveDisabled()).toBe(false);
  });
  it('save_SendsIfMatchAndUpdatesBaseline', async () => {
    const { store, stories } = await loaded(); edit(store);
    stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-next"', version: 2, wordCount: 42, qualityHints: [{ ruleId: 'rule', severity: 'advisory', kind: 'objective', segment: 'hook', messageKey: 'quality.hook_short', evidence: '', repairDirection: null }] })));
    await store.saveVersion();
    expect(stories.calls[0]).toMatchObject({ episodeId: EPISODE_ID, etag: storySnapshot().etag, userAction: true });
    expect(stories.calls[0]?.content.title).toBe('Changed title'); expect(store.snapshot()).toMatchObject({ etag: '"sv-next"', version: 2, wordCount: 42 });
    expect(store.snapshot()?.qualityHints).toHaveLength(1); expect(store.hasUnsavedChanges()).toBe(false); expect(store.isDirty()).toBe(false); expect(store.savedAt()).not.toBeNull();
  });
  it('save_AdoptsRepositoryEtagAndClearsDirtyForChangedBody', async () => {
    const { store, stories } = await loaded(); edit(store, 'The lantern '); await store.saveVersion();
    expect(stories.calls[0]?.content.title).toBe('The lantern ');
    expect(store.snapshot()?.etag).toBe(storySnapshot().etag); expect(store.hasUnsavedChanges()).toBe(false); expect(store.isDirty()).toBe(false); expect(stories.calls).toHaveLength(1);
  });
  it('save_400_MapsFieldErrorsToSegments', async () => {
    const { store, stories } = await loaded(); edit(store);
    stories.scriptSave(() => rejected(appError('story_invalid_content', 400, { fieldErrors: { 'p:3': ['Never show server message'], 'paragraphs[0].keywords': ['Never show'], mystery: ['Raw detail'] } })));
    await store.saveVersion();
    expect(store.fieldErrors()['p:3']).toEqual(['errors.story_invalid_content']); expect(store.fieldErrors()['p:0']).toEqual(['errors.story_invalid_content']);
    expect(store.formErrors()).toEqual(['errors.story_invalid_content']); expect(JSON.stringify(store.fieldErrors())).not.toContain('Never show'); expect(store.hasUnsavedChanges()).toBe(true);
  });
  it('save_400_ArrayRows_MapFieldsWithoutExposingMessages', async () => {
    const { store, stories } = await loaded(); edit(store);
    stories.scriptSave(() => rejected(appError('story_invalid_content', 400, { problem: { errors: [{ field: 'paragraphs[1].text', rule: 'too_long', message: 'Story secret' }] } })));
    await store.saveVersion(); expect(store.fieldErrors()['p:1']).toEqual(['errors.story_invalid_content']);
  });
  it('save_409_RefetchesAndExposesActions', async () => {
    const { store, detail, episodes, stories } = await loaded(); edit(store);
    episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['reopen_story'], currentStory: storySnapshot(), status: 'story_approved' })));
    stories.scriptSave(() => rejected(appError('episode_invalid_state', 409))); await store.saveVersion();
    expect(episodes.getCalls).toHaveLength(2); expect(episodes.getCalls[1]?.userAction).toBe(false);
    expect(detail.nextActions()).toEqual(['reopen_story']); expect(store.invalidState()).toBe(true); expect(store.readOnly()).toBe(true); expect(store.formErrors()).toEqual(['errors.episode_invalid_state']);
  });
  it('save_412_KeepsDraftAndOpensConflict', async () => {
    const state = await loaded(); await conflict(state);
    expect(state.store.title()).toBe('Changed title'); expect(state.store.conflict()).not.toBeNull(); expect(state.store.hasUnsavedChanges()).toBe(true); expect(state.stories.calls).toHaveLength(1);
  });
  it('resolveConflict_Cancel_KeepsDraft', async () => {
    const state = await loaded(); await conflict(state); await state.store.resolveConflict('cancel');
    expect(state.store.title()).toBe('Changed title'); expect(state.store.hasUnsavedChanges()).toBe(true); expect(state.store.conflict()).toBeNull(); expect(state.stories.calls).toHaveLength(1);
  });
  it('resolveConflict_Reload_ReplacesDraftWithServerContent', async () => {
    const state = await loaded(); await conflict(state);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ content: storyContent({ title: 'Server title' }) }), nextActions: ['edit_story'] })));
    await state.store.resolveConflict('reload');
    expect(state.store.title()).toBe('Server title'); expect(state.store.hasUnsavedChanges()).toBe(false); expect(state.store.conflict()).toBeNull(); expect(state.episodes.getCalls[1]?.userAction).toBe(true);
  });
  it('resolveConflict_SaveAsNew_RefetchesEtagThenPutsWithoutLosingText', async () => {
    const state = await loaded(); await conflict(state);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"', content: storyContent({ title: 'Other writer' }) }), nextActions: ['edit_story'] })));
    await state.store.resolveConflict('saveAsNew');
    expect(state.stories.calls[1]).toMatchObject({ etag: '"sv-fresh"', userAction: true }); expect(state.stories.calls[1]?.content.title).toBe('Changed title'); expect(state.store.title()).toBe('Changed title'); expect(state.episodes.getCalls[1]?.userAction).toBe(true);
  });
  it('resolveConflict_SaveAsNew_Repeated412_KeepsDialogAndDraft', async () => {
    const state = await loaded(); await conflict(state);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"' }), nextActions: ['edit_story'] })));
    state.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    await state.store.resolveConflict('saveAsNew'); expect(state.store.conflict()).not.toBeNull(); expect(state.store.title()).toBe('Changed title'); expect(state.store.hasUnsavedChanges()).toBe(true);
  });
  it('addKeyword_ThirdKeyword_IsRefusedClientSide', async () => {
    const { store, stories } = await loaded(); const paragraph = store.paragraphs()[0]; if (!paragraph) throw new Error('Missing fixture paragraph');
    store.addKeyword(paragraph.segKey, 'third'); expect(paragraph.keywords()).toEqual(['road', 'mist']); expect(store.keywordLimitHit()).toBe(paragraph.segKey); expect(stories.calls).toHaveLength(0);
  });
  it('keywordEditing_PreservesOneToTwoAndStableSegKeys', async () => {
    const { store } = await loaded(); const key = store.hookKey();
    store.removeKeyword(key, 0); expect(store.hookKeywords()).toEqual(['lantern']); expect(store.keywordLimitHit()).toBe(key);
    store.addKeyword(key, 'road'); store.updateKeyword(key, 1, 'mist'); store.removeKeyword(key, 0);
    expect(store.hookKeywords()).toEqual(['mist']); expect(store.hookKey()).toBe(key);
  });
  it('save_Twice_IsGuardedBySaving', async () => {
    const { store, stories } = await loaded(); const response = deferred<StorySnapshot>(); stories.scriptSave(() => response.promise); edit(store);
    const first = store.saveVersion(); await store.saveVersion(); expect(stories.calls).toHaveLength(1); expect(store.saving()).toBe(true);
    response.resolve(storySnapshot()); await first; expect(store.saving()).toBe(false);
  });
  it('save_EditsTypedDuringTheInFlightPut_RemainDirtyAfterThe200', async () => {
    const { store, stories } = await loaded(); const response = deferred<StorySnapshot>(); stories.scriptSave(() => response.promise); edit(store, 'First sent');
    const first = store.saveVersion(); const sentVersion = store.version(); edit(store, 'Second live'); response.resolve(storySnapshot({ etag: '"sv-next"' })); await first; vi.advanceTimersByTime(150);
    expect(store.savedVersion()).toBe(sentVersion); expect(store.hasUnsavedChanges()).toBe(true); expect(store.isDirty()).toBe(true); expect(store.saveDisabled()).toBe(false);
    await store.saveVersion(); expect(stories.calls[1]?.content.title).toBe('Second live'); expect(stories.calls[1]?.etag).toBe('"sv-next"');
  });
  it('load_And_ReloadLatest_ResetSavedVersionToTheCurrentVersion', async () => {
    const state = await loaded(); await conflict(state);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'] })));
    await state.store.resolveConflict('reload'); expect(state.store.savedVersion()).toBe(state.store.version());
    edit(state.store); state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'] })));
    await state.store.load(EPISODE_ID); expect(state.store.savedVersion()).toBe(state.store.version()); expect(state.store.saveDisabled()).toBe(true);
  });
  it('load_NullCurrentStory_NeverCreatesAnEmptyDraft', async () => {
    const { store } = await loaded(null); expect(store.snapshot()).toBeNull(); expect(store.paragraphs()).toHaveLength(0); expect(store.saveDisabled()).toBe(true);
  });
  it('readOnly_AbsentEditAction_RefusesEveryEditAndSave', async () => {
    const { store, stories } = await loaded(storySnapshot(), ['reopen_story']); edit(store); store.setText(store.hookKey(), 'forbidden'); store.setMeta({ description: 'forbidden' }); store.addKeyword(store.hookKey(), 'forbidden'); await store.saveVersion();
    expect(store.title()).toBe('The lantern'); expect(store.hook()).toBe('A light appeared.'); expect(store.meta().description).toBe('A midnight story'); expect(stories.calls).toHaveLength(0);
  });
  it('save_MissingEtag_RefusesLocallyWithGenericMessage', async () => {
    const { store, stories } = await loaded(storySnapshot({ etag: '' })); edit(store); await store.saveVersion();
    expect(stories.calls).toHaveLength(0); expect(store.formErrors()).toEqual(['errors.unknown_error']);
  });
  it('save_428_UsesGenericMessageAndKeepsDraft', async () => {
    const { store, stories } = await loaded(); edit(store); stories.scriptSave(() => rejected(appError('if_match_required', 428))); await store.saveVersion();
    expect(store.formErrors()).toEqual(['errors.unknown_error']); expect(store.hasUnsavedChanges()).toBe(true);
  });
  it('textEdits_UpdateTokensWithoutRecreatingOtherParagraphsOrRequests', async () => {
    const { store, stories, episodes } = await loaded(); const paragraphs = store.paragraphs(); const target = paragraphs[0]; if (!target) throw new Error('Missing paragraph');
    store.setText(target.segKey, 'Một hai'); expect(store.segmentTokens().get('p:0')).toBe(2); expect(store.totalTokens()).toBe(22); expect(store.paragraphs()[1]).toBe(paragraphs[1]);
    vi.advanceTimersByTime(1000); expect(stories.calls).toHaveLength(0); expect(episodes.getCalls).toHaveLength(1);
  });
  it('realtime_Burst_CoalescesBackgroundReadAndKeepsDirtyDraftAndEtag', async () => {
    const { store, episodes, realtime, detail } = await loaded(); edit(store);
    episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-elsewhere"', content: storyContent({ title: 'Elsewhere' }) }), nextActions: ['edit_story'] })));
    realtime.emit(EPISODE_ID); realtime.emit(EPISODE_ID); realtime.resync(); vi.advanceTimersByTime(250); await settle();
    expect(episodes.getCalls).toHaveLength(2); expect(episodes.getCalls[1]?.userAction).toBe(false); expect(detail.episode()?.currentStory?.etag).toBe('"sv-elsewhere"'); expect(store.title()).toBe('Changed title'); expect(store.snapshot()?.etag).toBe(storySnapshot().etag);
  });
  it('realtime_CleanDraft_AdoptsFreshSnapshot', async () => {
    const { store, episodes, realtime } = await loaded(); episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"', content: storyContent({ title: 'Fresh title' }) }), nextActions: ['edit_story'] })));
    realtime.resync(); vi.advanceTimersByTime(250); await settle(); expect(store.title()).toBe('Fresh title'); expect(store.hasUnsavedChanges()).toBe(false);
  });
  it('load_LatePreviousGet_CannotOverwriteNewEpisode', async () => {
    const state = setup(); const late = deferred<EpisodeDetail>();
    const first = state.store.load(EPISODE_ID); await first;
    state.episodes.scriptGet(() => late.promise); const old = state.store.load('old');
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ content: storyContent({ title: 'New episode' }) }), nextActions: ['edit_story'] })));
    await state.store.load('new'); late.resolve(episodeDetail({ currentStory: storySnapshot() })); await old;
    expect(state.store.title()).toBe('New episode'); expect(state.realtime.subscriptions[1]?.unsubscribed).toBe(true);
  });
  it.each(['logout', 'destroy'])('save_LatePutAfter_%s_IsDiscardedAndSubscriptionTornDown', async (boundary) => {
    const state = await loaded(); const late = deferred<StorySnapshot>(); state.stories.scriptSave(() => late.promise); edit(state.store); const save = state.store.saveVersion();
    if (boundary === 'logout') { state.sessionGeneration.update((value) => value + 1); TestBed.tick(); } else TestBed.resetTestingModule();
    late.resolve(storySnapshot({ etag: '"sv-late"' })); await save; expect(state.store.snapshot()).toBeNull(); expect(state.realtime.subscriptions[0]?.unsubscribed).toBe(true);
    const reads = state.episodes.getCalls.length; state.realtime.subscriptions[0]?.handler(); vi.advanceTimersByTime(1000); await settle(); expect(state.episodes.getCalls).toHaveLength(reads);
  });
  it('realtime_GetStartedDuringPut_CannotOverwriteSuccessfulSentDraft', async () => {
    const state = await loaded(); const read = deferred<EpisodeDetail>(); const write = deferred<StorySnapshot>();
    state.episodes.scriptGet(() => read.promise); state.stories.scriptSave(() => write.promise);
    edit(state.store, 'New saved text'); const save = state.store.saveVersion();
    state.realtime.resync(); vi.advanceTimersByTime(250); await settle();
    write.resolve(storySnapshot({ etag: '"sv-next"' })); await save;
    read.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'] })); await settle();
    expect(state.store.title()).toBe('New saved text'); expect(state.store.snapshot()?.etag).toBe('"sv-next"');
  });
  it('resolveConflict_DoubleSaveAsNew_UsesOneReadAndPut', async () => {
    const state = await loaded(); await conflict(state); const read = deferred<EpisodeDetail>(); state.episodes.scriptGet(() => read.promise);
    const first = state.store.resolveConflict('saveAsNew'); await state.store.resolveConflict('saveAsNew');
    expect(state.episodes.getCalls).toHaveLength(2); read.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"' }), nextActions: ['edit_story'] }));
    await first; expect(state.stories.calls).toHaveLength(2);
  });
  it('save_400_OversizedParagraphAddress_IsFormErrorNotException', async () => {
    const state = await loaded(); edit(state.store); state.stories.scriptSave(() => rejected(appError('story_invalid_content', 400, { fieldErrors: { 'paragraphs[9007199254740992].text': ['bad'] } })));
    await state.store.saveVersion(); expect(state.store.formErrors()).toEqual(['errors.story_invalid_content']);
  });
  it('realtime_UnchangedEtag_KeepsKeysParagraphSignalsAndSavedAt', async () => {
    const state = await loaded(); edit(state.store); await state.store.saveVersion();
    const paragraphs = state.store.paragraphs(), hookKey = state.store.hookKey(), endingKey = state.store.endingKey(), savedAt = state.store.savedAt();
    state.store.invalidState.set(true);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: state.store.snapshot(), nextActions: ['edit_story'] })));
    state.realtime.resync(); vi.advanceTimersByTime(250); await settle();
    expect(state.store.paragraphs()).toBe(paragraphs); expect(state.store.hookKey()).toBe(hookKey); expect(state.store.endingKey()).toBe(endingKey);
    expect(state.store.title()).toBe('Changed title'); expect(state.store.savedAt()).toBe(savedAt); expect(state.store.invalidState()).toBe(false);
  });
  it('realtime_EditStoryReturns_UnlocksDirtyDraftWithoutAdopting', async () => {
    const state = await loaded(); edit(state.store);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['reopen_story'], currentStory: storySnapshot() })));
    state.stories.scriptSave(() => rejected(appError('episode_invalid_state', 409))); await state.store.saveVersion();
    expect(state.store.invalidState()).toBe(true);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], currentStory: storySnapshot({ etag: '"sv-other"' }) })));
    state.realtime.resync(); vi.advanceTimersByTime(250); await settle();
    expect(state.store.invalidState()).toBe(false); expect(state.store.title()).toBe('Changed title'); expect(state.store.saveDisabled()).toBe(false);
    edit(state.store, 'Editing resumed'); expect(state.store.title()).toBe('Editing resumed');
  });
  it.each([{ code: 'network_error', status: 0 }, { code: 'unknown_error', status: 500 }, { code: 'story_invalid_content', status: 400 }, { code: 'payload_too_large', status: 413 }])('resolveConflict_SaveAsNew_$code_ClearsStaleConflict', async ({ code, status }) => {
    const state = await loaded(); await conflict(state);
    state.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"' }), nextActions: ['edit_story'] })));
    state.stories.scriptSave(() => rejected(appError(code, status)));
    await state.store.resolveConflict('saveAsNew');
    expect(state.store.conflict()).toBeNull(); expect(state.store.error()?.status).toBe(status); expect(state.store.hasUnsavedChanges()).toBe(true);
    expect(state.store.title()).toBe('Changed title'); expect(state.stories.calls).toHaveLength(2);
  });
  it('resolveConflict_SaveAsNew_FailedRead_ClearsStaleConflict', async () => {
    const state = await loaded(); await conflict(state);
    state.episodes.scriptGet(() => rejected(appError('network_error', 0)));
    await state.store.resolveConflict('saveAsNew');
    expect(state.store.conflict()).toBeNull(); expect(state.store.error()?.status).toBe(0); expect(state.stories.calls).toHaveLength(1);
  });
  it('save_400_DeduplicatesMultiplePointersOnOneSegment', async () => {
    const state = await loaded(); edit(state.store);
    state.stories.scriptSave(() => rejected(appError('story_invalid_content', 400, {
      fieldErrors: { 'p:1': ['invalid'], 'paragraphs[1].keywords': ['invalid'], description: ['invalid'], tags: ['invalid'] },
      problem: { errors: [{ field: 'paragraphs[1].text' }, { field: 'content_warnings' }] },
    })));
    await state.store.saveVersion();
    expect(state.store.fieldErrors()['p:1']).toEqual(['errors.story_invalid_content']); expect(state.store.fieldErrors().meta).toEqual(['errors.story_invalid_content']);
  });
  it('metadata_DeduplicatesServerAndEditedTagsAndWarnings', async () => {
    const { store } = await loaded(storySnapshot({ content: storyContent({ tags: ['night', 'night'], content_warnings: ['fear', 'fear'] }) }));
    expect(store.meta().tags).toEqual(['night']); expect(store.meta().contentWarnings).toEqual(['fear']); expect(store.hasUnsavedChanges()).toBe(false);
    store.setMeta({ tags: ['night', 'night'], contentWarnings: ['fear', 'fear'] }); expect(store.saveDisabled()).toBe(true);
    store.setMeta({ tags: ['moon', 'moon'], contentWarnings: ['storm', 'storm'] });
    expect(store.meta().tags).toEqual(['moon']); expect(store.meta().contentWarnings).toEqual(['storm']); expect(store.saveDisabled()).toBe(false);
  });
  it('saveDisabled_DoesNotSerializeWhileTypingOrRevertingAnyField', async () => {
    const { store } = await loaded(); const stringify = vi.spyOn(JSON, 'stringify');
    try {
      store.setSegmentText('title', 'changed'); expect(store.saveDisabled()).toBe(false);
      store.setSegmentText('title', 'The lantern'); expect(store.saveDisabled()).toBe(true);
      const paragraph = store.paragraphs()[0]; if (!paragraph) throw new Error('Missing paragraph');
      store.setText(paragraph.segKey, 'changed'); store.setSegmentText('hook', 'changed hook');
      store.setText(paragraph.segKey, 'Paragraph 1 waits.'); expect(store.saveDisabled()).toBe(false);
      store.setSegmentText('hook', 'A light appeared.'); expect(store.saveDisabled()).toBe(true);
      store.updateKeyword(paragraph.segKey, 0, 'new'); expect(store.saveDisabled()).toBe(false);
      store.updateKeyword(paragraph.segKey, 0, 'road'); expect(store.saveDisabled()).toBe(true);
      store.setMeta({ description: 'new', tags: ['moon'] }); expect(store.saveDisabled()).toBe(false);
      store.setMeta({ description: 'A midnight story', tags: ['night'] }); expect(store.saveDisabled()).toBe(true);
      expect(stringify).not.toHaveBeenCalled();
      vi.advanceTimersByTime(150); expect(stringify).toHaveBeenCalledTimes(1); expect(store.isDirty()).toBe(false);
    } finally { stringify.mockRestore(); }
  });
  it('tokenCounts_MemoizeEachSegmentAndShareTheMetaCount', async () => {
    const { store } = await loaded(); expect(store.totalTokens()).toBe(23);
    const normalize = vi.spyOn(String.prototype, 'normalize');
    try {
      const paragraph = store.paragraphs()[0]; if (!paragraph) throw new Error('Missing paragraph');
      store.setText(paragraph.segKey, 'Một hai'); expect(store.totalTokens()).toBe(22);
      expect(store.metaTokens()).toBe(3); expect(paragraph.tokens()).toBe(2); expect(normalize).toHaveBeenCalledTimes(1);
      store.setMeta({ tags: ['moon'] }); expect(store.totalTokens()).toBe(22); expect(store.metaTokens()).toBe(3); expect(normalize).toHaveBeenCalledTimes(1);
      store.setMeta({ description: 'Một hai ba bốn' }); expect(store.metaTokens()).toBe(4); expect(store.totalTokens()).toBe(23); expect(normalize).toHaveBeenCalledTimes(2);
    } finally { normalize.mockRestore(); }
  });
});
