import type { EpisodeDetailDto, EpisodeListItemDto } from '@shared/api';
import { EPISODE_STATUSES } from '../domain/episode.model';
import { mapEpisodeDetail, mapEpisodeListItem } from './episode.mapper';

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

const DETAIL: EpisodeDetailDto = {
  id: DTO.id, seriesId: DTO.seriesId, title: null, brief: 'A lantern', lengthTier: 'standard',
  status: 'story_generating', etag: DTO.etag, currentStory: null,
  generation: { kind: 'story', status: 'running', stage: 'writing', errorCode: null, startedAt: null },
  nextActions: ['edit_story'], finalVideo: null, publications: null, renderJob: null,
};

describe('mapEpisodeDetail', () => {
  it('mapsGenerationAndNextActions_WithoutCopyingStoryText', () => {
    expect(mapEpisodeDetail(DETAIL)).toEqual({
      id: DETAIL.id, seriesId: DETAIL.seriesId, status: DETAIL.status, etag: DETAIL.etag,
      generation: { ...DETAIL.generation, processingPaused: null },
      hasCurrentStory: false, nextActions: ['edit_story'],
    });
  });

  it('currentStoryPresence_IsMappedToBooleanOnly', () => {
    const current = { ...DETAIL, currentStory: { body: 'Never copy this text' } } as unknown as EpisodeDetailDto;
    const result = mapEpisodeDetail(current);
    expect(result.hasCurrentStory).toBe(true);
    expect(result).not.toHaveProperty('currentStory');
  });

  it('nullGeneration_IsPreserved', () => {
    expect(mapEpisodeDetail({ ...DETAIL, generation: null }).generation).toBeNull();
  });

  it('processingPaused_String_IsReadDefensivelyAcrossTheDtoGap', () => {
    const paused = { ...DETAIL, generation: { ...DETAIL.generation, processingPaused: 'provider_funds_exhausted' } } as unknown as EpisodeDetailDto;
    expect(mapEpisodeDetail(paused).generation?.processingPaused).toBe('provider_funds_exhausted');
  });

  it.each([undefined, null, 4, true])('processingPaused_%s_NonStringIsIgnored', (processingPaused) => {
    const generation = { kind: 'story', status: 'running', stage: 'writing', errorCode: null, startedAt: null, processingPaused };
    const wire = { ...DETAIL, generation } as unknown as EpisodeDetailDto;
    expect(mapEpisodeDetail(wire).generation?.processingPaused).toBeNull();
  });
});
