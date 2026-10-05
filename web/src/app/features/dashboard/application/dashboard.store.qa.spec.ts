import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { EpisodesRepository, type EpisodeListItem, type EpisodeStatus } from '@features/episodes';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { DASHBOARD_STATUSES } from '../dashboard.model';
import { FakeDashboardEpisodesRepository, dashboardEpisode } from '../testing/fake-episode.repository';
import { DashboardStore } from './dashboard.store';

// QA (10.4.1-T1): adversarial store paths the shipped spec does not cover — an unknown wire status that must be
// ignored and never counted, a drift to a status without a group that must drop the row, a drift that crosses
// groups, a second load after a failure, a load racing an in-flight refresh, late results/rejections after a
// session reset (logout), and refresh requests that still carry exactly the five scalar statuses.

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

/** Cans every dashboard status with `items`; every item carries the requested status when none is given. */
function canAll(
  repo: FakeDashboardEpisodesRepository,
  items: readonly EpisodeListItem[] = [],
): FakeDashboardEpisodesRepository {
  for (const status of DASHBOARD_STATUSES) repo.withStatus(status, items);
  return repo;
}

describe('DashboardStore_QA_UnknownStatus', () => {
  it('Load_UnknownWireStatus_IsIgnoredAndNotCounted', async () => {
    const { store, repo } = setup();
    // `story_approved` is a real enum member with no group; `mystery_status` is a value the wire may one day send.
    repo.withStatus('story_review', [
      dashboardEpisode(1, { status: 'story_review' }),
      dashboardEpisode(8, { status: 'story_approved' }),
      dashboardEpisode(9, { status: 'mystery_status' as EpisodeStatus }),
    ]);
    await store.load();
    expect(store.counts()).toEqual({ needsReview: 1, inProgress: 0, ready: 0 });
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
    expect(store.needsReview()[0]?.episode.status).toBe('story_review');
  });

  it('Load_StatusDrift_ToStatusWithoutGroup_DropsTheRow', async () => {
    const { store, repo } = setup();
    // The episode drifted to `published` between the two calls: the most advanced status wins and has no group,
    // so the row disappears from the overview entirely rather than staying under needsReview.
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    repo.withStatus('video_review', [dashboardEpisode(1, { status: 'published' })]);
    await store.load();
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 0 });
    expect(store.needsReview()).toEqual([]);
    expect(store.inProgress()).toEqual([]);
    expect(store.ready()).toEqual([]);
  });

  it('Load_StatusDrift_AcrossGroups_PlacesRowUnderTheAdvancedGroup', async () => {
    const { store, repo } = setup();
    // `ready` is more advanced than `video_review`/`story_review`: the row moves to the ready group.
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    repo.withStatus('video_review', [dashboardEpisode(1, { status: 'video_review' })]);
    repo.withStatus('ready', [dashboardEpisode(1, { status: 'ready' })]);
    await store.load();
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 1 });
    expect(store.ready()[0]?.episode.id).toBe('dep-1');
    expect(store.ready()[0]?.episode.status).toBe('ready');
    expect(store.ready()[0]?.primaryAction).toBe('open');
  });
});

describe('DashboardStore_QA_LoadEdges', () => {
  it('Load_AfterFailure_RetrySucceeds_AndClearsTheError', async () => {
    const { store, repo } = setup();
    repo.failStatus('ready', appError('internal_error', 500, 'server'));
    await store.load();
    expect(store.error()).not.toBeNull();
    expect(store.hasLoaded()).toBe(false);
    canAll(repo, [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    expect(store.error()).toBeNull();
    expect(store.hasLoaded()).toBe(true);
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
  });

  it('Load_AfterSuccess_Failure_ClearsEveryGroup', async () => {
    // A Retry (`load()`) after rows are on screen: the contract keeps nothing stale — all groups empty.
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    expect(store.needsReview()).toHaveLength(1);
    repo.failStatus('ready', appError('internal_error', 500, 'server'));
    await store.load();
    expect(store.error()).toMatchObject({ code: 'internal_error', status: 500 });
    expect(store.needsReview()).toEqual([]);
    expect(store.inProgress()).toEqual([]);
    expect(store.ready()).toEqual([]);
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 0 });
    // `hasLoaded` stays true: a load DID succeed once; the flag is not a data-presence flag.
    expect(store.hasLoaded()).toBe(true);
  });

  it('Load_WhileRefreshInFlight_SupersedesTheRefreshResult', async () => {
    // A `load()` started while a `refresh()` is in flight: the newer run wins, the refresh's data is dropped.
    const { store, repo } = setup();
    canAll(repo, [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    expect(store.needsReview()).toHaveLength(1);

    repo.defer();
    const refreshing = store.refresh();
    expect(store.refreshing()).toBe(true);
    // The load runs live and commits dep-3 while the refresh batch is still pending. Other statuses must be
    // re-canned empty: they still hold the dep-1 fixture from the first load.
    repo.defer(false);
    canAll(repo, []);
    repo.withStatus('story_review', [dashboardEpisode(3, { status: 'story_review' })]);
    await store.load();
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-3']);
    // Now the refresh answers dep-2: a superseded run must drop it, even though it resolves last.
    canAll(repo, []);
    repo.withStatus('story_review', [dashboardEpisode(2, { status: 'story_review' })]);
    repo.resolveAll();
    await refreshing;
    await settle();
    expect(store.loading()).toBe(false);
    expect(store.refreshing()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.refreshError()).toBeNull();
  });
});

describe('DashboardStore_QA_SessionEnds', () => {
  it('LateResult_AfterSessionReset_IsIgnored', async () => {
    // `reset()` bumps the generation without navigating (the "session replaced" half of a logout + sign-in).
    const { store, repo, session } = setup();
    repo.defer();
    const loading = store.load();
    expect(store.loading()).toBe(true);
    session.reset();
    repo.resolveAll();
    await loading;
    await settle();
    expect(store.needsReview()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(store.hasLoaded()).toBe(false);
    expect(store.loading()).toBe(false);
  });

  it('LateRejection_AfterSessionReset_PaintsNoError', async () => {
    // Task failure row "session ends while loading": no error state is painted for the dropped run.
    const { store, repo, session } = setup();
    repo.defer();
    const loading = store.load();
    session.reset();
    repo.rejectAll(appError('internal_error', 500, 'server'));
    await loading;
    await settle();
    expect(store.error()).toBeNull();
    expect(store.refreshError()).toBeNull();
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 0 });
    expect(store.loading()).toBe(false);
  });

  it('LateRejection_AfterDestroy_IsIgnored', async () => {
    const { store, repo } = setup();
    repo.defer();
    const loading = store.load();
    TestBed.resetTestingModule();
    repo.rejectAll(appError('internal_error', 500, 'server'));
    await loading;
    await settle();
    expect(store.error()).toBeNull();
    // By design the destroyed store never clears `loadingState`: its signals are unreachable after teardown,
    // so the stale flag costs nothing and keeps the finally guard simple (`!destroyed && owned`).
    expect(store.loading()).toBe(true);
  });

  it('Refresh_LateResult_AfterSignIn_IsIgnored', async () => {
    // A refresh whose result lands after a new sign-in is stale: nothing of the new page commits. The old rows
    // stay only because the unit never navigates — in the app the page (and this store) dies with the route.
    const { store, repo, session } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    repo.defer();
    repo.withStatus('story_review', [dashboardEpisode(7, { status: 'story_review' })]);
    const refreshing = store.refresh();
    session.signIn(
      { accessToken: fakeJwt('user', ['pwd'], 'other-user'), expiresAt: Date.now() + 900_000 },
      'user',
      ['pwd'],
    );
    repo.resolveAll();
    await refreshing;
    await settle();
    // Generation changed: the dep-7 refresh page is dropped, the dep-1 rows of the old generation survive.
    expect(store.needsReview().map((row) => row.episode.id)).toEqual(['dep-1']);
    expect(store.refreshError()).toBeNull();
  });
});

describe('DashboardStore_QA_Refresh', () => {
  it('Refresh_WithoutPriorLoad_RunsAndPopulates', async () => {
    // The store's refresh() is coalesced but not gated on a previous load; the page never calls it that way.
    const { store, repo } = setup();
    repo.withStatus('ready', [dashboardEpisode(5, { status: 'ready' })]);
    await store.refresh();
    expect(repo.calls).toHaveLength(DASHBOARD_STATUSES.length);
    expect(store.ready().map((row) => row.episode.id)).toEqual(['dep-5']);
    expect(store.hasLoaded()).toBe(true);
    expect(store.refreshError()).toBeNull();
  });

  it('Refresh_LateRejection_AfterNewerRun_IsDropped', async () => {
    const { store, repo } = setup();
    repo.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    await store.load();
    repo.defer();
    const refreshing = store.refresh();
    // The newer load fails live: it owns the page-level error and clears the rows.
    repo.defer(false);
    repo.failStatus('ready', appError('internal_error', 500, 'server'));
    await store.load();
    expect(store.error()).toMatchObject({ code: 'internal_error' });
    expect(store.counts()).toEqual({ needsReview: 0, inProgress: 0, ready: 0 });
    // The superseded refresh's rejection must not surface as a refresh error afterwards.
    repo.rejectAll(new HttpErrorResponse({ status: 503 }));
    await refreshing;
    await settle();
    expect(store.refreshError()).toBeNull();
    expect(store.error()).toMatchObject({ code: 'internal_error' });
  });
});
