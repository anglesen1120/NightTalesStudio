import type { APIRequestContext } from '@playwright/test';
import { cspProbe, expect, test } from '../fixtures/csp.fixture';

// Trusted Types allow-list proof (task 10.1.6-T1, WR12). Chromium exposes no policy-enumeration API, so the allow-list
// is read from the response `trusted-types` directive and proven by explicit createPolicy probes.
const CONTAINER = process.env['NTS_E2E_TARGET'] === 'container';
test.skip(!CONTAINER, 'dev server sends no CSP; run with NTS_E2E_TARGET=container');

async function trustedTypesDirective(request: APIRequestContext): Promise<string> {
  const policy = (await request.get('/')).headers()['content-security-policy'] ?? '';
  const part = policy
    .split(';')
    .map((entry) => entry.trim())
    .find((entry) => entry.startsWith('trusted-types '));
  expect(part, 'no trusted-types directive in the CSP').toBeTruthy();
  return part ?? '';
}

test('Trusted Types is enforced: no default policy, allow-list is exactly angular, angular#bundler, nts-sanitizer', async ({
  page,
  request,
}) => {
  expect(await trustedTypesDirective(request)).toBe('trusted-types angular angular#bundler nts-sanitizer');
  await page.goto('/');
  const state = await page.evaluate(() => ({
    present: window.trustedTypes !== undefined,
    defaultPolicy: window.trustedTypes?.defaultPolicy ?? null,
  }));
  expect(state.present).toBe(true);
  expect(state.defaultPolicy).toBeNull();
});

test('nts-sanitizer can be created and used once, in a scratch evaluation', async ({ page }) => {
  await page.goto('/');
  const html = await page.evaluate(() => {
    const policy = window.trustedTypes?.createPolicy('nts-sanitizer', { createHTML: (input: string) => input });
    return String(policy?.createHTML('<p>x</p>'));
  });
  expect(html).toBe('<p>x</p>');
});

// These two creations are REFUSED by the header, and a refusal is itself a recorded violation, so the test opts
// out of the auto-fail and asserts exactly those two violations instead.
cspProbe('angular#unsafe-bypass and angular#unsafe-jit cannot be created (WR12)', async ({ page, cspLog }) => {
  await page.goto('/');
  for (const name of ['angular#unsafe-bypass', 'angular#unsafe-jit']) {
    const outcome = await page.evaluate((policyName) => {
      try {
        window.trustedTypes?.createPolicy(policyName, { createHTML: (input: string) => input });
        return 'created';
      } catch (error) {
        return error instanceof TypeError ? 'TypeError' : 'other';
      }
    }, name);
    expect(outcome, name).toBe('TypeError');
  }
  await expect.poll(() => cspLog.filter((r) => r.violatedDirective.startsWith('trusted-types') && r.source === 'event').length).toBe(2);
  expect(cspLog.every((r) => r.violatedDirective.startsWith('trusted-types'))).toBe(true);
});
