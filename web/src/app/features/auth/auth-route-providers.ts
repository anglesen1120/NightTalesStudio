import type { Provider } from '@angular/core';
import { provideTranslocoScope } from '@jsverse/transloco';
import { AuthRepository } from './application/auth.repository';
import { AuthApiRepository } from './data-access/auth.repository';

// The HTTP adapter bound to the port, and the `auth` copy scope (README D10). Imported only by the lazy `*.routes.ts`
// modules, so the adapter and the generated auth client stay out of the main bundle (README D13).
export const authRouteProviders: Provider[] = [
  { provide: AuthRepository, useClass: AuthApiRepository },
  provideTranslocoScope('auth'),
];
