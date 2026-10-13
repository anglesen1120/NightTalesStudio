import type { EpisodeDetailDto, EpisodeListItemDto } from '@shared/api';
import { EPISODE_STATUSES, type EpisodeDetail, type EpisodeListItem } from '../domain/episode.model';
import { mapStorySnapshot } from './story.mapper';

/**
 * Maps one wire row. A status this client does not know (a newer server) yields `null`: the row cannot be given a
 * status chip (`episodes.status.<status>` has no key), so the repository drops it instead of crashing the page or
 * rendering a raw wire string. `etag` and the ISO date strings pass through untouched.
 */
export function mapEpisodeListItem(dto: EpisodeListItemDto): EpisodeListItem | null {
  const status = EPISODE_STATUSES.find((known) => known === dto.status);
  if (status === undefined) return null;
  return {
    id: dto.id,
    seriesId: dto.seriesId,
    title: dto.title,
    status,
    lengthTier: dto.lengthTier,
    createdAt: dto.createdAt,
    updatedAt: dto.updatedAt,
    etag: dto.etag,
  };
}

export function mapEpisodeDetail(dto: EpisodeDetailDto): EpisodeDetail {
  const generation = dto.generation;
  const paused = (generation as { processingPaused?: unknown } | null)?.processingPaused;
  return {
    id: dto.id,
    seriesId: dto.seriesId,
    status: dto.status,
    lengthTier: dto.lengthTier,
    etag: dto.etag,
    generation: generation === null ? null : {
      kind: generation.kind,
      status: generation.status,
      stage: generation.stage,
      errorCode: generation.errorCode,
      startedAt: generation.startedAt,
      processingPaused: typeof paused === 'string' ? paused : null,
    },
    hasCurrentStory: dto.currentStory !== null,
    currentStory: dto.currentStory === null ? null : mapStorySnapshot(dto.currentStory),
    nextActions: dto.nextActions,
  };
}
