import { HttpClient, HttpContext, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, type TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { type Observable, firstValueFrom } from 'rxjs';
import { ActivityTracker } from '../session/activity';
import { SESSION_SCOPED } from '../session/session-scope';
import { SessionApi } from '../session/session.api';
import { sessionInterceptor } from '../session/session.interceptor';
import { SessionStore } from '../session/session-store';
import { FakeSessionApi, fakeJwt } from '../session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { appConfig } from '../../app.config';
import { userAction, withIdempotency } from './http-context';
import { IdempotencyKeys } from './idempotency-keys';
import { idempotencyInterceptor } from './idempotency.interceptor';

const GENERATE = '/api/v1/episodes/e1/story/generate';
const FRESH_MS = 10 * 60_000;

function run(source: Observable<unknown>): Promise<{ ok: boolean }> {
  return firstValueFrom(source).then(
    () => ({ ok: true }),
    () => ({ ok: false }),
  );
}

describe('IdempotencyInterceptor', () => {
  let keys: IdempotencyKeys;
  let http: HttpClient;
  let mock: HttpTestingController;
  let fake: FakeSessionApi;
  let store: SessionStore;

  beforeEach(() => {
    fake = new FakeSessionApi();
    fake.next = { accessToken: fakeJwt('user', ['pwd', 'otp']), expiresAt: Date.now() + FRESH_MS };
    // The interceptors are chained in `app.config.ts` order: idempotency first, then session.
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideRouter([{ path: '**', children: [] }]),
        provideHttpClient(withInterceptors([idempotencyInterceptor, sessionInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionApi, useValue: fake },
        { provide: ActivityTracker, useValue: { isActive: () => true, isVisible: () => true } },
        { provide: SESSION_SCOPED, useValue: { reset: () => undefined }, multi: true },
      ],
    });
    keys = TestBed.inject(IdempotencyKeys);
    http = TestBed.inject(HttpClient);
    mock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(SessionStore);
    store.signIn({ accessToken: fakeJwt('user', ['pwd']), expiresAt: Date.now() + FRESH_MS }, 'user', ['pwd']);
  });

  afterEach(() => {
    mock.verify();
    vi.restoreAllMocks();
  });

  function request(url: string): Promise<TestRequest> {
    return vi.waitFor(() => mock.expectOne(url), { timeout: 2_000, interval: 1 });
  }

  it('IdempotencyInterceptor_AddsHeader_ForMarkedPost', async () => {
    const intent = keys.begin('episode.generate');
    const done = run(http.post(GENERATE, {}, { context: withIdempotency(intent) }));
    const req = await request(GENERATE);
    const value = req.request.headers.get('Idempotency-Key');
    expect(value).toBe(intent.key);
    expect(value).toHaveLength(36);
    expect(value).toMatch(/^[\x21-\x7e]{1,128}$/);
    req.flush({});
    expect((await done).ok).toBe(true);
  });

  it('IdempotencyInterceptor_KeepsExistingHeader', async () => {
    const intent = keys.begin('episode.generate');
    const done = run(
      http.post(GENERATE, {}, { context: withIdempotency(intent), headers: new HttpHeaders({ 'Idempotency-Key': 'caller-key' }) }),
    );
    const req = await request(GENERATE);
    expect(req.request.headers.get('Idempotency-Key')).toBe('caller-key');
    req.flush({});
    await done;
  });

  it('IdempotencyInterceptor_SkipsGetAndUnmarked', async () => {
    const intent = keys.begin('x');
    const marked = run(http.get('/api/v1/episodes', { context: withIdempotency(intent) }));
    const get = await request('/api/v1/episodes');
    expect(get.request.headers.has('Idempotency-Key')).toBe(false);
    get.flush([]);
    await marked;

    const plain = run(http.get('/api/v1/episodes'));
    const list = await request('/api/v1/episodes');
    expect(list.request.headers.has('Idempotency-Key')).toBe(false);
    list.flush([]);
    await plain;

    const unmarked = run(http.post(GENERATE, {}));
    const post = await request(GENERATE);
    expect(post.request.headers.has('Idempotency-Key')).toBe(false);
    post.flush({});
    await unmarked;
  });

  it('IdempotencyInterceptor_SurvivesSessionRetry', async () => {
    const intent = keys.begin('episode.generate');
    const done = run(http.post(GENERATE, {}, { context: userAction(withIdempotency(intent)) }));
    const original = await request(GENERATE);
    expect(original.request.headers.get('Idempotency-Key')).toBe(intent.key);
    original.flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    const replay = await request(GENERATE);
    expect(replay.request.headers.get('Idempotency-Key')).toBe(intent.key);
    expect(replay.request.headers.get('Authorization')).toBe(`Bearer ${fake.next.accessToken}`);
    replay.flush({});
    expect((await done).ok).toBe(true);
    expect(fake.calls).toBe(1);
  });

  it('never logs the key and persists nothing', async () => {
    const log = vi.spyOn(console, 'log').mockImplementation(() => undefined);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    const intent = keys.begin('episode.generate');
    const done = run(http.post(GENERATE, {}, { context: withIdempotency(intent, new HttpContext()) }));
    (await request(GENERATE)).flush({});
    await done;
    expect(log).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
    expect(error).not.toHaveBeenCalled();
    expect(setItem).not.toHaveBeenCalled();
  });

  it('adds no automatic retry: a failed paid POST is sent exactly once', async () => {
    const intent = keys.begin('episode.generate');
    const done = run(http.post(GENERATE, {}, { context: withIdempotency(intent) }));
    (await request(GENERATE)).flush({ code: 'internal_error' }, { status: 500, statusText: 'Server Error' });
    expect((await done).ok).toBe(false);
    mock.expectNone(GENERATE);
  });

  it.each(['PUT', 'PATCH', 'DELETE', 'POST'] as const)('marked %s carries the key', async (method) => {
    const intent = keys.begin('x');
    const done = run(http.request(method, '/api/v1/things/1', { body: {}, context: withIdempotency(intent) }));
    const req = await request('/api/v1/things/1');
    expect(req.request.headers.get('Idempotency-Key')).toBe(intent.key);
    req.flush({});
    await done;
  });

  it.each(['HEAD', 'OPTIONS'] as const)('marked %s carries no key', async (method) => {
    const done = run(http.request(method, '/api/v1/things/1', { context: withIdempotency(keys.begin('x')) }));
    const req = await request('/api/v1/things/1');
    expect(req.request.headers.has('Idempotency-Key')).toBe(false);
    req.flush({});
    await done;
  });

  it('a lower-case caller header is not overwritten (header names are case-insensitive)', async () => {
    const done = run(
      http.post(GENERATE, {}, { context: withIdempotency(keys.begin('x')), headers: { 'idempotency-key': 'mine' } }),
    );
    const req = await request(GENERATE);
    expect(req.request.headers.getAll('Idempotency-Key')).toEqual(['mine']);
    req.flush({});
    await done;
  });

  it('a settled intent still stamps its own key on a late retry (settle does not rotate the key)', async () => {
    const intent = keys.begin('x');
    intent.settle();
    const done = run(http.post(GENERATE, {}, { context: withIdempotency(intent) }));
    const req = await request(GENERATE);
    expect(req.request.headers.get('Idempotency-Key')).toBe(intent.key);
    req.flush({});
    await done;
  });

  it('two concurrent intents send different keys, and the 401 replay keeps each request on its own key', async () => {
    const a = keys.begin('a');
    const b = keys.begin('b');
    const doneA = run(http.post('/api/v1/a', {}, { context: userAction(withIdempotency(a)) }));
    const doneB = run(http.post('/api/v1/b', {}, { context: userAction(withIdempotency(b)) }));
    const reqA = await request('/api/v1/a');
    const reqB = await request('/api/v1/b');
    expect(reqA.request.headers.get('Idempotency-Key')).toBe(a.key);
    expect(reqB.request.headers.get('Idempotency-Key')).toBe(b.key);
    expect(a.key).not.toBe(b.key);
    reqA.flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    reqB.flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    const replayA = await request('/api/v1/a');
    const replayB = await request('/api/v1/b');
    expect(replayA.request.headers.get('Idempotency-Key')).toBe(a.key);
    expect(replayB.request.headers.get('Idempotency-Key')).toBe(b.key);
    replayA.flush({});
    replayB.flush({});
    expect((await doneA).ok && (await doneB).ok).toBe(true);
  });

  it('a second 401 on the replay is not retried again and keeps the same single key', async () => {
    const intent = keys.begin('x');
    const done = run(http.post(GENERATE, {}, { context: userAction(withIdempotency(intent)) }));
    (await request(GENERATE)).flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    const replay = await request(GENERATE);
    expect(replay.request.headers.getAll('Idempotency-Key')).toEqual([intent.key]);
    replay.flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    expect((await done).ok).toBe(false);
    mock.expectNone(GENERATE);
  });
});

describe('IdempotencyInterceptor with the real appConfig providers', () => {
  it('app.config registers idempotency before session: the 401 replay keeps the key', async () => {
    const fake = new FakeSessionApi();
    fake.next = { accessToken: fakeJwt('user', ['pwd', 'otp']), expiresAt: Date.now() + FRESH_MS };
    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        provideHttpClientTesting(),
        { provide: SessionApi, useValue: fake },
        { provide: ActivityTracker, useValue: { isActive: () => true, isVisible: () => true } },
      ],
    });
    const http = TestBed.inject(HttpClient);
    const mock = TestBed.inject(HttpTestingController);
    TestBed.inject(SessionStore).signIn(
      { accessToken: fakeJwt('user', ['pwd']), expiresAt: Date.now() + FRESH_MS },
      'user',
      ['pwd'],
    );
    const intent = TestBed.inject(IdempotencyKeys).begin('episode.generate');
    const done = run(http.post(GENERATE, {}, { context: userAction(withIdempotency(intent)) }));
    const original = await vi.waitFor(() => mock.expectOne(GENERATE), { timeout: 2_000, interval: 1 });
    expect(original.request.headers.get('Idempotency-Key')).toBe(intent.key);
    original.flush({ code: 'unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    const replay = await vi.waitFor(() => mock.expectOne(GENERATE), { timeout: 2_000, interval: 1 });
    expect(replay.request.headers.get('Idempotency-Key')).toBe(intent.key);
    replay.flush({});
    expect((await done).ok).toBe(true);
    mock.verify();
  });
});
