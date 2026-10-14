import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { EpisodeDetail, EpisodeStatus } from '../domain/episode.model';
import type { ApprovalOutcome, StorySnapshot } from '../domain/story.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { StoryRepository } from '../domain/story.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeStoryRepository, approvalOutcome, attestation, storySnapshot } from '../testing/fake-story.repository';
import { ApprovalStore } from './approval.store';
import { EpisodeDetailStore } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

const ETAG = '"sv-33333333-3333-4333-8333-333333333333"';

function appError(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  const kind = status === 400 ? 'precondition' : status === 409 ? 'precondition' : status === 412 ? 'conflict' : status === 428 ? 'client_bug' : status === 429 ? 'rate_limit' : 'unknown';
  return { code, status, kind, fieldErrors: {}, ...extra };
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
/** A `409 keywords_flagged` body: rows are `{segment, index, rule}`, never a field-error map (03.2.2-T2). */
const KEYWORD_ROW = { segment: 'p:2', index: 1, rule: 'keyword_name_detected' };

interface StoreState {
  readonly store: ApprovalStore;
  readonly editor: StoryEditorStore;
  readonly detail: EpisodeDetailStore;
  readonly episodes: FakeEpisodesRepository;
  readonly stories: FakeStoryRepository;
  readonly sessionGeneration: WritableSignal<number>;
}

interface SetupOptions {
  readonly status?: EpisodeStatus;
  readonly actions?: readonly string[];
  readonly snapshot?: StorySnapshot | null;
  /** The answer of the read a command makes after its own failure (the fake's default state is `story_generating`). */
  readonly refetch?: Partial<EpisodeDetail>;
}
function detailAnswer(overrides: Partial<EpisodeDetail> = {}): EpisodeDetail {
  return episodeDetail({ id: EPISODE_ID, status: 'story_review', hasCurrentStory: true, currentStory: storySnapshot(), nextActions: ['edit_story'], ...overrides });
}
function setup(options: SetupOptions = {}): StoreState {
  const snapshot = options.snapshot === undefined ? storySnapshot() : options.snapshot;
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(detailAnswer({
    status: options.status ?? 'story_review', nextActions: options.actions ?? ['edit_story'], currentStory: snapshot, hasCurrentStory: snapshot !== null,
  })));
  if (options.refetch !== undefined) episodes.scriptGet(() => Promise.resolve(detailAnswer(options.refetch)));
  const stories = new FakeStoryRepository();
  const realtime = new FakeEpisodeRealtime();
  const sessionGeneration = signal(0);
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, EpisodeDetailStore, StoryEditorStore, ApprovalStore,
    { provide: EpisodesRepository, useValue: episodes }, { provide: StoryRepository, useValue: stories },
    { provide: RealtimeService, useValue: realtime }, { provide: SessionStore, useValue: { generation: sessionGeneration } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  return {
    store: TestBed.inject(ApprovalStore), editor: TestBed.inject(StoryEditorStore), detail: TestBed.inject(EpisodeDetailStore),
    episodes, stories, sessionGeneration,
  };
}
async function loaded(options: SetupOptions = {}): Promise<StoreState> {
  const state = setup(options); await state.editor.load(EPISODE_ID); return state;
}

describe('ApprovalStore', () => {
  afterEach(() => { vi.useRealTimers(); TestBed.resetTestingModule(); });

  it('approve_WithoutAnAttestationBlock_OmitsTheFieldAndRecordsTheServerAnswer', async () => {
    const state = await loaded({ refetch: { status: 'story_approved', nextActions: ['reopen_story'] } });
    expect(state.store.canApprove()).toBe(true);
    expect(state.store.attestation()).toBeNull();
    await state.store.approve(EPISODE_ID);
    expect(state.stories.approveCalls).toEqual([{ episodeId: EPISODE_ID, etag: ETAG, attestation: undefined }]);
    expect(state.store.status()).toBe('story_approved');
    expect(state.store.outcome()).toBe('approved');
    expect(state.episodes.getCalls).toHaveLength(2);
  });

  it('approve_WithARequiredAttestation_SendsTheServerTextVersionVerbatim', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation({ textVersion: '2026-10-01.1' }) }) });
    state.store.setAccepted(true);
    expect(state.store.canApprove()).toBe(true);
    await state.store.approve(EPISODE_ID);
    expect(state.stories.approveCalls).toEqual([{ episodeId: EPISODE_ID, etag: ETAG, attestation: { accepted: true, textVersion: '2026-10-01.1' } }]);
  });

  it('approve_AnAttestationAcceptedOnTheServer_IsNotTickedOnTheUsersBehalf', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation({ acceptedAt: '2026-10-14T10:00:00Z' }) }) });
    expect(state.store.attestation()?.acceptedAt).toBe('2026-10-14T10:00:00Z');
    expect(state.store.attestationAccepted()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.stories.approveCalls).toHaveLength(0);
    state.store.setAccepted(true);
    expect(state.store.canApprove()).toBe(true);
  });

  it('approve_AnAttestationWithNoTextAtAll_IsNeverAttestedTo', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation({ text: null }) }) });
    state.store.setAccepted(true);
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.stories.approveCalls).toHaveLength(0);
  });

  it('attestationTick_SurvivesARefetchOfTheSameTextVersion_AndTheBodyCarriesThatVersion', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation({ textVersion: 'v1' }) }) });
    state.store.setAccepted(true);
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ currentStory: storySnapshot({ attestation: attestation({ textVersion: 'v1' }) }) })));
    await state.detail.refetch();
    expect(state.store.attestationAccepted()).toBe(true);
    expect(state.store.canApprove()).toBe(true);
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    expect(state.stories.approveCalls[0]?.attestation).toEqual({ accepted: true, textVersion: 'v1' });
  });

  it('attestationTick_IsTakenBackWhenTheServerMovesTheTextVersion', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation({ textVersion: 'v1' }) }) });
    state.store.setAccepted(true);
    expect(state.store.attestationAccepted()).toBe(true);
    // A realtime refresh brings a new block text: the tick the user gave was for the old one.
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ currentStory: storySnapshot({ attestation: attestation({ textVersion: 'v2' }) }) })));
    await state.detail.refetch();
    expect(state.store.attestationAccepted()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.stories.approveCalls).toHaveLength(0);
  });

  it('attestationTick_WithNoBlockOnScreen_RecordsNothing', async () => {
    const state = await loaded();
    state.store.setAccepted(true);
    expect(state.store.attestation()).toBeNull();
    expect(state.store.attestationAccepted()).toBe(false);
  });

  it('approve_FromAnApprovedStory_IsRefused', async () => {
    const state = await loaded({ status: 'story_approved', actions: ['reopen_story'] });
    expect(state.store.canApprove()).toBe(false);
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.stories.approveCalls).toHaveLength(0);
  });

  it('approve_WithADirtyOrSavingDraft_IsDisabled', async () => {
    const state = await loaded();
    state.editor.setSegmentText('title', 'Changed title');
    expect(state.editor.dirty()).toBe(true);
    expect(state.store.canApprove()).toBe(false);
    state.editor.setSegmentText('title', storySnapshot().content.title);
    expect(state.store.canApprove()).toBe(true);
    state.editor.saving.set(true);
    expect(state.store.canApprove()).toBe(false);
  });

  it('approve_WithAQualityHintSet_IsStillEnabled_HintsAreAdvisoryOnly', async () => {
    const hints = [{ ruleId: 'rule', severity: 'advisory' as const, kind: 'taste' as const, segment: null, messageKey: 'quality.hook_short', evidence: 'x', repairDirection: null }];
    const state = await loaded({ snapshot: storySnapshot({ qualityHints: hints }) });
    expect(state.store.hintCount()).toBe(1);
    expect(state.store.canOpenApprove()).toBe(true);
    expect(state.store.canApprove()).toBe(true);
    await state.store.approve(EPISODE_ID);
    expect(state.stories.approveCalls).toHaveLength(1);
  });

  it('approve_WithAKeywordFlag_IsDisabledAndMakeVideoStaysBlocked', async () => {
    const state = await loaded();
    expect(state.editor.setKeywordFlags([{ address: 'p:2', index: 1, rule: 'keyword_name_detected' }], EPISODE_ID)).toBe(true);
    expect(state.editor.videoBlocked()).toBe(true);
    expect(state.store.canApprove()).toBe(false);
  });

  it('approve_AfterAStoryModeratedRefusal_IsHeldUntilTheDraftChanges', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('story_moderated', 409)));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.blocked()?.code).toBe('story_moderated');
    // Nothing else explains a disabled button here: the draft is clean, the story is in review, no flag is set.
    expect(state.editor.dirty()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
    expect(state.store.status()).toBe('story_review');
    state.editor.setSegmentText('title', 'A new draft');
    expect(state.store.canApprove()).toBe(false);
    // Back to the baseline: the draft on screen is no longer the one the verdict was about, so the hold is released.
    state.editor.setSegmentText('title', storySnapshot().content.title);
    expect(state.editor.dirty()).toBe(false);
    expect(state.store.canApprove()).toBe(true);
  });

  it('approve_400_StoryAttestationRequired_MarksTheBoxAndRefetchesOnce', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation() }) });
    state.store.setAccepted(true);
    state.stories.scriptApprove(() => rejected(appError('story_attestation_required', 400)));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.checkboxInvalid()).toBe(true);
    expect(state.store.attestationAccepted()).toBe(false);
    expect(state.store.error()?.code).toBe('story_attestation_required');
    expect(state.store.blocked()).toBeNull();
    expect(state.episodes.getCalls).toHaveLength(2);
  });

  it.each(['episode_invalid_state', 'story_fact_sheet_outdated', 'story_sources_required', 'story_too_similar_to_source', 'story_moderated'])(
    'approve_409_%s_IsTheBlockedRegionAndNeverAConflict', async (code) => {
      const state = await loaded({ refetch: {} });
      state.stories.scriptApprove(() => rejected(appError(code, 409)));
      expect(await state.store.approve(EPISODE_ID)).toBe(false);
      expect(state.store.blocked()?.code).toBe(code);
      expect(state.store.conflict()).toBe(false);
      // The refusal is a gate, not a version conflict: the story status is untouched.
      expect(state.store.status()).toBe('story_review');
      expect(state.episodes.getCalls).toHaveLength(code === 'episode_invalid_state' ? 2 : 1);
    },
  );

  it('approve_409_KeywordsFlagged_HandsTheRowsToTheEditorStoreOnceAndKeepsNoCopy', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('keywords_flagged', 409, { problem: { code: 'keywords_flagged', errors: [KEYWORD_ROW] } })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.blocked()?.code).toBe('keywords_flagged');
    expect(state.store.error()).toBeNull();
    expect(state.store.canApprove()).toBe(false);
    expect(state.editor.videoBlocked()).toBe(true);
    expect(state.editor.keywordFlags()).toHaveLength(1);
    expect(state.stories.approveCalls).toHaveLength(1);
  });

  it('keywords_flagged_409_IsRecoveredByAFixAndASave_ThenTheSecondApproveSucceeds', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('keywords_flagged', 409, { problem: { code: 'keywords_flagged', errors: [KEYWORD_ROW] } })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.blocked()?.code).toBe('keywords_flagged');
    expect(state.store.error()).toBeNull();
    expect(state.editor.keywordFlags()).toHaveLength(1);
    expect(state.editor.videoBlocked()).toBe(true);
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
    // The fix: the flagged chip goes, and the save is what clears the flag — an edit on its own never does (WR6).
    state.editor.setSegmentText('title', 'The fixed lantern');
    state.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-2"' })));
    await state.editor.saveVersion();
    expect(state.editor.keywordFlags()).toEqual([]);
    expect(state.editor.videoBlocked()).toBe(false);
    expect(state.store.canOpenApprove()).toBe(true);
    expect(state.store.canApprove()).toBe(true);
    // The detail read the page makes after a save is what hands the fresh story ETag to the next command.
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ currentStory: storySnapshot({ etag: '"sv-2"' }) })));
    await state.detail.refetch();
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    expect(state.stories.approveCalls).toHaveLength(2);
    expect(state.stories.approveCalls[1]?.etag).toBe('"sv-2"');
    expect(state.store.status()).toBe('story_approved');
  });

  it('approve_409_KeywordsFlaggedWithoutAUsableRow_IsTheErrorCopy', async () => {
    const state = await loaded();
    const applied = vi.spyOn(state.editor, 'applyKeywordsFlaggedProblem');
    state.stories.scriptApprove(() => rejected(appError('keywords_flagged', 409, { problem: { code: 'keywords_flagged', errors: [{ segment: 'nope', index: 1, rule: 'keyword_invalid' }] } })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(applied).toHaveBeenCalledTimes(1);
    expect(applied).toHaveBeenCalledWith(expect.objectContaining({ code: 'keywords_flagged' }), EPISODE_ID);
    applied.mockRestore();
    // No usable row means no chip was written, so the mapped copy is the only thing the dialog can show.
    expect(state.editor.keywordFlags()).toEqual([]);
    expect(state.editor.videoBlocked()).toBe(false);
    expect(state.store.error()?.code).toBe('keywords_flagged');
    expect(state.store.blocked()?.code).toBe('keywords_flagged');
  });

  it('approve_412_RequestsTheConflictDialogAndLeavesTheStatusAlone', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('story_version_conflict', 412)));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.conflict()).toBe(true);
    expect(state.store.blocked()).toBeNull();
    expect(state.store.error()).toBeNull();
    expect(state.store.status()).toBe('story_review');
  });

  it('approve_429_HoldsTheButtonForTheServerWaitAndReleasesIt', async () => {
    vi.useFakeTimers();
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('rate_limited', 429, { retryAfterSec: 30 })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.retryAfterSec()).toBe(30);
    expect(state.store.error()?.code).toBe('rate_limited');
    expect(state.store.canApprove()).toBe(false);
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.stories.approveCalls).toHaveLength(1);
    vi.advanceTimersByTime(30_000);
    expect(state.store.retryAfterSec()).toBeNull();
    // The wait was the whole message: the 429 the user was looking at is done with when it expires.
    expect(state.store.error()).toBeNull();
    expect(state.store.canApprove()).toBe(true);
  });

  it('approve_429_LeavesALaterUnrelatedErrorAlone', async () => {
    vi.useFakeTimers();
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('rate_limited', 429, { retryAfterSec: 30 })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    state.store.error.set(appError('service_unavailable', 503));
    vi.advanceTimersByTime(30_000);
    expect(state.store.retryAfterSec()).toBeNull();
    expect(state.store.error()?.code).toBe('service_unavailable');
  });

  it('approve_428_IsDevOnlyBreadcrumbAndAGenericError', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('if_match_required', 428, { traceId: 'trace-1' })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.error()?.code).toBe('if_match_required');
    expect(state.store.blocked()).toBeNull();
    expect(warn).toHaveBeenCalledWith('if_match_required', 'trace-1');
    warn.mockRestore();
  });

  it('approve_AnUnmappedFailure_IsExposedAsError', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('service_unavailable', 503)));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.error()?.code).toBe('service_unavailable');
    expect(state.store.blocked()).toBeNull();
  });

  it('approve_WritesTheStatusOnlyAfterTheServerAnswered', async () => {
    const state = await loaded({ refetch: { status: 'story_approved', nextActions: ['reopen_story'] } });
    const pending = deferred<ApprovalOutcome>();
    state.stories.scriptApprove(() => pending.promise);
    const call = state.store.approve(EPISODE_ID);
    await settle();
    expect(state.store.approving()).toBe(true);
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.status()).toBe('story_review');
    expect(state.store.outcome()).toBeNull();
    // Re-entry while the request is in flight is refused: one approve, never two.
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    pending.resolve(approvalOutcome());
    expect(await call).toBe(true);
    expect(state.stories.approveCalls).toHaveLength(1);
    expect(state.store.status()).toBe('story_approved');
    expect(state.store.outcome()).toBe('approved');
  });

  it('approve_ReadsTheEtagAtCallTimeAndPrefersTheRefreshedDetail', async () => {
    const state = await loaded();
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ currentStory: storySnapshot({ etag: '"sv-2"' }) })));
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    await settle();
    expect(state.stories.approveCalls[0]?.etag).toBe(ETAG);
    // The record is dropped once the detail landed, so a story that moved on is never masked by it.
    expect(state.store.status()).toBe('story_review');
    expect(state.store.canApprove()).toBe(true);
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    expect(state.stories.approveCalls[1]?.etag).toBe('"sv-2"');
  });

  it('approve_AnAnswerThatOutlivedItsEpisode_WritesNothing', async () => {
    const state = await loaded();
    const pending = deferred<ApprovalOutcome>();
    state.stories.scriptApprove(() => pending.promise);
    const call = state.store.approve(EPISODE_ID);
    await settle();
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ id: 'other-episode', status: 'story_review' })));
    await state.detail.load('other-episode');
    pending.resolve(approvalOutcome());
    expect(await call).toBe(false);
    expect(state.store.outcome()).toBeNull();
    expect(state.store.status()).toBe('story_review');
    expect(state.store.approving()).toBe(false);
  });

  it('approve_AnAnswerAfterTheSessionEnded_WritesNothing', async () => {
    const state = await loaded();
    const pending = deferred<ApprovalOutcome>();
    state.stories.scriptApprove(() => pending.promise);
    const call = state.store.approve(EPISODE_ID);
    await settle();
    state.sessionGeneration.update((value) => value + 1);
    TestBed.tick();
    pending.resolve(approvalOutcome());
    expect(await call).toBe(false);
    expect(state.store.outcome()).toBeNull();
    expect(state.store.status()).toBeNull();
    expect(state.store.approving()).toBe(false);
  });

  it('reopen_Success_ReturnsToReviewAndMakesTheDraftEditable', async () => {
    const state = await loaded({ status: 'story_approved', actions: ['reopen_story'] });
    expect(state.editor.readOnly()).toBe(true);
    expect(state.store.canReopen()).toBe(true);
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer()));
    expect(await state.store.reopen(EPISODE_ID)).toBe(true);
    expect(state.stories.reopenCalls).toEqual([{ episodeId: EPISODE_ID, etag: ETAG }]);
    expect(state.store.status()).toBe('story_review');
    expect(state.store.outcome()).toBe('reopened');
    await settle();
    expect(state.detail.nextActions()).toContain('edit_story');
    expect(state.editor.readOnly()).toBe(false);
  });

  it('reopen_FromAStoryInReview_IsRefused', async () => {
    const state = await loaded();
    expect(state.store.canReopen()).toBe(false);
    expect(await state.store.reopen(EPISODE_ID)).toBe(false);
    expect(state.stories.reopenCalls).toHaveLength(0);
  });

  it('reopen_412_RequestsTheConflictDialog', async () => {
    const state = await loaded({ status: 'story_approved', actions: ['reopen_story'] });
    state.stories.scriptReopen(() => rejected(appError('story_version_conflict', 412)));
    expect(await state.store.reopen(EPISODE_ID)).toBe(false);
    expect(state.store.conflict()).toBe(true);
    expect(state.store.status()).toBe('story_approved');
  });

  it('reopen_IsUnavailableUntilTheApprovesDetailReadLands', async () => {
    const state = await loaded();
    const fresh = deferred<EpisodeDetail>();
    state.episodes.scriptGet(() => fresh.promise);
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    expect(state.store.status()).toBe('story_approved');
    // The detail still holds the pre-approve ETag: a reopen now would be refused as a conflict, so it is held back.
    expect(state.store.canReopen()).toBe(false);
    expect(state.store.canOpenApprove()).toBe(false);
    expect(await state.store.reopen(EPISODE_ID)).toBe(false);
    expect(state.stories.reopenCalls).toHaveLength(0);
    fresh.resolve(detailAnswer({ status: 'story_approved', nextActions: ['reopen_story'], currentStory: storySnapshot({ etag: '"sv-2"' }) }));
    await settle();
    expect(state.store.canReopen()).toBe(true);
    expect(await state.store.reopen(EPISODE_ID)).toBe(true);
    // Read at call time from the detail that landed: never the ETag the story had before the approve.
    expect(state.stories.reopenCalls[0]?.etag).toBe('"sv-2"');
  });

  it('refetchFailure_KeepsTheServersAnswerAndTheEtagTheNextCommandMustSend', async () => {
    const state = await loaded();
    state.episodes.scriptGet(() => rejected<EpisodeDetail>(appError('service_unavailable', 503)));
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    expect(state.store.status()).toBe('story_approved');
    await settle();
    // The read failed, so the detail on screen is still the pre-approve story: the server's answer stays the truth.
    expect(state.detail.error()?.code).toBe('service_unavailable');
    expect(state.store.status()).toBe('story_approved');
    expect(state.store.canOpenApprove()).toBe(false);
    // Reopen is offered from that state, and it must send the ETag the approve answered with, not the stale one.
    expect(state.store.canReopen()).toBe(true);
    state.episodes.scriptGet(() => Promise.resolve(detailAnswer({ status: 'story_review', nextActions: ['edit_story'], currentStory: storySnapshot({ etag: '"sv-3"' }) })));
    expect(await state.store.reopen(EPISODE_ID)).toBe(true);
    expect(state.stories.reopenCalls[0]).toEqual({ episodeId: EPISODE_ID, etag: approvalOutcome().etag });
    // The read the reopen starts lands, and a read that lands is the newer truth: the record goes with it.
    await settle();
    expect(state.store.status()).toBe('story_review');
    expect(state.store.canOpenApprove()).toBe(true);
  });

  it('aSupersededDetailRead_HoldsTheButtonsAndLeavesTheAnswerToTheNewerRead', async () => {
    const state = await loaded();
    const ours = deferred<EpisodeDetail>();
    state.episodes.scriptGet(() => ours.promise);
    expect(await state.store.approve(EPISODE_ID)).toBe(true);
    // Something else reads the detail while the command's own read is out: that read now owns the answer.
    const newer = deferred<EpisodeDetail>();
    state.episodes.scriptGet(() => newer.promise);
    const reading = state.detail.refetch();
    expect(state.store.status()).toBe('story_approved');
    // A superseded read is not a failure, so the hold is not released here: it waits for the read that owns the answer.
    expect(state.store.canReopen()).toBe(false);
    newer.resolve(detailAnswer({ status: 'story_approved', nextActions: ['reopen_story'], currentStory: storySnapshot({ etag: '"sv-2"' }) }));
    await reading; await settle();
    expect(state.store.canReopen()).toBe(true);
    ours.resolve(detailAnswer({ status: 'story_review', currentStory: storySnapshot({ etag: '"sv-9"' }) }));
    await settle();
    TestBed.tick();
    // Ours answered late and was ignored: the newer read's answer stands, and the record went with the hold.
    expect(state.detail.episode()?.currentStory?.etag).toBe('"sv-2"');
    expect(state.store.status()).toBe('story_approved');
    expect(state.store.canOpenApprove()).toBe(false);
  });

  it('canOpenApprove_IsCanApproveWithoutTheTick', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation() }) });
    expect(state.store.canOpenApprove()).toBe(true);
    expect(state.store.canApprove()).toBe(false);
    state.store.setAccepted(true);
    expect(state.store.canOpenApprove()).toBe(true);
    expect(state.store.canApprove()).toBe(true);
    state.store.setAccepted(false);
    expect(state.store.canOpenApprove()).toBe(true);
    expect(state.store.canApprove()).toBe(false);
  });

  it('canOpenApprove_WithAnApprovedStory_IsFalse', async () => {
    const state = await loaded({ status: 'story_approved', actions: ['reopen_story'] });
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
  });

  it('canOpenApprove_WithNoCurrentStory_IsFalse', async () => {
    const state = await loaded({ snapshot: null });
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
  });

  it('canOpenApprove_WithADirtyOrSavingDraft_IsFalse', async () => {
    const state = await loaded();
    state.editor.setSegmentText('title', 'Changed title');
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
    state.editor.setSegmentText('title', storySnapshot().content.title);
    state.editor.saving.set(true);
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
  });

  it('canOpenApprove_WithAKeywordFlag_IsFalse', async () => {
    const state = await loaded();
    expect(state.editor.setKeywordFlags([{ address: 'p:2', index: 1, rule: 'keyword_name_detected' }], EPISODE_ID)).toBe(true);
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
  });

  it('canOpenApprove_UnderTheRateLimitHold_IsFalseUntilItExpires', async () => {
    vi.useFakeTimers();
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('rate_limited', 429, { retryAfterSec: 30 })));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.canOpenApprove()).toBe(false);
    vi.advanceTimersByTime(30_000);
    expect(state.store.canOpenApprove()).toBe(true);
  });

  it('canOpenApprove_UnderAModerationHold_IsFalse', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('story_moderated', 409)));
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.canOpenApprove()).toBe(false);
    expect(state.store.canApprove()).toBe(false);
  });

  it('reopen_429_HoldsTheButtonForTheServerWaitAndReleasesIt', async () => {
    vi.useFakeTimers();
    const state = await loaded({ status: 'story_approved', actions: ['reopen_story'] });
    expect(state.store.canReopen()).toBe(true);
    state.stories.scriptReopen(() => rejected(appError('rate_limited', 429, { retryAfterSec: 30 })));
    expect(await state.store.reopen(EPISODE_ID)).toBe(false);
    expect(state.store.error()?.code).toBe('rate_limited');
    expect(state.store.retryAfterSec()).toBe(30);
    expect(state.store.canReopen()).toBe(false);
    // The hold is also the re-entry guard: one reopen, never two.
    expect(await state.store.reopen(EPISODE_ID)).toBe(false);
    expect(state.stories.reopenCalls).toHaveLength(1);
    vi.advanceTimersByTime(30_000);
    expect(state.store.retryAfterSec()).toBeNull();
    expect(state.store.canReopen()).toBe(true);
  });

  it('reopen_409_EpisodeInvalidState_RefetchesOnceAndLeavesTheStatusAlone', async () => {
    const state = await loaded({ status: 'story_approved', actions: ['reopen_story'] });
    const fresh = deferred<EpisodeDetail>();
    state.stories.scriptReopen(() => rejected(appError('episode_invalid_state', 409)));
    state.episodes.scriptGet(() => fresh.promise);
    expect(await state.store.reopen(EPISODE_ID)).toBe(false);
    expect(state.store.error()?.code).toBe('episode_invalid_state');
    expect(state.store.blocked()).toBeNull();
    // The other tab moved the episode: this tab reads it again instead of retrying blind, and writes nothing itself.
    expect(state.episodes.getCalls).toHaveLength(2);
    expect(state.store.status()).toBe('story_approved');
    expect(state.store.outcome()).toBeNull();
    fresh.resolve(detailAnswer({ status: 'story_review', nextActions: ['edit_story'] }));
    await settle();
    expect(state.store.status()).toBe('story_review');
  });

  it('approve_409_EpisodeInvalidState_RefetchesOnceAndLeavesTheStatusAlone', async () => {
    const state = await loaded();
    const fresh = deferred<EpisodeDetail>();
    state.stories.scriptApprove(() => rejected(appError('episode_invalid_state', 409)));
    state.episodes.scriptGet(() => fresh.promise);
    expect(await state.store.approve(EPISODE_ID)).toBe(false);
    expect(state.store.blocked()?.code).toBe('episode_invalid_state');
    // The other tab already approved it: this tab re-reads the episode and never flips the status on its own.
    expect(state.episodes.getCalls).toHaveLength(2);
    expect(state.store.status()).toBe('story_review');
    expect(state.store.outcome()).toBeNull();
    fresh.resolve(detailAnswer({ status: 'story_approved', nextActions: ['reopen_story'] }));
    await settle();
    expect(state.store.status()).toBe('story_approved');
  });

  it('reset_ClearsTheTickTheRefusalsAndTheOutcome', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation() }) });
    state.store.setAccepted(true);
    state.stories.scriptApprove(() => rejected(appError('story_moderated', 409)));
    await state.store.approve(EPISODE_ID);
    state.store.clearOutcome();
    expect(state.store.blocked()).not.toBeNull();
    state.store.reset();
    expect(state.store.attestationAccepted()).toBe(false);
    expect(state.store.checkboxInvalid()).toBe(false);
    expect(state.store.blocked()).toBeNull();
    expect(state.store.error()).toBeNull();
    expect(state.store.conflict()).toBe(false);
    expect(state.store.retryAfterSec()).toBeNull();
    expect(state.store.outcome()).toBeNull();
    expect(state.store.approving()).toBe(false);
    expect(state.store.status()).toBe('story_review');
  });

  it('clearBlocked_ReleasesTheRefusalAndTheSpentWait', async () => {
    const state = await loaded();
    state.stories.scriptApprove(() => rejected(appError('story_fact_sheet_outdated', 409)));
    await state.store.approve(EPISODE_ID);
    expect(state.store.blocked()).not.toBeNull();
    state.store.clearBlocked();
    expect(state.store.blocked()).toBeNull();
    expect(state.store.error()).toBeNull();
    expect(state.store.retryAfterSec()).toBeNull();
  });

  it('setAccepted_UntickingRestoresTheRefusal', async () => {
    const state = await loaded({ snapshot: storySnapshot({ attestation: attestation() }) });
    expect(state.store.canApprove()).toBe(false);
    state.store.setAccepted(true);
    expect(state.store.canApprove()).toBe(true);
    state.store.setAccepted(false);
    expect(state.store.canApprove()).toBe(false);
  });

  it('outcome_IsAnnouncedOncePerCommand', async () => {
    const state = await loaded();
    await state.store.approve(EPISODE_ID);
    expect(state.store.outcome()).toBe('approved');
    state.store.clearOutcome();
    expect(state.store.outcome()).toBeNull();
  });
});
