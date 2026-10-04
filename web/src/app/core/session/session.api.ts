import type { TokenPair } from './token-pair';

/**
 * The session port (domain side, no Angular). Both operations REJECT with an `AppError` (built by `AppErrorMapper`)
 * on failure; `HttpSessionRepository` implements it, `FakeSessionApi` scripts it in specs.
 */
export abstract class SessionApi {
  /** `POST /auth/refresh` with the HttpOnly cookie; resolves the new access token. */
  abstract refresh(): Promise<TokenPair>;
  /** `POST /auth/logout`; a 401 (already logged out) resolves. */
  abstract logout(): Promise<void>;
}
