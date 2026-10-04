import type { Route, Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { GeneratedLegalRepository } from './data-access/legal.repository';
import { LEGAL_DOCS, LEGAL_LANGS, type LegalDoc, type LegalLang } from './domain/legal.model';
import { LegalRepository } from './domain/legal.port';
import { legalResolver } from './feature/legal.resolver';

function legalRoute(doc: LegalDoc, lang: LegalLang): Route {
  return {
    path: `${lang}/${doc}`,
    loadComponent: () =>
      import('./feature/legal-document-page/legal-document-page').then((module) => module.LegalDocumentPage),
    // Static data is bound to the page's `doc` / `lang` inputs (withComponentInputBinding in app.config.ts).
    data: { doc, lang },
    resolve: { legal: legalResolver },
    providers: [{ provide: LegalRepository, useClass: GeneratedLegalRepository }, provideTranslocoScope('public')],
  };
}

/**
 * The prerendered public pages (README D1/D12): `/vi|en/privacy|terms`, plus the default-language redirects of the
 * 13 contract ("published at `/privacy`"). No route sets `title`: `PublicSeoService` owns the document title.
 */
export const publicRoutes: Routes = [
  ...LEGAL_LANGS.flatMap((lang) => LEGAL_DOCS.map((doc) => legalRoute(doc, lang))),
  ...LEGAL_DOCS.map((doc): Route => ({ path: doc, redirectTo: `/vi/${doc}`, pathMatch: 'full' })),
];
