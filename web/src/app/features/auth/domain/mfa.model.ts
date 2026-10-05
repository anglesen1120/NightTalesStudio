// Pure client model of the MFA step and enrolment (10.3.3-T3). Imports nothing (lint D2). These are the immutable
// presentation contracts between the route containers (feature/) and the presentational components (ui/).

/** `POST /me/mfa/setup` result (09.10.4-T1): base32 secret + `otpauth://totp/NightTales:…` URI. Never logged or stored. */
export interface MfaSetup {
  readonly secret: string;
  readonly otpauthUri: string;
}

/** The 10 one-time recovery codes from `verify` or regeneration; shown exactly once. */
export type RecoveryCodes = readonly string[];

/** Which body field the MFA login sends: `code` (TOTP) or `recoveryCode`; never both (C10). */
export type MfaCodeMode = 'totp' | 'recovery';

/** Enrolment page step (plan §3 `EnrolmentStore`). `done` = codes confirmed saved. */
export type EnrolmentStep = 'loading' | 'show' | 'verify' | 'codes' | 'done' | 'error';

/** View model of the MFA block inside `/login`; the code `FormGroup` is passed separately (as in `LoginFormComponent`). */
export interface MfaChallengeViewModel {
  readonly recoveryMode: boolean;
  readonly submitting: boolean;
  readonly canSubmit: boolean;
  /** i18n key of the non-field alert, or `null`. */
  readonly formError: string | null;
  readonly traceId: string | null;
  /** `Retry-After` seconds of a 429; `null` when not rate-limited. */
  readonly retryAfterSec: number | null;
}

/** View model of `/account/security/mfa-setup`; the verify-code `FormGroup` is passed separately. */
export interface MfaSetupViewModel {
  readonly step: EnrolmentStep;
  /** Present on `show`/`verify`; `null` once codes are revealed or after destroy. */
  readonly setup: MfaSetup | null;
  /** Present only on `codes`; `null` after `confirmSaved`. */
  readonly codes: RecoveryCodes | null;
  /** Entered with the enrolment-only token: after verify the only way on is `/login` (C6). */
  readonly enrolmentOnly: boolean;
  readonly submitting: boolean;
  readonly canSubmit: boolean;
  /** i18n key of the non-field alert, or `null`. */
  readonly formError: string | null;
  readonly traceId: string | null;
  readonly retryAfterSec: number | null;
  readonly confirmedSaved: boolean;
  /** A re-auth dialog was cancelled: show the "re-auth required" state with a retry button (never a silent failure). */
  readonly reauthRequired: boolean;
  /** The enrolment token is spent or expired: show `auth.enrol.reLogin` and the sign-in-again action. */
  readonly reLoginRequired: boolean;
}
