import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError, Result } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IdempotencyKeys } from '@core/http';
import { COALESCE_WINDOW_MS, RealtimeService, RealtimeSubscriptions } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import { MeRepository, SessionStore } from '@core/session';
import type { FeatureKey, MeSummary } from '@core/session';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { EpisodeGeneration } from '../domain/episode.model';
import type { SourceCandidate } from '../domain/source-candidate.model';
import { SourceFinderRepository } from '../domain/source-finder.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { FakeSourceFinderRepository, candidate } from '../testing/fake-source-finder.repository';
import { ResearchStore } from './research.store';
import { SourceFinderStore } from './source-finder.store';

const EPISODE_ID = '22222222-2222-4222-8222-222222222222';
const OTHER_EPISODE_ID = '33333333-3333-4333-8333-333333333333';

function appError(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  const kind = status === 0 ? 'network' : status === 404 ? 'not_found' : status === 409 ? 'precondition'
    : status === 422 ? 'client_bug' : status === 429 ? 'rate_limit' : status >= 500 ? 'server' : 'unknown';
  return { code, status, kind, fieldErrors: {}, ...extra };
}

function rejected<T>(error: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- structured domain-port failure
  return Promise.reject(error);
}

/** Lets the store's await chain settle (its awaits are microtasks). */
function flush(): Promise<void> {
  // The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function deferred<T>(): { readonly promise: Promise<T>; resolve(value: T): void } {
  // Same reason as `flush`: `Promise.withResolvers` is not in the project's lib.
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => { resolve = settle; });
  return { promise, resolve };
}

function meSummary(): MeSummary {
  return {
    id: 'user-1', uiLocale: 'vi', role: 'owner', features: ['source_finder'],
    displayName: 'Owner', email: 'owner@example.com',
  };
}

/** The page's own store: the block reads the episode's `generation()` and asks for a re-read after an add. */
class FakeResearchStore {
  readonly generation = signal<EpisodeGeneration | null>(null);
  readonly refreshCalls: string[] = [];

  refresh(episodeId: string): Promise<Result<void>> {
    this.refreshCalls.push(episodeId);
    return Promise.resolve({ ok: true, value: undefined });
  }

  run(state: { kind: string; status: string; errorCode?: string | null }): void {
    this.generation.set({ stage: '', errorCode: null, startedAt: null, processingPaused: null, ...state });
  }
}

/** What the store needs of the realtime service; `FakeEpisodeRealtime` (immediate) and the coalescing registry both fit. */
interface RealtimeFake {
  subscribeEpisode(id: string, handler: () => void): RealtimeSubscription;
  emit(id: string): void;
}

/**
 * The service's own coalescing (README D8): the same `RealtimeSubscriptions` registry the app uses, driven here by
 * `emit`, so a spec can prove that two events inside one 250 ms window cost one read.
 */
class CoalescingRealtime implements RealtimeFake {
  private readonly subscriptions = new RealtimeSubscriptions();

  subscribeEpisode(id: string, handler: () => void): RealtimeSubscription {
    return this.subscriptions.add(id, handler);
  }

  emit(id: string): void {
    this.subscriptions.notify(id);
  }

  /** The service's resync after a (re)connect: every live key's window is opened again. */
  resync(): void {
    this.subscriptions.notifyAll();
  }
}

class FakeMeRepository {
  calls = 0;

  get(): Promise<MeSummary> {
    this.calls += 1;
    return Promise.resolve(meSummary());
  }
}

interface Harness {
  readonly store: SourceFinderStore;
  readonly repo: FakeSourceFinderRepository;
  readonly research: FakeResearchStore;
  readonly realtime: RealtimeFake;
  /** Always present; `withMe: false` simply does not bind it, so `calls === 0` proves the store never reached it. */
  readonly me: FakeMeRepository;
  readonly features: WritableSignal<readonly FeatureKey[]>;
  readonly sessionGeneration: WritableSignal<number>;
  readonly setMeCalls: Array<MeSummary | null>;
}

function setup(options: { features?: readonly FeatureKey[]; realtime?: RealtimeFake; withMe?: boolean } = {}): Harness {
  const repo = new FakeSourceFinderRepository();
  const research = new FakeResearchStore();
  const realtime = options.realtime ?? new FakeEpisodeRealtime();
  const me = new FakeMeRepository();
  const features = signal<readonly FeatureKey[]>(options.features ?? ['source_finder']);
  const sessionGeneration = signal(0);
  const setMeCalls: Array<MeSummary | null> = [];
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, SourceFinderStore, IdempotencyKeys,
    { provide: SourceFinderRepository, useValue: repo },
    { provide: ResearchStore, useValue: research },
    { provide: RealtimeService, useValue: realtime },
    ...(options.withMe === false ? [] : [{ provide: MeRepository, useValue: me }]),
    { provide: SessionStore, useValue: {
      generation: sessionGeneration,
      hasFeature: (key: string) => features().includes(key as FeatureKey),
      setMe: (summary: MeSummary | null) => { setMeCalls.push(summary); },
    } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  return { store: TestBed.inject(SourceFinderStore), repo, research, realtime, me, features, sessionGeneration, setMeCalls };
}

/** The page's own wiring: the store is armed for the episode, then its first read is allowed to settle. */
async function watchAndSettle(harness: Harness, episodeId = EPISODE_ID): Promise<void> {
  harness.store.watch(episodeId);
  await flush();
}

afterEach(() => { vi.useRealTimers(); TestBed.resetTestingModule(); });

describe('SourceFinderStore flag gate', () => {
  it('calls the API not at all while the flag is off, not even through an event or a click', async () => {
    const harness = setup({ features: [] });
    const { store, repo } = harness;
    await watchAndSettle(harness);

    expect(store.visible()).toBe(false);
    harness.realtime.emit(EPISODE_ID);
    await flush();

    const search = await store.find(EPISODE_ID, 'anything');
    expect(search.ok).toBe(false);
    await store.add(EPISODE_ID, 'candidate-1');
    await store.dismiss(EPISODE_ID, 'candidate-1');

    expect(repo.candidatesCalls).toEqual([]);
    expect(repo.findCalls).toEqual([]);
    expect(repo.addCalls).toEqual([]);
    expect(repo.dismissCalls).toEqual([]);
    expect(store.canFind()).toBe(false);
  });

  it('hides the block on a 404, re-reads the profile once and shows no error', async () => {
    const harness = setup();
    const { store, repo, me } = harness;
    repo.scriptCandidates(() => rejected(appError('feature_disabled', 404, { kind: 'feature_disabled' })));
    await watchAndSettle(harness);

    expect(store.visible()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.loading()).toBe(false);
    expect(me.calls).toBe(1);
    expect(harness.setMeCalls).toHaveLength(1);

    // The block is off for this session: another event reads nothing, and the profile is not re-read again.
    harness.realtime.emit(EPISODE_ID);
    await flush();
    expect(repo.candidatesCalls).toHaveLength(1);
    expect(me.calls).toBe(1);
  });

  it('hides the block when the search itself answers 404, with no error on the panel', async () => {
    const harness = setup();
    const { store, repo } = harness;
    await watchAndSettle(harness);
    repo.scriptFind(() => rejected(appError('feature_disabled', 404, { kind: 'feature_disabled' })));

    const result = await store.find(EPISODE_ID);

    expect(result.ok).toBe(false);
    expect(store.visible()).toBe(false);
    expect(store.error()).toBeNull();
    expect(harness.me.calls).toBe(1);
  });

  it('hides the block when a row action answers 404, with no row error', async () => {
    const harness = setup();
    const { store, repo } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);
    repo.scriptDismiss(() => rejected(appError('feature_disabled', 404, { kind: 'feature_disabled' })));

    await store.dismiss(EPISODE_ID, 'candidate-1');

    expect(store.visible()).toBe(false);
    expect(store.rowErrors().size).toBe(0);
  });
});

describe('SourceFinderStore search', () => {
  it('starts one search per click, in flight until the answer, and reads the run state from the episode', async () => {
    const harness = setup();
    const { store, repo, research } = harness;
    await watchAndSettle(harness);

    const pending = store.find(EPISODE_ID, '  bronze hoard  ');
    expect(store.finding()).toBe(true);
    expect(store.canFind()).toBe(false);

    // The double click that lands while the first request is open sends nothing.
    const second = await store.find(EPISODE_ID);
    expect(second.ok).toBe(false);
    expect(repo.findCalls).toHaveLength(1);
    expect(repo.findCalls[0]).toMatchObject({ episodeId: EPISODE_ID, query: 'bronze hoard', intent: 'source.find' });

    await pending;
    expect(store.finding()).toBe(false);
    expect(repo.findCalls).toHaveLength(1);

    // The run state is the episode's own generation, never a local guess.
    research.run({ kind: 'find_sources', status: 'queued' });
    expect(store.running()).toBe(true);
    expect(store.canFind()).toBe(false);
    research.run({ kind: 'find_sources', status: 'running' });
    expect(store.running()).toBe(true);
    research.run({ kind: 'find_sources', status: 'succeeded' });
    expect(store.running()).toBe(false);
    expect(store.canFind()).toBe(true);
  });

  it('sends no query when the caller has none, an empty or a too long one', async () => {
    const harness = setup();
    const { store, repo } = harness;
    await watchAndSettle(harness);

    await store.find(EPISODE_ID);
    await store.find(EPISODE_ID, '   ');
    await store.find(EPISODE_ID, 'x'.repeat(301));
    await store.find(EPISODE_ID, 'x'.repeat(300));

    expect(repo.findCalls.map((call) => call.query)).toEqual([null, null, null, 'x'.repeat(300)]);
  });

  it('replays one intent across the retries of a click and mints a new one after a settled answer', async () => {
    const harness = setup();
    const { store, repo } = harness;
    await watchAndSettle(harness);
    repo.scriptFind(() => rejected(appError('network_error', 0)));
    repo.scriptFind(() => rejected(appError('network_error', 0)));

    const first = await store.find(EPISODE_ID);
    expect(first.ok).toBe(false);
    expect(store.error()?.code).toBe('network_error');

    // The retry of the same click replays the same intent, so the server sees one key for one effect.
    await store.find(EPISODE_ID);
    expect(repo.findCalls[1]!.key).toBe(repo.findCalls[0]!.key);
    expect(repo.findCalls[1]!.intent).toBe('source.find');

    // The attempt that answers settles the intent: the next click is a new intent with a new key.
    await store.find(EPISODE_ID);
    expect(repo.findCalls[2]!.key).toBe(repo.findCalls[0]!.key);
    await store.find(EPISODE_ID);
    expect(repo.findCalls[3]!.key).not.toBe(repo.findCalls[0]!.key);
  });

  it('mints a new key for the next attempt after a 422 idempotency_key_reused', async () => {
    const harness = setup();
    const { store, repo } = harness;
    await watchAndSettle(harness);
    repo.scriptFind(() => rejected(appError('idempotency_key_reused', 422)));

    await store.find(EPISODE_ID);
    await store.find(EPISODE_ID);

    expect(repo.findCalls).toHaveLength(2);
    expect(repo.findCalls[1]!.key).not.toBe(repo.findCalls[0]!.key);
    expect(store.finding()).toBe(false);
  });

  it('shows the in-progress refusal and keeps the running state', async () => {
    const harness = setup();
    const { store, repo, research } = harness;
    await watchAndSettle(harness);
    repo.scriptFind(() => rejected(appError('find_sources_in_progress', 409)));

    const result = await store.find(EPISODE_ID);

    expect(result.ok).toBe(false);
    expect(store.error()?.code).toBe('find_sources_in_progress');
    research.run({ kind: 'find_sources', status: 'running' });
    expect(store.running()).toBe(true);
    expect(store.canFind()).toBe(false);
  });

  it('holds the search for the wait a 429 announces and carries no amounts', async () => {
    const harness = setup();
    const { store, repo } = harness;
    await watchAndSettle(harness);
    repo.scriptFind(() => rejected(appError('spend_cap_reached', 429, { kind: 'rate_limit', retryAfterSec: 42 })));

    await store.find(EPISODE_ID);

    expect(store.retryAfterSec()).toBe(42);
    expect(store.error()).toMatchObject({ code: 'spend_cap_reached', kind: 'rate_limit', status: 429, retryAfterSec: 42 });
    // The copy is the catalogue's `errors.spend_cap_reached`, which carries no amount (D14): the block renders no
    // number of its own and keeps no row error for a panel-level refusal.
    expect(store.rowErrors().size).toBe(0);
    expect(store.canFind()).toBe(false);

    // A second click while the wait is on sends nothing.
    await store.find(EPISODE_ID);
    expect(repo.findCalls).toHaveLength(1);

    store.clearRetry();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.canFind()).toBe(true);
  });

  it('treats a Retry-After of zero as no wait at all', async () => {
    const harness = setup();
    const { store, repo } = harness;
    await watchAndSettle(harness);
    repo.scriptFind(() => rejected(appError('rate_limited', 429, { kind: 'rate_limit', retryAfterSec: 0 })));

    await store.find(EPISODE_ID);

    expect(store.retryAfterSec()).toBeNull();
    expect(store.canFind()).toBe(true);
  });

  it("reports a failed run until the owner starts a new search, and reports that new run's own failure", async () => {
    const harness = setup();
    const { store, research } = harness;
    await watchAndSettle(harness);

    // The run the page found on load failed: the block says so.
    research.run({ kind: 'find_sources', status: 'failed', errorCode: 'find_sources_generation_failed' });
    expect(store.failedRun()).toBe(true);

    // The click retires that message at once (it is about the run before this search).
    await store.find(EPISODE_ID);
    expect(store.failedRun()).toBe(false);

    // The run this click started reports a state of its own; it fails, and that failure is this block's message.
    research.run({ kind: 'find_sources', status: 'running' });
    await flush();
    TestBed.tick();
    research.run({ kind: 'find_sources', status: 'failed', errorCode: 'find_sources_generation_failed' });
    expect(store.failedRun()).toBe(true);
  });

  it("never reports another kind of run as this block's failure", async () => {
    const harness = setup();
    const { store, research } = harness;
    await watchAndSettle(harness);

    research.run({ kind: 'story', status: 'failed', errorCode: 'story_moderated' });
    expect(store.failedRun()).toBe(false);
    expect(store.running()).toBe(false);
  });
});

describe('SourceFinderStore candidates', () => {
  it('loads the candidates when the block becomes visible, in the server order', async () => {
    const harness = setup();
    const { store, repo } = harness;
    repo.seed([candidate(2), candidate(1, { status: 'dismissed' })]);

    await watchAndSettle(harness);

    expect(repo.candidatesCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
    expect(store.candidates().map((row) => row.id)).toEqual(['candidate-2', 'candidate-1']);
    expect(store.candidates()[1]!.status).toBe('dismissed');
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('refetches on a realtime event as a background read, and stays quiet without one', async () => {
    const harness = setup();
    const { repo, realtime } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);

    realtime.emit(EPISODE_ID);
    await flush();
    realtime.emit(EPISODE_ID);
    await flush();

    expect(repo.candidatesCalls).toEqual([
      { episodeId: EPISODE_ID, userAction: true },
      { episodeId: EPISODE_ID, userAction: false },
      { episodeId: EPISODE_ID, userAction: false },
    ]);

    // Nothing happens without an event: the block starts no timer of its own.
    await flush();
    await flush();
    expect(repo.candidatesCalls).toHaveLength(3);
  });

  it('turns a burst of events and a resync inside one 250 ms window into a single read, and reads nothing on its own', async () => {
    vi.useFakeTimers();
    const realtime = new CoalescingRealtime();
    const harness = setup({ realtime });
    const { store, repo } = harness;
    repo.seed([candidate(1)]);
    store.watch(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(0);
    expect(repo.candidatesCalls).toHaveLength(1);

    realtime.emit(EPISODE_ID);
    realtime.emit(EPISODE_ID);
    realtime.resync();
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS);
    expect(repo.candidatesCalls).toHaveLength(2);

    // No timer of this store's own: nothing happens without an event.
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS * 4);
    expect(repo.candidatesCalls).toHaveLength(2);
  });

  it('picks up a candidate committed while its own read was open, with one trailing read per burst', async () => {
    vi.useFakeTimers();
    const realtime = new CoalescingRealtime();
    const harness = setup({ realtime });
    const { store, repo } = harness;
    store.watch(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(0);
    expect(repo.candidatesCalls).toHaveLength(1);
    expect(store.candidates()).toEqual([]);

    // A read the server answers slowly.
    const slow = deferred<readonly SourceCandidate[]>();
    repo.scriptCandidates(() => slow.promise);
    realtime.emit(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS);
    expect(repo.candidatesCalls).toHaveLength(2);

    // The server commits a candidate and announces it — twice — while that read is still open.
    repo.seed([candidate(1)]);
    realtime.emit(EPISODE_ID);
    realtime.emit(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS);
    expect(repo.candidatesCalls).toHaveLength(2);

    // The open read answers with the list as it was before the commit; the queued read is what picks the row up.
    slow.resolve([]);
    await vi.advanceTimersByTimeAsync(0);
    expect(repo.candidatesCalls).toHaveLength(3);
    expect(store.candidates().map((row) => row.id)).toEqual(['candidate-1']);

    // One trailing read per burst, and nothing polls.
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS * 4);
    expect(repo.candidatesCalls).toHaveLength(3);
  });

  it('drops a queued read of the episode the page has left', async () => {
    vi.useFakeTimers();
    const realtime = new CoalescingRealtime();
    const harness = setup({ realtime });
    const { store, repo } = harness;
    store.watch(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(0);

    const slow = deferred<readonly SourceCandidate[]>();
    repo.scriptCandidates(() => slow.promise);
    realtime.emit(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS);
    realtime.emit(EPISODE_ID);
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS);

    repo.seed([]);
    store.watch(OTHER_EPISODE_ID);
    await vi.advanceTimersByTimeAsync(0);
    const afterSwitch = repo.candidatesCalls.length;
    slow.resolve([]);
    await vi.advanceTimersByTimeAsync(COALESCE_WINDOW_MS * 4);

    // The read that was open for the old episode may end, but its queued follow-up never runs.
    expect(repo.candidatesCalls).toHaveLength(afterSwitch);
  });

  it('fails the first read as the block error and swallows a background one', async () => {
    const harness = setup();
    const { store, repo, realtime } = harness;
    repo.scriptCandidates(() => rejected(appError('internal_error', 500)));
    await watchAndSettle(harness);

    expect(store.error()?.code).toBe('internal_error');
    expect(store.loading()).toBe(false);

    store.clearError();
    repo.scriptCandidates(() => rejected(appError('internal_error', 500)));
    realtime.emit(EPISODE_ID);
    await flush();

    expect(store.error()).toBeNull();
  });

  it('drops a late answer of the episode the page has left', async () => {
    const harness = setup();
    const { store, repo } = harness;
    const slow = deferred<readonly SourceCandidate[]>();
    repo.scriptCandidates(() => slow.promise);
    store.watch(EPISODE_ID);
    repo.seed([candidate(9)]);
    store.watch(OTHER_EPISODE_ID);
    await flush();

    slow.resolve([candidate(1)]);
    await flush();

    expect(store.candidates().map((row) => row.id)).toEqual(['candidate-9']);
  });

  it('arms one episode at a time, and stays armed for the episode it is already watching', async () => {
    const harness = setup();
    const { store, repo, realtime } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);

    store.watch(EPISODE_ID);
    await flush();
    expect(repo.candidatesCalls).toHaveLength(1);

    store.watch(OTHER_EPISODE_ID);
    await flush();
    expect(repo.candidatesCalls).toEqual([
      { episodeId: EPISODE_ID, userAction: true },
      { episodeId: OTHER_EPISODE_ID, userAction: true },
    ]);

    realtime.emit(EPISODE_ID);
    await flush();
    expect(repo.candidatesCalls).toHaveLength(2);
  });
});

describe('SourceFinderStore rows', () => {
  it('adds one row per click and re-reads the page data and the row after the 201', async () => {
    const harness = setup();
    const { store, repo, research } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);

    const pending = store.add(EPISODE_ID, 'candidate-1');
    expect(store.busyId()).toBe('candidate-1');
    // A second row cannot act while the first request is open.
    const second = await store.add(EPISODE_ID, 'candidate-2');
    expect(second.ok).toBe(false);
    expect(repo.addCalls).toHaveLength(1);

    await pending;
    expect(store.busyId()).toBeNull();
    // `refresh` is the research read that re-reads the source list (`load` no-ops once the page has loaded), so the
    // new source appears above; the block itself re-reads the candidates behind it.
    expect(research.refreshCalls).toEqual([EPISODE_ID]);
    expect(repo.candidatesCalls.map((call) => call.userAction)).toEqual([true, false]);
  });

  it('marks a dismissed row locally and never touches the source list', async () => {
    const harness = setup();
    const { store, repo, research } = harness;
    repo.seed([candidate(1), candidate(2)]);
    await watchAndSettle(harness);

    const result = await store.dismiss(EPISODE_ID, 'candidate-1');

    expect(result.ok).toBe(true);
    expect(store.candidates().map((row) => [row.id, row.status])).toEqual([
      ['candidate-1', 'dismissed'],
      ['candidate-2', 'proposed'],
    ]);
    expect(store.busyId()).toBeNull();
    expect(research.refreshCalls).toEqual([]);
  });

  it('refreshes a row another tab already handled, with no second error', async () => {
    const harness = setup();
    const { store, repo, research } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);
    repo.scriptAdd(() => rejected(appError('source_candidate_not_proposed', 409)));
    repo.seed([candidate(1, { status: 'added', sourceId: 'source-1' })]);

    const result = await store.add(EPISODE_ID, 'candidate-1');

    expect(result.ok).toBe(false);
    expect(store.rowErrors().size).toBe(0);
    expect(store.candidates()[0]!.status).toBe('added');
    expect(repo.candidatesCalls).toHaveLength(2);
    // Nothing was created, so the page's own data is not re-read.
    expect(research.refreshCalls).toEqual([]);
  });

  it('keeps a row error on its own row and clears one without touching the other', async () => {
    const harness = setup();
    const { store, repo } = harness;
    repo.seed([candidate(1), candidate(2)]);
    await watchAndSettle(harness);
    repo.scriptAdd(() => rejected(appError('source_duplicate', 409)));
    repo.scriptDismiss(() => rejected(appError('not_found', 404)));

    await store.add(EPISODE_ID, 'candidate-1');
    await store.dismiss(EPISODE_ID, 'candidate-2');

    expect(store.rowErrors().get('candidate-1')?.code).toBe('source_duplicate');
    expect(store.rowErrors().get('candidate-2')?.code).toBe('not_found');
    // The row the server does not have reloads the list.
    expect(repo.candidatesCalls).toHaveLength(2);
    expect(store.error()).toBeNull();

    store.clearRowError('candidate-1');
    expect(store.rowErrors().has('candidate-1')).toBe(false);
    expect(store.rowErrors().get('candidate-2')?.code).toBe('not_found');
  });

  it('reports a candidate the server does not have on its own row, reloads the list and leaves the panel quiet', async () => {
    const harness = setup();
    const { store, repo, research } = harness;
    repo.seed([candidate(1), candidate(2)]);
    await watchAndSettle(harness);
    repo.scriptAdd(() => rejected(appError('not_found', 404)));
    repo.scriptDismiss(() => rejected(appError('not_found', 404)));

    const addResult = await store.add(EPISODE_ID, 'candidate-1');
    const dismissResult = await store.dismiss(EPISODE_ID, 'candidate-2');

    expect(addResult.ok).toBe(false);
    expect(dismissResult.ok).toBe(false);
    expect(store.rowErrors().get('candidate-1')?.code).toBe('not_found');
    expect(store.rowErrors().get('candidate-2')?.code).toBe('not_found');
    // A 404 on a row is never the panel's message, and nothing was created so the page data is not re-read.
    expect(store.error()).toBeNull();
    expect(research.refreshCalls).toEqual([]);
    expect(store.busyId()).toBeNull();
    // Each refused row asks the server for the list again, and no other row is disturbed.
    expect(repo.candidatesCalls).toHaveLength(3);
  });

  it('holds only the refused row for the wait a row 429 announces', async () => {
    const harness = setup();
    const { store, repo } = harness;
    repo.seed([candidate(1), candidate(2)]);
    await watchAndSettle(harness);
    repo.scriptAdd(() => rejected(appError('source_fetch_limit_reached', 429, { kind: 'rate_limit', retryAfterSec: 60 })));

    await store.add(EPISODE_ID, 'candidate-1');

    expect(store.rowRetryAfterSec()).toBe(60);
    expect(store.retryAfterSec()).toBeNull();
    expect(store.rowErrors().get('candidate-1')).toMatchObject({ code: 'source_fetch_limit_reached', retryAfterSec: 60 });

    const blocked = await store.add(EPISODE_ID, 'candidate-2');
    expect(blocked.ok).toBe(false);
    expect(repo.addCalls).toHaveLength(1);

    // The panel's countdown reached zero.
    store.clearRowError('candidate-1');
    expect(store.rowRetryAfterSec()).toBeNull();
    expect(store.busyId()).toBeNull();
  });

  it('holds a row for a source limit the server reports', async () => {
    const harness = setup();
    const { store, repo } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);
    repo.scriptAdd(() => rejected(appError('source_limit_reached', 409)));

    await store.add(EPISODE_ID, 'candidate-1');

    expect(store.rowErrors().get('candidate-1')?.code).toBe('source_limit_reached');
    expect(store.rowRetryAfterSec()).toBeNull();
    // The sources list is unchanged: nothing was created.
    expect(harness.research.refreshCalls).toEqual([]);
  });
});

describe('SourceFinderStore lifecycle', () => {
  it('drops the list, the errors and the intent on reset, so the next search is a new intent', async () => {
    const harness = setup();
    const { store, repo } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);
    await store.find(EPISODE_ID);
    const firstKey = repo.findCalls[0]!.key;
    repo.scriptAdd(() => rejected(appError('source_duplicate', 409)));
    await store.add(EPISODE_ID, 'candidate-1');

    store.reset();

    expect(store.candidates()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(store.rowErrors().size).toBe(0);
    expect(store.busyId()).toBeNull();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.loading()).toBe(false);
    expect(store.finding()).toBe(false);

    await watchAndSettle(harness);
    await store.find(EPISODE_ID);
    expect(repo.findCalls[1]!.key).not.toBe(firstKey);
  });

  it('drops everything when the session generation changes', async () => {
    const harness = setup();
    const { store, repo, sessionGeneration } = harness;
    repo.seed([candidate(1)]);
    await watchAndSettle(harness);
    expect(store.candidates()).toHaveLength(1);

    sessionGeneration.update((value) => value + 1);
    TestBed.tick();

    expect(store.candidates()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(store.busyId()).toBeNull();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.visible()).toBe(true);
  });

  it('ignores a late answer of the session that has ended', async () => {
    const harness = setup();
    const { store, repo, sessionGeneration } = harness;
    const slow = deferred<readonly SourceCandidate[]>();
    repo.scriptCandidates(() => slow.promise);
    store.watch(EPISODE_ID);
    await flush();

    sessionGeneration.update((value) => value + 1);
    TestBed.tick();
    slow.resolve([candidate(1)]);
    await flush();

    expect(store.candidates()).toEqual([]);
  });

  it('hides the block even when the profile port is not bound (outside the shell)', async () => {
    const harness = setup({ withMe: false });
    harness.repo.scriptCandidates(() => rejected(appError('feature_disabled', 404, { kind: 'feature_disabled' })));

    await watchAndSettle(harness);

    // Without a profile to re-read the block still hides, and nothing is reported: the hide never depends on the read.
    expect(harness.me.calls).toBe(0);
    expect(harness.store.visible()).toBe(false);
    expect(harness.store.error()).toBeNull();
    expect(harness.store.candidates()).toEqual([]);
  });
});
