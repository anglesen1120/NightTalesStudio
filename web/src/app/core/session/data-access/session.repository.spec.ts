import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SILENT_REQUEST } from '@core/http';
import type { MockInstance } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { HttpSessionRepository } from './session.repository';

const REFRESH_URL = '/api/v1/auth/refresh';
const LOGOUT_URL = '/api/v1/auth/logout';

function setup(): { repository: HttpSessionRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), HttpSessionRepository],
  });
  return { repository: TestBed.inject(HttpSessionRepository), http: TestBed.inject(HttpTestingController) };
}

describe('HttpSessionRepository', () => {
  let warn: MockInstance<typeof console.warn>;

  beforeEach(() => {
    warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    vi.restoreAllMocks();
  });

  describe('refresh', () => {
    it('SessionRepository_Refresh_ParsesIsoExpiresAtToEpochMs', async () => {
      const { repository, http } = setup();
      const result = repository.refresh();
      const req = http.expectOne(REFRESH_URL);
      expect(req.request.method).toBe('POST');
      expect(req.request.headers.has('Authorization')).toBe(false);
      expect(req.request.urlWithParams).toBe(REFRESH_URL);
      expect(req.request.context.get(SILENT_REQUEST)).toBe(true);
      req.flush({ accessToken: 'a.b.c', expiresAt: '2026-10-04T10:15:00.000+00:00' });

      await expect(result).resolves.toEqual({ accessToken: 'a.b.c', expiresAt: Date.parse('2026-10-04T10:15:00Z') });
      expect(warn).not.toHaveBeenCalled();
    });

    it.each([['not a date'], [''], [undefined], [12345]])('SessionRepository_Refresh_UnparsableExpiresAt_IsStale (%s)', async (raw) => {
      const { repository, http } = setup();
      const result = repository.refresh();
      http.expectOne(REFRESH_URL).flush({ accessToken: 'a.b.c', expiresAt: raw });

      await expect(result).resolves.toEqual({ accessToken: 'a.b.c', expiresAt: 0 });
      expect(warn).toHaveBeenCalledTimes(1);
      expect(String(warn.mock.calls[0]?.[0])).not.toContain('a.b.c');
    });

    it.each([
      [401, { code: 'refresh_token_reused' }, 'refresh_token_reused', 'auth'],
      [401, { code: 'session_revoked' }, 'session_revoked', 'auth'],
      [401, null, 'unauthorized', 'auth'],
      [403, { code: 'forbidden' }, 'forbidden', 'auth'],
      [500, null, 'internal_error', 'server'],
    ] as const)('SessionRepository_Refresh_MapsHttpFailure_ToAppError (%s %o)', async (status, body, code, kind) => {
      const { repository, http } = setup();
      const result = repository.refresh();
      http.expectOne(REFRESH_URL).flush(body, { status, statusText: 'x' });

      const error: unknown = await result.catch((e: unknown) => e);
      expect(error).not.toBeInstanceOf(HttpErrorResponse);
      expect(error).toMatchObject({ code, status, kind });
    });

    it('SessionRepository_Refresh_NetworkFailure_IsNetworkError', async () => {
      const { repository, http } = setup();
      const result = repository.refresh();
      http.expectOne(REFRESH_URL).error(new ProgressEvent('error'));

      await expect(result).rejects.toMatchObject({ code: 'network_error', status: 0, kind: 'network' });
    });
  });

  describe('logout', () => {
    const TOKEN = 'access-A';
    const bearerOf = (req: { request: { headers: { get: (name: string) => string | null } } }): string | null =>
      req.request.headers.get('Authorization');

    it('SessionRepository_Logout_Posts204WithBearer_Resolves', async () => {
      const { repository, http } = setup();
      const result = repository.logout(TOKEN);
      const req = http.expectOne(LOGOUT_URL);
      expect(req.request.method).toBe('POST');
      expect(req.request.context.get(SILENT_REQUEST)).toBe(true);
      expect(bearerOf(req)).toBe(`Bearer ${TOKEN}`);
      req.flush(null, { status: 204, statusText: 'No Content' });

      await expect(result).resolves.toBeUndefined();
    });

    it('SessionRepository_Logout_401SessionRevoked_FamilyGone_Resolves', async () => {
      const { repository, http } = setup();
      const result = repository.logout(TOKEN);
      http.expectOne(LOGOUT_URL).flush({ code: 'session_revoked' }, { status: 401, statusText: 'Unauthorized' });

      await expect(result).resolves.toBeUndefined();
    });

    it('SessionRepository_Logout_401Unauthorized_WithToken_Rejects', async () => {
      const { repository, http } = setup();
      const result = repository.logout(TOKEN);
      http.expectOne(LOGOUT_URL).flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });

      await expect(result).rejects.toMatchObject({ code: 'unauthorized', status: 401 });
    });

    it('SessionRepository_Logout_NullToken_StillPostsWithoutHeader_AndResolvesOnAnyOutcome', async () => {
      const { repository, http } = setup();
      const unauthorized = repository.logout(null);
      const first = http.expectOne(LOGOUT_URL);
      expect(bearerOf(first)).toBeNull();
      first.flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
      await expect(unauthorized).resolves.toBeUndefined();

      const server = repository.logout(null);
      http.expectOne(LOGOUT_URL).flush(null, { status: 500, statusText: 'x' });
      await expect(server).resolves.toBeUndefined();

      const offline = repository.logout(null);
      http.expectOne(LOGOUT_URL).error(new ProgressEvent('error'));
      await expect(offline).resolves.toBeUndefined();
    });

    it('SessionRepository_Logout_OtherFailures_WithToken_RejectWithAppError', async () => {
      const { repository, http } = setup();
      const server = repository.logout(TOKEN);
      http.expectOne(LOGOUT_URL).flush(null, { status: 500, statusText: 'x' });
      await expect(server).rejects.toMatchObject({ status: 500, kind: 'server' });

      const offline = repository.logout(TOKEN);
      http.expectOne(LOGOUT_URL).error(new ProgressEvent('error'));
      await expect(offline).rejects.toMatchObject({ code: 'network_error', status: 0 });
    });
  });
});
