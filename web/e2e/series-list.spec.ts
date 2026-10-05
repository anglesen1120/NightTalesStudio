import type { Page } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import {
  CORE_COPY,
  SERIES_COPY,
  SERIES_LIST_PAGE_SIZE,
  liveRegion,
  seriesListItem,
  stubSeriesApi,
  stubSession,
  type SeriesApiStubOptions,
} from './fixtures/series-stubs';

// 10.4.2-T1 (README D15/D16, task file §Tests `series-list.spec.ts` and Acceptance checks 1-3).
// Backend-free: the session and every `/api/v1/series` endpoint are stubbed with `page.route`
// (`fixtures/series-stubs.ts`), so the spec needs no seeded account or TOTP. Copy comes from the shipped
// `assets/i18n/series/vi.json` bundle through `SERIES_COPY`.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const L = SERIES_COPY.vi;
const CORE = CORE_COPY.vi;

/** The "Open series" prefix, so a per-row accessible name can be matched without re-typing copy. */
const OPEN_NAME = `${L.row.openNamed} `;

/** 30 series: page 1 holds the first 25, page 2 the remaining 5. */
const TOTAL = 30;
const ITEMS = Array.from({ length: TOTAL }, (_value, index) => seriesListItem(index + 1, { title: `Series ${index + 1}` }));
const ARCHIVED_ITEM = seriesListItem(99, { title: 'Series đã lưu trữ', archivedAt: '2026-10-04T12:00:00Z' });

const rows = (page: Page) => page.locator('main ul.series-list__rows > li');
const loadMore = (page: Page) => page.getByRole('button', { name: L.list.loadMore });

async function openList(
  page: Page,
  { path = '/series', ...apiOptions }: { readonly path?: string } & SeriesApiStubOptions = {},
) {
  await stubSession(page, 'vi');
  const api = await stubSeriesApi(page, apiOptions);
  await page.goto(path);
  await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
  return api;
}

test('List_Paginates: load more appends the next page, disappears at nextCursor null, one request per click', async ({ page }) => {
  const api = await openList(page, { items: ITEMS });
  await expect(rows(page)).toHaveCount(SERIES_LIST_PAGE_SIZE);

  // First page: one request, no cursor, the documented limit.
  const firstPage = api.urlsOf(api.listRequests);
  expect(firstPage).toHaveLength(1);
  expect(firstPage[0]?.searchParams.get('limit')).toBe(String(SERIES_LIST_PAGE_SIZE));
  expect(firstPage[0]?.searchParams.get('cursor')).toBeNull();

  // Two clicks in the same task: the single-flight guard issues exactly one page-2 request.
  const button = loadMore(page);
  await expect(button).toBeVisible();
  await button.evaluate((element: HTMLElement) => {
    element.click();
    element.click();
  });
  await expect(rows(page)).toHaveCount(TOTAL);
  expect(api.urlsOf(api.listRequests).filter((url) => url.searchParams.get('cursor') === 'c2')).toHaveLength(1);
  expect(api.listRequests, 'one request per click, none for the second').toHaveLength(2);

  // Every series appears once: two "Open series <title>" buttons per row, one distinct name per series.
  const openNames = await page
    .getByRole('button', { name: new RegExp(`^${OPEN_NAME}`) })
    .evaluateAll((elements) => elements.map((element) => element.getAttribute('aria-label') ?? ''));
  expect(openNames).toHaveLength(TOTAL * 2);
  expect(new Set(openNames).size).toBe(TOTAL);

  // `nextCursor === null`: load more is gone, a further click cannot exist.
  await expect(loadMore(page)).toHaveCount(0);
});

test('Archive_RemovesRow: ConfirmDialog then the row leaves the list', async ({ page }) => {
  const api = await openList(page, { items: ITEMS });
  await expect(rows(page)).toHaveCount(SERIES_LIST_PAGE_SIZE);

  const archiveName = `${L.row.archiveNamed} Series 1`;
  // `exact: true`: the accessible name is a substring of "Series 10"–"Series 19" otherwise.
  const archiveButton = page.getByRole('button', { name: archiveName, exact: true });
  await archiveButton.click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(L.archive.title);
  await expect(dialog).toContainText('Series 1');

  await dialog.getByRole('button', { name: L.row.archive }).click();
  await expect(rows(page)).toHaveCount(SERIES_LIST_PAGE_SIZE - 1);
  expect(api.deletes, 'one DELETE for the confirmed row').toHaveLength(1);
  await expect(archiveButton).toHaveCount(0);
  await expect(liveRegion(page, L.archive.done).first()).toContainText(L.archive.done);
});

test('Archive_RemovesRow (failing DELETE): the row returns and series.archive.failed is announced', async ({ page }) => {
  const api = await openList(page, { items: ITEMS, deleteStatus: 500 });
  await expect(rows(page)).toHaveCount(SERIES_LIST_PAGE_SIZE);

  const archiveName = `${L.row.archiveNamed} Series 1`;
  const archiveButton = page.getByRole('button', { name: archiveName, exact: true });
  await archiveButton.click();
  await page.getByRole('dialog').getByRole('button', { name: L.row.archive }).click();

  // The DELETE settles, the optimistic removal is rolled back and the failure is announced politely.
  await expect.poll(() => api.deletes.length).toBe(1);
  await expect(rows(page)).toHaveCount(SERIES_LIST_PAGE_SIZE);
  await expect(archiveButton).toHaveCount(1);
  await expect(liveRegion(page, L.archive.failed).first()).toContainText(L.archive.failed);
  await expect(liveRegion(page, L.archive.done)).toHaveCount(0);
});

test('LiteralBraceTitles_RenderVerbatim_AndArchiveDialogShowsTitleAsText (PS1)', async ({ page }) => {
  // Titles are user-controlled: they must never be Transloco params — the transpiler re-interpolates
  // substituted values, so "{{title}}" used to loop it forever and freeze the list and the dialog.
  const titles = ['{{title}}', '{{ x }}'];
  await openList(page, { items: titles.map((title, index) => seriesListItem(index + 1, { title })) });
  await expect(rows(page)).toHaveCount(2);
  for (const title of titles) {
    const row = rows(page).filter({ hasText: title });
    await expect(row).toHaveCount(1);
    await expect(row.locator('.series-row__title')).toHaveText(title);
    await expect(row.getByRole('button', { name: `${L.row.openNamed} ${title}`, exact: true }).first()).toBeVisible();
  }

  await page.getByRole('button', { name: `${L.row.archiveNamed} {{title}}`, exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(L.archive.title);
  await expect(dialog).toContainText(L.archive.confirm);
  await expect(dialog.locator('.confirm-dialog__subject')).toHaveText('{{title}}');
  await dialog.getByRole('button', { name: CORE.core.ui.confirm.cancel }).click();
  await expect(dialog).toHaveCount(0);
});


test('includeArchived: archived rows are hidden by default, the toggle shows them with the archived chip', async ({ page }) => {
  const api = await openList(page, { items: [...ITEMS.slice(0, 2), ARCHIVED_ITEM] });
  await expect(rows(page)).toHaveCount(2);
  await expect(rows(page).filter({ hasText: L.list.archived })).toHaveCount(0);

  await page.getByRole('checkbox', { name: L.list.includeArchived }).check();
  await expect(rows(page)).toHaveCount(3);
  await expect(rows(page).filter({ hasText: L.list.archived })).toHaveCount(1);
  const lastRequest = api.urlsOf(api.listRequests).at(-1);
  expect(lastRequest?.searchParams.get('includeArchived')).toBe('true');
  expect(lastRequest?.searchParams.get('cursor')).toBeNull();
});

test('axe (WCAG 2.1 AA) on /series', async ({ page }) => {
  await openList(page, { items: ITEMS });
  await expect(rows(page)).toHaveCount(SERIES_LIST_PAGE_SIZE);
  await expectA11yClean(page);
});
