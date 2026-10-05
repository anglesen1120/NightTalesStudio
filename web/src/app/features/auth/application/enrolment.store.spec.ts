import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { AppError, Result } from '@core/errors';
import { ReauthService, SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FAKE_SETUP, FakeMfaRepository, fakeRecoveryCodes } from '../testing/fake-mfa.repository';
import { EnrolmentStore } from './enrolment.store';
import { MfaRepository } from './mfa.repository';

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

const OK_VOID: Result<void> = { ok: true, value: undefined };
const REAUTH_REQUIRED = appError('reauth_required', 401, 'auth');

/**
 * Hand-written `ReauthService` stand-in (README D15: no mocking library) with the same contract as the real one:
 * `withReauth` runs the action, and on `reauth_required` asks `ensureReauthenticated()` and re-runs the action once.
 * `repoCallsAtReauth` records how many repository calls had happened when the dialog was asked for (ordering proof).
 */
class FakeReauthService {
  ensureResult: Result<void> = OK_VOID;
  ensureCalls = 0;
  withReauthCalls = 0;
  repoCallsAtReauth: number[] = [];

  constructor(private readonly repo: FakeMfaRepository) {}

  ensureReauthenticated(): Promise<Result<void>> {
    this.ensureCalls += 1;
    this.repoCallsAtReauth.push(this.repo.calls.length);
    return Promise.resolve(this.ensureResult);
  }

  async withReauth<T>(action: () => Promise<Result<T>>): Promise<Result<T>> {
    this.withReauthCalls += 1;
    const first = await action();
    if (first.ok || first.error.code !== 'reauth_required') return first;
    const reauthenticated = await this.ensureReauthenticated();
    return reauthenticated.ok ? action() : reauthenticated;
  }
}

function setup(options: { enrolmentOnly?: boolean } = {}) {
  const enrolmentOnly = options.enrolmentOnly ?? true;
  const repo = new FakeMfaRepository();
  const reauth = new FakeReauthService(repo);
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: MfaRepository, useValue: repo },
      { provide: ReauthService, useValue: reauth },
      EnrolmentStore,
    ],
  });
  const session = TestBed.inject(SessionStore);
  if (enrolmentOnly) session.beginEnrolment('enrolment-token');
  else session.signIn({ accessToken: fakeJwt('owner', ['pwd', 'otp']), expiresAt: Date.now() + 900_000 }, 'owner', ['pwd', 'otp']);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const store = TestBed.inject(EnrolmentStore);
  TestBed.tick();
  return { store, repo, reauth, session, navigate };
}

describe('EnrolmentStore', () => {
  afterEach(() => vi.restoreAllMocks());

  describe('load', () => {
    it('EnrolmentStore_Load_EnrolmentOnly_ShowsSetupWithoutReauth', async () => {
      const { store, repo, reauth } = setup();
      await store.load();
      expect(store.step()).toBe('show');
      expect(store.setup()).toEqual(FAKE_SETUP);
      expect(store.viewModel().enrolmentOnly).toBe(true);
      expect(repo.callsOf('setup')).toBe(1);
      expect(reauth.withReauthCalls).toBe(0);
      expect(reauth.ensureCalls).toBe(0);
    });

    it('EnrolmentStore_Load_FullSession_RunsThroughWithReauth', async () => {
      const { store, repo, reauth } = setup({ enrolmentOnly: false });
      await store.load();
      expect(store.step()).toBe('show');
      expect(store.viewModel().enrolmentOnly).toBe(false);
      expect(reauth.withReauthCalls).toBe(1);
      expect(repo.callsOf('setup')).toBe(1);
    });

    it('EnrolmentStore_Load_ReauthRequired_OpensDialogThenRetriesSetupOnce', async () => {
      const { store, repo, reauth } = setup({ enrolmentOnly: false });
      repo.willFail('setup', REAUTH_REQUIRED).willReturn('setup', { ok: true, value: FAKE_SETUP });
      await store.load();
      expect(reauth.ensureCalls).toBe(1);
      expect(repo.callsOf('setup')).toBe(2);
      expect(store.step()).toBe('show');
      expect(store.viewModel().reauthRequired).toBe(false);
    });

    it('EnrolmentStore_Load_ReauthCancelled_ShowsReauthRequiredWithRetry', async () => {
      const { store, repo, reauth } = setup({ enrolmentOnly: false });
      const cancelled = appError('reauth_required', 0, 'auth');
      repo.willFail('setup', REAUTH_REQUIRED);
      reauth.ensureResult = { ok: false, error: cancelled };
      await store.load();
      expect(store.step()).toBe('show');
      expect(store.error()).toEqual(cancelled);
      expect(store.viewModel().reauthRequired).toBe(true);
      expect(store.viewModel().formError).toBeNull();
      expect(store.setup()).toBeNull();
      expect(store.canSubmit()).toBe(false);
      expect(repo.callsOf('setup')).toBe(1);
      await store.verify('123456');
      expect(repo.callsOf('verify')).toBe(0);
      expect(store.viewModel().reauthRequired).toBe(true);
      // Retry opens re-auth again; only its successful result retries setup.
      reauth.ensureResult = OK_VOID;
      repo.willFail('setup', REAUTH_REQUIRED).willReturn('setup', { ok: true, value: FAKE_SETUP });
      await store.load();
      expect(reauth.ensureCalls).toBe(2);
      expect(repo.callsOf('setup')).toBe(3);
      expect(store.step()).toBe('show');
      expect(store.viewModel().reauthRequired).toBe(false);
      expect(store.setup()).toEqual(FAKE_SETUP);
      expect(store.canSubmit()).toBe(true);
    });

    it('EnrolmentStore_Load_ConcurrencyConflict_RerunsOnceAndKeepsTheNewSecret', async () => {
      const { store, repo } = setup();
      const fresh = { secret: 'NEWSECRET', otpauthUri: 'otpauth://totp/NightTales:o?secret=NEWSECRET' };
      repo.willFail('setup', appError('concurrency_conflict', 409, 'precondition')).willReturn('setup', { ok: true, value: fresh });
      await store.load();
      expect(repo.callsOf('setup')).toBe(2);
      expect(store.step()).toBe('show');
      expect(store.setup()).toEqual(fresh);
    });

    it('EnrolmentStore_Load_ConcurrencyConflictTwice_StopsAfterOneRetry', async () => {
      const { store, repo } = setup();
      repo.willFail('setup', appError('concurrency_conflict', 409, 'precondition'));
      await store.load();
      expect(repo.callsOf('setup')).toBe(2);
      expect(store.step()).toBe('error');
      expect(store.submitting()).toBe(false);
    });

    it('EnrolmentStore_Load_NotFoundOnEnrolmentToken_IsReLoginRequiredWithoutRetry', async () => {
      const { store, repo } = setup();
      repo.willFail('setup', appError('not_found', 404, 'not_found'));
      await store.load();
      expect(repo.callsOf('setup')).toBe(1);
      expect(store.step()).toBe('error');
      expect(store.viewModel().reLoginRequired).toBe(true);
      expect(store.viewModel().formError).toBeNull();
    });

    it('EnrolmentStore_Load_OtherFailure_ShowsErrorKeyAndKeepsNoSecret', async () => {
      const { store, repo } = setup();
      repo.willFail('setup', appError('network_error', 0, 'network'));
      await store.load();
      expect(store.step()).toBe('error');
      expect(store.viewModel().formError).toBe('errors.network_error');
      expect(store.viewModel().reLoginRequired).toBe(false);
      expect(store.setup()).toBeNull();
    });
  });

  describe('verify', () => {
    async function atShow(options: { enrolmentOnly?: boolean } = {}) {
      const ctx = setup(options);
      await ctx.store.load();
      return ctx;
    }

    it('EnrolmentStore_Verify_Ok_RevealsExactlyTenCodesAndDropsTheSecret', async () => {
      const { store, repo } = await atShow();
      const codes = fakeRecoveryCodes('rc');
      repo.willReturn('verify', { ok: true, value: codes });
      await store.verify(' 123456 ');
      expect(repo.calls.at(-1)).toEqual({ method: 'verify', args: ['123456'] });
      expect(store.step()).toBe('codes');
      expect(store.codes()).toEqual(codes);
      expect(store.codes()).toHaveLength(10);
      expect(store.setup()).toBeNull();
      expect(store.confirmedSaved()).toBe(false);
    });

    it('EnrolmentStore_Verify_EnrolmentOnly_NeverOpensReauth', async () => {
      const { store, reauth } = await atShow();
      await store.verify('123456');
      expect(reauth.withReauthCalls).toBe(0);
      expect(reauth.ensureCalls).toBe(0);
    });

    it('EnrolmentStore_Verify_EnrolmentOnly_ClearsTokenImmediatelyAndKeepsCodesVisible', async () => {
      const { store, session } = await atShow();
      await store.verify('123456');

      expect(session.enrolmentToken()).toBeNull();
      expect(session.mode()).toBe('full');
      expect(store.step()).toBe('codes');
      expect(store.codes()).toHaveLength(10);
      TestBed.tick();
      expect(store.step()).toBe('codes');
      expect(store.codes()).toHaveLength(10);
    });

    it('EnrolmentStore_Verify_FullSession_RunsThroughWithReauth', async () => {
      const { store, reauth } = await atShow({ enrolmentOnly: false });
      await store.verify('123456');
      expect(reauth.withReauthCalls).toBe(2);
      expect(store.step()).toBe('codes');
    });

    it('EnrolmentStore_Verify_InvalidCode_StaysOnVerifyWithFieldErrorAndKeepsSecret', async () => {
      const { store, repo } = await atShow();
      repo.willFail('verify', appError('invalid_mfa_code', 401, 'auth'));
      await store.verify('000000');
      expect(store.step()).toBe('verify');
      expect(store.error()?.code).toBe('invalid_mfa_code');
      expect(store.viewModel().formError).toBeNull();
      expect(store.setup()).toEqual(FAKE_SETUP);
      expect(store.codes()).toBeNull();
      expect(store.submitting()).toBe(false);
      repo.willReturn('verify', { ok: true, value: fakeRecoveryCodes() });
      await store.verify('123456');
      expect(store.step()).toBe('codes');
    });

    it('EnrolmentStore_Verify_RateLimited_SetsCountdownAndBlocksSubmit', async () => {
      const { store, repo } = await atShow();
      repo.willFail('verify', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 900 }));
      await store.verify('000000');
      expect(store.viewModel().retryAfterSec).toBe(900);
      expect(store.canSubmit()).toBe(false);
      await store.verify('123456');
      expect(repo.callsOf('verify')).toBe(1);
      store.retryAfterElapsed();
      expect(store.canSubmit()).toBe(true);
      expect(store.viewModel().retryAfterSec).toBeNull();
    });

    it('EnrolmentStore_Verify_EmptyCode_MakesNoRequest', async () => {
      const { store, repo } = await atShow();
      await store.verify('   ');
      expect(repo.callsOf('verify')).toBe(0);
    });

    it('EnrolmentStore_Verify_BeforeSetupLoaded_MakesNoRequest', async () => {
      const { store, repo } = setup();
      await store.verify('123456');
      expect(repo.callsOf('verify')).toBe(0);
    });

    it('EnrolmentStore_Verify_DoubleSubmit_CallsRepositoryOnce', async () => {
      const { store, repo } = await atShow();
      repo.defer();
      const first = store.verify('123456');
      const second = store.verify('123456');
      expect(store.submitting()).toBe(true);
      repo.resolveNext();
      await Promise.all([first, second]);
      expect(repo.callsOf('verify')).toBe(1);
    });

    it('EnrolmentStore_Verify_NotFoundOnEnrolmentToken_IsReLoginRequired', async () => {
      const { store, repo } = await atShow();
      repo.willFail('verify', appError('not_found', 404, 'not_found'));
      await store.verify('123456');
      expect(store.viewModel().reLoginRequired).toBe(true);
      expect(store.codes()).toBeNull();
    });
  });

  describe('codes', () => {
    async function atCodes() {
      const ctx = setup({ enrolmentOnly: false });
      await ctx.store.load();
      await ctx.store.verify('123456');
      return ctx;
    }

    it('EnrolmentStore_Finish_RequiresConfirmedSaved_ThenDropsCodesForGood', async () => {
      const { store } = await atCodes();
      expect(store.finish()).toBe(false);
      expect(store.codes()).toHaveLength(10);
      store.setConfirmedSaved(true);
      expect(store.viewModel().confirmedSaved).toBe(true);
      expect(store.finish()).toBe(true);
      expect(store.codes()).toBeNull();
      expect(store.step()).toBe('done');
      expect(store.finish()).toBe(false);
    });

    it('EnrolmentStore_Regenerate_ReauthenticatesBeforeTheRepositoryCall', async () => {
      const { store, repo, reauth } = await atCodes();
      const before = repo.calls.length;
      const fresh = fakeRecoveryCodes('new');
      repo.willReturn('regenerateRecoveryCodes', { ok: true, value: fresh });
      store.setConfirmedSaved(true);
      await store.regenerate();
      expect(reauth.ensureCalls).toBe(1);
      expect(reauth.repoCallsAtReauth).toEqual([before]);
      expect(repo.callsOf('regenerateRecoveryCodes')).toBe(1);
      expect(repo.calls.at(-1)?.method).toBe('regenerateRecoveryCodes');
      expect(store.codes()).toEqual(fresh);
      expect(store.step()).toBe('codes');
      expect(store.confirmedSaved()).toBe(false);
    });

    it('EnrolmentStore_Regenerate_ReauthCancelled_MakesNoRequestAndKeepsOldCodes', async () => {
      const { store, repo, reauth } = await atCodes();
      const old = store.codes();
      reauth.ensureResult = { ok: false, error: REAUTH_REQUIRED };
      await store.regenerate();
      expect(repo.callsOf('regenerateRecoveryCodes')).toBe(0);
      expect(store.codes()).toEqual(old);
      expect(store.viewModel().reauthRequired).toBe(true);
      expect(store.submitting()).toBe(false);
    });

    it('EnrolmentStore_Regenerate_RepositoryFailure_KeepsOldCodesAndShowsError', async () => {
      const { store, repo } = await atCodes();
      const old = store.codes();
      repo.willFail('regenerateRecoveryCodes', appError('network_error', 0, 'network'));
      await store.regenerate();
      expect(store.codes()).toEqual(old);
      expect(store.viewModel().formError).toBe('errors.network_error');
    });
  });

  describe('leaving and teardown', () => {
    it('EnrolmentStore_LeaveToLogin_EnrolmentOnly_ClearsTheTokenThenNavigates', async () => {
      const { store, session, navigate } = setup();
      await store.load();
      await store.verify('123456');
      await store.leaveToLogin();
      expect(session.mode()).toBe('full');
      expect(session.enrolmentToken()).toBeNull();
      expect(session.endReason()).toBeNull();
      expect(navigate).toHaveBeenCalledWith('/login');
    });

    it('EnrolmentStore_LeaveToLogin_FullSession_KeepsTheSession', async () => {
      const { store, session, navigate } = setup({ enrolmentOnly: false });
      await store.leaveToLogin();
      expect(session.isAuthenticated()).toBe(true);
      expect(navigate).toHaveBeenCalledWith('/login');
    });

    it('EnrolmentStore_Destroy_ClearsSetupSecret', async () => {
      const { repo } = setup();
      const injector = createEnvironmentInjector([EnrolmentStore], TestBed.inject(EnvironmentInjector));
      const store = injector.get(EnrolmentStore);
      await store.load();
      expect(store.setup()).not.toBeNull();
      injector.destroy();
      expect(store.setup()).toBeNull();
      expect(repo.callsOf('setup')).toBe(1);
    });

    it('EnrolmentStore_Destroy_ClearsCodes', async () => {
      setup();
      const injector = createEnvironmentInjector([EnrolmentStore], TestBed.inject(EnvironmentInjector));
      const store = injector.get(EnrolmentStore);
      await store.load();
      await store.verify('123456');
      expect(store.codes()).toHaveLength(10);
      injector.destroy();
      expect(store.codes()).toBeNull();
    });

    it('EnrolmentStore_ResultAfterDestroy_PaintsNothing', async () => {
      const { repo } = setup();
      const injector = createEnvironmentInjector([EnrolmentStore], TestBed.inject(EnvironmentInjector));
      const store = injector.get(EnrolmentStore);
      repo.defer();
      const pending = store.load();
      injector.destroy();
      repo.resolveNext();
      await pending;
      expect(store.setup()).toBeNull();
      expect(store.step()).toBe('loading');
    });

    it('EnrolmentStore_SessionEnd_ClearsSecretsAndCodes', async () => {
      const { store, session } = setup({ enrolmentOnly: false });
      await store.load();
      expect(store.setup()).not.toBeNull();
      session.signOut('expired');
      TestBed.tick();
      expect(store.setup()).toBeNull();
      await store.load();
      await store.verify('123456');
      expect(store.codes()).toHaveLength(10);
      session.reset();
      TestBed.tick();
      expect(store.codes()).toBeNull();
    });

    it('EnrolmentStore_ResultAfterGenerationChange_PaintsNothing', async () => {
      const { store, repo, session } = setup();
      repo.defer();
      const pending = store.load();
      session.reset();
      TestBed.tick();
      repo.resolveNext();
      await pending;
      expect(store.setup()).toBeNull();
      expect(store.step()).toBe('loading');
      expect(store.submitting()).toBe(false);
    });

    it('EnrolmentStore_ViewModel_NeverExposesSecretOrCodesOutsideTheirSteps', async () => {
      const { store } = setup();
      await store.load();
      expect(store.viewModel().codes).toBeNull();
      await store.verify('123456');
      expect(store.viewModel().setup).toBeNull();
      expect(store.viewModel().codes).toHaveLength(10);
    });
  });
});
