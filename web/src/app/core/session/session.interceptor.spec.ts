import { HttpClient, HttpContext, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, type TestRequest } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { type Observable, firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { SILENT_REQUEST } from '../http/http-context-tokens';
import { silently, userAction } from '../http/http-context';
import { HttpSessionRepository } from './data-access/session.repository';
import { ActivityTracker } from './activity';
import { SESSION_SCOPED, type SessionScoped } from './session-scope';
import { SessionApi } from './session.api';
import { sessionInterceptor } from './session.interceptor';
import { SessionStore } from './session-store';
import { FakeSessionApi, fakeJwt } from './testing/fake-session.api';
import type { TokenPair } from './token-pair';

/** Comfortably outside the 30 s freshness margin. */
const FRESH_MS = 10 * 60_000;
/** Inside the margin: the next request must refresh first. */
const STALE_MS = 1_000;

const AUTH_URLS = [
  '/api/v1/auth/login',
  '/api/v1/auth/login/mfa',
  '/api/v1/auth/refresh',
  '/api/v1/auth/forgot-password',
  '/api/v1/auth/reset-password',
];

const PASS_THROUGH_CODES = [
  'reauth_required',
  'invalid_credentials',
  'invalid_mfa_code',
  'mfa_token_invalid',
  'mfa_enrolment_required',
  'hub_ticket_invalid',
  'refresh_token_reused',
  'session_revoked',
];

type Outcome = { readonly ok: true; readonly value: unknown } | { readonly ok: false; readonly error: unknown };

class CountingScope implements SessionScoped {
  resets = 0;
  reset(): void {
    this.resets += 1;
  }
}

function tokens(amr: string[], ttlMs: number): TokenPair {
  return { accessToken: fakeJwt('user', amr), expiresAt: Date.now() + ttlMs };
}

function run(source: Observable<unknown>): Promise<Outcome> {
  return firstValueFrom(source).then(
    (value): Outcome => ({ ok: true, value }),
    (error: unknown): Outcome => ({ ok: false, error }),
  );
}

function httpError(outcome: Outcome): HttpErrorResponse {
  if (outcome.ok || !(outcome.error instanceof HttpErrorResponse)) throw new Error('expected an HttpErrorResponse');
  return outcome.error;
}

function settle(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 25));
}


describe('SessionInterceptor', () => {
  let fake: FakeSessionApi;
  let store: SessionStore;
  let http: HttpClient;
  let mock: HttpTestingController;
  let router: Router;
  let mapper: AppErrorMapper;
  let scopes: CountingScope[];
  let active: boolean;

  /** The first token the session holds; the refresh hands out `fake.next`, which differs from it. */
  let first: TokenPair;

  function signIn(ttlMs: number): void {
    first = tokens(['pwd'], ttlMs);
    store.signIn(first, 'user', ['pwd']);
  }

  /** Waits for a request the interceptor sends after an async refresh, or for a pass-through one. */
  function request(url: string): Promise<TestRequest> {
    return vi.waitFor(() => mock.expectOne(url), { timeout: 2_000, interval: 1 });
  }

  function authorization(req: TestRequest): string | null {
    return req.request.headers.get('Authorization');
  }

  function unauthorized(req: TestRequest, body: object | null = null, headers: Record<string, string> = {}): void {
    req.flush(body, { status: 401, statusText: 'Unauthorized', headers });
  }

  beforeEach(() => {
    active = true;
    scopes = [new CountingScope(), new CountingScope(), new CountingScope()];
    fake = new FakeSessionApi();
    fake.next = tokens(['pwd', 'otp'], FRESH_MS);
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideRouter([{ path: '**', children: [] }]),
        provideHttpClient(withInterceptors([sessionInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionApi, useValue: fake },
        { provide: ActivityTracker, useValue: { isActive: () => active, isVisible: () => true } },
        ...scopes.map((scope) => ({ provide: SESSION_SCOPED, useValue: scope, multi: true })),
      ],
    });
    store = TestBed.inject(SessionStore);
    http = TestBed.inject(HttpClient);
    mock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    mapper = TestBed.inject(AppErrorMapper);
  });

  afterEach(() => {
    mock.verify();
    vi.restoreAllMocks();
  });

  describe('AttachesBearer_ExceptAuthRoutes', () => {
    it('attaches the current token to a business route', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      const req = await request('/api/v1/series');
      expect(authorization(req)).toBe(`Bearer ${first.accessToken}`);
      req.flush({});
      expect((await done).ok).toBe(true);
      expect(fake.calls).toBe(0);
    });

    it.each(AUTH_URLS)('sends %s without an Authorization header and without touching the session', async (url) => {
      signIn(FRESH_MS);
      const done = run(http.post(url, {}));
      const req = await request(url);
      expect(authorization(req)).toBeNull();
      expect(req.request.urlWithParams).not.toContain(first.accessToken);
      req.flush({});
      expect((await done).ok).toBe(true);
      expect(fake.calls).toBe(0);
    });

    it.each([
      '/api/v1/auth/logout',
      '/api/v1/auth/refresh/',
      '/api/v1/auth/REFRESH',
      '/api/v1/auth/%72efresh',
      '/api/v1/auth/refresh?x=1',
      '/api/v1/auth/refresh#frag',
    ])('treats %s as an auth route (matched on the decoded pathname)', async (url) => {
      signIn(FRESH_MS);
      const done = run(http.post(url, {}));
      const req = await request(url);
      expect(authorization(req)).toBeNull();
      req.flush({});
      await done;
    });

    it('does not mistake a lookalike for an auth route', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/auth/refresh-evil'));
      const req = await request('/api/v1/auth/refresh-evil');
      expect(authorization(req)).toBe(`Bearer ${first.accessToken}`);
      req.flush({});
      await done;
    });

    it.each(['https://evil.example/api/v1/series', '/assets/i18n/en.json', '/other/api/v1/series'])(
      'never attaches the token to %s (cross-origin or outside /api/v1/)',
      async (url) => {
        signIn(FRESH_MS);
        const done = run(http.get(url));
        const req = await request(url);
        expect(authorization(req)).toBeNull();
        req.flush({});
        await done;
        expect(fake.calls).toBe(0);
      },
    );

    it('sends the enrolment token while in enrolment mode', async () => {
      store.beginEnrolment('enrol-token');
      const done = run(http.get('/api/v1/me/mfa/status'));
      const req = await request('/api/v1/me/mfa/status');
      expect(authorization(req)).toBe('Bearer enrol-token');
      req.flush({});
      await done;
      expect(fake.calls).toBe(0);
    });
  });

  describe('refresh before sending', () => {
    it('refreshes a stale token first and sends the new one', async () => {
      signIn(STALE_MS);
      const done = run(http.get('/api/v1/series'));
      const req = await request('/api/v1/series');
      expect(authorization(req)).toBe(`Bearer ${fake.next.accessToken}`);
      req.flush({});
      await done;
      expect(fake.calls).toBe(1);
    });

    it('five concurrent requests with a stale token produce exactly one refresh', async () => {
      signIn(STALE_MS);
      fake.mode = 'deferred';
      const urls = [1, 2, 3, 4, 5].map((i) => `/api/v1/items/${i}`);
      const outcomes = urls.map((url) => run(http.get(url)));
      await vi.waitFor(() => expect(fake.calls).toBe(1), { timeout: 2_000, interval: 1 });
      fake.resolveNext(fake.next);
      const requests = await Promise.all(urls.map((url) => request(url)));
      for (const req of requests) {
        expect(authorization(req)).toBe(`Bearer ${fake.next.accessToken}`);
        req.flush({});
      }
      expect((await Promise.all(outcomes)).every((outcome) => outcome.ok)).toBe(true);
      expect(fake.calls).toBe(1);
    });
  });

  describe('401Retryable_RefreshesOnceAndReplaysOnce', () => {
    it.each([
      ['no code', null, {}],
      ['unauthorized', { code: 'unauthorized' }, {}],
      ['unauthenticated', { code: 'unauthenticated' }, {}],
      ['an invalid_token challenge', null, { 'WWW-Authenticate': 'Bearer error="invalid_token"' }],
    ])('%s: two attempts, one refresh, the replay carries the new token', async (_name, body, headers) => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      const original = await request('/api/v1/series');
      expect(authorization(original)).toBe(`Bearer ${first.accessToken}`);
      unauthorized(original, body, headers);
      const replay = await request('/api/v1/series');
      expect(authorization(replay)).toBe(`Bearer ${fake.next.accessToken}`);
      replay.flush({ ok: 1 });
      expect(await done).toEqual({ ok: true, value: { ok: 1 } });
      expect(fake.calls).toBe(1);
      await settle();
      mock.expectNone('/api/v1/series');
      expect(store.status()).toBe('authenticated');
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
    });

    it('five concurrent 401s on one rejected token produce exactly one refresh and five replays', async () => {
      signIn(FRESH_MS);
      const urls = [1, 2, 3, 4, 5].map((i) => `/api/v1/items/${i}`);
      const outcomes = urls.map((url) => run(http.get(url)));
      const originals = await Promise.all(urls.map((url) => request(url)));
      for (const req of originals) unauthorized(req, { code: 'unauthorized' });
      const replays = await Promise.all(urls.map((url) => request(url)));
      for (const req of replays) {
        expect(authorization(req)).toBe(`Bearer ${fake.next.accessToken}`);
        req.flush({});
      }
      expect((await Promise.all(outcomes)).every((outcome) => outcome.ok)).toBe(true);
      expect(fake.calls).toBe(1);
    });

    it('never refreshes on a SILENT_REQUEST', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/poll', { context: silently() }));
      const req = await request('/api/v1/poll');
      unauthorized(req, { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      expect(store.status()).toBe('authenticated');
      await settle();
      mock.expectNone('/api/v1/poll');
    });

    it('keeps the SILENT flag on the context it reads (silently() sets SILENT_REQUEST)', () => {
      expect(silently().get(SILENT_REQUEST)).toBe(true);
    });
  });

  describe('IdleGated401_KeepsSession', () => {
    it('a background 401 while inactive: one attempt, no refresh, session and scopes untouched', async () => {
      signIn(FRESH_MS);
      active = false;
      const done = run(http.get('/api/v1/poll'));
      const req = await request('/api/v1/poll');
      unauthorized(req, { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      expect(store.status()).toBe('authenticated');
      expect(store.endReason()).toBeNull();
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
      await settle();
      mock.expectNone('/api/v1/poll');
    });
  });

  describe('Background401_WhileIdle_DoesNotRefresh', () => {
    it('a stale token and no USER_ACTION while inactive: no refresh, the request goes out bare, its 401 keeps the session', async () => {
      signIn(STALE_MS);
      active = false;
      const done = run(http.get('/api/v1/poll'));
      const req = await request('/api/v1/poll');
      expect(authorization(req)).toBeNull();
      unauthorized(req, { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      expect(store.endReason()).toBeNull();
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
    });
  });

  describe('UserAction401_WhileIdle_Refreshes', () => {
    it('USER_ACTION refreshes even while inactive and replays once', async () => {
      signIn(FRESH_MS);
      active = false;
      const done = run(http.post('/api/v1/series', {}, { context: userAction(new HttpContext()) }));
      const original = await request('/api/v1/series');
      unauthorized(original, { code: 'unauthorized' });
      const replay = await request('/api/v1/series');
      expect(authorization(replay)).toBe(`Bearer ${fake.next.accessToken}`);
      replay.flush({ id: 1 });
      expect((await done).ok).toBe(true);
      expect(fake.calls).toBe(1);
    });
  });

  describe('401Passthrough_IsNeverReplayed', () => {
    it.each(PASS_THROUGH_CODES)('%s: one attempt, no refresh, the 401 reaches the caller unchanged', async (code) => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      const req = await request('/api/v1/series');
      unauthorized(req, { code });
      const error = httpError(await done);
      expect(error.status).toBe(401);
      expect(mapper.from(error).code).toBe(code);
      expect(fake.calls).toBe(0);
      await settle();
      mock.expectNone('/api/v1/series');
    });

    it.each(['reauth_required', 'invalid_credentials', 'invalid_mfa_code', 'mfa_token_invalid', 'mfa_enrolment_required', 'hub_ticket_invalid'])(
      '%s does not end the session',
      async (code) => {
        signIn(FRESH_MS);
        const done = run(http.get('/api/v1/series'));
        unauthorized(await request('/api/v1/series'), { code });
        await done;
        expect(store.status()).toBe('authenticated');
        expect(store.endReason()).toBeNull();
        expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
      },
    );
  });

  describe('SessionRevoked_EndsSessionWithoutReplay', () => {
    it('signs out with revoked, resets every scope once and returns the response', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'session_revoked' });
      expect(httpError(await done).status).toBe(401);
      expect(store.endReason()).toBe('revoked');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      expect(fake.calls).toBe(0);
      await vi.waitFor(() => expect(router.url).toMatch(/^\/login\?returnUrl=/), { timeout: 2_000, interval: 1 });
    });
  });

  describe('RefreshTokenReused_EndsSessionWithReused', () => {
    it('signs out with reused and returns the response', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'refresh_token_reused' });
      expect(httpError(await done).status).toBe(401);
      expect(store.endReason()).toBe('reused');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      expect(fake.calls).toBe(0);
    });

    it('a refresh that answers refresh_token_reused ends the session with reused and the caller sees a 401', async () => {
      signIn(STALE_MS);
      fake.mode = 'reused';
      const done = run(http.get('/api/v1/series'));
      expect(httpError(await done).status).toBe(401);
      expect(store.endReason()).toBe('reused');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      mock.expectNone('/api/v1/series');
    });
  });

  describe('ReplayAlso401_EndsSession', () => {
    it('two attempts, no third, revoked, scopes reset once, redirect to /login?returnUrl=', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'unauthorized' });
      unauthorized(await request('/api/v1/series'), { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(1);
      expect(store.endReason()).toBe('revoked');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      await vi.waitFor(() => expect(router.url).toMatch(/^\/login\?returnUrl=/), { timeout: 2_000, interval: 1 });
      await settle();
      mock.expectNone('/api/v1/series');
    });

    it('a refresh that answers a plain 401 ends the session with revoked and the caller sees the original 401', async () => {
      signIn(FRESH_MS);
      fake.mode = '401';
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(store.endReason()).toBe('revoked');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
    });
  });

  describe('AuthRoutes_NeverReplayed', () => {
    it.each(AUTH_URLS)('%s: a 401 reaches the screen after one attempt', async (url) => {
      signIn(FRESH_MS);
      const done = run(http.post(url, {}));
      unauthorized(await request(url), { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      expect(store.endReason()).toBeNull();
      await settle();
      mock.expectNone(url);
    });
  });

  describe('Refresh403_DoesNotEndSession', () => {
    it('the caller gets unknown_error, nothing is sent and the session is kept', async () => {
      signIn(STALE_MS);
      fake.mode = 'forbidden';
      const done = run(http.get('/api/v1/series'));
      const outcome = await done;
      expect(outcome.ok).toBe(false);
      expect(!outcome.ok && mapper.from(outcome.error).code).toBe('unknown_error');
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('authenticated');
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
      mock.expectNone('/api/v1/series');
    });

    it('a refresh 403 during a replay does not end the session either', async () => {
      signIn(FRESH_MS);
      fake.mode = 'forbidden';
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'unauthorized' });
      const outcome = await done;
      expect(!outcome.ok && mapper.from(outcome.error).code).toBe('unknown_error');
      expect(store.endReason()).toBeNull();
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
    });
  });

  describe('NetworkRefreshFailure_KeepsSession', () => {
    it('surfaces network_error and keeps the session until the token expires', async () => {
      signIn(STALE_MS);
      fake.mode = 'network';
      const error = httpError(await run(http.get('/api/v1/series')));
      expect(error.status).toBe(0);
      expect(mapper.from(error).code).toBe('network_error');
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('authenticated');
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
      mock.expectNone('/api/v1/series');
    });

    it('a network failure of the refresh during a replay keeps the session too', async () => {
      signIn(FRESH_MS);
      fake.mode = 'network';
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'unauthorized' });
      expect(mapper.from(httpError(await done)).code).toBe('network_error');
      expect(store.endReason()).toBeNull();
    });
  });

  describe('NoLogging', () => {
    it('writes nothing to the console across retry, pass-through and network paths', async () => {
      const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((method) =>
        vi.spyOn(console, method).mockImplementation(() => undefined),
      );
      signIn(FRESH_MS);
      const retried = run(http.get('/api/v1/a'));
      unauthorized(await request('/api/v1/a'), { code: 'unauthorized' });
      (await request('/api/v1/a')).flush({});
      await retried;
      const passed = run(http.get('/api/v1/b'));
      unauthorized(await request('/api/v1/b'), { code: 'invalid_mfa_code' });
      await passed;
      fake.mode = 'network';
      const offline = run(http.get('/api/v1/c'));
      unauthorized(await request('/api/v1/c'), { code: 'unauthorized' });
      await offline;
      for (const spy of spies) expect(spy).not.toHaveBeenCalled();
    });
  });

  describe('QA: exploratory', () => {
    it('retries an unknown-code 401 only when it carries an invalid_token challenge', async () => {
      signIn(FRESH_MS);
      const challenged = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'weird_code' }, { 'WWW-Authenticate': 'Bearer error="invalid_token"' });
      (await request('/api/v1/series')).flush({});
      expect((await challenged).ok).toBe(true);
      expect(fake.calls).toBe(1);

      const otherChallenge = run(http.get('/api/v1/other'));
      unauthorized(await request('/api/v1/other'), { code: 'weird_code' }, { 'WWW-Authenticate': 'Bearer error="insufficient_scope"' });
      expect(httpError(await otherChallenge).status).toBe(401);

      const noChallenge = run(http.get('/api/v1/third'));
      unauthorized(await request('/api/v1/third'), { code: 'weird_code' });
      expect(httpError(await noChallenge).status).toBe(401);
      expect(fake.calls).toBe(1);
      await settle();
      mock.expectNone('/api/v1/other');
      mock.expectNone('/api/v1/third');
    });

    it('a pass-through code wins over an invalid_token challenge', async () => {
      signIn(FRESH_MS);
      const done = run(http.get('/api/v1/series'));
      unauthorized(await request('/api/v1/series'), { code: 'reauth_required' }, { 'WWW-Authenticate': 'Bearer error="invalid_token"' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      await settle();
      mock.expectNone('/api/v1/series');
    });

    it('a 401 on an enrolment-token request neither refreshes nor ends anything', async () => {
      store.beginEnrolment('enrol-token');
      const done = run(http.post('/api/v1/me/mfa/verify', { code: '123456' }));
      const req = await request('/api/v1/me/mfa/verify');
      expect(authorization(req)).toBe('Bearer enrol-token');
      unauthorized(req, { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      expect(store.endReason()).toBeNull();
      expect(store.mode()).toBe('enrolment');
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
    });

    it('treats a protocol-relative host as foreign and a same-origin absolute URL as the API', async () => {
      signIn(FRESH_MS);
      const foreign = run(http.get('//evil.example/api/v1/series'));
      const foreignReq = await request('//evil.example/api/v1/series');
      expect(authorization(foreignReq)).toBeNull();
      foreignReq.flush({});
      await foreign;

      const sameOrigin = `${document.location.origin}/api/v1/series`;
      const own = run(http.get(sameOrigin));
      const ownReq = await request(sameOrigin);
      expect(authorization(ownReq)).toBe(`Bearer ${first.accessToken}`);
      ownReq.flush({});
      await own;
    });

    it('replays the same method and body with the new token', async () => {
      signIn(FRESH_MS);
      const done = run(http.post('/api/v1/series', { title: 'x' }));
      unauthorized(await request('/api/v1/series'), { code: 'unauthorized' });
      const replay = await request('/api/v1/series');
      expect(replay.request.method).toBe('POST');
      expect(replay.request.body).toEqual({ title: 'x' });
      expect(authorization(replay)).toBe(`Bearer ${fake.next.accessToken}`);
      replay.flush({});
      await done;
    });

    it('a request issued while the boot restore is pending waits for it and carries the restored token', async () => {
      fake.mode = 'deferred';
      void store.restore();
      const done = run(http.get('/api/v1/series'));
      await settle();
      mock.expectNone('/api/v1/series');

      fake.resolveNext(fake.next);
      const req = await request('/api/v1/series');
      expect(authorization(req)).toBe(`Bearer ${fake.next.accessToken}`);
      req.flush({});
      await done;
      expect(fake.calls).toBe(1);
    });

    it('signOut while the pre-request refresh is in flight: the request goes out bare, its 401 neither refreshes nor ends twice', async () => {
      signIn(STALE_MS);
      fake.mode = 'deferred';
      const done = run(http.get('/api/v1/series'));
      await vi.waitFor(() => expect(fake.calls).toBe(1), { timeout: 2_000, interval: 1 });

      store.signOut('expired');
      fake.resolveNext(fake.next);
      const req = await request('/api/v1/series');
      expect(authorization(req)).toBeNull();
      unauthorized(req, { code: 'unauthorized' });

      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(1);
      expect(store.endReason()).toBe('expired');
      expect(store.accessToken()).toBeNull();
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      await settle();
      mock.expectNone('/api/v1/series');
    });
  });

  describe('Review fixes: generation binding, logout, failure fidelity', () => {
    const SERIES = '/api/v1/series';
    const LOGOUT = '/api/v1/auth/logout';

    function signInB(): TokenPair {
      const b: TokenPair = { accessToken: fakeJwt('admin', ['B'], 'user-B'), expiresAt: Date.now() + FRESH_MS };
      store.signIn(b, 'admin', ['pwd']);
      return b;
    }

    function expectBUntouched(b: TokenPair): void {
      expect(store.accessToken()).toBe(b.accessToken);
      expect(store.endReason()).toBeNull();
      expect(scopes.map((scope) => scope.resets)).toEqual([0, 0, 0]);
    }

    /** Real repository behind the interceptor: the HTTP-level path of `SessionStore.logout`. */
    function useRealRepository(): void {
      TestBed.resetTestingModule();
      scopes = [new CountingScope()];
      TestBed.configureTestingModule({
        providers: [
          ...ZONELESS_TEST_PROVIDERS,
          provideRouter([
            { path: 'login', children: [] },
            { path: '**', children: [] },
          ]),
          provideHttpClient(withInterceptors([sessionInterceptor])),
          provideHttpClientTesting(),
          { provide: SessionApi, useClass: HttpSessionRepository },
          { provide: ActivityTracker, useValue: { isActive: () => active, isVisible: () => true } },
          ...scopes.map((scope) => ({ provide: SESSION_SCOPED, useValue: scope, multi: true })),
        ],
      });
      store = TestBed.inject(SessionStore);
      http = TestBed.inject(HttpClient);
      mock = TestBed.inject(HttpTestingController);
      router = TestBed.inject(Router);
    }

    it('Interceptor_Logout_ExplicitAuthorizationPassesThroughUntouched_NeverRefreshedOrReplayed', async () => {
      signIn(STALE_MS);
      const done = run(http.post(LOGOUT, null, { headers: { Authorization: 'Bearer explicit' } }));
      const req = await request(LOGOUT);
      expect(authorization(req)).toBe('Bearer explicit');
      unauthorized(req, { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      expect(fake.calls).toBe(0);
      expect(store.endReason()).toBeNull();
      await settle();
      mock.expectNone(LOGOUT);
    });

    it('Logout_SendsBearer_ClearsLocalStateBeforeServerAnswers_WithoutReturnUrl', async () => {
      useRealRepository();
      await router.navigateByUrl('/dashboard');
      signIn(FRESH_MS);
      const loggingOut = store.logout();
      const req = await request(LOGOUT);
      expect(authorization(req)).toBe(`Bearer ${first.accessToken}`);
      // The server has not answered yet.
      expect(store.accessToken()).toBeNull();
      expect(store.endReason()).toBe('logout');
      expect(scopes.map((scope) => scope.resets)).toEqual([1]);
      await vi.waitFor(() => expect(router.url).toBe('/login'), { timeout: 2_000, interval: 1 });
      req.flush(null, { status: 204, statusText: 'No Content' });
      await expect(loggingOut).resolves.toBeUndefined();
    });

    it('Logout_401SessionRevoked_Resolves', async () => {
      useRealRepository();
      signIn(FRESH_MS);
      const loggingOut = store.logout();
      unauthorized(await request(LOGOUT), { code: 'session_revoked' });
      await expect(loggingOut).resolves.toBeUndefined();
      expect(store.endReason()).toBe('logout');
    });

    it('Logout_401Unauthorized_WithToken_Rejects_ButStateStaysCleared', async () => {
      useRealRepository();
      signIn(FRESH_MS);
      const loggingOut = store.logout();
      unauthorized(await request(LOGOUT), { code: 'unauthorized' });
      await expect(loggingOut).rejects.toMatchObject({ code: 'unauthorized', status: 401 });
      expect(store.accessToken()).toBeNull();
      expect(store.endReason()).toBe('logout');
      expect(scopes.map((scope) => scope.resets)).toEqual([1]);
    });

    it('Logout_NullToken_StillPostsWithoutHeader_AndResolves', async () => {
      useRealRepository();
      signIn(FRESH_MS);
      store.signOut('expired');
      const loggingOut = store.logout();
      const req = await request(LOGOUT);
      expect(authorization(req)).toBeNull();
      req.flush(null, { status: 500, statusText: 'x' });
      await expect(loggingOut).resolves.toBeUndefined();
    });

    it.each([['session_revoked'], ['refresh_token_reused'], ['unauthorized']])(
      'a late %s 401 of A after signIn(B): B is not signed out and nothing is replayed',
      async (code) => {
        signIn(FRESH_MS);
        const done = run(http.get(SERIES));
        const req = await request(SERIES);
        const b = signInB();
        unauthorized(req, { code });
        expect(httpError(await done).status).toBe(401);
        expect(fake.calls).toBe(0);
        await settle();
        mock.expectNone(SERIES);
        expectBUntouched(b);
      },
    );

    it('a replay 401 of A after signIn(B) does not sign B out', async () => {
      signIn(FRESH_MS);
      const done = run(http.get(SERIES));
      unauthorized(await request(SERIES), { code: 'unauthorized' });
      const replay = await request(SERIES);
      const b = signInB();
      unauthorized(replay, { code: 'unauthorized' });
      expect(httpError(await done).status).toBe(401);
      await settle();
      mock.expectNone(SERIES);
      expectBUntouched(b);
    });

    it('a deferred refresh for A\'s request resolving after B signs in: the request never carries B\'s token', async () => {
      signIn(STALE_MS);
      fake.mode = 'deferred';
      const done = run(http.post(SERIES, { x: 1 }));
      await vi.waitFor(() => expect(fake.calls).toBe(1), { timeout: 2_000, interval: 1 });
      const b = signInB();
      fake.resolveNext(tokens(['late'], FRESH_MS));
      const req = await request(SERIES);
      expect(authorization(req)).toBeNull();
      req.flush({});
      await done;
      expectBUntouched(b);
    });

    it('a deferred refresh after a 401 of A resolving after B signs in: no replay under B', async () => {
      signIn(FRESH_MS);
      fake.mode = 'deferred';
      const done = run(http.post(SERIES, { x: 1 }));
      unauthorized(await request(SERIES), { code: 'unauthorized' });
      await vi.waitFor(() => expect(fake.calls).toBe(1), { timeout: 2_000, interval: 1 });
      const b = signInB();
      fake.resolveNext(tokens(['late'], FRESH_MS));
      expect(httpError(await done).status).toBe(401);
      await settle();
      mock.expectNone(SERIES);
      expectBUntouched(b);
    });

    it('boot network failure, then a USER_ACTION request whose refresh answers a plain 401: anonymous, no navigation, scopes reset once overall', async () => {
      fake.mode = 'network';
      await store.restore();
      expect(store.endReason()).toBe('restore_failed');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      const navigate = vi.spyOn(router, 'navigate');

      fake.mode = '401';
      const outcome = await run(http.post(SERIES, {}, { context: userAction(new HttpContext()) }));

      expect(httpError(outcome).status).toBe(401);
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('anonymous');
      expect(navigate).not.toHaveBeenCalled();
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1]);
      mock.expectNone(SERIES);
    });

    it('an unreadable refreshed JWT reaches the caller as unknown_error, not network_error', async () => {
      signIn(STALE_MS);
      fake.next = { accessToken: 'garbage', expiresAt: Date.now() + FRESH_MS };
      const outcome = await run(http.get(SERIES));
      expect(outcome.ok).toBe(false);
      if (outcome.ok) return;
      expect(outcome.error).not.toBeInstanceOf(HttpErrorResponse);
      expect(mapper.from(outcome.error).code).toBe('unknown_error');
      mock.expectNone(SERIES);
    });

    it('a refresh 429 keeps code, Retry-After and traceId for the caller mapper', async () => {
      signIn(STALE_MS);
      fake.mode = 'deferred';
      const done = run(http.get(SERIES));
      await vi.waitFor(() => expect(fake.calls).toBe(1), { timeout: 2_000, interval: 1 });
      fake.rejectNext({
        code: 'rate_limited',
        status: 429,
        kind: 'rate_limit',
        fieldErrors: {},
        retryAfterSec: 7,
        traceId: 'trace-7f3',
        problem: { code: 'rate_limited', traceId: 'trace-7f3' },
      });
      const outcome = await done;
      expect(outcome.ok).toBe(false);
      if (outcome.ok) return;
      expect(mapper.from(outcome.error)).toMatchObject({
        code: 'rate_limited',
        status: 429,
        retryAfterSec: 7,
        traceId: 'trace-7f3',
      });
      mock.expectNone(SERIES);
    });
  });
});
