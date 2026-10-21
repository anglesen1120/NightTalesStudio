import type { SeriesListItem } from './series.model';

/** Readonly presentation state; rows never own the archive operation. */
export interface SeriesRowViewModel {
  readonly series: SeriesListItem;
  readonly archived: boolean;
  readonly autopilot: boolean;
}

export function toSeriesRowViewModel(series: SeriesListItem): SeriesRowViewModel {
  return { series, archived: series.archivedAt !== null, autopilot: series.autopilotEnabled };
}
