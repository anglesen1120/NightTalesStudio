import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-char-counter',
  imports: [TranslocoPipe],
  templateUrl: './char-counter.html',
  styleUrl: './char-counter.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.char-counter--over]': 'isOver()' },
})
export class CharCounter {
  readonly value = input.required<number>();
  readonly max = input.required<number>();
  readonly labelKey = input('core.ui.charCounter.label');
  readonly descriptionId = input('nts-char-counter', { alias: 'id' });
  protected readonly isOver = computed(() => this.value() > this.max());
}
