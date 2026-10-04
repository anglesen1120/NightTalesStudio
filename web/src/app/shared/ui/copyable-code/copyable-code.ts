import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { IconCopy } from '../icons/icon-copy/icon-copy';

type CopyResultKey = 'core.ui.copyableCode.copied' | 'core.ui.copyableCode.unavailable' | undefined;

@Component({
  selector: 'nts-copyable-code',
  imports: [TranslocoPipe, IconCopy],
  templateUrl: './copyable-code.html',
  styleUrl: './copyable-code.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CopyableCode {
  readonly value = input.required<string>();
  readonly labelKey = input<string>();
  protected readonly resultKey = signal<CopyResultKey>(undefined);

  protected async copy(): Promise<void> {
    try {
      if (typeof navigator === 'undefined' || navigator.clipboard?.writeText === undefined) {
        this.resultKey.set('core.ui.copyableCode.unavailable');
        return;
      }
      await navigator.clipboard.writeText(this.value());
      this.resultKey.set('core.ui.copyableCode.copied');
    } catch {
      this.resultKey.set('core.ui.copyableCode.unavailable');
    }
  }
}
