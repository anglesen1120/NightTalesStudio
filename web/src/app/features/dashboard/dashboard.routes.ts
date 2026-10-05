import type { Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { provideEpisodesRepository } from '@features/episodes';

// The repository binding and the two scopes live on the route (lazy chunk). The page-scoped DashboardStore is
// provided on DashboardPage itself, so it is destroyed with the component, not cached with the route injector. The
// `episodes` scope carries the status labels the rows reuse; neither scope is prerendered (authenticated shell route),
// so neither belongs in bundled-translations.ts (mirrors episodes.routes.ts).
export const dashboardRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./feature/dashboard-page').then((module) => module.DashboardPage),
    providers: [provideEpisodesRepository(), provideTranslocoScope('dashboard'), provideTranslocoScope('episodes')],
  },
];
