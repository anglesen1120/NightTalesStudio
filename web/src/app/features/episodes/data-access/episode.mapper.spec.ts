import type { EpisodeListItemDto } from '@shared/api';
import { EPISODE_STATUSES } from '../domain/episode.model';
import { mapEpisodeListItem } from './episode.mapper';

const DTO: EpisodeListItemDto = {
  id: 'e1',
  seriesId: '11111111-1111-4111-8111-111111111111',
  title: 'Night walk',
  status: 'story_review',
  lengthTier: 'standard',
  createdAt: '2026-10-01T10:00:00.000+00:00',
  updatedAt: '2026-10-02T10:30:00.000+00:00',
  etag: '"ep-4711"',
};

describe('mapEpisodeListItem', () => {
  it('passesSnakeCaseStatus_EtagAndIsoDatesThroughUntouched', () => {
    expect(mapEpisodeListItem(DTO)).toEqual({ ...DTO });
  });

  it('keepsNullTitleNull_NeverSubstitutesText', () => {
    expect(mapEpisodeListItem({ ...DTO, title: null })?.title).toBeNull();
  });

  it.each(EPISODE_STATUSES)('knowsWireStatus_%s', (status) => {
    expect(mapEpisodeListItem({ ...DTO, status })?.status).toBe(status);
  });

  it('unknownWireStatus_YieldsNullInsteadOfThrowingOrLeakingTheRawString', () => {
    const unknown = { ...DTO, status: 'archived_in_the_future' } as unknown as EpisodeListItemDto;
    expect(mapEpisodeListItem(unknown)).toBeNull();
  });
});
