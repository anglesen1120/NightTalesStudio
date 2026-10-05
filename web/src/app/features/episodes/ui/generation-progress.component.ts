import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { ErrorState, StatusChip } from '@shared/ui';

const STAGE_KEYS: Readonly<Record<string, true>> = {
  'episodes.generation.stage.queued': true,
  'story.generation.stage.writing': true,
  'story.generation.stage.checking': true,
  'story.generation.stage.reviewing': true,
  'story.generation.stage.revising': true,
  'story.generation.stage.scoring': true,
  'story.generation.stage.saving': true,
  'episodes.generation.stage.done': true,
};

@Component({
  selector: 'nts-generation-progress',
  imports: [RouterLink, TranslocoPipe, ErrorState, StatusChip],
  providers: [provideTranslocoScope('episodes', 'story', 'errors')],
  templateUrl: './generation-progress.component.html',
  styleUrl: './generation-progress.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GenerationProgressComponent {
  readonly stageKey = input<string | null>(null);
  readonly failureKey = input<string | null>(null);
  readonly paused = input<string | null>(null);
  readonly canRetry = input(false);
  readonly retrying = input(false);
  readonly episodeId = input<string | null>(null);
  readonly retry = output<void>();

  protected readonly visibleStageKey = computed(() => {
    const key = this.stageKey();
    return key !== null && Object.hasOwn(STAGE_KEYS, key) ? key : null;
  });
  protected readonly pausedKey = computed(() =>
    this.paused() === 'provider_funds_exhausted'
      ? 'story.generation.paused.provider_funds_exhausted'
      : 'episodes.generation.paused',
  );

  protected onRetry(): void {
    if (!this.canRetry() || this.retrying() || this.paused() !== null) return;
    this.retry.emit();
  }
}
