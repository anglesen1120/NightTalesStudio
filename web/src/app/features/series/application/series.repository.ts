import type { Result } from '@core/errors';
import type { CreateSeriesInput, Series, SeriesListItem, SeriesPatchInput } from '../domain/series.model';

/** The query of one `GET /api/v1/series` page (09.4.1 §5). `cursor` is the opaque value of the previous page. */
export interface SeriesListQuery {
  readonly cursor: string | null;
  readonly limit?: number;
  readonly includeArchived: boolean;
}

/** One page as the wire returns it; `nextCursor === null` is the last page. */
export interface SeriesPage {
  readonly items: SeriesListItem[];
  readonly nextCursor: string | null;
}

/** The 201 body plus the `ETag` header of the created row (`null` when the header was unusable). */
export interface CreatedSeries {
  readonly id: string;
  readonly etag: string | null;
}

/** The 200 body of a `PATCH`: the committed ETag, the same value the `ETag` header carries. */
export interface UpdatedSeries {
  readonly etag: string;
}

/**
 * The series write/read port. Declared in `application/`, not in `domain/` as plan §3.2 sketched: every method
 * returns the core-wide `Result<T>` from `@core/errors`, and domain may not import `@core` (lint D2) — the same
 * placement and reason as `CatalogRepository`/`AuthRepository`.
 *
 * `patch()` never fabricates a `Series`: the 200 body is only `{ etag }`, so a caller that needs authoritative
 * fields reloads through `get()`. `archive()` is the `DELETE` of the API (the row is archived server-side, never
 * deleted) and carries no `If-Match` — the endpoint documents none.
 */
export abstract class SeriesRepository {
  abstract list(query: SeriesListQuery): Promise<Result<SeriesPage>>;
  abstract get(id: string): Promise<Result<Series>>;
  abstract create(input: CreateSeriesInput): Promise<Result<CreatedSeries>>;
  abstract patch(id: string, patch: SeriesPatchInput, etag: string): Promise<Result<UpdatedSeries>>;
  abstract archive(id: string): Promise<Result<void>>;
}
