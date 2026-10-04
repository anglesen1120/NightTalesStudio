import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { type FormControl, type FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { RetryCountdown } from '@shared/ui';

export interface LoginFormControls {
  readonly email: FormControl<string>;
  readonly password: FormControl<string>;
}

@Component({
  selector: 'nts-login-form',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TranslocoPipe,
    RetryCountdown,
  ],
  providers: [provideTranslocoScope('errors')],
  templateUrl: './login-form.component.html',
  styleUrl: './login-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginFormComponent {
  readonly form = input.required<FormGroup<LoginFormControls>>();
  readonly submitting = input(false);
  readonly canSubmit = input(true);
  readonly formError = input<string | null>(null);
  readonly retryAfterSec = input<number | null>(null);
  readonly sessionEndKey = input<string | null>(null);
  readonly fieldState = input<unknown>();

  readonly submitPassword = output<void>();
  readonly retryAfterElapsed = output<void>();
  readonly forgotPassword = output<void>();

  protected fieldErrorKey(control: FormControl<string>): string | null {
    this.fieldState();
    const server = control.errors?.['server'] as { readonly messageKeys?: readonly string[] } | undefined;
    return server?.messageKeys?.[0] ?? (control.invalid && control.touched ? 'errors.validation_failed' : null);
  }

  protected describedBy(control: FormControl<string>, fieldId: string): string | null {
    const ids: string[] = [];
    if (this.fieldErrorKey(control) !== null) ids.push(`${fieldId}-error`);
    if (this.formError() !== null) ids.push('login-form-alert');
    return ids.length > 0 ? ids.join(' ') : null;
  }
}
