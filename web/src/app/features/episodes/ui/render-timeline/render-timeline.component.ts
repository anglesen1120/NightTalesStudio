import { ChangeDetectionStrategy, Component, computed, input, output, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconAlert, IconCheck, IconClock } from '@shared/ui';
import type { RenderTimelineStep, SubStage } from '../../domain/render.model';

@Component({
  selector: 'nts-render-timeline',
  imports: [MatButtonModule, TranslocoPipe, IconAlert, IconCheck, IconClock],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './render-timeline.component.html',
  styleUrl: './render-timeline.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RenderTimelineComponent {
  readonly steps = input.required<readonly RenderTimelineStep[]>();
  readonly percent = input.required<number>();
  readonly subStepKey = input<SubStage | null>(null);
  readonly elapsedSec = input(0);
  readonly waitingForSlot = input(false);
  readonly showEmailNotice = input(false);
  readonly cancellable = input(false);
  readonly cancelling = input(false);
  readonly cancelErrorKey = input<string | null>(null);
  readonly cancelRequest = output<void>();

  protected readonly activeKey = computed(() => this.steps().find((step) => step.state === 'active')?.key ?? null);
  protected readonly activeSubStep = computed(() => this.activeKey() === 'running' ? this.subStepKey() : null);
  protected readonly showProgress = computed(() => this.activeKey() !== null && !this.steps().some((step) => step.state === 'failed'));
  protected readonly safePercent = computed(() => Number.isFinite(this.percent()) ? Math.min(100, Math.max(0, this.percent())) : 0);
  protected readonly duration = computed(() => this.formatDuration(this.elapsedSec()));
  // Freeze the screen-reader duration between guaranteed-stage changes; the visual clock can tick freely.
  protected readonly stageDuration = computed(() => {
    this.activeKey();
    return this.formatDuration(untracked(this.elapsedSec));
  });

  protected onCancel(event: Event): void {
    if (!this.cancellable() || this.cancelling()) {
      event.preventDefault();
      return;
    }
    this.cancelRequest.emit();
  }

  private formatDuration(value: number): string {
    const seconds = Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0;
    const minutes = Math.floor(seconds / 60);
    const tail = String(seconds % 60).padStart(2, '0');
    return minutes < 60 ? `${minutes}:${tail}` : `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}:${tail}`;
  }
}
