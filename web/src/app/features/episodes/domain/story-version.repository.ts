import type { StoryVersionDetail, StoryVersionSummary } from './story-version.model';

/** Read-only port over the version history routes; neither route is If-Match guarded (task Steps 3). */
export abstract class StoryVersionRepository {
  abstract list(episodeId: string, opts?: { userAction?: boolean }): Promise<readonly StoryVersionSummary[]>;
  abstract detail(episodeId: string, versionId: string, opts?: { userAction?: boolean }): Promise<StoryVersionDetail>;
}
