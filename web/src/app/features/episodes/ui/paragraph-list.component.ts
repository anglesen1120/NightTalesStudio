import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CdkDrag, CdkDropList } from '@angular/cdk/drag-drop';
import type { CdkDragDrop } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, inject, input, output } from '@angular/core';
import { TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { ParagraphCardComponent } from './paragraph-card.component';
import type { SegmentEditorViewModel, StoryEditorIntent } from './story-editor.view-model';

@Component({
  selector: 'nts-paragraph-list',
  imports: [CdkDrag, CdkDropList, ParagraphCardComponent],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './paragraph-list.component.html',
  styleUrl: './paragraph-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ParagraphListComponent {
  readonly paragraphs = input.required<readonly SegmentEditorViewModel[]>();
  readonly readOnly = input(false);
  readonly keywordLimitHit = input<string | null>(null);
  // Buttons use the same path when the optional pointer enhancement is absent.
  readonly pointerReorder = input(true);
  readonly intent = output<StoryEditorIntent>();
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly transloco = inject(TranslocoService);

  protected drop(event: CdkDragDrop<readonly SegmentEditorViewModel[]>): void {
    if (!event.isPointerOverContainer || event.previousContainer !== event.container) return;
    this.move(event.previousIndex, event.currentIndex);
  }

  protected forward(intent: StoryEditorIntent): void {
    if (intent.kind === 'moveParagraph') this.move(intent.from, intent.to);
    else if (intent.kind !== 'removeParagraph' || !this.readOnly()) this.intent.emit(intent);
  }

  private move(from: number, to: number): void {
    const paragraph = this.paragraphs()[from];
    if (this.readOnly() || paragraph === undefined || paragraph.readOnly || from === to || to < 0 || to >= this.paragraphs().length) return;
    this.intent.emit({ kind: 'moveParagraph', from, to });
    afterNextRender(() => {
      // Only report a move the single writer actually applied, not a rejected intent.
      if (this.paragraphs()[to]?.segKey !== paragraph.segKey) return;
      const target = this.host.nativeElement.querySelector<HTMLTextAreaElement>(`textarea[data-segment="p:${to}"]`);
      if (target === null) return;
      target.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      target.focus();
      void this.announcer.announce(this.transloco.translate('episodes.editor.reorderAnnounce', { n: from + 1, m: to + 1 }), 'polite');
    }, { injector: this.injector });
  }
}
