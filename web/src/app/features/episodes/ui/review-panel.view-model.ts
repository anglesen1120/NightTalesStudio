import type { SegmentAddress } from '@shared/util/segment-address';
import type { ReviewCounts, ReviewIssue, ScoreDimension, SimilarityLevel } from '../domain/audit.model';

export type ReviewScores = Readonly<Record<ScoreDimension, number | null>>;

export interface SimilarityHighlightViewModel {
  readonly address: SegmentAddress;
  readonly share: number;
  readonly level: SimilarityLevel | null;
  readonly addressLabelKey: string;
  readonly addressLabelIndex: number | null;
}

export interface ReviewPanelViewModel {
  readonly counts: ReviewCounts;
  readonly scores: ReviewScores | null;
  readonly stale: boolean;
  readonly whyIssues: readonly ReviewIssue[];
  readonly driftNote: boolean;
  readonly similarityRows: readonly SimilarityHighlightViewModel[];
  readonly similarityCorpus: string | null;
  readonly similarityScore: number | null;
  readonly similarityUnavailable: boolean;
  readonly hasAudit: boolean;
  readonly partial: boolean;
}

export interface RevisedBadgeViewModel {
  readonly describedById: string | null;
}
