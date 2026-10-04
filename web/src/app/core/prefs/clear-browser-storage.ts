/**
 * Empties `localStorage` and `sessionStorage`. Browser storage is only touched inside core/prefs (README D2/D6), so
 * the global Vitest setup (`src/test-setup.ts`) calls this instead of reaching for the storage objects itself.
 * The optional chaining covers specs that stub a storage object away on purpose.
 */
export function clearBrowserStorage(): void {
  globalThis.localStorage?.clear();
  globalThis.sessionStorage?.clear();
}
