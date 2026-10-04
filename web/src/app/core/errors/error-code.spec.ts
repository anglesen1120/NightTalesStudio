import errorsEn from '../../../assets/i18n/errors/en.json';
import errorsVi from '../../../assets/i18n/errors/vi.json';
import {
  ERROR_CODES,
  ERROR_CODE_ALIASES,
  codeForStatus,
  isKnownCode,
  kindFor,
  messageCodeFor,
  type KnownErrorCode,
} from './error-code';

const SCOPES = { vi: errorsVi, en: errorsEn } as const;
const UI_GROUPS = ['form', 'unknown', 'action', 'conflict'];
/** Top-level UI string leaves of the contract (not catalogue codes). */
const UI_STRING_LEAVES = ['retryIn', 'retryNow'];

describe('ERROR_CODES', () => {
  const codes = Object.keys(ERROR_CODES) as KnownErrorCode[];

  it.each(['vi', 'en'] as const)('has an errors.<code> leaf in %s for every code (Acceptance 1)', (lang) => {
    const scope: Readonly<Record<string, unknown>> = SCOPES[lang];
    const missing = codes.map(messageCodeFor).filter((leaf) => typeof scope[leaf] !== 'string');
    expect(missing).toEqual([]);
  });

  it.each(['vi', 'en'] as const)('has no orphan code leaf in %s', (lang) => {
    const orphans = Object.entries(SCOPES[lang])
      .filter(([key, value]) => typeof value === 'string' && !isKnownCode(key) && !UI_STRING_LEAVES.includes(key))
      .map(([key]) => key);
    expect(orphans).toEqual([]);
  });

  it.each(['vi', 'en'] as const)('carries the UI-level leaves of the contract in %s', (lang) => {
    const scope = SCOPES[lang];
    expect(scope.form.summaryTitle).toBeTypeOf('string');
    expect(scope.form.unmappedCount).toContain('{{count}}');
    expect(scope.unknown.trace).toContain('{{traceId}}');
    expect(scope.action.retry).toBeTypeOf('string');
    expect(scope.action.dismiss).toBeTypeOf('string');
    expect(scope.retryIn).toContain('{{seconds}}');
    expect(scope.retryNow).toBeTypeOf('string');
    expect(Object.keys(scope.conflict).sort()).toEqual(['cancel', 'reload', 'saveAsNew', 'title']);
    expect(Object.keys(scope).filter((key) => !isKnownCode(key) && !UI_GROUPS.includes(key)).sort()).toEqual([
      'retryIn',
      'retryNow',
    ]);
  });

  it('keeps the canonical status of each code consistent with its kind family', () => {
    for (const code of codes) {
      const { status, kind } = ERROR_CODES[code];
      if (status === 412) expect(kind).toBe('conflict');
      if (status === 429) expect(kind).toBe('rate_limit');
      if (status === 428 || status === 422) expect(kind).toBe('client_bug');
    }
  });

  it('aliases unauthenticated to the session_revoked leaf and nothing else', () => {
    expect(ERROR_CODE_ALIASES).toEqual({ unauthenticated: 'session_revoked' });
    expect(messageCodeFor('unauthenticated')).toBe('session_revoked');
    expect(messageCodeFor('validation_failed')).toBe('validation_failed');
  });
});

describe('isKnownCode / messageCodeFor / kindFor', () => {
  it.each(['wat', '', 'toString', 'constructor', '__proto__', 'hasOwnProperty'])('does not know %j', (code) => {
    expect(isKnownCode(code)).toBe(false);
    expect(messageCodeFor(code)).toBe('unknown_error');
  });

  it('classifies an unknown code by status: server on 5xx, unknown otherwise', () => {
    expect(kindFor('wat', 502)).toBe('server');
    expect(kindFor('wat', 418)).toBe('unknown');
    expect(kindFor('story_version_conflict', 412)).toBe('conflict');
  });
});

describe('codeForStatus', () => {
  it.each([
    [0, 'network_error'],
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
    [502, 'internal_error'],
    [599, 'internal_error'],
    [418, 'unknown_error'],
    [302, 'unknown_error'],
    [600, 'unknown_error'],
    [-1, 'unknown_error'],
    [Number.NaN, 'unknown_error'],
  ])('maps status %s to %s', (status, code) => {
    expect(codeForStatus(status)).toBe(code);
  });
});
