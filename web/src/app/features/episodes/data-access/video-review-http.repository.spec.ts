import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IDEMPOTENCY_INTENT, IdempotencyIntent, IdempotencyKeys, USER_ACTION } from '@core/http';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { VideoReviewHttpRepository } from './video-review-http.repository';

const EPISODE = '22222222-2222-4222-8222-222222222222';
const URL_ENDPOINT = `/api/v1/episodes/${EPISODE}/video-url`;
const APPROVE_ENDPOINT = `/api/v1/episodes/${EPISODE}/video/approve`;
const REJECT_ENDPOINT = `/api/v1/episodes/${EPISODE}/video/reject`;
const ETAG = '"ep-7"';
const SIGNED = { url: 'https://media.example.test/ep-7.mp4?sig=abc', expiresAt: '2026-10-19T10:15:00.000+00:00' };
const STATE = { id: EPISODE, status: 'ready', etag: '"ep-8"' };

function setup() {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideHttpClient(),
      provideHttpClientTesting(),
      VideoReviewHttpRepository,
      { provide: APP_ERROR_DEV_MODE, useValue: false },
    ],
  });
  return { repository: TestBed.inject(VideoReviewHttpRepository), http: TestBed.inject(HttpTestingController) };
}

function intent(key = '11111111-1111-4111-8111-111111111111'): IdempotencyIntent {
  return new IdempotencyIntent(key, 'episode.video.reject');
}

describe('VideoReviewHttpRepository', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('url_AutomaticRead_IsAGetWithNoUserIntentAndMapsTheTwoContractMembersOnly', async () => {
    const { repository, http } = setup();
    const answer = repository.getVideoUrl(EPISODE);
    const request = http.expectOne(URL_ENDPOINT);
    expect(request.request.method).toBe('GET');
    // The automatic read carries no USER_ACTION: an idle tab must not extend its own session (WR11/D7).
    expect(request.request.context.get(USER_ACTION)).toBe(false);
    expect(request.request.headers.has('If-Match')).toBe(false);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush({ ...SIGNED, extra: 'ignored' });
    await expect(answer).resolves.toEqual({ url: SIGNED.url, expiresAt: SIGNED.expiresAt });
  });

  it('url_ManualRetry_CarriesUserIntent', async () => {
    const { repository, http } = setup();
    const answer = repository.getVideoUrl(EPISODE, { userAction: true });
    const request = http.expectOne(URL_ENDPOINT);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush(SIGNED);
    await answer;
  });

  it('url_NoFinalAsset_IsTheMappedNotFound', async () => {
    const { repository, http } = setup();
    const answer = repository.getVideoUrl(EPISODE);
    http.expectOne(URL_ENDPOINT).flush({ code: 'not_found' }, { status: 404, statusText: 'Not Found' });
    await expect(answer).rejects.toMatchObject({ code: 'not_found', status: 404, kind: 'not_found' });
  });

  it('url_DegradedStorage_IsTheMappedServiceUnavailableWithItsRetryAfter', async () => {
    const { repository, http } = setup();
    const answer = repository.getVideoUrl(EPISODE);
    http.expectOne(URL_ENDPOINT).flush(
      { code: 'service_unavailable' },
      { status: 503, statusText: 'Service Unavailable', headers: { 'Retry-After': '30' } },
    );
    await expect(answer).rejects.toMatchObject({ code: 'service_unavailable', status: 503, retryAfterSec: 30 });
  });

  it('url_LostResponse_IsTheNetworkError', async () => {
    const { repository, http } = setup();
    const answer = repository.getVideoUrl(EPISODE);
    http.expectOne(URL_ENDPOINT).error(new ProgressEvent('error'));
    await expect(answer).rejects.toMatchObject({ code: 'network_error', status: 0, kind: 'network' });
  });

  it('approve_SendsIfMatchAndUserIntentWithNoIdempotencyKey_AnswersStateOnly', async () => {
    const { repository, http } = setup();
    const answer = repository.approveVideo(EPISODE, ETAG, 'typo in scene 3');
    const request = http.expectOne(APPROVE_ENDPOINT);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('If-Match')).toBe(ETAG);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(request.request.body).toEqual({ note: 'typo in scene 3' });
    request.flush({ ...STATE, warnings: ['ignored'] });
    // Only the two state members travel on: no body field beyond them is read.
    await expect(answer).resolves.toEqual({ status: STATE.status, etag: STATE.etag });
  });

  it('approve_BlankNote_SendsNoBodyFieldAtAll', async () => {
    const { repository, http } = setup();
    const answer = repository.approveVideo(EPISODE, ETAG);
    const request = http.expectOne(APPROVE_ENDPOINT);
    expect(request.request.body).toBeNull();
    request.flush(STATE);
    await answer;
  });

  it('approve_StaleEtag_IsTheMappedConflict', async () => {
    const { repository, http } = setup();
    const answer = repository.approveVideo(EPISODE, ETAG);
    http.expectOne(APPROVE_ENDPOINT).flush({ code: 'episode_conflict' }, { status: 412, statusText: 'Precondition Failed' });
    await expect(answer).rejects.toMatchObject({ code: 'episode_conflict', status: 412, kind: 'conflict' });
  });

  it('reject_SendsIfMatchTheIntentKeyAndTheIntentContext_StoryAnswersAcceptedFalse', async () => {
    const { repository, http } = setup();
    const held = intent();
    const answer = repository.rejectVideo(EPISODE, ETAG, 'voice is wrong', 'story', held);
    const request = http.expectOne(REJECT_ENDPOINT);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('If-Match')).toBe(ETAG);
    expect(request.request.headers.get('Idempotency-Key')).toBe(held.key);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    // The very object the caller holds goes into the context, so the interceptor and the caller share one key.
    expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBe(held);
    expect(request.request.body).toEqual({ reason: 'voice is wrong', target: 'story' });
    request.flush({ id: EPISODE, status: 'story_review', etag: '"ep-8"' });
    await expect(answer).resolves.toEqual({ status: 'story_review', etag: '"ep-8"', accepted: false });
  });

  it('reject_ReRender202_IsAcceptedWithNoInventedJobId', async () => {
    const { repository, http } = setup();
    const held = intent();
    const answer = repository.rejectVideo(EPISODE, ETAG, 'images are wrong', 're_render', held);
    const request = http.expectOne(REJECT_ENDPOINT);
    expect(request.request.body).toEqual({ reason: 'images are wrong', target: 're_render' });
    // A 202 with no Location, and a body that claims a job id the route never returns.
    request.flush(
      { id: EPISODE, status: 'story_approved', etag: '"ep-9"', renderJobId: 'invented' },
      { status: 202, statusText: 'Accepted' },
    );
    await expect(answer).resolves.toEqual({ status: 'story_approved', etag: '"ep-9"', accepted: true });
  });

  it('reject_RetryOfOneClick_ReplaysTheSameIntent', async () => {
    const { repository, http } = setup();
    const held = intent();
    const first = repository.rejectVideo(EPISODE, ETAG, 'voice is wrong', 'story', held);
    http.expectOne(REJECT_ENDPOINT).error(new ProgressEvent('error'));
    await expect(first).rejects.toMatchObject({ code: 'network_error' });

    const second = repository.rejectVideo(EPISODE, ETAG, 'voice is wrong', 'story', held);
    const replay = http.expectOne(REJECT_ENDPOINT);
    expect(replay.request.headers.get('Idempotency-Key')).toBe(held.key);
    expect(replay.request.context.get(IDEMPOTENCY_INTENT)).toBe(held);
    replay.flush({ id: EPISODE, status: 'story_review', etag: '"ep-8"' });
    await expect(second).resolves.toMatchObject({ accepted: false });
  });

  it('reject_KeyFromIdempotencyKeys_IsANonEmptyUuidWithinTheApiLimit', async () => {
    const { repository, http } = setup();
    const answer = repository.rejectVideo(EPISODE, ETAG, 'voice is wrong', 'story', new IdempotencyKeys().begin('episode.video.reject'));
    const request = http.expectOne(REJECT_ENDPOINT);
    const key = request.request.headers.get('Idempotency-Key');
    expect(key).toMatch(/^[0-9a-f-]{36}$/i);
    expect(key?.length ?? 129).toBeLessThanOrEqual(128);
    request.flush({ id: EPISODE, status: 'story_review', etag: '"ep-8"' });
    await answer;
  });

  it('reject_Refusal_BecomesTheMappedAppError', async () => {
    const { repository, http } = setup();
    const answer = repository.rejectVideo(EPISODE, ETAG, 'voice is wrong', 'story', intent());
    http.expectOne(REJECT_ENDPOINT).flush(
      { code: 'episode_invalid_state', traceId: 'trace-1' },
      { status: 409, statusText: 'Conflict' },
    );
    await expect(answer).rejects.toMatchObject({ code: 'episode_invalid_state', status: 409, kind: 'precondition', traceId: 'trace-1' });
  });
});
