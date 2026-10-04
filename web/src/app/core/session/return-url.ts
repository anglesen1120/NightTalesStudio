/** Where a missing, foreign or looping `returnUrl` goes. */
const FALLBACK_URL = '/dashboard';

/** A same-origin absolute path: one leading `/`, never `//` or `/\` (protocol-relative), no whitespace. */
const SAFE_PATH = /^\/(?![/\\])[^\s]*$/;

/** C0 controls and DEL: browsers strip some of them from URLs, so `/\u0001/evil` must not survive. */
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/;

/** Pages a signed-out user lands on; returning to one of them after sign-in would loop. */
const AUTH_PAGES: readonly string[] = ['/login', '/forgot-password', '/reset-password', '/verify-email'];

function isSafe(url: string): boolean {
  return SAFE_PATH.test(url) && !CONTROL_CHARACTERS.test(url);
}

/**
 * The only way a `returnUrl` query parameter (or `Router.url`) becomes a navigation target (README D6): an in-app path
 * or `/dashboard`. The raw value and its percent-decoded form must both be safe, so `/%2F%2Fevil` and `/%09/evil`
 * cannot turn into a protocol-relative URL once something decodes them. Query and fragment are kept when the path is
 * allowed. The auth pages are refused with any query, fragment, trailing slash or letter case.
 */
export function sanitizeReturnUrl(raw: string | null): string {
  if (raw === null || !isSafe(raw)) return FALLBACK_URL;
  let decoded: string;
  try {
    decoded = decodeURIComponent(raw);
  } catch {
    return FALLBACK_URL;
  }
  if (!isSafe(decoded)) return FALLBACK_URL;
  const path = (decoded.split(/[?#;]/, 1)[0] ?? '').toLowerCase().replace(/\/+$/, '');
  return AUTH_PAGES.includes(path) ? FALLBACK_URL : raw;
}
