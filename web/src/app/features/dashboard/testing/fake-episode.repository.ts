import type { CursorPage } from '@shared/util/cursor-page';
import { EpisodesRepository, type EpisodeListItem, type EpisodeStatus } from '@features/episodes';

/** The query the hand-written fake logs; the store's five calls are asserted through `calls`. */
export interface DashboardListQuery {
  readonly status?: EpisodeStatus;
  readonly limit: number;
}

interface Pending {
  readonly query: DashboardListQuery;
  readonly resolve: (page: CursorPage<EpisodeListItem>) => void;
  readonly reject: (error: unknown) => void;
}

/**
 * Hand-written scriptable `EpisodesRepository` for the dashboard (README D15: no mocking library). Answers each call
 * from the page canned for its status (`withStatus`), or from a per-status failure (`failStatus`). With `defer()` every
 * call stays pending until `resolveAll()`/`rejectAll()`, which lets a spec hold five requests open at once (coalescing
 * and late-result tests).
 */
export class FakeDashboardEpisodesRepository extends EpisodesRepository {
  readonly calls: DashboardListQuery[] = [];
  private readonly pages = new Map<string, CursorPage<EpisodeListItem>>();
  private readonly failures = new Map<string, unknown>();
  private readonly pending: Pending[] = [];
  private deferred = false;

  /** Cans the page every future call for `status` answers with; re-canning clears every scripted failure. */
  withStatus(status: EpisodeStatus, items: readonly EpisodeListItem[]): this {
    this.pages.set(status, { items: [...items], nextCursor: null });
    this.failures.clear();
    return this;
  }

  /** Scripts a failure for every future call for `status`. */
  failStatus(status: EpisodeStatus, error: unknown): this {
    this.failures.set(status, error);
    return this;
  }

  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  /** Resolves every pending call with the page canned for its status (empty when none is canned). */
  resolveAll(): void {
    for (const pending of this.pending.splice(0)) pending.resolve(this.answer(pending.query));
  }

  /** Rejects every pending call with the same error. */
  rejectAll(error: unknown): void {
    for (const pending of this.pending.splice(0)) pending.reject(error);
  }

  override list(q: DashboardListQuery): Promise<CursorPage<EpisodeListItem>> {
    this.calls.push(q);
    if (this.deferred) {
      return new Promise((resolve, reject) => {
        this.pending.push({ query: q, resolve, reject });
      });
    }
    const failure = q.status === undefined ? undefined : this.failures.get(q.status);
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts any wire failure, incl. AppError objects
    if (failure !== undefined) return Promise.reject(failure);
    return Promise.resolve(this.answer(q));
  }

  private answer(q: DashboardListQuery): CursorPage<EpisodeListItem> {
    const page = q.status === undefined ? undefined : this.pages.get(q.status);
    return page ?? { items: [], nextCursor: null };
  }
}

/** A row with defaults; `n` makes the id/title/etag unique and ordered. */
export function dashboardEpisode(n: number, overrides: Partial<EpisodeListItem> = {}): EpisodeListItem {
  return {
    id: `dep-${String(n)}`,
    seriesId: '11111111-1111-4111-8111-111111111111',
    title: `Episode ${String(n)}`,
    status: 'story_review',
    lengthTier: 'standard',
    createdAt: '2026-10-01T10:00:00Z',
    updatedAt: '2026-10-02T10:00:00Z',
    etag: `"dep-${String(n)}"`,
    ...overrides,
  };
}

/** One needs-review, two in-progress, zero ready (task §Steps 2: the empty-group path is covered by `ready`). */
export function defaultDashboardFixture(): FakeDashboardEpisodesRepository {
  return new FakeDashboardEpisodesRepository()
    .withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })])
    .withStatus('story_generating', [dashboardEpisode(2, { status: 'story_generating' })])
    .withStatus('rendering', [dashboardEpisode(3, { status: 'rendering' })])
    .withStatus('ready', []);
}
