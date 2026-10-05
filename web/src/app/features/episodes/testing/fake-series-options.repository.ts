import type { SeriesOptionsPage } from '../domain/episode.model';
import { SeriesOptionsRepository } from '../domain/series-options.repository';

/**
 * Hand-written scriptable `SeriesOptionsRepository`. Immediate mode answers `page` (or fails with `error`); with
 * `defer()` the call stays pending until `resolve`/`reject`.
 */
export class FakeSeriesOptionsRepository extends SeriesOptionsRepository {
  calls = 0;
  page: SeriesOptionsPage = { items: [], bounded: false };
  error: unknown = null;
  private deferred = false;
  private pending: { resolve: (page: SeriesOptionsPage) => void; reject: (error: unknown) => void } | null = null;

  defer(on = true): this {
    this.deferred = on;
    return this;
  }

  resolve(page: SeriesOptionsPage): void {
    this.pending?.resolve(page);
    this.pending = null;
  }

  reject(error: unknown): void {
    this.pending?.reject(error);
    this.pending = null;
  }

  override list(): Promise<SeriesOptionsPage> {
    this.calls++;
    if (this.deferred) {
      return new Promise((resolve, reject) => {
        this.pending = { resolve, reject };
      });
    }
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- scripts any wire failure
    return this.error === null ? Promise.resolve(this.page) : Promise.reject(this.error);
  }
}
