import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-empty-state',
  imports: [TranslocoPipe],
  templateUrl: './empty-state.html',
  styleUrl: './empty-state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'status' },
})
export class EmptyState {
  readonly titleKey = input.required<string>();
  readonly bodyKey = input<string>();
  readonly actionLabelKey = input<string>();
  readonly action = output<void>();
}
