import type { Translation } from '@jsverse/transloco';
import coreEn from '../../../assets/i18n/core/en.json';
import coreVi from '../../../assets/i18n/core/vi.json';
import publicEn from '../../../assets/i18n/public/en.json';
import publicVi from '../../../assets/i18n/public/vi.json';
import type { AppLocale } from './locale.model';

/**
 * Scopes a prerendered route may use: a prerender cannot fetch `/assets/i18n/**` (README D12/F18), so these are
 * bundled. `PRERENDER_SAFE_SCOPES` is derived from this map, so the list can never name a scope that is not
 * actually bundled: `public` (10.3.1-T1) is bundled here; `errors` joins when 10.2.2-T1 lands
 * its files. A route outside this list must not be prerendered.
 */
const BUNDLED = {
  core: { vi: coreVi, en: coreEn },
  public: { vi: publicVi, en: publicEn },
} satisfies Record<string, Readonly<Record<AppLocale, Translation>>>;

export type PrerenderSafeScope = keyof typeof BUNDLED;

export const PRERENDER_SAFE_SCOPES = Object.keys(BUNDLED) as readonly PrerenderSafeScope[];

/** The bundled translation of a scope, or `undefined` when none is bundled (the loader then takes its error path). */
export function bundledFor(scope: string, lang: string): Translation | undefined {
  if (!Object.hasOwn(BUNDLED, scope)) return undefined;
  const languages: Readonly<Record<string, Translation>> = BUNDLED[scope as PrerenderSafeScope];
  return Object.hasOwn(languages, lang) ? languages[lang] : undefined;
}
