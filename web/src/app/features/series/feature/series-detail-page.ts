import { ChangeDetectionStrategy, Component, computed, effect, inject, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { AppErrorMapper } from '@core/errors';
import { EmptyState, ErrorState, LoadingState, PageHeader, IconPause } from '@shared/ui';
import { SeriesDetailStore } from '../application/series-detail.store';
import { aspectLabelKey, topicLabelKey } from '../domain/series.model';

@Component({
  selector: 'nts-series-detail-page',
  imports: [RouterLink, MatButtonModule, TranslocoPipe, EmptyState, ErrorState, LoadingState, PageHeader, IconPause],
  providers: [SeriesDetailStore, provideTranslocoScope('series', 'errors')],
  templateUrl: './series-detail-page.html',
  styleUrl: './series-detail-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeriesDetailPage {
  protected readonly store = inject(SeriesDetailStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly mapper = inject(AppErrorMapper);
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  protected readonly tab = computed(() => this.query().get('tab') === 'autopilot' ? 'autopilot' : 'overview');
  protected readonly topicKey = topicLabelKey;
  protected readonly aspectKey = aspectLabelKey;
  protected readonly errorKey = computed(() => {
    const error = this.store.error();
    return error === null ? null : this.mapper.messageKey(error);
  });

  constructor() {
    effect(() => {
      const id = this.params().get('id');
      if (id !== null) untracked(() => { void this.store.load(id); });
    });
  }

  protected retry(): void {
    const id = this.params().get('id');
    if (id !== null) void this.store.load(id);
  }

  protected backToList(): void { void this.router.navigate(['/series']); }
}
