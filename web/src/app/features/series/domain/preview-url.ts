// No Angular/rxjs/core imports in domain files (README D2). Pure URL guard for catalog preview URLs.

/**
 * Whitelist for a `previewUrl` before it ever reaches a media element (10.4.2-T2, 09.4.2-T1:144-146).
 *
 * Accepted:
 * - absolute `https:`/`http:` URLs (`URL` parses them; scheme casing is normalised by the parser);
 * - same-origin root-relative paths (`/…`), which are served under the app's own CSP media-src 'self'.
 *
 * Rejected (returns `null`, the no-preview path):
 * - `null`/empty and any string containing whitespace or C0/DEL control characters (header smuggling);
 * - protocol-relative `//host/…` and backslash paths (`\…`, `/\…`), which browsers resolve like `//`;
 * - `javascript:`, `data:`, `blob:` and every other non-http(s) scheme;
 * - relative paths without a leading slash (would resolve against the current route).
 *
 * Never paired with `bypassSecurityTrust*` (D4): a rejected URL is simply a missing preview.
 */
export function safePreviewUrl(url: string | null): string | null {
  if (url === null || url === '' || /[\s\u0000-\u001f\u007f]/.test(url)) return null;
  if (url.startsWith('/')) {
    return url.startsWith('//') || url.includes('\\') ? null : url;
  }
  if (url.includes('\\')) return null;
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? url : null;
  } catch {
    return null;
  }
}
