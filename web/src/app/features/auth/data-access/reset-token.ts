/** The only legal channel of a reset token: `#t=<base64url>` and nothing else (never the query string, F03). */
const TOKEN_FRAGMENT = /^#t=([A-Za-z0-9_-]+)$/;

/** The token of a `#t=...` fragment, or `null` for an absent, empty, malformed or multi-parameter fragment. */
export function readTokenFromHash(hash: string): string | null {
  return TOKEN_FRAGMENT.exec(hash)?.[1] ?? null;
}

/**
 * Removes the fragment from the address bar (`history.replaceState(null, '', pathname + search)`). Browser only; a
 * server render or an environment without `history.replaceState` leaves the URL untouched and never throws.
 */
export function stripHash(): void {
  if (typeof location === 'undefined' || typeof history === 'undefined') return;
  if (typeof history.replaceState !== 'function') return;
  history.replaceState(null, '', location.pathname + location.search);
}
