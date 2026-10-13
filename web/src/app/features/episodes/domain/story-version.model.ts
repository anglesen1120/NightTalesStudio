import type { StoryDraftWire } from './story.model';

/** The known server `source` values; anything else maps to `other` (task i18n note). */
export type VersionSource = 'generated' | 'revised' | 'edited' | 'other';

export interface StoryVersionSummary {
  readonly versionId: string;
  readonly version: number;
  readonly source: VersionSource;
  readonly createdAt: string;
  readonly wordCount: number;
  readonly etag: string;
  readonly similarityScore: number | null;
  readonly approved: boolean;
  readonly isCurrent: boolean;
}

export interface StoryVersionDetail extends StoryVersionSummary {
  readonly body: string;
  /** The raw story.v1 object; `null` when the stored content is absent or not story.v1 (a read-only history row). */
  readonly content: StoryDraftWire | null;
  readonly createdBy: string;
}
