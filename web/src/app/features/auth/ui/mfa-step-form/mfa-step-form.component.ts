import { ChangeDetectionStrategy, Component, effect, input, output, untracked } from '@angular/core';
import { type FormControl, type FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { RetryCountdown } from '@shared/ui';
import type { MfaChallengeViewModel } from '../../domain/mfa.model';

/** The single code control of the MFA step: it carries a TOTP `code` or a `recoveryCode`, never both (C10). */
export interface MfaCodeControls {
  readonly code: FormControl<string>;
}

/**
 * Step 2 of `/login` (deck frames `f-login-mfa`, `f-login-mfa-recovery`, `f-login-mfa-error`, `f-login-mfa-locked`):
 * one code field, the recovery-mode toggle that swaps the label/hint (and drops `inputmode="numeric"`, the codes have
 * letters and dashes), and a back link to step 1. Presentational only: it emits intents and the container maps them
 * onto `LoginStore`. `fieldState` is the zoneless change-detection bridge the page feeds from the form's streams.
 */
@Component({
  selector: 'nts-mfa-step-form',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TranslocoPipe,
    RetryCountdown,
  ],
  providers: [provideTranslocoScope('auth', 'errors')],
  templateUrl: './mfa-step-form.component.html',
  styleUrl: './mfa-step-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MfaStepFormComponent {
  readonly form = input.required<FormGroup<MfaCodeControls>>();
  readonly vm = input.required<MfaChallengeViewModel>();
  readonly fieldState = input<unknown>();

  readonly submitCode = output<void>();
  readonly toggleRecovery = output<void>();
  readonly backToPassword = output<void>();
  readonly retryAfterElapsed = output<void>();

  constructor() {
    // The lock must live on the control, not only on the `[attr.disabled]` binding: the forms value accessor calls
    // `setDisabledState(false)` when it initialises, which wipes that attribute for a view created already locked.
    // The effect re-runs only when the lock itself changes, so it never fights the container's submit lock.
    effect(() => {
      const locked = this.vm().retryAfterSec !== null;
      const control = untracked(() => this.form().controls.code);
      if (locked && control.enabled) control.disable({ emitEvent: false });
      else if (!locked && control.disabled) control.enable({ emitEvent: false });
    });
  }

  /** The 429 lock: the field and the submit button are both disabled until the countdown ends (f-login-mfa-locked). */
  protected locked(): boolean {
    return this.vm().retryAfterSec !== null;
  }

  protected fieldErrorKey(): string | null {
    this.fieldState();
    const control = this.form().controls.code;
    const server = control.errors?.['server'] as { readonly messageKeys?: readonly string[] } | undefined;
    return server?.messageKeys?.[0] ?? (control.invalid && control.touched ? 'errors.validation_failed' : null);
  }

  protected describedBy(): string | null {
    const ids: string[] = [];
    if (this.fieldErrorKey() !== null) ids.push('mfa-code-error');
    if (this.vm().recoveryMode) ids.push('mfa-code-hint');
    if (this.vm().formError !== null) ids.push('mfa-form-alert');
    return ids.length > 0 ? ids.join(' ') : null;
  }
}
