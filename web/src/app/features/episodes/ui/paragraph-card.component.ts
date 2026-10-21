import { CdkDragHandle } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { SegmentEditorComponent } from './segment-editor.component';
import type { InlineAnnotationViewModel } from '@shared/ui';
import type { SegmentEditorViewModel, StoryEditorIntent } from './story-editor.view-model';
import type { SimilarityLevel } from '../domain/audit.model';

@Component({
  selector: 'nts-paragraph-card',
  imports: [CdkDragHandle, TranslocoPipe, SegmentEditorComponent],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './paragraph-card.component.html',
  styleUrl: './paragraph-card.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ParagraphCardComponent {
  readonly viewModel = input.required<SegmentEditorViewModel>();
  readonly index = input.required<number>();
  readonly count = input.required<number>();
  readonly readOnly = input(false);
  readonly pointerReorder = input(true);
  readonly limitHit = input(false);
  readonly annotations = input<readonly InlineAnnotationViewModel[]>([]);
  readonly similarityLevel = input<SimilarityLevel | null>(null);
  readonly flaggedChips = input<ReadonlyMap<number, { readonly annotationId: string; readonly messageKey: string }>>(new Map());
  readonly intent = output<StoryEditorIntent>();
  protected readonly locked = computed(() => this.readOnly() || this.viewModel().readOnly);

  protected move(delta: -1 | 1): void {
    const from = this.index();
    const to = from + delta;
    if (this.locked() || to < 0 || to >= this.count()) return;
    this.intent.emit({ kind: 'moveParagraph', from, to });
  }

  protected remove(): void {
    if (!this.locked()) this.intent.emit({ kind: 'removeParagraph', index: this.index() });
  }

  protected forward(intent: StoryEditorIntent): void {
    this.intent.emit(intent);
  }
}
