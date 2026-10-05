import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { USER_ACTION } from '@core/http';
import { SessionApi } from '@core/session';
import { describe, expect, it, afterEach } from 'vitest';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EpisodesHttpRepository } from './episodes-http.repository';

// QA (10.5.1-T1): the episode list HTTP adapter is the only file that touches the wire, yet it shipped without a spec.
// These probe the acceptance-delta item "scalar filter/query serialization" and the D5 limit rule directly on the
// generated client: exactly one scalar value per param, no array params, `limit` never above 100, `cursor` omitted on
// the first page, `USER_ACTION` present on every list call (WR11), and the server envelope kept verbatim.

const SERIES_ID = '11111111-1111-4111-8111-111111111111';
const EPISODES_URL = '/api/v1/episodes';

/** A wire row; `status` is a raw string so an unknown wire value can be scripted. */
function row(id: string, status: string): Record<string, unknown> {
  return {
    id,
    seriesId: SERIES_ID,
    title: `Episode ${id}`,
    status,
    lengthTier: 'standard',
    createdAt: '2026-10-01T10:00:00Z',
    updatedAt: '2026-10-02T10:00:00Z',
    etag: `"ep-${id}"`,
  };
}

function setup(): { repository: EpisodesHttpRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      EpisodesHttpRepository,
    ],
  });
  return { repository: TestBed.inject(EpisodesHttpRepository), http: TestBed.inject(HttpTestingController) };
}

describe('EpisodesHttpRepository (QA)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('list_SerializesEveryFilterAsExactlyOneScalarQueryParam', async () => {
    const { repository, http } = setup();
    const result = repository.list({ status: 'story_review', seriesId: SERIES_ID, cursor: 'opaque-1', limit: 20 });
    const req = http.expectOne((r) => r.url === EPISODES_URL);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.keys().sort()).toEqual(['cursor', 'limit', 'seriesId', 'status']);
    expect(req.request.params.getAll('status')).toEqual(['story_review']);
    expect(req.request.params.getAll('seriesId')).toEqual([SERIES_ID]);
    expect(req.request.params.getAll('cursor')).toEqual(['opaque-1']);
    expect(req.request.params.getAll('limit')).toEqual(['20']);
    req.flush({ items: [row('1', 'draft')], nextCursor: null });
    expect((await result).items.map((item) => item.id)).toEqual(['1']);
  });

  it('list_OmitsTheCursorParam_OnTheFirstPageAndForAnEmptyCursor', async () => {
    const { repository, http } = setup();

    const firstPage = repository.list({ limit: 20 });
    const first = http.expectOne((r) => r.url === EPISODES_URL);
    expect(first.request.params.has('cursor')).toBe(false);
    first.flush({ items: [], nextCursor: null });
    await firstPage;

    const emptyCursor = repository.list({ limit: 20, cursor: '' });
    const second = http.expectOne((r) => r.url === EPISODES_URL);
    expect(second.request.params.has('cursor')).toBe(false);
    second.flush({ items: [], nextCursor: null });
    await emptyCursor;
  });

  it('list_ClampsAnOversizedLimitTo100_NeverSendingLimit400', async () => {
    const { repository, http } = setup();
    const result = repository.list({ limit: 500 });
    const req = http.expectOne((r) => r.url === EPISODES_URL);
    expect(req.request.params.getAll('limit')).toEqual(['100']);
    req.flush({ items: [], nextCursor: null });
    await result;
  });

  it('list_MarksTheRequestAsAUserAction_WR11', async () => {
    const { repository, http } = setup();
    const result = repository.list({ limit: 20 });
    const req = http.expectOne((r) => r.url === EPISODES_URL);
    expect(req.request.context.get(USER_ACTION)).toBe(true);
    req.flush({ items: [], nextCursor: null });
    await result;
  });

  it('list_KeepsTheServerEnvelopeVerbatim_AndDropsRowsWithAnUnknownStatus', async () => {
    const { repository, http } = setup();
    const result = repository.list({ limit: 20 });
    http.expectOne((r) => r.url === EPISODES_URL).flush({
      items: [row('1', 'draft'), row('2', 'story_generated'), row('3', 'ready')],
      nextCursor: 'c2',
    });
    const page = await result;
    expect(page.items.map((item) => item.id)).toEqual(['1', '3']);
    expect(page.nextCursor).toBe('c2');
  });

  it('list_PassesAnOpaqueCursorVerbatim_WithoutDoubleEncoding', async () => {
    const { repository, http } = setup();
    const cursor = 'c1+/=abc def';
    const result = repository.list({ limit: 20, cursor });
    const req = http.expectOne((r) => r.url === EPISODES_URL);
    expect(req.request.params.get('cursor')).toBe(cursor);
    // Encoded exactly once: no raw space and no percent-escaping of the already-encoded form.
    const encoded = req.request.urlWithParams;
    expect(encoded).toContain('cursor=c1%2B%2F%3Dabc%20def');
    expect(encoded).not.toContain(' ');
    req.flush({ items: [], nextCursor: null });
    await result;
  });

  it('list_KeepsAnEmptyStringNextCursor_SoTheStoreCanTreatItAsTheEnd', async () => {
    const { repository, http } = setup();
    const result = repository.list({ limit: 20 });
    http.expectOne((r) => r.url === EPISODES_URL).flush({ items: [row('1', 'draft')], nextCursor: '' });
    expect((await result).nextCursor).toBe('');
  });
});
