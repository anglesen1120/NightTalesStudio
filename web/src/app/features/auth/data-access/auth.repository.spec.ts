import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SILENT_REQUEST } from '@core/http';
import { SessionApi } from '@core/session';
import { FakeSessionApi, fakeJwt } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { AuthApiRepository } from './auth.repository';

const LOGIN = '/api/v1/auth/login';
const FORGOT = '/api/v1/auth/forgot-password';
const RESET = '/api/v1/auth/reset-password';

function setup(): { repository: AuthApiRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      AuthApiRepository,
    ],
  });
  return { repository: TestBed.inject(AuthApiRepository), http: TestBed.inject(HttpTestingController) };
}

describe('AuthApiRepository', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    vi.restoreAllMocks();
  });

  describe('login', () => {
    it('AuthRepository_Login_Ok_MapsResponseToSessionWithEpochMs', async () => {
      const { repository, http } = setup();
      const token = fakeJwt('owner', ['pwd']);
      const result = repository.login('a@b.c', 'pw');
      const req = http.expectOne(LOGIN);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ email: 'a@b.c', password: 'pw' });
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      expect(req.request.headers.has('Authorization')).toBe(false);
      expect(req.request.context.get(SILENT_REQUEST)).toBe(true);
      req.flush({ accessToken: token, expiresAt: '2026-10-04T10:15:00.000+00:00', role: 'owner', amr: ['pwd', 'otp'] });
      expect(await result).toEqual({
        ok: true,
        value: { kind: 'ok', session: { accessToken: token, expiresAt: Date.parse('2026-10-04T10:15:00Z'), role: 'owner', amr: ['pwd', 'otp'] } },
      });
    });

    it('AuthRepository_Login_UnparsableExpiresAt_IsStaleZero', async () => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http.expectOne(LOGIN).flush({ accessToken: 'a.b.c', expiresAt: 'nope', role: 'admin', amr: [] });
      const outcome = await result;
      expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.session.expiresAt : -1).toBe(0);
    });

    it('AuthRepository_Login_UnknownRole_IsUnknownErrorNotASession', async () => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http.expectOne(LOGIN).flush({ accessToken: 'a.b.c', expiresAt: '2026-10-04T10:15:00Z', role: 'root', amr: [] });
      const outcome = await result;
      expect(outcome.ok).toBe(false);
      expect(outcome.ok ? '' : outcome.error.code).toBe('unknown_error');
    });

    it('AuthRepository_Login_MfaRequired_IsOkMfaOutcome', async () => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http.expectOne(LOGIN).flush({ code: 'mfa_required', mfaToken: 'mfa-1' }, { status: 401, statusText: 'Unauthorized' });
      expect(await result).toEqual({ ok: true, value: { kind: 'mfa', mfaToken: 'mfa-1' } });
    });

    it.each([{ token: 'enrol-1' }, { mfaToken: 'enrol-1' }])('AuthRepository_Login_EnrolmentRequired_IsOkEnrolmentOutcome_%j', async (body) => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http.expectOne(LOGIN).flush({ code: 'mfa_enrolment_required', ...body }, { status: 401, statusText: 'Unauthorized' });
      expect(await result).toEqual({ ok: true, value: { kind: 'enrolment', token: 'enrol-1' } });
    });

    it.each(['mfa_required', 'mfa_enrolment_required'])('AuthRepository_Login_%s_WithoutToken_StaysAnError', async (code) => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http.expectOne(LOGIN).flush({ code }, { status: 401, statusText: 'Unauthorized' });
      const outcome = await result;
      expect(outcome.ok).toBe(false);
    });

    it('AuthRepository_Login_InvalidCredentials_IsError401', async () => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'bad');
      http.expectOne(LOGIN).flush({ code: 'invalid_credentials', detail: 'no such user' }, { status: 401, statusText: 'Unauthorized' });
      const outcome = await result;
      expect(outcome.ok).toBe(false);
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'invalid_credentials', status: 401, kind: 'auth' });
    });

    it('AuthRepository_Login_RateLimited_CarriesRetryAfter', async () => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http
        .expectOne(LOGIN)
        .flush({ code: 'rate_limited' }, { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '900' } });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'rate_limited', status: 429, kind: 'rate_limit', retryAfterSec: 900 });
    });

    it('AuthRepository_Login_Network_IsNetworkError', async () => {
      const { repository, http } = setup();
      const result = repository.login('a@b.c', 'pw');
      http.expectOne(LOGIN).error(new ProgressEvent('error'));
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'network_error', status: 0 });
    });
  });

  describe('loginMfa', () => {
    const MFA = '/api/v1/auth/login/mfa';
    const OK_BODY = { accessToken: 'a.b.c', expiresAt: '2026-10-04T10:15:00Z', role: 'owner', amr: ['pwd', 'otp'] };

    it('AuthRepository_LoginMfa_Totp_SendsCodeAndNullRecoveryCode_NoIdempotencyKey', async () => {
      const { repository, http } = setup();
      const result = repository.loginMfa('mfa-1', '123456', 'totp');
      const req = http.expectOne(MFA);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ mfaToken: 'mfa-1', code: '123456', recoveryCode: null });
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      req.flush(OK_BODY);
      const outcome = await result;
      expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.session : null).toMatchObject({
        accessToken: 'a.b.c',
        role: 'owner',
        amr: ['pwd', 'otp'],
        expiresAt: Date.parse('2026-10-04T10:15:00Z'),
      });
    });

    it('AuthRepository_LoginMfa_Recovery_SendsRecoveryCodeAndNullCode', async () => {
      const { repository, http } = setup();
      const result = repository.loginMfa('mfa-1', 'abcd-efgh', 'recovery');
      const req = http.expectOne(MFA);
      expect(req.request.body).toEqual({ mfaToken: 'mfa-1', code: null, recoveryCode: 'abcd-efgh' });
      req.flush(OK_BODY);
      expect((await result).ok).toBe(true);
    });

    it.each([
      ['invalid_mfa_code', 401, 'auth'],
      ['mfa_token_invalid', 401, 'auth'],
    ])('AuthRepository_LoginMfa_%s_IsErrorNotChallenge', async (code, status, kind) => {
      const { repository, http } = setup();
      const result = repository.loginMfa('mfa-1', '000000', 'totp');
      http.expectOne(MFA).flush({ code }, { status, statusText: 'Unauthorized' });
      const outcome = await result;
      expect(outcome.ok).toBe(false);
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code, status, kind });
    });

    it('AuthRepository_LoginMfa_RateLimited_CarriesRetryAfter', async () => {
      const { repository, http } = setup();
      const result = repository.loginMfa('mfa-1', '000000', 'totp');
      http.expectOne(MFA).flush({ code: 'rate_limited' }, { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '60' } });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'rate_limited', kind: 'rate_limit', retryAfterSec: 60 });
    });

    it('AuthRepository_LoginMfa_UnknownRole_IsUnknownError', async () => {
      const { repository, http } = setup();
      const result = repository.loginMfa('mfa-1', '123456', 'totp');
      http.expectOne(MFA).flush({ ...OK_BODY, role: 'root' });
      const outcome = await result;
      expect(outcome.ok ? '' : outcome.error.code).toBe('unknown_error');
    });
  });

  describe('forgotPassword', () => {
    it('AuthRepository_Forgot_Accepted_IsOkVoid_NoIdempotencyKey', async () => {
      const { repository, http } = setup();
      const result = repository.forgotPassword('a@b.c');
      const req = http.expectOne(FORGOT);
      expect(req.request.body).toEqual({ email: 'a@b.c' });
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      expect(req.request.context.get(SILENT_REQUEST)).toBe(true);
      req.flush(null, { status: 202, statusText: 'Accepted' });
      expect(await result).toEqual({ ok: true, value: undefined });
    });

    it('AuthRepository_Forgot_Accepted_NeverBranchesOnBodyOrHeaders (account enumeration)', async () => {
      const outcomes: unknown[] = [];
      const replies: Array<{ body: object | null; headers: Record<string, string> }> = [
        { body: null, headers: {} },
        { body: { queued: true, id: 'mail-1' }, headers: { 'X-Mail': 'queued', 'Retry-After': '9' } },
        { body: { queued: false }, headers: { 'X-Mail': 'skipped' } },
      ];
      const { repository, http } = setup();
      for (const reply of replies) {
        const result = repository.forgotPassword('a@b.c');
        http.expectOne(FORGOT).flush(reply.body, { status: 202, statusText: 'Accepted', headers: reply.headers });
        outcomes.push(await result);
      }
      for (const outcome of outcomes) expect(outcome).toEqual({ ok: true, value: undefined });
    });

    it('AuthRepository_Forgot_RateLimited_IsErrorWithRetryAfter', async () => {
      const { repository, http } = setup();
      const result = repository.forgotPassword('a@b.c');
      http.expectOne(FORGOT).flush({ code: 'rate_limited' }, { status: 429, statusText: 'x', headers: { 'Retry-After': '30' } });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'rate_limited', retryAfterSec: 30 });
    });
  });

  describe('resetPassword', () => {
    it('AuthRepository_Reset_Ok_SendsTokenAndNewPasswordInBodyOnly', async () => {
      const { repository, http } = setup();
      const result = repository.resetPassword('tok', 'Str0ng!pass');
      const req = http.expectOne(RESET);
      expect(req.request.urlWithParams).toBe(RESET);
      expect(req.request.body).toEqual({ token: 'tok', newPassword: 'Str0ng!pass' });
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      req.flush(null, { status: 204, statusText: 'No Content' });
      expect(await result).toEqual({ ok: true, value: undefined });
    });

    it('AuthRepository_Reset_BreachedPassword_IsValidationError', async () => {
      const { repository, http } = setup();
      const result = repository.resetPassword('tok', 'password');
      http.expectOne(RESET).flush({ code: 'breached_password' }, { status: 400, statusText: 'Bad Request' });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error.code).toBe('breached_password');
    });
  });
});
