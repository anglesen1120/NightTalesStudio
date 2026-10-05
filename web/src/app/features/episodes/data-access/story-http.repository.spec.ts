import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IDEMPOTENCY_INTENT, USER_ACTION } from '@core/http/http-context-tokens';
import { storySnapshot } from '../testing/fake-story.repository';
import { StoryHttpRepository } from './story-http.repository';

const ID = '22222222-2222-4222-8222-222222222222';
describe('StoryHttpRepository', () => {
  let repo: StoryHttpRepository;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), StoryHttpRepository, { provide: APP_ERROR_DEV_MODE, useValue: false }] });
    repo = TestBed.inject(StoryHttpRepository); http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => { http.verify(); TestBed.resetTestingModule(); });
  it('save_SendsRawStoryV1ExactIfMatchAndUserAction_NoIdempotencyKey', async () => {
    const snapshot = storySnapshot();
    const answer = repo.save(ID, snapshot.etag, snapshot.content, { userAction: true });
    const request = http.expectOne(`/api/v1/episodes/${ID}/story`);
    expect(request.request.method).toBe('PUT'); expect(request.request.headers.get('If-Match')).toBe(snapshot.etag);
    expect(request.request.body).toEqual(snapshot.content); expect(request.request.body).not.toHaveProperty('content');
    expect(request.request.context.get(USER_ACTION)).toBe(true); expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBeNull();
    request.flush(snapshot, { headers: { ETag: '"sv-header"' } });
    expect((await answer).etag).toBe('"sv-header"');
  });
  it('save_NoHeader_UsesBodyEtag', async () => {
    const snapshot = storySnapshot(); const answer = repo.save(ID, snapshot.etag, snapshot.content, { userAction: true });
    http.expectOne(`/api/v1/episodes/${ID}/story`).flush(snapshot);
    expect((await answer).etag).toBe(snapshot.etag);
  });
  it.each([[400, 'story_invalid_content'], [409, 'episode_invalid_state'], [412, 'story_version_conflict'], [428, 'if_match_required']])('save_%s_MapsAppError', async (status, code) => {
    const snapshot = storySnapshot(); const answer = repo.save(ID, snapshot.etag, snapshot.content, { userAction: true });
    const rejected = expect(answer).rejects.toMatchObject({ status, code });
    http.expectOne(`/api/v1/episodes/${ID}/story`).flush({ code, detail: 'Never rendered' }, { status: Number(status), statusText: 'Rejected' });
    await rejected;
  });
  it('save_MalformedSuccess_RejectsTypedError', async () => {
    const snapshot = storySnapshot(); const answer = repo.save(ID, snapshot.etag, snapshot.content, { userAction: true });
    const rejected = expect(answer).rejects.toMatchObject({ status: 502, code: 'internal_error' });
    http.expectOne(`/api/v1/episodes/${ID}/story`).flush({ ...snapshot, content: null }); await rejected;
  });
});
