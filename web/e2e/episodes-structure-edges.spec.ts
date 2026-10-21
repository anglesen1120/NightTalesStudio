import type { Page } from '@playwright/test';
import { expect, test } from './fixtures/csp.fixture';
import {
  EDITED, EDITOR_COPY, EDITOR_STRUCTURE_COPY, openEditor, segment, type E2eLang,
} from './fixtures/story-editor-stubs';

// QA for 10.5.9-T1 (written by the QA agent, not the implementer). Negative and boundary cases the shipped structure
// spec does not cover: the leave dialog dismissed with Escape or the backdrop must mean Stay, and a keystroke made
// inside the 150 ms dirty debounce must still raise the browser beforeunload prompt.

async function dirtyThenBack(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  await segment(page, L.title).fill(EDITED.title);
  await page.getByRole('link', { name: C.back, exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
}

for (const lang of ['vi', 'en'] as const) {
  test.describe(`story structure QA (${lang}, stubbed)`, () => {
    test.use({ locale: lang === 'vi' ? 'vi-VN' : 'en-US', timezoneId: 'UTC' });

    test('LeaveDialog_EscapeKey_IsStay_KeepsRouteAndDraftAndSendsNoPut', async ({ page }) => {
      const L = EDITOR_COPY[lang];
      const api = await openEditor(page, lang);
      await dirtyThenBack(page, lang);
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page).toHaveURL(/\/episodes\/[^/?]+$/);
      await expect(segment(page, L.title)).toHaveValue(EDITED.title);
      expect(api.puts).toHaveLength(0);
    });

    test('LeaveDialog_BackdropClick_IsStay_KeepsRouteAndDraftAndSendsNoPut', async ({ page }) => {
      const L = EDITOR_COPY[lang];
      const api = await openEditor(page, lang);
      await dirtyThenBack(page, lang);
      await page.locator('.cdk-overlay-backdrop').click({ position: { x: 4, y: 4 } });
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page).toHaveURL(/\/episodes\/[^/?]+$/);
      await expect(segment(page, L.title)).toHaveValue(EDITED.title);
      expect(api.puts).toHaveLength(0);
    });

    test('BeforeUnload_KeystrokeInsideDebounceThenClose_StillPrompts', async ({ page }) => {
      const L = EDITOR_COPY[lang];
      const dialogs: string[] = [];
      page.on('dialog', (dialog) => {
        dialogs.push(dialog.type());
        void dialog.accept();
      });
      await openEditor(page, lang);
      await segment(page, L.title).click();
      await segment(page, L.title).pressSequentially('!');
      // Let change detection run the listener effect, but stay well inside the 150 ms dirty debounce window.
      await page.evaluate(() => new Promise<void>((resolve) => { setTimeout(resolve, 30); }));
      const closed = page.waitForEvent('close');
      await page.close({ runBeforeUnload: true });
      await closed;
      expect(dialogs).toContain('beforeunload');
    });
  });
}
