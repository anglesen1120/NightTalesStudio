import { RenderMode, ServerRoute } from '@angular/ssr';

// Only the four public/legal pages are prerendered (README D1, 10.3.1-T1); everything else stays a client route.
// The `/privacy` and `/terms` redirects need no entry: `**` covers them and @angular/ssr emits a redirect for a
// non-prerendered route that has `redirectTo`.
export const serverRoutes: ServerRoute[] = [
  { path: 'vi/privacy', renderMode: RenderMode.Prerender },
  { path: 'en/privacy', renderMode: RenderMode.Prerender },
  { path: 'vi/terms', renderMode: RenderMode.Prerender },
  { path: 'en/terms', renderMode: RenderMode.Prerender },
  { path: '**', renderMode: RenderMode.Client },
];
