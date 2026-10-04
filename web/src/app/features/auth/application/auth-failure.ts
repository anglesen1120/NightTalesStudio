import type { AppError } from '@core/errors';

/** Used when a 429 carries no usable `Retry-After`: the submit button must not stay disabled forever. */
export const DEFAULT_RETRY_AFTER_SEC = 60;

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

/** The seconds to wait after a 429 (`Retry-After`, else the default), or `null` for any other failure. */
export function retryAfterOf(error: AppError): number | null {
  return error.kind === 'rate_limit' ? (error.retryAfterSec ?? DEFAULT_RETRY_AFTER_SEC) : null;
}
