import type { EpisodeStatus, SeriesOption } from '../domain/episode.model';

/** Chip colour families the approved kit ships (`status-chip.scss` tokens); `warn`/`info` have no dedicated colour. */
export type EpisodeStatusTone = 'neutral' | 'info' | 'success' | 'error';

/**
 * Tone of the row status chip: pending steps read `info`, accepted states `success`, failure `error`, neutral rest.
 * The icon carries the distinction between equal tones — colour alone never does (D16, WR12).
 */
export const EPISODE_STATUS_TONE: Readonly<Record<EpisodeStatus, EpisodeStatusTone>> = {
  draft: 'neutral',
  story_generating: 'info',
  story_review: 'info',
  story_approved: 'success',
  rendering: 'info',
  video_review: 'info',
  ready: 'success',
  scheduled: 'neutral',
  published: 'success',
  failed: 'error',
  cancelled: 'error',
};

/** Immutable input bundle of `EpisodeFiltersComponent`; the page composes it from the store's readonly signals. */
export interface EpisodeFiltersViewModel {
  /** The applied status filter (`null` = all) or `null`; an unknown `?status=` never reaches it. */
  readonly status: EpisodeStatus | null;
  /** The applied series id filter (`null` = all); always a uuid — validated upstream — or `null`. */
  readonly seriesId: string | null;
  /** First-page series options (`limit=100`); empty while loading or unavailable. */
  readonly seriesOptions: readonly SeriesOption[];
  readonly seriesOptionsState: 'loading' | 'ready' | 'unavailable';
  /** `true` when the server returned a next cursor: the options are the first page only, never "all series". */
  readonly seriesOptionsBounded: boolean;
  readonly disabled: boolean;
}

/** One `Intl.DateTimeFormat` per app language, created once at module load and shared by every row (task perf note). */
const DATE_FORMAT_BY_LANG = {
  vi: new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'short' }),
  en: new Intl.DateTimeFormat('en-US', { dateStyle: 'short', timeStyle: 'short' }),
} as const;

/** The shared formatter of `language`; anything that is not `en` falls back to `vi-VN`, the app default (D10). */
export function episodeDateFormatter(language: string): Intl.DateTimeFormat {
  return language === 'en' ? DATE_FORMAT_BY_LANG.en : DATE_FORMAT_BY_LANG.vi;
}
