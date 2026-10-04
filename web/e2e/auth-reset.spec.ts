import { AUTH_SPEC_USE } from './fixtures/auth';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import type { Page, Route } from '@playwright/test';

// 10.3.3-T1 (README D6/D9/D15/D16, plan.md §8 item 4 + §9 axe). Backend dependence (orchestrator decision):
//  - CLIENT-BEHAVIOUR tests stub `/api/v1/auth/forgot-password` and `/api/v1/auth/reset-password` with `page.route`
//    (fixtures/auth.ts allows only the UI and `page.route`/`page.clock`). No backend, no mail, no seeded account.
//  - REAL-STACK tests (`describe('real stack')`) are skipped unless `NTS_E2E_STACK=1` (compose/native stack, run by
//    11.5.1-T1's e2e.yml): forgot-password for a known and an unknown address, one run each, must be identical.
// Wire facts used by the stubs (generated client): ForgotPasswordCommand {email}; ResetPasswordCommand {token,
// newPassword}; reset answers 204; forgot answers 202; problem bodies carry {code, status, traceId, errors?}.

test.use(AUTH_SPEC_USE);

const LABEL = {
  email: /^E-mail$/i,
  sendLink: /^(Gửi liên kết đặt lại|Send reset link)$/,
  newPassword: /^(Mật khẩu mới|New password)/,
  repeatPassword: /^(Nhập lại mật khẩu mới|Repeat the new password)/,
  save: /^(Lưu mật khẩu mới|Save new password)$/,
  requestNew: /^(Yêu cầu liên kết mới|Request a new link)$/,
  signIn: /^(Đăng nhập|Sign in)$/,
  backToSignIn: /^(Về trang đăng nhập|Back to sign in)$/,
} as const;

const SENT_TITLE = /Kiểm tra hộp thư của bạn|Check your inbox/;
const EXPIRED_TITLE = /Liên kết đặt lại mật khẩu không dùng được|This reset link cannot be used/;
const SUCCESS_TITLE = /Đã đổi mật khẩu|Password changed/;
const MISMATCH = /Hai mật khẩu không khớp|The two passwords do not match/;
const BREACHED = /Mật khẩu này đã lộ trong một vụ rò rỉ dữ liệu|This password appeared in a data breach/;
const NETWORK_ERROR = /Không kết nối được tới máy chủ|No connection to the server/;
const COUNTDOWN = /(\d+)\s*(giây|seconds?)/i;

const FORGOT_URL = '**/api/v1/auth/forgot-password';
const RESET_URL = '**/api/v1/auth/reset-password';

const TOKEN = 'rT4k9Q_zLm-82Xv0aBcDeFgHiJkLmNoPqRsTuVwXyZ1';
const PASSWORD = 'Correct-Horse-Battery-42'; // gitleaks:allow (fake test fixture)
const BREACHED_PASSWORD = 'Password123456';

const LOCALES = [
  { tag: 'vi-VN', lang: 'vi' },
  { tag: 'en-GB', lang: 'en' },
] as const;

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, status, title: code, traceId: 'NTS-E2E', ...extra };
}

async function fulfilJson(route: Route, status: number, body: unknown, headers: Record<string, string> = {}): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(body) });
}

interface Recorded {
  readonly url: string;
  readonly pageUrl: string;
  readonly referer: string | undefined;
  readonly body: Record<string, unknown>;
}

/** Records every POST to `pathname` (URL, the page URL at send time, Referer, JSON body). */
function record(page: Page, pathname: string): Recorded[] {
  const calls: Recorded[] = [];
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === pathname) {
      calls.push({
        url: request.url(),
        pageUrl: page.url(),
        referer: request.headers()['referer'],
        body: request.postDataJSON() as Record<string, unknown>,
      });
    }
  });
  return calls;
}

async function fillReset(page: Page, password: string, confirm: string): Promise<void> {
  await page.getByLabel(LABEL.newPassword).fill(password);
  await page.getByLabel(LABEL.repeatPassword).fill(confirm);
}

/** Where a secret string is visible in the page: storage, DOM markup, attributes, input values, cookies. */
async function leaks(page: Page, secret: string): Promise<string[]> {
  return page.evaluate((value) => {
    const found: string[] = [];
    if (JSON.stringify({ ...localStorage }).includes(value)) found.push('localStorage');
    if (JSON.stringify({ ...sessionStorage }).includes(value)) found.push('sessionStorage');
    if (document.cookie.includes(value)) found.push('cookie');
    if (document.documentElement.outerHTML.includes(value)) found.push('DOM markup');
    if (
      [...document.querySelectorAll('*')].some((element) => [...element.attributes].some((attribute) => attribute.value.includes(value)))
    ) {
      found.push('attribute');
    }
    if ([...document.querySelectorAll('input,textarea')].some((field) => (field as HTMLInputElement).value.includes(value))) {
      found.push('input value');
    }
    if (location.href.includes(value) || document.referrer.includes(value)) found.push('URL/referrer');
    return found;
  }, secret);
}

for (const { tag, lang } of LOCALES) {
  test.describe(`forgot-password (${lang}), stubbed API`, () => {
    test.use({ locale: tag });

    test(`known and unknown address: identical panel, one request per submit, no retry loop [${lang}]`, async ({ page }) => {
      await page.route(FORGOT_URL, (route) => route.fulfill({ status: 202, body: '' }));
      const calls = record(page, '/api/v1/auth/forgot-password');
      const panels: string[] = [];
      for (const address of ['owner@example.test', 'nobody@example.test']) {
        await page.goto('/forgot-password');
        await page.getByLabel(LABEL.email).fill(address);
        await page.getByRole('button', { name: LABEL.sendLink }).click();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(SENT_TITLE);
        panels.push((await page.locator('main, [role="main"], app-root').first().innerText()).replace(/\s+/g, ' ').trim());
        expect(page.url(), 'the address never travels in the URL').not.toContain(encodeURIComponent(address));
      }
      await page.waitForTimeout(1_000);
      expect(calls.map((call) => call.body)).toEqual([{ email: 'owner@example.test' }, { email: 'nobody@example.test' }]);
      expect(panels[0]).toBe(panels[1]);
      expect(panels[0]).not.toContain('owner@example.test');
      expect(panels[1]).not.toContain('nobody@example.test');
    });

    test(`a 400 validation_failed keeps the form and does not show the sent panel [${lang}]`, async ({ page }) => {
      await page.route(FORGOT_URL, (route) =>
        fulfilJson(route, 400, problem(400, 'validation_failed', { errors: { email: ['invalid'] } })),
      );
      await page.goto('/forgot-password');
      await page.getByLabel(LABEL.email).fill('not-an-address');
      await page.getByRole('button', { name: LABEL.sendLink }).click();
      await expect(page.getByLabel(LABEL.email)).toBeVisible();
      await expect(page.getByRole('heading', { level: 1 })).not.toHaveText(SENT_TITLE);
    });

    test(`429 + Retry-After: submit disabled with a countdown, enabled at 0, a new submit is sent and shows the panel [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(FORGOT_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': '2' })
          : route.fulfill({ status: 202, body: '' });
      });
      await page.goto('/forgot-password');
      await page.getByLabel(LABEL.email).fill('owner@example.test');
      const submit = page.getByRole('button', { name: LABEL.sendLink });
      await submit.click();
      await expect(page.getByRole('status').filter({ hasText: COUNTDOWN })).toBeVisible();
      await expect(submit).toBeDisabled();
      expect(count).toBe(1);
      await expect(submit).toBeEnabled({ timeout: 10_000 });
      await submit.click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SENT_TITLE);
      expect(count).toBe(2);
    });

    test(`network failure shows the network-error alert and sends no second request on its own [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(FORGOT_URL, (route) => {
        count += 1;
        return route.abort('failed');
      });
      await page.goto('/forgot-password');
      await page.getByLabel(LABEL.email).fill('owner@example.test');
      await page.getByRole('button', { name: LABEL.sendLink }).click();
      await expect(page.getByRole('alert')).toContainText(NETWORK_ERROR);
      await page.waitForTimeout(1_500);
      expect(count).toBe(1);
    });

    test(`axe, 360 px without horizontal scroll and no CSP violation on /forgot-password (form, sent, 429) [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(FORGOT_URL, (route) => {
        count += 1;
        return count === 1 ? route.fulfill({ status: 202, body: '' }) : fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': '30' });
      });
      await page.setViewportSize({ width: 360, height: 740 });
      const noScroll = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

      await page.goto('/forgot-password');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);

      await page.getByLabel(LABEL.email).fill('owner@example.test');
      await page.getByRole('button', { name: LABEL.sendLink }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SENT_TITLE);
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);

      await page.goto('/forgot-password');
      await page.getByLabel(LABEL.email).fill('owner@example.test');
      await page.getByRole('button', { name: LABEL.sendLink }).click();
      await expect(page.getByRole('status').filter({ hasText: COUNTDOWN })).toBeVisible();
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);
    });
  });

  test.describe(`reset-password (${lang}), stubbed API`, () => {
    test.use({ locale: tag });

    test(`the #t= fragment is gone before the POST; the token is only in the request body [${lang}]`, async ({ page }) => {
      const logs: string[] = [];
      page.on('console', (message) => logs.push(message.text()));
      page.on('pageerror', (error) => logs.push(error.message));
      await page.route(RESET_URL, (route) => route.fulfill({ status: 204, body: '' }));
      const calls = record(page, '/api/v1/auth/reset-password');

      await page.goto(`/reset-password#t=${TOKEN}`);
      await expect(page.getByLabel(LABEL.newPassword)).toBeEnabled();
      expect(await page.evaluate(() => location.hash), 'fragment removed on load').toBe('');
      expect(await leaks(page, TOKEN)).toEqual([]);

      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SUCCESS_TITLE);

      expect(await page.evaluate(() => location.hash), 'fragment still empty after submit').toBe('');
      expect(calls).toHaveLength(1);
      const [call] = calls;
      expect(call?.body).toEqual({ token: TOKEN, newPassword: PASSWORD });
      expect(call?.url).not.toContain(TOKEN);
      expect(call?.pageUrl).not.toContain(TOKEN);
      expect(call?.referer ?? '').not.toContain(TOKEN);
      expect(await leaks(page, TOKEN)).toEqual([]);
      expect(await leaks(page, PASSWORD)).toEqual([]);
      expect(logs.join('\n')).not.toContain(TOKEN);
      expect(logs.join('\n')).not.toContain(PASSWORD);
    });

    test(`success panel links back to /login and the token cannot be re-read after a reload [${lang}]`, async ({ page }) => {
      await page.route(RESET_URL, (route) => route.fulfill({ status: 204, body: '' }));
      const calls = record(page, '/api/v1/auth/reset-password');
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SUCCESS_TITLE);
      await expect(page.getByRole('link', { name: LABEL.signIn })).toHaveAttribute('href', /\/login(\?|$)/);

      await page.reload();
      await expect(page.getByLabel(LABEL.newPassword)).toHaveCount(0);
      await expect(page.getByRole('link', { name: LABEL.requestNew })).toHaveAttribute('href', /\/forgot-password/);
      expect(calls).toHaveLength(1);
    });

    test(`no fragment, a malformed fragment or a token in the query: missing-token state, form disabled, no request [${lang}]`, async ({ page }) => {
      const calls = record(page, '/api/v1/auth/reset-password');
      for (const target of ['/reset-password', '/reset-password#t=', '/reset-password#t=a%20b', `/reset-password#t=${TOKEN}&x=1`, `/reset-password?t=${TOKEN}`]) {
        await page.goto(target);
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
        const submit = page.getByRole('button', { name: LABEL.save });
        if ((await submit.count()) > 0) await expect(submit).toBeDisabled();
        const newPassword = page.getByLabel(LABEL.newPassword);
        if ((await newPassword.count()) > 0) await expect(newPassword).toBeDisabled();
        await expect(page.getByRole('link', { name: LABEL.requestNew })).toHaveAttribute('href', /\/forgot-password/);
      }
      expect(calls).toHaveLength(0);
    });

    test(`mismatching confirmation shows the mismatch message and sends no request [${lang}]`, async ({ page }) => {
      const calls = record(page, '/api/v1/auth/reset-password');
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, PASSWORD, `${PASSWORD}-x`);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByText(MISMATCH)).toBeVisible();
      expect(calls).toHaveLength(0);
    });

    test(`used/expired/unknown token: request-a-new-link state with a link to /forgot-password [${lang}]`, async ({ page }) => {
      await page.route(RESET_URL, (route) =>
        fulfilJson(route, 400, problem(400, 'validation_failed', { errors: { token: ['invalid'] } })),
      );
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(EXPIRED_TITLE);
      await expect(page.getByRole('link', { name: LABEL.requestNew })).toHaveAttribute('href', /\/forgot-password/);
      expect(await leaks(page, TOKEN)).toEqual([]);
    });

    test(`breached password: field error, form stays submittable and the second POST carries the same token [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(RESET_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 400, problem(400, 'breached_password', { errors: { newPassword: ['breached'] } }))
          : route.fulfill({ status: 204, body: '' });
      });
      const calls = record(page, '/api/v1/auth/reset-password');
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, BREACHED_PASSWORD, BREACHED_PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();

      await expect(page.getByText(BREACHED)).toBeVisible();
      await expect(page.getByLabel(LABEL.newPassword)).toHaveAttribute('aria-invalid', 'true');
      await expect(page.getByLabel(LABEL.newPassword)).toBeFocused();
      await expect(page.getByRole('button', { name: LABEL.save })).toBeEnabled();
      expect(await page.evaluate(() => location.hash)).toBe('');

      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SUCCESS_TITLE);
      expect(calls.map((call) => call.body)).toEqual([
        { token: TOKEN, newPassword: BREACHED_PASSWORD },
        { token: TOKEN, newPassword: PASSWORD },
      ]);
    });

    test(`weak password (400 validation_failed on newPassword): field error and the same token stays usable [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(RESET_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 400, problem(400, 'validation_failed', { errors: { newPassword: ['too short'] } }))
          : route.fulfill({ status: 204, body: '' });
      });
      const calls = record(page, '/api/v1/auth/reset-password');
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, 'weak-but-12-chars', 'weak-but-12-chars');
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByLabel(LABEL.newPassword)).toHaveAttribute('aria-invalid', 'true');
      await expect(page.getByRole('button', { name: LABEL.save })).toBeEnabled();
      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SUCCESS_TITLE);
      expect(calls.map((call) => call.body['token'])).toEqual([TOKEN, TOKEN]);
    });

    test(`429 + Retry-After on reset: countdown, then the same token is sent again [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(RESET_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': '2' })
          : route.fulfill({ status: 204, body: '' });
      });
      const calls = record(page, '/api/v1/auth/reset-password');
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, PASSWORD, PASSWORD);
      const submit = page.getByRole('button', { name: LABEL.save });
      await submit.click();
      await expect(page.getByRole('status').filter({ hasText: COUNTDOWN })).toBeVisible();
      await expect(submit).toBeDisabled();
      await expect(submit).toBeEnabled({ timeout: 10_000 });
      await submit.click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SUCCESS_TITLE);
      expect(calls.map((call) => call.body['token'])).toEqual([TOKEN, TOKEN]);
    });

    test(`axe, 360 px without horizontal scroll and no CSP violation on /reset-password (form, mismatch, missing token, expired, success) [${lang}]`, async ({ page }) => {
      let count = 0;
      await page.route(RESET_URL, (route) => {
        count += 1;
        return count === 1
          ? fulfilJson(route, 400, problem(400, 'validation_failed', { errors: { token: ['invalid'] } }))
          : route.fulfill({ status: 204, body: '' });
      });
      await page.setViewportSize({ width: 360, height: 740 });
      const noScroll = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

      await page.goto(`/reset-password#t=${TOKEN}`);
      await expect(page.getByLabel(LABEL.newPassword)).toBeEnabled();
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);

      await fillReset(page, PASSWORD, `${PASSWORD}-x`);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByText(MISMATCH)).toBeVisible();
      await expectA11yClean(page);

      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(EXPIRED_TITLE);
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);

      await page.goto('/reset-password');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);

      // Same path with a new fragment is a same-document navigation (the SPA does not restart); the e-mail link is a full load.
      await page.goto('about:blank');
      await page.goto(`/reset-password#t=${TOKEN}`);
      await fillReset(page, PASSWORD, PASSWORD);
      await page.getByRole('button', { name: LABEL.save }).click();
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SUCCESS_TITLE);
      await expectA11yClean(page);
      expect(await noScroll()).toBeLessThanOrEqual(0);
    });
  });
}

test.describe('real stack', () => {
  test.describe.configure({ mode: 'serial' });
  test.skip(process.env['NTS_E2E_STACK'] !== '1', 'needs the compose/native stack; set NTS_E2E_STACK=1 (run by 11.5.1-T1 e2e.yml)');

  const known = process.env['NTS_E2E_OWNER_EMAIL'] ?? '';
  const outcomes = new Map<string, { status: number; panel: string }>();

  for (const [label, address] of [
    ['known', known],
    ['unknown', `nobody-${Date.now()}@example.test`],
  ] as const) {
    test(`forgot-password with a ${label} address shows the sent panel`, async ({ page }) => {
      test.skip(address === '', 'NTS_E2E_OWNER_EMAIL is not set');
      await page.goto('/forgot-password');
      await page.getByLabel(LABEL.email).fill(address);
      const response = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/v1/auth/forgot-password');
      await page.getByRole('button', { name: LABEL.sendLink }).click();
      const { status } = await response;
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(SENT_TITLE);
      const panel = (await page.locator('main, [role="main"], app-root').first().innerText()).replace(/\s+/g, ' ').trim();
      outcomes.set(label, { status: status(), panel });
    });
  }

  test('known and unknown addresses produced the same status and the same panel text', () => {
    test.skip(!outcomes.has('known') || !outcomes.has('unknown'), 'one of the two runs was skipped');
    expect(outcomes.get('unknown')).toEqual(outcomes.get('known'));
  });
});
