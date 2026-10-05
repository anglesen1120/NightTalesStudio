import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FakeSeriesRepository } from '../testing/fake-series.repository';
import { SeriesDetailStore } from './series-detail.store';
import { SeriesRepository } from './series.repository';

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

interface Setup {
  readonly store: SeriesDetailStore;
  readonly repo: FakeSeriesRepository;
  readonly session: SessionStore;
}

function setup(): Setup {
  const repo = new FakeSeriesRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: SeriesRepository, useValue: repo },
      SeriesDetailStore,
    ],
  });
  return { store: TestBed.inject(SeriesDetailStore), repo, session: TestBed.inject(SessionStore) };
}

describe('SeriesDetailStore', () => {
  it('Load_StoresTheSeriesAndItsEtag', async () => {
    const { store, repo } = setup();
    await store.load('s-ghost');
    expect(repo.getCalls).toEqual(['s-ghost']);
    expect(store.series()?.id).toBe('s-ghost');
    expect(store.series()?.etag).toBe('"se-41"');
    expect(store.loading()).toBe(false);
    expect(store.notFound()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.archived()).toBe(false);
  });

  it('Load_ArchivedSeries_IsStillReadable', async () => {
    const { store } = setup();
    await store.load('s-archived');
    expect(store.series()?.id).toBe('s-archived');
    expect(store.archived()).toBe(true);
    expect(store.notFound()).toBe(false);
  });

  it('Load_NotFound_SetsNotFoundWithoutError', async () => {
    const { store } = setup();
    await store.load('missing');
    expect(store.notFound()).toBe(true);
    expect(store.series()).toBeNull();
    expect(store.error()).toBeNull();
    expect(store.loading()).toBe(false);
  });

  it('Load_NetworkError_SetsError', async () => {
    const { store, repo } = setup();
    repo.failNext(appError('network_error', 0, 'network'));
    await store.load('s-ghost');
    expect(store.error()?.code).toBe('network_error');
    expect(store.notFound()).toBe(false);
    expect(store.loading()).toBe(false);
  });

  it('Load_RateLimited_ExposesRetryAfterSec', async () => {
    const { store, repo } = setup();
    repo.failNext(appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 12 }));
    await store.load('s-ghost');
    expect(store.retryAfterSec()).toBe(12);
  });

  it('Load_ClearsAPreviousFailure', async () => {
    const { store, repo } = setup();
    repo.failNext(appError('network_error', 0, 'network'));
    await store.load('s-ghost');
    expect(store.error()?.code).toBe('network_error');
    await store.load('s-ghost');
    expect(store.error()).toBeNull();
    expect(store.series()?.id).toBe('s-ghost');
    expect(store.retryAfterSec()).toBeNull();
  });

  it('Load_LateResponseAfterANewerLoad_IsDiscarded', async () => {
    const { store, repo } = setup();
    repo.defer();
    const first = store.load('s-ghost');
    repo.defer(false);
    await store.load('s-spiritual');
    repo.resolveAll();
    await first;
    expect(store.series()?.id).toBe('s-spiritual');
    expect(store.loading()).toBe(false);
  });

  it('Load_LateResponseAfterSessionChange_IsDiscarded', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const loading = store.load('s-ghost');
    session.reset();
    repo.resolveAll();
    await loading;
    expect(store.series()).toBeNull();
    expect(store.error()).toBeNull();
  });
});
