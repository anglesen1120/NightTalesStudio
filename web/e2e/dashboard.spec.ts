import type { Page, Request } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';

// 10.4.1-T1 (README D11/D14/D15/D16/D17, task file §Acceptance checks + §Tests, Vietnamese copy). Backend-free:
// every endpoint is stubbed with `page.route` (the same session stubs as shell.spec.ts / episodes-list.spec.ts), so
// the spec needs no seeded account or TOTP. `/dashboard` issues exactly five scalar-status episode requests, one per
// status in `DASHBOARD_STATUSES`, and the store de-duplicates + groups the results client-side.
// The `en` smoke variant lives in dashboard-en.spec.ts; the request-count, refresh and hidden-document behaviours
// are asserted here only.

declare global {
  interface Window {
    /** Installed by the hidden-document `addInitScript` in `HiddenDocument_IssuesNoRequest`. */
    __setVisibility?: (value: 'visible' | 'hidden') => void;
  }
}

test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const LANG = 'vi';
const PAGE_SIZE = 20;
// `DASHBOARD_STATUSES` request order (features/dashboard/dashboard.model.ts).
const STATUSES: readonly string[] = ['story_review', 'video_review', 'story_generating', 'rendering', 'ready'];
const EPISODES_URL = /\/api\/v1\/episodes(\?|$)/;

// Section headings and inline empty messages (contract item 8, scope `dashboard`).
const GROUP = { needsReview: 'Cần bạn xem', inProgress: 'Đang thực hiện', ready: 'Sẵn sàng đăng' } as const;
const EMPTY = {
  needsReview: 'Không có gì cần bạn xem',
  inProgress: 'Không có tập nào đang chạy',
  ready: 'Chưa có tập nào sẵn sàng',
} as const;
// `dashboard.card.action.*` (button text).
const ACTION = { reviewStory: 'Xem truyện', reviewVideo: 'Xem video', open: 'Mở', publish: 'Đăng' } as const;

const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const CREATED_AT = '2026-10-01T12:00:00Z';

interface EpisodeDto {
  id: string;
  seriesId: string;
  title: string | null;
  status: string;
  lengthTier: string;
  createdAt: string;
  updatedAt: string;
  etag: string;
}

function episode(n: number, status: string, title: string | null): EpisodeDto {
  return {
    id: `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    seriesId: SERIES_ID,
    title,
    status,
    lengthTier: 'standard',
    createdAt: CREATED_AT,
    updatedAt: `2026-10-0${(n % 9) + 1}T12:00:00Z`,
    etag: `"e${n}"`,
  };
}

// One episode per dashboard status: two under "Needs your review" (story_review, video_review), two under
// "In progress" (story_generating, rendering) and one "Ready to publish".
const ITEMS: Record<string, readonly EpisodeDto[]> = {
  story_review: [episode(1, 'story_review', 'Story review episode')],
  video_review: [episode(2, 'video_review', 'Video review episode')],
  story_generating: [episode(3, 'story_generating', 'Story generating episode')],
  rendering: [episode(4, 'rendering', 'Rendering episode')],
  ready: [episode(5, 'ready', 'Ready episode')],
};

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
  // A ready episode gets the "Publish" action only when the session carries the publish feature.
  features: ['youtube_publish'],
};

interface Stubs {
  readonly episodeRequests: Request[];
  readonly urls: () => URL[];
  readonly statuses: () => (string | null)[];
}

async function stub(page: Page, items: Record<string, readonly EpisodeDto[]>): Promise<Stubs> {
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
  await page.route(EPISODES_URL, async (route) => {
    const request = route.request();
    episodeRequests.push(request);
    const status = new URL(request.url()).searchParams.get('status') ?? '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: items[status] ?? [], nextCursor: null }),
    });
  });
  const urls = (): URL[] => episodeRequests.map((request) => new URL(request.url()));
  return { episodeRequests, urls, statuses: () => urls().map((url) => url.searchParams.get('status')) };
}

// Each group is a `<section aria-labelledby>` with a heading, so it is a named `region` landmark; rows are `<li>`.
const group = (page: Page, heading: string) => page.getByRole('region', { name: heading });
const rows = (page: Page, heading: string) => group(page, heading).getByRole('listitem');

/** Primary actions are buttons or links whose accessible name is `<verb> <title>` (`card.actionLabel.*`). */
function primaryAction(page: Page, heading: string, label: string, title: string) {
  const section = group(page, heading);
  const name = `${label} ${title}`;
  return section.getByRole('button', { name }).or(section.getByRole('link', { name })).first();
}

async function openDashboard(page: Page, items: Record<string, readonly EpisodeDto[]> = ITEMS): Promise<Stubs> {
  const stubs = await stub(page, items);
  await page.goto('/dashboard');
  await expect(page.locator('html')).toHaveAttribute('lang', LANG);
  await expect(group(page, GROUP.needsReview)).toBeVisible();
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

test.describe('dashboard (vi)', () => {
  test('Groups_ShowExpectedEpisodes', async ({ page }) => {
    await openDashboard(page);

    // story_review → needsReview ("Review story"), video_review → needsReview ("Review video").
    await expect(rows(page, GROUP.needsReview)).toHaveCount(2);
    await expect(rows(page, GROUP.needsReview).filter({ hasText: 'Story review episode' })).toHaveCount(1);
    await expect(rows(page, GROUP.needsReview).filter({ hasText: 'Video review episode' })).toHaveCount(1);
    await expect(primaryAction(page, GROUP.needsReview, ACTION.reviewStory, 'Story review episode')).toBeVisible();
    await expect(primaryAction(page, GROUP.needsReview, ACTION.reviewVideo, 'Video review episode')).toBeVisible();

    // story_generating / rendering → inProgress ("Open").
    await expect(rows(page, GROUP.inProgress)).toHaveCount(2);
    await expect(primaryAction(page, GROUP.inProgress, ACTION.open, 'Story generating episode')).toBeVisible();
    await expect(primaryAction(page, GROUP.inProgress, ACTION.open, 'Rendering episode')).toBeVisible();

    // ready → the ready group, "Publish" because the owner session carries `youtube_publish`.
    await expect(rows(page, GROUP.ready)).toHaveCount(1);
    await expect(primaryAction(page, GROUP.ready, ACTION.publish, 'Ready episode')).toBeVisible();
  });

  test('EmptyGroup_ShowsMessage', async ({ page }) => {
    await openDashboard(page, { story_review: ITEMS.story_review });
    await expect(rows(page, GROUP.needsReview)).toHaveCount(1);
    await expect(group(page, GROUP.inProgress).getByText(EMPTY.inProgress)).toBeVisible();
    await expect(group(page, GROUP.ready).getByText(EMPTY.ready)).toBeVisible();
  });

  test('Load_IssuesExactlyFiveScalarStatusRequests', async ({ page }) => {
    const { statuses, urls } = await openDashboard(page);
    await expect.poll(() => statuses().length).toBe(STATUSES.length);
    expect(new Set(statuses()).size, 'one request per status').toBe(STATUSES.length);
    expect([...statuses()].sort()).toEqual([...STATUSES].sort());
    for (const url of urls()) {
      expect(url.searchParams.getAll('status'), 'exactly one status parameter per request').toHaveLength(1);
      const status = url.searchParams.get('status') ?? '';
      expect(status, 'the status is scalar, never comma-separated').not.toContain(',');
      expect(STATUSES.includes(status), 'the status is one of DASHBOARD_STATUSES').toBe(true);
      expect(url.searchParams.get('limit'), 'the dashboard page size').toBe(String(PAGE_SIZE));
    }
    await page.waitForTimeout(500);
    expect(statuses(), 'no request beyond the five on first load').toHaveLength(STATUSES.length);
  });

  test('RefreshOnFocus_AtMostOncePer10s', async ({ page }) => {
    const { statuses } = await openDashboard(page);
    await expect.poll(() => statuses().length).toBe(STATUSES.length);

    // Inside the 10 s throttle window a focus + visibility change issues nothing.
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await page.waitForTimeout(600);
    expect(statuses(), 'no refresh inside the throttle window').toHaveLength(STATUSES.length);

    // Past the window exactly one coalesced refresh batch of five requests is issued.
    await page.waitForTimeout(10_500);
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await expect.poll(() => statuses().length).toBe(STATUSES.length * 2);
    await page.waitForTimeout(600);
    expect(statuses(), 'the refresh is coalesced to one batch').toHaveLength(STATUSES.length * 2);
    expect(statuses().filter((status) => status === 'ready'), 'the refresh repeats every status').toHaveLength(2);
  });

  test('HiddenDocument_IssuesNoRequest', async ({ page }) => {
    // Emulate a hidden document before any app script runs; the page defers its first load until visible.
    await page.addInitScript(() => {
      let state: 'visible' | 'hidden' = 'hidden';
      const apply = (): void => {
        Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
        Object.defineProperty(document, 'hidden', { configurable: true, get: () => state === 'hidden' });
      };
      apply();
      window.__setVisibility = (value) => {
        state = value;
        apply();
        document.dispatchEvent(new Event('visibilitychange'));
      };
    });
    const { statuses } = await stub(page, ITEMS);
    await page.goto('/dashboard');
    await expect(page.locator('html')).toHaveAttribute('lang', LANG);
    await page.waitForTimeout(800);
    expect(statuses(), 'no request while the document stays hidden').toHaveLength(0);

    await page.evaluate(() => window.__setVisibility?.('visible'));
    await expect.poll(() => statuses().length).toBe(STATUSES.length);
  });

  test('NoCreditUiInPersonalPhase', async ({ page }) => {
    await openDashboard(page);
    await expect(page.locator('cost-line, .cost-line, [data-slot=cost-line]')).toHaveCount(0);
    const texts = [await page.getByRole('main').innerText(), await page.getByRole('banner').innerText()];
    for (const text of texts) {
      expect(text, 'no credit, balance or buy copy in the personal-phase UI').not.toMatch(
        /tín dụng|số dư|credit|balance|mua|buy/i,
      );
    }
  });

  test('axe (WCAG 2.1 AA) on /dashboard (vi)', async ({ page }) => {
    await openDashboard(page);
    await expectA11yClean(page);
  });
});

test.describe('dashboard (360 px, vi)', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  test('no horizontal scroll on /dashboard at 360 px', async ({ page }) => {
    await openDashboard(page);
    await expectNoHorizontalScroll(page);
  });
});
