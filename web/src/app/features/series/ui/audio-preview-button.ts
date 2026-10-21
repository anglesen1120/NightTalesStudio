import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, viewChild, type ElementRef, type OnDestroy } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { IconPause, IconPlay } from '@shared/ui';

let nextPreviewButtonId = 0;

/**
 * Play/stop toggle for one voice preview (10.4.2-T2, WR4). Presentational only (D2): the parent
 * (`VoicePicker`) owns "one audio playing at a time" by passing `playing` and listening to
 * `started`/`stopped`.
 *
 * - `previewUrl === null` renders a disabled button with the `previewUnavailable` tooltip and no
 *   `<audio>` element at all — never a request to a missing asset (f-new-step3).
 * - Otherwise the native `<audio preload="none">` is created inside `@defer (on interaction)`
 *   keyed to the toggle, so zero media request happens before the user asks (D13/F12).
 * - `aria-pressed` tracks `playing`; starting emits `started` exactly once per activation.
 * - A media error — the `error` event or a `play()` rejection other than our own `AbortError` — surfaces
 *   `series.voice.previewFailed` inline (aria-live), with no retry loop.
 * - The URL is used verbatim — it is already validated by the repository mapper (D4 CSP; no
 *   `bypassSecurityTrust*` here).
 */
@Component({
  selector: 'nts-audio-preview-button',
  imports: [TranslocoPipe, IconPlay, IconPause],
  providers: [provideTranslocoScope('series')],
  templateUrl: './audio-preview-button.html',
  styleUrl: './audio-preview-button.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AudioPreviewButton implements OnDestroy {
  readonly previewUrl = input.required<string | null>();
  /** Accessible name part, e.g. `series.voice.preview`; `${label} — ${displayName}` names the button. */
  readonly label = input('series.voice.preview');
  /** Accessible name part while playing, e.g. `series.voice.stop`. */
  readonly stopLabel = input('series.voice.stop');
  readonly displayName = input('');
  /** Controlled by the parent: `false` pauses and rewinds the preview. */
  readonly playing = input(false);
  readonly disabled = input(false);

  readonly started = output<void>();
  readonly stopped = output<void>();

  protected readonly audio = viewChild<ElementRef<HTMLAudioElement>>('player');
  protected readonly failed = signal(false);
  protected readonly liveId = `nts-preview-live-${nextPreviewButtonId++}`;
  protected readonly pressed = computed(() => this.playing() && !this.failed());

  /** Kept for teardown: view queries may already be cleared when ngOnDestroy runs. */
  private lastAudioEl: HTMLAudioElement | null = null;

  private readonly syncPlayback = effect(() => {
    const el = this.audio()?.nativeElement;
    if (el !== undefined) this.lastAudioEl = el;
    if (el === undefined) return;
    // The `@defer` inserts the element before any template binding would land; the button is the only place that
    // sets `src`, so it is assigned here the moment the player exists, before `play()` is ever asked (once).
    const url = this.previewUrl();
    if (url !== null && el.src === '') el.src = url;
    if (this.playing() && !this.failed()) {
      // Once armed, the button is the only place that sets `src`; play() may reject in
      // unsupported-media environments — the media `error` event still reports the failure.
      // `AbortError` is our own pause() interrupting play(); every other rejection (e.g. an autoplay
      // policy `NotAllowedError`) is a real failure and takes the same path as the `error` event.
      void el.play().catch((reason: unknown) => {
        if (reason instanceof DOMException && reason.name === 'AbortError') return;
        this.onError();
      });
    } else {
      el.pause();
      el.currentTime = 0;
    }
  });

  protected toggle(): void {
    if (this.disabled() || this.previewUrl() === null) return;
    if (this.playing()) {
      this.stopped.emit();
      return;
    }
    this.failed.set(false);
    this.started.emit();
  }

  protected onEnded(): void {
    this.stopped.emit();
  }

  protected onError(): void {
    this.failed.set(true);
    this.stopped.emit();
  }

  ngOnDestroy(): void {
    const el = this.audio()?.nativeElement ?? this.lastAudioEl;
    if (el === null) return;
    this.lastAudioEl = null;
    el.pause();
    el.removeAttribute('src');
    el.load(); // releases the media resource (D13: no listeners/timers survive the route)
  }
}
