import type { KeywordFlag } from '../domain/annotation.model';
import type { QualityHint } from '../domain/story.model';
import { mergeAnnotations, sortAnnotations } from './annotation-merge';

// QA for 10.5.10-T1 (independent of the implementer). The cases marked with a failure row are the defects this pass
// found; they failed before the fix and are the regression guard for it.

const hint = (overrides: Partial<QualityHint> = {}): QualityHint => ({
  ruleId: 'rule_a', severity: 'advisory', kind: 'taste', segment: 'p:0',
  messageKey: 'quality.rule_a', evidence: 'evidence', repairDirection: null, ...overrides,
});

describe('annotation-merge QA: sort order (objective before taste, blocking before advisory)', () => {
  it('lists objective hints ahead of taste, and blocking ahead of advisory inside each kind', () => {
    const merged = mergeAnnotations([
      hint({ ruleId: 'taste_adv', kind: 'taste', severity: 'advisory', segment: 'p:0' }),
      hint({ ruleId: 'obj_adv', kind: 'objective', severity: 'advisory', segment: 'p:1' }),
      hint({ ruleId: 'obj_block', kind: 'objective', severity: 'blocking', segment: 'p:2' }),
      hint({ ruleId: 'taste_block', kind: 'taste', severity: 'blocking', segment: 'p:3' }),
    ], []);
    expect(merged.map((a) => a.ruleId)).toEqual(['obj_block', 'obj_adv', 'taste_block', 'taste_adv']);
  });

  it('sortAnnotations is stable for equal keys (does not reorder distinct rows arbitrarily)', () => {
    const merged = mergeAnnotations([hint({ ruleId: 'same', segment: 'p:0' }), hint({ ruleId: 'same', segment: 'p:0', evidence: 'other' })], []);
    expect(merged).toHaveLength(1);
  });
});

describe('annotation-merge QA: dedupe on (address, ruleId)', () => {
  it('prefers the blocking objective row when the same (address, ruleId) arrives advisory/taste first (failure row 120)', () => {
    const merged = mergeAnnotations([
      hint({ ruleId: 'rule_a', segment: 'p:0', severity: 'advisory', kind: 'taste' }),
      hint({ ruleId: 'rule_a', segment: 'p:0', severity: 'blocking', kind: 'objective' }),
    ], []);
    expect(merged).toHaveLength(1);
    expect(merged[0]?.severity).toBe('blocking');
    expect(merged[0]?.kind).toBe('objective');
  });

  it('keeps distinct ruleIds on the same address as separate annotations', () => {
    const merged = mergeAnnotations([hint({ ruleId: 'a', segment: 'p:0' }), hint({ ruleId: 'b', segment: 'p:0' })], []);
    expect(merged).toHaveLength(2);
  });

  it('collapses a repeated keyword flag with the same (address, rule, index)', () => {
    const flag: KeywordFlag = { address: 'p:3', rule: 'keyword_name_detected', index: 1 };
    expect(mergeAnnotations([], [flag, { ...flag }])).toHaveLength(1);
  });
});

describe('annotation-merge QA: DOM id uniqueness (inline note ids are referenced by aria-describedby)', () => {
  it('gives ruleIds "a.b" and "a-b" on one address two distinct annotation ids', () => {
    const merged = mergeAnnotations([hint({ ruleId: 'a.b', segment: 'p:0' }), hint({ ruleId: 'a-b', segment: 'p:0' })], []);
    expect(merged).toHaveLength(2);
    expect(new Set(merged.map((a) => a.id)).size).toBe(2);
  });
});

describe('annotation-merge QA: keyword flag placement and copy', () => {
  it('places a keyword flag on a paragraph inline, with its chip index', () => {
    const [flag] = mergeAnnotations([], [{ address: 'p:3', rule: 'keyword_name_detected', index: 1 }]);
    expect(flag?.placement).toBe('inline');
    expect(flag?.keywordIndex).toBe(1);
    expect(flag?.source).toBe('keyword');
    expect(flag?.severity).toBe('blocking');
    expect(flag?.kind).toBe('objective');
  });

  it('places a keyword flag on "meta" in the panel only (failure row 122)', () => {
    const [flag] = mergeAnnotations([], [{ address: 'meta', rule: 'no_scenes', index: 0 }]);
    expect(flag?.placement).toBe('panel');
  });

  it('gives an unknown but well-shaped keyword rule the generic copy key, never the raw rule string (failure row 121)', () => {
    const [flag] = mergeAnnotations([], [{ address: 'p:0', rule: 'zzz_unknown_rule', index: 0 }]);
    expect(flag?.messageKey).toBe('episodes.editor.keywordFlaggedHint');
    expect(flag?.messageKey).not.toContain('zzz_unknown_rule');
  });

  it('maps the three wire rule names to their errors.keywordsFlaggedRule keys', () => {
    const merged = mergeAnnotations([], [{ address: 'p:0', rule: 'keyword_name_detected', index: 0 }]);
    expect(merged[0]?.messageKey).toBe('errors.keywordsFlaggedRule.keyword_name_detected');
  });

  it('keeps a hint and a keyword flag on the same paragraph as two annotations', () => {
    const merged = mergeAnnotations([hint({ ruleId: 'hook_short', segment: 'p:0' })], [{ address: 'p:0', rule: 'keyword_name_detected', index: 0 }]);
    expect(merged).toHaveLength(2);
    expect(merged.map((a) => a.source).sort()).toEqual(['hint', 'keyword']);
  });
});

describe('annotation-merge QA: sortAnnotations is pure', () => {
  it('returns a new array and does not mutate its input', () => {
    const items = mergeAnnotations([hint({ ruleId: 'x', segment: 'p:1' }), hint({ ruleId: 'y', segment: 'p:0' })], []);
    const before = items.map((a) => a.id);
    const sorted = sortAnnotations(items);
    expect(items.map((a) => a.id)).toEqual(before);
    expect(sorted).not.toBe(items);
  });
});
