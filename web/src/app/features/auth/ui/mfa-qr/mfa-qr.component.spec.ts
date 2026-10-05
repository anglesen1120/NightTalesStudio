import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest';
import authEn from '../../../../../assets/i18n/auth/en.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { MfaQrComponent } from './mfa-qr.component';

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

async function setup(uri = 'otpauth://totp/NightTales:owner?secret=JBSWY3DPEHPK3PXP') {
  await TestBed.configureTestingModule({
    imports: [MfaQrComponent, translations],
    providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(MfaQrComponent);
  fixture.componentRef.setInput('otpauthUri', uri);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

describe('MfaQrComponent', () => {
  beforeEach(() => {
    toDataURLMock.mockReset();
  });

  it('renders the generated data URL with the translated alt text', async () => {
    toDataURLMock.mockResolvedValue('data:image/png;base64,QR');
    const { root } = await setup();
    // The QR renders after `afterNextRender` + the lazy `import('qrcode')` resolve, so poll for the image.
    await vi.waitFor(() => {
      expect(root.querySelector<HTMLImageElement>('img')?.src).toContain('data:image/png;base64,QR');
    });
    const img = root.querySelector<HTMLImageElement>('img');
    expect(img?.alt).toBe(authEn.enrol.qrAlt);
    expect(toDataURLMock).toHaveBeenCalledTimes(1);
    expect(toDataURLMock.mock.calls[0]?.[0]).toContain('otpauth://totp/');
  });

  it('shows an error state with retry when generation fails', async () => {
    toDataURLMock.mockRejectedValue(new Error('bad uri'));
    const { fixture, root } = await setup('not-otpauth');
    expect(root.querySelector('img')).toBeNull();
    await vi.waitFor(() => {
      expect(root.querySelector('nts-error-state')?.textContent).toContain(authEn.enrol.qrFailed);
    });
    const errorState = root.querySelector('nts-error-state');
    const retry = errorState?.querySelector<HTMLButtonElement>('button');
    expect(retry?.textContent).toContain(errorsEn.action.retry);

    toDataURLMock.mockResolvedValue('data:image/png;base64,RETRY');
    retry?.click();
    await fixture.whenStable();
    await vi.waitFor(() => {
      expect(root.querySelector<HTMLImageElement>('img')?.src).toContain('RETRY');
    });
    expect(toDataURLMock).toHaveBeenCalledTimes(2);
  });
});
