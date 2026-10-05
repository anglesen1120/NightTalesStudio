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
    expect(store.step()).toBe('mfa');
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

  it('LoginStore_RateLimitedWithoutRetryAfter_InventsNoCountdownAndKeepsSubmitEnabled', async () => {
    const { store, repo } = setup();
    repo.willFail('login', appError('rate_limited', 429, 'rate_limit'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.retryAfterSec()).toBeNull();
    expect(store.rateLimitedUntil()).toBeNull();
    expect(store.canSubmit()).toBe(true);
    expect(store.formError()).toBe('auth.rateLimited.message');
  });

  it('LoginStore_RateLimitedWithRetryAfterZero_KeepsZeroSoTheFormRendersTheCountdownThatElapsesAtOnce', async () => {
    const { store, repo } = setup();
    repo.willFail('login', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 0 }));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.retryAfterSec()).toBe(0);
    expect(store.canSubmit()).toBe(false);
    store.retryAfterElapsed();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.canSubmit()).toBe(true);
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

  it('LoginStore_ResultAfterGenerationChange_LeavesSubmittingErrorAndMfaTokenUntouched', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const pending = store.submitPassword('a@b.c', 'pw');
    expect(store.submitting()).toBe(true);
    session.reset();
    TestBed.tick();
    expect(store.submitting()).toBe(false);
    repo.resolveNext({ ok: false, error: appError('invalid_credentials', 401, 'auth') });
    await pending;
    expect(store.submitting()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.formError()).toBeNull();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.rateLimitedUntil()).toBeNull();
    expect(store.mfaToken()).toBeNull();
  });

  it('LoginStore_ResultAfterGenerationChange_DoesNotClearSubmittingOfNewerRequest', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const first = store.submitPassword('a@b.c', 'pw');
    session.reset();
    TestBed.tick();
    const second = store.submitPassword('a@b.c', 'pw');
    expect(store.submitting()).toBe(true);
    repo.resolveNext({ ok: false, error: appError('invalid_credentials', 401, 'auth') });
    await first;
    expect(store.submitting()).toBe(true);
    expect(store.error()).toBeNull();
    repo.resolveNext({ ok: false, error: appError('invalid_credentials', 401, 'auth') });
    await second;
    expect(store.submitting()).toBe(false);
  });

  it('LoginStore_Submit_ClearsStaleMfaTokenBeforeTheRequest', async () => {
    const { store, repo } = setup();
    repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 'mfa-secret' } });
    await store.submitPassword('a@b.c', 'pw');
    expect(store.mfaToken()).toBe('mfa-secret');
    repo.willFail('login', appError('invalid_credentials', 401, 'auth'));
    await store.submitPassword('a@b.c', 'pw');
    expect(store.error()).not.toBeNull();
    expect(store.mfaToken()).toBeNull();
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

  describe('MFA step', () => {
    async function atMfaStep(returnUrl: string | null = null) {
      const ctx = setup(returnUrl);
      ctx.repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 'mfa-secret' } });
      await ctx.store.submitPassword('a@b.c', 'pw');
      return ctx;
    }

    const lastCall = (repo: FakeAuthRepository) => repo.calls.filter((call) => call.method === 'loginMfa').at(-1);

    it('LoginStore_Mfa_ShowsTheMfaStepWithoutNavigating', async () => {
      const { store, navigate } = await atMfaStep();
      expect(store.step()).toBe('mfa');
      expect(store.recoveryMode()).toBe(false);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('LoginStore_SubmitMfa_TotpMode_SendsTrimmedCodeSignsInAndNavigates', async () => {
      const { store, repo, session, navigate } = await atMfaStep('/series');
      const signIn = vi.spyOn(session, 'signIn');
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: { ...SESSION, amr: ['pwd', 'otp'] } } });
      await store.submitMfa(' 123456 ');
      expect(lastCall(repo)?.args).toEqual(['mfa-secret', '123456', 'totp']);
      expect(signIn).toHaveBeenCalledWith({ accessToken: SESSION.accessToken, expiresAt: SESSION.expiresAt }, 'owner', ['pwd', 'otp']);
      expect(navigate).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith('/series');
      expect(store.mfaToken()).toBeNull();
    });

    it.each(['https://evil.example', '//evil.example'])('LoginStore_SubmitMfa_Ok_RejectsReturnUrl_%s', async (bad) => {
      const { store, repo, navigate } = await atMfaStep(bad);
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: SESSION } });
      await store.submitMfa('123456');
      expect(navigate).toHaveBeenCalledWith('/dashboard');
    });

    it('LoginStore_SubmitMfa_RecoveryMode_SendsRecoveryMode', async () => {
      const { store, repo } = await atMfaStep();
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: SESSION } });
      store.toggleRecovery();
      expect(store.recoveryMode()).toBe(true);
      await store.submitMfa('abcd-1234');
      expect(lastCall(repo)?.args).toEqual(['mfa-secret', 'abcd-1234', 'recovery']);
    });

    it('LoginStore_SubmitMfa_EmptyOrBlankCode_MakesNoRequest', async () => {
      const { store, repo } = await atMfaStep();
      await store.submitMfa('');
      await store.submitMfa('   ');
      expect(repo.calls.some((call) => call.method === 'loginMfa')).toBe(false);
      expect(store.submitting()).toBe(false);
    });

    it('LoginStore_SubmitMfa_WithoutChallenge_MakesNoRequest', async () => {
      const { store, repo } = setup();
      await store.submitMfa('123456');
      expect(repo.calls).toHaveLength(0);
    });

    it('LoginStore_SubmitMfa_InvalidCode_KeepsStepAndTokenWithError', async () => {
      const { store, repo, session } = await atMfaStep();
      const signIn = vi.spyOn(session, 'signIn');
      repo.willFail('loginMfa', appError('invalid_mfa_code', 401, 'auth'));
      await store.submitMfa('000000');
      expect(store.step()).toBe('mfa');
      expect(store.mfaToken()).toBe('mfa-secret');
      expect(store.error()?.code).toBe('invalid_mfa_code');
      expect(store.submitting()).toBe(false);
      expect(signIn).not.toHaveBeenCalled();
    });

    it('LoginStore_SubmitMfa_MfaTokenInvalid_ReturnsToPasswordWithChallengeExpired', async () => {
      const { store, repo } = await atMfaStep();
      store.toggleRecovery();
      repo.willFail('loginMfa', appError('mfa_token_invalid', 401, 'auth'));
      await store.submitMfa('000000');
      expect(store.step()).toBe('password');
      expect(store.mfaToken()).toBeNull();
      expect(store.recoveryMode()).toBe(false);
      expect(store.formError()).toBe('auth.mfa.challengeExpired');
      expect(store.error()).toBeNull();
    });

    it('LoginStore_SubmitMfa_RateLimited_SetsCountdownAndBlocksSubmit', async () => {
      const { store, repo } = await atMfaStep();
      repo.willFail('loginMfa', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 900 }));
      await store.submitMfa('000000');
      expect(store.step()).toBe('mfa');
      expect(store.retryAfterSec()).toBe(900);
      expect(store.canSubmit()).toBe(false);
      await store.submitMfa('123456');
      expect(repo.calls.filter((call) => call.method === 'loginMfa')).toHaveLength(1);
      store.retryAfterElapsed();
      expect(store.canSubmit()).toBe(true);
    });

    it('LoginStore_SubmitMfa_ValidationFailed_KeepsFieldErrorsAndNoFormAlert', async () => {
      const { store, repo } = await atMfaStep();
      const error = appError('validation_failed', 400, 'validation', { fieldErrors: { code: ['required'] } });
      repo.willFail('loginMfa', error);
      await store.submitMfa('1');
      expect(store.step()).toBe('mfa');
      expect(store.error()).toBe(error);
      expect(store.formError()).toBeNull();
    });

    it('LoginStore_SubmitMfa_DoubleSubmit_CallsRepositoryOnce', async () => {
      const { store, repo } = await atMfaStep();
      repo.defer();
      repo.willReturn('loginMfa', { ok: false, error: appError('invalid_mfa_code', 401, 'auth') });
      const first = store.submitMfa('123456');
      const second = store.submitMfa('123456');
      expect(store.submitting()).toBe(true);
      repo.resolveNext();
      await Promise.all([first, second]);
      expect(repo.calls.filter((call) => call.method === 'loginMfa')).toHaveLength(1);
      expect(store.submitting()).toBe(false);
    });

    it('LoginStore_ToggleRecovery_SwapsModeAndClearsError', async () => {
      const { store, repo } = await atMfaStep();
      repo.willFail('loginMfa', appError('invalid_mfa_code', 401, 'auth'));
      await store.submitMfa('000000');
      store.toggleRecovery();
      expect(store.recoveryMode()).toBe(true);
      expect(store.error()).toBeNull();
      store.toggleRecovery();
      expect(store.recoveryMode()).toBe(false);
    });

    it('LoginStore_BackToPassword_FromMfa_ClearsTokenModeAndStep', async () => {
      const { store } = await atMfaStep();
      store.toggleRecovery();
      store.backToPassword();
      expect(store.step()).toBe('password');
      expect(store.mfaToken()).toBeNull();
      expect(store.recoveryMode()).toBe(false);
    });

    it('LoginStore_SessionEnd_Logout_ClearsMfaTokenAndModeAndStep', async () => {
      const { store, session } = await atMfaStep();
      store.toggleRecovery();
      await session.logout();
      TestBed.tick();
      expect(store.mfaToken()).toBeNull();
      expect(store.recoveryMode()).toBe(false);
      expect(store.step()).toBe('password');
    });

    it('LoginStore_SubmitMfa_ResultAfterDestroy_IsIgnored', async () => {
      const { repo, session, navigate } = setup();
      const injector = createEnvironmentInjector([LoginStore], TestBed.inject(EnvironmentInjector));
      const store = injector.get(LoginStore);
      repo.willReturn('login', { ok: true, value: { kind: 'mfa', mfaToken: 'mfa-secret' } });
      await store.submitPassword('a@b.c', 'pw');
      const signIn = vi.spyOn(session, 'signIn');
      repo.defer();
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: SESSION } });
      const pending = store.submitMfa('123456');
      injector.destroy();
      repo.resolveNext();
      await pending;
      expect(signIn).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
      expect(store.mfaToken()).toBeNull();
    });

    it('LoginStore_SubmitMfa_ResultAfterGenerationChange_IsIgnored', async () => {
      const { store, repo, session, navigate } = await atMfaStep();
      repo.defer();
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: SESSION } });
      const pending = store.submitMfa('123456');
      session.reset();
      const signIn = vi.spyOn(session, 'signIn');
      repo.resolveNext();
      await pending;
      expect(signIn).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
    });

    it('LoginStore_SubmitMfa_BackToPasswordMidFlight_DropsLateSuccess', async () => {
      const { store, repo, session, navigate } = await atMfaStep();
      repo.defer();
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: SESSION } });
      const pending = store.submitMfa('123456');
      expect(store.submitting()).toBe(true);
      store.backToPassword();
      expect(store.step()).toBe('password');
      expect(store.submitting()).toBe(false);
      const signIn = vi.spyOn(session, 'signIn');
      repo.resolveNext();
      await pending;
      expect(signIn).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
      expect(store.step()).toBe('password');
      expect(store.error()).toBeNull();
    });

    it('LoginStore_SubmitMfa_BackToPasswordMidFlight_LateMfaTokenInvalidPaintsNoAlert', async () => {
      const { store, repo, session } = await atMfaStep();
      repo.defer();
      repo.willFail('loginMfa', appError('mfa_token_invalid', 401, 'auth'));
      const pending = store.submitMfa('123456');
      store.backToPassword();
      const signIn = vi.spyOn(session, 'signIn');
      repo.resolveNext();
      await pending;
      expect(store.step()).toBe('password');
      expect(store.formError()).toBeNull();
      expect(store.error()).toBeNull();
      expect(signIn).not.toHaveBeenCalled();
    });

    it('LoginStore_SubmitMfa_ToggleRecoveryMidFlight_SupersedesTheAttempt', async () => {
      const { store, repo, session, navigate } = await atMfaStep();
      repo.defer();
      repo.willReturn('loginMfa', { ok: true, value: { kind: 'ok', session: SESSION } });
      const pending = store.submitMfa('123456');
      store.toggleRecovery();
      expect(store.recoveryMode()).toBe(true);
      expect(store.submitting()).toBe(false);
      const signIn = vi.spyOn(session, 'signIn');
      repo.resolveNext();
      await pending;
      expect(signIn).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
      expect(store.step()).toBe('mfa');
      expect(store.recoveryMode()).toBe(true);
      expect(store.error()).toBeNull();
    });
  });
});
