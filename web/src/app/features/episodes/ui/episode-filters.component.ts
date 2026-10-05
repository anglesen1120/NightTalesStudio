import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { LIST_STATUSES, isListStatus, type EpisodeStatus } from '../domain/episode.model';
import type { EpisodeFiltersViewModel } from './episode-list.view-model';

let nextFiltersId = 0;

/**
 * The status + series filter row (deck `f-list-filters`, `f-list-filter-unavailable`). Two labelled native selects,
 * a bounded/unavailable hint, and one clearable chip per active filter plus a "clear all" action (deck note: "one
 * keyboard action to clear both filters"). Inputs/outputs only — intents are emitted, never applied here (D2).
 */
@Component({
  selector: 'nts-episode-filters',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './episode-filters.component.html',
  styleUrl: './episode-filters.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EpisodeFiltersComponent {
  readonly filters = input.required<EpisodeFiltersViewModel>();

  /** New status selection — the literal `EpisodeStatus`, or `null` for "all". */
  readonly statusChange = output<EpisodeStatus | null>();
  /** New series selection — the option's id, or `null` for "all". */
  readonly seriesChange = output<string | null>();
  /** The deck's one-keyboard-action "clear both filters" intent (the filtered-empty action emits it too). */
  readonly clearFilters = output<void>();

  protected readonly statusOptions = LIST_STATUSES;
  protected readonly statusSelectId = `nts-episode-status-${nextFiltersId++}`;
  protected readonly seriesSelectId = `nts-episode-series-${nextFiltersId++}`;

  /** A deep-linked series id that is not on the first options page stays applied and shown as the id (failure table). */
  protected readonly missingSeries = computed(() => {
    const selected = this.filters().seriesId;
    return (
      selected !== null && !this.filters().seriesOptions.some((option) => option.id === selected)
    );
  });

  /** The series chip/option label: the resolved title, or the id itself while the options do not contain it. */
  protected readonly seriesTitle = computed(() => {
    const selected = this.filters().seriesId;
    if (selected === null) return null;
    return this.filters().seriesOptions.find((option) => option.id === selected)?.title ?? selected;
  });

  /** The hint under the series select — a bounded note or the unavailable note; plain "saved in URL" otherwise. */
  protected readonly seriesHintKey = computed(() => {
    if (this.filters().seriesOptionsState === 'unavailable') return 'episodes.list.seriesFilterUnavailable';
    if (this.filters().seriesOptionsBounded) return 'episodes.list.seriesFilterBounded';
    if (this.filters().status !== null || this.filters().seriesId !== null)
      return 'episodes.list.filtersSavedInUrl';
    return null;
  });

  /** The series select is disabled while options are in flight or unavailable; a set filter stays visible. */
  protected readonly seriesDisabled = computed(
    () => this.filters().disabled || this.filters().seriesOptionsState !== 'ready',
  );

  protected onStatusChange(event: Event): void {
    const { value } = event.currentTarget as HTMLSelectElement;
    this.statusChange.emit(value === '' ? null : isListStatus(value) ? value : null);
  }

  protected onSeriesChange(event: Event): void {
    const { value } = event.currentTarget as HTMLSelectElement;
    this.seriesChange.emit(value === '' ? null : value);
  }
}
