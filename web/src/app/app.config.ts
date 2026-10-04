import {
  ApplicationConfig,
  inject,
  provideBrowserGlobalErrorListeners,
  provideEnvironmentInitializer,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { PreferencesStore, ThemeStore } from '@core/prefs';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes),
    // Read preferences once, then apply the validated theme before the first view renders.
    provideEnvironmentInitializer(() => {
      inject(PreferencesStore).read();
      inject(ThemeStore).init();
    }),
  ],
};
