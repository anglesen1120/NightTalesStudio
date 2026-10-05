import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  untracked,
  viewChild,
} from '@angular/core';
import { type AbstractControl, FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { merge } from 'rxjs';
import { type AppError, bindFieldErrors, clearServerErrors, type ServerControlError } from '@core/errors';
import { type SessionEndReason, SessionStore } from '@core/session';
import { traceIdOf } from '../../application/auth-failure';
import { LoginStore } from '../../application/login.store';
import type { MfaChallengeViewModel } from '../../domain/mfa.model';
import { AuthPageShellComponent } from '../../ui/auth-page-shell/auth-page-shell.component';
import { LoginFormComponent, type LoginFormControls } from '../../ui/login-form/login-form.component';
import { MfaStepFormComponent, type MfaCodeControls } from '../../ui/mfa-step-form/mfa-step-form.component';

const SESSION_KEY: Readonly<Record<SessionEndReason, string>> = {
  logout: 'core.session.signedOut',
  expired: 'core.session.expired',
  revoked: 'core.session.revoked',
  reused: 'core.session.reused',
  restore_failed: 'core.session.restoreFailed',
};

const INVALID_MFA_CODE_KEY = 'errors.invalid_mfa_code';

@Component({
  selector: 'nts-login-page',
  imports: [AuthPageShellComponent, LoginFormComponent, MfaStepFormComponent],
  providers: [LoginStore],
  templateUrl: './login-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginPageComponent {
  protected readonly store = inject(LoginStore);
  protected readonly traceId = computed(() => traceIdOf(this.store.error()));
  protected readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly formHost = viewChild<LoginFormComponent, ElementRef<HTMLElement>>(LoginFormComponent, { read: ElementRef });
  private bootRedirected = false;

  readonly form = new FormGroup<LoginFormControls>({
    email: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control), (control: AbstractControl) => Validators.email(control)] }),
    password: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
  });

  /** The single code field of the MFA step (TOTP or recovery code, one at a time; C10). */
  readonly mfaForm = new FormGroup<MfaCodeControls>({
    code: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
  });

  protected readonly fieldState = toSignal(merge(this.form.statusChanges, this.form.valueChanges), {
    initialValue: this.form.status,
  });
  protected readonly mfaFieldState = toSignal(merge(this.mfaForm.statusChanges, this.mfaForm.valueChanges), {
    initialValue: this.mfaForm.status,
  });
  private readonly valueState = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  private readonly mfaValueState = toSignal(this.mfaForm.valueChanges, { initialValue: this.mfaForm.getRawValue() });

  /** An invalid code is a field error on the code control (not the form alert); the rest follows the store. */
  protected readonly mfaViewModel = computed<MfaChallengeViewModel>(() => {
    const error = this.store.error();
    const fieldLevel = error !== null && (error.code === 'invalid_mfa_code' || Object.keys(error.fieldErrors).length > 0);
    return {
      recoveryMode: this.store.recoveryMode(),
      submitting: this.store.submitting(),
      canSubmit: this.store.canSubmit(),
      formError: this.store.retryAfterSec() === null && !fieldLevel ? this.store.formError() : null,
      traceId: this.traceId(),
      retryAfterSec: this.store.retryAfterSec(),
    };
  });

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
      this.mfaValueState();
      clearServerErrors(this.mfaForm);
    });

    effect(() => {
      const submitting = this.store.submitting();
      if (submitting && this.form.enabled) this.form.disable();
      if (!submitting && this.form.disabled) this.form.enable();
      if (submitting && this.mfaForm.enabled) this.mfaForm.disable();
      if (!submitting && this.mfaForm.disabled) this.mfaForm.enable();
    });

    // A step change (password <-> mfa) or a recovery-mode toggle starts from an empty code and moves focus to the new
    // step's heading (the value never lingers in a control of a hidden step).
    effect(() => {
      const step = this.store.step();
      this.store.recoveryMode();
      untracked(() => {
        this.mfaForm.reset({ code: '' });
        if (step === 'password') this.form.controls.password.setValue('');
      });
      afterNextRender(() => this.focusIn(this.host.nativeElement, '[data-page-heading]'), { injector: this.injector });
    });

    effect(() => {
      const error = this.store.error();
      if (error === null) return;
      if (this.store.step() === 'mfa') {
        this.showMfaError(error);
        return;
      }
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

  protected submitMfa(): void {
    this.mfaForm.markAllAsTouched();
    if (this.mfaForm.invalid) {
      this.focusIn(this.host.nativeElement, 'nts-mfa-step-form input');
      return;
    }
    clearServerErrors(this.mfaForm);
    void this.store.submitMfa(this.mfaForm.controls.code.getRawValue());
  }

  protected goToForgotPassword(): void {
    void this.router.navigateByUrl('/forgot-password');
  }

  private showMfaError(error: AppError): void {
    if (error.code === 'invalid_mfa_code') {
      const control = this.mfaForm.controls.code;
      const serverError: ServerControlError = { messageKeys: [INVALID_MFA_CODE_KEY] };
      control.setErrors({ ...control.errors, server: serverError });
      control.markAsTouched();
    } else if (Object.keys(error.fieldErrors).length > 0) {
      bindFieldErrors(this.mfaForm, error, { markTouched: true });
    }
    if (this.store.retryAfterSec() === null) queueMicrotask(() => this.focusIn(this.host.nativeElement, 'nts-mfa-step-form input'));
  }

  private focusFirstInvalid(): void {
    const control = this.form.controls.email.invalid ? '#login-email' : '#login-password';
    this.focus(control);
  }

  private focus(selector: string): void {
    this.formHost()?.nativeElement.querySelector<HTMLElement>(selector)?.focus();
  }

  private focusIn(root: HTMLElement, selector: string): void {
    root.querySelector<HTMLElement>(selector)?.focus();
  }
}
