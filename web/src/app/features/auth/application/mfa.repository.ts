import type { Result } from '@core/errors';
import type { MfaSetup, RecoveryCodes } from '../domain/mfa.model';

/**
 * The MFA-management port (abstract class = DI token). Declared in `application/`, not `domain/mfa.port.ts` as the
 * task text says, because it returns the core-wide `Result<T>` and domain may not import `@core` (lint D2), exactly
 * like `AuthRepository`. The HTTP adapter is `data-access/mfa.repository.ts`, the scripted fake
 * `testing/fake-mfa.repository.ts`. None of these results is ever logged or stored (secret, URI, recovery codes).
 */
export abstract class MfaRepository {
  /** `POST /me/mfa/setup` → base32 secret + `otpauth://` URI. */
  abstract setup(): Promise<Result<MfaSetup>>;
  /** `POST /me/mfa/verify { code }` → the 10 recovery codes (shown once). */
  abstract verify(code: string): Promise<Result<RecoveryCodes>>;
  /** `POST /me/mfa/recovery-codes` → a fresh set of 10 (the caller re-authenticates first). */
  abstract regenerateRecoveryCodes(): Promise<Result<RecoveryCodes>>;
}
