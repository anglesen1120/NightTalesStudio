import type { Browser, Locator, Page, Request } from '@playwright/test';
import { expectA11yClean, waitForOverlayAnimationEnd } from './fixtures/axe';
import { describeCsp, expect, installCspDetector, test } from './fixtures/csp.fixture';
import { EPISODE_ID, stubEpisodes, type EpisodeStubs } from './fixtures/episodes-stubs';
import { fulfilJson, problem } from './fixtures/series-stubs';
import {
  EDITED, EDITOR_COPY, EDITOR_PATH, STORED_STORY_ETAG, STORED_VERSION_ID, STORY_CONTENT, editorDetail,
  expectEditor, expectSaved, expectStoryPut, saveButton, segment, storySnapshot, type StoryWire,
} from './fixtures/story-editor-stubs';
import coreEn from '../src/assets/i18n/core/en.json' with { type: 'json' };
import coreVi from '../src/assets/i18n/core/vi.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };

// 10.5.10-T1 conflict flow (vi, stubbed): TWO real browser contexts write the same episode through one scripted
// server, so a stale `If-Match` is a genuine race rather than a canned reply. Covers the 412 conflict dialog (role,
// focus trap, focus return to Save after Cancel, the differing-segment list, WCAG 2.1 AA), the single user-authorised
// "save mine as new" attempt (GET fresh etag then PUT with it), the destructive confirm on the reload path, the busy
// variant after a second 412, byte-identical typed text throughout, and the non-modal "changed elsewhere" notice a
// real hub invalidation raises while the draft is dirty. The English describe at the end repeats the full two-context
// flow with the en bundles and walks BOTH dialog actions, so neither language can drift. Real compose acceptance
// stays with 11.5.1-T1.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });
const L = EDITOR_COPY.vi;
/**
 * The conflict dialog renders its own `errors.conflict.*` chrome; the page only passes body/detail keys into it. Both
 * languages are mapped here so a scenario can pick its own copy; the existing ones keep the vi default.
 */
const DIALOG_COPY = { vi: errorsVi.conflict, en: errorsEn.conflict };
const PAGE_CONFLICT_COPY = { vi: episodesVi.conflict, en: episodesEn.conflict };
const D = DIALOG_COPY.vi;
const C = PAGE_CONFLICT_COPY.vi;
/** The shared confirm dialog's own chrome comes from the `core` scope, per language. */
const CONFIRM_CANCEL: Readonly<Record<'vi' | 'en', string>> = {
  vi: coreVi.core.ui.confirm.cancel,
  en: coreEn.core.ui.confirm.cancel,
};
/** Typed by hand, not filled: every step below re-reads it to prove the draft survived byte for byte. */
const TYPED_TITLE = 'Bản nháp chưa lưu của B ✓';
/** What context A saved: a different title, so B's draft differs from the server on title/hook/paragraph 1. */
const SERVER_TITLE = 'Bản của A đã lưu trên máy chủ';
const SERVER_CONTENT: StoryWire = { ...STORY_CONTENT, title: SERVER_TITLE };
/** A later writer's content for the reload path: only the title differs, so the adopted draft is unambiguous. */
const RELOADED_TITLE = 'Bản mới nhất từ máy chủ';
const RELOADED_CONTENT: StoryWire = { ...STORY_CONTENT, title: RELOADED_TITLE };
/** The losing page's second dirty edit, which the reload path must keep until the confirm is accepted. */
const REDRAFTED_TITLE = 'Bản nháp thứ hai chưa lưu';
/** B's draft: the typed title plus the shared `EDITED` hook and first paragraph. */
const B_CONTENT: StoryWire = {
  ...STORY_CONTENT,
  title: TYPED_TITLE,
  hook: { ...STORY_CONTENT.hook, text: EDITED.hook },
  paragraphs: STORY_CONTENT.paragraphs.map((paragraph, index) => index === 0 ? { ...paragraph, text: EDITED.paragraph } : paragraph),
};
const VERSION_IDS = [
  '88888888-8888-4888-8888-888888888888',
  '99999999-9999-4999-8999-999999999999',
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
] as const;

interface StoryServer {
  version: number;
  versionId: string;
  content: StoryWire;
  /** Upcoming PUTs that lose the race even with a matching `If-Match`: another writer wins each of them. */
  forced412: number;
}

const etagFor = (versionId: string): string => `"sv-${versionId}"`;
const detailOf = (server: StoryServer) => editorDetail({ currentStory: storySnapshot(server.content, server.versionId, server.version) });
let nextVersionId = 0;
/** The other writer's save: a new version id (hence a new etag) and new content. */
function bump(server: StoryServer, content: StoryWire): void {
  server.version += 1;
  server.versionId = VERSION_IDS[nextVersionId++ % VERSION_IDS.length]!;
  server.content = content;
}

interface PageStub {
  readonly transport: EpisodeStubs;
  readonly puts: Request[];
  readonly detailGets: Request[];
  readonly order: ('get' | 'put')[];
}

/**
 * One page's routes over the shared server: the episode detail read, the story write (If-Match checked exactly like
 * the real endpoint) and the fixture's session + SignalR transport. Both contexts register it over the SAME server
 * object, which is what makes context B's stale etag a real conflict.
 */
async function stubSharedStory(page: Page, server: StoryServer, lang: 'vi' | 'en' = 'vi'): Promise<PageStub> {
  const transport = await stubEpisodes(page, { lang });
  const puts: Request[] = [];
  const detailGets: Request[] = [];
  const order: ('get' | 'put')[] = [];
  await page.route(`**/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    expect(route.request().method()).toBe('GET');
    detailGets.push(route.request()); order.push('get');
    const detail = detailOf(server);
    await fulfilJson(route, 200, detail, { ETag: detail.etag });
  });
  await page.route(`**/api/v1/episodes/${EPISODE_ID}/story`, async (route) => {
    const request = route.request();
    expect(request.method()).toBe('PUT');
    puts.push(request); order.push('put');
    if (server.forced412 > 0) {
      server.forced412 -= 1;
      bump(server, STORY_CONTENT);
      await fulfilJson(route, 412, problem(412, 'story_version_conflict'), { 'Content-Type': 'application/problem+json' });
      return;
    }
    if (request.headers()['if-match'] !== etagFor(server.versionId)) {
      await fulfilJson(route, 412, problem(412, 'story_version_conflict'), { 'Content-Type': 'application/problem+json' });
      return;
    }
    const content = request.postDataJSON() as StoryWire;
    bump(server, content);
    const etag = etagFor(server.versionId);
    await fulfilJson(route, 200, {
      versionId: server.versionId, version: server.version, etag, source: 'manual', wordCount: 123, qualityHints: [],
      keywords: [], title: content.title, content, createdAt: '2026-10-05T13:00:00Z', factSheetId: null,
      parentVersionId: STORED_VERSION_ID,
    }, { ETag: etag });
  });
  return { transport, puts, detailGets, order };
}

const conflictDialog = (page: Page, lang: 'vi' | 'en' = 'vi') => page.getByRole('dialog', { name: DIALOG_COPY[lang].title, exact: true });
const confirmDialog = (page: Page, lang: 'vi' | 'en' = 'vi') => page.getByRole('dialog', { name: PAGE_CONFLICT_COPY[lang].reloadConfirmTitle, exact: true });
const notice = (page: Page, lang: 'vi' | 'en' = 'vi') => page.getByRole('region', { name: PAGE_CONFLICT_COPY[lang].elsewhereTitle });
/** Types the draft title by hand (never `fill`), so the value can be re-read byte for byte after every step. */
async function typeTitle(page: Page, text: string, lang: 'vi' | 'en' = 'vi'): Promise<void> {
  const title = segment(page, EDITOR_COPY[lang].title);
  await title.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type(text, { delay: 5 });
  await expect(title).toHaveValue(text);
}

/**
 * The shared stale-save scenario (AC #10). Two real browser contexts load the same episode, A saves and moves the
 * etag, and B's own save then carries a stale `If-Match`: the dialog opens over B's dirty draft. Walks focus trap →
 * Cancel (focus back on Save, draft byte-identical) → a second stale save → "save mine as new" (exactly one fresh
 * read, one PUT with that etag). Both languages call it, so their copy cannot drift. Returns the losing page's stub
 * and the shared server, so a caller can carry on into the next conflict.
 */
async function twoContextStaleSave(page: Page, browser: Browser, lang: 'vi' | 'en'): Promise<{ stub: PageStub; server: StoryServer }> {
  const copy = EDITOR_COPY[lang], dialogCopy = DIALOG_COPY[lang], pageCopy = PAGE_CONFLICT_COPY[lang];
  const server: StoryServer = { version: 1, versionId: STORED_VERSION_ID, content: STORY_CONTENT, forced412: 0 };
  const stub = await stubSharedStory(page, server, lang);
  const contextA = await browser.newContext({ locale: lang === 'vi' ? 'vi-VN' : 'en-US', timezoneId: 'UTC' });
  const cspA = await installCspDetector(contextA);
  try {
    const pageA = await contextA.newPage();
    const stubA = await stubSharedStory(pageA, server, lang);
    await page.goto(EDITOR_PATH);
    await expectEditor(page, lang);
    await pageA.goto(EDITOR_PATH);
    await expectEditor(pageA, lang);
    await expect.poll(stubA.transport.connected).toBe(1);
    await expect.poll(stub.transport.connected).toBe(1);

    // Context A saves a version of its own: the shared server moves to a new etag.
    await segment(pageA, copy.title).fill(SERVER_TITLE);
    await saveButton(pageA, lang).click();
    await expectSaved(pageA, lang);
    expect(stubA.puts).toHaveLength(1);
    expectStoryPut(stubA.puts[0]!, STORED_STORY_ETAG, SERVER_CONTENT);
    const freshEtag = etagFor(server.versionId);

    // Context B still holds the etag it loaded with and a dirty draft of its own.
    await typeTitle(page, TYPED_TITLE, lang);
    await segment(page, copy.hook).fill(EDITED.hook);
    await segment(page, copy.paragraph(1)).fill(EDITED.paragraph);
    await saveButton(page, lang).click();

    const dialog = conflictDialog(page, lang);
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    await expect(dialog).toContainText(pageCopy.diffHeading);
    for (const label of [copy.title, copy.hook, copy.paragraph(1)]) await expect(dialog).toContainText(label);
    expect(stub.puts).toHaveLength(1);
    expectStoryPut(stub.puts[0]!, STORED_STORY_ETAG, B_CONTENT);
    // Focus is trapped inside the dialog (the Cancel action carries cdkFocusInitial).
    await expect(dialog.getByRole('button', { name: dialogCopy.cancel, exact: true })).toBeFocused();
    for (let step = 0; step < 5; step += 1) {
      await page.keyboard.press('Tab');
      expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
    }
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);

    // Cancel keeps every byte of the draft and hands focus back to the Save control.
    await dialog.getByRole('button', { name: dialogCopy.cancel, exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(saveButton(page, lang)).toBeFocused();
    await expect(segment(page, copy.title)).toHaveValue(TYPED_TITLE);
    await expect(segment(page, copy.hook)).toHaveValue(EDITED.hook);
    await expect(segment(page, copy.paragraph(1))).toHaveValue(EDITED.paragraph);
    await page.waitForTimeout(650);
    expect(stub.puts, 'a 412 must never be retried automatically').toHaveLength(1);

    // Save again, then the one user-authorised attempt: a fresh read followed by exactly one PUT with that etag.
    await saveButton(page, lang).click();
    await expect(dialog).toBeVisible();
    expect(stub.puts, 'the second manual save is its own request').toHaveLength(2);
    const getsBefore = stub.detailGets.length;
    const orderBefore = stub.order.length;
    await dialog.getByRole('button', { name: dialogCopy.saveAsNew, exact: true }).click();
    await expectSaved(page, lang);
    await expect(dialog).toHaveCount(0);
    expect(stub.detailGets.length).toBe(getsBefore + 1);
    expect(stub.order.slice(orderBefore)).toEqual(['get', 'put']);
    expect(stub.puts).toHaveLength(3);
    expectStoryPut(stub.puts[2]!, freshEtag, B_CONTENT);
    await expect(segment(page, copy.title)).toHaveValue(TYPED_TITLE);
    await page.waitForTimeout(650);
    expect(stub.puts).toHaveLength(3);

    // The other context must be free of CSP / Trusted Types violations too.
    await pageA.evaluate(() => new Promise<void>((resolve) => setTimeout(resolve, 100)));
    expect(cspA, `CSP / Trusted Types violation(s) in context A:\n${describeCsp(cspA)}`).toEqual([]);
  } finally {
    await contextA.close();
  }
  return { stub, server };
}

/**
 * Walks "Reload latest" from an open conflict dialog: the destructive confirm is declined once (the dialog returns
 * with the draft untouched) and accepted once, after which the server story replaces it. The caller asserts the
 * adopted content, which only it knows.
 */
async function reloadLatestFromDialog(page: Page, dialog: Locator, lang: 'vi' | 'en', draftTitle: string): Promise<void> {
  const copy = EDITOR_COPY[lang], dialogCopy = DIALOG_COPY[lang], pageCopy = PAGE_CONFLICT_COPY[lang];
  await dialog.getByRole('button', { name: dialogCopy.reload, exact: true }).click();
  const confirm = confirmDialog(page, lang);
  await expect(confirm).toBeVisible();
  await expect(confirm).toContainText(pageCopy.reloadConfirmBody);
  // Destructive: the confirm action is the danger style, the safe action holds initial focus.
  await expect(confirm.getByRole('button', { name: pageCopy.reload, exact: true })).toHaveClass(/confirm-dialog__danger/);
  await waitForOverlayAnimationEnd(page);
  await expectA11yClean(page);

  // Declining is not a decision about the conflict: the dialog comes back with the draft untouched.
  await confirm.getByRole('button', { name: CONFIRM_CANCEL[lang], exact: true }).click();
  await expect(confirm).toHaveCount(0);
  await expect(dialog).toBeVisible();
  await expect(segment(page, copy.title)).toHaveValue(draftTitle);

  await dialog.getByRole('button', { name: dialogCopy.reload, exact: true }).click();
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: pageCopy.reload, exact: true }).click();
  await expect(confirm).toHaveCount(0);
  await expect(dialog).toHaveCount(0);
}

test.describe('story conflict (vi, stubbed, shared server)', () => {
  test('TwoContexts_StaleSave_OpensConflictDialog_FocusTrappedAndReturned_DraftKept_SaveMineUsesFreshEtag', async ({ page, browser }) => {
    await twoContextStaleSave(page, browser, 'vi');
  });

  test('ReloadPath_AsksTheDestructiveConfirm_DecliningReturnsToTheDialog_AcceptingReplacesTheDraft', async ({ page }) => {
    const server: StoryServer = { version: 1, versionId: STORED_VERSION_ID, content: STORY_CONTENT, forced412: 0 };
    const stub = await stubSharedStory(page, server);
    await page.goto(EDITOR_PATH);
    await expectEditor(page, 'vi');
    await typeTitle(page, TYPED_TITLE);
    // Another writer saved a different story while this draft was dirty.
    bump(server, SERVER_CONTENT);
    await saveButton(page, 'vi').click();

    const dialog = conflictDialog(page);
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(L.title);
    await expect(segment(page, L.title)).toHaveValue(TYPED_TITLE);

    await reloadLatestFromDialog(page, dialog, 'vi', TYPED_TITLE);

    // Accepting reloads the server story over the unsaved text.
    await expect(segment(page, L.title)).toHaveValue(SERVER_TITLE);
    expect(stub.puts).toHaveLength(1);
    await expectA11yClean(page);
  });

  test('Second412OnSaveAsNew_ShowsTheBusyVariant_AndNeverIssuesAnotherPut', async ({ page }) => {
    const server: StoryServer = { version: 1, versionId: STORED_VERSION_ID, content: STORY_CONTENT, forced412: 2 };
    const stub = await stubSharedStory(page, server);
    await page.goto(EDITOR_PATH);
    await expectEditor(page, 'vi');
    await typeTitle(page, TYPED_TITLE);
    await saveButton(page, 'vi').click();

    const dialog = conflictDialog(page);
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(C.body);
    await dialog.getByRole('button', { name: D.saveAsNew, exact: true }).click();

    // The single authorised re-attempt lost the race too: the dialog returns in its busy variant.
    await expect(dialog).toContainText(C.busyBody);
    expect(stub.puts).toHaveLength(2);
    await expect(segment(page, L.title)).toHaveValue(TYPED_TITLE);
    await page.waitForTimeout(650);
    expect(stub.puts, 'a second 412 must not be retried automatically').toHaveLength(2);
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);
  });

  test('HubInvalidationWhileDirty_RaisesTheChangedElsewhereNotice_AndKeepsTheDraft', async ({ page }) => {
    const server: StoryServer = { version: 1, versionId: STORED_VERSION_ID, content: STORY_CONTENT, forced412: 0 };
    const stub = await stubSharedStory(page, server);
    await page.goto(EDITOR_PATH);
    await expectEditor(page, 'vi');
    await expect.poll(stub.transport.connected).toBe(1);
    await typeTitle(page, TYPED_TITLE);

    const getsBefore = stub.detailGets.length;
    // Another tab/person saved a newer version; the hub invalidation makes the page refetch in the background.
    bump(server, SERVER_CONTENT);
    stub.transport.push({ episodeId: EPISODE_ID });
    await expect.poll(() => stub.detailGets.length).toBe(getsBefore + 1);

    const changed = notice(page);
    await expect(changed).toBeVisible();
    await expect(changed.getByRole('status')).toContainText(C.elsewhereBody);
    await expect(changed).toContainText(C.elsewhereBody);
    await expect(changed).toContainText(L.title);
    await expect(segment(page, L.title)).toHaveValue(TYPED_TITLE);
    await expect(conflictDialog(page)).toHaveCount(0);
    expect(stub.puts, 'a background read must never write').toHaveLength(0);
    await expectA11yClean(page);

    await changed.getByRole('button', { name: C.dismiss, exact: true }).click();
    await expect(changed).toHaveCount(0);
    await expect(segment(page, L.title)).toHaveValue(TYPED_TITLE);
  });
});

test.describe('story conflict in English (stubbed)', () => {
  test.use({ locale: 'en-US' });
  const EN = EDITOR_COPY.en;
  const END = DIALOG_COPY.en;
  const ENC = PAGE_CONFLICT_COPY.en;

  test('StaleSave_OpensTheEnglishDialog_CancelKeepsTheTypedText_AxeClean', async ({ page }) => {
    const server: StoryServer = { version: 1, versionId: STORED_VERSION_ID, content: STORY_CONTENT, forced412: 0 };
    const stub = await stubSharedStory(page, server, 'en');
    await page.goto(EDITOR_PATH);
    await expectEditor(page, 'en');
    await typeTitle(page, TYPED_TITLE, 'en');

    // Another writer saved a different title while this draft was dirty: the next save sends a stale `If-Match`.
    bump(server, SERVER_CONTENT);
    await saveButton(page, 'en').click();

    const dialog = conflictDialog(page, 'en');
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    await expect(dialog).toContainText(END.title);
    await expect(dialog).toContainText(ENC.diffHeading);
    await expect(dialog).toContainText(EN.title);
    expect(stub.puts, 'a stale save is one request').toHaveLength(1);
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);

    // Cancel is a decision about the dialog only: the typed text survives byte for byte and focus goes back to Save.
    await dialog.getByRole('button', { name: END.cancel, exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(segment(page, EN.title)).toHaveValue(TYPED_TITLE);
    await expect(saveButton(page, 'en')).toBeFocused();
    await page.waitForTimeout(650);
    expect(stub.puts, 'a 412 must never be retried automatically').toHaveLength(1);
  });

  test('TwoContexts_EnglishDialog_FocusTrappedAndReturned_SaveMineAsNew_ThenReloadLatestThroughItsConfirm', async ({ page, browser }) => {
    // Same two-context race as the vi describe, with the en bundles: the loser sees the English dialog and walks both
    // actions (save mine as new, then reload latest through its destructive confirm).
    const { stub, server } = await twoContextStaleSave(page, browser, 'en');

    // A later writer moved on again after the successful save-as-new, so the reload path gets a real conflict of its own.
    bump(server, RELOADED_CONTENT);
    await segment(page, EN.title).fill(REDRAFTED_TITLE);
    await saveButton(page, 'en').click();
    const dialog = conflictDialog(page, 'en');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(EN.title);

    await reloadLatestFromDialog(page, dialog, 'en', REDRAFTED_TITLE);

    await expect(segment(page, EN.title)).toHaveValue(RELOADED_TITLE);
    expect(stub.puts, 'three stale saves and one save-as-new, nothing else').toHaveLength(4);
    await expectA11yClean(page);
  });
});
