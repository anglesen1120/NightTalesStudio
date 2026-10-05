import type { Result } from '@core/errors';

/**
 * The re-authentication port (domain side, no Angular). `reauth` never rejects: every failure resolves as
 * `{ ok: false, error }` with the `AppError` built by `AppErrorMapper`, passed through unchanged (WR9).
 * `ReauthApiRepository` implements it, `FakeReauthRepository` scripts it in specs. `code` is `''` for an account
 * without TOTP; the password and the code are never logged or stored.
 */
export abstract class ReauthPort {
  /** `POST /auth/reauth`. */
  abstract reauth(password: string, code: string): Promise<Result<{ reauthenticatedAt: string }>>;
}
