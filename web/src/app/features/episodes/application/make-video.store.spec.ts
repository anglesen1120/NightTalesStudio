import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import type { Observable } from 'rxjs';
import { vi, type MockInstance } from 'vitest';
import { AppErrorMapper } from '@core/errors';
import type { AppError, AppErrorKind } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IdempotencyIntent } from '@core/http';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { ConfirmDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { KeywordFlag } from '../domain/annotation.model';
import type { EpisodeDetail } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { RenderRepository } from '../domain/render.repository';
import type { RenderCommandIntent } from '../domain/render.repository';
import { StoryRepository } from '../domain/story.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeRenderRepository } from '../testing/fake-render.repository';
import { FakeStoryRepository, storySnapshot } from '../testing/fake-story.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { MakeVideoStore } from './make-video.store';
import { StoryEditorStore } from './story-editor.store';

const ETAG = '"ep-1"';
const OTHER_EPISODE_ID = '33333333-3333-4333-8333-333333333333';
const ACTION = 'episodes.render.make.action';
const RETRY = 'episodes.render.make.retry';
const BLOCKED = 'episodes.render.make.blockedKeywords';
/** A `409 keywords_flagged` body: rows are `{segment, index, rule}`, never a field-error map (03.2.2-T2). */
const KEYWORD_ROW = { segment: 'p:3', index: 1, rule: 'keyword_name_detected' };
const MAPPED_ROW = { address: 'p:3', index: 1, rule: 'keyword_name_detected' } satisfies KeywordFlag;

function appError(code: string, status: number, kind: AppErrorKind, extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}
function networkError(): AppError {
  return appError('network_error', 0, 'network');
}
async function settle(): Promise<void> { for (let i = 0; i < 10; i++) await Promise.resolve(); }
/** A promise a test resolves by hand: a scripted read held open, so an assertion can run while it is pending. */
function gate<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

/** The episode as the server reports it while the render action is on offer. */
function detailAnswer(overrides: Partial<EpisodeDetail> = {}): EpisodeDetail {
  return episodeDetail({
    id: EPISODE_ID, status: 'story_approved', etag: ETAG, hasCurrentStory: true, currentStory: storySnapshot(), nextActions: ['render'], ...overrides,
  });
}

interface StoreState {
  readonly store: MakeVideoStore;
  readonly detail: EpisodeDetailStore;
  readonly editor: StoryEditorStore;
  readonly episodes: FakeEpisodesRepository;
  readonly repo: FakeRenderRepository;
  readonly confirm: MockInstance<() => { afterClosed: () => Observable<boolean> }>;
}
interface SetupOptions {
  readonly actions?: readonly string[];
  readonly devMode?: boolean;
}

function setup(options: SetupOptions = {}): StoreState {
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(detailAnswer({ nextActions: options.actions ?? ['render'] })));
  const repo = new FakeRenderRepository();
  const confirm: MockInstance<() => { afterClosed: () => Observable<boolean> }> = vi.fn(() => ({ afterClosed: () => of(true) }));
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, EpisodeDetailStore, StoryEditorStore, MakeVideoStore,
    { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: new FakeStoryRepository() },
    { provide: RenderRepository, useValue: repo }, { provide: RealtimeService, useValue: new FakeEpisodeRealtime() },
    { provide: SessionStore, useValue: { generation: signal(0) } },
    { provide: ConfirmDialog, useValue: { confirm } },
    { provide: APP_ERROR_DEV_MODE, useValue: options.devMode === true },
  ] });
  return { store: TestBed.inject(MakeVideoStore), detail: TestBed.inject(EpisodeDetailStore), editor: TestBed.inject(StoryEditorStore), episodes, repo, confirm };
}
async function loaded(options: SetupOptions = {}): Promise<StoreState> {
  const state = setup(options);
  await state.editor.load(EPISODE_ID);
  return state;
}
/**
 * Moves the page to another `:id` the way the route does it: the scripted read answers the new episode and the detail
 * store's own `load` installs it. The make-video store is component-scoped, so nothing else resets it.
 */
async function openEpisode(state: StoreState, id: string): Promise<void> {
  state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ id, nextActions: ['render'] })));
  await state.detail.load(id);
}

function keyOf(call: RenderCommandIntent | undefined): string {
  return call?.key ?? '';
}

describe('MakeVideoStore', () => {
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); TestBed.resetTestingModule(); });

  it('makeVideo_WithRenderOffered_IsEnabledAndLabelledAsTheAction', async () => {
    const state = await loaded({ actions: ['render'] });
    expect(state.store.allowed()).toBe(true);
    expect(state.store.blockedByKeywords()).toBe(false);
    expect(state.store.reasonKey()).toBeNull();
    expect(state.store.labelKey()).toBe(ACTION);
    expect(state.store.canMakeVideo()).toBe(true);
  });

  it('makeVideo_WithOnlyRetryRender_IsLabelledAsTheRetry', async () => {
    const state = await loaded({ actions: ['retry_render'] });
    expect(state.store.allowed()).toBe(true);
    expect(state.store.labelKey()).toBe(RETRY);
  });

  it('makeVideo_WithBothActions_ReadsAsThePlainAction', async () => {
    const state = await loaded({ actions: ['render', 'retry_render'] });
    expect(state.store.labelKey()).toBe(ACTION);
  });

  it('makeVideo_WithNoRenderAction_IsAbsentAndSendsNothing', async () => {
    const state = await loaded({ actions: ['reopen_story'] });
    expect(state.store.labelKey()).toBeNull();
    expect(state.store.allowed()).toBe(false);
    expect(state.store.canMakeVideo()).toBe(false);
    await state.store.makeVideo();
    expect(state.repo.requestCalls).toEqual([]);
    expect(state.confirm).not.toHaveBeenCalled();
  });

  it('makeVideo_WithAFlaggedKeyword_IsStillPresentButDisabledWithItsReason', async () => {
    const state = await loaded();
    state.editor.setKeywordFlags([MAPPED_ROW]);
    expect(state.store.blockedByKeywords()).toBe(true);
    expect(state.store.labelKey()).toBe(ACTION);
    expect(state.store.canMakeVideo()).toBe(false);
    expect(state.store.reasonKey()).toBe(BLOCKED);
    await state.store.makeVideo();
    expect(state.repo.requestCalls).toEqual([]);
  });

  it('makeVideo_WithNothingOffered_HasNoReasonTextEither', async () => {
    const state = await loaded({ actions: [] });
    state.editor.setKeywordFlags([MAPPED_ROW]);
    // The reason describes a *present* button: with no action at all the region is absent, not disabled-with-reason.
    expect(state.store.blockedByKeywords()).toBe(true);
    expect(state.store.reasonKey()).toBeNull();
  });

  it('makeVideo_ConfirmsOncePerClick_AndPostsTheCurrentEtagWithAnIdempotencyIntent', async () => {
    const state = await loaded();
    await state.store.makeVideo();
    expect(state.confirm).toHaveBeenCalledTimes(1);
    expect(state.confirm).toHaveBeenCalledWith(expect.objectContaining({
      titleKey: 'episodes.render.make.confirm.title', bodyKey: 'episodes.render.make.confirm.body', confirmLabelKey: ACTION,
    }));
    const call = state.repo.requestCalls[0];
    expect(state.repo.requestCalls).toHaveLength(1);
    expect(call?.episodeId).toBe(EPISODE_ID);
    expect(call?.etag).toBe(ETAG);
    // The port receives the intent OBJECT, never a bare key string (the interceptor writes the header from it).
    expect(call?.intent).toBeInstanceOf(IdempotencyIntent);
    expect(keyOf(call?.intent)).toMatch(/^[0-9a-f-]{36}$/i);
    expect(state.store.error()).toBeNull();
    expect(state.store.errorKey()).toBeNull();
    expect(state.store.submitting()).toBe(false);
  });

  it('makeVideo_Unconfirmed_SendsNothingAndKeepsNoIntent', async () => {
    const state = await loaded();
    state.confirm.mockReturnValue({ afterClosed: () => of(false) });
    await state.store.makeVideo();
    expect(state.repo.requestCalls).toEqual([]);
    expect(state.store.intent()).toBeNull();
    expect(state.store.error()).toBeNull();
  });

  it('makeVideo_OnThe202_WritesNoStatusLocally', async () => {
    const state = await loaded();
    const before = state.detail.episode();
    expect(before?.status).toBe('story_approved');
    // The 202's own read is held open on purpose: while it is pending, a status this store painted optimistically
    // would be the ONLY thing that could make the episode signal differ from the server's last answer, so `toBe` here
    // is the real "nothing painted locally" assertion (a refetch that answers first would mask such a write).
    const read = gate<EpisodeDetail>();
    state.episodes.scriptGet(() => read.promise);
    state.repo.willAcceptRender('job-42');
    await state.store.makeVideo();
    await settle();
    // The view switches to the timeline only when the episode read reports the job (F1): nothing is painted here.
    expect(state.detail.episode()).toBe(before);
    expect(state.detail.episode()?.status).toBe('story_approved');
    expect(state.detail.episode()?.nextActions).toEqual(['render']);

    // The read landing is what moves the detail, and it carries the server's own status.
    read.resolve(detailAnswer({ nextActions: ['render'] }));
    await settle();
    expect(state.detail.episode()?.status).toBe('story_approved');
  });

  it('makeVideo_DoubleClickWhileSubmitting_IssuesOneRequestAndOneDialog', async () => {
    const state = await loaded();
    const before = state.detail.episode();
    const read = gate<EpisodeDetail>();
    state.episodes.scriptGet(() => read.promise);
    state.repo.deferRender();
    const first = state.store.makeVideo();
    await settle();
    expect(state.store.submitting()).toBe(true);
    const second = state.store.makeVideo();
    await settle();
    expect(state.repo.requestCalls).toHaveLength(1);
    expect(state.confirm).toHaveBeenCalledTimes(1);
    expect(state.repo.requestPendingCount).toBe(1);
    state.repo.resolveRender('job-1');
    await Promise.all([first, second]);
    await settle();
    expect(state.store.submitting()).toBe(false);
    // The post-202 read is still pending here, so the episode must be untouched: the server's answer is the only thing
    // that may move it, never a status painted on the way.
    expect(state.detail.episode()).toBe(before);
    expect(state.detail.episode()?.status).toBe('story_approved');

    read.resolve(detailAnswer({ nextActions: ['render'] }));
    await settle();
    expect(state.detail.episode()?.status).toBe('story_approved');
  });

  it('makeVideo_LostResponse_ReusesTheSameIntentForTheRetry', async () => {
    const state = await loaded();
    state.repo.willFailRender(networkError());
    await state.store.makeVideo();
    expect(state.store.error()?.code).toBe('network_error');
    expect(state.store.intent()).not.toBeNull();

    state.repo.willAcceptRender('job-2');
    await state.store.makeVideo();
    const [first, second] = state.repo.requestCalls;
    // The very object is replayed, so the server sees one key and applies one effect (09.6.1-T1:197).
    expect(second?.intent).toBe(first?.intent);
    expect(keyOf(second?.intent)).toBe(keyOf(first?.intent));
    expect(state.store.intent()).toBeNull();
  });

  it('makeVideo_ServerFailure_KeepsTheIntentToo', async () => {
    const state = await loaded();
    state.repo.willFailRender(appError('unknown_error', 503, 'server'));
    await state.store.makeVideo();
    expect(state.store.intent()).not.toBeNull();
    expect(state.store.error()?.status).toBe(503);
  });

  it('makeVideo_TerminalRefusal_SettlesTheIntentSoTheNextClickMintsANewKey', async () => {
    const state = await loaded();
    state.repo.willFailRender(appError('validation_failed', 400, 'validation'));
    await state.store.makeVideo();
    expect(state.store.intent()).toBeNull();

    state.repo.willAcceptRender();
    await state.store.makeVideo();
    const [first, second] = state.repo.requestCalls;
    expect(state.repo.requestCalls).toHaveLength(2);
    expect(keyOf(second?.intent)).not.toBe(keyOf(first?.intent));
    expect(keyOf(second?.intent)).not.toBe('');
  });

  it('makeVideo_AfterTheJobWasCancelled_ASecondRenderSendsANewKey', async () => {
    const state = await loaded();
    await state.store.makeVideo();
    const firstKey = keyOf(state.repo.requestCalls[0]?.intent);

    // The cancel half belongs to RenderTimelineStore: the server puts the episode back to `story_approved` with the
    // render action. Reusing the spent key would replay the stored 202 and silently create no second job.
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ nextActions: ['render'] })));
    await state.detail.refetch();
    expect(state.store.canMakeVideo()).toBe(true);
    await state.store.makeVideo();

    expect(state.repo.requestCalls).toHaveLength(2);
    expect(keyOf(state.repo.requestCalls[1]?.intent)).not.toBe(firstKey);
  });

  it('makeVideo_KeywordsFlagged_HandsTheMappedRowsToTheEditorOnceAndKeepsNoCopy', async () => {
    const state = await loaded();
    const setKeywordFlags = vi.spyOn(state.editor, 'setKeywordFlags');
    state.repo.willFailRender(appError('keywords_flagged', 409, 'precondition', { problem: { errors: [KEYWORD_ROW] } }));
    await state.store.makeVideo();

    expect(setKeywordFlags).toHaveBeenCalledTimes(1);
    expect(setKeywordFlags).toHaveBeenCalledWith([MAPPED_ROW], EPISODE_ID);
    expect(state.editor.keywordFlags()).toEqual([MAPPED_ROW]);
    // WR6: the flags arm the gate and the button stays visible with its reason; the error is shown once.
    expect(state.editor.videoBlocked()).toBe(true);
    expect(state.store.canMakeVideo()).toBe(false);
    expect(state.store.reasonKey()).toBe(BLOCKED);
    expect(state.store.error()?.code).toBe('keywords_flagged');
    expect(state.store.errorKey()).toBe('errors.keywords_flagged');
    expect('keywordFlags' in state.store).toBe(false);
  });

  it('makeVideo_KeywordsFlaggedWithNoUsableRow_WritesNoFlags', async () => {
    const state = await loaded();
    const setKeywordFlags = vi.spyOn(state.editor, 'setKeywordFlags');
    state.repo.willFailRender(appError('keywords_flagged', 409, 'precondition', { problem: { errors: [{ nonsense: true }] } }));
    await state.store.makeVideo();
    // An unusable refusal must not clear or seed the flagged set with an empty list.
    expect(setKeywordFlags).not.toHaveBeenCalled();
    expect(state.editor.videoBlocked()).toBe(false);
    expect(state.store.error()?.code).toBe('keywords_flagged');
  });

  it.each([['episode_invalid_state'], ['concurrency_conflict']])('makeVideo_%s_RefetchesAndShowsTheError', async (code) => {
    const state = await loaded();
    const reads = state.episodes.getCalls.length;
    state.repo.willFailRender(appError(code, 409, 'precondition'));
    await state.store.makeVideo();
    expect(state.store.error()?.code).toBe(code);
    expect(state.store.errorKey()).toBe(`errors.${code}`);
    expect(state.store.needsRefresh()).toBe(false);
    expect(state.episodes.getCalls.length).toBe(reads + 1);
    expect(state.repo.requestCalls).toHaveLength(1);
  });

  it('makeVideo_StaleEtag_RefetchesThenOffersTheRetryOnTheFreshOne', async () => {
    const state = await loaded();
    const reads = state.episodes.getCalls.length;
    state.repo.willFailRender(appError('episode_conflict', 412, 'conflict'));
    await state.store.makeVideo();
    expect(state.store.needsRefresh()).toBe(true);
    expect(state.store.error()?.code).toBe('episode_conflict');
    expect(state.store.errorKey()).toBe('errors.episode_conflict');
    // The read is awaited before the button is released, and no second POST is ever sent by itself.
    expect(state.episodes.getCalls.length).toBe(reads + 1);
    expect(state.repo.requestCalls).toHaveLength(1);
    expect(state.store.submitting()).toBe(false);
  });

  it.each([[428, 'if_match_required'], [422, 'idempotency_key_reused']])(
    'makeVideo_%i_%s_ShowsTheGenericCopyAndWarnsInDevOnly',
    async (status, code) => {
      const state = await loaded({ devMode: true });
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
      state.repo.willFailRender(appError(code, status, 'client_bug', { traceId: 'trace-9' }));
      await state.store.makeVideo();
      expect(state.store.errorKey()).toBe('errors.unknown_error');
      // The AppError itself stays exactly as the mapper built it: no code is rewritten to reach the generic copy.
      expect(state.store.error()?.code).toBe(code);
      expect(state.store.error()?.traceId).toBe('trace-9');
      expect(warn).toHaveBeenCalled();
      expect(state.store.intent()).toBeNull();
    },
  );

  it('makeVideo_SpendCapReached_HoldsTheActionForTheRetryAfterThenReleasesIt', async () => {
    vi.useFakeTimers();
    const state = await loaded();
    state.repo.willFailRender(appError('spend_cap_reached', 429, 'rate_limit', { retryAfterSec: 3 }));
    await state.store.makeVideo();
    expect(state.store.error()?.code).toBe('spend_cap_reached');
    expect(state.store.errorKey()).toBe('errors.spend_cap_reached');
    expect(state.store.retryAfterSec()).toBe(3);
    expect(state.store.canMakeVideo()).toBe(false);

    vi.advanceTimersByTime(3_000);
    expect(state.store.retryAfterSec()).toBeNull();
    expect(state.store.canMakeVideo()).toBe(true);
    expect(state.store.error()).toBeNull();
  });

  it('makeVideo_RateLimitWithoutARetryAfter_KeepsTheActionUsable', async () => {
    const state = await loaded();
    state.repo.willFailRender(appError('rate_limited', 429, 'rate_limit'));
    await state.store.makeVideo();
    expect(state.store.retryAfterSec()).toBeNull();
    expect(state.store.canMakeVideo()).toBe(true);
  });

  it('makeVideo_HeldIntentOfAnotherEpisode_IsSettledSoTheNewEpisodeGetsItsOwnKey', async () => {
    const state = await loaded();
    state.repo.willFailRender(networkError());
    await state.store.makeVideo();
    const heldA = state.store.intent();
    expect(heldA).not.toBeNull();

    // The same page for another `:id` (the route is reused): the key of the first click is not a key for this episode.
    await openEpisode(state, OTHER_EPISODE_ID);
    state.repo.willAcceptRender('job-b');
    await state.store.makeVideo();

    const [first, second] = state.repo.requestCalls;
    expect(second?.episodeId).toBe(OTHER_EPISODE_ID);
    expect(keyOf(second?.intent)).not.toBe(keyOf(first?.intent));
    expect(keyOf(second?.intent)).not.toBe('');
    // The abandoned key is settled and released here, so no later click can replay it on a third episode.
    expect(heldA?.isSettled).toBe(true);
    expect(state.store.intent()).toBeNull();
  });

  it('makeVideo_EpisodeSwitchesWhileACallIsInFlight_LateAnswerWritesNothingAndTheNewEpisodeGetsItsOwnKey', async () => {
    const state = await loaded();
    state.repo.deferRender();
    const runningA = state.store.makeVideo();
    await settle();

    await openEpisode(state, OTHER_EPISODE_ID);
    // The in-flight hold is the store's own: nothing on B can mint a key while A's answer is outstanding.
    expect(state.store.canMakeVideo()).toBe(false);

    // A's answer arrives after the switch: it belongs to a superseded command on another episode, so it writes nothing
    // and leaves no key of A's behind for B's click to replay (the 422 path the server would answer with).
    state.repo.resolveRender('job-a', 0);
    await runningA;
    await settle();
    expect(state.store.error()).toBeNull();
    expect(state.store.intent()).toBeNull();

    state.repo.deferRender(false);
    state.repo.willAcceptRender('job-b');
    await state.store.makeVideo();
    const [first, second] = state.repo.requestCalls;
    expect(second?.episodeId).toBe(OTHER_EPISODE_ID);
    expect(keyOf(second?.intent)).not.toBe(keyOf(first?.intent));
    expect(state.store.intent()).toBeNull();
  });

  it.each([
    [408, 'Request Timeout'],
    [425, 'Too Early'],
  ] as const)('makeVideo_UnmappedTransportStatus_%i_KeepsTheKeyForTheRetry', async (status, statusText) => {
    const state = await loaded();
    // Built by the real mapper, as the wire does: an unmapped status is `unknown_error` with kind `unknown`, which is
    // not a definitive refusal — an intermediary may have answered after the API already created the job.
    const upstream = TestBed.inject(AppErrorMapper).from(new HttpErrorResponse({ status, statusText, url: '/api/v1/episodes/x/render' }));
    expect(upstream.kind).toBe('unknown');
    expect(upstream.status).toBe(status);

    state.repo.willFailRender(upstream);
    await state.store.makeVideo();
    expect(state.store.error()?.status).toBe(status);
    expect(state.store.intent()).not.toBeNull();

    state.repo.willAcceptRender('job-2');
    await state.store.makeVideo();
    const [first, second] = state.repo.requestCalls;
    // One key on the wire: the server replays its stored answer instead of a second job and a second reservation.
    expect(second?.intent).toBe(first?.intent);
    expect(keyOf(second?.intent)).toBe(keyOf(first?.intent));
    expect(state.store.intent()).toBeNull();
  });

  it.each([
    [400, 'validation_failed', 'validation'],
    [422, 'idempotency_key_reused', 'client_bug'],
    [429, 'spend_cap_reached', 'rate_limit'],
  ] as const)('makeVideo_MappedRefusal_%i_%s_SettlesTheKeySoTheRetryMintsANewOne', async (status, code, kind) => {
    const state = await loaded();
    state.repo.willFailRender(appError(code, status, kind));
    await state.store.makeVideo();
    // A definitive refusal is the whole answer: the intent is done with, so the next click is a new command.
    expect(state.store.intent()).toBeNull();

    state.repo.willAcceptRender('job-2');
    await state.store.makeVideo();
    const [first, second] = state.repo.requestCalls;
    expect(state.repo.requestCalls).toHaveLength(2);
    expect(keyOf(second?.intent)).not.toBe(keyOf(first?.intent));
  });

  it('dismiss_Teardown_DropsTheHeldIntent', async () => {
    const state = await loaded();
    state.repo.willFailRender(networkError());
    await state.store.makeVideo();
    expect(state.store.intent()).not.toBeNull();

    state.store.dismiss();
    expect(state.store.intent()).toBeNull();
    expect(state.store.submitting()).toBe(false);
  });

  it('makeVideo_AnswerAfterTeardown_WritesNothing', async () => {
    const state = await loaded();
    state.repo.deferRender();
    const running = state.store.makeVideo();
    await settle();
    expect(state.repo.requestPendingCount).toBe(1);

    state.store.dismiss();
    TestBed.resetTestingModule();
    state.repo.resolveRender('job-3');
    await running;

    expect(state.store.error()).toBeNull();
    expect(state.store.submitting()).toBe(false);
    expect(state.store.intent()).toBeNull();
  });
});
