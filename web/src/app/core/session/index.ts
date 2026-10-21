// Public surface of core/session. `testing/` (FakeSessionApi, FakeReauthRepository) and `data-access/` (the only
// importers of the generated client, lint D5) are deliberately not re-exported; `provideSession()` wires the repositories.
export * from './activity';
export * from './auth.guard';
export * from './me-summary';
export * from './me.repository';
export * from './provide-session';
export * from './reauth.port';
export * from './reauth.service';
export * from './refresh-lock';
export * from './return-url';
export * from './session-end';
export * from './session-scope';
export * from './session-free-path';
export * from './session-store';
export * from './session.api';
export * from './session.interceptor';
export * from './token-pair';
