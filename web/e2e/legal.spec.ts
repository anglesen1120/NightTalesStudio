import type { Page } from '@playwright/test';
import { LEGAL_CONTENT } from '../src/app/features/public/data-access/legal-content.generated';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';

// 10.3.1-T1 (README D1/D4/D12/D15/D16). The four prerendered legal routes, with the CSP fixture active in every
// test (a `securitypolicyviolation` or a CSP/Trusted-Types console error fails it in `afterEach`), no `/api/**`
// request, JavaScript disabled, a 360 px viewport and axe (WCAG 2.1 AA). No sign-in, no `storageState`.
// The D4 header matrix for these routes is asserted by `security/csp.spec.ts` (they are in `CSP_ROUTES`); here the
// presence of the CSP header is re-checked on the container target only, because `ng serve` sends no D4 headers.

const CONTAINER = process.env['NTS_E2E_TARGET'] === 'container';
const DOCS = ['privacy', 'terms'] as const;
const LANGS = ['vi', 'en'] as const;
const ROUTES = LANGS.flatMap((lang) => DOCS.map((doc) => ({ lang, doc, path: `/${lang}/${doc}` })));
const SIBLING = { vi: 'en', en: 'vi' } as const;

// Independent of the generated module: fixed strings copied verbatim from the authored Markdown in
// `src/content/legal/<doc>.1.0.0.<lang>.md`, so a generator that silently drops a block cannot pass (CR3).
// Per document x language: one `##` heading, one paragraph sentence fragment and one list item.
const MARKERS = {
  privacy: {
    vi: {
      heading: '2. Dữ liệu chúng tôi thu thập',
      paragraph: 'là một dịch vụ cá nhân do chính chủ vận hành',
      item: 'Yêu cầu xoá tài khoản của bạn được hoàn thành trong vòng 7 ngày.',
    },
    en: {
      heading: '2. Data we collect',
      paragraph: 'is a personal, owner-operated service',
      item: 'Account data: your e-mail address, your display name',
    },
  },
  terms: {
    vi: {
      heading: '2. Điều kiện sử dụng',
      paragraph: 'là một dịch vụ cá nhân do chính chủ vận hành',
      item: 'Tín dụng được cấp dùng thử hết hạn sau 30 (ba mươi) ngày kể từ khi được cấp.',
    },
    en: {
      heading: '2. Eligibility',
      paragraph: 'is a personal, owner-operated service that turns a story you approve',
      item: 'Credits granted as a trial expire 30 (thirty) days after they are granted.',
    },
  },
} as const;

/** Asserts the three authored markers of one document x language are on the page. */
async function expectMarkers(page: Page, doc: (typeof DOCS)[number], lang: (typeof LANGS)[number]): Promise<void> {
  const marker = MARKERS[doc][lang];
  await expect(page.locator('h2', { hasText: marker.heading }).first()).toBeVisible();
  await expect(page.locator('p', { hasText: marker.paragraph }).first()).toBeVisible();
  await expect(page.locator('li', { hasText: marker.item }).first()).toBeVisible();
}

/** Blocks every `/api/**` request and records it; the pages have no runtime data, so the list must stay empty. */
async function blockApi(page: Page): Promise<string[]> {
  const seen: string[] = [];
  await page.route('**/api/**', async (route) => {
    seen.push(new URL(route.request().url()).pathname);
    await route.abort();
  });
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith('/api/')) seen.push(new URL(request.url()).pathname);
  });
  return seen;
}

for (const { lang, doc, path } of ROUTES) {
  const content = LEGAL_CONTENT[doc][lang];
  const sibling = SIBLING[lang];

  test.describe(path, () => {
    test('renders title, version, effective date, TOC, SEO head and the sibling link with zero API requests', async ({
      page,
    }) => {
      const api = await blockApi(page);
      const response = await page.goto(path);
      expect(response?.status()).toBe(200);
      await page.waitForLoadState('networkidle');

      await expect(page.locator('h1')).toHaveText(content.title);
      await expectMarkers(page, doc, lang);
      await expect(page.locator('html')).toHaveAttribute('lang', lang);
      const body = page.locator('body');
      await expect(body).toContainText(content.version);
      const [year, month, day] = content.effectiveDate.split('-');
      await expect(body).toContainText(
        new RegExp(`(?=[\\s\\S]*\\b${day}\\b)(?=[\\s\\S]*\\b${month}\\b)(?=[\\s\\S]*\\b${year}\\b)`),
      );
      // A translated string, never a key path.
      await expect(body).not.toContainText(/public\.(legal|nav)\./);

      await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', path);
      for (const hreflang of ['vi', 'en', 'x-default']) {
        await expect(page.locator(`link[rel="alternate"][hreflang="${hreflang}"]`)).toHaveAttribute(
          'href',
          hreflang === 'en' ? `/en/${doc}` : `/vi/${doc}`,
        );
      }
      await expect(page.locator('meta[property="og:title"]')).toHaveAttribute('content', /\S/);
      await expect(page.locator('meta[property="og:description"]')).toHaveAttribute('content', /\S/);
      await expect(page.locator('meta[property="og:type"]')).toHaveAttribute('content', 'article');
      await expect(page.locator('meta[name="description"]')).toHaveAttribute('content', /\S/);
      await expect(page).toHaveTitle(new RegExp(content.title));

      const toc = page.locator('nav a[href^="#"]');
      expect(await toc.count()).toBeGreaterThan(0);
      await expect(page.locator(`a[hreflang="${sibling}"]`)).toHaveAttribute('href', `/${sibling}/${doc}`);

      expect(api, 'no /api/** request may be issued').toEqual([]);
    });

    test('works with JavaScript disabled: body, TOC anchors, version/date, canonical/hreflang, sibling link', async ({
      browser,
      baseURL,
    }) => {
      const context = await browser.newContext({ javaScriptEnabled: false, baseURL });
      try {
        const page = await context.newPage();
        const api = await blockApi(page);
        const response = await page.goto(path);
        expect(response?.status()).toBe(200);

        await expect(page.locator('h1')).toHaveText(content.title);
        await expectMarkers(page, doc, lang);
        await expect(page.locator('body')).toContainText(content.version);
        const firstHeading = content.blocks.find((block) => block.kind === 'heading');
        const headingText = (firstHeading?.runs ?? []).map((run) => run.text).join('');
        if (headingText !== '') await expect(page.locator('main, body').first()).toContainText(headingText);

        const toc = page.locator('nav a[href^="#"]');
        const count = await toc.count();
        expect(count).toBeGreaterThan(0);
        for (let index = 0; index < count; index += 1) {
          const href = await toc.nth(index).getAttribute('href');
          expect(href).toMatch(/^#[\w-]+$/);
          await expect(page.locator(href ?? '#missing')).toHaveCount(1);
        }

        await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', path);
        await expect(page.locator('link[rel="alternate"][hreflang="vi"]')).toHaveAttribute('href', `/vi/${doc}`);
        await expect(page.locator('link[rel="alternate"][hreflang="en"]')).toHaveAttribute('href', `/en/${doc}`);
        await expect(page.locator('link[rel="alternate"][hreflang="x-default"]')).toHaveAttribute('href', `/vi/${doc}`);
        await expect(page.locator(`a[hreflang="${sibling}"]`)).toHaveAttribute('href', `/${sibling}/${doc}`);
        expect(api).toEqual([]);
      } finally {
        await context.close();
      }
    });

    test('has no horizontal scroll at 360 px', async ({ page }) => {
      await page.setViewportSize({ width: 360, height: 740 });
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await expect(page.locator('h1')).toBeVisible();
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow).toBeLessThanOrEqual(0);
    });

    test('is axe clean (WCAG 2.1 AA)', async ({ page }) => {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await expect(page.locator('h1')).toBeVisible();
      await expectA11yClean(page);
    });

    test('the language switch goes to the same document in the other language', async ({ page }) => {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await page.locator(`a[hreflang="${sibling}"]`).click();
      await expect(page).toHaveURL(new RegExp(`/${sibling}/${doc}$`));
      await expect(page.locator('html')).toHaveAttribute('lang', sibling);
      await expect(page.locator('h1')).toHaveText(LEGAL_CONTENT[doc][sibling].title);
    });
  });
}

test.describe('default-language redirects', () => {
  for (const doc of DOCS) {
    // The redirect path is not a legal leaf, but it is session-free too: no `/api/**` request (no boot refresh).
    test(`/${doc} lands on /vi/${doc} with zero API requests`, async ({ page }) => {
      const api = await blockApi(page);
      await page.goto(`/${doc}`);
      await expect(page).toHaveURL(new RegExp(`/vi/${doc}$`));
      await expect(page.locator('h1')).toHaveText(LEGAL_CONTENT[doc].vi.title);
      await page.waitForLoadState('networkidle');
      expect(api, 'no /api/** request may be issued on the redirect path').toEqual([]);
    });
  }
});

test.describe('language of a public page is not persisted', () => {
  test('visiting /en/privacy leaves no saved preference', async ({ page }) => {
    await page.goto('/en/privacy');
    await page.waitForLoadState('networkidle');
    const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('nts.prefs.v1') ?? '{}').uiLanguage ?? null);
    expect(saved).toBeNull();
  });
});

test.describe('container target: D4 headers', () => {
  test.skip(!CONTAINER, 'dev server sends no CSP; run with NTS_E2E_TARGET=container');
  for (const { path } of ROUTES) {
    test(`${path} carries the CSP and Trusted Types header`, async ({ request }) => {
      const response = await request.get(path);
      expect(response.status()).toBe(200);
      const policy = response.headers()['content-security-policy'];
      expect(policy, 'Content-Security-Policy header is missing').toContain("require-trusted-types-for 'script'");
      expect(response.headers()['x-content-type-options']).toBe('nosniff');
    });
  }
});
