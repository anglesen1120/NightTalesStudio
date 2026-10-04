import { createHmac } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Owner authentication helper (README D15, WR14).
 *
 * - No storageState: the refresh cookie rotates, so replaying it in a second test trips reuse detection
 *   (README F08). Each spec file that needs the owner calls `loginAsOwner` once, through the UI.
 * - No in-app test hooks: only the UI and `page.route`/`page.clock` are used.
 * - The enrolled TOTP secret lives only in `e2e/.auth/owner-totp.json` (git-ignored, mode 0600) and is
 *   deleted by `global-teardown.ts` at the end of every run.
 */

/** `test.use(AUTH_SPEC_USE)` at the top of every spec that authenticates: no trace or video, even on failure. */
export const AUTH_SPEC_USE = { trace: 'off', video: 'off' } as const;

export const AUTH_DIR = resolve(__dirname, '..', '.auth');
const SECRET_FILE = join(AUTH_DIR, 'owner-totp.json');

const STEP_SECONDS = 30;

// Copy of the approved design frames (design/ui/screens/01-shell-auth.html), vi and en, so both locales
// work. The screens ship with 10.3.3-T1/T3; until then these selectors are bound to the approved copy.
const LABEL = {
  email: /^E-mail$/i,
  password: /^(Mật khẩu|Password)$/,
  signIn: /^(Đăng nhập|Sign in)$/,
  authCode: /^(Mã xác thực|Authentication code)$/,
  verify: /^(Xác nhận|Verify)$/,
  manualKey: /^(Khoá nhập tay|Manual key)$/,
  sixDigitCode: /^(Mã 6 số|6-digit code)$/,
  confirmCode: /^(Xác nhận mã|Confirm code)$/,
  savedRecoveryCodes: /^(Tôi đã lưu mã khôi phục|I saved my recovery codes)$/,
  continue: /^(Tiếp tục|Continue)$/,
  signInAgain: /^(Đăng nhập lại|Sign in again)$/,
} as const;

const BASE32_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

function base32Decode(input: string): Buffer {
  const bytes: number[] = [];
  let value = 0;
  let bits = 0;
  for (const character of input.replace(/[\s=-]/g, '').toUpperCase()) {
    const index = BASE32_ALPHABET.indexOf(character);
    if (index < 0) {
      throw new Error('TOTP secret is not valid base32');
    }
    value = (value << 5) | index;
    bits += 5;
    if (bits >= 8) {
      bytes.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(bytes);
}

/** RFC 6238 TOTP (HMAC-SHA1) of a base32 secret. `t` is a time in milliseconds. */
export function totp(
  base32Secret: string,
  { digits = 6, step = STEP_SECONDS, t = Date.now() }: { digits?: number; step?: number; t?: number } = {},
): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(t / 1000 / step)));
  const mac = createHmac('sha1', base32Decode(base32Secret)).update(counter).digest();
  const offset = mac[mac.length - 1] & 0x0f;
  const binary =
    ((mac[offset] & 0x7f) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
  return String(binary % 10 ** digits).padStart(digits, '0');
}

/** Milliseconds until the server sees the next 30 s step, plus 1.5 s of margin for its clock. */
export function msUntilNextStep(now = Date.now()): number {
  return 30_000 - (now % 30_000) + 1_500;
}

function requiredEnv(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} must be set to run a spec that signs in as the owner`);
  }
  return value;
}

function saveSecret(secret: string): void {
  mkdirSync(AUTH_DIR, { recursive: true, mode: 0o700 });
  writeFileSync(SECRET_FILE, JSON.stringify({ secret }), { mode: 0o600 });
}

function loadSecret(): string {
  if (!existsSync(SECRET_FILE)) {
    throw new Error(
      'the owner already has two-step verification but this run holds no secret (e2e/.auth is per run); ' +
        'start from an owner without TOTP, e.g. a fresh stack',
    );
  }
  const stored: unknown = JSON.parse(readFileSync(SECRET_FILE, 'utf8'));
  if (typeof stored === 'object' && stored !== null && 'secret' in stored && typeof stored.secret === 'string') {
    return stored.secret;
  }
  throw new Error('e2e/.auth/owner-totp.json is malformed; delete e2e/.auth and rerun');
}

interface CodeSubmission {
  field: Locator;
  button: Locator;
  apiPath: string;
  secret: string;
}

/**
 * Types a current code and submits it. A rejection with the API code `invalid_mfa_code` (a code reused within
 * its 30 s step, or one that crossed a boundary) waits for the next step and retries ONCE; any other
 * failure, or a second rejection, is a hard failure. Returns the time the accepted code was generated for.
 */
async function submitCode(page: Page, { field, button, apiPath, secret }: CodeSubmission): Promise<number> {
  for (const attempt of [1, 2]) {
    const generatedAt = Date.now();
    await field.fill(totp(secret, { t: generatedAt }));
    const responded = page.waitForResponse(
      (response) => response.request().method() === 'POST' && new URL(response.url()).pathname === apiPath,
    );
    await button.click();
    const response = await responded;
    if (response.ok()) {
      return generatedAt;
    }
    const body: unknown = await response.json().catch(() => null);
    const code =
      typeof body === 'object' && body !== null && 'code' in body && typeof body.code === 'string'
        ? body.code
        : undefined;
    if (attempt === 1 && code === 'invalid_mfa_code') {
      // The server validates against its own clock, so the next step is a real wall-clock wait
      // (page.clock cannot fake it, README D15).
      await sleep(msUntilNextStep());
      continue;
    }
    throw new Error(`POST ${apiPath} failed: HTTP ${response.status()} code=${code ?? 'none'}`);
  }
  throw new Error('unreachable');
}

async function submitCredentials(page: Page, email: string, password: string): Promise<void> {
  await page.goto('/login');
  await page.getByLabel(LABEL.email).fill(email);
  await page.getByLabel(LABEL.password).fill(password);
  await page.getByRole('button', { name: LABEL.signIn }).click();
}

/** First-run enrolment: reads the manual key, persists it, confirms a code, acknowledges the recovery codes. */
async function enrol(page: Page): Promise<number> {
  const secret = (await page.getByLabel(LABEL.manualKey).inputValue()).replace(/\s+/g, '');
  saveSecret(secret);
  const usedAt = await submitCode(page, {
    field: page.getByLabel(LABEL.sixDigitCode),
    button: page.getByRole('button', { name: LABEL.confirmCode }),
    apiPath: '/api/v1/me/mfa/verify',
    secret,
  });
  await page.getByLabel(LABEL.savedRecoveryCodes).check();
  await page.getByRole('button', { name: LABEL.continue }).click();
  return usedAt;
}

/**
 * Signs in as the owner through the UI (credentials from NTS_E2E_OWNER_EMAIL / NTS_E2E_OWNER_PASSWORD).
 * On the first run of a stack the owner has no TOTP yet: the helper enrols one, then signs in again with a
 * code from the next step (a code is single-use per step, README D15).
 */
export async function loginAsOwner(page: Page): Promise<void> {
  const email = requiredEnv('NTS_E2E_OWNER_EMAIL');
  const password = requiredEnv('NTS_E2E_OWNER_PASSWORD');
  const codeField = page.getByLabel(LABEL.authCode);
  const manualKey = page.getByLabel(LABEL.manualKey);

  await submitCredentials(page, email, password);
  await expect(codeField.or(manualKey)).toBeVisible();

  if (await manualKey.isVisible()) {
    const usedAt = await enrol(page);
    await page.getByRole('link', { name: LABEL.signInAgain }).or(page.getByRole('button', { name: LABEL.signInAgain })).click();
    await submitCredentials(page, email, password);
    await expect(codeField).toBeVisible();
    const sameStep = Math.floor(Date.now() / 30_000) === Math.floor(usedAt / 30_000);
    if (sameStep) {
      await sleep(msUntilNextStep());
    }
  }

  await submitCode(page, {
    field: codeField,
    button: page.getByRole('button', { name: LABEL.verify }),
    apiPath: '/api/v1/auth/login/mfa',
    secret: loadSecret(),
  });
  await page.waitForURL((url) => !url.pathname.startsWith('/login'));
}
