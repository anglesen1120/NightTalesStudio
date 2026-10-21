import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FakeSeriesRepository } from '../testing/fake-series.repository';
import { SeriesListStore, SERIES_LIST_PAGE_SIZE } from './series-list.store';
import { SeriesRepository } from './series.repository';

class FakeAnnouncer {
  readonly messages: string[] = [];
  announce(message: string): Promise<void> {
    this.messages.push(message);
    return Promise.resolve();
  }
}

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

/** Lets the fire-and-forget translation lookup and announcement settle before a message assertion. */
async function flush(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: { series: { archive: { failed: 'Không lưu trữ được series' } }, errors: { validation_failed: 'Dữ liệu không hợp lệ' } } },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi' },
  preloadLangs: true,
});

interface Setup {
  readonly store: SeriesListStore;
  readonly repo: FakeSeriesRepository;
  readonly announcer: FakeAnnouncer;
  readonly session: SessionStore;
}

function setup(): Setup {
  const repo = new FakeSeriesRepository();
  const announcer = new FakeAnnouncer();
  TestBed.configureTestingModule({
    imports: [translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: SeriesRepository, useValue: repo },
      { provide: LiveAnnouncer, useValue: announcer },
      SeriesListStore,
    ],
  });
  return {
    store: TestBed.inject(SeriesListStore),
    repo,
    announcer,
    session: TestBed.inject(SessionStore),
  };
}

function ids(store: SeriesListStore): string[] {
  return store.items().map((item) => item.id);
}

describe('SeriesListStore', () => {
  it('Load_FirstPage', async () => {
    const { store, repo } = setup();
    await store.load();
    expect(repo.listCalls).toEqual([{ cursor: null, limit: SERIES_LIST_PAGE_SIZE, includeArchived: false }]);
    expect(ids(store)).toEqual(['s-ghost', 's-spiritual']);
    expect(store.hasMore()).toBe(true);
    expect(store.loading()).toBe(false);
    expect(store.loadingMore()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('LoadMore_Appends_AndStops', async () => {
    const { store, repo } = setup();
    await store.load();
    await store.loadMore();
    expect(repo.listCalls.map((call) => call.cursor)).toEqual([null, 'p2']);
    expect(ids(store)).toEqual(['s-ghost', 's-spiritual', 's-true-crime']);
    expect(store.hasMore()).toBe(false);
    await store.loadMore();
    expect(repo.listCalls.length).toBe(2);
  });

  it('LoadMore_SingleFlight', async () => {
    const { store, repo } = setup();
    repo.defer();
    const first = store.load();
    void store.loadMore();
    void store.loadMore();
    expect(repo.listCalls.length).toBe(1);
    expect(store.loading()).toBe(true);
    repo.resolveAll();
    await first;
    expect(repo.listCalls.length).toBe(1);
    expect(ids(store)).toEqual(['s-ghost', 's-spiritual']);
    expect(store.loading()).toBe(false);
  });

  it('Archive_Optimistic_AndRollback', async () => {
    const { store, repo, announcer } = setup();
    await store.load();
    repo.failNext(appError('internal_error', 500, 'server'));
    repo.defer();
    const archiving = store.archive('s-ghost');
    expect(store.archivingId()).toBe('s-ghost');
    expect(ids(store)).toEqual(['s-spiritual']);
    repo.resolveNext();
    expect(await archiving).toBe(false);
    expect(store.archivingId()).toBeNull();
    expect(ids(store)).toEqual(['s-ghost', 's-spiritual']);
    expect(repo.stored('s-ghost')?.archivedAt).toBeNull();
    await flush();
    expect(announcer.messages).toEqual(['Không lưu trữ được series']);

    repo.defer(false);
    expect(await store.archive('s-ghost')).toBe(true);
    expect(ids(store)).toEqual(['s-spiritual']);
    expect(store.archivingId()).toBeNull();
    expect(repo.stored('s-ghost')?.archivedAt).not.toBeNull();
  });

  it('Archive_AlreadyGone_IsIdempotentSuccess', async () => {
    const { store, repo, announcer } = setup();
    await store.load();
    repo.failNext(appError('series_archived', 409, 'precondition'));
    expect(await store.archive('s-ghost')).toBe(true);
    expect(ids(store)).toEqual(['s-spiritual']);
    await flush();
    expect(announcer.messages).toEqual([]);

    repo.failNext(appError('not_found', 404, 'not_found'));
    expect(await store.archive('s-spiritual')).toBe(true);
    expect(ids(store)).toEqual([]);
  });

  it('IncludeArchived_TogglesQuery', async () => {
    const { store, repo } = setup();
    await store.load();
    await store.toggleIncludeArchived();
    expect(store.includeArchived()).toBe(true);
    expect(repo.listCalls[1]).toEqual({ cursor: null, limit: SERIES_LIST_PAGE_SIZE, includeArchived: true });
    await store.loadMore();
    expect(ids(store)).toEqual(['s-ghost', 's-spiritual', 's-true-crime', 's-archived']);
    expect(store.hasMore()).toBe(false);
    await store.toggleIncludeArchived();
    expect(store.includeArchived()).toBe(false);
    // The toggle reloads the first page. Index 2 is the `loadMore` page above, so the new first page is the fourth
    // call and the only request with the cleared filter.
    expect(repo.listCalls).toHaveLength(4);
    expect(repo.listCalls[3]).toEqual({ cursor: null, limit: SERIES_LIST_PAGE_SIZE, includeArchived: false });
  });

  it('Load_ValidationFailed_MapsFieldError', async () => {
    const { store, repo } = setup();
    repo.failNext(appError('validation_failed', 400, 'validation', { fieldErrors: { limit: ['errors.limit'] } }));
    await store.load();
    expect(store.fieldError()).toBe('errors.validation_failed');
    expect(store.error()?.code).toBe('validation_failed');
    expect(repo.listCalls.length).toBe(1);
    expect(store.loading()).toBe(false);
    expect(ids(store)).toEqual([]);
  });

  it('Load_ValidationFailed_RetryDropsCursorAndReloadsFirstPage', async () => {
    const { store, repo } = setup();
    await store.load();
    repo.failNext(appError('validation_failed', 400, 'validation'));
    await store.loadMore();
    expect(repo.listCalls[1]?.cursor).toBe('p2');
    await store.retry();
    expect(repo.listCalls[2]).toEqual({ cursor: null, limit: SERIES_LIST_PAGE_SIZE, includeArchived: false });
    expect(ids(store)).toEqual(['s-ghost', 's-spiritual']);
    expect(store.fieldError()).toBeNull();
    expect(store.error()).toBeNull();
    expect(store.hasMore()).toBe(true);
  });

  it('Load_NetworkError_SetsError', async () => {
    const { store, repo } = setup();
    repo.failNext(appError('network_error', 0, 'network'));
    await store.load();
    expect(store.error()?.code).toBe('network_error');
    expect(ids(store)).toEqual([]);
    expect(store.loading()).toBe(false);
    expect(store.hasMore()).toBe(true);
    expect(store.retryAfterSec()).toBeNull();
  });

  it('Load_RateLimited_ExposesRetryAfterSec', async () => {
    const { store, repo } = setup();
    repo.failNext(appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }));
    await store.load();
    expect(store.error()?.code).toBe('rate_limited');
    expect(store.retryAfterSec()).toBe(30);
  });

  it('Load_LateResponseAfterSessionChange_IsDiscarded', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const loading = store.load();
    session.reset();
    repo.resolveAll();
    await loading;
    expect(ids(store)).toEqual([]);
    expect(store.error()).toBeNull();
  });

  it('Archive_SecondCallWhileOneIsPending_IsRefused', async () => {
    const { store, repo } = setup();
    await store.load();
    repo.defer();
    const first = store.archive('s-ghost');
    expect(await store.archive('s-spiritual')).toBe(false);
    expect(repo.archiveCalls).toEqual(['s-ghost']);
    expect(ids(store)).toEqual(['s-spiritual']);
    repo.resolveAll();
    expect(await first).toBe(true);
    expect(ids(store)).toEqual(['s-spiritual']);
  });
});
