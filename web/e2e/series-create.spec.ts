import type { Locator, Page } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import {
  CORE_COPY,
  ERRORS_COPY,
  PREVIEW_PATH,
  SERIES_COPY,
  STYLE_SEED,
  TRUE_CRIME_STYLE_NAMES,
  VOICES_COPY,
  VOICES_EN,
  VOICES_VI,
  expectNoHorizontalScroll,
  liveRegion,
  seriesDetail,
  stubCatalog,
  stubSeriesApi,
  stubSession,
  styleCopy,
  type CatalogStub,
  type CatalogStubOptions,
  type E2eLang,
  type E2eTopic,
  type SeriesApiStub,
  type SeriesApiStubOptions,
  type StyleTemplateDto,
  type VoiceDto,
} from './fixtures/series-stubs';

// 10.4.2-T1 (README D15/D16, task file §Tests `series-create.spec.ts` + Acceptance checks 4-6 and 11, plus the
// 10.4.2-T2 §Notes "As built" hand-over rows for the voice step). Backend-free: the session, `/api/v1/series`,
// `/api/v1/voices`, `/api/v1/style-templates` and the same-origin preview audio are stubbed with `page.route`
// (`fixtures/series-stubs.ts`). Copy comes from the shipped bundles, never re-typed.
//
// The series language control is independent of the UI locale: it defaults to `vi`, so the voice step always lists
// the Vietnamese catalog (even in the `en` block). The `vi` block carries the verbatim task-file test names; the
// `en` block repeats the create, true-crime and axe checks with an `[en]` suffix (T2 plan: "vi project + en smoke").
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const TOPIC_COPY_KEY: Readonly<Record<E2eTopic, 'ghost' | 'spiritual' | 'trueCrime'>> = {
  ghost: 'ghost',
  spiritual: 'spiritual',
  true_crime: 'trueCrime',
};

interface EditorHarness {
  readonly api: SeriesApiStub;
  readonly catalog: CatalogStub;
}

interface OpenEditorOptions {
  readonly path?: string;
  readonly mode?: 'create' | 'edit';
  readonly api?: SeriesApiStubOptions;
  readonly catalog?: CatalogStubOptions;
}

/** What the focused element is, in the terms the keyboard helpers match on. */
interface ActiveInfo {
  readonly tag: string;
  readonly id: string;
  readonly name: string;
  readonly type: string;
  readonly value: string;
}

const main = (page: Page) => page.getByRole('main');
const currentStep = (page: Page) => page.locator('[aria-current="step"]');

const continueButton = (page: Page, lang: E2eLang) => page.getByRole('button', { name: SERIES_COPY[lang].editor.next });
const submitButton = (page: Page, lang: E2eLang) => page.getByRole('button', { name: SERIES_COPY[lang].list.new });
const backButton = (page: Page, lang: E2eLang) => page.getByRole('button', { name: CORE_COPY[lang].common.back });
const languageSelect = (page: Page, lang: E2eLang) => main(page).getByRole('combobox', { name: SERIES_COPY[lang].field.language });

/** The preview toggle of one voice row: its accessible name is the play or the stop label plus the display name. */
function previewButton(page: Page, lang: E2eLang, displayName: string): Locator {
  const copy = SERIES_COPY[lang].voice;
  return page.getByRole('button', { name: new RegExp(`(?:${copy.preview}|${copy.stop}) — ${displayName}`) });
}

async function openEditor(page: Page, lang: E2eLang, options: OpenEditorOptions = {}): Promise<EditorHarness> {
  await stubSession(page, lang);
  const api = await stubSeriesApi(page, options.api ?? {});
  const catalog = await stubCatalog(page, options.catalog ?? {});
  await page.goto(options.path ?? '/series/new');
  await expect(page.locator('html')).toHaveAttribute('lang', lang);
  await expect(main(page)).toBeVisible();
  if (options.mode === 'edit') {
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(SERIES_COPY[lang].editor.editTitle);
  } else {
    await expect(main(page).getByLabel(SERIES_COPY[lang].field.title)).toBeVisible();
  }
  return { api, catalog };
}

async function fillTitle(page: Page, lang: E2eLang, title: string): Promise<void> {
  await main(page).getByLabel(SERIES_COPY[lang].field.title).fill(title);
}

async function selectTopic(page: Page, lang: E2eLang, topic: E2eTopic): Promise<void> {
  const radio = main(page).getByRole('radio', { name: SERIES_COPY[lang].topic[TOPIC_COPY_KEY[topic]] });
  await radio.check();
  await expect(radio).toBeChecked();
}

async function selectVoice(page: Page, voice: VoiceDto): Promise<void> {
  const radio = main(page).getByRole('radio', { name: voice.displayName });
  await radio.check();
  await expect(radio).toBeChecked();
}

async function selectLanguage(page: Page, lang: E2eLang, target: E2eLang): Promise<void> {
  const copy = SERIES_COPY[lang];
  await languageSelect(page, lang).click();
  await page.getByRole('option', { name: copy.language[target] }).click();
  await expect(languageSelect(page, lang)).toContainText(copy.language[target]);
}

/**
 * The wizard's back button is one-step (`SeriesEditorStore.goBack` / `StepOrder_NextBack`), so returning to the
 * topic+language step (step 1) from the voice step (step 3) takes two clicks.
 */
async function backToTopicStep(page: Page, lang: E2eLang): Promise<void> {
  await backButton(page, lang).click();
  await backButton(page, lang).click();
  await expect(currentStep(page)).toContainText(SERIES_COPY[lang].step.topic);
}

/** Fills step 1 and walks forward until `step` is current, selecting what each step needs to be left behind. */
async function advanceToStep(page: Page, lang: E2eLang, step: 1 | 2 | 3 | 4, title = 'Series kiểm thử'): Promise<void> {
  await fillTitle(page, lang, title);
  if (step === 1) return;
  await continueButton(page, lang).click();
  await expect(currentStep(page)).toContainText(SERIES_COPY[lang].step.aspect);
  if (step === 2) return;
  await continueButton(page, lang).click();
  await expect(currentStep(page)).toContainText(SERIES_COPY[lang].step.voice);
  if (step === 3) return;
  await selectVoice(page, VOICES_VI[0] as VoiceDto);
  await continueButton(page, lang).click();
  await expect(currentStep(page)).toContainText(SERIES_COPY[lang].step.style);
}

async function activeInfo(page: Page): Promise<ActiveInfo> {
  return page.evaluate(() => {
    const element = document.activeElement;
    if (element === null) return { tag: 'none', id: '', name: '', type: '', value: '' };
    return {
      tag: element.tagName.toLowerCase(),
      id: (element as HTMLElement).id,
      name: (element.getAttribute('aria-label') ?? element.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 80),
      type: element.getAttribute('type') ?? '',
      value: (element as HTMLInputElement).value ?? '',
    };
  });
}

/** Tabs forward until the focused element matches; throws with the visited trail when it never does. */
async function tabUntil(page: Page, description: string, matches: (info: ActiveInfo) => boolean, max = 80): Promise<ActiveInfo> {
  const visited: string[] = [];
  for (let index = 0; index < max; index += 1) {
    await page.keyboard.press('Tab');
    const info = await activeInfo(page);
    if (matches(info)) return info;
    visited.push(`${info.tag}#${info.id}${info.name === '' ? '' : `(${info.name})`}`);
  }
  throw new Error(`Tab never reached ${description}; visited: ${visited.join(' → ')}`);
}

const isButton = (label: string) => (info: ActiveInfo) => info.tag === 'button' && info.name.includes(label);

test.describe('series create (vi)', () => {
  const L = SERIES_COPY.vi;

  test('Create_FourSteps_PersistsAndAppears: keyboard-only completion posts the trimmed CreateSeriesInput and lands on /series/:id', async ({ page }) => {
    const createdTitle = 'Series mới';
    const firstVoice = VOICES_VI[0] as VoiceDto;
    const firstStyle = STYLE_SEED[0] as StyleTemplateDto;
    const { api } = await openEditor(page, 'vi', {
      api: { detail: seriesDetail({ id: '22222222-2222-4222-8222-222222222222', title: createdTitle }) },
    });

    // Step 1: Tab to the title field, type (the store trims), then Tab to Continue. `ghost` stays the deck default.
    await tabUntil(page, 'the title field', (info) => info.id === 'series-title');
    await page.keyboard.type(`  ${createdTitle}  `);
    await tabUntil(page, 'Continue (step 1)', isButton(L.editor.next));
    await page.keyboard.press('Enter');
    await expect(currentStep(page)).toContainText(L.step.aspect);

    // Step 2: `landscape` is the default.
    await tabUntil(page, 'Continue (step 2)', isButton(L.editor.next));
    await page.keyboard.press('Enter');
    await expect(currentStep(page)).toContainText(L.step.voice);

    // Step 3: Space selects the first voice.
    await tabUntil(page, 'the first voice radio', (info) => info.type === 'radio' && info.value === firstVoice.id);
    await page.keyboard.press('Space');
    await expect(main(page).getByRole('radio', { name: firstVoice.displayName })).toBeChecked();
    await tabUntil(page, 'Continue (step 3)', isButton(L.editor.next));
    await page.keyboard.press('Enter');
    await expect(currentStep(page)).toContainText(L.step.style);

    // Step 4: Space selects the first style, then Enter submits.
    await tabUntil(page, 'the first style radio', (info) => info.type === 'radio' && info.value === firstStyle.id);
    await page.keyboard.press('Space');
    await tabUntil(page, 'the create submit', isButton(L.list.new));
    await page.keyboard.press('Enter');

    await expect.poll(() => api.creates.length).toBe(1);
    expect(api.creates[0]?.postDataJSON()).toEqual({
      title: createdTitle,
      topic: 'ghost',
      language: 'vi',
      aspect: 'landscape',
      voiceId: firstVoice.id,
      styleTemplateId: firstStyle.id,
      storyPreset: null,
    });
    await expect(page).toHaveURL(/\/series\/22222222-2222-4222-8222-222222222222$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(createdTitle);
  });

  test('TrueCrime_OnlyTwoStyles_AndNotice: two cards, the notice, and the seed labels/hints', async ({ page }) => {
    await openEditor(page, 'vi');
    await advanceToStep(page, 'vi', 1, 'Vụ án thử');
    await selectTopic(page, 'vi', 'true_crime');
    await continueButton(page, 'vi').click();
    await continueButton(page, 'vi').click();
    await selectVoice(page, VOICES_VI[0] as VoiceDto);
    await continueButton(page, 'vi').click();

    await expect(currentStep(page)).toContainText(L.step.style);
    await expect(page.getByRole('note')).toHaveText(L.style.trueCrimeNotice);

    const grid = page.getByRole('radiogroup', { name: L.field.style });
    await expect(grid.getByRole('radio')).toHaveCount(TRUE_CRIME_STYLE_NAMES.length);
    for (const name of TRUE_CRIME_STYLE_NAMES) {
      const copy = styleCopy('vi', name);
      await expect(grid.getByRole('radio', { name: copy.label })).toHaveCount(1);
      await expect(grid.getByText(copy.hint, { exact: true })).toHaveCount(1);
    }
    // A fiction template is not offered for a true-crime series.
    await expect(grid.getByText(styleCopy('vi', 'watercolor-night').label, { exact: true })).toHaveCount(0);
  });

  test('LanguageChange_ClearsVoice: the language switch clears the voice and announces series.voice.clearedByLanguage', async ({ page }) => {
    const { catalog } = await openEditor(page, 'vi');
    await advanceToStep(page, 'vi', 3, 'Series đổi ngôn ngữ');
    const firstVoice = VOICES_VI[0] as VoiceDto;
    await selectVoice(page, firstVoice);

    await backToTopicStep(page, 'vi');
    await selectLanguage(page, 'vi', 'en');

    await expect(liveRegion(page, L.voice.clearedByLanguage).first()).toContainText(L.voice.clearedByLanguage);
    // One voices request for the switch itself.
    const languages = catalog.urlsOf(catalog.voicesRequests).map((url) => url.searchParams.get('language'));
    expect(languages.slice(0, 2)).toEqual(['vi', 'en']);

    await continueButton(page, 'vi').click();
    await continueButton(page, 'vi').click();
    await expect(main(page).getByRole('radio', { name: firstVoice.displayName })).toHaveCount(0);
  });

  test('VoiceStep_NoPreviewHidesPlayButton: the no-preview row is a disabled button with no <audio> and no media request', async ({ page }) => {
    const { catalog } = await openEditor(page, 'vi');
    await advanceToStep(page, 'vi', 3);

    // One row per active voice, gender and tags translated from `voices.tag.*`, no rate control anywhere.
    await expect(main(page).getByRole('radio')).toHaveCount(VOICES_VI.length);
    await expect(main(page).getByText(L.voice.gender['female'] ?? '', { exact: true })).toBeVisible();
    await expect(main(page).getByText(VOICES_COPY.vi.tag['storyteller'] ?? '', { exact: true })).toBeVisible();
    await expect(main(page).getByText(L.voice.rateFixed)).toBeVisible();
    await expect(main(page).locator('input[type="range"]')).toHaveCount(0);

    const noPreview = VOICES_VI[1] as VoiceDto;
    await expect(page.getByRole('button', { name: `${L.voice.previewUnavailable} — ${noPreview.displayName}` })).toBeDisabled();
    await expect(page.locator('audio')).toHaveCount(0);
    expect(catalog.audioRequests, 'no media request before (and without) an activation').toHaveLength(0);
  });

  test('VoiceStep_PlaysPreview_AfterInteraction_AndStopsOthers', async ({ page }) => {
    const { catalog } = await openEditor(page, 'vi');
    await advanceToStep(page, 'vi', 3);
    const firstVoice = VOICES_VI[0] as VoiceDto;
    const thirdVoice = VOICES_VI[2] as VoiceDto;

    // `@defer (on interaction)`: no <audio> and no media request before the user asks.
    await expect(page.locator('audio')).toHaveCount(0);
    expect(catalog.audioRequests).toHaveLength(0);

    const first = previewButton(page, 'vi', firstVoice.displayName);
    const third = previewButton(page, 'vi', thirdVoice.displayName);
    await first.click();
    await expect.poll(() => catalog.audioRequests.length).toBe(1);
    await expect(first).toHaveAttribute('aria-pressed', 'true');

    await third.click();
    await expect.poll(() => catalog.audioRequests.length).toBe(2);
    await expect(third).toHaveAttribute('aria-pressed', 'true');
    await expect(first).toHaveAttribute('aria-pressed', 'false');
  });

  test('VoiceStep_LanguageChange_ClearsVoice_AndAnnounces: the picker swaps to the new catalog, nothing stays selected', async ({ page }) => {
    const { catalog } = await openEditor(page, 'vi');
    await advanceToStep(page, 'vi', 3);
    const firstVoice = VOICES_VI[0] as VoiceDto;
    await selectVoice(page, firstVoice);

    await backToTopicStep(page, 'vi');
    await selectLanguage(page, 'vi', 'en');
    await expect(liveRegion(page, L.voice.clearedByLanguage).first()).toContainText(L.voice.clearedByLanguage);
    const languages = catalog.urlsOf(catalog.voicesRequests).map((url) => url.searchParams.get('language'));
    expect(languages.slice(0, 2)).toEqual(['vi', 'en']);

    await continueButton(page, 'vi').click();
    await continueButton(page, 'vi').click();
    for (const voice of VOICES_EN) {
      await expect(main(page).getByRole('radio', { name: voice.displayName })).toBeVisible();
      await expect(main(page).getByRole('radio', { name: voice.displayName })).not.toBeChecked();
    }
    await expect(main(page).getByRole('radio', { name: firstVoice.displayName })).toHaveCount(0);
  });

  test('VoiceStep_ValidationFailed_MarksLanguageControl: the 400 keeps the last good list and marks the language control', async ({ page }) => {
    await openEditor(page, 'vi', { catalog: { voicesFailure: { language: 'en', status: 400, code: 'validation_failed' } } });
    await advanceToStep(page, 'vi', 3);

    await backToTopicStep(page, 'vi');
    await selectLanguage(page, 'vi', 'en');

    // `aria-describedby` settles one observable turn after `mat-select`/`MatFormField` syncs it.
    await expect(languageSelect(page, 'vi')).toHaveAttribute('aria-describedby', /series-language-error/);
    await expect(page.getByRole('alert').filter({ hasText: ERRORS_COPY.vi.validation_failed })).toBeVisible();
    await expect(languageSelect(page, 'vi')).toBeEnabled();

    // The step stays usable: the last good (vi) list is still rendered.
    await continueButton(page, 'vi').click();
    await continueButton(page, 'vi').click();
    await expect(main(page).getByRole('radio')).toHaveCount(VOICES_VI.length);
  });

  test('VoiceStep_InactiveVoiceFallbackRow: a stored voice missing from the catalog keeps its row with the inactive hint', async ({ page }) => {
    const stored = seriesDetail({ voiceId: 'v-gone', title: 'Series cũ' });
    await openEditor(page, 'vi', { path: `/series/${stored.id}/edit`, mode: 'edit', api: { detail: stored } });
    await advanceToStep(page, 'vi', 3, 'Series cũ');

    await expect(main(page).getByText(L.voice.inactive)).toBeVisible();
    await expect(main(page).getByText('v-gone', { exact: true })).toBeVisible();
  });

  test('ConsoleCapture: no console error, and neither the series title nor the preview URL is logged', async ({ page }) => {
    const messages: string[] = [];
    page.on('console', (message) => messages.push(`${message.type()}: ${message.text()}`));

    const { catalog } = await openEditor(page, 'vi');
    const secretTitle = 'Series bí mật';
    await advanceToStep(page, 'vi', 3, secretTitle);
    const firstVoice = VOICES_VI[0] as VoiceDto;
    await previewButton(page, 'vi', firstVoice.displayName).click();
    await expect.poll(() => catalog.audioRequests.length).toBe(1);
    await selectVoice(page, firstVoice);
    await continueButton(page, 'vi').click();
    await expect(page.getByRole('radiogroup', { name: L.field.style })).toBeVisible();

    const leaked = messages.filter(
      (entry) => entry.includes(secretTitle) || entry.includes(PREVIEW_PATH) || entry.includes(firstVoice.id),
    );
    expect(leaked, `console output leaked: ${leaked.join(' | ')}`).toEqual([]);
    // The browser logs one `error` line per refused response; the harness stubs the hub ticket with 503 on purpose,
    // so only app-generated console errors count here (the CSP/Trusted-Types fixture already fails on its own ones).
    const errors = messages.filter((entry) => entry.startsWith('error:') && !entry.includes('Failed to load resource'));
    expect(errors, `console errors: ${errors.join(' | ')}`).toEqual([]);
  });

  test('Create_ServerValidationFailedOnStyle_RendersTheStep4Error_AndLinksIt', async ({ page }) => {
    const { api } = await openEditor(page, 'vi', {
      api: { createFailure: { status: 400, code: 'validation_failed', extra: { errors: { styleTemplateId: ['invalid'] } } } },
    });
    await advanceToStep(page, 'vi', 4);
    const styleGrid = page.getByRole('radiogroup', { name: L.field.style });
    await styleGrid.getByRole('radio', { name: styleCopy('vi', (STYLE_SEED[0] as StyleTemplateDto).name).label }).check();
    await submitButton(page, 'vi').click();

    // The create was refused with field errors for styleTemplateId: the page stays on step 4, the bound control
    // shows the code-specific key, and the grid's radio group is linked to that error text.
    await expect.poll(() => api.creates.length).toBe(1);
    await expect(currentStep(page)).toContainText(L.step.style);
    await expect(page.getByRole('alert').filter({ hasText: ERRORS_COPY.vi.validation_failed })).toBeVisible();
    await expect(styleGrid).toHaveAttribute('aria-describedby', 'series-style-error');
    await expect(page).toHaveURL(/\/series\/new$/);
  });

  test('Catalogs_BackNextCycle_IssuesNoExtraRequest: voices and templates are fetched once per value', async ({ page }) => {
    const { catalog } = await openEditor(page, 'vi');
    await advanceToStep(page, 'vi', 4);
    await expect(page.getByRole('radiogroup', { name: L.field.style })).toBeVisible();
    expect(catalog.voicesRequests).toHaveLength(1);
    expect(catalog.templateRequests).toHaveLength(1);

    await backButton(page, 'vi').click();
    await backButton(page, 'vi').click();
    await backButton(page, 'vi').click();
    await expect(currentStep(page)).toContainText(L.step.topic);
    await continueButton(page, 'vi').click();
    await continueButton(page, 'vi').click();
    await continueButton(page, 'vi').click();
    await expect(currentStep(page)).toContainText(L.step.style);

    // Walking back and re-entering steps 3 and 4 never re-asks the catalogs the wizard already loaded.
    expect(catalog.voicesRequests).toHaveLength(1);
    expect(catalog.templateRequests).toHaveLength(1);
  });

  for (const step of [1, 2, 3, 4] as const) {
    test(`axe (WCAG 2.1 AA) on /series/new step ${step}`, async ({ page }) => {
      await openEditor(page, 'vi');
      await advanceToStep(page, 'vi', step);
      await expectA11yClean(page);
    });
  }
});

test.describe('series create (360 px, vi)', () => {
  test.use({ viewport: { width: 360, height: 740 } });

  test('no horizontal scroll on every step of /series/new', async ({ page }) => {
    await openEditor(page, 'vi');
    await expectNoHorizontalScroll(page);

    await fillTitle(page, 'vi', 'Series 360');
    await continueButton(page, 'vi').click();
    await expect(currentStep(page)).toContainText(SERIES_COPY.vi.step.aspect);
    await expectNoHorizontalScroll(page);

    await continueButton(page, 'vi').click();
    await expect(currentStep(page)).toContainText(SERIES_COPY.vi.step.voice);
    await expectNoHorizontalScroll(page);

    await selectVoice(page, VOICES_VI[0] as VoiceDto);
    await continueButton(page, 'vi').click();
    await expect(currentStep(page)).toContainText(SERIES_COPY.vi.step.style);
    await expectNoHorizontalScroll(page);
  });
});

test.describe('series create (en)', () => {
  test.use({ locale: 'en-US' });

  const L = SERIES_COPY.en;

  test('Create_FourSteps_PersistsAndAppears [en]', async ({ page }) => {
    const createdTitle = 'My new series';
    const firstVoice = VOICES_VI[0] as VoiceDto;
    const firstStyle = STYLE_SEED[0] as StyleTemplateDto;
    const { api } = await openEditor(page, 'en', {
      api: { detail: seriesDetail({ id: '22222222-2222-4222-8222-222222222222', title: createdTitle }) },
    });
    await advanceToStep(page, 'en', 3, `  ${createdTitle}  `);
    await selectVoice(page, firstVoice);
    await continueButton(page, 'en').click();
    await main(page).getByRole('radio', { name: styleCopy('en', firstStyle.name).label }).check();
    await submitButton(page, 'en').click();

    await expect.poll(() => api.creates.length).toBe(1);
    expect(api.creates[0]?.postDataJSON()).toEqual({
      title: createdTitle,
      topic: 'ghost',
      language: 'vi',
      aspect: 'landscape',
      voiceId: firstVoice.id,
      styleTemplateId: firstStyle.id,
      storyPreset: null,
    });
    await expect(page).toHaveURL(/\/series\/22222222-2222-4222-8222-222222222222$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(createdTitle);
  });

  test('TrueCrime_OnlyTwoStyles_AndNotice [en]: the seed labels/hints in English', async ({ page }) => {
    await openEditor(page, 'en');
    await advanceToStep(page, 'en', 1, 'Case study');
    await selectTopic(page, 'en', 'true_crime');
    await continueButton(page, 'en').click();
    await continueButton(page, 'en').click();
    await selectVoice(page, VOICES_VI[0] as VoiceDto);
    await continueButton(page, 'en').click();

    await expect(page.getByRole('note')).toHaveText(L.style.trueCrimeNotice);
    const grid = page.getByRole('radiogroup', { name: L.field.style });
    await expect(grid.getByRole('radio')).toHaveCount(TRUE_CRIME_STYLE_NAMES.length);
    for (const name of TRUE_CRIME_STYLE_NAMES) {
      const copy = styleCopy('en', name);
      await expect(grid.getByRole('radio', { name: copy.label })).toHaveCount(1);
      await expect(grid.getByText(copy.hint, { exact: true })).toHaveCount(1);
    }
  });

  for (const step of [1, 2, 3, 4] as const) {
    test(`axe (WCAG 2.1 AA) on /series/new step ${step} [en]`, async ({ page }) => {
      await openEditor(page, 'en');
      await advanceToStep(page, 'en', step);
      await expectA11yClean(page);
    });
  }
});
