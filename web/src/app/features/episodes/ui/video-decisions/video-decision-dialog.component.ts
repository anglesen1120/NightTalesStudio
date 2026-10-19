import { ChangeDetectionStrategy, Component, computed, input, output, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { map, startWith, switchMap } from 'rxjs';
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
    // `events` is the complete control stream (value, status, touched/pristine), so a `markAsTouched()` from the
    // store after a failed submit reaches the view as well (C7).
    switchMap((control) => control.events.pipe(
      startWith(null),
      map(() => ({ value: control.value, invalid: control.invalid, touched: control.touched, dirty: control.dirty })),
    )),
  ));
  protected readonly reasonState = toSignal(toObservable(this.reasonControl).pipe(
    switchMap((control) => control.events.pipe(
      startWith(null),
      map(() => ({ value: control.value, invalid: control.invalid, touched: control.touched, dirty: control.dirty })),
    )),
  ));
  private readonly countdown = viewChild(RetryCountdown);
  protected readonly retryPending = computed(() => this.retryAfterSec() !== null && (this.countdown()?.remaining() ?? this.retryAfterSec() ?? 0) > 0);
  protected readonly prefix = computed(() => `episodes.render.review.${this.mode() === 'approve' ? 'approve' : this.target() === 're_render' ? 'reMake' : 'reject'}`);
  /**
   * The control's own verdict, nothing recomputed here (C8/D2): the store's `reason` validator already judges the
   * trimmed 1–500 and the NUL, and the note's `maxLength` its raw 500, so the disabled confirm and the store's
   * `reject()`/`approve()` cannot disagree. It is also what keeps an invalid field unsubmittable, as before.
   */
  protected readonly fieldInvalid = computed(() => this.mode() === 'approve'
    ? this.noteState()?.invalid ?? this.noteControl().invalid
    : this.reasonState()?.invalid ?? this.reasonControl().invalid);
  /** The note has been interacted with: typed into (dirty) or blurred / marked by the store (touched). */
  private readonly noteMarked = computed(() => (this.noteState()?.touched ?? this.noteControl().touched) || (this.noteState()?.dirty ?? this.noteControl().dirty));
  /** The reason has been interacted with: typed into (dirty) or blurred / marked by the store (touched). */
  private readonly reasonMarked = computed(() => (this.reasonState()?.touched ?? this.reasonControl().touched) || (this.reasonState()?.dirty ?? this.reasonControl().dirty));
  /**
   * Whether the red message and `aria-invalid` are due (C7): only once the field has been touched or modified — the
   * native accessor marks it dirty as the user types and touched on blur, the store on a failed submit — so a
   * pristine, empty reason no longer opens red, while typing still gives immediate feedback. The confirm's disabled
   * state keeps following `fieldInvalid()`, so the mechanism and its focus behaviour are unchanged.
   */
  protected readonly fieldErrorVisible = computed(() => this.fieldInvalid() && (this.mode() === 'approve' ? this.noteMarked() : this.reasonMarked()));

  protected confirm(): void {
    if (!this.busy() && !this.confirmDisabled() && !this.fieldInvalid() && !this.retryPending()) this.confirmed.emit();
  }

  protected dismiss(): void {
    if (!this.busy()) this.dismissed.emit();
  }
}
