import type { SegmentAddress } from '@shared/util/segment-address';
import type { QualityHint } from './story.model';

/**
 * The seven 1–5 rubric dimensions of `model_review.scores`, in the order `AuditReportBuilder.Scores` writes them
 * (02.6.1-T1). An unknown key of the wire object is ignored, and a dimension the payload omitted reads as `null`.
 */
export const SCORE_DIMENSIONS = ['hook', 'pacing', 'atmosphere', 'natural_language', 'originality', 'factual_care', 'length_fit'] as const;
export type ScoreDimension = (typeof SCORE_DIMENSIONS)[number];

/** The platform-emitted type of the revision's own drift advisory (`AuditReportBuilder.RevisionDriftType`). */
export const REVISION_DRIFT_TYPE = 'revision_drift';

/**
 * The two similarity hints of `02.5.6-T1`. They arrive inside `qualityHints` (address `meta`) and are the ONLY source of
 * a highlight verdict: the thresholds behind them live server-side (`Similarity:WarnAbove` / `BlockAbove`), so the
 * client never re-derives a verdict from `similarity.score` — it does not know them and must not guess.
 */
export const SIMILARITY_WARN_RULE = 'Q-SIM-01';
export const SIMILARITY_BLOCK_RULE = 'Q-SIM-02';

/**
 * One recorded `model_review.issues[]` row. It is the expanded "why" panel's content only: every review issue already
 * arrives flattened inside `qualityHints` (`02.3.4-T2:42`), so this list is never an inline annotation source.
 */
export interface ReviewIssue {
  /** The recorded id; `''` when the payload's id was missing or not safe for a DOM id / i18n key. */
  readonly id: string;
  /** The recorded type (`ear_rule`, `revision_drift`, …); `''` when the payload carried none. */
  readonly type: string;
  readonly kind: 'objective' | 'taste';
  /** Already clamped: a taste issue is advisory even when the payload claimed `blocking` (`02.6.1-T1:99`). */
  readonly severity: 'blocking' | 'advisory';
  /** The recorded address, or `null` when it is not one this client knows (the row is kept, never dropped). */
  readonly segment: SegmentAddress | null;
  readonly impact: string | null;
  readonly evidence: string;
  readonly repairDirection: string | null;
}

/**
 * The current state of the model's self-review, exactly as the server read model derives it (`StoryReadProjection.Review`):
 * an applied revision's review #2 (`revision.review_after`) wins over `model_review`; the `stale` flag always comes from
 * the `model_review` block, which is the only one that carries it.
 */
export interface ModelReview {
  readonly stale: boolean;
  /** The seven rubric scores, or `null` when the payload carried no `scores` object at all. */
  readonly scores: Readonly<Record<ScoreDimension, number | null>> | null;
  readonly issues: readonly ReviewIssue[];
}

/** The revision block of the audit document; `applied` false covers "never revised" and "the revision was refused". */
export interface RevisionInfo {
  readonly applied: boolean;
  /** The server-derived drift bit: a `revision_drift` issue, or any set `revision.drift` detail. */
  readonly drift: boolean;
  /** The stable refusal code (`revision_invalid_output`, …) of a revision that was not applied; `null` otherwise. */
  readonly reason: string | null;
}

/** One `similarity.segments[]` row: an address the server found overlapping a source, and its share of the story. */
export interface SimilaritySegment {
  readonly segment: SegmentAddress;
  readonly share: number;
}

/** `audit.v1.similarity`: `score` is `null` when the story is not scorable (`02.5.6-T1`). */
export interface SimilaritySection {
  readonly score: number | null;
  readonly corpus: string | null;
  /** Ordered as the server sent it (top 10, share desc), bounded on the way in. */
  readonly segments: readonly SimilaritySegment[];
}

/** The `audit.v1` document as the client reads it; a malformed or absent part is `null`, never an error. */
export interface AuditReport {
  readonly modelReview: ModelReview | null;
  readonly revision: RevisionInfo | null;
  readonly similarity: SimilaritySection | null;
}

/** The panel header's per-severity counts, derived from `qualityHints` only (CR-7 has no count field). */
export interface ReviewCounts {
  readonly blocking: number;
  readonly advisory: number;
}

/** The verdict a similarity highlight may carry — the server's, never a locally thresholded one. */
export type SimilarityLevel = 'warn' | 'block';

/** Count the flattened hint set by severity, with a taste issue always counted as advisory. */
export function countBySeverity(hints: readonly QualityHint[]): ReviewCounts {
  let blocking = 0;
  let advisory = 0;
  for (const hint of hints) {
    if (hint.severity === 'blocking' && hint.kind !== 'taste') blocking += 1;
    else advisory += 1;
  }
  return { blocking, advisory };
}

/**
 * The raw recorded review issues for the expanded "why" panel ONLY, taste clamped to advisory. An absent audit or
 * `model_review` yields an empty list: the panel shows its empty state, never a fabricated issue or a `0` count claim.
 */
export function whyIssues(audit: AuditReport | null): readonly ReviewIssue[] {
  // `02.6.1-T1:99` downgrades a taste issue to advisory server-side; repeated here so no payload can render one as a must-fix.
  return (audit?.modelReview?.issues ?? []).map((issue): ReviewIssue =>
    issue.severity === 'blocking' && issue.kind === 'taste' ? { ...issue, severity: 'advisory' } : issue);
}

/**
 * `address → warn|block` for the segments the server reported overlapping a source. The level is read from the hint set
 * (`Q-SIM-02` blocks, `Q-SIM-01` warns, neither means no highlight at all), and the addresses come from
 * `similarity.segments[]` only — so a score without segments marks nothing, and the client never invents a verdict.
 */
export function highlightMap(similarity: SimilaritySection | null, hints: readonly QualityHint[]): ReadonlyMap<SegmentAddress, SimilarityLevel> {
  const highlights = new Map<SegmentAddress, SimilarityLevel>();
  if (similarity === null) return highlights;
  const level: SimilarityLevel | null = hints.some((hint) => hint.ruleId === SIMILARITY_BLOCK_RULE) ? 'block'
    : hints.some((hint) => hint.ruleId === SIMILARITY_WARN_RULE) ? 'warn'
    : null;
  if (level === null) return highlights;
  for (const row of similarity.segments) {
    if (row.share > 0) highlights.set(row.segment, level);
  }
  return highlights;
}
