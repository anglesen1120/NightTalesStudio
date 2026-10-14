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

  it('approve_NoAttestation_OmitsTheBodyFieldAndMapsTheOutcome', async () => {
    const answer = repo.approve(ID, '"sv-1"');
    const request = http.expectOne(`/api/v1/episodes/${ID}/story/approve`);
    expect(request.request.method).toBe('POST'); expect(request.request.headers.get('If-Match')).toBe('"sv-1"');
    // Never `{ attestation: null }`: the field is absent when the user was shown no block.
    expect(request.request.body).toBeNull();
    expect(request.request.context.get(USER_ACTION)).toBe(true); expect(request.request.context.get(IDEMPOTENCY_INTENT)).toBeNull();
    // The wire's `storyVersionId` is nothing we read: a response that omits it must not fail a committed approve.
    request.flush({ approvedAt: '2026-10-15T09:00:00Z', episodeId: ID, etag: '"sv-approved"', status: 'story_approved' });
    const outcome = await answer;
    expect(outcome).toEqual({ status: 'story_approved', etag: '"sv-approved"' });
    expect(outcome).not.toHaveProperty('storyVersionId');
  });
  it('approve_SendsTheTickTheUserGave_AndPrefersTheResponseEtagHeader', async () => {
    const answer = repo.approve(ID, '"sv-1"', { accepted: true, textVersion: '2026-10-01.1' });
    const request = http.expectOne(`/api/v1/episodes/${ID}/story/approve`);
    expect(request.request.body).toEqual({ attestation: { accepted: true, textVersion: '2026-10-01.1' } });
    // Both sides carry an ETag here: the header describes the state the response installed, so it wins (`mapStorySnapshot`).
    request.flush({ approvedAt: 'x', episodeId: ID, etag: '"sv-body"', status: 'story_approved' }, { headers: { ETag: '"sv-header"' } });
    expect((await answer).etag).toBe('"sv-header"');
  });
  it('approve_NoEtagHeader_UsesTheBodyEtag', async () => {
    const answer = repo.approve(ID, '"sv-1"');
    http.expectOne(`/api/v1/episodes/${ID}/story/approve`).flush({ episodeId: ID, etag: '"sv-body"', status: 'story_approved' });
    expect(await answer).toEqual({ status: 'story_approved', etag: '"sv-body"' });
  });
  it('approve_429_ExposesRetryAfter', async () => {
    const answer = repo.approve(ID, '"sv-1"');
    const rejected = expect(answer).rejects.toMatchObject({ status: 429, code: 'rate_limited', retryAfterSec: 30 });
    http.expectOne(`/api/v1/episodes/${ID}/story/approve`).flush({ code: 'rate_limited' }, { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '30' } });
    await rejected;
  });
  it.each([[400, 'story_attestation_required'], [409, 'story_moderated'], [409, 'keywords_flagged'], [412, 'story_version_conflict'], [428, 'if_match_required']])('approve_%s_%s_MapsAppError', async (status, code) => {
    const answer = repo.approve(ID, '"sv-1"');
    const rejected = expect(answer).rejects.toMatchObject({ status, code });
    http.expectOne(`/api/v1/episodes/${ID}/story/approve`).flush({ code, detail: 'Never rendered' }, { status: Number(status), statusText: 'Rejected' });
    await rejected;
  });
  it('approve_MalformedSuccess_RejectsTypedError', async () => {
    const answer = repo.approve(ID, '"sv-1"');
    const rejected = expect(answer).rejects.toMatchObject({ status: 502, code: 'internal_error' });
    http.expectOne(`/api/v1/episodes/${ID}/story/approve`).flush({ episodeId: ID, etag: '"sv-1"', status: '' });
    await rejected;
  });
  it('reopen_SendsIfMatchWithNoBody_AndReadsTheOutcome', async () => {
    const answer = repo.reopen(ID, '"sv-approved"');
    const request = http.expectOne(`/api/v1/episodes/${ID}/story/reopen`);
    expect(request.request.method).toBe('POST'); expect(request.request.headers.get('If-Match')).toBe('"sv-approved"');
    expect(request.request.body).toBeNull(); expect(request.request.context.get(USER_ACTION)).toBe(true);
    request.flush({ episodeId: ID, etag: '"sv-reopened"', status: 'story_review' });
    expect(await answer).toEqual({ status: 'story_review', etag: '"sv-reopened"' });
  });
  it.each([[409, 'episode_invalid_state'], [412, 'story_version_conflict']])('reopen_%s_%s_MapsAppError', async (status, code) => {
    const answer = repo.reopen(ID, '"sv-approved"');
    const rejected = expect(answer).rejects.toMatchObject({ status, code });
    http.expectOne(`/api/v1/episodes/${ID}/story/reopen`).flush({ code, detail: 'Never rendered' }, { status: Number(status), statusText: 'Rejected' });
    await rejected;
  });
  it('reopen_MalformedSuccess_RejectsTypedError', async () => {
    const answer = repo.reopen(ID, '"sv-approved"');
    const rejected = expect(answer).rejects.toMatchObject({ status: 502, code: 'internal_error' });
    http.expectOne(`/api/v1/episodes/${ID}/story/reopen`).flush({ episodeId: ID, etag: '', status: 'story_review' });
    await rejected;
  });
});
