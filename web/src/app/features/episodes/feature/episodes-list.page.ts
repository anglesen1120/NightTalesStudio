import { ChangeDetectionStrategy, Component, computed, effect, inject, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, type ParamMap } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { PageHeader } from '@shared/ui';
import { EpisodeListStore } from '../application/episode-list.store';
import { isListStatus, isUuid, type EpisodeStatus } from '../domain/episode.model';
import { EpisodeFiltersComponent } from '../ui/episode-filters.component';
import { EpisodeListComponent } from '../ui/episode-list.component';
import type { EpisodeFiltersViewModel } from '../ui/episode-list.view-model';

/**
 * Routed container of `/episodes` (D2: `feature/` is the composition root — the only layer that injects the store,
 * Router and ActivatedRoute). `EpisodeListStore` lives on the component (`providers`), not `Route.providers`, so it
 * is destroyed with the page (README §3.1). The `episodes`/`errors` scopes are re-declared here so the page renders
 * the same under a bare TestBed as under `episodes.routes.ts`.
 *
 * URL contract: `?status=` accepts only a `LIST_STATUSES` literal and `?series=` only a uuid — anything else is
 * ignored (never sent, never reflected). Filter intents write both params with `replaceUrl: true`; the cursor never
 * reaches the URL. A param-only change on the reused component calls `setFilters` once; `init` runs exactly once.
 */
@Component({
  selector: 'nts-episodes-list-page',
  imports: [PageHeader, EpisodeFiltersComponent, EpisodeListComponent],
  providers: [EpisodeListStore, provideTranslocoScope('episodes', 'errors')],
  templateUrl: './episodes-list.page.html',
  styleUrl: './episodes-list.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EpisodesListPage {
  protected readonly store = inject(EpisodeListStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly queryParams = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  /** Any applied filter — switches the empty state to the filtered variant and keeps filters enabled on it. */
  protected readonly filtersActive = computed(
    () => this.store.statusFilter() !== null || this.store.seriesFilter() !== null,
  );

  /** The deck disables both selects while the first page skeletons and on the plain (unfiltered) empty state. */
  protected readonly filtersDisabled = computed(
    () => this.store.loading() || (this.store.isEmpty() && !this.filtersActive()),
  );

  protected readonly filtersModel = computed<EpisodeFiltersViewModel>(() => ({
    status: this.store.statusFilter(),
    seriesId: this.store.seriesFilter(),
    seriesOptions: this.store.seriesOptions(),
    seriesOptionsState: this.store.seriesOptionsState(),
    seriesOptionsBounded: this.store.seriesOptionsBounded(),
    disabled: this.filtersDisabled(),
  }));

  private inited = false;

  constructor() {
    effect(() => this.applyParams(this.queryParams()));
  }

  /**
   * Applies validated route params to the store: `init` on the first emission, `setFilters` on later param-only
   * changes — skipped entirely when nothing differs, so a filter intent's own `replaceUrl` echo costs no request.
   */
  private applyParams(params: ParamMap): void {
    const statusParam = params.get('status');
    const seriesParam = params.get('series');
    const status: EpisodeStatus | null = isListStatus(statusParam) ? statusParam : null;
    const seriesId: string | null = isUuid(seriesParam) ? seriesParam : null;
    if (!this.inited) {
      this.inited = true;
      this.store.init({ status, seriesId });
      return;
    }
    const unchanged = untracked(
      () => this.store.statusFilter() === status && this.store.seriesFilter() === seriesId,
    );
    if (!unchanged) this.store.setFilters({ status, seriesId });
  }

  protected onStatusChange(value: EpisodeStatus | null): void {
    this.store.setStatus(value);
    this.writeFilters(value, this.store.seriesFilter());
  }

  protected onSeriesChange(value: string | null): void {
    this.store.setSeries(value);
    this.writeFilters(this.store.statusFilter(), value);
  }

  protected onClearFilters(): void {
    this.store.setFilters({ status: null, seriesId: null });
    this.writeFilters(null, null);
  }

  /** The filtered-empty action is the same intent as the filters' "clear all". */
  protected onListClearFilters(): void {
    this.onClearFilters();
  }

  /** `/episodes/:id` — the row also navigates via routerLink; this keeps `open` consumers under one route hop. */
  protected onOpen(id: string): void {
    void this.router.navigate(['/episodes', id]);
  }

  protected onEmptyAction(): void {
    void this.router.navigate(['/series']);
  }

  private writeFilters(status: EpisodeStatus | null, seriesId: string | null): void {
    void this.router.navigate([], {
      queryParams: { status, series: seriesId },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
