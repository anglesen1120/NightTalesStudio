// Public surface of features/dashboard. `ui/`, `testing/` and `dashboard.model.ts` are not re-exported; the lazy
// `dashboard.routes.ts` owns the bindings, and `app.routes.ts` only needs the route table.
export { dashboardRoutes } from './dashboard.routes';
export { DashboardPage } from './feature/dashboard-page';
export { DashboardStore } from './application/dashboard.store';
