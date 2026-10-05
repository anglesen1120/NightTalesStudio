import { AUTH_SPEC_USE } from './fixtures/auth';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import type { Page, Route } from '@playwright/test';

// 10 CR-47 (from 10.2.1-T1 §Acceptance/§Tests) + D16 a11y of the session-end surface.
// Backend dependence (orchestrator decision): EVERYTHING here is client behaviour and stubs `/api/v1/auth/*` with
// `page.route` (fixtures/auth.ts allows only the UI and `page.route`/`page.clock`). The stubbed access token is an
// UNSIGNED dummy JWT carrying `sub`/`role`/`amr`/`exp` (the SPA reads the middle segment only; the server is the
// authority). No real-stack block: the authenticated flow through the real backend is 10.3.3-T3's auth-mfa.spec.ts.
// Endpoints the app calls (core/session, core/realtime): POST /auth/login, POST /auth/refresh, POST /auth/logout,
// POST /hubs/ticket (background call fired after every sign-in; used here as the "guarded API call" that the server
// answers 401). The app never requests GET /me before the shell (10.2.6-T1) exists, so it is not stubbed.
// No dashboard page exists until 10.4.1-T1 (S13's 10.2.6-T1 adds the not-found `**`), so post-login assertions read
// the URL PATH only. Limit: a burst of several concurrent callers needs a page that issues API calls (10.4.1-T1 /
// 10.5.2-T1); the single-flight lock itself is unit-tested (refresh-lock.spec.ts, session-store.spec.ts).

test.use(AUTH_SPEC_USE);

const LABEL = {
  email: /^E-mail$/i,
  password: /^(Mật khẩu|Password)$/,
  signIn: /^(Đăng nhập|Sign in)$/,
} as const;

// `core.session.*` copy, vi + en (assets/i18n/core/{vi,en}.json).
const BANNER = {
  revoked: /Phiên đã bị thu hồi|Your session was revoked/,
  expired: /Phiên đã hết hạn|Your session expired/,
  signedOut: /Bạn đã đăng xuất|You have signed out/,
  restoreFailed: /Không thể khôi phục phiên làm việc|Your session could not be restored/,
} as const;

const LOCALES = [
  { tag: 'vi-VN', lang: 'vi' },
  { tag: 'en-GB', lang: 'en' },
] as const;

const LOGIN = '**/api/v1/auth/login';
const REFRESH = '**/api/v1/auth/refresh';
const TICKET = '**/api/v1/hubs/ticket';
const LOGOUT = '**/api/v1/auth/logout';

function b64url(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString('base64url');
}

/** Unsigned dummy JWT; `lifetimeSec` < 0 yields an already-expired token. */
function jwt(lifetimeSec: number, sub = 'u-owner'): string {
  const exp = Math.floor(Date.now() / 1000) + lifetimeSec;
  return `${b64url({ alg: 'none', typ: 'JWT' })}.${b64url({ sub, role: 'owner', amr: ['pwd', 'mfa'], exp })}.`;
}

function tokenBody(lifetimeSec = 900): Record<string, unknown> {
  return { accessToken: jwt(lifetimeSec), expiresAt: new Date(Date.now() + lifetimeSec * 1000).toISOString() };
}

function loginBody(lifetimeSec = 900): Record<string, unknown> {
  return { ...tokenBody(lifetimeSec), role: 'owner', amr: ['pwd', 'mfa'] };
}

async function fulfilJson(route: Route, status: number, body: unknown): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

const REVOKED_PROBLEM = { code: 'session_revoked', status: 401, title: 'session_revoked', traceId: 'NTS-E2E' };
const TICKET_BODY = { ticket: 'e2e-ticket-0123456789abcdef', expiresAt: new Date(Date.now() + 60_000).toISOString() };

function countPosts(page: Page, pathname: string): { readonly count: () => number } {
  let seen = 0;
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === pathname) seen += 1;
  });
  return { count: () => seen };
}

async function signIn(page: Page, returnUrl?: string): Promise<void> {
  await page.goto(returnUrl === undefined ? '/login' : `/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  await page.getByLabel(LABEL.email).fill('owner@example.test');
  await page.getByLabel(LABEL.password).fill('Correct-horse-1');
  await page.getByRole('button', { name: LABEL.signIn }).click();
  await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
}

for (const { tag, lang } of LOCALES) {
  test.describe(`session (${lang}), stubbed API`, () => {
    test.use({ locale: tag });

    test(`a reload restores the session: one boot refresh, and /login bounces an authenticated user to /dashboard [${lang}]`, async ({ page }) => {
      // The refresh cookie exists only after the stubbed sign-in; before it the boot refresh must settle anonymous.
      let signedIn = false;
      await page.route(LOGIN, (route) => {
        signedIn = true;
        return fulfilJson(route, 200, loginBody());
      });
      await page.route(REFRESH, (route) => (signedIn ? fulfilJson(route, 200, tokenBody()) : fulfilJson(route, 401, REVOKED_PROBLEM)));
      await page.route(TICKET, (route) => fulfilJson(route, 200, TICKET_BODY));
      await signIn(page);

      const refreshes = countPosts(page, '/api/v1/auth/refresh');
      await page.reload();
      await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
      await expect.poll(() => refreshes.count(), { message: 'the boot refresh after the reload' }).toBe(1);
      await page.waitForTimeout(500);
      expect(refreshes.count(), 'exactly one boot refresh after the reload, no loop').toBe(1);

      await page.goto('/login');
      await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
      expect(refreshes.count(), 'the second boot restores once more, never in a loop').toBe(2);
    });

    test(`no usable refresh cookie at boot settles anonymous: /login stays, no banner, no loop [${lang}]`, async ({ page }) => {
      const refreshes = countPosts(page, '/api/v1/auth/refresh');
      await page.route(REFRESH, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.goto('/login');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await page.waitForTimeout(1_000);
      expect(new URL(page.url()).pathname).toBe('/login');
      expect(refreshes.count()).toBe(1);
      await expect(page.getByText(BANNER.revoked)).toHaveCount(0);
      await expect(page.getByText(BANNER.expired)).toHaveCount(0);
      await expect(page.getByText(BANNER.restoreFailed)).toHaveCount(0);
    });

    test(`an expired access token is refreshed once and the request is replayed once [${lang}]`, async ({ page }) => {
      // The sign-in hands over an already-expired token: the background hub-ticket call must trigger ONE refresh.
      let tickets = 0;
      let signedIn = false;
      await page.route(LOGIN, (route) => {
        signedIn = true;
        return fulfilJson(route, 200, loginBody(-60));
      });
      let refreshes = 0;
      await page.route(REFRESH, (route) => {
        if (!signedIn) return fulfilJson(route, 401, REVOKED_PROBLEM);
        refreshes += 1;
        return fulfilJson(route, 200, tokenBody());
      });
      await page.route(TICKET, (route) => {
        tickets += 1;
        return route.request().headers()['authorization'] === undefined
          ? fulfilJson(route, 401, { code: 'unauthorized', status: 401, title: 'unauthorized', traceId: 'NTS-E2E' })
          : fulfilJson(route, 200, TICKET_BODY);
      });
      await signIn(page);
      await expect.poll(() => tickets, { timeout: 10_000 }).toBeGreaterThanOrEqual(1);
      await page.waitForTimeout(1_000);
      expect(refreshes, 'one refresh for the burst').toBe(1);
      expect(tickets, 'at most one replay').toBeLessThanOrEqual(2);
      expect(new URL(page.url()).pathname, 'the session survived').toBe('/dashboard');
    });

    test(`server revoke: /login?returnUrl=… with the banner in an aria-live=polite region and focus on the heading [${lang}]`, async ({ page }) => {
      await page.route(LOGIN, (route) => fulfilJson(route, 200, loginBody()));
      await page.route(REFRESH, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.route(TICKET, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await signIn(page);

      await expect(page).toHaveURL((url) => url.pathname === '/login');
      expect(new URL(page.url()).searchParams.get('returnUrl')).toBe('/dashboard');

      const banner = page.locator('[aria-live="polite"]').filter({ hasText: BANNER.revoked });
      await expect(banner).toBeVisible();
      await expect(page.getByRole('heading', { level: 1 })).toBeFocused();
      await page.keyboard.press('Tab');
      const activeTag = await page.evaluate(() => document.activeElement?.tagName ?? 'NONE');
      expect(['BODY', 'HTML', 'NONE'], 'focus is not lost to a removed subtree').not.toContain(activeTag);
      await expectA11yClean(page);
    });

    test(`after a revoke the old session cannot sign a new user out and the next sign-in works [${lang}]`, async ({ page }) => {
      let phase: 'revoke' | 'healthy' = 'revoke';
      await page.route(LOGIN, (route) => fulfilJson(route, 200, loginBody()));
      await page.route(REFRESH, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.route(TICKET, (route) => (phase === 'revoke' ? fulfilJson(route, 401, REVOKED_PROBLEM) : fulfilJson(route, 200, TICKET_BODY)));
      await signIn(page);
      await expect(page).toHaveURL((url) => url.pathname === '/login');
      phase = 'healthy';
      await page.getByLabel(LABEL.email).fill('owner@example.test');
      await page.getByLabel(LABEL.password).fill('Correct-horse-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
      await page.waitForTimeout(1_000);
      expect(new URL(page.url()).pathname).toBe('/dashboard');
    });

    test(`a refresh answering 403 at boot ends the restore in place with the restore-failed banner [${lang}]`, async ({ page }) => {
      await page.route(REFRESH, (route) => fulfilJson(route, 403, { code: 'forbidden', status: 403, title: 'forbidden', traceId: 'NTS-E2E' }));
      await page.goto('/login');
      await expect(page.locator('[aria-live="polite"]').filter({ hasText: BANNER.restoreFailed })).toBeVisible();
      expect(new URL(page.url()).pathname).toBe('/login');
      await expectA11yClean(page);
    });

    test(`returnUrl=//evil.example is rejected: the sign-in lands on /dashboard on this origin [${lang}]`, async ({ page }) => {
      await page.route(LOGIN, (route) => fulfilJson(route, 200, loginBody()));
      await page.route(REFRESH, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.route(TICKET, (route) => fulfilJson(route, 200, TICKET_BODY));
      const login = countPosts(page, '/api/v1/auth/login');
      await page.goto(`/login?returnUrl=${encodeURIComponent('//evil.example')}`);
      expect(login.count(), 'no login attempt before the user submits').toBe(0);
      await page.getByLabel(LABEL.email).fill('owner@example.test');
      await page.getByLabel(LABEL.password).fill('Correct-horse-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
      expect(new URL(page.url()).hostname).toBe(new URL(test.info().project.use.baseURL ?? 'http://localhost:4200').hostname);
    });

    test(`a safe in-app returnUrl is kept in the query until sign-in and is not rewritten by a failed attempt [${lang}]`, async ({ page }) => {
      await page.route(LOGIN, (route) => fulfilJson(route, 401, { code: 'invalid_credentials', status: 401, title: 'invalid_credentials', traceId: 'NTS-E2E' }));
      await page.route(REFRESH, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.goto('/login?returnUrl=%2Fseries');
      await page.getByLabel(LABEL.email).fill('owner@example.test');
      await page.getByLabel(LABEL.password).fill('wrong-password-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page.getByRole('alert')).toBeVisible();
      expect(new URL(page.url()).searchParams.get('returnUrl')).toBe('/series');
    });

    test(`axe, 360 px without horizontal scroll and no CSP violation on /login with each session-end banner [${lang}]`, async ({ page }) => {
      await page.setViewportSize({ width: 360, height: 740 });
      await page.route(LOGIN, (route) => fulfilJson(route, 200, loginBody()));
      await page.route(REFRESH, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.route(TICKET, (route) => fulfilJson(route, 401, REVOKED_PROBLEM));
      await page.route(LOGOUT, (route) => route.fulfill({ status: 204, body: '' }));

      await page.goto('/login');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expectA11yClean(page);

      await signIn(page);
      const banner = page.locator('[aria-live="polite"]').filter({ hasText: BANNER.revoked });
      await expect(banner).toBeVisible();
      await expect(banner).toBeInViewport();
      await expectA11yClean(page);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, 'scrollWidth - clientWidth with the banner shown').toBeLessThanOrEqual(0);
    });
  });
}
