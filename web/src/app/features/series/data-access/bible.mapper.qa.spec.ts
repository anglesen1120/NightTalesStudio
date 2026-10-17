// QA (10.5.7-T1) — negative and boundary cases of the bible mapper that the task's own mapper spec does not cover:
// coercions and missing members (D5: explicit per-field mapping, `''` never `undefined`), the 30-entry id boundary,
// characters the form rejects but the mapper must carry verbatim, and an unusable pair of tags.
import { describe, expect, it } from 'vitest';
import type { BibleContentResponseV1, BibleDto } from '@shared/api';
import { bibleContent } from '../testing/fake-bible.repository';
import { mapBible, mapBibleContent, mapBibleContentToWire, mapBibleWrite } from './bible.mapper';

const SERIES_ID = '11111111-1111-4111-8111-111111111111';

const STRUCTURE = { sub_genre: 'ghost', opening: 'hook', twist_type: 'reveal', setting: 'apartment', narrator_mode: 'first', beats: ['a'] };

const CONTENT: BibleContentResponseV1 = {
  schema_version: 'bible.v1',
  narrator: { name: 'Người kể', persona: 'Giọng trầm', voice_notes: 'Ngắt câu' },
  recurring_characters: [{ id: 'ch1', name: 'Bà Hoa', description: 'Gác cổng', first_episode_id: 'ep-1' }],
  places: [{ id: 'pl1', name: 'Chung cư', description: 'Hành lang' }],
  rules: [{ id: 'rl1', text: 'Không mở cửa' }],
  episode_summaries: [{ episode_id: 'ep-1', title: 'Tập 1', summary: 'Tóm tắt', structure: STRUCTURE, approved_at: '2026-10-01T10:00:00Z' }],
};

const WIRE: BibleDto = { seriesId: SERIES_ID, version: 1, etag: '"sb-1"', content: CONTENT, proposal: null };

describe('bible mapper (QA)', () => {
  it('normalises a null, a numeric-string and a padded version without throwing', () => {
    const fromString = mapBible({ ...WIRE, version: '7' });
    const fromNull = mapBible({ ...WIRE, version: null as unknown as number });
    const fromPadded = mapBible({ ...WIRE, version: '007' });
    expect(fromString.version).toBe(7);
    expect(fromPadded.version).toBe(7);
    // `Number(null)` is 0: a missing version reads as 0 (finite), never NaN — the store then keeps the ETag it got.
    expect(fromNull.version).toBe(0);
  });

  it('reads every missing array as an empty list and a missing narrator as empty strings', () => {
    const content = mapBibleContent({ schema_version: 'bible.v1' } as BibleContentResponseV1);
    expect(content).toEqual({
      narrator: { name: '', persona: '', voiceNotes: '' },
      recurringCharacters: [],
      places: [],
      rules: [],
      episodeSummaries: [],
    });
    const wire = mapBibleContentToWire(content);
    expect(wire).toEqual({
      narrator: { name: '', persona: '', voice_notes: '' },
      recurring_characters: [],
      places: [],
      rules: [],
      episode_summaries: [],
    });
  });

  it('omits a summary structure/approved_at that the server sent as null', () => {
    const content = mapBibleContent({
      ...CONTENT,
      episode_summaries: [{ episode_id: 'ep-1', title: 'T', summary: 'S', structure: null, approved_at: null } as unknown as BibleContentResponseV1['episode_summaries'][number]],
    });
    expect(content.episodeSummaries[0]).toEqual({ episodeId: 'ep-1', title: 'T', summary: 'S' });
    expect(JSON.stringify(mapBibleContentToWire(content))).not.toContain('structure');
  });

  it('carries 30 entries with ch1..ch30 untouched and omits the id only of an additional new row', () => {
    const characters = Array.from({ length: 30 }, (_, index) => ({
      id: `ch${index + 1}`,
      name: `Nhân vật ${index + 1}`,
      description: '',
      first_episode_id: index === 0 ? 'ep-1' : null,
    }));
    const content = mapBibleContent({ ...CONTENT, recurring_characters: characters });
    expect(content.recurringCharacters).toHaveLength(30);
    expect(content.recurringCharacters.map((row) => row.id)).toEqual(characters.map((row) => row.id));

    const wire = mapBibleContentToWire(bibleContent({ recurringCharacters: [...content.recurringCharacters, { name: 'Người thứ 31', description: '' }] }));
    expect(wire.recurring_characters).toHaveLength(31);
    expect(wire.recurring_characters[29]!.id).toBe('ch30');
    expect('id' in wire.recurring_characters[30]!).toBe(false);
    expect(wire.recurring_characters[0]!.first_episode_id).toBe('ep-1');
  });

  it('carries angle brackets and template markers verbatim: the mapper is not the rejecting layer', () => {
    const raw = '<img src=x onerror="alert(1)"> {{title}}';
    const content = mapBibleContent({
      ...CONTENT,
      narrator: { name: raw, persona: raw, voice_notes: raw },
      recurring_characters: [{ id: 'ch1', name: raw, description: raw, first_episode_id: null }],
      places: [{ id: 'pl1', name: raw, description: raw }],
      rules: [{ id: 'rl1', text: raw }],
      episode_summaries: [{ episode_id: 'ep-1', title: raw, summary: raw, structure: STRUCTURE, approved_at: '2026-10-01T10:00:00Z' }],
    });
    const wire = mapBibleContentToWire(content);
    expect(wire.narrator.name).toBe(raw);
    expect(wire.recurring_characters[0]!.name).toBe(raw);
    expect(wire.rules[0]!.text).toBe(raw);
    expect(wire.episode_summaries[0]!.title).toBe(raw);
  });

  it('maps an op whose value members are all null to a kind-only row', () => {
    const model = mapBible({
      ...WIRE,
      proposal: {
        basedOnVersion: 2,
        proposedFromEpisodeId: null,
        proposedAt: '2026-10-02T10:00:00Z',
        ops: [{ kind: 'AddCharacter', name: null, description: null, text: null, summary: null, reason: null }],
        preview: { characters: 1, places: 0, rules: 0, summaryForEpisodeId: null },
      },
    });
    expect(model.proposal?.ops[0]).toEqual({ kind: 'AddCharacter' });
    expect(model.proposal?.proposedFromEpisodeId).toBeNull();
  });

  it('treats an absent ops list as no rows and an empty episode id as null', () => {
    const model = mapBible({
      ...WIRE,
      proposal: {
        basedOnVersion: 1,
        proposedFromEpisodeId: '',
        proposedAt: '',
        ops: undefined as unknown as [],
        preview: { characters: 0, places: 0, rules: 0, summaryForEpisodeId: '' },
      },
    });
    expect(model.proposal?.ops).toEqual([]);
    expect(model.proposal?.proposedFromEpisodeId).toBeNull();
    expect(model.proposal?.preview.summaryForEpisodeId).toBeNull();
  });

  it('coerces unusable preview counters to 0 rather than NaN', () => {
    const model = mapBible({
      ...WIRE,
      proposal: {
        basedOnVersion: 'x' as unknown as number,
        proposedFromEpisodeId: null,
        proposedAt: '',
        ops: [],
        preview: { characters: 'x', places: '2', rules: -1, summaryForEpisodeId: null },
      },
    });
    expect(model.proposal?.basedOnVersion).toBe(0);
    expect(model.proposal?.preview).toEqual({ characters: 0, places: 2, rules: -1, summaryForEpisodeId: null });
  });

  it('rejects an unusable body tag and falls back to the header, then to an empty tag', () => {
    const unquoted = mapBible({ ...WIRE, etag: 'sb-1' }, '"sb-1"');
    const weak = mapBible({ ...WIRE, etag: 'W/"sb-1"' });
    const none = mapBible({ ...WIRE, etag: '' }, null);
    expect(unquoted.etag).toBe('"sb-1"');
    // `isEntityTag` accepts the weak grammar (a server bug); the store can only echo what the GET carried.
    expect(weak.etag).toBe('W/"sb-1"');
    expect(none.etag).toBe('');
  });

  it('maps a write response with a rejected body tag and no header to an empty tag', () => {
    expect(mapBibleWrite({ version: 2, etag: 'sb-2' }, null)).toEqual({ version: 2, etag: '' });
    expect(mapBibleWrite({ version: '2', etag: null as unknown as string }, '"sb-2"')).toEqual({ version: 2, etag: '"sb-2"' });
  });
});
