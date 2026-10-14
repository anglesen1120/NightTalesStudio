import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IdempotencyKeys } from '@core/http';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EpisodesRepository } from '../domain/episodes.repository';
import { ResearchRepository } from '../domain/research.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { episodeDetail, FakeEpisodesRepository } from '../testing/fake-episodes.repository';
import { FakeResearchRepository, fact, factSheet, source, sourceList } from '../testing/fake-research.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { ResearchStore } from './research.store';

const EPISODE_ID = '22222222-2222-4222-8222-222222222222';
const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const SHEET_ETAG = '"fs-1"';

function setup() {
  const repo = new FakeResearchRepository();
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ id: EPISODE_ID, seriesId: SERIES_ID, generation: null })));
  const series = new FakeSeriesOptionsRepository();
  series.summary = { ...series.summary, id: SERIES_ID, topic: 'true_crime' };
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, ResearchStore, IdempotencyKeys,
    { provide: ResearchRepository, useValue: repo },
    { provide: EpisodesRepository, useValue: episodes },
    { provide: SeriesOptionsRepository, useValue: series },
    { provide: RealtimeService, useValue: new FakeEpisodeRealtime() },
    { provide: SessionStore, useValue: { generation: signal(0) } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  return { store: TestBed.inject(ResearchStore), repo };
}

describe('ResearchStore approve in-flight state (QA)', () => {
  it('reports approvingFactSheet true while an approve request is in flight', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    // Executor form: tsconfig lib predates Promise.withResolvers (same note as research.store.spec.ts).
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    repo.scriptApprove(async () => {
      await gate;
      return { id: 'sheet-1', version: 1, status: 'approved', etag: SHEET_ETAG };
    });
    repo.scriptSheet(() => Promise.resolve(factSheet(1, { etag: SHEET_ETAG, status: 'approved', facts: [fact(1), fact(2)] })));

    const pending = store.approve(EPISODE_ID);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(store.savingFactSheet() || store.approvingFactSheet()).toBe(true);
    expect(store.approvingFactSheet()).toBe(true);

    release();
    await pending;
  });

  it('blocks a second approve while the first is in flight', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    repo.scriptApprove(async () => {
      await gate;
      return { id: 'sheet-1', version: 1, status: 'approved', etag: SHEET_ETAG };
    });
    repo.scriptSheet(() => Promise.resolve(factSheet(1, { etag: SHEET_ETAG, status: 'approved', facts: [fact(1), fact(2)] })));

    const first = store.approve(EPISODE_ID);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(store.canApprove()).toBe(false);
    const second = await store.approve(EPISODE_ID);
    expect(second.ok).toBe(false);
    expect(repo.approveCalls).toHaveLength(1);

    release();
    await first;
  });

  it('does not let a late approve from episode A overwrite episode B fact sheet', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: '"fs-a"', facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    repo.scriptApprove(async () => {
      await gate;
      return { id: 'sheet-1', version: 2, status: 'approved', etag: '"fs-a-approved"' };
    });
    const pending = store.approve(EPISODE_ID);
    await new Promise((resolve) => setTimeout(resolve, 0));

    const otherEpisode = '33333333-3333-4333-8333-333333333333';
    repo.scriptSheet(() => Promise.resolve(factSheet(5, { id: 'sheet-b', etag: '"fs-b"', facts: [fact(1)] })));
    await store.load(otherEpisode);

    release();
    await pending;

    expect(store.factSheet()).toMatchObject({ id: 'sheet-b', status: 'draft', etag: '"fs-b"' });
  });

  it('keeps a late remove failure from episode A out of the banner after reset', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    let fail!: (cause: Error) => void;
    repo.scriptRemove(() => new Promise<void>((_resolve, reject) => { fail = reject; }));
    const pending = store.remove(EPISODE_ID, 'source-2');
    await new Promise((resolve) => setTimeout(resolve, 0));
    store.reset();
    fail(new Error('network down'));
    await pending;

    expect(store.panelError()).toBeNull();
  });

  it('sends one save request for a double-clicked save', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'A corrected statement' });
    // The dirty flag trails an edit by 150 ms (D18), so wait it out before the save is allowed.
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(store.canSave()).toBe(true);

    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    repo.scriptSave(async () => {
      await gate;
      return { id: 'sheet-1', version: 2, status: 'draft', etag: '"fs-2"' };
    });
    const first = store.save(EPISODE_ID);
    await new Promise((resolve) => setTimeout(resolve, 0));
    const second = await store.save(EPISODE_ID);
    expect(second.ok).toBe(false);
    expect(repo.saveCalls).toHaveLength(1);

    release();
    await first;
  });
});

describe('ResearchStore publish gate (QA)', () => {
  it('reports open before the editor route has loaded anything', () => {
    const { store } = setup();
    expect(store.gate()).toBe('open');
  });

  it('reports sheet_not_approved for a loaded draft sheet', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);
    expect(store.gate()).toBe('sheet_not_approved');
  });

  it('reports open for an approved sheet citing two fetched sources', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { status: 'approved', etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);
    expect(store.gate()).toBe('open');
  });

  it('reports sources_insufficient for an approved sheet citing one fetched source', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { status: 'approved', etag: SHEET_ETAG, facts: [fact(1)] }));
    await store.load(EPISODE_ID);
    expect(store.gate()).toBe('sources_insufficient');
  });
});
