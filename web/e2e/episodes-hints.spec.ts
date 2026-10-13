import type { Locator, Page, Request } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { EPISODE_ID } from './fixtures/episodes-stubs';
import { fulfilJson } from './fixtures/series-stubs';
import {
  EDITOR_COPY, EDITOR_STRUCTURE_COPY, SAVED_VERSION_ID, STORED_VERSION_ID, STORY_CONTENT, editorDetail,
  expectSaved, openEditor, saveButton, segment, segmentGroup, storySnapshot, type StoryWire,
} from './fixtures/story-editor-stubs';
import type { CurrentStoryDto } from '../src/app/shared/api/models/current-story-dto';
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };
import qualityEn from '../src/assets/i18n/quality/en.json' with { type: 'json' };

declare global {
  interface Window {
    /** Text of the editor's annotation live region, recorded by the observer this spec installs. */
    __ntsAnnounce?: string[];
  }
}

// 10.5.10-T1 quality hints + inline annotations (en): the saved `qualityHints[]` set renders inline under the segment
// it is about and, for `meta`/unresolved addresses, only in the panel; a rule the shipped assets cannot resolve shows
// the shared "not translated" copy and never the raw key; a local edit never re-keys them; a reorder keeps a hint on
// the text it describes; 360 px wraps the annotation under its textarea; and the `409 keywords_flagged` chips render
// through the real store path (`window.ng` debug API, the producer itself is 10.5.6-T1) and are cleared by the next
// successful save. Stubbed with `page.route` like every other episodes spec; real compose acceptance stays 11.5.1-T1.
test.use({ locale: 'en-US', timezoneId: 'UTC' });
const L = EDITOR_COPY.en;
const PREFS_KEY = 'nts.prefs.v1';

type HintRow = CurrentStoryDto['qualityHints'][number];
const hint = (overrides: Partial<HintRow>): HintRow => ({
  ruleId: 'rule', messageKey: 'quality.hook_missing', evidence: '', kind: 'objective', severity: 'advisory',
  segment: 'p:0', repairDirection: null, measured: null, threshold: null, ...overrides,
});

/** Real `quality.*` keys of `src/assets/i18n/quality/en.json`; the last one deliberately has no entry. */
const BLOCKING_HINT = hint({
  ruleId: 'keyword_count_per_segment', severity: 'blocking', kind: 'objective', segment: 'p:1',
  messageKey: 'quality.keyword_count_per_segment', evidence: 'Paragraph 2 has 3 scenes', repairDirection: 'Keep at most two scenes',
});
const TASTE_HINT = hint({
  ruleId: 'repetitive_phrasing', severity: 'advisory', kind: 'taste', segment: 'hook',
  messageKey: 'quality.repetitive_phrasing', evidence: 'Repeated wording',
});
const META_HINT = hint({ ruleId: 'scenes_sparse', severity: 'advisory', kind: 'taste', segment: 'meta', messageKey: 'quality.scenes_sparse' });
const UNKNOWN_HINT = hint({
  ruleId: 'made_up_rule', severity: 'advisory', kind: 'objective', segment: 'p:2', messageKey: 'quality.made_up_rule_not_in_assets',
});
const HINTS: readonly HintRow[] = [BLOCKING_HINT, TASTE_HINT, META_HINT, UNKNOWN_HINT];
/** What the stub returns for the next PUT: a different set, so the save both clears the flags and moves the count. */
const SAVED_HINTS: readonly HintRow[] = [
  hint({ ruleId: 'hook_too_long', severity: 'blocking', kind: 'objective', segment: 'hook', messageKey: 'quality.hook_too_long', evidence: 'Hook has 4 sentences' }),
  hint({ ruleId: 'fact_refs_missing', severity: 'advisory', kind: 'objective', segment: 'p:2', messageKey: 'quality.fact_refs_missing' }),
];
const HINT_DETAIL = editorDetail({ currentStory: { ...storySnapshot(), qualityHints: [...HINTS] } });

const BLOCKING_ID = 'ann-p-1-keyword_count_per_segment';
const TASTE_ID = 'ann-hook-repetitive_phrasing';
const META_ID = 'ann-meta-scenes_sparse';
const UNKNOWN_ID = 'ann-p-2-made_up_rule';
const FLAG_ID = 'ann-p-1-keyword_name_detected-k1';

const sheetNoteIds = (page: Page): Promise<string[]> =>
  page.locator('.story-editor__sheet .inline-annotation').evaluateAll((nodes) => nodes.map((node) => node.id));
const paragraphValues = (page: Page): Promise<string[]> =>
  page.locator('textarea[data-segment^="p:"]').evaluateAll((nodes) => nodes.map((node) => (node as HTMLTextAreaElement).value));
const noHorizontalOverflow = (page: Page): Promise<boolean> =>
  page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
const inlineNote = (page: Page, id: string): Locator => page.locator(`#${id}`);

/**
 * Runs in the page: drives the exact store entry point 10.5.6-T1's 409 handler will call, through the dev-build
 * Angular debug API, and answers `false` when that API or the component is unavailable. No translation work here: the
 * annotations already on screen are rendered by components that declare the `episodes`/`quality`/`errors` scopes, so
 * those scopes are loaded by the time the flags are applied.
 */
async function driveKeywordFlag(): Promise<boolean> {
  const debug: unknown = Reflect.get(window, 'ng');
  if (typeof debug !== 'object' || debug === null) return false;
  const getComponent = Reflect.get(debug, 'getComponent');
  if (typeof getComponent !== 'function') return false;
  const host = document.querySelector('nts-story-review-page');
  if (host === null) return false;
  const component: unknown = getComponent.call(debug, host);
  if (typeof component !== 'object' || component === null) return false;
  const store: unknown = Reflect.get(component, 'store');
  if (typeof store !== 'object' || store === null) return false;
  const setKeywordFlags = Reflect.get(store, 'setKeywordFlags');
  if (typeof setKeywordFlags !== 'function') return false;
  setKeywordFlags.call(store, [{ address: 'p:1', index: 1, rule: 'keyword_name_detected' }]);
  return true;
}

/** Runs in the page: records every non-empty text the annotation live region is set to. */
function recordAnnouncements(): void {
  const target = document.querySelector('.story-editor__annotation-announcement');
  const seen: string[] = [];
  window.__ntsAnnounce = seen;
  if (target === null) return;
  new MutationObserver(() => {
    const text = (target.textContent ?? '').trim();
    if (text !== '') seen.push(text);
  }).observe(target, { childList: true, characterData: true, subtree: true });
}

/**
 * Shadows the shared stub's story PUT with a 200 that carries a chosen `qualityHints[]` set: the save path is what
 * the announcement and the flag-clearing assertions depend on. Registered after `openEditor` (Playwright resolves the
 * most recently registered matching route first), so the shared GET/detail/list routes stay the fixture's.
 */
async function stubSaveResponse(page: Page, hints: readonly HintRow[]): Promise<Request[]> {
  const puts: Request[] = [];
  await page.route(`**/api/v1/episodes/${EPISODE_ID}/story`, async (route) => {
    const request = route.request();
    puts.push(request);
    const content = request.postDataJSON() as StoryWire;
    const etag = `"sv-${SAVED_VERSION_ID}"`;
    await fulfilJson(route, 200, {
      versionId: SAVED_VERSION_ID, version: 2, etag, source: 'manual', wordCount: 123, qualityHints: hints,
      keywords: [], title: content.title, content, createdAt: '2026-10-05T13:00:00Z',
      factSheetId: null, parentVersionId: STORED_VERSION_ID,
    }, { ETag: etag });
  });
  return puts;
}

test.describe('quality hints and inline annotations (en, stubbed)', () => {
  test('SavedHints_RenderInlineUnderTheirSegment_MetaOnlyInPanel_UnknownKeyNeverRaw', async ({ page }) => {
    await openEditor(page, 'en', { detail: HINT_DETAIL });

    // (1) A blocking objective hint sits under the paragraph textarea it is about, with the severity icon + copy,
    // and the textarea points at it through aria-describedby.
    const paragraph = page.locator('textarea[data-segment="p:1"]');
    const blocking = inlineNote(page, BLOCKING_ID);
    await expect(blocking).toBeVisible();
    await expect(blocking).toHaveAttribute('role', 'note');
    await expect(blocking).toContainText(episodesEn.annotations.blocking);
    await expect(blocking).toContainText(qualityEn.keyword_count_per_segment);
    await expect(blocking.locator('nts-icon-alert')).toHaveCount(1);
    await expect(paragraph).toHaveAttribute('aria-describedby', new RegExp(BLOCKING_ID));
    // The advisory taste hint on the hook renders the same way under its own segment.
    await expect(inlineNote(page, TASTE_ID)).toBeVisible();
    await expect(page.locator('textarea[data-segment="hook"]')).toHaveAttribute('aria-describedby', new RegExp(TASTE_ID));
    // (2) A hint whose rule the shipped assets cannot resolve shows the shared sentence, never the raw key.
    await expect(inlineNote(page, UNKNOWN_ID)).toContainText(episodesEn.annotations.unknown);
    await expect(page.locator('body')).not.toContainText('made_up_rule_not_in_assets');

    // (3) The panel (deferred until it is scrolled into view) lists every hint, grouped by kind.
    const panel = page.locator('nts-annotation-panel');
    await expect(panel.locator('.annotation-panel__count')).toHaveText(String(HINTS.length));
    await panel.scrollIntoViewIfNeeded();
    await expect(panel.locator('.annotation-panel__body')).toBeVisible();
    for (const id of [BLOCKING_ID, TASTE_ID, META_ID, UNKNOWN_ID]) {
      await expect(panel.locator(`#${id}-panel`)).toHaveCount(1);
    }
    await expect(panel.getByRole('heading', { name: episodesEn.annotations.objective, exact: true })).toBeVisible();
    await expect(panel.getByRole('heading', { name: episodesEn.annotations.taste, exact: true })).toBeVisible();

    // (4) The `meta` hint is in the panel only: the description textarea points at nothing but its own counter.
    await expect(panel.locator(`#${META_ID}-panel`)).toContainText(qualityEn.scenes_sparse);
    await expect(panel.locator(`#${META_ID}-panel`)).toContainText(episodesEn.annotations.meta);
    await expect(page.locator('textarea[data-segment="meta"]')).toHaveAttribute('aria-describedby', 'seg-meta-counter');
    await expect(page.locator('nts-story-meta-editor nts-inline-annotation')).toHaveCount(0);

    await expectA11yClean(page);
  });

  test('EditingText_DoesNotRekeyOrDropAnnotations', async ({ page }) => {
    await openEditor(page, 'en', { detail: HINT_DETAIL });
    const before = await sheetNoteIds(page);
    expect(before).toEqual([TASTE_ID, BLOCKING_ID, UNKNOWN_ID]);

    await segment(page, L.paragraph(2)).fill('A rewritten paragraph with entirely different words.');
    await expect(saveButton(page, 'en')).toBeEnabled();
    await expect.poll(() => sheetNoteIds(page)).toEqual(before);
    await expect(inlineNote(page, BLOCKING_ID)).toContainText(qualityEn.keyword_count_per_segment);
    await expect(page.locator('textarea[data-segment="p:1"]')).toHaveAttribute('aria-describedby', new RegExp(BLOCKING_ID));
    await expectA11yClean(page);
  });

  test('Reorder_KeepsTheAnnotationOnTheMovedParagraph', async ({ page }) => {
    await openEditor(page, 'en', { detail: HINT_DETAIL });
    const before = STORY_CONTENT.paragraphs.map((paragraph) => paragraph.text);
    expect(await paragraphValues(page)).toEqual(before);

    // Paragraph 2 (the one the blocking hint is about) moves up: the hint must follow its text, not its old index.
    await page.getByRole('button', { name: EDITOR_STRUCTURE_COPY.en.moveUpAt(2), exact: true }).click();
    await expect.poll(() => paragraphValues(page)).toEqual([before[1]!, before[0]!, before[2]!, before[3]!]);
    const movedId = 'ann-p-0-keyword_count_per_segment';
    await expect(inlineNote(page, movedId)).toContainText(qualityEn.keyword_count_per_segment);
    await expect(page.locator('textarea[data-segment="p:0"]')).toHaveAttribute('aria-describedby', new RegExp(movedId));
    await expect(inlineNote(page, BLOCKING_ID)).toHaveCount(0);
    await expect(page.locator('textarea[data-segment="p:1"]')).not.toHaveAttribute('aria-describedby', new RegExp(movedId));
    await expectA11yClean(page);
  });

  test('Narrow360_NoHorizontalScroll_AnnotationWrapsUnderItsTextarea', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await openEditor(page, 'en', { detail: HINT_DETAIL });
    expect(await noHorizontalOverflow(page)).toBe(true);

    const field = await page.locator('textarea[data-segment="p:1"]').boundingBox();
    const note = await inlineNote(page, BLOCKING_ID).boundingBox();
    expect(field, 'the paragraph textarea must be laid out').not.toBeNull();
    expect(note, 'the inline annotation must be laid out').not.toBeNull();
    expect(note!.y, 'the annotation must wrap below its textarea').toBeGreaterThanOrEqual(field!.y + field!.height - 1);
    expect(note!.x).toBeGreaterThanOrEqual(0);
    expect(note!.x + note!.width, 'the annotation must stay inside the 360 px viewport').toBeLessThanOrEqual(360);
    expect(await noHorizontalOverflow(page)).toBe(true);
    await expectA11yClean(page);
  });

  test('KeywordFlags_RealStorePath_ChipSurvivesALocalEdit_NextSaveClearsIt_AnnouncementCountedOnce', async ({ page }) => {
    await openEditor(page, 'en', { detail: HINT_DETAIL });
    const puts = await stubSaveResponse(page, SAVED_HINTS);
    // The producer is 10.5.6-T1 (its 409 handler calls exactly this entry point); until it lands the spec drives it
    // itself through the dev-build Angular debug API. Without the API the whole scenario is skipped loudly.
    expect(await page.evaluate(driveKeywordFlag), 'window.ng.getComponent must expose the page store in a dev build').toBe(true);

    // The chip renders icon + text and its input is described by the rule note.
    const chips = segmentGroup(page, L.paragraph(2)).locator('.segment-chips__item--flagged');
    await expect(chips).toHaveCount(1);
    await expect(chips.locator('nts-icon-alert')).toHaveCount(1);
    await expect(chips).toContainText(episodesEn.editor.keywordFlagged);
    const input = chips.getByRole('textbox');
    await expect(input).toHaveAttribute('aria-invalid', 'true');
    await expect(input).toHaveAttribute('aria-describedby', FLAG_ID);
    await expect(page.locator(`#${FLAG_ID}`)).toContainText(errorsEn.keywordsFlaggedRule.keyword_name_detected);

    // A local edit of the flagged chip never clears the flag (WR6).
    await input.fill('a rewritten scene');
    await expect(chips).toHaveCount(1);
    await expect(input).toHaveAttribute('aria-invalid', 'true');
    await expect(page.locator(`#${FLAG_ID}`)).toContainText(errorsEn.keywordsFlaggedRule.keyword_name_detected);

    // The next successful save clears it and announces the new count exactly once.
    await page.evaluate(recordAnnouncements);
    await expect(saveButton(page, 'en')).toBeEnabled();
    await saveButton(page, 'en').click();
    await expectSaved(page, 'en');
    expect(puts, 'one successful save is one request').toHaveLength(1);
    await expect(chips).toHaveCount(0);
    await expect(page.locator(`#${FLAG_ID}`)).toHaveCount(0);
    await expect(inlineNote(page, 'ann-hook-hook_too_long')).toBeVisible();
    const announcement = episodesEn.annotations.announce.replace('{{count}}', String(SAVED_HINTS.length));
    await expect(page.locator('.story-editor__annotation-announcement')).toHaveText(announcement);
    await expect.poll(() => page.evaluate(() => window.__ntsAnnounce ?? [])).toEqual([announcement]);
    await expectA11yClean(page);
  });

  test('Axe_LightAndDark_WithInlineAnnotationsAndPanel', async ({ page }) => {
    for (const theme of ['light', 'dark'] as const) {
      await page.goto('about:blank');
      await page.addInitScript(
        ([key, value]) => localStorage.setItem(key as string, value as string),
        [PREFS_KEY, JSON.stringify({ uiLanguage: 'en', theme })],
      );
      await openEditor(page, 'en', { detail: HINT_DETAIL });
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      const panel = page.locator('nts-annotation-panel');
      await panel.scrollIntoViewIfNeeded();
      await expect(panel.locator('.annotation-panel__body')).toBeVisible();
      await expectA11yClean(page);
      await page.unrouteAll({ behavior: 'ignoreErrors' });
    }
  });
});
