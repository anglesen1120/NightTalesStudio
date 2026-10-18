import { DOCUMENT } from '@angular/common';
import { signal } from '@angular/core';
import type { Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { EpisodeDetail } from '../domain/episode.model';
import type { RenderJob } from '../domain/render.model';
import { EPISODE_ID, episodeDetail } from '../testing/fake-episodes.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { RenderTimelineStore, WAITING_FOR_SLOT_SEC, elapsedSeconds } from './render-timeline.store';

const OTHER_EPISODE_ID = '33333333-3333-4333-8333-333333333333';
const JOB_ID = 'job-1';

/** One entry of the live progress map (10.2.4-T1): `{ stage, percent }`, with a job id only when the entry names one. */
interface LiveEntry {
  readonly stage: string;
  readonly percent: number;
  readonly renderJobId?: string;
}

/** Stand-in for `RealtimeService`: the progress map, and the episode registrations the store made. */
class FakeRealtime {
  private readonly state = signal<ReadonlyMap<string, LiveEntry>>(new Map());
  readonly progress: Signal<ReadonlyMap<string, LiveEntry>> = this.state.asReadonly();
  readonly subscriptions: Array<{ readonly episodeId: string; readonly handler: () => void; unsubscribed: boolean }> = [];

  subscribeEpisode(episodeId: string, handler: () => void): RealtimeSubscription {
    const entry = { episodeId, handler, unsubscribed: false };
    this.subscriptions.push(entry);
    return { unsubscribe: () => { entry.unsubscribed = true; } };
  }

  set(episodeId: string, entry: LiveEntry | null): void {
    const next = new Map(this.state());
    if (entry === null) next.delete(episodeId);
    else next.set(episodeId, entry);
    this.state.set(next);
  }

  get unsubscribedCount(): number {
    return this.subscriptions.filter((entry) => entry.unsubscribed).length;
  }
}

/** Stand-in for `EpisodeDetailStore`: one settable episode, and a log of the reads it was asked for. */
class FakeDetailStore {
  readonly episodeState = signal<EpisodeDetail | null>(null);
  readonly episode = this.episodeState.asReadonly();
  readonly detail = this.episode;
  readonly loadCalls: string[] = [];
  readonly refetchCalls: Array<{ userAction?: boolean } | undefined> = [];

  load(id: string): Promise<EpisodeDetail | null> {
    this.loadCalls.push(id);
    return Promise.resolve(this.episodeState());
  }

  refetch(options?: { userAction?: boolean }): Promise<EpisodeDetail | null> {
    this.refetchCalls.push(options);
    return Promise.resolve(this.episodeState());
  }
}

/** A `Document` with only what the store uses, so a test can hide and show the tab deterministically. */
class FakeDocument {
  visibilityState: DocumentVisibilityState = 'visible';
  private listeners: Array<() => void> = [];

  addEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange' && !this.listeners.includes(listener)) this.listeners.push(listener);
  }

  removeEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange') this.listeners = this.listeners.filter((entry) => entry !== listener);
  }

  get listenerCount(): number {
    return this.listeners.length;
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

/** One configured TestBed: the store under test plus the three fakes it was given. */
interface Harness {
  readonly store: RenderTimelineStore;
  readonly realtime: FakeRealtime;
  readonly detail: FakeDetailStore;
  readonly document: FakeDocument;
}

function setup(): Harness {
  const realtime = new FakeRealtime();
  const detail = new FakeDetailStore();
  const document = new FakeDocument();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      RenderTimelineStore,
      { provide: EpisodeDetailStore, useValue: detail },
      { provide: RealtimeService, useValue: realtime },
      { provide: DOCUMENT, useValue: document },
    ],
  });
  return { store: TestBed.inject(RenderTimelineStore), realtime, detail, document };
}

function job(overrides: Partial<RenderJob> = {}): RenderJob {
  return {
    renderJobId: JOB_ID,
    status: 'queued',
    stage: 'queued',
    percent: 0,
    attempt: 1,
    imagesNeeded: 0,
    submittedAt: null,
    finishedAt: null,
    errorCode: null,
    hints: [],
    images: null,
    ...overrides,
  };
}

function renderingEpisode(renderJob: RenderJob | null, overrides: Partial<EpisodeDetail> = {}): EpisodeDetail {
  return episodeDetail({ id: EPISODE_ID, status: 'rendering', renderJob, ...overrides });
}

/** The page's own init: the detail is there, the store is armed for the id, and the first flush has run. */
function armed(harness: Harness, renderJob: RenderJob | null): void {
  harness.detail.episodeState.set(renderingEpisode(renderJob));
  harness.store.start(EPISODE_ID);
  TestBed.tick();
}

describe('RenderTimelineStore', () => {
  beforeEach(() => {
    // Only the clock and the interval: the tick is the store's, and nothing else in the app may be faked away.
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
  });

  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('registers the episode and issues no read of its own', () => {
    const harness = setup();
    harness.detail.episodeState.set(renderingEpisode(job()));
    harness.store.start(EPISODE_ID);

    expect(harness.realtime.subscriptions.map((entry) => entry.episodeId)).toEqual([EPISODE_ID]);
    expect(harness.detail.loadCalls).toEqual([]);
    expect(harness.detail.refetchCalls).toEqual([]);
  });

  it('shows the detail job while no live entry exists, and merges the live stage and percent over it', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 10, submittedAt: '2026-10-18T09:59:50.000+00:00' }));

    expect(harness.store.job()?.stage).toBe('voice');
    expect(harness.store.percent()).toBe(10);
    expect(harness.store.subStepKey()).toBe('voice');

    harness.realtime.set(EPISODE_ID, { stage: 'assembling', percent: 42 });
    TestBed.tick();

    expect(harness.store.job()?.stage).toBe('assembling');
    expect(harness.store.percent()).toBe(42);
    expect(harness.store.subStepKey()).toBe('assembling');
    // The detail's own fields survive the merge.
    expect(harness.store.job()?.status).toBe('running');
    expect(harness.store.job()?.submittedAt).toBe('2026-10-18T09:59:50.000+00:00');
  });

  it('ignores a live entry that names another job of the same episode', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued', percent: 0 }));

    harness.realtime.set(EPISODE_ID, { stage: 'assembling', percent: 42, renderJobId: 'job-old' });
    TestBed.tick();

    // The stale entry contributes nothing: the authoritative detail is shown, and no raw stage reaches the view.
    expect(harness.store.job()?.stage).toBe('queued');
    expect(harness.store.percent()).toBe(0);
    expect(harness.store.steps().some((step) => step.subStepKey === 'assembling')).toBe(false);

    harness.realtime.set(EPISODE_ID, { stage: 'assembling', percent: 42, renderJobId: JOB_ID });
    TestBed.tick();

    expect(harness.store.job()?.stage).toBe('assembling');
    expect(harness.store.percent()).toBe(42);
  });

  it('moves the step list when a live running event beats a detail still saying queued', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued', percent: 0, submittedAt: null }));

    // The detail alone: the job waits for a slot long enough to reach the notice, so `queued` is the active step.
    vi.advanceTimersByTime(WAITING_FOR_SLOT_SEC * 1000 + 1000);
    expect(harness.store.steps().map((step) => `${step.key}:${step.state}`)).toEqual(['queued:active', 'running:todo', 'ingesting:todo']);
    expect(harness.store.elapsedSec()).toBe(31);
    expect(harness.store.waitingForSlot()).toBe(true);

    harness.realtime.set(EPISODE_ID, { stage: 'running', percent: 42 });
    TestBed.tick();

    // The live stage wins for the step list: a 42 % bar must not sit under an active `queued` step. The detail keeps
    // its own fields, the elapsed baseline it stamped survives, and a job that is running is no longer "waiting for a
    // slot" — that state is `queued` only.
    expect(harness.store.job()?.status).toBe('running');
    expect(harness.store.job()?.stage).toBe('running');
    expect(harness.store.percent()).toBe(42);
    expect(harness.store.steps().map((step) => `${step.key}:${step.state}`)).toEqual(['queued:done', 'running:active', 'ingesting:todo']);
    expect(harness.store.subStepKey()).toBeNull();
    expect(harness.store.active()).toBe(true);
    expect(harness.store.elapsedSec()).toBe(31);
    expect(harness.store.waitingForSlot()).toBe(false);

    // The clock keeps running on the same baseline: the promotion changes the shown status, not the wait it came from.
    vi.advanceTimersByTime(1000);
    expect(harness.store.elapsedSec()).toBe(32);
  });

  it('promotes nothing for a live stage that is not ahead of the detail: a live queued keeps queued active', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued', percent: 0, submittedAt: null }));

    harness.realtime.set(EPISODE_ID, { stage: 'queued', percent: 3 });
    TestBed.tick();

    expect(harness.store.job()?.status).toBe('queued');
    expect(harness.store.steps().map((step) => `${step.key}:${step.state}`)).toEqual(['queued:active', 'running:todo', 'ingesting:todo']);
  });

  it('never shows the progress of another episode', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 10 }));

    harness.realtime.set(OTHER_EPISODE_ID, { stage: 'assembling', percent: 77 });
    TestBed.tick();

    expect(harness.store.job()?.stage).toBe('voice');
    expect(harness.store.percent()).toBe(10);
  });

  it('ignores an unknown live stage and clamps an out-of-range percent', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 10 }));

    harness.realtime.set(EPISODE_ID, { stage: 'v2', percent: 250 });
    TestBed.tick();

    expect(harness.store.job()?.stage).toBe('voice');
    expect(harness.store.percent()).toBe(100);
    expect(harness.store.subStepKey()).toBe('voice');
    expect(JSON.stringify(harness.store.job())).not.toContain('v2');
  });

  it('merges a live entry that arrived before the detail did', () => {
    const harness = setup();
    harness.realtime.set(EPISODE_ID, { stage: 'images', percent: 30 });
    harness.store.start(EPISODE_ID);
    TestBed.tick();

    expect(harness.store.job()).toBeNull();

    harness.detail.episodeState.set(renderingEpisode(job({ status: 'running', stage: 'voice', percent: 10 })));
    TestBed.tick();

    expect(harness.store.job()?.stage).toBe('images');
    expect(harness.store.percent()).toBe(30);
  });

  it('waits for a slot only after 30 s without a submit time, and counts from the queued baseline', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued', submittedAt: null }));

    expect(harness.store.queuedSince()).not.toBeNull();
    expect(harness.store.elapsedSec()).toBe(0);
    expect(harness.store.waitingForSlot()).toBe(false);

    vi.advanceTimersByTime(WAITING_FOR_SLOT_SEC * 1000 - 1000);
    expect(harness.store.elapsedSec()).toBe(29);
    expect(harness.store.waitingForSlot()).toBe(false);

    // The threshold is inclusive (`>= 30`): 29 s is not waiting, 30 s is.
    vi.advanceTimersByTime(1000);
    expect(harness.store.elapsedSec()).toBe(30);
    expect(harness.store.waitingForSlot()).toBe(true);

    vi.advanceTimersByTime(1000);
    expect(harness.store.elapsedSec()).toBe(31);
    expect(harness.store.waitingForSlot()).toBe(true);
  });

  it('stops waiting once the job reports a submit time, and counts from it', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued', submittedAt: null }));
    vi.advanceTimersByTime(WAITING_FOR_SLOT_SEC * 1000 + 1000);
    expect(harness.store.waitingForSlot()).toBe(true);

    const submittedAt = new Date(Date.now() - 5000).toISOString();
    harness.detail.episodeState.set(renderingEpisode(job({ status: 'running', stage: 'voice', percent: 5, submittedAt })));
    TestBed.tick();

    expect(harness.store.queuedSince()).toBeNull();
    expect(harness.store.elapsedSec()).toBe(5);
    expect(harness.store.waitingForSlot()).toBe(false);
  });

  it('runs the clock from the submit time and never from the finish time', () => {
    const harness = setup();
    const submittedAt = new Date(Date.now() - 61_000).toISOString();
    const finishedAt = new Date().toISOString();
    armed(harness, job({ status: 'succeeded', stage: 'ingesting', percent: 100, submittedAt, finishedAt }));

    expect(harness.store.elapsedSec()).toBe(61);
    // A finished job is not active, so its clock is not running: the number it shows is the number it keeps.
    expect(harness.store.active()).toBe(false);
    const stopped = harness.store.now();
    vi.advanceTimersByTime(4000);
    expect(harness.store.now()).toBe(stopped);
  });

  it('stops the interval at the first terminal status', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 40 }));
    vi.advanceTimersByTime(1000);
    const running = harness.store.now();

    harness.detail.episodeState.set(renderingEpisode(job({ status: 'failed', stage: 'assembling', percent: 61, errorCode: 'render_assembly_failed' })));
    TestBed.tick();

    expect(harness.store.active()).toBe(false);
    const stopped = harness.store.now();
    vi.advanceTimersByTime(5000);
    expect(stopped).toBe(running);
    expect(harness.store.now()).toBe(stopped);
  });

  it('stops the interval while the tab is hidden and resumes when it is shown', () => {
    const harness = setup();
    const submittedAt = new Date(Date.now() - 10_000).toISOString();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 40, submittedAt }));
    expect(harness.store.elapsedSec()).toBe(10);
    const start = harness.store.now();

    harness.document.hide();
    TestBed.tick();

    expect(harness.store.visible()).toBe(false);
    vi.advanceTimersByTime(3000);
    expect(harness.store.now()).toBe(start);

    harness.document.show();
    TestBed.tick();

    expect(harness.store.visible()).toBe(true);
    vi.advanceTimersByTime(1000);
    // The resuming interval stamps the clock it finds, not the clock it left: the elapsed time stayed honest.
    expect(harness.store.now()).toBe(start + 4000);
    expect(harness.store.elapsedSec()).toBe(14);
  });

  it('stamps the wall clock the moment the tab is shown again, before the resuming tick', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued', submittedAt: null }));
    expect(harness.store.waitingForSlot()).toBe(false);
    const start = harness.store.now();

    harness.document.hide();
    TestBed.tick();
    vi.advanceTimersByTime(60_000);
    expect(harness.store.now()).toBe(start);

    harness.document.show();
    // Nothing has ticked since the tab was hidden, so only the visibility handler can move the clock here: without it
    // the view would show the pre-hide instant until the re-armed interval's first tick, a second later.
    expect(harness.store.now()).toBe(start + 60_000);
    expect(harness.store.elapsedSec()).toBe(60);
    expect(harness.store.waitingForSlot()).toBe(true);
  });

  it('runs no clock while the document was already hidden when the store was armed', () => {
    const harness = setup();
    harness.document.hide();
    const submittedAt = new Date(Date.now() - 5000).toISOString();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 10, submittedAt }));

    const start = harness.store.now();
    vi.advanceTimersByTime(4000);
    expect(harness.store.now(), 'a hidden tab never ticks').toBe(start);
    expect(harness.store.elapsedSec()).toBe(5);

    harness.document.show();
    TestBed.tick();
    vi.advanceTimersByTime(1000);
    expect(harness.store.now(), 'showing the tab re-arms the clock at the current wall time').toBe(start + 5000);
    expect(harness.store.elapsedSec()).toBe(10);
  });

  it('reads nothing while progress events arrive', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 10 }));

    harness.realtime.set(EPISODE_ID, { stage: 'assembling', percent: 60 });
    TestBed.tick();
    vi.advanceTimersByTime(2000);
    harness.realtime.set(EPISODE_ID, { stage: 'checking', percent: 95 });
    TestBed.tick();

    expect(harness.store.percent()).toBe(95);
    expect(harness.detail.loadCalls).toEqual([]);
    expect(harness.detail.refetchCalls).toEqual([]);
  });

  it('lets an ingesting detail beat a live assembling stage, and shows no sub-step then', () => {
    const harness = setup();
    armed(harness, job({ status: 'ingesting', stage: 'ingesting', percent: 95, submittedAt: new Date(Date.now() - 120_000).toISOString() }));

    harness.realtime.set(EPISODE_ID, { stage: 'assembling', percent: 60 });
    TestBed.tick();

    expect(harness.store.job()?.status).toBe('ingesting');
    expect(harness.store.subStepKey()).toBeNull();
    expect(harness.store.steps().map((step) => `${step.key}:${step.state}`)).toEqual(['queued:done', 'running:done', 'ingesting:active']);

    // The live promotion is only for a detail still queued/submitted: an ingesting detail is never pulled back.
    harness.realtime.set(EPISODE_ID, { stage: 'running', percent: 42 });
    TestBed.tick();
    expect(harness.store.job()?.status).toBe('ingesting');
    expect(harness.store.steps().map((step) => `${step.key}:${step.state}`)).toEqual(['queued:done', 'running:done', 'ingesting:active']);
  });

  it('opens the failure panel from the error code and the skipped scenes, and never for a cancelled job', () => {
    const harness = setup();
    const images = { needed: 3, succeeded: 1, skipped: [{ segment: 'seg-1', reason: 'moderated' }, { segment: 'not-an-address', reason: 'timeout' }] };
    armed(harness, job({ status: 'failed', stage: 'assembling', percent: 61, errorCode: 'image_generation_failed', images }));

    expect(harness.store.failure()).toEqual({
      code: 'image_generation_failed',
      scenes: [{ segment: 'seg-1', reason: 'moderated' }, { segment: 'not-an-address', reason: 'timeout' }],
      detailCode: 'timeout',
    });

    harness.detail.episodeState.set(renderingEpisode(job({ status: 'cancelled', stage: 'running', errorCode: 'render_cancelled' })));
    TestBed.tick();

    expect(harness.store.failure()).toBeNull();
    expect(harness.store.active()).toBe(false);
  });

  it('shows a plain job with no error as no failure at all', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 12 }));

    expect(harness.store.failure()).toBeNull();
  });

  it('opens the generic failure for a failed job the wire gave no code for, and still carries its scenes', () => {
    const harness = setup();
    const images = { needed: 3, succeeded: 1, skipped: [{ segment: 'p:3', reason: 'timeout' }] };
    armed(harness, job({ status: 'failed', stage: 'assembling', percent: 61, errorCode: null, images }));

    // No code is not "no failure": the panel has a generic title for it, so the user is never left with a dead timeline.
    expect(harness.store.failure()).toEqual({ code: '', scenes: [{ segment: 'p:3', reason: 'timeout' }], detailCode: 'timeout' });

    harness.detail.episodeState.set(renderingEpisode(job({ status: 'failed', stage: 'assembling', errorCode: '', images: null })));
    TestBed.tick();

    expect(harness.store.failure()).toEqual({ code: '', scenes: [], detailCode: null });
  });

  it('keeps a failed job with no images at all readable: the code, no scene and no detail', () => {
    const harness = setup();
    armed(harness, job({ status: 'failed', stage: 'assembling', percent: 61, errorCode: 'render_quality_failed', images: null }));

    expect(harness.store.failure()).toEqual({ code: 'render_quality_failed', scenes: [], detailCode: null });
  });

  it('asks for a re-read only for a rendering episode whose job row has not arrived, and never as a user action', () => {
    const harness = setup();
    armed(harness, null);

    expect(harness.store.needsRefetch()).toBe(true);
    harness.store.refresh();
    expect(harness.detail.refetchCalls).toEqual([undefined]);

    harness.detail.episodeState.set(renderingEpisode(job()));
    TestBed.tick();

    expect(harness.store.needsRefetch()).toBe(false);
  });

  it('is idempotent per episode and restarts for another one', () => {
    const harness = setup();
    armed(harness, job({ status: 'queued', stage: 'queued' }));
    expect(harness.store.queuedSince()).not.toBeNull();

    harness.store.start(EPISODE_ID);
    expect(harness.realtime.subscriptions).toHaveLength(1);
    expect(harness.realtime.unsubscribedCount).toBe(0);

    vi.advanceTimersByTime(5000);
    expect(harness.store.elapsedSec()).toBe(5);

    harness.store.start(OTHER_EPISODE_ID);

    // The previous registration is released, a new one exists, and the clock of the old episode is gone with it.
    expect(harness.realtime.unsubscribedCount).toBe(1);
    expect(harness.realtime.subscriptions.map((entry) => entry.episodeId)).toEqual([EPISODE_ID, OTHER_EPISODE_ID]);
    const moved = harness.store.now();
    vi.advanceTimersByTime(1000);
    expect(harness.store.now()).toBe(moved);
    expect(harness.store.queuedSince()).toBeNull();
    // The detail still describes the previous episode: it is not this store's job.
    expect(harness.store.job()).toBeNull();
    expect(harness.store.needsRefetch()).toBe(false);
  });

  it('releases the subscription and the interval when the page is destroyed', () => {
    const harness = setup();
    armed(harness, job({ status: 'running', stage: 'voice', percent: 40 }));
    vi.advanceTimersByTime(1000);
    const running = harness.store.now();

    TestBed.resetTestingModule();

    expect(harness.realtime.unsubscribedCount).toBe(1);
    expect(harness.document.listenerCount).toBe(0);
    vi.advanceTimersByTime(5000);
    expect(harness.store.now()).toBe(running);

    // Both entry points are inert after destroy: no read, no second registration.
    harness.store.refresh();
    harness.store.start(OTHER_EPISODE_ID);
    expect(harness.detail.refetchCalls).toEqual([]);
    expect(harness.realtime.subscriptions).toHaveLength(1);
  });
});

describe('elapsedSeconds', () => {
  const now = Date.parse('2026-10-18T10:00:00.000Z');

  it.each([
    [null, 0],
    ['', 0],
    ['not a date', 0],
    ['2026-10-18T10:00:00.000Z', 0],
    ['2026-10-18T09:59:30.000Z', 30],
    ['2026-10-18T10:00:30.000Z', 0],
  ] as const)('reads %s as %s s', (since, expected) => {
    expect(elapsedSeconds(since, now)).toBe(expected);
  });

  it('accepts the client-only baseline in epoch milliseconds', () => {
    expect(elapsedSeconds(now - 12_500, now)).toBe(12);
  });
});
