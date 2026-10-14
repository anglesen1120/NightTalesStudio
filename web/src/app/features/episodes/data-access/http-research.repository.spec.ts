import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { USER_ACTION } from '@core/http';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { ResearchCommandIntent } from '../domain/research.repository';
import { HttpResearchRepository } from './http-research.repository';

const EPISODE_ID = '22222222-2222-4222-8222-222222222222';
const SOURCE_ID = '44444444-4444-4444-8444-444444444444';
const SOURCES = `/api/v1/episodes/${EPISODE_ID}/sources`;
const SHEET = `/api/v1/episodes/${EPISODE_ID}/fact-sheet`;

const SOURCE_ROW = {
  id: SOURCE_ID,
  kind: 'url',
  url: 'https://example.com/article',
  registrableDomain: 'example.com',
  noteText: null,
  status: 'fetched',
  errorCode: null,
  errorMessageKey: null,
  httpStatus: 200,
  title: 'An article',
  finalUrl: null,
  contentType: 'text/html',
  textChars: 4200,
  isCited: true,
  createdAt: '2026-10-01T10:00:00Z',
  fetchedAt: '2026-10-01T10:00:05Z',
};

const SHEET_BODY = {
  id: '33333333-3333-4333-8333-333333333333',
  version: 2,
  status: 'draft',
  etag: '"fs-2"',
  facts: [{
    id: 'f1',
    statement: 'Fact 1',
    sources: [{ sourceId: SOURCE_ID, alias: 'S1', title: 'An article', url: 'https://example.com/article', isUserNote: false }],
    sourceOverlap: 0.5,
    origin: 'model',
    confidence: 'high',
  }],
  promptVersion: 'fact-sheet.v1',
  createdAt: '2026-10-01T10:00:00Z',
  approvedAt: null,
  maxFactId: 1,
};

function intent(key = 'key-1'): ResearchCommandIntent {
  return { key, intent: 'source.add', isSettled: false, settle: () => undefined };
}

let http: HttpTestingController;
let repository: HttpResearchRepository;

beforeEach(() => {
  TestBed.configureTestingModule({ providers: [
    provideHttpClient(), provideHttpClientTesting(), HttpResearchRepository,
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  http = TestBed.inject(HttpTestingController);
  repository = TestBed.inject(HttpResearchRepository);
});

afterEach(() => http.verify());

describe('HttpResearchRepository', () => {
  it('lists the sources and marks the read as a user action only when the caller started it', async () => {
    const pending = repository.listSources(EPISODE_ID, { userAction: true });
    const request = http.expectOne(SOURCES);
    expect(request.request.method).toBe('GET');
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush({ items: [SOURCE_ROW], limits: { maxPerEpisode: 10, urlSourcesLast24h: 1, maxUrlSourcesPerDay: 20 } });

    const list = await pending;
    expect(list.items[0]!.status).toBe('fetched');
    expect(list.limits.maxPerEpisode).toBe(10);

    const background = repository.listSources(EPISODE_ID);
    const backgroundRequest = http.expectOne(SOURCES);
    expect(backgroundRequest.request.context.get(USER_ACTION)).toBe(false);
    backgroundRequest.flush({ items: [], limits: { maxPerEpisode: 10, urlSourcesLast24h: 0, maxUrlSourcesPerDay: 20 } });
    await background;
  });

  it('posts a url or a note with one Idempotency-Key', async () => {
    const url = repository.addSource(EPISODE_ID, { url: 'https://example.com/article' }, intent('key-url'));
    const urlRequest = http.expectOne(SOURCES);
    expect(urlRequest.request.method).toBe('POST');
    expect(urlRequest.request.headers.get('Idempotency-Key')).toBe('key-url');
    expect(urlRequest.request.body).toEqual({ url: 'https://example.com/article', noteText: null });
    urlRequest.flush(SOURCE_ROW);
    expect((await url).id).toBe(SOURCE_ID);

    const note = repository.addSource(EPISODE_ID, { noteText: 'Owner note' }, intent('key-note'));
    const noteRequest = http.expectOne(SOURCES);
    expect(noteRequest.request.body).toEqual({ url: null, noteText: 'Owner note' });
    noteRequest.flush({ ...SOURCE_ROW, kind: 'user_note', url: null, noteText: 'Owner note' });
    expect((await note).kind).toBe('user_note');
  });

  it('deletes and retries one row', async () => {
    const removed = repository.removeSource(EPISODE_ID, SOURCE_ID);
    const deleteRequest = http.expectOne(`${SOURCES}/${SOURCE_ID}`);
    expect(deleteRequest.request.method).toBe('DELETE');
    deleteRequest.flush(null);
    await removed;

    const retried = repository.retrySource(EPISODE_ID, SOURCE_ID);
    const retryRequest = http.expectOne(`${SOURCES}/${SOURCE_ID}/retry`);
    expect(retryRequest.request.method).toBe('POST');
    retryRequest.flush({ ...SOURCE_ROW, status: 'pending' });
    expect((await retried).status).toBe('pending');
  });

  it('reads the fact sheet, taking the ETag from the response header', async () => {
    const pending = repository.getFactSheet(EPISODE_ID, { userAction: true });
    const request = http.expectOne(SHEET);
    expect(request.request.method).toBe('GET');
    request.flush(SHEET_BODY, { headers: { ETag: '"fs-9"' } });

    const sheet = await pending;
    expect(sheet?.etag).toBe('"fs-9"');
    expect(sheet?.facts[0]!.sourceIds).toEqual([SOURCE_ID]);
  });

  it('reads an empty body as the normal no-sheet state', async () => {
    const pending = repository.getFactSheet(EPISODE_ID);
    http.expectOne(SHEET).flush(null, { headers: { ETag: '' } });
    expect(await pending).toBeNull();
  });

  it('fails the read with the shared 502 when no ETag describes the document', async () => {
    const pending = repository.getFactSheet(EPISODE_ID);
    http.expectOne(SHEET).flush({ ...SHEET_BODY, etag: '' });
    await expect(pending).rejects.toMatchObject({ status: 502 });
  });

  it('saves the draft with If-Match and nulls the members the store omitted', async () => {
    const pending = repository.saveFactSheet(EPISODE_ID, '"fs-2"', [
      { id: 'f1', statement: 'Fact 1', sourceIds: [SOURCE_ID] },
      { statement: 'Added', sourceIds: [SOURCE_ID], confidence: 'low' },
    ]);
    const request = http.expectOne(SHEET);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('If-Match')).toBe('"fs-2"');
    expect(request.request.body).toEqual({ facts: [
      { id: 'f1', statement: 'Fact 1', sourceIds: [SOURCE_ID], confidence: null },
      { id: null, statement: 'Added', sourceIds: [SOURCE_ID], confidence: 'low' },
    ] });
    request.flush({ id: SHEET_BODY.id, version: 3, status: 'draft', etag: '"fs-3"', factCount: 2, createdAt: SHEET_BODY.createdAt }, { headers: { ETag: '"fs-3"' } });

    expect(await pending).toEqual({ id: SHEET_BODY.id, version: 3, status: 'draft', etag: '"fs-3"' });
  });

  it('reports a stale save as the conflict code and status', async () => {
    const pending = repository.saveFactSheet(EPISODE_ID, '"fs-1"', [{ statement: 'Fact 1', sourceIds: [] }]);
    http.expectOne(SHEET).flush({ code: 'fact_sheet_conflict', errors: [], traceId: 'trace-1' }, { status: 412, statusText: 'Precondition Failed' });

    await expect(pending).rejects.toMatchObject({ code: 'fact_sheet_conflict', status: 412, kind: 'conflict' });
  });

  it('approves with If-Match and reports the refused approval as its own code', async () => {
    const approved = repository.approveFactSheet(EPISODE_ID, '"fs-2"');
    const approveRequest = http.expectOne(`${SHEET}/approve`);
    expect(approveRequest.request.method).toBe('POST');
    expect(approveRequest.request.headers.get('If-Match')).toBe('"fs-2"');
    approveRequest.flush({ id: SHEET_BODY.id, version: 2, status: 'approved', etag: '"fs-2"', factCount: 1, createdAt: SHEET_BODY.createdAt }, { headers: { ETag: '"fs-2"' } });
    expect((await approved).status).toBe('approved');

    const refused = repository.approveFactSheet(EPISODE_ID, '"fs-2"');
    http.expectOne(`${SHEET}/approve`).flush({ code: 'story_sources_required', errors: [], traceId: 'trace-2' }, { status: 409, statusText: 'Conflict' });
    await expect(refused).rejects.toMatchObject({ code: 'story_sources_required', status: 409 });
  });

  it('generates with one Idempotency-Key and carries Retry-After into the failure', async () => {
    const generated = repository.generateFactSheet(EPISODE_ID, intent('key-gen'));
    const generateRequest = http.expectOne(`${SHEET}/generate`);
    expect(generateRequest.request.method).toBe('POST');
    expect(generateRequest.request.headers.get('Idempotency-Key')).toBe('key-gen');
    generateRequest.flush({ runId: 'run-1', factSheetId: null, status: 'running' }, { status: 202, statusText: 'Accepted' });
    expect((await generated).runId).toBe('run-1');

    const limited = repository.generateFactSheet(EPISODE_ID, intent('key-gen-2'));
    http.expectOne(`${SHEET}/generate`).flush(
      { code: 'spend_cap_reached', errors: [], traceId: 'trace-3' },
      { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '42' } },
    );
    await expect(limited).rejects.toMatchObject({ code: 'spend_cap_reached', status: 429, retryAfterSec: 42 });
  });

  it('reports a missing-sheet approval as fact_sheet_not_found', async () => {
    const pending = repository.approveFactSheet(EPISODE_ID, '"fs-2"');
    http.expectOne(`${SHEET}/approve`).flush({ code: 'fact_sheet_not_found', errors: [], traceId: 'trace-4' }, { status: 404, statusText: 'Not Found' });
    await expect(pending).rejects.toMatchObject({ code: 'fact_sheet_not_found', status: 404, kind: 'not_found' });
  });
});
