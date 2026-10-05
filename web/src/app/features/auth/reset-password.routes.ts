import type { Routes } from '@angular/router';
import { authRouteProviders } from './auth-route-providers';
import { ResetPasswordPageComponent } from './feature/reset-password-page/reset-password-page.component';

export default [{ path: '', providers: authRouteProviders, component: ResetPasswordPageComponent }] satisfies Routes;
