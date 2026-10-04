import type { ProblemWire } from './data-access/problem-wire';

/**
 * What a screen needs to know about a failure (README D9):
 * - `validation`: field errors to bind (`validation_failed`, `*_invalid_content`, ...);
 * - `auth`: the caller is not (or no longer) allowed (401/403 codes);
 * - `conflict`: a stale write, the answer is the ConflictDialog (412 + `concurrency_conflict`);
 * - `precondition`: a business-state refusal (409 guards, 413) — show `errors.<code>`;
 * - `rate_limit`: wait for `retryAfterSec`;
 * - `not_found`, `feature_disabled`: the resource or flag is absent;
 * - `client_bug`: the client forgot a header the contract requires (`If-Match`, `Idempotency-Key`);
 * - `network`: no response; `server`: 5xx; `unknown`: a code this client does not know.
 */
export type AppErrorKind =
  | 'validation'
  | 'auth'
  | 'conflict'
  | 'precondition'
  | 'rate_limit'
  | 'not_found'
  | 'feature_disabled'
  | 'client_bug'
  | 'network'
  | 'server'
  | 'unknown';

/** The typed form of any HTTP failure; built only by `AppErrorMapper`. Never carries text meant for the DOM. */
export interface AppError {
  /** A catalogue code, the alias `unauthenticated`, or `unknown_error` (an unknown wire code is never passed through). */
  readonly code: string;
  /** The HTTP status of the response (`0` when there was none); a status inside the body is not trusted. */
  readonly status: number;
  readonly kind: AppErrorKind;
  /** Pointer → messages, in payload order. Pointers are dotted paths (`segments.2.text`). Only string messages. */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
  /** From the `Retry-After` header, integer seconds only. */
  readonly retryAfterSec?: number;
  readonly traceId?: string;
  /** The sanitised body, kept for structured payloads (`keywords_flagged` rows). Its `detail`/`title`/`type` are never rendered. */
  readonly problem?: ProblemWire;
}

export function isValidation(error: AppError): boolean {
  return error.kind === 'validation';
}

/** A stale write (412 and `concurrency_conflict`): open the ConflictDialog. */
export function isConflict(error: AppError): boolean {
  return error.kind === 'conflict';
}

export function isRateLimited(error: AppError): boolean {
  return error.kind === 'rate_limit';
}

/** The client omitted a required `If-Match`/`Idempotency-Key` or reused a key: a bug, not something the user can fix. */
export function isClientBug(error: AppError): boolean {
  return error.kind === 'client_bug';
}
