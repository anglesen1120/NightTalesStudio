import type { RenderJob } from './render.model';
import type { StorySnapshot } from './story.model';

/** The 11 wire statuses of an episode, in enum order (plan F19). */
export const EPISODE_STATUSES = [
  'draft',
  'story_generating',
  'story_review',
  'story_approved',
  'rendering',
  'video_review',
  'ready',
  'scheduled',
  'published',
  'failed',
  'cancelled',
] as const;

export type EpisodeStatus = (typeof EPISODE_STATUSES)[number];

/** Statuses the filter offers. The list endpoint never returns `cancelled`, so it is not offered. */
export const LIST_STATUSES: readonly EpisodeStatus[] = EPISODE_STATUSES.filter((status) => status !== 'cancelled');

/** Closed-list validation for `?status=`: only a literal of `LIST_STATUSES` passes; anything else is ignored. */
export function isListStatus(value: unknown): value is EpisodeStatus {
  return typeof value === 'string' && (LIST_STATUSES as readonly string[]).includes(value);
}

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Closed validation for `?series=`: a canonical 8-4-4-4-12 hex id, nothing else is sent to the API. */
export function isUuid(value: unknown): value is string {
  return typeof value === 'string' && UUID_PATTERN.test(value);
}

export interface EpisodeListItem {
  readonly id: string;
  readonly seriesId: string;
  readonly title: string | null;
  readonly status: EpisodeStatus;
  readonly lengthTier: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly etag: string;
}

export interface SeriesOption {
  readonly id: string;
  readonly title: string;
}

/** `bounded`: the server returned a next cursor, so more than the first 100 series exist and the options are partial. */
export interface SeriesOptionsPage {
  readonly items: readonly SeriesOption[];
  readonly bounded: boolean;
}

/** The detail page owns the real actions (plan §10 O5); the list always offers "Continue" to the detail route. */
export const nextActionFor: (status: EpisodeStatus) => { readonly labelKey: string; readonly segment: 'detail' } = () => ({
  labelKey: 'episodes.list.continue',
  segment: 'detail',
});

export type GenerationStage = 'queued' | 'writing' | 'checking' | 'reviewing' | 'revising' | 'scoring' | 'saving' | 'done';
export type GenerationRunStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
export type LengthTier = 'short' | 'standard' | 'long';

export interface EpisodeGeneration {
  readonly kind: string;
  /** Forward-compatible wire value; generation-state guards recognise the closed run status set. */
  readonly status: string;
  readonly stage: string;
  readonly errorCode: string | null;
  readonly startedAt: string | null;
  readonly processingPaused: string | null;
}

/**
 * The finished video as `GET /episodes/{id}` reports it (09.5.3-T1, WD4): asset metadata only — never a media URL
 * (09.7.2-T1 owns the signed URL endpoint). The counts arrive as `(number | string)` on the wire and are mapped to
 * numbers, so nothing here is a raw wire string.
 */
export interface FinalVideoAsset {
  readonly assetId: string;
  readonly contentType: string;
  /** `size_bytes`: always a number on the wire (integer or numeric string); an unusable value is not a size. */
  readonly sizeBytes: number;
  /** `duration_sec`: `null` when the asset row carries none. */
  readonly durationSec: number | null;
  readonly retentionUntil: string | null;
  readonly sha256: string;
}

export interface EpisodeDetail {
  readonly id: string;
  readonly seriesId: string;
  /** Forward-compatible wire value; consumers compare only recognised episode statuses. */
  readonly status: string;
  /** The story-length tier the server owns (C9 bands); the editor's structure advisory reads it. */
  readonly lengthTier: string;
  readonly etag: string;
  readonly generation: EpisodeGeneration | null;
  readonly hasCurrentStory: boolean;
  readonly currentStory: StorySnapshot | null;
  readonly nextActions: readonly string[];
  /** The newest render job (F5/F6): `null` when the episode has never rendered. Mapping lives in the episode mapper. */
  readonly renderJob: RenderJob | null;
  /**
   * The episode's own title, or `null` when the server has none (C8). Optional on the model because every
   * fixture that predates 10.6.2-T1 builds a detail literal without it; the repository always maps it.
   */
  readonly title?: string | null;
  /**
   * The final asset of a reviewed render (WD4), metadata only: the video-review view reads its size/duration/retention
   * and the download builds its file name from the title. `null`/absent — a race or a hand-inserted row, and the
   * publication projections of 10.7.4-T1 — hides the metadata block and lets the URL read 404 into `errors.not_found`.
   */
  readonly finalVideo?: FinalVideoAsset | null;
}

export interface SeriesSummary {
  readonly id: string;
  readonly title: string;
  readonly topic: string;
}

export interface CreatedEpisode {
  readonly id: string;
  readonly etag: string | null;
}
