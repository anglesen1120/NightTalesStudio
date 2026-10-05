import { type Router, NavigationCancel, NavigationEnd, NavigationError, NavigationSkipped } from '@angular/router';
import { filter, firstValueFrom } from 'rxjs';

/** The only legal channel of a reset token: `#t=<base64url>` and nothing else (never the query string, F03). */
const TOKEN_FRAGMENT = /^#t=([A-Za-z0-9_-]+)$/;

/** The token of a `#t=...` fragment, or `null` for an absent, empty, malformed or multi-parameter fragment. */
export function readTokenFromHash(hash: string): string | null {
  return TOKEN_FRAGMENT.exec(hash)?.[1] ?? null;
}

/**
 * Removes the fragment from the address bar (`history.replaceState(null, '', pathname + search)`). Browser only; a
 * server render or an environment without `history.replaceState` leaves the URL untouched and never throws.
 */
export function stripHash(): void {
  if (typeof location === 'undefined' || typeof history === 'undefined') return;
  if (typeof history.replaceState !== 'function') return;
  history.replaceState(null, '', location.pathname + location.search);
}

/**
 * Removes the fragment from the Router's own state (`router.url`, route snapshots, last navigation) with a
 * fragment-only `replaceUrl` navigation. It waits for a navigation in flight to settle first (the page is created
 * during one). A fragment-only change re-runs no guard and recreates no component.
 */
export async function clearRouterFragment(router: Router): Promise<void> {
  if (router.currentNavigation() !== null) {
    await firstValueFrom(
      router.events.pipe(
        filter((e) => e instanceof NavigationEnd || e instanceof NavigationCancel || e instanceof NavigationError || e instanceof NavigationSkipped),
      ),
    );
  }
  const tree = router.parseUrl(router.url);
  if (tree.fragment === null) return;
  tree.fragment = null;
  await router.navigateByUrl(tree, { replaceUrl: true });
}
