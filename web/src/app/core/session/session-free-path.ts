import { EnvironmentProviders, InjectionToken, makeEnvironmentProviders } from '@angular/core';

/**
 * Decides whether the page the browser booted on needs no session (public, prerendered pages). The boot initializer
 * skips the refresh call for such a path; the memoized `restore()` still runs on the first guarded navigation.
 * Takes `location.pathname` (no query, no fragment). The default is "no path is session-free".
 */
export type SessionFreePathPredicate = (path: string) => boolean;

export const SESSION_FREE_PATH = new InjectionToken<SessionFreePathPredicate>('SESSION_FREE_PATH', {
  providedIn: 'root',
  factory: () => () => false,
});

export function provideSessionFreePaths(predicate: SessionFreePathPredicate): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: SESSION_FREE_PATH, useValue: predicate }]);
}
