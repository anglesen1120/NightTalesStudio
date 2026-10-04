import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { type FormControl, type FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { RetryCountdown } from '@shared/ui';

export interface ForgotPasswordFormControls {
  readonly email: FormControl<string>;
}

@Component({
  selector: 'nts-forgot-password-form',
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
  templateUrl: './forgot-password-form.component.html',
  styleUrl: './forgot-password-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPasswordFormComponent {
  readonly form = input.required<FormGroup<ForgotPasswordFormControls>>();
  readonly submitting = input(false);
  readonly canSubmit = input(true);
  readonly formError = input<string | null>(null);
  readonly traceId = input<string | null>(null);
  readonly retryAfterSec = input<number | null>(null);
  readonly fieldState = input<unknown>();

  readonly requestReset = output<void>();
  readonly retryAfterElapsed = output<void>();
  readonly backToLogin = output<void>();

  protected fieldErrorKey(control: FormControl<string>): string | null {
    this.fieldState();
    const server = control.errors?.['server'] as { readonly messageKeys?: readonly string[] } | undefined;
    return server?.messageKeys?.[0] ?? (control.invalid && control.touched ? 'errors.validation_failed' : null);
  }
}
