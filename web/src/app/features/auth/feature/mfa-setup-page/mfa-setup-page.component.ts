import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { type AbstractControl, FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { merge } from 'rxjs';
import { type AppError, bindFieldErrors, clearServerErrors, type ServerControlError } from '@core/errors';
import { downloadBlob } from '@shared/util/download-blob';
import { safeFilename } from '@shared/util/safe-filename';
import { EnrolmentStore } from '../../application/enrolment.store';
import { AuthPageShellComponent } from '../../ui/auth-page-shell/auth-page-shell.component';
import type { MfaCodeControls } from '../../ui/mfa-step-form/mfa-step-form.component';
import { MfaSetupViewComponent } from '../../ui/mfa-setup-view/mfa-setup-view.component';

const INVALID_MFA_CODE_KEY = 'errors.invalid_mfa_code';
const FULL_SESSION_HOME = '/dashboard';

/** `YYYYMMDD` of the local date, for `recovery-codes-YYYYMMDD.txt`. */
function datestamp(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}${month}${day}`;
}

/**
 * Route container of `/account/security/mfa-setup` over a page-scoped `EnrolmentStore` (destroyed with the route, which
 * drops the secret and the codes). It owns the verify-code `FormGroup` and the recovery-code download; the secret, the
 * URI and the codes only flow store -> view model -> presentational view, never to a log, storage or an attribute.
 */
@Component({
  selector: 'nts-mfa-setup-page',
  imports: [AuthPageShellComponent, MfaSetupViewComponent],
  providers: [EnrolmentStore],
  templateUrl: './mfa-setup-page.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MfaSetupPageComponent {
  protected readonly store = inject(EnrolmentStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly form = new FormGroup<MfaCodeControls>({
    code: new FormControl('', { nonNullable: true, validators: [(control: AbstractControl) => Validators.required(control)] }),
  });

  protected readonly fieldState = toSignal(merge(this.form.statusChanges, this.form.valueChanges), { initialValue: this.form.status });

  /** The code value of the last submit; any pending server error belongs to it (D2: no `valueChanges` subscribe). */
  private lastCode = '';

  constructor() {
    void this.store.load();

    effect(() => {
      const submitting = this.store.submitting();
      if (submitting && this.form.enabled) this.form.disable();
      if (!submitting && this.form.disabled) this.form.enable();
    });

    // Every step change starts from an empty code field and moves focus to the new step's heading.
    effect(() => {
      this.store.step();
      untracked(() => {
        this.lastCode = '';
        this.form.reset({ code: '' });
      });
      afterNextRender(() => {
        const host = this.host.nativeElement;
        // A setup/regeneration dialog can open before this deferred step-focus callback. The modal owns focus.
        if (host.ownerDocument.querySelector('[role="dialog"], [role="alertdialog"]') !== null) return;
        host.querySelector<HTMLElement>('[data-page-heading]')?.focus();
      }, { injector: this.injector });
    });

    effect(() => {
      const error = this.store.error();
      // The code input may only be created by this very change (show -> verify); binding it to the control re-runs the
      // validators and wipes a manually set server error, so the error is applied once the view is rendered.
      if (error !== null) afterNextRender(() => this.showError(error), { injector: this.injector });
    });
  }

  protected verifyCode(): void {
    const code = this.form.controls.code.getRawValue();
    // A server error belongs to the code that produced it: a different code drops it synchronously, before the
    // `form.invalid` guard, so the corrected code is not blocked by a stale server error. An unchanged value
    // (e.g. an enable()/disable() emission) leaves the current error intact.
    if (code !== this.lastCode) clearServerErrors(this.form);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.focusCode();
      return;
    }
    this.lastCode = code;
    void this.store.verify(code);
  }

  protected confirmSavedChange(value: boolean): void {
    this.store.setConfirmedSaved(value);
  }

  /** Completes the codes gate, or retries navigation when an earlier dashboard navigation left this page mounted. */
  protected finish(): void {
    const completed = this.store.step() === 'done' || this.store.finish();
    if (completed && !this.store.enrolmentOnly()) void this.router.navigateByUrl(FULL_SESSION_HOME);
  }

  protected regenerate(): void {
    void this.store.regenerate();
  }

  protected retry(): void {
    void this.store.load();
  }

  protected reLogin(): void {
    void this.store.leaveToLogin();
  }

  /** One `.txt` of the 10 codes through the shared sanitising download path (D4); a no-op once the codes are gone. */
  protected download(): void {
    const codes = this.store.codes();
    if (codes === null) return;
    const text = `NightTales recovery codes\n\n${codes.join('\n')}\n`;
    downloadBlob(new Blob([text], { type: 'text/plain' }), safeFilename(`recovery-codes-${datestamp(new Date())}.txt`));
  }

  private showError(error: AppError): void {
    if (error.code === 'invalid_mfa_code') {
      const control = this.form.controls.code;
      const serverError: ServerControlError = { messageKeys: [INVALID_MFA_CODE_KEY] };
      control.setErrors({ ...control.errors, server: serverError });
      control.markAsTouched();
      if (this.store.retryAfterSec() === null) queueMicrotask(() => this.focusCode());
    } else if (Object.keys(error.fieldErrors).length > 0) {
      bindFieldErrors(this.form, error, { markTouched: true });
    }
  }

  private focusCode(): void {
    this.host.nativeElement.querySelector<HTMLElement>('[data-code-field]')?.focus();
  }
}
