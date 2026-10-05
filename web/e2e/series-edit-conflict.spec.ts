import type { Page } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import {
  AUTOPILOT_COPY,
  CORE_COPY,
  ERRORS_COPY,
  SERIES_COPY,
  VOICES_VI,
  seriesDetail,
  stubCatalog,
  stubSeriesApi,
  stubSession,
  styleCopy,
  type E2eLang,
  type SeriesApiStub,
  type SeriesApiStubOptions,
  type SeriesDto,
  type VoiceDto,
} from './fixtures/series-stubs';

// 10.4.2-T1 (README D15/D16, task file §Tests `series-edit-conflict.spec.ts` + Acceptance checks 7-9).
// Backend-free: the session and every `/api/v1/series` endpoint are stubbed with `page.route`
// (`fixtures/series-stubs.ts`). `stubSeriesApi` answers 412 `series_conflict` while `If-Match` still carries the
// loaded ETag and serves a newer entity to the conflict re-read, so both dialog answers are provable.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const L = SERIES_COPY.vi;
const ERR = ERRORS_COPY.vi;

const STORED_ETAG = '"v1"';
const FRESH_ETAG = '"v2"';
const FRESH_TITLE = 'Series máy chủ';

/** The entity the editor loads first, and the one the server holds once the write was refused. */
const STORED_SERIES: SeriesDto = seriesDetail({ title: 'Series gốc', etag: STORED_ETAG, voiceId: 'v-vi-1', styleTemplateId: 't-dark-folklore-ink' });
const FRESH_SERIES: SeriesDto = seriesDetail({ title: FRESH_TITLE, etag: FRESH_ETAG, voiceId: 'v-vi-1', styleTemplateId: 't-dark-folklore-ink' });

const main = (page: Page) => page.getByRole('main');
const titleInput = (page: Page, lang: E2eLang) => main(page).getByLabel(SERIES_COPY[lang].field.title);
const languageSelect = (page: Page, lang: E2eLang) => main(page).getByRole('combobox', { name: SERIES_COPY[lang].field.language });
const nextButton = (page: Page, lang: E2eLang) => page.getByRole('button', { name: SERIES_COPY[lang].editor.next });
const conflictDialog = (page: Page) => page.getByRole('dialog');

/** The page-header save action (the step-4 submit carries the same label, so the header group scopes it). */
function headerSaveButton(page: Page, lang: E2eLang) {
  return page
    .getByRole('group', { name: CORE_COPY[lang].core.ui.pageHeader.actions })
    .getByRole('button', { name: SERIES_COPY[lang].save.changes });
}

interface EditHarness {
  readonly api: SeriesApiStub;
}

async function openEdit(page: Page, stored: SeriesDto, apiOptions: SeriesApiStubOptions = {}): Promise<EditHarness> {
  await stubSession(page, 'vi');
  const api = await stubSeriesApi(page, { detail: stored, ...apiOptions });
  await stubCatalog(page);
  await page.goto(`/series/${stored.id}/edit`);
  await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(L.editor.editTitle);
  await expect(titleInput(page, 'vi')).toHaveValue(stored.title);
  return { api };
}

/** Loads a stale edit session, edits the title and submits into the 412 conflict dialog. */
async function openConflict(page: Page): Promise<EditHarness> {
  const harness = await openEdit(page, STORED_SERIES, {
    detailAfterConflict: FRESH_SERIES,
    patchStaleEtag: STORED_ETAG,
    patchFreshEtag: '"v3"',
  });
  await titleInput(page, 'vi').fill('Bản của tôi');
  await headerSaveButton(page, 'vi').click();
  await expect(conflictDialog(page)).toBeVisible();
  return harness;
}

test('Edit_PrefillsAndLocksLanguage: every field is prefilled and the language stays disabled with its hint', async ({ page }) => {
  const stored = seriesDetail({
    title: 'Series gốc',
    topic: 'spiritual',
    aspect: 'portrait',
    voiceId: 'v-vi-1',
    styleTemplateId: 't-watercolor-night',
    etag: STORED_ETAG,
  });
  await openEdit(page, stored);
  const firstVoice = VOICES_VI[0] as VoiceDto;

  // Step 1: the stored title/topic, the locked language and an editable topic (autopilot is off).
  await expect(titleInput(page, 'vi')).toHaveValue('Series gốc');
  await expect(main(page).getByRole('radio', { name: L.topic.spiritual })).toBeChecked();
  await expect(languageSelect(page, 'vi')).toContainText(L.language.vi);
  await expect(languageSelect(page, 'vi')).toBeDisabled();
  await expect(main(page).getByText(L.language.locked)).toBeVisible();
  await expect(main(page).getByRole('radio', { name: L.topic.ghost })).toBeEnabled();

  // Step 2: the stored aspect.
  await nextButton(page, 'vi').click();
  await expect(main(page).getByRole('radio', { name: L.aspect.portrait })).toBeChecked();

  // Step 3: the stored voice.
  await nextButton(page, 'vi').click();
  await expect(main(page).getByRole('radio', { name: firstVoice.displayName })).toBeChecked();

  // Step 4: the stored style.
  await nextButton(page, 'vi').click();
  await expect(page.getByRole('radiogroup', { name: L.field.style }).getByRole('radio', { name: styleCopy('vi', 'watercolor-night').label })).toBeChecked();
});

test('Edit_TopicLocked_WhenAutopilotEnabled: the topic is disabled with the autopilot hint and the tab link', async ({ page }) => {
  const stored = seriesDetail({ title: 'Series tự động', autopilotEnabled: true, etag: STORED_ETAG });
  await openEdit(page, stored);

  await expect(main(page).getByText(AUTOPILOT_COPY.vi.topic_locked)).toBeVisible();
  await expect(main(page).getByRole('radio', { name: L.topic.ghost })).toBeDisabled();
  await expect(main(page).getByRole('link', { name: L.detail.autopilot })).toBeVisible();
});

test('StaleIfMatch_ConflictDialog_ReloadLatest: the dialog keeps the entered values, reload replaces them with the server state', async ({ page }) => {
  const { api } = await openConflict(page);

  // The dialog names the conflict, the entered value is not lost before the choice, and 412 is never retried.
  await expect(conflictDialog(page)).toContainText(ERR.conflict.title);
  await expect(conflictDialog(page)).toContainText(L.save.conflict);
  await expect(titleInput(page, 'vi')).toHaveValue('Bản của tôi');
  expect(api.patches).toHaveLength(1);
  expect(api.patches[0]?.headers()['if-match']).toBe(STORED_ETAG);

  await conflictDialog(page).getByRole('button', { name: ERR.conflict.reload }).click();
  await expect(page).toHaveURL(new RegExp(`/series/${STORED_SERIES.id}$`));
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(FRESH_TITLE);
  expect(api.detailGets.length, 'the initial load plus the conflict re-read').toBeGreaterThanOrEqual(2);
  expect(api.patches, 'a 412 is never auto-retried').toHaveLength(1);
});

test('StaleIfMatch_SaveMineAsNewVersion: the second PATCH carries the freshly read ETag', async ({ page }) => {
  const { api } = await openConflict(page);

  await conflictDialog(page).getByRole('button', { name: ERR.conflict.saveAsNew }).click();
  await expect.poll(() => api.patches.length).toBe(2);
  expect(api.patches[0]?.headers()['if-match'], 'the first attempt used the stale tag').toBe(STORED_ETAG);
  expect(api.patches[1]?.headers()['if-match'], 'the re-read tag, never a recomputed one').toBe(FRESH_ETAG);
  expect(api.patches[1]?.postDataJSON()).toEqual({ title: 'Bản của tôi' });

  await expect(page).toHaveURL(new RegExp(`/series/${STORED_SERIES.id}$`));
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(FRESH_TITLE);
});

test('axe (WCAG 2.1 AA) on the conflict dialog', async ({ page }) => {
  await openConflict(page);
  await expect(conflictDialog(page)).toBeVisible();
  // axe samples computed colours: the flag is a sampling artifact — while MatDialog's entrance fade runs,
  // the button blends toward the dark scrim (~0.7 opacity) and a false color-contrast is reported. Wait for the
  // dialog's own fade to settle; an all-`getAnimations()` wait hangs on infinite mdc-radio/ripple transitions.
  await expect(page.locator('.mat-mdc-dialog-inner-container')).toHaveCSS('opacity', '1');
  await expectA11yClean(page);
  await conflictDialog(page).getByRole('button', { name: ERR.conflict.cancel }).click();
  await expect(conflictDialog(page)).toHaveCount(0);
  // Cancelling keeps the entered value and the edit route.
  await expect(titleInput(page, 'vi')).toHaveValue('Bản của tôi');
  await expect(page).toHaveURL(/\/edit$/);
});
