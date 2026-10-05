import type { Page } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, expectNoExtraDetails, NEW_EPISODE_PATH, SERIES_ID, stubEpisodes, watchPrivacy, type EpisodeStubOptions, type EpisodeStubs } from './fixtures/episodes-stubs';
import { liveRegion } from './fixtures/series-stubs';

// 10.5.2-T1: stub-only, same session/locale/axe/CSP footprint as episodes-list.spec.ts.
// Real compose + mock-mpt/editor loading is owned by 11.5.1-T1; editor/research URLs by 10.5.3-T1/10.5.5-T1.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });
const L = {
  lang: 'vi', brief: 'Ý tưởng', title: 'Tiêu đề (không bắt buộc)', submit: 'Viết kịch bản', retry: 'Thử lại',
  trueCrimeNext: 'Tạo tập phim và thêm nguồn',
  trueCrimeNotice: 'Tập tội phạm thật cần nguồn và bảng dữ kiện được duyệt trước khi viết.',
  invalid: 'Bản nháp không hợp lệ. Hãy thử lại.', limit: /giới hạn.*5|5.*giới hạn/i,
  countdown: /\d+/,
  stages: ['Đang xếp hàng', 'Đang viết kịch bản…', 'Đang kiểm tra…', 'Đang tự đánh giá…', 'Đang sửa lần cuối…', 'Đang kiểm tra trùng lặp…', 'Đang lưu…'],
};
const BRIEF = 'Một ngọn đèn trong ngôi nhà bỏ hoang chỉ sáng khi không có người ở đó.';
const STAGES = ['queued', 'writing', 'checking', 'reviewing', 'revising', 'scoring', 'saving'] as const;

async function open(page: Page, options: EpisodeStubOptions = {}): Promise<{ stubs: EpisodeStubs; privacy: () => Promise<void> }> {
  const privacy = watchPrivacy(page, BRIEF);
  const stubs = await stubEpisodes(page, { lang: 'vi', ...options });
  await page.goto(NEW_EPISODE_PATH);
  await expect(page.locator('html')).toHaveAttribute('lang', L.lang);
  await expect(page.getByRole('textbox', { name: L.brief, exact: true })).toBeVisible();
  await expect.poll(stubs.connected).toBe(1);
  return { stubs, privacy };
}

async function submit(page: Page): Promise<void> {
  await page.getByRole('textbox', { name: L.brief, exact: true }).fill(BRIEF);
  await page.getByRole('button', { name: L.submit, exact: true }).click();
}

function expectInitialOrdering(stubs: EpisodeStubs): void {
  expect(stubs.entries('series')).toHaveLength(1);
  expect(stubs.entries('create')).toHaveLength(1);
  expect(stubs.entries('create')[0]?.method).toBe('POST');
  expect(stubs.entries('create')[0]?.idempotencyKey).toBeNull();
  expect(stubs.entries('generate')).toHaveLength(1);
  const generate = stubs.entries('generate')[0]!;
  expect(generate.method).toBe('POST');
  expect(generate.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
  expect(stubs.entries('ticket')).toHaveLength(1);
  expect(stubs.entries('websocket')).toHaveLength(1);
  expect(stubs.entries('ticket')[0]!.order).toBeLessThan(generate.order);
  expect(stubs.entries('websocket')[0]!.order).toBeLessThan(generate.order);
  expect(stubs.entries('detail')).toHaveLength(1);
  expect(stubs.entries('detail')[0]!.order).toBeGreaterThan(generate.order);
}

test.describe('new episode generation (vi, stubbed)', () => {
  test('fiction: one create, subscribed transport before generate, seed and ids-only live stages without polling', async ({ page }) => {
    const { stubs, privacy } = await open(page, { details: STAGES.map((stage) => episodeDetail(stage)) });
    await expectA11yClean(page);
    await submit(page);
    await expect(liveRegion(page, L.stages[0]!)).toContainText(L.stages[0]!);
    expectInitialOrdering(stubs);
    await expectNoExtraDetails(page, stubs, 1);
    for (let index = 1; index < STAGES.length; index += 1) {
      stubs.advance();
      // One deliberately misleading block proves GET, not the event's generation, supplies the stage.
      stubs.push(undefined, index === 2 ? { generation: { stage: 'queued', status: 'queued' } } : {});
      const stage = liveRegion(page, L.stages[index]!);
      await expect(stage).toContainText(L.stages[index]!);
      await expect(stage).toHaveAttribute('aria-live', 'polite');
      expect(stubs.entries('detail')).toHaveLength(index + 1);
      expect(stubs.entries('detail').at(-1)!.order).toBeGreaterThan(stubs.entries('event').at(-1)!.order);
      await expectNoExtraDetails(page, stubs, index + 1);
      await expectA11yClean(page);
    }
    expect(stubs.entries('create')).toHaveLength(1);
    expect(stubs.entries('generate')).toHaveLength(1);
    await privacy();
  });

  test('terminal invalid output announces an alert and retry rotates the key, not the episode', async ({ page }) => {
    const failed = episodeDetail('done', { status: 'draft', generation: { kind: 'story', status: 'failed', stage: 'done', errorCode: 'story_invalid_output', startedAt: null } });
    const { stubs, privacy } = await open(page, { details: [episodeDetail(), failed, episodeDetail('writing')] });
    await submit(page);
    await expect(liveRegion(page, L.stages[0]!)).toBeVisible();
    stubs.advance();
    stubs.push();
    await expect(page.getByRole('alert')).toContainText(L.invalid);
    const retry = page.getByRole('button', { name: L.retry, exact: true });
    await expect(retry).toBeEnabled();
    await expectA11yClean(page);
    const key = stubs.entries('generate')[0]!.idempotencyKey;
    stubs.advance();
    await retry.click();
    await expect(liveRegion(page, L.stages[1]!)).toBeVisible();
    expect(stubs.entries('generate')).toHaveLength(2);
    expect(stubs.entries('generate')[1]!.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
    expect(stubs.entries('generate')[1]!.idempotencyKey).not.toBe(key);
    expect(stubs.entries('create')).toHaveLength(1);
    await expectNoExtraDetails(page, stubs, 3);
    await expectA11yClean(page);
    await privacy();
  });

  test('unknown generate outcome preserves the key on explicit retry', async ({ page }) => {
    const { stubs, privacy } = await open(page, { generateReplies: ['abort', 'accepted'] });
    await submit(page);
    await expect(page.getByRole('alert')).toBeVisible();
    await expectA11yClean(page);
    const key = stubs.entries('generate')[0]!.idempotencyKey;
    await page.getByRole('button', { name: L.retry, exact: true }).click();
    await expect(liveRegion(page, L.stages[0]!)).toBeVisible();
    expect(stubs.entries('generate')).toHaveLength(2);
    expect(stubs.entries('generate')[1]!.idempotencyKey).toBe(key);
    expect(stubs.entries('create')).toHaveLength(1);
    await privacy();
  });

  test('429 honours Retry-After with a countdown and no enabled generation action', async ({ page }) => {
    const { stubs, privacy } = await open(page, { generateReplies: [{ status: 429, code: 'story_generation_limit', retryAfter: 20 }] });
    await submit(page);
    const countdown = page.locator('nts-retry-countdown');
    await expect(countdown).toContainText(L.limit);
    await expect(countdown.locator('.retry-countdown__value')).toContainText(L.countdown);
    await expect(countdown.locator('.retry-countdown__announcement')).toHaveAttribute('aria-live', 'polite');
    await expect(page.locator('.new-episode-page__countdown').getByRole('button', { name: L.retry, exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: L.retry, exact: true }).and(page.locator(':enabled'))).toHaveCount(0);
    await expect(page.getByRole('button', { name: L.submit, exact: true }).and(page.locator(':enabled'))).toHaveCount(0);
    expect(stubs.entries('generate')).toHaveLength(1);
    await expectA11yClean(page);
    await privacy();
  });

  test('a lost create response is never automatically retried', async ({ page }) => {
    const { stubs, privacy } = await open(page, { createAbort: true });
    await submit(page);
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page.getByRole('button', { name: L.submit, exact: true })).toBeEnabled();
    await page.waitForTimeout(650);
    expect(stubs.entries('create')).toHaveLength(1);
    expect(stubs.entries('generate')).toHaveLength(0);
    expect(stubs.entries('detail')).toHaveLength(0);
    await expectA11yClean(page);
    await privacy();
  });

  test('true crime: read topic once, create once, never generate without research', async ({ page }) => {
    const { stubs, privacy } = await open(page, { topic: 'true_crime' });
    await expect(page.getByText(L.trueCrimeNotice, { exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: L.brief, exact: true }).fill(BRIEF);
    await expectA11yClean(page);
    await page.getByRole('button', { name: L.trueCrimeNext, exact: true }).click();
    await expect.poll(() => stubs.entries('create').length).toBe(1);
    await page.waitForTimeout(650);
    expect(stubs.entries('series')).toHaveLength(1);
    expect(stubs.entries('create')[0]!.idempotencyKey).toBeNull();
    expect(stubs.entries('generate')).toHaveLength(0);
    expect(stubs.entries('detail')).toHaveLength(0);
    await expectA11yClean(page);
    await privacy();
  });

  test('empty/overlong brief and overlong title prevent submission before requests', async ({ page }) => {
    const { stubs, privacy } = await open(page);
    const brief = page.getByRole('textbox', { name: L.brief, exact: true });
    const button = page.getByRole('button', { name: L.submit, exact: true });
    await expect(button).toBeDisabled();
    await brief.fill('x'.repeat(1001));
    await brief.blur();
    await expect(button).toBeDisabled();
    await expect(brief).toHaveAttribute('aria-describedby', /\S+/);
    await brief.fill(BRIEF);
    await page.getByRole('textbox', { name: L.title, exact: true }).fill('x'.repeat(121));
    await expect(button).toBeDisabled();
    expect(stubs.entries('create')).toHaveLength(0);
    await expectA11yClean(page);
    await privacy();
  });

  test('story_review hands off to the editor URL', async ({ page }) => {
    test.fixme(true, '10.5.3-T1 owns the unmatched /episodes/:id editor route and currentStory loading; 11.5.1-T1 owns compose/mock-mpt proof.');
    const { stubs } = await open(page, { details: [episodeDetail(), episodeDetail('done', { status: 'story_review', generation: { kind: 'story', status: 'succeeded', stage: 'done', errorCode: null, startedAt: null } })] });
    await submit(page);
    await expect(liveRegion(page, L.stages[0]!)).toBeVisible();
    stubs.advance(); stubs.push({ episodeId: EPISODE_ID, seriesId: SERIES_ID });
    await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}$`));
    await expectA11yClean(page);
  });

  test('true crime hands off to the research URL', async ({ page }) => {
    test.fixme(true, '10.5.5-T1 owns the unmatched /episodes/:id/research route; active test above proves create-only branching.');
    await open(page, { topic: 'true_crime' });
    await page.getByRole('textbox', { name: L.brief, exact: true }).fill(BRIEF);
    await page.getByRole('button', { name: L.trueCrimeNext, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}/research$`));
    await expectA11yClean(page);
  });
});
