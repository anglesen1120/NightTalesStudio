import type { Page } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, expectNoExtraDetails, NEW_EPISODE_PATH, stubEpisodes, watchPrivacy, type EpisodeStubOptions } from './fixtures/episodes-stubs';
import { liveRegion } from './fixtures/series-stubs';
import { editorDetail, expectEditor } from './fixtures/story-editor-stubs';
import { stubResearch } from './fixtures/research-stubs';

// Same stub/CSP/axe/locale footprint as episodes-list-en.spec.ts; no compose account or TOTP seed needed.
test.use({ locale: 'en-US', timezoneId: 'UTC' });
const L = {
  lang: 'en', brief: 'Your idea', submit: 'Write story', retry: 'Try again',
  trueCrimeNext: 'Create episode and add sources',
  trueCrimeNotice: 'A true-crime episode needs sources and an approved fact sheet before it is written.',
  invalid: 'The draft did not pass validation. Try again.',
  stages: ['Queued', 'Writing the script…', 'Checking…', 'Self-reviewing…', 'Applying one correction…', 'Checking similarity…', 'Saving…'],
};
const BRIEF = 'A lamp in an abandoned house lights up only when nobody is inside.';
const STAGES = ['queued', 'writing', 'checking', 'reviewing', 'revising', 'scoring', 'saving'] as const;

async function open(page: Page, options: EpisodeStubOptions = {}) {
  const privacy = watchPrivacy(page, BRIEF);
  const stubs = await stubEpisodes(page, { lang: 'en', ...options });
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

test.describe('new episode generation (en, stubbed)', () => {
  test('fiction: create once, connected before generate, seed and ids-only stages through GETs without polling', async ({ page }) => {
    const { stubs, privacy } = await open(page, { details: STAGES.map((stage) => episodeDetail(stage)) });
    await expectA11yClean(page);
    await submit(page);
    await expect(liveRegion(page, L.stages[0]!)).toBeVisible();
    expect(stubs.entries('create')).toHaveLength(1);
    expect(stubs.entries('create')[0]!.method).toBe('POST');
    expect(stubs.entries('create')[0]!.idempotencyKey).toBeNull();
    expect(stubs.entries('generate')).toHaveLength(1);
    const generate = stubs.entries('generate')[0]!;
    expect(generate.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
    expect(stubs.entries('ticket')).toHaveLength(1);
    expect(stubs.entries('websocket')).toHaveLength(1);
    expect(stubs.entries('ticket')[0]!.order).toBeLessThan(generate.order);
    expect(stubs.entries('websocket')[0]!.order).toBeLessThan(generate.order);
    expect(stubs.entries('detail')).toHaveLength(1);
    expect(stubs.entries('detail')[0]!.order).toBeGreaterThan(generate.order);
    await expectNoExtraDetails(page, stubs, 1);
    for (let index = 1; index < STAGES.length; index += 1) {
      stubs.advance(); stubs.push();
      const stage = liveRegion(page, L.stages[index]!);
      await expect(stage).toContainText(L.stages[index]!);
      await expect(stage).toHaveAttribute('aria-live', 'polite');
      expect(stubs.entries('detail').at(-1)!.order).toBeGreaterThan(stubs.entries('event').at(-1)!.order);
      await expectNoExtraDetails(page, stubs, index + 1);
      await expectA11yClean(page);
    }
    expect(stubs.entries('create')).toHaveLength(1);
    expect(stubs.entries('generate')).toHaveLength(1);
    await privacy();
  });

  test('terminal failure: alert and a new generation key on retry', async ({ page }) => {
    const failed = episodeDetail('done', { status: 'draft', generation: { kind: 'story', status: 'failed', stage: 'done', errorCode: 'story_invalid_output', startedAt: null } });
    const { stubs, privacy } = await open(page, { details: [episodeDetail(), failed, episodeDetail('writing')] });
    await submit(page);
    await expect(liveRegion(page, L.stages[0]!)).toBeVisible();
    stubs.advance(); stubs.push();
    await expect(page.getByRole('alert')).toContainText(L.invalid);
    await expectA11yClean(page);
    const key = stubs.entries('generate')[0]!.idempotencyKey;
    stubs.advance();
    await page.getByRole('button', { name: L.retry, exact: true }).click();
    await expect(liveRegion(page, L.stages[1]!)).toBeVisible();
    expect(stubs.entries('generate')).toHaveLength(2);
    expect(stubs.entries('generate')[1]!.idempotencyKey).not.toBe(key);
    expect(stubs.entries('create')).toHaveLength(1);
    await expectA11yClean(page);
    await privacy();
  });

  test('true crime creates once and never generates before research', async ({ page }) => {
    const { stubs, privacy } = await open(page, { topic: 'true_crime' });
    await expect(page.getByText(L.trueCrimeNotice, { exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: L.brief, exact: true }).fill(BRIEF);
    await expectA11yClean(page);
    await page.getByRole('button', { name: L.trueCrimeNext, exact: true }).click();
    await expect.poll(() => stubs.entries('create').length).toBe(1);
    await page.waitForTimeout(650);
    expect(stubs.entries('series')).toHaveLength(1);
    expect(stubs.entries('generate')).toHaveLength(0);
    expect(stubs.entries('detail')).toHaveLength(0);
    await expectA11yClean(page);
    await privacy();
  });

  test('story_review hands off to the editor URL', async ({ page }) => {
    const { stubs, privacy } = await open(page, { details: [episodeDetail(), editorDetail({ generation: { kind: 'story', status: 'succeeded', stage: 'done', errorCode: null, startedAt: null } })] });
    await submit(page);
    await expect(liveRegion(page, L.stages[0]!)).toBeVisible();
    stubs.advance(); stubs.push();
    await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}$`));
    await expectEditor(page, 'en');
    await expectA11yClean(page);
    await privacy();
  });

  test('true crime hands off to the research URL', async ({ page }) => {
    await open(page, { topic: 'true_crime' });
    await stubResearch(page);
    await page.getByRole('textbox', { name: L.brief, exact: true }).fill(BRIEF);
    await page.getByRole('button', { name: L.trueCrimeNext, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}/research$`));
    await expect(page.getByRole('heading', { name: 'Sources and facts', exact: true })).toBeVisible();
    await expectA11yClean(page);
  });
});
