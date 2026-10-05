import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import type { AppError } from '@core/errors';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EPISODE_GATE } from './episode-gate';
import type { GateState } from '../domain/episode-gate';
import type { EpisodeDetail, EpisodeGeneration, SeriesSummary } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { NewEpisodeStore } from './new-episode.store';

const SERIES_ID = '11111111-1111-4111-8111-111111111111';
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}
function error(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  return { code, status, kind: status === 0 ? 'network' : status === 429 ? 'rate_limit' : 'precondition', fieldErrors: {}, ...extra };
}

function rejectAppError<T>(cause: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts the port's structured AppError rejection
  return Promise.reject(cause);
}
function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
function run(overrides: Partial<EpisodeGeneration> = {}): EpisodeDetail {
  return episodeDetail({ generation: { kind: 'story', status: 'running', stage: 'queued', errorCode: null, startedAt: null, processingPaused: null, ...overrides } });
}
function setup(gate?: GateState, topic = 'ghost', id = SERIES_ID, seriesAnswer?: () => Promise<SeriesSummary>) {
  const log: string[] = [];
  const repo = new FakeEpisodesRepository(log);
  const series = new FakeSeriesOptionsRepository();
  series.summary = { ...series.summary, topic };
  if (seriesAnswer) series.scriptGet(seriesAnswer);
  const realtime = new FakeEpisodeRealtime(log);
  const sessionGeneration = signal(0);
  const navigate = vi.fn().mockResolvedValue(true);
  const gateState = signal<GateState>(gate ?? 'open');
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS,
    { provide: EpisodesRepository, useValue: repo },
    { provide: SeriesOptionsRepository, useValue: series },
    { provide: RealtimeService, useValue: realtime },
    { provide: SessionStore, useValue: { generation: sessionGeneration } },
    { provide: Router, useValue: { navigate } },
    ...(gate === undefined ? [] : [{ provide: EPISODE_GATE, useValue: gateState }]),
    NewEpisodeStore,
  ] });
  const store = TestBed.inject(NewEpisodeStore);
  store.form.controls.brief.setValue('A lantern appears at midnight.');
  store.init(id);
  return { store, repo, series, realtime, sessionGeneration, navigate, log, gateState };
}

describe('NewEpisodeStore', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('create_ValidBrief_CreatesOnceAndGenerates', async () => {
    const { store, repo, series, log } = setup();
    await settle();
    store.init(SERIES_ID);
    await Promise.all([store.create(), store.create()]);
    expect(series.getCalls).toEqual([SERIES_ID]);
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(1);
    expect(repo.getCalls).toHaveLength(1);
    expect(log).toEqual(['create', 'subscribe', 'generate', 'get']);
    expect(store.phase()).toBe('generating');
  });

  it('create_Brief1001_IsRejectedBeforeAnyRequest', async () => {
    const { store, repo } = setup();
    await settle();
    store.form.controls.brief.setValue('a'.repeat(1001));
    expect(store.briefLength()).toBe(1001);
    expect(store.canSubmit()).toBe(false);
    await store.create();
    expect(repo.createCalls).toHaveLength(0);
    expect(store.form.controls.brief.touched).toBe(true);
  });

  it.each(['', '   '])('create_EmptyBrief_%s_IsRejected', async (brief) => {
    const { store, repo } = setup();
    await settle();
    store.form.controls.brief.setValue(brief);
    await store.create();
    expect(store.canSubmit()).toBe(false);
    expect(repo.createCalls).toHaveLength(0);
  });

  it('create_Title121_IsRejected', async () => {
    const { store, repo } = setup();
    await settle();
    store.form.controls.title.setValue('t'.repeat(121));
    await store.create();
    expect(repo.createCalls).toHaveLength(0);
  });

  it('generate_NetworkError_RetryReusesTheSameKey', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('network_error', 0)));
    await settle();
    await store.create();
    expect(store.canRetry()).toBe(true);
    expect(repo.generateCalls[0]?.intent.isSettled).toBe(false);
    await store.retry();
    expect(repo.generateCalls).toHaveLength(2);
    expect(repo.generateCalls[1]?.key).toBe(repo.generateCalls[0]?.key);
    expect(repo.generateCalls[1]?.intent.isSettled).toBe(true);
  });

  it.each([500, 503])('generate_ServerError_%s_RetryReusesTheSameKey', async (status) => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('unknown_error', status, { kind: 'server' })));
    await settle();
    await store.create();
    await store.retry();
    expect(repo.generateCalls[1]?.key).toBe(repo.generateCalls[0]?.key);
  });

  it('generate_TerminalFailure_RetryMintsANewKey', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(run({ status: 'failed', errorCode: 'story_invalid_output' })));
    await settle();
    await store.create();
    expect(store.failureKey()).toBe('story.generation.error.story_invalid_output');
    expect(store.canRetry()).toBe(true);
    await store.retry();
    expect(repo.generateCalls[1]?.key).not.toBe(repo.generateCalls[0]?.key);
    expect(repo.createCalls).toHaveLength(1);
  });

  it('event_StageSequence_IsShownThroughRefetches', async () => {
    const { store, repo, realtime } = setup();
    await settle();
    await store.create();
    for (const stage of ['writing', 'checking', 'reviewing', 'revising', 'scoring', 'saving']) {
      repo.scriptGet(() => Promise.resolve(run({ stage })));
      realtime.emit(EPISODE_ID);
      await settle();
      expect(store.stageKey()).toBe(`story.generation.stage.${stage}`);
    }
    expect(repo.getCalls).toHaveLength(7);
  });

  it('event_UnknownStage_IsIgnoredAfterRefetch', async () => {
    const { store, repo, realtime } = setup();
    await settle();
    await store.create();
    repo.scriptGet(() => Promise.resolve(run({ stage: 'future_stage' })));
    realtime.emit(EPISODE_ID);
    await settle();
    expect(store.stageKey()).toBeNull();
    expect(store.phase()).toBe('generating');
  });

  it('event_IdsOnlyPayload_StillRefetchesOnce', async () => {
    const { store, repo, realtime } = setup();
    await settle();
    await store.create();
    realtime.emit(EPISODE_ID);
    await settle();
    expect(repo.getCalls).toHaveLength(2);
  });

  it('event_StoryReview_NavigatesToTheEditor', async () => {
    const { store, repo, realtime, navigate } = setup();
    await settle();
    await store.create();
    repo.scriptGet(() => Promise.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true })));
    realtime.emit(EPISODE_ID);
    await settle();
    expect(store.phase()).toBe('handoff');
    expect(navigate).toHaveBeenCalledExactlyOnceWith(['/episodes', EPISODE_ID]);
  });

  it('event_SucceededWithCurrentStory_NavigatesToTheEditor', async () => {
    const { store, repo, navigate } = setup();
    repo.scriptGet(() => Promise.resolve({ ...run({ status: 'succeeded', stage: 'done' }), hasCurrentStory: true }));
    await settle();
    await store.create();
    expect(navigate).toHaveBeenCalledWith(['/episodes', EPISODE_ID]);
  });

  it('error_409EpisodeInvalidState_ShowsCodeMessageAndActions', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('episode_invalid_state', 409)));
    repo.scriptGet(() => Promise.resolve(episodeDetail({ status: 'story_approved', generation: null, nextActions: ['render'] })));
    await settle();
    await store.create();
    expect(store.errorKey()).toBe('errors.episode_invalid_state');
    expect(store.detail()?.nextActions).toEqual(['render']);
    expect(store.canRetry()).toBe(false);
  });

  it('error_429_StartsCountdownAndDisablesSubmit', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('story_generation_limit', 429, { retryAfterSec: 7 })));
    await settle();
    await store.create();
    expect(store.retryAfterSec()).toBe(7);
    expect(store.canSubmit()).toBe(false);
    expect(store.canRetry()).toBe(false);
    await store.retry();
    expect(repo.generateCalls).toHaveLength(1);
    store.countdownElapsed();
    expect(store.canRetry()).toBe(true);
    await store.retry();
    expect(repo.generateCalls[1]?.key).not.toBe(repo.generateCalls[0]?.key);
  });

  it('destroy_UnsubscribesTheRealtimeHandler', async () => {
    const { store, realtime } = setup();
    await settle();
    await store.create();
    TestBed.resetTestingModule();
    expect(realtime.subscriptions[0]?.unsubscribed).toBe(true);
  });

  it('backgroundRefetch_CarriesNoUserAction', async () => {
    const { store, repo, realtime } = setup();
    await settle();
    await store.create();
    realtime.emit(EPISODE_ID);
    await settle();
    expect(repo.getCalls.map((call) => call.userAction)).toEqual([false, false]);
  });

  it('create_TrueCrimeSeries_NavigatesToResearchWithoutGenerate', async () => {
    const { store, repo, series, navigate } = setup(undefined, 'true_crime');
    await settle();
    await store.create();
    expect(series.getCalls).toEqual([SERIES_ID]);
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(0);
    expect(navigate).toHaveBeenCalledWith(['/episodes', EPISODE_ID, 'research']);
  });

  it('create_NetworkError_ReturnsIdleWithoutAutomaticRetry', async () => {
    const { store, repo } = setup();
    repo.scriptCreate(() => rejectAppError(error('network_error', 0)));
    await settle();
    await store.create();
    await settle();
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(0);
    expect(store.createLost()).toBe(true);
    expect(store.errorKey()).toBe('errors.network_error');
    expect(store.phase()).toBe('form');
    expect(store.submitting()).toBe(false);
    expect(store.canSubmit()).toBe(true);
  });

  it.each(['destroy', 'session'])('lateEvent_After_%s_IsIgnored', async (reason) => {
    const { store, repo, realtime, sessionGeneration, navigate } = setup();
    await settle();
    await store.create();
    const pending = deferred<EpisodeDetail>();
    repo.scriptGet(() => pending.promise);
    const handler = realtime.subscriptions[0]?.handler;
    handler?.();
    if (reason === 'destroy') TestBed.resetTestingModule();
    else sessionGeneration.update((value) => value + 1);
    pending.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true }));
    await settle();
    const count = repo.getCalls.length;
    handler?.();
    await settle();
    expect(repo.getCalls).toHaveLength(count);
    expect(navigate).not.toHaveBeenCalled();
    expect(store.phase()).toBe('generating');
  });

  it('lateCreate_AfterSessionChange_IsIgnored', async () => {
    const { store, repo, realtime, sessionGeneration } = setup();
    const pending = deferred<{ id: string; etag: string | null }>();
    repo.scriptCreate(() => pending.promise);
    await settle();
    const create = store.create();
    sessionGeneration.update((value) => value + 1);
    pending.resolve({ id: EPISODE_ID, etag: null });
    await create;
    expect(store.episodeId()).toBeNull();
    expect(realtime.subscriptions).toHaveLength(0);
    expect(repo.generateCalls).toHaveLength(0);
  });

  it('pausedState_HasNoRetryAndNoError', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(run({ processingPaused: 'provider_funds_exhausted' })));
    await settle();
    await store.create();
    expect(store.phase()).toBe('paused');
    expect(store.paused()).toBe('provider_funds_exhausted');
    expect(store.errorKey()).toBeNull();
    expect(store.failureKey()).toBeNull();
    expect(store.canRetry()).toBe(false);
    await store.retry();
    expect(repo.generateCalls).toHaveLength(1);
  });

  it('error_409StoryGenerationInProgress_GoesLive', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('story_generation_in_progress', 409)));
    repo.scriptGet(() => Promise.resolve(run({ stage: 'writing' })));
    await settle();
    await store.create();
    expect(store.phase()).toBe('generating');
    expect(store.errorKey()).toBeNull();
    expect(store.stageKey()).toBe('story.generation.stage.writing');
  });

  it('error_FactSheetRequired_DoesNotRetry', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('fact_sheet_required', 409)));
    await settle();
    await store.create();
    expect(store.factSheetRequired()).toBe(true);
    expect(store.canRetry()).toBe(false);
    await store.retry();
    expect(repo.generateCalls).toHaveLength(1);
  });

  it('error_Validation_BindsFieldsAndMapsUnmappedPointersToCopyKeys', async () => {
    const { store, repo } = setup();
    repo.scriptCreate(() => rejectAppError(error('validation_failed', 400, { kind: 'validation', fieldErrors: { brief: ['Invalid'], alien: ['Invalid'] } })));
    await settle();
    await store.create();
    expect(store.form.controls.brief.errors?.['server']).toBeDefined();
    expect(store.unmappedFieldKeys()).toEqual(['errors.validation_failed']);
    expect(store.errorKey()).toBe('errors.validation_failed');
    store.form.controls.brief.setValue('A corrected brief');
    expect(store.form.controls.brief.errors).toBeNull();
    expect(store.unmappedFieldKeys()).toEqual([]);
  });

  it('error_413PayloadTooLarge_IsNotRetried', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('payload_too_large', 413)));
    await settle();
    await store.create();
    expect(store.errorKey()).toBe('errors.payload_too_large');
    expect(store.canRetry()).toBe(false);
    expect(repo.generateCalls[0]?.intent.isSettled).toBe(true);
  });

  it('gateToken_Blocked_DisablesCreate', async () => {
    const { store, repo, gateState } = setup('sheet_not_approved');
    await settle();
    expect(store.gateBlocked()).toBe(true);
    expect(store.canSubmit()).toBe(false);
    await store.create();
    expect(repo.createCalls).toHaveLength(0);
    gateState.set('open');
    expect(store.canSubmit()).toBe(true);
  });

  it('gateToken_Absent_AllowsGenerate', async () => {
    const { store, repo } = setup();
    await settle();
    expect(store.gateBlocked()).toBe(false);
    await store.create();
    expect(repo.generateCalls).toHaveLength(1);
  });

  it('hub_Resync_RefreshesWithoutPolling', async () => {
    const { store, repo, realtime } = setup();
    await settle();
    await store.create();
    await settle();
    expect(repo.getCalls).toHaveLength(1);
    realtime.resync();
    await settle();
    expect(repo.getCalls).toHaveLength(2);
  });

  it('create_BoundaryBrief1000AndTitle120_AreAccepted', async () => {
    const { store, repo } = setup();
    await settle();
    store.form.controls.brief.setValue('a'.repeat(1000));
    store.form.controls.title.setValue('t'.repeat(120));
    expect(store.canSubmit()).toBe(true);
    await store.create();
    expect(repo.createCalls).toHaveLength(1);
  });

  it('generate_Definite4xx_RetryMintsNewIntent', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejectAppError(error('unknown_error', 400)));
    await settle();
    await store.create();
    expect(repo.generateCalls[0]?.intent.isSettled).toBe(true);
    await store.retry();
    expect(repo.generateCalls[1]?.key).not.toBe(repo.generateCalls[0]?.key);
  });

  it('generate_IdempotencyKeyReused_ShowsGenericErrorAndTraceWithoutTheKey', async () => {
    const { store, repo } = setup();
    const diagnostic = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      repo.scriptGenerate(() => rejectAppError(error('idempotency_key_reused', 422, { traceId: 'trace-safe' })));
      await settle();
      await store.create();
      expect(store.errorKey()).toBe('errors.unknown_error');
      expect(store.traceId()).toBe('trace-safe');
      expect(store.canRetry()).toBe(false);
      const key = repo.generateCalls[0]?.key;
      expect(key).toBeDefined();
      expect(JSON.stringify(diagnostic.mock.calls)).not.toContain(key);
      expect(repo.generateCalls[0]?.intent.isSettled).toBe(true);
    } finally {
      diagnostic.mockRestore();
    }
  });

  it('event_DuringPendingRefetch_CoalescesAndDiscardsInvalidatedDetail', async () => {
    const { store, repo, realtime, navigate } = setup();
    await settle();
    await store.create();
    const pending = deferred<EpisodeDetail>();
    repo.scriptGet(() => pending.promise);
    realtime.emit(EPISODE_ID);
    repo.scriptGet(() => Promise.resolve(run({ stage: 'saving' })));
    realtime.emit(EPISODE_ID);
    expect(repo.getCalls).toHaveLength(2);
    await settle();
    pending.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true }));
    await settle();
    expect(store.stageKey()).toBe('story.generation.stage.saving');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('generate_LateSuccessAfterSessionChange_DoesNotSeedOrNavigate', async () => {
    const { store, repo, sessionGeneration, navigate } = setup();
    const pending = deferred<void>();
    repo.scriptGenerate(() => pending.promise);
    await settle();
    const creating = store.create();
    await settle();
    sessionGeneration.update((value) => value + 1);
    pending.resolve(undefined);
    await creating;
    expect(repo.getCalls).toHaveLength(0);
    expect(navigate).not.toHaveBeenCalled();
    expect(store.canRetry()).toBe(false);
  });

  it.each([
    ['fact_sheet_required', 'errors.fact_sheet_required'],
    ['future_error', 'errors.unknown_error'],
  ])('generation_ErrorCode_%s_UsesApplicationCatalogueFallback', async (code, key) => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(run({ status: 'failed', errorCode: code })));
    await settle();
    await store.create();
    expect(store.failureKey()).toBe(key);
  });

  it.each(['false', 'reject'])('handoff_EditorNavigation_%s_RetainsEpisodeAndAllowsLaterResync', async (outcome) => {
    const { store, repo, realtime, navigate } = setup();
    repo.scriptGet(() => Promise.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true, generation: null })));
    if (outcome === 'false') navigate.mockResolvedValueOnce(false);
    else navigate.mockRejectedValueOnce(new Error('Navigation cancelled'));
    await settle();
    await store.create();
    expect(store.phase()).toBe('failed');
    expect(store.errorKey()).toBe('errors.unknown_error');
    expect(store.episodeId()).toBe(EPISODE_ID);
    expect(store.createLost()).toBe(false);
    expect(store.canRetry()).toBe(false);
    expect(repo.generateCalls).toHaveLength(1);
    repo.scriptGet(() => Promise.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true })));
    realtime.resync();
    await settle();
    expect(navigate).toHaveBeenCalledTimes(2);
    expect(store.phase()).toBe('handoff');
    expect(store.errorKey()).toBeNull();
  });

  it.each(['false', 'reject'])('handoff_ResearchNavigation_%s_KeepsResearchActionWithoutCreateLost', async (outcome) => {
    const { store, repo, navigate } = setup(undefined, 'true_crime');
    if (outcome === 'false') navigate.mockResolvedValueOnce(false);
    else navigate.mockRejectedValueOnce(new Error('Navigation cancelled'));
    await settle();
    await store.create();
    expect(store.phase()).toBe('failed');
    expect(store.episodeId()).toBe(EPISODE_ID);
    expect(store.factSheetRequired()).toBe(true);
    expect(store.errorKey()).toBe('errors.unknown_error');
    expect(store.createLost()).toBe(false);
    expect(store.canRetry()).toBe(false);
    await store.create();
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(0);
  });

  it('generation_RunLimit_UsesCopyWithoutCountdownParameters', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(run({ status: 'failed', errorCode: 'story_generation_limit' })));
    await settle();
    await store.create();
    expect(store.failureKey()).toBe('errors.story_generation_limit');
    expect(store.retryAfterSec()).toBeNull();
    expect(store.canRetry()).toBe(true);
  });

  it('create_503_ReportsUnknownOutcomeWithoutAutomaticReplay', async () => {
    const { store, repo } = setup();
    repo.scriptCreate(() => rejectAppError(error('unknown_error', 503, { kind: 'server' })));
    await settle();
    await store.create();
    await settle();
    expect(store.createLost()).toBe(true);
    expect(store.phase()).toBe('form');
    expect(store.canSubmit()).toBe(true);
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(0);
  });

  it('retry_UnknownOutcomeThenPreviousFailedRun_ReusesTheSameIntent', async () => {
    const { store, repo, realtime } = setup();
    repo.scriptGet(() => Promise.resolve(run({ status: 'failed', errorCode: 'story_invalid_output' })));
    await settle();
    await store.create();
    repo.scriptGenerate(() => rejectAppError(error('network_error', 0)));
    await store.retry();
    const retryKey = repo.generateCalls[1]?.key;
    repo.scriptGet(() => Promise.resolve(run({ status: 'failed', errorCode: 'story_invalid_output' })));
    realtime.resync();
    await settle();
    expect(store.canRetry()).toBe(true);
    expect(repo.generateCalls[1]?.intent.isSettled).toBe(false);
    expect(store.errorKey()).toBeNull();
    await store.retry();
    expect(repo.generateCalls[2]?.key).toBe(retryKey);
  });

  it('retry_PendingPost_IgnoresPreviousFailureUntilTheSeedingGet', async () => {
    const { store, repo, realtime } = setup();
    repo.scriptGet(() => Promise.resolve(run({ status: 'failed', errorCode: 'story_invalid_output' })));
    await settle();
    await store.create();
    const post = deferred<void>();
    repo.scriptGenerate(() => post.promise);
    const retrying = store.retry();
    realtime.resync();
    await settle();
    expect(store.phase()).toBe('generating');
    expect(store.failureKey()).toBeNull();
    expect(store.canRetry()).toBe(false);
    expect(repo.getCalls).toHaveLength(1);
    post.resolve(undefined);
    await retrying;
    expect(repo.getCalls).toHaveLength(2);
    expect(store.phase()).toBe('generating');
  });

  it.each(['failed', 'cancelled'])('generation_%sWithoutCode_ClearsStaleCommandErrorAndShowsFallback', async (status) => {
    const { store, repo, realtime } = setup();
    repo.scriptGenerate(() => rejectAppError(error('network_error', 0, { traceId: 'stale-trace' })));
    await settle();
    await store.create();
    repo.scriptGet(() => Promise.resolve(run({ status, errorCode: null })));
    realtime.resync();
    await settle();
    expect(store.phase()).toBe('failed');
    expect(store.failureKey()).toBe('story.generation.error.story_generation_failed');
    expect(store.errorKey()).toBeNull();
    expect(store.traceId()).toBeNull();
  });

  it('series_InitFailure_DisablesFormAndExplicitReloadRecovers', async () => {
    const { store, repo, series } = setup(undefined, 'ghost', SERIES_ID,
      () => rejectAppError(error('unknown_error', 503, { traceId: 'series-trace' })));
    await settle();
    expect(store.seriesFailed()).toBe(true);
    expect(store.canSubmit()).toBe(false);
    expect(store.errorKey()).toBe('errors.unknown_error');
    expect(store.traceId()).toBe('series-trace');
    await store.create();
    expect(repo.createCalls).toHaveLength(0);
    await store.reloadSeries();
    expect(series.getCalls).toEqual([SERIES_ID, SERIES_ID]);
    expect(store.seriesFailed()).toBe(false);
    expect(store.canSubmit()).toBe(true);
    expect(store.errorKey()).toBeNull();
    expect(store.traceId()).toBeNull();
  });

  it.each(['', 'not-a-uuid', `${SERIES_ID}/../other`, `${SERIES_ID} `])('series_InvalidRoute_%s_DoesNotSendAnyRequest', async (id) => {
    const { store, repo, series } = setup(undefined, 'ghost', id);
    await settle();
    expect(store.seriesFailed()).toBe(true);
    expect(store.errorKey()).toBe('errors.not_found');
    expect(store.canSubmit()).toBe(false);
    await store.reloadSeries();
    await store.create();
    expect(series.getCalls).toHaveLength(0);
    expect(repo.createCalls).toHaveLength(0);
  });

  it.each([
    error('story_generation_limit', 429, { retryAfterSec: 30 }),
    error('validation_failed', 400, { fieldErrors: { brief: ['Invalid'], alien: ['Invalid'] } }),
    error('idempotency_key_reused', 422),
    error('fact_sheet_required', 409),
  ])('backgroundRead_%s_OnlySetsMessageAndTrace', async (cause) => {
    const { store, repo, realtime } = setup();
    const diagnostic = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      await settle();
      await store.create();
      const phase = store.phase();
      repo.scriptGet(() => rejectAppError({ ...cause, traceId: 'read-trace' }));
      realtime.resync();
      await settle();
      expect(store.errorKey()).not.toBeNull();
      expect(store.traceId()).toBe('read-trace');
      expect(store.phase()).toBe(phase);
      expect(store.retryAfterSec()).toBeNull();
      expect(store.form.controls.brief.errors).toBeNull();
      expect(store.unmappedFieldKeys()).toEqual([]);
      expect(store.factSheetRequired()).toBe(false);
      expect(diagnostic).not.toHaveBeenCalled();
    } finally {
      diagnostic.mockRestore();
    }
  });

  it('form_TypingWithNoServerErrors_PreservesEmptyFieldErrorSignal', async () => {
    const { store } = setup();
    await settle();
    const empty = store.unmappedFieldKeys();
    store.form.controls.brief.setValue('A new brief');
    store.form.controls.title.setValue('A new title');
    expect(store.unmappedFieldKeys()).toBe(empty);
  });

  it('seedingGet_InvalidationsWhileInFlight_SchedulesOnlyOneSerialFollowup', async () => {
    const { store, repo, realtime, navigate } = setup();
    const seed = deferred<EpisodeDetail>();
    repo.scriptGet(() => seed.promise);
    repo.scriptGet(() => Promise.resolve(run({ stage: 'writing' })));
    await settle();
    const creating = store.create();
    await settle();
    expect(repo.getCalls).toHaveLength(1);
    realtime.emit(EPISODE_ID);
    realtime.resync();
    realtime.emit(EPISODE_ID);
    await settle();
    expect(repo.getCalls).toHaveLength(1);
    seed.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true }));
    await creating;
    await settle();
    expect(repo.getCalls).toHaveLength(2);
    expect(store.stageKey()).toBe('story.generation.stage.writing');
    expect(navigate).not.toHaveBeenCalled();
  });
});
