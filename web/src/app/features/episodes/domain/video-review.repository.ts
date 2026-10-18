/**
 * The video-review half of `/episodes/:id` (WD4): the short-lived signed playback URL and the two decisions the owner
 * takes on the finished cut.
 *
 * Domain rules (D2): no Angular, no rxjs, nothing of `@core` — so the reject's command intent is a structural
 * interface here (exactly like `RenderCommandIntent` of the sibling port), which the store's own
 * `IdempotencyIntent` satisfies as it is and which the adapter wraps for the `HttpContext` of `withIdempotency`.
 * No field of any answer is trusted beyond `status`/`etag`/`accepted`: the episode detail stays the authority for
 * the state the page renders.
 */

/**
 * The signed playback URL and the instant it stops working (ISO-8601 UTC). Memory-only: never persisted, cached for
 * the session, logged, put in the URL bar or sent anywhere but the `<video>`/download element (WD10/F10).
 */
export interface VideoUrl {
  readonly url: string;
  readonly expiresAt: string;
}

/** The two targets a rejection can name (09.7.1-T1): fix the script, or re-make the video from the same script. */
export type RejectTarget = 'story' | 're_render';

/** `200`/`202` of a decision: the state the server reports, and whether the command was merely accepted (202). */
export interface VideoDecisionAnswer {
  readonly status: string;
  readonly etag: string;
  /** `true` for the `re_render` 202 — an accepted command with no resource and no `Location` (F9/F12). */
  readonly accepted: boolean;
}

/** The approve answer: `200`, no idempotency key on the route and no body beyond the state (F9). */
export interface VideoApproveAnswer {
  readonly status: string;
  readonly etag: string;
}

/**
 * Structural command intent of the reject POST; independent of the core HTTP implementation (D2). The store's
 * `IdempotencyKeys.begin('episode.video.reject')` instance satisfies it, so one click's retries carry one key and the
 * server replays one rejection instead of applying a second one (09.7.1-T1:167 requires the key for exactly that).
 */
export interface VideoRejectIntent {
  readonly key: string;
  readonly intent: string;
  readonly isSettled: boolean;
  settle(): void;
}

/**
 * Port of the review commands. `getVideoUrl` issues a fresh, audited, short-lived URL on every call — a client that
 * cached or persisted one would keep serving an expired link (WB3/F10) — and carries `USER_ACTION` only when the
 * owner asked for it (a manual retry), never on the automatic read, so an idle tab does not extend the session
 * (WR11/D7). `approveVideo` is `If-Match` guarded and keyless; `rejectVideo` is `If-Match` + `Idempotency-Key`
 * guarded through the caller's own intent, and answers `accepted = true` for the `re_render` 202 (no `Location`).
 * Every failure is the structured `AppError` of `@core/errors`, exactly like the sibling ports.
 */
export abstract class VideoReviewRepository {
  /** `GET /api/v1/episodes/{id}/video-url` (200 `{ url, expiresAt }`; 404 no final asset or foreign). */
  abstract getVideoUrl(episodeId: string, opts?: { readonly userAction?: boolean }): Promise<VideoUrl>;

  /** `POST /api/v1/episodes/{id}/video/approve` with the episode's ETag and an optional note (≤ 500). */
  abstract approveVideo(episodeId: string, etag: string, note?: string): Promise<VideoApproveAnswer>;

  /**
   * `POST /api/v1/episodes/{id}/video/reject` with the episode's ETag, the reason (1–500), the target and the
   * caller's own intent. `story` answers 200; `re_render` answers 202 with **no** `Location`, which is
   * `accepted = true` and never a fabricated render job id.
   */
  abstract rejectVideo(
    episodeId: string,
    etag: string,
    reason: string,
    target: RejectTarget,
    intent: VideoRejectIntent,
  ): Promise<VideoDecisionAnswer>;
}
