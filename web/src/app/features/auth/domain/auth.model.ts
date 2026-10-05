// Pure client model of the auth feature. The domain layer imports nothing (lint D2): the `AuthRepository` port returns
// `Result<T>`/`AppError` from `@core/errors`, so it lives in `application/auth.repository.ts` instead.

/** Same three values as `PlatformRole` of `core/session`; redeclared because domain may not import `@core`. */
export type AuthRole = 'user' | 'admin' | 'owner';

/** The signed-in result of a password (or later MFA) login. `expiresAt` is epoch ms. */
export interface Session {
  readonly accessToken: string;
  readonly expiresAt: number;
  readonly role: AuthRole;
  readonly amr: readonly string[];
}

export type LoginOutcome =
  | { readonly kind: 'ok'; readonly session: Session }
  | { readonly kind: 'mfa'; readonly mfaToken: string }
  | { readonly kind: 'enrolment'; readonly token: string };

/** T3 (10.3.3-T3) widens this to `'password' | 'mfa'`. */
export type LoginStep = 'password';
