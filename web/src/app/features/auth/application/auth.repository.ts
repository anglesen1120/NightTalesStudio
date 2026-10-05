import type { Result } from '@core/errors';
import type { LoginOutcome } from '../domain/auth.model';

/**
 * The auth port (abstract class = DI token). Declared here, not in `domain/`, because it returns the core-wide
 * `Result<T>` and domain may not import `@core` (lint D2). The HTTP adapter is `data-access/auth.repository.ts`,
 * the scripted fake `testing/fake-auth.repository.ts`. `loginMfa` is added by 10.3.3-T3.
 */
export abstract class AuthRepository {
  abstract login(email: string, password: string): Promise<Result<LoginOutcome>>;
  abstract forgotPassword(email: string): Promise<Result<void>>;
  abstract resetPassword(token: string, password: string): Promise<Result<void>>;
  abstract logout(): Promise<Result<void>>;
}
