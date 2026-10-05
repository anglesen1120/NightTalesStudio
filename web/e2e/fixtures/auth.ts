import { createHmac } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { expect, type Locator, type Page, type Route } from '@playwright/test';

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

export const AUTH_DIR = resolve(import.meta.dirname, '..', '.auth');
const SECRET_FILE = join(AUTH_DIR, 'owner-totp.json');

const STEP_SECONDS = 30;

// Copy of the approved design frames (design/ui/screens/01-shell-auth.html), vi and en, so both locales
// work. The screens ship with 10.3.3-T1/T3; until then these selectors are bound to the approved copy.
// Exported for the 10.3.3-T3 specs (auth-mfa / auth-enrolment / auth-reauth) so every spec shares one binding.
export const LABEL = {
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
  // 10.3.3-T3: MFA-step mode toggles (f-login-mfa, f-login-mfa-recovery).
  useRecovery: /^(Dùng mã khôi phục|Use a recovery code)$/,
  useTotp: /^(Dùng ứng dụng xác thực|Use authenticator app)$/,
  back: /^(Quay lại|Back)$/,
  recoveryCode: /^(Mã khôi phục|Recovery code)$/,
  // 10.3.3-T3: ReauthDialog actions (f-reauth); scope to `getByRole('dialog')`.
  confirm: /^(Xác nhận|Confirm)$/,
  cancel: /^(Huỷ|Cancel)$/,
  // 10.3.3-T3: enrolment page (f-enrol-show, f-enrol-codes, f-enrol-gate, f-enrol-error, f-enrol-done).
  copySetupKey: /^(Sao chép khoá thiết lập|Copy setup key)$/,
  copyCode: /^(Sao chép mã \d+|Copy code \d+)$/,
  downloadRecoveryCodes: /^(Tải tệp mã khôi phục|Download recovery codes)$/,
  newRecoveryCodes: /^(Tạo mã khôi phục mới|Create new recovery codes)$/,
  tryAgain: /^(Thử lại|Try again)$/,
  goToDashboard: /^(Về bảng điều khiển|Go to the dashboard)$/,
  // 10.3.3-T3: frame headings / inline states (f-login-mfa-expired, f-login-mfa-locked, f-enrol-show, f-enrol-codes,
  // f-enrol-reauth, f-enrol-done, f-enrol-error, f-reauth).
  challengeExpired: /(Mã xác thực đã hết hạn|The authentication challenge expired)/,
  mfaLocked: /(Tạm khoá xác thực trên thiết bị này|Verification is locked on this device)/,
  enrolTitle: /^(Bật xác thực hai lớp|Turn on two-step verification)$/,
  codesTitle: /^(Mã khôi phục|Recovery codes)$/,
  codesShownOnce: /(Các mã này chỉ hiện một lần|These codes appear once)/,
  enrolReauthRequired: /(Cần xác nhận lại mật khẩu|Confirm your password again)/,
  enrolDone: /(Đã bật xác thực hai lớp|Two-step verification is on)/,
  enrolExpired: /(Phiên ghi danh đã hết hạn|The enrolment session expired)/,
  reauthTitle: /^(Xác nhận lại mật khẩu|Confirm your password)$/,
  wrongCode: /(Mã không đúng|That code is not right)/,
  qrAlt: /(Mã QR để thêm Night Tales Studio vào ứng dụng xác thực|QR code)/i,
  countdown: /(\d+)\s*(giây|seconds?)/i,
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

export function loadSecret(): string {
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

// ---------------------------------------------------------------------------------------------------------------
// 10.3.3-T3 shared stub helpers (auth-mfa / auth-enrolment / auth-reauth). Only `page.route` / `page.clock`; no hooks.
// ---------------------------------------------------------------------------------------------------------------

/** Stubbed secrets used by the specs; never real credentials. */
export const STUB = {
  mfaToken: 'stub-mfa-token-7f3a91c2d4e85b60',
  enrolmentToken: 'stub-enrol-token-1b2c3d4e5f607182',
  secret: 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP',
  otpauthUri: 'otpauth://totp/NightTales:owner@example.test?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=NightTales',
  recoveryCodes: [
    '4K7P-9QX2-M3TD',
    '7NPL-2VC8-K9FA',
    'M8QW-5DTR-1J6H',
    '3ZKC-8RPA-5VTX',
    '9HME-4LQ7-2BWN',
    '6TYF-3JXA-8CQR',
    '2PVD-7NKM-4EGS',
    '5RXA-1TLC-9QHU',
    '8BWJ-6MFP-3KZD',
    '1CGN-5VTR-7YQA',
  ],
  newRecoveryCodes: [
    'AA11-BB22-CC33',
    'DD44-EE55-FF66',
    'GG77-HH88-JJ99',
    'KK12-LL34-MM56',
    'NN78-PP90-QQ12',
    'RR34-SS56-TT78',
    'UU90-VV12-WW34',
    'XX56-YY78-ZZ90',
    'AB12-CD34-EF56',
    'GH78-JK90-LM12',
  ],
} as const;

/** Unsigned dummy JWT: the SPA reads `role`/`amr`/`sub` from the middle segment only. */
export function dummyJwt(amr: string[] = ['pwd', 'mfa']): string {
  const exp = Math.floor(Date.now() / 1000) + 900;
  const [header, payload] = [{ alg: 'none', typ: 'JWT' }, { sub: 'u-owner', role: 'owner', amr, exp }].map((part) =>
    Buffer.from(JSON.stringify(part)).toString('base64url'),
  );
  return `${header}.${payload}.`;
}

export function loginOkBody(): Record<string, unknown> {
  return {
    accessToken: dummyJwt(),
    expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
    role: 'owner',
    amr: ['pwd', 'mfa'],
  };
}

export function problem(status: number, code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, status, title: code, traceId: 'NTS-E2E', ...extra };
}

export async function fulfilJson(
  route: Route,
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(body) });
}

/** Background calls after a stubbed sign-in must not reach a missing backend or end the session. */
export async function quietBackground(page: Page): Promise<void> {
  await page.route('**/api/v1/hubs/ticket', (route) => route.abort('failed'));
  await page.route('**/api/v1/auth/refresh', (route) => fulfilJson(route, 401, problem(401, 'session_revoked')));
}

export interface CapturedPost {
  readonly body: unknown;
  readonly authorization: string | undefined;
}

/** Records every POST to `apiPath` (request bodies are asserted on the capture, never on the UI). */
export function trackPosts(page: Page, apiPath: string): CapturedPost[] {
  const calls: CapturedPost[] = [];
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === apiPath) {
      calls.push({ body: request.postDataJSON() as unknown, authorization: request.headers()['authorization'] });
    }
  });
  return calls;
}

/** Collects console text so a spec can assert a secret never reached it. */
export function trackConsole(page: Page): string[] {
  const lines: string[] = [];
  page.on('console', (message) => lines.push(message.text()));
  page.on('pageerror', (error) => lines.push(error.message));
  return lines;
}

/** `code` XOR `recoveryCode`, never both and never empty (task AC2). `mfaToken` must be present. */
export function expectMfaBodyShape(body: unknown, mode: 'totp' | 'recovery'): void {
  expect(typeof body === 'object' && body !== null).toBe(true);
  const { mfaToken, code, recoveryCode } = body as Record<string, unknown>;
  expect(typeof mfaToken === 'string' && mfaToken !== '').toBe(true);
  const used = mode === 'totp' ? code : recoveryCode;
  const unused = mode === 'totp' ? recoveryCode : code;
  expect(typeof used === 'string' && used !== '', `${mode} value is a non-empty string`).toBe(true);
  expect(unused === null || unused === undefined, 'the other field is absent or null').toBe(true);
}

/**
 * None of `secrets` may appear in the URL, localStorage, sessionStorage, any DOM attribute, or the console lines.
 * (Page text is asserted separately where the value is meant to be visible and must disappear.)
 */
export async function expectNoSecretLeak(page: Page, secrets: readonly string[], consoleLines: readonly string[]): Promise<void> {
  expect(secrets.length).toBeGreaterThan(0);
  const url = page.url();
  const snapshot = await page.evaluate(() => {
    const attributes: string[] = [];
    for (const element of Array.from(document.querySelectorAll('*'))) {
      for (const attribute of Array.from(element.attributes)) {
        attributes.push(attribute.value);
      }
    }
    const dump = (storage: Storage): string[] => {
      const out: string[] = [];
      for (let index = 0; index < storage.length; index += 1) {
        const key = storage.key(index) ?? '';
        out.push(key, storage.getItem(key) ?? '');
      }
      return out;
    };
    return { attributes, local: dump(localStorage), session: dump(sessionStorage) };
  });
  const haystacks: Array<[string, string]> = [
    ['url', url],
    ['attributes', snapshot.attributes.join('\n')],
    ['localStorage', snapshot.local.join('\n')],
    ['sessionStorage', snapshot.session.join('\n')],
    ['console', consoleLines.join('\n')],
  ];
  for (const secret of secrets) {
    for (const [where, text] of haystacks) {
      expect(text.includes(secret), `a secret leaked into ${where}`).toBe(false);
    }
  }
}

/** The visible page text must not contain any of `values` (used after a reveal is dismissed). */
export async function expectNotInPageText(page: Page, values: readonly string[]): Promise<void> {
  const text = await page.locator('body').innerText();
  for (const value of values) {
    expect(text.includes(value), 'a one-time value is still rendered').toBe(false);
  }
}
