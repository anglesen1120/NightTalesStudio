import { TestBed } from '@angular/core/testing';
import type { AppError } from '@core/errors';
import type { CursorPage, CursorPageQuery } from '@shared/util/cursor-page';
import { describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { cursorList } from './cursor-list';

// QA (10.2.6-T1): adversarial inputs around cursorList — bad limits, synchronous throws, malformed pages, retry cursor.

const serverError: AppError = { code: 'server_error', status: 500, kind: 'server', fieldErrors: {} };
const mapError = (e: unknown): AppError => (typeof e === 'object' && e !== null && 'code' in e ? (e as AppError) : serverError);

function scripted(pages: readonly (CursorPage<string> | Error | AppError)[]) {
  const queries: CursorPageQuery[] = [];
  let index = 0;
  const fetchPage = (query: CursorPageQuery): Promise<CursorPage<string>> => {
    queries.push(query);
    const next = pages[index++];
    if (next === undefined) return Promise.reject(new Error('unscripted call'));
    if ('items' in next) return Promise.resolve(next);
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- AppError is a plain object by contract
    return Promise.reject(next);
  };
  return { queries, fetchPage };
}

describe('cursorList (QA)', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: ZONELESS_TEST_PROVIDERS }));

  it.each([0, 101, 1.5, -3, Number.NaN])('creation with initialLimit %s throws before any request', (limit) => {
    const { queries, fetchPage } = scripted([]);
    expect(() => cursorList<string>({ fetchPage, initialLimit: limit, mapError })).toThrow(RangeError);
    expect(queries).toHaveLength(0);
  });

  it('a synchronously throwing fetchPage surfaces as error and clears loading (never rejects)', async () => {
    const list = cursorList<string>({
      fetchPage: () => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error -- AppError is a plain object by contract
        throw serverError;
      },
      initialLimit: 5,
      mapError,
    });
    await expect(list.loadMore()).resolves.toBeUndefined();
    expect(list.error()).toBe(serverError);
    expect(list.loading()).toBe(false);
    expect(list.items()).toStrictEqual([]);
  });

  it('a failed page keeps loaded items, retries the SAME cursor, and clears the error on success', async () => {
    const { queries, fetchPage } = scripted([
      { items: ['a'], nextCursor: 'c1' },
      serverError,
      { items: ['b'], nextCursor: null },
    ]);
    const list = cursorList<string>({ fetchPage, initialLimit: 3, mapError });
    await list.loadMore();
    await list.loadMore();
    expect(list.items()).toStrictEqual(['a']);
    expect(list.error()).toBe(serverError);
    expect(list.hasMore()).toBe(true);
    await list.loadMore();
    expect(queries.map((q) => q.cursor)).toStrictEqual([undefined, 'c1', 'c1']);
    expect(list.items()).toStrictEqual(['a', 'b']);
    expect(list.error()).toBeNull();
    expect(list.hasMore()).toBe(false);
  });

  it('after the last page further loadMore calls issue no request', async () => {
    const { queries, fetchPage } = scripted([{ items: ['a'], nextCursor: null }]);
    const list = cursorList<string>({ fetchPage, initialLimit: 3, mapError });
    await list.loadMore();
    await list.loadMore();
    await list.loadMore();
    expect(queries).toHaveLength(1);
  });

  it('a malformed page (items missing) is reported as an error, keeps prior items and does not wedge loading', async () => {
    const { fetchPage } = scripted([
      { items: ['a'], nextCursor: 'c1' },
      { nextCursor: null } as unknown as CursorPage<string>,
    ]);
    const list = cursorList<string>({ fetchPage, initialLimit: 3, mapError });
    await list.loadMore();
    await list.loadMore();
    expect(list.items()).toStrictEqual(['a']);
    expect(list.error()).not.toBeNull();
    expect(list.loading()).toBe(false);
  });

  it('an empty-string nextCursor must not restart pagination from the first page (duplicate items)', async () => {
    // Wire contract: `null` is the last page; any other string is an opaque continuation token. `''` is degenerate:
    // it is treated as exhausted, so the helper never requests again (and never restarts from page 1).
    const { queries, fetchPage } = scripted([
      { items: ['a'], nextCursor: '' },
      { items: ['a'], nextCursor: null },
    ]);
    const list = cursorList<string>({ fetchPage, initialLimit: 3, mapError });
    await list.loadMore();
    await list.loadMore();
    expect(queries).toHaveLength(1);
    expect(list.items()).toStrictEqual(['a']);
    expect(list.nextCursor()).toBeNull();
    expect(list.hasMore()).toBe(false);
  });

  it('items is a fresh array per page and the server array is never aliased/mutated', async () => {
    const serverItems = ['a'];
    const { fetchPage } = scripted([
      { items: serverItems, nextCursor: 'c' },
      { items: ['b'], nextCursor: null },
    ]);
    const list = cursorList<string>({ fetchPage, initialLimit: 3, mapError });
    await list.loadMore();
    const first = list.items();
    expect(first).not.toBe(serverItems);
    await list.loadMore();
    expect(first).toStrictEqual(['a']);
    expect(serverItems).toStrictEqual(['a']);
  });

  it('a hundred re-entrant loadMore calls issue exactly one request', async () => {
    const { queries, fetchPage } = scripted([{ items: ['a'], nextCursor: null }]);
    const list = cursorList<string>({ fetchPage, initialLimit: 3, mapError });
    await Promise.all(Array.from({ length: 100 }, () => list.loadMore()));
    expect(queries).toHaveLength(1);
  });
});
