import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Injectable, Injector, computed, inject, runInInjectionContext, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { cursorList, type CursorList } from '@core/state/cursor-list';
import type { CursorPage, CursorPageQuery } from '@shared/util/cursor-page';
import type { SeriesListItem } from '../domain/series.model';
import { SeriesRepository } from './series.repository';

/** The page size of `GET /api/v1/series` (plan §4 U2: 25 rows) — inside the server's 1..100 bound (09.5.1-T1). */
export const SERIES_LIST_PAGE_SIZE = 25;

/** The `errors.validation_failed` leaf the list shows when the server refuses the query itself. */
const VALIDATION_FAILED_KEY = 'errors.validation_failed';

/** Announced when an archive failed and the row came back (10.4.2-T1 archive row). */
const ARCHIVE_FAILED_KEY = 'series.archive.failed';

/**
 * State of `/series` (README D2: application layer, page-scoped `@Injectable()` — the list page provides it, never
 * `providedIn: 'root'`).
 *
 * - One `GET /series` per `load()` and one per `loadMore()`; `cursorList` (core/state) is the single-flight guard and
 *   the only writer of `items`, so a click while a page is in flight issues no second request.
 * - `load()` builds a fresh `cursorList`, which is also how a superseded page is discarded: a late answer writes into
 *   the abandoned list, which no signal exposes any more. A reply that arrives after the session generation changed
 *   is dropped by `fetchPage` before it can commit anything.
 * - Archive is optimistic: the row leaves `items` at once, comes back on failure with a polite
 *   `series.archive.failed` announcement, and is kept removed when the server says the row is already gone
 *   (`not_found`) or already archived (`series_archived`) — both mean the row is archived.
 */
@Injectable()
export class SeriesListStore {
  private readonly repository = inject(SeriesRepository);
  private readonly session = inject(SessionStore);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly transloco = inject(TranslocoService);
  private readonly injector = inject(Injector);

  private readonly pages = signal<CursorList<SeriesListItem>>(this.createPageList());
  private readonly removedIds = signal<ReadonlySet<string>>(new Set<string>());
  private generation = 0;

  /** Whether archived series are requested as well (off by default: the list shows the active ones). */
  readonly includeArchived = signal(false);
  /** The id of the row an archive request is in flight for, or `null`; one archive at a time. */
  readonly archivingId = signal<string | null>(null);
  /** `errors.validation_failed` while the server refused the list query; the page shows it with the list error. */
  readonly fieldError = signal<string | null>(null);

  readonly items = computed(() => this.pages().items().filter((item) => !this.removedIds().has(item.id)));
  readonly hasMore = computed(() => this.pages().hasMore());
  readonly loading = computed(() => this.pages().loading() && this.pages().items().length === 0);
  readonly loadingMore = computed(() => this.pages().loading() && this.pages().items().length > 0);
  readonly error = computed<AppError | null>(() => this.pages().error());
  /** Seconds to wait after a 429 (from `Retry-After`), `null` otherwise; feeds `RetryCountdown`. */
  readonly retryAfterSec = computed<number | null>(() => {
    const error = this.error();
    return error !== null && error.kind === 'rate_limit' ? (error.retryAfterSec ?? null) : null;
  });

  /** Loads the first page, replacing whatever the list held (used on open, on retry and on the archived toggle). */
  async load(): Promise<void> {
    this.generation += 1;
    this.removedIds.set(new Set<string>());
    this.fieldError.set(null);
    const list = this.createPageList();
    this.pages.set(list);
    await list.loadMore();
  }

  /** Appends the next page; a no-op while a page is in flight or after the last page. */
  async loadMore(): Promise<void> {
    await this.pages().loadMore();
  }

  /**
   * Retries the failed request. A `validation_failed` list failure is a refused query — most often a cursor the
   * server no longer accepts (09.4.1 §5) — so the cursor is dropped and the first page is loaded again through a
   * fresh list, instead of appending page 1 to pages that are already there.
   */
  async retry(): Promise<void> {
    const refused = this.error()?.code === 'validation_failed';
    this.fieldError.set(null);
    if (refused) {
      await this.load();
      return;
    }
    await this.pages().loadMore();
  }

  /** Flips the archived filter and reloads the first page (the filter is part of the query, so the list restarts). */
  async toggleIncludeArchived(): Promise<void> {
    this.includeArchived.update((value) => !value);
    await this.load();
  }

  /**
   * Archives a series (`DELETE /series/{id}`) and reports whether the row is gone. The row leaves the list before
   * the request; a real failure restores it and announces `series.archive.failed`. A `not_found` or
   * `series_archived` answer is a success: the row is archived either way (plan §4 U3).
   */
  async archive(id: string): Promise<boolean> {
    if (this.archivingId() !== null) return false;
    this.archivingId.set(id);
    this.removedIds.update((ids) => new Set(ids).add(id));
    const result = await this.repository.archive(id);
    this.archivingId.set(null);
    if (result.ok) return true;
    if (result.error.status === 404 || result.error.code === 'series_archived') return true;
    this.removedIds.update((ids) => {
      const restored = new Set(ids);
      restored.delete(id);
      return restored;
    });
    void this.announce(ARCHIVE_FAILED_KEY);
    return false;
  }

  /**
   * `cursorList` is created here rather than in a field initializer because `load()` replaces it; the documented
   * `runInInjectionContext` route gives its default error mapper a legal `inject()` (cursor-list.ts).
   */
  private createPageList(): CursorList<SeriesListItem> {
    return runInInjectionContext(this.injector, () =>
      cursorList<SeriesListItem>({ initialLimit: SERIES_LIST_PAGE_SIZE, fetchPage: (query) => this.fetchPage(query) }),
    );
  }

  private async fetchPage(query: CursorPageQuery): Promise<CursorPage<SeriesListItem>> {
    const requestGeneration = this.generation;
    const sessionGeneration = this.session.generation();
    const includeArchived = this.includeArchived();
    const result = await this.repository.list({ cursor: query.cursor ?? null, limit: query.limit, includeArchived });
    if (requestGeneration !== this.generation || sessionGeneration !== this.session.generation()) {
      // A late answer nobody is waiting for: append nothing and leave the cursor exactly where it was.
      return { items: [], nextCursor: query.cursor ?? null };
    }
    if (result.ok) return { items: result.value.items, nextCursor: result.value.nextCursor };
    if (result.error.code === 'validation_failed') {
      // The server refused the query itself (a tampered or stale cursor, 09.4.1 §5 400). `retry()` drops the cursor
      // and restarts from the first page; here the failure is surfaced like any other, so nothing is appended twice.
      this.fieldError.set(VALIDATION_FAILED_KEY);
    }
    // The `cursorList` `fetchPage` contract rejects with an `AppError` (`cursor-list.ts` `isAppError` passes it
    // through, so the page keeps the real code / traceId / retryAfterSec). Same rule and reason as the repository
    // ports that do the same: core/realtime/data-access/hub-ticket.http-repository.ts:29,
    // core/session/data-access/session.repository.ts:75.
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- the fetchPage port rejects with an AppError
    throw result.error;
  }

  private async announce(key: string): Promise<void> {
    const message = await firstValueFrom(this.transloco.selectTranslate<string>(key));
    await this.announcer.announce(message, 'polite');
  }
}
