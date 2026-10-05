import { EPISODE_STATUSES, type EpisodeListItem, type EpisodeStatus } from '@features/episodes';

/** The three groups of the overview, in render order: review first, then work in flight, then ready to publish. */
export type DashboardGroup = 'needsReview' | 'inProgress' | 'ready';

/** The primary next action a row offers; the page maps it to a route through `destinationFor`. */
export type PrimaryAction = 'reviewStory' | 'reviewVideo' | 'open' | 'publish';

/**
 * The five scalar statuses the store requests, in request order (10.4.1-T1 contract item 2). The live list API accepts
 * one status per call, so a `load()` is five parallel requests — never a comma-separated status. The order is also the
 * order the fake logs, so the spec asserts it verbatim.
 */
export const DASHBOARD_STATUSES = ['story_review', 'video_review', 'story_generating', 'rendering', 'ready'] as const;

/** Rows per request; the server caps a page at 100 (`CURSOR_PAGE_MAX_LIMIT`), the overview shows the first 20 per status. */
export const DASHBOARD_PAGE_SIZE = 20;

/** One ruled row: the episode, the group it landed in and the action the row offers. */
export interface NextActionRowViewModel {
  readonly episode: EpisodeListItem;
  readonly group: DashboardGroup;
  readonly primaryAction: PrimaryAction;
}

/** A de-duplicated, grouped, sorted snapshot of the three groups; the store's internal state. */
export interface DashboardGroups {
  readonly needsReview: readonly EpisodeListItem[];
  readonly inProgress: readonly EpisodeListItem[];
  readonly ready: readonly EpisodeListItem[];
}

/** The three groups in render order; used to build and to reset the snapshot. */
export const DASHBOARD_GROUPS: readonly DashboardGroup[] = ['needsReview', 'inProgress', 'ready'];

/** The status → group table; any other status (unknown to this screen, or `draft`/`published`/…) has no group. */
const GROUP_BY_STATUS: Readonly<Record<string, DashboardGroup>> = {
  story_review: 'needsReview',
  video_review: 'needsReview',
  story_generating: 'inProgress',
  rendering: 'inProgress',
  ready: 'ready',
};

/** The group a status belongs to, or `null` when it belongs to none (it is then never rendered nor counted). */
export function groupOf(status: EpisodeStatus): DashboardGroup | null {
  return GROUP_BY_STATUS[status] ?? null;
}

/**
 * The one action a row offers. `ready` offers "publish" only when the account's feature set allows it; every other
 * status opens the episode (the detail page owns the real mutations, plan §10 O5).
 */
export function primaryActionFor(status: EpisodeStatus, canPublish: boolean): PrimaryAction {
  if (status === 'story_review') return 'reviewStory';
  if (status === 'video_review') return 'reviewVideo';
  if (status === 'ready' && canPublish) return 'publish';
  return 'open';
}

/** The router commands for a row action; only `publish` goes past the episode detail into the publication flow. */
export function destinationFor(episodeId: string, action: PrimaryAction): readonly string[] {
  return action === 'publish' ? ['/episodes', episodeId, 'publish', 'youtube'] : ['/episodes', episodeId];
}

/**
 * How advanced a status is, for de-duplication: the `EPISODE_STATUSES` enum order, where a higher index wins
 * (`ready` > `video_review` > `rendering` > `story_review` > `story_generating`). A status outside the enum is `-1`,
 * so it never beats a known one; `groupOf` then drops it anyway.
 */
function advancement(status: EpisodeStatus): number {
  return EPISODE_STATUSES.indexOf(status);
}

/** Most recently updated first; ties break on the id so the order is stable across equal timestamps. */
function byRecency(a: EpisodeListItem, b: EpisodeListItem): number {
  if (a.updatedAt !== b.updatedAt) return a.updatedAt < b.updatedAt ? 1 : -1;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/**
 * Merges the five status pages into the three groups (10.4.1-T1 contract item 2): an episode that drifted between two
 * calls appears once, under its most advanced known status; a status with no group is ignored; each group is sorted
 * `updatedAt` desc, ties by id. Pure — the store only stores the result.
 */
export function mergeDashboardGroups(pages: readonly (readonly EpisodeListItem[])[]): DashboardGroups {
  const byId = new Map<string, EpisodeListItem>();
  for (const page of pages) {
    for (const item of page) {
      const previous = byId.get(item.id);
      if (previous === undefined || advancement(item.status) > advancement(previous.status)) byId.set(item.id, item);
    }
  }
  const buckets: Record<DashboardGroup, EpisodeListItem[]> = { needsReview: [], inProgress: [], ready: [] };
  for (const item of byId.values()) {
    const group = groupOf(item.status);
    if (group !== null) buckets[group].push(item);
  }
  for (const group of DASHBOARD_GROUPS) buckets[group].sort(byRecency);
  return { needsReview: buckets.needsReview, inProgress: buckets.inProgress, ready: buckets.ready };
}

/** The empty snapshot; the state before any load and the state after a whole-page failure. */
export function emptyDashboardGroups(): DashboardGroups {
  return { needsReview: [], inProgress: [], ready: [] };
}
