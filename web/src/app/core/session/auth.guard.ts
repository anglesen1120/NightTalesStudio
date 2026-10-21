import { inject } from '@angular/core';
import { CanActivateChildFn, CanActivateFn, Router, UrlTree } from '@angular/router';
import { sanitizeReturnUrl } from './return-url';
import { SessionStore } from './session-store';

const LOGIN = '/login';
const DASHBOARD = '/dashboard';
const MFA_SETUP = '/account/security/mfa-setup';

// Every guard injects the store and the Router synchronously at entry, then awaits the memoized `restore()` Promise
// (never the boolean `restoreSettled()` view). Nothing calls `inject()` after an `await` (NG0203), and the decisions
// read the store's current `mode()`/`status()` once restore has settled, so a child navigation re-checks a session
// that ended while the shell stayed mounted.

function loginTree(router: Router, targetUrl: string): UrlTree {
  return router.createUrlTree([LOGIN], { queryParams: { returnUrl: sanitizeReturnUrl(targetUrl) } });
}

// `refreshing` is transient (an authenticated session mid-refresh) and is never a sign-out.
function hasFullSession(store: SessionStore): boolean {
  if (store.mode() === 'enrolment') {
    return false;
  }
  const status = store.status();
  return status === 'authenticated' || status === 'refreshing';
}

async function decideFullSession(store: SessionStore, router: Router, targetUrl: string): Promise<boolean | UrlTree> {
  await store.restore();
  if (store.mode() === 'enrolment') {
    return router.createUrlTree([MFA_SETUP]);
  }
  return hasFullSession(store) ? true : loginTree(router, targetUrl);
}

export const authGuard: CanActivateFn = (_route, state) => {
  const store = inject(SessionStore);
  const router = inject(Router);
  return decideFullSession(store, router, state.url);
};

export const authChildGuard: CanActivateChildFn = (_childRoute, state) => {
  const store = inject(SessionStore);
  const router = inject(Router);
  return decideFullSession(store, router, state.url);
};

// Guards `/account/security/mfa-setup`, which sits outside the full-session shell: a full session (an unenrolled
// admin sent here by `adminGuard`, or a user enrolling voluntarily) and an enrolment-only session may both enter.
export const mfaEnrolmentGuard: CanActivateFn = (_route, state) => {
  const store = inject(SessionStore);
  const router = inject(Router);
  return (async (): Promise<boolean | UrlTree> => {
    await store.restore();
    if (store.mode() === 'enrolment' || hasFullSession(store)) {
      return true;
    }
    return loginTree(router, state.url);
  })();
};

// Keeps authenticated users off `/login` and friends; an ended session may see `/login` (the banner lives there).
export const anonymousGuard: CanActivateFn = () => {
  const store = inject(SessionStore);
  const router = inject(Router);
  return (async (): Promise<boolean | UrlTree> => {
    await store.restore();
    if (store.mode() === 'enrolment') {
      return router.createUrlTree([MFA_SETUP]);
    }
    return hasFullSession(store) ? router.createUrlTree([DASHBOARD]) : true;
  })();
};
