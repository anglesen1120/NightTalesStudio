import { TranslocoTestingModule } from '@jsverse/transloco';
import en from '../../../../assets/i18n/core/en.json';
import vi from '../../../../assets/i18n/core/vi.json';

export function uiTestingTranslations(activeLang: 'vi' | 'en' = 'vi') {
  return TranslocoTestingModule.forRoot({
    langs: { vi, en },
    translocoConfig: {
      availableLangs: ['vi', 'en'],
      defaultLang: activeLang,
      fallbackLang: activeLang,
      reRenderOnLangChange: true,
    },
    preloadLangs: true,
  });
}

export { en, vi };
