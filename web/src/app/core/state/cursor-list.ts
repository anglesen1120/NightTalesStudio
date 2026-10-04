import { type Signal, computed, inject, signal } from '@angular/core';
import { type AppError, AppErrorMapper } from '@core/errors';
import { type CursorPage, type CursorPageQuery, cursorPage } from '@shared/util/cursor-page';

export interface CursorListOptions<T> {
  /** Loads one page. Receives the validated query (`limit` in 1..100, `cursor` absent on the first page). */
  readonly fetchPage: (query: CursorPageQuery) => Promise<CursorPage<T>>;
  /** Page size of every request; validated once at creation (RangeError outside 1..100), before any call. */
  readonly initialLimit: number;
  /**
   * Turns a rejection into an `AppError`. Optional: by default an `AppError` passes through and anything else goes
   * through `AppErrorMapper`, which is resolved at creation — so without this option `cursorList` must be created in
   * an injection context (a field initializer or `runInInjectionContext`).
   */
  readonly mapError?: (error: unknown) => AppError;
}

export interface CursorList<T> {
  readonly items: Signal<readonly T[]>;
  readonly nextCursor: Signal<string | null>;
  readonly loading: Signal<boolean>;
  readonly error: Signal<AppError | null>;
  /** `false` once a page came back with `nextCursor === null`; `true` before the first load. */
  readonly hasMore: Signal<boolean>;
  /** Loads the next page. Ignored while a request is in flight and after the last page. Never rejects. */
  loadMore(): Promise<void>;
}

function isAppError(value: unknown): value is AppError {
  if (typeof value !== 'object' || value === null) return false;
  const candidate = value as Partial<Record<keyof AppError, unknown>>;
  return (
    typeof candidate.code === 'string' &&
    typeof candidate.status === 'number' &&
    typeof candidate.kind === 'string' &&
    typeof candidate.fieldErrors === 'object'
  );
}

function defaultMapError(): (error: unknown) => AppError {
  const mapper = inject(AppErrorMapper);
  return (error) => (isAppError(error) ? error : mapper.from(error));
}

/**
 * Append-only cursor pagination state (WR4). Nothing is requested until the caller invokes `loadMore()`; one request
 * is in flight at a time; every page produces a new `items` array (never mutated); a failure keeps the loaded items,
 * sets `error` and clears `loading`, and the next `loadMore()` retries the same cursor.
 */
export function cursorList<T>(options: CursorListOptions<T>): CursorList<T> {
  cursorPage(options.initialLimit); // fail fast on a bad limit, before any request
  const mapError = options.mapError ?? defaultMapError();

  const items = signal<readonly T[]>([]);
  const nextCursor = signal<string | null>(null);
  const exhausted = signal(false);
  const loading = signal(false);
  const error = signal<AppError | null>(null);
  const hasMore = computed(() => !exhausted());

  async function loadMore(): Promise<void> {
    if (loading() || exhausted()) return;
    loading.set(true);
    error.set(null);
    try {
      const page = await options.fetchPage(cursorPage(options.initialLimit, nextCursor()));
      items.update((current) => [...current, ...page.items]);
      nextCursor.set(page.nextCursor);
      exhausted.set(page.nextCursor === null);
    } catch (caught) {
      error.set(mapError(caught));
    } finally {
      loading.set(false);
    }
  }

  return {
    items: items.asReadonly(),
    nextCursor: nextCursor.asReadonly(),
    loading: loading.asReadonly(),
    error: error.asReadonly(),
    hasMore,
    loadMore,
  };
}
