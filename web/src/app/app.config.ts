import {
  ApplicationConfig,
  inject,
  provideBrowserGlobalErrorListeners,
  provideEnvironmentInitializer,
  provideZonelessChangeDetection,
} from '@angular/core';
import { APP_BASE_HREF } from '@angular/common';
import { provideRouter } from '@angular/router';
import { PreferencesStore, ThemeStore } from '@core/prefs';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    // `base-uri 'none'` (D4): the document emits no <base>; assets are root-relative via `deployUrl: "/"`.
    { provide: APP_BASE_HREF, useValue: '/' },
    provideRouter(routes),
    // Read preferences once, then apply the validated theme before the first view renders.
    provideEnvironmentInitializer(() => {
      inject(PreferencesStore).read();
      inject(ThemeStore).init();
    }),
  ],
};
