import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { withIdempotency } from './http-context';
import { IdempotencyIntent, IdempotencyKeys } from './idempotency-keys';
import { idempotencyInterceptor } from './idempotency.interceptor';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** A repository fake: sends the paid POST through the real interceptor chain. */
class FakeRepo {
  constructor(private readonly http: HttpClient) {}

  generate(intent: IdempotencyIntent): Promise<unknown> {
    return firstValueFrom(
      this.http.post('/api/v1/episodes/e1/story/generate', {}, { context: withIdempotency(intent) }),
    );
  }
}

/** A store following the 10.2.3-T1 usage contract: one intent per click, kept open until settled. */
class FakeStore {
  private open: IdempotencyIntent | null = null;
  private requestGeneration = 0;
  busy = false;

  constructor(
    private readonly keys: IdempotencyKeys,
    private readonly repo: FakeRepo,
  ) {}

  /** `run()` without a "Retry" in between is a double click: the open intent is reused. */
  async run(): Promise<void> {
    const intent = this.open !== null && !this.open.isSettled ? this.open : (this.open = this.keys.begin('episode.generate'));
    const requestGeneration = this.requestGeneration;
    this.busy = true;
    try {
      await this.repo.generate(intent);
      if (requestGeneration !== this.requestGeneration) return;
      intent.settle();
    } catch {
      if (requestGeneration !== this.requestGeneration) return;
      // A network error is retryable: the intent stays open for "Retry".
    } finally {
      if (requestGeneration === this.requestGeneration) this.busy = false;
    }
  }

  reset(): void {
    this.requestGeneration += 1;
    this.open = null;
    this.busy = false;
  }

  get current(): IdempotencyIntent | null {
    return this.open;
  }
}

describe('IdempotencyKeys', () => {
  let keys: IdempotencyKeys;
  let http: HttpClient;
  let mock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        provideHttpClient(withInterceptors([idempotencyInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    keys = TestBed.inject(IdempotencyKeys);
    http = TestBed.inject(HttpClient);
    mock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    mock.verify();
    vi.restoreAllMocks();
  });

  it('IdempotencyKeys_Begin_ReturnsUuidPerCall', () => {
    const a = keys.begin('episode.generate');
    const b = keys.begin('episode.generate');
    expect(a.key).toMatch(UUID_V4);
    expect(b.key).toMatch(UUID_V4);
    expect(a.key).not.toBe(b.key);
    expect(a.key).toHaveLength(36);
    expect(a.key).toMatch(/^[\x21-\x7e]{1,128}$/);
    expect(a.intent).toBe('episode.generate');
    expect(keys.newKey()).toMatch(UUID_V4);
  });

  it('IdempotencyIntent_Settle_IsOneWay', () => {
    const intent = keys.begin('x');
    expect(intent.isSettled).toBe(false);
    intent.settle();
    expect(intent.isSettled).toBe(true);
    intent.settle();
    expect(intent.isSettled).toBe(true);
  });

  it('IdempotencyKeys_TwoClicksOneIntent_OneKey', async () => {
    const repo = new FakeRepo(http);
    const sent: string[] = [];
    const store = new FakeStore(keys, repo);
    const first = store.run();
    const second = store.run();
    const requests = mock.match('/api/v1/episodes/e1/story/generate');
    expect(requests).toHaveLength(2);
    for (const req of requests) {
      sent.push(req.request.headers.get('Idempotency-Key') ?? '');
      req.flush({});
    }
    await Promise.all([first, second]);
    expect(sent).toHaveLength(2);
    expect(new Set(sent).size).toBe(1);
    expect(sent[0]).toMatch(UUID_V4);
  });

  it('IdempotencyKeys_NewIntent_NewKey', async () => {
    const store = new FakeStore(keys, new FakeRepo(http));
    const first = store.run();
    const one = mock.expectOne('/api/v1/episodes/e1/story/generate');
    const keyOne = one.request.headers.get('Idempotency-Key');
    one.flush({});
    await first;
    expect(store.current?.isSettled).toBe(true);

    const second = store.run();
    const two = mock.expectOne('/api/v1/episodes/e1/story/generate');
    const keyTwo = two.request.headers.get('Idempotency-Key');
    two.flush({});
    await second;
    expect(keyTwo).toMatch(UUID_V4);
    expect(keyTwo).not.toBe(keyOne);
  });

  it('a manual Retry after a network error reuses the key; a new intent does not', async () => {
    const store = new FakeStore(keys, new FakeRepo(http));
    const failed = store.run();
    const one = mock.expectOne('/api/v1/episodes/e1/story/generate');
    const keyOne = one.request.headers.get('Idempotency-Key');
    one.error(new ProgressEvent('error'));
    await failed;
    expect(store.current?.isSettled).toBe(false);

    const retry = store.run();
    const two = mock.expectOne('/api/v1/episodes/e1/story/generate');
    expect(two.request.headers.get('Idempotency-Key')).toBe(keyOne);
    two.flush({});
    await retry;
    expect(store.current?.isSettled).toBe(true);

    const other = keys.begin('episode.regenerate');
    void firstValueFrom(http.post('/api/v1/episodes/e2/story/generate', {}, { context: withIdempotency(other) }));
    const three = mock.expectOne('/api/v1/episodes/e2/story/generate');
    expect(three.request.headers.get('Idempotency-Key')).not.toBe(keyOne);
    three.flush({});
  });

  it('a late answer after reset neither settles a newer intent nor restores busy', async () => {
    const store = new FakeStore(keys, new FakeRepo(http));
    const late = store.run();
    const stale = mock.expectOne('/api/v1/episodes/e1/story/generate');
    store.reset();

    const next = store.run();
    const fresh = mock.expectOne('/api/v1/episodes/e1/story/generate');
    expect(fresh.request.headers.get('Idempotency-Key')).not.toBe(stale.request.headers.get('Idempotency-Key'));

    stale.flush({});
    await late;
    expect(store.current?.isSettled).toBe(false);
    expect(store.busy).toBe(true);

    fresh.flush({});
    await next;
    expect(store.current?.isSettled).toBe(true);
    expect(store.busy).toBe(false);
  });

  it('IdempotencyKeyReused_MapsToClientBug', async () => {
    const mapper = TestBed.inject(AppErrorMapper);
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const intent = keys.begin('episode.generate');
    const done = firstValueFrom(http.post('/api/v1/episodes/e1/story/generate', { a: 1 }, { context: withIdempotency(intent) }))
      .then(() => null, (error: unknown) => error);
    mock
      .expectOne('/api/v1/episodes/e1/story/generate')
      .flush({ code: 'idempotency_key_reused', detail: 'SECRET' }, { status: 422, statusText: 'Unprocessable Entity' });
    const error = await done;
    expect(error).toBeInstanceOf(HttpErrorResponse);
    const mapped = mapper.from(error);
    expect(mapped.code).toBe('idempotency_key_reused');
    expect(mapped.kind).toBe('client_bug');
    expect(JSON.stringify(warn.mock.calls)).not.toContain(intent.key);
    expect(JSON.stringify(warn.mock.calls)).not.toContain('SECRET');
  });

  it('IdempotencyKeyRequired_MapsToClientBug', async () => {
    const mapper = TestBed.inject(AppErrorMapper);
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const done = firstValueFrom(http.post('/api/v1/episodes/e1/story/generate', {})).then(() => null, (error: unknown) => error);
    const req = mock.expectOne('/api/v1/episodes/e1/story/generate');
    expect(req.request.headers.has('Idempotency-Key')).toBe(false);
    req.flush({ code: 'idempotency_key_required' }, { status: 400, statusText: 'Bad Request' });
    const mapped = mapper.from(await done);
    expect(mapped.code).toBe('idempotency_key_required');
    expect(mapped.kind).toBe('client_bug');
  });
});
