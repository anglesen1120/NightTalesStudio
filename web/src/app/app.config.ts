import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideBrowserGlobalErrorListeners,
  provideEnvironmentInitializer,
  provideZonelessChangeDetection,
} from '@angular/core';
import { APP_BASE_HREF } from '@angular/common';
import { provideRouter } from '@angular/router';
import { LocaleService, provideAppI18n } from '@core/i18n';
import { PreferencesStore, ThemeStore } from '@core/prefs';
import { provideRealtime } from '@core/realtime';
import { provideSession, provideSessionFreePaths, sessionInterceptor } from '@core/session';
import { idempotencyInterceptor } from '@core/http';
import { isSessionFreePublicPath } from '@features/public';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    // `base-uri 'none'` (D4): the document emits no <base>; assets are root-relative via `deployUrl: "/"`.
    { provide: APP_BASE_HREF, useValue: '/' },
    provideRouter(routes),
    // Same-origin HttpClient for the i18n loader and the API. `idempotencyInterceptor` runs before `sessionInterceptor`
    // so the single 401 replay keeps the Idempotency-Key header.
    provideHttpClient(withFetch(), withInterceptors([idempotencyInterceptor, sessionInterceptor])),
    provideSession(),
    // The legal pages issue no /api request: the boot refresh is skipped when the browser lands on one.
    provideSessionFreePaths(isSessionFreePublicPath),
    provideRealtime(),
    provideAppI18n(),
    // Read preferences once, then apply the validated theme and the initial language before the first view renders.
    // The profile locale (`GET /me`) is not known yet; the shell re-activates it once `me()` is set.
    provideEnvironmentInitializer(() => {
      const prefs = inject(PreferencesStore);
      prefs.read();
      inject(ThemeStore).init();
      // The server platform (prerender) never reads the build host's locale: `resolveStartup` yields `vi` there.
      const locale = inject(LocaleService);
      locale.activate(locale.resolveStartup(null), { persist: false });
    }),
  ],
};
