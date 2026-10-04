import type { Translation } from '@jsverse/transloco';
import coreEn from '../../../assets/i18n/core/en.json';
import coreVi from '../../../assets/i18n/core/vi.json';
import type { AppLocale } from './locale.model';

/**
 * Scopes a prerendered route may use: a prerender cannot fetch `/assets/i18n/**` (README D12/F18), so these are
 * bundled. 10.3.1-T1 adds `public` here (one import per language); `errors` joins when 10.2.2-T1 lands its files.
 * A route outside this list must not be prerendered.
 */
export const PRERENDER_SAFE_SCOPES = ['core', 'errors', 'public'] as const;

export type PrerenderSafeScope = (typeof PRERENDER_SAFE_SCOPES)[number];

/** Data-driven: a scope absent from the map has no bundled translation. */
const BUNDLED: Readonly<Partial<Record<string, Readonly<Record<AppLocale, Translation>>>>> = {
  core: { vi: coreVi, en: coreEn },
};

/** The bundled translation of a scope, or `undefined` when none is bundled (the loader then takes its error path). */
export function bundledFor(scope: string, lang: string): Translation | undefined {
  return BUNDLED[scope]?.[lang as AppLocale];
}
