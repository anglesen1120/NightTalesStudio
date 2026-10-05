import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import type { SegmentAddress } from '@shared/util/segment-address';
import { SceneStripComponent } from './scene-strip.component';
import { SegmentEditorComponent } from './segment-editor.component';
import { SegmentNavComponent } from './segment-nav.component';
import { StoryMetaEditorComponent } from './story-meta-editor.component';
import { EMPTY_ERRORS, equalStrings } from './story-editor.view-model';
import { StorySaveBarComponent } from './story-save-bar.component';
import type { SceneSegmentViewModel, SegmentNavViewModel, SegmentTexts, StoryEditorIntent, StoryEditorViewModel } from './story-editor.view-model';

@Component({
  selector: 'nts-story-editor',
  imports: [TranslocoPipe, SceneStripComponent, SegmentEditorComponent, SegmentNavComponent, StoryMetaEditorComponent, StorySaveBarComponent],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './story-editor.component.html',
  styleUrl: './story-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryEditorComponent {
  readonly viewModel = input.required<StoryEditorViewModel>();
  readonly segmentTexts = input.required<SegmentTexts>();
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
