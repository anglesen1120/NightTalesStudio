import type { CursorPage } from '@shared/util/cursor-page';
import type { EpisodeListItem, EpisodeStatus } from './episode.model';

/** Port of the episode endpoints. `get()` is added by 10.5.2-T1. */
export abstract class EpisodesRepository {
  abstract list(q: {
    status?: EpisodeStatus;
    seriesId?: string;
    cursor?: string;
    limit: number;
  }): Promise<CursorPage<EpisodeListItem>>;
}
