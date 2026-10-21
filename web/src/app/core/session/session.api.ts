import type { TokenPair } from './token-pair';

/**
 * The session port (domain side, no Angular). Both operations REJECT with an `AppError` (built by `AppErrorMapper`)
 * on failure; `HttpSessionRepository` implements it, `FakeSessionApi` scripts it in specs.
 */
export abstract class SessionApi {
  /** `POST /auth/refresh` with the HttpOnly cookie; resolves the new access token. */
  abstract refresh(): Promise<TokenPair>;
  /**
   * `POST /auth/logout` with the cookie AND the access token as `Bearer` (`null`: the POST still clears the browser's
   * cookie copy and never rejects). Resolves on 204 or 401 `session_revoked`; any other failure with a token rejects.
   */
  abstract logout(accessToken: string | null): Promise<void>;
}
