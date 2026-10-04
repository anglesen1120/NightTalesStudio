import { expect, test } from './fixtures/csp.fixture';
import { expectA11yClean, expectA11yHarnessDetects } from './fixtures/axe';

// This spec never signs in (`loginAsOwner` belongs to the specs of the screens that need the owner) and sets no
// `storageState`, so it keeps the default failure-only trace/video.

const TARGET = process.env['NTS_E2E_TARGET'] ?? 'dev';
if (TARGET !== 'dev' && TARGET !== 'container') {
  throw new Error(`NTS_E2E_TARGET must be "dev" or "container", got "${TARGET}"`);
}
const CONTAINER = TARGET === 'container';

// What a page that has only the document and its chunks may request (README D13/D17: no polling, no extra fetch).
// `font` is the self-hosted Be Vietnam Pro from 10.1.3-T1; every boot response must stay same-origin.
const BOOT_RESOURCE_TYPES = ['document', 'script', 'stylesheet', 'font'];

/** The D4 headers every web response carries except CSP, which only HTML responses carry (README D4, WR2). */
const D4_HEADERS: Record<string, string> = {
  'referrer-policy': 'no-referrer',
  'x-content-type-options': 'nosniff',
  'permissions-policy': 'camera=(), microphone=(), geolocation=(), payment=()',
  'cross-origin-opener-policy': 'same-origin',
};

test.describe('smoke: both targets', () => {
  test.beforeAll(() => {
    console.log(`smoke target: ${TARGET}`);
  });

  test('axe reports a known-bad page (the harness is not silently passing everything)', async ({ page }) => {
    await expectA11yHarnessDetects(page);
  });

  test('the app boots at the baseURL: axe clean, no console error, no failed or extra request', async ({ page }) => {
    const consoleErrors: string[] = [];
    const failedRequests: string[] = [];
    const responses: { url: string; status: number; type: string }[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        consoleErrors.push(message.text());
      }
    });
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    page.on('requestfailed', (request) => failedRequests.push(`${request.url()} ${request.failure()?.errorText}`));
    page.on('response', (response) =>
      responses.push({ url: response.url(), status: response.status(), type: response.request().resourceType() }),
    );

    await page.goto('/');
    await expect(page.locator('app-root')).toBeAttached();
    await page.waitForLoadState('networkidle');
    await expectA11yClean(page);

    expect(consoleErrors).toEqual([]);
    expect(failedRequests).toEqual([]);
    // Nothing beyond the document, its script/style chunks and the self-hosted fonts: guards accidental polling and early fetches.
    expect(responses.filter(({ type }) => !BOOT_RESOURCE_TYPES.includes(type))).toEqual([]);
    const pageOrigin = new URL(page.url()).origin;
    expect(responses.filter(({ url }) => !url.startsWith('data:') && new URL(url).origin !== pageOrigin)).toEqual([]);
    expect(responses.filter(({ status }) => status >= 400)).toEqual([]);
    expect(responses.some(({ type }) => type === 'document')).toBe(true);
  });

  test('five minutes of fake time issue no fetch/xhr request (no app timer polls)', async ({ page }) => {
    await page.clock.install();
    await page.goto('/');
    await expect(page.locator('app-root')).toBeAttached();
    await page.waitForLoadState('networkidle');

    const apiCalls: string[] = [];
    page.on('request', (request) => {
      if (['fetch', 'xhr'].includes(request.resourceType())) {
        apiCalls.push(`${request.method()} ${request.url()}`);
      }
    });
    await page.clock.fastForward('05:00');
    await page.waitForLoadState('networkidle');

    expect(apiCalls).toEqual([]);
  });

  // The ruling: Chromium must keep the `__Secure-` refresh cookie over plain http on localhost. The anonymous
  // refresh of a real API never sets a cookie (and this spec never signs in), so the browser side is proven
  // by fulfilling the refresh response with the cookie exactly as 09's `RefreshCookie` writes it
  // (`HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`). The probe is gated on the real API being up.
  test('accepted-cookie probe: __Secure-nts_rt survives POST /api/v1/auth/refresh over http://localhost', async ({
    page,
    context,
    baseURL,
  }) => {
    const origin = new URL(baseURL ?? 'http://localhost:4200').origin;
    const apiProbe = await page.request
      .post('/api/v1/auth/refresh', { headers: { Origin: origin }, failOnStatusCode: false })
      .catch((error: unknown) => error);
    const apiUp = !(apiProbe instanceof Error) && (apiProbe.headers()['content-type'] ?? '').includes('json');
    const skipReason = `dev API unreachable behind ${origin}/api (start it with 11.1.1-T1's compose or dotnet run); probe skipped`;
    if (!apiUp) {
      console.log(skipReason);
    }
    test.skip(!apiUp, skipReason);

    await page.goto('/');
    await page.route('**/api/v1/auth/refresh', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: {
          'set-cookie': '__Secure-nts_rt=probe; Path=/api/v1/auth; HttpOnly; Secure; SameSite=Strict; Max-Age=60',
        },
        body: '{}',
      }),
    );
    const status = await page.evaluate(
      async () => (await fetch('/api/v1/auth/refresh', { method: 'POST', credentials: 'include' })).status,
    );
    expect(status).toBe(200);

    // `cookies(url)` filters by path too: the cookie's Path is /api/v1/auth, so ask at that URL, not at the origin root.
    const cookies = await context.cookies(`${origin}/api/v1/auth/refresh`);
    expect(cookies.map(({ name }) => name)).toContain('__Secure-nts_rt');
    expect(cookies).toEqual([
      expect.objectContaining({ name: '__Secure-nts_rt', httpOnly: true, secure: true, sameSite: 'Strict' }),
    ]);
  });
});

test.describe('smoke: container target (nginx serves; WR2 header matrix)', () => {
  const containerSkipReason =
    'dev server sends no D4 headers and answers /healthz with the SPA fallback; run with NTS_E2E_TARGET=container';
  if (process.env.CI && !process.env.NTS_E2E_TARGET) {
    throw new Error('CI e2e job: NTS_E2E_TARGET is unset; container-only smoke tests would skip silently (QA D3)');
  }
  if (!CONTAINER) {
    console.log(`skipping container target tests: ${containerSkipReason}`);
  }
  test.skip(!CONTAINER, containerSkipReason);

  // These FAIL (never skip) when a header is missing: the only skip is the legal route below.
  test('D4 headers on / and on a deep client route that falls back', async ({ request }) => {
    for (const path of ['/', '/dashboard/episodes/42']) {
      const response = await request.get(path);
      expect(response.status(), path).toBe(200);
      const headers = response.headers();
      for (const [name, value] of Object.entries(D4_HEADERS)) {
        expect(headers[name], `${path} ${name}`).toBe(value);
      }
      expect(headers['content-security-policy'], `${path} content-security-policy`).toContain("default-src 'none'");
      expect(headers['content-security-policy'], `${path} content-security-policy`).toContain(
        "require-trusted-types-for 'script'",
      );
      expect(headers['cache-control'], `${path} cache-control`).toContain('no-cache');
    }
  });

  test('D4 headers on a prerendered legal route (/vi/privacy)', async ({ request }) => {
    const [legal, fallback] = await Promise.all([request.get('/vi/privacy'), request.get('/')]);
    const prerendered = legal.status() === 200 && (await legal.text()) !== (await fallback.text());
    const skipReason = '/vi/privacy is not prerendered yet (it ships with 10.3.1-T1); the fallback is served instead';
    if (!prerendered) {
      console.log(skipReason);
    }
    test.skip(!prerendered, skipReason);

    const headers = legal.headers();
    for (const [name, value] of Object.entries(D4_HEADERS)) {
      expect(headers[name], `/vi/privacy ${name}`).toBe(value);
    }
    expect(headers['content-security-policy'], '/vi/privacy content-security-policy').toContain("default-src 'none'");
  });

  test('a hashed asset is immutable, has the security headers and no CSP', async ({ request }) => {
    const document = await (await request.get('/')).text();
    const chunk = /<script[^>]+src="([^"]+\.js)"/.exec(document)?.[1];
    expect(chunk, 'no <script src> in the fallback document').toBeTruthy();

    // `deployUrl: "/"` (base-uri 'none', 10.1.6-T1) makes the document reference root-relative URLs.
    const response = await request.get(chunk?.startsWith('/') ? (chunk ?? '/') : `/${chunk}`);
    expect(response.status()).toBe(200);
    const headers = response.headers();
    expect(headers['cache-control']).toBe('public, max-age=31536000, immutable');
    for (const [name, value] of Object.entries(D4_HEADERS)) {
      expect(headers[name], `${chunk} ${name}`).toBe(value);
    }
    expect(headers['content-security-policy']).toBeUndefined();
  });

  test('/healthz answers 200 text/plain ok', async ({ request }) => {
    const response = await request.get('/healthz');
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toMatch(/^text\/plain/);
    expect((await response.text()).trim()).toBe('ok');
  });
});
