import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FakeAuthRepository } from '../testing/fake-auth.repository';
import { AuthRepository } from './auth.repository';
import { ResetPasswordStore } from './reset-password.store';

const OK = { ok: true, value: undefined } as const;

interface Harness {
  readonly store: ResetPasswordStore;
  readonly repo: FakeAuthRepository;
  readonly session: SessionStore;
}

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

/** Builds the store for the current `location`; the constructor reads and strips the fragment. */
function create(): Harness {
  const repo = new FakeAuthRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: AuthRepository, useValue: repo },
      ResetPasswordStore,
    ],
  });
  return { store: TestBed.inject(ResetPasswordStore), repo, session: TestBed.inject(SessionStore) };
}

/** `suffix` is what follows `/reset-password` in the address bar (`#t=abc`, `?t=abc`, ...). */
function setup(suffix: string): Harness {
  history.replaceState(null, '', `/reset-password${suffix}`);
  return create();
}

describe('ResetPasswordStore', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    Reflect.deleteProperty(history, 'replaceState');
    history.replaceState(null, '', '/');
  });

  it('ResetPasswordStore_ParsesTokenFromHash_AndSendsItOnSubmit', async () => {
    const { store, repo } = setup('#t=abc');
    expect(store.hasToken()).toBe(true);
    repo.willReturn('resetPassword', OK);
    await store.submit('Str0ng!pass', 'Str0ng!pass');
    expect(repo.calls).toEqual([{ method: 'resetPassword', args: ['abc', 'Str0ng!pass'] }]);
  });

  it('ResetPasswordStore_StripsHashInConstructor_BeforeAnyPortCall', () => {
    history.replaceState(null, '', '/reset-password#t=abc');
    const replaceState = vi.spyOn(history, 'replaceState');
    const { repo } = create();
    expect(replaceState).toHaveBeenCalledWith(null, '', '/reset-password');
    expect(location.hash).toBe('');
    expect(repo.calls).toHaveLength(0);
  });

  it('ResetPasswordStore_TokenIsNeverAPublicMemberStoredOrLogged', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    const log = vi.spyOn(console, 'log');
    const { store, repo } = setup('#t=secret-token');
    repo.willReturn('resetPassword', OK);
    await store.submit('p', 'p');
    expect(Object.values(store).some((v) => typeof v === 'string' && v.includes('secret-token'))).toBe(false);
    expect(setItem).not.toHaveBeenCalled();
    expect(log).not.toHaveBeenCalled();
  });

  it.each(['', '#t=', '#t=abc&x=1', '#x=abc', '?t=abc'])('ResetPasswordStore_NoValidFragment_%j_NoTokenNoCalls', async (suffix) => {
    const { store, repo } = setup(suffix);
    expect(store.hasToken()).toBe(false);
    expect(store.canSubmit()).toBe(false);
    await store.submit('p', 'p');
    expect(repo.calls).toHaveLength(0);
  });

  it('ResetPasswordStore_ReplaceStateUnavailable_StillReadsTokenAndSubmits', async () => {
    history.replaceState(null, '', '/reset-password#t=abc');
    Object.defineProperty(history, 'replaceState', { value: undefined, configurable: true });
    const { store, repo } = create();
    expect(store.hasToken()).toBe(true);
    repo.willReturn('resetPassword', OK);
    await store.submit('p', 'p');
    expect(repo.calls).toEqual([{ method: 'resetPassword', args: ['abc', 'p'] }]);
  });

  it('ResetPasswordStore_Mismatch_SetsFlagAndMakesNoCall', async () => {
    const { store, repo } = setup('#t=abc');
    await store.submit('one', 'two');
    expect(store.mismatch()).toBe(true);
    expect(repo.calls).toHaveLength(0);
    repo.willReturn('resetPassword', OK);
    await store.submit('same', 'same');
    expect(store.mismatch()).toBe(false);
    expect(repo.calls).toHaveLength(1);
  });

  it('ResetPasswordStore_Success_SetsDone', async () => {
    const { store, repo } = setup('#t=abc');
    repo.willReturn('resetPassword', OK);
    await store.submit('p', 'p');
    expect(store.done()).toBe(true);
    expect(store.canSubmit()).toBe(false);
  });

  it('ResetPasswordStore_Breached_BindsPasswordFieldError_TokenStaysUsable', async () => {
    const { store, repo } = setup('#t=abc');
    repo.willFail('resetPassword', appError('breached_password', 400, 'validation'));
    await store.submit('p', 'p');
    expect(store.done()).toBe(false);
    expect(store.expired()).toBe(false);
    expect(store.error()?.fieldErrors['password']).toBeDefined();
    expect(store.formError()).toBeNull();
    expect(store.canSubmit()).toBe(true);
    repo.willReturn('resetPassword', OK);
    await store.submit('p2', 'p2');
    expect(repo.calls.map((c) => c.args[0])).toEqual(['abc', 'abc']);
    expect(store.done()).toBe(true);
  });

  it('ResetPasswordStore_WeakPassword_RenamesWirePointerToPasswordControl', async () => {
    const { store, repo } = setup('#t=abc');
    repo.willFail('resetPassword', appError('validation_failed', 400, 'validation', { fieldErrors: { newPassword: ['weak'] } }));
    await store.submit('p', 'p');
    expect(Object.keys(store.error()?.fieldErrors ?? {})).toEqual(['password']);
    expect(store.expired()).toBe(false);
    expect(store.canSubmit()).toBe(true);
  });

  it('ResetPasswordStore_UsedOrExpiredToken_SetsExpiredAndStopsSubmitting', async () => {
    const { store, repo } = setup('#t=abc');
    repo.willFail('resetPassword', appError('validation_failed', 400, 'validation'));
    await store.submit('p', 'p');
    expect(store.expired()).toBe(true);
    expect(store.done()).toBe(false);
    expect(store.canSubmit()).toBe(false);
    await store.submit('p', 'p');
    expect(repo.calls).toHaveLength(1);
  });

  it('ResetPasswordStore_DoubleSubmit_OneCall', async () => {
    const { store, repo } = setup('#t=abc');
    repo.defer();
    const first = store.submit('p', 'p');
    const second = store.submit('p', 'p');
    repo.resolveNext(OK);
    await Promise.all([first, second]);
    expect(repo.calls).toHaveLength(1);
  });

  it('ResetPasswordStore_RateLimited_HoldsRetryAfter', async () => {
    const { store, repo } = setup('#t=abc');
    repo.willFail('resetPassword', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 12 }));
    await store.submit('p', 'p');
    expect(store.retryAfterSec()).toBe(12);
    expect(store.canSubmit()).toBe(false);
    expect(store.formError()).toBe('auth.rateLimited.message');
    store.retryAfterElapsed();
    expect(store.canSubmit()).toBe(true);
  });

  it('ResetPasswordStore_ResultAfterDestroy_IsIgnored', async () => {
    const { store, repo } = setup('#t=abc');
    repo.defer();
    const pending = store.submit('p', 'p');
    TestBed.resetTestingModule();
    repo.resolveNext(OK);
    await pending;
    expect(store.done()).toBe(false);
  });

  it('ResetPasswordStore_ResultAfterGenerationChange_IsIgnored', async () => {
    const { store, repo, session } = setup('#t=abc');
    repo.defer();
    const pending = store.submit('p', 'p');
    session.reset();
    TestBed.tick();
    expect(store.submitting()).toBe(false);
    repo.resolveNext(OK);
    await pending;
    expect(store.done()).toBe(false);
    expect(store.submitting()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.expired()).toBe(false);
  });

  it('ResetPasswordStore_FailureAfterGenerationChange_PaintsNothingAndKeepsTokenUsable', async () => {
    const { store, repo, session } = setup('#t=abc');
    repo.defer();
    const pending = store.submit('p', 'p');
    session.reset();
    TestBed.tick();
    repo.resolveNext({ ok: false, error: appError('validation_failed', 400, 'validation') });
    await pending;
    expect(store.error()).toBeNull();
    expect(store.formError()).toBeNull();
    expect(store.expired()).toBe(false);
    expect(store.canSubmit()).toBe(true);
  });

  it('ResetPasswordStore_ClearMismatch_DropsTheMismatchVerdict', async () => {
    const { store } = setup('#t=abc');
    await store.submit('one', 'two');
    expect(store.mismatch()).toBe(true);
    store.clearMismatch();
    expect(store.mismatch()).toBe(false);
  });

  it('ResetPasswordStore_Token_IsRemovedFromRouterStateButStillSent', async () => {
    history.replaceState(null, '', '/reset-password#t=abc');
    const repo = new FakeAuthRepository();
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideRouter([{ path: 'reset-password', children: [] }]),
        { provide: SessionApi, useValue: new FakeSessionApi() },
        { provide: AuthRepository, useValue: repo },
        ResetPasswordStore,
      ],
    });
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/reset-password#t=abc');
    expect(router.url).toContain('t=abc');
    const store = TestBed.inject(ResetPasswordStore);
    await vi.waitFor(() => expect(router.url).not.toContain('t='));
    expect(router.url).toBe('/reset-password');
    expect(router.routerState.snapshot.root.fragment).toBeNull();
    repo.willReturn('resetPassword', OK);
    await store.submit('Str0ng!pass', 'Str0ng!pass');
    expect(repo.calls).toEqual([{ method: 'resetPassword', args: ['abc', 'Str0ng!pass'] }]);
  });

  it('ResetPasswordStore_RouterFragmentClearRejects_NoUnhandledRejectionAndTokenStillSent', async () => {
    history.replaceState(null, '', '/reset-password#t=abc');
    const repo = new FakeAuthRepository();
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideRouter([{ path: 'reset-password', children: [] }]),
        { provide: SessionApi, useValue: new FakeSessionApi() },
        { provide: AuthRepository, useValue: repo },
        ResetPasswordStore,
      ],
    });
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/reset-password#t=abc');
    const navigate = vi.spyOn(router, 'navigateByUrl').mockRejectedValue(new Error('navigation failed'));
    const unhandled: unknown[] = [];
    const onUnhandled = (event: PromiseRejectionEvent): void => {
      event.preventDefault();
      unhandled.push(event.reason);
    };
    const onProcessUnhandled = (reason: unknown): void => {
      unhandled.push(reason);
    };
    globalThis.addEventListener('unhandledrejection', onUnhandled);
    process.on('unhandledRejection', onProcessUnhandled);
    try {
      const store = TestBed.inject(ResetPasswordStore);
      await vi.waitFor(() => expect(navigate).toHaveBeenCalledTimes(1));
      // The tsconfig lib predates Promise.withResolvers, so the executor form is the only typed option here.
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 20);
      });
      expect(unhandled).toEqual([]);
      repo.willReturn('resetPassword', OK);
      await store.submit('Str0ng!pass', 'Str0ng!pass');
      expect(repo.calls).toEqual([{ method: 'resetPassword', args: ['abc', 'Str0ng!pass'] }]);
    } finally {
      globalThis.removeEventListener('unhandledrejection', onUnhandled);
      process.off('unhandledRejection', onProcessUnhandled);
    }
  });
});
