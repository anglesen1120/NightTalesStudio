import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import errorsEn from '../../../assets/i18n/errors/en.json';
import errorsVi from '../../../assets/i18n/errors/vi.json';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { isClientBug, isConflict, isRateLimited, isValidation, type AppError } from './app-error';
import { APP_ERROR_DEV_MODE, AppErrorMapper } from './app-error-mapper';
import { ERROR_CODES, type KnownErrorCode } from './error-code';

function setup(devMode = false): AppErrorMapper {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: APP_ERROR_DEV_MODE, useValue: devMode }],
  });
  return TestBed.inject(AppErrorMapper);
}

function http(status: number, error: unknown, headers?: Record<string, string>): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText: 'x',
    error,
    ...(headers === undefined ? {} : { headers: new HttpHeaders(headers) }),
  });
}

describe('AppErrorMapper', () => {
  let warnings: unknown[][];
  let originalWarn: typeof console.warn;

  beforeEach(() => {
    warnings = [];
    originalWarn = console.warn;
    console.warn = (...args: unknown[]) => {
      warnings.push(args);
    };
  });

  afterEach(() => {
    console.warn = originalWarn;
  });

  it.each(Object.entries(ERROR_CODES) as [KnownErrorCode, (typeof ERROR_CODES)[KnownErrorCode]][])(
    'AppErrorMapper_MapsCatalogueCodes_ToKindAndMessageKey: %s',
    (code, entry) => {
      const mapper = setup();
      // `unknown_error` is the one entry whose kind depends on the status (a 5xx unknown code is `server`).
      const status = code === 'unknown_error' ? 400 : entry.status;
      const error = mapper.fromProblem({ code, traceId: 't-1' }, status, null);
      expect(error.code).toBe(code);
      expect(error.status).toBe(status);
      expect(error.kind).toBe(entry.kind);
      const leaf = code === 'unauthenticated' ? 'session_revoked' : code;
      expect(mapper.messageKey(error)).toBe(`errors.${leaf}`);
      // no key is missing for the mapped list: the leaf exists in both languages
      expect(typeof (errorsVi as Record<string, unknown>)[leaf]).toBe('string');
      expect(typeof (errorsEn as Record<string, unknown>)[leaf]).toBe('string');
    },
  );

  it('AppErrorMapper_Conflict412_AndRateLimit429_AndClientBug', () => {
    const mapper = setup();
    for (const code of ['story_version_conflict', 'series_conflict', 'fact_sheet_conflict', 'bible_conflict', 'setting_conflict']) {
      const error = mapper.from(http(412, { code, traceId: 't' }));
      expect(error.kind).toBe('conflict');
      expect(isConflict(error)).toBe(true);
    }
    const limited = mapper.from(http(429, { code: 'rate_limited', traceId: 't' }, { 'Retry-After': '30' }));
    expect(isRateLimited(limited)).toBe(true);
    expect(limited.retryAfterSec).toBe(30);
    for (const [status, code] of [
      [428, 'if_match_required'],
      [422, 'idempotency_key_reused'],
      [400, 'idempotency_key_required'],
    ] as const) {
      expect(isClientBug(mapper.from(http(status, { code })))).toBe(true);
    }
    expect(isValidation(mapper.from(http(400, { code: 'validation_failed' })))).toBe(true);
    expect(isValidation(mapper.from(http(409, { code: 'episode_invalid_state' })))).toBe(false);
    expect(mapper.from(http(409, { code: 'episode_invalid_state' })).kind).toBe('precondition');
  });

  it('AppErrorMapper_UnknownCode_ReturnsUnknownErrorWithTraceId', () => {
    const mapper = setup();
    const error = mapper.from(http(418, { code: 'wat', traceId: 'trace-123', title: 'Teapot', detail: 'secret' }));
    expect(error.code).toBe('unknown_error');
    expect(error.kind).toBe('unknown');
    expect(error.status).toBe(418);
    expect(error.traceId).toBe('trace-123');
    expect(mapper.messageKey(error)).toBe('errors.unknown_error');
    expect(mapper.traceFooter(error)).toEqual({ key: 'errors.unknown.trace', params: { traceId: 'trace-123' } });
    // an unknown non-empty code wins over the status table, also on a 5xx (kind is then `server`)
    const server = mapper.from(http(502, { code: 'wat' }));
    expect(server.code).toBe('unknown_error');
    expect(server.kind).toBe('server');
    expect(mapper.traceFooter(server)).toBeNull();
  });

  it('AppErrorMapper_ValidationErrors_GroupedByField', () => {
    const mapper = setup();
    const body = JSON.parse(
      '{"code":"validation_failed","traceId":"t","errors":{"title":["title_required","title_too_long"],' +
        '"segments.2.text":["text_required"],"mixed":[1,"keep",null,{"a":1}],"scalar":"nope","numbers":[1,2],' +
        '"__proto__":["polluted"],"constructor":["polluted"]}}',
    ) as unknown;
    const error = mapper.from(http(400, body));
    expect(Object.entries(error.fieldErrors)).toEqual([
      ['title', ['title_required', 'title_too_long']],
      ['segments.2.text', ['text_required']],
      ['mixed', ['keep']],
    ]);
    expect(Object.getPrototypeOf(error.fieldErrors)).toBeNull();
    expect(Object.hasOwn(error.fieldErrors, '__proto__')).toBe(false);
    expect(Object.hasOwn(error.fieldErrors, 'constructor')).toBe(false);
    expect(({} as Record<string, unknown>)['polluted']).toBeUndefined();
    expect(error.kind).toBe('validation');
  });

  it('AppErrorMapper_NonJsonBody_FallsBackToStatus', () => {
    const mapper = setup();
    const html = '<html><body><h1>502 Bad Gateway</h1></body></html>';
    const oversized = `{"code":"not_found","pad":"${'x'.repeat(70_000)}"}`;
    const bodies: unknown[] = [html, '', null, undefined, '{not json', new Blob(['x']), new ProgressEvent('error'), 42, ['a'], oversized];
    for (const body of bodies) {
      const error = mapper.from(http(502, body));
      expect(error.code).toBe('internal_error');
      expect(error.status).toBe(502);
      expect(error.kind).toBe('server');
      expect(error.problem).toBeUndefined();
      expect(error.traceId).toBeUndefined();
      expect(Object.keys(error.fieldErrors)).toEqual([]);
    }
    // a JSON object delivered as text (responseType text) is still read
    expect(mapper.from(http(404, '{"code":"not_found","traceId":"t9"}'))).toMatchObject({ code: 'not_found', traceId: 't9' });
    // an empty or code-less object falls back to the status
    expect(mapper.from(http(404, {})).code).toBe('not_found');
    expect(mapper.from(http(400, { code: '', traceId: 7 })).code).toBe('validation_failed');
    expect(mapper.from(http(400, { code: 7 })).code).toBe('validation_failed');
  });

  it.each([
    [400, 'validation_failed'],
    [401, 'unauthorized'],
    [403, 'forbidden'],
    [404, 'not_found'],
    [409, 'concurrency_conflict'],
    [412, 'concurrency_conflict'],
    [413, 'payload_too_large'],
    [422, 'idempotency_key_reused'],
    [428, 'if_match_required'],
    [429, 'rate_limited'],
    [500, 'internal_error'],
    [503, 'internal_error'],
    [418, 'unknown_error'],
  ])('AppErrorMapper_StatusFallback: %s without a code -> %s', (status, code) => {
    expect(setup().from(http(status, null)).code).toBe(code);
  });

  it('AppErrorMapper_NetworkError_MapsStatusZero', () => {
    const mapper = setup();
    const error = mapper.from(http(0, new ProgressEvent('error')));
    expect(error).toMatchObject({ code: 'network_error', status: 0, kind: 'network' });
    expect(mapper.messageKey(error)).toBe('errors.network_error');
    // a body cannot exist without a response: status 0 always wins
    expect(mapper.fromProblem({ code: 'not_found' }, 0, null).code).toBe('network_error');
  });

  it('AppErrorMapper_NonHttpErrors_AreUnknown', () => {
    const mapper = setup();
    for (const thrown of [new Error('boom'), new TypeError('x'), 'text', 42, null, undefined, { status: 500, code: 'not_found' }]) {
      expect(mapper.from(thrown)).toMatchObject({ code: 'unknown_error', status: 0, kind: 'unknown' });
    }
  });

  it('AppErrorMapper_RetryAfter_SecondsParsedOrIgnored', () => {
    const mapper = setup();
    const withHeader = (value: string) => mapper.from(http(429, { code: 'rate_limited' }, { 'Retry-After': value }));
    expect(withHeader('30').retryAfterSec).toBe(30);
    expect(withHeader(' 7 ').retryAfterSec).toBe(7);
    expect(withHeader('0').retryAfterSec).toBe(0);
    for (const ignored of ['Wed, 21 Oct 2026 07:28:00 GMT', '-5', '1.5', '', 'abc', '99999999999', '1e3']) {
      expect(withHeader(ignored).retryAfterSec).toBeUndefined();
    }
    // missing header on a 429: no invented countdown
    expect(mapper.from(http(429, { code: 'rate_limited', retryAfterSeconds: 30 })).retryAfterSec).toBeUndefined();
    // fromProblem takes already-parsed seconds and rejects the unusable ones
    expect(mapper.fromProblem({ code: 'rate_limited' }, 429, 30).retryAfterSec).toBe(30);
    for (const bad of [Number.NaN, -1, 1.5, Number.POSITIVE_INFINITY, null]) {
      expect(mapper.fromProblem({ code: 'rate_limited' }, 429, bad).retryAfterSec).toBeUndefined();
    }
  });

  it('AppErrorMapper_DoesNotExposeDetail', () => {
    const mapper = setup();
    const body = { code: 'validation_failed', detail: 'errors.session_revoked', title: 'errors.not_found', type: 'errors.forbidden', traceId: 'tt' };
    const error = mapper.from(http(400, body));
    expect(mapper.messageKey(error)).toBe('errors.validation_failed');
    expect(mapper.fieldMessageKey(error, 'x')).not.toContain('session_revoked');
    expect(JSON.stringify(mapper.traceFooter(error))).not.toContain('errors.session_revoked');
    // without a code, detail/title/type still do not decide anything
    const codeless = mapper.from(http(503, { detail: 'errors.session_revoked', title: 'errors.forbidden' }));
    expect(mapper.messageKey(codeless)).toBe('errors.internal_error');
    // the body is kept for structured payloads only; it is never turned into a message
    expect(error.problem?.detail).toBe('errors.session_revoked');
  });

  it('AppErrorMapper_BodyStatusIsNotTrusted', () => {
    const error = setup().from(http(502, { status: 200, code: 'service_unavailable' }));
    expect(error.status).toBe(502);
    expect(error.problem?.status).toBe(200);
  });

  it('AppErrorMapper_Unauthenticated401_MapsToSessionRevoked', () => {
    const mapper = setup();
    const error = mapper.from(http(401, { code: 'unauthenticated', traceId: 'me-1' }));
    expect(error.code).toBe('unauthenticated');
    expect(error.kind).toBe('auth');
    expect(mapper.messageKey(error)).toBe('errors.session_revoked');
    expect(mapper.messageKey(error)).not.toBe('errors.unknown_error');
    // 401 without any code is the terminal-only fallback
    expect(mapper.messageKey(mapper.from(http(401, null)))).toBe('errors.unauthorized');
  });

  it('AppErrorMapper_KeywordsFlagged_KeepsStructuredRowsOnProblem', () => {
    const mapper = setup();
    const rows = [
      { segment: 'p:1', index: 0, rule: 'real_name', nested: { a: 1 } },
      'not a row',
      { segment: 'ending', index: 1, rule: 'banned_term' },
    ];
    const error = mapper.from(http(409, { code: 'keywords_flagged', traceId: 't', errors: rows }));
    expect(error.kind).toBe('precondition');
    expect(Object.keys(error.fieldErrors)).toEqual([]);
    expect(error.problem?.errors).toHaveLength(2);
    expect(Array.isArray(error.problem?.errors)).toBe(true);
    const kept = error.problem?.errors as readonly Record<string, unknown>[];
    expect(Object.entries(kept[0] ?? {})).toEqual([
      ['segment', 'p:1'],
      ['index', 0],
      ['rule', 'real_name'],
    ]);
    expect(kept[1]).toMatchObject({ segment: 'ending', rule: 'banned_term' });
  });

  it('AppErrorMapper_ProblemKeepsAllowListedScalarsOnly', () => {
    const error = setup().from(
      http(429, { code: 'story_generation_limit', traceId: 't', limit: 5, field: 'x', retryAfterSeconds: 9, nested: { a: 1 }, extra: 'z' }),
    );
    expect(error.problem).toEqual({ code: 'story_generation_limit', traceId: 't', limit: 5, field: 'x', retryAfterSeconds: 9 });
  });

  it('AppErrorMapper_ClientBug_LogsOneDevLineWithoutBody', () => {
    const mapper = setup(true);
    const error = mapper.from(http(428, { code: 'if_match_required', traceId: 'tr-1', detail: 'SECRET-DETAIL', title: 'SECRET-TITLE' }));
    expect(error.kind).toBe('client_bug');
    expect(warnings).toEqual([['client_bug if_match_required/428/tr-1']]);
    expect(JSON.stringify(warnings)).not.toContain('SECRET');
    mapper.from(http(422, { code: 'idempotency_key_reused' }));
    expect(warnings).toHaveLength(2);
    expect(warnings[1]).toEqual(['client_bug idempotency_key_reused/422/-']);
    // other kinds are silent
    mapper.from(http(400, { code: 'validation_failed' }));
    expect(warnings).toHaveLength(2);
  });

  it('AppErrorMapper_ClientBug_IsSilentOutsideDevMode', () => {
    setup(false).from(http(428, { code: 'if_match_required', traceId: 'tr-1' }));
    expect(warnings).toEqual([]);
  });

  it('AppErrorMapper_FieldMessageKey_UsesCodeKeyForCarriedPointers', () => {
    const mapper = setup();
    const error = mapper.from(http(400, { code: 'story_invalid_content', errors: { 'segments.2.text': ['x'] } }));
    expect(mapper.fieldMessageKey(error, 'segments.2.text')).toBe('errors.story_invalid_content');
    expect(mapper.fieldMessageKey(error, 'other')).toBe('errors.validation_failed');
  });

  it('AppErrorMapper_TraceFooter_NullWithoutTrace', () => {
    const mapper = setup();
    const error: AppError = mapper.from(http(500, { code: 'internal_error', traceId: '' }));
    expect(error.traceId).toBeUndefined();
    expect(mapper.traceFooter(error)).toBeNull();
  });
});
