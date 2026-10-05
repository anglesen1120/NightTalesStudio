import type { LegalBlock, LegalDoc, LegalDocument, LegalLang } from './legal.model';

/** Immutable state passed from the routed legal container to its presentational view. */
export interface LegalDocumentViewModel {
  readonly doc: LegalDoc;
  readonly document: LegalDocument | null;
  readonly toc: readonly LegalBlock[];
  readonly otherLang: LegalLang;
  readonly siblingRoute: readonly ['/', LegalLang, LegalDoc];
}
