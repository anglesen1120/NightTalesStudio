import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { parseAddress } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import type { InlineAnnotationViewModel } from '@shared/ui';
import { AnnotationPanelComponent } from './annotation-panel.component';
import { EMPTY_ANNOTATION_VIEWS } from './annotation.view-model';
import type { AnnotationViews } from './annotation.view-model';
import { ParagraphListComponent } from './paragraph-list.component';
import { SceneStripComponent } from './scene-strip.component';
import { SegmentEditorComponent } from './segment-editor.component';
import { SegmentNavComponent } from './segment-nav.component';
import { StoryMetaEditorComponent } from './story-meta-editor.component';
import { EMPTY_ERRORS, equalStrings } from './story-editor.view-model';
import { StorySaveBarComponent } from './story-save-bar.component';
import type { SceneSegmentViewModel, SegmentNavViewModel, SegmentTexts, StoryEditorIntent, StoryEditorViewModel } from './story-editor.view-model';

/**
 * One polite announcement for the editor's live region (10.5.10-T1). `text` is already translated and frozen for
 * `sequence`; the sequence is the track key, so a new one re-creates the announced node even when two saves report the
 * same count — the region mutates and speaks again, with no trailing-space trick and no timer.
 */
export interface StoryEditorAnnouncement {
  readonly text: string;
  readonly sequence: number;
}

@Component({
  selector: 'nts-story-editor',
  imports: [TranslocoPipe, AnnotationPanelComponent, ParagraphListComponent, SceneStripComponent, SegmentEditorComponent, SegmentNavComponent, StoryMetaEditorComponent, StorySaveBarComponent],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './story-editor.component.html',
  styleUrl: './story-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryEditorComponent {
  readonly viewModel = input.required<StoryEditorViewModel>();
  readonly segmentTexts = input.required<SegmentTexts>();
  readonly annotationViews = input<AnnotationViews>(EMPTY_ANNOTATION_VIEWS);
  readonly announcement = input<StoryEditorAnnouncement | null>(null);
  /** `@for` source: an empty list renders nothing, a new `sequence` re-creates the announced node. */
  protected readonly announcementQueue = computed<readonly StoryEditorAnnouncement[]>(() => {
    const announcement = this.announcement();
    return announcement === null ? [] : [announcement];
  });
  protected readonly emptyAnnotations: readonly InlineAnnotationViewModel[] = [];
  protected readonly emptyFlags: ReadonlyMap<number, { readonly annotationId: string; readonly messageKey: string }> = new Map();
  readonly intent = output<StoryEditorIntent>();
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly textSegments = computed<readonly SegmentNavViewModel[]>(() => {
    const vm = this.viewModel();
    return [vm.title, vm.hook, ...vm.paragraphs, vm.ending].map(({ address, segKey, labelKey, labelIndex }) => ({ address, segKey, labelKey, labelIndex }));
  }, { equal: equalLabels });
  protected readonly sceneSegments = computed<readonly SceneSegmentViewModel[]>(() => {
    const vm = this.viewModel();
    return [vm.hook, ...vm.paragraphs, vm.ending].map(({ address, segKey, labelKey, labelIndex, keywords }) => ({ address, segKey, labelKey, labelIndex, keywords }));
  }, { equal: (left, right) => equalLabels(left, right) && left.every((segment, index) => equalStrings(segment.keywords, right[index]!.keywords)) });
  protected readonly metaErrors = computed(() => this.viewModel().fieldErrors.meta ?? EMPTY_ERRORS);

  focusSegment(address: SegmentAddress): boolean {
    const target = this.host.nativeElement.querySelector<HTMLTextAreaElement>(`textarea[data-segment="${address}"]`);
    if (target === null) return false;
    target.scrollIntoView?.({ block: 'center', inline: 'nearest' });
    target.focus();
    return true;
  }

  protected forward(intent: StoryEditorIntent): void {
    this.intent.emit(intent);
  }

  protected openAnnotation(annotation: InlineAnnotationViewModel): void {
    if (annotation.canGoTo && annotation.address !== null && parseAddress(annotation.address) !== null) this.open(annotation.address as SegmentAddress);
  }

  protected open(address: SegmentAddress): void {
    this.intent.emit({ kind: 'openSegment', address });
  }
}

function equalLabels(left: readonly SegmentNavViewModel[], right: readonly SegmentNavViewModel[]): boolean {
  return left.length === right.length && left.every((segment, index) => {
    const other = right[index]!;
    return segment.address === other.address && segment.segKey === other.segKey && segment.labelKey === other.labelKey && segment.labelIndex === other.labelIndex;
  });
}
