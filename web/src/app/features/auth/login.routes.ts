import type { Routes } from '@angular/router';
import { authRouteProviders } from './auth-route-providers';
import { LoginPageComponent } from './feature/login-page/login-page.component';

export default [{ path: '', providers: authRouteProviders, component: LoginPageComponent }] satisfies Routes;
