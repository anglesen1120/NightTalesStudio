import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { type FormControl, type FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { RetryCountdown } from '@shared/ui';

export interface ResetPasswordFormControls {
  readonly password: FormControl<string>;
  readonly confirm: FormControl<string>;
}

@Component({
  selector: 'nts-reset-password-form',
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
  templateUrl: './reset-password-form.component.html',
  styleUrl: './reset-password-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPasswordFormComponent {
  readonly form = input.required<FormGroup<ResetPasswordFormControls>>();
  readonly submitting = input(false);
  readonly canSubmit = input(true);
  readonly formError = input<string | null>(null);
  readonly traceId = input<string | null>(null);
  readonly mismatch = input(false);
  readonly retryAfterSec = input<number | null>(null);
  readonly fieldState = input<unknown>();

  readonly resetPassword = output<void>();
  readonly retryAfterElapsed = output<void>();

  protected passwordErrorKey(control: FormControl<string>): string | null {
    this.fieldState();
    const server = control.errors?.['server'] as { readonly messageKeys?: readonly string[] } | undefined;
    if (server?.messageKeys?.[0] !== undefined) return server.messageKeys[0];
    return control.hasError('minlength') && control.touched ? 'auth.reset.passwordTooShort' : null;
  }

  protected confirmErrorKey(control: FormControl<string>): string | null {
    this.fieldState();
    const server = control.errors?.['server'] as { readonly messageKeys?: readonly string[] } | undefined;
    if (server?.messageKeys?.[0] !== undefined) return server.messageKeys[0];
    return this.mismatch() ? 'auth.reset.mismatch' : null;
  }
}
