import type { Result } from '@core/errors';
import type { Bible, BibleContent } from '../domain/bible.model';

/** The 200 body of a bible `PUT`/`POST …/accept`: the committed `{ version, etag }` (02.6.3 §5). */
export interface BibleWriteResult {
  readonly version: number;
  /** The quoted `"sb-<version>"` entity tag, identical to the response `ETag` header. */
  readonly etag: string;
}

/**
 * The series-bible read/write port (10.5.7-T1). Declared in `application/`, like `SeriesRepository`, because every
 * member returns the core-wide `Result<T>` from `@core/errors` and domain may not import `@core` (lint D2).
 *
 * `save()` is the only write that carries `If-Match` (the ETag the caller loaded); `accept()`/`dismiss()` carry
 * neither a body nor an ETag (02.6.3 §4 U3/U4) and are therefore safe to repeat. Nothing here retries or polls:
 * one call is one request.
 */
export abstract class BibleRepository {
  abstract get(seriesId: string): Promise<Result<Bible>>;

  /**
   * `PUT /api/v1/series/{id}/bible` with `If-Match: etag` and `{ content }` (full `bible.v1`, no `schema_version`).
   * `content` is the explicit camelCase model; the adapter maps it to snake_case and omits server-derived members.
   */
  abstract save(seriesId: string, content: BibleContent, etag: string): Promise<Result<BibleWriteResult>>;

  /** `POST /api/v1/series/{id}/bible/proposal/accept` — no body, no `If-Match`; the caller refetches. */
  abstract accept(seriesId: string): Promise<Result<void>>;

  /** `POST /api/v1/series/{id}/bible/proposal/dismiss` — no body, no `If-Match`; the caller refetches. */
  abstract dismiss(seriesId: string): Promise<Result<void>>;

  /**
   * The title of the episode a proposal was proposed from: one `GET /api/v1/episodes/{id}` whose only read member is
   * the title (the bible wire carries the id but no title, 10.5.7 `[OPEN] 08`). Any failure answers `null` — the
   * banner falls back to its `bannerNoTitle` copy and no error state is raised (P5: no retry).
   */
  abstract getEpisodeTitle(episodeId: string): Promise<string | null>;
}
