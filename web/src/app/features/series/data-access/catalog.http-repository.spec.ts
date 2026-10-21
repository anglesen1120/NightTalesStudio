import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { CatalogHttpRepository } from './catalog.http-repository';

const VOICES = '/api/v1/voices?language=vi';

function setup(): { repository: CatalogHttpRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), CatalogHttpRepository],
  });
  return { repository: TestBed.inject(CatalogHttpRepository), http: TestBed.inject(HttpTestingController) };
}

function dto(over: Record<string, unknown> = {}): Record<string, unknown> {
  return { id: 'v1', displayName: 'Minh', gender: 'male', language: 'vi', previewUrl: null, tags: ['warm'], ...over };
}

function templateDto(over: Record<string, unknown> = {}): Record<string, unknown> {
  return { id: 't1', name: 'dark-folklore-ink', topicHint: 'ghost', previewUrl: null, ...over };
}

describe('CatalogHttpRepository', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('CatalogRepository_Voices_MapsDtoToModel', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush({ items: [dto({ previewUrl: 'https://cdn.example.com/p/v1.mp3', tags: ['warm', 'calm'] })] });
    expect(await result).toEqual({
      ok: true,
      value: {
        kind: 'ok',
        etag: null,
        voices: [
          {
            id: 'v1',
            displayName: 'Minh',
            language: 'vi',
            gender: 'male',
            tags: ['warm', 'calm'],
            previewUrl: 'https://cdn.example.com/p/v1.mp3',
          },
        ],
      },
    });
  });

  it('CatalogRepository_Voices_DropsUnknownTags', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush({ items: [dto({ tags: ['warm', 'robotic', '', 'central'] })] });
    const outcome = await result;
    expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.voices[0]?.tags : null).toEqual(['warm', 'central']);
  });

  it('CatalogRepository_Voices_UnsafePreviewUrl_BecomesNull', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush({
      items: [
        dto({ id: 'a', previewUrl: 'javascript:alert(1)' }),
        dto({ id: 'b', previewUrl: '//evil.example/a.mp3' }),
        dto({ id: 'c', previewUrl: '/media/c.mp3' }),
      ],
    });
    const outcome = await result;
    const voices = outcome.ok && outcome.value.kind === 'ok' ? outcome.value.voices : [];
    expect(voices.map((v) => v.previewUrl)).toEqual([null, null, '/media/c.mp3']);
  });

  it('CatalogRepository_Voices_DropsVoiceWithUnknownLanguage', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush({ items: [dto({ language: 'fr' }), dto({ id: 'ok' })] });
    const outcome = await result;
    expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.voices.map((v) => v.id) : null).toEqual(['ok']);
  });

  it('CatalogRepository_Voices_ReadsEtagFromResponse', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush({ items: [dto()] }, { headers: { ETag: '"abc123"' } });
    const outcome = await result;
    expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.etag : undefined).toBe('"abc123"');
  });

  it('CatalogRepository_Voices_MalformedEtag_IsNull', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush({ items: [dto()] }, { headers: { ETag: 'no quotes' } });
    const outcome = await result;
    expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.etag : undefined).toBeNull();
  });

  it('CatalogRepository_Voices_SameEtagAsHeld_IsNotModified', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi', '"abc123"');
    http.expectOne(VOICES).flush({ items: [dto()] }, { headers: { ETag: '"abc123"' } });
    expect(await result).toEqual({ ok: true, value: { kind: 'notModified' } });
  });

  it('CatalogRepository_Voices_Status304_IsNotModified', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi', '"abc123"');
    http.expectOne(VOICES).flush(null, { status: 304, statusText: 'Not Modified' });
    expect(await result).toEqual({ ok: true, value: { kind: 'notModified' } });
  });

  it('CatalogRepository_Voices_Error_MapsToAppError_ThroughAppErrorMapper', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).flush(
      { code: 'rate_limited' },
      { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '30' } },
    );
    const outcome = await result;
    expect(outcome.ok).toBe(false);
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'rate_limited', status: 429, kind: 'rate_limit', retryAfterSec: 30 });
  });

  it('CatalogRepository_Voices_NetworkFailure_IsNetworkError', async () => {
    const { repository, http } = setup();
    const result = repository.voices('vi');
    http.expectOne(VOICES).error(new ProgressEvent('error'));
    const outcome = await result;
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'network_error', status: 0 });
  });

  it('CatalogRepository_Voices_IssuesOneRequest', async () => {
    const { repository, http } = setup();
    const result = repository.voices('en');
    const req = http.expectOne('/api/v1/voices?language=en');
    expect(req.request.method).toBe('GET');
    req.flush({ items: [] });
    await result;
    http.expectNone(() => true);
  });

  it('CatalogRepository_StyleTemplates_IssuesOneRequestWithTheTopic', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('true_crime');
    const req = http.expectOne('/api/v1/style-templates?topic=true_crime');
    expect(req.request.method).toBe('GET');
    req.flush({ items: [] });
    await result;
    http.expectNone(() => true);
  });

  it('CatalogRepository_StyleTemplates_MapsDtoToModel', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('ghost');
    http.expectOne('/api/v1/style-templates?topic=ghost').flush({
      items: [templateDto({ previewUrl: 'https://cdn.example.com/p/ink.png' }), templateDto({ id: 't2', name: 'foggy-cinematic', topicHint: null })],
    });
    expect(await result).toEqual({
      ok: true,
      value: {
        kind: 'ok',
        etag: null,
        templates: [
          { id: 't1', name: 'dark-folklore-ink', topicHint: 'ghost', previewUrl: 'https://cdn.example.com/p/ink.png' },
          { id: 't2', name: 'foggy-cinematic', topicHint: null, previewUrl: null },
        ],
      },
    });
  });

  it('CatalogRepository_StyleTemplates_DropsRowsThatCannotBecomeAKey', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('ghost');
    http.expectOne('/api/v1/style-templates?topic=ghost').flush({
      items: [
        templateDto({ id: 'bad-name', name: '../secret' }),
        templateDto({ id: 'bad-hint', name: 'noir-sketch', topicHint: 'aliens' }),
        templateDto({ id: 'ok', name: 'noir-sketch', topicHint: 'true_crime' }),
      ],
    });
    const outcome = await result;
    expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.templates.map((template) => template.id) : null).toEqual(['ok']);
  });

  it('CatalogRepository_StyleTemplates_UnsafePreviewUrl_BecomesNull', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('ghost');
    http.expectOne('/api/v1/style-templates?topic=ghost').flush({
      items: [
        templateDto({ id: 'a', previewUrl: 'javascript:alert(1)' }),
        templateDto({ id: 'b', previewUrl: '//evil.example/a.png' }),
        templateDto({ id: 'c', previewUrl: '/media/c.png' }),
      ],
    });
    const outcome = await result;
    const templates = outcome.ok && outcome.value.kind === 'ok' ? outcome.value.templates : [];
    expect(templates.map((template) => template.previewUrl)).toEqual([null, null, '/media/c.png']);
  });

  it('CatalogRepository_StyleTemplates_ReadsEtagFromResponse_AndHonoursNotModified', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('ghost', '"abc123"');
    http.expectOne('/api/v1/style-templates?topic=ghost').flush({ items: [templateDto()] }, { headers: { ETag: '"abc123"' } });
    expect(await result).toEqual({ ok: true, value: { kind: 'notModified' } });

    const fresh = repository.styleTemplates('ghost');
    http.expectOne('/api/v1/style-templates?topic=ghost').flush({ items: [templateDto()] }, { headers: { ETag: '"abc123"' } });
    const outcome = await fresh;
    expect(outcome.ok && outcome.value.kind === 'ok' ? outcome.value.etag : undefined).toBe('"abc123"');
  });

  it('CatalogRepository_StyleTemplates_Status304_IsNotModified', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('ghost', '"abc123"');
    http.expectOne('/api/v1/style-templates?topic=ghost').flush(null, { status: 304, statusText: 'Not Modified' });
    expect(await result).toEqual({ ok: true, value: { kind: 'notModified' } });
  });

  it('CatalogRepository_StyleTemplates_Error_MapsToAppError_ThroughAppErrorMapper', async () => {
    const { repository, http } = setup();
    const result = repository.styleTemplates('ghost');
    http
      .expectOne('/api/v1/style-templates?topic=ghost')
      .flush({ code: 'validation_failed' }, { status: 400, statusText: 'Bad Request' });
    const outcome = await result;
    expect(!outcome.ok && outcome.error).toMatchObject({ code: 'validation_failed', status: 400, kind: 'validation' });
  });
});
