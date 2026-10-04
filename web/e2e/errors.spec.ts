import { AUTH_SPEC_USE } from './fixtures/auth';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import type { Page, Route } from '@playwright/test';

// 10 CR-46 (from 10.2.2-T1 §Tests): AppErrorMapper / bindFieldErrors / RetryCountdown observed on the real `/login`.
// Backend dependence (orchestrator decision): the client-behaviour tests stub `/api/v1/auth/login` with `page.route`
// (fixtures/auth.ts allows only the UI and `page.route`/`page.clock`). The one assertion that the REAL API's empty
// submit answers `validation_failed` lives in `describe('real stack')`, skipped unless `NTS_E2E_STACK=1`
// (compose/native stack, run by 11.5.1-T1's e2e.yml). Bound controls show the code-level key `errors.<code>`
// (the wire `errors` values are English descriptions and are never rendered).

test.use(AUTH_SPEC_USE);

const LABEL = {
  email: /^E-mail$/i,
  password: /^(Mật khẩu|Password)$/,
  signIn: /^(Đăng nhập|Sign in)$/,
} as const;

const VALIDATION_FAILED = /Một số trường chưa hợp lệ|Some fields are not valid/;
const UNKNOWN_ERROR = /Đã xảy ra lỗi|Something went wrong/;
const COUNTDOWN = /(\d+)\s*(giây|seconds?)/i;
const LOGIN_URL = '**/api/v1/auth/login';

const LOCALES = [
  { tag: 'vi-VN', lang: 'vi' },
  { tag: 'en-GB', lang: 'en' },
] as const;

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, status, title: code, traceId: 'NTS-7K2P', ...extra };
}

async function fulfilJson(route: Route, status: number, body: unknown, headers: Record<string, string> = {}): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(body) });
}

/** Every `aria-describedby` target of the control resolves to an element with visible text. */
async function describedText(page: Page, label: RegExp): Promise<string> {
  const control = page.getByLabel(label);
  const ids = ((await control.getAttribute('aria-describedby')) ?? '').split(/\s+/).filter(Boolean);
  expect(ids.length, 'aria-describedby is set on the invalid control').toBeGreaterThan(0);
  const texts: string[] = [];
  for (const id of ids) {
    texts.push(((await page.locator(`[id="${id}"]`).first().textContent()) ?? '').trim());
  }
  return texts.join(' ').trim();
}

for (const { tag, lang } of LOCALES) {
  test.describe(`errors on /login (${lang}), stubbed API`, () => {
    test.use({ locale: tag });

    test(`empty submit: errors.validation_failed on the bound controls, aria-describedby link, focus on the first invalid control [${lang}]`, async ({ page }) => {
      await page.route(LOGIN_URL, (route) =>
        fulfilJson(route, 400, problem(400, 'validation_failed', { errors: { email: ['English description, never rendered'], password: ['English description, never rendered'] } })),
      );
      await page.goto('/login');
      await page.getByRole('button', { name: LABEL.signIn }).click();

      const email = page.getByLabel(LABEL.email);
      await expect(email).toHaveAttribute('aria-invalid', 'true');
      await expect(email).toBeFocused();
      expect(await describedText(page, LABEL.email)).toMatch(VALIDATION_FAILED);
      await expect(page.getByLabel(LABEL.password)).toHaveAttribute('aria-invalid', 'true');
      expect(await describedText(page, LABEL.password)).toMatch(VALIDATION_FAILED);
      await expect(page.getByText('English description, never rendered')).toHaveCount(0);
      await expectA11yClean(page);
    });

    test(`429 through a route stub: RetryCountdown disables the action and announces the wait [${lang}]`, async ({ page }) => {
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': '42' }));
      await page.goto('/login');
      await page.getByLabel(LABEL.email).fill('ha.nguyen@example.test');
      await page.getByLabel(LABEL.password).fill('wrong-password-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();

      const countdown = page.getByRole('status').filter({ hasText: COUNTDOWN });
      await expect(countdown).toBeVisible();
      const seconds = Number(COUNTDOWN.exec(await countdown.innerText())?.[1]);
      expect(seconds).toBeGreaterThan(0);
      expect(seconds).toBeLessThanOrEqual(42);
      await expect(page.getByRole('button', { name: LABEL.signIn })).toBeDisabled();
      await expectA11yClean(page);
    });

    test(`429 without a usable Retry-After does not invent a countdown number [${lang}]`, async ({ page }) => {
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Retry-After': 'Wed, 21 Oct 2026 07:28:00 GMT' }));
      await page.goto('/login');
      await page.getByLabel(LABEL.email).fill('ha.nguyen@example.test');
      await page.getByLabel(LABEL.password).fill('wrong-password-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page.getByRole('alert').or(page.getByRole('status')).first()).toBeVisible();
      await expect(page.getByRole('status').filter({ hasText: COUNTDOWN })).toHaveCount(0);
    });

    test(`an unknown wire code and an HTML 502 body map to the unknown-error copy, never to raw server text [${lang}]`, async ({ page }) => {
      const bodies = [
        (route: Route) => fulfilJson(route, 500, problem(500, 'definitely_not_a_known_code', { detail: 'RAW-SERVER-TEXT' })),
        (route: Route) => route.fulfill({ status: 502, contentType: 'text/html', body: '<html><body>RAW-PROXY-TEXT</body></html>' }),
      ];
      let index = 0;
      await page.route(LOGIN_URL, (route) => {
        const handler = bodies[Math.min(index, bodies.length - 1)];
        index += 1;
        return handler?.(route) ?? route.abort('failed');
      });
      await page.goto('/login');
      await page.getByLabel(LABEL.email).fill('ha.nguyen@example.test');
      for (let attempt = 0; attempt < bodies.length; attempt += 1) {
        await page.getByLabel(LABEL.password).fill('wrong-password-1');
        await page.getByRole('button', { name: LABEL.signIn }).click();
        await expect(page.getByRole('alert')).toContainText(UNKNOWN_ERROR);
        await expect(page.getByText(/RAW-SERVER-TEXT|RAW-PROXY-TEXT/)).toHaveCount(0);
      }
    });

    test(`the trace id of a server error is shown for support and the alert is not colour-only (axe) [${lang}]`, async ({ page }) => {
      await page.route(LOGIN_URL, (route) => fulfilJson(route, 500, problem(500, 'internal_error')));
      await page.goto('/login');
      await page.getByLabel(LABEL.email).fill('ha.nguyen@example.test');
      await page.getByLabel(LABEL.password).fill('wrong-password-1');
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(page.getByRole('alert')).toContainText('NTS-7K2P');
      await expectA11yClean(page);
    });
  });
}

test.describe('real stack', () => {
  test.skip(process.env['NTS_E2E_STACK'] !== '1', 'needs the compose/native stack; set NTS_E2E_STACK=1 (run by 11.5.1-T1 e2e.yml)');

  test('login empty submit against the real API: errors.validation_failed, aria-describedby link and focus move', async ({ page }) => {
    await page.goto('/login');
    await page.getByRole('button', { name: LABEL.signIn }).click();
    await expect(page.getByLabel(LABEL.email)).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel(LABEL.email)).toBeFocused();
    expect(await describedText(page, LABEL.email)).toMatch(VALIDATION_FAILED);
    await expectA11yClean(page);
  });
});
