import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { FormControl, FormGroup } from '@angular/forms';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import authEn from '../../../../../assets/i18n/auth/en.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import { onEmit } from '../../../../shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import type { MfaSetupViewModel } from '../../domain/mfa.model';
import type { MfaCodeControls } from '../mfa-step-form/mfa-step-form.component';
import { MfaSetupViewComponent } from './mfa-setup-view.component';

vi.mock('qrcode', () => ({ toDataURL: vi.fn() }));

const { toDataURL } = await import('qrcode');
// `qrcode.toDataURL` is overloaded (promise and callback); pin the promise overload the component calls.
const toDataURLMock = vi.mocked(toDataURL) as unknown as MockInstance<
  (uri: string, options?: unknown) => Promise<string>
>;

const translations = TranslocoTestingModule.forRoot({
  langs: { en: {}, 'auth/en': authEn, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

const SETUP = { secret: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/NightTales:owner?secret=JBSWY3DPEHPK3PXP' };
const CODES = ['code-01', 'code-02', 'code-03', 'code-04', 'code-05', 'code-06', 'code-07', 'code-08', 'code-09', 'code-10'];

function viewModel(overrides: Partial<MfaSetupViewModel> = {}): MfaSetupViewModel {
  return {
    step: 'show',
    setup: SETUP,
    codes: null,
    enrolmentOnly: false,
    submitting: false,
    canSubmit: true,
    formError: null,
    traceId: null,
    retryAfterSec: null,
    confirmedSaved: false,
    reauthRequired: false,
    reLoginRequired: false,
    ...overrides,
  };
}

function codeForm(): FormGroup<MfaCodeControls> {
  return new FormGroup<MfaCodeControls>({ code: new FormControl('', { nonNullable: true }) });
}

async function setupView(vm: MfaSetupViewModel, form = codeForm()) {
  await TestBed.configureTestingModule({
    imports: [MfaSetupViewComponent, translations],
    providers: ZONELESS_TEST_PROVIDERS,
    deferBlockBehavior: DeferBlockBehavior.Manual,
  }).compileComponents();
  const fixture = TestBed.createComponent(MfaSetupViewComponent);
  fixture.componentRef.setInput('form', form);
  fixture.componentRef.setInput('vm', vm);
  fixture.componentRef.setInput('fieldState', 'VALID');
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, form, root: fixture.nativeElement as HTMLElement };
}

async function renderDeferBlocks(fixture: { getDeferBlocks(): Promise<{ render(state: number): Promise<void> }[]> }) {
  for (const block of await fixture.getDeferBlocks()) {
    await block.render(DeferBlockState.Complete);
  }
}

describe('MfaSetupViewComponent', () => {
  beforeEach(() => {
    toDataURLMock.mockReset();
    toDataURLMock.mockResolvedValue('data:image/png;base64,QR');
  });

  it('loading step renders only the silent skeleton', async () => {
    const { root } = await setupView(viewModel({ step: 'loading', setup: null }));
    expect(root.querySelector('nts-loading-state')).not.toBeNull();
    expect(root.querySelector('#enrol-code')).toBeNull();
  });

  it('show step renders the QR via @defer, the grouped manual key, its copy button and the code form', async () => {
    const { fixture, root } = await setupView(viewModel());
    await renderDeferBlocks(fixture);
    await fixture.whenStable();

    // The deferred QR renders after `afterNextRender` + the lazy `import('qrcode')` resolve, so poll for it.
    await vi.waitFor(() => {
      expect(root.querySelector<HTMLImageElement>('nts-mfa-qr img')?.src).toContain('data:image/png;base64,QR');
    });
    const img = root.querySelector<HTMLImageElement>('nts-mfa-qr img');
    expect(img?.alt).toBe(authEn.enrol.qrAlt);

    const keyInput = root.querySelector<HTMLInputElement>('#enrol-key');
    expect(keyInput?.value).toBe('JBSW Y3DP EHPK 3PXP');
    const copyButton = [...root.querySelectorAll('button')].find((b) =>
      b.textContent?.includes(authEn.enrol.copy),
    );
    expect(copyButton).toBeDefined();

    const code = root.querySelector<HTMLInputElement>('#enrol-code');
    expect(code?.getAttribute('autocomplete')).toBe('one-time-code');
    expect(code?.getAttribute('inputmode')).toBe('numeric');
    // The code field precedes the readonly key in the page's error focus path via [data-code-field].
    expect(code?.hasAttribute('data-code-field')).toBe(true);
    const verify = [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(authEn.enrol.verify));
    expect(verify).toBeDefined();
  });

  it.each([
    ['placeholder', DeferBlockState.Placeholder],
    ['loading', DeferBlockState.Loading],
    ['error', DeferBlockState.Error],
  ])('keeps the %s QR state in its slot beside the usable manual enrolment form', async (_name, state) => {
    const { fixture, root } = await setupView(viewModel());
    const [block] = await fixture.getDeferBlocks();
    await block!.render(state);
    const slot = root.querySelector('.enrol-layout > .enrol-qr-slot');
    expect(slot).not.toBeNull();
    expect(root.querySelector('.enrol-layout')?.children).toHaveLength(2);
    if (state === DeferBlockState.Error) {
      expect(slot?.querySelector('nts-error-state')).not.toBeNull();
      const retry = vi.fn();
      onEmit(fixture.componentInstance.retry, retry);
      slot?.querySelector<HTMLButtonElement>('button')?.click();
      expect(retry).toHaveBeenCalledTimes(1);
    } else {
      expect(slot?.querySelector('.enrol-qr-placeholder')).not.toBeNull();
    }
    expect(root.querySelector<HTMLInputElement>('#enrol-key')?.value).toBe('JBSW Y3DP EHPK 3PXP');
    expect(root.querySelector<HTMLInputElement>('#enrol-code')?.disabled).toBe(false);
    const verify = vi.fn();
    onEmit(fixture.componentInstance.verifyCode, verify);
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(verify).toHaveBeenCalledTimes(1);
  });

  it('QR failure keeps the image empty; the inner retry re-calls toDataURL', async () => {
    toDataURLMock.mockRejectedValue(new Error('bad'));
    const { fixture, root } = await setupView(viewModel());
    await renderDeferBlocks(fixture);
    await fixture.whenStable();

    // The deferred QR's failure state appears after `afterNextRender` + the rejected `import('qrcode')` resolve.
    await vi.waitFor(() => {
      expect(root.querySelector('nts-error-state')?.textContent).toContain(authEn.enrol.qrFailed);
    });
    expect(root.querySelector('.enrol-qr-slot nts-mfa-qr nts-error-state')).not.toBeNull();
    expect(root.querySelector<HTMLInputElement>('#enrol-key')?.value).toBe('JBSW Y3DP EHPK 3PXP');
    expect(root.querySelector<HTMLInputElement>('#enrol-code')?.disabled).toBe(false);
    const retryButton = [...root.querySelectorAll<HTMLButtonElement>('nts-error-state button')].find((b) =>
      b.textContent?.includes(errorsEn.action.retry),
    );
    expect(retryButton).toBeDefined();

    toDataURLMock.mockResolvedValue('data:image/png;base64,QR2');
    retryButton?.click();
    await fixture.whenStable();
    await vi.waitFor(() => {
      expect(root.querySelector<HTMLImageElement>('nts-mfa-qr img')?.src).toContain('QR2');
    });
    expect(toDataURLMock).toHaveBeenCalledTimes(2);
  });

  it('emits verifyCode on form submit', async () => {
    const { fixture, root } = await setupView(viewModel());
    const verify = vi.fn();
    onEmit(fixture.componentInstance.verifyCode, verify);
    root.querySelector('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(verify).toHaveBeenCalledTimes(1);
  });

  it('codes step lists 10 codes with copy buttons and the role=alert shown-once warning', async () => {
    const { root } = await setupView(viewModel({ step: 'codes', codes: CODES }));
    const warning = root.querySelector('[role="alert"]');
    expect(warning?.textContent).toContain(authEn.enrol.recoveryWarning);
    const items = [...root.querySelectorAll<HTMLElement>('.enrol-codes li')];
    expect(items).toHaveLength(10);
    expect(items[0]?.textContent).toContain('code-01');
    expect(root.querySelectorAll('.enrol-codes .enrol-copy').length).toBe(10);
  });

  it.each([
    ['errors.network_error', errorsEn.network_error],
    ['errors.service_unavailable', errorsEn.service_unavailable],
  ])('codes step renders the %s failure as a live alert and retries regeneration', async (formError, message) => {
    const { fixture, root } = await setupView(viewModel({ step: 'codes', codes: CODES, formError }));
    const regenerate = vi.fn();
    onEmit(fixture.componentInstance.regenerate, regenerate);

    const alert = root.querySelector<HTMLElement>('#enrol-codes-form-alert');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.getAttribute('tabindex')).toBe('-1');
    expect(alert?.textContent).toContain(message);
    [...(alert?.querySelectorAll<HTMLButtonElement>('button') ?? [])]
      .find((button) => button.textContent?.includes(errorsEn.action.retry))
      ?.click();
    expect(regenerate).toHaveBeenCalledTimes(1);
  });

  it('codes step renders cancelled re-auth as a status and retries regeneration', async () => {
    const { fixture, root } = await setupView(
      viewModel({ step: 'codes', codes: CODES, reauthRequired: true }),
    );
    const regenerate = vi.fn();
    onEmit(fixture.componentInstance.regenerate, regenerate);

    const banner = root.querySelector<HTMLElement>('.enrol-banner');
    expect(banner?.getAttribute('role')).toBe('status');
    expect(banner?.textContent).toContain(authEn.enrol.reauthTitle);
    expect(banner?.textContent).toContain(errorsEn.reauth_required);
    banner?.querySelector<HTMLButtonElement>('button')?.click();
    expect(regenerate).toHaveBeenCalledTimes(1);
  });

  it('codes step renders the rate-limit countdown and locks regeneration', async () => {
    const { root } = await setupView(
      viewModel({ step: 'codes', codes: CODES, formError: 'errors.rate_limited', retryAfterSec: 120 }),
    );

    expect(root.querySelector('#enrol-codes-retry nts-retry-countdown')).not.toBeNull();
    expect(root.querySelector('#enrol-codes-retry')?.textContent).toContain(authEn.rateLimited.message);
    const regenerate = [...root.querySelectorAll<HTMLButtonElement>('button')].find((button) =>
      button.textContent?.includes(authEn.enrol.regenerate),
    );
    expect(regenerate?.disabled).toBe(true);
  });

  it('codes step hides regeneration for an enrolment-only visit', async () => {
    const { root } = await setupView(viewModel({ step: 'codes', codes: CODES, enrolmentOnly: true }));
    expect(root.textContent).not.toContain(authEn.enrol.regenerate);
  });

  it('primary action is disabled until confirmSaved and emits finish when allowed', async () => {
    const { fixture, root } = await setupView(viewModel({ step: 'codes', codes: CODES, confirmedSaved: false }));
    const finish = vi.fn();
    const confirmSaved = vi.fn();
    onEmit(fixture.componentInstance.finish, finish);
    onEmit(fixture.componentInstance.confirmSavedChange, confirmSaved);

    const primary = [...root.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(authEn.enrol.continue),
    );
    expect(primary?.disabled).toBe(true);

    const checkbox = root.querySelector<HTMLInputElement>('.enrol-confirm input');
    checkbox?.click();
    expect(confirmSaved).toHaveBeenCalledWith(true);
    expect(primary?.disabled).toBe(true); // vm().confirmedSaved is container-owned; still false

    fixture.componentRef.setInput('vm', viewModel({ step: 'codes', codes: CODES, confirmedSaved: true }));
    fixture.detectChanges();
    primary?.click();
    expect(finish).toHaveBeenCalledTimes(1);
  });

  it('download is disabled until codes exist and emits download', async () => {
    const { fixture, root } = await setupView(viewModel({ step: 'codes', codes: CODES }));
    const download = vi.fn();
    onEmit(fixture.componentInstance.download, download);
    const downloadButton = [...root.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(authEn.enrol.download),
    );
    expect(downloadButton?.disabled).toBe(false);
    downloadButton?.click();
    expect(download).toHaveBeenCalledTimes(1);
  });

  it('regenerate emits regenerate', async () => {
    const { fixture, root } = await setupView(viewModel({ step: 'codes', codes: CODES }));
    const regenerate = vi.fn();
    onEmit(fixture.componentInstance.regenerate, regenerate);
    const regenButton = [...root.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(authEn.enrol.regenerate),
    );
    regenButton?.click();
    expect(regenerate).toHaveBeenCalledTimes(1);
  });

  it('re-login state shows the reLogin action', async () => {
    const { fixture, root } = await setupView(viewModel({ step: 'error', reLoginRequired: true }));
    const reLogin = vi.fn();
    onEmit(fixture.componentInstance.reLogin, reLogin);
    expect(root.querySelectorAll('h1, h2')).toHaveLength(1);
    expect(root.querySelector('h1')?.textContent?.trim()).toBe(authEn.enrol.expiredTitle);
    expect(root.querySelector('h1')?.hasAttribute('data-page-heading')).toBe(true);
    expect(root.querySelector('h1')?.getAttribute('tabindex')).toBe('-1');
    expect(root.textContent).toContain(authEn.enrol.expiredBody);
    const reLoginButton = [...root.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(authEn.enrol.reLogin),
    );
    reLoginButton?.click();
    expect(reLogin).toHaveBeenCalledTimes(1);
  });

  it('cancelled setup reauth shows the required banner instead of a generic error and emits retry', async () => {
    const { fixture, root } = await setupView(viewModel({ reauthRequired: true, setup: null, canSubmit: false }));
    const retry = vi.fn();
    onEmit(fixture.componentInstance.retry, retry);
    expect(root.querySelector('.enrol-banner')?.textContent).toContain(authEn.enrol.reauthTitle);
    expect(root.querySelector('.enrol-banner')?.textContent).toContain(authEn.enrol.reauthBody);
    expect(root.querySelector('nts-error-state')).toBeNull();
    expect(root.textContent).not.toContain(errorsEn.unknown_error);
    expect(root.querySelector<HTMLButtonElement>('form button[type="submit"]')?.disabled).toBe(true);
    const retryButton = [...root.querySelectorAll<HTMLButtonElement>('.enrol-banner button')].find((b) =>
      b.textContent?.includes(errorsEn.action.retry),
    );
    retryButton?.click();
    expect(retry).toHaveBeenCalledTimes(1);
  });

  it('the countdown locks the verify button', async () => {
    const { root } = await setupView(viewModel({ retryAfterSec: 120 }));
    const code = root.querySelector<HTMLInputElement>('#enrol-code');
    const verify = [...root.querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      b.textContent?.includes(authEn.enrol.verify),
    );
    expect(code?.hasAttribute('disabled')).toBe(true);
    expect(verify?.disabled).toBe(true);
  });

  it('done step emits reLogin for enrolment-only tokens', async () => {
    const { fixture, root } = await setupView(viewModel({ step: 'done', enrolmentOnly: true }));
    const reLogin = vi.fn();
    onEmit(fixture.componentInstance.reLogin, reLogin);
    [...root.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => b.textContent?.includes(authEn.enrol.reLogin))
      ?.click();
    expect(reLogin).toHaveBeenCalledTimes(1);
  });

  it('done step emits finish for full sessions', async () => {
    const { fixture, root } = await setupView(viewModel({ step: 'done', enrolmentOnly: false }));
    const finish = vi.fn();
    onEmit(fixture.componentInstance.finish, finish);
    [...root.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => b.textContent?.includes(authEn.enrol.toDashboard))
      ?.click();
    expect(finish).toHaveBeenCalledTimes(1);
  });
});
