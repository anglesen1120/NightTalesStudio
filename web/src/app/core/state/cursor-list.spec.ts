import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import type { CursorPage, CursorPageQuery } from '@shared/util/cursor-page';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { cursorList } from './cursor-list';

/** An `AppError` that is also an `Error`, so it can be thrown and rejected like a real failure. */
class FakeAppError extends Error implements AppError {
  readonly code = 'server_error';
  readonly status = 500;
  readonly kind = 'server';
  readonly fieldErrors = {};
}

const SERVER_ERROR = new FakeAppError('server');

interface Deferred {
  query: CursorPageQuery;
  resolve: (page: CursorPage<string>) => void;
  reject: (reason: unknown) => void;
}

/** Hand-written fake: every call parks until the test settles it, and is recorded. */
class FakePages {
  readonly calls: Deferred[] = [];
  call(index: number): Deferred {
    const found = this.calls[index];
    if (found === undefined) throw new Error(`no fetchPage call #${String(index)}`);
    return found;
  }

  readonly fetchPage = (query: CursorPageQuery): Promise<CursorPage<string>> =>
    new Promise<CursorPage<string>>((resolve, reject) => {
      this.calls.push({ query, resolve, reject });
    });
}

function setup(limit = 2) {
  const pages = new FakePages();
  const list = cursorList<string>({ fetchPage: pages.fetchPage, initialLimit: limit, mapError: (e) => e as AppError });
  return { pages, list };
}

describe('cursorList', () => {
  it('cursorList_NoRequestWithoutLoadMore', async () => {
    const { pages, list } = setup();
    await Promise.resolve();
    expect(pages.calls).toHaveLength(0);
    expect(list.items()).toStrictEqual([]);
    expect(list.loading()).toBe(false);
    expect(list.hasMore()).toBe(true);
    expect(list.nextCursor()).toBeNull();
  });

  it('cursorList_FirstLoadMore_AppendsAndUpdatesNextCursor', async () => {
    const { pages, list } = setup();
    const first = list.loadMore();
    expect(list.loading()).toBe(true);
    expect(pages.call(0).query).toStrictEqual({ limit: 2 });
    pages.call(0).resolve({ items: ['a', 'b'], nextCursor: 'c1' });
    await first;
    expect(list.items()).toStrictEqual(['a', 'b']);
    expect(list.nextCursor()).toBe('c1');
    expect(list.hasMore()).toBe(true);
    expect(list.loading()).toBe(false);

    const before = list.items();
    const second = list.loadMore();
    expect(pages.call(1).query).toStrictEqual({ cursor: 'c1', limit: 2 });
    pages.call(1).resolve({ items: ['c'], nextCursor: null });
    await second;
    expect(list.items()).toStrictEqual(['a', 'b', 'c']);
    expect(list.items()).not.toBe(before); // a new array, never mutated
    expect(before).toStrictEqual(['a', 'b']);
  });

  it('cursorList_ReentrantLoadMore_IssuesExactlyOneRequest', async () => {
    const { pages, list } = setup();
    const a = list.loadMore();
    const b = list.loadMore();
    const c = list.loadMore();
    expect(pages.calls).toHaveLength(1);
    pages.call(0).resolve({ items: ['a'], nextCursor: 'c1' });
    await Promise.all([a, b, c]);
    expect(list.items()).toStrictEqual(['a']);
    expect(pages.calls).toHaveLength(1);
  });

  it('cursorList_LastPage_HasMoreFalseAndFurtherCallsDoNothing', async () => {
    const { pages, list } = setup();
    const load = list.loadMore();
    pages.call(0).resolve({ items: ['a'], nextCursor: null });
    await load;
    expect(list.hasMore()).toBe(false);
    expect(list.nextCursor()).toBeNull();
    await list.loadMore();
    await list.loadMore();
    expect(pages.calls).toHaveLength(1);
    expect(list.items()).toStrictEqual(['a']);
  });

  it('cursorList_AppError_KeepsItemsSetsErrorClearsLoadingAndRetriesSameCursor', async () => {
    const { pages, list } = setup();
    const first = list.loadMore();
    pages.call(0).resolve({ items: ['a'], nextCursor: 'c1' });
    await first;

    const failing = list.loadMore();
    pages.call(1).reject(SERVER_ERROR);
    await failing;
    expect(list.items()).toStrictEqual(['a']);
    expect(list.error()).toBe(SERVER_ERROR);
    expect(list.loading()).toBe(false);
    expect(list.hasMore()).toBe(true);
    expect(list.nextCursor()).toBe('c1');

    const retry = list.loadMore();
    expect(list.error()).toBeNull();
    expect(pages.call(2).query).toStrictEqual({ cursor: 'c1', limit: 2 });
    pages.call(2).resolve({ items: ['b'], nextCursor: null });
    await retry;
    expect(list.items()).toStrictEqual(['a', 'b']);
  });

  it('cursorList_SynchronousFetchThrow_SurfacesAsErrorAndClearsLoading', async () => {
    const list = cursorList<string>({
      fetchPage: () => {
        throw SERVER_ERROR;
      },
      initialLimit: 5,
      mapError: (e) => e as AppError,
    });
    await list.loadMore();
    expect(list.error()).toBe(SERVER_ERROR);
    expect(list.loading()).toBe(false);
  });

  it('cursorList_LimitAbove100_IsRejectedAtCreationBeforeAnyCall', () => {
    const pages = new FakePages();
    expect(() => cursorList<string>({ fetchPage: pages.fetchPage, initialLimit: 101, mapError: (e) => e as AppError })).toThrow(
      RangeError,
    );
    expect(pages.calls).toHaveLength(0);
  });

  it('cursorList_DefaultMapper_PassesAppErrorAndMapsHttpFailures', async () => {
    TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS] });
    const failures: unknown[] = [SERVER_ERROR, new HttpErrorResponse({ status: 503 })];
    // `inject()` only works synchronously, so both lists are created before anything is awaited.
    const lists = TestBed.runInInjectionContext(() =>
      failures.map((failure) => cursorList<string>({ fetchPage: () =>
            new Promise<CursorPage<string>>((_, reject) => {
              // HttpClient rejects with an HttpErrorResponse, which is not an Error subclass.
              // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors
              reject(failure);
            }), initialLimit: 3 })),
    );
    await Promise.all(lists.map((list) => list.loadMore()));
    const results = lists.map((list) => list.error());
    expect(results[0]).toBe(SERVER_ERROR);
    expect(results[1]?.status).toBe(503);
    expect(results[1]?.kind).toBe('server');
  });
});
