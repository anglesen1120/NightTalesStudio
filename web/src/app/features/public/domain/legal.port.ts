import type { LegalDoc, LegalDocument, LegalLang } from './legal.model';

/** Synchronous: the content is a build-time module, so there is no loading state and no HTTP during prerender. */
export abstract class LegalRepository {
  /** Throws `Error('legal_content_missing')` when the doc/lang pair is absent. */
  abstract get(doc: LegalDoc, lang: LegalLang): LegalDocument;
}
