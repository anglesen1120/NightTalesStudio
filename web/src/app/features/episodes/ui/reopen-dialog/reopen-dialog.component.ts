import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { messageKeyOf } from '@core/errors/error-code';
import { ConfirmDialogComponent } from '@shared/ui/confirm-dialog/confirm-dialog';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';

@Component({
  selector: 'nts-reopen-dialog',
  imports: [ConfirmDialogComponent, RetryCountdown, TranslocoPipe],
  templateUrl: './reopen-dialog.component.html',
  styleUrl: './reopen-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReopenDialogComponent {
  readonly busy = input(false);
  readonly blocked = input<AppError | null>(null);
  readonly retryAfterSec = input<number | null>(null);
  readonly reopened = output<void>();
  readonly cancelled = output<void>();

  protected readonly blockedKey = computed(() => {
    const error = this.blocked();
    return error ? messageKeyOf(error) : 'errors.unknown_error';
  });

  protected cancel(): void {
    if (!this.busy()) this.cancelled.emit();
  }
}
