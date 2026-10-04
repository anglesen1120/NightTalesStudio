import type { Page, Request } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';

// 10.2.6-T1 (README D10/D15/D16, task file §Tests + Acceptance checks 12-15).
//
// Recorded deviation: no `/login` route exists until 10.3.3-T1 merges, so `loginAsOwner` (UI + TOTP) cannot run here.
// The session is authenticated with `page.route` mocks only (no in-app test hooks): the boot restore's
// `POST /api/v1/auth/refresh` answers a token pair whose unsigned JWT carries just the claims the client decodes
// (`sub`, `role`, `amr`), `GET`/`PATCH /api/v1/me` answer a `MeDto`, and `POST /api/v1/hubs/ticket` answers 503, so the
// realtime hub never connects (this spec never asserts a realtime state).
// 10.3.3-T1 may switch this file to `loginAsOwner` once `/login` exists.
//
// `NAV_ITEMS` is empty right after 10.2.6-T1 (task file, failure table), so the nav has no links: the `aria-current`
// check is skipped here and covered by `side-nav.component.spec.ts`. Likewise no route declares `data.titleKey` yet, so
// the document title stays the product name; the title is asserted to be stable and non-empty across the language
// switch (a titleKey-driven change is `shell.store.spec.ts`'s `pageTitle_UsesShellTitleKeyAndFollowsLocaleChange`).

const ME_ENDPOINT = '**/api/v1/me';

const LABELS = {
  vi: { skip: 'Chuyển tới nội dung chính', nav: 'Điều hướng chính', open: 'Mở điều hướng', account: 'Tài khoản', language: 'Ngôn ngữ' },
  en: { skip: 'Skip to main content', nav: 'Main navigation', open: 'Open navigation', account: 'Account', language: 'Language' },
} as const;

type Lang = keyof typeof LABELS;

function fakeAccessToken(): string {
  const encode = (value: object): string => Buffer.from(JSON.stringify(value)).toString('base64url');
  return `${encode({ alg: 'none', typ: 'JWT' })}.${encode({ sub: 'e2e-owner', role: 'owner', amr: ['pwd', 'otp'] })}.signature`;
}

const ME_DTO = {
  id: 'e2e-owner',
  displayName: 'Chủ Sở Hữu',
  email: 'owner@example.test',
  uiLocale: 'vi',
  role: 'owner',
  mfaEnabled: true,
  lastLoginAt: null,
  features: [],
};

interface MockedSession {
  readonly patches: Request[];
}

async function mockSession(page: Page): Promise<MockedSession> {
  const patches: Request[] = [];
  await page.route('**/api/v1/auth/refresh', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ accessToken: fakeAccessToken(), expiresAt: new Date(Date.now() + 3_600_000).toISOString() }),
    }),
  );
  await page.route(ME_ENDPOINT, async (route) => {
    const request = route.request();
    if (request.method() === 'PATCH') {
      patches.push(request);
      const body = request.postDataJSON() as { uiLocale: Lang };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...ME_DTO, uiLocale: body.uiLocale }) });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME_DTO) });
  });
  await page.route('**/api/v1/hubs/ticket', (route) =>
    route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 503, code: 'unavailable' }) }),
  );
  return { patches };
}

async function openShell(page: Page, lang: Lang = 'vi'): Promise<MockedSession> {
  const session = await mockSession(page);
  await page.goto('/');
  await expect(page.getByRole('main')).toBeVisible();
  // The profile load settled: the user menu trigger shows the display name.
  await expect(page.getByRole('button', { name: LABELS[lang].account })).toContainText('Chủ Sở Hữu');
  return session;
}

async function switchLanguage(page: Page, from: Lang, to: Lang): Promise<void> {
  await page.getByRole('combobox', { name: LABELS[from].language }).click();
  await page.getByRole('option', { name: to === 'en' ? 'English' : 'Tiếng Việt' }).click();
  await expect(page.locator('html')).toHaveAttribute('lang', to);
}

async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const element = document.scrollingElement ?? document.documentElement;
    return { scrollWidth: element.scrollWidth, clientWidth: element.clientWidth };
  });
  expect(overflow.scrollWidth, `scrollWidth ${overflow.scrollWidth} > clientWidth ${overflow.clientWidth}`).toBeLessThanOrEqual(
    overflow.clientWidth,
  );
}

test.use({ locale: 'vi-VN' });

test.describe('shell (desktop)', () => {
  test('skip link is the first Tab stop and moves focus to #content', async ({ page }) => {
    await openShell(page);
    await page.keyboard.press('Tab');
    const skipLink = page.getByRole('link', { name: LABELS.vi.skip });
    await expect(skipLink).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator('#content')).toBeFocused();
  });

  test('landmarks: banner, labelled navigation, main; the nav has no links while NAV_ITEMS is empty', async ({ page }) => {
    await openShell(page);
    await expect(page.getByRole('banner')).toBeVisible();
    const navigation = page.getByRole('navigation', { name: LABELS.vi.nav });
    await expect(navigation).toBeVisible();
    await expect(page.getByRole('main')).toBeVisible();
    await expect(navigation.getByRole('link')).toHaveCount(0);
    // `aria-current="page"` on the active item: skipped (no item exists yet), see side-nav.component.spec.ts.
  });

  test('user menu: opens, Escape closes it and focus returns to the trigger', async ({ page }) => {
    await openShell(page);
    const trigger = page.getByRole('button', { name: LABELS.vi.account });
    await trigger.click();
    await expect(page.getByRole('menu')).toBeVisible();
    // Material moves focus into the panel after it opens; Escape before that would test a half-open menu.
    await expect(page.getByRole('menuitem', { name: /.+/ }).first()).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('menu')).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test('language switch vi→en: <html lang> changes, PATCH /me is sent once with exactly {"uiLocale":"en"}', async ({ page }) => {
    const { patches } = await openShell(page);
    const titleBefore = await page.title();
    expect(titleBefore).not.toBe('');
    await switchLanguage(page, 'vi', 'en');
    await expect(page.getByRole('navigation', { name: LABELS.en.nav })).toBeVisible();
    await expect.poll(() => patches.length).toBe(1);
    expect(patches[0]?.postDataJSON()).toEqual({ uiLocale: 'en' });
    expect(patches[0]?.postData()).toBe('{"uiLocale":"en"}');
    // No route has a `data.titleKey` yet: the title stays the product name in both languages (see header comment).
    expect(await page.title()).toBe(titleBefore);
    expect(patches).toHaveLength(1);
  });

  for (const lang of ['vi', 'en'] as const) {
    test(`axe (WCAG 2.1 AA) on the shell in ${lang}`, async ({ page }) => {
      await openShell(page);
      if (lang === 'en') await switchLanguage(page, 'vi', 'en');
      await expect(page.locator('html')).toHaveAttribute('lang', lang);
      await expectA11yClean(page);
    });
  }
});

test.describe('shell (360 px)', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  test('no horizontal scroll in vi and en; the labelled hamburger opens the overlay nav, Escape closes it and returns focus', async ({
    page,
  }) => {
    await openShell(page);
    await expectNoHorizontalScroll(page);

    // While the overlay is open the workspace (incl. this button) is `aria-hidden` + `inert`, so a role query would not
    // find it; the CSS locator stays valid and the accessible name is asserted through its label.
    const hamburger = page.locator('button.top-bar__menu');
    await expect(hamburger).toHaveAttribute('aria-label', LABELS.vi.open);
    await expect(hamburger).toHaveAttribute('aria-expanded', 'false');
    await hamburger.click();
    await expect(hamburger).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByRole('navigation', { name: LABELS.vi.nav })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(hamburger).toHaveAttribute('aria-expanded', 'false');
    await expect(hamburger).toBeFocused();

    await switchLanguage(page, 'vi', 'en');
    await expect(page.getByRole('button', { name: LABELS.en.open })).toBeVisible();
    await expectNoHorizontalScroll(page);
  });
});
