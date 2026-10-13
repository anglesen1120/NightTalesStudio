import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IDEMPOTENCY_INTENT, USER_ACTION } from '@core/http/http-context-tokens';
import { HttpStoryVersionRepository } from './http-story-version.repository';

const ID = '22222222-2222-4222-8222-222222222222';
const VERSION = 'v1';
const CONTENT = {
  title: 'The lantern', hook: { text: 'A light appeared.', keywords: ['lantern'], fact_refs: [] },
  paragraphs: [{ beat: 'beat-0', text: 'Paragraph 1 waits.', keywords: [], fact_refs: [] }],
  ending: { text: 'The light vanished.', keywords: [], fact_refs: [] },
  description: 'A midnight story', tags: ['night'], content_warnings: [], structure: null,
};
const SUMMARY = {
  versionId: VERSION, version: '2', source: 'revised', createdAt: '2026-10-01T10:00:00Z',
  wordCount: '120', etag: '"sv-v1"', similarityScore: null, approved: true, isCurrent: true,
};
const DETAIL = {
  versionId: VERSION, version: '2', source: 'revised', createdAt: '2026-10-01T10:00:00Z', wordCount: '120',
  etag: '"sv-v1"', similarityScore: null, auditReport: null, body: 'Plain body.', content: CONTENT,
  createdBy: 'owner@example.com', factSheetId: null, keywords: ['lantern'], qualityHints: [],
};

describe('HttpStoryVersionRepository', () => {
  let repo: HttpStoryVersionRepository;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), HttpStoryVersionRepository, { provide: APP_ERROR_DEV_MODE, useValue: false }] });
    repo = TestBed.inject(HttpStoryVersionRepository);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => { http.verify(); TestBed.resetTestingModule(); });

  it('list_RequestsTheVersionRoute_WithUserAction_AndMapsSummaries', async () => {
    const answer = repo.list(ID, { userAction: true });
    const request = http.expectOne(`/api/v1/episodes/${ID}/story/versions`);
    expect(request.request.method).toBe('GET');
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBeNull();
    request.flush([SUMMARY]);
    expect(await answer).toEqual([{
      versionId: VERSION, version: 2, source: 'revised', createdAt: '2026-10-01T10:00:00Z',
      wordCount: 120, etag: '"sv-v1"', similarityScore: null, approved: true, isCurrent: true,
    }]);
  });

  it('list_WithoutUserAction_LeavesTheContextUnset', () => {
    void repo.list(ID);
    const request = http.expectOne(`/api/v1/episodes/${ID}/story/versions`);
    expect(request.request.context.get(USER_ACTION)).toBe(false);
    request.flush([]);
  });

  it('detail_RequestsTheVersionRoute_AndMapsTheDetail', async () => {
    const answer = repo.detail(ID, VERSION, { userAction: true });
    const request = http.expectOne(`/api/v1/episodes/${ID}/story/versions/${VERSION}`);
    expect(request.request.method).toBe('GET');
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush(DETAIL);
    const detail = await answer;
    expect(detail.body).toBe('Plain body.');
    expect(detail.content).toEqual(CONTENT);
    expect(detail.createdBy).toBe('owner@example.com');
  });

  it('list_404_MapsToAppError', async () => {
    const answer = repo.list(ID);
    const rejected = expect(answer).rejects.toMatchObject({ status: 404, code: 'not_found' });
    http.expectOne(`/api/v1/episodes/${ID}/story/versions`).flush({}, { status: 404, statusText: 'Not Found' });
    await rejected;
  });

  it('detail_MalformedSuccess_RejectsTyped502', async () => {
    const answer = repo.detail(ID, VERSION);
    const rejected = expect(answer).rejects.toMatchObject({ status: 502, code: 'internal_error' });
    http.expectOne(`/api/v1/episodes/${ID}/story/versions/${VERSION}`).flush({ ...DETAIL, body: null });
    await rejected;
  });
});
