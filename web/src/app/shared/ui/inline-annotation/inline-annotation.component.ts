import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconAlert } from '../icons/icon-alert/icon-alert';
import type { InlineAnnotationViewModel } from './inline-annotation.model';

@Component({
  selector: 'nts-inline-annotation',
  imports: [TranslocoPipe, IconAlert],
  providers: [provideTranslocoScope('episodes', 'quality', 'errors')],
  templateUrl: './inline-annotation.component.html',
  styleUrl: './inline-annotation.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InlineAnnotationComponent {
  readonly annotation = input.required<InlineAnnotationViewModel>();
  readonly compact = input(false);
  // The panel repeats the note with a separate DOM identity from its inline copy.
  readonly noteId = input<string | null>(null);
  readonly focusRequested = output<InlineAnnotationViewModel>();

  protected requestFocus(): void {
    if (this.annotation().canGoTo) this.focusRequested.emit(this.annotation());
  }
}
