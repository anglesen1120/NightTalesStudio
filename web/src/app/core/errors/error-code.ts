import type { AppError, AppErrorKind } from './app-error';

interface ErrorCodeEntry {
  /** The canonical HTTP status of the code (09 §5.4, `ErrorCatalogue.Status`); `0` = no response. */
  readonly status: number;
  readonly kind: AppErrorKind;
}

/**
 * Every code a wave-1 screen can receive: 09 §5.4 + 02 D8 + the 10.2.1 plan §6 key list, checked against the backend
 * `ErrorCodes` (no invented code). Four entries are client-side only and never arrive in a body: `network_error`,
 * `payload_too_large`, `unknown_error`, and the alias `unauthenticated` (the 401 of `GET/PATCH /me`, absent from the
 * backend catalogue — plan §5 [OPEN]; it renders `errors.session_revoked`, see `ERROR_CODE_ALIASES`).
 *
 * 412 codes and `concurrency_conflict` are `conflict` (ConflictDialog); other 409 guards are `precondition`.
 */
export const ERROR_CODES = {
  // 400
  validation_failed: { status: 400, kind: 'validation' },
  voice_language_mismatch: { status: 400, kind: 'validation' },
  story_invalid_content: { status: 400, kind: 'validation' },
  fact_sheet_invalid_content: { status: 400, kind: 'validation' },
  bible_invalid_content: { status: 400, kind: 'validation' },
  source_url_invalid: { status: 400, kind: 'validation' },
  source_domain_not_allowed: { status: 400, kind: 'validation' },
  autopilot_settings_invalid: { status: 400, kind: 'validation' },
  story_attestation_required: { status: 400, kind: 'precondition' },
  idempotency_key_required: { status: 400, kind: 'client_bug' },
  // 401
  unauthorized: { status: 401, kind: 'auth' },
  unauthenticated: { status: 401, kind: 'auth' },
  session_revoked: { status: 401, kind: 'auth' },
  refresh_token_reused: { status: 401, kind: 'auth' },
  invalid_credentials: { status: 401, kind: 'auth' },
  mfa_required: { status: 401, kind: 'auth' },
  mfa_enrolment_required: { status: 401, kind: 'auth' },
  invalid_mfa_code: { status: 401, kind: 'auth' },
  mfa_token_invalid: { status: 401, kind: 'auth' },
  reauth_required: { status: 401, kind: 'auth' },
  hub_ticket_invalid: { status: 401, kind: 'auth' },
  // 400, 403 (registration / password policy codes of 09.3.1)
  breached_password: { status: 400, kind: 'validation' },
  registration_closed: { status: 403, kind: 'auth' },
  // 403, 404
  forbidden: { status: 403, kind: 'auth' },
  email_not_verified: { status: 403, kind: 'auth' },
  not_found: { status: 404, kind: 'not_found' },
  fact_sheet_not_found: { status: 404, kind: 'not_found' },
  feature_disabled: { status: 404, kind: 'feature_disabled' },
  // 409
  episode_invalid_state: { status: 409, kind: 'precondition' },
  concurrency_conflict: { status: 409, kind: 'conflict' },
  series_archived: { status: 409, kind: 'precondition' },
  series_topic_locked: { status: 409, kind: 'precondition' },
  story_generation_in_progress: { status: 409, kind: 'precondition' },
  fact_sheet_generation_in_progress: { status: 409, kind: 'precondition' },
  find_sources_in_progress: { status: 409, kind: 'precondition' },
  fact_sheet_required: { status: 409, kind: 'precondition' },
  story_sources_required: { status: 409, kind: 'precondition' },
  story_fact_sheet_outdated: { status: 409, kind: 'precondition' },
  story_too_similar_to_source: { status: 409, kind: 'precondition' },
  story_moderated: { status: 409, kind: 'precondition' },
  fact_sheet_sources_insufficient: { status: 409, kind: 'precondition' },
  source_duplicate: { status: 409, kind: 'precondition' },
  source_limit_reached: { status: 409, kind: 'precondition' },
  source_candidate_not_proposed: { status: 409, kind: 'precondition' },
  bible_proposal_not_pending: { status: 409, kind: 'precondition' },
  autopilot_authorization_required: { status: 409, kind: 'precondition' },
  autopilot_topic_locked: { status: 409, kind: 'precondition' },
  autopilot_pause_reason_active: { status: 409, kind: 'precondition' },
  keywords_flagged: { status: 409, kind: 'precondition' },
  legal_acceptance_required: { status: 409, kind: 'precondition' },
  legal_version_outdated: { status: 409, kind: 'precondition' },
  // 412 (stale If-Match)
  episode_conflict: { status: 412, kind: 'conflict' },
  story_version_conflict: { status: 412, kind: 'conflict' },
  fact_sheet_conflict: { status: 412, kind: 'conflict' },
  bible_conflict: { status: 412, kind: 'conflict' },
  series_conflict: { status: 412, kind: 'conflict' },
  setting_conflict: { status: 412, kind: 'conflict' },
  // 413, 422, 428
  payload_too_large: { status: 413, kind: 'precondition' },
  idempotency_key_reused: { status: 422, kind: 'client_bug' },
  if_match_required: { status: 428, kind: 'client_bug' },
  // 429
  rate_limited: { status: 429, kind: 'rate_limit' },
  story_generation_limit: { status: 429, kind: 'rate_limit' },
  source_fetch_limit_reached: { status: 429, kind: 'rate_limit' },
  spend_cap_reached: { status: 429, kind: 'rate_limit' },
  mfa_locked_untrusted: { status: 429, kind: 'rate_limit' },
  // 5xx and client-side
  internal_error: { status: 500, kind: 'server' },
  service_unavailable: { status: 503, kind: 'server' },
  network_error: { status: 0, kind: 'network' },
  unknown_error: { status: 500, kind: 'unknown' },
} as const satisfies Readonly<Record<string, ErrorCodeEntry>>;

export type KnownErrorCode = keyof typeof ERROR_CODES;

/** Codes whose copy is another code's leaf. The keys are not in the backend catalogue (plan §5 [OPEN]). */
export const ERROR_CODE_ALIASES = { unauthenticated: 'session_revoked' } as const satisfies Partial<
  Record<KnownErrorCode, KnownErrorCode>
>;

export function isKnownCode(code: string): code is KnownErrorCode {
  return Object.hasOwn(ERROR_CODES, code);
}

/**
 * The code the client uses when the body carries none (README D9, task Design): the status decides. An unknown
 * non-empty body code is not a status fallback — it maps to `unknown_error` in `AppErrorMapper`.
 */
export function codeForStatus(status: number): KnownErrorCode {
  if (status === 0) return 'network_error';
  if (status >= 500 && status <= 599) return 'internal_error';
  switch (status) {
    case 400:
      return 'validation_failed';
    case 401:
      return 'unauthorized';
    case 403:
      return 'forbidden';
    case 404:
      return 'not_found';
    case 409:
    case 412:
      return 'concurrency_conflict';
    case 413:
      return 'payload_too_large';
    case 422:
      return 'idempotency_key_reused';
    case 428:
      return 'if_match_required';
    case 429:
      return 'rate_limited';
    default:
      return 'unknown_error';
  }
}

/** The kind of a code; an unknown code is a `server` error on a 5xx and `unknown` otherwise. */
export function kindFor(code: string, status: number): AppErrorKind {
  if (!isKnownCode(code) || code === 'unknown_error') return status >= 500 ? 'server' : 'unknown';
  return ERROR_CODES[code].kind;
}

/** The i18n leaf (scope `errors`) that carries the copy of `code`; `unknown_error` for a code without one. */
export function messageCodeFor(code: string): KnownErrorCode {
  if (!isKnownCode(code)) return 'unknown_error';
  return code in ERROR_CODE_ALIASES ? ERROR_CODE_ALIASES[code as keyof typeof ERROR_CODE_ALIASES] : code;
}

/** `errors.<code>`, the runtime Transloco key. Used by the mapper and by `bindFieldErrors` (which has no injector). */
export function messageKeyOf(error: AppError): string {
  return `errors.${messageCodeFor(error.code)}`;
}

/**
 * The key a control shows for `pointer`. The per-field strings of the wire are validator codes, not copy (D9), so the
 * control gets the code-specific key of the whole error. A pointer the payload does not carry has no such message and
 * falls back to the generic validation copy.
 */
export function fieldMessageKeyOf(error: AppError, pointer: string): string {
  return Object.hasOwn(error.fieldErrors, pointer) ? messageKeyOf(error) : 'errors.validation_failed';
}
