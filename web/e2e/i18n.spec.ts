import { expect, test } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';

// 10.2.5-T1 (README D10/D16). This spec never signs in and sets no `storageState`. The protected shell and its
// language switch arrive with 10.2.6-T1, so the "switch vi↔en, two visible strings change, no key path is
// rendered" check is that task's e2e; here the initial-language resolution, `<html lang>`, the served scope
// files and axe in both languages are exercised against the real bundle.

const PREFS_KEY = 'nts.prefs.v1';

async function htmlLang(page: import('@playwright/test').Page): Promise<string> {
  return page.evaluate(() => document.documentElement.lang);
}

async function boot(page: import('@playwright/test').Page): Promise<void> {
  await page.goto('/');
  await expect(page.locator('app-root')).toBeAttached();
  await page.waitForLoadState('networkidle');
}

test.describe('initial language: Vietnamese browser / fresh profile', () => {
  test.use({ locale: 'vi-VN' });

  test('<html lang> is vi, nothing is persisted and no translation file is fetched before a screen needs one', async ({
    page,
  }) => {
    const apiCalls: string[] = [];
    page.on('request', (request) => {
      if (['fetch', 'xhr'].includes(request.resourceType())) apiCalls.push(request.url());
    });
    await boot(page);
    expect(await htmlLang(page)).toBe('vi');
    expect(await page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? '{}').uiLanguage ?? null, PREFS_KEY)).toBeNull();
    expect(apiCalls).toEqual([]);
    await expectA11yClean(page);
  });
});

test.describe('initial language: English browser', () => {
  test.use({ locale: 'en-GB' });

  test('no saved preference: the browser language wins, <html lang> is en, still nothing persisted, axe clean', async ({
    page,
  }) => {
    await boot(page);
    expect(await htmlLang(page)).toBe('en');
    expect(await page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? '{}').uiLanguage ?? null, PREFS_KEY)).toBeNull();
    await expectA11yClean(page);
  });

  test('a saved vi preference wins over the browser language', async ({ page }) => {
    await page.addInitScript((key) => localStorage.setItem(key, JSON.stringify({ uiLanguage: 'vi', theme: 'dark' })), PREFS_KEY);
    await boot(page);
    expect(await htmlLang(page)).toBe('vi');
  });
});

test.describe('initial language: unsupported browser language', () => {
  test.use({ locale: 'fr-FR' });

  test('falls back to vi without a warning, and a saved en preference still wins', async ({ page }) => {
    const warnings: string[] = [];
    page.on('console', (message) => {
      if (['warning', 'error'].includes(message.type())) warnings.push(message.text());
    });
    await boot(page);
    expect(await htmlLang(page)).toBe('vi');
    expect(warnings).toEqual([]);

    await page.addInitScript((key) => localStorage.setItem(key, JSON.stringify({ uiLanguage: 'en', theme: 'dark' })), PREFS_KEY);
    await page.reload();
    await page.waitForLoadState('networkidle');
    expect(await htmlLang(page)).toBe('en');
  });
});

test.describe('served scope files', () => {
  test('core and quality are served as JSON for both languages with the same top-level shape', async ({ request }) => {
    for (const scope of ['core', 'quality']) {
      const bodies: Record<string, unknown> = {};
      for (const lang of ['vi', 'en']) {
        const response = await request.get(`/assets/i18n/${scope}/${lang}.json`);
        expect(response.status(), `${scope}/${lang}`).toBe(200);
        expect(response.headers()['content-type'] ?? '').toContain('json');
        bodies[lang] = await response.json();
      }
      expect(Object.keys(bodies['vi'] as object).sort()).toEqual(Object.keys(bodies['en'] as object).sort());
    }
  });

  test('a scope that does not exist is a 404 the loader turns into {} (never an HTML fallback page)', async ({ request }) => {
    const response = await request.get('/assets/i18n/does-not-exist/vi.json');
    expect(response.status()).toBe(404);
  });
});
