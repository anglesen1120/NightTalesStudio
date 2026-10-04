import { Routes } from '@angular/router';
import { authRoutes } from '@features/auth';

// Public entries (10.3.1-T1, S15) go before the auth routes; the shell and the `**` not-found route (10.2.6-T1, S13) go after.
export const routes: Routes = [...authRoutes];
