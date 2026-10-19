import { DOCUMENT } from '@angular/common';
import { EnvironmentInjector, createEnvironmentInjector, signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError, AppErrorKind } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { ActivityTracker, SessionStore } from '@core/session';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { EpisodeDetail, FinalVideoAsset } from '../domain/episode.model';
import type { RenderJob } from '../domain/render.model';
import { VideoReviewRepository } from '../domain/video-review.repository';
import { EPISODE_ID, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeVideoReviewRepository } from '../testing/fake-video-review.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { VideoReviewStore } from './video-review.store';

const OTHER_EPISODE_ID = '33333333-3333-4333-8333-333333333333';
const FIRST_URL = 'https://media.example.test/ep-1.mp4?sig=1';
const SWAPPED_URL = 'https://media.example.test/ep-1.mp4?sig=2';
const FAR_FUTURE = '2026-10-20T10:00:00.000+00:00';

const FINAL_VIDEO: FinalVideoAsset = {
  assetId: 'asset-1',
  contentType: 'video/mp4',
  sizeBytes: 12_345_678,
  durationSec: 412,
  retentionUntil: '2026-11-18T10:00:00Z',
  sha256: 'a'.repeat(64),
};

function videoJob(overrides: Partial<RenderJob> = {}): RenderJob {
  return {
    renderJobId: 'job-1',
    status: 'succeeded',
    stage: 'done',
    percent: 100,
    attempt: 1,
    imagesNeeded: 3,
    submittedAt: '2026-10-19T09:00:00Z',
    finishedAt: '2026-10-19T09:20:00Z',
    errorCode: null,
    hints: [],
    images: null,
    ...overrides,
  };
}

/** Stand-in for `EpisodeDetailStore`: one settable episode, a log of the refetches, and an optional held refetch. */
class FakeDetailStore {
  readonly episodeState = signal<EpisodeDetail | null>(null);
  readonly episode = this.episodeState.asReadonly();
  /** The read's own mapped failure, `null` while nothing failed — `refetch()` answers `null` either way (C5). */
  readonly errorState = signal<AppError | null>(null);
  readonly error = this.errorState.asReadonly();
  readonly loadCalls: string[] = [];
  readonly refetchCalls: Array<{ userAction?: boolean } | undefined> = [];
  private readonly pendingRefetch: Array<(value: EpisodeDetail | null) => void> = [];
  private deferred = false;

  load(id: string): Promise<EpisodeDetail | null> {
    this.loadCalls.push(id);
    return Promise.resolve(this.episodeState());
  }

  refetch(options?: { userAction?: boolean }): Promise<EpisodeDetail | null> {
    this.refetchCalls.push(options);
    if (!this.deferred) return Promise.resolve(this.episodeState());
    return new Promise<EpisodeDetail | null>((resolve) => {
      this.pendingRefetch.push(resolve);
    });
  }

  deferRefetch(on = true): void {
    this.deferred = on;
  }

  resolveRefetch(value: EpisodeDetail | null): void {
    this.pendingRefetch.shift()?.(value);
  }
}

/** Stand-in for `ActivityTracker` (`visiblePoll`'s second gate, README D7). */
class FakeActivity {
  active = true;

  isActive(): boolean {
    return this.active;
  }
}

/** A `Document` with only what `visiblePoll` uses, so a test can hide and show the tab deterministically. */
class FakeDocument {
  visibilityState: DocumentVisibilityState = 'visible';
  private listeners: Array<() => void> = [];

  addEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange' && !this.listeners.includes(listener)) this.listeners.push(listener);
  }

  removeEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange') this.listeners = this.listeners.filter((entry) => entry !== listener);
  }

  hide(): void {
    this.setVisibility('hidden');
  }

  show(): void {
    this.setVisibility('visible');
  }

  private setVisibility(state: DocumentVisibilityState): void {
    this.visibilityState = state;
    for (const listener of [...this.listeners]) listener();
  }
}

interface Harness {
  readonly store: VideoReviewStore;
  readonly repo: FakeVideoReviewRepository;
  readonly detail: FakeDetailStore;
  readonly document: FakeDocument;
  readonly activity: FakeActivity;
  readonly generation: WritableSignal<number>;
}

function setup(options: { actions?: readonly string[]; detail?: Partial<EpisodeDetail> } = {}): Harness {
  const repo = new FakeVideoReviewRepository();
  const detail = new FakeDetailStore();
  const document = new FakeDocument();
  const activity = new FakeActivity();
  const generation = signal(0);
  detail.episodeState.set(
    episodeDetail({
      status: 'video_review',
      title: 'Night walk',
      nextActions: options.actions ?? ['approve_video', 'reject_video', 'download'],
      renderJob: videoJob({
        hints: ['audio_clipped'],
        images: { needed: 3, succeeded: 2, skipped: [{ segment: 's-2', reason: 'moderated' }] },
      }),
      finalVideo: FINAL_VIDEO,
      ...options.detail,
    }),
  );
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      VideoReviewStore,
      { provide: VideoReviewRepository, useValue: repo },
      { provide: EpisodeDetailStore, useValue: detail },
      { provide: DOCUMENT, useValue: document },
      { provide: ActivityTracker, useValue: activity },
      { provide: SessionStore, useValue: { generation } },
      { provide: APP_ERROR_DEV_MODE, useValue: false },
    ],
  });
  return { store: TestBed.inject(VideoReviewStore), repo, detail, document, activity, generation };
}

function appError(code: string, status: number, kind: AppErrorKind, extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

/** A URL whose due instant (`expiresAt − 60 s`) is `leadMs` after the next 60 s poll tick. */
function dueAtTheNextTick(leadMs: number): string {
  return new Date(Date.now() + 60_000 + leadMs).toISOString();
}

beforeEach(() => {
  // Only the clock, the poll interval and the 429 wait: the tick itself stays the store's (nothing else is faked).
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-19T10:00:00.000Z'));
});

afterEach(() => {
  vi.useRealTimers();
});

describe('VideoReviewStore - the signed URL', () => {
  it('open_IssuesExactlyOneReadAndKeepsTheTwoContractMembers', async () => {
    const state = setup();

    await state.store.loadUrl();

    expect(state.repo.urlCalls).toHaveLength(1);
    expect(state.repo.urlCalls[0]).toEqual({ method: 'getVideoUrl', episodeId: EPISODE_ID, userAction: false });
    expect(state.store.url()).toEqual({ url: FIRST_URL, expiresAt: '2026-10-19T10:15:00.000+00:00' });
    expect(state.store.loadingUrl()).toBe(false);
    expect(state.store.urlError()).toBeNull();
    expect(state.store.urlErrorKey()).toBeNull();
    expect(state.store.urlFailed()).toBe(false);

    await state.store.loadUrl();
    expect(state.repo.urlCalls).toHaveLength(1);
  });

  it('open_WithoutAnEpisodeIssuesNoRead', async () => {
    const state = setup();
    state.detail.episodeState.set(null);

    await state.store.loadUrl();
    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(state.repo.urlCalls).toHaveLength(0);
  });

  it('open_SetsTheLoadingFlagOnlyUntilTheFirstAnswerLands', async () => {
    const state = setup();
    state.repo.deferUrl();

    const opening = state.store.loadUrl();
    await settle();
    expect(state.store.loadingUrl()).toBe(true);
    expect(state.store.url()).toBeNull();

    state.repo.resolveUrl({ url: FIRST_URL, expiresAt: FAR_FUTURE });
    await opening;
    expect(state.store.loadingUrl()).toBe(false);
    expect(state.store.url()?.url).toBe(FIRST_URL);
  });

  it('open_FailureIsTheFailedStateWithARetryThatCarriesUserAction', async () => {
    const state = setup();
    state.repo.willFailUrl(appError('not_found', 404, 'not_found'));

    await state.store.loadUrl();
    expect(state.store.url()).toBeNull();
    expect(state.store.urlFailed()).toBe(true);
    expect(state.store.urlErrorKey()).toBe('errors.not_found');

    state.repo.willAnswerUrl({ url: FIRST_URL, expiresAt: FAR_FUTURE });
    await state.store.retryUrl();
    expect(state.repo.urlCalls[1]?.userAction).toBe(true);
    expect(state.store.url()?.url).toBe(FIRST_URL);
    expect(state.store.urlFailed()).toBe(false);
    expect(state.store.urlError()).toBeNull();
  });

  it('dueCheck_BeforeTheLeadIssuesNoRefresh_AndOneOnceItPasses', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(61_000);
    await state.store.loadUrl();
    state.repo.willAnswerUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE });

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);
    // 61 s before the expiry: still inside the 60 s safety margin, so no request at all.
    expect(state.repo.urlCalls).toHaveLength(1);
    expect(state.store.refreshCount()).toBe(0);

    await vi.advanceTimersByTimeAsync(60_000);
    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.refreshCount()).toBe(1);
  });

  it('dueCheck_AtTheLeadSwapsUrlAndExpiresAtTogetherAndCountsTheHook', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    const refreshed = { url: SWAPPED_URL, expiresAt: FAR_FUTURE };
    state.repo.willAnswerUrl(refreshed);

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.repo.urlCalls[1]?.userAction).toBe(false);
    expect(state.store.url()).toEqual(refreshed);
    expect(state.store.refreshDueAt()).toBe(Date.parse(FAR_FUTURE) - 60_000);
    expect(state.store.refreshCount()).toBe(1);

    await vi.advanceTimersByTimeAsync(60_000);
    // The new link is a day out: the next due check makes no request.
    expect(state.repo.urlCalls).toHaveLength(2);
  });

  it('dueCheck_HiddenTabRunsNothingAndTheFirstVisibleTickCatchesUp', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    state.store.startUrlRefresh();

    state.document.hide();
    await vi.advanceTimersByTimeAsync(120_000);
    expect(state.repo.urlCalls).toHaveLength(1);

    state.document.show();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(2);
  });

  it('dueCheck_IdleUserRunsNothing', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    state.activity.active = false;

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(120_000);
    expect(state.repo.urlCalls).toHaveLength(1);

    state.activity.active = true;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(state.repo.urlCalls).toHaveLength(2);
  });

  it('dueCheck_WhileAReadIsInFlightIssuesNoSecondRequest', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    state.repo.deferUrl();

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(state.repo.urlCalls).toHaveLength(2);

    await vi.advanceTimersByTimeAsync(60_000);
    await state.store.refreshUrl();
    expect(state.repo.urlCalls).toHaveLength(2);

    state.repo.resolveUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE });
    await settle();
    expect(state.store.url()?.url).toBe(SWAPPED_URL);
    expect(state.store.refreshCount()).toBe(1);
  });

  it('refreshFailure_KeepsThePreviousUrlAndReportsTheMappedError', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    const previous = state.store.url();
    state.repo.willFailUrl(appError('network_error', 0, 'network'));

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(state.store.url()).toEqual(previous);
    expect(state.store.urlErrorKey()).toBe('errors.network_error');
    expect(state.store.urlFailed()).toBe(false);
    expect(state.store.playerError()).toBe(false);
    expect(state.store.refreshCount()).toBe(0);
  });

  it('refreshFailure_WithRetryAfterIsHandedToTheCountdown', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    state.repo.willFailUrl(appError('service_unavailable', 503, 'server', { retryAfterSec: 20 }));

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(state.store.urlRetryAfterSec()).toBe(20);
    expect(state.store.urlErrorKey()).toBe('errors.service_unavailable');
  });

  it('playerError_ForcesExactlyOneRefreshAndRestoresThePlayer', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.repo.willAnswerUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE });

    state.store.onPlayerError();
    expect(state.store.playerError()).toBe(true);
    await settle();

    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.repo.urlCalls[1]?.userAction).toBe(false);
    expect(state.store.url()?.url).toBe(SWAPPED_URL);
    expect(state.store.playerError()).toBe(false);
    expect(state.store.urlFailed()).toBe(false);
    expect(state.store.refreshCount()).toBe(1);

    // The player's own refresh does not re-arm the one it just spent (SP-1/C1): a link that still does not play has to
    // reach the retry state, never another GET.
    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.urlFailed()).toBe(true);
  });

  it('playerError_OnAPersistentlyUnplayableLinkBuysOneExtraReadAndThenGivesUp', async () => {
    const state = setup();
    await state.store.loadUrl();
    // The link is signed correctly but cannot be played (a CSP `media-src` miss, a corrupt asset): the element fires
    // `error` again for every new `src` the swap hands it, and the endpoint must not be read for each one.
    state.repo.willAnswerUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE });

    state.store.onPlayerError();
    await settle();
    state.store.onPlayerError();
    await settle();
    state.store.onPlayerError();
    await settle();

    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.url()?.url).toBe(SWAPPED_URL);
    expect(state.store.urlFailed()).toBe(true);
  });

  it('playerError_WhileAReadIsAlreadyInFlightSpendsNothingAndFailsOverWithIt', async () => {
    const state = setup();
    state.repo.expiresAt = dueAtTheNextTick(59_000);
    await state.store.loadUrl();
    state.repo.deferUrl();

    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(state.repo.urlCalls).toHaveLength(2);

    // The error arrives while the due check is on its way: that read is not the player's refresh, so nothing is spent.
    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.playerError()).toBe(true);
    expect(state.store.urlFailed()).toBe(false);

    // That read fails: the player must not be left holding a URL it cannot play (C6).
    state.repo.rejectUrl(appError('service_unavailable', 503, 'server'));
    await settle();
    expect(state.store.urlFailed()).toBe(true);
    expect(state.store.urlErrorKey()).toBe('errors.service_unavailable');

    // The one refresh is still owed: the next error buys exactly one read and clears the failed state with it.
    state.repo.willAnswerUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE });
    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(3);
    expect(state.repo.urlCalls[2]?.userAction).toBe(false);
    expect(state.store.url()?.url).toBe(SWAPPED_URL);
    expect(state.store.urlFailed()).toBe(false);
  });

  it('retryUrl_ReArmsTheOneRefreshAPlayerErrorIsAllowed', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.repo.willAnswerUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE });

    // The player's own refresh is spent, and a link that still does not play reaches the retry state.
    state.store.onPlayerError();
    await settle();
    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.urlFailed()).toBe(true);

    // The owner's own retry is a read the player did not ask for: it re-arms the one refresh.
    await state.store.retryUrl();
    expect(state.repo.urlCalls[2]?.userAction).toBe(true);
    expect(state.store.urlFailed()).toBe(false);

    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(4);
    expect(state.repo.urlCalls[3]?.userAction).toBe(false);
    expect(state.store.urlFailed()).toBe(false);

    // …and that one is spent in its turn: a broken asset never buys more than one read per non-player success.
    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(4);
    expect(state.store.urlFailed()).toBe(true);
  });

  it('playerError_WhenTheRefreshFailsReplacesThePlayerAndNeverHammersTheEndpoint', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.repo.willFailUrl(appError('service_unavailable', 503, 'server'));

    state.store.onPlayerError();
    await settle();

    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.url()?.url).toBe(FIRST_URL);
    expect(state.store.playerError()).toBe(true);
    expect(state.store.urlFailed()).toBe(true);
    expect(state.store.urlErrorKey()).toBe('errors.service_unavailable');

    state.store.onPlayerError();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.urlFailed()).toBe(true);
  });

  it('lateAnswerAfterTheEpisodeChanged_IsDiscardedAndTheNewReadStillRuns', async () => {
    const state = setup();
    state.repo.deferUrl();

    const stale = state.store.loadUrl();
    state.detail.episodeState.set(episodeDetail({ id: OTHER_EPISODE_ID, status: 'video_review', nextActions: [] }));
    await settle();
    const fresh = state.store.loadUrl();
    await settle();
    expect(state.repo.urlCalls).toHaveLength(2);

    // The first episode's answer lands late: nothing of it is stored, and its release must not free the newer read.
    state.repo.resolveUrl({ url: FIRST_URL, expiresAt: FAR_FUTURE }, 0);
    await stale;
    expect(state.store.url()).toBeNull();

    await state.store.loadUrl();
    expect(state.repo.urlCalls).toHaveLength(2);

    state.repo.resolveUrl({ url: SWAPPED_URL, expiresAt: FAR_FUTURE }, 0);
    await fresh;
    expect(state.store.url()?.url).toBe(SWAPPED_URL);
    expect(state.repo.urlCalls[1]?.episodeId).toBe(OTHER_EPISODE_ID);
  });

  it('sessionEnd_DropsTheUrlWithNoRequest', async () => {
    const state = setup();
    await state.store.loadUrl();
    expect(state.store.url()).not.toBeNull();

    state.generation.set(1);
    TestBed.tick();

    expect(state.store.url()).toBeNull();
    expect(state.repo.urlCalls).toHaveLength(1);
  });

  it('release_ForgetsTheUrlStopsThePollAndTheNextOpenReadsAFreshLink', async () => {
    const state = setup();
    await state.store.loadUrl();
    expect(state.store.url()?.url).toBe(FIRST_URL);
    state.store.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(vi.getTimerCount()).toBeGreaterThan(0);

    state.store.release();

    expect(state.store.url()).toBeNull();
    expect(state.store.loadingUrl()).toBe(false);
    expect(state.store.urlError()).toBeNull();
    expect(state.store.urlFailed()).toBe(false);
    expect(state.store.playerError()).toBe(false);
    expect(state.store.refreshCount()).toBe(0);
    expect(state.store.dialog()).toBeNull();
    expect(vi.getTimerCount()).toBe(0);

    // The `re_render` cycle: the same page instance returns to `video_review` for the same episode and reads a NEW
    // link (the previous render's signed URL is gone) with the poll armed again.
    state.repo.url = SWAPPED_URL;
    state.store.startUrlRefresh();
    await state.store.loadUrl();
    expect(state.repo.urlCalls).toHaveLength(2);
    expect(state.store.url()?.url).toBe(SWAPPED_URL);
    expect(vi.getTimerCount()).toBeGreaterThan(0);
    state.store.release();
  });

  it('teardown_StopsThePollAndForgetsTheUrl', async () => {
    const state = setup();
    await state.store.loadUrl();
    const owner = createEnvironmentInjector([VideoReviewStore], TestBed.inject(EnvironmentInjector));
    const scoped = owner.get(VideoReviewStore);

    await scoped.loadUrl();
    scoped.startUrlRefresh();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(vi.getTimerCount()).toBeGreaterThan(0);

    owner.destroy();
    expect(vi.getTimerCount()).toBe(0);
    expect(scoped.url()).toBeNull();
    expect(scoped.dialog()).toBeNull();
  });
});

describe('VideoReviewStore - the episode state it reads', () => {
  it('readsTheHintCodesTheSkippedScenesAndTheFinalAssetFromTheDetail', () => {
    const state = setup();

    expect(state.store.notes()).toEqual(['audio_clipped']);
    expect(state.store.skipped()).toEqual([{ segment: 's-2', reason: 'moderated' }]);
    expect(state.store.finalVideo()?.assetId).toBe('asset-1');
    expect(state.store.finalVideo()?.durationSec).toBe(412);
  });

  it('withoutARenderJobOrAFinalAssetReadsAsEmpty', () => {
    const state = setup({ detail: { renderJob: null, finalVideo: null } });

    expect(state.store.notes()).toEqual([]);
    expect(state.store.skipped()).toEqual([]);
    expect(state.store.finalVideo()).toBeNull();
  });

  it.each([
    [['approve_video', 'reject_video', 'download'], true, true, true],
    [['reject_video', 'download'], false, true, true],
    [['approve_video', 'download'], true, false, true],
    [['approve_video', 'reject_video'], true, true, false],
    [[], false, false, false],
  ])('nextActions %s gate the three controls', (actions, approve, reject, download) => {
    const state = setup({ actions });

    expect(state.store.canApprove()).toBe(approve);
    expect(state.store.canReject()).toBe(reject);
    expect(state.store.canDownload()).toBe(download);
  });

  it('downloadNameIsTheSanitisedTitleWithTheMp4Extension', () => {
    const state = setup({ detail: { title: 'Night walk' } });

    expect(state.store.downloadName()).toBe('Night walk.mp4');
  });

  it('downloadNameOnAnEpisodeWithoutATitleFallsBackToTheProductName', () => {
    const state = setup({ detail: { title: null } });

    expect(state.store.downloadName()).toBe('nighttales.mp4');
  });

  it.each(['a"b/c\\d', 'line\u0007break', '   ', '../../etc/passwd', 'x'.repeat(200)])(
    'downloadName sanitises the hostile title %j',
    (title) => {
      const state = setup({ detail: { title } });

      const name = state.store.downloadName();
      expect(name.endsWith('.mp4')).toBe(true);
      expect(name).not.toMatch(/[\u0000-\u001f\u007f"'`/\\:<>|*?]/);
      expect(name.length).toBeLessThanOrEqual(104);
      expect(name.replace(/\.mp4$/, '').trim().length).toBeGreaterThan(0);
    },
  );
});

describe('VideoReviewStore - approve', () => {
  it('trimsTheNoteAndSendsItWithTheLiveEtagAndNoKey', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    expect(state.store.dialog()).toBe('approve');
    state.store.note.setValue('  Look at the last scene  ');

    await state.store.approve();

    expect(state.repo.approveCalls).toHaveLength(1);
    expect(state.repo.approveCalls[0]).toEqual({
      method: 'approveVideo',
      episodeId: EPISODE_ID,
      etag: '"ep-1"',
      note: 'Look at the last scene',
    });
    expect(state.store.dialog()).toBeNull();
    expect(state.store.deciding()).toBe(false);
    expect(state.store.error()).toBeNull();
    expect(state.detail.refetchCalls).toHaveLength(1);
  });

  it('omitsABlankNoteEntirely', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.store.note.setValue('   ');

    await state.store.approve();

    expect(state.repo.approveCalls[0]?.note).toBeUndefined();
  });

  it('closesTheDialogOnTheSuccessButReleasesTheHoldOnlyAfterTheRefetch', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.detail.deferRefetch();

    const sending = state.store.approve();
    await settle();

    expect(state.repo.approveCalls).toHaveLength(1);
    expect(state.store.dialog()).toBeNull();
    expect(state.store.deciding()).toBe(true);
    expect(state.store.canApprove()).toBe(false);
    expect(state.detail.refetchCalls).toHaveLength(1);

    state.detail.resolveRefetch(state.detail.episodeState());
    await sending;
    expect(state.store.deciding()).toBe(false);
  });

  it('blocksANoteLongerThanTheWireLimitBeforeSending', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.store.note.setValue('x'.repeat(501));

    await state.store.approve();

    expect(state.repo.approveCalls).toHaveLength(0);
    expect(state.store.note.touched).toBe(true);
    expect(state.store.dialog()).toBe('approve');
  });

  it('aRefusalKeepsTheDialogOpenAndSendsNothingLocally', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('episode_conflict', 412, 'precondition'));

    await state.store.approve();

    expect(state.store.dialog()).toBe('approve');
    // The fresh read has landed by the time the click resolves, so the confirm is offered again on the new ETag.
    expect(state.store.needsRefresh()).toBe(false);
    expect(state.store.errorKey()).toBe('errors.episode_conflict');
    expect(state.store.deciding()).toBe(false);
    expect(state.detail.refetchCalls).toHaveLength(1);
  });

  it('cannotOpenWithoutTheServersAction', () => {
    const state = setup({ actions: ['reject_video'] });

    state.store.openApprove();

    expect(state.store.dialog()).toBeNull();
  });

  it('a412WhoseRefetchFailedKeepsTheConfirmHeld', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('episode_conflict', 412, 'precondition'));
    state.detail.deferRefetch();

    const sending = state.store.approve();
    await settle();
    expect(state.store.needsRefresh()).toBe(true);

    // The read itself failed: `refetch()` answers `null` and the hold stays, so the retry cannot go out on the same
    // stale ETag (C5).
    state.detail.errorState.set(appError('service_unavailable', 503, 'server'));
    state.detail.resolveRefetch(null);
    await sending;

    expect(state.store.needsRefresh()).toBe(true);
    expect(state.store.errorKey()).toBe('errors.episode_conflict');
    expect(state.store.deciding()).toBe(false);
  });

  it('aFailedRefetchAfterAnAppliedApproveKeepsTheDialogOnTheReadsOwnError', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.detail.deferRefetch();
    state.detail.errorState.set(appError('network_error', 0, 'network'));

    const sending = state.store.approve();
    await settle();
    expect(state.repo.approveCalls).toHaveLength(1);
    // The dialog is closed on the answer, before the read lands — the page pins that.
    expect(state.store.dialog()).toBeNull();

    state.detail.resolveRefetch(null);
    await sending;

    // The decision was applied but the fresh state never landed: the read's own mapped error is shown in the dialog
    // again, so the owner is not left with a stale snapshot, a stale ETag and live buttons and no message (C5).
    expect(state.store.dialog()).toBe('approve');
    expect(state.store.errorKey()).toBe('errors.network_error');
    expect(state.store.deciding()).toBe(false);
    expect(state.store.error()).toEqual(appError('network_error', 0, 'network'));

    // The confirm of an applied decision repeats the read, never the command: the server already approved it.
    state.repo.willAcceptApprove();
    state.detail.errorState.set(null);
    const repeating = state.store.approve();
    await settle();
    expect(state.store.deciding()).toBe(true);
    state.detail.resolveRefetch(state.detail.episodeState());
    await repeating;

    expect(state.repo.approveCalls).toHaveLength(1);
    expect(state.detail.refetchCalls).toHaveLength(2);
    expect(state.store.dialog()).toBeNull();
    expect(state.store.errorKey()).toBeNull();
    expect(state.store.deciding()).toBe(false);
  });

  it('aRepeatReadThatFailsAgainKeepsTheDialogAndSendsNoSecondApprove', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.detail.deferRefetch();
    state.detail.errorState.set(appError('network_error', 0, 'network'));

    const sending = state.store.approve();
    await settle();
    state.detail.resolveRefetch(null);
    await sending;
    expect(state.store.dialog()).toBe('approve');

    // The repeated read fails too: the dialog and its error stay, and the command is still never re-sent.
    const repeating = state.store.approve();
    await settle();
    expect(state.store.deciding()).toBe(true);
    state.detail.resolveRefetch(null);
    await repeating;

    expect(state.store.dialog()).toBe('approve');
    expect(state.store.errorKey()).toBe('errors.network_error');
    expect(state.repo.approveCalls).toHaveLength(1);
    expect(state.detail.refetchCalls).toHaveLength(2);
    expect(state.store.deciding()).toBe(false);
  });

  it('aRefetchSupersededByANewerReadReopensNothing', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.detail.deferRefetch();

    const sending = state.store.approve();
    await settle();
    // A `null` with no error on the detail store is a read a newer one superseded, not a failure: that read owns the
    // state, so no dialog comes back on an error nobody recorded.
    state.detail.resolveRefetch(null);
    await sending;

    expect(state.store.dialog()).toBeNull();
    expect(state.store.errorKey()).toBeNull();
  });
});

describe('VideoReviewStore - reject', () => {
  it('blocksAnEmptyOrWhitespaceOrOverlongReasonBeforeSending', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    expect(state.store.dialog()).toBe('reject');
    expect(state.store.target()).toBe('story');

    await state.store.reject();
    expect(state.repo.rejectCalls).toHaveLength(0);
    expect(state.store.reason.touched).toBe(true);

    state.store.reason.setValue('   ');
    await state.store.reject();
    expect(state.repo.rejectCalls).toHaveLength(0);

    state.store.reason.setValue('x'.repeat(501));
    await state.store.reject();
    expect(state.repo.rejectCalls).toHaveLength(0);
    expect(state.store.deciding()).toBe(false);

    state.store.reason.setValue('x'.repeat(500));
    await state.store.reject();
    expect(state.repo.rejectCalls).toHaveLength(1);
  });

  it('mintsOneIntentPerClickReplaysItWhileTheOutcomeIsUnknownAndSettlesItOnSuccess', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('The last line is wrong');
    state.repo.willFailReject(appError('network_error', 0, 'network'));
    state.repo.willFailReject(appError('network_error', 0, 'network'));

    await state.store.reject();
    const first = state.repo.rejectCalls[0]?.intent;
    expect(first).toBeDefined();
    expect(first?.isSettled).toBe(false);
    expect(state.store.errorKey()).toBe('errors.network_error');

    await state.store.reject();
    await state.store.reject();
    expect(state.repo.rejectCalls[1]?.intent).toBe(first);
    expect(state.repo.rejectCalls[2]?.intent).toBe(first);

    state.store.openReject('story');
    state.store.reason.setValue('A different reason');
    await state.store.reject();

    expect(first?.isSettled).toBe(true);
    expect(state.repo.rejectCalls[3]?.intent).not.toBe(first);
    expect(state.repo.rejectCalls[3]?.intent.key).not.toBe(first?.key);
  });

  it('mintsAFreshKeyWhenTheReasonChangesAfterAnUnknownOutcome', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('The last line is wrong');
    state.repo.willFailReject(appError('network_error', 0, 'network'));

    await state.store.reject();
    const first = state.repo.rejectCalls[0]?.intent;
    expect(first?.isSettled).toBe(false);
    expect(state.store.dialog()).toBe('reject');

    // C2/SP-5: a changed reason is a new decision — replaying the held key with a different body is what the server
    // refuses as 422 `idempotency_key_reused`, so the old intent is settled and a fresh key minted.
    state.store.reason.setValue('The middle line is wrong');
    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(2);
    expect(state.repo.rejectCalls[1]?.intent).not.toBe(first);
    expect(state.repo.rejectCalls[1]?.intent.key).not.toBe(first?.key);
    expect(first?.isSettled).toBe(true);
    expect(state.repo.rejectCalls[1]?.reason).toBe('The middle line is wrong');
  });

  it('mintsAFreshKeyWhenTheTargetChangesAfterAnUnknownOutcome', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('network_error', 0, 'network'));

    await state.store.reject();
    const first = state.repo.rejectCalls[0]?.intent;
    expect(first?.isSettled).toBe(false);

    // The other half of the payload binding: flipping story ↔ re_render is a different command, so it gets its own key.
    state.store.setTarget('re_render');
    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(2);
    expect(state.repo.rejectCalls[1]?.intent.key).not.toBe(first?.key);
    expect(state.repo.rejectCalls[1]?.target).toBe('re_render');
    expect(first?.isSettled).toBe(true);
  });

  it('reopensTheDialogWithAFreshFieldSoThePreviousAttemptLeavesNoMark', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('   ');
    state.store.reason.markAsDirty();

    await state.store.reject();
    expect(state.store.reason.touched).toBe(true);
    expect(state.store.reason.dirty).toBe(true);

    // C7: a reopened dialog is a fresh attempt — the field starts empty, untouched and pristine again.
    state.store.closeDialog();
    state.store.openReject('story');

    expect(state.store.dialog()).toBe('reject');
    expect(state.store.reason.value).toBe('');
    expect(state.store.reason.touched).toBe(false);
    expect(state.store.reason.dirty).toBe(false);
  });

  it('aRefusalSettlesTheIntentSoTheNextClickUsesAFreshKey', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('episode_invalid_state', 409, 'precondition'));

    await state.store.reject();
    const refused = state.repo.rejectCalls[0]?.intent;
    expect(refused?.isSettled).toBe(true);
    expect(state.store.errorKey()).toBe('errors.episode_invalid_state');
    expect(state.detail.refetchCalls).toHaveLength(1);

    await state.store.reject();
    expect(state.repo.rejectCalls[1]?.intent).not.toBe(refused);
  });

  it('theStory200ClosesTheDialogAndAwaitsTheRefetch', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willAcceptReject({ status: 'story_review', etag: '"ep-2"', accepted: false });

    await state.store.reject();

    expect(state.repo.rejectCalls[0]).toMatchObject({ target: 'story', reason: 'Wrong tone', etag: '"ep-1"' });
    expect(state.store.accepted()).toBe(false);
    expect(state.store.dialog()).toBeNull();
    expect(state.store.deciding()).toBe(false);
    expect(state.detail.refetchCalls).toHaveLength(1);
  });

  it('theReRender202IsAcceptedWithNoJobIdAndNoOptimisticStatus', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('re_render');
    state.store.reason.setValue('The cut is unusable');
    state.repo.willAcceptReject({ status: 'rendering', etag: '"ep-2"', accepted: true });
    state.detail.deferRefetch();

    const sending = state.store.reject();
    await settle();

    expect(state.repo.rejectCalls[0]).toMatchObject({ target: 're_render', reason: 'The cut is unusable' });
    expect(state.store.accepted()).toBe(true);
    expect(state.store.dialog()).toBeNull();
    expect(state.store.deciding()).toBe(true);
    expect(state.detail.episode()?.status).toBe('video_review');

    state.detail.resolveRefetch(state.detail.episodeState());
    await sending;
    expect(state.store.deciding()).toBe(false);
  });

  it('aFailedRefetchAfterAnAppliedRejectKeepsTheDialogOnTheReadsOwnError', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willAcceptReject({ status: 'story_review', etag: '"ep-2"', accepted: false });
    state.detail.deferRefetch();
    state.detail.errorState.set(appError('service_unavailable', 503, 'server'));

    const sending = state.store.reject();
    await settle();
    expect(state.repo.rejectCalls).toHaveLength(1);

    state.detail.resolveRefetch(null);
    await sending;

    // The reject was applied and the read that would have moved the page failed: the dialog returns on the read's own
    // error instead of leaving the stale `video_review` snapshot on screen with nothing said (C5).
    expect(state.store.dialog()).toBe('reject');
    expect(state.store.errorKey()).toBe('errors.service_unavailable');
    expect(state.store.deciding()).toBe(false);

    // The confirm repeats the read, never the reject: the key was settled with the applied command, so a second POST
    // would mint a fresh key for a decision the server already took.
    state.repo.willAcceptReject({ status: 'story_review', etag: '"ep-2"', accepted: false });
    state.detail.errorState.set(null);
    const repeating = state.store.reject();
    await settle();
    state.detail.resolveRefetch(state.detail.episodeState());
    await repeating;

    expect(state.repo.rejectCalls).toHaveLength(1);
    expect(state.detail.refetchCalls).toHaveLength(2);
    expect(state.store.dialog()).toBeNull();
  });

  it('a400BindsTheReasonFieldAndKeepsTheDialog', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(
      appError('validation_failed', 400, 'validation', { fieldErrors: { reason: ['too_long'] } }),
    );

    await state.store.reject();

    expect(state.store.errorKey()).toBe('errors.validation_failed');
    expect(state.store.reason.errors?.['server']).toMatchObject({ messageKeys: ['errors.validation_failed'] });
    expect(state.store.reason.touched).toBe(true);
    expect(state.store.dialog()).toBe('reject');
    expect(state.detail.refetchCalls).toHaveLength(0);
  });

  it('a429HoldsTheDecisionsForTheCountdown', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }));

    await state.store.reject();
    expect(state.store.retryAfterSec()).toBe(30);

    await state.store.reject();
    expect(state.repo.rejectCalls).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(30_000);
    expect(state.store.retryAfterSec()).toBeNull();
  });

  it('a428ReportsTheGenericCopyBecauseItIsOurOwnBug', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('if_match_required', 428, 'precondition', { traceId: 'trace-1' }));

    await state.store.reject();

    expect(state.store.errorKey()).toBe('errors.unknown_error');
    expect(state.store.error()?.traceId).toBe('trace-1');
  });

  it('aDismissalDropsTheHeldKeyAndFencesTheAnswerStillOnItsWay', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.deferReject();

    const sending = state.store.reject();
    await settle();
    expect(state.store.deciding()).toBe(true);
    const held = state.repo.rejectCalls[0]?.intent;

    state.store.closeDialog();
    expect(state.store.dialog()).toBeNull();
    expect(state.store.deciding()).toBe(false);
    expect(held?.isSettled).toBe(true);

    state.repo.resolveReject({ status: 'story_review', etag: '"ep-2"', accepted: false });
    await sending;
    expect(state.detail.refetchCalls).toHaveLength(0);
    expect(state.store.errorKey()).toBeNull();
    expect(state.store.accepted()).toBe(false);
  });
});
