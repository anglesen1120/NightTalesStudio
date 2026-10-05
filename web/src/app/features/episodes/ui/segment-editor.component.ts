import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { SegmentChipsComponent } from '@shared/ui';
import type { StoryEditorIntent, SegmentEditorViewModel } from './story-editor.view-model';

@Component({
  selector: 'nts-segment-editor',
  imports: [TranslocoPipe, SegmentChipsComponent],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './segment-editor.component.html',
  styleUrl: './segment-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SegmentEditorComponent {
  readonly viewModel = input.required<SegmentEditorViewModel>();
  readonly limitHit = input(false);
  readonly intent = output<StoryEditorIntent>();

  protected readonly controlId = computed(() => `seg-${this.viewModel().address.replace(':', '-')}`);
  protected readonly labelId = computed(() => `${this.controlId()}-label`);
  protected readonly counterId = computed(() => `${this.controlId()}-counter`);
  protected readonly errorId = computed(() => `${this.controlId()}-errors`);
  protected readonly describedBy = computed(() => [
    this.counterId(),
    ...(this.viewModel().errorKeys.length > 0 ? [this.errorId()] : []),
  ].join(' '));
  protected readonly editableKey = computed(() => {
    const vm = this.viewModel();
    return vm.address === 'title' || vm.address === 'hook' || vm.address === 'ending' ? vm.address : vm.segKey;
  });
  protected readonly showKeywords = computed(() => this.viewModel().address !== 'title');

  protected editText(event: Event): void {
    if (this.viewModel().readOnly || !(event.target instanceof HTMLTextAreaElement)) return;
    this.intent.emit({ kind: 'editText', segKey: this.editableKey(), text: event.target.value });
  }

  protected addKeyword(): void {
    this.intent.emit({ kind: 'editKeyword', segKey: this.viewModel().segKey, op: 'add', value: '' });
  }

  protected updateKeyword(edit: { readonly index: number; readonly value: string }): void {
    this.intent.emit({ kind: 'editKeyword', segKey: this.viewModel().segKey, op: 'update', index: edit.index, value: edit.value });
  }

  protected removeKeyword(index: number): void {
    this.intent.emit({ kind: 'editKeyword', segKey: this.viewModel().segKey, op: 'remove', index });
  }
}
