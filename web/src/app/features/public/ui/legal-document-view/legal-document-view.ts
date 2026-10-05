import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { EmptyState, PageHeader } from '@shared/ui';
import type { LegalDocumentViewModel } from '../../domain/legal-document-view.model';

@Component({
  selector: 'nts-legal-document-view',
  imports: [EmptyState, PageHeader, RouterLink, TranslocoPipe],
  templateUrl: './legal-document-view.html',
  styleUrl: './legal-document-view.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalDocumentView {
  readonly viewModel = input.required<LegalDocumentViewModel>();
}
