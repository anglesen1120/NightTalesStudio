import { expectA11yClean } from '../fixtures/axe';
import { expect, test } from '../fixtures/csp.fixture';
import { CSP_ROUTES } from './routes';

// The header assertions need nginx: `ng serve` sends no D4 headers (documented in 10.1.6-T1). Run with
// NTS_E2E_TARGET=container (e2e.yml does).
const CONTAINER = process.env['NTS_E2E_TARGET'] === 'container';
test.skip(!CONTAINER, 'dev server sends no CSP; run with NTS_E2E_TARGET=container');

/** What the container was started with (`NTS_CSP_MEDIA_ORIGINS`), space separated; empty by default (WR13). */
const MEDIA_ORIGINS = (process.env['NTS_E2E_MEDIA_ORIGINS'] ?? '').trim();

const REQUIRED_DIRECTIVES = [
  "default-src 'none'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
  "connect-src 'self'",
  "form-action 'self'",
  "require-trusted-types-for 'script'",
  'trusted-types angular angular#bundler nts-sanitizer',
];

const D4_HEADERS: Record<string, string> = {
  'referrer-policy': 'no-referrer',
  'x-content-type-options': 'nosniff',
  'permissions-policy': 'camera=(), microphone=(), geolocation=(), payment=()',
  'cross-origin-opener-policy': 'same-origin',
};

function directive(policy: string, name: string): string | undefined {
  return policy
    .split(';')
    .map((part) => part.trim())
    .find((part) => part === name || part.startsWith(`${name} `));
}

for (const route of CSP_ROUTES) {
  test(`CSP header, hash coverage, inherited headers and axe on ${route}`, async ({ page, request }) => {
    const response = await request.get(route);
    expect(response.status()).toBe(200);
    const headers = response.headers();
    const policy = headers['content-security-policy'];
    expect(policy, 'Content-Security-Policy header is missing').toBeTruthy();

    for (const required of REQUIRED_DIRECTIVES) {
      expect(policy, required).toContain(required);
    }
    const scriptSrc = directive(policy ?? '', 'script-src') ?? '';
    expect(scriptSrc).toMatch(/^script-src 'self'( 'sha256-[A-Za-z0-9+/=]+')*$/);
    expect(policy).not.toContain('unsafe-eval');
    expect(scriptSrc).not.toContain('unsafe-inline');

    // The container's envsubst must have filled the placeholder; an unset env would silently drop every origin.
    expect(policy).not.toContain('${');
    expect(policy).not.toContain('NTS_CSP_MEDIA_ORIGINS');
    const expectedImg = ["'self'", 'data:', ...(MEDIA_ORIGINS === '' ? [] : MEDIA_ORIGINS.split(/\s+/))].join(' ');
    expect(directive(policy ?? '', 'img-src')).toBe(`img-src ${expectedImg}`);
    const expectedMedia = ["'self'", ...(MEDIA_ORIGINS === '' ? [] : MEDIA_ORIGINS.split(/\s+/))].join(' ');
    expect(directive(policy ?? '', 'media-src')).toBe(`media-src ${expectedMedia}`);

    for (const [name, value] of Object.entries(D4_HEADERS)) {
      expect(headers[name], `${route} ${name}`).toBe(value);
    }

    // Every executable inline <script> of the served document is covered by a hash in the header.
    await page.goto(route);
    await page.waitForLoadState('networkidle');
    const inlineScripts = await page.evaluate(
      () => document.querySelectorAll('script:not([src]):not([type="application/json"])').length,
    );
    const hashCount = (scriptSrc.match(/'sha256-/g) ?? []).length;
    expect(inlineScripts, 'executable inline <script> count vs sha256 hashes in the header').toBe(hashCount);

    await expectA11yClean(page);
  });
}

test('a hashed asset carries the four D4 headers and no CSP', async ({ request }) => {
  const document = await (await request.get('/')).text();
  const chunk = /<script[^>]+src="([^"]+\.js)"/.exec(document)?.[1];
  expect(chunk, 'no <script src> in the document').toBeTruthy();
  const response = await request.get(chunk ?? '/');
  expect(response.status()).toBe(200);
  for (const [name, value] of Object.entries(D4_HEADERS)) {
    expect(response.headers()[name], `${chunk} ${name}`).toBe(value);
  }
  expect(response.headers()['content-security-policy']).toBeUndefined();
});
