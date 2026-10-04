import type { ProblemDetails } from '@shared/api';

/**
 * The parsed `application/problem+json` body (09.1.4-T1, README D5/D9). It is the generated `ProblemDetails` plus the
 * members the OpenAPI document does not describe yet (backend gap recorded in STATUS §Plan 10 hand-overs):
 *
 * - `errors` — field → messages for `validation_failed` and the `*_invalid_content` codes; for `keywords_flagged` it is
 *   an array of `{ segment, index, rule }` rows instead (03 D10);
 * - the allow-listed flattened scalars the API copies from error metadata (`retryAfterSeconds`, `field`, `limit`) and the
 *   login step's `mfaToken`.
 *
 * `code` and `traceId` are optional here although the generated type requires them: a proxy or load-balancer page, or a
 * hand-written body, is not guaranteed to carry them, and the mapper falls back to the HTTP status in that case.
 *
 * This is the only file of `core/errors` that imports `@shared/api` (lint: only `**\/data-access/**` may, D2/D5).
 */
export type ProblemWire = Omit<ProblemDetails, 'code' | 'traceId'> & {
  readonly code?: string;
  readonly traceId?: string;
  readonly errors?: Readonly<Record<string, unknown>> | readonly unknown[];
  readonly retryAfterSeconds?: number;
  readonly field?: string;
  readonly limit?: number;
  readonly mfaToken?: string;
};
