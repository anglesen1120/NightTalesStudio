import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Series } from '../domain/series.model';
import { SeriesHttpRepository } from './series.http-repository';

const LIST_FIRST = '/api/v1/series?limit=25&includeArchived=false';

function setup(): { repository: SeriesHttpRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), SeriesHttpRepository],
  });
  return { repository: TestBed.inject(SeriesHttpRepository), http: TestBed.inject(HttpTestingController) };
}

function listItemDto(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 's1',
    title: 'Chuyện ma',
    topic: 'ghost',
    language: 'vi',
    aspect: 'landscape',
    voiceId: 'v1',
    styleTemplateId: 't1',
    autopilotEnabled: false,
    archivedAt: null,
    createdAt: '2026-09-01T02:00:00+00:00',
    updatedAt: '2026-09-02T02:00:00+00:00',
    etag: '"se-41"',
    ...over,
  };
}

function seriesDto(over: Record<string, unknown> = {}): Record<string, unknown> {
  return { ...listItemDto(), storyPreset: null, ...over };
}

/** The mapped list row: no `storyPreset`, no per-row `etag`, every other value verbatim. */
const MAPPED_ROW = {
  id: 's1',
  title: 'Chuyện ma',
  topic: 'ghost',
  language: 'vi',
  aspect: 'landscape',
  voiceId: 'v1',
  styleTemplateId: 't1',
  autopilotEnabled: false,
  archivedAt: null,
  createdAt: '2026-09-01T02:00:00+00:00',
  updatedAt: '2026-09-02T02:00:00+00:00',
} as const;

const CREATE_INPUT = {
  title: 'Chuyện ma',
  topic: 'ghost',
  language: 'vi',
  aspect: 'landscape',
  voiceId: 'v1',
  styleTemplateId: 't1',
  storyPreset: null,
} as const;

describe('SeriesHttpRepository', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('MapsDtoToListModel_OmitsStoryPreset', async () => {
    const { repository, http } = setup();
    const result = repository.list({ cursor: null, limit: 25, includeArchived: false });
    http.expectOne(LIST_FIRST).flush({ items: [listItemDto()], nextCursor: null });
    const outcome = await result;
    expect(outcome.ok && outcome.value.items).toEqual([MAPPED_ROW]);
    expect(Object.hasOwn(MAPPED_ROW, 'storyPreset')).toBe(false);
  });

  it('List_KeepsTheWireStoryPresetOut_AndPassesTheCursorThrough', async () => {
    const { repository, http } = setup();
    const first = repository.list({ cursor: null, limit: 25, includeArchived: false });
    const firstRequest = http.expectOne(LIST_FIRST);
    expect(firstRequest.request.method).toBe('GET');
    firstRequest.flush({ items: [listItemDto(), listItemDto({ id: 'bad', topic: 'aliens' })], nextCursor: 'p2' });
    const firstOutcome = await first;
    expect(firstOutcome.ok && firstOutcome.value.items.map((item) => item.id)).toEqual(['s1']);
    expect(firstOutcome.ok && firstOutcome.value.nextCursor).toBe('p2');

    const second = repository.list({ cursor: 'p2', limit: 25, includeArchived: true });
    http.expectOne('/api/v1/series?cursor=p2&limit=25&includeArchived=true').flush({ items: [], nextCursor: null });
    const secondOutcome = await second;
    expect(secondOutcome.ok && secondOutcome.value.nextCursor).toBeNull();
  });

  it('Get_MapsEntityAndKeepsStoryPresetAbsentAsNull', async () => {
    const { repository, http } = setup();
    const result = repository.get('s1');
    http.expectOne('/api/v1/series/s1').flush(seriesDto({ storyPreset: undefined }));
    const outcome = await result;
    expect(outcome.ok && outcome.value).toEqual({ ...MAPPED_ROW, storyPreset: null, etag: '"se-41"' } satisfies Series);
  });

  it('Get_UnrepresentableEntity_MapsToUnknownError', async () => {
    const { repository, http } = setup();
    const result = repository.get('s1');
    http.expectOne('/api/v1/series/s1').flush(seriesDto({ aspect: 'square' }));
    const outcome = await result;
    expect(outcome.ok).toBe(false);
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'unknown_error', status: 0, kind: 'unknown' });
  });

  it('Create_SendsCreateSeriesCommand_AndReadsEtagFromTheHeader', async () => {
    const { repository, http } = setup();
    const result = repository.create(CREATE_INPUT);
    const request = http.expectOne('/api/v1/series');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(CREATE_INPUT);
    request.flush({ id: 's-new' }, { status: 201, statusText: 'Created', headers: { ETag: '"se-1"' } });
    expect(await result).toEqual({ ok: true, value: { id: 's-new', etag: '"se-1"' } });
  });

  it('Create_WithoutUsableEtag_ReportsNullEtag', async () => {
    const { repository, http } = setup();
    const result = repository.create(CREATE_INPUT);
    http.expectOne('/api/v1/series').flush({ id: 's-new' }, { status: 201, statusText: 'Created', headers: { ETag: 'weak' } });
    expect(await result).toEqual({ ok: true, value: { id: 's-new', etag: null } });
  });

  it('Patch_SendsIfMatch_AndReadsEtag', async () => {
    const { repository, http } = setup();
    const result = repository.patch('s1', { title: 'Mới' }, '"se-41"');
    const request = http.expectOne('/api/v1/series/s1');
    expect(request.request.method).toBe('PATCH');
    expect(request.request.headers.get('If-Match')).toBe('"se-41"');
    expect(request.request.body).toEqual({ title: 'Mới' });
    request.flush({ etag: '"se-42"' }, { headers: { ETag: '"se-42"' } });
    expect(await result).toEqual({ ok: true, value: { etag: '"se-42"' } });
  });

  it('Patch_MapsPartialEtagWithoutFabricatingSeries', async () => {
    const { repository, http } = setup();
    const result = repository.patch('s1', { aspect: 'portrait' }, '"se-41"');
    // No `ETag` header: the body member is the documented fallback (09.4.1 §5).
    http.expectOne('/api/v1/series/s1').flush({ etag: '"se-42"' });
    const outcome = await result;
    expect(outcome).toEqual({ ok: true, value: { etag: '"se-42"' } });
    expect(outcome.ok && Object.keys(outcome.value)).toEqual(['etag']);
  });

  it('Patch_BodyCarriesOnlyChangedMembers_NeverLanguage', async () => {
    const { repository, http } = setup();
    const result = repository.patch('s1', { title: 'Mới', styleTemplateId: 't2' }, '"se-41"');
    const request = http.expectOne('/api/v1/series/s1');
    expect(request.request.body).toEqual({ title: 'Mới', styleTemplateId: 't2' });
    expect(Object.hasOwn(request.request.body as object, 'language')).toBe(false);
    expect(Object.hasOwn(request.request.body as object, 'storyPreset')).toBe(false);
    request.flush({ etag: '"se-42"' });
    await result;
  });

  it('Patch_StoryPresetClear_IsSentAsExplicitNull', async () => {
    const { repository, http } = setup();
    const result = repository.patch('s1', { storyPreset: null }, '"se-41"');
    const request = http.expectOne('/api/v1/series/s1');
    expect(request.request.body).toEqual({ storyPreset: null });
    request.flush({ etag: '"se-42"' });
    await result;
  });

  it('Patch_CommittedEtagUnusable_MapsToUnknownError', async () => {
    const { repository, http } = setup();
    const result = repository.patch('s1', { title: 'Mới' }, '"se-41"');
    http.expectOne('/api/v1/series/s1').flush({ etag: 'not a tag' });
    const outcome = await result;
    expect(outcome.ok).toBe(false);
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'unknown_error', status: 0, kind: 'unknown' });
  });

  it('Archive_UsesDelete', async () => {
    const { repository, http } = setup();
    const result = repository.archive('s1');
    const request = http.expectOne('/api/v1/series/s1');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.has('If-Match')).toBe(false);
    request.flush(null, { status: 204, statusText: 'No Content' });
    const outcome = await result;
    expect(outcome.ok).toBe(true);
    expect(outcome.ok && outcome.value).toBeUndefined();
  });

  it('ErrorMapping_SeriesConflict_TopicLocked_Archived', async () => {
    const { repository, http } = setup();
    const conflict = repository.patch('s1', { title: 'Mới' }, '"se-1"');
    http.expectOne('/api/v1/series/s1').flush({ code: 'series_conflict' }, { status: 412, statusText: 'Precondition Failed' });
    const conflictOutcome = await conflict;
    expect(!conflictOutcome.ok && conflictOutcome.error).toMatchObject({ code: 'series_conflict', status: 412, kind: 'conflict' });

    const locked = repository.patch('s1', { topic: 'ghost' }, '"se-1"');
    http.expectOne('/api/v1/series/s1').flush({ code: 'series_topic_locked' }, { status: 409, statusText: 'Conflict' });
    const lockedOutcome = await locked;
    expect(!lockedOutcome.ok && lockedOutcome.error).toMatchObject({
      code: 'series_topic_locked',
      status: 409,
      kind: 'precondition',
    });

    const archived = repository.archive('s1');
    http
      .expectOne('/api/v1/series/s1')
      .flush(JSON.stringify({ code: 'series_archived' }), { status: 409, statusText: 'Conflict' });
    const archivedOutcome = await archived;
    expect(!archivedOutcome.ok && archivedOutcome.error).toMatchObject({
      code: 'series_archived',
      status: 409,
      kind: 'precondition',
    });
  });

  it('List_FieldErrors_AreKeptOnTheMappedError', async () => {
    const { repository, http } = setup();
    const result = repository.list({ cursor: 'tampered', limit: 25, includeArchived: false });
    http
      .expectOne('/api/v1/series?cursor=tampered&limit=25&includeArchived=false')
      .flush({ code: 'validation_failed', errors: { cursor: ['invalid'] } }, { status: 400, statusText: 'Bad Request' });
    const outcome = await result;
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'validation_failed', kind: 'validation', status: 400 });
    expect(!outcome.ok && outcome.error.fieldErrors).toEqual({ cursor: ['invalid'] });
  });

  it('List_RateLimited_KeepsRetryAfterSec', async () => {
    const { repository, http } = setup();
    const result = repository.list({ cursor: null, limit: 25, includeArchived: false });
    http
      .expectOne(LIST_FIRST)
      .flush({ code: 'rate_limited' }, { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '30' } });
    const outcome = await result;
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'rate_limited', status: 429, kind: 'rate_limit', retryAfterSec: 30 });
  });

  it('List_NetworkFailure_IsNetworkError', async () => {
    const { repository, http } = setup();
    const result = repository.list({ cursor: null, limit: 25, includeArchived: false });
    http.expectOne(LIST_FIRST).error(new ProgressEvent('error'));
    const outcome = await result;
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'network_error', status: 0 });
  });
});
