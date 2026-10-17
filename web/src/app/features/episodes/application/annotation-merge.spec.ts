import type { KeywordFlag } from '../domain/annotation.model';
import type { QualityHint, StoryDraft, StoryParagraph, StorySegment } from '../domain/story.model';
import { diffSegmentAddresses, mergeAnnotations, sortAnnotations } from './annotation-merge';

const hint = (overrides: Partial<QualityHint> = {}): QualityHint => ({
  ruleId: 'rule_a', severity: 'advisory', kind: 'objective', segment: 'p:0',
  messageKey: 'quality.rule_a', evidence: 'evidence', repairDirection: null, ...overrides,
});
const flag = (overrides: Partial<KeywordFlag> = {}): KeywordFlag => ({ address: 'p:1', index: 0, rule: 'keyword_invalid', ...overrides });

const segment = (text: string, keywords: readonly string[]): StorySegment => ({ segKey: text, text, keywords, factRefs: [] });
const paragraph = (text: string, keywords: readonly string[]): StoryParagraph => ({ ...segment(text, keywords), beat: `beat-${text}` });
function draft(overrides: Partial<StoryDraft> = {}): StoryDraft {
  return {
    title: 'Title', hook: segment('Hook', ['lantern']),
    paragraphs: [paragraph('First', ['road']), paragraph('Second', ['door'])],
    ending: segment('Ending', ['dawn']), description: 'Description', tags: ['night'], contentWarnings: ['fear'], structure: null,
    ...overrides,
  };
}

describe('annotation merge', () => {
  it('sortsKindThenSeverityThenAddressWithUnresolvedLast', () => {
    const items = mergeAnnotations([
      hint({ ruleId: 'taste_b', kind: 'taste', severity: 'blocking', segment: 'title' }),
      hint({ ruleId: 'obj_advisory', segment: 'p:1' }),
      hint({ ruleId: 'obj_blocking_z', severity: 'blocking', segment: 'ending' }),
      hint({ ruleId: 'obj_blocking_a', severity: 'blocking', segment: 'ending' }),
      hint({ ruleId: 'obj_none', severity: 'blocking', segment: null }),
    ], []);
    expect(items.map((item) => item.ruleId)).toEqual(['obj_blocking_a', 'obj_blocking_z', 'obj_none', 'obj_advisory', 'taste_b']);
    expect(mergeAnnotations([
      hint({ ruleId: 'taste_advisory', kind: 'taste' }),
      hint({ ruleId: 'taste_blocking', kind: 'taste', severity: 'blocking' }),
    ], []).map((item) => item.ruleId)).toEqual(['taste_blocking', 'taste_advisory']);
  });

  it('ordersAddressesCanonically_MetaBeforeUnresolved', () => {
    const items = mergeAnnotations([
      hint({ segment: null }), hint({ segment: 'meta' }), hint({ segment: 'ending' }),
      hint({ segment: 'p:0' }), hint({ segment: 'hook' }), hint({ segment: 'title' }),
    ], []);
    expect(items.map((item) => item.address)).toEqual(['title', 'hook', 'p:0', 'ending', 'meta', null]);
  });

  it('dedupesPerSourceAddressRuleAndKeywordIndex_FirstInputWins', () => {
    const items = mergeAnnotations(
      [hint({ ruleId: 'dup', messageKey: 'quality.first' }), hint({ ruleId: 'dup', messageKey: 'quality.second' })],
      [flag({ rule: 'keyword_invalid', index: 1 }), flag({ rule: 'keyword_invalid', index: 1 }), flag({ rule: 'keyword_invalid', index: 2 })],
    );
    expect(items).toHaveLength(3);
    expect(items.find((item) => item.ruleId === 'dup')?.messageKey).toBe('quality.first');
    expect(items.filter((item) => item.keywordIndex === 1)).toHaveLength(1);
    expect(items.filter((item) => item.keywordIndex === 2)).toHaveLength(1);
  });

  it('placesInlineSegmentsInline_AndMetaOrUnresolvedInThePanel', () => {
    const items = mergeAnnotations([
      hint({ ruleId: 'a', segment: 'title' }), hint({ ruleId: 'b', segment: 'hook' }),
      hint({ ruleId: 'c', segment: 'p:3' }), hint({ ruleId: 'd', segment: 'ending' }),
      hint({ ruleId: 'e', segment: 'meta' }), hint({ ruleId: 'f', segment: null }),
    ], []);
    expect(items.map((item) => item.placement)).toEqual(['inline', 'inline', 'inline', 'inline', 'panel', 'panel']);
    expect(items.map((item) => item.id)).toEqual(['ann-title-a', 'ann-hook-b', 'ann-p-3-c', 'ann-ending-d', 'ann-meta-e', 'ann-unresolved-f']);
  });

  it('keepsAnUnresolvableAddressInThePanel_LastInTheOrder', () => {
    const items = mergeAnnotations([hint({ ruleId: 'a', segment: null }), hint({ ruleId: 'b', segment: 'title' })], []);
    expect(items.map((item) => item.ruleId)).toEqual(['b', 'a']);
    expect(items[1]).toMatchObject({ address: null, placement: 'panel', id: 'ann-unresolved-a', source: 'hint' });
  });

  it('mapsAKeywordFlagToABlockingObjectiveAnnotationWithAKeywordIndex', () => {
    const items = mergeAnnotations([], [flag({ address: 'p:2', index: 3, rule: 'keyword_name_detected' })]);
    expect(items[0]).toEqual({
      id: 'ann-p-2-keyword_name_detected-k3', source: 'keyword', address: 'p:2', ruleId: 'keyword_name_detected',
      severity: 'blocking', kind: 'objective', messageKey: 'errors.keywordsFlaggedRule.keyword_name_detected',
      evidence: '', repairDirection: null, keywordIndex: 3, placement: 'inline',
    });
  });

  it.each([
    ['keyword_name_detected', 'errors.keywordsFlaggedRule.keyword_name_detected'],
    ['keyword_invalid', 'errors.keywordsFlaggedRule.keyword_invalid'],
    ['no_scenes', 'errors.keywordsFlaggedRule.no_scenes'],
    ['brand_new_rule', 'episodes.editor.keywordFlaggedHint'],
  ])('resolvesTheKeywordCopyOfRule_%s', (rule, expected) => {
    expect(mergeAnnotations([], [flag({ rule })])[0]?.messageKey).toBe(expected);
  });

  it('replacesAMessageKeyThatFailsTheShapeGuard', () => {
    expect(mergeAnnotations([hint({ messageKey: 'quality.bad key' })], [])[0]?.messageKey).toBe('episodes.annotations.unknown');
    expect(mergeAnnotations([hint({ messageKey: '{{injected}}' })], [])[0]?.messageKey).toBe('episodes.annotations.unknown');
  });

  it('sanitisesAHostileRuleIdOutOfTheDomId', () => {
    expect(mergeAnnotations([hint({ ruleId: 'a:b.c', segment: 'p:1' })], [])[0]?.id).toBe('ann-p-1-a-b-c');
  });

  it('reserves double underscores for panel ids and resolves sanitized-rule collisions', () => {
    const items = mergeAnnotations(['x', 'x-panel', 'x__panel', 'x_panel', 'a.b', 'a-b'].map((ruleId) => hint({ ruleId })), []);
    expect(items).toHaveLength(6);
    expect(items.find((item) => item.ruleId === 'x-panel')?.id).toBe('ann-p-0-x-panel');
    expect(items.find((item) => item.ruleId === 'x__panel')?.id).toBe('ann-p-0-x_panel');
    expect(items.every((item) => !item.id.includes('__'))).toBe(true);
    expect(new Set(items.map((item) => item.id)).size).toBe(items.length);
    expect(new Set(items.flatMap((item) => [item.id, item.id + '__panel'])).size).toBe(items.length * 2);
  });

  it('prefers stronger duplicates independent of input order, keeping distinct rules', () => {
    const weak = hint({ kind: 'taste' });
    const strong = hint({ kind: 'objective', severity: 'blocking' });
    for (const pair of [[weak, strong], [strong, weak]]) {
      const items = mergeAnnotations([...pair, hint({ ruleId: 'other' })], []);
      expect(items).toHaveLength(2);
      expect(items.find((item) => item.ruleId === 'rule_a')).toMatchObject({ kind: 'objective', severity: 'blocking' });
    }
    expect(mergeAnnotations([weak, hint({ kind: 'objective' })], [])[0]?.kind).toBe('objective');
  });

  it('keeps meta keyword flags panel-only and does not dedupe a hint against a keyword flag', () => {
    expect(mergeAnnotations([], [flag({ address: 'meta', rule: 'no_scenes' })])[0]?.placement).toBe('panel');
    const items = mergeAnnotations([hint({ ruleId: 'keyword_invalid' })], [flag({ address: 'p:0' })]);
    expect(items).toHaveLength(2);
    expect(items.map((item) => item.source).sort()).toEqual(['hint', 'keyword']);
  });

  it('sorts without mutating the input list', () => {
    const items = [...mergeAnnotations([hint({ ruleId: 'x', segment: 'p:1' }), hint({ ruleId: 'y', segment: 'p:0' })], [])].reverse();
    const before = [...items];
    expect(sortAnnotations(items).map((item) => item.address)).toEqual(['p:0', 'p:1']);
    expect(items).toEqual(before);
  });

  it('keepsNoAnnotationForAnEmptyInput', () => {
    expect(mergeAnnotations([], [])).toEqual([]);
  });
});

describe('draft segment diff', () => {
  it('reportsNoAddressForIdenticalDrafts', () => {
    expect(diffSegmentAddresses(draft(), draft())).toEqual([]);
  });

  it('detectsAChangedSegmentText_ChangedKeywordList_AndMetaFields', () => {
    const base = draft();
    expect(diffSegmentAddresses({ ...base, title: 'Other' }, base)).toEqual(['title']);
    expect(diffSegmentAddresses({ ...base, hook: { ...base.hook, keywords: ['new'] } }, base)).toEqual(['hook']);
    expect(diffSegmentAddresses(base, { ...base, ending: { ...base.ending, text: 'Other' } })).toEqual(['ending']);
    expect(diffSegmentAddresses(base, { ...base, description: 'Other' })).toEqual(['meta']);
    expect(diffSegmentAddresses(base, { ...base, tags: ['night', 'storm'] })).toEqual(['meta']);
    expect(diffSegmentAddresses(base, { ...base, contentWarnings: [] })).toEqual(['meta']);
  });

  it('detectsAChangedParagraphByIndex_AndAnAddedOrRemovedParagraph', () => {
    const base = draft();
    const first = base.paragraphs[0]!;
    const second = base.paragraphs[1]!;
    expect(diffSegmentAddresses({ ...base, paragraphs: [{ ...first, text: 'Other' }, second] }, base)).toEqual(['p:0']);
    expect(diffSegmentAddresses({ ...base, paragraphs: [first, { ...second, keywords: ['new'] }] }, base)).toEqual(['p:1']);
    expect(diffSegmentAddresses({ ...base, paragraphs: [...base.paragraphs, paragraph('Third', [])] }, base)).toEqual(['p:2']);
    expect(diffSegmentAddresses(base, { ...base, paragraphs: [first] })).toEqual(['p:1']);
  });

  it('listsSeveralChangesInCanonicalAddressOrder', () => {
    const base = draft();
    expect(diffSegmentAddresses({ ...base, title: 'Other', paragraphs: [base.paragraphs[0]!, base.paragraphs[1]!, paragraph('Third', [])], description: 'Other' }, base))
      .toEqual(['title', 'p:2', 'meta']);
  });
});

describe('annotation merge against the self-review payload', () => {
  it('rendersAFlattenedReviewIssueOnce_SoTheInlineListLengthEqualsTheHintCount', () => {
    // A `model_review` issue already arrives inside `qualityHints` (02.3.4-T2:42): merging the audit document again
    // would render every review issue twice, so the merge input is the hint set and nothing else.
    const hints = [
      hint({ ruleId: 'Q-REV-01', segment: 'p:1', severity: 'blocking', kind: 'objective' }),
      hint({ ruleId: 'Q-REV-02', segment: 'p:2', severity: 'advisory', kind: 'taste' }),
    ];
    const items = mergeAnnotations(hints, []);
    expect(items).toHaveLength(hints.length);
    expect(items.map((item) => item.ruleId)).toEqual(['Q-REV-01', 'Q-REV-02']);
  });

  it('neverTurnsASimilarityShareIntoAStandaloneAnnotation_TheQSIMHintIsTheAnnotation', () => {
    // `similarity.segments[]` carries `p:3`, and only the `Q-SIM-01` hint (at `meta`) may produce a row.
    const items = mergeAnnotations([hint({ ruleId: 'Q-SIM-01', segment: 'meta', messageKey: 'quality.similarity_warn' })], []);
    expect(items.map((item) => [item.address, item.placement])).toEqual([['meta', 'panel']]);
    expect(items.some((item) => item.address === 'p:3')).toBe(false);
    expect(mergeAnnotations([], [])).toEqual([]);
  });

  it('collapsesDuplicateAddressAndRulePairs_AndAddsNoRowBeyondAHintOrAKeywordFlag', () => {
    const items = mergeAnnotations(
      [hint({ ruleId: 'dup', segment: 'p:1' }), hint({ ruleId: 'dup', segment: 'p:1' }), hint({ ruleId: 'Q-SIM-02', segment: 'meta' })],
      [flag({ address: 'p:2', index: 0, rule: 'no_scenes' }), flag({ address: 'p:2', index: 0, rule: 'no_scenes' })],
    );
    expect(items.filter((item) => item.address === 'p:1')).toHaveLength(1);
    expect(items.filter((item) => item.source === 'keyword')).toHaveLength(1);
    expect(items).toHaveLength(3);
    expect(items.every((item) => item.source === 'hint' || item.source === 'keyword')).toBe(true);
  });
});
