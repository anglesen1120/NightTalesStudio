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
  it.each([['critical', 'vibes'], ['BLOCKING', 'Objective']])('neutralisesUnknownSeverityAndKind_%s_%s', (severity, kind) => {
    const result = mapStorySnapshot(withHints(hintRow({ severity, kind })));
    expect(result.qualityHints).toHaveLength(1);
    expect(result.qualityHints[0]?.severity).toBe('advisory');
    expect(result.qualityHints[0]?.kind).toBe('taste');
  });
  it('dropsMalformedHints_WithoutThrowing', () => {
    const result = mapStorySnapshot(withHints(
      hintRow({ ruleId: 42 }), hintRow({ ruleId: 'bad rule' }), hintRow({ ruleId: '<img src=x onerror=alert(1)>' }),
      hintRow({ messageKey: 'not a key' }), hintRow({ messageKey: '<script>x</script>' }),
      hintRow({ evidence: null }), hintRow({ evidence: 42 }), hintRow({ repairDirection: 7 }), null, hintRow({ ruleId: 'kept' }),
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
  it('keeps evidence at the cap and never splits an astral character at the boundary', () => {
    for (const evidence of ['x'.repeat(200), 'x'.repeat(201)]) {
      expect(mapStorySnapshot(withHints(hintRow({ evidence }))).qualityHints[0]?.evidence).toBe('x'.repeat(200));
    }
    expect(mapStorySnapshot(withHints(hintRow({ evidence: 'x'.repeat(199) + '\u{1F600}' }))).qualityHints[0]?.evidence).toBe('x'.repeat(199));
    const completePair = 'x'.repeat(198) + '\u{1F600}';
    expect(mapStorySnapshot(withHints(hintRow({ evidence: completePair + 'z' }))).qualityHints[0]?.evidence).toBe(completePair);
  });
  it('drops oversized rule ids and message keys but accepts the exact limits', () => {
    const result = mapStorySnapshot(withHints(
      hintRow({ ruleId: 'r'.repeat(65) }), hintRow({ messageKey: 'k'.repeat(129) }),
      hintRow({ ruleId: 'r'.repeat(64), messageKey: 'k'.repeat(128) }),
    ));
    expect(result.qualityHints).toHaveLength(1);
    expect(result.qualityHints[0]?.ruleId).toHaveLength(64);
    expect(result.qualityHints[0]?.messageKey).toHaveLength(128);
  });
  it('bounds repair directions at 300 without splitting surrogate pairs', () => {
    for (const repairDirection of ['r'.repeat(300), 'r'.repeat(301)]) {
      expect(mapStorySnapshot(withHints(hintRow({ repairDirection }))).qualityHints[0]?.repairDirection).toBe('r'.repeat(300));
    }
    expect(mapStorySnapshot(withHints(hintRow({ repairDirection: 'r'.repeat(299) + '\u{1F600}' }))).qualityHints[0]?.repairDirection).toBe('r'.repeat(299));
    const completePair = 'r'.repeat(298) + '\u{1F600}';
    expect(mapStorySnapshot(withHints(hintRow({ repairDirection: completePair + 'z' }))).qualityHints[0]?.repairDirection).toBe(completePair);
  });
  it('keepsAtMost200Hints', () => {
    const rows = Array.from({ length: 250 }, (_, index) => hintRow({ ruleId: `rule_${String(index)}` }));
    const result = mapStorySnapshot(withHints(...rows));
    expect(result.qualityHints).toHaveLength(200);
    expect(result.qualityHints[199]?.ruleId).toBe('rule_199');
  });
  it('caps the raw list before validation, ignoring a valid row after 200 malformed rows', () => {
    const badRows = Array.from({ length: 200 }, () => hintRow({ ruleId: '<bad>' }));
    expect(mapStorySnapshot(withHints(...badRows, hintRow({ ruleId: 'late_valid' }))).qualityHints).toEqual([]);
  });
});
