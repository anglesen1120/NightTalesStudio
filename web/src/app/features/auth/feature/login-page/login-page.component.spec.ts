import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { TranslocoTestingModule, provideTranslocoScope } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AppError, Result } from '@core/errors';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import authEn from '../../../../../assets/i18n/auth/en.json';
import coreEn from '../../../../../assets/i18n/core/en.json';
import errorsEn from '../../../../../assets/i18n/errors/en.json';
import { AuthRepository } from '../../application/auth.repository';
import type { LoginOutcome } from '../../domain/auth.model';
import { FakeAuthRepository } from '../../testing/fake-auth.repository';
import { LoginPageComponent } from './login-page.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { en: coreEn, 'auth/en': authEn, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' },
  preloadLangs: true,
});

const session = {
  generation: signal(0),
  endReason: signal(null),
  isAuthenticated: signal(false),
  signIn: vi.fn(),
  beginEnrolment: vi.fn(),
};

const router = { navigateByUrl: vi.fn(() => Promise.resolve(true)) };

const invalidCredentials: AppError = {
  code: 'invalid_credentials',
  status: 401,
  kind: 'auth',
  fieldErrors: {},
};

async function setup(error?: AppError, returnUrl?: string) {
  const repository = new FakeAuthRepository();
  if (error !== undefined) repository.willFail('login', error);
  else {
    repository.willReturn('login', {
      ok: true,
      value: { kind: 'ok', session: { accessToken: 'token', expiresAt: Date.now() + 60_000, role: 'owner', amr: ['pwd'] } },
    });
  }
  await TestBed.configureTestingModule({
    imports: [LoginPageComponent, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideTranslocoScope('auth'),
      { provide: AuthRepository, useValue: repository },
      { provide: SessionStore, useValue: session },
      { provide: Router, useValue: router },
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl === undefined ? {} : { returnUrl }) } },
      },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(LoginPageComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, repository, root: fixture.nativeElement as HTMLElement };
}

function type(root: HTMLElement, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(selector);
  if (input === null) throw new Error(`missing ${selector}`);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

function submit(root: HTMLElement): void {
  root.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

afterEach(() => {
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
  vi.useRealTimers();
  router.navigateByUrl.mockClear();
  session.signIn.mockReset();
  session.generation.set(0);
  session.endReason.set(null);
  session.isAuthenticated.set(false);
});

describe('LoginPageComponent', () => {
  it('links labels to both inputs and never reflects a password into an attribute', async () => {
    const { fixture, root } = await setup(invalidCredentials);
    for (const id of ['login-email', 'login-password']) {
      expect(root.querySelector(`label[for="${id}"]`)).not.toBeNull();
    }
    const secret = 'never-put-this-in-an-attribute';
    type(root, '#login-password', secret);
    await fixture.whenStable();
    const password = root.querySelector<HTMLInputElement>('#login-password');
    expect(password?.value).toBe(secret);
    expect(password?.getAttribute('value')).toBeNull();
    expect([...root.querySelectorAll('*')].some((node) => [...node.attributes].some((attr) => attr.value.includes(secret)))).toBe(false);
  });

  it('focuses the alert for a non-field server error', async () => {
    const error: AppError = { code: 'unknown_error', status: 500, kind: 'server', fieldErrors: {} };
    const { fixture, root } = await setup(error);
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'correct horse battery staple');
    submit(root);
    await fixture.whenStable();
    expect(root.querySelector('[data-form-alert]')).toBe(document.activeElement);
  });

  it('binds field errors and focuses the first invalid control', async () => {
    const error: AppError = {
      code: 'validation_failed',
      status: 400,
      kind: 'validation',
      fieldErrors: { email: ['invalid'] },
    };
    const { fixture, root } = await setup(error);
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'correct horse battery staple');
    submit(root);
    await fixture.whenStable();
    expect(root.querySelector('#login-email')).toBe(document.activeElement);
    expect(root.querySelector('#login-email')?.getAttribute('aria-describedby')).toContain('login-email-error');
  });

  it('clears and refocuses the password after invalid credentials', async () => {
    const { fixture, root } = await setup(invalidCredentials);
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'wrong password');
    submit(root);
    await fixture.whenStable();
    const password = root.querySelector<HTMLInputElement>('#login-password');
    expect(password?.value).toBe('');
    expect(password).toBe(document.activeElement);
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(authEn.login.invalidCredentials);
  });

  it('disables submit for a 429 countdown and re-enables it at zero without detectChanges', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    const error: AppError = {
      code: 'rate_limited',
      status: 429,
      kind: 'rate_limit',
      fieldErrors: {},
      retryAfterSec: 1,
    };
    const { fixture, root } = await setup(error);
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'correct horse battery staple');
    submit(root);
    await fixture.whenStable();
    const button = root.querySelector<HTMLButtonElement>('button[type="submit"]');
    expect(button?.disabled).toBe(true);
    vi.advanceTimersByTime(1_000);
    await fixture.whenStable();
    expect(button?.disabled).toBe(false);
  });

  it('re-enables submit and shows no stuck countdown for a 429 with Retry-After 0', async () => {
    const error: AppError = { code: 'rate_limited', status: 429, kind: 'rate_limit', fieldErrors: {}, retryAfterSec: 0 };
    const { fixture, root } = await setup(error);
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'correct horse battery staple');
    submit(root);
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);
  });

  it('renders byte-identical alerts for 401s whose server detail differs (no account enumeration)', async () => {
    async function alertFor(detail: string): Promise<string> {
      const { fixture, root } = await setup({ ...invalidCredentials, problem: { code: 'invalid_credentials', detail } });
      type(root, '#login-email', 'someone@example.test');
      type(root, '#login-password', 'wrong password');
      submit(root);
      await fixture.whenStable();
      const html = root.querySelector('[role="alert"]')?.outerHTML ?? '';
      TestBed.resetTestingModule();
      return html;
    }
    const unknownEmail = await alertFor('No account for that email');
    const wrongPassword = await alertFor('Password does not match');
    expect(unknownEmail).toContain(authEn.login.invalidCredentials);
    expect(unknownEmail).not.toContain('No account');
    expect(wrongPassword).toBe(unknownEmail);
  });

  it('navigates exactly once, to the sanitised returnUrl, after a successful sign-in', async () => {
    session.signIn.mockImplementation(() => session.isAuthenticated.set(true));
    const { fixture, root } = await setup(undefined, '/series/3?tab=story');
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'correct horse battery staple');
    submit(root);
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(session.signIn).toHaveBeenCalledTimes(1);
    expect(router.navigateByUrl).toHaveBeenCalledTimes(1);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/series/3?tab=story');
  });

  it('navigates to /dashboard exactly once when a boot restore resolves while idle on /login', async () => {
    const { fixture } = await setup(undefined, '/series/3?tab=story');
    session.isAuthenticated.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    session.isAuthenticated.set(false);
    session.isAuthenticated.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(router.navigateByUrl).toHaveBeenCalledTimes(1);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });
});

describe('LoginPageComponent MFA step', () => {
  async function atMfaStep(loginMfa: Result<LoginOutcome> | AppError, returnUrl?: string) {
    const repository = new FakeAuthRepository();
    repository.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 'mfa-secret-token' } });
    if ('code' in loginMfa) repository.willFail('loginMfa', loginMfa);
    else repository.willReturn('loginMfa', loginMfa);
    await TestBed.configureTestingModule({
      imports: [LoginPageComponent, translations],
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideTranslocoScope('auth'),
        { provide: AuthRepository, useValue: repository },
        { provide: SessionStore, useValue: session },
        { provide: Router, useValue: router },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl === undefined ? {} : { returnUrl }) } },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(LoginPageComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    type(root, '#login-email', 'owner@example.test');
    type(root, '#login-password', 'correct horse battery staple');
    submit(root);
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, repository, root };
  }

  const OK: Result<LoginOutcome> = {
    ok: true,
    value: { kind: 'ok', session: { accessToken: 'token', expiresAt: Date.now() + 60_000, role: 'owner', amr: ['pwd', 'otp'] } },
  };

  it('swaps the password form for the MFA step form without navigating, and never puts the mfaToken in the DOM', async () => {
    const { root } = await atMfaStep(OK);
    expect(root.querySelector('nts-mfa-step-form')).not.toBeNull();
    expect(root.querySelector('nts-login-form')).toBeNull();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
    expect([...root.querySelectorAll('*')].some((node) => [...node.attributes].some((attr) => attr.value.includes('mfa-secret-token')))).toBe(false);
    expect(root.textContent).not.toContain('mfa-secret-token');
  });

  it('sends the typed TOTP code as `code` and navigates once to the sanitised returnUrl', async () => {
    session.signIn.mockImplementation(() => session.isAuthenticated.set(true));
    const { fixture, root, repository } = await atMfaStep(OK, '/series/3?tab=story');
    type(root, 'nts-mfa-step-form input', ' 123456 ');
    root.querySelector<HTMLFormElement>('nts-mfa-step-form form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(repository.calls.filter((call) => call.method === 'loginMfa')).toEqual([
      { method: 'loginMfa', args: ['mfa-secret-token', '123456', 'totp'] },
    ]);
    expect(session.signIn).toHaveBeenCalledTimes(1);
    expect(router.navigateByUrl).toHaveBeenCalledTimes(1);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/series/3?tab=story');
  });

  it('keeps the MFA step, makes no request for an empty code, and focuses the code field', async () => {
    const { fixture, root, repository } = await atMfaStep(OK);
    root.querySelector<HTMLFormElement>('nts-mfa-step-form form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(repository.calls.some((call) => call.method === 'loginMfa')).toBe(false);
    expect(root.querySelector('nts-mfa-step-form')).not.toBeNull();
    expect(root.querySelector('nts-mfa-step-form input')).toBe(document.activeElement);
  });

  it('stays on the MFA step with a field error for a wrong code and does not sign in', async () => {
    const wrong: AppError = { code: 'invalid_mfa_code', status: 401, kind: 'auth', fieldErrors: {} };
    const { fixture, root } = await atMfaStep(wrong);
    type(root, 'nts-mfa-step-form input', '000000');
    root.querySelector<HTMLFormElement>('nts-mfa-step-form form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('nts-mfa-step-form')).not.toBeNull();
    expect(session.signIn).not.toHaveBeenCalled();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('returns to the password form with the expired-challenge alert when the mfaToken is rejected', async () => {
    const expired: AppError = { code: 'mfa_token_invalid', status: 401, kind: 'auth', fieldErrors: {} };
    const { fixture, root } = await atMfaStep(expired);
    type(root, 'nts-mfa-step-form input', '123456');
    root.querySelector<HTMLFormElement>('nts-mfa-step-form form')?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('nts-mfa-step-form')).toBeNull();
    expect(root.querySelector('nts-login-form')).not.toBeNull();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(authEn.mfa.challengeExpired);
  });
});
