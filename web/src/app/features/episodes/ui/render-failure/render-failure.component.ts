import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconAlert } from '@shared/ui';
import type { FailureCopy, RenderFailure } from '../../domain/render.model';
import { SkippedScenesComponent } from '../skipped-scenes/skipped-scenes.component';

let nextFailureId = 0;

@Component({
  selector: 'nts-render-failure',
  imports: [MatButtonModule, TranslocoPipe, IconAlert, SkippedScenesComponent],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './render-failure.component.html',
  styleUrl: './render-failure.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RenderFailureComponent {
  readonly failure = input.required<RenderFailure>();
  readonly copy = input.required<FailureCopy>();
  readonly actions = input.required<readonly string[]>();
  readonly episodeId = input.required<string>();
  readonly canEditScenes = input(false);
  readonly editScenes = output<void>();
  readonly retry = output<void>();

  protected readonly hintId = `nts-render-edit-hint-${++nextFailureId}`;
  protected readonly titleKey = computed(() => this.scopedKey(this.copy().titleKey));
  protected readonly bodyKeys = computed(() => this.copy().bodyKeys.map((key) => this.scopedKey(key)));
  protected readonly offersRetry = computed(() => this.copy().actionKeys.includes('retry'));
  protected readonly retryAllowed = computed(() => this.offersRetry() && this.actions().includes('retry_render'));
  protected readonly offersEdit = computed(() => this.copy().actionKeys.includes('editScenes'));
  protected readonly editAllowed = computed(() => this.offersEdit() && this.canEditScenes() && this.actions().includes('reopen_story'));

  private scopedKey(key: string): string {
    return key.startsWith('render.') ? `episodes.${key}` : key;
  }
}
