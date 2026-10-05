import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { RealtimeState } from '@core/realtime';
import { IconClock, IconPause } from '@shared/ui';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-realtime-status',
  imports: [IconClock, IconPause, TranslocoPipe],
  templateUrl: './realtime-status.component.html',
  styleUrl: './realtime-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' },
})
export class RealtimeStatusComponent {
  readonly state = input.required<RealtimeState>();
  readonly resumeRequested = output<void>();
}
