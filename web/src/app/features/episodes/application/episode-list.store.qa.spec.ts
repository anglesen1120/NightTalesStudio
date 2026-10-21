import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { SessionApi, SessionStore } from '@core/session';
import { afterEach, describe, expect, it } from 'vitest';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodesRepository, episodeItem } from '../testing/fake-episodes.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { EpisodeListStore } from './episode-list.store';

// QA (10.5.1-T1): adversarial store paths the shipped spec does not cover — the `nextCursor: ''` end-of-list, an
// empty page that still carries a cursor, a second page superseded by a filter change, a session change while a later
// page is in flight, and the cursor-400 contract proved through the REAL AppErrorMapper (`fieldErrors.cursor`) rather
// than a hand-built AppError.

const SERIES_ID = '22222222-2222-4222-8222-222222222222';
const NO_FILTERS = { status: null, seriesId: null } as const;

const cursorError = {
  code: 'validation_failed',
  status: 400,
  kind: 'validation',
  fieldErrors: { cursor: ['The cursor is invalid.'] },
} as const;

/** A 400 as the interceptor would hand it to the store: the mapper, not the fixture, extracts `fieldErrors.cursor`. */
const httpCursorError = new HttpErrorResponse({
  status: 400,
  statusText: 'Bad Request',
  url: '/api/v1/episodes',
  error: { code: 'validation_failed', errors: { cursor: ['The cursor is invalid.'] } },
});

async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

function setup(): {
  store: EpisodeListStore;
  repo: FakeEpisodesRepository;
  series: FakeSeriesOptionsRepository;
  session: SessionStore;
} {
  const repo = new FakeEpisodesRepository();
  const series = new FakeSeriesOptionsRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: EpisodesRepository, useValue: repo },
      { provide: SeriesOptionsRepository, useValue: series },
      EpisodeListStore,
    ],
  });
  return { store: TestBed.inject(EpisodeListStore), repo, series, session: TestBed.inject(SessionStore) };
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodeListStore (QA)', () => {
  it('nextCursor_EmptyString_EndsPagingAndNeverReissuesTheFirstPage', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: '' });
    store.init(NO_FILTERS);
    await settle();

    expect(store.hasMore()).toBe(false);
    const before = repo.calls.length;
    await store.load('more');
    expect(repo.calls).toHaveLength(before);
  });

  it('nextCursor_EmptyStringOnAnEmptyFirstPage_IsHonestlyEmpty', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [], nextCursor: '' });
    store.init(NO_FILTERS);
    await settle();

    expect(store.isEmpty()).toBe(true);
    expect(store.hasMore()).toBe(false);
    expect(store.loading()).toBe(false);
  });

  it('emptyPageWithACursor_KeepsTheListOpenAndAppendsTheNextPage', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [], nextCursor: 'c1' });
    repo.willReturn({ items: [episodeItem(1)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();

    // A page with no rows but a cursor is NOT the empty state (the failure table forbids claiming "no episodes").
    expect(store.isEmpty()).toBe(false);
    expect(store.hasMore()).toBe(true);
    expect(store.items()).toEqual([]);

    await store.load('more');
    expect(store.items().map((item) => item.id)).toEqual(['ep-1']);
    expect(store.hasMore()).toBe(false);
  });

  it('twoRapidLoadMoreCallsInOneTick_IssueOneRequest', async () => {
    const { store, repo } = setup();
    repo.defer();
    store.init(NO_FILTERS);
    repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'c1' });
    await settle();

    const first = store.load('more');
    const second = store.load('more');
    await settle();

    expect(repo.calls).toHaveLength(2);
    expect(repo.pendingCount).toBe(1);
    repo.resolveNext({ items: [episodeItem(2)], nextCursor: null });
    await Promise.all([first, second]);
    expect(store.items().map((item) => item.id)).toEqual(['ep-1', 'ep-2']);
  });

  it('cursorErrorOnAPage_ClearsHasMore_AndRetryRestartsFromPageOneWithoutTheCursor', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'c1' });
    repo.willFail(cursorError);
    repo.willReturn({ items: [episodeItem(2)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();
    expect(store.hasMore()).toBe(true);

    await store.load('more');
    expect(store.error()?.fieldErrors['cursor']).toBeDefined();
    expect(store.hasMore()).toBe(false);

    await store.retry();
    expect(repo.calls).toHaveLength(3);
    expect(repo.calls.at(-1)).toEqual({ limit: 20 });
    expect(store.items().map((item) => item.id)).toEqual(['ep-2']);
    expect(store.error()).toBeNull();
  });

  it('cursorErrorFromARealHttpErrorResponse_IsRecognizedThroughTheRealMapper', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'c1' });
    repo.willFail(httpCursorError);
    repo.willReturn({ items: [episodeItem(2)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();

    await store.load('more');
    // The whole "terminal cursor error" behaviour hinges on `fieldErrors.cursor`; prove the mapper produces that key.
    expect(store.error()?.fieldErrors['cursor']).toBeDefined();
    expect(store.hasMore()).toBe(false);

    await store.retry();
    expect(repo.calls.at(-1)).toEqual({ limit: 20 });
  });

  it('filterChangeWhileASecondPageIsInFlight_DropsTheSupersededRows', async () => {
    const { store, repo } = setup();
    repo.defer();
    store.init(NO_FILTERS);
    repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'c1' });
    await settle();

    void store.load('more'); // page 2 of the unfiltered list stays pending
    store.setStatus('ready');
    await settle();
    expect(repo.calls).toHaveLength(3);

    repo.resolveNext({ items: [episodeItem(9)], nextCursor: 'c9' }, 0); // the superseded page 2
    repo.resolveNext({ items: [episodeItem(3)], nextCursor: null }, 0); // the filtered first page
    await settle();

    const ids = store.items().map((item) => item.id);
    expect(ids).toEqual(['ep-3']);
    expect(ids).not.toContain('ep-9');
  });

  it('sessionChangeWhileASecondPageIsInFlight_DropsTheLateRowsAndIssuesNoFurtherRequest', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    store.init(NO_FILTERS);
    repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'c1' });
    await settle();

    void store.load('more');
    // A different identity (logout/login or a fresh restore) bumps the session generation.
    session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
    repo.resolveNext({ items: [episodeItem(9)], nextCursor: 'c9' });
    await settle();

    expect(store.items().map((item) => item.id)).toEqual(['ep-1']);
    expect(store.error()).toBeNull();

    const before = repo.calls.length;
    await store.load('more');
    await settle();
    expect(repo.calls).toHaveLength(before);
  });

  it('seriesOptions_AreRequestedOncePerStore_AcrossManyFilterChanges', async () => {
    const { store, repo, series } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: null });
    repo.willReturn({ items: [episodeItem(2)], nextCursor: null });
    repo.willReturn({ items: [episodeItem(3)], nextCursor: null });
    repo.willReturn({ items: [episodeItem(4)], nextCursor: null });

    store.init(NO_FILTERS);
    await settle();
    store.setStatus('ready');
    await settle();
    store.setSeries(SERIES_ID);
    await settle();
    store.setFilters({ status: 'failed', seriesId: null });
    await settle();

    expect(series.calls).toBe(1);
    expect(repo.calls).toHaveLength(4);
  });

  it('staleInstanceAfterSessionChangeAndFilterRestart_DoesNotFreezeTheLiveInstance', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    store.init(NO_FILTERS); // instance A: unfiltered first page, still pending
    session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
    store.setStatus('failed'); // restarts into instance B under the new generation
    await settle();
    expect(repo.calls).toHaveLength(2);

    repo.resolveNext({ items: [episodeItem(9)], nextCursor: 'c9' }, 0); // A's late response
    await settle();

    // B must still be the readable instance, so its own page lands and paging continues.
    repo.resolveNext({ items: [episodeItem(3)], nextCursor: 'c3' }, 0);
    await settle();
    expect(store.items().map((item) => item.id)).toEqual(['ep-3']);
    expect(store.hasMore()).toBe(true);

    const nextPage = store.load('more');
    repo.resolveNext({ items: [episodeItem(4)], nextCursor: null });
    await nextPage;
    expect(store.items().map((item) => item.id)).toEqual(['ep-3', 'ep-4']);
    expect(store.hasMore()).toBe(false);
  });

  it('rejectionAfterASessionChange_SetsNoErrorOnTheLiveList', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    store.init(NO_FILTERS);
    session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);

    repo.rejectNext(new HttpErrorResponse({ status: 503 }));
    await settle();

    // A failure that outlives its session must not paint an error (with a dead traceId) on the current list.
    expect(store.error()).toBeNull();
    expect(store.items()).toEqual([]);
    expect(store.loading()).toBe(false);
    expect(store.hasMore()).toBe(false);
  });
});
