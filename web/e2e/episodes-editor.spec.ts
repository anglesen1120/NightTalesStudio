import type { Locator } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { EPISODE_ID } from './fixtures/episodes-stubs';
import { ERRORS_COPY } from './fixtures/series-stubs';
import {
  EDITED, EDITED_CONTENT, EDITOR_COPY, EDITOR_PATH, FRESH_STORY_ETAG, FRESH_VERSION_ID,
  STORED_STORY_ETAG, STORY_CONTENT, editStory, editorDetail, expectEditor, expectSaved,
  expectStoryPut, mobileSegmentsFlow, openEditor, saveButton, saveReloadFlow, segment,
  segmentGroup, storySnapshot, stubStoryEditor, watchStoryPrivacy,
} from './fixtures/story-editor-stubs';

// 10.5.3-T1: backend-free editor proof, sharing session/SignalR/axe/CSP fixtures with generation and list specs.
// Real compose/mock-mpt acceptance remains owned by 11.5.1-T1.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });
const L = EDITOR_COPY.vi;
const CONFLICT = ERRORS_COPY.vi.conflict;

test.describe('story editor (vi, stubbed)', () => {
  test('EpisodeList_RowClickThrough_LoadsCurrentStory', async ({ page }) => {
    await stubStoryEditor(page, 'vi');
    await page.goto('/episodes');
    await page.getByRole('main').getByRole('link', { name: STORY_CONTENT.title, exact: true }).click();
    await expectEditor(page, 'vi');
    await expectA11yClean(page);
  });

  test('Dashboard_RowClickThrough_LoadsCurrentStory', async ({ page }) => {
    await stubStoryEditor(page, 'vi');
    await page.goto('/dashboard');
    const review = page.getByRole('region').filter({
      has: page.getByRole('heading', { name: 'Cần bạn xem', exact: true }),
    });
    const row = review.getByRole('listitem').filter({ hasText: STORY_CONTENT.title });
    await expect(row).toHaveCount(1);
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: `Xem truyện ${STORY_CONTENT.title}`, exact: true }).click();
    await expectEditor(page, 'vi');
    await expectA11yClean(page);
  });

  test('EditTitleHookParagraphKeyword_SaveExactlyOneFullStoryWithIfMatch_ReloadsSavedVersion', async ({ page }) => {
    await saveReloadFlow(page, 'vi');
  });

  test('Typing_NoAutosaveAndNoBrowserStorage_BeforeAndAfterExplicitSave', async ({ page }) => {
    const privacy = watchStoryPrivacy(page);
    const api = await openEditor(page, 'vi');
    await page.waitForTimeout(650);
    let requestsWhileTyping = 0;
    const count = (): void => { requestsWhileTyping += 1; };
    page.on('request', count);
    await editStory(page, 'vi');
    await expect(saveButton(page, 'vi')).toBeEnabled();
    await page.waitForTimeout(650);
    page.off('request', count);
    expect(requestsWhileTyping, 'typing must not issue any HTTP request').toBe(0);
    expect(api.puts).toHaveLength(0);
    await privacy();
    await saveButton(page, 'vi').click();
    await expectSaved(page, 'vi');
    expect(api.puts).toHaveLength(1);
    await privacy();
  });

  test('Save412_CancelKeepsAllDraftEdits_SaveMineRefetchesThenUsesFreshStoryEtag', async ({ page }) => {
    const fresh = editorDetail({ currentStory: storySnapshot({ ...STORY_CONTENT, title: 'Bản vừa được lưu trên máy chủ' }, FRESH_VERSION_ID, 2) });
    const api = await openEditor(page, 'vi', { failures: [{ status: 412, detailAfter: fresh }, { status: 412, detailAfter: fresh }] });
    await editStory(page, 'vi');
    await saveButton(page, 'vi').click();
    const dialog = page.getByRole('dialog', { name: CONFLICT.title, exact: true });
    await expect(dialog).toBeVisible();
    await expect(segment(page, L.title)).toHaveValue(EDITED.title);
    await expect(segment(page, L.hook)).toHaveValue(EDITED.hook);
    await expect(segment(page, L.paragraph(1))).toHaveValue(EDITED.paragraph);
    await expect(segmentGroup(page, L.paragraph(1)).getByRole('textbox', { name: L.scene(1), exact: true })).toHaveValue(EDITED.keyword);
    await page.waitForTimeout(650);
    expect(api.puts, '412 must never automatically retry').toHaveLength(1);
    expectStoryPut(api.puts[0]!, STORED_STORY_ETAG);
    await expectA11yClean(page);
    await dialog.getByRole('button', { name: CONFLICT.cancel, exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(segment(page, L.title)).toHaveValue(EDITED.title);
    await expect(segment(page, L.hook)).toHaveValue(EDITED.hook);
    await expect(segment(page, L.paragraph(1))).toHaveValue(EDITED.paragraph);
    await expect(saveButton(page, 'vi')).toBeEnabled();
    await saveButton(page, 'vi').click();
    await expect(dialog).toBeVisible();
    const getsBeforeChoice = api.detailGets.length;
    const orderBeforeChoice = api.order.length;
    await dialog.getByRole('button', { name: CONFLICT.saveAsNew, exact: true }).click();
    await expectSaved(page, 'vi');
    await expect(dialog).toHaveCount(0);
    expect(api.detailGets.length).toBe(getsBeforeChoice + 1);
    expect(api.order.slice(orderBeforeChoice)).toEqual(['get', 'put']);
    expect(api.puts).toHaveLength(3);
    expectStoryPut(api.puts[1]!, STORED_STORY_ETAG);
    expectStoryPut(api.puts[2]!, FRESH_STORY_ETAG, EDITED_CONTENT);
    await expect(segment(page, L.title)).toHaveValue(EDITED.title);
    await expect(page.getByText('PRIVATE SERVER DETAIL MUST NOT BE RENDERED', { exact: true })).toHaveCount(0);
  });

  test('Save409_RefetchesShowsInvalidStateAndOnlyAllowedNextActions', async ({ page }) => {
    const approved = editorDetail({ status: 'story_approved', nextActions: ['reopen_story'] });
    const api = await openEditor(page, 'vi', { failures: [{ status: 409, detailAfter: approved }] });
    await segment(page, L.title).fill(EDITED.title);
    const getCount = api.detailGets.length;
    await saveButton(page, 'vi').click();
    await expect.poll(() => api.detailGets.length).toBe(getCount + 1);
    await expect(page.getByText(L.invalid, { exact: true }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: L.reopen, exact: true }).or(page.getByRole('link', { name: L.reopen, exact: true }))).toBeVisible();
    await expect(page.getByRole('button', { name: L.approve, exact: true }).or(page.getByRole('link', { name: L.approve, exact: true }))).toHaveCount(0);
    await expect(saveButton(page, 'vi').and(page.locator(':enabled'))).toHaveCount(0);
    expect(api.puts).toHaveLength(1);
    await expect(page.getByText('PRIVATE SERVER DETAIL MUST NOT BE RENDERED', { exact: true })).toHaveCount(0);
    await expectA11yClean(page);
  });

  test('NoEditStoryAction_ReadOnlySegmentsAndSceneControls_NoSave', async ({ page }) => {
    const api = await openEditor(page, 'vi', { detail: editorDetail({ status: 'story_approved', nextActions: ['reopen_story'] }) });
    const labels = [L.title, L.hook, ...STORY_CONTENT.paragraphs.map((_p, index) => L.paragraph(index + 1)), L.ending, L.description];
    for (const label of labels) await expect(segment(page, label)).toHaveAttribute('readonly', '');
    await expect(saveButton(page, 'vi').and(page.locator(':enabled'))).toHaveCount(0);
    await expect(page.getByRole('button', { name: L.add, exact: true }).and(page.locator(':enabled'))).toHaveCount(0);
    await expect(page.getByRole('button', { name: new RegExp(`^${L.remove}`) }).and(page.locator(':enabled'))).toHaveCount(0);
    await expect(page.getByText(L.invalid, { exact: true }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: L.reopen, exact: true }).or(page.getByRole('link', { name: L.reopen, exact: true }))).toBeVisible();
    expect(api.puts).toHaveLength(0);
    await expectA11yClean(page);
  });

  test('SegmentDeepLink_P3FocusesFourthParagraphByCanonicalId', async ({ page }) => {
    await openEditor(page, 'vi', {}, '?segment=p:3');
    const paragraph = segment(page, L.paragraph(4));
    await expect(paragraph).toHaveAttribute('id', 'seg-p-3');
    await expect(paragraph).toHaveAttribute('data-segment', 'p:3');
    await expect(paragraph).toBeFocused();
    await expect(paragraph).toBeInViewport();
    await expect(page).toHaveURL(`${EDITOR_PATH}?segment=p:3`);
  });

  test('ThirdSceneKeyword_IsRefusedWithLimitMessageAndNoRequest', async ({ page }) => {
    const api = await openEditor(page, 'vi');
    const group = segmentGroup(page, L.paragraph(2));
    const scenes = group.getByRole('list', { name: L.scenes, exact: true });
    await expect(scenes.getByRole('textbox')).toHaveCount(2);
    await group.getByRole('button', { name: L.add, exact: true }).click();
    await expect(group.getByText(L.limit, { exact: true })).toBeVisible();
    await expect(scenes.getByRole('textbox')).toHaveCount(2);
    await expect(scenes.getByRole('textbox', { name: L.scene(1), exact: true })).toHaveValue(STORY_CONTENT.paragraphs[1]!.keywords[0]!);
    await expect(scenes.getByRole('textbox', { name: L.scene(2), exact: true })).toHaveValue(STORY_CONTENT.paragraphs[1]!.keywords[1]!);
    await expect(saveButton(page, 'vi')).toBeDisabled();
    expect(api.puts).toHaveLength(0);
    await expectA11yClean(page);
  });

  test('Mobile360_AllSegmentsReachableByNamedSelector_AxeAndNoHorizontalScroll', async ({ page }) => {
    await mobileSegmentsFlow(page, 'vi');
  });

  test('KeyboardTabOrder_ReachesEveryTextareaSceneControlAndSave', async ({ page }) => {
    await openEditor(page, 'vi');
    await segment(page, L.title).fill(EDITED.title);
    await expect(saveButton(page, 'vi')).toBeEnabled();
    const labels = [L.title, L.hook, ...STORY_CONTENT.paragraphs.map((_p, index) => L.paragraph(index + 1)), L.ending, L.description];
    const textareas = labels.map((label) => segment(page, label));
    const targets: Locator[] = [...textareas];
    for (const label of [L.hook, ...STORY_CONTENT.paragraphs.map((_p, index) => L.paragraph(index + 1)), L.ending]) {
      const group = segmentGroup(page, label);
      const scenes = group.getByRole('list', { name: L.scenes, exact: true });
      for (const input of await scenes.getByRole('textbox').all()) targets.push(input);
      for (const remove of await group.getByRole('button', { name: new RegExp(`^${L.remove}`) }).all()) targets.push(remove);
      targets.push(group.getByRole('button', { name: L.add, exact: true }));
    }
    targets.push(saveButton(page, 'vi'));
    const reached = new Map<number, number>();
    // Start at the first textarea; all remaining navigation is keyboard-only, with one wrap for title itself.
    for (let step = 0; step < 140 && reached.size < targets.length; step += 1) {
      await page.keyboard.press('Tab');
      for (let index = 0; index < targets.length; index += 1) {
        if (!reached.has(index) && await targets[index]!.evaluate((node) => node === document.activeElement)) reached.set(index, step);
      }
    }
    expect(reached.size, 'Tab must reach every segment, keyword input, remove/add control and save').toBe(targets.length);
    // Focus began at title after the edit, so it wraps last; remaining textareas keep the story reading order.
    const textareaOrder = textareas.slice(1).map((_textarea, index) => reached.get(index + 1)!);
    expect(textareaOrder).toEqual([...textareaOrder].sort((a, b) => a - b));
    await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}$`));
  });
});
