import type { Page, Request } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';

// 10.4.1-T1 (README D11/D14/D15/D16/D17, task file §Acceptance checks + §Tests, English copy). Backend-free: every
// endpoint is stubbed with `page.route` (the same session stubs as shell.spec.ts / episodes-list-en.spec.ts), so the
// spec needs no seeded account or TOTP. This is the `en` smoke variant (groups, empty state, no credit UI, axe); the
// request-count, refresh and hidden-document behaviours are asserted in dashboard.spec.ts.

test.use({ locale: 'en-US', timezoneId: 'UTC' });

const LANG = 'en';
const EPISODES_URL = /\/api\/v1\/episodes(\?|$)/;

// Section headings and inline empty messages (contract item 8, scope `dashboard`).
const GROUP = { needsReview: 'Needs your review', inProgress: 'In progress', ready: 'Ready to publish' } as const;
const EMPTY = {
  needsReview: 'Nothing needs your review',
  inProgress: 'Nothing is in progress',
  ready: 'Nothing is ready yet',
} as const;
// `dashboard.card.action.*` (button text).
const ACTION = { reviewStory: 'Review story', reviewVideo: 'Review video', open: 'Open', publish: 'Publish' } as const;

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

async function stub(page: Page, items: Record<string, readonly EpisodeDto[]>): Promise<Request[]> {
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
  return episodeRequests;
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

async function openDashboard(page: Page, items: Record<string, readonly EpisodeDto[]> = ITEMS): Promise<void> {
  await stub(page, items);
  await page.goto('/dashboard');
  await expect(page.locator('html')).toHaveAttribute('lang', LANG);
  await expect(group(page, GROUP.needsReview)).toBeVisible();
}

test.describe('dashboard (en)', () => {
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

  test('axe (WCAG 2.1 AA) on /dashboard (en)', async ({ page }) => {
    await openDashboard(page);
    await expectA11yClean(page);
  });
});
