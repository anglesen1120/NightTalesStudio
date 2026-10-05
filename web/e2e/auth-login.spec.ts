import { AUTH_SPEC_USE } from './fixtures/auth';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import type { Page, Route } from '@playwright/test';

// 10.3.3-T1 (README D6/D9/D15/D16, plan.md §8 item 1). Backend dependence (orchestrator decision):
//  - CLIENT-BEHAVIOUR tests stub `/api/v1/auth/*` with `page.route` (allowed by fixtures/auth.ts: only the UI and
//    `page.route`/`page.clock`). They need no backend, no seeded account and no TOTP.
//  - REAL-STACK tests live in `describe('real stack')` and are skipped unless `NTS_E2E_STACK=1`
//    (compose/native stack, run by 11.5.1-T1's e2e.yml).
//  - The authenticated happy path through the real backend is 10.3.3-T3's `auth-mfa.spec.ts` (P seeds one MFA owner
//    and no non-MFA account, plan.md §10 [OPEN]).
// The post-login target is `/dashboard` (README D6). No dashboard page exists until 10.4.1-T1 and the not-found `**`
// route arrives with 10.2.6-T1 (S13), so the stubbed-success assertions read the URL PATH only and need the merged
// tree (a navigation to an unknown path is rejected by the router until then).

test.use(AUTH_SPEC_USE);

const LABEL = {
  email: /^E-mail$/i,
  password: /^(Mật khẩu|Password)$/,
  signIn: /^(Đăng nhập|Sign in)$/,
  signingIn: /^(Đang đăng nhập…|Signing in…)$/,
  forgot: /^(Quên mật khẩu\?|Forgot your password\?)$/,
} as const;

const INVALID_CREDENTIALS = /E-mail hoặc mật khẩu không đúng|That e-mail or password is not right/;
const NETWORK_ERROR = /Không kết nối được tới máy chủ|No connection to the server/;
const COUNTDOWN = /(\d+)\s*(giây|seconds?)/i;

const LOCALES = [
  { tag: 'vi-VN', lang: 'vi' },
  { tag: 'en-GB', lang: 'en' },
] as const;

const LOGIN_URL = '**/api/v1/auth/login';

function b64url(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString('base64url');
}

/** Unsigned dummy JWT: the SPA reads `role`/`amr`/`sub` from the middle segment only. */
function dummyJwt(): string {
  const exp = Math.floor(Date.now() / 1000) + 900;
  return `${b64url({ alg: 'none', typ: 'JWT' })}.${b64url({ sub: 'u-owner', role: 'owner', amr: ['pwd', 'mfa'], exp })}.`;
}

function loginOkBody(): Record<string, unknown> {
  return {
    accessToken: dummyJwt(),
    expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
    role: 'owner',
    amr: ['pwd', 'mfa'],
  };
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, status, title: code, traceId: 'NTS-E2E', ...extra };
}

async function fulfilJson(route: Route, status: number, body: unknown, headers: Record<string, string> = {}): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(body) });
}

/** Background calls the app may make after a stubbed sign-in must not reach a missing backend or end the session. */
async function quietBackground(page: Page): Promise<void> {
  await page.route('**/api/v1/hubs/ticket', (route) => route.abort('failed'));
  await page.route('**/api/v1/auth/refresh', (route) => fulfilJson(route, 401, problem(401, 'session_revoked')));
}

function trackLogin(page: Page): { calls: Array<{ body: unknown }> } {
  const calls: Array<{ body: unknown }> = [];
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/auth/login') {
      calls.push({ body: request.postDataJSON() as unknown });
    }
  });
  return { calls };
}

async function fillAndSubmit(page: Page, email: string, password: string): Promise<void> {
  await page.getByLabel(LABEL.email).fill(email);
  await page.getByLabel(LABEL.password).fill(password);
  await page.getByRole('button', { name: LABEL.signIn }).click();
}

function pathOf(page: Page): string {
  return new URL(page.url()).pathname;
}

for (const { tag, lang } of LOCALES) {
  test.describe(`login (${lang}), stubbed API`, () => {
    test.use({ locale: tag });

    test(`wrong password: invalid-credentials alert, e-mail kept, password emptied and focused, no navigation [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 401, problem(401, 'invalid_credentials')));
      const login = trackLogin(page);
      await page.goto('/login');
      await fillAndSubmit(page, 'ha.nguyen@example.test', 'wrong-password-1');

      await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
      await expect(page.getByLabel(LABEL.email)).toHaveValue('ha.nguyen@example.test');
      await expect(page.getByLabel(LABEL.password)).toHaveValue('');
      await expect(page.getByLabel(LABEL.password)).toBeFocused();
      expect(pathOf(page)).toBe('/login');
      expect(login.calls).toHaveLength(1);
      expect(login.calls[0]?.body).toEqual({ email: 'ha.nguyen@example.test', password: 'wrong-password-1' });
    });

    test(`unknown e-mail and wrong password render the same alert [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 401, problem(401, 'invalid_credentials')));
      await page.goto('/login');
      const texts: string[] = [];
      for (const email of ['known@example.test', 'nobody@example.test']) {
        await fillAndSubmit(page, email, 'wrong-password-1');
        await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
        texts.push((await page.getByRole('alert').innerText()).trim());
      }
      expect(texts[0]).toBe(texts[1]);
    });

    test(`429 + Retry-After: submit disabled with a live countdown, enabled at 0, a new submit is sent [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      let count = 0;
      await page.route(LOGIN_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': '2' })
          : fulfilJson(route, 401, problem(401, 'invalid_credentials'));
      });
      await page.goto('/login');
      await fillAndSubmit(page, 'ha.nguyen@example.test', 'wrong-password-1');

      const submit = page.getByRole('button', { name: LABEL.signIn });
      await expect(page.getByRole('status').filter({ hasText: COUNTDOWN })).toBeVisible();
      await expect(submit).toBeDisabled();
      expect(count).toBe(1);

      await expect(submit).toBeEnabled({ timeout: 10_000 });
      await page.getByLabel(LABEL.password).fill('wrong-password-2');
      await submit.click();
      await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
      expect(count).toBe(2);
    });

    test(`429 with a negative, fractional, textual or empty Retry-After never leaves submit stuck disabled [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      for (const value of ['-5', '1.5', 'abc', '']) {
        await page.route(LOGIN_URL, (route) => fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': value }));
        await page.goto('/login');
        await fillAndSubmit(page, 'ha.nguyen@example.test', 'wrong-password-1');
        await expect(page.getByRole('alert').or(page.getByRole('status')).first(), `Retry-After "${value}" is announced`).toBeVisible();
        await expect(page.getByRole('button', { name: LABEL.signIn }), `Retry-After "${value}" re-enables submit`).toBeEnabled({ timeout: 5000 });
        await page.unroute(LOGIN_URL);
      }
    });

    test(`a second 429 restarts the countdown on one live region instead of stacking [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      let count = 0;
      await page.route(LOGIN_URL, (route) => {
        count += 1;
        return fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': count === 1 ? '2' : '4' });
      });
      await page.goto('/login');
      await fillAndSubmit(page, 'ha.nguyen@example.test', 'wrong-password-1');
      const submit = page.getByRole('button', { name: LABEL.signIn });
      const countdown = page.getByRole('status').filter({ hasText: COUNTDOWN });
      await expect(countdown).toBeVisible();
      await expect(submit).toBeEnabled({ timeout: 10_000 });

      await submit.click();
      await expect(countdown).toBeVisible();
      await expect(countdown).toHaveCount(1);
      const seconds = Number(COUNTDOWN.exec(await countdown.innerText())?.[1]);
      expect(seconds, 'the restarted countdown starts from the second Retry-After, not the first').toBeGreaterThan(2);
      expect(seconds).toBeLessThanOrEqual(4);
      await expect(submit).toBeDisabled();
      await expect(submit).toBeEnabled({ timeout: 10_000 });
      expect(count).toBe(2);
    });

    test(`network failure: network-error alert, no automatic retry, a manual submit sends one more request [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      let count = 0;
      await page.route(LOGIN_URL, (route) => {
        count += 1;
        return route.abort('failed');
      });
      await page.goto('/login');
      await fillAndSubmit(page, 'ha.nguyen@example.test', 'wrong-password-1');
      await expect(page.getByRole('alert')).toContainText(NETWORK_ERROR);
      await page.waitForTimeout(1_500);
      expect(count, 'no retry loop').toBe(1);

      await page.getByLabel(LABEL.password).fill('wrong-password-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page.getByRole('alert')).toContainText(NETWORK_ERROR);
      expect(count).toBe(2);
    });

    test(`double submit sends exactly one request while the first is in flight [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      let release: () => void = () => undefined;
      const gate = new Promise<void>((resolve) => {
        release = resolve;
      });
      const login = trackLogin(page);
      await page.route(LOGIN_URL, async (route) => {
        await gate;
        await fulfilJson(route, 401, problem(401, 'invalid_credentials'));
      });
      await page.goto('/login');
      await page.getByLabel(LABEL.email).fill('ha.nguyen@example.test');
      await page.getByLabel(LABEL.password).fill('wrong-password-1');
      await page.getByLabel(LABEL.password).press('Enter');
      await page.getByLabel(LABEL.password).press('Enter').catch(() => undefined);
      await expect(page.getByRole('button', { name: LABEL.signingIn })).toBeDisabled();
      release();
      await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
      expect(login.calls).toHaveLength(1);
    });

    test(`the password value never reaches a DOM attribute, storage or the console [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      const secret = 'Zx9-never-in-the-dom-9xZ';
      const logs: string[] = [];
      page.on('console', (message) => logs.push(message.text()));
      page.on('pageerror', (error) => logs.push(error.message));
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 401, problem(401, 'invalid_credentials')));
      await page.goto('/login');
      await page.getByLabel(LABEL.email).fill('ha.nguyen@example.test');
      await page.getByLabel(LABEL.password).fill(secret);
      const inAttribute = await page.evaluate(
        (value) =>
          [...document.querySelectorAll('*')].some((element) =>
            [...element.attributes].some((attribute) => attribute.value.includes(value)),
          ),
        secret,
      );
      expect(inAttribute, 'password typed into the field is not mirrored into an attribute').toBe(false);
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page.getByRole('alert')).toBeVisible();
      const stored = await page.evaluate(
        (value) => JSON.stringify([{ ...localStorage }, { ...sessionStorage }, document.cookie]).includes(value),
        secret,
      );
      expect(stored).toBe(false);
      expect(logs.join('\n')).not.toContain(secret);
    });

    test(`axe (WCAG 2.1 AA), 360 px without horizontal scroll and no CSP violation on /login [${lang}]`, async ({ page }) => {
      await page.setViewportSize({ width: 360, height: 740 });
      await page.goto('/login');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expectA11yClean(page);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, 'scrollWidth - clientWidth').toBeLessThanOrEqual(0);
    });

    test(`axe stays clean with the invalid-credentials alert and with the 429 countdown shown [${lang}]`, async ({ page }) => {
      await quietBackground(page);
      let count = 0;
      await page.route(LOGIN_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 401, problem(401, 'invalid_credentials'))
          : fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': '30' });
      });
      await page.goto('/login');
      await fillAndSubmit(page, 'ha.nguyen@example.test', 'wrong-password-1');
      await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
      await expectA11yClean(page);
      await page.getByLabel(LABEL.password).fill('wrong-password-2');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page.getByRole('status').filter({ hasText: COUNTDOWN })).toBeVisible();
      await expectA11yClean(page);
    });
  });
}

test.describe('login: returnUrl is sanitised before any login attempt', () => {
  const REJECTED: ReadonlyArray<{ name: string; query: string }> = [
    { name: 'https://evil.example', query: encodeURIComponent('https://evil.example') },
    { name: '//evil.example', query: encodeURIComponent('//evil.example') },
    { name: '/\\evil', query: encodeURIComponent('/\\evil') },
    { name: '/login', query: encodeURIComponent('/login') },
    { name: '%2F%2Fevil.example (double-encoded)', query: encodeURIComponent(encodeURIComponent('//evil.example')) },
    { name: '/%5Cevil.example (encoded backslash)', query: encodeURIComponent('/%5Cevil.example') },
    { name: '/LOGIN (mixed case)', query: encodeURIComponent('/LOGIN') },
    { name: '/\\t/evil.example (raw tab)', query: encodeURIComponent('/\t/evil.example') },
    { name: '/\\n/evil.example (raw newline)', query: encodeURIComponent('/\n/evil.example') },
    { name: '/%09/evil.example (encoded tab)', query: encodeURIComponent('/%09/evil.example') },
    { name: '/login?returnUrl=/x (auth page with a query)', query: encodeURIComponent('/login?returnUrl=/x') },
  ];

  for (const { name, query } of REJECTED) {
    test(`returnUrl=${name} is rejected: no request before submit, the target after a stubbed sign-in is /dashboard`, async ({ page }) => {
      await quietBackground(page);
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 200, loginOkBody()));
      const login = trackLogin(page);
      await page.goto(`/login?returnUrl=${query}`);
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      const startOrigin = new URL(page.url()).origin;
      expect(login.calls, 'no login attempt happens just by opening the page').toHaveLength(0);
      expect(pathOf(page)).toBe('/login');

      await fillAndSubmit(page, 'owner@example.test', 'Correct-horse-1');
      await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
      expect(new URL(page.url()).origin, 'never left this origin').toBe(startOrigin);
      expect(login.calls).toHaveLength(1);
    });
  }
});

test.describe('real stack', () => {
  test.skip(process.env['NTS_E2E_STACK'] !== '1', 'needs the compose/native stack with a seeded owner; set NTS_E2E_STACK=1 (run by 11.5.1-T1 e2e.yml)');
  const email = process.env['NTS_E2E_OWNER_EMAIL'] ?? '';

  test('wrong password for the seeded owner: invalid-credentials alert, e-mail kept, password emptied and focused, no navigation', async ({ page }) => {
    test.skip(email === '', 'NTS_E2E_OWNER_EMAIL is not set');
    await page.goto('/login');
    await fillAndSubmit(page, email, `wrong-${Date.now()}-Aa1!`);
    await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
    await expect(page.getByLabel(LABEL.email)).toHaveValue(email);
    await expect(page.getByLabel(LABEL.password)).toHaveValue('');
    await expect(page.getByLabel(LABEL.password)).toBeFocused();
    expect(pathOf(page)).toBe('/login');
    await expectA11yClean(page);
  });

  test('an unknown e-mail renders the same alert as a wrong password', async ({ page }) => {
    await page.goto('/login');
    await fillAndSubmit(page, `nobody-${Date.now()}@example.test`, 'wrong-Aa1!-password');
    await expect(page.getByRole('alert')).toContainText(INVALID_CREDENTIALS);
    expect(pathOf(page)).toBe('/login');
  });
});
