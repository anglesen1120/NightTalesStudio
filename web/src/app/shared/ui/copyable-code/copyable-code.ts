import { ChangeDetectionStrategy, Component, input, linkedSignal } from '@angular/core';
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
  protected readonly resultKey = linkedSignal<string, CopyResultKey>({
    source: this.value,
    computation: () => undefined,
  });

  protected async copy(): Promise<void> {
    let key: CopyResultKey = 'core.ui.copyableCode.copied';
    try {
      if (typeof navigator === 'undefined' || navigator.clipboard?.writeText === undefined) {
        key = 'core.ui.copyableCode.unavailable';
      } else {
        await navigator.clipboard.writeText(this.value());
      }
    } catch {
      key = 'core.ui.copyableCode.unavailable';
    }
    // Clear the live region, then restore the message on the next task so a
    // repeated identical result is announced again by screen readers.
    this.resultKey.set(undefined);
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    this.resultKey.set(key);
  }
}
