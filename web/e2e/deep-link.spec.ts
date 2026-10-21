import { expect, test } from './fixtures/csp.fixture';

// Deep-link reload (10.1.6-T1 QA D1). The production document has no <base> (`base-uri 'none'`), so its assets are
// root-relative via `deployUrl`; `ng serve` ignores `deployUrl`, so the development build configuration carries
// `baseHref: "/"` instead. Without either, a nested route requests /dashboard/episodes/main.js and never boots.
// Runs against both targets (`NTS_E2E_TARGET` dev | container).

const ROUTES = ['/dashboard/episodes/42', '/a/b/c/d'];

for (const route of ROUTES) {
  test(`a reload on ${route} boots the SPA and loads every script and stylesheet`, async ({ page }) => {
    const failed: string[] = [];
    page.on('response', (response) => {
      const type = response.request().resourceType();
      if ((type === 'script' || type === 'stylesheet') && response.status() >= 400) {
        failed.push(`${response.status()} ${response.url()}`);
      }
    });

    await page.goto(route);
    await expect(page.locator('app-root[ng-version]')).toHaveCount(1);
    await page.reload();
    await expect(page.locator('app-root[ng-version]')).toHaveCount(1);
    expect(failed, 'script/stylesheet responses with a 4xx/5xx status').toEqual([]);
  });
}
