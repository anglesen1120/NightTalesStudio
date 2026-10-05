import type { SeriesOptionsPage } from './episode.model';

/** Port of the series filter options: exactly the first page (limit 100) of the caller's active series. */
export abstract class SeriesOptionsRepository {
  abstract list(): Promise<SeriesOptionsPage>;
}
