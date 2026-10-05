import type { Routes } from '@angular/router';
import { authRouteProviders } from './auth-route-providers';
import { ForgotPasswordPageComponent } from './feature/forgot-password-page/forgot-password-page.component';

export default [{ path: '', providers: authRouteProviders, component: ForgotPasswordPageComponent }] satisfies Routes;
