import { ChangeDetectionStrategy, Component, computed, input, output, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { messageKeyOf } from '@core/errors/error-code';
import { ConfirmDialogComponent } from '@shared/ui/confirm-dialog/confirm-dialog';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';
import type { Attestation } from '../../domain/story.model';
import { AttestationBlockComponent } from '../attestation-block/attestation-block.component';

/**
 * The two approval 409s whose fix lives on the episode research page (10.5.6-T1 hand-over): a stale or under-cited
 * fact sheet. Every other block carries no route, so the dialog never ships a half-built link.
 */
const RESEARCH_LINK_CODES: ReadonlySet<string> = new Set(['story_fact_sheet_outdated', 'story_sources_required']);

@Component({
  selector: 'nts-approve-dialog',
  imports: [ConfirmDialogComponent, AttestationBlockComponent, RetryCountdown, TranslocoPipe, RouterLink],
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
  /** The research deep link of the two fixable approval blocks, `null` for every other one. */
  protected readonly researchHref = computed<readonly string[] | null>(() => {
    const error = this.blocked();
    return error !== null && RESEARCH_LINK_CODES.has(error.code) ? ['/episodes', this.episodeId(), 'research'] : null;
  });

  protected cancel(): void {
    if (!this.busy()) this.cancelled.emit();
  }
}
