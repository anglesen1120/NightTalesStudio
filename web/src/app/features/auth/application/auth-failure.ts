import type { AppError } from '@core/errors';

const KEY_BY_CODE: Readonly<Record<string, string>> = {
  invalid_credentials: 'auth.login.invalidCredentials',
  email_not_verified: 'auth.login.emailNotVerified',
  rate_limited: 'auth.rateLimited.message',
  network_error: 'errors.network_error',
};

/** Field-level errors are bound onto the form controls by the container, so they have no form-level alert. */
export function hasFieldErrors(error: AppError): boolean {
  return Object.keys(error.fieldErrors).length > 0;
}

/**
 * The i18n key of the non-field alert, or `null` when the error is bound to controls. It depends on the error code
 * only, never on `detail`/`title`, so an unknown e-mail and a wrong password render identically (no enumeration).
 */
export function formErrorKey(error: AppError): string | null {
  if (hasFieldErrors(error)) return null;
  return KEY_BY_CODE[error.code] ?? `errors.${error.code}`;
}

/**
 * The seconds to wait after a 429, read from `Retry-After` only; `null` for any other failure and for a 429 without a
 * usable header. No number is invented: that case shows the plain rate-limited alert and leaves submit enabled.
 */
export function retryAfterOf(error: AppError): number | null {
  return error.kind === 'rate_limit' ? (error.retryAfterSec ?? null) : null;
}

/** The support trace id of a server-side or unclassified failure (design: 500 shows a trace; 4xx and 429 do not). */
export function traceIdOf(error: AppError | null): string | null {
  if (error === null || error.traceId === undefined) return null;
  return error.kind === 'server' || error.code === 'unknown_error' ? error.traceId : null;
}
