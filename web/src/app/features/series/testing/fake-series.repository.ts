import type { AppError, Result } from '@core/errors';
import {
  SeriesRepository,
  type CreatedSeries,
  type SeriesListQuery,
  type SeriesPage,
  type UpdatedSeries,
} from '../application/series.repository';
import type { CreateSeriesInput, Series, SeriesPatchInput } from '../domain/series.model';

// The three failures the fake scripts, spelled like the mapper's own answers so a store sees the same codes.
const NOT_FOUND: AppError = { code: 'not_found', status: 404, kind: 'not_found', fieldErrors: {} };
const SERIES_CONFLICT: AppError = { code: 'series_conflict', status: 412, kind: 'conflict', fieldErrors: {} };
const SERIES_ARCHIVED: AppError = { code: 'series_archived', status: 409, kind: 'precondition', fieldErrors: {} };

/** A canned row: three active series and one archived, so both list modes and the archive path have data. */
export const CANNED_SERIES: readonly Series[] = [
  {
    id: 's-ghost',
    title: 'Chuyện ma đêm khuya',
    topic: 'ghost',
    language: 'vi',
    aspect: 'landscape',
    voiceId: 'vi-quiet',
    styleTemplateId: 't-dark-folklore-ink',
    storyPreset: null,
    autopilotEnabled: false,
    archivedAt: null,
    createdAt: '2026-09-01T02:00:00+00:00',
    updatedAt: '2026-09-02T02:00:00+00:00',
    etag: '"se-41"',
  },
  {
    id: 's-spiritual',
    title: 'Đền chùa kể chuyện',
    topic: 'spiritual',
    language: 'vi',
    aspect: 'portrait',
    voiceId: 'vi-warm',
    styleTemplateId: 't-watercolor-night',
    storyPreset: 'calm',
    autopilotEnabled: true,
    archivedAt: null,
    createdAt: '2026-09-03T02:00:00+00:00',
    updatedAt: '2026-09-04T02:00:00+00:00',
    etag: '"se-52"',
  },
  {
    id: 's-true-crime',
    title: 'Hồ sơ chưa khép',
    topic: 'true_crime',
    language: 'en',
    aspect: 'landscape',
    voiceId: 'en-news',
    styleTemplateId: 't-noir-sketch',
    storyPreset: null,
    autopilotEnabled: false,
    archivedAt: null,
    createdAt: '2026-09-05T02:00:00+00:00',
    updatedAt: '2026-09-06T02:00:00+00:00',
    etag: '"se-63"',
  },
  {
    id: 's-archived',
    title: 'Loạt bài cũ',
    topic: 'ghost',
    language: 'vi',
    aspect: 'landscape',
    voiceId: 'vi-hue',
    styleTemplateId: 't-foggy-cinematic',
    storyPreset: null,
    autopilotEnabled: false,
    archivedAt: '2026-09-07T02:00:00+00:00',
    createdAt: '2026-08-01T02:00:00+00:00',
    updatedAt: '2026-09-07T02:00:00+00:00',
    etag: '"se-74"',
  },
];

/** The `xmin` an ETag was derived from (`"se-41"` → 41); the fake's version, so a write can bump it (09 README D15). */
function etagVersion(etag: string): number {
  const digits = /(\d+)/.exec(etag)?.[1];
  return digits === undefined ? 0 : Number.parseInt(digits, 10);
}

function nextEtag(etag: string): string {
  return `"se-${String(etagVersion(etag) + 1)}"`;
}

/**
 * Hand-written scriptable `SeriesRepository` (README D15: no mocking library).
 *
 * - Every method appends to its call log (`listCalls`, `getCalls`, `createCalls`, `patchCalls`, `archiveCalls`), so a
 *   spec asserts behaviour — request counts and payload shapes — and never wiring.
 * - `list` pages over the canned rows by its own `pageSize` (default 2) and mints an opaque cursor per page, so a
 *   store spec sees two pages without 26 rows.
 * - `patch` is version-guarded: a call whose `etag` is not the row's current one answers 412 `series_conflict`, and a
 *   row with `archivedAt !== null` answers 409 `series_archived`. `advanceVersion(id)` makes the tag the caller holds
 *   stale on purpose. A successful patch returns the new tag and never a fabricated `Series`.
 * - `failWith(error)` fails every later call; `failNext(error)` fails exactly one call (FIFO). `defer()` holds every
 *   later call until `resolveNext()`/`resolveAll()`, which is how a spec creates a second click while one is pending
 *   and how it delivers a late response.
 */
export class FakeSeriesRepository extends SeriesRepository {
  readonly listCalls: SeriesListQuery[] = [];
  readonly getCalls: string[] = [];
  readonly createCalls: CreateSeriesInput[] = [];
  readonly patchCalls: Array<{ id: string; patch: SeriesPatchInput; etag: string }> = [];
  readonly archiveCalls: string[] = [];

  /** Page size the fake applies regardless of `limit`, so a small canned set still has two pages. */
  pageSize = 2;

  private rows: Series[] = CANNED_SERIES.map((series) => ({ ...series }));
  private failure: AppError | null = null;
  private readonly queuedFailures: AppError[] = [];
  private deferred = false;
  private readonly pending: Array<() => void> = [];

  /** Replaces the served rows (fresh copies; the canned set stays untouched). */
  willServe(series: readonly Series[]): this {
    this.rows = series.map((row) => ({ ...row }));
    return this;
  }

  /** Fails every later call with `error` until `failWith(null)`. */
  failWith(error: AppError | null): this {
    this.failure = error;
    return this;
  }

  /** Fails exactly the next call (in call order) with `error`. */
  failNext(error: AppError): this {
    this.queuedFailures.push(error);
    return this;
  }

  /** Holds every later call until `resolveNext()`/`resolveAll()`. */
  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  get pendingCount(): number {
    return this.pending.length;
  }

  resolveNext(): void {
    const next = this.pending.shift();
    if (next === undefined) throw new Error('no pending call');
    next();
  }

  resolveAll(): void {
    for (const resolve of this.pending.splice(0)) resolve();
  }

  /** Bumps a row's tag without changing its values: the tag a caller already holds becomes stale. */
  advanceVersion(id: string): void {
    const row = this.rowOf(id);
    const bumped: Series = { ...row, etag: nextEtag(row.etag) };
    this.rows = this.rows.map((candidate) => (candidate.id === row.id ? bumped : candidate));
  }

  /** The row as stored (a copy), for an assertion on what a patch did or did not change. */
  stored(id: string): Series | null {
    const row = this.rows.find((candidate) => candidate.id === id);
    return row === undefined ? null : { ...row };
  }

  override list(query: SeriesListQuery): Promise<Result<SeriesPage>> {
    this.listCalls.push(query);
    const failure = this.takeFailure();
    if (failure !== null) return this.settle({ ok: false, error: failure });
    const visible = this.rows.filter((series) => query.includeArchived || series.archivedAt === null);
    const start = query.cursor === null ? 0 : this.offsetOf(query.cursor);
    const slice = visible.slice(start, start + this.pageSize);
    const end = start + slice.length;
    const nextCursor = end < visible.length ? `p${String(end)}` : null;
    return this.settle({ ok: true, value: { items: slice.map((series) => ({ ...series })), nextCursor } });
  }

  override get(id: string): Promise<Result<Series>> {
    this.getCalls.push(id);
    const failure = this.takeFailure();
    if (failure !== null) return this.settle({ ok: false, error: failure });
    const row = this.rows.find((candidate) => candidate.id === id);
    return this.settle(row === undefined ? { ok: false, error: NOT_FOUND } : { ok: true, value: { ...row } });
  }

  override create(input: CreateSeriesInput): Promise<Result<CreatedSeries>> {
    this.createCalls.push(input);
    const failure = this.takeFailure();
    if (failure !== null) return this.settle({ ok: false, error: failure });
    const created: Series = {
      id: `s-created-${String(this.rows.length + 1)}`,
      ...input,
      autopilotEnabled: false,
      archivedAt: null,
      createdAt: '2026-10-05T02:00:00+00:00',
      updatedAt: '2026-10-05T02:00:00+00:00',
      etag: '"se-1"',
    };
    this.rows.push(created);
    return this.settle({ ok: true, value: { id: created.id, etag: created.etag } });
  }

  override patch(id: string, patch: SeriesPatchInput, etag: string): Promise<Result<UpdatedSeries>> {
    this.patchCalls.push({ id, patch, etag });
    const failure = this.takeFailure();
    if (failure !== null) return this.settle({ ok: false, error: failure });
    const row = this.rows.find((candidate) => candidate.id === id);
    if (row === undefined) return this.settle({ ok: false, error: NOT_FOUND });
    if (row.archivedAt !== null) return this.settle({ ok: false, error: SERIES_ARCHIVED });
    if (etag !== row.etag) return this.settle({ ok: false, error: SERIES_CONFLICT });
    // `Series` members are readonly: every mutate replaces the stored row instead of assigning into it.
    const updated: Series = Object.assign({}, row, patch, { etag: nextEtag(etag), updatedAt: '2026-10-05T03:00:00+00:00' });
    this.rows = this.rows.map((candidate) => (candidate.id === id ? updated : candidate));
    return this.settle({ ok: true, value: { etag: updated.etag } });
  }

  override archive(id: string): Promise<Result<void>> {
    this.archiveCalls.push(id);
    const failure = this.takeFailure();
    if (failure !== null) return this.settle({ ok: false, error: failure });
    const row = this.rows.find((candidate) => candidate.id === id);
    if (row === undefined) return this.settle({ ok: false, error: NOT_FOUND });
    if (row.archivedAt !== null) return this.settle({ ok: false, error: SERIES_ARCHIVED });
    const archived: Series = { ...row, archivedAt: '2026-10-05T03:00:00+00:00' };
    this.rows = this.rows.map((candidate) => (candidate.id === id ? archived : candidate));
    return this.settle({ ok: true, value: undefined });
  }

  private rowOf(id: string): Series {
    const row = this.rows.find((candidate) => candidate.id === id);
    if (row === undefined) throw new Error(`no row ${id}`);
    return row;
  }

  private takeFailure(): AppError | null {
    return this.queuedFailures.shift() ?? this.failure;
  }

  /** The offset of an opaque cursor, with no validation: a tampered value lands on the first page (0). */
  private offsetOf(cursor: string): number {
    const parsed = Number.parseInt(cursor.slice(1), 10);
    return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : 0;
  }

  private settle<T>(value: Result<T>): Promise<Result<T>> {
    if (!this.deferred) return Promise.resolve(value);
    return new Promise<Result<T>>((resolve) => {
      this.pending.push(() => {
        resolve(value);
      });
    });
  }
}
