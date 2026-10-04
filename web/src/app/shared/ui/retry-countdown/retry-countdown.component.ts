import { ChangeDetectionStrategy, Component, type OnChanges, type OnDestroy, type SimpleChanges, input, output, signal } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';

@Component({
  selector: 'nts-retry-countdown',
  imports: [TranslocoPipe],
  providers: [provideTranslocoScope('errors')],
  templateUrl: './retry-countdown.component.html',
  styleUrl: './retry-countdown.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RetryCountdown implements OnChanges, OnDestroy {
  readonly seconds = input.required<number>();
  readonly messageKey = input<string | null>(null);
  readonly elapsed = output<void>();
  readonly remaining = signal(0);

  /** The live-region snapshot changes only when armed and when elapsed; visual ticks are aria-hidden. */
  protected readonly announcementKey = signal('errors.retryNow');
  protected readonly announcementSeconds = signal(0);
  private timer: number | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['seconds'] !== undefined) this.arm(this.seconds());
  }

  ngOnDestroy(): void {
    this.clearTimer();
  }

  private arm(seconds: number): void {
    this.clearTimer();
    const normalized = Number.isFinite(seconds) ? Math.max(0, Math.ceil(seconds)) : 0;
    this.remaining.set(normalized);

    if (normalized === 0) {
      this.announcementSeconds.set(0);
      this.announcementKey.set('errors.retryNow');
      this.elapsed.emit();
      return;
    }

    this.announcementSeconds.set(normalized);
    this.announcementKey.set(this.messageKey() ?? 'errors.retryIn');
    this.timer = window.setInterval(() => {
      const next = Math.max(0, this.remaining() - 1);
      this.remaining.set(next);
      if (next === 0) {
        this.clearTimer();
        this.announcementSeconds.set(0);
        this.announcementKey.set('errors.retryNow');
        this.elapsed.emit();
      }
    }, 1_000);
  }

  private clearTimer(): void {
    if (this.timer === null) return;
    window.clearInterval(this.timer);
    this.timer = null;
  }
}
