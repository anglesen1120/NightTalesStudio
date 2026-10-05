import type { StoryVersionDto } from '@shared/api';
import { storyContent } from '../testing/fake-story.repository';
import { StoryMappingError, isStoryContent, mapStorySnapshot } from './story.mapper';

export const STORY_DTO: StoryVersionDto = {
  versionId: '33333333-3333-4333-8333-333333333333', version: 1, etag: '"sv-body"', source: 'generated', wordCount: 18,
  content: storyContent(), qualityHints: [], createdAt: '2026-10-05T00:00:00Z', factSheetId: null,
  keywords: ['lantern'], parentVersionId: null, title: 'The lantern',
};
describe('story mapper', () => {
  it('mapsContentAndNumericStrings_WithoutClientTrackingKeys', () => {
    const result = mapStorySnapshot({ ...STORY_DTO, version: '2', wordCount: '28' });
    expect(result.version).toBe(2); expect(result.wordCount).toBe(28); expect(result.content).toBe(STORY_DTO.content);
  });
  it('responseEtag_HeaderWinsThenBodyFallback', () => {
    expect(mapStorySnapshot(STORY_DTO, '"sv-header"').etag).toBe('"sv-header"'); expect(mapStorySnapshot(STORY_DTO, null).etag).toBe(STORY_DTO.etag);
  });
  it.each([null, {}, { ...storyContent(), hook: { text: 'Only text' } }, { ...storyContent(), paragraphs: [null] }, { ...storyContent(), content_warnings: 'warning' }])('malformedContent_FailsClosed_%j', (content) => {
    expect(isStoryContent(content)).toBe(false); expect(() => mapStorySnapshot({ ...STORY_DTO, content })).toThrow(StoryMappingError);
  });
  it('mapsQualityHintAddresses_AndUnknownAddressToNull', () => {
    const hint = { ruleId: 'rule', severity: 'advisory', kind: 'objective', segment: 'p:0', messageKey: 'quality.hook_short', evidence: 'hint', repairDirection: null, measured: null, threshold: null };
    const result = mapStorySnapshot({ ...STORY_DTO, qualityHints: [hint, { ...hint, segment: 'unknown' }] });
    expect(result.qualityHints[0]?.segment).toBe('p:0'); expect(result.qualityHints[1]?.segment).toBeNull();
  });
  it('backendOwnedFactSources_AreNotEdited', () => {
    const content = { ...storyContent(), fact_sources: [{ ignored: 'opaque' }] };
    expect(isStoryContent(content)).toBe(true);
  });
});
