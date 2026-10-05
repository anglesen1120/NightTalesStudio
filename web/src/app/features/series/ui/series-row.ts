import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconCheck, IconPause } from '@shared/ui';
import type { SeriesRowViewModel } from '../domain/series-row.view-model';
import { aspectLabelKey, topicLabelKey } from '../domain/series.model';

@Component({
  selector: 'li[nts-series-row]',
  imports: [TranslocoPipe, IconCheck, IconPause],
  providers: [provideTranslocoScope('series')],
  templateUrl: './series-row.html',
  styleUrl: './series-row.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'series-row' },
})
export class SeriesRow {
  readonly vm = input.required<SeriesRowViewModel>();
  readonly busy = input(false);
  readonly open = output<string>();
  readonly archive = output<string>();
  protected readonly topicKey = topicLabelKey;
  protected readonly aspectKey = aspectLabelKey;
}
