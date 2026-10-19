import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterRenderEffect, inject, input, output, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'nts-video-player',
  imports: [TranslocoPipe],
  templateUrl: './video-player.component.html',
  styleUrl: './video-player.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VideoPlayerComponent {
  readonly url = input<string | null>(null);
  readonly label = input.required<string>();
  readonly mediaError = output<void>();
  protected readonly refreshCount = signal(0);
  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');
  private source: string | null = null;
  private position = 0;
  private resumePlayback = false;
  private seekingAfterSwap = false;

  constructor() {
    afterRenderEffect(() => {
      const video = this.video()?.nativeElement;
      const url = this.url();
      if (!video || !url || this.source === url) return;
      if (this.source !== null) {
        if (!this.seekingAfterSwap) {
          this.position = Number.isFinite(video.currentTime) ? video.currentTime : 0;
          this.resumePlayback = !video.paused && !video.ended;
        }
        this.seekingAfterSwap = true;
        this.refreshCount.update((count) => count + 1);
      }
      this.source = url;
      video.setAttribute('src', url);
      video.load();
    });
    inject(DestroyRef).onDestroy(() => {
      this.video()?.nativeElement.removeAttribute('src');
      this.source = null;
      this.position = 0;
      this.resumePlayback = false;
    });
  }

  protected restorePosition(): void {
    const video = this.video()?.nativeElement;
    if (!video) return;
    if (this.position > 0) video.currentTime = Number.isFinite(video.duration) ? Math.min(this.position, video.duration) : this.position;
    this.seekingAfterSwap = false;
    if (this.resumePlayback) {
      this.resumePlayback = false;
      void video.play().catch(() => { /* Native controls remain available when the browser refuses resume. */ });
    }
  }
}
