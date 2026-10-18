import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { IdempotencyIntent, userAction, withIdempotency } from '@core/http';
import { RenderingEndpointsClient } from '@shared/api';
import { RenderRepository, type RenderCommandIntent } from '../domain/render.repository';

/** A render job id is a bounded URL-safe token; anything else in a `Location` is not an id this client sends back. */
const RENDER_JOB_ID_PATTERN = /^[A-Za-z0-9_-]{1,128}$/;

/** Keeps HTTP-context settlement linked to the domain port's caller-owned intent (10.2.3-T1). */
class RenderCommandHttpIntent extends IdempotencyIntent {
  constructor(private readonly source: RenderCommandIntent) {
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
 * The render job id of a 202's `Location`, or `null`. The route answers `/render-jobs/{id}` (F1); the API also
 * answers an absolute URL behind a proxy, so both forms are accepted. The query and the fragment are dropped, the id
 * must be the last segment and must follow `render-jobs`, and its shape is bounded — a missing, foreign or truncated
 * location is never turned into an id. The header value is never logged or echoed anywhere.
 *
 * `null` is an ACCEPTED command whose id this client cannot use, not a refusal: the server has already queued the
 * render, so the caller reads the authoritative episode instead of claiming an id it was never given (PS1/C6).
 */
export function renderJobIdFromLocation(location: string | null): string | null {
  if (location === null) return null;
  const path = location.split('?')[0]?.split('#')[0] ?? '';
  const segments = path.split('/').filter((segment) => segment !== '');
  if (segments.length < 2) return null;
  const id = segments[segments.length - 1] ?? '';
  return segments[segments.length - 2] === 'render-jobs' && RENDER_JOB_ID_PATTERN.test(id) ? id : null;
}

/**
 * HTTP adapter of the render port; one of the two files of `features/episodes` that import the generated rendering
 * client (D5). `requestRender` sends the caller's ETag as `If-Match` (no blind credit-spending write) and the
 * caller-owned intent's key as `Idempotency-Key` together with the `IDEMPOTENCY_INTENT` context, so the interceptor
 * (and a 401 replay) sees the same key; it answers only the job id of the 202's `Location` — no status is adopted
 * here. `cancelRender` sends neither header: the DELETE is server-idempotent (09.6.4-T1). Every failure becomes the
 * shared `AppError`; a 202 without a usable `Location` resolves `null` — an accepted command, never a fabricated id
 * and never a refusal.
 */
@Injectable()
export class RenderHttpRepository extends RenderRepository {
  private readonly client = inject(RenderingEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async requestRender(episodeId: string, etag: string, intent: RenderCommandIntent): Promise<string | null> {
    // The context has to carry an `IdempotencyIntent` (the interceptor writes the header from it). The store's own
    // instance already is one, so it is passed through unchanged and only a structural caller is wrapped.
    const contextIntent = intent instanceof IdempotencyIntent ? intent : new RenderCommandHttpIntent(intent);
    try {
      const response = await firstValueFrom(
        this.client.apiV1EpisodesIdRenderPost$Response(
          { id: episodeId, 'If-Match': etag, 'Idempotency-Key': intent.key },
          withIdempotency(contextIntent, userAction()),
        ),
      );
      return renderJobIdFromLocation(response.headers.get('Location'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async cancelRender(renderJobId: string): Promise<void> {
    try {
      // 204, also on a repeat: the server owns the refund (WT51) and needs no key of ours.
      await firstValueFrom(this.client.apiV1RenderJobsIdDelete$Response({ id: renderJobId }, userAction()));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }
}
