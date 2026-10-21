import type { AuditReport, ReviewIssue, SimilaritySection } from './audit.model';
import { SIMILARITY_BLOCK_RULE, SIMILARITY_WARN_RULE, countBySeverity, highlightMap, whyIssues } from './audit.model';
import type { QualityHint } from './story.model';

const hint = (overrides: Partial<QualityHint> = {}): QualityHint => ({
  ruleId: 'rule_a', severity: 'advisory', kind: 'objective', segment: 'p:0',
  messageKey: 'quality.rule_a', evidence: 'evidence', repairDirection: null, ...overrides,
});
const issue = (overrides: Partial<ReviewIssue> = {}): ReviewIssue => ({
  id: 'M1', type: 'ear_rule', kind: 'objective', severity: 'blocking', segment: 'hook',
  impact: null, evidence: 'test evidence', repairDirection: null, ...overrides,
});
const similarity = (overrides: Partial<SimilaritySection> = {}): SimilaritySection =>
  ({ score: 0.21, corpus: 'sources', segments: [{ segment: 'p:1', share: 0.21 }], ...overrides });
const review = (issues: readonly ReviewIssue[]): AuditReport => ({ modelReview: { stale: false, scores: null, issues }, revision: null, similarity: null });

describe('audit model counts', () => {
  it('countsTheFlattenedHintSetBySeverity_SoTheHeaderNeverInventsANumber', () => {
    const hints = [
      hint({ ruleId: 'a', severity: 'blocking' }),
      hint({ ruleId: 'b', severity: 'blocking', kind: 'objective' }),
      hint({ ruleId: 'c', severity: 'advisory' }),
      hint({ ruleId: 'd', severity: 'advisory', kind: 'taste' }),
    ];
    const counts = countBySeverity(hints);
    expect(counts).toEqual({ blocking: 2, advisory: 2 });
    expect(counts.blocking + counts.advisory).toBe(hints.length);
  });

  it('clampsATasteIssueToAdvisory_EvenWhenTheHintClaimsBlocking', () => {
    expect(countBySeverity([hint({ kind: 'taste', severity: 'blocking' })])).toEqual({ blocking: 0, advisory: 1 });
    expect(countBySeverity([])).toEqual({ blocking: 0, advisory: 0 });
  });
});

describe('audit model why list', () => {
  it('keepsTheRecordedModelReviewIssuesForTheExpandedPanelOnly', () => {
    const audit = review([issue({ id: 'M1' }), issue({ id: 'M2', severity: 'advisory', segment: 'p:2' })]);
    expect(whyIssues(audit).map((row) => row.id)).toEqual(['M1', 'M2']);
    expect(whyIssues(audit)[0]?.evidence).toBe('test evidence');
  });

  it('clampsATasteIssueToAdvisory_AndNeverTakesItsRowsFromTheHints', () => {
    expect(whyIssues(review([issue({ kind: 'taste', severity: 'blocking' })])).map((row) => row.severity)).toEqual(['advisory']);
    expect(whyIssues(review([issue({ kind: 'objective', severity: 'blocking' })])).map((row) => row.severity)).toEqual(['blocking']);
  });

  it('readsMissingOrEmptyPartsAsAnEmptyList_SoThePanelShowsItsEmptyState', () => {
    expect(whyIssues(null)).toEqual([]);
    expect(whyIssues({ modelReview: null, revision: null, similarity: null })).toEqual([]);
    expect(whyIssues(review([]))).toEqual([]);
  });
});

describe('audit model similarity highlights', () => {
  it('marksEveryReportedSegmentWithTheVerdictOfTheHintSet_NotFromTheScore', () => {
    const section = similarity({ score: 0.99, segments: [{ segment: 'p:3', share: 0.21 }, { segment: 'hook', share: 0.05 }] });
    expect([...highlightMap(section, [hint({ ruleId: SIMILARITY_WARN_RULE, segment: 'meta' })])]).toEqual([['p:3', 'warn'], ['hook', 'warn']]);
    expect([...highlightMap(section, [hint({ ruleId: SIMILARITY_BLOCK_RULE, segment: 'meta' })])]).toEqual([['p:3', 'block'], ['hook', 'block']]);
  });

  it('letsTheBlockingHintWinAndMarksNothingWithoutAVerdictHint', () => {
    const hints = [hint({ ruleId: SIMILARITY_WARN_RULE, segment: 'meta' }), hint({ ruleId: SIMILARITY_BLOCK_RULE, segment: 'meta' })];
    expect([...highlightMap(similarity(), hints).values()]).toEqual(['block']);
    expect(highlightMap(similarity(), [hint({ ruleId: 'Q-OTHER-01' })]).size).toBe(0);
    expect(highlightMap(similarity(), []).size).toBe(0);
  });

  it('yieldsAnEmptyMapForAMissingSection_AnEmptySegmentListOrANonPositiveShare', () => {
    const warn = [hint({ ruleId: SIMILARITY_WARN_RULE, segment: 'meta' })];
    expect(highlightMap(null, warn).size).toBe(0);
    expect(highlightMap(similarity({ segments: [] }), warn).size).toBe(0);
    expect(highlightMap(similarity({ segments: [{ segment: 'p:1', share: 0 }] }), warn).size).toBe(0);
  });
});
