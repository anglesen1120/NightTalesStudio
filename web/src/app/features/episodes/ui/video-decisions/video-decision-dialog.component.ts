import { ChangeDetectionStrategy, Component, computed, input, output, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { map, merge, startWith, switchMap } from 'rxjs';
import { CharCounter } from '@shared/ui/char-counter/char-counter';
import { ConfirmDialogComponent } from '@shared/ui/confirm-dialog/confirm-dialog';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';
import type { RejectTarget } from '../../domain/video-review.repository';

let nextDecisionId = 0;

@Component({
  selector: 'nts-video-decision-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, CharCounter, ConfirmDialogComponent, RetryCountdown],
  templateUrl: './video-decision-dialog.component.html',
  styleUrl: './video-decision-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VideoDecisionDialogComponent {
  readonly mode = input.required<'approve' | 'reject'>();
  readonly target = input<RejectTarget>('story');
  readonly noteControl = input.required<FormControl<string>>();
  readonly reasonControl = input.required<FormControl<string>>();
  readonly busy = input(false);
  readonly errorKey = input<string | null>(null);
  readonly retryAfterSec = input<number | null>(null);
  readonly confirmDisabled = input(false);
  readonly confirmed = output<void>();
  readonly dismissed = output<void>();
  readonly targetChange = output<RejectTarget>();
  protected readonly fieldId = `nts-video-decision-${++nextDecisionId}`;
  protected readonly noteState = toSignal(toObservable(this.noteControl).pipe(
    switchMap((control) => merge(control.valueChanges, control.statusChanges).pipe(
      startWith(null),
      map(() => ({ value: control.value, invalid: control.invalid })),
    )),
  ));
  protected readonly reasonState = toSignal(toObservable(this.reasonControl).pipe(
    switchMap((control) => merge(control.valueChanges, control.statusChanges).pipe(
      startWith(null),
      map(() => ({ value: control.value, invalid: control.invalid })),
    )),
  ));
  private readonly countdown = viewChild(RetryCountdown);
  protected readonly retryPending = computed(() => this.retryAfterSec() !== null && (this.countdown()?.remaining() ?? this.retryAfterSec() ?? 0) > 0);
  protected readonly prefix = computed(() => `episodes.render.review.${this.mode() === 'approve' ? 'approve' : this.target() === 're_render' ? 'reMake' : 'reject'}`);
  protected readonly fieldInvalid = computed(() => this.mode() === 'approve'
    ? (this.noteState()?.invalid ?? this.noteControl().invalid) || (this.noteState()?.value ?? this.noteControl().value).length > 500
    : (this.reasonState()?.invalid ?? this.reasonControl().invalid) || (this.reasonState()?.value ?? this.reasonControl().value).trim().length === 0 || (this.reasonState()?.value ?? this.reasonControl().value).length > 500);

  protected confirm(): void {
    if (!this.busy() && !this.confirmDisabled() && !this.fieldInvalid() && !this.retryPending()) this.confirmed.emit();
  }

  protected dismiss(): void {
    if (!this.busy()) this.dismissed.emit();
  }
}
