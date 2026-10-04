import { Routes } from '@angular/router';
import { publicRoutes } from '@features/public';

// Order: public (10.3.1-T1), then auth (10.3.3-T1) and the shell (10.2.6-T1) entries; `**` stays last.
export const routes: Routes = [...publicRoutes];
