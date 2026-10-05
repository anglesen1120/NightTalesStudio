import type { Locator, Page, Request } from '@playwright/test';
import type { CurrentStoryDto } from '../../src/app/shared/api/models/current-story-dto';
import type { EpisodeDetailDto } from '../../src/app/shared/api/models/episode-detail-dto';
import type { EpisodeListItemDto } from '../../src/app/shared/api/models/episode-list-item-dto';
import type { StoryVersionDto } from '../../src/app/shared/api/models/story-version-dto';
import errorsEn from '../../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../../src/assets/i18n/errors/vi.json' with { type: 'json' };
import { expectA11yClean } from './axe';
import { expect } from './csp.fixture';
import { episodeDetail, EPISODE_ID, SERIES_ID, stubEpisodes, watchPrivacy } from './episodes-stubs';
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
  vi: { title: 'Tiêu đề', hook: 'Mở đầu', paragraph: (n: number) => `Đoạn ${n}`, ending: 'Kết', description: 'Mô tả', scenes: 'Cảnh', scene: (n: number) => `Cảnh ${n}`, add: 'Thêm cảnh', remove: 'Xoá cảnh', choose: 'Chọn đoạn', save: 'Lưu phiên bản', saved: 'Đã lưu', dirty: 'Có thay đổi chưa lưu', limit: 'Mỗi đoạn cần 1–2 cảnh', reopen: 'Duyệt lại', approve: 'Duyệt kịch bản', invalid: errorsVi.episode_invalid_state },
  en: { title: 'Title', hook: 'Hook', paragraph: (n: number) => `Paragraph ${n}`, ending: 'Ending', description: 'Description', scenes: 'Scenes', scene: (n: number) => `Scene ${n}`, add: 'Add scene', remove: 'Remove scene', choose: 'Choose segment', save: 'Save version', saved: 'Saved', dirty: 'Unsaved changes', limit: 'Each paragraph needs 1–2 scenes', reopen: 'Reopen story', approve: 'Approve story', invalid: errorsEn.episode_invalid_state },
} as const;

export function storySnapshot(content: StoryWire = STORY_CONTENT, versionId = STORED_VERSION_ID, version = 1): CurrentStoryDto {
  return {
    versionId, version, etag: `"sv-${versionId}"`, source: 'manual', wordCount: 123,
    content, body: [content.hook.text, ...content.paragraphs.map((paragraph) => paragraph.text), content.ending.text].join('\n'),
    keywords: [...content.hook.keywords, ...content.paragraphs.flatMap((paragraph) => paragraph.keywords), ...content.ending.keywords],
    approvedStoryVersionId: null, auditReport: null, factSheetId: null, qualityHints: [], review: null, similarityScore: null, status: 'story_review',
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
