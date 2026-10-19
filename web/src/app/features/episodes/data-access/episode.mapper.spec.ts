import type { EpisodeDetailDto, EpisodeListItemDto, RenderJobSummaryDto } from '@shared/api';
import { EPISODE_STATUSES } from '../domain/episode.model';
import { storySnapshot } from '../testing/fake-story.repository';
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
  it('mapsGenerationActionsAndNullCurrentStory', () => {
    expect(mapEpisodeDetail(DETAIL)).toEqual({
      id: DETAIL.id, seriesId: DETAIL.seriesId, status: DETAIL.status, etag: DETAIL.etag,
      generation: { ...DETAIL.generation, processingPaused: null },
      hasCurrentStory: false, currentStory: null, nextActions: ['edit_story'], lengthTier: DETAIL.lengthTier,
      renderJob: null, title: null, finalVideo: null,
    });
  });

  it('currentStory_IsValidatedAndMappedForTheEditor', () => {
    const snapshot = storySnapshot();
    const current = { ...DETAIL, currentStory: snapshot } as unknown as EpisodeDetailDto;
    const result = mapEpisodeDetail(current);
    expect(result.hasCurrentStory).toBe(true);
    expect(result.currentStory).toEqual(snapshot);
  });

  it('malformedCurrentStory_NeverBecomesAnEmptyDraft', () => {
    const current = { ...DETAIL, currentStory: { body: 'Not a story.v1 document' } } as unknown as EpisodeDetailDto;
    expect(() => mapEpisodeDetail(current)).toThrow('Invalid story response');
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

/** A wire render summary; `percent`/`attempt`/`imagesNeeded` are `(number | string)` in the generated type. */
function wireJob(overrides: Partial<RenderJobSummaryDto> = {}): RenderJobSummaryDto {
  return {
    renderJobId: 'job-1',
    status: 'queued',
    stage: 'queued',
    percent: 0,
    attempt: 1,
    imagesNeeded: 0,
    submittedAt: null,
    finishedAt: null,
    errorCode: null,
    hints: [],
    ...overrides,
  };
}

/** The detail DTO of a rendering episode whose job is the given (possibly malformed) wire object. */
function withJob(renderJob: unknown): EpisodeDetailDto {
  return { ...DETAIL, status: 'rendering', renderJob } as unknown as EpisodeDetailDto;
}

describe('mapEpisodeDetail render job', () => {
  it.each([null, undefined, 'not-a-job'])('renderJobOfShape_%s_StaysNull_NeverAFabricatedJob', (renderJob) => {
    expect(mapEpisodeDetail(withJob(renderJob)).renderJob).toBeNull();
  });

  it('queuedJob_KeepsNullTimes_NeverInventsASubmitTime', () => {
    expect(mapEpisodeDetail(withJob(wireJob())).renderJob).toEqual({
      renderJobId: 'job-1', status: 'queued', stage: 'queued', percent: 0, attempt: 1, imagesNeeded: 0,
      submittedAt: null, finishedAt: null, errorCode: null, hints: [], images: null,
    });
  });

  it('runningJob_ReadsNumericStringCountersAndKeepsTheWireTimes', () => {
    const job = wireJob({
      status: 'running', stage: 'assembling', percent: '42', attempt: '2', imagesNeeded: '7',
      submittedAt: '2026-10-18T10:00:00.000+00:00', finishedAt: null,
    });
    expect(mapEpisodeDetail(withJob(job)).renderJob).toEqual({
      renderJobId: 'job-1', status: 'running', stage: 'assembling', percent: 42, attempt: 2, imagesNeeded: 7,
      submittedAt: '2026-10-18T10:00:00.000+00:00', finishedAt: null, errorCode: null, hints: [], images: null,
    });
  });

  it.each([['250', 100], ['42', 42], ['-3', 0], ['abc', 0], ['', 0], ['0x10', 0], ['4.2e1', 42], ['.5', 0]])('percent %j is parsed and clamped to %s', (percent, expected) => {
    expect(mapEpisodeDetail(withJob(wireJob({ percent }))).renderJob?.percent).toBe(expected);
  });

  it('hostilelyLongNumericString_FallsBackWithoutScanningTheWholePayload', () => {
    // A digit run the API never sends: the test itself would take seconds (quadratic backtracking) without the
    // length bound, and the mapper must still answer with the safe default rather than freeze the detail fetch.
    const hostile = `${'1'.repeat(200_000)}x`;
    const detail = mapEpisodeDetail(withJob(wireJob({ percent: hostile, attempt: hostile, imagesNeeded: hostile })));
    expect(detail.renderJob?.percent).toBe(0);
    expect(detail.renderJob?.attempt).toBe(0);
    expect(detail.renderJob?.imagesNeeded).toBe(0);
  });

  it('nullStage_BecomesAnEmptyString_SoNothingRendersItRaw', () => {
    expect(mapEpisodeDetail(withJob(wireJob({ stage: null }))).renderJob?.stage).toBe('');
  });

  it('unknownStatus_IsMappedToAStatusThatIsNeitherActiveNorAnError', () => {
    const detail = mapEpisodeDetail(withJob(wireJob({ status: 'awaiting_gpu', stage: 'queued' })));
    expect(detail.renderJob?.status).toBe('cancelled');
    expect(JSON.stringify(detail)).not.toContain('awaiting_gpu');
  });

  it('failedJob_KeepsTheErrorCodeAndOnlyTheStringHints', () => {
    // `hints` is typed `string[]`: the malformed wire shape (a number, a null, an empty string) only exists through a
    // cast, which is exactly the shape the mapper has to survive.
    const job = {
      ...wireJob({ status: 'failed', stage: 'assembling', percent: 61, finishedAt: '2026-10-18T10:05:00.000+00:00', errorCode: 'render_quality_failed' }),
      hints: ['Retry the render', 4, null, ''],
    } as unknown as RenderJobSummaryDto;
    const mapped = mapEpisodeDetail(withJob(job)).renderJob;
    expect(mapped?.errorCode).toBe('render_quality_failed');
    expect(mapped?.finishedAt).toBe('2026-10-18T10:05:00.000+00:00');
    expect(mapped?.hints).toEqual(['Retry the render']);
  });

  it('emptyErrorCode_IsNull_SoThePanelNeverOffersAnEmptyCode', () => {
    expect(mapEpisodeDetail(withJob(wireJob({ errorCode: '' }))).renderJob?.errorCode).toBeNull();
  });

  it('images_SoftField_IsMappedFromTheUntypedSideOfTheDto', () => {
    const images = { needed: 4, succeeded: 3, skipped: [{ segment: 'seg-1', reason: 'moderated' }] };
    expect(mapEpisodeDetail(withJob({ ...wireJob(), images })).renderJob?.images).toEqual({
      needed: 4, succeeded: 3, skipped: [{ segment: 'seg-1', reason: 'moderated' }],
    });
  });

  it('images_NumericStringCountersAreRead', () => {
    expect(mapEpisodeDetail(withJob({ ...wireJob(), images: { needed: '4', succeeded: '3', skipped: [] } })).renderJob?.images)
      .toEqual({ needed: 4, succeeded: 3, skipped: [] });
  });

  it.each([undefined, null, 'nope', 7, []])('images_%s_IsNull_NotAJobWithoutScenes', (images) => {
    expect(mapEpisodeDetail(withJob({ ...wireJob(), images })).renderJob?.images).toBeNull();
  });

  it('images_WithoutAnyUsableMember_IsNull', () => {
    expect(mapEpisodeDetail(withJob({ ...wireJob(), images: {} })).renderJob?.images).toBeNull();
    expect(mapEpisodeDetail(withJob({ ...wireJob(), images: { needed: 'x', skipped: [] } })).renderJob?.images).toBeNull();
  });

  it('images_DropsEveryMalformedSceneAndKeepsTheCountersItCouldRead', () => {
    const images = { needed: 'x', succeeded: 3, skipped: [{ segment: '', reason: 'moderated' }, 'nope', { segment: 'seg-2', reason: 'timeout' }, { segment: 'seg-3' }, { segment: 4, reason: 5 }] };
    expect(mapEpisodeDetail(withJob({ ...wireJob(), images })).renderJob?.images).toEqual({
      needed: 0, succeeded: 3, skipped: [{ segment: 'seg-2', reason: 'timeout' }],
    });
  });

  it('aJobOfEveryWrongShape_NeverThrows', () => {
    const nonsense = {
      renderJobId: 7, status: 42, stage: 9, percent: {}, attempt: [], imagesNeeded: 'x',
      submittedAt: 5, finishedAt: {}, errorCode: 12, hints: 'no', images: 3,
    };
    expect(mapEpisodeDetail(withJob(nonsense)).renderJob).toEqual({
      renderJobId: '', status: 'cancelled', stage: '', percent: 0, attempt: 0, imagesNeeded: 0,
      submittedAt: null, finishedAt: null, errorCode: null, hints: [], images: null,
    });
  });
});
