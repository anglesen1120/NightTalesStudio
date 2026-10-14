import { ChangeDetectionStrategy, Component, computed, input, output, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { messageKeyOf } from '@core/errors/error-code';
import { ConfirmDialogComponent } from '@shared/ui/confirm-dialog/confirm-dialog';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';
import type { Attestation } from '../../domain/story.model';
import { AttestationBlockComponent } from '../attestation-block/attestation-block.component';

@Component({
  selector: 'nts-approve-dialog',
  imports: [ConfirmDialogComponent, AttestationBlockComponent, RetryCountdown, TranslocoPipe],
  templateUrl: './approve-dialog.component.html',
  styleUrl: './approve-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApproveDialogComponent {
  readonly attestation = input<Attestation | null>(null);
  readonly hintCount = input(0);
  readonly busy = input(false);
  readonly blocked = input<AppError | null>(null);
  readonly accepted = input(false);
  readonly checkboxInvalid = input(false);
  readonly retryAfterSec = input<number | null>(null);
  readonly confirmDisabled = input(false);
  readonly episodeId = input.required<string>();
  readonly acceptedChange = output<boolean>();
  readonly approved = output<void>();
  readonly cancelled = output<void>();

  private readonly countdown = viewChild(RetryCountdown);
  protected readonly retryPending = computed(() => this.retryAfterSec() !== null && (this.countdown()?.remaining() ?? this.retryAfterSec() ?? 0) > 0);
  protected readonly blockedKey = computed(() => {
    const error = this.blocked();
    return error ? messageKeyOf(error) : 'errors.unknown_error';
  });

  protected cancel(): void {
    if (!this.busy()) this.cancelled.emit();
  }
}
