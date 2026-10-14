export type { SegmentAddress } from '@shared/util/segment-address';
import type { SegmentAddress } from '@shared/util/segment-address';

export interface StorySegment { readonly segKey: string; readonly text: string; readonly keywords: readonly string[]; readonly factRefs: readonly unknown[] }
export interface StoryParagraph extends StorySegment { readonly beat: string }
export interface StoryMetadata { readonly title: string; readonly description: string; readonly tags: readonly string[]; readonly contentWarnings: readonly string[]; readonly structure: unknown }
export interface StoryDraft { readonly title: string; readonly hook: StorySegment; readonly paragraphs: readonly StoryParagraph[]; readonly ending: StorySegment; readonly description: string; readonly tags: readonly string[]; readonly contentWarnings: readonly string[]; readonly structure: unknown }
export interface StorySegmentWire { readonly text: string; readonly keywords: readonly string[]; readonly fact_refs: readonly unknown[] }
export interface StoryParagraphWire extends StorySegmentWire { readonly beat: string }
export interface StoryDraftWire { readonly title: string; readonly hook: StorySegmentWire; readonly paragraphs: readonly StoryParagraphWire[]; readonly ending: StorySegmentWire; readonly description: string; readonly tags: readonly string[]; readonly content_warnings: readonly string[]; readonly structure: unknown }
export interface QualityHint { readonly ruleId: string; readonly severity: 'advisory' | 'blocking'; readonly kind: 'objective' | 'taste'; readonly segment: SegmentAddress | null; readonly messageKey: string; readonly evidence: string; readonly repairDirection: string | null }
/**
 * The server-owned true-crime attestation of the current story version (02.3.3-T1). Its text is localised by the API
 * for the UI language, so the client renders it verbatim: interpolated, never through `innerHTML`.
 */
export interface Attestation {
  /** True for a true-crime governed episode (`series.Topic == TrueCrime`, or a version carrying a fact sheet). */
  readonly required: boolean;
  /** The attestation text version the server sent; echoed back verbatim on accept, never derived or cached locally. */
  readonly textVersion: string;
  /** The attestation copy in the UI language. `null` when the server sent none for this story. */
  readonly text: string | null;
  /** When this exact `textVersion` was accepted; `null` while it is not (a reopen or a version bump clears it). */
  readonly acceptedAt: string | null;
}

export interface StorySnapshot { readonly versionId: string; readonly version: number; readonly etag: string; readonly source: string; readonly wordCount: number; readonly content: StoryDraftWire; readonly qualityHints: readonly QualityHint[]; readonly attestation: Attestation | null }

/** What an approve or a reopen answers: the episode's new story status and the ETag of that state. */
export interface ApprovalOutcome { readonly status: string; readonly etag: string }

/**
 * The tick the user gave, as sent to the server: the version of the text they were shown, never the text itself. A
 * caller with no block to show sends no attestation at all, so the request body omits the field (never `null`).
 */
export interface AttestationAcceptance { readonly accepted: true; readonly textVersion: string }
