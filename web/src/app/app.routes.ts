import { Routes } from '@angular/router';
import { authRoutes } from '@features/auth';
import { authChildGuard, authGuard } from '@core/session';
import { publicRoutes } from '@features/public';

// Ownership, in order: public/legal entries (10.3.1-T1), auth entries (10.3.3-T1), then the shell parent (10.2.6-T1),
// whose children are appended by each screen task: `episodes` (10.5.1-T1), `series` (10.4.2-T1). Each screen task appends its own child (and
// NAV_ITEMS entry) under the shell parent. The not-found `**` route (kept last) is deferred: no approved not-found
// design/copy yet (owner 2026-10-05).
export const routes: Routes = [
  ...publicRoutes,
  ...authRoutes,
  {
    path: '',
    loadComponent: () => import('./core/shell/shell.component').then((m) => m.ShellComponent),
    canActivate: [authGuard],
    canActivateChild: [authChildGuard],
    children: [
      { path: 'episodes', loadChildren: () => import('./features/episodes/episodes.routes').then((m) => m.episodesRoutes) },
      { path: 'series', loadChildren: () => import('./features/series/series.routes').then((m) => m.SERIES_ROUTES) },
    ],
  },
];
