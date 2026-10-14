import { TranslocoTestingModule } from '@jsverse/transloco';
import coreEn from '../../../../assets/i18n/core/en.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import type { Fact, FactSheet, ResearchSource } from '../domain/research.model';
export const researchTranslations = (lang: 'vi' | 'en' = 'en') => TranslocoTestingModule.forRoot({
  langs: { en: { ...coreEn, episodes: episodesEn, errors: errorsEn }, vi: { ...coreVi, episodes: episodesVi, errors: errorsVi }, 'episodes/en': episodesEn, 'episodes/vi': episodesVi, 'errors/en': errorsEn, 'errors/vi': errorsVi },
  translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true }, preloadLangs: true,
});
export function source(overrides: Partial<ResearchSource> = {}): ResearchSource { return { id: 'source-a', kind: 'url', url: 'https://vnexpress.net/story', registrableDomain: 'vnexpress.net', noteText: null, status: 'fetched', errorCode: null, errorMessageKey: null, httpStatus: null, title: 'Public record', finalUrl: null, contentType: 'text/html', textChars: 100, isCited: true, createdAt: '2026-10-14T00:00:00Z', fetchedAt: '2026-10-14T00:01:00Z', ...overrides }; }
export function fact(overrides: Partial<Fact> = {}): Fact & {readonly clientKey: string} { return { clientKey: 'f1', id: 'f1', statement: 'A verified fact.', confidence: 'high', sourceIds: ['source-a'], sources: [{ sourceId: 'source-a', alias: 'S1', title: 'Public record', url: 'https://vnexpress.net/story', isUserNote: false }], sourceOverlap: null, origin: 'model', ...overrides }; }
export function sheet(overrides: Partial<FactSheet> = {}): FactSheet { return { id: 'sheet-a', version: 1, status: 'draft', etag: '"fs-sheet-a"', facts: [fact()], promptVersion: 'facts.v1', createdAt: '2026-10-14T00:00:00Z', approvedAt: null, maxFactId: 1, ...overrides }; }
