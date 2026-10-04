// Public surface of core/session. `testing/` (FakeSessionApi) and `data-access/` (the only importer of the generated
// client, lint D5) are deliberately not re-exported; `provideSession()` wires the repository.
export * from './activity';
export * from './auth.guard';
export * from './me-summary';
export * from './provide-session';
export * from './refresh-lock';
export * from './return-url';
export * from './session-end';
export * from './session-scope';
export * from './session-store';
export * from './session.api';
export * from './session.interceptor';
export * from './token-pair';
