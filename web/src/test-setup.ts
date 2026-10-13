// Global setup for the Vitest unit tests (10.1.5-T1, README D15).
//
// `@angular/build:unit-test` already runs `TestBed.initTestEnvironment(BrowserTestingModule,
// platformBrowserTesting(), ...)` (zoneless: no zone.js is installed, teardown after each test) before any
// setup file, and a second call would throw. This file only holds what the builder does not do: the builder
// runs every spec file in one jsdom (`isolate: false`), so browser storage must not leak from one test to
// the next. The optional chaining in the helper covers specs that stub `localStorage` away on purpose; the helper
// lives in core/prefs because that is the only place D2/D6 allows browser storage access.
import { clearBrowserStorage } from './app/core/prefs/clear-browser-storage';

/**
 * jsdom implements no `IntersectionObserver`, and an `@defer (on viewport)` block (the annotation panel, 10.5.10-T1)
 * starts one over its host as soon as the block renders, inside the after-render phase — without the global that
 * trigger throws. The stub only exists so the trigger can be set up: it never reports an intersection, because a
 * deferred block is rendered eagerly in tests (`DeferBlockBehavior.Playthrough`), never on a viewport event.
 */
class NoopIntersectionObserver implements IntersectionObserver {
  readonly root: Element | Document | null = null;
  readonly rootMargin: string = '';
  readonly scrollMargin: string = '';
  readonly thresholds: readonly number[] = [];
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
  takeRecords(): IntersectionObserverEntry[] { return []; }
}
if (typeof globalThis.IntersectionObserver === 'undefined') {
  globalThis.IntersectionObserver = NoopIntersectionObserver;
}

afterEach(clearBrowserStorage);
