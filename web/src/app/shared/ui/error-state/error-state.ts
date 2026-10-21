import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

let nextErrorStateId = 0;

@Component({
  selector: 'nts-error-state',
  imports: [TranslocoPipe],
  templateUrl: './error-state.html',
  styleUrl: './error-state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'alert', '[attr.aria-describedby]': 'bodyId' },
})
export class ErrorState {
  readonly titleKey = input('core.ui.errorState.title');
  readonly bodyKey = input.required<string>();
  readonly traceId = input<string>();
  readonly retryLabelKey = input<string>();
  readonly retry = output<void>();
  protected readonly bodyId = `nts-error-body-${nextErrorStateId++}`;
}
