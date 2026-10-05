/** The server caps every cursor-paged list at this many items (09.5.1-T1); a larger `limit` is a 400 on the wire. */
export const CURSOR_PAGE_MAX_LIMIT = 100;

/** The wire shape of every `GET /…?cursor=`-style list response (09.5.1-T1). `nextCursor === null` is the last page. */
export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
}

/** The query members of one page request; `cursor` is absent on the first page. */
export interface CursorPageQuery {
  readonly cursor?: string;
  readonly limit: number;
}

/**
 * Builds the query of one page request and rejects an invalid `limit` (not an integer in 1..100) with a `RangeError`
 * **before** any request exists, so a bad size never reaches the wire. A `null`/empty cursor means the first page and
 * yields no `cursor` member. Pure: no Angular, no `core` import (shared/util stays a leaf).
 */
export function cursorPage(limit: number, cursor: string | null = null): CursorPageQuery {
  if (!Number.isInteger(limit) || limit < 1 || limit > CURSOR_PAGE_MAX_LIMIT) {
    throw new RangeError(`limit must be an integer between 1 and ${String(CURSOR_PAGE_MAX_LIMIT)}`);
  }
  return cursor === null || cursor === '' ? { limit } : { cursor, limit };
}
