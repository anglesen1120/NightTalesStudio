import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Session } from '../domain/auth.model';
import { FakeAuthRepository } from '../testing/fake-auth.repository';
import type { MockInstance } from 'vitest';
import { AuthRepository } from './auth.repository';
import { LoginStore } from './login.store';

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

const SESSION: Session = { accessToken: fakeJwt('owner', ['pwd']), expiresAt: Date.now() + 900_000, role: 'owner', amr: ['pwd'] };

function setup(returnUrl: string | null = null): {
  store: LoginStore;
  repo: FakeAuthRepository;
  session: SessionStore;
  navigate: MockInstance<Router['navigateByUrl']>;
} {
  const repo = new FakeAuthRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: AuthRepository, useValue: repo },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl === null ? {} : { returnUrl }) } } },
      LoginStore,
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const store = TestBed.inject(LoginStore);
  TestBed.tick();
  return { store, repo, session: TestBed.inject(SessionStore), navigate };
}

describe('LoginStore', () => {
  afterEach(() => vi.restoreAllMocks());

  it('LoginStore_Ok_SignsInOnceAndNavigatesOnceToSanitizedReturnUrl', async () => {
    const { store, repo, session, navigate } = setup('/series');
    const signIn = vi.spyOn(session, 'signIn');
    repo.willReturn('login', { ok: true, value: { kind: 'ok', session: SESSION } });
    await store.submitPassword('a@b.c', 'pw');
    expect(signIn).toHaveBeenCalledTimes(1);
    expect(signIn).toHaveBeenCalledWith({ accessToken: SESSION.accessToken, expiresAt: SESSION.expiresAt }, 'owner', ['pwd']);
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith('/series');
    expect(session.isAuthenticated()).toBe(true);
  });

  it.each(['https://evil.example', '//evil.example', '/\\evil', '/login'])('LoginStore_Ok_RejectsReturnUrl_%s', async (bad) => {
    const { store, repo, navigate } = setup(bad);
    repo.willReturn('login', { ok: true, value: { kind: 'ok', session: SESSION } });
    await store.submitPassword('a@b.c', 'pw');
    expect(navigate).toHaveBeenCalledWith('/dashboard');
  });

  it('LoginStore_Mfa_HoldsTokenInMemoryOnly_NeverInStorage', async () => {
    const { store, repo, session, navigate } = setup();
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 'mfa-secret' } });
    await store.submitPassword('a@b.c', 'pw');
    expect(store.mfaToken()).toBe('mfa-secret');
    expect(store.step()).toBe('password');
    expect(setItem).not.toHaveBeenCalled();
    expect(session.isAuthenticated()).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('LoginStore_Enrolment_BeginsEnrolmentAndRedirects', async () => {
    const { store, repo, session, navigate } = setup('/series');
    repo.willReturn('login', { ok: true, value: { kind: 'enrolment', token: 'enrol' } });
    await store.submitPassword('a@b.c', 'pw');
    expect(session.enrolmentToken()).toBe('enrol');
    expect(session.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith('/account/security/mfa-setup');
  });

  it('LoginStore_InvalidCredentials_SetsErrorAndKeyWithoutSession', async () => {
    const { store, repo, session, navigate } = setup();
    repo.willFail('login', appError('invalid_credentials', 401, 'auth'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.error()?.code).toBe('invalid_credentials');
    expect(store.formError()).toBe('auth.login.invalidCredentials');
    expect(store.submitting()).toBe(false);
    expect(store.canSubmit()).toBe(true);
    expect(session.isAuthenticated()).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('LoginStore_EmailNotVerified_MapsKey', async () => {
    const { store, repo } = setup();
    repo.willFail('login', appError('email_not_verified', 403, 'auth'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.formError()).toBe('auth.login.emailNotVerified');
  });

  it('LoginStore_NetworkAndUnknown_MapToErrorsKeys', async () => {
    const { store, repo } = setup();
    repo.willFail('login', appError('network_error', 0, 'network'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.formError()).toBe('errors.network_error');
    repo.willFail('login', appError('unknown_error', 500, 'server'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.formError()).toBe('errors.unknown_error');
  });

  it('LoginStore_RateLimited_SetsRetryAfterAndBlocksSubmitUntilElapsed', async () => {
    const { store, repo } = setup();
    repo.willFail('login', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }));
    const before = Date.now();
    await store.submitPassword('a@b.c', 'pw');
    expect(store.retryAfterSec()).toBe(30);
    expect(store.rateLimitedUntil()).toBeGreaterThanOrEqual(before + 30_000);
    expect(store.canSubmit()).toBe(false);
    expect(store.formError()).toBe('auth.rateLimited.message');
    await store.submitPassword('a@b.c', 'pw');
    expect(repo.calls).toHaveLength(1);
    store.retryAfterElapsed();
    expect(store.canSubmit()).toBe(true);
    expect(store.retryAfterSec()).toBeNull();
    await store.submitPassword('a@b.c', 'pw');
    expect(repo.calls).toHaveLength(2);
  });

  it('LoginStore_SecondRateLimit_ReplacesValueInsteadOfStacking', async () => {
    const { store, repo } = setup();
    repo.willFail('login', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }));
    await store.submitPassword('a@b.c', 'pw');
    store.retryAfterElapsed();
    repo.willFail('login', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 5 }));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.retryAfterSec()).toBe(5);
  });

  it('LoginStore_ValidationFailed_KeepsFieldErrorsAndNoFormAlert', async () => {
    const { store, repo } = setup();
    const error = appError('validation_failed', 400, 'validation', { fieldErrors: { email: ['invalid'] } });
    repo.willFail('login', error);
    await store.submitPassword('bad', 'pw');
    expect(store.error()).toBe(error);
    expect(store.formError()).toBeNull();
  });

  it('LoginStore_DoubleSubmit_CallsRepositoryOnce', async () => {
    const { store, repo } = setup();
    repo.defer();
    repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 't' } });
    const first = store.submitPassword('a@b.c', 'pw');
    const second = store.submitPassword('a@b.c', 'pw');
    expect(store.submitting()).toBe(true);
    expect(store.canSubmit()).toBe(false);
    repo.resolveNext();
    await Promise.all([first, second]);
    expect(repo.calls).toHaveLength(1);
    expect(store.submitting()).toBe(false);
  });

  it('LoginStore_ResultAfterDestroy_IsIgnored', async () => {
    const { repo, session, navigate } = setup();
    const injector = createEnvironmentInjector([LoginStore], TestBed.inject(EnvironmentInjector));
    const store = injector.get(LoginStore);
    const signIn = vi.spyOn(session, 'signIn');
    repo.defer();
    const pending = store.submitPassword('a@b.c', 'pw');
    injector.destroy();
    repo.resolveNext({ ok: true, value: { kind: 'ok', session: SESSION } });
    await pending;
    expect(signIn).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(store.mfaToken()).toBeNull();
  });

  it('LoginStore_ResultAfterGenerationChange_IsIgnored', async () => {
    const { store, repo, session, navigate } = setup();
    repo.defer();
    const pending = store.submitPassword('a@b.c', 'pw');
    session.reset();
    const signIn = vi.spyOn(session, 'signIn');
    repo.resolveNext({ ok: true, value: { kind: 'ok', session: SESSION } });
    await pending;
    expect(signIn).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(store.error()).toBeNull();
  });

  it('LoginStore_SessionEnd_ClearsMfaTokenAndError', async () => {
    const { store, repo, session } = setup();
    repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 'mfa-secret' } });
    await store.submitPassword('a@b.c', 'pw');
    repo.willFail('login', appError('invalid_credentials', 401, 'auth'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.error()).not.toBeNull();
    session.signOut('expired');
    TestBed.tick();
    expect(session.endReason()).toBe('expired');
    expect(store.mfaToken()).toBeNull();
    expect(store.error()).toBeNull();
    expect(store.formError()).toBeNull();
    expect(store.step()).toBe('password');
  });

  it('LoginStore_BackToPassword_ClearsMfaToken', async () => {
    const { store, repo } = setup();
    repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 't' } });
    await store.submitPassword('a@b.c', 'pw');
    store.backToPassword();
    expect(store.mfaToken()).toBeNull();
  });
});
