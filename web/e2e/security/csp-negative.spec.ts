import type { APIRequestContext, Page } from '@playwright/test';
import { cspProbe, expect, pageCspRecords, test } from '../fixtures/csp.fixture';

// Positive control for the detector (task 10.1.6-T1). A detector-only suite passes when its listener is dead, so
// this spec serves a synthetic same-origin document under the REAL CSP header and demands that the detector sees
// it. `cspProbe` keeps the detector but not the auto-fail, so the deliberate violation cannot fail its own proof.
// The trigger is an inline <script> (blocked at parse time): Chromium enforces an inline `onclick=` only on
// dispatch, so a never-clicked handler would report nothing and the control would look dead.
const CONTAINER = process.env['NTS_E2E_TARGET'] === 'container';
test.skip(!CONTAINER, 'dev server sends no CSP; run with NTS_E2E_TARGET=container');

const FIELDS = ['disposition', 'effectiveDirective', 'lineNumber', 'route', 'source', 'violatedDirective'];

async function serveProbe(page: Page, path: string, csp: string, body: string): Promise<void> {
  await page.route(`**${path}`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html; charset=utf-8',
      headers: { 'content-security-policy': csp },
      body: `<!doctype html><html lang="en"><head><title>probe</title></head><body>${body}</body></html>`,
    }),
  );
}

async function realCsp(request: APIRequestContext): Promise<string> {
  const policy = (await request.get('/')).headers()['content-security-policy'];
  expect(policy, 'the app response carries no CSP to copy').toBeTruthy();
  return policy ?? '';
}

cspProbe('detector records a violation for an inline <script> served under the real CSP', async ({ page, request, cspLog }) => {
  await serveProbe(page, '/__csp-probe', await realCsp(request), '<script>window.__probeRan = true;</script>');
  await page.goto('/__csp-probe');
  await expect.poll(async () => (await pageCspRecords(page)).length).toBeGreaterThan(0);

  const records = await pageCspRecords(page);
  expect(records.some((r) => r.effectiveDirective.startsWith('script-src'))).toBe(true);
  expect(await page.evaluate(() => Reflect.get(window, '__probeRan'))).toBeUndefined();
  // Sanitized shape only: no blockedURI / sourceFile / sample ever leaves the page.
  for (const record of records) {
    expect(Object.keys(record).sort()).toEqual(FIELDS);
    expect(record.route).toBe('/__csp-probe');
  }
  // The Node-side history (what the auto-fail reads) saw it too.
  await expect.poll(() => cspLog.length).toBeGreaterThan(0);
});

test('a clean probe under the same header records nothing', async ({ page, request }) => {
  await serveProbe(page, '/__csp-probe-clean', await realCsp(request), '<p>ok</p>');
  await page.goto('/__csp-probe-clean');
  await page.waitForLoadState('load');
  expect(await pageCspRecords(page)).toEqual([]);
});
