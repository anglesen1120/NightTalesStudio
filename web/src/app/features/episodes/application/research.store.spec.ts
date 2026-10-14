import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IdempotencyKeys } from '@core/http';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EpisodesRepository } from '../domain/episodes.repository';
import type { FactSheet } from '../domain/research.model';
import { ResearchRepository } from '../domain/research.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { episodeDetail, FakeEpisodesRepository } from '../testing/fake-episodes.repository';
import { FakeResearchRepository, fact, factSheet, INVALID_CONTENT_FIELDS, source, sourceList } from '../testing/fake-research.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { DIRTY_DEBOUNCE_MS, ResearchStore } from './research.store';

const EPISODE_ID = '22222222-2222-4222-8222-222222222222';
const OTHER_EPISODE_ID = '33333333-3333-4333-8333-333333333333';
const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const SHEET_ETAG = '"fs-1"';

function appError(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  const kind = status === 0 ? 'network' : status === 412 ? 'conflict'
    : status === 403 ? 'auth' : status === 409 ? 'precondition' : status === 429 ? 'rate_limit' : 'unknown';
  return { code, status, kind, fieldErrors: {}, ...extra };
}

function rejected<T>(error: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- structured domain-port failure
  return Promise.reject(error);
}

/** Lets the store's read chain settle (its awaits are microtasks) and, with `ms`, the dirty debounce fire. */
function flush(ms = 0): Promise<void> {
  // The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** README D18: an edit turns the draft dirty only after the debounce, and every write needs a dirty draft. */
function edited(): Promise<void> {
  return flush(DIRTY_DEBOUNCE_MS + 5);
}

/**
 * The page's own "the run is over" path: an accepted generate holds the build button until a read says otherwise (F3),
 * so a spec that generates twice has to let the episode report the run finished in between.
 */
async function settleRun(store: ResearchStore, episodes: FakeEpisodesRepository): Promise<void> {
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ id: EPISODE_ID, seriesId: SERIES_ID, generation: null })));
  await store.refresh(EPISODE_ID);
}

interface Harness {
  readonly store: ResearchStore;
  readonly repo: FakeResearchRepository;
  readonly episodes: FakeEpisodesRepository;
  readonly series: FakeSeriesOptionsRepository;
  readonly realtime: FakeEpisodeRealtime;
  readonly sessionGeneration: WritableSignal<number>;
}

function setup(topic = 'true_crime'): Harness {
  const repo = new FakeResearchRepository();
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ id: EPISODE_ID, seriesId: SERIES_ID, generation: null })));
  const series = new FakeSeriesOptionsRepository();
  series.summary = { ...series.summary, id: SERIES_ID, topic };
  const realtime = new FakeEpisodeRealtime();
  const sessionGeneration = signal(0);
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, ResearchStore, IdempotencyKeys,
    { provide: ResearchRepository, useValue: repo },
    { provide: EpisodesRepository, useValue: episodes },
    { provide: SeriesOptionsRepository, useValue: series },
    { provide: RealtimeService, useValue: realtime },
    { provide: SessionStore, useValue: { generation: sessionGeneration } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  return { store: TestBed.inject(ResearchStore), repo, episodes, series, realtime, sessionGeneration };
}

/** A draft sheet whose single fact cites the first fetched url source. */
function draftSheet(overrides: Partial<FactSheet> = {}): FactSheet {
  return factSheet(1, {
    etag: SHEET_ETAG,
    facts: [fact(1, { sources: [{ sourceId: 'source-1', alias: 'S1', title: 'Title 1', url: 'https://example1.com/article', isUserNote: false }] })],
    ...overrides,
  });
}

afterEach(() => { vi.useRealTimers(); TestBed.resetTestingModule(); });

describe('ResearchStore reads', () => {
  it('loads sources and the fact sheet in two parallel calls and fills the counter from limits', async () => {
    const { store, repo, episodes } = setup();
    repo.seed(sourceList([source(1), source(2, { kind: 'user_note' })], { maxPerEpisode: 10, urlSourcesLast24h: 3, maxUrlSourcesPerDay: 20 }), draftSheet());

    await store.load(EPISODE_ID);

    expect(repo.listCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
    expect(repo.sheetCalls).toEqual([{ episodeId: EPISODE_ID, userAction: true }]);
    // The episode/series context of the gate is a background read: it never claims a user action.
    expect(episodes.getCalls[0]!).toEqual({ episodeId: EPISODE_ID, userAction: false });
    expect(store.loaded()).toBe(true);
    expect(store.loading()).toBe(false);
    expect(store.usedCount()).toBe(2);
    expect(store.maxCount()).toBe(10);
    expect(store.urlCount()).toBe(1);
    expect(store.pendingCount()).toBe(0);
    expect(store.error()).toBeNull();
  });

  it('reads a second time only when the episode changed or the first load failed', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    await store.load(EPISODE_ID);
    expect(repo.listCalls).toHaveLength(1);
    await store.load(OTHER_EPISODE_ID);
    expect(repo.listCalls).toHaveLength(2);
  });

  it('treats a 200 with a null body as the normal empty state: no error, generate allowed, save and approve refused', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), null);

    await store.load(EPISODE_ID);

    expect(store.factSheet()).toBeNull();
    expect(store.draft()).toEqual([]);
    expect(store.error()).toBeNull();
    expect(store.loaded()).toBe(true);
    expect(store.canGenerate()).toBe(true);
    expect(store.canSave()).toBe(false);
    expect(store.canApprove()).toBe(false);
    expect(store.gate()).toBe('sheet_not_approved');
  });

  it('reports the load error of a failed sources read and keeps the gate open', async () => {
    const { store, repo } = setup();
    repo.scriptList(() => rejected(appError('internal_error', 500)));

    await store.load(EPISODE_ID);

    expect(store.error()?.code).toBe('internal_error');
    expect(store.loaded()).toBe(false);
    expect(store.loading()).toBe(false);
    expect(store.gate()).toBe('open');
  });

  it('reports a 403 on the fact-sheet read as the page error (the sheet never 404s)', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), null);
    repo.scriptSheet(() => rejected(appError('forbidden', 403)));

    await store.load(EPISODE_ID);

    expect(store.error()?.code).toBe('forbidden');
    expect(store.error()?.status).toBe(403);
    expect(store.loaded()).toBe(false);
  });

  it('reports a failing background read as a banner without losing the loaded page', async () => {
    const { store, repo, realtime } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptList(() => rejected(appError('internal_error', 500)));

    realtime.emit(EPISODE_ID);
    await flush();

    expect(store.loaded()).toBe(true);
    expect(store.error()).toBeNull();
    expect(store.sources()).toHaveLength(1);
  });

  it('refreshes on demand and reports its answer', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.listResult = sourceList([source(1), source(2)]);

    const result = await store.refresh(EPISODE_ID);

    expect(result).toEqual({ ok: true, value: undefined });
    expect(store.sources()).toHaveLength(2);
    expect(store.refreshing()).toBe(false);
  });

  it('resets every draft and read of the instance', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Changed' });

    store.reset();

    expect(store.loaded()).toBe(false);
    expect(store.sources()).toEqual([]);
    expect(store.limits()).toBeNull();
    expect(store.factSheet()).toBeNull();
    expect(store.draft()).toEqual([]);
    expect(store.dirty()).toBe(false);
    expect(store.conflict()).toBeNull();
    expect(store.rateLimitSeconds()).toBeNull();
    expect(store.gate()).toBe('open');
  });
});

describe('ResearchStore gate', () => {
  it('reports an unloaded store as open', () => {
    expect(setup().store.gate()).toBe('open');
  });

  it('reports a non-true-crime series as not applicable', async () => {
    const { store, repo } = setup('ghost');
    repo.seed(sourceList([source(1)]), factSheet(1, { status: 'approved' }));
    await store.load(EPISODE_ID);
    expect(store.gate()).toBe('not_applicable');
  });

  it('needs an approved sheet and two cited fetched url sources before it opens', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    expect(store.gate()).toBe('sheet_not_approved');

    repo.sheetResult = factSheet(2, { status: 'approved', facts: [fact(1)] });
    await store.refresh(EPISODE_ID);
    expect(store.gate()).toBe('sources_insufficient');

    repo.listResult = sourceList([source(1), source(2)]);
    repo.sheetResult = factSheet(2, {
      status: 'approved',
      facts: [fact(1), fact(2)],
    });
    await store.refresh(EPISODE_ID);
    expect(store.gate()).toBe('open');

    // A failed url and a user note never count as the two cited, fetched sources.
    repo.listResult = sourceList([source(1), source(2, { status: 'failed' })]);
    await store.refresh(EPISODE_ID);
    expect(store.gate()).toBe('sources_insufficient');
  });
});

describe('ResearchStore sources', () => {
  it('sends the url or the note of the submit and re-reads the server rows', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);

    await store.addUrl(EPISODE_ID, 'https://example9.com/article');
    await store.addNote(EPISODE_ID, 'Owner note');

    expect(repo.addCalls.map((call) => call.input)).toEqual([
      { url: 'https://example9.com/article' },
      { noteText: 'Owner note' },
    ]);
    expect(repo.addCalls.map((call) => call.intent)).toEqual(['source.add', 'source.add']);
    expect(repo.addCalls[0]!.key).not.toBe('');
    // One list read per accepted write: the rows and the 24 h counters are the server's.
    expect(repo.listCalls).toHaveLength(3);
    expect(store.formError()).toBeNull();
  });

  it('keeps the intent key on a retry after a network failure and mints a new one after a 422', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptAdd(() => rejected(appError('network_error', 0)));

    const first = await store.addUrl(EPISODE_ID, 'https://example9.com/article');
    expect(first.ok).toBe(false);
    expect(store.formError()?.code).toBe('network_error');
    // The replay carries the same key and is accepted: the intent is only settled by a real answer.
    const retry = await store.addUrl(EPISODE_ID, 'https://example9.com/article');
    expect(retry.ok).toBe(true);
    expect(repo.addCalls[1]!.key).toBe(repo.addCalls[0]!.key);

    repo.scriptAdd(() => rejected(appError('idempotency_key_reused', 422)));
    await store.addUrl(EPISODE_ID, 'https://example9.com/article');
    await store.addUrl(EPISODE_ID, 'https://example9.com/article');
    expect(repo.addCalls[3]!.key).not.toBe(repo.addCalls[0]!.key);
  });

  it('exposes the row a 409 duplicate names and clears it on the next submit', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptAdd(() => rejected(appError('source_duplicate', 409)));

    await store.addUrl(EPISODE_ID, 'https://example1.com/article');

    expect(store.formError()?.code).toBe('source_duplicate');
    expect(store.duplicateOf()).toBe('source-1');

    store.clearFormError();
    expect(store.duplicateOf()).toBeNull();
    expect(store.formError()).toBeNull();
  });

  it('keeps the field pointers of a 400 so the input can show them inline', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptAdd(() => rejected(appError('source_url_invalid', 400, { fieldErrors: { url: ['url_invalid'] } })));

    await store.addUrl(EPISODE_ID, 'not a url');

    expect(store.formError()?.code).toBe('source_url_invalid');
    expect(store.formError()?.fieldErrors['url']).toEqual(['url_invalid']);
    expect(store.duplicateOf()).toBeNull();
  });

  it('maps a blocked domain to its own code without touching the panel banner', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptAdd(() => rejected(appError('source_domain_not_allowed', 400)));

    await store.addUrl(EPISODE_ID, 'https://youtube.com/watch?v=1');

    expect(store.formError()?.code).toBe('source_domain_not_allowed');
    expect(store.panelError()).toBeNull();
  });

  it('carries Retry-After into the rate limit and holds every write while it runs', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Changed' });
    repo.scriptRemove(() => rejected(appError('rate_limited', 429, { retryAfterSec: 42 })));

    const result = await store.remove(EPISODE_ID, 'source-1');

    expect(result.ok).toBe(false);
    expect(store.rateLimitSeconds()).toBe(42);
    expect(store.canSave()).toBe(false);
    expect(store.canApprove()).toBe(false);
    expect(store.canGenerate()).toBe(false);
    const refused = await store.retry(EPISODE_ID, 'source-1');
    expect(refused.ok).toBe(false);
    expect(repo.retryCalls).toHaveLength(0);

    store.clearPanelError();
    expect(store.rateLimitSeconds()).toBeNull();
    expect(store.canGenerate()).toBe(true);
  });

  it('holds the url add at both caps and keeps notes free of the 24 h budget', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)], { maxPerEpisode: 10, urlSourcesLast24h: 20, maxUrlSourcesPerDay: 20 }), draftSheet());
    await store.load(EPISODE_ID);
    expect(store.canAddUrl()).toBe(false);

    repo.listResult = sourceList([source(1)], { maxPerEpisode: 1, urlSourcesLast24h: 1, maxUrlSourcesPerDay: 20 });
    await store.refresh(EPISODE_ID);
    expect(store.canAddUrl()).toBe(false);

    repo.listResult = sourceList([source(1, { kind: 'user_note' })], { maxPerEpisode: 1, urlSourcesLast24h: 0, maxUrlSourcesPerDay: 20 });
    await store.refresh(EPISODE_ID);
    expect(store.canAddUrl()).toBe(true);
  });

  it('removes and retries one row, re-reading the list and reporting a refused state', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);

    await store.remove(EPISODE_ID, 'source-1');
    await store.retry(EPISODE_ID, 'source-1');

    expect(repo.removeCalls).toEqual([{ episodeId: EPISODE_ID, sourceId: 'source-1' }]);
    expect(repo.retryCalls).toEqual([{ episodeId: EPISODE_ID, sourceId: 'source-1' }]);
    expect(repo.listCalls).toHaveLength(3);

    repo.scriptRemove(() => rejected(appError('episode_invalid_state', 409)));
    const result = await store.remove(EPISODE_ID, 'source-1');
    expect(result.ok).toBe(false);
    expect(store.panelError()?.code).toBe('episode_invalid_state');
  });

  it('never double-submits one row and never removes while the rate limit holds', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptRemove(() => rejected(appError('rate_limited', 429, { retryAfterSec: 5 })));
    await store.remove(EPISODE_ID, 'source-1');

    const again = await store.remove(EPISODE_ID, 'source-1');
    expect(again.ok).toBe(false);
    expect(repo.removeCalls).toHaveLength(1);
  });
});

describe('ResearchStore generation', () => {
  it('generates once per click: the same key after a network error, a new one after a 409 or a 422', async () => {
    const { store, repo, episodes } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptGenerate(() => rejected(appError('network_error', 0)));

    const first = await store.generate(EPISODE_ID);
    expect(first.ok).toBe(false);
    expect(store.generating()).toBe(false);
    expect(store.panelError()?.code).toBe('network_error');
    await store.generate(EPISODE_ID);
    expect(repo.generateCalls[1]!.key).toBe(repo.generateCalls[0]!.key);
    expect(repo.generateCalls[0]!.intent).toBe('fact_sheet.generate');

    // That replay was accepted, so a run is live now (F3): the next clicks wait for the episode to report it finished.
    await settleRun(store, episodes);

    repo.scriptGenerate(() => rejected(appError('fact_sheet_generation_in_progress', 409)));
    await store.generate(EPISODE_ID);
    await store.generate(EPISODE_ID);
    expect(repo.generateCalls[3]!.key).not.toBe(repo.generateCalls[0]!.key);

    // The second click of that phase was accepted too, so its run settles before the last phase as well.
    await settleRun(store, episodes);

    // A 422 (the key was already used) settles the intent too, so the next click mints a fresh key (Acceptance).
    repo.scriptGenerate(() => rejected(appError('idempotency_key_reused', 422)));
    await store.generate(EPISODE_ID);
    await store.generate(EPISODE_ID);
    expect(repo.generateCalls[5]!.key).not.toBe(repo.generateCalls[4]!.key);
  });

  it('refuses to build a sheet without one fetched source', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1, { status: 'failed' })]), draftSheet());
    await store.load(EPISODE_ID);

    expect(store.canGenerate()).toBe(false);
    const result = await store.generate(EPISODE_ID);

    expect(result.ok).toBe(false);
    expect(repo.generateCalls).toHaveLength(0);
  });

  it('holds generate while any run of the episode is queued or running', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    const episodes = TestBed.inject(EpisodesRepository) as FakeEpisodesRepository;
    episodes.scriptGet(() => Promise.resolve(episodeDetail({
      id: EPISODE_ID,
      seriesId: SERIES_ID,
      generation: { kind: 'fact_sheet', status: 'running', stage: 'extracting', errorCode: null, startedAt: '2026-10-01T10:00:00Z', processingPaused: null },
    })));
    await store.refresh(EPISODE_ID);

    expect(store.running()).toBe(true);
    expect(store.canGenerate()).toBe(false);

    // Design: another kind of run holds the build too; the panel names the kind itself, the button only waits.
    episodes.scriptGet(() => Promise.resolve(episodeDetail({
      id: EPISODE_ID,
      seriesId: SERIES_ID,
      generation: { kind: 'story', status: 'queued', stage: 'queued', errorCode: null, startedAt: null, processingPaused: null },
    })));
    await store.refresh(EPISODE_ID);

    expect(store.kind()).toBe('story');
    expect(store.running()).toBe(true);
    expect(store.canGenerate()).toBe(false);
  });
});

describe('ResearchStore draft and writes', () => {
  it('debounces the dirty flag by 150 ms and sets it at once for an adopted document', async () => {
    vi.useFakeTimers();
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    expect(store.dirty()).toBe(false);

    store.editFact('f1', { statement: 'Changed' });
    expect(store.dirty()).toBe(false);
    vi.advanceTimersByTime(DIRTY_DEBOUNCE_MS - 1);
    expect(store.dirty()).toBe(false);
    vi.advanceTimersByTime(1);
    expect(store.dirty()).toBe(true);
  });

  it('refuses to save a draft whose fact has no statement', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);

    store.addFact(['source-1']);

    expect(store.draft()).toHaveLength(2);
    expect(store.canSave()).toBe(false);
  });

  it('edits, adds and removes facts and drops one citation of a rejected fact', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);

    store.addFact(['source-1']);
    const added = store.draft()[1]!;
    store.editFact(added.clientKey, { statement: 'Added fact' });
    store.editFact('f1', { confidence: 'low' });
    store.dropSourceFromFact(added.clientKey, 'source-1');

    expect(store.draft()[1]!.statement).toBe('Added fact');
    expect(store.draft()[0]!.confidence).toBe('low');
    expect(store.draft()[1]!.sourceIds).toEqual([]);

    store.removeFact(added.clientKey);
    expect(store.draft()).toHaveLength(1);
  });

  it('sends the draft with omitted ids for new facts and omitted confidence for untouched ones', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: '  Edited 1  ' });
    store.addFact(['source-1']);
    store.editFact(store.draft()[1]!.clientKey, { statement: 'New fact' });
    await edited();
    repo.writeResult = { id: 'sheet-1', version: 2, status: 'draft', etag: '"fs-2"' };
    // The write answer carries no facts: the store re-reads the document and adopts the version the server now has.
    repo.scriptSheet(() => Promise.resolve(factSheet(1, { etag: '"fs-2"', facts: [fact(1), fact(2)] })));

    const result = await store.save(EPISODE_ID);

    expect(result.ok).toBe(true);
    expect(repo.saveCalls[0]!.etag).toBe(SHEET_ETAG);
    expect(repo.saveCalls[0]!.facts).toEqual([
      { id: 'f1', statement: 'Edited 1', sourceIds: ['source-1'] },
      { statement: 'New fact', sourceIds: ['source-1'], confidence: 'high' },
    ]);
    // The write answer carries no facts: the store re-reads the document and adopts the new ETag.
    expect(repo.sheetCalls[repo.sheetCalls.length - 1]).toEqual({ episodeId: EPISODE_ID, userAction: false });
    expect(store.factSheet()?.etag).toBe('"fs-2"');
    expect(store.dirty()).toBe(false);
    expect(store.canSave()).toBe(false);
  });

  it('keeps the draft and opens the conflict on a 412 without a banner', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    // The draft reports dirty only after the 150 ms debounce (D18), and every write needs a dirty draft.
    await edited();
    repo.scriptSave(() => rejected(appError('fact_sheet_conflict', 412)));

    const result = await store.save(EPISODE_ID);

    expect(result.ok).toBe(false);
    expect(store.conflict()).toEqual({ etag: SHEET_ETAG });
    // F5: the dialog can tell which write lost the race — a save here.
    expect(store.conflictSource()).toBe('save');
    expect(store.panelError()).toBeNull();
    expect(store.draft()[0]!.statement).toBe('Edited 1');
    expect(store.savingFactSheet()).toBe(false);
  });

  it('treats a 428 as the banner of a client bug and never opens the conflict dialog', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    await edited();
    repo.scriptSave(() => rejected(appError('if_match_required', 428)));

    await store.save(EPISODE_ID);

    // 428 is our own bug (no If-Match went out): there is nothing for the user to choose between, so it is a banner.
    expect(store.conflict()).toBeNull();
    expect(store.conflictSource()).toBeNull();
    expect(store.panelError()?.code).toBe('if_match_required');
  });

  it('reloads the server version on conflict and drops the local draft', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    // The draft reports dirty only after the 150 ms debounce (D18), and every write needs a dirty draft.
    await edited();
    repo.scriptSave(() => rejected(appError('fact_sheet_conflict', 412)));
    await store.save(EPISODE_ID);
    repo.scriptSheet(() => Promise.resolve(factSheet(2, { facts: [fact(1, { statement: 'Theirs' })] })));

    const result = await store.reloadLatest(EPISODE_ID);

    expect(result.ok).toBe(true);
    expect(store.conflict()).toBeNull();
    expect(store.conflictSource()).toBeNull();
    expect(store.factSheet()?.version).toBe(2);
    expect(store.draft()[0]!.statement).toBe('Theirs');
    expect(store.dirty()).toBe(false);
  });

  it('re-puts the draft against the fresh ETag for save-mine-as-new-version', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    // The draft reports dirty only after the 150 ms debounce (D18), and every write needs a dirty draft.
    await edited();
    repo.scriptSave(() => rejected(appError('fact_sheet_conflict', 412)));
    await store.save(EPISODE_ID);
    repo.scriptSheet(() => Promise.resolve(factSheet(2, { facts: [fact(1, { statement: 'Theirs' })] })));

    const result = await store.saveMineAsNewVersion(EPISODE_ID);

    expect(result.ok).toBe(true);
    expect(repo.saveCalls[1]!.etag).toBe('"fs-2"');
    expect(repo.saveCalls[1]!.facts[0]!.statement).toBe('Edited 1');
    expect(store.conflict()).toBeNull();
  });

  it('approves only a clean draft and adopts the approved answer', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);
    expect(store.canApprove()).toBe(true);
    // The post-write re-read is the truth about the version the server now has.
    repo.scriptSheet(() => Promise.resolve(factSheet(1, { etag: SHEET_ETAG, status: 'approved', facts: [fact(1), fact(2)] })));

    const result = await store.approve(EPISODE_ID);

    expect(result.ok).toBe(true);
    expect(repo.approveCalls).toEqual([{ episodeId: EPISODE_ID, etag: SHEET_ETAG }]);
    expect(store.isApproved()).toBe(true);
    expect(store.approvingFactSheet()).toBe(false);
  });

  it('refuses to approve a draft the owner is editing', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    await edited();

    expect(store.canApprove()).toBe(false);
    const result = await store.approve(EPISODE_ID);
    expect(result.ok).toBe(false);
    expect(repo.approveCalls).toHaveLength(0);
  });

  it('maps a refused approve to its own code and keeps the draft unapproved', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    repo.scriptApprove(() => rejected(appError('story_sources_required', 409)));

    const result = await store.approve(EPISODE_ID);

    expect(result.ok).toBe(false);
    expect(store.panelError()?.code).toBe('story_sources_required');
    expect(store.isApproved()).toBe(false);
  });

  it('starts the next draft from an approved sheet with one PUT of its current facts', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), factSheet(3, { status: 'approved', facts: [fact(1)] }));
    await store.load(EPISODE_ID);
    repo.writeResult = { id: 'sheet-1', version: 4, status: 'draft', etag: '"fs-4"' };
    repo.scriptSheet(() => Promise.resolve(factSheet(4, { facts: [fact(1)] })));

    const result = await store.startEdit(EPISODE_ID);

    expect(result.ok).toBe(true);
    expect(repo.saveCalls[0]!.etag).toBe('"fs-3"');
    expect(repo.saveCalls[0]!.facts).toEqual([{ id: 'f1', statement: 'Fact 1', sourceIds: ['source-1'] }]);
    expect(store.factSheet()?.version).toBe(4);
    expect(store.isApproved()).toBe(false);
  });

  it('reports a failed post-write re-read as a banner without losing the write', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    await edited();
    repo.writeResult = { id: 'sheet-1', version: 2, status: 'draft', etag: '"fs-2"' };
    repo.scriptSheet(() => rejected(appError('internal_error', 500)));

    const result = await store.save(EPISODE_ID);

    expect(result.ok).toBe(true);
    expect(store.factSheet()?.etag).toBe('"fs-2"');
    expect(store.panelError()?.code).toBe('internal_error');
  });
});

describe('ResearchStore realtime', () => {
  it('re-reads sources and the sheet for a coalesced event about this episode only', async () => {
    const { store, repo, series, realtime } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.watch(EPISODE_ID);
    const lists = repo.listCalls.length;
    const sheets = repo.sheetCalls.length;
    const seriesReads = series.getCalls.length;

    realtime.emit(OTHER_EPISODE_ID);
    await flush();
    expect(repo.listCalls).toHaveLength(lists);

    realtime.emit(EPISODE_ID);
    await flush();

    expect(repo.listCalls).toHaveLength(lists + 1);
    expect(repo.sheetCalls).toHaveLength(sheets + 1);
    // A background event never re-reads the series (its topic cannot change under the page).
    expect(series.getCalls).toHaveLength(seriesReads);
    expect(store.error()).toBeNull();
  });

  it('ignores an event while an unsaved draft is open', async () => {
    const { store, repo, realtime } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.watch(EPISODE_ID);
    store.editFact('f1', { statement: 'Edited 1' });
    await edited();

    realtime.emit(EPISODE_ID);
    await flush();

    expect(store.draft()[0]!.statement).toBe('Edited 1');
    expect(store.dirty()).toBe(true);
  });

  it('unsubscribes when the store is destroyed', async () => {
    const { store, repo, realtime } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.watch(EPISODE_ID);
    expect(realtime.subscriptions).toHaveLength(1);

    TestBed.resetTestingModule();

    expect(realtime.subscriptions[0]!.unsubscribed).toBe(true);
  });
});

describe('ResearchStore review fixes', () => {
  it('scopes a 429 of the add form to that form and reads Retry-After: 0 as no wait', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.editFact('f1', { statement: 'Changed' });
    repo.scriptAdd(() => rejected(appError('rate_limited', 429, { retryAfterSec: 30 })));

    await store.addUrl(EPISODE_ID, 'https://example9.com/article');

    expect(store.formError()?.code).toBe('rate_limited');
    expect(store.formRateLimitSeconds()).toBe(30);
    // F2: the refused submit holds its own form only — the sheet path and the rows stay live.
    expect(store.rateLimitSeconds()).toBeNull();
    expect(store.canSave()).toBe(true);
    expect(store.canGenerate()).toBe(true);
    await store.remove(EPISODE_ID, 'source-1');
    expect(repo.removeCalls).toHaveLength(1);

    repo.scriptAdd(() => rejected(appError('rate_limited', 429, { retryAfterSec: 0 })));
    await store.addNote(EPISODE_ID, 'A note');

    // `Retry-After: 0` is not a wait: neither state may hold a control for zero seconds.
    expect(store.formRateLimitSeconds()).toBeNull();
    expect(store.rateLimitSeconds()).toBeNull();
  });

  it('reads the run a 202 named without waiting for an event', async () => {
    const { store, repo, episodes } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    expect(store.running()).toBe(false);
    repo.generateResult = { runId: 'run-9', factSheetId: null, status: 'queued' };

    const result = await store.generate(EPISODE_ID);

    expect(result.ok).toBe(true);
    // F3: the answer is the truth about the run until the next read, so the build button holds from here on.
    expect(store.running()).toBe(true);
    expect(store.kind()).toBe('fact_sheet');
    expect(store.generating()).toBe(false);
    expect(store.canGenerate()).toBe(false);

    // The next detail read replaces the local run state (a finished run releases the button).
    episodes.scriptGet(() => Promise.resolve(episodeDetail({
      id: EPISODE_ID,
      seriesId: SERIES_ID,
      generation: { kind: 'fact_sheet', status: 'succeeded', stage: 'done', errorCode: null, startedAt: null, processingPaused: null },
    })));
    await store.refresh(EPISODE_ID);

    expect(store.running()).toBe(false);
    expect(store.canGenerate()).toBe(true);
  });

  it('clears the open intents when the page moves to another episode', async () => {
    const { store, repo, episodes } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    // A network failure is the one answer that keeps its key open (a retry replays it).
    repo.scriptAdd(() => rejected(appError('network_error', 0)));
    repo.scriptGenerate(() => rejected(appError('network_error', 0)));
    await store.addUrl(EPISODE_ID, 'https://example9.com/article');
    await store.generate(EPISODE_ID);
    const addKey = repo.addCalls[0]!.key;
    const generateKey = repo.generateCalls[0]!.key;

    episodes.scriptGet(() => Promise.resolve(episodeDetail({ id: OTHER_EPISODE_ID, seriesId: SERIES_ID, generation: null })));
    await store.load(OTHER_EPISODE_ID);
    repo.scriptAdd(() => Promise.resolve(source(2)));
    repo.generateResult = { runId: 'run-2', factSheetId: null, status: 'queued' };
    await store.addUrl(OTHER_EPISODE_ID, 'https://example10.com/article');
    await store.generate(OTHER_EPISODE_ID);

    // F4: the failed submit of the episode we left is never replayed for the new one (that would be a duplicate write).
    expect(repo.addCalls[1]!.key).not.toBe(addKey);
    expect(repo.generateCalls[1]!.key).not.toBe(generateKey);
  });

  it('names an approve conflict and refuses save-mine while the draft is clean', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), factSheet(1, { etag: SHEET_ETAG, facts: [fact(1), fact(2)] }));
    await store.load(EPISODE_ID);
    repo.scriptApprove(() => rejected(appError('fact_sheet_conflict', 412)));

    await store.approve(EPISODE_ID);

    expect(store.conflict()).toEqual({ etag: SHEET_ETAG });
    expect(store.conflictSource()).toBe('approve');
    // F5: nothing was edited, so the local document *is* the server's — there is nothing of mine to re-send.
    const refused = await store.saveMineAsNewVersion(EPISODE_ID);
    expect(refused.ok).toBe(false);
    expect(repo.saveCalls).toHaveLength(0);

    // After an edit it is a real divergence, and the dialog's second button sends it against the fresh ETag.
    repo.scriptSheet(() => Promise.resolve(factSheet(2, { etag: '"fs-2"', facts: [fact(1), fact(2)] })));
    store.editFact('f1', { statement: 'Mine' });

    const saved = await store.saveMineAsNewVersion(EPISODE_ID);

    expect(saved.ok).toBe(true);
    expect(repo.saveCalls[0]!.etag).toBe('"fs-2"');
  });

  it('reports "Edit" on an approved sheet as the source of its conflict', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), factSheet(3, { status: 'approved', facts: [fact(1)] }));
    await store.load(EPISODE_ID);
    repo.scriptSave(() => rejected(appError('fact_sheet_conflict', 412)));

    await store.startEdit(EPISODE_ID);

    expect(store.conflictSource()).toBe('edit');
  });

  it('decides save, approve and a refetch without waiting for the 150 ms debounce', async () => {
    vi.useFakeTimers();
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);

    store.editFact('f1', { statement: 'Changed' });

    // D18 keeps the tab guard trailing the edit; the decisions never wait for it (F7).
    expect(store.dirty()).toBe(false);
    expect(store.canSave()).toBe(true);
    expect(store.canApprove()).toBe(false);

    // A read inside the window must not overwrite the draft with the version it fetched.
    repo.scriptSheet(() => Promise.resolve(factSheet(2, { etag: '"fs-2"', facts: [fact(1, { statement: 'Theirs' })] })));
    await store.refresh(EPISODE_ID);

    expect(store.draft()[0]!.statement).toBe('Changed');
    expect(store.factSheet()?.version).toBe(1);
    vi.useRealTimers();
  });

  it('retires the invalid-content banner when the owner edits the facts it named', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());
    await store.load(EPISODE_ID);
    store.addFact(['source-1']);
    store.editFact(store.draft()[1]!.clientKey, { statement: 'New fact' });
    // The refusal names the second row with the fake's own pointer (`content.facts[1].sourceIds`).
    repo.scriptSave(() => rejected(appError('fact_sheet_invalid_content', 400, { fieldErrors: INVALID_CONTENT_FIELDS })));
    await store.save(EPISODE_ID);
    expect(store.panelError()?.code).toBe('fact_sheet_invalid_content');
    expect(store.panelError()?.fieldErrors['content.facts[1].sourceIds']).toBeDefined();

    // F8: the edit is what fixes the named field, so the banner (and its per-fact pointer) retires with it.
    store.editFact(store.draft()[1]!.clientKey, { statement: 'Now it has text' });

    expect(store.panelError()).toBeNull();
    expect(store.canSave()).toBe(true);
  });

  it('keeps the row keys and the source list identity across refetches', async () => {
    const { store, repo } = setup();
    repo.seed(sourceList([source(1), source(2)]), draftSheet());
    await store.load(EPISODE_ID);
    const sources = store.sources();
    const keys = store.draft().map((fact) => fact.clientKey);

    // P1: a no-op refetch is not a change, so `[sources]` is the very same array and no row is re-keyed.
    await store.refresh(EPISODE_ID);
    expect(store.sources()).toBe(sources);

    // A new document under a new ETag is adopted, and a stored fact keeps the row key it already had.
    repo.scriptSheet(() => Promise.resolve(factSheet(2, { etag: '"fs-2"', facts: [fact(1, { statement: 'Theirs' })] })));
    await store.refresh(EPISODE_ID);

    expect(store.factSheet()?.version).toBe(2);
    expect(store.draft()[0]!.statement).toBe('Theirs');
    expect(store.draft()[0]!.clientKey).toBe(keys[0]);

    // A real change of the rows still lands.
    repo.listResult = sourceList([source(1), source(2, { status: 'failed' })]);
    await store.refresh(EPISODE_ID);

    expect(store.sources()).not.toBe(sources);
    expect(store.sources()[1]!.status).toBe('failed');
  });

  it('starts the episode read with the two research reads in the same tick', async () => {
    const { store, repo, episodes } = setup();
    repo.seed(sourceList([source(1)]), draftSheet());

    const pending = store.load(EPISODE_ID);

    // P2: all three requests go out before any of them answers (the old chain waited for the pair first).
    expect(repo.listCalls).toHaveLength(1);
    expect(repo.sheetCalls).toHaveLength(1);
    expect(episodes.getCalls).toHaveLength(1);
    await pending;
    expect(store.loaded()).toBe(true);
  });

  it('keeps the gate open when the series topic could not be read', async () => {
    const { store, repo, series } = setup();
    repo.seed(sourceList([source(1)]), factSheet(1, { status: 'approved' }));
    series.scriptGet(() => rejected(appError('internal_error', 500)));

    await store.load(EPISODE_ID);

    // P2: an unknown topic is never read as "not applicable": the editor must not be told anything about a topic
    // the page does not know yet, and the server's own 409 stays the authority.
    expect(store.loaded()).toBe(true);
    expect(store.gate()).toBe('open');
  });
});
