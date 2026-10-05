import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodesRepository, episodeItem } from '../testing/fake-episodes.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { EpisodeListStore } from './episode-list.store';

const SERIES_ID = '22222222-2222-4222-8222-222222222222';

const NO_FILTERS = { status: null, seriesId: null } as const;

function appError(code: string, status: number, kind: AppError['kind'], fieldErrors: AppError['fieldErrors'] = {}): AppError {
  return { code, status, kind, fieldErrors };
}

/** Lets every already-resolved promise chain (and the store's microtask debounce) run. */
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

function setup(): { store: EpisodeListStore; repo: FakeEpisodesRepository; series: FakeSeriesOptionsRepository; session: SessionStore } {
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

describe('EpisodeListStore', () => {
  it('load_FirstPage_SetsItemsAndCursor', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1), episodeItem(2)], nextCursor: 'c1' });
    store.init(NO_FILTERS);
    expect(store.loading()).toBe(true);
    await settle();
    expect(store.items().map((i) => i.id)).toEqual(['ep-1', 'ep-2']);
    expect(store.hasMore()).toBe(true);
    expect(store.loading()).toBe(false);
    expect(repo.calls).toEqual([{ limit: 20 }]);
  });

  it('load_More_AppendsWithoutDuplicates', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1), episodeItem(2)], nextCursor: 'c1' });
    repo.willReturn({ items: [episodeItem(3), episodeItem(4)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();
    await store.load('more');
    const ids = store.items().map((i) => i.id);
    expect(ids).toEqual(['ep-1', 'ep-2', 'ep-3', 'ep-4']);
    expect(new Set(ids).size).toBe(ids.length);
    expect(repo.calls[1]).toEqual({ cursor: 'c1', limit: 20 });
    expect(store.hasMore()).toBe(false);
  });

  it('loadMore_WhileInFlight_IssuesOneRequest', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'c1' });
    store.init(NO_FILTERS);
    await settle();
    repo.defer();
    const first = store.load('more');
    const second = store.load('more');
    expect(store.loadingMore()).toBe(true);
    expect(store.loading()).toBe(false);
    expect(repo.calls).toHaveLength(2);
    repo.resolveNext({ items: [episodeItem(2)], nextCursor: null });
    await Promise.all([first, second]);
    expect(store.items()).toHaveLength(2);
  });

  it('setStatus_ResetsCursorAndItems', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'c1' });
    repo.willReturn({ items: [episodeItem(9, { status: 'failed' })], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();
    store.setStatus('failed');
    await settle();
    expect(repo.calls[1]).toEqual({ status: 'failed', limit: 20 });
    expect(store.items().map((i) => i.id)).toEqual(['ep-9']);
    expect(store.hasMore()).toBe(false);
    expect(store.statusFilter()).toBe('failed');
  });

  it('setSeries_ResetsCursorAndItems', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'c1' });
    repo.willReturn({ items: [episodeItem(7)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();
    store.setSeries(SERIES_ID);
    await settle();
    expect(repo.calls[1]).toEqual({ seriesId: SERIES_ID, limit: 20 });
    expect(store.items().map((i) => i.id)).toEqual(['ep-7']);
    expect(store.seriesFilter()).toBe(SERIES_ID);
  });

  it('error_400Cursor_SetsTerminalErrorAndStopsPaging', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'bad' });
    repo.willFail(appError('validation_failed', 400, 'validation', { cursor: ['invalid'] }));
    store.init(NO_FILTERS);
    await settle();
    await store.load('more');
    expect(store.error()?.fieldErrors['cursor']).toBeDefined();
    expect(store.hasMore()).toBe(false);
    expect(store.items()).toHaveLength(1);
    await store.load('more');
    expect(repo.calls).toHaveLength(2);
  });

  it('error_Network_MapsToNetworkError', async () => {
    const { store, repo } = setup();
    repo.willFail(new HttpErrorResponse({ status: 0 }));
    store.init(NO_FILTERS);
    await settle();
    expect(store.error()).toMatchObject({ code: 'network_error', kind: 'network', status: 0 });
    expect(store.loading()).toBe(false);
    expect(store.isEmpty()).toBe(false);
  });

  it('retry_AfterError_IssuesExactlyOneRequest', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'c1' });
    repo.willFail(new HttpErrorResponse({ status: 503 }));
    repo.willReturn({ items: [episodeItem(2)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();
    await store.load('more');
    expect(store.error()).not.toBeNull();
    await store.retry();
    expect(repo.calls).toHaveLength(3);
    expect(repo.calls[2]).toEqual({ cursor: 'c1', limit: 20 });
    expect(store.error()).toBeNull();
    expect(store.items().map((i) => i.id)).toEqual(['ep-1', 'ep-2']);
  });

  it('retry_After400Cursor_RestartsFromPageOneWithOneRequest', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [episodeItem(1)], nextCursor: 'bad' });
    repo.willFail(appError('validation_failed', 400, 'validation', { cursor: ['invalid'] }));
    repo.willReturn({ items: [episodeItem(5)], nextCursor: null });
    store.init({ status: 'ready', seriesId: null });
    await settle();
    await store.load('more');
    await store.retry();
    expect(repo.calls).toHaveLength(3);
    expect(repo.calls[2]).toEqual({ status: 'ready', limit: 20 });
    expect(store.error()).toBeNull();
    expect(store.items().map((i) => i.id)).toEqual(['ep-5']);
  });

  it('isEmpty_OnlyWhenLoadedAndEmpty', async () => {
    const { store, repo } = setup();
    expect(store.isEmpty()).toBe(false);
    repo.defer();
    store.init(NO_FILTERS);
    expect(store.isEmpty()).toBe(false);
    repo.resolveNext({ items: [], nextCursor: null });
    await settle();
    expect(store.isEmpty()).toBe(true);
    expect(store.hasMore()).toBe(false);
  });

  it('isEmpty_EmptyPageWithCursor_KeepsHasMoreAndIsNotEmpty', async () => {
    const { store, repo } = setup();
    repo.willReturn({ items: [], nextCursor: 'more' });
    store.init(NO_FILTERS);
    await settle();
    expect(store.isEmpty()).toBe(false);
    expect(store.hasMore()).toBe(true);
  });

  it('track_StableIds_SurviveAppend', async () => {
    const { store, repo } = setup();
    const first = episodeItem(1);
    repo.willReturn({ items: [first], nextCursor: 'c1' });
    repo.willReturn({ items: [episodeItem(2)], nextCursor: null });
    store.init(NO_FILTERS);
    await settle();
    await store.load('more');
    expect(store.items()[0]).toBe(first);
    expect(store.items().map((i) => i.id)).toEqual(['ep-1', 'ep-2']);
  });

  describe('staleness and coalescing', () => {
    it('setters_InTheSameTick_IssueOneRequestWithLastState', async () => {
      const { store, repo } = setup();
      store.init(NO_FILTERS);
      await settle();
      store.setStatus('draft');
      store.setSeries(SERIES_ID);
      store.setStatus('ready');
      await settle();
      expect(repo.calls).toHaveLength(2);
      expect(repo.calls[1]).toEqual({ status: 'ready', seriesId: SERIES_ID, limit: 20 });
    });

    it('setters_BackToLoadedState_IssueNoRequest', async () => {
      const { store, repo } = setup();
      store.init(NO_FILTERS);
      await settle();
      store.setStatus('draft');
      store.setStatus(null);
      await settle();
      expect(repo.calls).toHaveLength(1);
    });

    it('setFilters_ParamOnlyChangeOnReusedPage_OneRequestAndPreviousResponseDiscarded', async () => {
      const { store, repo } = setup();
      repo.defer();
      store.init({ status: 'draft', seriesId: null });
      store.setFilters({ status: 'ready', seriesId: null });
      await settle();
      expect(repo.calls).toHaveLength(2);
      expect(repo.calls[1]).toEqual({ status: 'ready', limit: 20 });
      repo.resolveNext({ items: [episodeItem(2, { status: 'ready' })], nextCursor: null }, 1);
      await settle();
      repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'old' }, 0);
      await settle();
      expect(store.items().map((i) => i.id)).toEqual(['ep-2']);
      expect(store.hasMore()).toBe(false);
      expect(store.statusFilter()).toBe('ready');
    });

    it('setFilters_WithAppliedFilters_IssuesNoRequest', async () => {
      const { store, repo } = setup();
      store.init({ status: 'ready', seriesId: SERIES_ID });
      await settle();
      store.setFilters({ status: 'ready', seriesId: SERIES_ID });
      await settle();
      expect(repo.calls).toHaveLength(1);
    });

    it('destroy_DuringInFlight_DiscardsTheResponse', async () => {
      const { store, repo } = setup();
      repo.defer();
      store.init(NO_FILTERS);
      TestBed.resetTestingModule();
      repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'c1' });
      await settle();
      expect(store.items()).toEqual([]);
      expect(store.hasMore()).toBe(false);
    });

    it('destroy_DropsPendingMicrotaskRestart', async () => {
      const { store, repo } = setup();
      store.init(NO_FILTERS);
      await settle();
      store.setStatus('failed');
      TestBed.resetTestingModule();
      await settle();
      expect(repo.calls).toHaveLength(1);
    });

    it('sessionChange_BeforeResponse_DiscardsTheResponse', async () => {
      const { store, repo, session } = setup();
      repo.defer();
      store.init(NO_FILTERS);
      session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
      repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'c1' });
      await settle();
      expect(store.items()).toEqual([]);
      expect(store.hasMore()).toBe(false);
      expect(store.error()).toBeNull();
    });

    it('sessionChangeWhileALaterPageIsInFlight_RetiresWithNoCursor', async () => {
      const { store, repo, session } = setup();
      repo.defer();
      store.init(NO_FILTERS);
      repo.resolveNext({ items: [episodeItem(1)], nextCursor: 'c1' });
      await settle();
      expect(store.hasMore()).toBe(true);

      void store.load('more');
      session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
      repo.resolveNext({ items: [episodeItem(9)], nextCursor: 'c9' });
      await settle();

      expect(store.items().map((i) => i.id)).toEqual(['ep-1']);
      // The retired snapshot must not keep the previous cursor: that would leave a dead "Load more".
      expect(store.hasMore()).toBe(false);
      expect(store.error()).toBeNull();
    });
  });

  describe('series options', () => {
    it('init_LoadsSeriesOptionsOnce_AndFilterChangesDoNotReload', async () => {
      const { store, repo, series } = setup();
      series.page = { items: [{ id: SERIES_ID, title: 'True crime' }], bounded: true };
      store.init(NO_FILTERS);
      expect(store.seriesOptionsState()).toBe('loading');
      await settle();
      store.setStatus('draft');
      await settle();
      expect(series.calls).toBe(1);
      expect(repo.calls).toHaveLength(2);
      expect(store.seriesOptionsState()).toBe('ready');
      expect(store.seriesOptions()).toEqual([{ id: SERIES_ID, title: 'True crime' }]);
      expect(store.seriesOptionsBounded()).toBe(true);
    });

    it('seriesFailure_MarksUnavailable_AndTheListKeepsWorking', async () => {
      const { store, repo, series } = setup();
      series.error = new HttpErrorResponse({ status: 500 });
      repo.willReturn({ items: [episodeItem(1)], nextCursor: null });
      store.init(NO_FILTERS);
      await settle();
      expect(store.seriesOptionsState()).toBe('unavailable');
      expect(store.seriesOptions()).toEqual([]);
      expect(store.items()).toHaveLength(1);
      expect(store.error()).toBeNull();
    });

    it('seriesResponse_AfterDestroy_IsDiscarded', async () => {
      const { store, series } = setup();
      series.defer();
      store.init(NO_FILTERS);
      TestBed.resetTestingModule();
      series.resolve({ items: [{ id: SERIES_ID, title: 'Late' }], bounded: false });
      await settle();
      expect(store.seriesOptions()).toEqual([]);
      expect(store.seriesOptionsState()).toBe('loading');
    });

    it('seriesResponse_AfterSessionChange_IsDiscarded', async () => {
      const { store, series, session } = setup();
      series.defer();
      store.init(NO_FILTERS);
      session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
      series.resolve({ items: [{ id: SERIES_ID, title: 'Late' }], bounded: false });
      await settle();
      expect(store.seriesOptions()).toEqual([]);
    });
  });
});
