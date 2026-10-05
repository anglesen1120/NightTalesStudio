import { describe, expect, it } from 'vitest';
import { dashboardEpisode } from './testing/fake-episode.repository';
import {
  DASHBOARD_PAGE_SIZE,
  DASHBOARD_STATUSES,
  destinationFor,
  emptyDashboardGroups,
  groupOf,
  mergeDashboardGroups,
  primaryActionFor,
} from './dashboard.model';

describe('DashboardModel_GroupOf', () => {
  it('maps the five statuses of the overview to their group', () => {
    expect(groupOf('story_review')).toBe('needsReview');
    expect(groupOf('video_review')).toBe('needsReview');
    expect(groupOf('story_generating')).toBe('inProgress');
    expect(groupOf('rendering')).toBe('inProgress');
    expect(groupOf('ready')).toBe('ready');
  });

  it('returns null for every status the overview does not show', () => {
    for (const status of ['draft', 'story_approved', 'scheduled', 'published', 'failed', 'cancelled'] as const) {
      expect(groupOf(status)).toBeNull();
    }
  });

  it('every requested status has a group', () => {
    for (const status of DASHBOARD_STATUSES) expect(groupOf(status)).not.toBeNull();
  });
});

describe('DashboardModel_PrimaryActionFor', () => {
  it('sends a story review and a video review to their own action', () => {
    expect(primaryActionFor('story_review', false)).toBe('reviewStory');
    expect(primaryActionFor('story_review', true)).toBe('reviewStory');
    expect(primaryActionFor('video_review', false)).toBe('reviewVideo');
    expect(primaryActionFor('video_review', true)).toBe('reviewVideo');
  });

  it('offers publish on ready only when the account may publish', () => {
    expect(primaryActionFor('ready', true)).toBe('publish');
    expect(primaryActionFor('ready', false)).toBe('open');
  });

  it('opens the episode for every other status', () => {
    for (const status of ['story_generating', 'rendering', 'draft', 'published'] as const) {
      expect(primaryActionFor(status, true)).toBe('open');
      expect(primaryActionFor(status, false)).toBe('open');
    }
  });
});

describe('DashboardModel_DestinationFor', () => {
  it('publish goes to the YouTube publication flow of the episode', () => {
    expect(destinationFor('ep-7', 'publish')).toEqual(['/episodes', 'ep-7', 'publish', 'youtube']);
  });

  it('every other action goes to the episode detail', () => {
    for (const action of ['reviewStory', 'reviewVideo', 'open'] as const) {
      expect(destinationFor('ep-7', action)).toEqual(['/episodes', 'ep-7']);
    }
  });
});

describe('DashboardModel_MergeDashboardGroups', () => {
  it('keeps the most advanced known status when an episode drifts between two calls', () => {
    const groups = mergeDashboardGroups([[dashboardEpisode(1, { status: 'story_review' })], [dashboardEpisode(1, { status: 'video_review' })]]);
    expect(groups.needsReview.map((item) => item.id)).toEqual(['dep-1']);
    expect(groups.needsReview[0]?.status).toBe('video_review');
  });

  it('ignores an episode whose status belongs to no group', () => {
    const groups = mergeDashboardGroups([[dashboardEpisode(1, { status: 'draft' }), dashboardEpisode(2, { status: 'story_review' })]]);
    expect(groups.needsReview.map((item) => item.id)).toEqual(['dep-2']);
    expect(groups.inProgress).toEqual([]);
    expect(groups.ready).toEqual([]);
  });

  it('sorts each group by updatedAt descending, ties by id', () => {
    const groups = mergeDashboardGroups([
      [
        dashboardEpisode(1, { status: 'rendering', updatedAt: '2026-10-02T10:00:00Z' }),
        dashboardEpisode(2, { status: 'story_generating', updatedAt: '2026-10-04T10:00:00Z' }),
        dashboardEpisode(3, { status: 'rendering', updatedAt: '2026-10-02T10:00:00Z' }),
      ],
    ]);
    expect(groups.inProgress.map((item) => item.id)).toEqual(['dep-2', 'dep-1', 'dep-3']);
  });

  it('an empty set of pages yields the empty groups', () => {
    expect(mergeDashboardGroups([])).toEqual({ needsReview: [], inProgress: [], ready: [] });
  });
});

describe('DashboardModel_Constants', () => {
  it('requests exactly the five scalar statuses in order at the page size', () => {
    expect(DASHBOARD_STATUSES).toEqual(['story_review', 'video_review', 'story_generating', 'rendering', 'ready']);
    expect(DASHBOARD_PAGE_SIZE).toBe(20);
  });

  it('emptyDashboardGroups returns a fresh, empty snapshot each call', () => {
    const first = emptyDashboardGroups();
    const second = emptyDashboardGroups();
    expect(first).toEqual(second);
    expect(first).not.toBe(second);
  });
});
