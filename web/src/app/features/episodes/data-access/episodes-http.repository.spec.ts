import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { IDEMPOTENCY_INTENT, IdempotencyIntent, USER_ACTION } from '@core/http';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { EpisodesHttpRepository } from './episodes-http.repository';

const EPISODE = '22222222-2222-4222-8222-222222222222';
const SERIES = '11111111-1111-4111-8111-111111111111';
const INPUT = { title: null, brief: 'A midnight lantern', lengthTier: 'standard' as const };
const DETAIL = {
  id: EPISODE, seriesId: SERIES, status: 'story_generating', etag: '"ep-1"', brief: INPUT.brief,
  title: null, lengthTier: 'standard', currentStory: null, generation: null, nextActions: [],
  finalVideo: null, publications: null, renderJob: null,
};
function setup() {
  TestBed.configureTestingModule({ providers: [
    ...ZONELESS_TEST_PROVIDERS, provideHttpClient(), provideHttpClientTesting(), EpisodesHttpRepository,
  ] });
  return { repository: TestBed.inject(EpisodesHttpRepository), http: TestBed.inject(HttpTestingController) };
}

describe('EpisodesHttpRepository new methods', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('create_JsonUuidScalar_CombinesIdAndEtagWithoutAnIdempotencyKey', async () => {
    const { repository, http } = setup();
    const response = repository.create(SERIES, INPUT);
    const request = http.expectOne(`/api/v1/series/${SERIES}/episodes`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(INPUT);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBeNull();
    request.flush(EPISODE, { status: 201, statusText: 'Created', headers: { ETag: '"ep-1"', Location: `/api/v1/episodes/${EPISODE}` } });
    expect(await response).toEqual({ id: EPISODE, etag: '"ep-1"' });
  });

  it.each([{ id: EPISODE }, 'not-a-uuid', 7])('create_InvalidScalar_%j_IsRejectedAsAppError', async (body) => {
    const { repository, http } = setup();
    const response = repository.create(SERIES, INPUT);
    http.expectOne(`/api/v1/series/${SERIES}/episodes`).flush(body, { status: 201, statusText: 'Created' });
    await expect(response).rejects.toMatchObject({ code: 'unknown_error', kind: 'unknown' });
  });

  it('create_MissingEtag_IsNull', async () => {
    const { repository, http } = setup();
    const response = repository.create(SERIES, INPUT);
    http.expectOne(`/api/v1/series/${SERIES}/episodes`).flush(EPISODE, { status: 201, statusText: 'Created' });
    expect(await response).toEqual({ id: EPISODE, etag: null });
  });

  it('generate_SendsIntentKeyHeaderAndUserActionContext', async () => {
    const { repository, http } = setup();
    const intent = new IdempotencyIntent('33333333-3333-4333-8333-333333333333', 'story.generate');
    const response = repository.generateStory(EPISODE, intent);
    const request = http.expectOne(`/api/v1/episodes/${EPISODE}/story/generate`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.key);
    expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBe(intent);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush({ generationRunId: 'run', status: 'queued', stage: 'queued' }, { status: 202, statusText: 'Accepted' });
    await response;
  });

  it.each([false, true])('generate_StructuralIntent_SettledInitially_%s_PreservesKeyAndLinksSettlement', async (isSettled) => {
    const { repository, http } = setup();
    const intent = {
      key: '33333333-3333-4333-8333-333333333333',
      intent: 'story.generate',
      isSettled,
      settle: vi.fn(function (this: { isSettled: boolean }) { this.isSettled = true; }),
    };
    const response = repository.generateStory(EPISODE, intent);
    const request = http.expectOne(`/api/v1/episodes/${EPISODE}/story/generate`);
    const contextIntent = request.request.context.get(IDEMPOTENCY_INTENT);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.key);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    expect(contextIntent).toBeInstanceOf(IdempotencyIntent);
    expect(contextIntent?.key).toBe(intent.key);
    expect(contextIntent?.intent).toBe(intent.intent);
    expect(contextIntent?.isSettled).toBe(isSettled);
    expect(intent.isSettled).toBe(isSettled);
    expect(intent.settle).not.toHaveBeenCalled();

    contextIntent?.settle();
    expect(contextIntent?.isSettled).toBe(true);
    expect(intent.isSettled).toBe(true);
    expect(intent.settle).toHaveBeenCalledOnce();
    request.flush({ generationRunId: 'run', status: 'queued', stage: 'queued' }, { status: 202, statusText: 'Accepted' });
    await response;
  });

  it('get_BackgroundDefault_CarriesNoUserAction', async () => {
    const { repository, http } = setup();
    const response = repository.get(EPISODE);
    const request = http.expectOne(`/api/v1/episodes/${EPISODE}`);
    expect(request.request.context.get(USER_ACTION)).toBe(false);
    request.flush(DETAIL);
    expect(await response).toMatchObject({ id: EPISODE, hasCurrentStory: false, generation: null });
  });

  it('get_ExplicitUserAction_IsMarked', async () => {
    const { repository, http } = setup();
    const response = repository.get(EPISODE, { userAction: true });
    const request = http.expectOne(`/api/v1/episodes/${EPISODE}`);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush(DETAIL);
    await response;
  });

  it('generate_429_PreservesRetryAfterAndTraceIdInAppError', async () => {
    const { repository, http } = setup();
    const response = repository.generateStory(EPISODE, new IdempotencyIntent('key', 'story.generate'));
    http.expectOne(`/api/v1/episodes/${EPISODE}/story/generate`).flush(
      { code: 'story_generation_limit', traceId: 'trace-123' },
      { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '9' } },
    );
    await expect(response).rejects.toMatchObject({ code: 'story_generation_limit', status: 429, retryAfterSec: 9, traceId: 'trace-123' });
  });

  it('create_NetworkFailure_IsMappedAndNeverAutomaticallyRetried', async () => {
    const { repository, http } = setup();
    const response = repository.create(SERIES, INPUT);
    http.expectOne(`/api/v1/series/${SERIES}/episodes`).error(new ProgressEvent('error'));
    await expect(response).rejects.toMatchObject({ code: 'network_error', status: 0 });
    http.expectNone(`/api/v1/series/${SERIES}/episodes`);
  });
});
