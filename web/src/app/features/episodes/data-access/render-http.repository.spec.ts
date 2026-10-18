import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { IDEMPOTENCY_INTENT, IdempotencyIntent, IdempotencyKeys, USER_ACTION } from '@core/http';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { RenderHttpRepository, renderJobIdFromLocation } from './render-http.repository';

const EPISODE = '22222222-2222-4222-8222-222222222222';
const JOB = 'render-job-7';
const RENDER_URL = `/api/v1/episodes/${EPISODE}/render`;
const CANCEL_URL = `/api/v1/render-jobs/${JOB}`;
const ACCEPTED = { renderJobId: JOB, status: 'queued' };

function setup() {
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideHttpClient(),
      provideHttpClientTesting(),
      RenderHttpRepository,
      { provide: APP_ERROR_DEV_MODE, useValue: false },
    ],
  });
  return { repository: TestBed.inject(RenderHttpRepository), http: TestBed.inject(HttpTestingController) };
}

function intent(key = '11111111-1111-4111-8111-111111111111'): IdempotencyIntent {
  return new IdempotencyIntent(key, 'episode.render');
}

describe('renderJobIdFromLocation', () => {
  it.each([
    ['/render-jobs/render-job-7', JOB],
    ['/api/v1/render-jobs/render-job-7', JOB],
    ['https://api.example.test/api/v1/render-jobs/render-job-7', JOB],
    ['/api/v1/render-jobs/render-job-7?trace=1#x', JOB],
  ])('reads %s as %s', (location, expected) => {
    expect(renderJobIdFromLocation(location)).toBe(expected);
  });

  it.each([
    [null],
    [''],
    ['/api/v1/episodes/22222222-2222-4222-8222-222222222222'],
    ['/render-jobs'],
    ['/render-jobs/'],
    ['/other/render-job-7'],
    ['/render-jobs/render job 7'],
    [`/render-jobs/${'j'.repeat(129)}`],
  ])('refuses %s', (location) => {
    expect(renderJobIdFromLocation(location)).toBeNull();
  });
});

describe('RenderHttpRepository', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('render_SendsIfMatchTheIntentKeyAndUserIntent_AnswersTheLocationJobId', async () => {
    const { repository, http } = setup();
    const held = intent();
    const answer = repository.requestRender(EPISODE, '"ep-3"', held);
    const request = http.expectOne(RENDER_URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    expect(request.request.headers.get('If-Match')).toBe('"ep-3"');
    expect(request.request.headers.get('Idempotency-Key')).toBe(held.key);
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    // The very object the caller holds goes into the context, so the interceptor and the caller share one key.
    expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBe(held);
    request.flush(ACCEPTED, { status: 202, statusText: 'Accepted', headers: { Location: `/api/v1/render-jobs/${JOB}` } });
    expect(await answer).toBe(JOB);
  });

  it('render_KeyFromIdempotencyKeys_IsANonEmptyUuidWithinTheApiLimit', async () => {
    const { repository, http } = setup();
    const answer = repository.requestRender(EPISODE, '"ep-3"', new IdempotencyKeys().begin('episode.render'));
    const request = http.expectOne(RENDER_URL);
    const key = request.request.headers.get('Idempotency-Key');
    expect(key).toMatch(/^[0-9a-f-]{36}$/i);
    expect(key).not.toBe('');
    expect(key?.length ?? 129).toBeLessThanOrEqual(128);
    request.flush(ACCEPTED, { status: 202, statusText: 'Accepted', headers: { Location: `/render-jobs/${JOB}` } });
    await answer;
  });

  it('render_ReplaysTheSameKeyForTheSameIntent', async () => {
    const { repository, http } = setup();
    const held = intent();
    const first = repository.requestRender(EPISODE, '"ep-3"', held);
    http.expectOne(RENDER_URL).flush(ACCEPTED, { status: 202, statusText: 'Accepted', headers: { Location: `/render-jobs/${JOB}` } });
    await first;

    // A retry of the same click passes the same intent again: one key reaches the wire, so the server replays.
    const second = repository.requestRender(EPISODE, '"ep-3"', held);
    const replay = http.expectOne(RENDER_URL);
    expect(replay.request.headers.get('Idempotency-Key')).toBe(held.key);
    expect(replay.request.context.get(IDEMPOTENCY_INTENT)).toBe(held);
    replay.flush(ACCEPTED, { status: 202, statusText: 'Accepted', headers: { Location: `/render-jobs/${JOB}` } });
    expect(await second).toBe(JOB);
  });

  it.each([
    [null],
    ['/api/v1/episodes/22222222-2222-4222-8222-222222222222'],
    ['/render-jobs/'],
  ] as const)('render_Location_%s_AnswersNoJobIdAsAnAcceptedCommand', async (location) => {
    const { repository, http } = setup();
    const answer = repository.requestRender(EPISODE, '"ep-3"', intent());
    http.expectOne(RENDER_URL).flush(ACCEPTED, {
      status: 202,
      statusText: 'Accepted',
      ...(location === null ? {} : { headers: { Location: location } }),
    });
    // The 202 was accepted — the render is queued and the credit reserved — so the adapter answers no id rather than a
    // fabricated one, and never a refusal: the caller reads the authoritative episode (PS1/C6).
    await expect(answer).resolves.toBeNull();
  });

  it('render_WhateverTheBodySays_TheJobIdComesFromTheLocationHeaderOnly', async () => {
    const { repository, http } = setup();
    const answer = repository.requestRender(EPISODE, '"ep-3"', intent());
    http.expectOne(RENDER_URL).flush({ renderJobId: 'a-different-job', status: 'queued' }, { status: 202, statusText: 'Accepted', headers: { Location: `/render-jobs/${JOB}` } });
    expect(await answer).toBe(JOB);
  });

  it('render_Refusal_BecomesTheMappedAppError', async () => {
    const { repository, http } = setup();
    const answer = repository.requestRender(EPISODE, '"ep-3"', intent());
    http.expectOne(RENDER_URL).flush(
      { code: 'episode_invalid_state', traceId: 'trace-1' },
      { status: 409, statusText: 'Conflict' },
    );
    await expect(answer).rejects.toMatchObject({ code: 'episode_invalid_state', status: 409, kind: 'precondition', traceId: 'trace-1' });
  });

  it('render_LostResponse_IsTheNetworkError', async () => {
    const { repository, http } = setup();
    const answer = repository.requestRender(EPISODE, '"ep-3"', intent());
    http.expectOne(RENDER_URL).error(new ProgressEvent('error'));
    await expect(answer).rejects.toMatchObject({ code: 'network_error', status: 0, kind: 'network' });
  });

  it('cancel_SendsNoIfMatchAndNoIdempotencyKey', async () => {
    const { repository, http } = setup();
    const answer = repository.cancelRender(JOB);
    const request = http.expectOne(CANCEL_URL);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.has('If-Match')).toBe(false);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBeNull();
    expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush(null, { status: 204, statusText: 'No Content' });
    await expect(answer).resolves.toBeUndefined();
  });

  it('cancel_RepeatDelete_IsAcceptedToo', async () => {
    const { repository, http } = setup();
    const first = repository.cancelRender(JOB);
    http.expectOne(CANCEL_URL).flush(null, { status: 204, statusText: 'No Content' });
    await first;

    const repeat = repository.cancelRender(JOB);
    const replay = http.expectOne(CANCEL_URL);
    expect(replay.request.headers.has('Idempotency-Key')).toBe(false);
    replay.flush(null, { status: 204, statusText: 'No Content' });
    await expect(repeat).resolves.toBeUndefined();
  });

  it('cancel_ForeignJob_IsTheMappedNotFound', async () => {
    const { repository, http } = setup();
    const answer = repository.cancelRender(JOB);
    http.expectOne(CANCEL_URL).flush({ code: 'not_found' }, { status: 404, statusText: 'Not Found' });
    await expect(answer).rejects.toMatchObject({ code: 'not_found', status: 404, kind: 'not_found' });
  });
});
