import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IdempotencyIntent, USER_ACTION } from '@core/http';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { HttpSourceFinderRepository } from './http-source-finder.repository';

const EPISODE_ID = '22222222-2222-4222-8222-222222222222';
const CANDIDATE_ID = '55555555-5555-4555-8555-555555555555';
const CANDIDATES = `/api/v1/episodes/${EPISODE_ID}/sources/candidates`;
const FIND = `/api/v1/episodes/${EPISODE_ID}/sources/find`;

const CANDIDATE_ROW = {
  id: CANDIDATE_ID,
  url: 'https://example.com/article',
  title: 'An article',
  siteName: 'example.com',
  rank: 1,
  status: 'proposed',
  sourceId: null,
  createdAt: '2026-10-01T10:00:00Z',
};

function intent(key = 'key-1'): IdempotencyIntent {
  return new IdempotencyIntent(key, 'find_sources');
}

let http: HttpTestingController;
let repository: HttpSourceFinderRepository;

beforeEach(() => {
  TestBed.configureTestingModule({ providers: [
    provideHttpClient(), provideHttpClientTesting(), HttpSourceFinderRepository,
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ] });
  http = TestBed.inject(HttpTestingController);
  repository = TestBed.inject(HttpSourceFinderRepository);
});

afterEach(() => http.verify());

describe('HttpSourceFinderRepository', () => {
  it('starts the search with one Idempotency-Key and the query the caller has', async () => {
    const pending = repository.find(EPISODE_ID, 'bronze age hoard', intent('key-find'));
    const request = http.expectOne(FIND);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-find');
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(request.request.body).toEqual({ query: 'bronze age hoard' });
    request.flush({ generationRunId: 'run-1' }, { status: 202, statusText: 'Accepted' });
    await expect(pending).resolves.toBeUndefined();
  });

  it('sends a null query when the caller has none', async () => {
    const pending = repository.find(EPISODE_ID, null, intent('key-find'));
    const request = http.expectOne(FIND);
    expect(request.request.body).toEqual({ query: null });
    request.flush({ generationRunId: 'run-1' }, { status: 202, statusText: 'Accepted' });
    await pending;
  });

  it('reports a running search as find_sources_in_progress and a reused key as idempotency_key_reused', async () => {
    const running = repository.find(EPISODE_ID, null, intent('key-2'));
    http.expectOne(FIND).flush({ code: 'find_sources_in_progress', errors: [], traceId: 'trace-1' }, { status: 409, statusText: 'Conflict' });
    await expect(running).rejects.toMatchObject({ code: 'find_sources_in_progress', status: 409, kind: 'precondition' });

    const reused = repository.find(EPISODE_ID, null, intent('key-3'));
    http.expectOne(FIND).flush({ code: 'idempotency_key_reused', errors: [], traceId: 'trace-2' }, { status: 422, statusText: 'Unprocessable Content' });
    await expect(reused).rejects.toMatchObject({ code: 'idempotency_key_reused', status: 422, kind: 'client_bug' });
  });

  it('carries Retry-After into a spend cap refusal', async () => {
    const pending = repository.find(EPISODE_ID, null, intent('key-4'));
    http.expectOne(FIND).flush(
      { code: 'spend_cap_reached', errors: [], traceId: 'trace-3' },
      { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '30' } },
    );
    await expect(pending).rejects.toMatchObject({ code: 'spend_cap_reached', status: 429, retryAfterSec: 30 });
  });

  it('lists the candidates for the episode, in the server order, as a user action only when asked', async () => {
    const pending = repository.candidates(EPISODE_ID, { userAction: true });
    const request = http.expectOne(CANDIDATES);
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.get('Idempotency-Key')).toBeNull();
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush({ items: [{ ...CANDIDATE_ROW, rank: 2, id: 'a' }, CANDIDATE_ROW] });

    const rows = await pending;
    expect(rows.map((row) => row.id)).toEqual(['a', CANDIDATE_ID]);
    expect(rows[1]).toEqual({ ...CANDIDATE_ROW, sourceId: null });

    const background = repository.candidates(EPISODE_ID);
    const backgroundRequest = http.expectOne(CANDIDATES);
    expect(backgroundRequest.request.context.get(USER_ACTION)).toBe(false);
    backgroundRequest.flush({ items: [] });
    await background;
  });

  it('fails an unreadable candidate list as the shared 502', async () => {
    const pending = repository.candidates(EPISODE_ID);
    http.expectOne(CANDIDATES).flush({ items: [{ ...CANDIDATE_ROW, id: '' }] });
    await expect(pending).rejects.toMatchObject({ code: 'internal_error', status: 502, kind: 'server' });
  });

  it('adds a candidate without an idempotency key and reports a row another tab already handled', async () => {
    const pending = repository.add(EPISODE_ID, CANDIDATE_ID);
    const request = http.expectOne(`${CANDIDATES}/${CANDIDATE_ID}/add`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBeNull();
    request.flush({ id: 'source-1' }, { status: 201, statusText: 'Created' });
    await expect(pending).resolves.toBeUndefined();

    const refused = repository.add(EPISODE_ID, CANDIDATE_ID);
    http.expectOne(`${CANDIDATES}/${CANDIDATE_ID}/add`).flush(
      { code: 'source_candidate_not_proposed', errors: [], traceId: 'trace-4' },
      { status: 409, statusText: 'Conflict' },
    );
    await expect(refused).rejects.toMatchObject({ code: 'source_candidate_not_proposed', status: 409, kind: 'precondition' });
  });

  it('dismisses a candidate and reads the 204 as done', async () => {
    const pending = repository.dismiss(EPISODE_ID, CANDIDATE_ID);
    const request = http.expectOne(`${CANDIDATES}/${CANDIDATE_ID}/dismiss`);
    expect(request.request.method).toBe('POST');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await expect(pending).resolves.toBeUndefined();
  });

  it('reports a disabled flag as feature_disabled on every route of the block', async () => {
    const find = repository.find(EPISODE_ID, null, intent('key-5'));
    http.expectOne(FIND).flush({ code: 'feature_disabled', errors: [], traceId: 'trace-5' }, { status: 404, statusText: 'Not Found' });
    await expect(find).rejects.toMatchObject({ code: 'feature_disabled', status: 404, kind: 'feature_disabled' });

    const list = repository.candidates(EPISODE_ID);
    http.expectOne(CANDIDATES).flush({ code: 'feature_disabled', errors: [], traceId: 'trace-6' }, { status: 404, statusText: 'Not Found' });
    await expect(list).rejects.toMatchObject({ code: 'feature_disabled', status: 404, kind: 'feature_disabled' });
  });
});
