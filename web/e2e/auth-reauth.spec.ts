import {
  AUTH_SPEC_USE,
  LABEL,
  STUB,
  expectNoSecretLeak,
  fulfilJson,
  loginAsOwner,
  loginOkBody,
  problem,
  trackConsole,
  trackPosts,
} from './fixtures/auth';
import { expectA11yClean, waitForOverlayAnimationEnd } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import type { Page, Route } from '@playwright/test';

// 10.3.3-T3 (plan.md §4 U7-U8, README D11). The shared `ReauthDialog` driven by `ReauthService`, stubbed with
// `page.route` only. A full session is restored by a stubbed boot refresh; the owner then opens the enrolment page
// (`mfaEnrolmentGuard` accepts a full session), whose first `POST /me/mfa/setup` answers `401 reauth_required`
// (failure-handling row "reauth_required on setup from an authenticated session"), or presses "new recovery codes".
// Acceptance checks covered: AC7 (regenerate behind re-auth, cancel = no request), AC8 (one dialog, retried once,
// wrong password keeps it open, Escape = no POST + focus restored), AC9 (5th attempt -> session_revoked -> /login;
// 429 countdown + disabled action), AC10 (no secret in storage/console), AC11 (axe on the open dialog + CSP).
// Not e2e-observable (covered by reauth.service.spec.ts): "two parallel callers -> one POST" (the UI issues one call at
// a time), "late 200 after logout ignored".
// Real stack: the owner needs a TOTP and a known password (NTS_E2E_OWNER_*); see the block at the bottom.

test.use(AUTH_SPEC_USE);
test.use({ locale: 'vi-VN' });

const SETUP = '**/api/v1/me/mfa/setup';
const SETUP_PATH = '/api/v1/me/mfa/setup';
const VERIFY = '**/api/v1/me/mfa/verify';
const REAUTH = '**/api/v1/auth/reauth';
const REAUTH_PATH = '/api/v1/auth/reauth';
const REGENERATE = '**/api/v1/me/mfa/recovery-codes';
const REGENERATE_PATH = '/api/v1/me/mfa/recovery-codes';
const MFA_SETUP_URL = '/account/security/mfa-setup';

type Reply = { status: number; body: unknown; headers?: Record<string, string> };

function queue(replies: Reply[]): (route: Route) => Promise<void> {
  let index = 0;
  return (route) => {
    const reply = replies[Math.min(index, replies.length - 1)] as Reply;
    index += 1;
    return fulfilJson(route, reply.status, reply.body, reply.headers);
  };
}

const SETUP_OK: Reply = { status: 200, body: { secret: STUB.secret, otpauthUri: STUB.otpauthUri } };
const REAUTH_REQUIRED: Reply = { status: 401, body: problem(401, 'reauth_required') };
const REAUTH_OK: Reply = { status: 200, body: { reauthenticatedAt: new Date().toISOString() } };

/** A reload restores the full session through the stubbed boot refresh. */
async function stubSession(page: Page): Promise<void> {
  await page.route('**/api/v1/hubs/ticket', (route) => route.abort('failed'));
  await page.route('**/api/v1/auth/refresh', (route) => fulfilJson(route, 200, loginOkBody()));
}

async function openEnrolment(page: Page): Promise<void> {
  await page.goto(MFA_SETUP_URL);
}

async function fillReauth(page: Page, password: string, code = '123456'): Promise<void> {
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(LABEL.password).fill(password);
  const codeField = dialog.getByLabel(LABEL.authCode);
  if (await codeField.isVisible()) {
    await codeField.fill(code);
  }
  await dialog.getByRole('button', { name: LABEL.confirm }).click();
}

function pathOf(page: Page): string {
  return new URL(page.url()).pathname;
}

test.describe.serial('re-auth dialog, stubbed API', () => {
  test('AC8 reauth_required on setup opens ONE dialog; a successful re-auth retries setup exactly once', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED, SETUP_OK]));
    await page.route(REAUTH, queue([REAUTH_OK]));
    const setup = trackPosts(page, SETUP_PATH);
    const reauth = trackPosts(page, REAUTH_PATH);
    const consoleLines = trackConsole(page);

    await openEnrolment(page);
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('heading', { name: LABEL.reauthTitle }).or(dialog.getByText(LABEL.reauthTitle))).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(dialog.getByLabel(LABEL.password)).toHaveAttribute('autocomplete', 'current-password');
    expect(reauth, 'no POST before the owner submits').toHaveLength(0);
    expect(setup).toHaveLength(1);

    await fillReauth(page, 'correct-horse-Aa1!');
    await expect(dialog).toHaveCount(0);
    await expect(page.getByLabel(LABEL.manualKey)).toBeVisible();
    expect(reauth).toHaveLength(1);
    expect(reauth[0]?.body).toEqual({ password: 'correct-horse-Aa1!', code: '123456' });
    expect(setup, 'the original call is retried exactly once').toHaveLength(2);
    await expectNoSecretLeak(page, [STUB.secret, STUB.otpauthUri, 'correct-horse-Aa1!'], consoleLines);
  });

  test('AC8 a wrong password shows a field error and keeps the dialog open; setup is not retried', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED, SETUP_OK]));
    await page.route(REAUTH, queue([{ status: 401, body: problem(401, 'invalid_credentials') }, REAUTH_OK]));
    const setup = trackPosts(page, SETUP_PATH);
    const reauth = trackPosts(page, REAUTH_PATH);

    await openEnrolment(page);
    await fillReauth(page, 'wrong-password-1');
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('alert').or(dialog.locator('[aria-invalid="true"]')).first()).toBeVisible();
    expect(reauth).toHaveLength(1);
    expect(setup, 'no retry before a successful re-auth').toHaveLength(1);

    await fillReauth(page, 'correct-horse-Aa1!');
    await expect(dialog).toHaveCount(0);
    expect(reauth).toHaveLength(2);
    expect(setup).toHaveLength(2);
  });

  test('AC8 Escape closes without a POST, leaves a retryable re-auth-required state, and a retry opens the dialog again', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED, REAUTH_REQUIRED, SETUP_OK]));
    await page.route(REAUTH, queue([REAUTH_OK]));
    const setup = trackPosts(page, SETUP_PATH);
    const reauth = trackPosts(page, REAUTH_PATH);

    await openEnrolment(page);
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(reauth, 'Escape sends no POST').toHaveLength(0);
    expect(setup).toHaveLength(1);

    // Never a silent failure: a re-auth-required state with a retry button.
    await expect(page.getByText(LABEL.enrolReauthRequired)).toBeVisible();
    const retry = page.getByRole('button', { name: LABEL.tryAgain });
    await expect(retry).toBeVisible();
    await retry.click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await fillReauth(page, 'correct-horse-Aa1!');
    await expect(page.getByLabel(LABEL.manualKey)).toBeVisible();
    expect(reauth).toHaveLength(1);
  });

  test('AC8 the Cancel button closes without a POST', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED]));
    await page.route(REAUTH, queue([REAUTH_OK]));
    const reauth = trackPosts(page, REAUTH_PATH);

    await openEnrolment(page);
    await page.getByRole('dialog').getByRole('button', { name: LABEL.cancel }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(reauth).toHaveLength(0);
    await expect(page.getByText(LABEL.enrolReauthRequired)).toBeVisible();
  });

  test('AC9 429 on re-auth shows a live countdown and disables the action', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED]));
    await page.route(REAUTH, queue([{ status: 429, body: problem(429, 'rate_limited'), headers: { 'Retry-After': '900' } }]));

    await openEnrolment(page);
    await fillReauth(page, 'correct-horse-Aa1!');
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText(LABEL.countdown).first()).toBeVisible();
    await expect(dialog.getByRole('button', { name: LABEL.confirm })).toBeDisabled();
    await expect(dialog).toBeVisible();
  });

  test('AC9 session_revoked on re-auth (5th wrong attempt) closes the dialog and lands on /login', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED]));
    await page.route(REAUTH, queue([{ status: 401, body: problem(401, 'session_revoked') }]));

    await openEnrolment(page);
    await fillReauth(page, 'wrong-password-5');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.waitForURL((url) => url.pathname === '/login');
    expect(pathOf(page)).toBe('/login');
  });

  test('AC7 regenerate opens the dialog BEFORE any request; cancel leaves the old codes and issues no request', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([SETUP_OK]));
    await page.route(VERIFY, queue([{ status: 200, body: { recoveryCodes: [...STUB.recoveryCodes] } }]));
    await page.route(REAUTH, queue([REAUTH_OK]));
    await page.route(REGENERATE, queue([{ status: 200, body: { recoveryCodes: [...STUB.newRecoveryCodes] } }]));
    const regenerate = trackPosts(page, REGENERATE_PATH);
    const reauth = trackPosts(page, REAUTH_PATH);

    await openEnrolment(page);
    await page.getByLabel(LABEL.sixDigitCode).fill('123456');
    await page.getByRole('button', { name: LABEL.confirmCode }).click();
    await expect(page.getByText(LABEL.codesShownOnce)).toBeVisible();

    await page.getByRole('button', { name: LABEL.newRecoveryCodes }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    expect(regenerate, 're-auth comes first, then the call').toHaveLength(0);

    await dialog.getByRole('button', { name: LABEL.cancel }).click();
    await expect(dialog).toHaveCount(0);
    expect(regenerate).toHaveLength(0);
    expect(reauth).toHaveLength(0);
    for (const code of STUB.recoveryCodes) {
      await expect(page.getByText(code, { exact: true })).toBeVisible();
    }

    // Successful re-auth: the 10 codes change.
    await page.getByRole('button', { name: LABEL.newRecoveryCodes }).click();
    await fillReauth(page, 'correct-horse-Aa1!');
    await expect(dialog).toHaveCount(0);
    for (const code of STUB.newRecoveryCodes) {
      await expect(page.getByText(code, { exact: true })).toBeVisible();
    }
    for (const code of STUB.recoveryCodes) {
      await expect(page.getByText(code, { exact: true })).toHaveCount(0);
    }
    expect(regenerate).toHaveLength(1);
    expect(reauth).toHaveLength(1);
  });

  test('AC8 focus returns to the triggering control after the dialog closes', async ({ page }) => {
    await stubSession(page);
    await page.route(SETUP, queue([SETUP_OK]));
    await page.route(VERIFY, queue([{ status: 200, body: { recoveryCodes: [...STUB.recoveryCodes] } }]));
    await openEnrolment(page);
    await page.getByLabel(LABEL.sixDigitCode).fill('123456');
    await page.getByRole('button', { name: LABEL.confirmCode }).click();
    const trigger = page.getByRole('button', { name: LABEL.newRecoveryCodes });
    await trigger.click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByRole('dialog').getByLabel(LABEL.password)).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test('AC11 the open dialog is axe-clean (default and error state) with no CSP violation', async ({ page }) => {
    // D7: the dialog (and the CDK backdrop, 400 ms) fade in; axe computes contrast from the rendered colours, so a
    // scan taken mid-fade reports colour-contrast failures at the interpolated opacity. The app honours
    // `prefers-reduced-motion` (src/styles.scss), and `waitForOverlayAnimationEnd` waits for the settled, fully
    // opaque rendering a real user sees. The contrast rule stays ON.
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await stubSession(page);
    await page.route(SETUP, queue([REAUTH_REQUIRED]));
    await page.route(REAUTH, queue([{ status: 401, body: problem(401, 'invalid_credentials') }]));

    await openEnrolment(page);
    await expect(page.getByRole('dialog')).toBeVisible();
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);
    await fillReauth(page, 'wrong-password-1');
    await expect(page.getByRole('dialog').getByRole('alert').or(page.getByRole('dialog').locator('[aria-invalid="true"]')).first()).toBeVisible();
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);
  });
});

test.describe('real stack', () => {
  test.skip(process.env['NTS_E2E_STACK'] !== '1', 'needs the compose/native stack with a seeded owner; set NTS_E2E_STACK=1 (run by 11.5.1-T1 e2e.yml)');
  test.skip((process.env['NTS_E2E_OWNER_EMAIL'] ?? '') === '', 'NTS_E2E_OWNER_EMAIL is not set');

  test('AC7/AC8 a signed-in owner opening enrolment is asked to re-authenticate; Escape closes without a POST', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await loginAsOwner(page);
    const reauth = trackPosts(REAUTH_PATH);
    // SPA navigation keeps the in-memory session (a full reload would depend on the refresh cookie).
    await page.evaluate((target) => {
      window.history.pushState({}, '', target);
      window.dispatchEvent(new PopStateEvent('popstate'));
    }, MFA_SETUP_URL);
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    expect(reauth).toHaveLength(0);
  });
});
