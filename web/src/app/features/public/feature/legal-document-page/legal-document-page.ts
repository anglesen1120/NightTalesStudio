import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import type { LegalDocumentViewModel } from '../../domain/legal-document-view.model';
import type { LegalDoc, LegalDocument, LegalLang } from '../../domain/legal.model';
import { LegalDocumentView } from '../../ui/legal-document-view/legal-document-view';

/**
 * Routed container of the four legal pages. The route is static, so its snapshot data is final: `doc` / `lang` come
 * from the route definition and `legal` is the document `legalResolver` already looked up (`null` for
 * `legal_content_missing`, which renders the EmptyState). No second repository lookup.
 */
@Component({
  selector: 'nts-legal-document-page',
  imports: [LegalDocumentView],
  templateUrl: './legal-document-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalDocumentPage {
  private readonly data = inject(ActivatedRoute).snapshot.data;

  protected readonly viewModel: LegalDocumentViewModel = (() => {
    const doc = this.data['doc'] as LegalDoc;
    const lang = this.data['lang'] as LegalLang;
    const document = (this.data['legal'] ?? null) as LegalDocument | null;
    const otherLang: LegalLang = lang === 'vi' ? 'en' : 'vi';
    return {
      doc,
      document,
      toc: document?.blocks.filter((block) => block.kind === 'heading') ?? [],
      otherLang,
      siblingRoute: ['/', otherLang, doc],
    };
  })();
}
