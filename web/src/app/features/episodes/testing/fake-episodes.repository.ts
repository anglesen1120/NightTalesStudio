import type { CursorPage } from '@shared/util/cursor-page';
import type { EpisodeListItem, EpisodeStatus } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';

export interface EpisodeListQuery {
  readonly status?: EpisodeStatus;
  readonly seriesId?: string;
  readonly cursor?: string;
  readonly limit: number;
}

interface Pending {
  readonly query: EpisodeListQuery;
  readonly resolve: (page: CursorPage<EpisodeListItem>) => void;
  readonly reject: (error: unknown) => void;
}

/**
 * Hand-written scriptable `EpisodesRepository` (README D15: no mocking library). In immediate mode (default) each call
 * answers the next scripted page/failure; with `defer()` every call stays pending until `resolveNext`/`rejectNext`,
 * which lets a spec resolve responses out of order.
 */
export class FakeEpisodesRepository extends EpisodesRepository {
  readonly calls: EpisodeListQuery[] = [];
  private readonly script: Array<CursorPage<EpisodeListItem> | { readonly error: unknown }> = [];
  private readonly pending: Pending[] = [];
  private deferred = false;

  /** Queues the answer of the next immediate call. */
  willReturn(page: CursorPage<EpisodeListItem>): this {
    this.script.push(page);
    return this;
  }

  willFail(error: unknown): this {
    this.script.push({ error });
    return this;
  }

  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  /** Resolves the oldest (or the given index of the) pending call. */
  resolveNext(page: CursorPage<EpisodeListItem>, index = 0): void {
    this.take(index).resolve(page);
  }

  rejectNext(error: unknown, index = 0): void {
    this.take(index).reject(error);
  }

  override list(q: EpisodeListQuery): Promise<CursorPage<EpisodeListItem>> {
    this.calls.push(q);
    if (this.deferred) {
      return new Promise((resolve, reject) => {
        this.pending.push({ query: q, resolve, reject });
      });
    }
    const next = this.script.shift();
    if (next === undefined) return Promise.resolve({ items: [], nextCursor: null });
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts any wire failure, incl. AppError objects
    return 'error' in next ? Promise.reject(next.error) : Promise.resolve(next);
  }

  private take(index: number): Pending {
    const [taken] = this.pending.splice(index, 1);
    if (taken === undefined) throw new Error('no pending call');
    return taken;
  }
}

/** A list row with defaults; `n` makes the id/title unique and ordered. */
export function episodeItem(n: number, overrides: Partial<EpisodeListItem> = {}): EpisodeListItem {
  return {
    id: `ep-${String(n)}`,
    seriesId: '11111111-1111-4111-8111-111111111111',
    title: `Episode ${String(n)}`,
    status: 'draft',
    lengthTier: 'standard',
    createdAt: '2026-10-01T10:00:00Z',
    updatedAt: '2026-10-02T10:00:00Z',
    etag: `"ep-${String(n)}"`,
    ...overrides,
  };
}
