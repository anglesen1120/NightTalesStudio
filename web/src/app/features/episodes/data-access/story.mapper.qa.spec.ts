import type { StoryVersionDto } from '@shared/api';
import { storyContent } from '../testing/fake-story.repository';
import { StoryMappingError, mapStorySnapshot } from './story.mapper';

// QA for 10.5.10-T1 (independent of the implementer). The case marked with a failure row is the defect this pass
// found; it failed before the fix and is the regression guard for it.

const baseDto = (qualityHints: unknown[]): StoryVersionDto => ({
  versionId: '33333333-3333-4333-8333-333333333333', version: 1, etag: '"sv-body"', source: 'generated', wordCount: 18,
  content: storyContent(), qualityHints: qualityHints as StoryVersionDto['qualityHints'], createdAt: '2026-10-05T00:00:00Z',
  factSheetId: null, keywords: ['lantern'], parentVersionId: null, title: 'The lantern',
});

const row = (overrides: Readonly<Record<string, unknown>> = {}): Record<string, unknown> => ({
  ruleId: 'rule_short', severity: 'advisory', kind: 'objective', segment: 'p:0',
  messageKey: 'quality.hook_short', evidence: 'hint', repairDirection: null, ...overrides,
});

describe('story.mapper QA: hostile hint rows are dropped, never mirrored', () => {
  it('drops a row whose ruleId carries markup', () => {
    const mapped = mapStorySnapshot(baseDto([row({ ruleId: '<img src=x onerror=alert(1)>' })]));
    expect(mapped.qualityHints).toEqual([]);
  });

  it('drops a row whose messageKey contains whitespace or a script tag', () => {
    const mapped = mapStorySnapshot(baseDto([row({ messageKey: 'quality hook short' }), row({ ruleId: 'r2', messageKey: '<script>x</script>' })]));
    expect(mapped.qualityHints).toEqual([]);
  });

  it('drops a row whose evidence is not a string', () => {
    expect(mapStorySnapshot(baseDto([row({ evidence: 42 })])).qualityHints).toEqual([]);
    expect(mapStorySnapshot(baseDto([row({ evidence: null })])).qualityHints).toEqual([]);
  });

  it('drops a row whose repairDirection is a non-string value', () => {
    expect(mapStorySnapshot(baseDto([row({ repairDirection: 7 })])).qualityHints).toEqual([]);
  });

  it('keeps a row with a bad segment, with segment null (hint stays visible)', () => {
    const [hint] = mapStorySnapshot(baseDto([row({ segment: 'bogus:xx' })])).qualityHints;
    expect(hint?.segment).toBeNull();
    expect(hint?.ruleId).toBe('rule_short');
  });
});

describe('story.mapper QA: unknown severity/kind are neutralised', () => {
  it('maps any severity other than the exact string "blocking" to advisory', () => {
    const [hint] = mapStorySnapshot(baseDto([row({ severity: 'BLOCKING' })])).qualityHints;
    expect(hint?.severity).toBe('advisory');
  });

  it('maps any kind other than the exact string "objective" to taste', () => {
    const [hint] = mapStorySnapshot(baseDto([row({ kind: 'Objective' })])).qualityHints;
    expect(hint?.kind).toBe('taste');
  });
});

describe('story.mapper QA: evidence cap boundary', () => {
  it('keeps evidence of exactly 200 characters unchanged', () => {
    const evidence = 'e'.repeat(200);
    expect(mapStorySnapshot(baseDto([row({ evidence })])).qualityHints[0]?.evidence).toBe(evidence);
  });

  it('truncates evidence of 201 characters to 200 (never drops the hint)', () => {
    const evidence = 'e'.repeat(201);
    const hints = mapStorySnapshot(baseDto([row({ evidence })])).qualityHints;
    expect(hints).toHaveLength(1);
    expect(hints[0]?.evidence.length).toBe(200);
  });

  it('truncates evidence at a surrogate pair without leaving a lone high surrogate', () => {
    // 199 ASCII + one astral emoji (2 UTF-16 units) => slice(0, 200) splits the pair.
    const evidence = 'x'.repeat(199) + '\u{1F600}';
    const kept = mapStorySnapshot(baseDto([row({ evidence })])).qualityHints[0]?.evidence ?? '';
    const last = kept.charCodeAt(kept.length - 1);
    expect(last >= 0xd800 && last <= 0xdbff).toBe(false);
  });
});

describe('story.mapper QA: server list cap', () => {
  it('considers only the first 200 rows of an oversize list', () => {
    const rows = Array.from({ length: 250 }, (_, i) => row({ ruleId: `rule_${String(i)}` }));
    const hints = mapStorySnapshot(baseDto(rows)).qualityHints;
    expect(hints).toHaveLength(200);
    expect(hints[199]?.ruleId).toBe('rule_199');
  });

  it('applies the cap before validation: a valid row past row 200 is ignored', () => {
    const bad = Array.from({ length: 200 }, () => row({ ruleId: '<bad>' }));
    expect(mapStorySnapshot(baseDto([...bad, row({ ruleId: 'late_valid' })])).qualityHints).toEqual([]);
  });
});

describe('story.mapper QA: fail-closed story shape', () => {
  it('throws StoryMappingError when content is not a story.v1 draft', () => {
    const malformed: unknown = { ...baseDto([]), content: { title: 'no hook' } };
    expect(() => mapStorySnapshot(malformed as StoryVersionDto)).toThrow(StoryMappingError);
  });
});
