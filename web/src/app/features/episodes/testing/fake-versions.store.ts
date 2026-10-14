import { signal } from '@angular/core';

/**
 * Inert `VersionsStore` stand-in for specs that replace the page's component providers (the page provides the real
 * store, so an override that swaps the others out must swap this one too). The page touches exactly one thing here: the
 * needs-reopen notice it clears once a reopen succeeded. Everything else the panel does stays with the real store.
 */
export function fakeVersionsStore() {
  return { needsReopen: signal(false) };
}
