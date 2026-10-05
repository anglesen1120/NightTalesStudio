import type { StoryDraftWire, StorySnapshot } from './story.model';

/** Story saves are If-Match guarded, never idempotency-key guarded. */
export abstract class StoryRepository {
  abstract save(episodeId: string, etag: string, content: StoryDraftWire, opts: { userAction: true }): Promise<StorySnapshot>;
}
