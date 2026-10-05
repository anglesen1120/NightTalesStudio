import { ChangeDetectionStrategy, Component, ElementRef, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { PageHeader, RetryCountdown } from '@shared/ui';
import { NewEpisodeStore } from '../application/new-episode.store';
import { GenerationProgressComponent } from '../ui/generation-progress.component';
import { StoryBriefFormComponent } from '../ui/story-brief-form.component';

@Component({
  selector: 'nts-new-episode-page',
  imports: [PageHeader, RetryCountdown, RouterLink, TranslocoPipe, StoryBriefFormComponent, GenerationProgressComponent],
  providers: [NewEpisodeStore, provideTranslocoScope('episodes', 'story', 'errors')],
  templateUrl: './new-episode.page.html',
  styleUrl: './new-episode.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NewEpisodePage {
  protected readonly store = inject(NewEpisodeStore);
  private readonly route = inject(ActivatedRoute);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    this.store.init(this.route.snapshot.paramMap.get('id') ?? '');
  }

  protected create(): void {
    const controls = this.store.form.controls;
    this.store.form.markAllAsTouched();
    // Focus only client-side invalid submissions, never an async/server alert.
    const field = controls.title.hasError('maxlength')
      ? 'title'
      : controls.brief.hasError('required') || controls.brief.hasError('maxlength') || controls.brief.value.trim().length === 0
        ? 'brief'
        : controls.lengthTier.invalid && !controls.lengthTier.hasError('server')
          ? 'lengthTier'
          : null;
    if (field !== null) {
      this.host.nativeElement.querySelector<HTMLElement>(`[data-control="${field}"]`)?.focus();
      return;
    }
    void this.store.create();
  }
}
