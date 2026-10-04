export type PlatformRole = 'user' | 'admin' | 'owner';

/** The access token and its expiry (epoch ms). Held in memory only (README D6). */
export interface TokenPair {
  readonly accessToken: string;
  readonly expiresAt: number;
}

/** Identity facts read from the access token claims (`role`, `amr`). */
export interface SessionUser {
  readonly role: PlatformRole;
  readonly amr: readonly string[];
}

/** A token with less than this left is refreshed before use (matches `Auth:Access:ClockSkewSeconds` 30). */
export const FRESHNESS_MARGIN_MS = 30_000;
