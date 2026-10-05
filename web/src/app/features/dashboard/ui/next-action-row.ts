import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { LocaleService } from '@core/i18n';
import { EPISODE_STATUS_TONE } from '@features/episodes';
import { IconAlert, IconCheck, IconClock, IconUnknown, STATUS_ICON } from '@shared/ui';
import type { NextActionRowViewModel, PrimaryAction } from '../dashboard.model';

type EpisodeStatusIcon = 'check' | 'clock' | 'alert' | 'unknown';

const ACTION_KEY: Readonly<Record<PrimaryAction, string>> = {
  reviewStory: 'dashboard.card.action.reviewStory',
  reviewVideo: 'dashboard.card.action.reviewVideo',
  open: 'dashboard.card.action.open',
  publish: 'dashboard.card.action.publish',
};

const ACTION_LABEL_KEY: Readonly<Record<PrimaryAction, string>> = {
  reviewStory: 'dashboard.card.actionLabel.reviewStory',
  reviewVideo: 'dashboard.card.actionLabel.reviewVideo',
  open: 'dashboard.card.actionLabel.open',
  publish: 'dashboard.card.actionLabel.publish',
};

/** Presentational ruled row for one dashboard next action. */
@Component({
  selector: 'li[nts-next-action-row]',
  imports: [TranslocoPipe, IconAlert, IconCheck, IconClock, IconUnknown],
  templateUrl: './next-action-row.html',
  styleUrl: './next-action-row.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'next-action-row' },
})
export class NextActionRow {
  readonly vm = input.required<NextActionRowViewModel>();
  readonly now = input.required<number>();
  readonly untitledLabel = input.required<string>();

  readonly open = output<string>();
  readonly primaryAction = output<NextActionRowViewModel>();

  private readonly locale = inject(LocaleService);
  private readonly transloco = inject(TranslocoService);

  protected readonly displayTitle = computed(() => this.vm().episode.title?.trim() || this.untitledLabel());
  protected readonly statusKey = computed(() => `episodes.status.${this.vm().episode.status}`);
  protected readonly icon = computed<EpisodeStatusIcon>(
    () => STATUS_ICON[this.vm().episode.status] ?? 'unknown',
  );
  protected readonly toneClass = computed(
    () => `next-action-row__status next-action-row__status--${EPISODE_STATUS_TONE[this.vm().episode.status]}`,
  );
  protected readonly actionKey = computed(() => ACTION_KEY[this.vm().primaryAction]);
  protected readonly actionLabelKey = computed(() => ACTION_LABEL_KEY[this.vm().primaryAction]);
  protected readonly relativeUpdated = computed(() => {
    const updated = new Date(this.vm().episode.updatedAt);
    if (Number.isNaN(updated.getTime())) return this.transloco.translate('updatedJustNow', {}, 'dashboard');
    return this.locale.formatRelative(updated, { now: this.now() });
  });
  protected readonly absoluteUpdated = computed(() => {
    const updated = new Date(this.vm().episode.updatedAt);
    if (Number.isNaN(updated.getTime())) return this.vm().episode.updatedAt;
    return this.locale.formatDateTime(updated);
  });
}
