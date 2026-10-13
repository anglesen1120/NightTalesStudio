import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { InlineAnnotationComponent } from '@shared/ui';
import type { InlineAnnotationViewModel } from '@shared/ui';
import { EMPTY_ANNOTATION_VIEWS } from './annotation.view-model';
import type { AnnotationPanelViewModel } from './annotation.view-model';

@Component({
  selector: 'nts-annotation-panel',
  imports: [TranslocoPipe, InlineAnnotationComponent],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './annotation-panel.component.html',
  styleUrl: './annotation-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnnotationPanelComponent {
  readonly viewModel = input<AnnotationPanelViewModel>(EMPTY_ANNOTATION_VIEWS.panel);
  readonly focusRequested = output<InlineAnnotationViewModel>();
}
