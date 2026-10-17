import type { Locator, Page, Request } from '@playwright/test';
import type { CurrentStoryDto } from '../../src/app/shared/api/models/current-story-dto';
import type { EpisodeDetailDto } from '../../src/app/shared/api/models/episode-detail-dto';
import type { EpisodeListItemDto } from '../../src/app/shared/api/models/episode-list-item-dto';
import type { StoryVersionDto } from '../../src/app/shared/api/models/story-version-dto';
import coreEn from '../../src/assets/i18n/core/en.json' with { type: 'json' };
import coreVi from '../../src/assets/i18n/core/vi.json' with { type: 'json' };
import episodesEn from '../../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsEn from '../../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../../src/assets/i18n/errors/vi.json' with { type: 'json' };
import qualityEn from '../../src/assets/i18n/quality/en.json' with { type: 'json' };
import qualityVi from '../../src/assets/i18n/quality/vi.json' with { type: 'json' };
import storyEn from '../../src/assets/i18n/story/en.json' with { type: 'json' };
import storyVi from '../../src/assets/i18n/story/vi.json' with { type: 'json' };
import type { SourceListItemDto } from '../../src/app/shared/api/models/source-list-item-dto';
import { expectA11yClean, waitForOverlayAnimationEnd } from './axe';
import { expect } from './csp.fixture';
import { episodeDetail, EPISODE_ID, SERIES_ID, stubEpisodes, watchPrivacy } from './episodes-stubs';
import { researchSource, stubResearch, type ResearchServer } from './research-stubs';
import { fulfilJson, problem, type E2eLang } from './series-stubs';

export const EDITOR_PATH = `/episodes/${EPISODE_ID}`;
export const STORED_VERSION_ID = '55555555-5555-4555-8555-555555555555';
export const FRESH_VERSION_ID = '66666666-6666-4666-8666-666666666666';
export const SAVED_VERSION_ID = '77777777-7777-4777-8777-777777777777';
export const STORED_STORY_ETAG = `"sv-${STORED_VERSION_ID}"`;
export const FRESH_STORY_ETAG = `"sv-${FRESH_VERSION_ID}"`;

export interface StoryWireSegment {
  readonly text: string;
  readonly keywords: readonly string[];
  readonly fact_refs: readonly unknown[];
}
export interface StoryWire {
  readonly title: string;
  readonly hook: StoryWireSegment;
  readonly paragraphs: readonly (StoryWireSegment & { readonly beat: string })[];
  readonly ending: StoryWireSegment;
  readonly description: string;
  readonly tags: readonly string[];
  readonly content_warnings: readonly string[];
  readonly structure: unknown;
}

export const STORY_CONTENT: StoryWire = {
  title: 'Ngọn đèn trong căn nhà vắng',
  hook: { text: 'Ánh đèn chỉ sáng khi căn nhà không còn ai.', keywords: ['ngọn đèn'], fact_refs: [{ source_id: 'opaque-hook-source' }] },
  paragraphs: [
    { beat: 'setup', text: 'Lan bước qua cánh cổng và nghe tiếng chuông cũ.', keywords: ['cánh cổng'], fact_refs: [{ source_id: 'opaque-paragraph-source', span: [0, 2] }] },
    { beat: 'escalation', text: 'Một bóng người đi qua cửa sổ rồi biến mất.', keywords: ['cửa sổ', 'bóng người'], fact_refs: [] },
    { beat: 'climax', text: 'Lan tắt đèn nhưng ngọn đèn ngoài sân vẫn sáng.', keywords: ['ngoài sân'], fact_refs: [] },
    { beat: 'payoff', text: 'Cô quay lại và thấy chiếc chìa khoá trên bàn.', keywords: ['chìa khoá'], fact_refs: [] },
  ],
  ending: { text: 'Sau lưng cô, căn nhà lại sáng đèn.', keywords: ['căn nhà'], fact_refs: [] },
  description: 'Một chuyện bí ẩn về ngọn đèn trong căn nhà vắng.',
  tags: ['mystery'], content_warnings: ['suspense'], structure: { schema: 'story.v1', opaque: { retained: ['unchanged', 1] } },
};
export const EDITED = {
  title: 'Bản riêng về ngọn đèn im lặng',
  hook: 'Lời mở đầu mới chỉ có trong bản riêng của người viết.',
  paragraph: 'Đoạn riêng mới kể về bước chân dừng dưới cánh cổng.',
  keyword: 'cánh cổng riêng',
} as const;
export const EDITED_CONTENT: StoryWire = {
  ...STORY_CONTENT, title: EDITED.title,
  hook: { ...STORY_CONTENT.hook, text: EDITED.hook },
  paragraphs: STORY_CONTENT.paragraphs.map((paragraph, index) => index === 0 ? { ...paragraph, text: EDITED.paragraph, keywords: [EDITED.keyword] } : paragraph),
};

export const EDITOR_COPY = {
  vi: { title: 'Tiêu đề', hook: 'Mở đầu', paragraph: (n: number) => `Đoạn ${n}`, ending: 'Kết', description: 'Mô tả', scenes: 'Cảnh', scene: (n: number) => `Cảnh ${n}`, add: 'Thêm cảnh', remove: 'Xoá cảnh', choose: 'Chọn đoạn', save: 'Lưu phiên bản', saved: 'Đã lưu', dirty: 'Có thay đổi chưa lưu', limit: 'Mỗi đoạn cần 1–2 cảnh', reopen: 'Mở lại để sửa', approve: 'Duyệt kịch bản', invalid: errorsVi.episode_invalid_state },
  en: { title: 'Title', hook: 'Hook', paragraph: (n: number) => `Paragraph ${n}`, ending: 'Ending', description: 'Description', scenes: 'Scenes', scene: (n: number) => `Scene ${n}`, add: 'Add scene', remove: 'Remove scene', choose: 'Choose segment', save: 'Save version', saved: 'Saved', dirty: 'Unsaved changes', limit: 'Each paragraph needs 1–2 scenes', reopen: 'Reopen story', approve: 'Approve story', invalid: errorsEn.episode_invalid_state },
} as const;

export function storySnapshot(content: StoryWire = STORY_CONTENT, versionId = STORED_VERSION_ID, version = 1): CurrentStoryDto {
  return {
    versionId, version, etag: `"sv-${versionId}"`, source: 'manual', wordCount: 123,
    content, body: [content.hook.text, ...content.paragraphs.map((paragraph) => paragraph.text), content.ending.text].join('\n'),
    keywords: [...content.hook.keywords, ...content.paragraphs.flatMap((paragraph) => paragraph.keywords), ...content.ending.keywords],
    approvedStoryVersionId: null, auditReport: null, attestation: null, factSheetId: null, qualityHints: [], review: null, similarityScore: null, status: 'story_review',
  };
}

export function editorDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return episodeDetail('done', {
    title: STORY_CONTENT.title, status: 'story_review', etag: '"episode-review"',
    nextActions: ['edit_story', 'approve_story'], currentStory: storySnapshot(), generation: null,
    ...overrides,
  });
}

interface PutFailure { readonly status: 409 | 412; readonly detailAfter: EpisodeDetailDto }
export interface StoryEditorStubOptions {
  readonly detail?: EpisodeDetailDto;
  readonly failures?: readonly PutFailure[];
}
export interface StoryEditorStubs {
  readonly puts: Request[];
  readonly detailGets: Request[];
  readonly order: readonly ('get' | 'put')[];
  readonly connected: () => number;
}

/** Reuses the generation fixture's session + SignalR handshake; only story detail/write/list are overridden. */
export async function stubStoryEditor(page: Page, lang: E2eLang, options: StoryEditorStubOptions = {}): Promise<StoryEditorStubs> {
  const transport = await stubEpisodes(page, { lang });
  const puts: Request[] = [];
  const detailGets: Request[] = [];
  const order: ('get' | 'put')[] = [];
  let detail = options.detail ?? editorDetail();
  await page.route(`**/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    expect(route.request().method()).toBe('GET');
    detailGets.push(route.request()); order.push('get');
    await fulfilJson(route, 200, detail, { ETag: detail.etag });
  });
  await page.route(`**/api/v1/episodes/${EPISODE_ID}/story`, async (route) => {
    const request = route.request();
    expect(request.method()).toBe('PUT');
    puts.push(request); order.push('put');
    const failure = options.failures?.[puts.length - 1];
    if (failure) {
      detail = failure.detailAfter;
      const code = failure.status === 412 ? 'story_version_conflict' : 'episode_invalid_state';
      await fulfilJson(route, failure.status, problem(failure.status, code, { detail: 'PRIVATE SERVER DETAIL MUST NOT BE RENDERED' }), { 'Content-Type': 'application/problem+json' });
      return;
    }
    // The fake server enforces optimistic concurrency too; a broken conflict retry cannot accidentally pass.
    if (request.headers()['if-match'] !== detail.currentStory?.etag) {
      await fulfilJson(route, 412, problem(412, 'story_version_conflict'), { 'Content-Type': 'application/problem+json' });
      return;
    }
    const content = request.postDataJSON() as StoryWire;
    const version = Number(detail.currentStory?.version ?? 0) + 1;
    const snapshot = storySnapshot(content, SAVED_VERSION_ID, version);
    const response: StoryVersionDto = {
      versionId: snapshot.versionId, version: snapshot.version, etag: snapshot.etag, source: snapshot.source,
      wordCount: snapshot.wordCount, qualityHints: snapshot.qualityHints, keywords: snapshot.keywords,
      title: content.title, content, createdAt: '2026-10-05T13:00:00Z', factSheetId: snapshot.factSheetId,
      parentVersionId: detail.currentStory?.versionId ?? null,
    };
    detail = { ...detail, title: content.title, etag: '"episode-saved"', currentStory: snapshot };
    await fulfilJson(route, 200, response, { ETag: snapshot.etag });
  });
  await page.route(/\/api\/v1\/episodes(?:\?|$)/, async (route) => {
    const status = new URL(route.request().url()).searchParams.get('status');
    const items: EpisodeListItemDto[] = status === null || status === detail.status ? [{
      id: EPISODE_ID, seriesId: SERIES_ID, title: detail.title, status: detail.status, lengthTier: 'standard',
      createdAt: '2026-10-01T12:00:00Z', updatedAt: '2026-10-03T12:00:00Z', etag: detail.etag,
    }] : [];
    await fulfilJson(route, 200, { items, nextCursor: null });
  });
  return { puts, detailGets, order, connected: transport.connected };
}

export const segment = (page: Page, label: string): Locator => page.getByRole('textbox', { name: label, exact: true });
export const segmentGroup = (page: Page, label: string): Locator => page.getByRole('group', { name: label, exact: true });
export const saveButton = (page: Page, lang: E2eLang): Locator => page.getByRole('button', { name: EDITOR_COPY[lang].save, exact: true });

export async function expectEditor(page: Page, lang: E2eLang): Promise<void> {
  await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}(?:\\?.*)?$`));
  await expect(page.locator('html')).toHaveAttribute('lang', lang);
  await expect(segment(page, EDITOR_COPY[lang].title)).toHaveValue(STORY_CONTENT.title);
  await expect(segment(page, EDITOR_COPY[lang].hook)).toHaveValue(STORY_CONTENT.hook.text);
}
export async function openEditor(page: Page, lang: E2eLang, options: StoryEditorStubOptions = {}, suffix = ''): Promise<StoryEditorStubs> {
  const api = await stubStoryEditor(page, lang, options);
  await page.goto(`${EDITOR_PATH}${suffix}`);
  await expectEditor(page, lang);
  await expect.poll(api.connected).toBe(1);
  return api;
}

export async function editStory(page: Page, lang: E2eLang): Promise<void> {
  const L = EDITOR_COPY[lang];
  await segment(page, L.title).fill(EDITED.title);
  await segment(page, L.hook).fill(EDITED.hook);
  await segment(page, L.paragraph(1)).fill(EDITED.paragraph);
  await segmentGroup(page, L.paragraph(1)).getByRole('list', { name: L.scenes, exact: true })
    .getByRole('textbox', { name: L.scene(1), exact: true }).fill(EDITED.keyword);
}

export async function expectSaved(page: Page, lang: E2eLang): Promise<void> {
  await expect(page.getByRole('status').filter({ hasText: EDITOR_COPY[lang].saved })).toContainText(EDITOR_COPY[lang].saved);
  await expect(saveButton(page, lang)).toBeDisabled();
}

export function expectStoryPut(request: Request, etag: string, content: StoryWire = EDITED_CONTENT): void {
  expect(request.method()).toBe('PUT');
  expect(new URL(request.url()).pathname).toBe(`/api/v1/episodes/${EPISODE_ID}/story`);
  expect(request.headers()['if-match']).toBe(etag);
  expect(request.headers()['idempotency-key']).toBeUndefined();
  const body = request.postDataJSON() as StoryWire;
  expect(body).toEqual(content);
  expect(Object.keys(body).sort()).toEqual(['title', 'hook', 'paragraphs', 'ending', 'description', 'tags', 'content_warnings', 'structure'].sort());
  for (const part of [body.hook, body.ending]) expect(Object.keys(part).sort()).toEqual(['text', 'keywords', 'fact_refs'].sort());
  for (const paragraph of body.paragraphs) expect(Object.keys(paragraph).sort()).toEqual(['beat', 'text', 'keywords', 'fact_refs'].sort());
}

export function watchStoryPrivacy(page: Page): () => Promise<void> {
  const checks = Object.values(EDITED).map((text) => watchPrivacy(page, text));
  return async () => {
    for (const check of checks) await check();
    const values = [STORY_CONTENT.title, STORY_CONTENT.hook.text, ...STORY_CONTENT.paragraphs.map((p) => p.text), STORY_CONTENT.ending.text, STORY_CONTENT.description,
      ...STORY_CONTENT.hook.keywords, ...STORY_CONTENT.paragraphs.flatMap((p) => p.keywords), ...STORY_CONTENT.ending.keywords, ...Object.values(EDITED)];
    const stored = await page.evaluate((texts) => [localStorage, sessionStorage].some((storage) => Object.keys(storage).some((key) =>
      texts.some((text) => key.includes(text) || (storage.getItem(key) ?? '').includes(text)),
    )), values);
    expect(stored, 'story content must not persist in localStorage or sessionStorage').toBe(false);
  };
}

export async function saveReloadFlow(page: Page, lang: E2eLang): Promise<void> {
  const privacy = watchStoryPrivacy(page);
  const api = await openEditor(page, lang);
  const L = EDITOR_COPY[lang];
  await expect(saveButton(page, lang)).toBeDisabled();
  await expect(segment(page, L.description)).toHaveValue(STORY_CONTENT.description);
  for (let index = 0; index < STORY_CONTENT.paragraphs.length; index += 1) {
    await expect(segment(page, L.paragraph(index + 1))).toHaveValue(STORY_CONTENT.paragraphs[index]!.text);
  }
  await expect(page.getByTestId('scene-strip').getByRole('textbox')).toHaveCount(0);
  await expectA11yClean(page);
  await editStory(page, lang);
  await expect(page.getByRole('status').filter({ hasText: L.dirty })).toContainText(L.dirty);
  await expect(saveButton(page, lang)).toBeEnabled();
  await privacy();
  await saveButton(page, lang).click();
  await expectSaved(page, lang);
  await page.waitForTimeout(650);
  expect(api.puts).toHaveLength(1);
  expectStoryPut(api.puts[0]!, STORED_STORY_ETAG);
  await privacy();
  await expectA11yClean(page);
  await page.reload();
  await expect(segment(page, L.title)).toHaveValue(EDITED.title);
  await expect(segment(page, L.hook)).toHaveValue(EDITED.hook);
  await expect(segment(page, L.paragraph(1))).toHaveValue(EDITED.paragraph);
  await expect(segmentGroup(page, L.paragraph(1)).getByRole('textbox', { name: L.scene(1), exact: true })).toHaveValue(EDITED.keyword);
  expect(api.puts).toHaveLength(1);
  await expectA11yClean(page);
  await privacy();
}

export async function mobileSegmentsFlow(page: Page, lang: E2eLang): Promise<void> {
  await page.setViewportSize({ width: 360, height: 800 });
  await openEditor(page, lang);
  const L = EDITOR_COPY[lang];
  const selector = page.getByRole('combobox', { name: L.choose, exact: true });
  const addresses = [
    { address: 'title', label: L.title }, { address: 'hook', label: L.hook },
    ...STORY_CONTENT.paragraphs.map((_paragraph, index) => ({ address: `p:${index}`, label: L.paragraph(index + 1) })),
    { address: 'ending', label: L.ending }, { address: 'meta', label: L.description },
  ];
  for (const { address, label } of addresses) {
    await selector.selectOption(address);
    const textarea = segment(page, label);
    await expect(textarea).toBeVisible();
    await expect(textarea).toBeFocused();
    await expect(textarea).toBeInViewport();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `360 px overflow at ${address}`).toBe(true);
  }
  await expectA11yClean(page);
}

// ---------------------------------------------------------------------------------------------------------------
// 10.5.9-T1 structure flows (paragraph reorder, delete + undo, dirty guard, logout, beforeunload, a11y, 360 px).
// `episodes-structure.spec.ts` (vi) and `episodes-structure-en.spec.ts` (en) run the same scenarios through these
// helpers, so both locales share one implementation of each acceptance bullet. Only the one long real wait (the 10 s
// undo window) is kept to the vi spec.
// ---------------------------------------------------------------------------------------------------------------

/**
 * Copy for the structure controls, read from the shipped `episodes` bundle plus the shared `core` dialog copy so a
 * label change fails these specs instead of drifting. Position placeholders are formatted here with the same values
 * the components interpolate.
 */
export const EDITOR_STRUCTURE_COPY = {
  vi: {
    moveHandle: episodesVi.editor.moveHandle,
    moveUpAt: (position: number): string => episodesVi.editor.moveUpPosition.replace('{{position}}', String(position)),
    moveDownAt: (position: number): string => episodesVi.editor.moveDownPosition.replace('{{position}}', String(position)),
    reorderAt: (from: number, to: number): string => episodesVi.editor.reorderAnnounce.replace('{{n}}', String(from)).replace('{{m}}', String(to)),
    deleteAt: (position: number): string => episodesVi.editor.deletePosition.replace('{{position}}', String(position)),
    deleted: episodesVi.editor.deleted,
    undo: episodesVi.editor.undo,
    structureWarning: episodesVi.editor.structureWarning,
    dirtyTitle: episodesVi.dirty.title,
    dirtyBody: episodesVi.dirty.body,
    dirtyLeave: episodesVi.dirty.leave,
    dirtyStay: episodesVi.dirty.stay,
    back: coreVi.core.ui.pageHeader.back,
    menuTrigger: coreVi.shell.menu.triggerLabel,
    menuLogOut: coreVi.shell.menu.logOut,
  },
  en: {
    moveHandle: episodesEn.editor.moveHandle,
    moveUpAt: (position: number): string => episodesEn.editor.moveUpPosition.replace('{{position}}', String(position)),
    moveDownAt: (position: number): string => episodesEn.editor.moveDownPosition.replace('{{position}}', String(position)),
    reorderAt: (from: number, to: number): string => episodesEn.editor.reorderAnnounce.replace('{{n}}', String(from)).replace('{{m}}', String(to)),
    deleteAt: (position: number): string => episodesEn.editor.deletePosition.replace('{{position}}', String(position)),
    deleted: episodesEn.editor.deleted,
    undo: episodesEn.editor.undo,
    structureWarning: episodesEn.editor.structureWarning,
    dirtyTitle: episodesEn.dirty.title,
    dirtyBody: episodesEn.dirty.body,
    dirtyLeave: episodesEn.dirty.leave,
    dirtyStay: episodesEn.dirty.stay,
    back: coreEn.core.ui.pageHeader.back,
    menuTrigger: coreEn.shell.menu.triggerLabel,
    menuLogOut: coreEn.shell.menu.logOut,
  },
} as const;

const paragraphTextareas = (page: Page): Locator => page.locator('textarea[data-segment^="p:"]');
const paragraphTextarea = (page: Page, index: number): Locator => page.locator(`textarea[data-segment="p:${index}"]`);
const paragraphValues = (page: Page): Promise<string[]> => paragraphTextareas(page).evaluateAll((nodes) => nodes.map((node) => (node as HTMLTextAreaElement).value));
// The LiveAnnouncer element carries no role (aria-live only), so its CDK class is the stable handle.
const liveAnnouncer = (page: Page): Locator => page.locator('.cdk-live-announcer-element');
const noHorizontalOverflow = (page: Page): Promise<boolean> => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
const paragraphDelete = (page: Page, label: string, name: string): Locator => segmentGroup(page, label).getByRole('button', { name, exact: true });
/** A control the browser does not lay out has no box at all, so a missing control fails loudly instead of as `NaN`. */
const paragraphBox = async (locator: Locator, what: string): Promise<{ readonly x: number; readonly y: number; readonly width: number; readonly height: number }> => {
  const box = await locator.boundingBox();
  expect(box, `${what} must be laid out`).not.toBeNull();
  return box!;
};
const snackbar = (page: Page): Locator => page.locator('.mat-mdc-snack-bar-container').last();
const snackbarAction = (page: Page, name: string): Locator => snackbar(page).getByRole('button', { name, exact: true }).first();
/**
 * Waits for an open snackbar to settle before an axe scan. Material moves the content out of a transient
 * `aria-hidden` wrapper into the live region 150 ms after the enter animation starts, and that enter animation fades
 * the container in over 150 ms; a scan inside either window reports `aria-hidden-focus` or contrast failures against
 * interpolated opacity, so callers wait for the settled structure and the finished animation.
 */
async function snackbarSettled(page: Page): Promise<void> {
  await expect(page.locator('mat-snack-bar-container [aria-hidden]')).toHaveCount(0);
  await page.waitForFunction(() => {
    const container = document.querySelector('mat-snack-bar-container');
    return container === null || container.getAnimations().every((animation) => animation.playState !== 'running');
  });
}
const motionToken = (page: Page, token: string): Promise<string> => page.evaluate((name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim(), token);
const cardButtonTransition = (page: Page): Promise<string | null> => page.evaluate(() => {
  const button = document.querySelector<HTMLElement>('nts-paragraph-card [data-action="move-up"]');
  return button === null ? null : getComputedStyle(button).transitionDuration;
});
/**
 * The longest `animation-duration`/`transition-duration` (seconds) computed for the snackbar container and its inner
 * `.mdc-snackbar__surface`. Material decides `mat-snack-bar-container-animations-enabled` in JS
 * (`MatSnackBarContainer._animationsDisabled`), never from `prefers-reduced-motion`, so the disabled motion is proven
 * on the computed styles the global rule in `web/src/styles.scss` applies (`0.01ms`/`0ms` with `!important`).
 */
const snackbarMotionSeconds = (container: Locator): Promise<{ readonly container: number; readonly surface: number }> =>
  container.evaluate((node) => {
    const longest = (target: Element | null): number => {
      if (target === null) return 0;
      const style = getComputedStyle(target);
      return [style.animationDuration, style.transitionDuration]
        .flatMap((value) => value.split(','))
        .map((value) => Number.parseFloat(value) || 0)
        .reduce((maximum, seconds) => Math.max(maximum, seconds), 0);
    };
    return { container: longest(node), surface: longest(node.querySelector('.mdc-snackbar__surface')) };
  });

/** Bullet 1: keyboard-only reorder (Enter + Space), the live announcement, focus return and the aria-disabled ends. */
export async function keyboardReorderFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const api = await openEditor(page, lang);
  const before = STORY_CONTENT.paragraphs.map((paragraph) => paragraph.text);
  expect(await paragraphValues(page)).toEqual(before);
  await expectA11yClean(page);

  // Paragraph 2's "Move up", activated with Enter: [P2, P1, P3, P4].
  const up2 = page.getByRole('button', { name: C.moveUpAt(2), exact: true });
  await up2.focus();
  await expect(up2).toBeFocused();
  await page.keyboard.press('Enter');
  const moved = [before[1]!, before[0]!, before[2]!, before[3]!];
  await expect.poll(() => paragraphValues(page)).toEqual(moved);
  await expect(liveAnnouncer(page)).toHaveText(C.reorderAt(2, 1));
  await expect(paragraphTextarea(page, 0)).toBeFocused();
  await expectA11yClean(page);

  // The moved paragraph is now position 1; Space moves it back down: [P1, P2, P3, P4].
  const down1 = page.getByRole('button', { name: C.moveDownAt(1), exact: true });
  await down1.focus();
  await page.keyboard.press('Space');
  await expect.poll(() => paragraphValues(page)).toEqual(before);
  await expect(liveAnnouncer(page)).toHaveText(C.reorderAt(1, 2));
  await expect(paragraphTextarea(page, 1)).toBeFocused();

  // The ends are aria-disabled yet keep their place in the tab order and stay focusable.
  const up1 = page.getByRole('button', { name: C.moveUpAt(1), exact: true });
  const down4 = page.getByRole('button', { name: C.moveDownAt(4), exact: true });
  await expect(up1).toHaveAttribute('aria-disabled', 'true');
  await expect(down4).toHaveAttribute('aria-disabled', 'true');
  await up1.focus();
  await expect(up1).toBeFocused();
  await down4.focus();
  await expect(down4).toBeFocused();

  // A keyboard press on an end is a no-op: no reorder, no request. The bounded wait gives a wrongly-applied reorder
  // (and its render) time to appear, so a broken guard cannot pass by racing the assertion.
  await up1.focus();
  await page.keyboard.press('Enter');
  await page.waitForTimeout(200);
  expect(await paragraphValues(page)).toEqual(before);
  expect(api.puts).toHaveLength(0);
}

/** Bullet 2: saving after a move sends the RAW story.v1 body in the new order + If-Match; the editor reflects it. */
export async function saveAfterMoveFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const api = await openEditor(page, lang);
  const paragraphs = STORY_CONTENT.paragraphs;
  const movedContent: StoryWire = { ...STORY_CONTENT, paragraphs: [paragraphs[1]!, paragraphs[0]!, paragraphs[2]!, paragraphs[3]!] };
  await page.getByRole('button', { name: C.moveUpAt(2), exact: true }).click();
  await expect.poll(() => paragraphValues(page)).toEqual(movedContent.paragraphs.map((paragraph) => paragraph.text));
  await expect(saveButton(page, lang)).toBeEnabled();
  await saveButton(page, lang).click();
  await expectSaved(page, lang);
  expect(api.puts).toHaveLength(1);
  // expectStoryPut proves the If-Match header and the exact story.v1 key shape; the body is the object itself,
  // never wrapped in `{ content }` (10.5.3-T1 Notes).
  expectStoryPut(api.puts[0]!, STORED_STORY_ETAG, movedContent);
  const body = api.puts[0]!.postDataJSON() as Record<string, unknown>;
  expect(body['content']).toBeUndefined();
  expect(Array.isArray(body['paragraphs'])).toBe(true);
  // The editor reflects the saved response: a reload keeps the new order.
  await page.reload();
  await expectEditor(page, lang);
  await expect.poll(() => paragraphValues(page)).toEqual(movedContent.paragraphs.map((paragraph) => paragraph.text));
  await expectA11yClean(page);
}

/**
 * Bullet 2 (pointer): dragging paragraph 3's handle above paragraph 1 reorders the list with the mouse, announces
 * the new position and saves the new order. The drag handle is declared inside `ParagraphCardComponent`'s own
 * template, so this flow guards that card's self-registration as the drop list's drag handle. CDK caches each item's
 * rect at drag start and reads only the pointer's Y for a vertical list, so the pointer ends inside paragraph 1's box
 * (above its midpoint, but still within its rect) and the result is polled instead of slept on.
 */
export async function mouseDragFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  // Pointer events are only delivered inside the viewport: keep paragraphs 1 and 3 both on screen.
  await page.setViewportSize({ width: 1280, height: 2400 });
  const api = await openEditor(page, lang);
  const paragraphs = STORY_CONTENT.paragraphs;
  const movedContent: StoryWire = { ...STORY_CONTENT, paragraphs: [paragraphs[2]!, paragraphs[0]!, paragraphs[1]!, paragraphs[3]!] };

  const handle = page.locator('nts-paragraph-card').nth(2).getByRole('img', { name: C.moveHandle, exact: true });
  const target = page.locator('nts-paragraph-card').nth(0);
  await expect(handle).toBeVisible();
  const from = await handle.boundingBox();
  const box = await target.boundingBox();
  if (from === null || box === null) throw new Error('paragraph drag handles must be laid out');

  const startX = from.x + from.width / 2;
  const startY = from.y + from.height / 2;
  const endX = box.x + box.width / 2;
  // Upper quarter of paragraph 1: past its midpoint yet inside its rect (a vertical list reads the pointer's Y only).
  const endY = box.y + box.height / 4;
  const steps = 20;

  await page.mouse.move(startX, startY);
  await page.mouse.down();
  for (let step = 1; step <= steps; step += 1) {
    await page.mouse.move(startX + ((endX - startX) * step) / steps, startY + ((endY - startY) * step) / steps);
  }
  await page.mouse.up();

  await expect.poll(() => paragraphValues(page)).toEqual(movedContent.paragraphs.map((paragraph) => paragraph.text));
  await expect(liveAnnouncer(page)).toHaveText(C.reorderAt(3, 1));
  await expect(paragraphTextarea(page, 0)).toBeFocused();

  // The save-after-move contract: Save sends the RAW story.v1 body in the new order, and a reload keeps that order.
  await expect(saveButton(page, lang)).toBeEnabled();
  await saveButton(page, lang).click();
  await expectSaved(page, lang);
  expect(api.puts).toHaveLength(1);
  expectStoryPut(api.puts[0]!, STORED_STORY_ETAG, movedContent);
  await page.reload();
  await expectEditor(page, lang);
  await expect.poll(() => paragraphValues(page)).toEqual(movedContent.paragraphs.map((paragraph) => paragraph.text));
  await expectA11yClean(page);
}

/**
 * Bullet 3: delete shows the snackbar (`deleted` + Undo); Undo restores the paragraph at its original position with
 * its keywords and beat and returns focus. With `waitForExpiry` the second deletion is left to expire (the real 10 s
 * window, exercised by the vi spec only) and the Undo action is gone while the deletion stands.
 */
export async function deleteUndoFlow(page: Page, lang: E2eLang, options: { readonly waitForExpiry: boolean }): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  const api = await openEditor(page, lang);
  const before = STORY_CONTENT.paragraphs;
  const values = before.map((paragraph) => paragraph.text);

  await paragraphDelete(page, L.paragraph(2), C.deleteAt(2)).click();
  await expect(snackbar(page)).toContainText(C.deleted);
  await expect(snackbarAction(page, C.undo)).toBeVisible();
  // Focus hands to the paragraph that takes the deleted one's place, and the order is reduced.
  await expect.poll(() => paragraphValues(page)).toEqual([values[0]!, values[2]!, values[3]!]);
  await expect(paragraphTextarea(page, 1)).toBeFocused();
  await expect(paragraphTextarea(page, 1)).toHaveValue(values[2]!);
  await snackbarSettled(page);
  await expectA11yClean(page);

  // Undo restores the paragraph at its original position with its keywords and beat, focus returns.
  await snackbarAction(page, C.undo).click();
  await expect.poll(() => paragraphValues(page)).toEqual(values);
  await expect(paragraphTextarea(page, 1)).toBeFocused();
  const restored = segmentGroup(page, L.paragraph(2));
  const scenes = restored.getByRole('list', { name: L.scenes, exact: true });
  await expect(scenes.getByRole('textbox', { name: L.scene(1), exact: true })).toHaveValue(before[1]!.keywords[0]!);
  await expect(scenes.getByRole('textbox', { name: L.scene(2), exact: true })).toHaveValue(before[1]!.keywords[1]!);
  await expect(restored).toContainText(before[1]!.beat);
  await expect(page.getByRole('button', { name: C.undo, exact: true })).toHaveCount(0);
  // The dismissed snackbar keeps a fading container for ~75 ms; the scan waits for it to be removed.
  await expect(snackbar(page)).toHaveCount(0);
  await expectA11yClean(page);

  if (options.waitForExpiry) {
    await paragraphDelete(page, L.paragraph(2), C.deleteAt(2)).click();
    await expect(snackbarAction(page, C.undo)).toBeVisible();
    await expect(page.getByRole('button', { name: C.undo, exact: true })).toHaveCount(0, { timeout: 15_000 });
    await expect(snackbar(page)).toHaveCount(0, { timeout: 15_000 });
    await expect.poll(() => paragraphValues(page)).toEqual([values[0]!, values[2]!, values[3]!]);
    await expectA11yClean(page);
  }
  expect(api.puts).toHaveLength(0);
}

/** Bullet 4: the dirty guard asks on in-app navigation only when dirty; Stay keeps, Leave discards. */
export async function dirtyGuardFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];

  // A clean draft navigates without a dialog.
  const api = await openEditor(page, lang);
  await page.getByRole('link', { name: C.back, exact: true }).click();
  await expect(page).toHaveURL((url) => url.pathname === '/episodes');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(api.puts).toHaveLength(0);

  // A dirty draft: the list row opens the editor in-app (no reload, same stubbed routes), and the back link now asks.
  await page.getByRole('main').getByRole('link', { name: STORY_CONTENT.title, exact: true }).click();
  await expectEditor(page, lang);
  await segment(page, L.title).fill(EDITED.title);
  await page.getByRole('link', { name: C.back, exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(C.dirtyTitle);
  await expect(dialog).toContainText(C.dirtyBody);
  // The MatDialog entrance fade blends the dialog's own colours toward the scrim; axe samples rendered colours, so
  // the scan waits for the overlay's animations to end and every surface to be fully opaque (10.3.3-T3 D7).
  await waitForOverlayAnimationEnd(page);
  await expectA11yClean(page);

  // Stay keeps the route and the draft.
  await dialog.getByRole('button', { name: C.dirtyStay, exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}$`));
  await expect(segment(page, L.title)).toHaveValue(EDITED.title);

  // Leave navigates away and discards the draft.
  await page.getByRole('link', { name: C.back, exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: C.dirtyLeave, exact: true }).click();
  await expect(page).toHaveURL((url) => url.pathname === '/episodes');
  expect(api.puts).toHaveLength(0);
}

/** Bullet 5 (dirty): closing the tab with unsaved changes raises the browser `beforeunload` dialog. */
export async function beforeUnloadDirtyFlow(page: Page, lang: E2eLang): Promise<void> {
  const L = EDITOR_COPY[lang];
  const dialogs: string[] = [];
  page.on('dialog', (dialog) => { dialogs.push(dialog.type()); void dialog.accept(); });
  await openEditor(page, lang);
  await segment(page, L.title).click();
  await segment(page, L.title).fill(EDITED.title);
  await expect(page.getByRole('status').filter({ hasText: L.dirty })).toContainText(L.dirty);
  const closed = page.waitForEvent('close');
  await page.close({ runBeforeUnload: true });
  await expect.poll(() => dialogs).toContain('beforeunload');
  await closed;
}

/** Bullet 5 (clean): after an explicit save the listener is gone, so closing the tab prompts nothing. */
export async function beforeUnloadCleanFlow(page: Page, lang: E2eLang): Promise<void> {
  const L = EDITOR_COPY[lang];
  const dialogs: string[] = [];
  page.on('dialog', (dialog) => { dialogs.push(dialog.type()); void dialog.accept(); });
  const api = await openEditor(page, lang);
  await segment(page, L.title).click();
  await segment(page, L.title).fill(EDITED.title);
  await saveButton(page, lang).click();
  await expectSaved(page, lang);
  expect(api.puts).toHaveLength(1);
  // `page.close({ runBeforeUnload: true })` returns before the page finishes closing; the `close` event marks that the
  // browser ran (or skipped) the unload, so the dialog list is complete when it fires.
  const closed = page.waitForEvent('close');
  await page.close({ runBeforeUnload: true });
  await closed;
  expect(dialogs).toHaveLength(0);
}

/** Bullet 6: logging out with unsaved changes asks first; Stay keeps the session, Leave signs out to /login. */
export async function logoutDirtyFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  await page.route('**/api/v1/auth/logout', (route) => route.fulfill({ status: 204, body: '' }));
  const api = await openEditor(page, lang);
  await segment(page, L.title).fill(EDITED.title);

  const signOut = async (): Promise<void> => {
    await page.getByRole('button', { name: C.menuTrigger, exact: true }).click();
    await page.getByRole('menuitem', { name: C.menuLogOut, exact: true }).click();
  };

  // Stay: the logout is aborted; the session and the draft survive.
  await signOut();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(C.dirtyTitle);
  await waitForOverlayAnimationEnd(page);
  await expectA11yClean(page);
  await dialog.getByRole('button', { name: C.dirtyStay, exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`/episodes/${EPISODE_ID}$`));
  await expect(segment(page, L.title)).toHaveValue(EDITED.title);

  // Leave: the logout proceeds to the signed-out route.
  await signOut();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: C.dirtyLeave, exact: true }).click();
  await expect(page).toHaveURL((url) => url.pathname === '/login');
  expect(api.puts).toHaveLength(0);
}

/** Bullet 7: the structure advisory (out of the tier band) is rendered and axe-clean and never gates Save. */
export async function structureAdvisoryFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  // `short` is the [4, 5]-segment band and a segment is a paragraph plus the hook and the ending, so a two-paragraph
  // draft sits inside the band (no advisory) while deleting one drops it below the minimum (advisory on). This is the
  // `Delete while only one paragraph remains` row of the task's failure-handling table.
  const twoParagraphs: StoryWire = { ...STORY_CONTENT, paragraphs: STORY_CONTENT.paragraphs.slice(0, 2) };
  const api = await openEditor(page, lang, { detail: editorDetail({ lengthTier: 'short', currentStory: storySnapshot(twoParagraphs) }) });
  const warning = page.getByRole('status').filter({ hasText: C.structureWarning });
  await expect(warning).toHaveCount(0);
  await expectA11yClean(page);

  await paragraphDelete(page, L.paragraph(1), C.deleteAt(1)).click();
  await expect(warning).toBeVisible();
  await expect(saveButton(page, lang)).toBeEnabled();
  await snackbarSettled(page);
  await expectA11yClean(page);
  expect(api.puts).toHaveLength(0);
}

/** Bullet 8: at 360 px there is no horizontal scroll and every paragraph and control is keyboard reachable. */
export async function mobileReorderFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  await page.setViewportSize({ width: 360, height: 800 });
  await openEditor(page, lang);
  expect(await noHorizontalOverflow(page)).toBe(true);

  // Every paragraph is reachable through the segment navigator, focused and on screen.
  const selector = page.getByRole('combobox', { name: L.choose, exact: true });
  for (let index = 0; index < STORY_CONTENT.paragraphs.length; index += 1) {
    await selector.selectOption(`p:${index}`);
    await expect(paragraphTextarea(page, index)).toBeFocused();
    await expect(paragraphTextarea(page, index)).toBeInViewport();
    expect(await noHorizontalOverflow(page), `360 px overflow at p:${index}`).toBe(true);
  }

  // Every paragraph's move/delete controls are reachable with the Tab key alone.
  const targets = STORY_CONTENT.paragraphs.flatMap((_paragraph, index) => [
    page.getByRole('button', { name: C.moveUpAt(index + 1), exact: true }),
    page.getByRole('button', { name: C.moveDownAt(index + 1), exact: true }),
    paragraphDelete(page, L.paragraph(index + 1), C.deleteAt(index + 1)),
  ]);
  const reached = new Set<number>();
  await paragraphTextarea(page, 0).focus();
  for (let step = 0; step < 120 && reached.size < targets.length; step += 1) {
    await page.keyboard.press('Tab');
    for (let index = 0; index < targets.length; index += 1) {
      if (!reached.has(index) && (await targets[index]!.evaluate((node) => node === document.activeElement))) reached.add(index);
    }
  }
  expect(reached.size, 'Tab must reach every paragraph move/delete control at 360 px').toBe(targets.length);
  await expectA11yClean(page);
  expect(await noHorizontalOverflow(page)).toBe(true);
}

/**
 * Bullet 8 (`prefers-reduced-motion`): the drop-list/card transition and the snackbar slide are disabled by the
 * global rule in `web/src/styles.scss` — the motion tokens resolve to 0, the paragraph-card button has no transition
 * duration, and the snackbar's Material enter animation (which Material keeps enabled in JS under every media query)
 * computes to the zeroed `0.01ms`/`0ms` durations. `0.01ms` is 1e-5 s, so the reduce side asserts `<= 0.00001 s`.
 */
export async function reducedMotionFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await openEditor(page, lang);
  expect(await motionToken(page, '--nts-motion-fast')).toBe('160ms');
  expect(await motionToken(page, '--nts-motion-base')).toBe('240ms');
  expect(await cardButtonTransition(page)).toBe('0.16s');
  await paragraphDelete(page, L.paragraph(2), C.deleteAt(2)).click();
  await expect(snackbar(page)).toBeVisible();
  // Material keeps `mat-snack-bar-container-enter` for as long as the snackbar is visible, so its 150 ms enter
  // animation is a stable non-zero duration here (the class it replaces is never dropped under reduced motion).
  await expect.poll(async () => (await snackbarMotionSeconds(snackbar(page))).container, {
    message: 'the snackbar motion is present without reduced motion',
  }).toBeGreaterThan(0);
  await snackbarAction(page, C.undo).click();

  await page.emulateMedia({ reducedMotion: 'reduce' });
  expect(await motionToken(page, '--nts-motion-fast')).toBe('0ms');
  expect(await motionToken(page, '--nts-motion-base')).toBe('0ms');
  expect(await cardButtonTransition(page)).toBe('0s');
  await paragraphDelete(page, L.paragraph(2), C.deleteAt(2)).click();
  await expect(snackbar(page)).toBeVisible();
  const reduced = await snackbarMotionSeconds(snackbar(page));
  expect(reduced.container, 'the snackbar container motion is disabled under reduced motion').toBeLessThanOrEqual(0.00001);
  expect(reduced.surface, 'the snackbar surface motion is disabled under reduced motion').toBeLessThanOrEqual(0.00001);
  await snackbarSettled(page);
  await expectA11yClean(page);
}

/**
 * Bullet 4/5 (task file acceptance 6): a deletion that was never saved is a client-side draft only. The paragraph is
 * gone from the list and nothing was written, and a reload of the editor serves the stored version again — every
 * original paragraph back in its original order, a clean save button and no unsaved indicator. The draft is dirty, so
 * the reload raises the browser `beforeunload` dialog: the listener is registered for this page only and accepts it,
 * which is what keeps the reload from being blocked (Playwright leaves an unhandled `beforeunload` raised).
 */
export async function deleteWithoutSavingReloadFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  page.on('dialog', (dialog) => { void dialog.accept(); });
  const api = await openEditor(page, lang);
  const saved = STORY_CONTENT.paragraphs.map((paragraph) => paragraph.text);

  await paragraphDelete(page, L.paragraph(2), C.deleteAt(2)).click();
  await expect(snackbar(page)).toContainText(C.deleted);
  await expect.poll(() => paragraphValues(page)).toEqual([saved[0]!, saved[2]!, saved[3]!]);
  expect(api.puts, 'deleting without saving must not reach the API').toHaveLength(0);

  await page.reload();
  // The reload re-reads the episode detail and re-renders the stored draft, so what comes back is the server state.
  await expect.poll(() => api.detailGets.length).toBeGreaterThan(1);
  await expect.poll(() => paragraphValues(page)).toEqual(saved);
  for (let index = 0; index < saved.length; index += 1) {
    await expect(segment(page, L.paragraph(index + 1))).toHaveValue(saved[index]!);
  }
  await expect(saveButton(page, lang)).toBeDisabled();
  await expect(page.getByRole('status').filter({ hasText: L.dirty })).toHaveCount(0);
  expect(api.puts, 'a reload must not reach the API either').toHaveLength(0);
}

/**
 * Bullet 8 (task file acceptance 13): at 360 px the paragraph card's action row wraps under its own textarea instead
 * of squeezing the field, so the field keeps the full card width and the move/delete buttons still fit the viewport
 * with no horizontal scroll. The card's `.paragraph-card__actions` is a wrapping flex row that goes `width: 100%`
 * under the 30 rem breakpoint and the segment editor stacks it after the textarea, hence the strict `y` order.
 */
export async function narrow360ActionWrapFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = EDITOR_STRUCTURE_COPY[lang];
  const L = EDITOR_COPY[lang];
  await page.setViewportSize({ width: 360, height: 800 });
  await openEditor(page, lang);
  expect(await noHorizontalOverflow(page)).toBe(true);

  const card = segmentGroup(page, L.paragraph(2));
  const textarea = await paragraphBox(paragraphTextarea(page, 1), 'paragraph 2 textarea');
  const controls: readonly (readonly [string, Locator])[] = [
    ['move up', card.getByRole('button', { name: C.moveUpAt(2), exact: true })],
    ['move down', card.getByRole('button', { name: C.moveDownAt(2), exact: true })],
    ['delete', paragraphDelete(page, L.paragraph(2), C.deleteAt(2))],
  ];
  for (const [what, control] of controls) {
    const box = await paragraphBox(control, `paragraph 2 ${what} button`);
    expect(box.y, `the ${what} button must wrap below the paragraph 2 textarea at 360 px`).toBeGreaterThanOrEqual(textarea.y + textarea.height - 1);
    expect(box.x, `the ${what} button must not be clipped past the left edge at 360 px`).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width, `the ${what} button must stay inside the 360 px viewport`).toBeLessThanOrEqual(360);
  }
  expect(await noHorizontalOverflow(page)).toBe(true);
}

// ---------------------------------------------------------------------------------------------------------------
// 10.5.3-T2 self-review panel, "automatically corrected once" badge and similarity highlights.
// `episodes-self-review.spec.ts` (vi) and `episodes-self-review-en.spec.ts` (en) run the same scenarios through these
// helpers, so both locales share one implementation of each acceptance bullet. Every payload here is a stubbed
// `currentStory`: `auditReport` is the verbatim `audit.v1` document in its snake_case wire shape, `qualityHints` is the
// flattened `local.issues ++ model_review.issues` set (02.3.4-T2:42) plus the 02.5.6 similarity hint, and the page is
// expected to read all of it from the detail read it already makes.
// ---------------------------------------------------------------------------------------------------------------

/**
 * Copy for the panel, the badge and the similarity note, read from the shipped bundles so a copy or key change fails
 * these specs instead of drifting. Placeholders are formatted here with the same values the components interpolate.
 */
export const SELF_REVIEW_COPY = {
  vi: {
    badge: storyVi.generation.revised.badge,
    heading: episodesVi.audit.heading,
    blocking: episodesVi.audit.blocking,
    advisory: episodesVi.audit.advisory,
    drift: episodesVi.audit.drift,
    scores: episodesVi.audit.scores,
    dimension: episodesVi.audit.dimension,
    scoreValue: episodesVi.audit.scoreValue,
    dimensions: [
      episodesVi.audit.score.hook, episodesVi.audit.score.pacing, episodesVi.audit.score.atmosphere,
      episodesVi.audit.score.natural_language, episodesVi.audit.score.originality, episodesVi.audit.score.factual_care,
      episodesVi.audit.score.length_fit,
    ],
    impact: episodesVi.audit.impact,
    evidence: episodesVi.audit.evidence,
    repair: episodesVi.audit.repair,
    showWhy: episodesVi.audit.showWhy,
    why: episodesVi.audit.why,
    noChecks: episodesVi.audit.noChecks,
    openSegment: (address: string): string => episodesVi.audit.openSegment.replace('{{segment}}', address),
    similarityHeading: episodesVi.similarity.heading,
    similarityNote: episodesVi.similarity.note,
    similarityScore: (percent: string): string => episodesVi.similarity.score.replace('{{percent}}', percent),
    segmentShare: (label: string, percent: string): string =>
      episodesVi.similarity.segmentShare.replace('{{segment}}', label).replace('{{percent}}', percent),
    similarityWarn: qualityVi.similarity_warn,
    similarityBlocked: qualityVi.similarity_blocked,
    warn: storyVi.similarity.warn,
    block: storyVi.similarity.block,
    unavailable: storyVi.similarity.unavailable,
  },
  en: {
    badge: storyEn.generation.revised.badge,
    heading: episodesEn.audit.heading,
    blocking: episodesEn.audit.blocking,
    advisory: episodesEn.audit.advisory,
    drift: episodesEn.audit.drift,
    scores: episodesEn.audit.scores,
    dimension: episodesEn.audit.dimension,
    scoreValue: episodesEn.audit.scoreValue,
    dimensions: [
      episodesEn.audit.score.hook, episodesEn.audit.score.pacing, episodesEn.audit.score.atmosphere,
      episodesEn.audit.score.natural_language, episodesEn.audit.score.originality, episodesEn.audit.score.factual_care,
      episodesEn.audit.score.length_fit,
    ],
    impact: episodesEn.audit.impact,
    evidence: episodesEn.audit.evidence,
    repair: episodesEn.audit.repair,
    showWhy: episodesEn.audit.showWhy,
    why: episodesEn.audit.why,
    noChecks: episodesEn.audit.noChecks,
    openSegment: (address: string): string => episodesEn.audit.openSegment.replace('{{segment}}', address),
    similarityHeading: episodesEn.similarity.heading,
    similarityNote: episodesEn.similarity.note,
    similarityScore: (percent: string): string => episodesEn.similarity.score.replace('{{percent}}', percent),
    segmentShare: (label: string, percent: string): string =>
      episodesEn.similarity.segmentShare.replace('{{segment}}', label).replace('{{percent}}', percent),
    similarityWarn: qualityEn.similarity_warn,
    similarityBlocked: qualityEn.similarity_blocked,
    warn: storyEn.similarity.warn,
    block: storyEn.similarity.block,
    unavailable: storyEn.similarity.unavailable,
  },
} as const;

/** A version id for the revised snapshot, distinct from the stored/fresh/saved ids of the editor flows. */
export const SELF_REVIEW_VERSION_ID = '88888888-8888-4888-8888-888888888888';
/** The blocking objective issue's evidence: literal markup the panel must render as text, never as an element. */
export const SELF_REVIEW_EVIDENCE = 'Draft line: <b>x</b> stays plain text.';
/** The taste issue arrives claiming `blocking`; the client (and the server) clamp it to advisory. */
const SELF_REVIEW_TASTE_EVIDENCE = 'Repeated wording in the opening.';
const SELF_REVIEW_DRIFT_EVIDENCE = 'Paragraph 2 changed after the automatic fix.';
const SELF_REVIEW_BLOCKING_IMPACT = 'The paragraph reads as a list of beats.';
const SELF_REVIEW_BLOCKING_REPAIR = 'Keep at most two scenes.';
/** The seven rubric scores, in `SCORE_DIMENSIONS` order; every row asserts one cell below. */
const SELF_REVIEW_SCORES = { hook: 3, pacing: 4, atmosphere: 2, natural_language: 5, originality: 3, factual_care: 4, length_fit: 2 } as const;

/** One `audit.v1` `model_review.issues[]` row (`kind`, `severity`, `repair_direction` are the wire names). */
function reviewIssue(options: {
  readonly id: string; readonly type: string; readonly kind: 'objective' | 'taste'; readonly severity: 'blocking' | 'advisory';
  readonly segment: string; readonly impact: string; readonly evidence: string; readonly repairDirection: string | null;
}): Record<string, unknown> {
  return {
    id: options.id, type: options.type, kind: options.kind, severity: options.severity, segment: options.segment,
    impact: options.impact, evidence: options.evidence, repair_direction: options.repairDirection,
  };
}

const BLOCKING_ISSUE = reviewIssue({
  id: 'review-p1-keyword_count_per_segment', type: 'keyword_count_per_segment', kind: 'objective', severity: 'blocking',
  segment: 'p:1', impact: SELF_REVIEW_BLOCKING_IMPACT, evidence: SELF_REVIEW_EVIDENCE, repairDirection: SELF_REVIEW_BLOCKING_REPAIR,
});
const TASTE_ISSUE = reviewIssue({
  id: 'review-hook-repetitive_phrasing', type: 'repetitive_phrasing', kind: 'taste', severity: 'blocking',
  segment: 'hook', impact: 'The opening reads as a repeat.', evidence: SELF_REVIEW_TASTE_EVIDENCE, repairDirection: null,
});
/** The revision's own advisory drift finding: review #2 claims it, so it is what the expanded panel lists. */
const DRIFT_ISSUE = reviewIssue({
  id: 'review-revision-drift', type: 'revision_drift', kind: 'objective', severity: 'advisory', segment: 'p:1',
  impact: 'The automatic fix may have drifted.', evidence: SELF_REVIEW_DRIFT_EVIDENCE, repairDirection: 'Review the fixed paragraph.',
});

export interface SelfReviewSeedOptions {
  /** The similarity hint the hint set carries: `Q-SIM-01` warns, `Q-SIM-02` blocks (the client never re-derives it). */
  readonly verdict?: 'warn' | 'block';
  /** `currentStory.similarityScore`; `null` means "not scorable" and shows `story.similarity.unavailable` when sources exist. */
  readonly similarityScore?: number | null;
  /** The recorded `similarity.segments[]` overlap; defaults to the single `p:1` row at 21%. */
  readonly segments?: readonly { readonly segment: string; readonly share: number }[];
  /** `false` drops `auditReport` entirely (soft 02.6.2-T1: empty panel, no badge, no error). */
  readonly audit?: boolean;
}

/** The verbatim `audit.v1` document this task reads: review #1, the applied revision with review #2, and similarity. */
export function selfReviewAuditReport(options: SelfReviewSeedOptions = {}): Record<string, unknown> {
  const similarityScore = options.similarityScore === undefined ? 0.21 : options.similarityScore;
  return {
    model_review: {
      model: 'e2e-review-model', prompt_version: 'review.v1', stale: false, scores: SELF_REVIEW_SCORES,
      issues: [BLOCKING_ISSUE, TASTE_ISSUE], real_world_claims: [],
    },
    revision: {
      applied: true,
      fixed_issue_ids: ['review-p1-keyword_count_per_segment'],
      parent_version_id: STORED_VERSION_ID,
      drift: { changed_segments: ['p:1'], unflagged_changed: [], added_findings: [] },
      review_after: { scores: SELF_REVIEW_SCORES, issues: [BLOCKING_ISSUE, TASTE_ISSUE, DRIFT_ISSUE] },
    },
    similarity: {
      score: similarityScore, n: 14, corpus: 'sources',
      segments: options.segments ?? [{ segment: 'p:1', share: 0.21 }],
    },
  };
}

/**
 * The flattened `qualityHints` set. The two review hints mirror their recorded issue (`segment`, `kind`, severity,
 * evidence, repair direction) so the panel's `partial` note never fires for a payload that agrees with itself; the
 * similarity hint is the ONLY source of the highlight verdict (`Q-SIM-01` warn / `Q-SIM-02` block).
 */
export function selfReviewHints(options: SelfReviewSeedOptions = {}): readonly CurrentStoryDto['qualityHints'][number][] {
  const blocked = (options.verdict ?? 'warn') === 'block';
  return [
    {
      ruleId: 'keyword_count_per_segment', messageKey: 'quality.keyword_count_per_segment', evidence: SELF_REVIEW_EVIDENCE,
      kind: 'objective', severity: 'blocking', segment: 'p:1', repairDirection: SELF_REVIEW_BLOCKING_REPAIR,
      measured: null, threshold: null,
    },
    {
      ruleId: 'repetitive_phrasing', messageKey: 'quality.repetitive_phrasing', evidence: SELF_REVIEW_TASTE_EVIDENCE,
      kind: 'taste', severity: 'advisory', segment: 'hook', repairDirection: null, measured: null, threshold: null,
    },
    {
      ruleId: blocked ? 'Q-SIM-02' : 'Q-SIM-01', messageKey: blocked ? 'quality.similarity_blocked' : 'quality.similarity_warn',
      evidence: '', kind: 'objective', severity: blocked ? 'blocking' : 'advisory', segment: 'meta',
      repairDirection: null, measured: null, threshold: null,
    },
  ];
}

/** A `story_review` episode whose current version is `source = revised`, carrying the seeded review payload. */
export function selfReviewDetail(options: SelfReviewSeedOptions = {}): EpisodeDetailDto {
  const similarityScore = options.similarityScore === undefined ? 0.21 : options.similarityScore;
  return editorDetail({
    currentStory: {
      ...storySnapshot(STORY_CONTENT, SELF_REVIEW_VERSION_ID, 2),
      // No audit document means no revision either (soft 02.6.2-T1): the version reads as a plain manual one, so the
      // empty-state case can also prove that no badge is rendered.
      source: options.audit === false ? 'manual' : 'revised',
      auditReport: options.audit === false ? null : selfReviewAuditReport(options),
      similarityScore,
      qualityHints: [...selfReviewHints(options)],
    },
  });
}

interface SelfReviewWorld {
  readonly api: StoryEditorStubs;
  readonly server: ResearchServer;
}

/**
 * Registers the read-only gate read (10.5.5-T1: `/sources`, `/fact-sheet`, `/episodes/{id}`, `/series/{id}`) BEFORE the
 * story stubs, so `stubStoryEditor`'s detail route answers the editor's own reads (Playwright resolves the most
 * recently registered matching route first) and the page's gate sees the seeded source list.
 */
async function openSelfReview(
  page: Page, lang: E2eLang,
  options: { readonly detail?: EpisodeDetailDto; readonly sources?: readonly SourceListItemDto[] } = {},
): Promise<SelfReviewWorld> {
  const server = await stubResearch(page, {
    sources: options.sources ?? [researchSource(1)], sheet: null, generating: false, requests: [],
  });
  const api = await openEditor(page, lang, { detail: options.detail ?? selfReviewDetail() });
  return { api, server };
}

const reviewPanel = (page: Page): Locator => page.locator('nts-review-panel');
const panelBody = (page: Page): Locator => reviewPanel(page).locator('.review-panel__body');

/** Scrolls the panel into view (its body is `@defer (on viewport)`, same as the annotation panel) and waits for it. */
async function openPanelBody(page: Page): Promise<Locator> {
  const panel = reviewPanel(page);
  await panel.scrollIntoViewIfNeeded();
  await expect(panelBody(page)).toBeVisible();
  return panel;
}

/**
 * Task file acceptance 1/2/3/5/6/7/12 (vi + en): the revised badge, the derived counts, the seven-row score table with
 * its caption, the expanded "why" panel rendering evidence/impact/repair as TEXT (a taste issue stays advisory), the
 * drift note, and the similarity panel row plus the inline `quality.similarity_warn` note on paragraph 2.
 */
export async function selfReviewPanelFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = SELF_REVIEW_COPY[lang];
  const L = EDITOR_COPY[lang];
  await openSelfReview(page, lang);

  // (1) The revised version shows the shared badge, eager in the page header, described by the drift note.
  const badge = page.locator('nts-revised-badge .revised-badge');
  await expect(badge).toHaveText(C.badge);
  await expect(badge).toHaveAttribute('role', 'note');
  await expect(badge).toHaveAttribute('aria-describedby', 'story-review-audit-drift');
  await expect(page.locator('#story-review-audit-drift')).toHaveText(C.drift);

  // (2) The panel is a named region (its section carries `episodes.audit.heading`) with the derived counts.
  await expect(page.getByRole('region', { name: C.heading, exact: true })).toBeVisible();
  const counts = reviewPanel(page).locator('.review-panel__counts');
  await expect(counts).toContainText(`${C.blocking}: 1`);
  await expect(counts).toContainText(`${C.advisory}: 2`);

  const panel = await openPanelBody(page);

  // (3) The scores are a real table: a caption, two column headers and seven row headers.
  const table = panel.getByRole('table');
  await expect(table).toBeVisible();
  await expect(table.locator('caption')).toHaveText(C.scores);
  await expect(table.getByRole('columnheader')).toHaveText([C.dimension, C.scoreValue]);
  await expect(table.getByRole('rowheader')).toHaveText([...C.dimensions]);
  await expect(table.getByRole('cell')).toHaveText(['3', '4', '2', '5', '3', '4', '2']);
  // `model_review.stale` is false, so the stale marker is not rendered next to the scores.
  await expect(page.locator('#story-review-audit-stale')).toHaveCount(0);

  // (4) Similarity: the overall share, the overlapping address with its percentage and the server's warn copy.
  const similarity = panel.locator('.review-panel__similarity');
  await expect(similarity.getByRole('heading', { name: C.similarityHeading, exact: true })).toBeVisible();
  await expect(similarity).toContainText(C.similarityScore('21'));
  const row = similarity.getByRole('button', { name: C.segmentShare('2', '21'), exact: true });
  await expect(row).toBeVisible();
  await expect(similarity.locator('li').filter({ hasText: C.segmentShare('2', '21') })).toContainText(C.warn);
  await expect(similarity).toContainText(C.similarityNote);

  // (5) The same verdict reaches paragraph 2 as a text note plus the quiet outline (decoration only).
  const note = page.locator('#seg-p-1-similarity');
  await expect(note).toHaveText(C.similarityWarn);
  await expect(page.locator('textarea[data-segment="p:1"]')).toHaveAttribute('aria-describedby', /seg-p-1-similarity/);
  await expect(segmentGroup(page, L.paragraph(2))).toHaveClass(/segment-editor--similarity-warn/);

  // (6) The why disclosure is a real control and the recorded review renders evidence/impact/repair as text.
  const disclosure = panel.locator('.review-panel__disclosure');
  await expect(disclosure).toHaveText(C.showWhy);
  await expect(disclosure).toHaveAttribute('aria-controls', 'story-review-audit-why');
  await disclosure.click();
  await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
  const why = page.locator('#story-review-audit-why');
  await expect(why).toBeVisible();
  await expect(why).toHaveAttribute('aria-label', C.why);

  const blocking = why.locator('li').filter({ hasText: SELF_REVIEW_EVIDENCE });
  await expect(blocking).toHaveCount(1);
  await expect(blocking).toContainText(C.blocking);
  await expect(blocking).toContainText(`${C.impact}:`);
  await expect(blocking).toContainText(SELF_REVIEW_BLOCKING_IMPACT);
  await expect(blocking).toContainText(`${C.evidence}:`);
  await expect(blocking).toContainText(`${C.repair}:`);
  await expect(blocking).toContainText(SELF_REVIEW_BLOCKING_REPAIR);
  // The literal `<b>` markers stay text: interpolation only, no innerHTML, no element.
  await expect(blocking.locator('b')).toHaveCount(0);
  await expect(blocking).toContainText(SELF_REVIEW_EVIDENCE);
  await expect(blocking.getByRole('button', { name: C.openSegment('p:1'), exact: true })).toBeVisible();

  // The taste issue's payload claimed `blocking`; it is rendered — and counted — as advisory.
  const taste = why.locator('li').filter({ hasText: SELF_REVIEW_TASTE_EVIDENCE });
  await expect(taste).toContainText(C.advisory);
  await expect(taste).not.toContainText(C.blocking);
  // The revision's drift finding is listed as an advisory issue too.
  await expect(why.locator('li').filter({ hasText: SELF_REVIEW_DRIFT_EVIDENCE })).toContainText(C.advisory);
}

/** Task file acceptance 6 (blocked variant): a `Q-SIM-02` hint set renders the blocked copy, never a re-derived verdict. */
export async function selfReviewBlockedVariantFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = SELF_REVIEW_COPY[lang];
  const L = EDITOR_COPY[lang];
  await openSelfReview(page, lang, { detail: selfReviewDetail({ verdict: 'block' }) });

  const panel = await openPanelBody(page);
  const similarity = panel.locator('.review-panel__similarity');
  const row = similarity.locator('li').filter({ hasText: C.segmentShare('2', '21') });
  await expect(row).toContainText(C.block);
  await expect(row).not.toContainText(C.warn);

  const note = page.locator('#seg-p-1-similarity');
  await expect(note).toHaveText(C.similarityBlocked);
  await expect(note).not.toHaveText(C.similarityWarn);
  await expect(segmentGroup(page, L.paragraph(2))).toHaveClass(/segment-editor--similarity-block/);
}

/** Task file acceptance 8: no `auditReport` means an empty state, no badge and no error — never a `0` count claim. */
export async function selfReviewEmptyAuditFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = SELF_REVIEW_COPY[lang];
  await openSelfReview(page, lang, { detail: selfReviewDetail({ audit: false, similarityScore: null }) });

  await expect(page.locator('nts-revised-badge')).toHaveCount(0);
  await expect(page.locator('nts-error-state')).toHaveCount(0);
  await expect(page.locator('#story-review-audit-drift')).toHaveCount(0);

  const panel = await openPanelBody(page);
  await expect(panel.locator('.review-panel__counts')).toHaveCount(0);
  await expect(panel.locator('.review-panel__empty')).toHaveText(C.noChecks);
  await expect(panel.getByRole('table')).toHaveCount(0);

  await panel.locator('.review-panel__disclosure').click();
  await expect(page.locator('#story-review-audit-why')).toContainText(C.noChecks);
  // No highlight was invented from the missing document.
  await expect(page.locator('#seg-p-1-similarity')).toHaveCount(0);
}

/** Task file acceptance 7 (first half): a `null` score with sources shows the verbatim copy and marks nothing. */
export async function selfReviewUnavailableFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = SELF_REVIEW_COPY[lang];
  await openSelfReview(page, lang, { detail: selfReviewDetail({ similarityScore: null, segments: [] }) });

  const panel = await openPanelBody(page);
  const similarity = panel.locator('.review-panel__similarity');
  await expect(similarity).toContainText(C.unavailable);
  await expect(similarity.locator('li')).toHaveCount(0);
  await expect(page.locator('#seg-p-1-similarity')).toHaveCount(0);
  await expect(page.locator('.segment-editor--similarity-warn, .segment-editor--similarity-block')).toHaveCount(0);
}

/** Task file acceptance 7 (second half): with no sources at all, nothing similarity-related is rendered. */
export async function selfReviewNoSourcesFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = SELF_REVIEW_COPY[lang];
  const { server } = await openSelfReview(page, lang, { sources: [] });
  // The page only reports "sources exist" once the gate read has completed (`ResearchStore.loaded()`), which needs both
  // research reads; wait for them plus a bounded quiet window so an absent block is a real absence, not an early
  // assertion made while `sourcesExist` is still false for lack of readiness.
  const researchGets = (suffix: string): number =>
    server.requests.filter((request) => new URL(request.url()).pathname.endsWith(suffix)).length;
  await expect.poll(() => researchGets('/sources')).toBe(1);
  await expect.poll(() => researchGets('/fact-sheet')).toBe(1);

  const panel = await openPanelBody(page);
  await page.waitForTimeout(500);
  await expect(panel.locator('.review-panel__similarity')).toHaveCount(0);
  await expect(panel).not.toContainText(C.similarityHeading);
  await expect(page.locator('#seg-p-1-similarity')).toHaveCount(0);
  // The review half of the panel is unaffected: sources only gate the similarity view.
  await expect(panel.getByRole('table')).toBeVisible();
}

/** Task file acceptance 9: a reorder keeps the highlight and its note on the same text (store remap, end to end). */
export async function selfReviewReorderFlow(page: Page, lang: E2eLang): Promise<void> {
  const C = SELF_REVIEW_COPY[lang];
  const L = EDITOR_COPY[lang];
  const S = EDITOR_STRUCTURE_COPY[lang];
  await openSelfReview(page, lang);

  const before = await paragraphValues(page);
  const moved = before[1];
  expect(moved, 'the seeded story has four paragraphs').toBeDefined();
  await expect(page.locator('#seg-p-1-similarity')).toHaveText(C.similarityWarn);

  await page.getByRole('button', { name: S.moveUpAt(2), exact: true }).click();
  await expect.poll(() => paragraphValues(page)).toEqual([before[1], before[0], before[2], before[3]]);

  // The note follows its text to the new address; the old address keeps none.
  await expect(page.locator('textarea[data-segment="p:0"]')).toHaveValue(moved ?? '');
  await expect(page.locator('#seg-p-0-similarity')).toHaveText(C.similarityWarn);
  await expect(page.locator('textarea[data-segment="p:0"]')).toHaveAttribute('aria-describedby', /seg-p-0-similarity/);
  await expect(page.locator('#seg-p-1-similarity')).toHaveCount(0);
  await expect(segmentGroup(page, L.paragraph(1))).toHaveClass(/segment-editor--similarity-warn/);
  await expect(segmentGroup(page, L.paragraph(2))).not.toHaveClass(/segment-editor--similarity-(?:warn|block)/);

  // ... and the panel row is re-keyed the same way.
  const panel = await openPanelBody(page);
  const similarity = panel.locator('.review-panel__similarity');
  await expect(similarity.getByRole('button', { name: C.segmentShare('1', '21'), exact: true })).toBeVisible();
  await expect(similarity.getByRole('button', { name: C.segmentShare('2', '21'), exact: true })).toHaveCount(0);
}

/** Task file acceptance 11: axe-clean with the score table and the expanded "why" panel visible. */
export async function selfReviewA11yFlow(page: Page, lang: E2eLang): Promise<void> {
  await openSelfReview(page, lang);
  const panel = await openPanelBody(page);
  await panel.locator('.review-panel__disclosure').click();
  await expect(page.locator('#story-review-audit-why')).toBeVisible();
  await expect(panel.getByRole('table')).toBeVisible();
  await expectA11yClean(page);
  // A second scan restricted to the panel proves the table and its row headers are clean on their own.
  await expectA11yClean(page, { include: ['nts-review-panel'] });
}

/** Task file acceptance 11: the panel fits the 360 px viewport with no horizontal overflow. */
export async function selfReviewNarrow360Flow(page: Page, lang: E2eLang): Promise<void> {
  await page.setViewportSize({ width: 360, height: 800 });
  await openSelfReview(page, lang);
  const panel = reviewPanel(page);
  await panel.scrollIntoViewIfNeeded();
  await expect(panelBody(page)).toBeVisible();

  expect(await noHorizontalOverflow(page)).toBe(true);
  const overflow = await panel.evaluate((node) => node.scrollWidth - node.clientWidth);
  expect(overflow, 'the review panel must fit the 360 px viewport').toBeLessThanOrEqual(0);
  const box = await panel.boundingBox();
  expect(box, 'the review panel must be laid out').not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(360);
  await expectA11yClean(page);
}

/**
 * Task file acceptance 10: rendering the panel adds no detail read. The baseline is the two reads the editor already
 * makes — its own detail read and the read-only gate's (10.5.5-T1 hand-over); every panel interaction after that must
 * leave the count alone.
 */
export async function selfReviewNoExtraRequestFlow(page: Page, lang: E2eLang): Promise<void> {
  const { api, server } = await openSelfReview(page, lang);
  await expect.poll(() => api.detailGets.length, { message: 'editor detail read + the read-only gate read' }).toBe(2);
  await expect.poll(() => server.requests.filter((request) => new URL(request.url()).pathname.endsWith('/sources')).length).toBe(1);
  const baseline = api.detailGets.length;

  const panel = await openPanelBody(page);
  await expect(panel.getByRole('table')).toBeVisible();
  await panel.locator('.review-panel__disclosure').click();
  await expect(page.locator('#story-review-audit-why')).toBeVisible();
  // A bounded quiet window (the same settle the editor specs use): a straggling read would surface here.
  await page.waitForTimeout(650);

  expect(api.detailGets.length, 'the panel renders from the already-loaded payload').toBe(baseline);
  expect(await panel.getByRole('rowheader').count()).toBe(7);
}
