// QA (10.5.7-T1) — concurrency and recovery paths of `BibleStore` beyond the task's own spec: a second commit of
// either action while one is in flight, accept after a 412 conflict, a series swap while a load is in flight, a title
// lookup that must be repeated after the proposal comes back, and a 404 that a retry clears.
import { TestBed } from '@angular/core/testing';
import type { Result } from '@core/errors';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Bible } from '../domain/bible.model';
import {
  BIBLE_CONFLICT,
  BIBLE_NOT_FOUND,
  BIBLE_PROPOSAL_GONE,
  BIBLE_SERIES_ID,
  FakeBibleRepository,
  OTHER_EPISODE_ID,
  OTHER_SERIES_ID,
  PROPOSING_EPISODE_ID,
  bible,
  bibleProposal,
} from '../testing/fake-bible.repository';
import { BibleRepository } from './bible.repository';
import { BibleStore } from './bible.store';

function deferred<T>(): { readonly promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

function loaded(value: Bible): Result<Bible> {
  return { ok: true, value };
}

function setup(): { store: BibleStore; repo: FakeBibleRepository } {
  const repo = new FakeBibleRepository();
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: BibleRepository, useValue: repo }, BibleStore],
  });
  return { store: TestBed.inject(BibleStore), repo };
}

describe('BibleStore (QA)', () => {
  it('Dismiss_SecondDismissWhileCommitting_IssuesOneRequestAndSettles', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"' }))));
    const gate = deferred<Result<void>>();
    repo.scriptDismiss(() => gate.promise);
    await store.load(BIBLE_SERIES_ID);

    const first = store.dismissProposal(BIBLE_SERIES_ID);
    const second = await store.dismissProposal(BIBLE_SERIES_ID);
    expect(second).toEqual({ ok: true, value: undefined });
    expect(repo.dismissCalls).toHaveLength(1);
    expect(store.committing()).toBe(true);

    gate.resolve({ ok: true, value: undefined });
    await first;
    expect(store.committing()).toBe(false);
    expect(store.proposal()).toBeNull();
    expect(repo.getCalls).toHaveLength(2);
  });

  it('Accept_WhileADismissIsInFlight_IsANoOpWithNoAcceptRequest', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"' }))));
    const gate = deferred<Result<void>>();
    repo.scriptDismiss(() => gate.promise);
    await store.load(BIBLE_SERIES_ID);

    const dismissing = store.dismissProposal(BIBLE_SERIES_ID);
    const accepted = await store.acceptProposal(BIBLE_SERIES_ID);
    expect(accepted.ok).toBe(true);
    expect(repo.acceptCalls).toEqual([]);
    expect(repo.dismissCalls).toEqual([BIBLE_SERIES_ID]);

    gate.resolve({ ok: true, value: undefined });
    await dismissing;
  });

  it('Accept_AfterA412Conflict_ClearsTheConflictAndAdoptsTheAcceptedBible', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    await store.load(BIBLE_SERIES_ID);
    repo.saveResult = { ok: false, error: BIBLE_CONFLICT };
    await store.save(BIBLE_SERIES_ID, bible().content, '"sb-1"');
    expect(store.conflict()).toBe(true);

    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: null }))));
    const result = await store.acceptProposal(BIBLE_SERIES_ID);

    expect(result.ok).toBe(true);
    expect(repo.acceptCalls).toEqual([BIBLE_SERIES_ID]);
    expect(store.conflict()).toBe(false);
    expect(store.proposal()).toBeNull();
    expect(store.bible()?.version).toBe(2);
    expect(store.saving()).toBe(false);
    expect(store.committing()).toBe(false);
  });

  it('Load_SeriesChangeWhileLoading_DropsTheStaleReplyOfTheOldSeries', async () => {
    const { store, repo } = setup();
    const stale = deferred<Result<Bible>>();
    repo.scriptGet(() => stale.promise);
    const staleLoad = store.load(BIBLE_SERIES_ID);
    await Promise.resolve();
    expect(store.bible()).toBeNull();

    repo.scriptGet(() => Promise.resolve(loaded(bible({ seriesId: OTHER_SERIES_ID, version: 4, etag: '"sb-4"' }))));
    await store.load(OTHER_SERIES_ID);
    expect(store.bible()?.seriesId).toBe(OTHER_SERIES_ID);

    stale.resolve(loaded(bible({ version: 9, etag: '"sb-9"', proposal: bibleProposal() })));
    await staleLoad;

    expect(store.bible()?.seriesId).toBe(OTHER_SERIES_ID);
    expect(store.bible()?.version).toBe(4);
    expect(store.proposal()).toBeNull();
    // The stale bible's proposal title is never requested for the series the store already left.
    expect(repo.titleCalls).toEqual([]);
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('Load_TitleLookupRepeatsAfterTheProposalWasResolvedAndComesBack', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal({ proposedFromEpisodeId: PROPOSING_EPISODE_ID }) }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: null }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal({ proposedFromEpisodeId: PROPOSING_EPISODE_ID }) }))));
    await store.load(BIBLE_SERIES_ID);
    await store.acceptProposal(BIBLE_SERIES_ID);
    expect(store.proposedTitle()).toBeNull();

    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID, PROPOSING_EPISODE_ID]);
    expect(store.proposedTitle()).toBe('Tập đã duyệt');
  });

  it('Load_OneProposalWithAnotherEpisodeAsksForTheNewTitle', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal({ proposedFromEpisodeId: PROPOSING_EPISODE_ID }) }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal({ proposedFromEpisodeId: OTHER_EPISODE_ID }) }))));
    await store.load(BIBLE_SERIES_ID);
    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID, OTHER_EPISODE_ID]);
  });

  it('Load_404_ThenRetry_ClearsTheErrorAndAdoptsTheBible', async () => {
    const { store, repo } = setup();
    repo.getResult = { ok: false, error: BIBLE_NOT_FOUND };
    await store.load(BIBLE_SERIES_ID);
    expect(store.error()?.code).toBe('not_found');
    expect(store.bible()).toBeNull();

    repo.getResult = loaded(bible());
    await store.load(BIBLE_SERIES_ID);
    expect(store.error()).toBeNull();
    expect(store.bible()?.seriesId).toBe(BIBLE_SERIES_ID);
  });

  it('Dismiss_409_SetsProposalGoneAndRefetchesWithoutAnErrorState', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: null }))));
    await store.load(BIBLE_SERIES_ID);
    repo.dismissResult = { ok: false, error: BIBLE_PROPOSAL_GONE };

    const result = await store.dismissProposal(BIBLE_SERIES_ID);

    expect(result.ok).toBe(false);
    expect(store.proposalGone()).toBe(true);
    expect(store.error()).toBeNull();
    expect(store.proposal()).toBeNull();
    expect(repo.getCalls).toHaveLength(2);
    expect(store.committing()).toBe(false);
  });

  it('Save_ReplyAfterANewerLoad_IsDroppedWithoutRefetchingOrPublishingTheEdits', async () => {
    const { store, repo } = setup();
    await store.load(BIBLE_SERIES_ID);
    const gate = deferred<Result<{ version: number; etag: string }>>();
    repo.scriptSave(() => gate.promise);

    const saving = store.save(BIBLE_SERIES_ID, bible().content, '"sb-1"');
    await Promise.resolve();
    expect(store.saving()).toBe(true);

    repo.getResult = loaded(bible({ version: 5, etag: '"sb-5"' }));
    await store.load(BIBLE_SERIES_ID);
    expect(store.saving()).toBe(false);
    expect(store.bible()?.version).toBe(5);

    gate.resolve({ ok: true, value: { version: 2, etag: '"sb-2"' } });
    const result = await saving;

    expect(result.ok).toBe(true);
    expect(store.bible()?.version).toBe(5);
    expect(store.bible()?.etag).toBe('"sb-5"');
    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID, BIBLE_SERIES_ID]);
    expect(store.saving()).toBe(false);
  });
});
