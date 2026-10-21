import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, NEW_EPISODE_PATH, SERIES_ID, stubEpisodes, watchPrivacy } from './fixtures/episodes-stubs';
import { expectNoHorizontalScroll } from './fixtures/series-stubs';
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import storyEn from '../src/assets/i18n/story/en.json' with { type: 'json' };
import storyVi from '../src/assets/i18n/story/vi.json' with { type: 'json' };

const BRIEF = 'Independent QA: a whispered name in an empty house.';

for (const lang of ['vi', 'en'] as const) {
  const episodes = lang === 'vi' ? episodesVi : episodesEn;
  const story = lang === 'vi' ? storyVi : storyEn;
  test.describe(`new episode independent QA (${lang})`, () => {
    test.use({ viewport: { width: 360, height: 800 }, locale: lang === 'vi' ? 'vi-VN' : 'en-US' });

    test('360px form boundaries, localized counter and no credit UI', async ({ page }) => {
      const stubs = await stubEpisodes(page, { lang });
      const privacy = watchPrivacy(page, BRIEF);
      await page.goto(NEW_EPISODE_PATH);
      const brief = page.getByRole('textbox', { name: episodes.new.briefLabel, exact: true });
      const title = page.getByRole('textbox', { name: episodes.new.titleOptional, exact: true });
      const button = page.getByRole('button', { name: story.generation.action.generate, exact: true });
      await brief.fill(' \t ');
      await expect(button).toBeDisabled();
      await brief.fill('x'.repeat(1001));
      await brief.blur();
      await expect(button).toBeDisabled();
      await expect(brief).toHaveAttribute('aria-describedby', /brief-error/);
      await brief.fill('x'.repeat(1000));
      await title.fill('t'.repeat(120));
      await expect(button).toBeEnabled();
      await expect(page.locator('nts-char-counter')).toContainText(lang === 'vi' ? '1.000/1.000' : '1,000/1,000');
      await title.fill('t'.repeat(121));
      await title.blur();
      await expect(button).toBeDisabled();
      await expect(title).toHaveAttribute('aria-describedby', /title-error/);
      expect(stubs.entries('create')).toHaveLength(0);
      expect(stubs.entries('generate')).toHaveLength(0);
      await expect(page.locator('.brief-form__cost-line')).toHaveText('');
      await expect(page.locator('nts-story-brief-form')).not.toContainText(/credits|balance|quote|buy|tín dụng|số dư|mua/i);
      await expectNoHorizontalScroll(page);
      await expectA11yClean(page);
      await privacy();
    });

    test('double-click/Enter while create is in flight remains one episode and one generation', async ({ page }) => {
      const stubs = await stubEpisodes(page, { lang });
      let release!: () => void;
      const held = new Promise<void>((resolve) => { release = resolve; });
      let posts = 0;
      await page.route(`**/api/v1/series/${SERIES_ID}/episodes`, async (route) => {
        posts += 1;
        await held;
        await route.fallback();
      });
      await page.goto(NEW_EPISODE_PATH);
      await page.getByRole('textbox', { name: episodes.new.briefLabel, exact: true }).fill(BRIEF);
      const button = page.getByRole('button', { name: story.generation.action.generate, exact: true });
      await button.click();
      await expect.poll(() => posts).toBe(1);
      await expect(button).toBeDisabled();
      await page.keyboard.press('Enter');
      await page.keyboard.press('Enter');
      expect(posts).toBe(1);
      release();
      await expect(page.locator('.generation-progress__stage')).toContainText(episodes.generation.stage.queued);
      expect(stubs.entries('create')).toHaveLength(1);
      expect(stubs.entries('generate')).toHaveLength(1);
      expect(stubs.entries('create')[0]?.idempotencyKey).toBeNull();
      await expectNoHorizontalScroll(page);
    });

    test('409 active generation is live without an alert; late failure announces without stealing focus', async ({ page }) => {
      const failed = episodeDetail('done', { status: 'draft', generation: {
        kind: 'story', status: 'failed', stage: 'done', errorCode: 'story_invalid_output', startedAt: null,
      } });
      const stubs = await stubEpisodes(page, { lang,
        generateReplies: [{ status: 409, code: 'story_generation_in_progress' }],
        details: [episodeDetail('writing'), failed],
      });
      await page.goto(NEW_EPISODE_PATH);
      await expect.poll(stubs.connected).toBe(1);
      await page.getByRole('textbox', { name: episodes.new.briefLabel, exact: true }).fill(BRIEF);
      await page.getByRole('button', { name: story.generation.action.generate, exact: true }).click();
      const stage = page.locator('.generation-progress__stage');
      await expect(stage).toContainText(story.generation.stage.writing);
      await expect(stage).toHaveAttribute('aria-live', 'polite');
      await expect(page.getByRole('alert')).toHaveCount(0);
      const back = page.locator('nts-page-header a').first();
      await back.focus();
      await expect(back).toBeFocused();
      stubs.advance();
      stubs.push();
      await expect(page.getByRole('alert')).toContainText(story.generation.error.story_invalid_output);
      await expect(back).toBeFocused();
      await expect(page.getByRole('link', { name: episodes.new.openEpisode, exact: true })).toHaveAttribute('href', `/episodes/${EPISODE_ID}`);
      await expectNoHorizontalScroll(page);
      await expectA11yClean(page);
    });

    test('generated story text remains private while an unknown outcome recovers to live progress', async ({ page }) => {
      const secret = 'QA generated story body must never reach console, storage or URL';
      const currentStory = {
        approvedStoryVersionId: null, auditReport: null, attestation: null, body: secret, content: null, etag: '"story-qa"',
        factSheetId: null, keywords: [], qualityHints: [], review: null, similarityScore: null,
        source: 'generated', status: 'story_generating' as const, version: 1,
        versionId: '55555555-5555-4555-8555-555555555555', wordCount: 12,
      };
      const stubs = await stubEpisodes(page, { lang, generateReplies: ['abort'],
        details: [episodeDetail('writing', { currentStory })],
      });
      const storyPrivacy = watchPrivacy(page, secret);
      const briefPrivacy = watchPrivacy(page, BRIEF);
      await page.goto(NEW_EPISODE_PATH);
      await expect.poll(stubs.connected).toBe(1);
      await page.getByRole('textbox', { name: episodes.new.briefLabel, exact: true }).fill(BRIEF);
      await page.getByRole('button', { name: story.generation.action.generate, exact: true }).click();
      await expect(page.getByRole('alert')).toBeVisible();
      stubs.push();
      await expect(page.locator('.generation-progress__stage')).toContainText(story.generation.stage.writing);
      await expect(page.locator('body')).not.toContainText(secret);
      await storyPrivacy();
      await briefPrivacy();
      expect(stubs.entries('generate')).toHaveLength(1);
      await expect(page.getByRole('alert')).toHaveCount(0);
    });

    test('413 never offers retry and navigating away rejects late hub invalidations', async ({ page }) => {
      const stubs = await stubEpisodes(page, { lang, generateReplies: [{ status: 413, code: 'payload_too_large' }] });
      await page.goto(NEW_EPISODE_PATH);
      await expect.poll(stubs.connected).toBe(1);
      await page.getByRole('textbox', { name: episodes.new.briefLabel, exact: true }).fill(BRIEF);
      await page.getByRole('button', { name: story.generation.action.generate, exact: true }).click();
      await expect(page.getByRole('alert')).toBeVisible();
      await expect(page.getByRole('button', { name: story.generation.action.retry, exact: true })).toHaveCount(0);
      expect(stubs.entries('generate')).toHaveLength(1);
      await expectNoHorizontalScroll(page);
      await expectA11yClean(page);
      await page.locator('nts-page-header a').first().click();
      await expect(page).toHaveURL(/\/episodes$/);
      const count = stubs.entries('detail').length;
      stubs.push();
      await page.waitForTimeout(650);
      expect(stubs.entries('detail')).toHaveLength(count);
      expect(stubs.entries('generate')).toHaveLength(1);
    });
  });
}
