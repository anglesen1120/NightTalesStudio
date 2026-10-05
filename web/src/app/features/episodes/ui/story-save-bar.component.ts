import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import type { StoryEditorIntent, StorySaveBarViewModel } from './story-editor.view-model';

@Component({
  selector: 'nts-story-save-bar',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './story-save-bar.component.html',
  styleUrl: './story-save-bar.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StorySaveBarComponent {
  readonly viewModel = input.required<StorySaveBarViewModel>();
  readonly totalTokens = input(0);
  readonly readOnly = input(false);
  readonly intent = output<StoryEditorIntent>();

  protected readonly statusKey = computed(() => {
    switch (this.viewModel().state) {
      case 'dirty': return 'episodes.editor.unsaved';
      case 'saving': return 'episodes.editor.saving';
      case 'saved': return 'episodes.editor.saved';
      case 'error': return this.viewModel().errorKey;
      case 'clean': return null;
    }
  });

  protected save(): void {
    if (!this.readOnly() && !this.viewModel().disabled) this.intent.emit({ kind: 'save' });
  }
}
