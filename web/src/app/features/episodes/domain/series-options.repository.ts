import type { SeriesOptionsPage, SeriesSummary } from './episode.model';

/** Port of the bounded series filter options and the series read on page open. */
export abstract class SeriesOptionsRepository {
  abstract list(): Promise<SeriesOptionsPage>;
  abstract get(id: string): Promise<SeriesSummary>;
}
