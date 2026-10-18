import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { IdempotencyIntent, userAction, withIdempotency } from '@core/http';
import { EpisodeVideoEndpointsClient, VideoReviewEndpointsClient } from '@shared/api';
import {
  VideoReviewRepository,
  type RejectTarget,
  type VideoApproveAnswer,
  type VideoDecisionAnswer,
  type VideoRejectIntent,
  type VideoUrl,
} from '../domain/video-review.repository';

/** Keeps HTTP-context settlement linked to the domain port's caller-owned intent (10.2.3-T1). */
class VideoRejectHttpIntent extends IdempotencyIntent {
  constructor(private readonly source: VideoRejectIntent) {
    super(source.key, source.intent);
  }

  override get isSettled(): boolean {
    return super.isSettled || this.source.isSettled;
  }

  override settle(): void {
    super.settle();
    this.source.settle();
  }
}

/**
 * HTTP adapter of the video-review port; one of the files of `features/episodes` that import the generated client
 * (D2/D5). `getVideoUrl` carries `USER_ACTION` only for the owner's own retry, so the automatic read on view open and
 * the silent refresh never extend an idle session (WR11/D7); its answer is reduced to `{ url, expiresAt }` and the
 * URL is never logged, echoed or stored. `approveVideo` sends the episode's ETag as `If-Match` and **no** body when
 * there is no note; `rejectVideo` sends `If-Match` plus the caller-owned intent's key as `Idempotency-Key` together
 * with the `IDEMPOTENCY_INTENT` context (so the interceptor and a 401 replay see the same key) and observes the
 * response, because only the HTTP status separates the `story` 200 from the `re_render` 202 — a 202 is
 * `accepted = true`, never a fabricated job id. Nothing beyond `status`/`etag` is read from either answer: the
 * episode detail stays the authority for what the page renders (no optimistic state). Every failure becomes the
 * shared `AppError`.
 */
@Injectable()
export class VideoReviewHttpRepository extends VideoReviewRepository {
  private readonly video = inject(EpisodeVideoEndpointsClient);
  private readonly review = inject(VideoReviewEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async getVideoUrl(episodeId: string, opts?: { readonly userAction?: boolean }): Promise<VideoUrl> {
    try {
      const dto = await firstValueFrom(
        this.video.apiV1EpisodesIdVideoUrlGet({ id: episodeId }, opts?.userAction === true ? userAction() : undefined),
      );
      return { url: dto.url, expiresAt: dto.expiresAt };
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async approveVideo(episodeId: string, etag: string, note?: string): Promise<VideoApproveAnswer> {
    try {
      const answer = await firstValueFrom(
        this.review.apiV1EpisodesIdVideoApprovePost(
          note === undefined ? { id: episodeId, 'If-Match': etag } : { id: episodeId, 'If-Match': etag, body: { note } },
          userAction(),
        ),
      );
      return { status: answer.status, etag: answer.etag };
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async rejectVideo(
    episodeId: string,
    etag: string,
    reason: string,
    target: RejectTarget,
    intent: VideoRejectIntent,
  ): Promise<VideoDecisionAnswer> {
    // The context has to carry an `IdempotencyIntent` (the interceptor writes the header from it). The store's own
    // instance already is one, so it is passed through unchanged and only a structural caller is wrapped.
    const contextIntent = intent instanceof IdempotencyIntent ? intent : new VideoRejectHttpIntent(intent);
    try {
      const response = await firstValueFrom(
        this.review.apiV1EpisodesIdVideoRejectPost$Response(
          { id: episodeId, 'If-Match': etag, 'Idempotency-Key': intent.key, body: { reason, target } },
          withIdempotency(contextIntent, userAction()),
        ),
      );
      return { status: response.body.status, etag: response.body.etag, accepted: response.status === 202 };
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }
}
