import { expect, type Locator, type Page, type Request, type Route } from '@playwright/test';
import autopilotEn from '../../src/assets/i18n/autopilot/en.json' with { type: 'json' };
import autopilotVi from '../../src/assets/i18n/autopilot/vi.json' with { type: 'json' };
import coreEn from '../../src/assets/i18n/core/en.json' with { type: 'json' };
import coreVi from '../../src/assets/i18n/core/vi.json' with { type: 'json' };
import errorsEn from '../../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../../src/assets/i18n/errors/vi.json' with { type: 'json' };
import seriesEn from '../../src/assets/i18n/series/en.json' with { type: 'json' };
import seriesVi from '../../src/assets/i18n/series/vi.json' with { type: 'json' };
import voicesEn from '../../src/assets/i18n/voices/en.json' with { type: 'json' };
import voicesVi from '../../src/assets/i18n/voices/vi.json' with { type: 'json' };

/**
 * Shared stubs for the 10.4.2-T1 Playwright specs (`series-list.spec.ts`, `series-create.spec.ts`,
 * `series-edit-conflict.spec.ts`).
 *
 * Backend-free, the same session-mock approach as `shell.spec.ts` / `episodes-list.spec.ts`: the boot restore's
 * `POST /api/v1/auth/refresh` answers a token pair whose unsigned JWT carries only the claims the client decodes
 * (`sub`/`role`/`amr`), `GET`/`PATCH /api/v1/me` answer a `MeDto` in the spec's UI language and
 * `POST /api/v1/hubs/ticket` answers 503, so the realtime hub never connects (no spec here asserts a realtime state).
 *
 * Copy is read from the shipped bundles (`src/assets/i18n/**`) and never re-typed: a label change in the app fails
 * these specs instead of silently drifting. `SERIES_COPY` is the `series` scope, `VOICES_COPY` the `voices` scope
 * (owner decision 2026-10-05, 10 CR-50), `ERRORS_COPY` the `errors` scope and `CORE_COPY` the root (`core`) scope.
 */

export type E2eLang = 'vi' | 'en';
export type E2eTopic = 'ghost' | 'spiritual' | 'true_crime';
export type E2eAspect = 'landscape' | 'portrait';

/** One `GET /api/v1/series` row (09.4.1 §5). */
export interface SeriesListDto {
  readonly id: string;
  readonly title: string;
  readonly topic: E2eTopic;
  readonly language: E2eLang;
  readonly aspect: E2eAspect;
  readonly voiceId: string;
  readonly styleTemplateId: string;
  readonly autopilotEnabled: boolean;
  readonly archivedAt: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** One `GET /api/v1/series/{id}` entity: the row plus `storyPreset` and the `etag` the next `If-Match` carries. */
export interface SeriesDto extends SeriesListDto {
  readonly storyPreset: string | null;
  readonly etag: string;
}

/** One `GET /api/v1/voices` item (09.4.2-T1 §5). */
export interface VoiceDto {
  readonly id: string;
  readonly displayName: string;
  readonly language: E2eLang;
  readonly gender: string;
  readonly tags: readonly string[];
  readonly previewUrl: string | null;
}

/** One `GET /api/v1/style-templates` item (09.4.2-T1 §5); `name` is a seed slug, never a translated string. */
export interface StyleTemplateDto {
  readonly id: string;
  readonly name: string;
  readonly topicHint: E2eTopic | null;
  readonly previewUrl: string | null;
}

// ---------------------------------------------------------------------------------------------------------------
// Copy, read from the shipped bundles
// ---------------------------------------------------------------------------------------------------------------

export interface SeriesCopy {
  readonly field: { readonly title: string; readonly topic: string; readonly language: string; readonly aspect: string; readonly voice: string; readonly style: string };
  readonly list: { readonly title: string; readonly subtitle: string; readonly empty: string; readonly emptyBody: string; readonly new: string; readonly archived: string; readonly includeArchived: string; readonly loadMore: string };
  readonly step: { readonly topic: string; readonly aspect: string; readonly voice: string; readonly style: string };
  readonly topic: { readonly ghost: string; readonly spiritual: string; readonly trueCrime: string };
  readonly topicHint: { readonly ghost: string; readonly spiritual: string; readonly true_crime: string };
  readonly aspect: { readonly landscape: string; readonly portrait: string };
  readonly language: { readonly vi: string; readonly en: string; readonly locked: string };
  readonly editor: { readonly subtitle: string; readonly editTitle: string; readonly editSubtitle: string; readonly next: string };
  readonly validation: { readonly required: string; readonly titleMax: string };
  readonly save: { readonly saved: string; readonly reloaded: string; readonly conflict: string; readonly changes: string; readonly saving: string };
  readonly archive: { readonly title: string; readonly confirm: string; readonly failed: string; readonly done: string };
  readonly row: { readonly open: string; readonly archive: string; readonly openNamed: string; readonly archiveNamed: string; readonly autopilotOn: string; readonly autopilotOff: string };
  readonly style: Readonly<Record<string, string | { readonly label: string; readonly hint: string }>> & {
    readonly trueCrimeNotice: string;
    readonly clearedByTopic: string;
    readonly inactive: string;
    readonly selected: string;
    readonly empty: string;
  };
  readonly voice: {
    readonly preview: string;
    readonly stop: string;
    readonly previewUnavailable: string;
    readonly previewFailed: string;
    readonly rateFixed: string;
    readonly clearedByLanguage: string;
    readonly empty: string;
    readonly inactive: string;
    readonly gender: Readonly<Record<string, string>>;
  };
  readonly detail: { readonly title: string; readonly subtitle: string; readonly edit: string; readonly notFound: string; readonly notFoundBody: string; readonly backToList: string; readonly autopilot: string };
}

export interface ErrorsCopy {
  readonly validation_failed: string;
  readonly series_archived: string;
  readonly series_conflict: string;
  readonly action: { readonly retry: string };
  readonly unknown: { readonly trace: string };
  readonly conflict: { readonly title: string; readonly reload: string; readonly saveAsNew: string; readonly cancel: string };
}

export interface CoreCopy {
  readonly common: { readonly back: string; readonly loading: string };
  readonly core: {
    readonly ui: {
      readonly confirm: { readonly cancel: string; readonly pending: string };
      readonly dialog: { readonly close: string };
      readonly pageHeader: { readonly back: string; readonly actions: string };
      readonly loadingState: { readonly label: string };
    };
  };
  readonly shell: { readonly nav: { readonly series: string } };
}

/** The `autopilot` scope (`autopilot.topic_locked`, 02.8.3 §6; moved out of `core` on 2026-10-05). */
export interface AutopilotCopy {
  readonly topic_locked: string;
}

export const SERIES_COPY: Readonly<Record<E2eLang, SeriesCopy>> = {
  vi: seriesVi as unknown as SeriesCopy,
  en: seriesEn as unknown as SeriesCopy,
};

export const VOICES_COPY: Readonly<Record<E2eLang, { readonly tag: Readonly<Record<string, string>> }>> = {
  vi: voicesVi as unknown as { readonly tag: Readonly<Record<string, string>> },
  en: voicesEn as unknown as { readonly tag: Readonly<Record<string, string>> },
};

export const ERRORS_COPY: Readonly<Record<E2eLang, ErrorsCopy>> = {
  vi: errorsVi as unknown as ErrorsCopy,
  en: errorsEn as unknown as ErrorsCopy,
};

export const CORE_COPY: Readonly<Record<E2eLang, CoreCopy>> = {
  vi: coreVi as unknown as CoreCopy,
  en: coreEn as unknown as CoreCopy,
};

export const AUTOPILOT_COPY: Readonly<Record<E2eLang, AutopilotCopy>> = {
  vi: autopilotVi as unknown as AutopilotCopy,
  en: autopilotEn as unknown as AutopilotCopy,
};

/** `series.style.<name>.label|.hint` of a seed slug (03 CR-7). */
export function styleCopy(lang: E2eLang, name: string): { readonly label: string; readonly hint: string } {
  const entry = SERIES_COPY[lang].style[name];
  if (entry === undefined || typeof entry === 'string') throw new Error(`series.style.${name} is not a label/hint entry`);
  return entry;
}

// ---------------------------------------------------------------------------------------------------------------
// Seed data
// ---------------------------------------------------------------------------------------------------------------

/** The seven seed templates (03.1.1-T1 `seed/style-templates.v1.json`): five fiction, two `true_crime`. */
export const STYLE_SEED: readonly StyleTemplateDto[] = [
  { id: 't-dark-folklore-ink', name: 'dark-folklore-ink', topicHint: 'ghost', previewUrl: null },
  { id: 't-foggy-cinematic', name: 'foggy-cinematic', topicHint: 'ghost', previewUrl: null },
  { id: 't-vintage-photo-grain', name: 'vintage-photo-grain', topicHint: 'ghost', previewUrl: null },
  { id: 't-watercolor-night', name: 'watercolor-night', topicHint: 'spiritual', previewUrl: null },
  { id: 't-temple-candlelight', name: 'temple-candlelight', topicHint: 'spiritual', previewUrl: null },
  { id: 't-noir-sketch', name: 'noir-sketch', topicHint: 'true_crime', previewUrl: null },
  { id: 't-case-file-illustration', name: 'case-file-illustration', topicHint: 'true_crime', previewUrl: null },
];

/** The two `true_crime` seed slugs whose label/hint the specs assert in both locales. */
export const TRUE_CRIME_STYLE_NAMES = ['noir-sketch', 'case-file-illustration'] as const;

/** A same-origin preview path: `safePreviewUrl` admits root-relative URLs and the CSP `media-src` is `'self'`. */
export const PREVIEW_PATH = '/media/voices/preview.wav';
export const PREVIEW_PATH_2 = '/media/voices/preview-2.wav';

/** Three Vietnamese voices: the first plays a preview, the second has none, the third carries no tag. */
export const VOICES_VI: readonly VoiceDto[] = [
  { id: 'v-vi-1', displayName: 'Mai', language: 'vi', gender: 'female', tags: ['storyteller', 'warm'], previewUrl: PREVIEW_PATH },
  { id: 'v-vi-2', displayName: 'Hùng', language: 'vi', gender: 'male', tags: ['news'], previewUrl: null },
  { id: 'v-vi-3', displayName: 'Linh', language: 'vi', gender: 'neutral', tags: [], previewUrl: PREVIEW_PATH_2 },
];

/** Two English voices with different display names, so a language switch is observable in the rows. */
export const VOICES_EN: readonly VoiceDto[] = [
  { id: 'v-en-1', displayName: 'Ada', language: 'en', gender: 'female', tags: ['natural'], previewUrl: PREVIEW_PATH },
  { id: 'v-en-2', displayName: 'Ben', language: 'en', gender: 'male', tags: ['deep'], previewUrl: null },
];

export const SERIES_ID = '11111111-1111-4111-8111-111111111111';
export const SERIES_ID_2 = '22222222-2222-4222-8222-222222222222';
export const SERIES_LIST_PAGE_SIZE = 25;

export function uuid(n: number): string {
  return `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

export function seriesListItem(n: number, overrides: Partial<SeriesListDto> = {}): SeriesListDto {
  return {
    id: uuid(n),
    title: `Series ${n}`,
    topic: 'ghost',
    language: 'vi',
    aspect: 'landscape',
    voiceId: 'v-vi-1',
    styleTemplateId: 't-dark-folklore-ink',
    autopilotEnabled: false,
    archivedAt: null,
    createdAt: '2026-10-01T12:00:00Z',
    updatedAt: '2026-10-03T12:00:00Z',
    ...overrides,
  };
}

export function seriesDetail(overrides: Partial<SeriesDto> = {}): SeriesDto {
  return { ...seriesListItem(1), storyPreset: null, etag: '"v1"', ...overrides };
}

export function voiceDto(overrides: Partial<VoiceDto> & Pick<VoiceDto, 'id' | 'displayName' | 'language'>): VoiceDto {
  return { gender: 'female', tags: [], previewUrl: null, ...overrides };
}

// ---------------------------------------------------------------------------------------------------------------
// Session and problem bodies
// ---------------------------------------------------------------------------------------------------------------

export function fakeAccessToken(): string {
  const encode = (value: object): string => Buffer.from(JSON.stringify(value)).toString('base64url');
  return `${encode({ alg: 'none', typ: 'JWT' })}.${encode({ sub: 'e2e-owner', role: 'owner', amr: ['pwd', 'otp'] })}.signature`;
}

export function meDto(lang: E2eLang): Record<string, unknown> {
  return {
    id: 'e2e-owner',
    displayName: 'Chủ Sở Hữu',
    email: 'owner@example.test',
    uiLocale: lang,
    role: 'owner',
    mfaEnabled: true,
    lastLoginAt: null,
    features: [],
  };
}

export function problem(status: number, code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { status, code, title: code, traceId: 'NTS-E2E', ...extra };
}

export async function fulfilJson(route: Route, status: number, body: unknown, headers: Record<string, string> = {}): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(body) });
}

export interface SessionStub {
  readonly mePatches: Request[];
}

/**
 * The session mocks every spec here needs: the boot refresh, the profile read (`GET`/`PATCH /me`) and a 503 hub
 * ticket. `lang` is the UI language the profile reports, so the shell re-activates it once `me()` settles.
 */
export async function stubSession(page: Page, lang: E2eLang = 'vi'): Promise<SessionStub> {
  const mePatches: Request[] = [];
  await page.route('**/api/v1/auth/refresh', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ accessToken: fakeAccessToken(), expiresAt: new Date(Date.now() + 3_600_000).toISOString() }),
    }),
  );
  await page.route('**/api/v1/me', async (route) => {
    const request = route.request();
    if (request.method() === 'PATCH') {
      mePatches.push(request);
      const body = request.postDataJSON() as { uiLocale?: E2eLang };
      return fulfilJson(route, 200, { ...meDto(lang), uiLocale: body.uiLocale ?? lang });
    }
    return fulfilJson(route, 200, meDto(lang));
  });
  await page.route('**/api/v1/hubs/ticket', (route) => fulfilJson(route, 503, { status: 503, code: 'unavailable' }));
  return { mePatches };
}

// ---------------------------------------------------------------------------------------------------------------
// Series API stub
// ---------------------------------------------------------------------------------------------------------------

export interface SeriesApiStubOptions {
  /** The whole list the cursor stub pages through (default: empty). */
  readonly items?: readonly SeriesListDto[];
  /** Rows per page (default `SERIES_LIST_PAGE_SIZE`). */
  readonly listPageSize?: number;
  /** The cursor whose page is answered after `listSlowDelayMs` (default `'c2'`), so a second click lands in flight. */
  readonly listSlowCursor?: string;
  readonly listSlowDelayMs?: number;
  /** The entity `GET /series/{id}` answers (default: `seriesDetail()`). */
  readonly detail?: SeriesDto;
  /** The entity `GET /series/{id}` answers once a `PATCH` was refused with 412 (the conflict re-read). An array
   * serves one row per consecutive conflict: the first entry after the first refusal, the next entry after the
   * next one, and the last entry repeats once it is exhausted (another conflict keeps serving it). */
  readonly detailAfterConflict?: SeriesDto | readonly SeriesDto[];
  /** An `If-Match` equal to one of these values is refused with 412 `series_conflict` (default: never). */
  readonly patchStaleEtag?: string | readonly string[];
  /** The `ETag` a successful `PATCH` commits (default `'"v2"'`). */
  readonly patchFreshEtag?: string;
  /** `POST /series` answers this problem instead of 201 (e.g. a 400 `validation_failed`). */
  readonly createFailure?: { readonly status: number; readonly code: string; readonly extra?: Record<string, unknown> };
  /** `POST /series` answers 201 with this id (default `SERIES_ID_2`). */
  readonly createId?: string;
  /** `DELETE /series/{id}` status (default 204; e.g. 500 to fail the archive). */
  readonly deleteStatus?: number;
}

export interface SeriesApiStub {
  readonly listRequests: Request[];
  readonly detailGets: Request[];
  readonly patches: Request[];
  readonly deletes: Request[];
  readonly creates: Request[];
  readonly urlsOf: (requests: readonly Request[]) => URL[];
  readonly patchCount: () => number;
}

/**
 * The `/api/v1/series` endpoints of 09.4.1: a cursor list (`items`/`nextCursor`, `includeArchived` honoured),
 * `GET /{id}` with the `ETag` header and the body `etag`, `PATCH /{id}` honouring `If-Match` (a stale value answers
 * 412 `series_conflict` problem+json, a fresh one 200 `{ etag }` plus the `ETag` header), `DELETE /{id}` and
 * `POST /series` (201 `{ id }` + `ETag`). Every request is recorded.
 */
export async function stubSeriesApi(page: Page, options: SeriesApiStubOptions = {}): Promise<SeriesApiStub> {
  const listRequests: Request[] = [];
  const detailGets: Request[] = [];
  const patches: Request[] = [];
  const deletes: Request[] = [];
  const creates: Request[] = [];

  const items = options.items ?? [];
  const pageSize = options.listPageSize ?? SERIES_LIST_PAGE_SIZE;
  const slowCursor = options.listSlowCursor ?? 'c2';
  const slowDelayMs = options.listSlowDelayMs ?? 400;
  const staleEtags = new Set(options.patchStaleEtag === undefined ? [] : Array.isArray(options.patchStaleEtag) ? options.patchStaleEtag : [options.patchStaleEtag]);
  const freshEtag = options.patchFreshEtag ?? '"v2"';
  const afterConflict = options.detailAfterConflict === undefined ? undefined : Array.isArray(options.detailAfterConflict) ? options.detailAfterConflict : [options.detailAfterConflict];
  let detail = options.detail ?? seriesDetail();
  let conflicts = 0;

  await page.route(/\/api\/v1\/series(\?|$)/, async (route) => {
    const request = route.request();
    if (request.method() === 'POST') {
      creates.push(request);
      const failure = options.createFailure;
      if (failure !== undefined) {
        return fulfilJson(route, failure.status, problem(failure.status, failure.code, failure.extra), { 'Content-Type': 'application/problem+json' });
      }
      const id = options.createId ?? SERIES_ID_2;
      return fulfilJson(route, 201, { id }, { ETag: '"created"' });
    }
    listRequests.push(request);
    const url = new URL(request.url());
    const includeArchived = url.searchParams.get('includeArchived') === 'true';
    const visible = items.filter((item) => includeArchived || item.archivedAt === null);
    const limit = Number(url.searchParams.get('limit') ?? pageSize) || pageSize;
    const cursor = url.searchParams.get('cursor');
    let body: { items: readonly SeriesListDto[]; nextCursor: string | null };
    if (cursor === slowCursor) {
      await page.waitForTimeout(slowDelayMs);
      body = { items: visible.slice(limit), nextCursor: null };
    } else if (cursor === null) {
      body = { items: visible.slice(0, limit), nextCursor: visible.length > limit ? slowCursor : null };
    } else {
      body = { items: [], nextCursor: null };
    }
    await fulfilJson(route, 200, body);
  });

  await page.route(/\/api\/v1\/series\/[0-9a-f-]{36}(\?|$)/, async (route) => {
    const request = route.request();
    const method = request.method();
    if (method === 'GET') {
      detailGets.push(request);
      if (conflicts > 0 && afterConflict !== undefined) {
        detail = afterConflict[Math.min(conflicts - 1, afterConflict.length - 1)]!;
      }
      return fulfilJson(route, 200, detail, { ETag: detail.etag });
    }
    if (method === 'PATCH') {
      patches.push(request);
      const ifMatch = request.headers()['if-match'] ?? null;
      if (ifMatch !== null && staleEtags.has(ifMatch)) {
        conflicts += 1;
        return fulfilJson(route, 412, problem(412, 'series_conflict'), { 'Content-Type': 'application/problem+json' });
      }
      detail = { ...detail, etag: freshEtag };
      return fulfilJson(route, 200, { etag: freshEtag }, { ETag: freshEtag });
    }
    if (method === 'DELETE') {
      deletes.push(request);
      const status = options.deleteStatus ?? 204;
      if (status === 204) return route.fulfill({ status: 204 });
      return fulfilJson(route, status, problem(status, status === 404 ? 'not_found' : 'internal_error'), { 'Content-Type': 'application/problem+json' });
    }
    return route.fallback();
  });

  return {
    listRequests,
    detailGets,
    patches,
    deletes,
    creates,
    urlsOf: (requests) => requests.map((request) => new URL(request.url())),
    patchCount: () => patches.length,
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Catalog API stub (voices, style templates, audio)
// ---------------------------------------------------------------------------------------------------------------

export interface CatalogStubOptions {
  readonly voices?: Readonly<Record<E2eLang, readonly VoiceDto[]>>;
  readonly templates?: readonly StyleTemplateDto[];
  /** A language whose voices read is refused instead of answered (the language-control error mark). */
  readonly voicesFailure?: { readonly language: E2eLang; readonly status: number; readonly code: string } | null;
}

export interface CatalogStub {
  readonly voicesRequests: Request[];
  readonly templateRequests: Request[];
  readonly audioRequests: Request[];
  readonly urlsOf: (requests: readonly Request[]) => URL[];
}

/** A silent 16-bit PCM WAV, long enough that `ended` never fires inside a test. */
export function silentWav(seconds = 30, sampleRate = 8000): Buffer {
  const dataSize = Math.floor(seconds * sampleRate) * 2;
  const buffer = Buffer.alloc(44 + dataSize);
  buffer.write('RIFF', 0, 'ascii');
  buffer.writeUInt32LE(36 + dataSize, 4);
  buffer.write('WAVE', 8, 'ascii');
  buffer.write('fmt ', 12, 'ascii');
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20); // PCM
  buffer.writeUInt16LE(1, 22); // mono
  buffer.writeUInt32LE(sampleRate, 24);
  buffer.writeUInt32LE(sampleRate * 2, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write('data', 36, 'ascii');
  buffer.writeUInt32LE(dataSize, 40);
  return buffer;
}

/**
 * `GET /api/v1/voices?language=` (unpaged `{ items }` with an `ETag`), `GET /api/v1/style-templates?topic=`
 * (filtered exactly like `StyleTopicPolicy`: a `true_crime` topic sees only `true_crime` templates, every other
 * topic sees all but those) and the same-origin preview audio route the CSP `media-src 'self'` allows.
 */
export async function stubCatalog(page: Page, options: CatalogStubOptions = {}): Promise<CatalogStub> {
  const voicesRequests: Request[] = [];
  const templateRequests: Request[] = [];
  const audioRequests: Request[] = [];
  const voices = options.voices ?? { vi: VOICES_VI, en: VOICES_EN };
  const templates = options.templates ?? STYLE_SEED;
  const audio = silentWav();

  await page.route(/\/api\/v1\/voices(\?|$)/, async (route) => {
    voicesRequests.push(route.request());
    const language = (new URL(route.request().url()).searchParams.get('language') ?? 'vi') as E2eLang;
    const failure = options.voicesFailure ?? null;
    if (failure !== null && failure.language === language) {
      return fulfilJson(route, failure.status, problem(failure.status, failure.code, { errors: { language: ['invalid'] } }), {
        'Content-Type': 'application/problem+json',
      });
    }
    return fulfilJson(route, 200, { items: voices[language] ?? [] }, { ETag: `"voices-${language}"` });
  });

  await page.route(/\/api\/v1\/style-templates(\?|$)/, async (route) => {
    templateRequests.push(route.request());
    const topic = (new URL(route.request().url()).searchParams.get('topic') ?? 'ghost') as E2eTopic;
    const matching = templates.filter((template) =>
      topic === 'true_crime' ? template.topicHint === 'true_crime' : template.topicHint !== 'true_crime',
    );
    return fulfilJson(route, 200, { items: matching }, { ETag: `"templates-${topic}"` });
  });

  await page.route('**/media/voices/**', (route) => {
    audioRequests.push(route.request());
    return route.fulfill({ status: 200, contentType: 'audio/wav', body: audio });
  });

  return { voicesRequests, templateRequests, audioRequests, urlsOf: (requests) => requests.map((request) => new URL(request.url())) };
}

// ---------------------------------------------------------------------------------------------------------------
// Small page helpers
// ---------------------------------------------------------------------------------------------------------------

/** No horizontal scroll: the scrolling element's `scrollWidth` never exceeds its `clientWidth`. */
export async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const element = document.scrollingElement ?? document.documentElement;
    return { scrollWidth: element.scrollWidth, clientWidth: element.clientWidth };
  });
  expect(overflow.scrollWidth, `scrollWidth ${overflow.scrollWidth} > clientWidth ${overflow.clientWidth}`).toBeLessThanOrEqual(
    overflow.clientWidth,
  );
}

/** The polite/assertive live region that carries `text` (the CDK `LiveAnnouncer` element, or an in-page one). */
export function liveRegion(page: Page, text: string | RegExp): Locator {
  return page.locator('[aria-live]').filter({ hasText: text });
}
