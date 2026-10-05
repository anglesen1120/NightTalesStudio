import { ChangeDetectionStrategy, Component, effect, input, output, signal, untracked } from '@angular/core';
import { type FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { ErrorState, IconCheck, IconCopy, LoadingState, RetryCountdown, Stepper, type StepperStep } from '@shared/ui';
import type { EnrolmentStep, MfaSetupViewModel } from '../../domain/mfa.model';
import type { MfaCodeControls } from '../mfa-step-form/mfa-step-form.component';
import { MfaQrComponent } from '../mfa-qr/mfa-qr.component';

/** The base32 key is shown grouped (JBSW Y3DP …) in the deck; the grouped form is also what copy writes. */
function groupedSecret(secret: string): string {
  return secret.replace(/\s+/g, '').replace(/(.{4})/g, '$1 ').trim();
}

type CopyResultKey = 'auth.enrol.copied' | 'core.ui.copyableCode.unavailable' | null;

/**
 * `/account/security/mfa-setup` (deck `f-enrol-*`): loading skeleton -> key show + code verify -> the ten recovery
 * codes revealed exactly once behind a `role="alert"` warning and the `confirmSaved` gate -> done. The cancelled
 * re-auth state keeps the frame plus a retry; a spent enrolment token swaps the whole view for the expired error.
 * Every control is an intent output; the secrets reach the DOM only as the QR pixels and the rendered key/code text
 * (properties and text, never attributes, logs or storage).
 */
@Component({
  selector: 'nts-mfa-setup-view',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TranslocoPipe,
    Stepper,
    ErrorState,
    LoadingState,
    RetryCountdown,
    IconCopy,
    IconCheck,
    MfaQrComponent,
  ],
  providers: [provideTranslocoScope('auth', 'errors')],
  templateUrl: './mfa-setup-view.component.html',
  styleUrl: './mfa-setup-view.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MfaSetupViewComponent {
  readonly form = input.required<FormGroup<MfaCodeControls>>();
  readonly vm = input.required<MfaSetupViewModel>();
  readonly fieldState = input<unknown>();

  readonly verifyCode = output<void>();
  readonly confirmSavedChange = output<boolean>();
  readonly finish = output<void>();
  readonly regenerate = output<void>();
  readonly retry = output<void>();
  readonly download = output<void>();
  readonly reLogin = output<void>();
  readonly retryAfterElapsed = output<void>();

  /** What the last copy attempt did; rendered in the aria-live region. */
  protected readonly copyResult = signal<CopyResultKey>(null);

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

  /** The 429 lock: the field and the verify button are both disabled until the countdown ends. */
  protected locked(): boolean {
    return this.vm().retryAfterSec !== null;
  }

  protected steps(): readonly StepperStep[] {
    return enrolmentSteps(this.vm().step);
  }

  /** Done state: an enrolment-only token opens no other route, so it signs in again; a full session goes home. */
  protected onDoneAction(): void {
    if (this.vm().enrolmentOnly) this.reLogin.emit();
    else this.finish.emit();
  }

  protected manualKey(): string {
    const setup = this.vm().setup;
    return setup === null ? '' : groupedSecret(setup.secret);
  }

  protected fieldErrorKey(): string | null {
    this.fieldState();
    const control = this.form().controls.code;
    const server = control.errors?.['server'] as { readonly messageKeys?: readonly string[] } | undefined;
    return server?.messageKeys?.[0] ?? (control.invalid && control.touched ? 'errors.validation_failed' : null);
  }

  protected codeDescribedBy(): string | null {
    const ids: string[] = [];
    if (this.fieldErrorKey() !== null) ids.push('enrol-code-error');
    if (this.vm().formError !== null) ids.push('enrol-form-alert');
    return ids.length > 0 ? ids.join(' ') : null;
  }

  protected onConfirmSaved(event: Event): void {
    this.confirmSavedChange.emit(event.target instanceof HTMLInputElement && event.target.checked);
  }

  /** Clipboard where available; otherwise select the rendered text so the user can copy it by hand. */
  protected async copyCode(code: string, element: HTMLElement | null): Promise<void> {
    const key = await this.tryCopy(code, element);
    await this.announce(key);
  }

  protected async copyManualKey(inputEl: HTMLInputElement): Promise<void> {
    const key = await this.tryCopy(inputEl.value, inputEl);
    await this.announce(key);
  }

  private async tryCopy(text: string, element: HTMLElement | null): Promise<Exclude<CopyResultKey, null>> {
    try {
      if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText !== undefined) {
        await navigator.clipboard.writeText(text);
        return 'auth.enrol.copied';
      }
    } catch {
      // Fall through to the selection fallback.
    }
    return this.selectText(element) ? 'auth.enrol.copied' : 'core.ui.copyableCode.unavailable';
  }

  private selectText(element: HTMLElement | null): boolean {
    try {
      if (element instanceof HTMLInputElement) {
        element.select();
        return true;
      }
      const target = element instanceof HTMLElement ? element : null;
      if (target === null) return false;
      const view = target.ownerDocument.defaultView;
      const selection = view?.getSelection();
      if (selection === null || selection === undefined) return false;
      const range = target.ownerDocument.createRange();
      range.selectNodeContents(target);
      selection.removeAllRanges();
      selection.addRange(range);
      return true;
    } catch {
      return false;
    }
  }

  /** Clear then re-set on the next task so a repeated identical result is announced again (CopyableCode pattern). */
  private async announce(key: Exclude<CopyResultKey, null>): Promise<void> {
    this.copyResult.set(null);
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    this.copyResult.set(key);
  }
}

/** Deck `ol.enrol-steps`: 1 get the key, 2 confirm the code, 3 save the recovery codes. */
function enrolmentSteps(step: EnrolmentStep): readonly StepperStep[] {
  const at = (index: number): StepperStep['state'] => {
    const current = step === 'loading' ? 1 : step === 'show' || step === 'verify' ? 2 : 3;
    return index < current ? 'done' : index === current ? 'current' : 'todo';
  };
  return [
    { labelKey: 'auth.enrol.stepKey', state: at(1) },
    { labelKey: 'auth.enrol.stepVerify', state: at(2) },
    { labelKey: 'auth.enrol.stepCodes', state: at(3) },
  ];
}
