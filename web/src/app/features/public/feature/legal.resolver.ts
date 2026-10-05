import { inject } from '@angular/core';
import type { ResolveFn } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { LocaleService } from '@core/i18n';
import { PublicSeoService } from '../application/public-seo.service';
import { LEGAL_DOCS, LEGAL_LANGS, type LegalDoc, type LegalDocument, type LegalLang } from '../domain/legal.model';
import { LegalRepository } from '../domain/legal.port';

/**
 * Runs before the legal route activates, in the browser and in the prerender pass: activates the route's language
 * (Transloco + `<html lang>`, never persisted: a public page must not overwrite the visitor's saved preference) and
 * writes the SEO head. A missing document yields `null` and no head metadata; the page then renders its empty state.
 * Reads route data only (`{ doc, lang }`), so it touches no `window`, storage or `navigator`.
 */
export const legalResolver: ResolveFn<Promise<LegalDocument | null>> = async (route) => {
  const doc: unknown = route.data['doc'];
  const lang: unknown = route.data['lang'];
  if (!LEGAL_DOCS.includes(doc as LegalDoc) || !LEGAL_LANGS.includes(lang as LegalLang)) return null;
  const locale = inject(LocaleService);
  const transloco = inject(TranslocoService);
  const repository = inject(LegalRepository);
  const seo = inject(PublicSeoService);
  locale.activate(lang as LegalLang, { persist: false });
  // The prerender pass does not wait for the pipe's own scope load; await it so the HTML holds copy, not empty text.
  await firstValueFrom(transloco.load(`public/${lang as LegalLang}`));
  let document: LegalDocument;
  try {
    document = repository.get(doc as LegalDoc, lang as LegalLang);
  } catch (error) {
    if (error instanceof Error && error.message === 'legal_content_missing') return null;
    throw error;
  }
  seo.apply(document, { vi: `/vi/${document.doc}`, en: `/en/${document.doc}` });
  return document;
};
