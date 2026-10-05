import type { StoryDraft, StoryDraftWire, StorySegment, StorySegmentWire } from './story.model';

export function toContent(draft: StoryDraft): StoryDraftWire {
  const segment = (value: StorySegment): StorySegmentWire => ({ text: value.text, keywords: [...value.keywords], fact_refs: [...value.factRefs] });
  return {
    title: draft.title,
    hook: segment(draft.hook),
    paragraphs: draft.paragraphs.map((paragraph) => ({ beat: paragraph.beat, ...segment(paragraph) })),
    ending: segment(draft.ending),
    description: draft.description,
    tags: [...draft.tags],
    content_warnings: [...draft.contentWarnings],
    structure: draft.structure,
  };
}

export function fromContent(content: StoryDraftWire): StoryDraft {
  const segment = (value: StorySegmentWire): StorySegment => ({ segKey: crypto.randomUUID(), text: value.text, keywords: [...value.keywords], factRefs: [...value.fact_refs] });
  return {
    title: content.title,
    hook: segment(content.hook),
    paragraphs: content.paragraphs.map((paragraph) => ({ beat: paragraph.beat, ...segment(paragraph) })),
    ending: segment(content.ending),
    description: content.description,
    tags: [...new Set(content.tags)],
    contentWarnings: [...new Set(content.content_warnings)],
    structure: content.structure,
  };
}

/** Explicit wire construction fixes key order and excludes all client tracking keys. */
export function serialize(draft: StoryDraft): string {
  return JSON.stringify(toContent(draft));
}
