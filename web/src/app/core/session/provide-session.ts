import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import {
  DestroyRef,
  EnvironmentProviders,
  PLATFORM_ID,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import { ActivityTracker } from './activity';
import { HttpSessionRepository } from './data-access/session.repository';
import { SessionApi } from './session.api';
import { SessionStore } from './session-store';

const FIRST_GESTURE_EVENTS = ['pointerdown', 'keydown'] as const;

// Registers the HTTP-backed `SessionApi` and the boot initializer (README D6). The initializer returns the memoized
// `restore()` Promise, as the Design prescribes: first render waits for the single boot refresh, which never rejects
// (401/network/timeout settle anonymous), and the guards await the same Promise. The server platform (prerender)
// never refreshes. `ActivityTracker.start()` runs on the first user gesture only, via a one-shot listener pair that
// removes itself (and is removed on injector destroy if no gesture ever arrives).
export function provideSession(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: SessionApi, useClass: HttpSessionRepository },
    provideAppInitializer(() => {
      if (!isPlatformBrowser(inject(PLATFORM_ID))) {
        return;
      }
      const store = inject(SessionStore);
      const tracker = inject(ActivityTracker);
      const doc = inject(DOCUMENT);
      const destroyRef = inject(DestroyRef);

      const detach = (): void => {
        for (const type of FIRST_GESTURE_EVENTS) {
          doc.removeEventListener(type, onFirstGesture, true);
        }
      };
      function onFirstGesture(): void {
        detach();
        tracker.start();
      }
      for (const type of FIRST_GESTURE_EVENTS) {
        doc.addEventListener(type, onFirstGesture, { capture: true, passive: true });
      }
      destroyRef.onDestroy(detach);

      return store.restore();
    }),
  ]);
}
