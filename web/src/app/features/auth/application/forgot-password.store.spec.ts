import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { FakeAuthRepository } from '../testing/fake-auth.repository';
import { AuthRepository } from './auth.repository';
import { ForgotPasswordStore } from './forgot-password.store';

const OK = { ok: true, value: undefined } as const;

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

function setup(): { store: ForgotPasswordStore; repo: FakeAuthRepository; session: SessionStore } {
  const repo = new FakeAuthRepository();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: AuthRepository, useValue: repo },
      ForgotPasswordStore,
    ],
  });
  return { store: TestBed.inject(ForgotPasswordStore), repo, session: TestBed.inject(SessionStore) };
}

describe('ForgotPasswordStore', () => {
  it('ForgotPasswordStore_Accepted_SetsSent', async () => {
    const { store, repo } = setup();
    repo.willReturn('forgotPassword', OK);
    await store.submit('a@b.c');
    expect(store.sent()).toBe(true);
    expect(store.error()).toBeNull();
    expect(repo.calls).toEqual([{ method: 'forgotPassword', args: ['a@b.c'] }]);
  });

  it('ForgotPasswordStore_SameSentPath_ForKnownAndUnknownAddress', async () => {
    const known = setup();
    known.repo.willReturn('forgotPassword', OK);
    await known.store.submit('known@b.c');
    TestBed.resetTestingModule();
    const unknown = setup();
    unknown.repo.willReturn('forgotPassword', OK);
    await unknown.store.submit('unknown@b.c');
    expect([known.store.sent(), known.store.error(), known.store.formError()]).toEqual([
      unknown.store.sent(),
      unknown.store.error(),
      unknown.store.formError(),
    ]);
  });

  it('ForgotPasswordStore_ValidationFailed_SetsErrorAndNotSent', async () => {
    const { store, repo } = setup();
    repo.willFail('forgotPassword', appError('validation_failed', 400, 'validation', { fieldErrors: { email: ['bad'] } }));
    await store.submit('bad');
    expect(store.sent()).toBe(false);
    expect(store.error()?.code).toBe('validation_failed');
    expect(store.formError()).toBeNull();
  });

  it('ForgotPasswordStore_Network_MapsFormError_NoRetry', async () => {
    const { store, repo } = setup();
    repo.willFail('forgotPassword', appError('network_error', 0, 'network'));
    await store.submit('a@b.c');
    expect(store.formError()).toBe('errors.network_error');
    expect(store.sent()).toBe(false);
    expect(repo.calls).toHaveLength(1);
  });

  it('ForgotPasswordStore_DoubleSubmit_OneRequest', async () => {
    const { store, repo } = setup();
    repo.defer();
    const first = store.submit('a@b.c');
    const second = store.submit('a@b.c');
    expect(store.canSubmit()).toBe(false);
    repo.resolveNext();
    await Promise.all([first, second]);
    expect(repo.calls).toHaveLength(1);
    expect(store.sent()).toBe(true);
  });

  it('ForgotPasswordStore_RateLimited_BlocksUntilElapsed_SecondRestarts', async () => {
    const { store, repo } = setup();
    repo.willFail('forgotPassword', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 20 }));
    await store.submit('a@b.c');
    expect(store.retryAfterSec()).toBe(20);
    expect(store.canSubmit()).toBe(false);
    expect(store.formError()).toBe('auth.rateLimited.message');
    await store.submit('a@b.c');
    expect(repo.calls).toHaveLength(1);
    store.retryAfterElapsed();
    expect(store.canSubmit()).toBe(true);
    repo.willFail('forgotPassword', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 7 }));
    await store.submit('a@b.c');
    expect(store.retryAfterSec()).toBe(7);
  });

  it('ForgotPasswordStore_RateLimitedWithRetryAfterZero_KeepsZeroUntilTheCountdownElapses', async () => {
    const { store, repo } = setup();
    repo.willFail('forgotPassword', appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 0 }));
    await store.submit('a@b.c');
    expect(store.retryAfterSec()).toBe(0);
    expect(store.canSubmit()).toBe(false);
    store.retryAfterElapsed();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.canSubmit()).toBe(true);
  });

  it('ForgotPasswordStore_ResultAfterDestroy_IsIgnored', async () => {
    const { store, repo } = setup();
    repo.defer();
    const pending = store.submit('a@b.c');
    TestBed.resetTestingModule();
    repo.resolveNext(OK);
    await pending;
    expect(store.sent()).toBe(false);
  });

  it('ForgotPasswordStore_ResultAfterGenerationChange_IsIgnored', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const pending = store.submit('a@b.c');
    session.reset();
    TestBed.tick();
    expect(store.submitting()).toBe(false);
    repo.resolveNext(OK);
    await pending;
    expect(store.sent()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.formError()).toBeNull();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.submitting()).toBe(false);
  });

  it('ForgotPasswordStore_FailureAfterGenerationChange_PaintsNoErrorAndNoCountdown', async () => {
    const { store, repo, session } = setup();
    repo.defer();
    const pending = store.submit('a@b.c');
    session.reset();
    TestBed.tick();
    repo.resolveNext({ ok: false, error: appError('rate_limited', 429, 'rate_limit', { retryAfterSec: 30 }) });
    await pending;
    expect(store.error()).toBeNull();
    expect(store.formError()).toBeNull();
    expect(store.retryAfterSec()).toBeNull();
    expect(store.canSubmit()).toBe(true);
  });
});
