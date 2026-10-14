import type { ApprovalOutcome, Attestation, AttestationAcceptance, StoryDraftWire, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';

/** Scriptable story port: records the immutable sent wire snapshot, exact ETag and user classification. */
export class FakeStoryRepository extends StoryRepository {
  readonly calls: Array<{ episodeId: string; etag: string; content: StoryDraftWire; userAction: boolean }> = [];
  readonly approveCalls: Array<{ episodeId: string; etag: string; attestation: AttestationAcceptance | undefined }> = [];
  readonly reopenCalls: Array<{ episodeId: string; etag: string }> = [];
  private readonly script: Array<() => Promise<StorySnapshot>> = [];
  private readonly approveScript: Array<() => Promise<ApprovalOutcome>> = [];
  private readonly reopenScript: Array<() => Promise<ApprovalOutcome>> = [];
  snapshot = storySnapshot();
  /** The default approve answer, used when no `scriptApprove` is queued. */
  outcome = approvalOutcome();
  /** The default reopen answer: reopen always answers `story_review`. */
  reopenedOutcome = approvalOutcome({ status: 'story_review', etag: '"sv-reopened"' });

  scriptSave(answer: () => Promise<StorySnapshot>): this { this.script.push(answer); return this; }

  scriptApprove(answer: () => Promise<ApprovalOutcome>): this { this.approveScript.push(answer); return this; }

  scriptReopen(answer: () => Promise<ApprovalOutcome>): this { this.reopenScript.push(answer); return this; }

  override save(episodeId: string, etag: string, content: StoryDraftWire, opts: { userAction: true }): Promise<StorySnapshot> {
    this.calls.push({ episodeId, etag, content, userAction: opts.userAction });
    return this.script.shift()?.() ?? Promise.resolve({ ...this.snapshot, content });
  }

  override approve(episodeId: string, etag: string, attestation?: AttestationAcceptance): Promise<ApprovalOutcome> {
    this.approveCalls.push({ episodeId, etag, attestation });
    return this.approveScript.shift()?.() ?? Promise.resolve(this.outcome);
  }

  override reopen(episodeId: string, etag: string): Promise<ApprovalOutcome> {
    this.reopenCalls.push({ episodeId, etag });
    return this.reopenScript.shift()?.() ?? Promise.resolve(this.reopenedOutcome);
  }
}

export function storyContent(overrides: Partial<StoryDraftWire> = {}): StoryDraftWire {
  return {
    title: 'The lantern', hook: { text: 'A light appeared.', keywords: ['lantern'], fact_refs: [] },
    paragraphs: Array.from({ length: 4 }, (_, index) => ({ beat: `beat-${String(index)}`, text: `Paragraph ${String(index + 1)} waits.`, keywords: ['road', 'mist'], fact_refs: [] })),
    ending: { text: 'The light vanished.', keywords: ['dawn'], fact_refs: [] },
    description: 'A midnight story', tags: ['night'], content_warnings: ['fear'], structure: { beats: [1, 2] }, ...overrides,
  };
}

/** A true-crime block by default: a spec turns `required` off for a plain episode. */
export function attestation(overrides: Partial<Attestation> = {}): Attestation {
  return { required: true, textVersion: '2026-10-01.1', text: 'I have verified the sources of this story.', acceptedAt: null, ...overrides };
}

export function approvalOutcome(overrides: Partial<ApprovalOutcome> = {}): ApprovalOutcome {
  return { status: 'story_approved', etag: '"sv-approved"', ...overrides };
}

export function storySnapshot(overrides: Partial<StorySnapshot> = {}): StorySnapshot {
  return { versionId: '33333333-3333-4333-8333-333333333333', version: 1, etag: '"sv-33333333-3333-4333-8333-333333333333"', source: 'generated', wordCount: 18, content: storyContent(), qualityHints: [], attestation: null, ...overrides };
}
