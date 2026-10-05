import { fromContent, serialize, toContent } from './story-codec';
import type { StoryDraftWire } from './story.model';

const CONTENT: StoryDraftWire = {
  title: 'Title', hook: { text: 'Hook', keywords: ['night'], fact_refs: [{ opaque: 1 }] },
  paragraphs: [{ beat: 'turn', text: 'Paragraph', keywords: ['door'], fact_refs: ['fact-1'] }],
  ending: { text: 'Ending', keywords: ['dawn'], fact_refs: [] }, description: 'Description',
  tags: ['tag'], content_warnings: ['fear'], structure: { unknown: { ordered: [1, 3] } },
};
describe('story-codec', () => {
  it('toContent_DropsSegKeysAndEmitsExactlyStoryV1', () => {
    const content = toContent(fromContent(CONTENT));
    expect(content).toEqual(CONTENT);
    expect(Object.keys(content)).toEqual(['title', 'hook', 'paragraphs', 'ending', 'description', 'tags', 'content_warnings', 'structure']);
    expect(content.hook).not.toHaveProperty('segKey'); expect(content.paragraphs[0]).not.toHaveProperty('segKey'); expect(content.ending).not.toHaveProperty('segKey');
  });
  it('fromContent_MintsUniqueFreshKeys', () => {
    const one = fromContent(CONTENT), two = fromContent(CONTENT);
    const keys = [one.hook.segKey, one.paragraphs[0]?.segKey, one.ending.segKey, two.hook.segKey, two.paragraphs[0]?.segKey, two.ending.segKey];
    expect(new Set(keys).size).toBe(6);
    for (const key of keys) expect(key).toMatch(/^[0-9a-f-]{36}$/);
  });
  it('serialize_IsStableAcrossCallsAndFreshClientKeys', () => {
    const draft = fromContent(CONTENT);
    expect(serialize(draft)).toBe(serialize(draft)); expect(serialize(draft)).toBe(serialize(fromContent(CONTENT)));
  });
  it('structure_RoundTripsIdenticallyAndRemainsOpaque', () => expect(toContent(fromContent(CONTENT)).structure).toBe(CONTENT.structure));
  it('factRefs_ArePreserved', () => expect(toContent(fromContent(CONTENT)).hook.fact_refs).toEqual(CONTENT.hook.fact_refs));
  it('fromContent_DeduplicatesTagsAndWarningsWithoutReordering', () => {
    const draft = fromContent({ ...CONTENT, tags: ['night', 'moon', 'night'], content_warnings: ['fear', 'fear', 'storm'] });
    expect(draft.tags).toEqual(['night', 'moon']); expect(draft.contentWarnings).toEqual(['fear', 'storm']);
    expect(toContent(draft).tags).toEqual(['night', 'moon']); expect(toContent(draft).content_warnings).toEqual(['fear', 'storm']);
  });
});
