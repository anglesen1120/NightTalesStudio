import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-loading-state',
  imports: [TranslocoPipe],
  templateUrl: './loading-state.html',
  styleUrl: './loading-state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.aria-busy]': 'true' },
})
export class LoadingState {
  readonly rows = input(3);
  readonly labelKey = input('core.ui.loadingState.label');
  protected readonly skeletonRows = computed(() => Array.from({ length: Math.min(6, Math.max(3, this.rows())) }));
}
