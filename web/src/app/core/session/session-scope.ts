import { InjectionToken } from '@angular/core';

/** A root-level store that holds per-user state and must forget it when the session ends. */
export interface SessionScoped {
  reset(): void;
}

/** Multi-provider: every root-level feature store registers itself here; `SessionStore.signOut` resets each once. */
export const SESSION_SCOPED = new InjectionToken<readonly SessionScoped[]>('SESSION_SCOPED');
