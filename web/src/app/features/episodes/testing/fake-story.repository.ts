import type { StoryDraftWire, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';

/** Scriptable save port: records the immutable sent wire snapshot, exact ETag and user classification. */
export class FakeStoryRepository extends StoryRepository {
  readonly calls: Array<{ episodeId: string; etag: string; content: StoryDraftWire; userAction: boolean }> = [];
  private readonly script: Array<() => Promise<StorySnapshot>> = [];
  snapshot = storySnapshot();

  scriptSave(answer: () => Promise<StorySnapshot>): this { this.script.push(answer); return this; }

  override save(episodeId: string, etag: string, content: StoryDraftWire, opts: { userAction: true }): Promise<StorySnapshot> {
    this.calls.push({ episodeId, etag, content, userAction: opts.userAction });
    return this.script.shift()?.() ?? Promise.resolve({ ...this.snapshot, content });
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

export function storySnapshot(overrides: Partial<StorySnapshot> = {}): StorySnapshot {
  return { versionId: '33333333-3333-4333-8333-333333333333', version: 1, etag: '"sv-33333333-3333-4333-8333-333333333333"', source: 'generated', wordCount: 18, content: storyContent(), qualityHints: [], ...overrides };
}
