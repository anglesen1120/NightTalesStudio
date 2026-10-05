import type { Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { MfaRepository } from './application/mfa.repository';
import { MfaApiRepository } from './data-access/mfa.repository';
import { MfaSetupPageComponent } from './feature/mfa-setup-page/mfa-setup-page.component';

// The MFA HTTP adapter and the `auth` scope are bound here, so they stay out of the main bundle. The page provides its
// own `EnrolmentStore` (one instance per visit, destroyed with the route).
export default [
  {
    path: '',
    providers: [{ provide: MfaRepository, useClass: MfaApiRepository }, provideTranslocoScope('auth')],
    component: MfaSetupPageComponent,
  },
] satisfies Routes;
