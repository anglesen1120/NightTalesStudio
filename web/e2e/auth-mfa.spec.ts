import {
  AUTH_SPEC_USE,
  LABEL,
  STUB,
  enrol,
  expectMfaBodyShape,
  expectNoSecretLeak,
  fulfilJson,
  loadSecret,
  loginOkBody,
  msUntilNextStep,
  problem,
  quietBackground,
  totp,
  trackConsole,
  trackPosts,
} from './fixtures/auth';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { setTimeout as sleep } from 'node:timers/promises';
import type { Page } from '@playwright/test';

// 10.3.3-T3 (README D6/D9/D15/D16, plan.md §4 U1-U3, §8). The MFA step inside `/login`.
//  - Stubbed describe: `page.route` only (no backend, no TOTP). The `/dashboard` assertions read the URL PATH only and
//    need the merged tree (no dashboard page until 10.4.1-T1), exactly like auth-login.spec.ts / session.spec.ts.
//  - `describe('real stack')` runs only with NTS_E2E_STACK=1 (and NTS_E2E_OWNER_*): it drives the seeded owner through
//    the UI and replays the same code to prove the server-side single-use rule (AC1). Inside the block the observed
//    server state decides the path (unenrolled -> enrolment must succeed, enrolled -> the TOTP step must appear): the
//    expectations are hard on both paths, never `test.skip`, so a broken flow fails (D8).
// One login per file; never a reused storageState (F08).
// Acceptance checks covered (task file §Acceptance): AC1 (stack: replay), AC2, AC3, AC11 (leak), AC12 (axe/CSP).

test.use(AUTH_SPEC_USE);
test.use({ locale: 'vi-VN' });

const LOGIN = '**/api/v1/auth/login';
const LOGIN_PATH = '/api/v1/auth/login';
const MFA = '**/api/v1/auth/login/mfa';
const MFA_PATH = '/api/v1/auth/login/mfa';

type MfaReply = { status: number; body: unknown; headers?: Record<string, string> };

/** Stubs the password step as `401 mfa_required { mfaToken }` and the MFA step with a queue of replies. */
async function stubChallenge(page: Page, replies: MfaReply[]): Promise<void> {
  await quietBackground(page);
  await page.route(LOGIN, (route) => fulfilJson(route, 401, problem(401, 'mfa_required', { mfaToken: STUB.mfaToken })));
  let index = 0;
  await page.route(MFA, (route) => {
    const reply = replies[Math.min(index, replies.length - 1)] as MfaReply;
    index += 1;
    return fulfilJson(route, reply.status, reply.body, reply.headers);
  });
}

async function reachMfaStep(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel(LABEL.email).fill('owner@example.test');
  await page.getByLabel(LABEL.password).fill('correct-horse-Aa1!');
  await page.getByRole('button', { name: LABEL.signIn }).click();
  await expect(page.getByLabel(LABEL.authCode)).toBeVisible();
}

function pathOf(page: Page): string {
  return new URL(page.url()).pathname;
}

/**
 * D6: `invalid_mfa_code` is a FIELD error, not a form-level alert (task Steps 5; the component binds a mat-error with
 * `aria-invalid` + `aria-describedby` and moves focus). Asserts the wiring, not just "something is shown".
 */
async function expectInvalidMfaCodeOnField(page: Page): Promise<void> {
  const input = page.locator('#mfa-code');
  await expect(input).toHaveAttribute('aria-invalid', 'true');
  await expect(input).toHaveAttribute('aria-describedby', /(^|\s)mfa-code-error(\s|$)/);
  await expect(page.locator('#mfa-code-error')).toHaveText(LABEL.invalidMfaCode);
  await expect(input).toBeFocused();
  await expect(page.locator('#mfa-form-alert'), 'a rejected code is not a form-level alert').toHaveCount(0);
}

test.describe.serial('login MFA step, stubbed API', () => {
  test('AC1 password -> MFA step without navigation; a TOTP code posts `code` only and lands on /dashboard', async ({ page }) => {
    await stubChallenge(page, [{ status: 200, body: loginOkBody() }]);
    const login = trackPosts(page, LOGIN_PATH);
    const mfa = trackPosts(page, MFA_PATH);

    await reachMfaStep(page);
    expect(pathOf(page), 'the address bar still shows /login').toBe('/login');
    expect(login).toHaveLength(1);
    expect(mfa, 'no MFA call before the code is submitted').toHaveLength(0);

    await page.getByLabel(LABEL.authCode).fill('123 456');
    await page.getByRole('button', { name: LABEL.verify }).click();

    await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
    expect(mfa).toHaveLength(1);
    expectMfaBodyShape(mfa[0]?.body, 'totp');
    expect((mfa[0]?.body as Record<string, unknown>)['code'], 'display spaces are normalised away').toBe('123456');
    expect((mfa[0]?.body as Record<string, unknown>)['mfaToken']).toBe(STUB.mfaToken);
  });

  test('AC1 a rejected code keeps the step and the field; the next code succeeds (single submit each)', async ({ page }) => {
    await stubChallenge(page, [
      { status: 401, body: problem(401, 'invalid_mfa_code') },
      { status: 200, body: loginOkBody() },
    ]);
    const mfa = trackPosts(page, MFA_PATH);

    await reachMfaStep(page);
    await page.getByLabel(LABEL.authCode).fill('111111');
    await page.getByRole('button', { name: LABEL.verify }).click();

    await expectInvalidMfaCodeOnField(page);
    await expect(page.getByLabel(LABEL.authCode)).toBeVisible();
    expect(pathOf(page)).toBe('/login');
    expect(mfa).toHaveLength(1);

    await page.getByLabel(LABEL.authCode).fill('222222');
    await page.getByRole('button', { name: LABEL.verify }).click();
    await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
    expect(mfa).toHaveLength(2);
    mfa.forEach((call) => expectMfaBodyShape(call.body, 'totp'));
  });

  test('AC2 recovery mode posts `recoveryCode` only (code null), toggling sends nothing, the second use fails', async ({ page }) => {
    await stubChallenge(page, [
      { status: 200, body: loginOkBody() },
      { status: 401, body: problem(401, 'invalid_mfa_code') },
    ]);
    const mfa = trackPosts(page, MFA_PATH);

    await reachMfaStep(page);
    await page.getByRole('button', { name: LABEL.useRecovery }).or(page.getByRole('link', { name: LABEL.useRecovery })).click();
    await expect(page.getByLabel(LABEL.recoveryCode)).toBeVisible();
    expect(mfa, 'toggling the mode issues no request').toHaveLength(0);

    await page.getByLabel(LABEL.recoveryCode).fill(STUB.recoveryCodes[0]);
    await page.getByRole('button', { name: LABEL.verify }).click();
    await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
    expect(mfa).toHaveLength(1);
    expectMfaBodyShape(mfa[0]?.body, 'recovery');
    expect((mfa[0]?.body as Record<string, unknown>)['recoveryCode']).toBe(STUB.recoveryCodes[0]);

    // Second use of the same code: the (stubbed) server refuses it; a fresh page keeps the step and shows the error.
    await page.context().clearCookies();
    await page.goto('/login');
    await page.getByLabel(LABEL.email).fill('owner@example.test');
    await page.getByLabel(LABEL.password).fill('correct-horse-Aa1!');
    await page.getByRole('button', { name: LABEL.signIn }).click();
    await page.getByRole('button', { name: LABEL.useRecovery }).or(page.getByRole('link', { name: LABEL.useRecovery })).click();
    await page.getByLabel(LABEL.recoveryCode).fill(STUB.recoveryCodes[0]);
    await page.getByRole('button', { name: LABEL.verify }).click();
    await expectInvalidMfaCodeOnField(page);
    expect(pathOf(page)).toBe('/login');
    expectMfaBodyShape(mfa[1]?.body, 'recovery');
  });

  test('AC2 an empty code is never posted', async ({ page }) => {
    await stubChallenge(page, [{ status: 200, body: loginOkBody() }]);
    const mfa = trackPosts(page, MFA_PATH);
    await reachMfaStep(page);
    await page.getByLabel(LABEL.authCode).fill('');
    await page.getByLabel(LABEL.authCode).press('Enter');
    expect(mfa).toHaveLength(0);
    expect(pathOf(page)).toBe('/login');
  });

  test('AC3 mfa_token_invalid returns the password step with the challenge-expired message; a reload does the same', async ({ page }) => {
    await stubChallenge(page, [{ status: 401, body: problem(401, 'mfa_token_invalid') }]);
    const consoleLines = trackConsole(page);

    await reachMfaStep(page);
    await page.getByLabel(LABEL.authCode).fill('123456');
    await page.getByRole('button', { name: LABEL.verify }).click();

    await expect(page.getByText(LABEL.challengeExpired)).toBeVisible();
    await expect(page.getByLabel(LABEL.password)).toBeVisible();
    await expect(page.getByLabel(LABEL.authCode)).toHaveCount(0);
    expect(pathOf(page)).toBe('/login');
    await expectNoSecretLeak(page, [STUB.mfaToken], consoleLines);

    // Reload during the MFA step: the token is memory-only, so the password step is back.
    await reachMfaStep(page);
    await page.reload();
    await expect(page.getByLabel(LABEL.password)).toBeVisible();
    await expect(page.getByLabel(LABEL.authCode)).toHaveCount(0);
    await expectNoSecretLeak(page, [STUB.mfaToken], consoleLines);
  });

  test('429 on the MFA step shows the Retry-After countdown and disables the code field and submit', async ({ page }) => {
    await stubChallenge(page, [{ status: 429, body: problem(429, 'rate_limited'), headers: { 'Retry-After': '900' } }]);
    await reachMfaStep(page);
    await page.getByLabel(LABEL.authCode).fill('123456');
    await page.getByRole('button', { name: LABEL.verify }).click();

    await expect(page.getByText(LABEL.countdown).first()).toBeVisible();
    await expect(page.getByRole('button', { name: LABEL.verify })).toBeDisabled();
    expect(pathOf(page)).toBe('/login');
  });

  test('AC11 the mfaToken is in no URL, storage, DOM attribute or console line during and after the step', async ({ page }) => {
    await stubChallenge(page, [{ status: 200, body: loginOkBody() }]);
    const consoleLines = trackConsole(page);
    await reachMfaStep(page);
    await expectNoSecretLeak(page, [STUB.mfaToken], consoleLines);
    await page.getByLabel(LABEL.authCode).fill('123456');
    await page.getByRole('button', { name: LABEL.verify }).click();
    await expect(page).toHaveURL((url) => url.pathname === '/dashboard');
    await expectNoSecretLeak(page, [STUB.mfaToken, '123456'], consoleLines);
  });

  test('AC12 the MFA step (TOTP and recovery mode) is axe-clean with no CSP violation', async ({ page }) => {
    await stubChallenge(page, [{ status: 200, body: loginOkBody() }]);
    await reachMfaStep(page);
    await expectA11yClean(page);
    await page.getByRole('button', { name: LABEL.useRecovery }).or(page.getByRole('link', { name: LABEL.useRecovery })).click();
    await expect(page.getByLabel(LABEL.recoveryCode)).toBeVisible();
    await expectA11yClean(page);
  });
});

test.describe('real stack', () => {
  test.skip(process.env['NTS_E2E_STACK'] !== '1', 'needs the compose/native stack with a seeded owner; set NTS_E2E_STACK=1 (run by 11.5.1-T1 e2e.yml)');
  test.skip((process.env['NTS_E2E_OWNER_EMAIL'] ?? '') === '', 'NTS_E2E_OWNER_EMAIL is not set');

  test('AC1 owner login: MFA step on /login, a TOTP code signs in, the same code replayed is refused, the next step succeeds', async ({ page }) => {
    const email = process.env['NTS_E2E_OWNER_EMAIL'] ?? '';
    const password = process.env['NTS_E2E_OWNER_PASSWORD'] ?? '';
    const mfa = trackPosts(page, MFA_PATH);

    await page.goto('/login');
    await page.getByLabel(LABEL.email).fill(email);
    await page.getByLabel(LABEL.password).fill(password);
    await page.getByRole('button', { name: LABEL.signIn }).click();
    const codeField = page.getByLabel(LABEL.authCode);
    const manualKey = page.getByLabel(LABEL.manualKey);
    // D8: the server state decides the path, and EACH path has hard expectations -- never a skip, so a broken
    // flow fails. Unenrolled -> the enrolment flow MUST succeed, then sign in again with a fresh code; enrolled ->
    // the TOTP step MUST appear. `enrol()` is the same helper `loginAsOwner` uses.
    await expect(codeField.or(manualKey), 'the owner login left neither the MFA step nor the enrolment page').toBeVisible({ timeout: 15_000 });
    if (!(await codeField.isVisible())) {
      const usedAt = await enrol(page);
      await page.getByRole('link', { name: LABEL.signInAgain }).or(page.getByRole('button', { name: LABEL.signInAgain })).click();
      await page.getByLabel(LABEL.email).fill(email);
      await page.getByLabel(LABEL.password).fill(password);
      await page.getByRole('button', { name: LABEL.signIn }).click();
      await expect(codeField, 'after enrolment the owner must reach the MFA step').toBeVisible({ timeout: 10_000 });
      if (Math.floor(Date.now() / 30_000) === Math.floor(usedAt / 30_000)) {
        await sleep(msUntilNextStep());
      }
    }
    expect(new URL(page.url()).pathname).toBe('/login');

    const secret = loadSecret();
    const code = totp(secret);
    await codeField.fill(code);
    await page.getByRole('button', { name: LABEL.verify }).click();
    await page.waitForURL((url) => !url.pathname.startsWith('/login'));
    expectMfaBodyShape(mfa[0]?.body, 'totp');

    // Replay the same code on a fresh challenge: single use per 30 s step.
    await page.context().clearCookies();
    await page.evaluate(() => sessionStorage.clear());
    await page.goto('/login');
    await page.getByLabel(LABEL.email).fill(email);
    await page.getByLabel(LABEL.password).fill(password);
    await page.getByRole('button', { name: LABEL.signIn }).click();
    await codeField.fill(code);
    await page.getByRole('button', { name: LABEL.verify }).click();
    await expectInvalidMfaCodeOnField(page);
    expect(new URL(page.url()).pathname).toBe('/login');

    await sleep(msUntilNextStep());
    await codeField.fill(totp(secret));
    await page.getByRole('button', { name: LABEL.verify }).click();
    await page.waitForURL((url) => !url.pathname.startsWith('/login'));
    expect(mfa.length).toBe(3);
    mfa.forEach((call) => expectMfaBodyShape(call.body, 'totp'));
  });
});
