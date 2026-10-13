import type { Locator, Page, Request } from '@playwright/test';
import { expectA11yClean, waitForOverlayAnimationEnd } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { EPISODE_ID } from './fixtures/episodes-stubs';
import { ERRORS_COPY, fulfilJson, problem, type E2eLang } from './fixtures/series-stubs';
import {
  EDITED, EDITED_CONTENT, EDITOR_COPY, EDITOR_PATH, FRESH_STORY_ETAG, FRESH_VERSION_ID, SAVED_VERSION_ID,
  STORED_VERSION_ID, STORY_CONTENT, editorDetail, expectEditor, expectStoryPut, segment, storySnapshot,
  stubStoryEditor, type StoryEditorStubOptions, type StoryEditorStubs, type StoryWire,
} from './fixtures/story-editor-stubs';
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import { diffWords } from '../src/app/shared/util/text-diff';

// 10.5.4-T1 versions panel proof: backend-free (page.route) coverage of the panel toggle, the deep-linked word
// diff (including injection safety), same-version selection, restore-as-new-version (PUT + If-Match), the 412
// conflict dialog, the 409 needs-reopen state and the WCAG 2.1 AA scan — in vi and en. Real compose/mock-mpt
// acceptance stays with 11.5.1-T1.
//
// One test is marked `test.fail` on purpose: it pins a production defect this spec must not hide (see the test's
// annotation in the vi block). Every other test asserts only behaviour the app actually has.

interface VersionsCopy {
  readonly versions: {
    readonly empty: string; readonly selection: string; readonly version: string; readonly current: string;
    readonly approved: string; readonly selectTwo: string; readonly restore: string; readonly needsReopen: string;
    readonly conflictBody: string;
    readonly source: Readonly<Record<string, string>>;
    readonly diff: { readonly summary: string; readonly added: string; readonly removed: string; readonly label: string };
    readonly restoreConfirm: { readonly title: string; readonly body: string; readonly dirtyBody: string; readonly confirm: string };
  };
}

const COPY: Readonly<Record<E2eLang, VersionsCopy>> = {
  vi: episodesVi as unknown as VersionsCopy,
  en: episodesEn as unknown as VersionsCopy,
};

/** One seeded `story_versions` row: the list DTO plus the detail-only body/content the diff and restore read. */
interface Seed {
  readonly versionId: string;
  readonly version: number;
  readonly source: string;
  readonly approved: boolean;
  readonly isCurrent: boolean;
  readonly createdAt: string;
  readonly wordCount: number;
  readonly similarityScore: number | null;
  readonly body: string;
  readonly content: StoryWire;
}

/** The current version carries the editor's story; the older one is the restore source and hides a script tag. */
const CURRENT_BODY = 'alpha beta epsilon zeta delta';
const OLDER_BODY = 'alpha <script>alert(1)</script> beta gamma delta';

const CURRENT: Seed = {
  versionId: FRESH_VERSION_ID, version: 2, source: 'generated', approved: false, isCurrent: true,
  createdAt: '2026-10-04T09:30:00Z', wordCount: 1200, similarityScore: null, body: CURRENT_BODY, content: STORY_CONTENT,
};
const OLDER: Seed = {
  versionId: STORED_VERSION_ID, version: 1, source: 'revised', approved: true, isCurrent: false,
  createdAt: '2026-10-02T08:15:00Z', wordCount: 900, similarityScore: 0.42, body: OLDER_BODY, content: EDITED_CONTENT,
};
/** Server order is `version DESC`: the newest row first, exactly as the endpoint returns it. */
const SEEDS: readonly Seed[] = [CURRENT, OLDER];

/** The follow-up reads after a successful restore: version 3 becomes current, the older rows stay untouched. */
const RESTORED: Seed = {
  versionId: SAVED_VERSION_ID, version: 3, source: 'edited', approved: false, isCurrent: true,
  createdAt: '2026-10-05T13:00:00Z', wordCount: 950, similarityScore: 0.8, body: OLDER_BODY, content: EDITED_CONTENT,
};
const AFTER_RESTORE: readonly Seed[] = [RESTORED, { ...CURRENT, isCurrent: false }, OLDER];

const OTHER_VERSION_ID = '99999999-9999-4999-8999-999999999999';
/** An unsaved draft the owner typed before restoring: the conflict must never replace it with the server's newer story. */
const DIRTY_TITLE = 'Bản nháp chưa lưu của người viết';
/** The editor's own story: version 2, content identical to `CURRENT`, so the editor ETag is `FRESH_STORY_ETAG`. */
const EDITOR_DETAIL = editorDetail({ currentStory: storySnapshot(STORY_CONTENT, FRESH_VERSION_ID, 2) });
const NEWER_DETAIL = editorDetail({ currentStory: storySnapshot(EDITED_CONTENT, OTHER_VERSION_ID, 4) });

const summaryDto = (seed: Seed): Record<string, unknown> => ({
  versionId: seed.versionId, version: seed.version, source: seed.source, approved: seed.approved,
  isCurrent: seed.isCurrent, createdAt: seed.createdAt, wordCount: seed.wordCount,
  similarityScore: seed.similarityScore, etag: `"sv-${seed.versionId}"`,
});

const detailDto = (seed: Seed): Record<string, unknown> => ({
  ...summaryDto(seed), body: seed.body, content: seed.content, createdBy: 'e2e-owner',
  auditReport: null, factSheetId: null, keywords: [], qualityHints: [],
});

interface VersionsStubs {
  readonly requests: Request[];
  readonly listGets: Request[];
  readonly detailGets: Request[];
  setSeeds: (seeds: readonly Seed[]) => void;
}

const VERSIONS_PATH = `/api/v1/episodes/${EPISODE_ID}/story/versions`;

/** Serves `GET …/story/versions` and `GET …/story/versions/{versionId}` and records every version read. */
async function stubVersions(page: Page, seeds: readonly Seed[]): Promise<VersionsStubs> {
  const requests: Request[] = [];
  const listGets: Request[] = [];
  const detailGets: Request[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith(VERSIONS_PATH)) requests.push(request);
  });
  const current = [...seeds];
  await page.route((url) => url.pathname === VERSIONS_PATH, async (route) => {
    listGets.push(route.request());
    await fulfilJson(route, 200, current.map(summaryDto));
  });
  await page.route((url) => url.pathname.startsWith(`${VERSIONS_PATH}/`), async (route) => {
    detailGets.push(route.request());
    const versionId = new URL(route.request().url()).pathname.slice(VERSIONS_PATH.length + 1);
    const seed = current.find((item) => item.versionId === versionId);
    if (seed === undefined) {
      await fulfilJson(route, 404, problem(404, 'story_version_not_found'), { 'Content-Type': 'application/problem+json' });
      return;
    }
    await fulfilJson(route, 200, detailDto(seed));
  });
  return {
    requests, listGets, detailGets,
    setSeeds: (next) => { current.length = 0; current.push(...next); },
  };
}

interface Fixture {
  readonly editor: StoryEditorStubs;
  readonly versions: VersionsStubs;
}

interface FixtureOptions {
  readonly editor?: StoryEditorStubOptions;
  readonly seeds?: readonly Seed[];
  readonly suffix?: string;
}

async function openEditor(page: Page, lang: E2eLang, options: FixtureOptions = {}): Promise<Fixture> {
  const editor = await stubStoryEditor(page, lang, { detail: EDITOR_DETAIL, ...options.editor });
  const versions = await stubVersions(page, options.seeds ?? SEEDS);
  await page.goto(`${EDITOR_PATH}${options.suffix ?? ''}`);
  await expectEditor(page, lang);
  await expect.poll(editor.connected).toBe(1);
  return { editor, versions };
}

const deepLink = (left: Seed, right: Seed): string =>
  `?panel=versions&left=${left.versionId}&right=${right.versionId}`;

const rows = (page: Page): Locator => page.locator('.versions__row');
const rowFor = (page: Page, seed: Seed): Locator => page.locator(`.versions__row[data-version-id="${seed.versionId}"]`);
const restoreButton = (page: Page): Locator => page.locator('.versions__restore');
/** The editor field for a canonical segment address: CSS, so it still resolves while a modal hides the page. */
const segmentField = (page: Page, address: string): Locator => page.locator(`textarea[data-segment="${address}"]`);
const dialog = (page: Page): Locator => page.locator('mat-dialog-container');
const confirmButton = (page: Page): Locator => dialog(page).locator('.confirm-dialog__actions button').last();

const versionIdsInDomOrder = (page: Page): Promise<(string | null)[]> =>
  rows(page).evaluateAll((elements) => elements.map((element) => element.getAttribute('data-version-id')));

/** Fills a Transloco `{{name}}` template with the values the component would pass. */
function template(text: string, values: Readonly<Record<string, string | number>>): string {
  return Object.entries(values).reduce((result, [key, value]) => result.replace(`{{${key}}}`, String(value)), text);
}

/**
 * (1) A closed panel reads nothing; the header control adds `?panel=versions` and lists the versions in server order
 * with the current/approved badges and the word count.
 */
async function panelToggleFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const { versions } = await openEditor(page, lang);
  await expect(page.locator('.versions')).toHaveCount(0);
  await page.waitForTimeout(300);
  expect(versions.requests, 'the closed panel must not read version history').toHaveLength(0);

  const toggle = page.locator('.story-review-page__versions-toggle');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  expect(new URL(page.url()).searchParams.get('panel')).toBe('versions');

  await expect(page.locator('.versions')).toBeVisible();
  await expect.poll(() => versions.listGets.length).toBe(1);
  await expect(rows(page)).toHaveCount(2);
  expect(await versionIdsInDomOrder(page)).toEqual([CURRENT.versionId, OLDER.versionId]);

  const current = rowFor(page, CURRENT);
  await expect(current.locator('time')).toHaveAttribute('datetime', CURRENT.createdAt);
  await expect(current.locator('.versions__badge')).toHaveCount(1);
  await expect(current.locator('.versions__badge')).toHaveText(copy.current);
  await expect(current).toContainText(new Intl.NumberFormat(lang === 'en' ? 'en-US' : 'vi-VN').format(CURRENT.wordCount));

  const older = rowFor(page, OLDER);
  await expect(older.locator('time')).toHaveAttribute('datetime', OLDER.createdAt);
  await expect(older.locator('.versions__badge')).toHaveCount(1);
  await expect(older.locator('.versions__badge')).toHaveText(copy.approved);

  // Deliberately not asserted here: the store's default (previous vs current) selection never runs in the app —
  // see `toggleDefaultSelectionFlow`, which records that production defect.
}

/**
 * The contract for a panel opened from the header (no ids in the query): the store itself picks previous vs current
 * — `versions.store.ts:88` documents it and `versions.store.ts:102` runs `defaultSelection()` — but the host never
 * reaches that branch because `versions-panel-host.component.ts:45` always passes `{ left, right }`, both `null`
 * without query ids, which `open()` reads as an explicit "no selection". The panel then opens with no comparison.
 * The assertions below are the intended behaviour; the test that calls this flow is marked `test.fail` until the
 * wiring passes `undefined` (or the store treats an all-null selection as "no selection").
 */
async function toggleDefaultSelectionFlow(page: Page, lang: E2eLang): Promise<void> {
  const { versions } = await openEditor(page, lang);
  await page.locator('.story-review-page__versions-toggle').click();
  await expect(rows(page)).toHaveCount(2);
  await expect.poll(() => versions.detailGets.length).toBe(2);
  await expect.poll(() => new URL(page.url()).searchParams.get('left')).toBe(OLDER.versionId);
  await expect.poll(() => new URL(page.url()).searchParams.get('right')).toBe(CURRENT.versionId);
  await expect(page.locator('.versions__hint[role="status"]')).toHaveCount(0);
}

/** (1) An episode without versions shows the empty state, never an error, and reads no detail. */
async function emptyFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const { versions } = await openEditor(page, lang, { seeds: [], suffix: '?panel=versions' });
  await expect(page.locator('.versions')).toBeVisible();
  await expect.poll(() => versions.listGets.length).toBe(1);
  await expect(page.locator('.versions').getByRole('heading', { name: copy.empty })).toBeVisible();
  await expect(page.locator('.versions nts-error-state')).toHaveCount(0);
  await expect(page.locator('.versions__side-error')).toHaveCount(0);
  expect(versions.detailGets, 'an empty history must not read a version detail').toHaveLength(0);
  await expect(restoreButton(page)).toHaveCount(0);
}

/**
 * (2) The deep link reads exactly one list and two details, and the deferred word diff renders `+`/`−` hunks plus
 * the polite summary live region; a version body carrying `<script>` stays text and never executes.
 */
async function diffFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const fired: string[] = [];
  page.on('dialog', (alert) => { fired.push(alert.message()); void alert.dismiss(); });

  const { versions } = await openEditor(page, lang, { suffix: deepLink(OLDER, CURRENT) });
  await expect(rows(page)).toHaveCount(2);
  await page.waitForTimeout(300);
  expect(versions.listGets, 'the panel reads the list once').toHaveLength(1);
  expect(versions.detailGets, 'one detail per side, no more').toHaveLength(2);
  expect(versions.detailGets.map((request) => new URL(request.url()).pathname.split('/').at(-1)).sort())
    .toEqual([CURRENT.versionId, OLDER.versionId].sort());

  // The comparison is behind `@defer (on interaction)`: silent until the user asks for it.
  await expect(page.locator('.diff')).toHaveCount(0);
  await page.locator('.versions__compare').click();
  const region = page.locator('.diff');
  await expect(region).toBeVisible();
  await expect(region).toHaveAttribute('role', 'region');
  await expect(region).toHaveAttribute('aria-label', copy.diff.label);
  await expect(region).toHaveAttribute('tabindex', '0');

  const expected = diffWords(OLDER.body, CURRENT.body);
  const inserted = expected.segments.filter((part) => part.kind === 'insert');
  const removed = expected.segments.filter((part) => part.kind === 'delete');
  expect(inserted.length, 'the seeded bodies must differ').toBeGreaterThan(0);
  expect(removed.length).toBeGreaterThan(0);

  const inserts = region.locator('ins[data-kind="insert"]');
  const deletes = region.locator('del[data-kind="delete"]');
  await expect(inserts).toHaveCount(inserted.length);
  await expect(deletes).toHaveCount(removed.length);
  for (const [index, part] of inserted.entries()) {
    await expect(inserts.nth(index)).toContainText(part.text);
    await expect(inserts.nth(index).locator('span[aria-hidden="true"]')).toHaveText('+');
  }
  for (const [index, part] of removed.entries()) {
    await expect(deletes.nth(index)).toContainText(part.text);
    await expect(deletes.nth(index).locator('span[aria-hidden="true"]')).toHaveText('−');
  }

  const summary = page.locator('.versions__summary');
  await expect(summary).toHaveAttribute('role', 'status');
  await expect(summary).toHaveAttribute('aria-live', 'polite');
  await expect(summary).toHaveAttribute('aria-atomic', 'true');
  await expect(summary).toHaveText(template(copy.diff.summary, { added: expected.added, removed: expected.removed }));

  // A version body is untrusted history: it renders as visible text and never runs.
  await expect(region).toContainText('<script>alert(1)</script>');
  await expect(region.locator('script')).toHaveCount(0);
  expect(fired, 'a stored story body must never execute').toEqual([]);
}

/** (3) Picking one version on both sides drops the comparison, shows the two-version hint and blocks restore. */
async function sameVersionFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const { versions } = await openEditor(page, lang, { suffix: deepLink(OLDER, CURRENT) });
  await expect(rows(page)).toHaveCount(2);

  await page.locator('.versions__slots button').nth(1).click();
  await rowFor(page, OLDER).click();
  await expect.poll(() => versions.detailGets.length).toBe(3);

  await expect(page.locator('.versions__hint[role="status"]')).toHaveText(copy.selectTwo);
  await expect(page.locator('.diff')).toHaveCount(0);
  await expect(restoreButton(page)).toBeDisabled();
}

/** (4a) Restore is offered only for a non-current left side. */
async function currentVersionFlow(page: Page, lang: E2eLang): Promise<void> {
  await openEditor(page, lang, { suffix: deepLink(CURRENT, OLDER) });
  await expect(rows(page)).toHaveCount(2);
  await expect(rowFor(page, CURRENT)).toContainText(COPY[lang].versions.current);
  await expect(restoreButton(page)).toBeDisabled();
}

/** (4b) Confirm -> the raw story.v1 of the old version with the editor ETag -> a new current version. */
async function restoreFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const editorCopy = EDITOR_COPY[lang];
  const { editor, versions } = await openEditor(page, lang, { suffix: deepLink(OLDER, CURRENT) });
  await expect(restoreButton(page)).toBeEnabled();
  await restoreButton(page).click();

  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page).getByRole('heading', { name: copy.restoreConfirm.title })).toBeVisible();
  await expect(dialog(page)).toContainText(copy.restoreConfirm.body);
  await expect(confirmButton(page)).toHaveText(copy.restoreConfirm.confirm);

  versions.setSeeds(AFTER_RESTORE);
  await confirmButton(page).click();
  await expect(dialog(page)).toBeHidden();

  await expect.poll(() => editor.puts.length).toBe(1);
  expectStoryPut(editor.puts[0]!, FRESH_STORY_ETAG, EDITED_CONTENT);
  // The editor adopts the restored text; the new version appears as current.
  await expect(segment(page, editorCopy.title)).toHaveValue(EDITED.title);
  await expect(segment(page, editorCopy.hook)).toHaveValue(EDITED.hook);
  await expect(segment(page, editorCopy.paragraph(1))).toHaveValue(EDITED.paragraph);
  const restored = rowFor(page, RESTORED);
  await expect(restored).toBeVisible();
  await expect(restored.locator('.versions__badge')).toHaveText(copy.current);
  await expect(restored).toContainText(copy.source['edited']!);
  await expect.poll(() => versionIdsInDomOrder(page)).toEqual([RESTORED.versionId, CURRENT.versionId, OLDER.versionId]);

  // Every earlier row keeps its identity, its date and its badge.
  await expect(rowFor(page, OLDER).locator('time')).toHaveAttribute('datetime', OLDER.createdAt);
  await expect(rowFor(page, OLDER).locator('.versions__badge')).toHaveText(copy.approved);
  await expect(rowFor(page, CURRENT).locator('time')).toHaveAttribute('datetime', CURRENT.createdAt);
  await expect(rowFor(page, CURRENT).locator('.versions__badge')).toHaveCount(0);
}

/** (5) A 412 opens the conflict dialog and nothing is overwritten until the user chooses. */
async function conflictFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const editorCopy = EDITOR_COPY[lang];
  const errors = ERRORS_COPY[lang].conflict;
  const { editor } = await openEditor(page, lang, {
    suffix: deepLink(OLDER, CURRENT),
    editor: { failures: [{ status: 412, detailAfter: NEWER_DETAIL }] },
  });

  // The owner has unsaved work: a 412 must never trade it for the server's newer story before a choice is made.
  await segment(page, editorCopy.title).fill(DIRTY_TITLE);
  await expect(segment(page, editorCopy.title)).toHaveValue(DIRTY_TITLE);

  await restoreButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await confirmButton(page).click();

  const conflict = page.locator('mat-dialog-container .conflict-dialog');
  await expect(conflict).toBeVisible();
  await expect(conflict.getByRole('heading', { name: errors.title })).toBeVisible();
  await expect(conflict).toContainText(copy.conflictBody);
  await expect(conflict.locator('button[data-outcome]')).toHaveCount(3);
  await expect(conflict.locator('button[data-outcome="cancel"]')).toHaveText(errors.cancel);
  await expect(conflict.locator('button[data-outcome="saveAsNew"]')).toHaveText(errors.saveAsNew);
  await expect(conflict.locator('button[data-outcome="reload"]')).toHaveText(errors.reload);

  await page.waitForTimeout(300);
  expect(editor.puts, 'the failed restore is the only write').toHaveLength(1);
  expect(editor.puts[0]!.headers()['if-match'], 'the stale editor ETag is what the server refused').toBe(FRESH_STORY_ETAG);
  // The open modal makes the page behind it aria-hidden, so role lookups cannot see the draft: read the field.
  await expect(segmentField(page, 'title')).toHaveValue(DIRTY_TITLE);

  await conflict.locator('button[data-outcome="cancel"]').click();
  await expect(conflict).toBeHidden();
  await page.waitForTimeout(300);
  expect(editor.puts, 'a cancelled conflict writes nothing').toHaveLength(1);
  await expect(segment(page, editorCopy.title)).toHaveValue(DIRTY_TITLE);
}

/** (6) A 409 `episode_invalid_state` renders the needs-reopen message and no dialog. */
async function needsReopenFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].versions;
  const editorCopy = EDITOR_COPY[lang];
  const { editor } = await openEditor(page, lang, {
    suffix: deepLink(OLDER, CURRENT),
    editor: { failures: [{ status: 409, detailAfter: EDITOR_DETAIL }] },
  });

  await restoreButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await confirmButton(page).click();

  const alert = page.locator('.versions__alert');
  await expect(alert).toHaveAttribute('role', 'alert');
  await expect(alert).toHaveText(copy.needsReopen);
  await expect(dialog(page)).toBeHidden();
  await page.waitForTimeout(300);
  expect(editor.puts).toHaveLength(1);
  await expect(segment(page, editorCopy.title)).toHaveValue(STORY_CONTENT.title);
}

/** Keyboard path: pick left, pick right, read, restore, confirm — with focus back on the trigger. */
async function keyboardFlow(page: Page, lang: E2eLang): Promise<void> {
  const editorCopy = EDITOR_COPY[lang];
  const { editor, versions } = await openEditor(page, lang, { suffix: deepLink(OLDER, CURRENT) });
  await expect(rows(page)).toHaveCount(2);

  const activate = async (target: Locator): Promise<void> => {
    await target.focus();
    await page.keyboard.press('Enter');
  };

  await activate(page.locator('.versions__slots button').first());
  await activate(rowFor(page, OLDER));
  await activate(page.locator('.versions__slots button').nth(1));
  await activate(rowFor(page, CURRENT));
  await expect.poll(() => versions.detailGets.length).toBe(4);

  // A side reload nulls the other side's placeholder, so wait for the panel to settle before driving the keyboard.
  await expect(page.locator('.versions nts-loading-state')).toHaveCount(0);
  await expect(page.locator('.versions__compare')).toBeVisible();
  await page.waitForTimeout(150);

  await activate(page.locator('.versions__compare'));
  await expect(page.locator('.diff')).toBeVisible();

  // The confirm dialog takes focus, and returns it to its trigger when it closes without writing.
  await activate(restoreButton(page));
  await expect(dialog(page)).toBeVisible();
  // Material re-traps focus when the enter animation ends: without this wait a queued trap would steal it back.
  await waitForOverlayAnimationEnd(page);
  await expect(dialog(page).locator(':focus')).toHaveCount(1);
  await activate(dialog(page).locator('.confirm-dialog__actions button').first());
  await expect(dialog(page)).toBeHidden();
  await expect(restoreButton(page)).toBeFocused();
  expect(editor.puts).toHaveLength(0);

  versions.setSeeds(AFTER_RESTORE);
  await activate(restoreButton(page));
  await expect(dialog(page)).toBeVisible();
  await waitForOverlayAnimationEnd(page);
  await expect(dialog(page).locator(':focus')).toHaveCount(1);
  await activate(confirmButton(page));
  await expect(dialog(page)).toBeHidden();
  await expect.poll(() => editor.puts.length).toBe(1);
  expectStoryPut(editor.puts[0]!, FRESH_STORY_ETAG, EDITED_CONTENT);
  await expect(rowFor(page, RESTORED)).toBeVisible();
  await expect(segment(page, editorCopy.title)).toHaveValue(EDITED.title);
}

/** (7) WCAG 2.1 AA clean on the editor route with the panel open, the diff rendered and the dialog open. */
async function axeFlow(page: Page, lang: E2eLang): Promise<void> {
  await openEditor(page, lang, { suffix: deepLink(OLDER, CURRENT) });
  await expect(rows(page)).toHaveCount(2);
  await page.locator('.versions__compare').click();
  await expect(page.locator('.diff')).toBeVisible();
  await expectA11yClean(page);

  await restoreButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await waitForOverlayAnimationEnd(page);
  await expectA11yClean(page);
}

test.describe('versions panel (vi, stubbed)', () => {
  test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

  test('ClosedPanel_NoVersionReads_ToggleOpensListInServerOrderWithBadges', async ({ page }) => {
    await panelToggleFlow(page, 'vi');
  });

  test('EmptyHistory_ShowsEmptyStateNotAnError', async ({ page }) => {
    await emptyFlow(page, 'vi');
  });

  test('PanelToggleWithoutIds_DefaultsToPreviousVsCurrentComparison', async ({ page }) => {
    await toggleDefaultSelectionFlow(page, 'vi');
  });

  test('DeepLink_OneListTwoDetails_RendersWordDiffHunksAndPoliteSummary_UnsafeBodyStaysText', async ({ page }) => {
    await diffFlow(page, 'vi');
  });

  test('SameVersionBothSides_DropsDiffShowsSelectTwoAndBlocksRestore', async ({ page }) => {
    await sameVersionFlow(page, 'vi');
  });

  test('CurrentVersionOnTheLeft_CannotBeRestored', async ({ page }) => {
    await currentVersionFlow(page, 'vi');
  });

  test('RestoreOlder_ConfirmPutsRawStoryWithEditorIfMatch_NewCurrentVersionAndEditorText', async ({ page }) => {
    await restoreFlow(page, 'vi');
  });

  test('Restore412_OpensConflictDialogAndOverwritesNothingUntilAChoice', async ({ page }) => {
    await conflictFlow(page, 'vi');
  });

  test('Restore409EpisodeInvalidState_ShowsNeedsReopenMessage', async ({ page }) => {
    await needsReopenFlow(page, 'vi');
  });

  test('Keyboard_PickLeftPickRightReadRestoreConfirm_FocusReturnsToTheTrigger', async ({ page }) => {
    await keyboardFlow(page, 'vi');
  });

  test('Axe_Wcag21aaCleanWithPanelDiffAndDialogOpen', async ({ page }) => {
    await axeFlow(page, 'vi');
  });
});

test.describe('versions panel (en, stubbed)', () => {
  test.use({ locale: 'en-US', timezoneId: 'UTC' });

  test('ClosedPanel_NoVersionReads_ToggleOpensListInServerOrderWithBadges', async ({ page }) => {
    await panelToggleFlow(page, 'en');
  });

  test('EmptyHistory_ShowsEmptyStateNotAnError', async ({ page }) => {
    await emptyFlow(page, 'en');
  });

  test('DeepLink_OneListTwoDetails_RendersWordDiffHunksAndPoliteSummary_UnsafeBodyStaysText', async ({ page }) => {
    await diffFlow(page, 'en');
  });

  test('SameVersionBothSides_DropsDiffShowsSelectTwoAndBlocksRestore', async ({ page }) => {
    await sameVersionFlow(page, 'en');
  });

  test('CurrentVersionOnTheLeft_CannotBeRestored', async ({ page }) => {
    await currentVersionFlow(page, 'en');
  });

  test('RestoreOlder_ConfirmPutsRawStoryWithEditorIfMatch_NewCurrentVersionAndEditorText', async ({ page }) => {
    await restoreFlow(page, 'en');
  });

  test('Restore412_OpensConflictDialogAndOverwritesNothingUntilAChoice', async ({ page }) => {
    await conflictFlow(page, 'en');
  });

  test('Restore409EpisodeInvalidState_ShowsNeedsReopenMessage', async ({ page }) => {
    await needsReopenFlow(page, 'en');
  });

  test('Keyboard_PickLeftPickRightReadRestoreConfirm_FocusReturnsToTheTrigger', async ({ page }) => {
    await keyboardFlow(page, 'en');
  });

  test('Axe_Wcag21aaCleanWithPanelDiffAndDialogOpen', async ({ page }) => {
    await axeFlow(page, 'en');
  });
});
