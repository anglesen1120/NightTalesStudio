import type { Provider } from '@angular/core';
import { provideTranslocoScope } from '@jsverse/transloco';
import { AuthRepository } from './application/auth.repository';
import { AuthApiRepository } from './data-access/auth.repository';

// The HTTP adapter and auth pages are lazy-loaded, but the generated AuthEndpointsClient is shared with core/session
// (SessionRepository injects it), so that client remains in the main bundle.
export const authRouteProviders: Provider[] = [
  { provide: AuthRepository, useClass: AuthApiRepository },
  provideTranslocoScope('auth'),
];
