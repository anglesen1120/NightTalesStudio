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

  it.each([
    ['grant_requires_approval', 202],
    ['setting_change_pending', 202],
    ['objection_recorded', 202],
    ['grant_requires_approval', 200],
  ])('AppErrorMapper_202BodyCode_%s_at_%s_IsUnknownError', (code, status) => {
    const mapper = setup();
    const error = mapper.from(http(status, { code, traceId: 't-202' }));
    expect(error.code).toBe('unknown_error');
    expect(error.status).toBe(status);
    expect(error.kind).toBe('unknown');
    expect(mapper.messageKey(error)).toBe('errors.unknown_error');
    expect(error.traceId).toBe('t-202');
  });

  it('AppErrorMapper_RetryAfter_MultiValuedSignedAndSuffixedFormsAreIgnored', () => {
    const mapper = setup();
    const withHeader = (value: string) => mapper.from(http(429, { code: 'rate_limited' }, { 'Retry-After': value }));
    for (const ignored of ['5, 10', '+5', '5s', '0x10', '\u0663', '1 2', '5.', '.5', '-0']) {
      expect(withHeader(ignored).retryAfterSec, JSON.stringify(ignored)).toBeUndefined();
    }
    expect(withHeader('\t12\n').retryAfterSec).toBe(12);
    expect(withHeader('007').retryAfterSec).toBe(7);
    expect(withHeader('999999999').retryAfterSec).toBe(3600);
    // the header is read on 429 and 503 only (503 + Retry-After is a real load-balancer answer)
    expect(mapper.from(http(503, null, { 'Retry-After': '9' })).retryAfterSec).toBe(9);
  });

  it('AppErrorMapper_JsonStringBody_KeywordsFlaggedRowsAndBomAreRead', () => {
    const mapper = setup();
    const text = JSON.stringify({
      code: 'keywords_flagged',
      traceId: 't-str',
      errors: [{ segment: 'p:1', index: 2, rule: 'real_name', __proto__x: 1 }],
    });
    const fromText = mapper.from(http(409, text));
    expect(fromText.code).toBe('keywords_flagged');
    expect((fromText.problem?.errors as readonly Record<string, unknown>[])[0]).toMatchObject({ segment: 'p:1', index: 2, rule: 'real_name' });
    // a UTF-8 BOM in front of the JSON (some proxies) does not turn a coded answer into a status fallback
    expect(mapper.from(http(404, `\uFEFF{"code":"feature_disabled"}`)).code).toBe('feature_disabled');
    // a JSON array or scalar delivered as text is not a ProblemDetails
    expect(mapper.from(http(400, '["validation_failed"]')).code).toBe('validation_failed');
    expect(mapper.from(http(404, '"not_found"')).problem).toBeUndefined();
    // status 0 always wins, even over a parseable JSON text
    expect(mapper.from(http(0, '{"code":"not_found"}')).code).toBe('network_error');
  });

  it('AppErrorMapper_HostileKeys_NeverReachObjectPrototypeOrProblem', () => {
    const mapper = setup();
    const body = JSON.parse(
      '{"code":"keywords_flagged","errors":[{"__proto__":{"polluted":1},"constructor":"x","rule":"r","prototype":"p"}],' +
        '"__proto__":{"polluted":2},"constructor":{"prototype":{"polluted":3}}}',
    ) as unknown;
    const error = mapper.from(http(409, body));
    const row = (error.problem?.errors as readonly Record<string, unknown>[])[0] ?? {};
    expect(Object.keys(row)).toEqual(['rule']);
    expect(({} as Record<string, unknown>)['polluted']).toBeUndefined();
    expect(Object.hasOwn(error.problem ?? {}, '__proto__')).toBe(false);
    expect(Object.hasOwn(error.problem ?? {}, 'constructor')).toBe(false);
    // validation: the reserved pointers (`__proto__`, `constructor`, `prototype`) are dropped, a sibling survives
    const validation = mapper.from(http(400, JSON.parse('{"code":"validation_failed","errors":{"__proto__":["a"],"prototype":["b"],"constructor":["c"],"ok":["d"]}}') as unknown));
    expect(Object.keys(validation.fieldErrors)).toEqual(['ok']);
  });

  it('AppErrorMapper_ClientBug_AllThreeCodesAreSilentOutsideDevMode', () => {
    const mapper = setup(false);
    for (const [status, code] of [
      [428, 'if_match_required'],
      [422, 'idempotency_key_reused'],
      [400, 'idempotency_key_required'],
    ] as const) {
      expect(mapper.from(http(status, { code, traceId: 'x', detail: 'SECRET' })).kind).toBe('client_bug');
    }
    // also the status fallback of a code-less 428/422
    expect(mapper.from(http(428, null)).kind).toBe('client_bug');
    expect(mapper.from(http(422, null)).kind).toBe('client_bug');
    expect(warnings).toEqual([]);
  });

  it('AppErrorMapper_ResultsAreFrozen_SoOneScreenCannotEditAnothersError', () => {
    const error = setup().from(http(400, { code: 'validation_failed', errors: { title: ['x'] } }));
    expect(Object.isFrozen(error)).toBe(true);
    expect(Object.isFrozen(error.fieldErrors)).toBe(true);
    expect(Object.isFrozen(error.fieldErrors['title'])).toBe(true);
  });

  it.each([
    ['bidi override', 'abc\u202Edef'],
    ['200 chars', 'a'.repeat(200)],
    ['markup', '<script>alert(1)</script>'],
    ['whitespace', 'a b'],
  ])('AppErrorMapper_HostileTraceId_%s_IsDropped', (_name, traceId) => {
    const mapper = setup(true);
    const error = mapper.from(http(428, { code: 'if_match_required', traceId }));
    expect(error.traceId).toBeUndefined();
    expect(error.problem?.traceId).toBeUndefined();
    expect(mapper.traceFooter(error)).toBeNull();
    expect(warnings).toEqual([['client_bug if_match_required/428/-']]);
    expect(mapper.from(http(500, { code: 'internal_error', traceId: 'a'.repeat(128) })).traceId).toBe('a'.repeat(128));
    expect(mapper.from(http(500, { code: 'internal_error', traceId: '00-ab.cd_ef:gh-01' })).traceId).toBe('00-ab.cd_ef:gh-01');
  });

  it('AppErrorMapper_FieldErrors_AreCapped_OnObjectAndStringBodies', () => {
    const mapper = setup();
    const many: Record<string, string[]> = {};
    for (let i = 0; i < 300; i++) many[`f${i}`] = ['x'];
    const manyMessages = { title: Array.from({ length: 50 }, (_unused, i) => `m${i}`), ['p'.repeat(400)]: ['x'], ok: ['y'] };
    for (const body of [{ code: 'validation_failed', errors: many }, JSON.stringify({ code: 'validation_failed', errors: many })]) {
      const keys = Object.keys(mapper.from(http(400, body)).fieldErrors);
      expect(keys).toHaveLength(200);
      expect(keys[0]).toBe('f0');
      expect(keys[199]).toBe('f199');
    }
    for (const body of [{ code: 'validation_failed', errors: manyMessages }, JSON.stringify({ code: 'validation_failed', errors: manyMessages })]) {
      const error = mapper.from(http(400, body));
      expect(error.fieldErrors['title']).toHaveLength(20);
      expect(error.fieldErrors['title']?.[19]).toBe('m19');
      expect(Object.keys(error.fieldErrors)).toEqual(['title', 'ok']);
      expect(Object.keys(error.problem?.errors ?? {})).toEqual(['title', 'ok']);
    }
    // a 256-char pointer is kept, a 257-char one is dropped
    const edge = mapper.from(http(400, { code: 'validation_failed', errors: { ['e'.repeat(256)]: ['x'], ['e'.repeat(257)]: ['x'] } }));
    expect(Object.keys(edge.fieldErrors)).toEqual(['e'.repeat(256)]);
  });

  it('AppErrorMapper_ProblemStrings_AreTruncated_AndNonStringsDropped', () => {
    const mapper = setup();
    const error = mapper.from(
      http(400, { code: 'validation_failed', title: 't'.repeat(5000), detail: 'd'.repeat(1024), instance: 'i'.repeat(1025), field: 'f'.repeat(2000), mfaToken: 'm'.repeat(3000), type: 'ty' }),
    );
    expect(error.problem?.title).toHaveLength(1024);
    expect(error.problem?.detail).toHaveLength(1024);
    expect(error.problem?.instance).toHaveLength(1024);
    expect(error.problem?.field).toHaveLength(1024);
    expect(error.problem?.mfaToken).toHaveLength(1024);
    expect(error.problem?.type).toBe('ty');
    const dropped = mapper.from(http(400, { code: 'validation_failed', title: 5, detail: { a: 1 }, field: ['x'], mfaToken: 7 }));
    expect(dropped.problem).toEqual({ code: 'validation_failed' });
  });

  it.each([
    [500, 'server'],
    [400, 'unknown'],
  ])('AppErrorMapper_ClientOnlyBodyCodes_AreUnknown_at_%s', (status, kind) => {
    const mapper = setup();
    for (const code of ['network_error', 'unknown_error']) {
      const error = mapper.from(http(status, { code }));
      expect(error.code).toBe('unknown_error');
      expect(error.kind).toBe(kind);
      expect(error.kind).not.toBe('network');
      expect(error.problem?.code).toBe(code);
    }
  });

  it('AppErrorMapper_RetryAfter_OnlyOn429And503_AndClampedToOneHour', () => {
    const mapper = setup();
    const retry = (status: number, value: string) => mapper.from(http(status, { code: 'rate_limited' }, { 'Retry-After': value })).retryAfterSec;
    expect(retry(400, '30')).toBeUndefined();
    expect(retry(500, '30')).toBeUndefined();
    expect(retry(429, '30')).toBe(30);
    expect(retry(503, '30')).toBe(30);
    expect(retry(429, '999999')).toBe(3600);
    expect(retry(503, '999999999')).toBe(3600);
    expect(retry(429, '3600')).toBe(3600);
    expect(mapper.fromProblem({ code: 'rate_limited' }, 400, 30).retryAfterSec).toBeUndefined();
    expect(mapper.fromProblem({ code: 'rate_limited' }, 429, 7200).retryAfterSec).toBe(3600);
  });
});
