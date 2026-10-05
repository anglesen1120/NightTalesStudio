import type { Page } from '@playwright/test';
import { expect, test } from './fixtures/csp.fixture';
import {
  ERRORS_COPY,
  SERIES_COPY,
  SERIES_ID,
  fulfilJson,
  problem,
  seriesDetail,
  seriesListItem,
  stubCatalog,
  stubSeriesApi,
  stubSession,
} from './fixtures/series-stubs';

// Independent QA of 10.4.2-T1 (README D14/D15/D16/D17, task §Failure handling). Backend-free like the other series
// specs: the later `page.route` wins, so a spec overrides one stubbed endpoint without touching the shared fixture.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const L = SERIES_COPY.vi;
const ERR = ERRORS_COPY.vi;

/** D14: nothing in a series screen mentions credits, balance, buying or prices. */
const MONEY = /credit|balance|buy|purchase|top[- ]?up|tín dụng|số dư|mua thêm|nạp|thanh toán|\$\s?\d|\d\s?(?:USD|VND|đ)\b/i;

async function boot(page: Page): Promise<void> {
  await stubSession(page, 'vi');
}

test('List_429_ShowsRetryCountdown_NotAnEmptyState_AndNoSecondRequestBeforeItElapses', async ({ page }) => {
  await boot(page);
  await stubSeriesApi(page, { items: [seriesListItem(1)] });
  let requests = 0;
  await page.route(/\/api\/v1\/series(\?|$)/, (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    requests += 1;
    return fulfilJson(route, 429, problem(429, 'rate_limited'), { 'Content-Type': 'application/problem+json', 'Retry-After': '30' });
  });
  await page.goto('/series');

  await expect(page.getByRole('button', { name: ERR.action.retry })).toBeDisabled();
  await expect(page.getByText(L.list.empty, { exact: true })).toHaveCount(0);
  await page.waitForTimeout(1500);
  expect(requests, 'a rate-limited list is not polled or auto-retried (D17)').toBe(1);
});

test('List_500_ShowsErrorWithRetry_AndRetryRecovers', async ({ page }) => {
  await boot(page);
  await stubSeriesApi(page, { items: [seriesListItem(1, { title: 'Phục hồi' })] });
  let fail = true;
  await page.route(/\/api\/v1\/series(\?|$)/, (route) => {
    if (route.request().method() !== 'GET' || !fail) return route.fallback();
    return fulfilJson(route, 500, problem(500, 'internal_error'), { 'Content-Type': 'application/problem+json' });
  });
  await page.goto('/series');
  const retry = page.getByRole('button', { name: ERR.action.retry });
  await expect(retry).toBeVisible();
  fail = false;
  await retry.click();
  await expect(page.getByText('Phục hồi')).toBeVisible();
  await expect(retry).toHaveCount(0);
});

test('DeepLink_UnknownSeries_ShowsNotFound_WithoutARetryLoopOrIdEcho', async ({ page }) => {
  await boot(page);
  await stubSeriesApi(page);
  let gets = 0;
  await page.route(/\/api\/v1\/series\/[0-9a-f-]{36}(\?|$)/, (route) => {
    gets += 1;
    return fulfilJson(route, 404, problem(404, 'not_found'), { 'Content-Type': 'application/problem+json' });
  });
  await page.goto(`/series/${SERIES_ID}`);
  await expect(page.getByText(L.detail.notFound, { exact: false }).first()).toBeVisible();
  await page.waitForTimeout(1200);
  expect(gets).toBe(1);
  expect(await page.locator('main').innerText()).not.toContain(SERIES_ID);

  await page.goto(`/series/${SERIES_ID}/edit`);
  await expect(page.getByText(L.detail.notFound, { exact: false }).first()).toBeVisible();
  expect(await page.locator('main').innerText()).not.toContain(SERIES_ID);
});

test('Edit_409TopicLocked_RevertsTopic_AndShowsTheServerReason_WithoutLosingTheTitle', async ({ page }) => {
  await boot(page);
  await stubSeriesApi(page, { detail: seriesDetail({ autopilotEnabled: false }) });
  await stubCatalog(page);
  await page.route(/\/api\/v1\/series\/[0-9a-f-]{36}(\?|$)/, (route) => {
    if (route.request().method() !== 'PATCH') return route.fallback();
    return fulfilJson(route, 409, problem(409, 'series_topic_locked'), { 'Content-Type': 'application/problem+json' });
  });
  await page.goto(`/series/${SERIES_ID}/edit`);
  const title = page.getByLabel(L.field.title);
  await title.fill('Tên khác');
  await page.getByRole('radio', { name: L.topic.spiritual }).check();
  await page.getByRole('button', { name: L.save.changes }).first().click();

  await expect(page.getByRole('radio', { name: L.topic.ghost })).toBeChecked();
  await expect(title).toHaveValue('Tên khác');
});

test('Edit_409SeriesArchived_DisablesSave_AndShowsTheArchivedNotice', async ({ page }) => {
  await boot(page);
  await stubSeriesApi(page, { detail: seriesDetail() });
  await stubCatalog(page);
  await page.route(/\/api\/v1\/series\/[0-9a-f-]{36}(\?|$)/, (route) => {
    if (route.request().method() !== 'PATCH') return route.fallback();
    return fulfilJson(route, 409, problem(409, 'series_archived'), { 'Content-Type': 'application/problem+json' });
  });
  await page.goto(`/series/${SERIES_ID}/edit`);
  await page.getByLabel(L.field.title).fill('Tên khác');
  await page.getByRole('button', { name: L.save.changes }).first().click();

  await expect(page.getByText(ERR.series_archived).first()).toBeVisible();
  await expect(page.getByRole('button', { name: L.save.changes }).first()).toBeDisabled();
});

test('Edit_PatchBody_NeverContainsLanguage_AndIfMatchIsTheServerEtagOnly', async ({ page }) => {
  await boot(page);
  const api = await stubSeriesApi(page, { detail: seriesDetail({ etag: '"srv-7"' }) });
  await stubCatalog(page);
  await page.goto(`/series/${SERIES_ID}/edit`);
  await page.getByLabel(L.field.title).fill('Tên khác');
  await page.getByRole('button', { name: L.save.changes }).first().click();
  await expect.poll(() => api.patches.length).toBe(1);

  const request = api.patches[0]!;
  expect(request.headers()['if-match']).toBe('"srv-7"');
  const body = request.postDataJSON() as Record<string, unknown>;
  expect(body).toEqual({ title: 'Tên khác' });
  expect(Object.keys(body)).not.toContain('language');
});

test('D14_NoCreditOrPriceText_OnListEditorOrDetail', async ({ page }) => {
  await boot(page);
  await stubSeriesApi(page, { items: [seriesListItem(1)], detail: seriesDetail() });
  await stubCatalog(page);

  await page.goto('/series');
  await expect(page.locator('main ul.series-list__rows > li')).toHaveCount(1);
  expect(await page.locator('main').innerText()).not.toMatch(MONEY);

  await page.goto(`/series/${SERIES_ID}/edit`);
  for (let step = 1; step <= 4; step += 1) {
    expect(await page.locator('main').innerText(), `edit step ${step}`).not.toMatch(MONEY);
    if (step < 4) await page.getByRole('button', { name: L.editor.next }).click();
  }

  await page.goto(`/series/${SERIES_ID}`);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  expect(await page.locator('main').innerText()).not.toMatch(MONEY);
});
