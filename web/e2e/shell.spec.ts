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
// `NAV_ITEMS` holds the episodes entry (10.5.1-T1) and the dashboard entry appended by 10.4.1-T1, so the nav has
// exactly two links; their `aria-current` behaviour is covered by `side-nav.component.spec.ts`,
// `episodes-list.spec.ts` and `dashboard.spec.ts`. Likewise no route declares
// `data.titleKey` yet, so
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

/** Selectors of the regions QA-6 checks: the top bar (banner), the main content and the realtime strip. */
const CLIP_REGIONS = ['header.shell__header', 'main#content', 'nts-realtime-status'] as const;

/**
 * `overflow-x: clip` on the shell hides overflow from `scrollWidth`, so measure each visible element's rectangle
 * against the viewport instead. Hidden elements (`display: none`, `visibility: hidden`, no box) are ignored; the
 * off-canvas side nav and the inert closed drawer are outside these three regions.
 */
async function clipOffenders(page: Page): Promise<string[]> {
  return page.evaluate((regions) => {
    const viewportWidth = document.documentElement.clientWidth;
    const offenders: string[] = [];
    for (const selector of regions) {
      const root = document.querySelector(selector);
      if (root === null) continue;
      for (const element of [root, ...Array.from(root.querySelectorAll('*'))]) {
        const style = getComputedStyle(element);
        if (style.display === 'none' || style.visibility === 'hidden') continue;
        if (element.closest('[inert][aria-hidden="true"]') !== null) continue;
        const rect = element.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) continue;
        if (rect.right > viewportWidth + 0.5 || rect.left < -0.5) {
          const classes = String(element.getAttribute('class') ?? '').trim().split(/\s+/).filter(Boolean).join('.');
          const label = `${element.tagName.toLowerCase()}${classes ? '.' + classes : ''}`;
          offenders.push(`${selector} > ${label}: left=${rect.left.toFixed(1)} right=${rect.right.toFixed(1)} viewport=${viewportWidth}`);
        }
      }
    }
    return offenders;
  }, CLIP_REGIONS);
}

async function expectNoElementOverflow(page: Page): Promise<void> {
  expect(await clipOffenders(page), 'elements outside the 360 px viewport').toEqual([]);
}

/** Interactive targets that are on screen right now, with their measured box. */
async function interactiveTargets(page: Page): Promise<{ target: string; width: number; height: number }[]> {
  return page.evaluate(() => {
    const found: { target: string; width: number; height: number }[] = [];
    const roots = ['header.shell__header', 'main#content', 'nts-realtime-status', 'nts-side-nav'];
    for (const selector of roots) {
      const root = document.querySelector(selector);
      if (root === null || root.closest('[inert]') !== null) continue;
      for (const element of Array.from(root.querySelectorAll('a[href], button, [role="combobox"], [role="button"], select, input'))) {
        const style = getComputedStyle(element);
        if (style.display === 'none' || style.visibility === 'hidden') continue;
        const rect = element.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) continue;
        const name = element.getAttribute('aria-label') ?? element.textContent?.trim().slice(0, 30) ?? '';
        found.push({ target: `${selector} ${element.tagName.toLowerCase()}[${name}]`, width: rect.width, height: rect.height });
      }
    }
    return found;
  });
}

async function expectTargetsAtLeast24(page: Page): Promise<void> {
  const targets = await interactiveTargets(page);
  expect(targets.length, 'no interactive target found').toBeGreaterThan(0);
  const small = targets.filter((entry) => entry.width < 24 || entry.height < 24);
  expect(small, 'interactive targets under 24x24 px').toEqual([]);
}

interface FocusSample {
  label: string;
  outlineStyle: string;
  outlineWidth: number;
  boxShadow: string;
}

/** Tabs through the shell and records the focus indicator of every stop (keyboard focus, so `:focus-visible` applies). */
async function tabWalk(page: Page, stops: number): Promise<FocusSample[]> {
  const samples: FocusSample[] = [];
  for (let index = 0; index < stops; index++) {
    await page.keyboard.press('Tab');
    samples.push(
      await page.evaluate(() => {
        const element = document.activeElement as HTMLElement;
        const style = getComputedStyle(element);
        const label = element.getAttribute('aria-label') ?? element.getAttribute('role') ?? element.textContent?.trim().slice(0, 30) ?? element.tagName;
        return {
          label: `${element.tagName.toLowerCase()}[${label}]`,
          outlineStyle: style.outlineStyle,
          outlineWidth: parseFloat(style.outlineWidth),
          boxShadow: style.boxShadow,
        };
      }),
    );
  }
  return samples;
}

/** A visible focus indicator: a drawn outline or a box-shadow ring. */
function withoutFocusIndicator(samples: FocusSample[]): FocusSample[] {
  return samples.filter((sample) => !((sample.outlineStyle !== 'none' && sample.outlineWidth > 0) || sample.boxShadow !== 'none'));
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

  test('landmarks: banner, labelled navigation with the episodes and dashboard links, main', async ({ page }) => {
    await openShell(page);
    await expect(page.getByRole('banner')).toBeVisible();
    const navigation = page.getByRole('navigation', { name: LABELS.vi.nav });
    await expect(navigation).toBeVisible();
    await expect(page.getByRole('main')).toBeVisible();
    await expect(navigation.getByRole('link')).toHaveCount(2);
    await expect(navigation.getByRole('link', { name: 'Tập' })).toBeVisible();
    await expect(navigation.getByRole('link', { name: 'Bảng điều khiển' })).toBeVisible();
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

test.describe('shell (A14: themes, targets, focus, reduced motion)', () => {
  for (const theme of ['dark', 'light'] as const) {
    for (const lang of ['vi', 'en'] as const) {
      test(`axe (WCAG 2.1 AA, incl. color-contrast) on the shell in ${theme} theme, ${lang}`, async ({ page }) => {
        // PreferencesStore storage key and shape (core/prefs/preferences.store.ts); no app hook needed.
        await page.addInitScript((value) => {
          localStorage.setItem('nts.prefs.v1', JSON.stringify({ theme: value }));
        }, theme);
        await openShell(page);
        if (lang === 'en') await switchLanguage(page, 'vi', 'en');
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
        await expectA11yClean(page);
      });
    }
  }

  test('desktop: interactive targets are >= 24 px and keyboard focus shows an indicator', async ({ page }) => {
    await openShell(page);
    await expectTargetsAtLeast24(page);
    // Stops: skip link, the episodes nav link (10.5.1-T1), the dashboard nav link (10.4.1-T1), language select,
    // user menu.
    const samples = await tabWalk(page, 5);
    const labels = samples.map((sample) => sample.label);
    expect(labels[0], 'first Tab stop is the skip link').toContain(LABELS.vi.skip);
    const skipBox = await page.getByRole('link', { name: LABELS.vi.skip }).boundingBox();
    expect(skipBox?.width ?? 0, 'focused skip link width').toBeGreaterThanOrEqual(24);
    expect(skipBox?.height ?? 0, 'focused skip link height').toBeGreaterThanOrEqual(24);
    expect(withoutFocusIndicator(samples), `Tab stops without a focus indicator: ${JSON.stringify(samples)}`).toEqual([]);
    expect(labels.some((label) => label.includes('combobox')), `language select is a Tab stop: ${labels.join(', ')}`).toBe(true);
    expect(labels.some((label) => label.includes(LABELS.vi.account)), `user menu is a Tab stop: ${labels.join(', ')}`).toBe(true);
    expect(
      labels.some((label) => label.includes('Bảng điều khiển')),
      `dashboard nav link is a Tab stop: ${labels.join(', ')}`,
    ).toBe(true);
  });

  test.describe('360 px', () => {
    test.use({ viewport: { width: 360, height: 740 } });

    for (const lang of ['vi', 'en'] as const) {
      test(`no element is clipped outside the viewport in ${lang} (top bar, main, realtime strip)`, async ({ page }) => {
        await openShell(page);
        if (lang === 'en') await switchLanguage(page, 'vi', 'en');
        await expectNoElementOverflow(page);
        // The mocked ticket 503 drives the realtime service into connecting/backoff, which renders the strip.
        await expect(page.locator('.realtime-status')).toBeVisible();
        await expectNoElementOverflow(page);
      });
    }

    test('hamburger, language select and user menu: targets >= 24 px, focus indicator on keyboard focus', async ({ page }) => {
      await openShell(page);
      await expectTargetsAtLeast24(page);
      const samples = await tabWalk(page, 4);
      const labels = samples.map((sample) => sample.label);
      expect(labels[0], 'first Tab stop is the skip link').toContain(LABELS.vi.skip);
      expect(labels.some((label) => label.includes(LABELS.vi.open)), `hamburger is a Tab stop: ${labels.join(', ')}`).toBe(true);
      expect(withoutFocusIndicator(samples), `Tab stops without a focus indicator: ${JSON.stringify(samples)}`).toEqual([]);
    });

    test('reduced motion: skip link and realtime strip run no animation; without it the reconnect icon animates', async ({ page }) => {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await openShell(page);
      await expect(page.locator('.realtime-status')).toBeVisible();
      const animations = await page.evaluate(() =>
        ['.shell__skip-link', '.realtime-status', '.realtime-status__icon--moving'].map((selector) => {
          const element = document.querySelector(selector);
          const style = element === null ? null : getComputedStyle(element);
          return { selector, found: element !== null, animationName: style?.animationName, transitionDuration: style?.transitionDuration };
        }),
      );
      for (const entry of animations.filter((item) => item.found)) {
        expect(entry.animationName, `${entry.selector} animation under reduced motion`).toBe('none');
      }
      expect(animations.find((entry) => entry.selector === '.realtime-status__icon--moving')?.found, 'moving icon rendered').toBe(true);

      await page.emulateMedia({ reducedMotion: 'no-preference' });
      const moving = await page.evaluate(() => getComputedStyle(document.querySelector('.realtime-status__icon--moving')!).animationName);
      expect(moving, 'control: the icon animates when motion is allowed').not.toBe('none');
    });
  });
});
