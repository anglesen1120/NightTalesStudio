import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { type AppError, AppErrorMapper } from '@core/errors';
import { SessionStore } from '@core/session';
import { EpisodesRepository } from '@features/episodes';
import {
  DASHBOARD_PAGE_SIZE,
  DASHBOARD_STATUSES,
  type DashboardGroup,
  type DashboardGroups,
  type NextActionRowViewModel,
  emptyDashboardGroups,
  mergeDashboardGroups,
  primaryActionFor,
} from '../dashboard.model';

/** The `AppError` shape check the episodes store uses: a rejection that already is one is not re-mapped. */
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

/**
 * State of the `/dashboard` overview (provided by the routed page, never root: it dies with the page).
 *
 * `load()` issues the five scalar-status requests in one `Promise.all` and keeps nothing on any failure. `refresh()`
 * (window focus) is coalesced and keeps the rows it already has. Staleness (README §3.1): every run captures the
 * session generation and a run id; a late result — or a late rejection — is dropped when the generation changed, a
 * newer run started, or the store was destroyed. No timers, no subscriptions: the page owns the focus listener and
 * the throttle.
 */
@Injectable()
export class DashboardStore {
  private readonly repo = inject(EpisodesRepository);
  private readonly session = inject(SessionStore);
  private readonly mapper = inject(AppErrorMapper);

  private readonly groupsState = signal<DashboardGroups>(emptyDashboardGroups());
  private readonly loadingState = signal(false);
  private readonly refreshingState = signal(false);
  private readonly hasLoadedState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly refreshErrorState = signal<AppError | null>(null);
  private readonly lastRefreshedAtState = signal<number | null>(null);

  private epoch = 0;
  private loadToken = 0;
  private refreshToken = 0;
  private destroyed = false;

  private readonly hasRows = computed(() => {
    const groups = this.groupsState();
    return groups.needsReview.length + groups.inProgress.length + groups.ready.length > 0;
  });

  /** First load (or a Retry with nothing on screen): the page paints skeletons. Never true while rows are shown. */
  readonly loading = computed(() => this.loadingState() && !this.hasRows());
  /** Focus refresh with rows on screen: the page keeps them and shows no skeleton. */
  readonly refreshing = this.refreshingState.asReadonly();
  readonly hasLoaded = this.hasLoadedState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly refreshError = this.refreshErrorState.asReadonly();
  readonly lastRefreshedAt = this.lastRefreshedAtState.asReadonly();
  readonly isOwner = computed(() => this.session.role() === 'owner');

  /** Whether a `ready` row may offer "publish" instead of "open"; recomputed when the feature set loads. */
  private readonly canPublish = computed(() => this.session.hasFeature('youtube_publish'));

  readonly needsReview = computed(() => this.rowsOf('needsReview'));
  readonly inProgress = computed(() => this.rowsOf('inProgress'));
  readonly ready = computed(() => this.rowsOf('ready'));

  readonly counts = computed(() => {
    const groups = this.groupsState();
    return { needsReview: groups.needsReview.length, inProgress: groups.inProgress.length, ready: groups.ready.length };
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.epoch++;
    });
  }

  /** The first load / Retry. Ignored while a load is already in flight. */
  load(): Promise<void> {
    if (this.destroyed || this.loadingState()) return Promise.resolve();
    return this.run('load');
  }

  /** The focus refresh. Coalesced: ignored while a load or a refresh is in flight. */
  refresh(): Promise<void> {
    if (this.destroyed || this.loadingState() || this.refreshingState()) return Promise.resolve();
    return this.run('refresh');
  }

  private rowsOf(group: DashboardGroup): readonly NextActionRowViewModel[] {
    const canPublish = this.canPublish();
    return this.groupsState()[group].map((episode) => ({ episode, group, primaryAction: primaryActionFor(episode.status, canPublish) }));
  }

  private async run(kind: 'load' | 'refresh'): Promise<void> {
    const runId = ++this.epoch;
    const token = kind === 'load' ? ++this.loadToken : ++this.refreshToken;
    const generation = this.session.generation();
    const stale = (): boolean => this.destroyed || this.epoch !== runId || this.session.generation() !== generation;
    if (kind === 'load') {
      this.loadingState.set(true);
      this.errorState.set(null);
    } else {
      this.refreshingState.set(true);
    }
    try {
      const pages = await Promise.all(DASHBOARD_STATUSES.map((status) => this.repo.list({ status, limit: DASHBOARD_PAGE_SIZE })));
      if (stale()) return;
      this.groupsState.set(mergeDashboardGroups(pages.map((page) => page.items)));
      this.hasLoadedState.set(true);
      this.lastRefreshedAtState.set(Date.now());
      this.errorState.set(null);
      this.refreshErrorState.set(null);
    } catch (caught) {
      if (stale()) return;
      const error = isAppError(caught) ? caught : this.mapper.from(caught);
      if (kind === 'load') {
        // One group failing fails the page: a partial render would be misleading (task failure table).
        this.groupsState.set(emptyDashboardGroups());
        this.errorState.set(error);
      } else {
        // The focus refresh keeps the last good rows and only flags the failure; a 429 discards the fresh page too.
        this.refreshErrorState.set(error);
      }
    } finally {
      // Own flag only: a newer run of the same kind owns it otherwise, and a destroyed store takes no writes.
      const owned = kind === 'load' ? this.loadToken === token : this.refreshToken === token;
      if (!this.destroyed && owned) {
        if (kind === 'load') this.loadingState.set(false);
        else this.refreshingState.set(false);
      }
    }
  }
}
