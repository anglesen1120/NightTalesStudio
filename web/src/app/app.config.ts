import { provideHttpClient, withFetch } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideBrowserGlobalErrorListeners,
  provideEnvironmentInitializer,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { LocaleService, provideAppI18n } from '@core/i18n';
import { PreferencesStore, ThemeStore } from '@core/prefs';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes),
    // Same-origin HttpClient for the i18n loader; interceptors and the generated client arrive with 10.1.4-T1.
    provideHttpClient(withFetch()),
    provideAppI18n(),
    // Read preferences once, then apply the validated theme and the initial language before the first view renders.
    // The profile locale (`GET /me`) is not known yet; the shell re-activates it once `me()` is set.
    provideEnvironmentInitializer(() => {
      const prefs = inject(PreferencesStore);
      prefs.read();
      inject(ThemeStore).init();
      const locale = inject(LocaleService);
      const browserLangs = typeof navigator === 'undefined' ? [] : navigator.languages;
      locale.activate(locale.resolveInitial(null, prefs.savedUiLanguage(), browserLangs), { persist: false });
    }),
  ],
};
