/**
 * The routes the CSP spec visits (task 10.1.6-T1). Each later feature appends the routes it adds: the prerendered
 * legal pages with 10.3.1-T1, login with 10.3.3-T1. Because every spec runs under the CSP fixture, the runtime
 * proof grows with the app; the first page that renders Material or opens the hub is verified by construction.
 *
 * Today the container serves the SPA shell on `/` and the client fallback (`index.csr.html`) on any other path,
 * so a deep route proves deep-link reload and lazy-asset loading under `base-uri 'none'`.
 */
export const CSP_ROUTES: readonly string[] = [
  '/',
  '/dashboard/episodes/42',
  '/vi/privacy',
  '/en/privacy',
  '/vi/terms',
  '/en/terms',
];
