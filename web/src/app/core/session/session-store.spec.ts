import { ApplicationRef, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { AppError } from '@core/errors';
import type { MockInstance } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { ActivityTracker } from './activity';
import type { MeSummary } from './me-summary';
import { SESSION_SCOPED, type SessionScoped } from './session-scope';
import { SessionStore } from './session-store';
import { SessionApi } from './session.api';
import { FakeSessionApi, fakeJwt } from './testing/fake-session.api';
import { FRESHNESS_MARGIN_MS, type PlatformRole, type TokenPair } from './token-pair';

const MINUTE = 60_000;
const DAY = 86_400_000;
const T0 = Date.parse('2026-10-04T10:00:00Z');

class CountingScope implements SessionScoped {
  resets = 0;
  reset(): void {
    this.resets += 1;
  }
}

class ThrowingScope implements SessionScoped {
  resets = 0;
  reset(): void {
    this.resets += 1;
    throw new Error('boom');
  }
}

/** A distinct token per `label`, expiring `ttlMs` after the (possibly faked) current time. */
function pair(role: PlatformRole, ttlMs: number, label = 'x'): TokenPair {
  return { accessToken: fakeJwt(role, [label]), expiresAt: Date.now() + ttlMs };
}

const FRESH = 15 * MINUTE;
const STALE = 5_000;

interface Harness {
  readonly store: SessionStore;
  readonly fake: FakeSessionApi;
  readonly router: Router;
  readonly tracker: ActivityTracker;
}

function setup(scopes: readonly SessionScoped[] = []): Harness {
  const fake = new FakeSessionApi();
  const scopeProviders: Provider[] = scopes.map((scope) => ({ provide: SESSION_SCOPED, multi: true, useValue: scope }));
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'login', children: [] },
        { path: '**', children: [] },
      ]),
      { provide: SessionApi, useValue: fake },
      ...scopeProviders,
    ],
  });
  return {
    store: TestBed.inject(SessionStore),
    fake,
    router: TestBed.inject(Router),
    tracker: TestBed.inject(ActivityTracker),
  };
}

function appError(code: string, status: number): AppError {
  return { code, status, kind: 'auth', fieldErrors: {} };
}

function lockedNavigator(): { release: () => void; hook: (fn: () => void) => void } {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let beforeGrant: () => void = () => undefined;
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request: async (_name: string, _options: unknown, callback: () => Promise<unknown>) => {
        await gate;
        beforeGrant();
        return callback();
      },
    },
  });
  return {
    release,
    hook: (fn) => {
      beforeGrant = fn;
    },
  };
}

describe('SessionStore', () => {
  let warn: MockInstance<typeof console.warn>;

  beforeEach(() => {
    warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    Reflect.deleteProperty(navigator, 'locks');
  });

  describe('restore', () => {
    it('SessionStore_Restore_OnValidCookie_IsAuthenticated', async () => {
      const { store, fake } = setup();
      fake.next = { accessToken: fakeJwt('admin', ['pwd', 'otp']), expiresAt: Date.now() + FRESH };
      expect(store.status()).toBe('anonymous');

      const first = store.restore();
      expect(store.status()).toBe('restoring');
      expect(store.restore()).toBe(first);
      await first;

      expect(fake.calls).toBe(1);
      expect(store.restoreSettled()).toBe(true);
      expect(store.status()).toBe('authenticated');
      expect(store.accessToken()).toBe(fake.next.accessToken);
      expect(store.role()).toBe('admin');
      expect(store.amr()).toEqual(['pwd', 'otp']);
      expect(store.isOwnerOrAdmin()).toBe(true);
      await store.restore();
      expect(fake.calls).toBe(1);
    });

    it('SessionStore_Restore_On401_IsAnonymousWithoutError', async () => {
      const { store, fake, router } = setup();
      fake.mode = '401';
      await expect(store.restore()).resolves.toBeUndefined();
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('anonymous');
      expect(store.restoreSettled()).toBe(true);
      expect(store.accessToken()).toBeNull();
      expect(router.url).toBe('/');
    });

    it.each([
      ['reused', 'reused'],
      ['network', 'restore_failed'],
      ['server', 'restore_failed'],
      ['forbidden', 'restore_failed'],
    ] as const)('SessionStore_Restore_OnFailure_Classified (%s -> %s)', async (mode, reason) => {
      const scope = new CountingScope();
      const { store, fake, router } = setup([scope]);
      fake.mode = mode;
      await expect(store.restore()).resolves.toBeUndefined();
      expect(store.endReason()).toBe(reason);
      expect(store.status()).toBe('ended');
      expect(store.restoreSettled()).toBe(true);
      expect(store.accessToken()).toBeNull();
      expect(scope.resets).toBe(1);
      expect(router.url).toBe('/');
      expect(warn).toHaveBeenCalledTimes(mode === 'forbidden' ? 1 : 0);

      store.signIn(pair('user', FRESH), 'user', []);
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('authenticated');
    });

    it('treats an unreadable access token as anonymous', async () => {
      const { store, fake } = setup();
      fake.next = { accessToken: 'not-a-jwt', expiresAt: Date.now() + FRESH };
      await store.restore();
      expect(store.status()).toBe('anonymous');
      expect(store.accessToken()).toBeNull();
      expect(store.endReason()).toBeNull();
    });

    it('decodes a string amr claim', async () => {
      const { store, fake } = setup();
      fake.next = { accessToken: fakeJwt('owner', ['rc']), expiresAt: Date.now() + FRESH };
      await store.restore();
      expect(store.amr()).toEqual(['rc']);
      expect(store.role()).toBe('owner');
    });
  });

  describe('ensureFreshAccessToken', () => {
    it('SessionStore_EnsureFreshAccessToken_SingleFlight', async () => {
      const { store, fake } = setup();
      store.signIn(pair('user', STALE, 'old'), 'user', ['pwd']);
      fake.mode = 'deferred';

      const callers = Array.from({ length: 5 }, () => store.ensureFreshAccessToken({ userAction: true }));
      await vi.waitFor(() => expect(fake.calls).toBe(1));
      expect(store.status()).toBe('refreshing');
      const next = pair('user', FRESH, 'new');
      fake.resolveNext(next);

      await expect(Promise.all(callers)).resolves.toEqual(Array(5).fill(next.accessToken));
      expect(fake.calls).toBe(1);
      expect(store.accessToken()).toBe(next.accessToken);
      expect(store.status()).toBe('authenticated');
    });

    it('SessionStore_EnsureFreshAccessToken_RefreshesWithinMargin', async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(T0);
      const { store, fake } = setup();
      const next = pair('user', FRESH, 'next');
      fake.next = next;

      store.signIn({ accessToken: fakeJwt('user', ['a']), expiresAt: T0 + FRESHNESS_MARGIN_MS + 1 }, 'user', []);
      await expect(store.ensureFreshAccessToken()).resolves.toBe(store.accessToken());
      expect(fake.calls).toBe(0);

      vi.setSystemTime(T0 + 2);
      await expect(store.ensureFreshAccessToken()).resolves.toBe(next.accessToken);
      expect(fake.calls).toBe(1);

      store.signIn({ accessToken: fakeJwt('user', ['b']), expiresAt: T0 + FRESHNESS_MARGIN_MS - 1 }, 'user', []);
      await store.ensureFreshAccessToken();
      expect(fake.calls).toBe(2);
    });

    it('SessionStore_EnsureFreshAccessToken_RefusesBackgroundWhileIdle', async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(T0);
      const { store, fake } = setup();
      store.signIn(pair('admin', STALE, 'old'), 'admin', ['pwd']);

      // Inside the 25 min admin window a background caller refreshes.
      vi.setSystemTime(T0 + 24 * MINUTE);
      store.signIn(pair('admin', STALE, 'old2'), 'admin', ['pwd']);
      await store.ensureFreshAccessToken();
      expect(fake.calls).toBe(1);

      // Past the window it gets null, no HTTP call, and the session is kept.
      vi.setSystemTime(T0 + 26 * MINUTE);
      store.signIn(pair('admin', STALE, 'old3'), 'admin', ['pwd']);
      await expect(store.ensureFreshAccessToken()).resolves.toBeNull();
      expect(fake.calls).toBe(1);
      expect(store.status()).toBe('authenticated');
      expect(store.endReason()).toBeNull();

      // A user action refreshes even while idle.
      await expect(store.ensureFreshAccessToken({ userAction: true })).resolves.toBe(fake.next.accessToken);
      expect(fake.calls).toBe(2);
    });

    it('uses the 13 day window for the user role', async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(T0);
      const { store, fake } = setup();

      vi.setSystemTime(T0 + 12 * DAY);
      store.signIn(pair('user', STALE, 'a'), 'user', []);
      await store.ensureFreshAccessToken();
      expect(fake.calls).toBe(1);

      vi.setSystemTime(T0 + 14 * DAY);
      store.signIn(pair('user', STALE, 'b'), 'user', []);
      await expect(store.ensureFreshAccessToken()).resolves.toBeNull();
      expect(fake.calls).toBe(1);
    });

    it('returns null without a call when ended, anonymous or in enrolment mode', async () => {
      const { store, fake } = setup();
      fake.mode = '401';
      await store.restore();
      fake.calls = 0;
      await expect(store.ensureFreshAccessToken({ userAction: true })).resolves.toBeNull();

      store.signIn(pair('user', STALE), 'user', []);
      store.signOut('expired');
      await expect(store.ensureFreshAccessToken({ userAction: true })).resolves.toBeNull();

      store.beginEnrolment('enrol-token');
      await expect(store.ensureFreshAccessToken({ userAction: true })).resolves.toBeNull();
      expect(fake.calls).toBe(0);
    });

    it('waits for the boot restore before deciding', async () => {
      const { store, fake } = setup();
      fake.next = pair('user', FRESH, 'boot');
      await expect(store.ensureFreshAccessToken()).resolves.toBe(fake.next.accessToken);
      expect(fake.calls).toBe(1);
    });

    it('refreshes a token the server rejected although it still looks fresh, once', async () => {
      const { store, fake } = setup();
      const old = pair('user', FRESH, 'old');
      store.signIn(old, 'user', []);
      fake.mode = 'deferred';

      const callers = Array.from({ length: 3 }, () => store.ensureFreshAccessToken({ userAction: true, rejectedToken: old.accessToken }));
      await vi.waitFor(() => expect(fake.calls).toBe(1));
      const next = pair('user', FRESH, 'new');
      fake.resolveNext(next);
      await expect(Promise.all(callers)).resolves.toEqual(Array(3).fill(next.accessToken));
      expect(fake.calls).toBe(1);

      // A late 401 for the old token finds the newer one and does not refresh again.
      await expect(store.ensureFreshAccessToken({ userAction: true, rejectedToken: old.accessToken })).resolves.toBe(next.accessToken);
      expect(fake.calls).toBe(1);
    });

    it.each([
      ['reused', 'refresh_token_reused', 'reused'],
      ['revoked', 'session_revoked', 'revoked'],
    ] as const)('SessionStore_Refresh_%s_EndsSession', async (mode, code, reason) => {
      const { store, fake } = setup();
      store.signIn(pair('user', STALE), 'user', []);
      fake.mode = mode;

      await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toMatchObject({ code });
      expect(store.endReason()).toBe(reason);
      expect(store.status()).toBe('ended');
      expect(store.accessToken()).toBeNull();
    });

    it.each(['network', 'forbidden', '401'] as const)('keeps the session when the refresh fails with %s', async (mode) => {
      const { store, fake } = setup();
      const old = pair('user', STALE);
      store.signIn(old, 'user', []);
      fake.mode = mode;

      await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toHaveProperty('code');
      expect(store.endReason()).toBeNull();
      expect(store.accessToken()).toBe(old.accessToken);
      // A failed flight is not remembered: the next call tries again.
      await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toBeDefined();
      expect(fake.calls).toBe(2);
    });

    it('writes one dev-console line for a 403 refresh', async () => {
      const { store, fake } = setup();
      store.signIn(pair('user', STALE), 'user', []);
      fake.mode = 'forbidden';
      await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toBeDefined();
      expect(warn).toHaveBeenCalledTimes(1);
    });

    it('rejects and clears an unreadable refreshed token without ending the session', async () => {
      const { store, fake } = setup();
      store.signIn(pair('user', STALE), 'user', []);
      fake.next = { accessToken: 'garbage', expiresAt: Date.now() + FRESH };
      await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toMatchObject({ code: 'unknown_error' });
      expect(store.accessToken()).toBeNull();
      expect(store.endReason()).toBeNull();
    });
  });

  describe('refresh lock', () => {
    // Under the lock the refresh is serialised, and a generation change while waiting commits nothing and issues no
    // call. The freshness branch itself covers same-tab only (cross-tab cannot share the in-memory token).
    it('SessionStore_Refresh_UnderLock_IsSerialisedAndStaleGenerationCommitsNothing', async () => {
      const locks = lockedNavigator();
      const { store, fake } = setup();
      store.signIn(pair('user', STALE, 'old'), 'user', []);
      const rotated = pair('user', FRESH, 'signed-in-while-waiting');
      // A new session replaced this one while the flight waited for the lock.
      locks.hook(() => {
        store.signIn(rotated, 'user', []);
      });

      const pending = store.ensureFreshAccessToken({ userAction: true });
      locks.release();

      await expect(pending).resolves.toBe(rotated.accessToken);
      expect(fake.calls).toBe(0);
    });

    it('SessionStore_Refresh_FallsBackWithoutWebLocks', async () => {
      const { store, fake } = setup();
      expect('locks' in navigator).toBe(false);
      store.signIn(pair('user', STALE), 'user', []);
      fake.mode = 'deferred';

      const both = [store.ensureFreshAccessToken({ userAction: true }), store.ensureFreshAccessToken({ userAction: true })];
      await vi.waitFor(() => expect(fake.calls).toBe(1));
      const next = pair('user', FRESH, 'n');
      fake.resolveNext(next);

      await expect(Promise.all(both)).resolves.toEqual([next.accessToken, next.accessToken]);
      expect(fake.calls).toBe(1);
    });
  });

  describe('stale work', () => {
    it('SessionStore_StaleFlight_DoesNotOverwriteNewSession', async () => {
      const { store, fake } = setup();
      store.signIn(pair('user', STALE, 'A'), 'user', []);
      fake.mode = 'deferred';
      const flightA = store.ensureFreshAccessToken({ userAction: true });
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      // Sign-in B replaces the session while A's refresh is pending.
      const b = pair('admin', STALE, 'B');
      store.signIn(b, 'admin', ['pwd']);
      const flightB = store.ensureFreshAccessToken({ userAction: true });

      // A's late success commits nothing and does not hand its token out.
      fake.resolveNext(pair('user', FRESH, 'A-late'));
      await expect(flightA).resolves.toBeNull();
      expect(store.accessToken()).toBe(b.accessToken);
      expect(store.role()).toBe('admin');

      // B's flight (started behind A on the lock) runs on its own; A's finally did not clear it.
      await vi.waitFor(() => expect(fake.calls).toBe(2));
      const joined = store.ensureFreshAccessToken({ userAction: true });
      expect(fake.calls).toBe(2);
      const b2 = pair('admin', FRESH, 'B2');
      fake.resolveNext(b2);
      await expect(Promise.all([flightB, joined])).resolves.toEqual([b2.accessToken, b2.accessToken]);
      expect(store.accessToken()).toBe(b2.accessToken);
      expect(store.role()).toBe('admin');
    });

    it('a stale failure cannot sign out the new session', async () => {
      const { store, fake, router } = setup();
      store.signIn(pair('user', STALE, 'A'), 'user', []);
      fake.mode = 'deferred';
      const flightA = store.ensureFreshAccessToken({ userAction: true });
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      const b = pair('user', FRESH, 'B');
      store.signIn(b, 'user', []);
      fake.rejectNext(appError('refresh_token_reused', 401));

      await expect(flightA).resolves.toBe(b.accessToken);
      expect(store.endReason()).toBeNull();
      expect(store.accessToken()).toBe(b.accessToken);
      expect(router.url).toBe('/');
    });

    it('a restore finishing after sign-in B changes nothing but still settles', async () => {
      const { store, fake, router } = setup();
      fake.mode = 'deferred';
      const restoring = store.restore();
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      const b = pair('owner', FRESH, 'B');
      store.signIn(b, 'owner', ['pwd']);
      store.setMe({ uiLocale: 'en', role: 'owner', features: ['source_finder'], displayName: 'B', email: 'b@example.test' });
      fake.resolveNext(pair('user', FRESH, 'A-late'));
      await restoring;

      expect(store.restoreSettled()).toBe(true);
      expect(store.accessToken()).toBe(b.accessToken);
      expect(store.role()).toBe('owner');
      expect(store.me()?.displayName).toBe('B');
      expect(router.url).toBe('/');
    });

    it('a restore failing after sign-in B does not end B', async () => {
      const { store, fake } = setup();
      fake.mode = 'deferred';
      const restoring = store.restore();
      await vi.waitFor(() => expect(fake.calls).toBe(1));
      store.signIn(pair('user', FRESH, 'B'), 'user', []);
      fake.rejectNext(appError('session_revoked', 401));
      await restoring;
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('authenticated');
    });

    it('a refresh finishing after sign-out does not resurrect the session', async () => {
      const { store, fake } = setup();
      store.signIn(pair('user', STALE, 'A'), 'user', []);
      fake.mode = 'deferred';
      const flightA = store.ensureFreshAccessToken({ userAction: true });
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      store.signOut('revoked');
      fake.resolveNext(pair('user', FRESH, 'A-late'));
      await expect(flightA).resolves.toBeNull();
      expect(store.accessToken()).toBeNull();
      expect(store.status()).toBe('ended');
    });
  });

  describe('sign-out', () => {
    it('SessionStore_SignOut_ResetsSessionScopedStores', () => {
      const scopes = [new CountingScope(), new ThrowingScope(), new CountingScope(), new CountingScope()];
      const { store } = setup(scopes);
      store.signIn(pair('user', FRESH), 'user', []);

      store.signOut('revoked');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1, 1]);
      expect(store.endReason()).toBe('revoked');
      expect(store.status()).toBe('ended');

      store.signOut('revoked');
      expect(scopes.map((scope) => scope.resets)).toEqual([1, 1, 1, 1]);
    });

    it('SessionStore_SignOut_IsIdempotent', async () => {
      const { store, router } = setup();
      await router.navigateByUrl('/episodes/1?tab=story');
      store.signIn(pair('user', FRESH), 'user', []);
      const navigate = vi.spyOn(router, 'navigate');

      store.signOut('revoked');
      store.signOut('expired');
      await TestBed.inject(ApplicationRef).whenStable();

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(store.endReason()).toBe('revoked');
      expect(router.url).toBe('/login?returnUrl=%2Fepisodes%2F1%3Ftab%3Dstory');
    });

    it('does not navigate again when already on /login', async () => {
      const { store, router } = setup();
      await router.navigateByUrl('/login?returnUrl=%2Fdashboard');
      const navigate = vi.spyOn(router, 'navigate');
      store.signIn(pair('user', FRESH), 'user', []);
      store.signOut('expired');
      expect(navigate).not.toHaveBeenCalled();
      expect(store.endReason()).toBe('expired');
    });

    it('SessionStore_AsyncSignOut_ResolvesRouter', async () => {
      const { store, router } = setup();
      await router.navigateByUrl('/dashboard');
      store.signIn(pair('user', FRESH), 'user', []);

      // Outside any injection context, from an async callback (an HTTP error handler would be one).
      await new Promise<void>((resolve) => setTimeout(resolve, 0));
      expect(() => {
        store.signOut('revoked');
      }).not.toThrow();
      await TestBed.inject(ApplicationRef).whenStable();

      expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fdashboard');
    });

    it('SessionStore_SetMe_DrivesHasFeature_AndClearsOnSignOut', () => {
      const { store } = setup();
      store.signIn(pair('user', FRESH), 'user', []);
      const me: MeSummary = { uiLocale: 'vi', role: 'user', features: ['source_finder'], displayName: 'An', email: 'an@example.test' };

      expect(store.hasFeature('source_finder')).toBe(false);
      store.setMe(me);
      expect(store.me()).toEqual(me);
      expect(store.hasFeature('source_finder')).toBe(true);
      expect(store.hasFeature('youtube_publish')).toBe(false);

      store.signOut('expired');
      expect(store.me()).toBeNull();
      expect(store.hasFeature('source_finder')).toBe(false);
    });

    it('reset() clears identity and the end reason without navigating', async () => {
      const { store, router } = setup();
      store.signIn(pair('user', FRESH), 'user', []);
      store.setMe({ uiLocale: 'vi', role: 'user', features: [], displayName: 'An', email: 'an@example.test' });
      const navigate = vi.spyOn(router, 'navigate');
      const before = store.generation();

      store.reset();
      expect(store.accessToken()).toBeNull();
      expect(store.me()).toBeNull();
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('anonymous');
      expect(store.generation()).toBeGreaterThan(before);
      expect(navigate).not.toHaveBeenCalled();
      await expect(store.ensureFreshAccessToken({ userAction: true })).resolves.toBeNull();
    });
  });

  describe('logout', () => {
    it('SessionStore_Logout_EndsWithoutReturnUrl', async () => {
      const scope = new CountingScope();
      const { store, fake, router } = setup([scope]);
      await router.navigateByUrl('/dashboard');
      store.signIn(pair('user', FRESH), 'user', []);
      store.setMe({ uiLocale: 'vi', role: 'user', features: [], displayName: 'An', email: 'an@example.test' });

      await store.logout();
      await TestBed.inject(ApplicationRef).whenStable();

      expect(fake.logoutCalls).toBe(1);
      expect(router.url).toBe('/login');
      expect(store.accessToken()).toBeNull();
      expect(store.enrolmentToken()).toBeNull();
      expect(store.me()).toBeNull();
      expect(store.endReason()).toBe('logout');
      expect(scope.resets).toBe(1);
    });

    it('still ends the session locally when the logout call fails, and surfaces the failure', async () => {
      const { store, fake } = setup();
      store.signIn(pair('user', FRESH), 'user', []);
      fake.logoutFailure = appError('network_error', 0);

      await expect(store.logout()).rejects.toMatchObject({ code: 'network_error' });
      expect(store.accessToken()).toBeNull();
      expect(store.endReason()).toBe('logout');
    });
  });

  describe('enrolment mode', () => {
    it('SessionStore_BeginEnrolment_IsNeverAuthenticated', () => {
      const { store } = setup();
      store.signIn(pair('user', FRESH, 'full'), 'user', []);
      store.beginEnrolment('enrol-token');

      expect(store.mode()).toBe('enrolment');
      expect(store.status()).not.toBe('authenticated');
      expect(store.isAuthenticated()).toBe(false);
      expect(store.authHeader()).toBe('enrol-token');
      expect(store.accessToken()).toBeNull();
    });

    it('signIn clears the enrolment token; the enrolment token wins the header while it exists', () => {
      const { store } = setup();
      store.beginEnrolment('enrol-token');
      const tokens = pair('admin', FRESH);
      store.signIn(tokens, 'admin', ['pwd', 'otp']);

      expect(store.enrolmentToken()).toBeNull();
      expect(store.mode()).toBe('full');
      expect(store.status()).toBe('authenticated');
      expect(store.authHeader()).toBe(tokens.accessToken);
    });

    it('signOut clears the enrolment token', () => {
      const { store } = setup();
      store.beginEnrolment('enrol-token');
      store.signOut('expired');
      expect(store.enrolmentToken()).toBeNull();
      expect(store.authHeader()).toBeNull();
    });
  });

  describe('persistence', () => {
    it('SessionStore_NeverPersistsToken', async () => {
      const setItem = vi.spyOn(Storage.prototype, 'setItem');
      const cookieWrite = vi.spyOn(Document.prototype, 'cookie', 'set');
      const { store, fake } = setup();

      await store.restore();
      store.signIn(pair('admin', STALE), 'admin', ['pwd']);
      await store.ensureFreshAccessToken({ userAction: true });
      store.beginEnrolment('enrol-token');
      store.signIn(pair('user', FRESH), 'user', []);
      await store.logout();
      store.reset();

      expect(fake.calls).toBeGreaterThan(0);
      expect(setItem).not.toHaveBeenCalled();
      expect(cookieWrite).not.toHaveBeenCalled();
    });
  });

  describe('QA: boundaries, races and backend contract', () => {
    it('treats a token with exactly the freshness margin left as stale (task rule: only `> margin` is returned as is)', async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(T0);
      const { store, fake } = setup();
      store.signIn({ accessToken: fakeJwt('user', ['edge']), expiresAt: T0 + FRESHNESS_MARGIN_MS }, 'user', []);
      await store.ensureFreshAccessToken();
      expect(fake.calls).toBe(1);
    });

    it('a rejectedToken that is not the current token does not force a refresh', async () => {
      const { store, fake } = setup();
      const current = pair('user', FRESH, 'current');
      store.signIn(current, 'user', []);
      await expect(store.ensureFreshAccessToken({ userAction: true, rejectedToken: 'some-older-token' })).resolves.toBe(
        current.accessToken,
      );
      expect(fake.calls).toBe(0);
    });

    it('a refresh resolving after logout() commits nothing and the flight settles null', async () => {
      const scope = new CountingScope();
      const { store, fake } = setup([scope]);
      store.signIn(pair('user', STALE, 'A'), 'user', []);
      fake.mode = 'deferred';
      const flight = store.ensureFreshAccessToken({ userAction: true });
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      await store.logout();
      fake.resolveNext(pair('user', FRESH, 'late'));

      await expect(flight).resolves.toBeNull();
      expect(store.accessToken()).toBeNull();
      expect(store.status()).toBe('ended');
      expect(store.endReason()).toBe('logout');
      expect(scope.resets).toBe(1);
    });

    it('signOut during the boot restore keeps its end reason, resets once and still settles the restore', async () => {
      const scope = new CountingScope();
      const { store, fake } = setup([scope]);
      fake.mode = 'deferred';
      const restoring = store.restore();
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      store.signOut('expired');
      fake.resolveNext(pair('user', FRESH, 'late'));
      await restoring;

      expect(store.restoreSettled()).toBe(true);
      expect(store.accessToken()).toBeNull();
      expect(store.endReason()).toBe('expired');
      expect(store.status()).toBe('ended');
      expect(scope.resets).toBe(1);
    });

    it('a late refresh_token_reused for a restore that signOut already replaced does not overwrite the end reason', async () => {
      const scope = new CountingScope();
      const { store, fake } = setup([scope]);
      fake.mode = 'deferred';
      const restoring = store.restore();
      await vi.waitFor(() => expect(fake.calls).toBe(1));

      store.signOut('expired');
      fake.rejectNext(appError('refresh_token_reused', 401));
      await restoring;

      expect(store.endReason()).toBe('expired');
      expect(scope.resets).toBe(1);
    });

    it('a logout() whose call is still pending does not end a session that signed in meanwhile', async () => {
      class SlowLogoutApi extends FakeSessionApi {
        release!: () => void;
        override logout(): Promise<void> {
          this.logoutCalls += 1;
          return new Promise<void>((resolve) => {
            this.release = resolve;
          });
        }
      }
      const api = new SlowLogoutApi();
      TestBed.configureTestingModule({
        providers: [
          ...ZONELESS_TEST_PROVIDERS,
          provideRouter([{ path: '**', children: [] }]),
          { provide: SessionApi, useValue: api },
        ],
      });
      const store = TestBed.inject(SessionStore);
      store.signIn(pair('user', FRESH, 'A'), 'user', []);
      const loggingOut = store.logout();
      const b = pair('admin', FRESH, 'B');
      store.signIn(b, 'admin', ['pwd']);
      api.release();
      await loggingOut;

      expect(store.accessToken()).toBe(b.accessToken);
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('authenticated');
    });

    // The real backend answers a refresh WITHOUT any cookie with 401 `session_revoked`
    // (AuthEndpoints.RefreshAsync, 09 README WT21, RefreshTokenTests.Refresh_WithoutCookie_ReturnsSessionRevoked), so
    // every first-time anonymous visitor boots through this exact branch: anonymous, no banner, no scope reset.
    it('SessionStore_Restore_OnSessionRevoked401_IsAnonymous (no cookie, WT21)', async () => {
      const scope = new CountingScope();
      const { store, fake, router } = setup([scope]);
      fake.mode = 'revoked';
      await store.restore();
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('anonymous');
      expect(scope.resets).toBe(0);
      expect(router.url).toBe('/');
    });

    it('SessionStore_Restore_OnUnknown401Code_IsAnonymous', async () => {
      const scope = new CountingScope();
      const { store, fake } = setup([scope]);
      fake.mode = 'deferred';
      const restoring = store.restore();
      await vi.waitFor(() => expect(fake.calls).toBe(1));
      fake.rejectNext(appError('something_new', 401));
      await restoring;
      expect(store.endReason()).toBeNull();
      expect(store.status()).toBe('anonymous');
      expect(scope.resets).toBe(0);
    });

    it('SessionStore_Restore_On403EmailNotVerified_IsRestoreFailed', async () => {
      const scope = new CountingScope();
      const { store, fake, router } = setup([scope]);
      fake.mode = 'deferred';
      const restoring = store.restore();
      await vi.waitFor(() => expect(fake.calls).toBe(1));
      fake.rejectNext(appError('email_not_verified', 403));
      await restoring;
      expect(store.endReason()).toBe('restore_failed');
      expect(store.status()).toBe('ended');
      expect(scope.resets).toBe(1);
      expect(router.url).toBe('/');
    });

    describe('restore_failed retry', () => {
      async function failedBoot(scope?: CountingScope): Promise<Harness> {
        const harness = setup(scope === undefined ? [] : [scope]);
        harness.fake.mode = 'network';
        await harness.store.restore();
        expect(harness.store.endReason()).toBe('restore_failed');
        harness.fake.calls = 0;
        return harness;
      }

      it('a user action refreshes again; success commits the session and clears the end reason', async () => {
        const { store, fake } = await failedBoot();
        fake.mode = 'ok';
        fake.next = pair('admin', FRESH, 'retry');

        await expect(store.ensureFreshAccessToken({ userAction: true })).resolves.toBe(fake.next.accessToken);
        expect(fake.calls).toBe(1);
        expect(store.endReason()).toBeNull();
        expect(store.status()).toBe('authenticated');
        expect(store.role()).toBe('admin');
      });

      it('a background call returns null without any call and keeps restore_failed', async () => {
        const { store, fake } = await failedBoot();
        fake.mode = 'ok';
        await expect(store.ensureFreshAccessToken()).resolves.toBeNull();
        expect(fake.calls).toBe(0);
        expect(store.endReason()).toBe('restore_failed');
      });

      it('a user action whose retry is answered by session_revoked settles anonymous without a banner or navigation', async () => {
        const scope = new CountingScope();
        const { store, fake, router } = await failedBoot(scope);
        expect(scope.resets).toBe(1);
        fake.mode = 'revoked';

        await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toMatchObject({ code: 'session_revoked' });
        expect(fake.calls).toBe(1);
        expect(store.endReason()).toBeNull();
        expect(store.status()).toBe('anonymous');
        expect(scope.resets).toBe(1);
        expect(router.url).toBe('/');
      });

      it('a user action whose retry fails again transiently stays restore_failed and resets the scopes once', async () => {
        const scope = new CountingScope();
        const { store, fake } = await failedBoot(scope);
        fake.mode = 'server';
        await expect(store.ensureFreshAccessToken({ userAction: true })).rejects.toMatchObject({ status: 503 });
        expect(store.endReason()).toBe('restore_failed');
        expect(scope.resets).toBe(1);
      });
    });
  });
});
