import { Routes } from '@angular/router';
import { authChildGuard, authGuard } from '@core/session';

// Ownership, in order: public/legal entries (10.3.1-T1), auth entries (10.3.3-T1), then the shell parent (10.2.6-T1).
// Each screen task appends its own child (and NAV_ITEMS entry) under the shell parent. The not-found `**` route (kept
// last) is deferred: no approved not-found design/copy yet (owner 2026-10-05).
export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./core/shell/shell.component').then((m) => m.ShellComponent),
    canActivate: [authGuard],
    canActivateChild: [authChildGuard],
    children: [],
  },
];
