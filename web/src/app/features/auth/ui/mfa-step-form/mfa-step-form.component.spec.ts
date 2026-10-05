import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup } from '@angular/forms';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { describe, expect, it, vi } from 'vitest';
import authEn from '../../../../../assets/i18n/auth/en.json';
import coreEn from '../../../../../assets/i18n/core/en.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import { onEmit } from '../../../../shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import type { MfaChallengeViewModel } from '../../domain/mfa.model';
import { MfaStepFormComponent, type MfaCodeControls } from './mfa-step-form.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: coreEn, 'auth/en': authEn, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

function viewModel(overrides: Partial<MfaChallengeViewModel> = {}): MfaChallengeViewModel {
  return {
    recoveryMode: false,
    submitting: false,
    canSubmit: true,
    formError: null,
    traceId: null,
    retryAfterSec: null,
    ...overrides,
  };
}

function codeForm(): FormGroup<MfaCodeControls> {
  return new FormGroup<MfaCodeControls>({ code: new FormControl('', { nonNullable: true }) });
}

async function setup(vm: MfaChallengeViewModel, form = codeForm()) {
  await TestBed.configureTestingModule({
    imports: [MfaStepFormComponent, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(MfaStepFormComponent);
  fixture.componentRef.setInput('form', form);
  fixture.componentRef.setInput('vm', vm);
  fixture.componentRef.setInput('fieldState', 'VALID');
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, form, root: fixture.nativeElement as HTMLElement };
}

describe('MfaStepFormComponent', () => {
  it('renders the TOTP step: heading, numeric one-time-code field and the three deck actions', async () => {
    const { root } = await setup(viewModel());
    const input = root.querySelector<HTMLInputElement>('#mfa-code');
    expect(input?.getAttribute('autocomplete')).toBe('one-time-code');
    expect(input?.getAttribute('inputmode')).toBe('numeric');
    expect(root.querySelector('[data-page-heading]')?.textContent).toContain(authEn.mfa.title);
    expect(root.querySelector('mat-label')?.textContent).toContain(authEn.mfa.code);
    const labels = [...root.querySelectorAll('button')].map((b) => b.textContent?.trim());
    expect(labels).toContain(authEn.mfa.submit);
    expect(labels).toContain(authEn.mfa.useRecovery);
    expect(labels).toContain(authEn.mfa.back);
  });

  it('recovery mode swaps the label to the recovery-code text and drops numeric inputmode', async () => {
    const { root } = await setup(viewModel({ recoveryMode: true }));
    const input = root.querySelector<HTMLInputElement>('#mfa-code');
    expect(input?.getAttribute('inputmode')).toBeNull();
    expect(root.querySelector('mat-label')?.textContent).toContain(authEn.mfa.recoveryCode);
    expect(root.querySelector('#mfa-code-hint')?.textContent).toContain(authEn.mfa.recoveryHint);
    const toggle = [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(authEn.mfa.useTotp));
    expect(toggle).toBeDefined();
  });

  it('emits submitCode on ngSubmit and toggleRecovery/backToPassword on their buttons', async () => {
    const { fixture, root } = await setup(viewModel());
    const submitCode = vi.fn();
    const toggleRecovery = vi.fn();
    const backToPassword = vi.fn();
    onEmit(fixture.componentInstance.submitCode, submitCode);
    onEmit(fixture.componentInstance.toggleRecovery, toggleRecovery);
    onEmit(fixture.componentInstance.backToPassword, backToPassword);

    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(submitCode).toHaveBeenCalledTimes(1);

    const buttons = [...root.querySelectorAll<HTMLButtonElement>('button')];
    buttons.find((b) => b.textContent?.includes(authEn.mfa.useRecovery))?.click();
    buttons.find((b) => b.textContent?.includes(authEn.mfa.back))?.click();
    expect(toggleRecovery).toHaveBeenCalledTimes(1);
    expect(backToPassword).toHaveBeenCalledTimes(1);
  });

  it('shows the server field error with aria wiring', async () => {
    // As the containers do: the server error is bound after the view exists (binding `formControlName` re-runs the
    // validators and would wipe an error set earlier), then the `fieldState` bridge input re-renders the form.
    const { fixture, form, root } = await setup(viewModel());
    form.controls.code.setErrors({ server: { messageKeys: ['errors.invalid_mfa_code'] } });
    form.controls.code.markAsTouched();
    fixture.componentRef.setInput('fieldState', form.controls.code.status);
    fixture.detectChanges();
    await fixture.whenStable();
    const error = root.querySelector('#mfa-code-error');
    expect(error?.textContent).toContain(errorsEn.invalid_mfa_code);
    const input = root.querySelector<HTMLInputElement>('#mfa-code');
    expect(input?.getAttribute('aria-describedby')).toContain('mfa-code-error');
    expect(input?.getAttribute('aria-invalid')).toBe('true');
  });

  it('renders the form-level alert and the trace id', async () => {
    const { root } = await setup(viewModel({ formError: 'errors.unknown_error', traceId: 'trace-1' }));
    const alert = root.querySelector('[data-form-alert]');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.textContent).toContain(errorsEn.unknown_error);
    expect(alert?.textContent).toContain('trace-1');
  });

  it('locks the field and the submit button while the countdown runs and emits elapsed', async () => {
    const { fixture, root } = await setup(viewModel({ retryAfterSec: 900, canSubmit: false }));
    const elapsed = vi.fn();
    onEmit(fixture.componentInstance.retryAfterElapsed, elapsed);
    const input = root.querySelector<HTMLInputElement>('#mfa-code');
    const submit = root.querySelector<HTMLButtonElement>('button[type="submit"]');
    expect(input?.hasAttribute('disabled')).toBe(true);
    expect(submit?.disabled).toBe(true);
    expect(submit?.getAttribute('aria-describedby')).toBe('mfa-retry');
    expect(root.querySelector('#mfa-retry')).not.toBeNull();
    expect(elapsed).not.toHaveBeenCalled();
  });

  it('disables the back and recovery-toggle buttons while the request is in flight', async () => {
    const { fixture, root } = await setup(viewModel({ submitting: true, canSubmit: false }));
    const toggleRecovery = vi.fn();
    const backToPassword = vi.fn();
    onEmit(fixture.componentInstance.toggleRecovery, toggleRecovery);
    onEmit(fixture.componentInstance.backToPassword, backToPassword);
    const mode = root.querySelector<HTMLButtonElement>('button.mfa-mode');
    const back = root.querySelector<HTMLButtonElement>('button.mfa-back');
    expect(mode?.disabled).toBe(true);
    expect(back?.disabled).toBe(true);
    mode?.click();
    back?.click();
    expect(toggleRecovery).not.toHaveBeenCalled();
    expect(backToPassword).not.toHaveBeenCalled();
  });
});
