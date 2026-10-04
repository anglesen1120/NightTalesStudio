import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  PLATFORM_ID,
  Renderer2,
  inject,
  input,
  output,
  signal,
  type OnChanges,
  type OnDestroy,
  type SimpleChanges,
} from '@angular/core';
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
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly document = inject(DOCUMENT);
  private readonly renderer = inject(Renderer2);

  readonly seconds = input.required<number>();
  readonly messageKey = input<string | null>(null);
  readonly elapsed = output<void>();
  readonly remaining = signal(0);

  /** The live-region snapshot changes only when armed and when elapsed; visual ticks are aria-hidden. */
  protected readonly announcementKey = signal('errors.retryNow');
  protected readonly announcementSeconds = signal(0);
  private timer: ReturnType<typeof globalThis.setInterval> | null = null;
  private stopVisibilityListener: (() => void) | null = null;
  private deadline = 0;
  private active = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['seconds'] !== undefined) this.arm(this.seconds());
  }

  ngOnDestroy(): void {
    this.cancelArm();
  }

  private arm(seconds: number): void {
    this.cancelArm();
    const normalized = Number.isFinite(seconds) ? Math.max(0, Math.ceil(seconds)) : 0;
    this.remaining.set(normalized);

    if (normalized === 0) {
      this.announcementSeconds.set(0);
      this.announcementKey.set('errors.retryNow');
      if (this.browser) this.elapsed.emit();
      return;
    }

    this.announcementSeconds.set(normalized);
    this.announcementKey.set(this.messageKey() ?? 'errors.retryIn');
    this.deadline = Date.now() + normalized * 1_000;
    if (!this.browser) return;

    this.active = true;
    this.stopVisibilityListener = this.renderer.listen(this.document, 'visibilitychange', () => {
      if (this.document.visibilityState === 'visible') this.syncRemaining();
    });
    this.timer = globalThis.setInterval(() => this.syncRemaining(), 1_000);
  }

  private syncRemaining(): void {
    if (!this.active) return;
    const next = Math.max(0, Math.ceil((this.deadline - Date.now()) / 1_000));
    this.remaining.set(next);
    if (next !== 0) return;

    this.cancelArm();
    this.announcementSeconds.set(0);
    this.announcementKey.set('errors.retryNow');
    this.elapsed.emit();
  }

  private cancelArm(): void {
    this.active = false;
    if (this.timer !== null) {
      globalThis.clearInterval(this.timer);
      this.timer = null;
    }
    this.stopVisibilityListener?.();
    this.stopVisibilityListener = null;
  }
}
