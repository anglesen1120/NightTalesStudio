import type { LegalDoc, LegalDocument, LegalLang } from '../domain/legal.model';
import { LegalRepository } from '../domain/legal.port';

/** Hand-written synchronous repository for legal component tests (README D15; no mocking library). */
export class FakeLegalRepository extends LegalRepository {
  private readonly documents = new Map<string, LegalDocument>();

  set(document: LegalDocument): void {
    this.documents.set(`${document.doc}:${document.lang}`, document);
  }

  override get(doc: LegalDoc, lang: LegalLang): LegalDocument {
    const document = this.documents.get(`${doc}:${lang}`);
    if (document === undefined) throw new Error('legal_content_missing');
    return document;
  }
}
