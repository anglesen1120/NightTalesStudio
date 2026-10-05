import type { Page, Request } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';

// 10.5.1-T1 (README D15/D16/D17, task file §Acceptance checks, Vietnamese copy). Backend-free: every endpoint is stubbed
// with `page.route` (same session stubs as shell.spec.ts), so the spec needs no seeded account or TOTP.
// Recorded deviation: no `/episodes/:id` route exists until 10.5.2-T1 and the router rejects unknown paths, so
// "open an episode" asserts the row link target (`href`) and does not click it.

test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const LANG = 'vi';
const L = {
  loadMore: /Tải thêm/,
  statusFilter: /Trạng thái/,
  storyReview: /Chờ bạn duyệt/,
  nav: /Tập/,
  date: /0?3\/0?10\/2026/,
};

const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const PAGE_SIZE = 20;
const EPISODES_URL = /\/api\/v1\/episodes(\?|$)/;

function uuid(n: number): string {
  return `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

function episode(n: number, status: string) {
  return {
    id: uuid(n),
    seriesId: SERIES_ID,
    title: n % 7 === 0 ? null : `Episode ${n}`,
    status,
    lengthTier: 'standard',
    createdAt: '2026-10-01T12:00:00Z',
    updatedAt: '2026-10-03T12:00:00Z',
    etag: `"e${n}"`,
  };
}

// 25 unfiltered episodes (page 1: 20, page 2: 5) and 3 story_review episodes.
const ALL = Array.from({ length: 25 }, (_v, i) => episode(i + 1, i % 3 === 0 ? 'story_review' : 'draft'));
const REVIEW = [episode(101, 'story_review'), episode(102, 'story_review'), episode(103, 'story_review')];

function fakeAccessToken(): string {
  const encode = (value: object): string => Buffer.from(JSON.stringify(value)).toString('base64url');
  return `${encode({ alg: 'none', typ: 'JWT' })}.${encode({ sub: 'e2e-owner', role: 'owner', amr: ['pwd', 'otp'] })}.signature`;
}

const ME_DTO = {
  id: 'e2e-owner',
  displayName: 'Owner',
  email: 'owner@example.test',
  uiLocale: LANG,
  role: 'owner',
  mfaEnabled: true,
  lastLoginAt: null,
  features: [],
};

interface Stubs {
  readonly episodeRequests: Request[];
  readonly urls: () => URL[];
}

async function stub(page: Page): Promise<Stubs> {
  const episodeRequests: Request[] = [];
  await page.route('**/api/v1/auth/refresh', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ accessToken: fakeAccessToken(), expiresAt: new Date(Date.now() + 3_600_000).toISOString() }),
    }),
  );
  await page.route('**/api/v1/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME_DTO) }),
  );
  await page.route('**/api/v1/hubs/ticket', (route) =>
    route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 503, code: 'unavailable' }) }),
  );
  await page.route(/\/api\/v1\/series(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        items: [{ id: SERIES_ID, title: 'Series A', topic: 'ghost', archivedAt: null }],
        nextCursor: null,
      }),
    }),
  );
  await page.route(EPISODES_URL, async (route) => {
    const request = route.request();
    episodeRequests.push(request);
    const url = new URL(request.url());
    const cursor = url.searchParams.get('cursor');
    const status = url.searchParams.get('status');
    let body: { items: unknown[]; nextCursor: string | null };
    if (status === 'story_review') {
      body = { items: REVIEW, nextCursor: null };
    } else if (cursor === 'c2') {
      // Slow enough that a second click lands while this request is still in flight.
      await new Promise((resolve) => setTimeout(resolve, 400));
      body = { items: ALL.slice(PAGE_SIZE), nextCursor: null };
    } else {
      body = { items: ALL.slice(0, PAGE_SIZE), nextCursor: 'c2' };
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  return { episodeRequests, urls: () => episodeRequests.map((request) => new URL(request.url())) };
}

const rows = (page: Page) => page.locator('main a[href^="/episodes/"]');
const loadMore = (page: Page) => page.getByRole('button', { name: L.loadMore });

async function openList(page: Page, path = '/episodes'): Promise<Stubs> {
  const stubs = await stub(page);
  await page.goto(path);
  await expect(page.locator('html')).toHaveAttribute('lang', LANG);
  await expect(rows(page).first()).toBeVisible();
  return stubs;
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

async function chooseStatus(page: Page, label: RegExp): Promise<void> {
  await page.getByRole('combobox', { name: L.statusFilter }).click();
  await page.getByRole('option', { name: label }).click();
}

test.describe('episodes list (Vietnamese)', () => {
  test('first page: 20 newest-first rows, side-nav link is current, dates use the vi-VN format', async ({ page }) => {
    const { urls } = await openList(page);
    await expect(rows(page)).toHaveCount(PAGE_SIZE);
    expect(urls()).toHaveLength(1);
    expect(urls()[0]?.searchParams.get('limit')).toBe(String(PAGE_SIZE));
    const link = page.getByRole('navigation').getByRole('link', { name: L.nav });
    await expect(link).toHaveAttribute('aria-current', 'page');
    await expect(rows(page).first()).toContainText(L.date);
    expect(new Set(await rows(page).evaluateAll((els) => els.map((el) => el.getAttribute('href')))).size).toBe(PAGE_SIZE);
  });

  test('filter by story_review, load more with two rapid clicks, open an episode', async ({ page }) => {
    const { urls } = await openList(page);

    // Load more: two clicks in the same task issue exactly one page-2 request; no duplicate ids.
    const button = loadMore(page);
    await expect(button).toBeVisible();
    await button.evaluate((el) => {
      el.click();
      el.click();
    });
    await expect(rows(page)).toHaveCount(ALL.length);
    expect(urls().filter((url) => url.searchParams.get('cursor') === 'c2')).toHaveLength(1);
    const hrefs = await rows(page).evaluateAll((els) => els.map((el) => el.getAttribute('href')));
    expect(new Set(hrefs).size, 'no duplicate ids after load more').toBe(ALL.length);
    await expect(loadMore(page)).toHaveCount(0);

    // Filter: the URL carries the status and the list restarts at page 1 with the filtered rows.
    await chooseStatus(page, L.storyReview);
    await expect(page).toHaveURL(/\/episodes\?status=story_review$/);
    await expect(rows(page)).toHaveCount(REVIEW.length);
    const filtered = urls().filter((url) => url.searchParams.get('status') === 'story_review');
    expect(filtered).toHaveLength(1);
    expect(filtered[0]?.searchParams.get('cursor')).toBeNull();

    // Open: the row link targets /episodes/<id> (the detail route ships with 10.5.2-T1).
    await expect(rows(page).first()).toHaveAttribute('href', `/episodes/${REVIEW[0]?.id}`);
  });

  test('reloading /episodes?status=story_review restores the filtered list; unknown values are ignored', async ({ page }) => {
    const { urls } = await openList(page, '/episodes?status=story_review');
    await expect(rows(page)).toHaveCount(REVIEW.length);
    expect(urls().every((url) => url.searchParams.get('status') === 'story_review')).toBe(true);
    await page.goto('/episodes?status=bogus&series=not-a-uuid');
    await expect(rows(page).first()).toBeVisible();
    const last = urls().at(-1);
    expect(last?.searchParams.get('status')).toBeNull();
    expect(last?.searchParams.get('seriesId')).toBeNull();
  });

  test('axe (WCAG 2.1 AA) on /episodes and on the filtered state', async ({ page }) => {
    await openList(page);
    await expectA11yClean(page);
    await chooseStatus(page, L.storyReview);
    await expect(page).toHaveURL(/status=story_review/);
    await expect(rows(page)).toHaveCount(REVIEW.length);
    await expectA11yClean(page);
  });

  test('keyboard: Tab order is filters, then rows, then load more', async ({ page }) => {
    await openList(page);
    await page.getByRole('combobox').first().focus();
    const kinds: string[] = [];
    for (let i = 0; i < 40; i++) {
      await page.keyboard.press('Tab');
      kinds.push(
        await page.evaluate(() => {
          const el = document.activeElement;
          if (el === null) return 'none';
          if (el.getAttribute('role') === 'combobox') return 'filter';
          if (el instanceof HTMLAnchorElement && el.getAttribute('href')?.startsWith('/episodes/')) return 'row';
          if (el instanceof HTMLButtonElement && el.closest('main') !== null) return 'button';
          return 'other';
        }),
      );
    }
    const firstRow = kinds.indexOf('row');
    const lastRow = kinds.lastIndexOf('row');
    const lastFilter = kinds.lastIndexOf('filter');
    const buttonAfterRows = kinds.indexOf('button', lastRow);
    expect(firstRow, `rows are reachable: ${kinds.join(',')}`).toBeGreaterThanOrEqual(0);
    expect(lastFilter, 'the second filter precedes the first row').toBeLessThan(firstRow === -1 ? 0 : firstRow);
    expect(buttonAfterRows, `load more follows the rows: ${kinds.join(',')}`).toBeGreaterThan(lastRow);
  });

  test('no console CSP / Trusted Types violations (the fixture fails the test on any)', async ({ page }) => {
    await openList(page);
    await chooseStatus(page, L.storyReview);
    await expect(rows(page)).toHaveCount(REVIEW.length);
  });
});

test.describe('episodes list (360 px, Vietnamese)', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  test('no horizontal scroll on the list, after load more, and when filtered', async ({ page }) => {
    await openList(page);
    await expectNoHorizontalScroll(page);
    await loadMore(page).click();
    await expect(rows(page)).toHaveCount(ALL.length);
    await expectNoHorizontalScroll(page);
    await chooseStatus(page, L.storyReview);
    await expect(rows(page)).toHaveCount(REVIEW.length);
    await expectNoHorizontalScroll(page);
  });
});
