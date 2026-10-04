import type { APIRequestContext, Page } from '@playwright/test';
import { cspProbe, expect, pageCspRecords, test } from '../fixtures/csp.fixture';
import { directive, expectInlineScriptsCoveredByCsp, scriptDigest } from './csp-inline-scripts';

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

// Negative control for the per-digest helper used by csp.spec.ts. That loop passes vacuously on a page with no
// inline script, so the helper itself is proven here: one inline script whose digest IS in the served header is
// accepted (and really runs), one whose digest is NOT is rejected (and is blocked by the browser).
const DIGEST_SCRIPT = 'window.__digestProbeRan = true;';

/** The real policy with the probe script's digest added to `script-src`, as gen-csp.mjs would have emitted it. */
function withDigest(policy: string, digest: string): string {
  const widened = policy.replace(/(script-src 'self')/, `$1 ${digest}`);
  expect(widened, 'the real policy has no `script-src \'self\'` to extend').not.toBe(policy);
  return widened;
}

test('per-digest helper accepts an inline script whose digest is in the header', async ({ page, request }) => {
  const digest = scriptDigest(DIGEST_SCRIPT);
  const csp = withDigest(await realCsp(request), digest);
  await serveProbe(page, '/__csp-digest-ok', csp, `<script>${DIGEST_SCRIPT}</script>`);
  await page.goto('/__csp-digest-ok');
  const scriptSrc = directive(csp, 'script-src') ?? '';
  expect(scriptSrc).toContain(digest);

  expect(await expectInlineScriptsCoveredByCsp(page, scriptSrc, '/__csp-digest-ok')).toBe(1);
  // The hash is honoured by the browser too: the script ran and nothing was reported (the fixture auto-fails on one).
  expect(await page.evaluate(() => Reflect.get(window, '__digestProbeRan'))).toBe(true);
});

cspProbe('per-digest helper rejects an inline script whose digest is not in the header', async ({ page, request }) => {
  const csp = await realCsp(request);
  await serveProbe(page, '/__csp-digest-missing', csp, `<script>${DIGEST_SCRIPT}</script>`);
  await page.goto('/__csp-digest-missing');
  const scriptSrc = directive(csp, 'script-src') ?? '';
  expect(scriptSrc).not.toContain(scriptDigest(DIGEST_SCRIPT));

  await expect(expectInlineScriptsCoveredByCsp(page, scriptSrc, '/__csp-digest-missing')).rejects.toThrow(
    /not in the script-src hashes/,
  );
  // Blocked at parse time, so the browser agrees with the helper.
  expect(await page.evaluate(() => Reflect.get(window, '__digestProbeRan'))).toBeUndefined();
});

test('per-digest helper reports a page with no inline script as 0 checked, not as a silent pass', async ({
  page,
  request,
}) => {
  const csp = await realCsp(request);
  await serveProbe(page, '/__csp-digest-none', csp, '<p>ok</p>');
  await page.goto('/__csp-digest-none');
  expect(await expectInlineScriptsCoveredByCsp(page, directive(csp, 'script-src') ?? '', '/__csp-digest-none')).toBe(0);
});
