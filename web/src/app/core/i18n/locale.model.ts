// No Angular/rxjs imports in *.model.ts (README D2).

export type AppLocale = 'vi' | 'en';

/** The default language (README D10) and the last step of the resolution order. */
export const DEFAULT_LOCALE: AppLocale = 'vi';

/** BCP 47 tag each UI language formats with (README D10: `Intl` for `vi-VN` / `en-US`). */
export const LOCALE_TAG: Readonly<Record<AppLocale, 'vi-VN' | 'en-US'>> = { vi: 'vi-VN', en: 'en-US' };

export function isAppLocale(value: unknown): value is AppLocale {
  return value === 'vi' || value === 'en';
}
