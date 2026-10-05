import { LEGAL_DOCS, LEGAL_LANGS } from './domain/legal.model';

const SESSION_FREE_PATHS: ReadonlySet<string> = new Set([
  ...LEGAL_DOCS.map((doc) => `/${doc}`),
  ...LEGAL_LANGS.flatMap((lang) => LEGAL_DOCS.map((doc) => `/${lang}/${doc}`)),
]);

/**
 * True for exactly the legal pages (`/privacy`, `/terms`, `/vi|en/privacy|terms`, one optional trailing slash). These
 * pages render build-time content and must issue no `/api` request, so the boot refresh is skipped for them.
 * Takes `location.pathname`; a query or fragment never reaches it.
 */
export function isSessionFreePublicPath(path: string): boolean {
  return SESSION_FREE_PATHS.has(path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path);
}
