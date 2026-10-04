import { RenderMode, ServerRoute } from '@angular/ssr';

// Nothing is prerendered or server-rendered (README D1). 10.3.1-T1 prepends the four
// public/legal route entries.
export const serverRoutes: ServerRoute[] = [{ path: '**', renderMode: RenderMode.Client }];
