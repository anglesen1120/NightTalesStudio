import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { USER_ACTION } from '@core/http';
import { SessionApi } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { SeriesOptionsHttpRepository } from './series-options-http.repository';

function series(id: string, title: string, archivedAt: string | null = null): Record<string, unknown> {
  return {
    id,
    title,
    archivedAt,
    aspect: 'vertical',
    autopilotEnabled: false,
    createdAt: '2026-10-01T00:00:00Z',
    etag: '"se-1"',
    language: 'vi',
    styleTemplateId: 's',
    topic: 'fiction',
    updatedAt: '2026-10-01T00:00:00Z',
    voiceId: 'v',
  };
}

function setup(): { repository: SeriesOptionsHttpRepository; http: HttpTestingController } {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SessionApi, useValue: new FakeSessionApi() },
      SeriesOptionsHttpRepository,
    ],
  });
  return { repository: TestBed.inject(SeriesOptionsHttpRepository), http: TestBed.inject(HttpTestingController) };
}

describe('SeriesOptionsHttpRepository', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('list_IssuesExactlyOneFirstPageRequest_WithLimit100_NoIncludeArchived_NoCursor', async () => {
    const { repository, http } = setup();
    const result = repository.list();
    const req = http.expectOne('/api/v1/series?limit=100');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.has('includeArchived')).toBe(false);
    expect(req.request.params.has('cursor')).toBe(false);
    expect(req.request.context.get(USER_ACTION)).toBe(true);
    req.flush({ items: [series('a', 'Alpha')], nextCursor: null });
    await result;
    http.verify();
  });

  it('list_MapsToIdAndTitleOnly_AndDropsArchivedRows', async () => {
    const { repository, http } = setup();
    const result = repository.list();
    http.expectOne('/api/v1/series?limit=100').flush({
      items: [series('a', 'Alpha'), series('b', 'Old', '2026-09-01T00:00:00Z'), series('c', 'Gamma')],
      nextCursor: null,
    });
    expect(await result).toEqual({
      items: [
        { id: 'a', title: 'Alpha' },
        { id: 'c', title: 'Gamma' },
      ],
      bounded: false,
    });
  });

  it('list_WithNextCursor_IsBoundedAndNeverRequestsASecondPage', async () => {
    const { repository, http } = setup();
    const result = repository.list();
    http.expectOne('/api/v1/series?limit=100').flush({ items: [series('a', 'Alpha')], nextCursor: 'more' });
    expect((await result).bounded).toBe(true);
    http.expectNone((r) => r.params.has('cursor'));
  });

  it('list_HttpFailure_Rejects', async () => {
    const { repository, http } = setup();
    const result = repository.list();
    http.expectOne('/api/v1/series?limit=100').flush({}, { status: 500, statusText: 'Server Error' });
    await expect(result).rejects.toMatchObject({ status: 500 });
  });
});
