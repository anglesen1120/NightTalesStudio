import { describe, expect, it } from 'vitest';
import type { BibleContentResponseV1, BibleDto } from '@shared/api';
import { bibleContent } from '../testing/fake-bible.repository';
import { mapBible, mapBibleContent, mapBibleContentToWire, mapBibleWrite } from './bible.mapper';

const SERIES_ID = '11111111-1111-4111-8111-111111111111';

/** The `bible.v1` snake_case fixture of 02.6.3 §3.1, plus one unknown key and a full summary (structure/approved_at). */
const WIRE_CONTENT: BibleContentResponseV1 = {
  schema_version: 'bible.v1',
  narrator: { name: 'Người kể chuyện', persona: 'Giọng trầm, chậm', voice_notes: 'Ngắt câu dài' },
  recurring_characters: [
    { id: 'ch1', name: 'Bà Hoa', description: 'Người gác cổng', first_episode_id: 'ep-1' },
  ],
  places: [{ id: 'pl1', name: 'Chung cư cũ', description: 'Hành lang tối' }],
  rules: [{ id: 'rl1', text: 'Không mở cửa sau 23 giờ' }],
  episode_summaries: [{
    episode_id: 'ep-1',
    title: 'Tập 1 — Tiếng gõ',
    summary: 'Bà Hoa nghe tiếng gõ nhưng hành lang trống.',
    structure: { sub_genre: 'ghost', opening: 'hook', twist_type: 'reveal', setting: 'apartment', narrator_mode: 'first', beats: ['a', 'b'] },
    approved_at: '2026-10-01T10:00:00Z',
  }],
  // An unknown member a future server could add: the mapper must not leak it into the model or back onto the wire.
  future_section: { anything: true },
} as BibleContentResponseV1;

/** The same fixture as the PUT body: no `schema_version`, no `structure`/`approved_at`, no unknown key. */
const EXPECTED_WIRE = {
  narrator: { name: 'Người kể chuyện', persona: 'Giọng trầm, chậm', voice_notes: 'Ngắt câu dài' },
  recurring_characters: [
    { id: 'ch1', name: 'Bà Hoa', description: 'Người gác cổng', first_episode_id: 'ep-1' },
  ],
  places: [{ id: 'pl1', name: 'Chung cư cũ', description: 'Hành lang tối' }],
  rules: [{ id: 'rl1', text: 'Không mở cửa sau 23 giờ' }],
  episode_summaries: [{
    episode_id: 'ep-1',
    title: 'Tập 1 — Tiếng gõ',
    summary: 'Bà Hoa nghe tiếng gõ nhưng hành lang trống.',
  }],
};

const WIRE: BibleDto = {
  seriesId: SERIES_ID,
  version: '3',
  etag: '"sb-3"',
  content: WIRE_CONTENT,
  proposal: null,
};

describe('bible mapper', () => {
  it('maps a snake_case bible to the model, normalising the version to a number', () => {
    const model = mapBible(WIRE);
    expect(model.seriesId).toBe(SERIES_ID);
    expect(model.version).toBe(3);
    expect(model.etag).toBe('"sb-3"');
    expect(model.content.narrator.voiceNotes).toBe('Ngắt câu dài');
    expect(model.content.recurringCharacters[0]).toEqual({
      id: 'ch1',
      name: 'Bà Hoa',
      description: 'Người gác cổng',
      firstEpisodeId: 'ep-1',
    });
    expect(model.content.rules[0]).toEqual({ id: 'rl1', text: 'Không mở cửa sau 23 giờ' });
    // `structure`/`approved_at` are echoed on the model (the page never renders them) and are not sent back.
    expect(model.content.episodeSummaries[0]!.approvedAt).toBe('2026-10-01T10:00:00Z');
    expect(model.content.episodeSummaries[0]!.structure).toEqual(WIRE_CONTENT.episode_summaries[0]!.structure);
    expect(model.proposal).toBeNull();
  });

  it('round-trips the mapped fields byte-stable, without schema_version or server-derived summary members', () => {
    const wire = mapBibleContentToWire(mapBibleContent(WIRE_CONTENT));
    expect(wire).toEqual(EXPECTED_WIRE);
    expect(JSON.stringify(wire)).toBe(JSON.stringify(EXPECTED_WIRE));
    expect('schema_version' in wire).toBe(false);
    expect('future_section' in wire).toBe(false);
  });

  it('maps first_episode_id, approved_at and voice_notes in both directions', () => {
    const content = mapBibleContent(WIRE_CONTENT);
    expect(content.recurringCharacters[0]!.firstEpisodeId).toBe('ep-1');
    expect(content.episodeSummaries[0]!.approvedAt).toBe('2026-10-01T10:00:00Z');
    expect(content.narrator.voiceNotes).toBe('Ngắt câu dài');

    const wire = mapBibleContentToWire(content);
    expect(wire.recurring_characters[0]!.first_episode_id).toBe('ep-1');
    expect(wire.narrator.voice_notes).toBe('Ngắt câu dài');
    expect('approved_at' in wire.episode_summaries[0]!).toBe(false);
    expect('structure' in wire.episode_summaries[0]!).toBe(false);
  });

  it('ignores an unknown fixture key in every section', () => {
    const model = mapBible(WIRE);
    expect(Object.keys(model.content)).toEqual(['narrator', 'recurringCharacters', 'places', 'rules', 'episodeSummaries']);
    expect(Object.keys(model)).toEqual(['seriesId', 'version', 'etag', 'content', 'proposal']);
  });

  it('reads a missing persona as an empty string, never undefined', () => {
    const content = mapBibleContent({
      ...WIRE_CONTENT,
      narrator: { name: 'Người kể', persona: null, voice_notes: null },
    });
    expect(content.narrator.persona).toBe('');
    expect(content.narrator.voiceNotes).toBe('');
  });

  it('omits the id of a newly added entry and echoes the id of an existing one', () => {
    const content = bibleContent({
      recurringCharacters: [
        { id: 'ch1', name: 'Bà Hoa', description: 'Cũ', firstEpisodeId: 'ep-1' },
        { name: 'Ông Tư', description: 'Mới' },
      ],
      places: [{ name: 'Căn hộ mới', description: 'Mới' }],
      rules: [{ text: 'Luật mới' }],
    });
    const wire = mapBibleContentToWire(content);
    expect(wire.recurring_characters[0]!.id).toBe('ch1');
    expect('id' in wire.recurring_characters[1]!).toBe(false);
    expect('id' in wire.places[0]!).toBe(false);
    expect('id' in wire.rules[0]!).toBe(false);
    expect(wire.recurring_characters[1]).toEqual({ name: 'Ông Tư', description: 'Mới', first_episode_id: null });
  });

  it('maps a proposal with nullable episode, numeric strings and op values', () => {
    const model = mapBible({
      ...WIRE,
      proposal: {
        basedOnVersion: '2',
        proposedFromEpisodeId: null,
        proposedAt: '2026-10-02T10:00:00Z',
        ops: [
          { kind: 'AddCharacter', name: 'Ông Tư', description: 'Hàng xóm', summary: null, text: null, reason: 'Mới' },
        ],
        preview: { characters: '1', places: 0, rules: '2', summaryForEpisodeId: null },
      },
    });
    expect(model.proposal).toEqual({
      basedOnVersion: 2,
      proposedFromEpisodeId: null,
      proposedAt: '2026-10-02T10:00:00Z',
      ops: [{ kind: 'AddCharacter', name: 'Ông Tư', description: 'Hàng xóm', reason: 'Mới' }],
      preview: { characters: 1, places: 0, rules: 2, summaryForEpisodeId: null },
    });
  });

  it('prefers the ETag header of a write response and normalises its version', () => {
    expect(mapBibleWrite({ version: '4', etag: '"sb-4"' }, '"sb-4"')).toEqual({ version: 4, etag: '"sb-4"' });
    expect(mapBibleWrite({ version: 5, etag: '"sb-5"' }, null)).toEqual({ version: 5, etag: '"sb-5"' });
  });

  it('falls back to the response ETag header when the GET body carries no usable tag', () => {
    expect(mapBible({ ...WIRE, etag: '' }, '"sb-3"').etag).toBe('"sb-3"');
    expect(mapBible({ ...WIRE, etag: '' }).etag).toBe('');
  });
});
