import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import type { StoryMetadata } from '../domain/story.model';
import type { StoryEditorIntent } from './story-editor.view-model';

@Component({
  selector: 'nts-story-meta-editor',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './story-meta-editor.component.html',
  styleUrl: './story-meta-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StoryMetaEditorComponent {
  readonly meta = input.required<StoryMetadata>();
  readonly tokens = input(0);
  readonly readOnly = input(false);
  readonly errorKeys = input<readonly string[]>([]);
  readonly intent = output<StoryEditorIntent>();

  protected readonly describedBy = computed(() => [
    'seg-meta-counter',
    ...(this.errorKeys().length > 0 ? ['seg-meta-errors'] : []),
  ].join(' '));

  protected editDescription(event: Event): void {
    if (this.readOnly() || !(event.target instanceof HTMLTextAreaElement)) return;
    this.intent.emit({ kind: 'editText', segKey: 'meta', text: event.target.value });
  }
}
