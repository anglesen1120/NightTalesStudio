import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SILENT_REQUEST } from '@core/http';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { ReauthApiRepository } from './reauth.repository';

const REAUTH_URL = '/api/v1/auth/reauth';

function setup(): { repository: ReauthApiRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), ReauthApiRepository],
  });
  return { repository: TestBed.inject(ReauthApiRepository), http: TestBed.inject(HttpTestingController) };
}

describe('ReauthApiRepository', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('ReauthRepository_Reauth_PostsPasswordAndCode_SilentlyAndResolvesTimestamp', async () => {
    const { repository, http } = setup();
    const result = repository.reauth('pw', '123456');
    const req = http.expectOne(REAUTH_URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ password: 'pw', code: '123456' });
    expect(req.request.headers.has('Idempotency-Key')).toBe(false);
    expect(req.request.context.get(SILENT_REQUEST)).toBe(true);
    req.flush({ reauthenticatedAt: '2026-10-05T10:00:00Z' });

    await expect(result).resolves.toEqual({ ok: true, value: { reauthenticatedAt: '2026-10-05T10:00:00Z' } });
  });

  it('ReauthRepository_Reauth_EmptyCode_SendsNull', async () => {
    const { repository, http } = setup();
    const result = repository.reauth('pw', '');
    const req = http.expectOne(REAUTH_URL);
    expect(req.request.body).toEqual({ password: 'pw', code: null });
    req.flush({ reauthenticatedAt: '2026-10-05T10:00:00Z' });

    await expect(result).resolves.toMatchObject({ ok: true });
  });

  it.each([
    [401, { code: 'invalid_credentials' }, 'invalid_credentials', 'auth'],
    [401, { code: 'invalid_mfa_code' }, 'invalid_mfa_code', 'auth'],
    [401, { code: 'session_revoked' }, 'session_revoked', 'auth'],
    [500, null, 'internal_error', 'server'],
  ] as const)('ReauthRepository_Reauth_MapsHttpFailure_ToErrResult (%s %o)', async (status, body, code, kind) => {
    const { repository, http } = setup();
    const result = repository.reauth('pw', '123456');
    http.expectOne(REAUTH_URL).flush(body, { status, statusText: 'x' });

    const outcome = await result;
    expect(outcome.ok).toBe(false);
    expect(outcome).toMatchObject({ ok: false, error: { code, status, kind } });
  });

  it('ReauthRepository_Reauth_429_CarriesRetryAfterSeconds', async () => {
    const { repository, http } = setup();
    const result = repository.reauth('pw', '123456');
    http.expectOne(REAUTH_URL).flush({ code: 'rate_limited' }, { status: 429, statusText: 'x', headers: { 'Retry-After': '900' } });

    await expect(result).resolves.toMatchObject({ ok: false, error: { code: 'rate_limited', status: 429, kind: 'rate_limit', retryAfterSec: 900 } });
  });

  it('ReauthRepository_Reauth_NetworkFailure_IsNetworkErr', async () => {
    const { repository, http } = setup();
    const result = repository.reauth('pw', '123456');
    http.expectOne(REAUTH_URL).error(new ProgressEvent('error'));

    await expect(result).resolves.toMatchObject({ ok: false, error: { status: 0, kind: 'network' } });
  });
});
