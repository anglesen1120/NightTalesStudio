import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { type AppError, messageKeyOf } from '@core/errors';
import { EmptyState, ErrorState, LoadingState } from '@shared/ui';
import type { EpisodeListItem } from '../domain/episode.model';
import { EpisodeRowComponent } from './episode-row.component';

const LOADING_MORE_SKELETONS = [0, 1] as const;

/**
 * The presentational episode list (deck `f-list-loading`, `f-list-rows`, `f-list-empty`, `f-list-filter-empty`,
 * `f-list-paging`, `f-list-error`). Skeleton on the first page; rows keep list order for Tab (title then Continue);
 * the footer button emits `loadMore` only while `hasMore` and not `loadingMore` — it stays focusable with
 * `aria-disabled`/`aria-busy` during a page load, so focus never moves (deck note). One `role="status"` region
 * announces the in-flight append and the number of appended rows. Inputs/outputs only (D2).
 */
@Component({
  selector: 'nts-episode-list',
  imports: [TranslocoPipe, EmptyState, ErrorState, LoadingState, EpisodeRowComponent],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './episode-list.component.html',
  styleUrl: './episode-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EpisodeListComponent {
  readonly items = input.required<readonly EpisodeListItem[]>();
  /** First page in flight (skeleton); appended pages use `loadingMore`. */
  readonly loading = input.required<boolean>();
  readonly loadingMore = input.required<boolean>();
  readonly hasMore = input.required<boolean>();
  readonly error = input.required<AppError | null>();
  /** `!loading && error === null && items.length === 0` — evaluated with `hasMore` so an empty page that still carries a cursor keeps the button (failure table). */
  readonly isEmpty = input.required<boolean>();
  /** Any filter is applied: the filtered-empty copy offers "clear filters" instead of the /series action. */
  readonly filtered = input.required<boolean>();

  readonly loadMore = output<void>();
  readonly retry = output<void>();
  /** A row's Continue intent, carrying the episode id. */
  readonly open = output<string>();
  /** The unfiltered empty state action → the page navigates to /series. */
  readonly emptyAction = output<void>();
  /** The filtered-empty action → the page clears both filters. */
  readonly clearFilters = output<void>();

  protected readonly skeletonRows = LOADING_MORE_SKELETONS;

  /** D9: only `errors.<code>` copy; a `ProblemDetails` `detail` is never rendered. */
  protected readonly errorKey = computed(() => {
    const error = this.error();
    // Rendered only inside `@if (error())`; the fallback keeps the input type non-null.
    return error === null ? 'errors.unknown_error' : messageKeyOf(error);
  });

  /** Appended rows of the most recent page append; 0 when the change was not an append. */
  protected readonly appendedCount = signal(0);

  constructor() {
    let previousIds: readonly string[] = [];
    effect(() => {
      const current = this.items();
      const currentIds = current.map((item) => item.id);
      // A true append keeps every loaded id as a strict prefix; the first page (no previous ids), a filter
      // reset, a retry or an error reload is never announced as appended rows.
      const appended =
        previousIds.length > 0 &&
        current.length > previousIds.length &&
        previousIds.every((id, index) => id === currentIds[index]);
      this.appendedCount.set(appended ? current.length - previousIds.length : 0);
      previousIds = currentIds;
    });
  }

  /** `aria-disabled` (never `disabled`) so a focused button stays focusable; a second activation is ignored. */
  protected onLoadMore(): void {
    if (!this.hasMore() || this.loadingMore()) return;
    this.loadMore.emit();
  }
}
