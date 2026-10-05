import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import type { AppError } from '@core/errors';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { CreatedEpisode, EpisodeDetail } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { NewEpisodeStore } from './new-episode.store';

const SERIES_ID = '11111111-1111-4111-8111-111111111111';
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
function rejected<T>(code: string, status: number): Promise<T> {
  const error: AppError = { code, status, kind: status === 0 ? 'network' : 'server', fieldErrors: {} };
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the domain port rejects structured AppError values
  return Promise.reject(error);
}
function setup() {
  const log: string[] = [];
  const repo = new FakeEpisodesRepository(log);
  const series = new FakeSeriesOptionsRepository();
  const realtime = new FakeEpisodeRealtime(log);
  const generation = signal(0);
  const navigate = vi.fn().mockResolvedValue(true);
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS,
    { provide: EpisodesRepository, useValue: repo },
    { provide: SeriesOptionsRepository, useValue: series },
    { provide: RealtimeService, useValue: realtime },
    { provide: SessionStore, useValue: { generation } },
    { provide: Router, useValue: { navigate } },
    NewEpisodeStore,
  ] });
  const store = TestBed.inject(NewEpisodeStore);
  store.form.controls.brief.setValue('A visitor arrives after midnight.');
  store.init(SERIES_ID);
  return { store, repo, realtime, generation, navigate, log };
}

afterEach(() => TestBed.resetTestingModule());

describe('NewEpisodeStore independent QA', () => {
  it('keeps a deferred create single-flight through repeated click/Enter intents and subscribes before generate', async () => {
    const { store, repo, log } = setup();
    const pending = deferred<CreatedEpisode>();
    repo.scriptCreate(() => pending.promise);
    await settle();
    const first = store.create();
    await Promise.all(Array.from({ length: 8 }, () => store.create()));
    expect(store.submitting()).toBe(true);
    expect(store.canSubmit()).toBe(false);
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(0);
    pending.resolve({ id: EPISODE_ID, etag: '"created"' });
    await first;
    expect(log).toEqual(['create', 'subscribe', 'generate', 'get']);
    expect(repo.generateCalls[0]?.key).toMatch(/^[0-9a-f-]{36}$/i);
    await store.create();
    expect(repo.createCalls).toHaveLength(1);
  });

  it('retains the exact key across multiple unknown outcomes then rotates after a terminal run failure', async () => {
    const { store, repo, realtime } = setup();
    repo.scriptGenerate(() => rejected('network_error', 0));
    repo.scriptGenerate(() => rejected('unknown_error', 503));
    await settle();
    await store.create();
    const key = repo.generateCalls[0]?.key;
    await store.retry();
    await store.retry();
    expect(repo.generateCalls.map((call) => call.key)).toEqual([key, key, key]);
    repo.scriptGet(() => Promise.resolve(episodeDetail({ generation: {
      kind: 'story', status: 'failed', stage: 'done', errorCode: 'story_invalid_output', startedAt: null, processingPaused: null,
    } })));
    realtime.emit(EPISODE_ID);
    await settle();
    expect(store.canRetry()).toBe(true);
    await store.retry();
    expect(repo.generateCalls[3]?.key).not.toBe(key);
    expect(repo.createCalls).toHaveLength(1);
  });

  it.each(['destroy', 'logout'] as const)('late successful refetch after %s changes neither detail nor generation nor navigation', async (reason) => {
    const { store, repo, realtime, generation, navigate } = setup();
    await settle();
    await store.create();
    const oldDetail = store.detail();
    const oldRun = store.generation();
    const pending = deferred<EpisodeDetail>();
    repo.scriptGet(() => pending.promise);
    const handler = realtime.subscriptions[0]?.handler;
    handler?.();
    if (reason === 'destroy') TestBed.resetTestingModule();
    else generation.update((value) => value + 1);
    pending.resolve(episodeDetail({ status: 'story_review', hasCurrentStory: true }));
    await settle();
    handler?.();
    await settle();
    expect(store.detail()).toBe(oldDetail);
    expect(store.generation()).toBe(oldRun);
    expect(store.errorKey()).toBeNull();
    expect(navigate).not.toHaveBeenCalled();
    expect(repo.getCalls).toHaveLength(2);
  });

  it('rejects whitespace and one-past-limit inputs without either write request', async () => {
    const { store, repo } = setup();
    await settle();
    for (const brief of [' \t\n ', 'x'.repeat(1001)]) {
      store.form.controls.brief.setValue(brief);
      await store.create();
      expect(store.canSubmit()).toBe(false);
    }
    store.form.controls.brief.setValue('x'.repeat(1000));
    store.form.controls.title.setValue('t'.repeat(121));
    await store.create();
    expect(repo.createCalls).toHaveLength(0);
    expect(repo.generateCalls).toHaveLength(0);
    store.form.controls.title.setValue('t'.repeat(120));
    await store.create();
    expect(repo.createCalls[0]?.input.brief).toHaveLength(1000);
    expect(repo.createCalls[0]?.input.title).toHaveLength(120);
  });

  it('does not replay a 413 even under repeated retry intents and retains the created episode', async () => {
    const { store, repo } = setup();
    repo.scriptGenerate(() => rejected('payload_too_large', 413));
    await settle();
    await store.create();
    await Promise.all([store.retry(), store.retry(), store.create()]);
    expect(store.errorKey()).toBe('errors.payload_too_large');
    expect(store.episodeId()).toBe(EPISODE_ID);
    expect(repo.createCalls).toHaveLength(1);
    expect(repo.generateCalls).toHaveLength(1);
    expect(store.canRetry()).toBe(false);
  });

  it('recovers an unknown generate outcome through an invalidation without keeping a stale error alert', async () => {
    const { store, repo, realtime } = setup();
    repo.scriptGenerate(() => rejected('network_error', 0));
    await settle();
    await store.create();
    expect(store.errorKey()).toBe('errors.network_error');
    realtime.emit(EPISODE_ID);
    await settle();
    expect(store.phase()).toBe('generating');
    expect(store.stageKey()).toBe('episodes.generation.stage.queued');
    expect(store.errorKey()).toBeNull();
    expect(store.canRetry()).toBe(false);
    expect(repo.generateCalls).toHaveLength(1);
  });
});
