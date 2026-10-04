import type { Route, Routes } from '@angular/router';
import { anonymousGuard } from '@core/session';

// Only the path table lives in the main bundle. Each path lazy-loads its own `*.routes.ts`, which carries the page, the
// `AuthRepository` -> `AuthApiRepository` binding and the `auth` scope (see auth-route-providers.ts), so the adapter and
// the generated client land in lazy chunks (README D13). Not an empty-path loadChildren: that would be tried for every URL.
const page = (path: string, loadChildren: Route['loadChildren']): Route => ({
  path,
  canActivate: [anonymousGuard],
  loadChildren,
});

export const authRoutes: Routes = [
  page('login', () => import('./login.routes')),
  page('forgot-password', () => import('./forgot-password.routes')),
  page('reset-password', () => import('./reset-password.routes')),
];
