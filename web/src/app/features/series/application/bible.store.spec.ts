import { TestBed } from '@angular/core/testing';
import type { Result } from '@core/errors';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Bible } from '../domain/bible.model';
import {
  BIBLE_CONFLICT,
  BIBLE_INVALID,
  BIBLE_NOT_FOUND,
  BIBLE_PROPOSAL_GONE,
  BIBLE_SERIES_ID,
  FakeBibleRepository,
  NETWORK_ERROR,
  OTHER_EPISODE_ID,
  PROPOSING_EPISODE_ID,
  bible,
  bibleContent,
  bibleProposal,
} from '../testing/fake-bible.repository';
import { BibleRepository } from './bible.repository';
import { BibleStore } from './bible.store';

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

interface Harness {
  readonly store: BibleStore;
  readonly repo: FakeBibleRepository;
}

function setup(): Harness {
  const repo = new FakeBibleRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: BibleRepository, useValue: repo },
      BibleStore,
    ],
  });
  return { store: TestBed.inject(BibleStore), repo };
}

function loaded(value: Bible): Result<Bible> {
  return { ok: true, value };
}

/** Lets queued microtasks and a resolved title promise settle before a state assertion. */
function flush(): Promise<void> {
  return new Promise((settle) => setTimeout(settle, 0));
}

describe('BibleStore', () => {
  it('Load_StoresTheBibleWithoutIssuingATitleRequestForAProposallessBible', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible());

    await store.load(BIBLE_SERIES_ID);

    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID]);
    expect(store.bible()?.version).toBe(1);
    expect(store.bible()?.etag).toBe('"sb-1"');
    expect(store.bible()?.content.narrator.persona).toBe('Giọng trầm, chậm');
    expect(store.proposal()).toBeNull();
    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
    expect(repo.titleCalls).toEqual([]);
  });

  it('Load_NotFoundAndNetwork_SurfaceThroughError', async () => {
    const { store, repo } = setup();
    repo.getResult = { ok: false, error: BIBLE_NOT_FOUND };
    await store.load(BIBLE_SERIES_ID);
    expect(store.error()?.code).toBe('not_found');
    expect(store.bible()).toBeNull();
    expect(store.loading()).toBe(false);

    repo.getResult = { ok: false, error: NETWORK_ERROR };
    await store.load(BIBLE_SERIES_ID);
    expect(store.error()?.code).toBe('network_error');
  });

  it('Load_WithAProposal_ResolvesTheTitleExactlyOnceAndNeverRetries', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible({ proposal: bibleProposal({ proposedFromEpisodeId: PROPOSING_EPISODE_ID }) }));

    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID]);
    expect(store.proposedTitle()).toBe('Tập đã duyệt');

    // A second read of the same proposal (a save/accept refetch path) asks once per episode id, never again.
    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID]);
  });

  it('Load_ProposalWithoutAnEpisode_IssuesNoTitleRequest', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible({ proposal: bibleProposal({ proposedFromEpisodeId: null }) }));

    await store.load(BIBLE_SERIES_ID);
    expect(store.proposal()?.proposedFromEpisodeId).toBeNull();
    expect(repo.titleCalls).toEqual([]);
  });

  it('Load_TitleFailureFallsBackWithoutAnErrorState', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible({ proposal: bibleProposal() }));
    repo.titleResult = null;

    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID]);
    expect(store.proposedTitle()).toBeNull();
    expect(store.error()).toBeNull();
    expect(store.bible()?.proposal).not.toBeNull();
  });

  it('Load_LateTitleOfASupersededProposal_IsDropped', async () => {
    const { store, repo } = setup();
    const staleTitle = deferred<string | null>();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({
      version: 2,
      etag: '"sb-2"',
      proposal: bibleProposal({ proposedFromEpisodeId: OTHER_EPISODE_ID }),
    }))));
    repo.scriptTitle(() => staleTitle.promise);

    const first = store.load(BIBLE_SERIES_ID);
    await Promise.resolve();
    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID, OTHER_EPISODE_ID]);
    expect(store.proposedTitle()).toBe('Tập đã duyệt');

    staleTitle.resolve('Tiêu đề cũ');
    await first;
    expect(store.proposedTitle()).toBe('Tập đã duyệt');
  });

  it('Save_SendsTheProvidedEtagAdoptsTheVersionAndRefetchesTheServerIds', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible())));
    const edited = bibleContent({ recurringCharacters: [{ name: 'Ông Tư', description: 'Hàng xóm' }] });
    repo.scriptGet(() => Promise.resolve(loaded(bible({
      version: 2,
      etag: '"sb-2"',
      content: bibleContent({ recurringCharacters: [{ id: 'ch2', name: 'Ông Tư', description: 'Hàng xóm' }] }),
    }))));
    await store.load(BIBLE_SERIES_ID);

    const result = await store.save(BIBLE_SERIES_ID, edited, '"sb-1"');

    expect(result.ok).toBe(true);
    expect(repo.saveCalls).toEqual([{ seriesId: BIBLE_SERIES_ID, content: edited, etag: '"sb-1"' }]);
    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID, BIBLE_SERIES_ID]);
    expect(store.bible()?.version).toBe(2);
    expect(store.bible()?.etag).toBe('"sb-2"');
    expect(store.bible()?.content.recurringCharacters[0]!.id).toBe('ch2');
    expect(store.saving()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('Save_SuccessfulWriteWithAFailedRefetchKeepsTheSavedContentAndVersion', async () => {
    const { store, repo } = setup();
    await store.load(BIBLE_SERIES_ID);
    repo.getResult = { ok: false, error: NETWORK_ERROR };
    const edited = bibleContent({ rules: [{ text: 'Luật đã sửa' }] });

    const result = await store.save(BIBLE_SERIES_ID, edited, '"sb-1"');

    expect(result.ok).toBe(true);
    expect(store.bible()?.content).toBe(edited);
    expect(store.bible()?.version).toBe(2);
    expect(store.bible()?.etag).toBe('"sb-2"');
    expect(store.error()).toBeNull();
    expect(store.saving()).toBe(false);
  });

  it('Save_412_SetsConflictAndLeavesTheBibleUntouched', async () => {
    const { store, repo } = setup();
    await store.load(BIBLE_SERIES_ID);
    const before = store.bible();
    repo.saveResult = { ok: false, error: BIBLE_CONFLICT };

    const result = await store.save(BIBLE_SERIES_ID, bibleContent(), '"sb-1"');

    expect(result.ok).toBe(false);
    expect(store.conflict()).toBe(true);
    expect(store.bible()).toBe(before);
    expect(store.error()).toBeNull();
    expect(store.fieldErrors()).toEqual({});
    expect(store.saving()).toBe(false);
  });

  it('Save_400_PublishesTheServerFieldErrorKeysVerbatim', async () => {
    const { store, repo } = setup();
    await store.load(BIBLE_SERIES_ID);
    repo.saveResult = { ok: false, error: BIBLE_INVALID };

    const result = await store.save(BIBLE_SERIES_ID, bibleContent(), '"sb-1"');

    expect(result.ok).toBe(false);
    expect(store.conflict()).toBe(false);
    expect(store.fieldErrors()).toEqual({
      recurring_characters: ['too_many'],
      'episode_summaries[2].episode_id': ['not_approved_episode'],
    });
    expect(store.error()?.code).toBe('bible_invalid_content');
  });

  it('Accept_RefetchesAndClearsTheProposal', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: null }))));
    await store.load(BIBLE_SERIES_ID);

    const result = await store.acceptProposal(BIBLE_SERIES_ID);

    expect(result.ok).toBe(true);
    expect(repo.acceptCalls).toEqual([BIBLE_SERIES_ID]);
    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID, BIBLE_SERIES_ID]);
    expect(store.proposal()).toBeNull();
    expect(store.bible()?.version).toBe(2);
    expect(store.committing()).toBe(false);
  });

  it('Dismiss_SuccessRefetchesWithoutAnIfMatchOrAnError', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: null }))));
    await store.load(BIBLE_SERIES_ID);

    const result = await store.dismissProposal(BIBLE_SERIES_ID);

    expect(result.ok).toBe(true);
    expect(repo.dismissCalls).toEqual([BIBLE_SERIES_ID]);
    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID, BIBLE_SERIES_ID]);
    expect(store.proposal()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('Accept_409_SetsProposalGoneAndRefetchesWithoutAnErrorState', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: null }))));
    await store.load(BIBLE_SERIES_ID);
    repo.acceptResult = { ok: false, error: BIBLE_PROPOSAL_GONE };

    const result = await store.acceptProposal(BIBLE_SERIES_ID);

    expect(result.ok).toBe(false);
    expect(store.proposalGone()).toBe(true);
    expect(store.error()).toBeNull();
    expect(repo.getCalls).toEqual([BIBLE_SERIES_ID, BIBLE_SERIES_ID]);
    expect(store.proposal()).toBeNull();
    expect(store.committing()).toBe(false);
  });

  it('Commit_SecondCallWhileCommittingIsANoOp', async () => {
    const { store, repo } = setup();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ proposal: bibleProposal() }))));
    const gate = deferred<Result<void>>();
    repo.scriptAccept(() => gate.promise);
    await store.load(BIBLE_SERIES_ID);

    const first = store.acceptProposal(BIBLE_SERIES_ID);
    const second = await store.acceptProposal(BIBLE_SERIES_ID);

    expect(second.ok).toBe(true);
    expect(repo.acceptCalls).toEqual([BIBLE_SERIES_ID]);
    expect(store.committing()).toBe(true);

    gate.resolve({ ok: true, value: undefined });
    await first;
    expect(store.committing()).toBe(false);
    expect(repo.getCalls).toHaveLength(2);
  });

  it('Reset_ClearsEverySignalAndDropsALateLoad', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible({ proposal: bibleProposal() }));
    await store.load(BIBLE_SERIES_ID);
    expect(store.bible()).not.toBeNull();

    const late = deferred<Result<Bible>>();
    repo.scriptGet(() => late.promise);
    const loading = store.load(BIBLE_SERIES_ID);
    await Promise.resolve();
    store.reset();
    late.resolve(loaded(bible({ version: 9, proposal: bibleProposal() })));
    await loading;

    expect(store.bible()).toBeNull();
    expect(store.proposal()).toBeNull();
    expect(store.loading()).toBe(false);
    expect(store.saving()).toBe(false);
    expect(store.committing()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.fieldErrors()).toEqual({});
    expect(store.conflict()).toBe(false);
    expect(store.proposedTitle()).toBeNull();
    expect(store.proposalGone()).toBe(false);
  });

  it('Load_LateReplyOfANewerRequestIsDiscarded', async () => {
    const { store, repo } = setup();
    const late = deferred<Result<Bible>>();
    repo.scriptGet(() => late.promise);
    const first = store.load(BIBLE_SERIES_ID);
    await Promise.resolve();

    repo.getResult = loaded(bible({ version: 2, etag: '"sb-2"' }));
    await store.load(BIBLE_SERIES_ID);
    late.resolve(loaded(bible({ version: 9, etag: '"sb-9"' })));
    await first;

    expect(store.bible()?.version).toBe(2);
    expect(store.loading()).toBe(false);
  });

  it('Load_TitleInFlight_PublishesTheBibleAndClearsLoadingBeforeTheTitleSettles', async () => {
    const { store, repo } = setup();
    const title = deferred<string | null>();
    repo.getResult = loaded(bible({ proposal: bibleProposal() }));
    repo.scriptTitle(() => title.promise);

    await store.load(BIBLE_SERIES_ID);

    expect(store.loading()).toBe(false);
    expect(store.bible()?.proposal).not.toBeNull();
    expect(store.proposedTitle()).toBeNull();

    title.resolve('Tập đã duyệt');
    await flush();
    expect(store.proposedTitle()).toBe('Tập đã duyệt');
  });

  it('Save_TitleInFlightAfterTheRefetch_ClearsSavingAndPublishesTheTitleLater', async () => {
    const { store, repo } = setup();
    await store.load(BIBLE_SERIES_ID);
    const title = deferred<string | null>();
    repo.scriptGet(() => Promise.resolve(loaded(bible({ version: 2, etag: '"sb-2"', proposal: bibleProposal() }))));
    repo.scriptTitle(() => title.promise);

    const result = await store.save(BIBLE_SERIES_ID, bibleContent(), '"sb-1"');

    expect(result.ok).toBe(true);
    expect(store.saving()).toBe(false);
    expect(store.bible()?.version).toBe(2);
    expect(store.proposedTitle()).toBeNull();

    title.resolve('Tập đã duyệt');
    await flush();
    expect(store.proposedTitle()).toBe('Tập đã duyệt');
  });

  it('Commit_TitleInFlightOfTheRefetchedProposal_ClearsCommittingAndDropsThePreviousTitle', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible({ proposal: bibleProposal() }));
    await store.load(BIBLE_SERIES_ID);
    expect(store.proposedTitle()).toBe('Tập đã duyệt');
    const title = deferred<string | null>();
    repo.scriptGet(() =>
      Promise.resolve(
        loaded(bible({ version: 2, etag: '"sb-2"', proposal: bibleProposal({ proposedFromEpisodeId: OTHER_EPISODE_ID }) })),
      ),
    );
    repo.scriptTitle(() => title.promise);

    const result = await store.acceptProposal(BIBLE_SERIES_ID);

    expect(result.ok).toBe(true);
    expect(store.committing()).toBe(false);
    expect(store.proposedTitle()).toBeNull();

    title.resolve('Tập mới');
    await flush();
    expect(store.proposedTitle()).toBe('Tập mới');
  });

  it('Load_TitleOfTheSeriesLeftBehind_IsDroppedWhileTheNewSeriesTitleLands', async () => {
    const { store, repo } = setup();
    const staleTitle = deferred<string | null>();
    const newTitle = deferred<string | null>();
    repo.getResult = loaded(bible({ proposal: bibleProposal() }));
    repo.scriptTitle(() => staleTitle.promise);
    await store.load(BIBLE_SERIES_ID);
    repo.scriptGet(() =>
      Promise.resolve(
        loaded(bible({ seriesId: 'series-other', proposal: bibleProposal({ proposedFromEpisodeId: OTHER_EPISODE_ID }) })),
      ),
    );
    repo.scriptTitle(() => newTitle.promise);
    await store.load('series-other');

    staleTitle.resolve('Tiêu đề cũ');
    await flush();
    expect(store.proposedTitle()).toBeNull();

    newTitle.resolve('Tiêu đề mới');
    await flush();
    expect(store.proposedTitle()).toBe('Tiêu đề mới');
  });

  it('Load_TitleLookupThatRejects_StaysSilentAndIsNeverRetried', async () => {
    const { store, repo } = setup();
    repo.getResult = loaded(bible({ proposal: bibleProposal() }));
    repo.scriptTitle(() => Promise.reject(new Error('title unavailable')));

    await store.load(BIBLE_SERIES_ID);
    await flush();

    expect(store.loading()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.proposedTitle()).toBeNull();

    await store.load(BIBLE_SERIES_ID);
    expect(repo.titleCalls).toEqual([PROPOSING_EPISODE_ID]);
  });

  it('Save_EmptyWriteEtagIsReplacedByTheVersionTagSoTheNextSaveNeverSendsAnEmptyIfMatch', async () => {
    const { store, repo } = setup();
    await store.load(BIBLE_SERIES_ID);
    repo.saveResult = { ok: true, value: { version: 2, etag: '' } };
    repo.getResult = { ok: false, error: NETWORK_ERROR };

    await store.save(BIBLE_SERIES_ID, bibleContent(), '"sb-1"');
    expect(store.bible()?.etag).toBe('"sb-2"');

    repo.saveResult = { ok: true, value: { version: 3, etag: '"sb-3"' } };
    await store.save(BIBLE_SERIES_ID, bibleContent(), store.bible()!.etag);

    expect(repo.saveCalls.map((call) => call.etag)).toEqual(['"sb-1"', '"sb-2"']);
    expect(store.bible()?.etag).toBe('"sb-3"');
  });
});
