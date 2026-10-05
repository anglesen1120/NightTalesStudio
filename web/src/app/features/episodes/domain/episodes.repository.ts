import type { CursorPage } from '@shared/util/cursor-page';
import type { CreatedEpisode, EpisodeDetail, EpisodeListItem, EpisodeStatus, LengthTier } from './episode.model';

/** Structural command intent: independent of the core HTTP implementation. */
export interface StoryGenerationIntent {
  readonly key: string;
  readonly intent: string;
  readonly isSettled: boolean;
  settle(): void;
}

/** Port of the episode endpoints. Background detail reads are not user actions. */
export abstract class EpisodesRepository {
  abstract list(q: {
    status?: EpisodeStatus;
    seriesId?: string;
    cursor?: string;
    limit: number;
  }): Promise<CursorPage<EpisodeListItem>>;
  abstract create(seriesId: string, input: { title: string | null; brief: string; lengthTier: LengthTier }): Promise<CreatedEpisode>;
  abstract generateStory(episodeId: string, intent: StoryGenerationIntent): Promise<void>;
  abstract get(episodeId: string, opts?: { userAction?: boolean }): Promise<EpisodeDetail>;
}
