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
// eslint-disable-next-line @typescript-eslint/no-unused-vars -- the contract takes the status; the detail page owns per-status actions
export const nextActionFor = (_status: EpisodeStatus) => ({ labelKey: 'episodes.list.continue', segment: 'detail' }) as const;
