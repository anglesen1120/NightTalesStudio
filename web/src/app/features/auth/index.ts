// Public surface of features/auth. `data-access/` (the generated-client adapter), `testing/` and the page/ui layers
// are deliberately not re-exported; the lazy `*.routes.ts` modules bind `AuthRepository` to its HTTP adapter, so
// nothing exported here pulls the adapter into the main bundle.
export { authRoutes } from './auth.routes';
export { AuthRepository } from './application/auth.repository';
export { LoginStore } from './application/login.store';
export type { LoginOutcome, LoginStep } from './domain/auth.model';
