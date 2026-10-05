import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { IconAlert, IconCheck, IconClock, IconUnknown, STATUS_ICON } from '@shared/ui';
import type { EpisodeListItem } from '../domain/episode.model';
import { EPISODE_STATUS_TONE, episodeDateFormatter } from './episode-list.view-model';

/** Same icon names the shared `StatusChip` maps (the `StatusIcon` union is not exported from `@shared/ui`). */
type EpisodeStatusIcon = 'check' | 'clock' | 'alert' | 'unknown';

/**
 * One ruled row of the episode list (deck `f-list-rows`): title link, status chip (inline-SVG icon + text, never
 * colour alone — WR12), `updatedAt` in the shared `Intl.DateTimeFormat`, and a "Continue" link to the detail route.
 * `StatusChip` itself is not composed here: it owns the `core.ui.statusChip.*` key map, while the approved contract
 * (plan §6, deck notes) renders `episodes.status.<status>`. The shared `STATUS_ICON` map and icon components are
 * reused so the chip keeps the kit's icon semantics. Text goes through the `transloco` pipe (not a computed
 * `translate`) so it renders only once the lazy `episodes` scope is loaded and re-renders on language change.
 * Pure: inputs/outputs only — no store, repository or HttpClient (D2).
 */
@Component({
  selector: 'nts-episode-row',
  imports: [RouterLink, TranslocoPipe, IconAlert, IconCheck, IconClock, IconUnknown],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './episode-row.component.html',
  styleUrl: './episode-row.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'episode-row' },
})
export class EpisodeRowComponent {
  readonly item = input.required<EpisodeListItem>();

  /** The row's "open" intent: emitted by the Continue link (which also navigates via its routerLink). */
  readonly open = output<string>();

  private readonly transloco = inject(TranslocoService);
  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });

  protected readonly detailCommands = computed(() => ['/episodes', this.item().id] as const);

  protected readonly icon = computed<EpisodeStatusIcon>(() => STATUS_ICON[this.item().status] ?? 'unknown');

  protected readonly toneClass = computed(
    () => `episode-row__status episode-row__status--${EPISODE_STATUS_TONE[this.item().status]}`,
  );

  protected readonly statusKey = computed(() => `episodes.status.${this.item().status}`);

  protected readonly updatedText = computed(() => {
    const date = new Date(this.item().updatedAt);
    if (Number.isNaN(date.getTime())) return this.item().updatedAt;
    return episodeDateFormatter(this.activeLang()).format(date);
  });
}
