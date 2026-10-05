import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { EpisodesRepository } from '@features/episodes';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { DASHBOARD_PAGE_SIZE, DASHBOARD_STATUSES } from '../dashboard.model';
import { FakeDashboardEpisodesRepository, dashboardEpisode } from '../testing/fake-episode.repository';
import { DashboardStore } from './dashboard.store';

/** A mapped error the fake can reject with, so the store's `isAppError` guard passes it through unchanged. */
function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

/** Lets every already-resolved promise chain (and the store's microtasks) run. */
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

function setup(): { store: DashboardStore; repo: FakeDashboardEpisodesRepository; session: SessionStore } {
  const repo = new FakeDashboardEpisodesRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: EpisodesRepository, useValue: repo },
      DashboardStore,
    ],
  });
  return { store: TestBed.inject(DashboardStore), repo, session: TestBed.inject(SessionStore) };
}

describe('DashboardStore_Load', () => {
  it('Load_IssuesExactlyFiveScalarStatusRequests', async () => {
    const { store, repo } = setup();
    const loading = store.load();
    expect(store.loading()).toBe(true);
    expect(repo.calls).toEqual([
      { status: 'story_review', limit: DASHBOARD_PAGE_SIZE },
      { status: 'video_review', limit: DASHBOARD_PAGE_SIZE },
      { status: 'story_generating', limit: DASHBOARD_PAGE_SIZE },
      { status: 'rendering', limit: DASHBOARD_PAGE_SIZE },
      { status: 'ready', limit: DASHBOARD_PAGE_SIZE },
    ]);
    for (const call of repo.calls) expect(Object.keys(call).sort()).toEqual(['limit', 'status']);
    await loading;
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
    expect(store.loading()).toBe(false);
    expect(store.hasLoaded()).toBe(true);
    expect(typeof store.lastRefreshedAt()).toBe('number');
  });

  it('Load_MapsStatusesToGroups', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    repo.withStatus('video_review', [dashboardEpisode(2, { status: 'video_review' })]);
    repo.withStatus('story_generating', [dashboardEpisode(3, { status: 'story_generating' })]);
    repo.withStatus('rendering', [dashboardEpisode(4, { status: 'rendering' })]);
    repo.withStatus('ready', [dashboardEpisode(5, { status: 'ready' })]);
    await store.load();
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1', 'dep-2']);
    expect(store.inProgress().map((row) => row.episode.id)).toEqual(['dep-3', 'dep-4']);
    expect(store.ready().map((row) => row.episode.id)).toEqual(['dep-5']);
    expect(store.counts()).toEqual({ needsReview: 2, inProgress: 2, ready: 1 });
    expect(store.needsReview()[0]?.primaryAction).toBe('reviewStory');
    expect(store.needsReview()[1]?.primaryAction).toBe('reviewVideo');
    expect(store.inProgress()[0]?.primaryAction).toBe('open');
    // No `youtube_publish` feature yet: a ready row opens the episode instead of publishing it.
    expect(store.ready()[0]?.primaryAction).toBe('open');
    expect(store.error()).toBeNull();
    expect(store.refreshError()).toBeNull();
  });

  it('Load_PublishAction_AppearsOnceTheFeatureIsKnown', async () => {
    const { store, repo, session } = setup();
    repo.withStatus('ready', [dashboardEpisode(5, { status: 'ready' })]);
    await store.load();
    expect(store.ready()[0]?.primaryAction).toBe('open');
    session.setMe({ id: 'user-1', uiLocale: 'vi', role: 'owner', features: ['youtube_publish'], displayName: 'An', email: 'an@example.test' });
    expect(store.ready()[0]?.primaryAction).toBe('publish');
  });

  it('Load_ParallelFailure_SetsError_AndKeepsNothing', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    repo.withStatus('ready', [dashboardEpisode(5, { status: 'ready' })]);
    repo.failStatus('rendering', appError('internal_error', 500, 'server'));
    await store.load();
    expect(store.error()).toMatchObject({ code: 'internal_error', kind: 'server', status: 500 });
    expect(store.needsReview()).toEqual([]);
    expect(store.inProgress()).toEqual([]);
    expect(store.ready()).toEqual([]);
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 0 });
    expect(store.hasLoaded()).toBe(false);
    expect(store.loading()).toBe(false);
    expect(store.refreshError()).toBeNull();
  });

  it('Load_RateLimited_SetsRetryAfterSec', async () => {
    const { store, repo } = setup();
    repo.failStatus('ready', new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '30' }) }));
    await store.load();
    expect(store.error()).toMatchObject({ kind: 'rate_limit', status: 429, retryAfterSec: 30 });
    expect(store.hasLoaded()).toBe(false);
  });

  it('Load_UnknownCode_SetsUnknownErrorWithTraceId', async () => {
    const { store, repo } = setup();
    repo.failStatus('story_generating', new HttpErrorResponse({ status: 500, error: { code: 'some_new_code', traceId: 'trace-42' } }));
    await store.load();
    expect(store.error()).toMatchObject({ code: 'unknown_error', status: 500, traceId: 'trace-42' });
  });

  it('Load_StatusDrift_KeepsOneRowUnderTheMostAdvancedStatus', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' }), dashboardEpisode(9, { status: 'draft' })]);
    repo.withStatus('video_review', [dashboardEpisode(1, { status: 'video_review' }), dashboardEpisode(8, { status: 'published' })]);
    await store.load();
    expect(store.counts()).toEqual({ needsReview: 1, inProgress: 0, ready: 0 });
    expect(store.needsReview()[0]?.episode.id).toBe('dep-1');
    expect(store.needsReview()[0]?.episode.status).toBe('video_review');
    expect(store.needsReview()[0]?.primaryAction).toBe('reviewVideo');
  });

  it('Load_WhileLoading_IsIgnored', async () => {
    const { store, repo } = setup();
    repo.defer();
    const first = store.load();
    const second = store.load();
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
    repo.resolveAll();
    await Promise.all([first, second]);
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
    expect(store.loading()).toBe(false);
  });

  it('Store_ExposesNoBalanceSignal', () => {
    const { store } = setup();
    const surface = [...Object.keys(store), ...Object.getOwnPropertyNames(Object.getPrototypeOf(store) as object)];
    expect(surface.filter((name) => /balance|credit|quote|purchase|buy|price|cost/i.test(name))).toEqual([]);
    // Non-vacuous: the group signals and the two entry points are on the same public surface.
    expect(surface).toContain('needsReview');
    expect(surface).toContain('counts');
    expect(typeof store.load).toBe('function');
  });
});

describe('DashboardStore_Refresh', () => {
  it('Refresh_Coalesced_WhileInFlight', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
    repo.defer();
    const first = store.refresh();
    const second = store.refresh();
    expect(store.refreshing()).toBe(true);
    expect(store.loading()).toBe(false);
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length * 2);
    expect(store.needsReview()).toHaveLength(1);
    repo.resolveAll();
    await Promise.all([first, second]);
    expect(store.refreshing()).toBe(false);
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length * 2);
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
  });

  it('Refresh_KeepsPreviousData_OnFailure', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    repo.failStatus('ready', appError('internal_error', 500, 'server'));
    await store.refresh();
    expect(store.refreshError()).toMatchObject({ code: 'internal_error', kind: 'server' });
    expect(store.error()).toBeNull();
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
    expect(store.refreshing()).toBe(false);
    expect(store.hasLoaded()).toBe(true);
  });

  it('Refresh_RateLimited_DiscardsFreshResultsAndKeepsTheRows', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    repo.withStatus('story_review', [dashboardEpisode(2, { status: 'story_review' })]);
    repo.failStatus('video_review', new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '10' }) }));
    await store.refresh();
    expect(store.refreshError()).toMatchObject({ kind: 'rate_limit', retryAfterSec: 10 });
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
    expect(store.error()).toBeNull();
  });

  it('Refresh_WhileLoading_IsIgnored', async () => {
    const { store, repo } = setup();
    repo.defer();
    const loading = store.load();
    const skipped = store.refresh();
    expect(store.refreshing()).toBe(false);
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
    repo.resolveAll();
    await Promise.all([loading, skipped]);
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
  });

  it('Refresh_ClearsAPreviousRefreshErrorOnSuccess', async () => {
    const { store, repo } = setup();
    repo.failStatus('ready', appError('internal_error', 500, 'server'));
    await store.load();
    expect(store.error()).not.toBeNull();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.refresh();
    expect(store.error()).toBeNull();
    expect(store.refreshError()).toBeNull();
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
  });
});

describe('DashboardStore_Staleness', () => {
  it('LateResult_AfterRouteOrSessionGenerationChange_IsIgnored', async () => {
    const { store, repo, session } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    repo.defer();
    const loading = store.load();
    expect(store.loading()).toBe(true);
    session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
    repo.resolveAll();
    await loading;
    await settle();
    expect(store.needsReview()).toEqual([]);
    expect(store.inProgress()).toEqual([]);
    expect(store.ready()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(store.hasLoaded()).toBe(false);
    expect(store.loading()).toBe(false);
  });

  it('LateRejection_AfterSessionGenerationChange_IsIgnored', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const loading = store.load();
    session.signIn({ accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 }, 'user', ['pwd']);
    repo.rejectAll(appError('internal_error', 500, 'server'));
    await loading;
    await settle();
    expect(store.error()).toBeNull();
    expect(store.refreshError()).toBeNull();
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 0 });
  });

  it('LateResult_AfterDestroy_IsIgnored', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    repo.defer();
    const loading = store.load();
    TestBed.resetTestingModule();
    repo.resolveAll();
    await loading;
    await settle();
    expect(store.needsReview()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(store.hasLoaded()).toBe(false);
  });

  it('isOwner_ReadsTheSessionRole', () => {
    const { store, session } = setup();
    expect(store.isOwner()).toBe(false);
    session.signIn({ accessToken: fakeJwt('owner'), expiresAt: Date.now() + 900_000 }, 'owner', ['pwd']);
    expect(store.isOwner()).toBe(true);
  });
});
