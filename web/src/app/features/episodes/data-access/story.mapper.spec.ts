import type { QualityHintDto, StoryVersionDto } from '@shared/api';
import { storyContent } from '../testing/fake-story.repository';
import { StoryMappingError, isStoryContent, mapStorySnapshot } from './story.mapper';

export const STORY_DTO: StoryVersionDto = {
  versionId: '33333333-3333-4333-8333-333333333333', version: 1, etag: '"sv-body"', source: 'generated', wordCount: 18,
  content: storyContent(), qualityHints: [], createdAt: '2026-10-05T00:00:00Z', factSheetId: null,
  keywords: ['lantern'], parentVersionId: null, title: 'The lantern',
};
const hintRow = (overrides: Readonly<Record<string, unknown>> = {}): Readonly<Record<string, unknown>> => ({
  ruleId: 'rule_short', severity: 'advisory', kind: 'objective', segment: 'p:0',
  messageKey: 'quality.hook_short', evidence: 'hint', repairDirection: null, ...overrides,
});
/** Hints go on the wire as opaque rows, so a spec may pass a malformed one without a type error. */
const withHints = (...rows: unknown[]): StoryVersionDto => ({ ...STORY_DTO, qualityHints: rows as QualityHintDto[] });
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
  it('neutralisesUnknownSeverityAndKind_KeepingTheHint', () => {
    const result = mapStorySnapshot(withHints(hintRow({ severity: 'critical', kind: 'vibes' })));
    expect(result.qualityHints).toHaveLength(1);
    expect(result.qualityHints[0]?.severity).toBe('advisory');
    expect(result.qualityHints[0]?.kind).toBe('taste');
  });
  it('dropsMalformedHints_WithoutThrowing', () => {
    const result = mapStorySnapshot(withHints(
      hintRow({ ruleId: 42 }), hintRow({ ruleId: 'bad rule' }), hintRow({ messageKey: 'not a key' }),
      hintRow({ evidence: null }), hintRow({ repairDirection: 7 }), null, hintRow({ ruleId: 'kept' }),
    ));
    expect(result.qualityHints).toHaveLength(1);
    expect(result.qualityHints[0]?.ruleId).toBe('kept');
  });
  it('treatsAMissingOrUndefinedRepairDirectionAsNull', () => {
    const result = mapStorySnapshot(withHints(hintRow({ repairDirection: undefined })));
    expect(result.qualityHints).toHaveLength(1);
    expect(result.qualityHints[0]?.repairDirection).toBeNull();
  });
  it('truncatesEvidenceTo200Chars', () => {
    expect(mapStorySnapshot(withHints(hintRow({ evidence: 'x'.repeat(300) }))).qualityHints[0]?.evidence).toHaveLength(200);
  });
  it('keepsAtMost200Hints', () => {
    const rows = Array.from({ length: 250 }, (_, index) => hintRow({ ruleId: `rule_${String(index)}` }));
    const result = mapStorySnapshot(withHints(...rows));
    expect(result.qualityHints).toHaveLength(200);
    expect(result.qualityHints[199]?.ruleId).toBe('rule_199');
  });
});
