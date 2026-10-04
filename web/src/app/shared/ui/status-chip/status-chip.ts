import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { IconAlert } from '../icons/icon-alert/icon-alert';
import { IconCheck } from '../icons/icon-check/icon-check';
import { IconClock } from '../icons/icon-clock/icon-clock';
import { IconUnknown } from '../icons/icon-unknown/icon-unknown';

export type StatusAppearance = 'neutral' | 'info' | 'success' | 'warn' | 'error';
type StatusIcon = 'check' | 'clock' | 'alert' | 'unknown';

export const STATUS_ICON: Readonly<Record<string, StatusIcon>> = {
  draft: 'clock',
  story_generating: 'clock',
  story_review: 'clock',
  story_approved: 'check',
  rendering: 'clock',
  video_review: 'clock',
  ready: 'check',
  scheduled: 'clock',
  published: 'check',
  failed: 'alert',
  cancelled: 'alert',
};

@Component({
  selector: 'nts-status-chip',
  imports: [TranslocoPipe, IconAlert, IconCheck, IconClock, IconUnknown],
  templateUrl: './status-chip.html',
  styleUrl: './status-chip.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'img', '[attr.aria-label]': 'ariaLabel()', '[class]': 'hostClass()' },
})
export class StatusChip {
  private readonly transloco = inject(TranslocoService);
  private readonly activeLang = toSignal(this.transloco.langChanges$, { initialValue: this.transloco.getActiveLang() });
  readonly statusKey = input.required<string>();
  /**
   * The approved design deck has no separate warn/info colours, so `warn`
   * renders with error styling and `info` with success styling until the
   * design adds dedicated tokens; the icon and text carry the meaning (D16).
   */
  readonly appearance = input<StatusAppearance>('neutral');
  protected readonly icon = computed<StatusIcon>(() =>
    (Object.hasOwn(STATUS_ICON, this.statusKey()) ? STATUS_ICON[this.statusKey()] : undefined) ?? 'unknown',
  );
  protected readonly labelKey = computed(() =>
    this.icon() === 'unknown' ? 'core.ui.statusChip.unknown' : `core.ui.statusChip.${this.statusKey()}`,
  );
  protected readonly ariaLabel = computed(() => {
    this.activeLang();
    return this.transloco.translate(this.labelKey());
  });
  protected readonly hostClass = computed(() =>
    `status-chip status-chip--${this.icon() === 'unknown' ? 'neutral' : this.appearance()}`,
  );
}
