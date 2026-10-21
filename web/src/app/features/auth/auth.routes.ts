import type { Route, Routes } from '@angular/router';
import { anonymousGuard, mfaEnrolmentGuard } from '@core/session';

// Only the path table lives in the main bundle. Each path lazy-loads its own `*.routes.ts`, which carries the page, the
// `AuthRepository` -> `AuthApiRepository` binding and the `auth` scope (see auth-route-providers.ts). The generated
// AuthEndpointsClient is shared with core/session, so it remains in the main bundle (README D13). Not an empty-path
// loadChildren: that would be tried for every URL.
const page = (path: string, loadChildren: Route['loadChildren']): Route => ({
  path,
  canActivate: [anonymousGuard],
  loadChildren,
});

// `/account/security/mfa-setup` sits outside the full-session shell and carries the SINGLE `mfaEnrolmentGuard`, which
// accepts a full session or the enrolment-only token. It must never be combined with `authGuard` (canActivate is AND:
// the enrolment-only owner would be bounced to `/login`) nor with `anonymousGuard`.
export const authRoutes: Routes = [
  page('login', () => import('./login.routes')),
  page('forgot-password', () => import('./forgot-password.routes')),
  page('reset-password', () => import('./reset-password.routes')),
  {
    path: 'account/security/mfa-setup',
    canActivate: [mfaEnrolmentGuard],
    loadChildren: () => import('./mfa-setup.routes'),
  },
];
