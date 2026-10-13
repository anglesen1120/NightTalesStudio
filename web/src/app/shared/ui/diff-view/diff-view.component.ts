import { ChangeDetectionStrategy, Component, computed, effect, input, output, untracked } from '@angular/core';
import { diffWords, type WordDiff } from '@shared/util/text-diff';
import type { DiffRow } from './diff-row';

@Component({
  selector: 'nts-diff-view',
  templateUrl: './diff-view.component.html',
  styleUrl: './diff-view.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DiffViewComponent {
  readonly left = input<string | null>(null);
  readonly right = input<string | null>(null);
  readonly rows = input<readonly DiffRow[]>([]);
  readonly mode = input<'word' | 'rows'>('word');
  readonly label = input('');
  /** Consumers supply localized text; shared UI never depends on a feature translation scope. */
  readonly addedLabel = input('+');
  readonly removedLabel = input('−');
  readonly truncatedNotice = input('');
  readonly rendered = output<WordDiff>();

  protected readonly diff = computed<WordDiff>(() => {
    if (this.mode() === 'word') return diffWords(this.left() ?? '', this.right() ?? '');
    const rows = this.rows();
    return {
      segments: [],
      added: rows.filter((row) => row.kind !== 'remove').length,
      removed: rows.filter((row) => row.kind !== 'add').length,
      truncated: false,
    };
  });

  constructor() {
    effect(() => {
      const value = this.diff();
      untracked(() => this.rendered.emit(value));
    });
  }
}
