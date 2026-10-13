import type { SegmentAddress } from '@shared/util/segment-address';

/** Where a piece of server feedback came from: a `qualityHints[]` row or a `409 keywords_flagged` `errors[]` row. */
export type AnnotationSource = 'hint' | 'keyword';

/** One `409 keywords_flagged` row: the 0-based chip on `address` the server refused, and its rule name. */
export interface KeywordFlag {
  readonly address: SegmentAddress;
  readonly index: number;
  readonly rule: string;
}

/**
 * One piece of server feedback placed next to the text it is about (10.5.10-T1). Immutable and keyed by the validated
 * segment/rule identity, so the panel and the inline slots can be re-sorted and re-rendered without touching the draft.
 */
export interface SegmentAnnotation {
  /** Stable, DOM-safe id (`[A-Za-z0-9_-]`): `ann-<address|unresolved>-<ruleId>`; a keyword flag appends `-k<index>`. */
  readonly id: string;
  readonly source: AnnotationSource;
  /** `null` when the address could not be resolved: the annotation stays in the panel and is never dropped. */
  readonly address: SegmentAddress | null;
  readonly ruleId: string;
  readonly severity: 'advisory' | 'blocking';
  readonly kind: 'objective' | 'taste';
  /** Transloco key used verbatim for a hint, or derived from the keyword rule; never the raw server prose. */
  readonly messageKey: string;
  /** Hint evidence, already truncated to 200 chars; `''` for keyword flags. Interpolated as text, never as markup. */
  readonly evidence: string;
  readonly repairDirection: string | null;
  /** Keyword flags only: the 0-based chip on the segment that is flagged. */
  readonly keywordIndex: number | null;
  readonly placement: 'inline' | 'panel';
}
