import { Injectable } from '@angular/core';
import type { LegalDoc, LegalDocument, LegalLang } from '../domain/legal.model';
import type { LegalRepository } from '../domain/legal.port';
import { LEGAL_CONTENT } from './legal-content.generated';

/**
 * Reads the build-time module produced by `tools/web/legal-to-json.mjs`. The generator already fails the build for a
 * missing doc/language, so the throw below only guards a hand-edited checkout; own-property checks keep a route value
 * such as `__proto__` from resolving.
 */
@Injectable()
export class GeneratedLegalRepository implements LegalRepository {
  get(doc: LegalDoc, lang: LegalLang): LegalDocument {
    if (!Object.hasOwn(LEGAL_CONTENT, doc)) throw new Error('legal_content_missing');
    const languages = LEGAL_CONTENT[doc];
    if (!Object.hasOwn(languages, lang)) throw new Error('legal_content_missing');
    return languages[lang];
  }
}
