import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Renderer2,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  type Signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { messageKeyOf } from '@core/errors';
import { EmptyState, ErrorState, LoadingState, PageHeader, RetryCountdown } from '@shared/ui';
import { DashboardStore } from '../application/dashboard.store';
import { destinationFor, type DashboardGroup, type NextActionRowViewModel } from '../dashboard.model';
import { NextActionRow } from '../ui/next-action-row';

const FOCUS_REFRESH_THROTTLE_MS = 10_000;

interface DashboardGroupView {
  readonly group: DashboardGroup;
  readonly headingId: string;
  readonly titleKey: string;
  readonly emptyKey: string;
  readonly rows: Signal<readonly NextActionRowViewModel[]>;
}

/** Routed `/dashboard` container: request-driven, with visibility-aware initial load and focus refresh. */
@Component({
  selector: 'nts-dashboard-page',
  imports: [
    RouterLink,
    TranslocoPipe,
    PageHeader,
    EmptyState,
    ErrorState,
    LoadingState,
    RetryCountdown,
    NextActionRow,
  ],
  providers: [DashboardStore, provideTranslocoScope('dashboard', 'episodes', 'errors')],
  templateUrl: './dashboard-page.html',
  styleUrl: './dashboard-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPage {
  protected readonly store = inject(DashboardStore);

  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly renderer = inject(Renderer2);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);
  private initialLoadRequested = false;
  private lastFocusRefreshRequestedAt = 0;

  protected readonly groups: readonly DashboardGroupView[] = [
    {
      group: 'needsReview',
      headingId: 'dashboard-needs-review',
      titleKey: 'dashboard.needsReview.title',
      emptyKey: 'dashboard.needsReview.empty',
      rows: this.store.needsReview,
    },
    {
      group: 'inProgress',
      headingId: 'dashboard-in-progress',
      titleKey: 'dashboard.inProgress.title',
      emptyKey: 'dashboard.inProgress.empty',
      rows: this.store.inProgress,
    },
    {
      group: 'ready',
      headingId: 'dashboard-ready',
      titleKey: 'dashboard.ready.title',
      emptyKey: 'dashboard.ready.empty',
      rows: this.store.ready,
    },
  ];

  protected readonly allEmpty = computed(
    () =>
      this.store.hasLoaded() &&
      !this.store.loading() &&
      this.store.error() === null &&
      this.groups.every((group) => group.rows().length === 0),
  );
  protected readonly errorKey = computed(() => {
    const error = this.store.error();
    return error === null ? 'errors.unknown_error' : messageKeyOf(error);
  });
  protected readonly retryAfterSec = computed(() => {
    const error = this.store.error();
    return error?.code === 'rate_limited' && error.retryAfterSec !== undefined
      ? error.retryAfterSec
      : undefined;
  });
  protected readonly retryElapsed = signal(false);
  protected readonly retryDisabled = computed(() => {
    const seconds = this.retryAfterSec();
    return seconds !== undefined && seconds > 0 && !this.retryElapsed();
  });
  protected readonly countsAnnouncement = signal<{
    readonly needsReview: number;
    readonly inProgress: number;
    readonly ready: number;
  } | null>(null);
  protected readonly untitledLabel = toSignal(
    this.transloco.selectTranslate<string>('list.untitled', {}, 'episodes'),
    { initialValue: '' },
  );
  protected readonly now = signal(Date.now());

  constructor() {
    let lastAnnouncedAt: number | null = null;
    effect(() => {
      const refreshedAt = this.store.lastRefreshedAt();
      if (refreshedAt === null || refreshedAt === lastAnnouncedAt) return;
      lastAnnouncedAt = refreshedAt;
      this.countsAnnouncement.set({ ...untracked(this.store.counts) });
    });

    effect(() => {
      const seconds = this.retryAfterSec();
      this.retryElapsed.set(seconds === undefined || seconds <= 0);
    });

    afterNextRender(() => {
      const onPageAttention = () => this.onPageAttention();
      const stopFocus = this.renderer.listen('window', 'focus', onPageAttention);
      const stopVisibility = this.renderer.listen(this.document, 'visibilitychange', onPageAttention);
      this.destroyRef.onDestroy(() => {
        stopFocus();
        stopVisibility();
      });
      this.requestInitialLoadWhenVisible();
    });
  }

  protected refresh(): void {
    if (this.document.visibilityState !== 'visible' || this.store.loading() || this.store.refreshing()) return;
    this.now.set(Date.now());
    if (this.store.hasLoaded()) void this.store.refresh();
    else {
      this.initialLoadRequested = true;
      void this.store.load();
    }
  }

  protected retry(): void {
    if (this.document.visibilityState !== 'visible' || this.retryDisabled()) return;
    this.now.set(Date.now());
    this.retryElapsed.set(false);
    this.initialLoadRequested = true;
    void this.store.load();
  }

  protected openEpisode(id: string): void {
    void this.router.navigate(['/episodes', id]);
  }

  protected runPrimaryAction(vm: NextActionRowViewModel): void {
    void this.router.navigate(destinationFor(vm.episode.id, vm.primaryAction));
  }

  private requestInitialLoadWhenVisible(): void {
    if (this.initialLoadRequested || this.document.visibilityState !== 'visible') return;
    this.now.set(Date.now());
    this.initialLoadRequested = true;
    void this.store.load();
  }

  private onPageAttention(): void {
    if (this.document.visibilityState !== 'visible') return;
    if (!this.initialLoadRequested) {
      this.requestInitialLoadWhenVisible();
      return;
    }
    if (!this.store.hasLoaded() || this.store.loading() || this.store.refreshing()) return;

    const now = Date.now();
    const lastSuccessfulRefresh = this.store.lastRefreshedAt() ?? 0;
    const throttleFrom = Math.max(lastSuccessfulRefresh, this.lastFocusRefreshRequestedAt);
    if (now - throttleFrom < FOCUS_REFRESH_THROTTLE_MS) return;
    this.now.set(now);

    this.lastFocusRefreshRequestedAt = now;
    void this.store.refresh();
  }
}
