import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SessionApi } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { MfaApiRepository } from './mfa.repository';

const SETUP = '/api/v1/me/mfa/setup';
const VERIFY = '/api/v1/me/mfa/verify';
const CODES = '/api/v1/me/mfa/recovery-codes';
const TEN = Array.from({ length: 10 }, (_, i) => `code-${i + 1}`);

function setup(): { repository: MfaApiRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      MfaApiRepository,
    ],
  });
  return { repository: TestBed.inject(MfaApiRepository), http: TestBed.inject(HttpTestingController) };
}

describe('MfaApiRepository', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    vi.restoreAllMocks();
  });

  describe('setup', () => {
    it('MfaRepository_Setup_Ok_ReturnsSecretAndUri_NoBody_NoIdempotencyKey', async () => {
      const { repository, http } = setup();
      const result = repository.setup();
      const req = http.expectOne(SETUP);
      expect(req.request.method).toBe('POST');
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      req.flush({ secret: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/NightTales:o?secret=JBSWY3DPEHPK3PXP' });
      expect(await result).toEqual({
        ok: true,
        value: { secret: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/NightTales:o?secret=JBSWY3DPEHPK3PXP' },
      });
    });

    it.each([{ secret: '', otpauthUri: 'otpauth://x' }, { secret: 'S' }, {}])('MfaRepository_Setup_IncompleteBody_IsUnknownError_%j', async (body) => {
      const { repository, http } = setup();
      const result = repository.setup();
      http.expectOne(SETUP).flush(body);
      const outcome = await result;
      expect(outcome.ok ? '' : outcome.error.code).toBe('unknown_error');
    });

    it.each([
      ['reauth_required', 401, 'auth'],
      ['concurrency_conflict', 409, 'conflict'],
      ['not_found', 404, 'not_found'],
    ])('MfaRepository_Setup_%s_ReachesTheCallerUnchanged', async (code, status, kind) => {
      const { repository, http } = setup();
      const result = repository.setup();
      http.expectOne(SETUP).flush({ code }, { status, statusText: 'x' });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code, status, kind });
    });
  });

  describe('verify', () => {
    it('MfaRepository_Verify_Ok_SendsCodeAndReturnsTenCodes', async () => {
      const { repository, http } = setup();
      const result = repository.verify('123456');
      const req = http.expectOne(VERIFY);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ code: '123456' });
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      req.flush({ recoveryCodes: TEN });
      expect(await result).toEqual({ ok: true, value: TEN });
    });

    it.each([[TEN.slice(0, 9)], [[...TEN, 'extra']], [[...TEN.slice(0, 9), '']], [undefined]])(
      'MfaRepository_Verify_NotExactlyTenNonEmptyCodes_IsUnknownError_%j',
      async (recoveryCodes) => {
        const { repository, http } = setup();
        const result = repository.verify('123456');
        http.expectOne(VERIFY).flush({ recoveryCodes });
        const outcome = await result;
        expect(outcome.ok ? '' : outcome.error.code).toBe('unknown_error');
      },
    );

    it('MfaRepository_Verify_InvalidCode_IsAuthError401', async () => {
      const { repository, http } = setup();
      const result = repository.verify('000000');
      http.expectOne(VERIFY).flush({ code: 'invalid_mfa_code' }, { status: 401, statusText: 'Unauthorized' });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'invalid_mfa_code', status: 401, kind: 'auth' });
    });

    it('MfaRepository_Verify_RateLimited_CarriesRetryAfter', async () => {
      const { repository, http } = setup();
      const result = repository.verify('000000');
      http.expectOne(VERIFY).flush({ code: 'rate_limited' }, { status: 429, statusText: 'x', headers: { 'Retry-After': '900' } });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ kind: 'rate_limit', retryAfterSec: 900 });
    });

    it('MfaRepository_Verify_Network_IsNetworkError', async () => {
      const { repository, http } = setup();
      const result = repository.verify('123456');
      http.expectOne(VERIFY).error(new ProgressEvent('error'));
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'network_error', status: 0 });
    });
  });

  describe('regenerateRecoveryCodes', () => {
    it('MfaRepository_Regenerate_Ok_ReturnsFreshTenCodes', async () => {
      const { repository, http } = setup();
      const result = repository.regenerateRecoveryCodes();
      const req = http.expectOne(CODES);
      expect(req.request.method).toBe('POST');
      expect(req.request.headers.has('Idempotency-Key')).toBe(false);
      req.flush({ recoveryCodes: TEN });
      expect(await result).toEqual({ ok: true, value: TEN });
    });

    it('MfaRepository_Regenerate_ReauthRequired_ReachesTheCallerUnchanged', async () => {
      const { repository, http } = setup();
      const result = repository.regenerateRecoveryCodes();
      http.expectOne(CODES).flush({ code: 'reauth_required' }, { status: 401, statusText: 'Unauthorized' });
      const outcome = await result;
      expect(outcome.ok ? null : outcome.error).toMatchObject({ code: 'reauth_required', status: 401 });
    });
  });
});
