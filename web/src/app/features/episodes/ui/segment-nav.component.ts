import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { formatAddress, parseAddress } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';
import type { SegmentNavViewModel } from './story-editor.view-model';

@Component({
  selector: 'nts-segment-nav',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './segment-nav.component.html',
  styleUrl: './segment-nav.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SegmentNavComponent {
  readonly segments = input.required<readonly SegmentNavViewModel[]>();
  readonly openSegment = output<SegmentAddress>();

  protected select(event: Event): void {
    if (!(event.target instanceof HTMLSelectElement)) return;
    const parsed = parseAddress(event.target.value);
    if (parsed === null) return;
    this.openSegment.emit(formatAddress(parsed.kind, parsed.index ?? undefined));
  }
}
