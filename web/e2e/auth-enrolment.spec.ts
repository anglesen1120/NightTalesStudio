import { readFileSync } from 'node:fs';
import {
  AUTH_SPEC_USE,
  LABEL,
  STUB,
  expectNoSecretLeak,
  expectNotInPageText,
  fulfilJson,
  problem,
  quietBackground,
  trackConsole,
  trackPosts,
} from './fixtures/auth';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import QRCode from 'qrcode';
import type { Page } from '@playwright/test';

// 10.3.3-T3 (plan.md §4 U4-U6, §8). Owner enrolment at /account/security/mfa-setup, driven by the enrolment-only token
// that `POST /auth/login` hands out as `401 mfa_enrolment_required { token }`. Everything is stubbed with `page.route`
// (no backend, no seeded account); the real-stack block only runs with NTS_E2E_STACK=1.
// Acceptance checks covered: AC4 (redirect, enrolment token, no re-auth dialog), AC5 (QR / manual key / wrong code /
// 10 codes once), AC6 (download filename), AC10 (no secret anywhere), AC11 (axe on all three steps + CSP), failure rows
// (token expiry 404, 409 reload, QR keeps the manual key). Regeneration behind re-auth lives in auth-reauth.spec.ts.
// The QR options below MUST mirror `src/app/features/auth/ui/mfa-qr/mfa-qr.component.ts` (margin 2, width 208,
// colour dark #202523ff / light #ffffffff): e2e must not import app source, so they are duplicated by hand.

test.use(AUTH_SPEC_USE);
test.use({ locale: 'vi-VN' });

const LOGIN = '**/api/v1/auth/login';
const SETUP = '**/api/v1/me/mfa/setup';
const SETUP_PATH = '/api/v1/me/mfa/setup';
const VERIFY = '**/api/v1/me/mfa/verify';
const VERIFY_PATH = '/api/v1/me/mfa/verify';
const SECRETS = [STUB.enrolmentToken, STUB.secret, STUB.otpauthUri, ...STUB.recoveryCodes];

type Reply = { status: number; body: unknown };

async function stubEnrolment(page: Page, { setup, verify }: { setup?: Reply[]; verify?: Reply[] } = {}): Promise<void> {
  await quietBackground(page);
  await page.route(LOGIN, (route) => fulfilJson(route, 401, problem(401, 'mfa_enrolment_required', { token: STUB.enrolmentToken })));
  const setupReplies = setup ?? [{ status: 200, body: { secret: STUB.secret, otpauthUri: STUB.otpauthUri } }];
  const verifyReplies = verify ?? [{ status: 200, body: { recoveryCodes: [...STUB.recoveryCodes] } }];
  let setupIndex = 0;
  let verifyIndex = 0;
  await page.route(SETUP, (route) => {
    const reply = setupReplies[Math.min(setupIndex, setupReplies.length - 1)] as Reply;
    setupIndex += 1;
    return fulfilJson(route, reply.status, reply.body);
  });
  await page.route(VERIFY, (route) => {
    const reply = verifyReplies[Math.min(verifyIndex, verifyReplies.length - 1)] as Reply;
    verifyIndex += 1;
    return fulfilJson(route, reply.status, reply.body);
  });
}

/** Owner without TOTP signs in: `/login` hands over to `/account/security/mfa-setup`. */
async function reachEnrolment(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel(LABEL.email).fill('owner@example.test');
  await page.getByLabel(LABEL.password).fill('correct-horse-Aa1!');
  await page.getByRole('button', { name: LABEL.signIn }).click();
  await page.waitForURL((url) => url.pathname === '/account/security/mfa-setup');
  await expect(page.getByLabel(LABEL.manualKey)).toBeVisible();
}

async function confirmCode(page: Page, code: string): Promise<void> {
  await page.getByLabel(LABEL.sixDigitCode).fill(code);
  await page.getByRole('button', { name: LABEL.confirmCode }).click();
}

function expectedQrDataUrl(uri: string): Promise<string> {
  // Mirrors `mfa-qr.component.ts` (margin 2, width 208, explicit colours); keep the two in step.
  return QRCode.toDataURL(uri, {
    margin: 2,
    width: 208,
    color: { dark: '#202523ff', light: '#ffffffff' },
  });
}

function pathOf(page: Page): string {
  return new URL(page.url()).pathname;
}

test.describe.serial('owner enrolment, stubbed API', () => {
  test('AC4/AC5 redirect to mfa-setup, enrolment token on setup, no re-auth dialog, QR + manual key match the otpauth URI', async ({ page }) => {
    await stubEnrolment(page);
    const setup = trackPosts(page, SETUP_PATH);

    await reachEnrolment(page);
    expect(setup, 'one POST per step').toHaveLength(1);
    expect(setup[0]?.authorization, 'the enrolment-only token is presented as a bearer').toBe(`Bearer ${STUB.enrolmentToken}`);
    await expect(page.getByRole('dialog'), 'WT41: no re-auth dialog for setup/verify with the enrolment token').toHaveCount(0);
    await expect(page.getByRole('heading', { name: LABEL.enrolTitle })).toBeVisible();

    // Manual key == `secret` of the otpauth URI.
    const manual = (await page.getByLabel(LABEL.manualKey).inputValue()).replace(/\s+/g, '');
    expect(manual).toBe(STUB.secret);
    expect(new URL(STUB.otpauthUri).searchParams.get('secret')).toBe(manual);
    await expect(page.getByRole('button', { name: LABEL.copySetupKey })).toBeVisible();

    // QR: an <img> with a text alternative whose data: URL is the PNG of the stubbed otpauth URI.
    const qr = page.getByRole('img', { name: LABEL.qrAlt });
    await qr.scrollIntoViewIfNeeded();
    await expect(qr).toBeVisible();
    await expect(qr).toHaveAttribute('src', /^data:image\/png;base64,/);
    const src = await qr.getAttribute('src');
    expect(src, 'the QR encodes exactly the stubbed otpauth URI').toBe(await expectedQrDataUrl(STUB.otpauthUri));
  });

  test('AC5 a wrong code keeps the page with a retryable error; a correct code reveals exactly 10 codes with the shown-once warning', async ({ page }) => {
    await stubEnrolment(page, {
      verify: [
        { status: 401, body: problem(401, 'invalid_mfa_code') },
        { status: 200, body: { recoveryCodes: [...STUB.recoveryCodes] } },
      ],
    });
    const verify = trackPosts(page, VERIFY_PATH);

    await reachEnrolment(page);
    await confirmCode(page, '000000');
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page.getByLabel(LABEL.manualKey)).toBeVisible();
    expect(pathOf(page)).toBe('/account/security/mfa-setup');
    expect(verify).toHaveLength(1);
    expect(verify[0]?.body).toEqual({ code: '000000' });

    await confirmCode(page, '123456');
    await expect(page.getByText(LABEL.codesShownOnce)).toBeVisible();
    await expect(page.getByRole('alert').filter({ hasText: LABEL.codesShownOnce })).toBeVisible();
    const items = page.getByRole('listitem').filter({ has: page.locator('code') });
    await expect(items).toHaveCount(10);
    for (const code of STUB.recoveryCodes) {
      await expect(page.getByText(code, { exact: true })).toBeVisible();
    }
    await expect(page.getByRole('button', { name: LABEL.copyCode })).toHaveCount(10);
    expect(verify).toHaveLength(2);
    expect(verify[1]?.body).toEqual({ code: '123456' });
  });

  test('AC5/AC6 confirmSaved gates Continue, the download is recovery-codes-YYYYMMDD.txt with a safe name, and the codes are gone after Continue', async ({ page }) => {
    await stubEnrolment(page);
    const consoleLines = trackConsole(page);
    await reachEnrolment(page);
    await confirmCode(page, '123456');
    await expect(page.getByText(LABEL.codesShownOnce)).toBeVisible();

    const next = page.getByRole('button', { name: LABEL.continue });
    await expect(next).toBeDisabled();

    const downloadEvent = page.waitForEvent('download');
    await page.getByRole('button', { name: LABEL.downloadRecoveryCodes }).click();
    const download = await downloadEvent;
    const name = download.suggestedFilename();
    expect(name).toMatch(/^recovery-codes-\d{8}\.txt$/);
    expect(name.length).toBeLessThanOrEqual(100);
    // eslint-disable-next-line no-control-regex
    expect(name, 'no quotes, slashes or control characters').not.toMatch(/["'/\\\u0000-\u001f\u007f]/);
    const downloaded = readFileSync(await download.path(), 'utf8');
    for (const code of STUB.recoveryCodes) {
      expect(downloaded).toContain(code);
    }

    await page.getByLabel(LABEL.savedRecoveryCodes).check();
    await expect(next).toBeEnabled();
    await next.click();

    // No second reveal: the list, the copy buttons and the download are gone; the done state is shown.
    await expect(page.getByText(LABEL.enrolDone)).toBeVisible();
    await expect(page.getByRole('button', { name: LABEL.copyCode })).toHaveCount(0);
    await expect(page.getByRole('button', { name: LABEL.downloadRecoveryCodes })).toHaveCount(0);
    await expectNotInPageText(page, [...STUB.recoveryCodes, STUB.secret]);
    await expect(page.getByRole('link', { name: LABEL.signInAgain }).or(page.getByRole('button', { name: LABEL.signInAgain }))).toBeVisible();
    await expectNoSecretLeak(page, SECRETS, consoleLines);
    await page.reload();
    await expectNotInPageText(page, [...STUB.recoveryCodes, STUB.secret]);
  });

  test('AC10 no token, secret, URI or recovery code reaches the URL, storage, DOM attributes or console at any step', async ({ page }) => {
    await stubEnrolment(page);
    const consoleLines = trackConsole(page);

    await reachEnrolment(page);
    await expectNoSecretLeak(page, SECRETS, consoleLines);
    await confirmCode(page, '123456');
    await expect(page.getByText(LABEL.codesShownOnce)).toBeVisible();
    await expectNoSecretLeak(page, SECRETS, consoleLines);
    await page.getByLabel(LABEL.savedRecoveryCodes).check();
    await page.getByRole('button', { name: LABEL.continue }).click();
    await expect(page.getByText(LABEL.enrolDone)).toBeVisible();
    await expectNoSecretLeak(page, SECRETS, consoleLines);
  });

  test('failure: an expired enrolment token (404 not_found) shows the re-login state and sends no retry', async ({ page }) => {
    await stubEnrolment(page, { setup: [{ status: 404, body: problem(404, 'not_found') }] });
    const setup = trackPosts(page, SETUP_PATH);

    await page.goto('/login');
    await page.getByLabel(LABEL.email).fill('owner@example.test');
    await page.getByLabel(LABEL.password).fill('correct-horse-Aa1!');
    await page.getByRole('button', { name: LABEL.signIn }).click();
    await expect(page.getByText(LABEL.enrolExpired)).toBeVisible();
    expect(setup, 'no retry loop').toHaveLength(1);
    await page.getByRole('link', { name: LABEL.signInAgain }).or(page.getByRole('button', { name: LABEL.signInAgain })).click();
    await page.waitForURL((url) => url.pathname === '/login');
    await expect(page.getByLabel(LABEL.password)).toBeVisible();
  });

  test('failure: 409 concurrency_conflict on setup reloads once and keeps the manual key and QR of the new response', async ({ page }) => {
    await stubEnrolment(page, {
      setup: [
        { status: 409, body: problem(409, 'concurrency_conflict') },
        { status: 200, body: { secret: STUB.secret, otpauthUri: STUB.otpauthUri } },
      ],
    });
    const setup = trackPosts(page, SETUP_PATH);

    await page.goto('/login');
    await page.getByLabel(LABEL.email).fill('owner@example.test');
    await page.getByLabel(LABEL.password).fill('correct-horse-Aa1!');
    await page.getByRole('button', { name: LABEL.signIn }).click();
    await expect(page.getByLabel(LABEL.manualKey)).toBeVisible();
    expect(setup, 'one automatic retry, no loop').toHaveLength(2);
    expect((await page.getByLabel(LABEL.manualKey).inputValue()).replace(/\s+/g, '')).toBe(STUB.secret);
  });

  test('AC11 all three steps (show, codes, done) are axe-clean, keyboard-completable, with no CSP violation', async ({ page }) => {
    await stubEnrolment(page);
    await reachEnrolment(page);
    await page.getByRole('img', { name: LABEL.qrAlt }).scrollIntoViewIfNeeded();
    await expectA11yClean(page);

    // Keyboard-only: the code field takes the code and Enter submits.
    await page.getByLabel(LABEL.sixDigitCode).fill('123456');
    await page.getByLabel(LABEL.sixDigitCode).press('Enter');
    await expect(page.getByText(LABEL.codesShownOnce)).toBeVisible();
    await expectA11yClean(page);

    await page.getByLabel(LABEL.savedRecoveryCodes).focus();
    await page.keyboard.press('Space');
    await expect(page.getByLabel(LABEL.savedRecoveryCodes)).toBeChecked();
    await page.getByRole('button', { name: LABEL.continue }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByText(LABEL.enrolDone)).toBeVisible();
    await expectA11yClean(page);
  });

  test('AC11 360 px: the enrolment page has no horizontal scroll', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 780 });
    await stubEnrolment(page);
    await reachEnrolment(page);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
});

test.describe('real stack', () => {
  test.skip(process.env['NTS_E2E_STACK'] !== '1', 'needs the compose/native stack with a seeded owner; set NTS_E2E_STACK=1 (run by 11.5.1-T1 e2e.yml)');
  test.skip((process.env['NTS_E2E_OWNER_EMAIL'] ?? '') === '', 'NTS_E2E_OWNER_EMAIL is not set');

  test('a seeded owner without TOTP is redirected from /login to the enrolment page; the key and QR render without a re-auth dialog', async ({ page }) => {
    await page.goto('/login');
    await page.getByLabel(LABEL.email).fill(process.env['NTS_E2E_OWNER_EMAIL'] ?? '');
    await page.getByLabel(LABEL.password).fill(process.env['NTS_E2E_OWNER_PASSWORD'] ?? '');
    await page.getByRole('button', { name: LABEL.signIn }).click();
    const manual = page.getByLabel(LABEL.manualKey);
    test.skip(!(await manual.isVisible({ timeout: 10_000 }).catch(() => false)), 'owner already has TOTP (enrolment ran in loginAsOwner)');
    expect(pathOf(page)).toBe('/account/security/mfa-setup');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('img', { name: LABEL.qrAlt })).toHaveAttribute('src', /^data:image\/png;base64,/);
    await expectA11yClean(page);
  });
});
