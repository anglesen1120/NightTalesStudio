import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconAlert } from '../icons/icon-alert/icon-alert';

export interface SegmentKeywordEdit {
  readonly index: number;
  readonly value: string;
}

@Component({
  selector: 'nts-segment-chips',
  imports: [TranslocoPipe, IconAlert],
  providers: [provideTranslocoScope('episodes', 'errors')],
  templateUrl: './segment-chips.component.html',
  styleUrl: './segment-chips.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SegmentChipsComponent {
  readonly keywords = input.required<readonly string[]>();
  readonly readOnly = input(false);
  readonly limitHit = input(false);
  readonly flagged = input<ReadonlyMap<number, { readonly annotationId: string; readonly messageKey: string }>>(new Map());

  readonly addKeyword = output<void>();
  readonly editKeyword = output<SegmentKeywordEdit>();
  readonly removeKeyword = output<number>();

  protected edit(index: number, event: Event): void {
    if (this.readOnly() || !(event.target instanceof HTMLInputElement)) return;
    this.editKeyword.emit({ index, value: event.target.value });
  }

  protected add(): void {
    if (!this.readOnly()) this.addKeyword.emit();
  }

  protected remove(index: number): void {
    if (!this.readOnly()) this.removeKeyword.emit(index);
  }
}
