import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
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
const OTHER_EPISODE_ID = '44444444-4444-4444-8444-444444444444';
const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const SHEET_ETAG = '"fs-1"';

function appError(code: string, status: number): AppError {
  const kind = status === 0 ? 'network' : status === 409 ? 'precondition' : 'unknown';
  return { code, status, kind, fieldErrors: {} };
}

function rejected<T>(error: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- structured domain-port failure
  return Promise.reject(error);
}

/** A domain-port call that stays pending until the test decides how it fails. */
function failLater() {
  let reject!: (error: AppError) => void;
  const failure = new Promise<never>((_resolve, rejectLater) => {
    reject = rejectLater;
  });
  return { failure, fail: (error: AppError): void => { reject(error); } };
}

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

describe('ResearchStore dirty draft and stale responses (QA)', () => {
  it('does not replace an edited draft or its ETag when a refresh reads a newer sheet (116)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    store.editFact('f1', { statement: 'Edited locally' });
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(store.dirty()).toBe(true);

    repo.scriptSheet(() => Promise.resolve(factSheet(2, { etag: '"fs-2"', facts: [fact(1), fact(2)] })));
    await store.refresh(EPISODE_ID);

    expect(store.draft()[0]?.statement).toBe('Edited locally');
    expect(store.factSheet()?.etag).toBe(SHEET_ETAG);
  });

  it('keeps a late add failure from the previous episode out of the form error after reset (D-B family)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), null);
    await store.load(EPISODE_ID);

    const late = failLater();
    repo.scriptAdd(() => late.failure);
    const pending = store.addUrl(EPISODE_ID, 'https://example9.com/article');
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(repo.addCalls).toHaveLength(1);

    store.reset();
    late.fail(appError('source_domain_not_allowed', 400));
    await pending;

    expect(store.formError()).toBeNull();
  });

  it('keeps a late generate failure from the previous episode out of the panel error after reset (D-B family)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), null);
    await store.load(EPISODE_ID);

    const late = failLater();
    repo.scriptGenerate(() => late.failure);
    const pending = store.generate(EPISODE_ID);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(repo.generateCalls).toHaveLength(1);

    store.reset();
    late.fail(appError('fact_sheet_unavailable', 503));
    await pending;

    expect(store.panelError()).toBeNull();
  });

  it('shows a generate failure in the panel while the episode is still current (control)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), null);
    await store.load(EPISODE_ID);

    repo.scriptGenerate(() => rejected(appError('fact_sheet_unavailable', 503)));
    await store.generate(EPISODE_ID);

    expect(store.panelError()?.code).toBe('fact_sheet_unavailable');
  });

  it('keeps a late approve conflict of the previous episode out of the dialog after the page moved on (D-B family)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    const late = failLater();
    repo.scriptApprove(() => late.failure);
    const pending = store.approve(EPISODE_ID);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(repo.approveCalls).toHaveLength(1);

    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: '"fs-9"', facts: [fact(1), fact(2)] }));
    await store.load(OTHER_EPISODE_ID);

    late.fail(appError('fact_sheet_conflict', 412));
    await pending;

    // The 412 of the episode we left is neither a dialog on this page nor a banner: the sheet we show is untouched.
    expect(store.conflict()).toBeNull();
    expect(store.panelError()).toBeNull();
    expect(store.approvingFactSheet()).toBe(false);
    expect(store.factSheet()?.etag).toBe('"fs-9"');
  });

  it('opens the conflict dialog for an approve conflict while the episode is still current (control)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);

    repo.scriptApprove(() => rejected(appError('fact_sheet_conflict', 412)));
    await store.approve(EPISODE_ID);

    expect(store.conflict()).toEqual({ etag: SHEET_ETAG });
    expect(store.approvingFactSheet()).toBe(false);
  });
});
