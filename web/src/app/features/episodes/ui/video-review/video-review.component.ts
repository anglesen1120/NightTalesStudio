import { ChangeDetectionStrategy, Component, computed, input, output, viewChild } from '@angular/core';
import { TranslocoPipe, provideTranslocoScope } from '@jsverse/transloco';
import { ErrorState } from '@shared/ui/error-state/error-state';
import { LoadingState } from '@shared/ui/loading-state/loading-state';
import { RetryCountdown } from '@shared/ui/retry-countdown/retry-countdown.component';
import type { FinalVideoAsset } from '../../domain/episode.model';
import type { SkippedScene } from '../../domain/render.model';
import type { VideoUrl } from '../../domain/video-review.repository';
import { FinalVideoMetaComponent } from '../final-video-meta/final-video-meta.component';
import { QualityNotesComponent } from '../quality-notes/quality-notes.component';
import { SkippedScenesComponent } from '../skipped-scenes/skipped-scenes.component';
import { VideoDecisionsComponent } from '../video-decisions/video-decisions.component';
import { VideoPlayerComponent } from '../video-player/video-player.component';

@Component({
  selector: 'nts-video-review',
  imports: [TranslocoPipe, ErrorState, LoadingState, RetryCountdown, FinalVideoMetaComponent, QualityNotesComponent, SkippedScenesComponent, VideoDecisionsComponent, VideoPlayerComponent],
  providers: [provideTranslocoScope('episodes')],
  templateUrl: './video-review.component.html',
  styleUrl: './video-review.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VideoReviewComponent {
  readonly url = input<VideoUrl | null>(null);
  readonly urlFailed = input(false);
  readonly urlErrorKey = input<string | null>(null);
  readonly urlRetryAfterSec = input<number | null>(null);
  readonly loadingUrl = input(false);
  readonly notes = input<readonly string[]>([]);
  readonly skipped = input<readonly SkippedScene[]>([]);
  readonly episodeId = input.required<string>();
  readonly finalVideo = input<FinalVideoAsset | null>(null);
  readonly canApprove = input(false);
  readonly canReject = input(false);
  readonly canDownload = input(false);
  readonly downloadName = input('nighttales.mp4');
  readonly busy = input(false);
  readonly ready = input(false);
  readonly approveRequest = output<void>();
  readonly rejectRequest = output<void>();
  readonly reMakeRequest = output<void>();
  readonly retryUrl = output<void>();
  readonly mediaError = output<void>();
  readonly downloadClick = output<void>();
  private readonly countdown = viewChild(RetryCountdown);
  protected readonly retryPending = computed(() => this.urlRetryAfterSec() !== null && (this.countdown()?.remaining() ?? this.urlRetryAfterSec() ?? 0) > 0);
  /**
   * The retry's own busy state (`loadingUrl` for a first read, the countdown for a storage wait). It is reported with
   * `aria-disabled`/`aria-busy` rather than the native attribute: the click is what makes the button busy, so a
   * focused button that natively disables itself loses the user's focus mid-interaction — and `retry()` is the guard.
   */
  protected readonly retryBusy = computed(() => this.loadingUrl() || this.retryPending());
  protected readonly storageDegraded = computed(() => this.urlErrorKey() === 'errors.service_unavailable');

  protected retry(): void {
    if (this.retryBusy()) return;
    this.retryUrl.emit();
  }
}
