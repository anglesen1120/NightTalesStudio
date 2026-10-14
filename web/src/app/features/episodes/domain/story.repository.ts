import type { ApprovalOutcome, AttestationAcceptance, StoryDraftWire, StorySnapshot } from './story.model';

/** Story commands are If-Match guarded, never idempotency-key guarded. */
export abstract class StoryRepository {
  abstract save(episodeId: string, etag: string, content: StoryDraftWire, opts: { userAction: true }): Promise<StorySnapshot>;
  /**
   * `POST …/story/approve`. `attestation` is sent only when the caller was shown the true-crime block: a call with no
   * block must omit the body field entirely. The caller re-reads the freshest ETag at call time.
   */
  abstract approve(episodeId: string, etag: string, attestation?: AttestationAcceptance): Promise<ApprovalOutcome>;
  /** `POST …/story/reopen`: same If-Match rule; the answer is the episode's new story state. */
  abstract reopen(episodeId: string, etag: string): Promise<ApprovalOutcome>;
}
