import { EnvironmentProviders, isDevMode } from '@angular/core';
import { provideTransloco, provideTranslocoMissingHandler } from '@jsverse/transloco';
import { MissingKeyHandler } from './missing-key.handler';
import { TranslocoHttpLoader } from './transloco.loader';

/**
 * Transloco for the app (README D10): `vi` default, `en` second language, scoped JSON loaded per route.
 * `fallbackLang` is the second language of the pair; `useFallbackTranslation` stays off so a lazy route loads
 * exactly its scope's file for the active language and never shows the other language as if it were translated.
 */
export function provideAppI18n(): EnvironmentProviders[] {
  return [
    ...provideTransloco({
      config: {
        availableLangs: ['vi', 'en'],
        defaultLang: 'vi',
        fallbackLang: 'en',
        reRenderOnLangChange: true,
        prodMode: !isDevMode(),
        failedRetries: 0,
        missingHandler: { logMissingKey: false, useFallbackTranslation: false, allowEmpty: false },
      },
      loader: TranslocoHttpLoader,
    }),
    provideTranslocoMissingHandler(MissingKeyHandler),
  ];
}
