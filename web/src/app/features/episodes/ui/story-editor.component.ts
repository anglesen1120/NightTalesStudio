import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import type { SegmentAddress } from '@shared/util/segment-address';
import { SceneStripComponent } from './scene-strip.component';
import { SegmentEditorComponent } from './segment-editor.component';
import { SegmentNavComponent } from './segment-nav.component';
import { StoryMetaEditorComponent } from './story-meta-editor.component';
import { countTokens } from '../domain/token-count';
import { StorySaveBarComponent } from './story-save-bar.component';
import type { StoryEditorIntent, StoryEditorViewModel } from './story-editor.view-model';

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
  readonly intent = output<StoryEditorIntent>();
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly textSegments = computed(() => {
    const vm = this.viewModel();
    return [vm.title, vm.hook, ...vm.paragraphs, vm.ending] as const;
  });
  protected readonly sceneSegments = computed(() => {
    const vm = this.viewModel();
    return [vm.hook, ...vm.paragraphs, vm.ending];
  });
  protected readonly metaErrors = computed(() => this.viewModel().fieldErrors.meta ?? []);
  protected readonly metaTokens = computed(() => countTokens(this.viewModel().meta.description));

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
