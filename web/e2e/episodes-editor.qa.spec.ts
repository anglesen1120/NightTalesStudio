import { mkdirSync } from 'node:fs';
import type { Page } from '@playwright/test';
import { collectA11yViolations, describeViolations } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { ERRORS_COPY } from './fixtures/series-stubs';
import {
  EDITOR_COPY, EDITOR_PATH, STORY_CONTENT, editorDetail, openEditor, saveButton, segment, segmentGroup, stubStoryEditor,
} from './fixtures/story-editor-stubs';

// QA S26 (10.5.3-T1): negative/visual/boundary matrix on the stubbed editor. Screenshots go to the gitignored .nts dir.
// Real compose proof stays with 11.5.1-T1.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });
const SHOTS = '/Volumes/Project/nts-wt/10.5.3-T1/.nts/qa-s26';
mkdirSync(SHOTS, { recursive: true });
const PREFS_KEY = 'nts.prefs.v1';

const useTheme = async (page: Page, lang: 'vi' | 'en', theme: 'dark' | 'light'): Promise<void> => {
  await page.addInitScript(([key, value]) => localStorage.setItem(key as string, value as string), [PREFS_KEY, JSON.stringify({ uiLanguage: lang, theme })]);
};

const noHorizontalOverflow = (page: Page): Promise<boolean> => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);

for (const lang of ['vi', 'en'] as const) {
  for (const theme of ['dark', 'light'] as const) {
    for (const width of [360, 768, 1280]) {
      test(`Visual_${lang}_${theme}_${String(width)}: axe clean, no horizontal overflow, no raw interpolation, screenshot`, async ({ page }) => {
        await page.setViewportSize({ width, height: width === 360 ? 800 : 900 });
        await useTheme(page, lang, theme);
        await openEditor(page, lang);
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
        expect(await noHorizontalOverflow(page)).toBe(true);
        const text = await page.locator('body').innerText();
        expect(text).not.toContain('{{'); expect(text).not.toMatch(/\b(episodes|errors|core)\.[a-z]+\.[a-zA-Z]+/);
        const violations = await collectA11yViolations(page);
        expect(violations, describeViolations(violations)).toEqual([]);
        await page.screenshot({ path: `${SHOTS}/editor-${lang}-${theme}-${String(width)}.png`, fullPage: true });
      });
    }
  }
}

for (const lang of ['vi', 'en'] as const) {
  test(`Empty_CurrentStoryNull_GeneratesAndOpensEditor_${lang}`, async ({ page }) => {
    const empty = editorDetail({ currentStory: null, hasCurrentStory: false, status: 'draft', nextActions: ['generate_story'] });
    await stubStoryEditor(page, lang, { detail: empty });
    let generated = false;
    const keys: string[] = [];
    await page.route('**/api/v1' + EDITOR_PATH + '/story/generate', async (route) => {
      expect(route.request().method()).toBe('POST');
      keys.push(route.request().headers()['idempotency-key'] ?? ''); generated = true;
      await route.fulfill({ status: 202, contentType: 'application/json', body: '{}' });
    });
    await page.route('**/api/v1' + EDITOR_PATH, async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(generated ? editorDetail() : empty) });
    });
    await page.goto(EDITOR_PATH);
    const action = page.getByRole('button', { name: lang === 'vi' ? 'Viết kịch bản' : 'Write story', exact: true });
    await expect(page.locator('nts-empty-state')).toBeVisible(); await expect(action).toBeVisible();
    await expect(page.getByRole('textbox')).toHaveCount(0); expect(keys).toHaveLength(0);
    await page.screenshot({ path: `${SHOTS}/empty-${lang}.png`, fullPage: true });
    await action.click();
    await expect(segment(page, EDITOR_COPY[lang].title)).toHaveValue(STORY_CONTENT.title);
    await expect(page.locator('nts-empty-state')).toHaveCount(0);
    expect(keys).toHaveLength(1); expect(keys[0]).toMatch(/^[0-9a-f-]{36}$/);
    expect(await collectA11yViolations(page)).toEqual([]);
  });
}

test('SaveAsNew_NonEditableRead_ClosesConflictWithoutAnotherDialog', async ({ page }) => {
  const approved = editorDetail({ status: 'story_approved', nextActions: ['reopen_story'] });
  const api = await openEditor(page, 'vi', { failures: [{ status: 412, detailAfter: approved }] });
  await segment(page, EDITOR_COPY.vi.title).fill('My preserved draft');
  await saveButton(page, 'vi').click();
  const dialog = page.getByRole('dialog', { name: ERRORS_COPY.vi.conflict.title, exact: true });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: ERRORS_COPY.vi.conflict.saveAsNew, exact: true }).click();
  await expect(segment(page, EDITOR_COPY.vi.title)).toHaveAttribute('readonly', '');
  await expect(dialog).toHaveCount(0);
  await page.waitForTimeout(650);
  await expect(dialog).toHaveCount(0); expect(api.puts).toHaveLength(1);
  await expect(segment(page, EDITOR_COPY.vi.title)).toHaveValue('My preserved draft');
  await expect(page.getByRole('button', { name: EDITOR_COPY.vi.reopen, exact: true })).toBeDisabled();
});

test('InvalidSegmentParams_FocusNothingAndEditorStaysUsable', async ({ page }) => {
  const L = EDITOR_COPY.vi;
  for (const bad of ['p:99', 'p:03', 'P:1', 'foo', 'p:-1']) {
    await page.goto('about:blank');
    const api = await openEditor(page, 'vi', {}, `?segment=${encodeURIComponent(bad)}`);
    await expect(page.locator('textarea:focus')).toHaveCount(0);
    await segment(page, L.title).fill(`ok ${bad}`);
    await expect(saveButton(page, 'vi')).toBeEnabled();
    expect(api.puts).toHaveLength(0);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
});

test('SceneKeyword_TypingSeveralCharacters_KeepsFocusAndSavesOnlyOnExplicitClick', async ({ page }) => {
  const L = EDITOR_COPY.vi;
  const api = await openEditor(page, 'vi');
  const input = segmentGroup(page, L.paragraph(1)).getByRole('list', { name: L.scenes, exact: true }).getByRole('textbox', { name: L.scene(1), exact: true });
  await input.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('một hai ba bốn', { delay: 15 });
  await expect(input).toBeFocused();
  await expect(input).toHaveValue('một hai ba bốn');
  await page.waitForTimeout(600);
  expect(api.puts).toHaveLength(0);
  const stored = await page.evaluate(() => [Object.keys(localStorage), Object.keys(sessionStorage)]);
  expect(stored[0]?.filter((key) => key !== PREFS_KEY)).toEqual([]); expect(stored[1]).toEqual([]);
});

test('PasteAndLongContent_NoHorizontalOverflowAt360_SaveStillExactlyOnePut', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  const L = EDITOR_COPY.vi;
  const api = await openEditor(page, 'vi');
  const unbroken = 'Đ'.repeat(600);
  await segment(page, L.paragraph(1)).fill(`${unbroken} ${'từ '.repeat(400)}`);
  expect(await noHorizontalOverflow(page)).toBe(true);
  await page.screenshot({ path: `${SHOTS}/long-vi-360.png`, fullPage: true });
  await saveButton(page, 'vi').click();
  await expect.poll(() => api.puts.length).toBe(1);
  const body = api.puts[0]?.postDataJSON() as typeof STORY_CONTENT;
  expect(body.paragraphs[0]?.text.startsWith('Đ')).toBe(true);
  expect(body.structure).toEqual(STORY_CONTENT.structure);
});

test('FocusRing_VisibleOnEveryKeyboardStop_BothThemes', async ({ page }) => {
  for (const theme of ['dark', 'light'] as const) {
    await page.goto('about:blank');
    await useTheme(page, 'vi', theme);
    await openEditor(page, 'vi');
    const missing: string[] = [];
    await page.locator('body').click({ position: { x: 1, y: 1 } });
    for (let i = 0; i < 40; i++) {
      await page.keyboard.press('Tab');
      const state = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        if (!el || el === document.body) return null;
        const style = getComputedStyle(el);
        const outlined = style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) >= 2;
        const shadowed = style.boxShadow !== 'none';
        const label = `${el.tagName.toLowerCase()}[${el.getAttribute('aria-label') ?? el.id ?? el.className}]`;
        const rect = el.getBoundingClientRect();
        return { label, ok: outlined || shadowed, w: Math.round(rect.width), h: Math.round(rect.height) };
      });
      if (state && !state.ok) missing.push(`${state.label} ${String(state.w)}x${String(state.h)}`);
    }
    expect(missing, `${theme}: stops without visible focus indicator`).toEqual([]);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
});

test('TouchTargets_ActionControlsAtLeast24px_At360', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await openEditor(page, 'vi');
  const small = await page.evaluate(() => Array.from(document.querySelectorAll<HTMLElement>('main button, main select, main input, main textarea'))
    .map((el) => ({ label: el.getAttribute('aria-label') ?? el.textContent?.trim() ?? el.tagName, r: el.getBoundingClientRect() }))
    .filter(({ r }) => r.width > 0 && (r.height < 24 || r.width < 24)).map(({ label, r }) => `${label} ${String(Math.round(r.width))}x${String(Math.round(r.height))}`));
  expect(small).toEqual([]);
});

test('ReadOnly_ApprovedStory_Axe_NoSaveBar_Screenshot', async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 900 });
  await openEditor(page, 'vi', { detail: editorDetail({ status: 'story_approved', nextActions: ['reopen_story'] }) });
  await expect(saveButton(page, 'vi')).toHaveCount(0);
  for (const area of await page.locator('textarea').all()) await expect(area).toHaveAttribute('readonly', '');
  const violations = await collectA11yViolations(page);
  expect(violations, describeViolations(violations)).toEqual([]);
  await page.screenshot({ path: `${SHOTS}/readonly-vi-768.png`, fullPage: true });
  const reopen = page.getByRole('button', { name: EDITOR_COPY.vi.reopen, exact: true });
  await expect(reopen).toBeVisible();
  // AC9 / failure row 2: "renders ... a reopen action" - observed enabled state recorded, not asserted as the plumbing is 10.5.6-T1.
  test.info().annotations.push({ type: 'reopen-enabled', description: String(await reopen.isEnabled()) });
});
