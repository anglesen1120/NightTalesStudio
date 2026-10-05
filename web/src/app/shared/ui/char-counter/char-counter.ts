import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

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
  private readonly transloco = inject(TranslocoService);
  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });
  private readonly numberFormatter = computed(() => {
    const activeLang = this.activeLang();
    const language = activeLang.slice(activeLang.lastIndexOf('/') + 1);
    return new Intl.NumberFormat(language === 'vi' ? 'vi-VN' : 'en-US');
  });
  readonly value = input.required<number>();
  readonly max = input.required<number>();
  readonly labelKey = input('core.ui.charCounter.label');
  readonly id = input(`nts-char-counter-${++nextCharCounterId}`);
  protected readonly isOver = computed(() => this.value() > this.max());
  protected readonly formattedValue = computed(() => this.numberFormatter().format(this.value()));
  protected readonly formattedMax = computed(() => this.numberFormatter().format(this.max()));
}
