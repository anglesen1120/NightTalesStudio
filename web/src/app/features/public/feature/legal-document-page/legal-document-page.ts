import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { LegalDocumentViewModel } from '../../domain/legal-document-view.model';
import type { LegalDoc, LegalDocument, LegalLang } from '../../domain/legal.model';
import { LegalRepository } from '../../domain/legal.port';
import { LegalDocumentView } from '../../ui/legal-document-view/legal-document-view';

@Component({
  selector: 'nts-legal-document-page',
  imports: [LegalDocumentView],
  templateUrl: './legal-document-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalDocumentPage {
  readonly doc = input.required<LegalDoc>();
  readonly lang = input.required<LegalLang>();

  private readonly repository = inject(LegalRepository);

  protected readonly viewModel = computed<LegalDocumentViewModel>(() => {
    const doc = this.doc();
    const lang = this.lang();
    let document: LegalDocument | null;
    try {
      document = this.repository.get(doc, lang);
    } catch (error: unknown) {
      if (!(error instanceof Error) || error.message !== 'legal_content_missing') throw error;
      document = null;
    }
    const otherLang: LegalLang = lang === 'vi' ? 'en' : 'vi';
    return {
      doc,
      document,
      toc: document?.blocks.filter((block) => block.kind === 'heading') ?? [],
      otherLang,
      siblingRoute: ['/', otherLang, doc],
    };
  });
}
