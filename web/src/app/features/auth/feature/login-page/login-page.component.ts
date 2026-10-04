import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  viewChild,
} from '@angular/core';
import { type AbstractControl, FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { merge } from 'rxjs';
import { bindFieldErrors, clearServerErrors } from '@core/errors';
import { type SessionEndReason, SessionStore } from '@core/session';
import { traceIdOf } from '../../application/auth-failure';
import { LoginStore } from '../../application/login.store';
import { AuthPageShellComponent } from '../../ui/auth-page-shell/auth-page-shell.component';
import { LoginFormComponent, type LoginFormControls } from '../../ui/login-form/login-form.component';

const SESSION_KEY: Readonly<Record<SessionEndReason, string>> = {
  logout: 'core.session.signedOut',
  expired: 'core.session.expired',
  revoked: 'core.session.revoked',
  reused: 'core.session.reused',
  restore_failed: 'core.session.restoreFailed',
};

@Component({
  selector: 'nts-login-page',
  imports: [AuthPageShellComponent, LoginFormComponent],
  providers: [LoginStore],
  templateUrl: './login-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginPageComponent {
  protected readonly store = inject(LoginStore);
  protected readonly traceId = computed(() => traceIdOf(this.store.error()));
  protected readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly formHost = viewChild<LoginFormComponent, ElementRef<HTMLElement>>(LoginFormComponent, { read: ElementRef });
  private bootRedirected = false;

  readonly form = new FormGroup<LoginFormControls>({
    email: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control), (control: AbstractControl) => Validators.email(control)] }),
    password: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
  });

  protected readonly fieldState = toSignal(merge(this.form.statusChanges, this.form.valueChanges), {
    initialValue: this.form.status,
  });
  private readonly valueState = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });


  protected readonly sessionEndKey = () => {
    const reason = this.session.endReason();
    return reason === null ? null : SESSION_KEY[reason];
  };

  constructor() {
    afterNextRender(() => this.focus('[data-page-heading]'));
    effect(() => {
      this.valueState();
      clearServerErrors(this.form);
    });


    effect(() => {
      const submitting = this.store.submitting();
      if (submitting && this.form.enabled) this.form.disable();
      if (!submitting && this.form.disabled) this.form.enable();
    });

    effect(() => {
      const error = this.store.error();
      if (error === null) return;
      if (Object.keys(error.fieldErrors).length > 0) {
        bindFieldErrors(this.form, error, { markTouched: true });
        queueMicrotask(() => this.focusFirstInvalid());
        return;
      }
      if (error.code === 'invalid_credentials') {
        this.form.controls.password.setValue('');
        queueMicrotask(() => this.focus('#login-password'));
        return;
      }
      if (error.kind === 'server' || error.code === 'unknown_error') this.form.controls.password.setValue('');
      if (this.store.retryAfterSec() === null) queueMicrotask(() => this.focus('[data-form-alert]'));
    });

    effect(() => {
      // Boot restore only: this page's own sign-in navigates once, to the sanitised returnUrl (LoginStore).
      if (!this.session.isAuthenticated() || this.store.userSignedIn() || this.bootRedirected) return;
      this.bootRedirected = true;
      void this.router.navigateByUrl('/dashboard');
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.focusFirstInvalid();
      return;
    }
    clearServerErrors(this.form);
    const { email, password } = this.form.getRawValue();
    void this.store.submitPassword(email, password);
  }

  protected goToForgotPassword(): void {
    void this.router.navigateByUrl('/forgot-password');
  }

  private focusFirstInvalid(): void {
    const control = this.form.controls.email.invalid ? '#login-email' : '#login-password';
    this.focus(control);
  }

  private focus(selector: string): void {
    this.formHost()?.nativeElement.querySelector<HTMLElement>(selector)?.focus();
  }
}
