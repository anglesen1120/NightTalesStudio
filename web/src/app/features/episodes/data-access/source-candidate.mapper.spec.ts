import type { SourceCandidateDto } from '@shared/api';
import { describe, expect, it } from 'vitest';
import { SourceCandidateMappingError, mapSourceCandidate, mapSourceCandidateList } from './source-candidate.mapper';

const ROW: SourceCandidateDto = {
  id: 'candidate-1',
  url: 'https://example.com/article',
  title: 'An article',
  siteName: 'example.com',
  rank: 1,
  status: 'proposed',
  sourceId: null,
  createdAt: '2026-10-01T10:00:00Z',
};

describe('mapSourceCandidate', () => {
  it('maps a row and keeps the nullable members null', () => {
    expect(mapSourceCandidate({ ...ROW, title: null })).toEqual({
      id: 'candidate-1',
      url: 'https://example.com/article',
      title: null,
      siteName: 'example.com',
      rank: 1,
      status: 'proposed',
      sourceId: null,
      createdAt: '2026-10-01T10:00:00Z',
    });
    expect(mapSourceCandidate({ ...ROW, status: 'added', sourceId: 'source-1' })).toMatchObject({
      status: 'added',
      sourceId: 'source-1',
    });
    expect(mapSourceCandidate({ ...ROW, status: 'dismissed' }).status).toBe('dismissed');
  });

  it('reads a status it does not know as already handled, never as proposed', () => {
    for (const status of ['superseded', 'PROPOSED', '']) {
      const mapped = mapSourceCandidate({ ...ROW, status });
      expect(mapped.status).toBe('dismissed');
      // The row keeps its place and its data: only the offered action is withheld.
      expect(mapped.id).toBe('candidate-1');
      expect(mapped.url).toBe('https://example.com/article');
    }
  });

  it('reads the rank as a number and never fails the row over it', () => {
    expect(mapSourceCandidate({ ...ROW, rank: '4' }).rank).toBe(4);
    expect(mapSourceCandidate({ ...ROW, rank: 0 }).rank).toBe(0);
    expect(mapSourceCandidate({ ...ROW, rank: 'n/a' }).rank).toBe(0);
    expect(mapSourceCandidate({ ...ROW, rank: 2.5 }).rank).toBe(0);
  });

  it('refuses a row without an id: it cannot be keyed, addressed or refreshed', () => {
    expect(() => mapSourceCandidate({ ...ROW, id: '' })).toThrow(SourceCandidateMappingError);
  });
});

describe('mapSourceCandidateList', () => {
  it('keeps the server order: a better rank later in the list stays later', () => {
    const list = mapSourceCandidateList({
      items: [{ ...ROW, id: 'a', rank: 3 }, { ...ROW, id: 'b', rank: 1 }, { ...ROW, id: 'c', rank: 2 }],
    });
    expect(list.map((row) => row.id)).toEqual(['a', 'b', 'c']);
  });

  it('maps an empty answer to an empty list', () => {
    expect(mapSourceCandidateList({ items: [] })).toEqual([]);
  });
});
