import { Injectable, computed, inject, signal } from '@angular/core';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import type { Series } from '../domain/series.model';
import { SeriesRepository } from './series.repository';

/**
 * State of `/series/:id` (README D2: application layer, page-scoped `@Injectable()` — the detail page provides it).
 * One `GET /series/{id}` per `load(id)`; a reply whose id or session generation is no longer current is discarded.
 * The archived and not-found states are separate signals so the page can render each without inspecting the error.
 */
@Injectable()
export class SeriesDetailStore {
  private readonly repository = inject(SeriesRepository);
  private readonly session = inject(SessionStore);

  private requestId = 0;

  readonly series = signal<Series | null>(null);
  readonly loading = signal(false);
  readonly notFound = signal(false);
  readonly error = signal<AppError | null>(null);
  /** The series exists and is archived (09.4.1: an archived row stays readable). */
  readonly archived = computed(() => (this.series()?.archivedAt ?? null) !== null);
  /** Seconds to wait after a 429 (from `Retry-After`), `null` otherwise; feeds `RetryCountdown`. */
  readonly retryAfterSec = computed<number | null>(() => {
    const error = this.error();
    return error !== null && error.kind === 'rate_limit' ? (error.retryAfterSec ?? null) : null;
  });

  /**
   * Loads one series. A `404`/`not_found` answers through `notFound` (no retry loop, no id echo); every other failure
   * through `error`. A reply that arrives after a newer `load()` or after the session ended changes nothing.
   */
  async load(id: string): Promise<void> {
    const requestId = ++this.requestId;
    const sessionGeneration = this.session.generation();
    this.loading.set(true);
    this.notFound.set(false);
    this.error.set(null);
    const result = await this.repository.get(id);
    if (requestId !== this.requestId || sessionGeneration !== this.session.generation()) return;
    this.loading.set(false);
    if (!result.ok) {
      if (result.error.status === 404 || result.error.code === 'not_found') {
        this.series.set(null);
        this.notFound.set(true);
        return;
      }
      this.error.set(result.error);
      return;
    }
    this.series.set(result.value);
  }
}
