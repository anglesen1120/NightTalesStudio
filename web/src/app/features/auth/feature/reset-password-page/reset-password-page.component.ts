import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { type AbstractControl, FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { merge } from 'rxjs';
import { bindFieldErrors, clearServerErrors } from '@core/errors';
import { traceIdOf } from '../../application/auth-failure';
import { ResetPasswordStore } from '../../application/reset-password.store';
import { AuthPageShellComponent } from '../../ui/auth-page-shell/auth-page-shell.component';
import { AuthStatePanelComponent } from '../../ui/auth-state-panel/auth-state-panel.component';
import {
  ResetPasswordFormComponent,
  type ResetPasswordFormControls,
} from '../../ui/reset-password-form/reset-password-form.component';

@Component({
  selector: 'nts-reset-password-page',
  imports: [AuthPageShellComponent, AuthStatePanelComponent, ResetPasswordFormComponent],
  providers: [ResetPasswordStore],
  templateUrl: './reset-password-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPasswordPageComponent {
  protected readonly store = inject(ResetPasswordStore);
  protected readonly traceId = computed(() => traceIdOf(this.store.error()));
  private readonly router = inject(Router);
  private readonly formHost = viewChild<ResetPasswordFormComponent, ElementRef<HTMLElement>>(ResetPasswordFormComponent, { read: ElementRef });
  private readonly stateHost = viewChild<AuthStatePanelComponent, ElementRef<HTMLElement>>(AuthStatePanelComponent, { read: ElementRef });

  readonly form = new FormGroup<ResetPasswordFormControls>({
    password: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control), Validators.minLength(12)] }),
    confirm: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
  });

  protected readonly fieldState = toSignal(merge(this.form.statusChanges, this.form.valueChanges), {
    initialValue: this.form.status,
  });
  private readonly valueState = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  constructor() {
    afterNextRender(() => this.focusCurrentHeading());

    effect(() => {
      const disabled = !this.store.hasToken() || this.store.submitting() || this.store.expired() || this.store.done();
      if (disabled && this.form.enabled) this.form.disable();
      if (!disabled && this.form.disabled) this.form.enable();
    });

    effect(() => {
      this.valueState();
      clearServerErrors(this.form);
      untracked(() => this.clearMismatch());
    });

    effect(() => {
      if (this.store.done()) {
        this.form.reset({ password: '', confirm: '' }, { emitEvent: true });
        queueMicrotask(() => this.focusCurrentHeading());
        return;
      }
      if (!this.store.hasToken() || this.store.expired()) {
        queueMicrotask(() => this.focusCurrentHeading());
        return;
      }
      if (this.store.mismatch()) {
        const confirm = this.form.controls.confirm;
        confirm.setErrors({ ...confirm.errors, mismatch: true });
        confirm.markAsTouched();
        queueMicrotask(() => this.focusForm('#reset-confirm'));
        return;
      }
      const error = this.store.error();
      if (error === null) return;
      if (Object.keys(error.fieldErrors).length > 0) {
        bindFieldErrors(this.form, error, { markTouched: true });
        queueMicrotask(() => this.focusFirstInvalid());
      } else if (this.store.retryAfterSec() === null) {
        queueMicrotask(() => this.focusForm('[data-form-alert]'));
      }
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.focusFirstInvalid();
      return;
    }
    clearServerErrors(this.form);
    const { password, confirm } = this.form.getRawValue();
    void this.store.submit(password, confirm);
  }

  /** Password or confirmation changed: drop the local `mismatch` error and the store's verdict so the next submit can run. */
  private clearMismatch(): void {
    const confirm = this.form.controls.confirm;
    if (confirm.errors?.['mismatch'] !== undefined) {
      const rest = { ...confirm.errors };
      delete rest['mismatch'];
      confirm.setErrors(Object.keys(rest).length === 0 ? null : rest);
    }
    this.store.clearMismatch();
  }

  protected goToForgotPassword(): void {
    void this.router.navigateByUrl('/forgot-password');
  }

  protected goToLogin(): void {
    void this.router.navigateByUrl('/login');
  }

  private focusFirstInvalid(): void {
    const selector = this.form.controls.password.invalid ? '#reset-password' : '#reset-confirm';
    this.focusForm(selector);
  }

  private focusCurrentHeading(): void {
    const host = this.stateHost()?.nativeElement ?? this.formHost()?.nativeElement;
    host?.querySelector<HTMLElement>('[data-page-heading]')?.focus();
  }

  private focusForm(selector: string): void {
    this.formHost()?.nativeElement.querySelector<HTMLElement>(selector)?.focus();
  }
}
