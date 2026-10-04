import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

let nextCharCounterId = 0;

@Component({
  selector: 'nts-char-counter',
  imports: [TranslocoPipe],
  templateUrl: './char-counter.html',
  styleUrl: './char-counter.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.char-counter--over]': 'isOver()', '[attr.id]': 'null' },
})
export class CharCounter {
  readonly value = input.required<number>();
  readonly max = input.required<number>();
  readonly labelKey = input('core.ui.charCounter.label');
  readonly id = input(`nts-char-counter-${++nextCharCounterId}`);
  protected readonly isOver = computed(() => this.value() > this.max());
}
