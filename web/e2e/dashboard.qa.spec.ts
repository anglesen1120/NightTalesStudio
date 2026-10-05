import type { Page, Request } from '@playwright/test';
import { expect, test } from './fixtures/csp.fixture';

// QA (10.4.1-T1): adversarial e2e paths the shipped dashboard.spec.ts does not cover — a 429 with Retry-After
// on load disabling Retry until the countdown elapses, a 429 on a focus refresh discarding the fresh page and
// keeping the rows, a server error's traceId + recovery via Retry, a session end mid-load navigating to /login
// without painting an error state, focus while hidden issuing nothing, a focus/visibility burst coalescing to
// one batch, focus listeners stopping once the route is left, the refresh keeping rows (no skeleton flash),
// the aria-live announcement writing once per refresh, an untitled row's accessible name, the all-empty create
// link, and a drifted episode appearing in exactly one group. Backend-free: same stub pattern as
// dashboard.spec.ts; Vietnamese copy.

declare global {
  interface Window {
    __setVisibility?: (value: 'visible' | 'hidden') => void;
    __ntsLiveWrites?: number;
  }
}

test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const LANG = 'vi';
const PAGE_SIZE = 20;
const STATUSES: readonly string[] = ['story_review', 'video_review', 'story_generating', 'rendering', 'ready'];
const EPISODES_URL = /\/api\/v1\/episodes(\?|$)/;

const GROUP = { needsReview: 'Cần bạn xem', inProgress: 'Đang thực hiện', ready: 'Sẵn sàng đăng' } as const;

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
  features: ['youtube_publish'],
};

interface Stubs {
  readonly episodeRequests: Request[];
  readonly urls: () => URL[];
  readonly statuses: () => (string | null)[];
}

type EpisodesMode =
  | { kind: 'items'; items: Record<string, readonly EpisodeDto[]> }
  | { kind: 'http'; status: number; body: unknown; headers?: Record<string, string> };

async function stub(page: Page, mode: () => EpisodesMode): Promise<Stubs> {
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
    const current = mode();
    if (current.kind === 'http') {
      await route.fulfill({
        status: current.status,
        headers: current.headers,
        contentType: 'application/json',
        body: JSON.stringify(current.body),
      });
      return;
    }
    const status = new URL(request.url()).searchParams.get('status') ?? '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: current.items[status] ?? [], nextCursor: null }),
    });
  });
  const urls = (): URL[] => episodeRequests.map((request) => new URL(request.url()));
  return { episodeRequests, urls, statuses: () => urls().map((url) => url.searchParams.get('status')) };
}

const group = (page: Page, heading: string) => page.getByRole('region', { name: heading });
const rows = (page: Page, heading: string) => group(page, heading).getByRole('listitem');

async function openDashboard(
  page: Page,
  items: Record<string, readonly EpisodeDto[]> = ITEMS,
): Promise<Stubs> {
  const stubs = await stub(page, () => ({ kind: 'items', items }));
  await page.goto('/dashboard');
  await expect(page.locator('html')).toHaveAttribute('lang', LANG);
  await expect(group(page, GROUP.needsReview)).toBeVisible();
  return stubs;
}

test.describe('dashboard QA — rate limiting (vi)', () => {
  test('Load429_RetryDisabled_UntilCountdownElapses', async ({ page }) => {
    await page.clock.install();
    let limited = true;
    await stub(page, () =>
      limited
        ? {
            kind: 'http',
            status: 429,
            headers: { 'Retry-After': '30' },
            body: { code: 'rate_limited', status: 429, title: 'rate_limited', traceId: 'NTS-QA' },
          }
        : { kind: 'items', items: ITEMS },
    );
    await page.goto('/dashboard');
    await expect(page.locator('html')).toHaveAttribute('lang', LANG);

    const retry = page.getByRole('button', { name: 'Thử lại' }).last();
    await expect(retry).toBeVisible();
    await expect(retry, 'Retry is disabled while Retry-After counts down').toBeDisabled();
    await page.clock.fastForward(31_000);
    await expect(retry, 'Retry is enabled again once the countdown reaches 0').toBeEnabled();

    limited = false;
    await retry.click();
    await expect(group(page, GROUP.needsReview)).toBeVisible();
    await expect(rows(page, GROUP.ready)).toHaveCount(1);
  });

  test('Refresh429_DiscardsFreshResults_KeepsRows', async ({ page }) => {
    let mode: EpisodesMode = { kind: 'items', items: ITEMS };
    const { statuses } = await stub(page, () => mode);
    await page.goto('/dashboard');
    await expect(group(page, GROUP.needsReview)).toBeVisible();
    await expect.poll(() => statuses().length).toBe(STATUSES.length);

    mode = {
      kind: 'http',
      status: 429,
      headers: { 'Retry-After': '30' },
      body: { code: 'rate_limited', status: 429, title: 'rate_limited', traceId: 'NTS-QA' },
    };
    await page.getByRole('button', { name: 'Làm mới' }).click();
    await expect.poll(() => statuses().length).toBe(STATUSES.length * 2);

    // The failure table: the focus refresh keeps the last good data and shows the partial-refresh notice; the
    // five fresh responses (one of them a 429) are never partially applied.
    await expect(page.getByText('Chưa làm mới được. Đang hiển thị dữ liệu gần nhất.')).toBeVisible();
    await expect(rows(page, GROUP.needsReview)).toHaveCount(2);
    await expect(rows(page, GROUP.ready)).toHaveCount(1);
    await expect(page.locator('nts-error-state')).toHaveCount(0);
  });
});

test.describe('dashboard QA — failure recovery (vi)', () => {
  test('Load500_ShowsTraceId_AndRetryRecovers', async ({ page }) => {
    let failing = true;
    await stub(page, () =>
      failing
        ? {
            kind: 'http',
            status: 500,
            body: { code: 'internal_error', status: 500, title: 'internal_error', traceId: 'NTS-QA-500' },
          }
        : { kind: 'items', items: ITEMS },
    );
    await page.goto('/dashboard');
    await expect(page.locator('html')).toHaveAttribute('lang', LANG);

    await expect(page.getByText('NTS-QA-500'), 'the unknown/server failure shows its trace id').toBeVisible();
    const retry = page.getByRole('button', { name: 'Thử lại' }).first();
    await expect(retry).toBeEnabled();

    failing = false;
    await retry.click();
    await expect(group(page, GROUP.needsReview)).toBeVisible();
    await expect(rows(page, GROUP.needsReview)).toHaveCount(2);
    await expect(rows(page, GROUP.ready)).toHaveCount(1);
  });

  test('SessionEnd_WhileLoading_NavigatesToLogin_NoErrorPainted', async ({ page }) => {
    const episodeRequests: string[] = [];
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
    // The hub ticket ends the session (401 session_revoked) while the dashboard's five calls are pending.
    await page.route('**/api/v1/hubs/ticket', (route) =>
      route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ code: 'session_revoked', status: 401, title: 'session_revoked', traceId: 'NTS-QA' }),
      }),
    );
    await page.route(EPISODES_URL, async (route) => {
      episodeRequests.push(new URL(route.request().url()).searchParams.get('status') ?? '');
      // Hold the answers open until the revoke has raced through.
      await new Promise((resolve) => setTimeout(resolve, 400));
      const status = new URL(route.request().url()).searchParams.get('status') ?? '';
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: ITEMS[status] ?? [], nextCursor: null }),
      });
    });
    await page.goto('/dashboard');

    await expect(page).toHaveURL(/\/login(\?|$)/, { timeout: 15_000 });
    // The revoke can land before or after the deferred URL update commits `/dashboard`: both `/` and
    // `/dashboard` are sanitized same-origin return targets; only a foreign/empty target would be wrong.
    expect(new URL(page.url()).searchParams.get('returnUrl') ?? '').toMatch(/^\/(dashboard)?$/);
    // D6 failure row: the dashboard paints no error state for a session-ended load.
    await expect(page.locator('nts-error-state')).toHaveCount(0);
    await expect(page.getByText('Phiên đã bị thu hồi')).toBeVisible();
    // The revoke races the five episode calls: whether the dashboard activated first or not, at most five
    // scalar-status requests ever went out and none of them painted an error.
    await page.waitForTimeout(500);
    expect(episodeRequests.length, 'at most the five scalar-status calls').toBeLessThanOrEqual(STATUSES.length);
  });
});

test.describe('dashboard QA — refresh discipline (vi)', () => {
  test('RapidFocusAndVisibilityEvents_CoalesceToOneBatch', async ({ page }) => {
    const { statuses } = await openDashboard(page);
    await expect.poll(() => statuses().length).toBe(STATUSES.length);
    await page.waitForTimeout(10_500);

    await page.evaluate(() => {
      window.dispatchEvent(new Event('focus'));
      document.dispatchEvent(new Event('visibilitychange'));
      window.dispatchEvent(new Event('focus'));
      window.dispatchEvent(new Event('focus'));
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await expect.poll(() => statuses().length).toBe(STATUSES.length * 2);
    await page.waitForTimeout(600);
    expect(statuses(), 'a burst of attention events yields exactly one refresh batch').toHaveLength(
      STATUSES.length * 2,
    );
  });

  test('Focus_WhileHidden_IssuesNoRequest_ThenVisibleRefreshes', async ({ page }) => {
    await page.addInitScript(() => {
      let state: 'visible' | 'hidden' = 'visible';
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
    const { statuses } = await openDashboard(page);
    await expect.poll(() => statuses().length).toBe(STATUSES.length);
    await page.waitForTimeout(10_500);

    await page.evaluate(() => window.__setVisibility?.('hidden'));
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await page.waitForTimeout(600);
    expect(statuses(), 'focus while hidden issues no refresh').toHaveLength(STATUSES.length);

    await page.evaluate(() => window.__setVisibility?.('visible'));
    await expect.poll(() => statuses().length).toBe(STATUSES.length * 2);
  });

  test('ListenersRemoved_AfterLeavingTheRoute_NoRefresh', async ({ page }) => {
    const episodeRequests: Request[] = [];
    const dashboardStatuses: (string | null)[] = [];
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
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], nextCursor: null }) }),
    );
    await page.route(EPISODES_URL, async (route) => {
      episodeRequests.push(route.request());
      const status = new URL(route.request().url()).searchParams.get('status');
      if (status !== null) dashboardStatuses.push(status);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: ITEMS[status ?? ''] ?? [], nextCursor: null }),
      });
    });
    await page.goto('/dashboard');
    await expect(group(page, GROUP.needsReview)).toBeVisible();
    await expect.poll(() => dashboardStatuses.length).toBe(STATUSES.length);

    await page.getByRole('link', { name: 'Tập phim', exact: true }).click();
    await expect(page).toHaveURL(/\/episodes/);
    const onEpisodes = dashboardStatuses.length;
    await page.waitForTimeout(10_500);
    await page.evaluate(() => {
      window.dispatchEvent(new Event('focus'));
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await page.waitForTimeout(600);
    expect(dashboardStatuses.length, 'the destroyed dashboard issues no focus refresh').toBe(onEpisodes);
  });

  test('Refresh_KeepsRows_NoSkeletonFlash', async ({ page }) => {
    const { statuses } = await openDashboard(page);
    await expect.poll(() => statuses().length).toBe(STATUSES.length);
    await expect(rows(page, GROUP.needsReview)).toHaveCount(2);

    await page.getByRole('button', { name: 'Làm mới' }).click();
    // Acceptance: the previous rows stay on screen while refreshing — no skeleton, no aria-busy sections.
    await expect(page.locator('nts-loading-state')).toHaveCount(0);
    await expect(page.locator('section[aria-busy="true"]')).toHaveCount(0);
    await expect(rows(page, GROUP.needsReview)).toHaveCount(2);
    await expect.poll(() => statuses().length).toBe(STATUSES.length * 2);
  });

  test('Refresh_AnnouncesCounts_AtMostOncePerRefresh', async ({ page }) => {
    const { statuses } = await openDashboard(page);
    await expect.poll(() => statuses().length).toBe(STATUSES.length);

    await page.evaluate(() => {
      const node = document.querySelector('[aria-live="polite"].dashboard-page__announcement');
      window.__ntsLiveWrites = 0;
      if (node !== null) {
        new MutationObserver(() => {
          window.__ntsLiveWrites = (window.__ntsLiveWrites ?? 0) + 1;
        }).observe(node, { characterData: true, childList: true, subtree: true });
      }
    });
    await page.getByRole('button', { name: 'Làm mới' }).click();
    await expect.poll(() => statuses().length).toBe(STATUSES.length * 2);
    await page.waitForTimeout(400);
    const writes = await page.evaluate(() => window.__ntsLiveWrites ?? -1);
    // The counts sentence is identical on this fixture, so a no-write refresh is legal (the announcement only
    // needs one write per refresh, and an unchanged text must never be written twice).
    expect(writes, 'the aria-live region is written at most once per completed refresh').toBeLessThanOrEqual(1);
  });
});

test.describe('dashboard QA — content edges (vi)', () => {
  test('UntitledRow_AccessibleName_UsesUntitledLabel', async ({ page }) => {
    await openDashboard(page, {
      story_review: [episode(1, 'story_review', null)],
    });
    const section = group(page, GROUP.needsReview);
    await expect(section.getByRole('button', { name: /Chưa đặt tên/ }).first()).toBeVisible();
    await expect(section.getByRole('button', { name: 'Xem truyện Chưa đặt tên' })).toBeVisible();
  });

  test('AllEmpty_ShowsCreateSeriesLink', async ({ page }) => {
    await openDashboard(page, {});
    await expect(page.getByRole('link', { name: 'Tạo series' })).toHaveAttribute('href', '/series/new');
    await expect(page.locator('nts-error-state')).toHaveCount(0);
    await expect(page.locator('nts-empty-state')).toHaveCount(3);
  });

  test('DriftedEpisode_AppearsInExactlyOneGroup', async ({ page }) => {
    // The same id is returned as story_review AND rendering between the five calls: it must appear once, under
    // the more advanced status (rendering → inProgress), never twice.
    await openDashboard(page, {
      story_review: [episode(1, 'story_review', 'Drifted episode')],
      rendering: [episode(1, 'rendering', 'Drifted episode')],
    });
    await expect(rows(page, GROUP.needsReview)).toHaveCount(0);
    await expect(rows(page, GROUP.inProgress)).toHaveCount(1);
    await expect(rows(page, GROUP.inProgress).filter({ hasText: 'Drifted episode' })).toHaveCount(1);
    await expect(rows(page, GROUP.ready)).toHaveCount(0);
    // And the group counts announce 0/1/0, not a double count.
    await expect(page.locator('.dashboard-group__count').first()).toHaveText('0');
  });
});
