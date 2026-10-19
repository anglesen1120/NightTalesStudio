import { DOCUMENT } from '@angular/common';
import { signal } from '@angular/core';
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
import { episodeDetail } from '../testing/fake-episodes.repository';
import { FakeVideoReviewRepository } from '../testing/fake-video-review.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { VideoReviewStore } from './video-review.store';

const FINAL_VIDEO: FinalVideoAsset = {
  assetId: 'asset-1',
  contentType: 'video/mp4',
  sizeBytes: 12_345_678,
  durationSec: 412,
  retentionUntil: '2026-11-18T10:00:00Z',
  sha256: 'a'.repeat(64),
};

function videoJob(): RenderJob {
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
  };
}

/** Same contract as the store spec's stand-in: a settable episode and an optional held refetch. */
class FakeDetailStore {
  readonly episodeState = signal<EpisodeDetail | null>(null);
  readonly episode = this.episodeState.asReadonly();
  readonly refetchCalls: Array<{ userAction?: boolean } | undefined> = [];
  private readonly pendingRefetch: Array<(value: EpisodeDetail | null) => void> = [];
  private deferred = false;

  load(): Promise<EpisodeDetail | null> {
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

class FakeDocument {
  visibilityState: DocumentVisibilityState = 'visible';
  addEventListener(): void {}
  removeEventListener(): void {}
}

class FakeActivity {
  isActive(): boolean {
    return true;
  }
}

interface Harness {
  readonly store: VideoReviewStore;
  readonly repo: FakeVideoReviewRepository;
  readonly detail: FakeDetailStore;
  readonly generation: WritableSignal<number>;
}

function setup(): Harness {
  const repo = new FakeVideoReviewRepository();
  const detail = new FakeDetailStore();
  const generation = signal(0);
  detail.episodeState.set(
    episodeDetail({
      status: 'video_review',
      title: 'Night walk',
      nextActions: ['approve_video', 'reject_video', 'download'],
      renderJob: videoJob(),
      finalVideo: FINAL_VIDEO,
    }),
  );
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      VideoReviewStore,
      { provide: VideoReviewRepository, useValue: repo },
      { provide: EpisodeDetailStore, useValue: detail },
      { provide: DOCUMENT, useValue: new FakeDocument() },
      { provide: ActivityTracker, useValue: new FakeActivity() },
      { provide: SessionStore, useValue: { generation } },
      { provide: APP_ERROR_DEV_MODE, useValue: false },
    ],
  });
  return { store: TestBed.inject(VideoReviewStore), repo, detail, generation };
}

function appError(code: string, status: number, kind: AppErrorKind, extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-19T10:00:00.000Z'));
});

afterEach(() => {
  vi.useRealTimers();
});

describe('QA probe - approve 409 (another tab decided first)', () => {
  // D1: the 409 branch awaits its refetch, so `deciding` is still held while the fresh detail is on its way and the
  // stale `nextActions` cannot offer the action again.
  it('keepsTheStaleActionsOutOfReachUntilTheRefetchLands', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('episode_invalid_state', 409, 'precondition'));
    state.detail.deferRefetch();

    const approving = state.store.approve();
    await settle();

    // Task Failure table: 409 → refetch, re-render from the fresh nextActions. The refetch is still in flight here,
    // so the stale approve action must not be offered again — and the click must not have been released either.
    expect(state.store.deciding()).toBe(true);
    expect(state.store.canApprove()).toBe(false);

    state.detail.resolveRefetch(state.detail.episodeState());
    await approving;

    expect(state.store.deciding()).toBe(false);
    expect(state.store.canApprove()).toBe(true);
  });

  // D1: a plain `it` keeps the gate honest — a second click while the refetch after a 409 is pending is absorbed.
  it('sendsNoSecondApproveWhileTheRefetchAfter409IsPending', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('episode_invalid_state', 409, 'precondition'));
    state.detail.deferRefetch();

    const approving = state.store.approve();
    await settle();
    await state.store.approve();

    // Task Failure table: "409 from a second tab → refetch … never a second decision request".
    expect(state.repo.approveCalls).toHaveLength(1);

    state.detail.resolveRefetch(state.detail.episodeState());
    await approving;

    expect(state.repo.approveCalls).toHaveLength(1);
    expect(state.store.deciding()).toBe(false);
  });

  it('refetchesOnceAndClearsTheHoldWhenTheRefetchIsImmediate', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('episode_invalid_state', 409, 'precondition'));

    await state.store.approve();

    expect(state.detail.refetchCalls).toHaveLength(1);
    expect(state.store.deciding()).toBe(false);
  });
});

describe('QA probe - approve 429 (Retry-After holds the approve action)', () => {
  it('holdsTheApproveActionForTheRetryAfterWindow', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }));

    await state.store.approve();
    expect(state.store.retryAfterSec()).toBe(30);

    // The dialog confirm is held by the countdown; the store must not send a second approve either.
    await state.store.approve();
    expect(state.repo.approveCalls).toHaveLength(1);

    vi.advanceTimersByTime(30_000);
    expect(state.store.retryAfterSec()).toBeNull();
    expect(state.store.canApprove()).toBe(true);
  });
});

describe('QA probe - reject 412 (stale ETag)', () => {
  it('sendsNoSecondRejectWhileTheRefreshAfter412IsPending', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('episode_conflict', 412, 'precondition'));
    state.detail.deferRefetch();

    const first = state.store.reject();
    await settle();
    expect(state.store.needsRefresh()).toBe(true);

    // A second programmatic reject while the fresh read is pending must not reuse the stale ETag.
    await state.store.reject();
    expect(state.repo.rejectCalls).toHaveLength(1);

    state.detail.resolveRefetch(state.detail.episodeState());
    await first;
  });
});

describe('QA probe - approve 412 retry', () => {
  it('retriesWithTheFreshEtagAfterTheRefetchLands', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('episode_conflict', 412, 'precondition'));

    await state.store.approve();
    expect(state.repo.approveCalls[0]?.etag).toBe('"ep-1"');

    state.detail.episodeState.set(episodeDetail({
      status: 'video_review',
      etag: '"ep-9"',
      nextActions: ['approve_video', 'reject_video', 'download'],
      renderJob: videoJob(),
      finalVideo: FINAL_VIDEO,
    }));
    state.store.openApprove();
    await state.store.approve();

    expect(state.repo.approveCalls[1]?.etag).toBe('"ep-9"');
  });
});

describe('QA probe - dismissal while an approve is in flight', () => {
  it('fencesTheLateAnswerAndDoesNotRefetchOrReopenTheDialog', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.deferApprove();

    const sending = state.store.approve();
    await settle();
    expect(state.store.deciding()).toBe(true);

    state.store.closeDialog();
    expect(state.store.deciding()).toBe(false);

    state.repo.resolveApprove({ status: 'ready', etag: '"ep-2"' });
    await sending;

    expect(state.store.dialog()).toBeNull();
    expect(state.detail.refetchCalls).toHaveLength(0);
    expect(state.store.deciding()).toBe(false);
  });
});

describe('QA probe - approve 428 (missing If-Match, our bug)', () => {
  it('keepsTheDialogOpenWithAGenericKeyAndReleasesTheHold', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.willFailApprove(appError('precondition_required', 428, 'precondition'));

    await state.store.approve();

    expect(state.store.dialog()).toBe('approve');
    expect(state.store.errorKey()).not.toBeNull();
  });
});

describe('QA probe - idempotency on reject', () => {
  it('replaysTheSameIntentWhileTheOutcomeIsUnknown', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('network_error', 0, 'network'));

    await state.store.reject();
    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(2);
    expect(state.repo.rejectCalls[1]?.intent).toBe(state.repo.rejectCalls[0]?.intent);
    expect(state.repo.rejectCalls[1]?.intent.key).toBe(state.repo.rejectCalls[0]?.intent.key);
  });

  it('usesAFreshKeyAfterARefusedReject', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    state.repo.willFailReject(appError('episode_conflict', 412, 'precondition'));

    await state.store.reject();
    expect(state.store.needsRefresh()).toBe(false);

    state.store.openReject('story');
    state.store.reason.setValue('Wrong tone');
    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(2);
    expect(state.repo.rejectCalls[1]?.intent.key).not.toBe(state.repo.rejectCalls[0]?.intent.key);
  });
});

describe('QA probe - session change while a URL read is pending', () => {
  it('discardsTheLateUrlWhenTheSessionGenerationMoves', async () => {
    const state = setup();
    state.repo.deferUrl();
    const pending = state.store.loadUrl();
    await settle();

    state.generation.set(1);
    TestBed.tick();
    state.repo.resolveUrl({ url: 'https://media.example.test/late.mp4?sig=9', expiresAt: '2026-10-19T11:00:00.000+00:00' });
    await pending;

    expect(state.store.url()).toBeNull();
  });
});

describe('QA probe - release with the decision dialog open', () => {
  it('clearsTheOpenDialogAndTheUrl', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();

    state.store.release();

    expect(state.store.url()).toBeNull();
    expect(state.store.dialog()).toBeNull();
  });
});

describe('QA probe - double-click approve', () => {
  it('sendsOneApproveForTwoClicks', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.repo.deferApprove();

    const first = state.store.approve();
    void state.store.approve();
    await settle();

    expect(state.repo.approveCalls).toHaveLength(1);
    state.repo.resolveApprove({ status: 'ready', etag: '"ep-2"' });
    await first;
  });
});

describe('QA probe - approve note bounds', () => {
  it('sends a 500-character note verbatim', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    const note = 'n'.repeat(500);
    state.store.note.setValue(note);

    await state.store.approve();

    expect(state.repo.approveCalls[0]?.note).toBe(note);
  });

  it('blocks a 501-character note and sends nothing', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.store.note.setValue('n'.repeat(501));

    await state.store.approve();

    expect(state.repo.approveCalls).toHaveLength(0);
  });

  it('omits the note field for a blank note', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openApprove();
    state.store.note.setValue('   ');

    await state.store.approve();

    expect(state.repo.approveCalls[0]?.note).toBeUndefined();
  });
});

describe('QA probe - reject reason bounds', () => {
  it('blocks an empty reason', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('');

    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(0);
  });

  it('blocks a whitespace-only reason', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('   ');

    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(0);
  });

  it('blocks a 501-character reason', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('r'.repeat(501));

    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(0);
  });

  // D2: a NUL (U+0000) is refused by the server's `RejectVideoCommandValidator` (PostgreSQL cannot carry it), so the
  // client refuses it on the same control — nothing is sent and the dialog stays open on the reason field.
  it('blocks a reason containing a NUL character and keeps the dialog on the reason field', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('Wrong\u0000tone');

    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(0);
    expect(state.store.dialog()).toBe('reject');
    expect(state.store.reason.touched).toBe(true);
    expect(state.store.reason.invalid).toBe(true);
    expect(state.store.reason.errors?.['nulCharacter']).toBe(true);
  });

  it('sends a reason of 500 characters plus a trailing newline, trimmed to the 500 the wire stores', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    // C8: the server counts after trimming, so 500 characters plus a pasted newline is accepted, not blocked locally.
    state.store.reason.setValue('r'.repeat(500) + '\n');

    await state.store.reject();

    expect(state.repo.rejectCalls).toHaveLength(1);
    expect(state.repo.rejectCalls[0]?.reason).toBe('r'.repeat(500));
  });

  it('sends a 500-character reason verbatim', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('story');
    state.store.reason.setValue('r'.repeat(500));

    await state.store.reject();

    expect(state.repo.rejectCalls[0]?.reason).toHaveLength(500);
  });

  it('sends the re_render target with the current ETag and one intent', async () => {
    const state = setup();
    await state.store.loadUrl();
    state.store.openReject('re_render');
    state.store.reason.setValue('Redo the pacing');

    await state.store.reject();

    expect(state.repo.rejectCalls[0]?.target).toBe('re_render');
    expect(state.repo.rejectCalls[0]?.etag).toBe('"ep-1"');
    expect(state.repo.rejectCalls[0]?.intent).toBeDefined();
  });
});
