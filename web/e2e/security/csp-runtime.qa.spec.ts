import type { Page } from '@playwright/test';
import { cspProbe, expect, test } from '../fixtures/csp.fixture';

// QA (10.1.6-T1): adversarial runtime checks under the REAL container CSP. Container target only: `ng serve` sends
// no CSP header, so none of these says anything there.
const CONTAINER = process.env['NTS_E2E_TARGET'] === 'container';
test.skip(!CONTAINER, 'dev server sends no CSP; run with NTS_E2E_TARGET=container');

const wsOrigin = (baseURL: string | undefined): string => new URL(baseURL ?? 'http://localhost').origin.replace(/^http/, 'ws');

/** Opens a WebSocket in the page and resolves once it settled (open | closed/failed). */
async function openSocket(page: Page, url: string): Promise<string> {
  return page.evaluate(
    (target) =>
      new Promise<string>((resolve) => {
        const socket = new WebSocket(target);
        socket.onopen = () => resolve('open');
        socket.onclose = (event) => resolve(`close:${event.code}`);
      }),
    url,
  );
}

test('same-origin WebSocket to /hubs is not blocked by connect-src (upstream result is irrelevant)', async ({
  page,
  baseURL,
}) => {
  await page.goto('/');
  // The container has no API upstream, so the handshake fails (404/1006) — a failure AFTER the policy allowed it.
  const outcome = await openSocket(page, `${wsOrigin(baseURL)}/hubs/qa-probe`);
  expect(outcome).toMatch(/^(open|close:\d+)$/);
  // The fixture's auto-fail afterEach proves no securitypolicyviolation / CSP console error was recorded.
});

cspProbe('a cross-origin WebSocket IS blocked by connect-src (the probe above can discriminate)', async ({
  page,
  cspLog,
}) => {
  await page.goto('/');
  // A policy-blocked socket may never settle, so fire it and read the violation instead of awaiting the socket.
  await page.evaluate(() => {
    try {
      new WebSocket('ws://evil.example.invalid/x');
    } catch {
      // a synchronous SecurityError is equally a refusal
    }
  });
  await expect.poll(() => cspLog.some((r) => r.violatedDirective.startsWith('connect-src'))).toBe(true);
});

cspProbe('application code cannot create any policy outside the allow-list, nor a default policy', async ({
  page,
  cspLog,
}) => {
  await page.goto('/');
  const outcomes = await page.evaluate(() => {
    const result: Record<string, string> = {};
    for (const name of ['evil', 'default', 'nts-sanitizer2', 'angular#unsafe-bypass', 'Angular']) {
      try {
        window.trustedTypes?.createPolicy(name, { createHTML: (input: string) => input });
        result[name] = 'created';
      } catch (error) {
        result[name] = error instanceof TypeError ? 'TypeError' : 'other';
      }
    }
    return { result, defaultPolicy: window.trustedTypes?.defaultPolicy ?? null };
  });
  expect(outcomes.result).toEqual({
    evil: 'TypeError',
    default: 'TypeError',
    'nts-sanitizer2': 'TypeError',
    'angular#unsafe-bypass': 'TypeError',
    Angular: 'TypeError',
  });
  expect(outcomes.defaultPolicy).toBeNull();
  await expect.poll(() => cspLog.filter((r) => r.violatedDirective.startsWith('trusted-types') && r.source === 'event').length).toBe(5);
});

cspProbe('Trusted Types blocks a raw string reaching an HTML sink (innerHTML)', async ({ page, cspLog }) => {
  await page.goto('/');
  const outcome = await page.evaluate(() => {
    try {
      document.createElement('div').innerHTML = '<img src=x onerror=alert(1)>';
      return 'assigned';
    } catch (error) {
      return error instanceof TypeError ? 'TypeError' : 'other';
    }
  });
  expect(outcome).toBe('TypeError');
  await expect.poll(() => cspLog.some((r) => r.violatedDirective.startsWith('require-trusted-types-for'))).toBe(true);
});

test('a blob download (the downloadBlob sequence) is not blocked by default-src none, even with an immediate revoke', async ({
  page,
}) => {
  await page.goto('/');
  const downloadPromise = page.waitForEvent('download');
  await page.evaluate(() => {
    const url = URL.createObjectURL(new Blob(['hello'], { type: 'text/plain' }));
    try {
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = 'bao-cao.txt';
      anchor.click();
    } finally {
      URL.revokeObjectURL(url);
    }
  });
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe('bao-cao.txt');
  expect(await download.path()).toBeTruthy();
});

test('no <base> element, root-relative assets, and nested deep links load every script/style with 200', async ({
  page,
}) => {
  const failures: string[] = [];
  page.on('response', (response) => {
    const type = response.request().resourceType();
    if (['script', 'stylesheet', 'font', 'image', 'fetch'].includes(type) && response.status() >= 400) {
      failures.push(`${response.status()} ${new URL(response.url()).pathname}`);
    }
  });
  for (const route of ['/dashboard/episodes/42', '/a/b/c/d/e/f', '/dashboard/episodes/42/']) {
    await page.goto(route);
    await page.waitForLoadState('networkidle');
    expect(await page.locator('base').count(), `${route}: no <base> element`).toBe(0);
    expect(await page.locator('app-root').count(), `${route}: SPA booted`).toBe(1);
    const urls = await page.evaluate(() =>
      [...document.querySelectorAll('script[src], link[rel="stylesheet"][href]')].map(
        (node) => node.getAttribute('src') ?? node.getAttribute('href') ?? '',
      ),
    );
    expect(urls.length).toBeGreaterThan(0);
    for (const url of urls) {
      expect(url, `${route}: asset URL must be root-relative`).toMatch(/^\/[^/]/);
    }
    // A reload on the deep link (nginx try_files fallback) boots the same document.
    await page.reload();
    await page.waitForLoadState('networkidle');
    expect(await page.locator('app-root').count(), `${route}: SPA boots after reload`).toBe(1);
  }
  expect(failures).toEqual([]);
});
