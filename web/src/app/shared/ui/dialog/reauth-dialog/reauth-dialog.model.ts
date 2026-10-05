// Presentation contract of the shared `ReauthDialog` (10.3.3-T3, D11). The dialog knows no API: `ReauthService`
// (core/session) opens it with `MatDialog`, pushes `state` through `componentRef.setInput`, listens to `submitted`, owns
// the `POST /auth/reauth`, and closes it. Escape / cancel close it with no result and no request.

/** `MatDialog` data passed on open. */
export interface ReauthDialogData {
  /** `false` hides the code field (account without TOTP). Owners/admins always have TOTP in P. */
  readonly requireCode: boolean;
}

/** What the dialog emits on submit. `code` is `''` when `requireCode` is `false`. Never logged or stored. */
export interface ReauthDialogResult {
  readonly password: string;
  readonly code: string;
}

/** Live state pushed by the service while the dialog is open. */
export interface ReauthDialogState {
  readonly submitting: boolean;
  /** i18n key of the error (`errors.<code>` or `reauth.failed`), or `null`. */
  readonly errorKey: string | null;
  /** Which control the error belongs to; `null` = form-level alert. */
  readonly errorField: 'password' | 'code' | null;
  readonly traceId: string | null;
  /** `Retry-After` seconds of a 429; submit disabled while it counts down. */
  readonly retryAfterSec: number | null;
}

export const REAUTH_DIALOG_IDLE: ReauthDialogState = {
  submitting: false,
  errorKey: null,
  errorField: null,
  traceId: null,
  retryAfterSec: null,
};
