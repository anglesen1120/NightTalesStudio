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
import { toSignal } from '@angular/core/rxjs-interop';
import { type AbstractControl, FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { merge } from 'rxjs';
import { bindFieldErrors, clearServerErrors } from '@core/errors';
import { traceIdOf } from '../../application/auth-failure';
import { ForgotPasswordStore } from '../../application/forgot-password.store';
import { AuthPageShellComponent } from '../../ui/auth-page-shell/auth-page-shell.component';
import { AuthStatePanelComponent } from '../../ui/auth-state-panel/auth-state-panel.component';
import {
  ForgotPasswordFormComponent,
  type ForgotPasswordFormControls,
} from '../../ui/forgot-password-form/forgot-password-form.component';

@Component({
  selector: 'nts-forgot-password-page',
  imports: [AuthPageShellComponent, AuthStatePanelComponent, ForgotPasswordFormComponent],
  providers: [ForgotPasswordStore],
  templateUrl: './forgot-password-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPasswordPageComponent {
  protected readonly store = inject(ForgotPasswordStore);
  protected readonly traceId = computed(() => traceIdOf(this.store.error()));
  private readonly router = inject(Router);
  private readonly formHost = viewChild<ForgotPasswordFormComponent, ElementRef<HTMLElement>>(ForgotPasswordFormComponent, { read: ElementRef });
  private readonly stateHost = viewChild<AuthStatePanelComponent, ElementRef<HTMLElement>>(AuthStatePanelComponent, { read: ElementRef });

  readonly form = new FormGroup<ForgotPasswordFormControls>({
    email: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control), (control: AbstractControl) => Validators.email(control)] }),
  });

  protected readonly fieldState = toSignal(merge(this.form.statusChanges, this.form.valueChanges), {
    initialValue: this.form.status,
  });
  private readonly valueState = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });


  constructor() {
    afterNextRender(() => this.focusForm('[data-page-heading]'));
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
      if (this.store.sent()) {
        queueMicrotask(() => this.stateHost()?.nativeElement.querySelector<HTMLElement>('[data-page-heading]')?.focus());
        return;
      }
      const error = this.store.error();
      if (error === null) return;
      if (Object.keys(error.fieldErrors).length > 0) {
        bindFieldErrors(this.form, error, { markTouched: true });
        queueMicrotask(() => this.focusForm('#forgot-email'));
      } else if (this.store.retryAfterSec() === null) {
        queueMicrotask(() => this.focusForm('[data-form-alert]'));
      }
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.focusForm('#forgot-email');
      return;
    }
    clearServerErrors(this.form);
    void this.store.submit(this.form.getRawValue().email);
  }

  protected goToLogin(): void {
    void this.router.navigateByUrl('/login');
  }

  private focusForm(selector: string): void {
    this.formHost()?.nativeElement.querySelector<HTMLElement>(selector)?.focus();
  }
}
