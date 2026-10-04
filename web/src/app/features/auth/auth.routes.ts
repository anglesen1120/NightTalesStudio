import type { Route, Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { anonymousGuard } from '@core/session';
import { AuthRepository } from './application/auth.repository';
import { AuthApiRepository } from './data-access/auth.repository';

// One shared provider list: the HTTP adapter bound to the port, and the `auth` copy scope (README D10). Route-level
// providers create one environment injector per route, loaded with the lazy page; nothing lands in the main bundle.
const providers = [{ provide: AuthRepository, useClass: AuthApiRepository }, provideTranslocoScope('auth')];

const page = (path: string, loadComponent: Route['loadComponent']): Route => ({
  path,
  canActivate: [anonymousGuard],
  providers,
  loadComponent,
});

export const authRoutes: Routes = [
  page('login', () => import('./feature/login-page/login-page.component').then((m) => m.LoginPageComponent)),
  page('forgot-password', () =>
    import('./feature/forgot-password-page/forgot-password-page.component').then((m) => m.ForgotPasswordPageComponent),
  ),
  page('reset-password', () =>
    import('./feature/reset-password-page/reset-password-page.component').then((m) => m.ResetPasswordPageComponent),
  ),
];
