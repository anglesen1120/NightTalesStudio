import { DestroyRef, Injectable, computed, inject, signal, untracked } from '@angular/core';
import { type AppError, AppErrorMapper } from '@core/errors';
import { SessionStore } from '@core/session';
import { type CursorList, cursorList } from '@core/state';
import type { CursorPage, CursorPageQuery } from '@shared/util/cursor-page';
import type { EpisodeListItem, EpisodeStatus, SeriesOption } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';

const PAGE_SIZE = 20;

interface Filters {
  readonly status: EpisodeStatus | null;
  readonly seriesId: string | null;
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

/** A 400 on the `cursor` field: the opaque cursor expired or is invalid. Re-sending it can never succeed. */
function isCursorError(error: AppError | null): boolean {
  return error !== null && error.status === 400 && error.fieldErrors['cursor'] !== undefined;
}

/** A read-only list that never requests anything: the state before `init()` and the frozen state after retirement. */
function inertList(items: readonly EpisodeListItem[], nextCursor: string | null, error: AppError | null): CursorList<EpisodeListItem> {
  return {
    items: signal(items).asReadonly(),
    nextCursor: signal(nextCursor).asReadonly(),
    loading: signal(false).asReadonly(),
    error: signal(error).asReadonly(),
    hasMore: signal(false).asReadonly(),
    loadMore: () => Promise.resolve(),
  };
}

/**
 * State of the episode list page (provided by the page component, never root: it dies with the page).
 *
 * Staleness (README §3.1): every filter state / reset gets its own `cursorList` instance, and only the current
 * instance is read by the public signals, so a response of an older instance can never reach them. Setters coalesce
 * on the microtask queue (last state wins, one request). When the component is destroyed, or a response arrives after
 * the session changed (`SessionStore.generation`), the visible state is frozen into an inert snapshot and the late
 * result is dropped. A session change with no response in flight is noticed on the next call or response; the router
 * leaves the page on session end. No timers, no polling.
 */
@Injectable()
export class EpisodeListStore {
  private readonly repo = inject(EpisodesRepository);
  private readonly seriesRepo = inject(SeriesOptionsRepository);
  private readonly session = inject(SessionStore);
  private readonly mapper = inject(AppErrorMapper);

  private readonly statusState = signal<EpisodeStatus | null>(null);
  private readonly seriesState = signal<string | null>(null);
  private readonly seriesOptionItems = signal<readonly SeriesOption[]>([]);
  private readonly seriesStatus = signal<'loading' | 'ready' | 'unavailable'>('loading');
  private readonly seriesBounded = signal(false);
  private readonly list = signal<CursorList<EpisodeListItem>>(inertList([], null, null));
  private readonly started = signal(false);

  private applied: Filters | null = null;
  private restartToken = 0;
  private seriesRequested = false;
  private destroyed = false;

  readonly statusFilter = this.statusState.asReadonly();
  readonly seriesFilter = this.seriesState.asReadonly();
  readonly seriesOptions = this.seriesOptionItems.asReadonly();
  readonly seriesOptionsState = this.seriesStatus.asReadonly();
  readonly seriesOptionsBounded = this.seriesBounded.asReadonly();

  readonly items = computed(() => this.list().items());
  readonly error = computed(() => this.list().error());
  /** First page in flight: nothing loaded yet and no cursor to continue from. */
  readonly loading = computed(() => {
    const list = this.list();
    return list.loading() && list.nextCursor() === null && list.items().length === 0;
  });
  readonly loadingMore = computed(() => this.list().loading() && !this.loading());
  /** A cursor remains. An empty page that still carries a cursor keeps this true; a cursor error ends paging. */
  readonly hasMore = computed(() => this.list().nextCursor() !== null && !isCursorError(this.error()));
  readonly isEmpty = computed(
    () => this.started() && !this.loading() && this.error() === null && this.items().length === 0 && this.list().nextCursor() === null,
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.restartToken++;
      this.retire(null);
    });
  }

  /** Sets the filters (already validated by the page), loads the series options once and the first page. */
  init(filters: Filters): void {
    this.statusState.set(filters.status);
    this.seriesState.set(filters.seriesId);
    void this.loadSeriesOptions();
    void this.restart();
  }

  /** `'reset'` restarts from page 1 under a new instance; `'more'` appends the next page (no-op while loading, at the end, or after a cursor error). */
  load(direction: 'reset' | 'more' = 'reset'): Promise<void> {
    if (direction === 'reset') return this.restart();
    if (this.destroyed || !this.hasMore() || this.list().loading()) return Promise.resolve();
    return this.list().loadMore();
  }

  setStatus(value: EpisodeStatus | null): void {
    this.statusState.set(value);
    this.scheduleRestart();
  }

  setSeries(value: string | null): void {
    this.seriesState.set(value);
    this.scheduleRestart();
  }

  /** A param-only route change on a reused component: one request, and none when nothing differs from what is loaded. */
  setFilters(filters: Filters): void {
    this.statusState.set(filters.status);
    this.seriesState.set(filters.seriesId);
    this.scheduleRestart();
  }

  /** A cursor error restarts from page 1 (the bad cursor is never re-sent); any other error re-requests the same page. One request either way. */
  retry(): Promise<void> {
    if (isCursorError(this.error())) return this.restart();
    if (this.destroyed || this.list().loading()) return Promise.resolve();
    return this.list().loadMore();
  }

  private scheduleRestart(): void {
    const token = ++this.restartToken;
    queueMicrotask(() => {
      if (token !== this.restartToken) return;
      const applied = this.applied;
      if (applied !== null && applied.status === this.statusState() && applied.seriesId === this.seriesState()) return;
      void this.restart();
    });
  }

  private restart(): Promise<void> {
    this.restartToken++;
    if (this.destroyed) return Promise.resolve();
    const filters: Filters = { status: this.statusState(), seriesId: this.seriesState() };
    this.applied = filters;
    const owner: { list: CursorList<EpisodeListItem> | null } = { list: null };
    const next = cursorList<EpisodeListItem>({
      initialLimit: PAGE_SIZE,
      mapError: (caught) => (isAppError(caught) ? caught : this.mapper.from(caught)),
      fetchPage: this.fetchPage(filters, this.session.generation(), owner),
    });
    owner.list = next;
    this.list.set(next);
    this.started.set(true);
    return next.loadMore();
  }

  private fetchPage(
    filters: Filters,
    sessionGeneration: number,
    owner: { list: CursorList<EpisodeListItem> | null },
  ): (query: CursorPageQuery) => Promise<CursorPage<EpisodeListItem>> {
    const stale = (): boolean => this.destroyed || this.session.generation() !== sessionGeneration;
    return async (query) => {
      let page: CursorPage<EpisodeListItem>;
      try {
        page = await this.repo.list({
          ...(filters.status === null ? {} : { status: filters.status }),
          ...(filters.seriesId === null ? {} : { seriesId: filters.seriesId }),
          ...query,
        });
      } catch (caught) {
        if (!stale()) throw caught;
        this.retire(owner.list);
        return { items: [], nextCursor: null };
      }
      if (stale()) {
        this.retire(owner.list);
        return { items: [], nextCursor: null };
      }
      return page;
    };
  }

  private async loadSeriesOptions(): Promise<void> {
    if (this.seriesRequested) return;
    this.seriesRequested = true;
    const sessionGeneration = this.session.generation();
    try {
      const page = await this.seriesRepo.list();
      if (this.destroyed || this.session.generation() !== sessionGeneration) return;
      this.seriesOptionItems.set(page.items);
      this.seriesBounded.set(page.bounded);
      this.seriesStatus.set('ready');
    } catch {
      if (this.destroyed || this.session.generation() !== sessionGeneration) return;
      this.seriesStatus.set('unavailable');
    }
  }

  /**
   * Freezes what is visible with no cursor (no dead "Load more"). With an `owner`, only retires when that instance is
   * still the current one, so a stale instance's late result can never freeze a live replacement.
   */
  private retire(owner: CursorList<EpisodeListItem> | null): void {
    const current = untracked(this.list);
    if (owner !== null && owner !== current) return;
    this.list.set(inertList(untracked(current.items), null, untracked(current.error)));
  }
}
