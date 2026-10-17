import type { CurrentStoryDto, QualityHintDto, StoryVersionDto } from '@shared/api';
import { storyContent } from '../testing/fake-story.repository';
import { StoryMappingError, isStoryContent, mapStorySnapshot } from './story.mapper';

export const STORY_DTO: StoryVersionDto = {
  versionId: '33333333-3333-4333-8333-333333333333', version: 1, etag: '"sv-body"', source: 'generated', wordCount: 18,
  content: storyContent(), qualityHints: [], createdAt: '2026-10-05T00:00:00Z', factSheetId: null,
  keywords: ['lantern'], parentVersionId: null, title: 'The lantern',
};
/**
 * `attestation` exists only on `CurrentStoryDto`, and a spec passes opaque rows through it: the same widening the
 * neighbouring mapper specs use, so a malformed row can be exercised without a type error.
 */
const wire = (extra: Readonly<Record<string, unknown>>): CurrentStoryDto => ({ ...STORY_DTO, ...extra }) as unknown as CurrentStoryDto;
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
  it('mapsTheServerAttestationVerbatim_AndNullWhenAbsent', () => {
    const text = '<b>Not markup</b>\n\nSecond paragraph';
    expect(mapStorySnapshot(wire({ attestation: { required: true, textVersion: '2026-10-01.1', text, acceptedAt: null } })).attestation)
      .toEqual({ required: true, textVersion: '2026-10-01.1', text, acceptedAt: null });
    // A story-version DTO carries no attestation field at all: the snapshot of a save has none.
    expect(mapStorySnapshot(STORY_DTO).attestation).toBeNull();
    expect(mapStorySnapshot(wire({ attestation: null })).attestation).toBeNull();
  });
  it('keepsTheAcceptedAtOfTheCurrentTextVersion', () => {
    const mapped = mapStorySnapshot(wire({ attestation: { required: true, textVersion: 'v2', text: 'Copy', acceptedAt: '2026-10-14T10:00:00Z' } }));
    expect(mapped.attestation?.acceptedAt).toBe('2026-10-14T10:00:00Z');
    expect(mapped.attestation?.textVersion).toBe('v2');
  });
  it.each([
    [42], [[]], [{ required: 'yes', textVersion: 'v1' }], [{ required: true }], [{ required: true, textVersion: 'v1', text: 7 }],
    [{ required: true, textVersion: 'v1', acceptedAt: 7 }],
  ])('malformedAttestation_FailsClosed_%j', (attestation) => {
    expect(() => mapStorySnapshot(wire({ attestation }))).toThrow(StoryMappingError);
  });
  it('aMissingTextAndAcceptedAtReadAsNull', () => {
    expect(mapStorySnapshot(wire({ attestation: { required: false, textVersion: 'v1' } })).attestation).toEqual({ required: false, textVersion: 'v1', text: null, acceptedAt: null });
  });
});

/**
 * The audit document is stored verbatim (`02.3.4-T2:34`) and read as opaque JSON, written here exactly as the server
 * does, so a malformed block can be exercised without a type error — the same widening the hint rows use above.
 */
const AUDIT = {
  schema_version: 'audit.v1',
  local: { issues: [] },
  model_review: {
    model: 'gpt', prompt_version: 'review.v1', stale: false,
    scores: { hook: 4, pacing: 3, atmosphere: 2, natural_language: 5, originality: 1, factual_care: 4, length_fit: 3, overall: 9 },
    issues: [{ id: 'M1', severity: 'blocking', type: 'ear_rule', kind: 'objective', segment: 'p:1', evidence: 'e', impact: 'i', repair_direction: 'r' }],
  },
  similarity: { score: 0.21, n: 12, corpus: 'sources', segments: [{ segment: 'p:1', share: 0.21 }, { segment: 'hook', share: 0.05 }] },
};
const withAudit = (auditReport: unknown, similarityScore: unknown = null): CurrentStoryDto => wire({ auditReport, similarityScore });

describe('story mapper audit report', () => {
  it('mapsTheVerbatimAuditDocument_AndHowTheClientReadsIt', () => {
    const snapshot = mapStorySnapshot(withAudit(AUDIT, '0.31'));
    expect(snapshot.auditReport?.modelReview?.stale).toBe(false);
    expect(snapshot.auditReport?.modelReview?.scores).toEqual({
      hook: 4, pacing: 3, atmosphere: 2, natural_language: 5, originality: 1, factual_care: 4, length_fit: 3,
    });
    expect(snapshot.auditReport?.modelReview?.issues).toEqual([{
      id: 'M1', type: 'ear_rule', kind: 'objective', severity: 'blocking', segment: 'p:1', impact: 'i', evidence: 'e', repairDirection: 'r',
    }]);
    expect(snapshot.auditReport?.revision).toBeNull();
    expect(snapshot.auditReport?.similarity).toEqual({ score: 0.21, corpus: 'sources', segments: [{ segment: 'p:1', share: 0.21 }, { segment: 'hook', share: 0.05 }] });
    expect(snapshot.similarityScore).toBe(0.31);
  });

  it('toleratesAMissingOrMalformedAudit_NeverThrowingAndNeverFailingTheRead', () => {
    expect(mapStorySnapshot(STORY_DTO).auditReport).toBeNull();
    expect(mapStorySnapshot(STORY_DTO).similarityScore).toBeNull();
    for (const raw of [null, 42, 'audit', [1], true]) {
      const snapshot = mapStorySnapshot(withAudit(raw, 'audit'));
      expect(snapshot.auditReport).toBeNull();
      expect(snapshot.similarityScore).toBeNull();
    }
    expect(mapStorySnapshot(withAudit({})).auditReport).toEqual({ modelReview: null, revision: null, similarity: null });
  });

  it('readsAMalformedBlockAsAbsent_AndAnAbsentListAsEmpty', () => {
    const mapped = mapStorySnapshot(withAudit({
      model_review: { stale: 'no', scores: [], issues: {} },
      revision: 'none',
      similarity: { score: 'x', corpus: 7, segments: 'p:1' },
    })).auditReport;
    expect(mapped?.modelReview).toEqual({ stale: true, scores: null, issues: [] });
    expect(mapped?.revision).toBeNull();
    expect(mapped?.similarity).toEqual({ score: null, corpus: null, segments: [] });
  });

  it('clampsATasteIssueToAdvisory_AndNeutralisesAnUnknownKindOrSeverity', () => {
    const mapped = mapStorySnapshot(withAudit({ model_review: { stale: false, issues: [
      { id: 'M1', severity: 'blocking', kind: 'taste', type: 'style', segment: 'p:1', evidence: 'e' },
      { id: 'M2', severity: 'critical', kind: 'vibes', type: 'style', segment: 'p:1', evidence: 'e' },
      { id: 'M3', severity: 'advisory', kind: 'objective', type: 'style', segment: 'p:1', evidence: 'e' },
    ] } })).auditReport?.modelReview?.issues;
    expect(mapped?.map((issue) => `${issue.severity}/${issue.kind}`)).toEqual(['advisory/taste', 'advisory/taste', 'advisory/objective']);
  });

  it('blanksAnUnsafeIssueId_AndKeepsTheRowWithoutItsId', () => {
    const mapped = mapStorySnapshot(withAudit({ model_review: { stale: false, issues: [
      { id: 'a b', severity: 'blocking', kind: 'objective', type: 'ear_rule', segment: 'p:1', evidence: 'kept' },
      { type: 'revision_drift', severity: 'advisory', kind: 'objective', segment: 'meta', evidence: 'drift' },
      null,
      'issue',
    ] } })).auditReport?.modelReview?.issues;
    expect(mapped?.map((issue) => issue.id)).toEqual(['', '']);
    expect(mapped?.map((issue) => issue.evidence)).toEqual(['kept', 'drift']);
    expect(mapped?.map((issue) => issue.type)).toEqual(['ear_rule', 'revision_drift']);
  });

  it('boundsTheReviewListsAndTheirProse', () => {
    const issues = Array.from({ length: 300 }, (_, index) => ({ id: `M${String(index)}`, type: 't'.repeat(100), severity: 'advisory', kind: 'objective', segment: 'p:1', evidence: 'e'.repeat(300), impact: 'i'.repeat(400), repair_direction: 'r'.repeat(400) }));
    const segments = Array.from({ length: 30 }, (_, index) => ({ segment: `p:${String(index)}`, share: 0.5 }));
    const audit = mapStorySnapshot(withAudit({ model_review: { stale: false, issues }, similarity: { score: 0.9, corpus: 'sources', segments } })).auditReport;
    expect(audit?.modelReview?.issues).toHaveLength(200);
    expect(audit?.modelReview?.issues[0]?.type).toHaveLength(64);
    expect(audit?.modelReview?.issues[0]?.evidence).toHaveLength(200);
    expect(audit?.modelReview?.issues[0]?.impact).toHaveLength(300);
    expect(audit?.modelReview?.issues[0]?.repairDirection).toHaveLength(300);
    expect(audit?.similarity?.segments).toHaveLength(10);
  });

  it('dropsASimilaritySegmentWithoutAKnownAddressOrWithANonPositiveShare', () => {
    const segments = [
      { segment: 'body', share: 0.4 }, { segment: 'p:1', share: 0 }, { segment: 'p:2', share: '0.4' },
      null, { segment: 'hook', share: 0.05 }, { segment: 'p:3', share: 0.02 },
    ];
    expect(mapStorySnapshot(withAudit({ similarity: { score: 0.5, corpus: 'sources', segments } })).auditReport?.similarity?.segments)
      .toEqual([{ segment: 'hook', share: 0.05 }, { segment: 'p:3', share: 0.02 }]);
  });

  it('readsTheRevisionBlock_WithItsDriftFromEitherPlace', () => {
    const refused = mapStorySnapshot(withAudit({ revision: { applied: false, reason: 'revision_invalid_output' } })).auditReport?.revision;
    expect(refused).toEqual({ applied: false, drift: false, reason: 'revision_invalid_output' });
    const driftDetail = mapStorySnapshot(withAudit({ revision: { applied: true, drift: { segment_count_changed: false, changed_segments: ['p:1'] } } })).auditReport?.revision;
    expect(driftDetail).toEqual({ applied: true, drift: true, reason: null });
    const cleanDetail = mapStorySnapshot(withAudit({ revision: { applied: true, drift: { segment_count_changed: false, changed_segments: [], unflagged_changed: [], added_findings: [] } } })).auditReport?.revision;
    expect(cleanDetail?.drift).toBe(false);
    const driftIssue = mapStorySnapshot(withAudit({ revision: { applied: true, review_after: { scores: {}, issues: [{ id: 'd1', type: 'revision_drift', severity: 'advisory', kind: 'objective', segment: 'meta', evidence: 'drift' }] } } })).auditReport?.revision;
    expect(driftIssue?.drift).toBe(true);
  });

  it('takesReviewAfterOfAnAppliedRevision_WhileStaleComesFromModelReview', () => {
    const revision = {
      applied: true,
      drift: { segment_count_changed: false, changed_segments: [], added_findings: [] },
      review_after: {
        scores: { hook: 2, pacing: 2, atmosphere: 2, natural_language: 2, originality: 2, factual_care: 2, length_fit: 2 },
        issues: [{ id: 'R2', type: 'ear_rule', severity: 'advisory', kind: 'objective', segment: 'p:2', evidence: 'second' }],
      },
    };
    const modelReview = {
      stale: true,
      scores: { hook: 1, pacing: 1, atmosphere: 1, natural_language: 1, originality: 1, factual_care: 1, length_fit: 1 },
      issues: [{ id: 'R1', type: 'ear_rule', severity: 'blocking', kind: 'objective', segment: 'p:1', evidence: 'first' }],
    };
    const applied = mapStorySnapshot(withAudit({ model_review: modelReview, revision })).auditReport?.modelReview;
    expect(applied?.stale).toBe(true);
    expect(applied?.scores?.hook).toBe(2);
    expect(applied?.issues.map((issue) => issue.id)).toEqual(['R2']);
    const notApplied = mapStorySnapshot(withAudit({ model_review: modelReview, revision: { ...revision, applied: false } })).auditReport?.modelReview;
    expect(notApplied?.scores?.hook).toBe(1);
    expect(notApplied?.issues.map((issue) => issue.id)).toEqual(['R1']);
    const incompleteAfter = mapStorySnapshot(withAudit({ model_review: modelReview, revision: { ...revision, review_after: { scores: { hook: 5 } } } })).auditReport?.modelReview;
    expect(incompleteAfter?.scores?.hook).toBe(1);
    expect(incompleteAfter?.issues.map((issue) => issue.id)).toEqual(['R1']);
  });

  it('readsMissingReviewAfterAsModelReview_AndKeepsANumericScoreOrNullsAGarbageOne', () => {
    const modelReview = { stale: false, scores: { hook: 3 }, issues: [] };
    expect(mapStorySnapshot(withAudit({ model_review: modelReview, revision: { applied: true } })).auditReport?.modelReview?.scores?.hook).toBe(3);
    for (const [value, expected] of [[0.5, 0.5], ['0.5', 0.5], [null, null], ['', null], ['abc', null], [Number.NaN, null]] as const) {
      expect(mapStorySnapshot(withAudit(AUDIT, value)).similarityScore).toBe(expected);
    }
  });
});
