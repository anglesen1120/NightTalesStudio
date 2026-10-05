import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, computed, inject, linkedSignal, OnInit, signal } from '@angular/core';
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { SessionStore } from '@core/session';
import { ConfirmDialog, EmptyState, ErrorState, LoadingState, PageHeader, RetryCountdown } from '@shared/ui';
import { SeriesListStore } from '../application/series-list.store';
import { toSeriesRowViewModel } from '../domain/series-row.view-model';
import { SeriesRow } from '../ui/series-row';

@Component({
  selector: 'nts-series-list-page',
  imports: [RouterLink, MatButtonModule, MatCheckboxModule, TranslocoPipe, PageHeader, EmptyState, ErrorState, LoadingState, RetryCountdown, SeriesRow],
  providers: [SeriesListStore, provideTranslocoScope('series', 'errors')],
  templateUrl: './series-list-page.html',
  styleUrl: './series-list-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeriesListPage implements OnInit {
  protected readonly store = inject(SeriesListStore);
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmDialog = inject(ConfirmDialog);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly transloco = inject(TranslocoService);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly mapper = inject(AppErrorMapper);
  protected readonly rows = computed(() => this.store.items().map(toSeriesRowViewModel));
  protected readonly errorKey = computed(() => {
    const error = this.store.error();
    return error === null ? null : this.mapper.messageKey(error);
  });
  protected readonly retryLocked = linkedSignal({ source: this.store.retryAfterSec, computation: () => true });
  private readonly confirmingId = signal<string | null>(null);
  protected readonly archiveBusy = computed(() => this.store.archivingId() ?? this.confirmingId());

  ngOnInit(): void { void this.store.load(); }

  protected open(id: string): void { void this.router.navigate(['/series', id]); }
  protected create(): void { void this.router.navigate(['/series/new']); }

  protected retry(): void {
    if (this.store.retryAfterSec() !== null && this.retryLocked()) return;
    void this.store.retry();
  }

  protected async archive(id: string): Promise<void> {
    if (this.archiveBusy() !== null) return;
    const item = this.store.items().find((candidate) => candidate.id === id);
    if (item === undefined || item.archivedAt !== null) return;
    const generation = this.session.generation();
    this.confirmingId.set(id);
    try {
      // Await lazy scope loading before the service-created overlay renders translated content. The scope is passed
      // as the third argument (episodes precedent, episode-row.component.ts:42): a key written with its `series.`
      // runtime prefix would be attributed to the root scope by `i18n:check`.
      await firstValueFrom(this.transloco.selectTranslate('archive.title', {}, 'series'));
      if (this.destroyRef.destroyed || generation !== this.session.generation()) return;
      const ref = this.confirmDialog.confirm({
        titleKey: 'series.archive.title',
        bodyKey: 'series.archive.confirm',
        // Plain text, never a Transloco param: Transloco re-interpolates substituted values, so a
        // title like "{{title}}" would loop the transpiler (PS1).
        subject: item.title,
        confirmLabelKey: 'series.row.archive',
        destructive: true,
      }, { viewContainerRef: this.viewContainerRef });
      const confirmed = await firstValueFrom(ref.afterClosed(), { defaultValue: false });
      if (!confirmed || this.destroyRef.destroyed || generation !== this.session.generation()) return;
      const archived = await this.store.archive(id);
      if (archived && !this.destroyRef.destroyed && generation === this.session.generation()) {
        const text = await firstValueFrom(this.transloco.selectTranslate<string>('archive.done', {}, 'series'));
        if (!this.destroyRef.destroyed && generation === this.session.generation()) await this.announcer.announce(text, 'polite');
      }
    } finally {
      if (!this.destroyRef.destroyed) this.confirmingId.set(null);
    }
  }
}
