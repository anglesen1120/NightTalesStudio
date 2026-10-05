import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { EpisodeDetail } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { EpisodeDetailStore } from './episode-detail.store';

function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
function setup() {
  const repo = new FakeEpisodesRepository(), generation = signal(0);
  TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS, EpisodeDetailStore, { provide: EpisodesRepository, useValue: repo }, { provide: SessionStore, useValue: { generation } }] });
  return { store: TestBed.inject(EpisodeDetailStore), repo, generation };
}
describe('EpisodeDetailStore', () => {
  afterEach(() => TestBed.resetTestingModule());
  it('load_IsUserActionAndExposesEpisodeActions', async () => {
    const { store, repo } = setup(); repo.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'] })));
    await store.load('id'); expect(store.episode()?.id).toBe('22222222-2222-4222-8222-222222222222'); expect(store.detail()).toBe(store.episode()); expect(store.nextActions()).toEqual(['edit_story']); expect(repo.getCalls[0]?.userAction).toBe(true);
  });
  it('refetch_DefaultIsBackgroundAndExplicitReloadIsUserAction', async () => {
    const { store, repo } = setup(); await store.load('id'); await store.refetch(); await store.refetch({ userAction: true }); expect(repo.getCalls.map((call) => call.userAction)).toEqual([true, false, true]);
  });
  it('load_LatePreviousRequest_IsDiscarded', async () => {
    const { store, repo } = setup(); const old = deferred<EpisodeDetail>(); repo.scriptGet(() => old.promise); const first = store.load('old'); await store.load('new'); old.resolve(episodeDetail({ id: 'old' })); await first; expect(store.episode()?.id).toBe('new');
  });
  it('load_TypedError_IsExposedWithoutFabricatingDetail', async () => {
    const { store, repo } = setup(); const error: AppError = { code: 'not_found', kind: 'not_found', status: 404, fieldErrors: {} };
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- domain-port AppError
    repo.scriptGet(() => Promise.reject(error)); await store.load('id'); expect(store.episode()).toBeNull(); expect(store.error()).toBe(error); expect(store.loading()).toBe(false);
  });
  it('logout_ClearsDetailAndFencesLateRequests', async () => {
    const { store, repo, generation } = setup(); await store.load('id'); const late = deferred<EpisodeDetail>(); repo.scriptGet(() => late.promise); const read = store.refetch(); generation.update((value) => value + 1); TestBed.tick(); late.resolve(episodeDetail()); await read; expect(store.episode()).toBeNull();
  });
});
