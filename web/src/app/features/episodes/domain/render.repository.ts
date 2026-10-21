/**
 * Structural command intent of the render POST; independent of the core HTTP implementation (10.2.3-T1, D2: a domain
 * port imports nothing of `@core`). `IdempotencyKeys.begin('episode.render')`'s `IdempotencyIntent` satisfies it as it
 * is, so the store passes its own intent straight through and the adapter wraps it for the `HttpContext` of
 * `withIdempotency` (the sibling `SourceFinderCommandIntent` sets the same precedent).
 */
export interface RenderCommandIntent {
  readonly key: string;
  readonly intent: string;
  readonly isSettled: boolean;
  settle(): void;
}

/**
 * Port of the two render commands of `/episodes/:id` (WD3): start a render from the approved story, and stop one the
 * owner no longer wants.
 *
 * `requestRender` is If-Match guarded — the episode's current ETag, so a credit-spending POST is never a blind write —
 * and idempotency-key guarded through the caller's own intent: the same intent is replayed by every retry of one
 * click, and a genuinely new click mints a new key. It answers the render job id of the 202's `Location`, or `null`
 * when that answer carried no usable one (a proxy that strips or rewrites the header): the command WAS applied, so
 * `null` is an accepted command whose id the caller never learns — never a refusal, and never a fabricated id.
 * `cancelRender` is server-idempotent (a repeat answers 204 as well) and therefore carries neither `If-Match` nor
 * `Idempotency-Key`. A failure is the structured `AppError` of `@core/errors`, exactly like the sibling ports.
 */
export abstract class RenderRepository {
  /**
   * `POST /api/v1/episodes/{id}/render` (202) → the job id parsed from `Location: /render-jobs/{id}`, or `null` when
   * the 202 carried no usable `Location`.
   */
  abstract requestRender(episodeId: string, etag: string, intent: RenderCommandIntent): Promise<string | null>;

  /** `DELETE /api/v1/render-jobs/{id}` (204, also on a repeat). */
  abstract cancelRender(renderJobId: string): Promise<void>;
}
