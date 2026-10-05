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
export interface StorySnapshot { readonly versionId: string; readonly version: number; readonly etag: string; readonly source: string; readonly wordCount: number; readonly content: StoryDraftWire; readonly qualityHints: readonly QualityHint[] }
